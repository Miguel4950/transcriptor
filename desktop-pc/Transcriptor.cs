using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Shapes;
using System.Windows.Threading;
using Microsoft.Win32;

namespace TranscriptorApp
{
    // ==========================================
    // GESTOR DE INICIO CON WINDOWS (REGISTRO)
    // ==========================================
    public static class StartupManager
    {
        private const string RUN_KEY = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private const string APP_NAME = "TranscriptorApp";
        private const string OLD_APP_NAME = "GroqWhisperLiquidGlass";

        public static bool IsStartupEnabled()
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(RUN_KEY, false))
                {
                    if (key == null) return false;
                    object val = key.GetValue(APP_NAME);
                    return val != null && !string.IsNullOrEmpty(val.ToString());
                }
            }
            catch { return false; }
        }

        public static void SetStartup(bool enable, string exePath)
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(RUN_KEY, true))
                {
                    if (key == null) return;
                    if (key.GetValue(OLD_APP_NAME) != null)
                    {
                        key.DeleteValue(OLD_APP_NAME, false);
                    }
                    if (enable)
                    {
                        key.SetValue(APP_NAME, "\"" + exePath + "\"");
                    }
                    else if (key.GetValue(APP_NAME) != null)
                    {
                        key.DeleteValue(APP_NAME, false);
                    }
                }
            }
            catch { }
        }
    }

    // ==========================================
    // MOTOR DE AUDIO Y TRANSCRIPCIÃ“N GROQ WHISPER V3
    // ==========================================
    public class AudioRecorderEngine
    {
        public static readonly string GROQ_API_KEY = Environment.GetEnvironmentVariable("GROQ_API_KEY") ?? (File.Exists("api_key.txt") ? File.ReadAllText("api_key.txt").Trim() : "");
        public const string GROQ_ENDPOINT = "https://api.groq.com/openai/v1/audio/transcriptions";
        public const string WHISPER_MODEL = "whisper-large-v3";

        public const int SAMPLE_RATE = 16000;
        public const short BITS_PER_SAMPLE = 16;
        public const short CHANNELS = 1;
        public const int BYTES_PER_SECOND = SAMPLE_RATE * CHANNELS * (BITS_PER_SAMPLE / 8); // 32,000 bytes/s
        public const int CHUNK_DURATION_SECONDS = 120; // 2 minutos exactos
        public const int CHUNK_MAX_BYTES = BYTES_PER_SECOND * CHUNK_DURATION_SECONDS; // ~3.66 MB (< 22 MB)
        public const int MAX_SAFE_BYTES = 22 * 1024 * 1024;

        private static readonly HttpClient httpClient = new HttpClient { Timeout = TimeSpan.FromMinutes(3) };

        public bool IsRecording { get; private set; }
        public bool IsPaused { get; private set; }
        public bool IsProcessing { get; private set; }
        public bool AppendMode { get; set; }
        public string AccumulatedText { get; set; }

        public TimeSpan TotalElapsedTime
        {
            get
            {
                if (!IsRecording) return accumulatedTime;
                return IsPaused ? accumulatedTime : accumulatedTime + (DateTime.Now - lastResumeTime);
            }
        }

        private TimeSpan accumulatedTime = TimeSpan.Zero;
        private DateTime lastResumeTime;
        private int maxSessionPeak = 0;

        private IntPtr hWaveIn = IntPtr.Zero;
        private WaveBuffer[] waveBuffers;
        private MemoryStream currentChunkStream;
        private int currentChunkIndex = 0;
        private List<Task<ChunkResult>> activeChunkTasks = new List<Task<ChunkResult>>();

        public event Action<double> OnAudioLevel;
        public event Action<string, string, string> OnStatusChanged; // title, subtitle, stateTag ("idle", "rec", "pause", "work", "done", "err")
        public event Action<string> OnTextUpdated;
        public event Action OnStateChanged;

        public AudioRecorderEngine()
        {
            AccumulatedText = "";
            ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12 | (SecurityProtocolType)3072;
        }

        public void ResetSession()
        {
            accumulatedTime = TimeSpan.Zero;
            AccumulatedText = "";
            if (OnTextUpdated != null) OnTextUpdated("");
            if (OnStatusChanged != null) OnStatusChanged("Listo", "Trozos automÃ¡ticos de 2 min â€¢ Alt + M", "idle");
            if (OnStateChanged != null) OnStateChanged();
        }

        public bool StartRecording(bool append)
        {
            if (IsProcessing) return false;

            try
            {
                AppendMode = append;
                if (!append)
                {
                    accumulatedTime = TimeSpan.Zero;
                    AccumulatedText = "";
                    if (OnTextUpdated != null) OnTextUpdated("");
                }

                currentChunkStream = new MemoryStream();
                currentChunkIndex = 0;
                maxSessionPeak = 0;
                activeChunkTasks.Clear();

                WAVEFORMATEX format = new WAVEFORMATEX
                {
                    wFormatTag = 1,
                    nChannels = (ushort)CHANNELS,
                    nSamplesPerSec = SAMPLE_RATE,
                    wBitsPerSample = (ushort)BITS_PER_SAMPLE,
                    nBlockAlign = (ushort)(CHANNELS * (BITS_PER_SAMPLE / 8)),
                    nAvgBytesPerSec = (uint)BYTES_PER_SECOND,
                    cbSize = 0
                };

                int result = waveInOpen(out hWaveIn, -1, ref format, IntPtr.Zero, IntPtr.Zero, 0);
                if (result != 0 || hWaveIn == IntPtr.Zero)
                {
                    if (OnStatusChanged != null) OnStatusChanged("Sin micrÃ³fono", "Conecta un micrÃ³fono o revisa permisos", "err");
                    return false;
                }

                int bufferSize = BYTES_PER_SECOND / 10; // Buffers de 100 ms para respuesta ultra fluida
                waveBuffers = new WaveBuffer[20];
                for (int i = 0; i < waveBuffers.Length; i++)
                {
                    waveBuffers[i] = new WaveBuffer(hWaveIn, bufferSize);
                    waveBuffers[i].Queue();
                }

                int startRes = waveInStart(hWaveIn);
                if (startRes != 0)
                {
                    CleanupWaveIn();
                    if (OnStatusChanged != null) OnStatusChanged("Error de audio", "No se pudo iniciar la captura", "err");
                    return false;
                }

                IsRecording = true;
                IsPaused = false;
                lastResumeTime = DateTime.Now;

                if (OnStatusChanged != null)
                {
                    OnStatusChanged(
                        append ? "Continuando grabaciÃ³n" : "Grabando",
                        append ? "Se aÃ±adirÃ¡ al final de tu texto actual" : "Corte automÃ¡tico cada 2 min (< 22 MB)",
                        "rec"
                    );
                }
                if (OnStateChanged != null) OnStateChanged();
                return true;
            }
            catch (Exception ex)
            {
                CleanupWaveIn();
                if (OnStatusChanged != null) OnStatusChanged("Error", ex.Message, "err");
                return false;
            }
        }

        public void PauseRecording()
        {
            if (!IsRecording || IsPaused || hWaveIn == IntPtr.Zero) return;

            IsPaused = true;
            accumulatedTime += DateTime.Now - lastResumeTime;
            waveInStop(hWaveIn);

            if (OnAudioLevel != null) OnAudioLevel(0);
            if (OnStatusChanged != null) OnStatusChanged("En pausa", "Pulsa 'Seguir Grabando' para reanudar o 'Finalizar'", "pause");
            if (OnStateChanged != null) OnStateChanged();
        }

        public void ResumeRecording()
        {
            if (!IsRecording || !IsPaused || hWaveIn == IntPtr.Zero) return;

            waveInStart(hWaveIn);
            IsPaused = false;
            lastResumeTime = DateTime.Now;

            if (OnStatusChanged != null) OnStatusChanged("Grabando", "GrabaciÃ³n reanudada sin perder audio", "rec");
            if (OnStateChanged != null) OnStateChanged();
        }

        public void PollAudioBuffers()
        {
            if (!IsRecording || hWaveIn == IntPtr.Zero) return;

            int maxSample = 0;
            if (waveBuffers != null)
            {
                for (int i = 0; i < waveBuffers.Length; i++)
                {
                    WaveBuffer buf = waveBuffers[i];
                    if (buf != null && buf.IsDone)
                    {
                        int peak;
                        byte[] recorded = buf.ReadRecordedBytes(out peak);
                        if (peak > maxSample) maxSample = peak;
                        if (peak > maxSessionPeak) maxSessionPeak = peak;

                        if (!IsPaused && recorded.Length > 0 && currentChunkStream != null)
                        {
                            currentChunkStream.Write(recorded, 0, recorded.Length);
                        }

                        buf.Queue();

                        if (!IsPaused && currentChunkStream != null &&
                            (currentChunkStream.Length >= CHUNK_MAX_BYTES || currentChunkStream.Length >= (MAX_SAFE_BYTES - 1024)))
                        {
                            DispatchCurrentChunk();
                        }
                    }
                }
            }

            if (IsPaused)
            {
                if (OnAudioLevel != null) OnAudioLevel(0);
            }
            else
            {
                double norm = Math.Min(1.0, maxSample / 14000.0);
                if (OnAudioLevel != null) OnAudioLevel(norm);
            }
        }

        private void DispatchCurrentChunk()
        {
            if (currentChunkStream == null || currentChunkStream.Length == 0) return;

            byte[] pcmBytes = currentChunkStream.ToArray();
            currentChunkStream.SetLength(0);

            int chunkIdx = ++currentChunkIndex;
            byte[] wavBytes = CreateWavFileBytes(pcmBytes, SAMPLE_RATE, CHANNELS, BITS_PER_SAMPLE);

            if (OnStatusChanged != null)
            {
                OnStatusChanged(
                    string.Format("Grabando (Tramo #{0})", chunkIdx + 1),
                    string.Format("Transcribiendo tramo #{0} ({1:F1} MB) en segundo plano...", chunkIdx, wavBytes.Length / (1024.0 * 1024.0)),
                    "rec"
                );
            }

            Task<ChunkResult> task = Task.Run(() => TranscribeWavChunkWithGroqAsync(wavBytes, chunkIdx));
            activeChunkTasks.Add(task);
        }

        public async Task<string> StopRecordingAndTranscribeAsync()
        {
            if (!IsPaused)
            {
                accumulatedTime += DateTime.Now - lastResumeTime;
            }

            IsRecording = false;
            IsPaused = false;
            IsProcessing = true;

            if (OnAudioLevel != null) OnAudioLevel(0);
            if (OnStatusChanged != null) OnStatusChanged("Transcribiendo...", "Procesando con Whisper Large v3...", "work");
            if (OnStateChanged != null) OnStateChanged();

            try
            {
                if (hWaveIn != IntPtr.Zero)
                {
                    waveInStop(hWaveIn);
                    waveInReset(hWaveIn);

                    if (waveBuffers != null)
                    {
                        for (int i = 0; i < waveBuffers.Length; i++)
                        {
                            int peak;
                            byte[] rem = waveBuffers[i].ReadRecordedBytes(out peak);
                            if (peak > maxSessionPeak) maxSessionPeak = peak;
                            if (rem.Length > 0 && currentChunkStream != null)
                            {
                                currentChunkStream.Write(rem, 0, rem.Length);
                            }
                        }
                    }

                    CleanupWaveIn();
                }

                // Evitar alucinaciones de Whisper ("Thank you.") cuando solo se grabÃ³ silencio total
                if (activeChunkTasks.Count == 0 && maxSessionPeak < 280)
                {
                    if (currentChunkStream != null)
                    {
                        currentChunkStream.Dispose();
                        currentChunkStream = null;
                    }
                    if (OnStatusChanged != null) OnStatusChanged("Sin voz detectada", "No se detectÃ³ sonido en el micrÃ³fono", "idle");
                    return "";
                }

                if (currentChunkStream != null && currentChunkStream.Length > (BYTES_PER_SECOND / 5))
                {
                    byte[] remainingPcm = currentChunkStream.ToArray();
                    currentChunkStream.Dispose();
                    currentChunkStream = null;

                    int offset = 0;
                    while (offset < remainingPcm.Length)
                    {
                        int length = Math.Min(CHUNK_MAX_BYTES, remainingPcm.Length - offset);
                        byte[] slice = new byte[length];
                        Buffer.BlockCopy(remainingPcm, offset, slice, 0, length);
                        offset += length;

                        int chunkIdx = ++currentChunkIndex;
                        byte[] wavBytes = CreateWavFileBytes(slice, SAMPLE_RATE, CHANNELS, BITS_PER_SAMPLE);
                        activeChunkTasks.Add(Task.Run(() => TranscribeWavChunkWithGroqAsync(wavBytes, chunkIdx)));
                    }
                }

                if (activeChunkTasks.Count == 0)
                {
                    if (OnStatusChanged != null) OnStatusChanged("GrabaciÃ³n muy corta", "MantÃ©n la grabaciÃ³n al menos 1 segundo", "idle");
                    return "";
                }

                ChunkResult[] results = await Task.WhenAll(activeChunkTasks);
                var ordered = results.OrderBy(r => r.Index).ToList();
                var errors = ordered.Where(r => !r.Success).Select(r => r.Error).ToList();
                var texts = ordered.Where(r => r.Success && !string.IsNullOrWhiteSpace(r.Text)).Select(r => r.Text.Trim()).ToList();

                string newTranscript = string.Join(" ", texts).Trim();

                // Filtrar alucinaciones tÃ­picas de silencio de Whisper
                if (maxSessionPeak < 450 && (newTranscript.Equals("Thank you.", StringComparison.OrdinalIgnoreCase) || newTranscript.Equals("Gracias.", StringComparison.OrdinalIgnoreCase)))
                {
                    newTranscript = "";
                }

                if (!string.IsNullOrEmpty(newTranscript))
                {
                    if (AppendMode && !string.IsNullOrWhiteSpace(AccumulatedText))
                    {
                        AccumulatedText = AccumulatedText.TrimEnd() + " " + newTranscript;
                    }
                    else
                    {
                        AccumulatedText = newTranscript;
                    }

                    if (OnTextUpdated != null) OnTextUpdated(AccumulatedText);

                    try
                    {
                        Clipboard.SetText(AccumulatedText);
                    }
                    catch { }

                    if (OnStatusChanged != null)
                    {
                        OnStatusChanged("Copiado al portapapeles", "Listo para pegar (Ctrl + V) o seguir grabando", "done");
                    }
                }
                else if (errors.Count > 0)
                {
                    if (OnStatusChanged != null) OnStatusChanged("Error de red", errors[0], "err");
                }
                else
                {
                    if (OnStatusChanged != null) OnStatusChanged("Sin voz detectada", "Intenta hablar mÃ¡s cerca del micrÃ³fono", "idle");
                }

                return newTranscript;
            }
            catch (Exception ex)
            {
                if (OnStatusChanged != null) OnStatusChanged("Error", ex.Message, "err");
                return "";
            }
            finally
            {
                IsProcessing = false;
                if (OnStateChanged != null) OnStateChanged();
            }
        }

        private async Task<ChunkResult> TranscribeWavChunkWithGroqAsync(byte[] wavBytes, int index)
        {
            for (int attempt = 1; attempt <= 2; attempt++)
            {
                try
                {
                    using (var request = new HttpRequestMessage(HttpMethod.Post, GROQ_ENDPOINT))
                    {
                        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", GROQ_API_KEY);

                        using (var content = new MultipartFormDataContent())
                        {
                            var fileContent = new ByteArrayContent(wavBytes);
                            fileContent.Headers.ContentType = new MediaTypeHeaderValue("audio/wav");
                            content.Add(fileContent, "file", string.Format("chunk_{0}.wav", index));
                            content.Add(new StringContent(WHISPER_MODEL), "model");
                            content.Add(new StringContent("0"), "temperature");
                            content.Add(new StringContent("json"), "response_format");

                            request.Content = content;

                            using (HttpResponseMessage response = await httpClient.SendAsync(request))
                            {
                                byte[] rawBytes = await response.Content.ReadAsByteArrayAsync();
                                string body = Encoding.UTF8.GetString(rawBytes);

                                if (!response.IsSuccessStatusCode)
                                {
                                    string errMsg = body;
                                    try
                                    {
                                        var serializer = new JavaScriptSerializer();
                                        var dict = serializer.Deserialize<Dictionary<string, object>>(body);
                                        if (dict != null && dict.ContainsKey("error"))
                                        {
                                            var errObj = dict["error"] as Dictionary<string, object>;
                                            if (errObj != null && errObj.ContainsKey("message"))
                                            {
                                                errMsg = Convert.ToString(errObj["message"]);
                                            }
                                        }
                                    }
                                    catch { }

                                    if (attempt < 2 && ((int)response.StatusCode >= 500 || (int)response.StatusCode == 429))
                                    {
                                        await Task.Delay(1000);
                                        continue;
                                    }

                                    return new ChunkResult { Index = index, Success = false, Error = errMsg };
                                }

                                var js = new JavaScriptSerializer();
                                var data = js.Deserialize<Dictionary<string, object>>(body);
                                string transcribedText = (data != null && data.ContainsKey("text")) ? Convert.ToString(data["text"]) : "";

                                return new ChunkResult { Index = index, Success = true, Text = transcribedText };
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    if (attempt >= 2)
                    {
                        return new ChunkResult { Index = index, Success = false, Error = ex.Message };
                    }
                }

                await Task.Delay(900);
            }

            return new ChunkResult { Index = index, Success = false, Error = "Fallo desconocido" };
        }

        private static byte[] CreateWavFileBytes(byte[] pcmData, int sampleRate, short channels, short bitsPerSample)
        {
            using (var ms = new MemoryStream(44 + pcmData.Length))
            using (var bw = new BinaryWriter(ms))
            {
                short blockAlign = (short)(channels * (bitsPerSample / 8));
                int byteRate = sampleRate * blockAlign;

                bw.Write(Encoding.ASCII.GetBytes("RIFF"));
                bw.Write(36 + pcmData.Length);
                bw.Write(Encoding.ASCII.GetBytes("WAVE"));

                bw.Write(Encoding.ASCII.GetBytes("fmt "));
                bw.Write(16);
                bw.Write((short)1);
                bw.Write(channels);
                bw.Write(sampleRate);
                bw.Write(byteRate);
                bw.Write(blockAlign);
                bw.Write(bitsPerSample);

                bw.Write(Encoding.ASCII.GetBytes("data"));
                bw.Write(pcmData.Length);
                bw.Write(pcmData);

                return ms.ToArray();
            }
        }

        public void CleanupWaveIn()
        {
            if (waveBuffers != null)
            {
                for (int i = 0; i < waveBuffers.Length; i++)
                {
                    if (waveBuffers[i] != null) waveBuffers[i].Dispose();
                }
                waveBuffers = null;
            }

            if (hWaveIn != IntPtr.Zero)
            {
                waveInClose(hWaveIn);
                hWaveIn = IntPtr.Zero;
            }
        }

        public class ChunkResult
        {
            public int Index { get; set; }
            public bool Success { get; set; }
            public string Text { get; set; }
            public string Error { get; set; }
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct WAVEFORMATEX
        {
            public ushort wFormatTag;
            public ushort nChannels;
            public uint nSamplesPerSec;
            public uint nAvgBytesPerSec;
            public ushort nBlockAlign;
            public ushort wBitsPerSample;
            public ushort cbSize;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct WAVEHDR
        {
            public IntPtr lpData;
            public uint dwBufferLength;
            public uint dwBytesRecorded;
            public IntPtr dwUser;
            public uint dwFlags;
            public uint dwLoops;
            public IntPtr lpNext;
            public IntPtr reserved;
        }

        [DllImport("winmm.dll")]
        private static extern int waveInOpen(out IntPtr phwi, int uDeviceID, ref WAVEFORMATEX pwfx, IntPtr dwCallback, IntPtr dwInstance, uint fdwOpen);

        [DllImport("winmm.dll")]
        private static extern int waveInPrepareHeader(IntPtr hwi, IntPtr pwh, uint cbwh);

        [DllImport("winmm.dll")]
        private static extern int waveInUnprepareHeader(IntPtr hwi, IntPtr pwh, uint cbwh);

        [DllImport("winmm.dll")]
        private static extern int waveInAddBuffer(IntPtr hwi, IntPtr pwh, uint cbwh);

        [DllImport("winmm.dll")]
        private static extern int waveInStart(IntPtr hwi);

        [DllImport("winmm.dll")]
        private static extern int waveInStop(IntPtr hwi);

        [DllImport("winmm.dll")]
        private static extern int waveInReset(IntPtr hwi);

        [DllImport("winmm.dll")]
        private static extern int waveInClose(IntPtr hwi);

        private sealed class WaveBuffer : IDisposable
        {
            private const uint WHDR_DONE = 0x00000001;
            private const uint WHDR_PREPARED = 0x00000002;

            private IntPtr hWaveIn;
            private byte[] managedBuffer;
            private GCHandle bufferHandle;
            private IntPtr headerPtr;
            private uint headerSize;
            private bool isQueued;

            public WaveBuffer(IntPtr waveInHandle, int bufferSize)
            {
                hWaveIn = waveInHandle;
                managedBuffer = new byte[bufferSize];
                bufferHandle = GCHandle.Alloc(managedBuffer, GCHandleType.Pinned);

                WAVEHDR hdr = new WAVEHDR
                {
                    lpData = bufferHandle.AddrOfPinnedObject(),
                    dwBufferLength = (uint)bufferSize,
                    dwBytesRecorded = 0,
                    dwFlags = 0
                };

                headerSize = (uint)Marshal.SizeOf(typeof(WAVEHDR));
                headerPtr = Marshal.AllocHGlobal((int)headerSize);
                Marshal.StructureToPtr(hdr, headerPtr, false);

                waveInPrepareHeader(hWaveIn, headerPtr, headerSize);
            }

            public bool IsDone
            {
                get
                {
                    if (!isQueued || headerPtr == IntPtr.Zero) return false;
                    WAVEHDR hdr = (WAVEHDR)Marshal.PtrToStructure(headerPtr, typeof(WAVEHDR));
                    return (hdr.dwFlags & WHDR_DONE) != 0;
                }
            }

            public byte[] ReadRecordedBytes(out int peakAmplitude)
            {
                peakAmplitude = 0;
                if (headerPtr == IntPtr.Zero) return new byte[0];

                WAVEHDR hdr = (WAVEHDR)Marshal.PtrToStructure(headerPtr, typeof(WAVEHDR));
                int bytesRecorded = (int)hdr.dwBytesRecorded;
                isQueued = false;

                if (bytesRecorded <= 0 || bytesRecorded > managedBuffer.Length)
                {
                    return new byte[0];
                }

                byte[] result = new byte[bytesRecorded];
                Buffer.BlockCopy(managedBuffer, 0, result, 0, bytesRecorded);

                for (int i = 0; i + 1 < bytesRecorded; i += 2)
                {
                    short sample = (short)(result[i] | (result[i + 1] << 8));
                    int abs = sample == short.MinValue ? short.MaxValue : Math.Abs(sample);
                    if (abs > peakAmplitude) peakAmplitude = abs;
                }

                return result;
            }

            public void Queue()
            {
                if (headerPtr == IntPtr.Zero) return;

                WAVEHDR hdr = (WAVEHDR)Marshal.PtrToStructure(headerPtr, typeof(WAVEHDR));
                hdr.dwBytesRecorded = 0;
                hdr.dwFlags &= ~WHDR_DONE;
                Marshal.StructureToPtr(hdr, headerPtr, false);

                if ((hdr.dwFlags & WHDR_PREPARED) == 0)
                {
                    waveInPrepareHeader(hWaveIn, headerPtr, headerSize);
                }

                int res = waveInAddBuffer(hWaveIn, headerPtr, headerSize);
                if (res == 0)
                {
                    isQueued = true;
                }
            }

            public void Dispose()
            {
                if (headerPtr != IntPtr.Zero)
                {
                    waveInUnprepareHeader(hWaveIn, headerPtr, headerSize);
                    Marshal.FreeHGlobal(headerPtr);
                    headerPtr = IntPtr.Zero;
                }
                if (bufferHandle.IsAllocated)
                {
                    bufferHandle.Free();
                }
            }
        }
    }

    // ==========================================
    // COMPONENTES VISUALES WPF ESTILO APPLE PRO BLACK
    // ==========================================
    public static class AppleTheme
    {
        public static SolidColorBrush BrushFromHex(string hex)
        {
            return (SolidColorBrush)new BrushConverter().ConvertFromString(hex);
        }

        public static Color ColorFromHex(string hex)
        {
            return (Color)ColorConverter.ConvertFromString(hex);
        }

        // Apple Pro Dark Monochrome Palette
        public static readonly SolidColorBrush BgBlack = BrushFromHex("#050507");
        public static readonly SolidColorBrush CardDark = BrushFromHex("#111114");
        public static readonly SolidColorBrush CardInner = BrushFromHex("#0A0A0D");
        public static readonly SolidColorBrush BorderSubtle = BrushFromHex("#222228");
        public static readonly SolidColorBrush BorderHighlight = BrushFromHex("#34343D");

        public static readonly SolidColorBrush TextWhite = BrushFromHex("#F5F5F7");
        public static readonly SolidColorBrush TextMuted = BrushFromHex("#86868B");
        public static readonly SolidColorBrush TextDim = BrushFromHex("#515158");

        public static readonly SolidColorBrush BtnWhiteBg = BrushFromHex("#F5F5F7");
        public static readonly SolidColorBrush BtnWhiteHover = BrushFromHex("#FFFFFF");
        public static readonly SolidColorBrush BtnDarkBg = BrushFromHex("#1C1C21");
        public static readonly SolidColorBrush BtnDarkHover = BrushFromHex("#282830");

        public static readonly SolidColorBrush AppleRed = BrushFromHex("#FF3B30");
        public static readonly SolidColorBrush AppleRedHover = BrushFromHex("#FF453A");
        public static readonly SolidColorBrush AppleGreen = BrushFromHex("#30D158");
        public static readonly SolidColorBrush AppleAmber = BrushFromHex("#FF9F0A");

        public static void AnimateWindowIn(Window win, ScaleTransform scale)
        {
            win.Opacity = 0;
            scale.ScaleX = 0.93;
            scale.ScaleY = 0.93;

            var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
            var dur = new Duration(TimeSpan.FromMilliseconds(260));

            win.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0, 1, dur) { EasingFunction = ease });
            scale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(0.93, 1.0, dur) { EasingFunction = ease });
            scale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(0.93, 1.0, dur) { EasingFunction = ease });
        }

        public static void AnimateWindowOut(Window win, ScaleTransform scale, Action onCompleted)
        {
            var ease = new CubicEase { EasingMode = EasingMode.EaseIn };
            var dur = new Duration(TimeSpan.FromMilliseconds(190));

            var fadeOut = new DoubleAnimation(win.Opacity, 0, dur) { EasingFunction = ease };
            fadeOut.Completed += (s, e) =>
            {
                if (onCompleted != null) onCompleted();
            };

            win.BeginAnimation(UIElement.OpacityProperty, fadeOut);
            scale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(1.0, 0.93, dur) { EasingFunction = ease });
            scale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(1.0, 0.93, dur) { EasingFunction = ease });
        }
    }

    // BotÃ³n Apple con esquinas vectoriales perfectas y animaciones suaves
    public class AppleButton : Border
    {
        private TextBlock labelBlock;
        private SolidColorBrush normalBg;
        private SolidColorBrush hoverBg;
        private SolidColorBrush normalFg;
        private bool isDisabled;

        public event RoutedEventHandler Click;

        public string Text
        {
            get { return labelBlock.Text; }
            set { labelBlock.Text = value; }
        }

        public bool IsDisabled
        {
            get { return isDisabled; }
            set
            {
                isDisabled = value;
                this.Opacity = isDisabled ? 0.42 : 1.0;
                this.Cursor = isDisabled ? Cursors.Arrow : Cursors.Hand;
            }
        }

        public AppleButton(string text, double radius, SolidColorBrush bg, SolidColorBrush hover, SolidColorBrush fg, double fontSize = 13.5, FontWeight weight = default(FontWeight))
        {
            if (weight == default(FontWeight)) weight = FontWeights.SemiBold;

            normalBg = bg;
            hoverBg = hover;
            normalFg = fg;

            this.CornerRadius = new CornerRadius(radius);
            this.Background = normalBg;
            this.BorderBrush = new SolidColorBrush(Color.FromArgb(28, 255, 255, 255));
            this.BorderThickness = new Thickness(1);
            this.Cursor = Cursors.Hand;
            this.SnapsToDevicePixels = true;

            var scale = new ScaleTransform(1, 1);
            this.RenderTransformOrigin = new Point(0.5, 0.5);
            this.RenderTransform = scale;

            labelBlock = new TextBlock
            {
                Text = text,
                Foreground = normalFg,
                FontSize = fontSize,
                FontWeight = weight,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                TextAlignment = TextAlignment.Center
            };
            this.Child = labelBlock;

            this.MouseEnter += (s, e) =>
            {
                if (isDisabled) return;
                this.Background = hoverBg;
            };

            this.MouseLeave += (s, e) =>
            {
                if (isDisabled) return;
                this.Background = normalBg;
                AnimateScale(scale, 1.0);
            };

            this.MouseLeftButtonDown += (s, e) =>
            {
                if (isDisabled) return;
                e.Handled = true;
                AnimateScale(scale, 0.96);
            };

            this.MouseLeftButtonUp += (s, e) =>
            {
                if (isDisabled) return;
                e.Handled = true;
                AnimateScale(scale, 1.0);
                if (Click != null) Click(this, new RoutedEventArgs());
            };
        }

        public void SetPalette(SolidColorBrush bg, SolidColorBrush hover, SolidColorBrush fg)
        {
            normalBg = bg;
            hoverBg = hover;
            normalFg = fg;
            this.Background = normalBg;
            labelBlock.Foreground = normalFg;
        }

        private static void AnimateScale(ScaleTransform st, double to)
        {
            var anim = new DoubleAnimation(to, new Duration(TimeSpan.FromMilliseconds(110)));
            st.BeginAnimation(ScaleTransform.ScaleXProperty, anim);
            st.BeginAnimation(ScaleTransform.ScaleYProperty, anim);
        }
    }

    // Visualizador de Onda estilo Apple Voice Memos / Dynamic Island (60 FPS)
    public class AppleWaveformVisualizer : Canvas
    {
        private readonly Rectangle[] bars;
        private readonly double[] currentHeights;
        private readonly double[] targetHeights;
        private readonly Random rng = new Random();
        private double currentAudioLevel = 0;
        private bool isRecordingActive = false;
        private double phase = 0;
        private readonly int barCount;

        public AppleWaveformVisualizer(int count)
        {
            barCount = count;
            bars = new Rectangle[barCount];
            currentHeights = new double[barCount];
            targetHeights = new double[barCount];
            this.ClipToBounds = true;

            for (int i = 0; i < barCount; i++)
            {
                currentHeights[i] = 4;
                targetHeights[i] = 4;
                var rect = new Rectangle
                {
                    RadiusX = 2,
                    RadiusY = 2,
                    Fill = new SolidColorBrush(Color.FromArgb(90, 245, 245, 247))
                };
                bars[i] = rect;
                this.Children.Add(rect);
            }

            this.SizeChanged += (s, e) => LayoutBars();
        }

        public void SetState(double level, bool recording)
        {
            currentAudioLevel = level;
            isRecordingActive = recording;
        }

        public void StepFrame()
        {
            phase += 0.14;
            double hAvail = Math.Max(14, this.ActualHeight - 10);

            for (int i = 0; i < barCount; i++)
            {
                double centerDist = Math.Abs(i - (barCount - 1) / 2.0) / (barCount / 2.0);
                double envelope = Math.Max(0.15, 1.0 - (centerDist * 0.78));

                if (!isRecordingActive)
                {
                    targetHeights[i] = 3.5 + Math.Sin(phase * 0.6 + i * 0.35) * 1.2;
                    bars[i].Fill = new SolidColorBrush(Color.FromArgb(65, 245, 245, 247));
                }
                else if (currentAudioLevel < 0.03)
                {
                    // RespiraciÃ³n sutil cuando estÃ¡s en silencio mientras grabas
                    targetHeights[i] = 5.0 + Math.Sin(phase + i * 0.45) * 2.5 * envelope;
                    bars[i].Fill = new SolidColorBrush(Color.FromArgb(165, 255, 69, 58));
                }
                else
                {
                    double wave = 0.65 + 0.35 * Math.Sin(phase * 1.9 + i * 0.55);
                    double jitter = 0.75 + rng.NextDouble() * 0.5;
                    double normalized = Math.Min(1.0, currentAudioLevel * envelope * wave * jitter * 1.45);
                    targetHeights[i] = Math.Max(5.0, normalized * hAvail);
                    bars[i].Fill = new SolidColorBrush(Color.FromArgb(245, 255, 255, 255));
                }

                currentHeights[i] = currentHeights[i] + (targetHeights[i] - currentHeights[i]) * 0.32;
            }

            LayoutBars();
        }

        private void LayoutBars()
        {
            double w = this.ActualWidth;
            double h = this.ActualHeight;
            if (w <= 10 || h <= 6) return;

            double totalBarSpace = w - 16;
            double step = totalBarSpace / barCount;
            double barW = Math.Max(2.5, Math.Min(4.5, step * 0.48));

            for (int i = 0; i < barCount; i++)
            {
                double bh = Math.Max(3.5, Math.Min(h - 4, currentHeights[i]));
                double x = 8 + i * step + (step - barW) / 2.0;
                double y = (h - bh) / 2.0;

                bars[i].Width = barW;
                bars[i].Height = bh;
                bars[i].RadiusX = barW / 2.0;
                bars[i].RadiusY = barW / 2.0;
                Canvas.SetLeft(bars[i], x);
                Canvas.SetTop(bars[i], y);
            }
        }
    }

    // ==========================================
    // VENTANA PRINCIPAL: TRANSCRIPTOR (ESTILO APPLE PRO BLACK)
    // ==========================================
    public class MainWindow : Window
    {
        private AudioRecorderEngine engine;
        private PillWindow pillWindow;
        private ScaleTransform windowScale;

        private Ellipse statusDot;
        private TextBlock lblStatusTitle;
        private TextBlock lblStatusSub;
        private TextBlock lblTimer;
        private TextBlock lblWordCount;
        private AppleWaveformVisualizer waveform;
        private AppleButton btnRecord;
        private AppleButton btnContinue;
        private AppleButton btnCopy;
        private AppleButton btnPillMode;
        private AppleButton btnStartup;
        private AppleButton btnClear;
        private TextBox txtOutput;
        private DispatcherTimer mainTimer;

        private const int HOTKEY_ID = 9001;
        private const uint MOD_ALT = 0x0001;
        private const uint VK_M = 0x4D;

        [DllImport("user32.dll")]
        private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

        [DllImport("user32.dll")]
        private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

        public MainWindow()
        {
            string exePath = System.Reflection.Assembly.GetExecutingAssembly().Location;
            if (!StartupManager.IsStartupEnabled())
            {
                StartupManager.SetStartup(true, exePath);
            }

            engine = new AudioRecorderEngine();
            engine.OnAudioLevel += (lvl) => waveform.SetState(lvl, engine.IsRecording && !engine.IsPaused);
            engine.OnStatusChanged += UpdateStatusUI;
            engine.OnTextUpdated += UpdateTextUI;
            engine.OnStateChanged += SyncButtonsState;

            BuildAppleBlackUI();

            pillWindow = new PillWindow(engine, this);

            mainTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(25) };
            mainTimer.Tick += (s, e) =>
            {
                engine.PollAudioBuffers();
                TimeSpan t = engine.TotalElapsedTime;
                lblTimer.Text = string.Format("{0:D2}:{1:D2}", (int)t.TotalMinutes, t.Seconds);
                if (this.IsVisible)
                {
                    waveform.StepFrame();
                }
            };
            mainTimer.Start();

            this.Loaded += (s, e) =>
            {
                AppleTheme.AnimateWindowIn(this, windowScale);
                RegisterGlobalHotkey();
            };
        }

        private void RegisterGlobalHotkey()
        {
            try
            {
                IntPtr handle = new WindowInteropHelper(this).Handle;
                HwndSource source = HwndSource.FromHwnd(handle);
                if (source != null) source.AddHook(HwndHook);
                RegisterHotKey(handle, HOTKEY_ID, MOD_ALT, VK_M);
            }
            catch { }
        }

        private IntPtr HwndHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            const int WM_HOTKEY = 0x0312;
            if (msg == WM_HOTKEY && wParam.ToInt32() == HOTKEY_ID)
            {
                TriggerHotkeyToggle();
                handled = true;
            }
            return IntPtr.Zero;
        }

        public async void TriggerHotkeyToggle()
        {
            if (engine.IsProcessing) return;
            if (!engine.IsRecording)
            {
                bool append = !string.IsNullOrWhiteSpace(engine.AccumulatedText);
                engine.StartRecording(append);
            }
            else
            {
                await engine.StopRecordingAndTranscribeAsync();
            }
        }

        private void BuildAppleBlackUI()
        {
            this.Title = "Transcriptor";
            this.Width = 520;
            this.Height = 640;
            this.WindowStartupLocation = WindowStartupLocation.CenterScreen;
            this.WindowStyle = WindowStyle.None;
            this.AllowsTransparency = true;
            this.Background = Brushes.Transparent;
            this.ResizeMode = ResizeMode.NoResize;
            this.FontFamily = new FontFamily("Segoe UI, -apple-system, BlinkMacSystemFont");

            windowScale = new ScaleTransform(1, 1);

            // Marco exterior con sombra suave y esquinas vectoriales de 24px (Cero bordes cortados)
            var outerShell = new Border
            {
                Margin = new Thickness(16),
                CornerRadius = new CornerRadius(24),
                Background = AppleTheme.BgBlack,
                BorderBrush = AppleTheme.BorderSubtle,
                BorderThickness = new Thickness(1),
                RenderTransformOrigin = new Point(0.5, 0.5),
                RenderTransform = windowScale,
                Effect = new DropShadowEffect
                {
                    Color = Colors.Black,
                    BlurRadius = 28,
                    ShadowDepth = 8,
                    Opacity = 0.78
                }
            };

            var rootGrid = new Grid { Margin = new Thickness(22, 16, 22, 22) };
            rootGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(44) });  // Fila 0: Barra superior Apple
            rootGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(154) }); // Fila 1: Tarjeta Estado + Onda de voz
            rootGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(64) });  // Fila 2: Botones Grabar / Seguir Grabando
            rootGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) }); // Fila 3: TranscripciÃ³n
            rootGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(56) });  // Fila 4: Copiar al portapapeles

            // ==========================================
            // FILA 0: CABECERA SUPERIOR (TÃTULO + MODO PÃLDORA + CERRAR)
            // ==========================================
            var headerGrid = new Grid { Background = Brushes.Transparent };
            headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            headerGrid.MouseLeftButtonDown += (s, e) =>
            {
                if (e.ButtonState == MouseButtonState.Pressed) this.DragMove();
            };

            var titleStack = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            var appDot = new Ellipse
            {
                Width = 8,
                Height = 8,
                Fill = AppleTheme.TextWhite,
                Margin = new Thickness(2, 0, 10, 0),
                VerticalAlignment = VerticalAlignment.Center
            };
            var lblAppName = new TextBlock
            {
                Text = "Transcriptor",
                Foreground = AppleTheme.TextWhite,
                FontSize = 15.5,
                FontWeight = FontWeights.SemiBold,
                VerticalAlignment = VerticalAlignment.Center
            };
            titleStack.Children.Add(appDot);
            titleStack.Children.Add(lblAppName);

            var controlsStack = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };

            btnStartup = new AppleButton(
                StartupManager.IsStartupEnabled() ? "Inicio PC: ON" : "Inicio PC: OFF",
                13,
                AppleTheme.CardDark,
                AppleTheme.BtnDarkHover,
                StartupManager.IsStartupEnabled() ? AppleTheme.AppleGreen : AppleTheme.TextMuted,
                11,
                FontWeights.Medium
            )
            {
                Width = 92,
                Height = 28,
                Margin = new Thickness(0, 0, 8, 0),
                ToolTip = "Activar o desactivar que Transcriptor inicie automÃ¡ticamente al encender el PC"
            };
            btnStartup.Click += (s, e) =>
            {
                string exePath = System.Reflection.Assembly.GetExecutingAssembly().Location;
                bool next = !StartupManager.IsStartupEnabled();
                StartupManager.SetStartup(next, exePath);
                btnStartup.Text = next ? "Inicio PC: ON" : "Inicio PC: OFF";
                btnStartup.SetPalette(AppleTheme.CardDark, AppleTheme.BtnDarkHover, next ? AppleTheme.AppleGreen : AppleTheme.TextMuted);
            };

            btnPillMode = new AppleButton(
                "Modo PÃ­ldora",
                13,
                AppleTheme.BtnDarkBg,
                AppleTheme.BtnDarkHover,
                AppleTheme.TextWhite,
                11.5,
                FontWeights.Medium
            )
            {
                Width = 104,
                Height = 28,
                Margin = new Thickness(0, 0, 10, 0),
                ToolTip = "Cambiar a la barra flotante compacta (Dynamic Island)"
            };
            btnPillMode.Click += (s, e) => SwitchToPillMode();

            var btnMin = new AppleButton("â€”", 14, AppleTheme.CardDark, AppleTheme.BtnDarkHover, AppleTheme.TextMuted, 11, FontWeights.Bold)
            {
                Width = 28,
                Height = 28,
                Margin = new Thickness(0, 0, 6, 0),
                ToolTip = "Minimizar"
            };
            btnMin.Click += (s, e) => this.WindowState = WindowState.Minimized;

            var btnClose = new AppleButton("âœ•", 14, AppleTheme.CardDark, AppleTheme.AppleRed, AppleTheme.TextMuted, 11, FontWeights.Bold)
            {
                Width = 28,
                Height = 28,
                ToolTip = "Cerrar Transcriptor"
            };
            btnClose.Click += (s, e) => CloseAppWithAnimation();

            controlsStack.Children.Add(btnStartup);
            controlsStack.Children.Add(btnPillMode);
            controlsStack.Children.Add(btnMin);
            controlsStack.Children.Add(btnClose);

            Grid.SetColumn(titleStack, 0);
            Grid.SetColumn(controlsStack, 1);
            headerGrid.Children.Add(titleStack);
            headerGrid.Children.Add(controlsStack);

            // ==========================================
            // FILA 1: TARJETA DE ESTADO Y ONDA APPLE VOICE MEMOS
            // ==========================================
            var heroCard = new Border
            {
                CornerRadius = new CornerRadius(18),
                Background = AppleTheme.CardDark,
                BorderBrush = AppleTheme.BorderSubtle,
                BorderThickness = new Thickness(1),
                Padding = new Thickness(18, 14, 18, 14),
                Margin = new Thickness(0, 8, 0, 12)
            };

            var heroGrid = new Grid();
            heroGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            heroGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

            var statusTopGrid = new Grid();
            statusTopGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            statusTopGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var statusTextStack = new StackPanel();
            var statusHeaderRow = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };

            statusDot = new Ellipse
            {
                Width = 8,
                Height = 8,
                Fill = AppleTheme.AppleGreen,
                Margin = new Thickness(0, 1, 8, 0),
                VerticalAlignment = VerticalAlignment.Center
            };

            lblStatusTitle = new TextBlock
            {
                Text = "Listo",
                Foreground = AppleTheme.TextWhite,
                FontSize = 14,
                FontWeight = FontWeights.SemiBold
            };

            statusHeaderRow.Children.Add(statusDot);
            statusHeaderRow.Children.Add(lblStatusTitle);

            lblStatusSub = new TextBlock
            {
                Text = "Trozos automÃ¡ticos de 2 min â€¢ Alt + M",
                Foreground = AppleTheme.TextMuted,
                FontSize = 11.5,
                Margin = new Thickness(0, 3, 0, 0)
            };

            statusTextStack.Children.Add(statusHeaderRow);
            statusTextStack.Children.Add(lblStatusSub);

            lblTimer = new TextBlock
            {
                Text = "00:00",
                Foreground = AppleTheme.TextWhite,
                FontFamily = new FontFamily("Consolas"),
                FontSize = 26,
                FontWeight = FontWeights.Bold,
                VerticalAlignment = VerticalAlignment.Center
            };

            Grid.SetColumn(statusTextStack, 0);
            Grid.SetColumn(lblTimer, 1);
            statusTopGrid.Children.Add(statusTextStack);
            statusTopGrid.Children.Add(lblTimer);

            var waveBorder = new Border
            {
                CornerRadius = new CornerRadius(12),
                Background = AppleTheme.CardInner,
                BorderBrush = new SolidColorBrush(Color.FromArgb(18, 255, 255, 255)),
                BorderThickness = new Thickness(1),
                Margin = new Thickness(0, 12, 0, 0),
                Padding = new Thickness(4, 2, 4, 2)
            };

            waveform = new AppleWaveformVisualizer(40);
            waveBorder.Child = waveform;

            Grid.SetRow(statusTopGrid, 0);
            Grid.SetRow(waveBorder, 1);
            heroGrid.Children.Add(statusTopGrid);
            heroGrid.Children.Add(waveBorder);
            heroCard.Child = heroGrid;

            // ==========================================
            // FILA 2: BOTONES DE ACCIÃ“N (EMPEZAR + SEGUIR GRABANDO)
            // ==========================================
            var actionGrid = new Grid { Margin = new Thickness(0, 0, 0, 12) };
            actionGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.05, GridUnitType.Star) });
            actionGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(12) });
            actionGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(0.95, GridUnitType.Star) });

            btnRecord = new AppleButton(
                "Empezar GrabaciÃ³n",
                15,
                AppleTheme.BtnWhiteBg,
                AppleTheme.BtnWhiteHover,
                AppleTheme.BgBlack,
                13.5,
                FontWeights.SemiBold
            );
            btnRecord.Click += async (s, e) =>
            {
                if (engine.IsProcessing) return;
                if (!engine.IsRecording)
                {
                    engine.StartRecording(append: false);
                }
                else
                {
                    await engine.StopRecordingAndTranscribeAsync();
                }
            };

            btnContinue = new AppleButton(
                "Seguir Grabando",
                15,
                AppleTheme.BtnDarkBg,
                AppleTheme.BtnDarkHover,
                AppleTheme.TextWhite,
                13.5,
                FontWeights.Medium
            );
            btnContinue.Click += (s, e) =>
            {
                if (engine.IsProcessing) return;
                if (!engine.IsRecording)
                {
                    engine.StartRecording(append: true);
                }
                else if (!engine.IsPaused)
                {
                    engine.PauseRecording();
                }
                else
                {
                    engine.ResumeRecording();
                }
            };

            Grid.SetColumn(btnRecord, 0);
            Grid.SetColumn(btnContinue, 2);
            actionGrid.Children.Add(btnRecord);
            actionGrid.Children.Add(btnContinue);

            // ==========================================
            // FILA 3: TARJETA DE TRANSCRIPCIÃ“N SOBRIA
            // ==========================================
            var transcriptCard = new Border
            {
                CornerRadius = new CornerRadius(18),
                Background = AppleTheme.CardDark,
                BorderBrush = AppleTheme.BorderSubtle,
                BorderThickness = new Thickness(1),
                Padding = new Thickness(16, 12, 16, 14),
                Margin = new Thickness(0, 0, 0, 12)
            };

            var transcriptGrid = new Grid();
            transcriptGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(28) });
            transcriptGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

            var transcriptHeader = new Grid();
            transcriptHeader.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            transcriptHeader.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var metaStack = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            var lblSection = new TextBlock
            {
                Text = "TRANSCRIPCIÃ“N",
                Foreground = AppleTheme.TextMuted,
                FontSize = 11,
                FontWeight = FontWeights.SemiBold
            };
            lblWordCount = new TextBlock
            {
                Text = "  â€¢  0 palabras",
                Foreground = AppleTheme.TextDim,
                FontSize = 11
            };
            metaStack.Children.Add(lblSection);
            metaStack.Children.Add(lblWordCount);

            btnClear = new AppleButton("Limpiar", 10, AppleTheme.CardInner, AppleTheme.BtnDarkHover, AppleTheme.TextMuted, 11, FontWeights.Regular)
            {
                Width = 64,
                Height = 24
            };
            btnClear.Click += (s, e) =>
            {
                txtOutput.Text = "";
                engine.ResetSession();
                lblTimer.Text = "00:00";
            };

            Grid.SetColumn(metaStack, 0);
            Grid.SetColumn(btnClear, 1);
            transcriptHeader.Children.Add(metaStack);
            transcriptHeader.Children.Add(btnClear);

            var txtContainer = new Border
            {
                CornerRadius = new CornerRadius(12),
                Background = AppleTheme.CardInner,
                BorderBrush = new SolidColorBrush(Color.FromArgb(16, 255, 255, 255)),
                BorderThickness = new Thickness(1),
                Margin = new Thickness(0, 6, 0, 0),
                Padding = new Thickness(10, 8, 10, 8)
            };

            txtOutput = new TextBox
            {
                Background = Brushes.Transparent,
                Foreground = AppleTheme.TextWhite,
                CaretBrush = AppleTheme.TextWhite,
                BorderThickness = new Thickness(0),
                FontSize = 14,
                TextWrapping = TextWrapping.Wrap,
                AcceptsReturn = true,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto
            };
            txtOutput.TextChanged += (s, e) =>
            {
                engine.AccumulatedText = txtOutput.Text;
                UpdateWordCount();
            };
            txtContainer.Child = txtOutput;

            Grid.SetRow(transcriptHeader, 0);
            Grid.SetRow(txtContainer, 1);
            transcriptGrid.Children.Add(transcriptHeader);
            transcriptGrid.Children.Add(txtContainer);
            transcriptCard.Child = transcriptGrid;

            // ==========================================
            // FILA 4: BOTÃ“N COPIAR AL PORTAPAPELES
            // ==========================================
            btnCopy = new AppleButton(
                "Copiar al Portapapeles",
                15,
                AppleTheme.BtnDarkBg,
                AppleTheme.BtnDarkHover,
                AppleTheme.TextWhite,
                14,
                FontWeights.SemiBold
            );
            btnCopy.Click += (s, e) => CopyAllText();

            Grid.SetRow(headerGrid, 0);
            Grid.SetRow(heroCard, 1);
            Grid.SetRow(actionGrid, 2);
            Grid.SetRow(transcriptCard, 3);
            Grid.SetRow(btnCopy, 4);

            rootGrid.Children.Add(headerGrid);
            rootGrid.Children.Add(heroCard);
            rootGrid.Children.Add(actionGrid);
            rootGrid.Children.Add(transcriptCard);
            rootGrid.Children.Add(btnCopy);

            outerShell.Child = rootGrid;
            this.Content = outerShell;
        }

        private void CopyAllText()
        {
            string t = txtOutput.Text.Trim();
            if (string.IsNullOrEmpty(t))
            {
                UpdateStatusUI("Sin texto", "Graba algo primero para copiar al portapapeles", "pause");
                return;
            }

            try
            {
                Clipboard.SetText(t);
                FlashCopyButton();
                UpdateStatusUI("Copiado al portapapeles", "Todo tu texto estÃ¡ listo para pegar (Ctrl + V)", "done");
            }
            catch { }
        }

        private void FlashCopyButton()
        {
            btnCopy.Text = "âœ“  Copiado al Portapapeles";
            btnCopy.SetPalette(AppleTheme.AppleGreen, AppleTheme.AppleGreen, AppleTheme.BgBlack);

            var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1800) };
            timer.Tick += (s, e) =>
            {
                timer.Stop();
                btnCopy.Text = "Copiar al Portapapeles";
                btnCopy.SetPalette(AppleTheme.BtnDarkBg, AppleTheme.BtnDarkHover, AppleTheme.TextWhite);
            };
            timer.Start();
        }

        private void UpdateWordCount()
        {
            string t = txtOutput.Text.Trim();
            int count = string.IsNullOrEmpty(t) ? 0 : t.Split(new char[] { ' ', '\r', '\n', '\t' }, StringSplitOptions.RemoveEmptyEntries).Length;
            lblWordCount.Text = string.Format("  â€¢  {0} palabra{1}", count, count == 1 ? "" : "s");
        }

        private void UpdateStatusUI(string title, string sub, string tag)
        {
            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.BeginInvoke(new Action(() => UpdateStatusUI(title, sub, tag)));
                return;
            }

            lblStatusTitle.Text = title;
            lblStatusSub.Text = sub;

            if (tag == "rec") statusDot.Fill = AppleTheme.AppleRed;
            else if (tag == "pause" || tag == "work") statusDot.Fill = AppleTheme.AppleAmber;
            else if (tag == "err") statusDot.Fill = AppleTheme.AppleRed;
            else statusDot.Fill = AppleTheme.AppleGreen;

            if (tag == "done")
            {
                FlashCopyButton();
            }
        }

        private void UpdateTextUI(string fullText)
        {
            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.BeginInvoke(new Action(() => UpdateTextUI(fullText)));
                return;
            }
            txtOutput.Text = fullText;
            txtOutput.CaretIndex = txtOutput.Text.Length;
            txtOutput.ScrollToEnd();
            UpdateWordCount();
        }

        private void SyncButtonsState()
        {
            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.BeginInvoke(new Action(SyncButtonsState));
                return;
            }

            if (engine.IsProcessing)
            {
                btnRecord.IsDisabled = true;
                btnContinue.IsDisabled = true;
                btnRecord.Text = "Transcribiendo...";
                btnRecord.SetPalette(AppleTheme.BtnDarkBg, AppleTheme.BtnDarkHover, AppleTheme.TextMuted);
            }
            else if (engine.IsRecording)
            {
                btnRecord.IsDisabled = false;
                btnContinue.IsDisabled = false;

                btnRecord.Text = "Finalizar GrabaciÃ³n";
                btnRecord.SetPalette(AppleTheme.AppleRed, AppleTheme.AppleRedHover, AppleTheme.TextWhite);

                if (engine.IsPaused)
                {
                    btnContinue.Text = "Seguir Grabando";
                    btnContinue.SetPalette(AppleTheme.BtnWhiteBg, AppleTheme.BtnWhiteHover, AppleTheme.BgBlack);
                }
                else
                {
                    btnContinue.Text = "Pausar";
                    btnContinue.SetPalette(AppleTheme.BtnDarkBg, AppleTheme.BtnDarkHover, AppleTheme.TextWhite);
                }
            }
            else
            {
                btnRecord.IsDisabled = false;
                btnContinue.IsDisabled = false;

                btnRecord.Text = "Empezar GrabaciÃ³n";
                btnRecord.SetPalette(AppleTheme.BtnWhiteBg, AppleTheme.BtnWhiteHover, AppleTheme.BgBlack);

                btnContinue.Text = "Seguir Grabando";
                btnContinue.SetPalette(AppleTheme.BtnDarkBg, AppleTheme.BtnDarkHover, AppleTheme.TextWhite);
            }
        }

        public void SwitchToPillMode()
        {
            AppleTheme.AnimateWindowOut(this, windowScale, () =>
            {
                this.Hide();
                pillWindow.ShowWithAnimation();
            });
        }

        public void RestoreFromPillMode()
        {
            pillWindow.HideWithAnimation(() =>
            {
                this.Show();
                this.Activate();
                AppleTheme.AnimateWindowIn(this, windowScale);
            });
        }

        public void CloseAppWithAnimation()
        {
            AppleTheme.AnimateWindowOut(this, windowScale, () =>
            {
                engine.CleanupWaveIn();
                Application.Current.Shutdown();
            });
        }

        protected override void OnClosed(EventArgs e)
        {
            try
            {
                IntPtr handle = new WindowInteropHelper(this).Handle;
                UnregisterHotKey(handle, HOTKEY_ID);
            }
            catch { }
            engine.CleanupWaveIn();
            base.OnClosed(e);
        }
    }

    // ==========================================
    // MODO PÃLDORA (DYNAMIC ISLAND ESTILO APPLE)
    // ==========================================
    public class PillWindow : Window
    {
        private AudioRecorderEngine engine;
        private MainWindow parentMain;
        private ScaleTransform pillScale;
        private bool positionedOnce = false;

        private Ellipse recDot;
        private TextBlock lblPillTimer;
        private AppleWaveformVisualizer miniWave;
        private AppleButton btnRecToggle;
        private AppleButton btnContToggle;
        private AppleButton btnCopy;
        private AppleButton btnExpand;
        private AppleButton btnClosePill;
        private DispatcherTimer pillTimer;

        public PillWindow(AudioRecorderEngine eng, MainWindow main)
        {
            this.engine = eng;
            this.parentMain = main;

            BuildDynamicIslandUI();

            engine.OnAudioLevel += (lvl) => miniWave.SetState(lvl, engine.IsRecording && !engine.IsPaused);
            engine.OnStateChanged += SyncPillButtons;
            engine.OnStatusChanged += (title, sub, tag) =>
            {
                if (!Dispatcher.CheckAccess())
                {
                    Dispatcher.BeginInvoke(new Action(() => UpdatePillDot(tag)));
                    return;
                }
                UpdatePillDot(tag);
            };

            pillTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(30) };
            pillTimer.Tick += (s, e) =>
            {
                if (!this.IsVisible) return;
                TimeSpan t = engine.TotalElapsedTime;
                lblPillTimer.Text = string.Format("{0:D2}:{1:D2}", (int)t.TotalMinutes, t.Seconds);
                miniWave.StepFrame();
            };
            pillTimer.Start();
        }

        private void BuildDynamicIslandUI()
        {
            this.Title = "Transcriptor â€” PÃ­ldora";
            this.Width = 420;
            this.Height = 76;
            this.WindowStyle = WindowStyle.None;
            this.AllowsTransparency = true;
            this.Background = Brushes.Transparent;
            this.Topmost = true;
            this.ShowInTaskbar = false;
            this.ResizeMode = ResizeMode.NoResize;
            this.FontFamily = new FontFamily("Segoe UI, -apple-system, BlinkMacSystemFont");

            pillScale = new ScaleTransform(1, 1);

            var pillBorder = new Border
            {
                Margin = new Thickness(12),
                CornerRadius = new CornerRadius(26),
                Background = AppleTheme.BgBlack,
                BorderBrush = AppleTheme.BorderHighlight,
                BorderThickness = new Thickness(1),
                Padding = new Thickness(14, 0, 10, 0),
                RenderTransformOrigin = new Point(0.5, 0.5),
                RenderTransform = pillScale,
                Effect = new DropShadowEffect
                {
                    Color = Colors.Black,
                    BlurRadius = 20,
                    ShadowDepth = 5,
                    Opacity = 0.85
                }
            };

            pillBorder.MouseLeftButtonDown += (s, e) =>
            {
                if (e.ButtonState == MouseButtonState.Pressed) this.DragMove();
            };

            var row = new Grid { VerticalAlignment = VerticalAlignment.Center };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); // Dot + Reloj
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) }); // Onda
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); // Botones

            var leftInfo = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                VerticalAlignment = VerticalAlignment.Center,
                Cursor = Cursors.SizeAll
            };

            recDot = new Ellipse
            {
                Width = 8,
                Height = 8,
                Fill = AppleTheme.AppleGreen,
                Margin = new Thickness(2, 0, 8, 0),
                VerticalAlignment = VerticalAlignment.Center
            };

            lblPillTimer = new TextBlock
            {
                Text = "00:00",
                Foreground = AppleTheme.TextWhite,
                FontFamily = new FontFamily("Consolas"),
                FontSize = 14,
                FontWeight = FontWeights.Bold,
                VerticalAlignment = VerticalAlignment.Center
            };

            leftInfo.Children.Add(recDot);
            leftInfo.Children.Add(lblPillTimer);

            miniWave = new AppleWaveformVisualizer(14)
            {
                Height = 28,
                Margin = new Thickness(10, 0, 10, 0),
                Cursor = Cursors.SizeAll
            };

            var btnStack = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };

            btnRecToggle = new AppleButton("â—", 17, AppleTheme.BtnWhiteBg, AppleTheme.BtnWhiteHover, AppleTheme.AppleRed, 13, FontWeights.Bold)
            {
                Width = 34,
                Height = 34,
                Margin = new Thickness(0, 0, 6, 0),
                ToolTip = "Empezar / Finalizar grabaciÃ³n (Alt + M)"
            };
            btnRecToggle.Click += async (s, e) =>
            {
                if (engine.IsProcessing) return;
                if (!engine.IsRecording) engine.StartRecording(append: false);
                else await engine.StopRecordingAndTranscribeAsync();
            };

            btnContToggle = new AppleButton("+", 17, AppleTheme.BtnDarkBg, AppleTheme.BtnDarkHover, AppleTheme.TextWhite, 15, FontWeights.SemiBold)
            {
                Width = 34,
                Height = 34,
                Margin = new Thickness(0, 0, 6, 0),
                ToolTip = "Seguir grabando (sin borrar lo anterior) / Pausar"
            };
            btnContToggle.Click += (s, e) =>
            {
                if (engine.IsProcessing) return;
                if (!engine.IsRecording) engine.StartRecording(append: true);
                else if (!engine.IsPaused) engine.PauseRecording();
                else engine.ResumeRecording();
            };

            btnCopy = new AppleButton("Copiar", 16, AppleTheme.BtnDarkBg, AppleTheme.BtnDarkHover, AppleTheme.TextWhite, 11, FontWeights.SemiBold)
            {
                Width = 54,
                Height = 34,
                Margin = new Thickness(0, 0, 6, 0),
                ToolTip = "Copiar todo el texto al portapapeles"
            };
            btnCopy.Click += (s, e) =>
            {
                string t = (engine.AccumulatedText ?? "").Trim();
                if (!string.IsNullOrEmpty(t))
                {
                    try
                    {
                        Clipboard.SetText(t);
                        FlashPillCopied();
                    }
                    catch { }
                }
            };

            btnExpand = new AppleButton("â†—", 16, AppleTheme.CardDark, AppleTheme.BtnDarkHover, AppleTheme.TextMuted, 12, FontWeights.Bold)
            {
                Width = 32,
                Height = 34,
                Margin = new Thickness(0, 0, 4, 0),
                ToolTip = "Expandir ventana principal"
            };
            btnExpand.Click += (s, e) => parentMain.RestoreFromPillMode();

            btnClosePill = new AppleButton("âœ•", 16, AppleTheme.CardDark, AppleTheme.AppleRed, AppleTheme.TextMuted, 10.5, FontWeights.Bold)
            {
                Width = 32,
                Height = 34,
                ToolTip = "Cerrar Transcriptor"
            };
            btnClosePill.Click += (s, e) =>
            {
                HideWithAnimation(() =>
                {
                    engine.CleanupWaveIn();
                    Application.Current.Shutdown();
                });
            };

            btnStack.Children.Add(btnRecToggle);
            btnStack.Children.Add(btnContToggle);
            btnStack.Children.Add(btnCopy);
            btnStack.Children.Add(btnExpand);
            btnStack.Children.Add(btnClosePill);

            Grid.SetColumn(leftInfo, 0);
            Grid.SetColumn(miniWave, 1);
            Grid.SetColumn(btnStack, 2);

            row.Children.Add(leftInfo);
            row.Children.Add(miniWave);
            row.Children.Add(btnStack);

            pillBorder.Child = row;
            this.Content = pillBorder;
        }

        private void FlashPillCopied()
        {
            btnCopy.Text = "âœ“";
            btnCopy.SetPalette(AppleTheme.AppleGreen, AppleTheme.AppleGreen, AppleTheme.BgBlack);
            var t = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1600) };
            t.Tick += (s, e) =>
            {
                t.Stop();
                btnCopy.Text = "Copiar";
                btnCopy.SetPalette(AppleTheme.BtnDarkBg, AppleTheme.BtnDarkHover, AppleTheme.TextWhite);
            };
            t.Start();
        }

        private void UpdatePillDot(string tag)
        {
            if (tag == "rec") recDot.Fill = AppleTheme.AppleRed;
            else if (tag == "pause" || tag == "work") recDot.Fill = AppleTheme.AppleAmber;
            else recDot.Fill = AppleTheme.AppleGreen;

            if (tag == "done")
            {
                FlashPillCopied();
            }
        }

        private void SyncPillButtons()
        {
            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.BeginInvoke(new Action(SyncPillButtons));
                return;
            }

            if (engine.IsProcessing)
            {
                btnRecToggle.IsDisabled = true;
                btnContToggle.IsDisabled = true;
                btnRecToggle.Text = "â€¦";
            }
            else if (engine.IsRecording)
            {
                btnRecToggle.IsDisabled = false;
                btnContToggle.IsDisabled = false;
                btnRecToggle.Text = "â– ";
                btnRecToggle.SetPalette(AppleTheme.AppleRed, AppleTheme.AppleRedHover, AppleTheme.TextWhite);

                if (engine.IsPaused)
                {
                    btnContToggle.Text = "â–¶";
                    btnContToggle.SetPalette(AppleTheme.BtnWhiteBg, AppleTheme.BtnWhiteHover, AppleTheme.BgBlack);
                }
                else
                {
                    btnContToggle.Text = "âšâš";
                    btnContToggle.SetPalette(AppleTheme.BtnDarkBg, AppleTheme.BtnDarkHover, AppleTheme.TextWhite);
                }
            }
            else
            {
                btnRecToggle.IsDisabled = false;
                btnContToggle.IsDisabled = false;
                btnRecToggle.Text = "â—";
                btnRecToggle.SetPalette(AppleTheme.BtnWhiteBg, AppleTheme.BtnWhiteHover, AppleTheme.AppleRed);

                btnContToggle.Text = "+";
                btnContToggle.SetPalette(AppleTheme.BtnDarkBg, AppleTheme.BtnDarkHover, AppleTheme.TextWhite);
            }
        }

        public void ShowWithAnimation()
        {
            if (!positionedOnce)
            {
                this.Left = (SystemParameters.WorkArea.Width - this.Width) / 2.0;
                this.Top = SystemParameters.WorkArea.Top + 14;
                positionedOnce = true;
            }
            this.Show();
            this.Topmost = true;
            AppleTheme.AnimateWindowIn(this, pillScale);
        }

        public void HideWithAnimation(Action afterHide)
        {
            AppleTheme.AnimateWindowOut(this, pillScale, () =>
            {
                this.Hide();
                if (afterHide != null) afterHide();
            });
        }
    }

    public static class Program
    {
        private static Mutex appMutex;

        [STAThread]
        public static void Main()
        {
            bool createdNew;
            appMutex = new Mutex(true, "Transcriptor_AppleBlack_SingleInstance_Mutex", out createdNew);
            if (!createdNew)
            {
                return;
            }

            var app = new Application();
            app.Run(new MainWindow());
        }
    }
}

