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
        public event Action<string, string, string> OnStatusChanged; // title, subtitle, tag ("idle", "rec", "pause", "work", "done", "err")
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

                int bufferSize = BYTES_PER_SECOND / 12; // ~83ms por buffer para respuesta inmediata
                waveBuffers = new WaveBuffer[24];
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
                        append ? "Siguiendo" : "Grabando",
                        append ? "AÃ±adiendo al final de tu texto actual" : "Corte automÃ¡tico cada 2 min (< 22 MB)",
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
            if (OnStatusChanged != null) OnStatusChanged("Pausado", "Pulsa â–¶ para seguir hablando o â–  para transcribir", "pause");
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
            bool readAnyBuffer = false;
            if (waveBuffers != null)
            {
                for (int i = 0; i < waveBuffers.Length; i++)
                {
                    WaveBuffer buf = waveBuffers[i];
                    if (buf != null && buf.IsDone)
                    {
                        readAnyBuffer = true;
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
            else if (readAnyBuffer)
            {
                double norm = Math.Min(1.0, maxSample / 13000.0);
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
                    string.Format("Tramo #{0}", chunkIdx + 1),
                    string.Format("Enviando tramo #{0} ({1:F1} MB) en segundo plano...", chunkIdx, wavBytes.Length / (1024.0 * 1024.0)),
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
            if (OnStatusChanged != null) OnStatusChanged("Transcribiendo", "Procesando con Whisper Large v3...", "work");
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

                // Evitar alucinaciones de Whisper cuando solo hubo silencio
                if (activeChunkTasks.Count == 0 && maxSessionPeak < 280)
                {
                    if (currentChunkStream != null)
                    {
                        currentChunkStream.Dispose();
                        currentChunkStream = null;
                    }
                    if (OnStatusChanged != null) OnStatusChanged("Sin voz", "No se detectÃ³ voz en el micrÃ³fono", "idle");
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
                    if (OnStatusChanged != null) OnStatusChanged("Muy corto", "Habla al menos 1 segundo para transcribir", "idle");
                    return "";
                }

                ChunkResult[] results = await Task.WhenAll(activeChunkTasks);
                var ordered = results.OrderBy(r => r.Index).ToList();
                var errors = ordered.Where(r => !r.Success).Select(r => r.Error).ToList();
                var texts = ordered.Where(r => r.Success && !string.IsNullOrWhiteSpace(r.Text)).Select(r => r.Text.Trim()).ToList();

                string newTranscript = string.Join(" ", texts).Trim();

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
                        OnStatusChanged("Copiado", AccumulatedText, "done");
                    }
                }
                else if (errors.Count > 0)
                {
                    if (OnStatusChanged != null) OnStatusChanged("Error", errors[0], "err");
                }
                else
                {
                    if (OnStatusChanged != null) OnStatusChanged("Sin voz", "Intenta hablar mÃ¡s cerca del micrÃ³fono", "idle");
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
    // TEMA APPLE PRO BLACK Y ANIMACIONES SPRING
    // ==========================================
    public static class AppleTheme
    {
        public static SolidColorBrush BrushFromHex(string hex)
        {
            var b = (SolidColorBrush)new BrushConverter().ConvertFromString(hex);
            if (b.CanFreeze) b.Freeze();
            return b;
        }

        public static readonly SolidColorBrush BgPure = BrushFromHex("#030304");
        public static readonly SolidColorBrush BgBlack = BrushFromHex("#070709");
        public static readonly SolidColorBrush CardDark = BrushFromHex("#111115");
        public static readonly SolidColorBrush CardInner = BrushFromHex("#09090C");
        public static readonly SolidColorBrush BorderSubtle = BrushFromHex("#222228");
        public static readonly SolidColorBrush BorderHighlight = BrushFromHex("#363640");
        public static readonly SolidColorBrush BorderHover = BrushFromHex("#4A4A56");

        public static readonly SolidColorBrush TextWhite = BrushFromHex("#F5F5F7");
        public static readonly SolidColorBrush TextMuted = BrushFromHex("#86868B");
        public static readonly SolidColorBrush TextDim = BrushFromHex("#525259");

        public static readonly SolidColorBrush BtnWhiteBg = BrushFromHex("#F5F5F7");
        public static readonly SolidColorBrush BtnWhiteHover = BrushFromHex("#FFFFFF");
        public static readonly SolidColorBrush BtnDarkBg = BrushFromHex("#1B1B20");
        public static readonly SolidColorBrush BtnDarkHover = BrushFromHex("#27272F");

        public static readonly SolidColorBrush AppleRed = BrushFromHex("#FF3B30");
        public static readonly SolidColorBrush AppleRedHover = BrushFromHex("#FF453A");
        public static readonly SolidColorBrush AppleGreen = BrushFromHex("#30D158");
        public static readonly SolidColorBrush AppleAmber = BrushFromHex("#FF9F0A");

        // Pinceles congelados para 60 FPS sin basura en memoria (Zero GC)
        public static readonly SolidColorBrush WaveIdleBrush;
        public static readonly SolidColorBrush WaveRecQuietBrush;
        public static readonly SolidColorBrush WaveRecActiveBrush;
        public static readonly SolidColorBrush WaveWorkBrush;

        static AppleTheme()
        {
            WaveIdleBrush = new SolidColorBrush(Color.FromArgb(62, 245, 245, 247));
            WaveIdleBrush.Freeze();

            WaveRecQuietBrush = new SolidColorBrush(Color.FromArgb(180, 255, 69, 58));
            WaveRecQuietBrush.Freeze();

            WaveRecActiveBrush = new SolidColorBrush(Color.FromArgb(250, 255, 255, 255));
            WaveRecActiveBrush.Freeze();

            WaveWorkBrush = new SolidColorBrush(Color.FromArgb(215, 255, 159, 10));
            WaveWorkBrush.Freeze();
        }

        public static void AnimateSpringIn(Window win, ScaleTransform scale)
        {
            win.Opacity = 0;
            scale.ScaleX = 0.86;
            scale.ScaleY = 0.86;

            var spring = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.28 };
            var fadeEase = new CubicEase { EasingMode = EasingMode.EaseOut };
            var durScale = new Duration(TimeSpan.FromMilliseconds(340));
            var durFade = new Duration(TimeSpan.FromMilliseconds(220));

            win.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0, 1, durFade) { EasingFunction = fadeEase });
            scale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(0.86, 1.0, durScale) { EasingFunction = spring });
            scale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(0.86, 1.0, durScale) { EasingFunction = spring });
        }

        public static void AnimateSpringOut(Window win, ScaleTransform scale, Action onCompleted)
        {
            var ease = new CubicEase { EasingMode = EasingMode.EaseIn };
            var dur = new Duration(TimeSpan.FromMilliseconds(190));

            var fadeOut = new DoubleAnimation(win.Opacity, 0, dur) { EasingFunction = ease };
            fadeOut.Completed += (s, e) =>
            {
                if (onCompleted != null) onCompleted();
            };

            win.BeginAnimation(UIElement.OpacityProperty, fadeOut);
            scale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(1.0, 0.88, dur) { EasingFunction = ease });
            scale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(1.0, 0.88, dur) { EasingFunction = ease });
        }
    }

    // BotÃ³n Apple con rebote tÃ¡ctil y esquinas vectoriales perfectas
    public class AppleButton : Border
    {
        private TextBlock labelBlock;
        private SolidColorBrush normalBg;
        private SolidColorBrush hoverBg;
        private SolidColorBrush normalFg;
        private ScaleTransform btnScale;
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
                this.Opacity = isDisabled ? 0.38 : 1.0;
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
            this.BorderBrush = new SolidColorBrush(Color.FromArgb(24, 255, 255, 255));
            this.BorderThickness = new Thickness(1);
            this.Cursor = Cursors.Hand;
            this.SnapsToDevicePixels = true;

            btnScale = new ScaleTransform(1, 1);
            this.RenderTransformOrigin = new Point(0.5, 0.5);
            this.RenderTransform = btnScale;

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
                AnimateScale(1.03, 120);
            };

            this.MouseLeave += (s, e) =>
            {
                if (isDisabled) return;
                this.Background = normalBg;
                AnimateScale(1.0, 140);
            };

            this.MouseLeftButtonDown += (s, e) =>
            {
                if (isDisabled) return;
                e.Handled = true;
                AnimateScale(0.92, 85);
            };

            this.MouseLeftButtonUp += (s, e) =>
            {
                if (isDisabled) return;
                e.Handled = true;
                AnimateBounceRelease();
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

        private void AnimateScale(double to, int ms)
        {
            var anim = new DoubleAnimation(to, new Duration(TimeSpan.FromMilliseconds(ms)))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            };
            btnScale.BeginAnimation(ScaleTransform.ScaleXProperty, anim);
            btnScale.BeginAnimation(ScaleTransform.ScaleYProperty, anim);
        }

        private void AnimateBounceRelease()
        {
            var anim = new DoubleAnimation(1.0, new Duration(TimeSpan.FromMilliseconds(220)))
            {
                EasingFunction = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.45 }
            };
            btnScale.BeginAnimation(ScaleTransform.ScaleXProperty, anim);
            btnScale.BeginAnimation(ScaleTransform.ScaleYProperty, anim);
        }
    }

    // Visualizador de Onda estilo Apple Voice Memos / Dynamic Island a 60 FPS reales
    public class AppleWaveformVisualizer : Canvas
    {
        private readonly Rectangle[] bars;
        private readonly double[] currentHeights;
        private readonly double[] targetHeights;
        private readonly Random rng = new Random();
        private double currentAudioLevel = 0;
        private bool isRecordingActive = false;
        private bool isProcessingActive = false;
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
                    Fill = AppleTheme.WaveIdleBrush
                };
                bars[i] = rect;
                this.Children.Add(rect);
            }

            this.SizeChanged += (s, e) => LayoutBars();
        }

        public void SetState(double level, bool recording, bool processing)
        {
            currentAudioLevel = level;
            isRecordingActive = recording;
            isProcessingActive = processing;
            if (!recording)
            {
                currentAudioLevel = 0;
            }
        }

        public void StepFrame()
        {
            phase += 0.16;
            double hAvail = Math.Max(12, this.ActualHeight - 8);

            for (int i = 0; i < barCount; i++)
            {
                double centerDist = Math.Abs(i - (barCount - 1) / 2.0) / (barCount / 2.0);
                double envelope = Math.Max(0.18, 1.0 - (centerDist * 0.76));

                if (isProcessingActive)
                {
                    // Onda viajera suave mientras Whisper transcribe
                    double shimmer = 0.5 + 0.5 * Math.Sin(phase * 1.8 - i * 0.45);
                    targetHeights[i] = 4.0 + (hAvail * 0.55 * shimmer * envelope);
                    bars[i].Fill = AppleTheme.WaveWorkBrush;
                }
                else if (!isRecordingActive)
                {
                    targetHeights[i] = 3.5 + Math.Sin(phase * 0.5 + i * 0.35) * 1.1;
                    bars[i].Fill = AppleTheme.WaveIdleBrush;
                }
                else if (currentAudioLevel < 0.025)
                {
                    targetHeights[i] = 4.8 + Math.Sin(phase * 1.1 + i * 0.45) * 2.4 * envelope;
                    bars[i].Fill = AppleTheme.WaveRecQuietBrush;
                }
                else
                {
                    double wave = 0.62 + 0.38 * Math.Sin(phase * 2.1 + i * 0.58);
                    double jitter = 0.76 + rng.NextDouble() * 0.48;
                    double normalized = Math.Min(1.0, currentAudioLevel * envelope * wave * jitter * 1.55);
                    targetHeights[i] = Math.Max(5.0, normalized * hAvail);
                    bars[i].Fill = AppleTheme.WaveRecActiveBrush;
                }

                currentHeights[i] += (targetHeights[i] - currentHeights[i]) * 0.34;
            }

            LayoutBars();
            // Decaimiento suave del nivel entre muestras de audio
            currentAudioLevel *= 0.88;
        }

        private void LayoutBars()
        {
            double w = this.ActualWidth;
            double h = this.ActualHeight;
            if (w <= 10 || h <= 6) return;

            double totalBarSpace = w - 10;
            double step = totalBarSpace / barCount;
            double barW = Math.Max(2.4, Math.Min(4.2, step * 0.50));

            for (int i = 0; i < barCount; i++)
            {
                double bh = Math.Max(3.5, Math.Min(h - 4, currentHeights[i]));
                double x = 5 + i * step + (step - barW) / 2.0;
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
    // VENTANA PRINCIPAL DE INICIO: MODO PÃLDORA (DYNAMIC ISLAND)
    // ==========================================
    public class PillWindow : Window
    {
        private AudioRecorderEngine engine;
        private MainWindow fullWindow;
        private Border pillBorder;
        private Border previewToastBorder;
        private TextBlock lblPreviewToast;
        private ScaleTransform pillScale;
        private ScaleTransform haloScale;
        private Ellipse recHalo;
        private Ellipse recDot;
        private TextBlock lblPillTimer;
        private TextBlock lblPillState;
        private AppleWaveformVisualizer miniWave;
        private AppleButton btnRecToggle;
        private AppleButton btnContToggle;
        private AppleButton btnCopy;
        private AppleButton btnExpand;
        private AppleButton btnClosePill;
        private DispatcherTimer masterTimer;
        private DispatcherTimer toastHideTimer;

        private const int HOTKEY_ID = 9001;
        private const uint MOD_ALT = 0x0001;
        private const uint VK_M = 0x4D;

        [DllImport("user32.dll")]
        private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

        [DllImport("user32.dll")]
        private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

        public PillWindow()
        {
            string exePath = System.Reflection.Assembly.GetExecutingAssembly().Location;
            if (!StartupManager.IsStartupEnabled())
            {
                StartupManager.SetStartup(true, exePath);
            }

            engine = new AudioRecorderEngine();
            BuildDynamicIslandUI();

            fullWindow = new MainWindow(engine, this);

            engine.OnAudioLevel += (lvl) =>
            {
                miniWave.SetState(lvl, engine.IsRecording && !engine.IsPaused, engine.IsProcessing);
                fullWindow.PushAudioLevel(lvl);
            };
            engine.OnStateChanged += OnEngineStateChanged;
            engine.OnStatusChanged += OnEngineStatusChanged;

            // Loop central a ~60 FPS (16ms) que procesa el micrÃ³fono y anima la pÃ­ldora o ventana grande
            masterTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
            masterTimer.Tick += (s, e) =>
            {
                engine.PollAudioBuffers();
                TimeSpan t = engine.TotalElapsedTime;
                string timeStr = string.Format("{0:D2}:{1:D2}", (int)t.TotalMinutes, t.Seconds);
                lblPillTimer.Text = timeStr;
                fullWindow.UpdateTimerText(timeStr);

                if (this.IsVisible)
                {
                    miniWave.StepFrame();
                }
                if (fullWindow.IsVisible)
                {
                    fullWindow.StepWaveform();
                }
            };
            masterTimer.Start();

            this.Loaded += (s, e) =>
            {
                PositionAtTopCenter();
                AppleTheme.AnimateSpringIn(this, pillScale);
                RegisterGlobalHotkey();
            };
        }

        private void PositionAtTopCenter()
        {
            this.Left = (SystemParameters.WorkArea.Width - this.Width) / 2.0;
            this.Top = SystemParameters.WorkArea.Top + 12;
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

        private void BuildDynamicIslandUI()
        {
            this.Title = "Transcriptor";
            this.Width = 480;
            this.Height = 118; // Espacio para la pÃ­ldora superior + burbuja de vista previa inferior
            this.WindowStyle = WindowStyle.None;
            this.AllowsTransparency = true;
            this.Background = Brushes.Transparent;
            this.Topmost = true;
            this.ShowInTaskbar = true;
            this.ResizeMode = ResizeMode.NoResize;
            this.FontFamily = new FontFamily("Segoe UI, -apple-system, BlinkMacSystemFont");

            var rootStack = new StackPanel
            {
                Orientation = Orientation.Vertical,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Top
            };

            pillScale = new ScaleTransform(1, 1);

            pillBorder = new Border
            {
                Width = 416,
                Height = 54,
                Margin = new Thickness(12, 8, 12, 6),
                CornerRadius = new CornerRadius(27),
                Background = AppleTheme.BgPure,
                BorderBrush = AppleTheme.BorderHighlight,
                BorderThickness = new Thickness(1),
                Padding = new Thickness(14, 0, 10, 0),
                RenderTransformOrigin = new Point(0.5, 0.5),
                RenderTransform = pillScale,
                Effect = new DropShadowEffect
                {
                    Color = Colors.Black,
                    BlurRadius = 22,
                    ShadowDepth = 6,
                    Opacity = 0.88
                }
            };

            pillBorder.MouseEnter += (s, e) =>
            {
                pillBorder.BorderBrush = AppleTheme.BorderHover;
            };
            pillBorder.MouseLeave += (s, e) =>
            {
                pillBorder.BorderBrush = AppleTheme.BorderHighlight;
            };
            pillBorder.MouseLeftButtonDown += (s, e) =>
            {
                if (e.ButtonState == MouseButtonState.Pressed) this.DragMove();
            };

            var row = new Grid { VerticalAlignment = VerticalAlignment.Center };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); // Orbe con Halo + Reloj + Estado
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) }); // Onda Dynamic Island
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); // Botones circulares

            var leftInfo = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                VerticalAlignment = VerticalAlignment.Center,
                Cursor = Cursors.SizeAll
            };

            // Contenedor de punto + halo animado
            var dotGrid = new Grid
            {
                Width = 18,
                Height = 18,
                Margin = new Thickness(0, 0, 6, 0),
                VerticalAlignment = VerticalAlignment.Center
            };

            haloScale = new ScaleTransform(1, 1);
            recHalo = new Ellipse
            {
                Width = 9,
                Height = 9,
                Fill = AppleTheme.AppleRed,
                Opacity = 0,
                RenderTransformOrigin = new Point(0.5, 0.5),
                RenderTransform = haloScale,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };

            recDot = new Ellipse
            {
                Width = 8,
                Height = 8,
                Fill = AppleTheme.AppleGreen,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };

            dotGrid.Children.Add(recHalo);
            dotGrid.Children.Add(recDot);

            var timerAndStateStack = new StackPanel
            {
                Orientation = Orientation.Vertical,
                VerticalAlignment = VerticalAlignment.Center
            };

            lblPillTimer = new TextBlock
            {
                Text = "00:00",
                Foreground = AppleTheme.TextWhite,
                FontFamily = new FontFamily("Consolas"),
                FontSize = 13.5,
                FontWeight = FontWeights.Bold,
                LineHeight = 14
            };

            lblPillState = new TextBlock
            {
                Text = "Listo",
                Foreground = AppleTheme.TextMuted,
                FontSize = 9.5,
                FontWeight = FontWeights.Medium
            };

            timerAndStateStack.Children.Add(lblPillTimer);
            timerAndStateStack.Children.Add(lblPillState);

            leftInfo.Children.Add(dotGrid);
            leftInfo.Children.Add(timerAndStateStack);

            miniWave = new AppleWaveformVisualizer(18)
            {
                Height = 30,
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
                        ShowPreviewToast("âœ“ Copiado: " + TruncatePreview(t));
                    }
                    catch { }
                }
                else
                {
                    ShowPreviewToast("AÃºn no hay texto transcrito");
                }
            };

            btnExpand = new AppleButton("â†—", 17, AppleTheme.CardDark, AppleTheme.BtnDarkHover, AppleTheme.TextWhite, 12, FontWeights.Bold)
            {
                Width = 34,
                Height = 34,
                Margin = new Thickness(0, 0, 5, 0),
                ToolTip = "Abrir Modo Grande (Editor completo)"
            };
            btnExpand.Click += (s, e) => ExpandToFullWindow();

            btnClosePill = new AppleButton("âœ•", 17, AppleTheme.CardDark, AppleTheme.AppleRed, AppleTheme.TextMuted, 10.5, FontWeights.Bold)
            {
                Width = 34,
                Height = 34,
                ToolTip = "Cerrar Transcriptor"
            };
            btnClosePill.Click += (s, e) =>
            {
                AppleTheme.AnimateSpringOut(this, pillScale, () =>
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

            // Burbuja inferior desplegable para previsualizar lo reciÃ©n transcrito sin salir de la pÃ­ldora
            previewToastBorder = new Border
            {
                MaxWidth = 410,
                CornerRadius = new CornerRadius(14),
                Background = AppleTheme.CardDark,
                BorderBrush = AppleTheme.BorderSubtle,
                BorderThickness = new Thickness(1),
                Padding = new Thickness(14, 7, 14, 7),
                HorizontalAlignment = HorizontalAlignment.Center,
                Opacity = 0,
                IsHitTestVisible = false
            };

            lblPreviewToast = new TextBlock
            {
                Text = "",
                Foreground = AppleTheme.TextWhite,
                FontSize = 11.5,
                TextTrimming = TextTrimming.CharacterEllipsis,
                MaxWidth = 380
            };
            previewToastBorder.Child = lblPreviewToast;

            rootStack.Children.Add(pillBorder);
            rootStack.Children.Add(previewToastBorder);

            this.Content = rootStack;
        }

        private static string TruncatePreview(string text)
        {
            string clean = text.Replace("\r", " ").Replace("\n", " ").Trim();
            return clean.Length > 52 ? clean.Substring(0, 52) + "â€¦" : clean;
        }

        private void ShowPreviewToast(string message)
        {
            lblPreviewToast.Text = message;
            var fadeIn = new DoubleAnimation(1.0, new Duration(TimeSpan.FromMilliseconds(200)));
            previewToastBorder.BeginAnimation(UIElement.OpacityProperty, fadeIn);

            if (toastHideTimer != null) toastHideTimer.Stop();
            toastHideTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(3200) };
            toastHideTimer.Tick += (s, e) =>
            {
                toastHideTimer.Stop();
                var fadeOut = new DoubleAnimation(0.0, new Duration(TimeSpan.FromMilliseconds(280)));
                previewToastBorder.BeginAnimation(UIElement.OpacityProperty, fadeOut);
            };
            toastHideTimer.Start();
        }

        private void AnimatePillWidth(double targetWidth)
        {
            var anim = new DoubleAnimation(targetWidth, new Duration(TimeSpan.FromMilliseconds(340)))
            {
                EasingFunction = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.24 }
            };
            pillBorder.BeginAnimation(FrameworkElement.WidthProperty, anim);
        }

        private void SetHaloAnimation(bool active)
        {
            if (active)
            {
                var dur = new Duration(TimeSpan.FromMilliseconds(1250));
                var scaleAnim = new DoubleAnimation(1.0, 2.5, dur) { RepeatBehavior = RepeatBehavior.Forever };
                var fadeAnim = new DoubleAnimation(0.65, 0.0, dur) { RepeatBehavior = RepeatBehavior.Forever };
                haloScale.BeginAnimation(ScaleTransform.ScaleXProperty, scaleAnim);
                haloScale.BeginAnimation(ScaleTransform.ScaleYProperty, scaleAnim);
                recHalo.BeginAnimation(UIElement.OpacityProperty, fadeAnim);
            }
            else
            {
                haloScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
                haloScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
                recHalo.BeginAnimation(UIElement.OpacityProperty, null);
                recHalo.Opacity = 0;
            }
        }

        private void FlashPillCopied()
        {
            btnCopy.Text = "âœ“";
            btnCopy.SetPalette(AppleTheme.AppleGreen, AppleTheme.AppleGreen, AppleTheme.BgPure);
            var t = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1800) };
            t.Tick += (s, e) =>
            {
                t.Stop();
                btnCopy.Text = "Copiar";
                btnCopy.SetPalette(AppleTheme.BtnDarkBg, AppleTheme.BtnDarkHover, AppleTheme.TextWhite);
            };
            t.Start();
        }

        private void OnEngineStatusChanged(string title, string sub, string tag)
        {
            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.BeginInvoke(new Action(() => OnEngineStatusChanged(title, sub, tag)));
                return;
            }

            lblPillState.Text = title;

            if (tag == "rec")
            {
                recDot.Fill = AppleTheme.AppleRed;
                lblPillState.Foreground = AppleTheme.AppleRed;
                SetHaloAnimation(true);
            }
            else if (tag == "pause" || tag == "work")
            {
                recDot.Fill = AppleTheme.AppleAmber;
                lblPillState.Foreground = AppleTheme.AppleAmber;
                SetHaloAnimation(false);
            }
            else if (tag == "err")
            {
                recDot.Fill = AppleTheme.AppleRed;
                lblPillState.Foreground = AppleTheme.AppleRed;
                SetHaloAnimation(false);
                ShowPreviewToast(sub);
            }
            else
            {
                recDot.Fill = AppleTheme.AppleGreen;
                lblPillState.Foreground = AppleTheme.TextMuted;
                SetHaloAnimation(false);
            }

            if (tag == "done")
            {
                FlashPillCopied();
                ShowPreviewToast("âœ“ Copiado: " + TruncatePreview(sub));
            }
        }

        private void OnEngineStateChanged()
        {
            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.BeginInvoke(new Action(OnEngineStateChanged));
                return;
            }

            miniWave.SetState(0, engine.IsRecording && !engine.IsPaused, engine.IsProcessing);

            if (engine.IsProcessing)
            {
                AnimatePillWidth(448);
                btnRecToggle.IsDisabled = true;
                btnContToggle.IsDisabled = true;
                btnRecToggle.Text = "â€¦";
            }
            else if (engine.IsRecording)
            {
                AnimatePillWidth(452);
                btnRecToggle.IsDisabled = false;
                btnContToggle.IsDisabled = false;
                btnRecToggle.Text = "â– ";
                btnRecToggle.SetPalette(AppleTheme.AppleRed, AppleTheme.AppleRedHover, AppleTheme.TextWhite);

                if (engine.IsPaused)
                {
                    btnContToggle.Text = "â–¶";
                    btnContToggle.SetPalette(AppleTheme.BtnWhiteBg, AppleTheme.BtnWhiteHover, AppleTheme.BgPure);
                }
                else
                {
                    btnContToggle.Text = "âšâš";
                    btnContToggle.SetPalette(AppleTheme.BtnDarkBg, AppleTheme.BtnDarkHover, AppleTheme.TextWhite);
                }
            }
            else
            {
                AnimatePillWidth(416);
                btnRecToggle.IsDisabled = false;
                btnContToggle.IsDisabled = false;
                btnRecToggle.Text = "â—";
                btnRecToggle.SetPalette(AppleTheme.BtnWhiteBg, AppleTheme.BtnWhiteHover, AppleTheme.AppleRed);

                btnContToggle.Text = "+";
                btnContToggle.SetPalette(AppleTheme.BtnDarkBg, AppleTheme.BtnDarkHover, AppleTheme.TextWhite);
            }
        }

        public void ExpandToFullWindow()
        {
            AppleTheme.AnimateSpringOut(this, pillScale, () =>
            {
                this.Hide();
                fullWindow.ShowWithSpring();
            });
        }

        public void ReturnToPillFromFullWindow()
        {
            this.Show();
            this.Topmost = true;
            AppleTheme.AnimateSpringIn(this, pillScale);
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
    // MODO GRANDE (EDITOR COMPLETO SECUNDARIO)
    // ==========================================
    public class MainWindow : Window
    {
        private AudioRecorderEngine engine;
        private PillWindow parentPill;
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

        public MainWindow(AudioRecorderEngine eng, PillWindow pill)
        {
            this.engine = eng;
            this.parentPill = pill;

            BuildAppleBlackUI();

            engine.OnStatusChanged += UpdateStatusUI;
            engine.OnTextUpdated += UpdateTextUI;
            engine.OnStateChanged += SyncButtonsState;
        }

        public void PushAudioLevel(double lvl)
        {
            waveform.SetState(lvl, engine.IsRecording && !engine.IsPaused, engine.IsProcessing);
        }

        public void UpdateTimerText(string formatted)
        {
            lblTimer.Text = formatted;
        }

        public void StepWaveform()
        {
            waveform.StepFrame();
        }

        public void ShowWithSpring()
        {
            this.Show();
            this.Activate();
            AppleTheme.AnimateSpringIn(this, windowScale);
        }

        private void BuildAppleBlackUI()
        {
            this.Title = "Transcriptor â€” Modo Grande";
            this.Width = 520;
            this.Height = 640;
            this.WindowStartupLocation = WindowStartupLocation.CenterScreen;
            this.WindowStyle = WindowStyle.None;
            this.AllowsTransparency = true;
            this.Background = Brushes.Transparent;
            this.ResizeMode = ResizeMode.NoResize;
            this.FontFamily = new FontFamily("Segoe UI, -apple-system, BlinkMacSystemFont");

            windowScale = new ScaleTransform(1, 1);

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
                    Opacity = 0.82
                }
            };

            var rootGrid = new Grid { Margin = new Thickness(22, 16, 22, 22) };
            rootGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(44) });
            rootGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(154) });
            rootGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(64) });
            rootGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            rootGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(56) });

            // Cabecera Superior
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
                ToolTip = "Iniciar automÃ¡ticamente en Modo PÃ­ldora al encender el PC"
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
                "â†™ Modo PÃ­ldora",
                13,
                AppleTheme.BtnWhiteBg,
                AppleTheme.BtnWhiteHover,
                AppleTheme.BgPure,
                11.5,
                FontWeights.SemiBold
            )
            {
                Width = 112,
                Height = 28,
                Margin = new Thickness(0, 0, 8, 0),
                ToolTip = "Volver a la barra flotante compacta"
            };
            btnPillMode.Click += (s, e) => CollapseToPill();

            var btnClose = new AppleButton("âœ•", 14, AppleTheme.CardDark, AppleTheme.AppleRed, AppleTheme.TextMuted, 11, FontWeights.Bold)
            {
                Width = 28,
                Height = 28,
                ToolTip = "Cerrar Transcriptor"
            };
            btnClose.Click += (s, e) =>
            {
                AppleTheme.AnimateSpringOut(this, windowScale, () =>
                {
                    engine.CleanupWaveIn();
                    Application.Current.Shutdown();
                });
            };

            controlsStack.Children.Add(btnStartup);
            controlsStack.Children.Add(btnPillMode);
            controlsStack.Children.Add(btnClose);

            Grid.SetColumn(titleStack, 0);
            Grid.SetColumn(controlsStack, 1);
            headerGrid.Children.Add(titleStack);
            headerGrid.Children.Add(controlsStack);

            // Tarjeta Hero
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
                Margin = new Thickness(0, 3, 0, 0),
                TextTrimming = TextTrimming.CharacterEllipsis
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

            waveform = new AppleWaveformVisualizer(42);
            waveBorder.Child = waveform;

            Grid.SetRow(statusTopGrid, 0);
            Grid.SetRow(waveBorder, 1);
            heroGrid.Children.Add(statusTopGrid);
            heroGrid.Children.Add(waveBorder);
            heroCard.Child = heroGrid;

            // Botones AcciÃ³n
            var actionGrid = new Grid { Margin = new Thickness(0, 0, 0, 12) };
            actionGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.05, GridUnitType.Star) });
            actionGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(12) });
            actionGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(0.95, GridUnitType.Star) });

            btnRecord = new AppleButton(
                "Empezar GrabaciÃ³n",
                15,
                AppleTheme.BtnWhiteBg,
                AppleTheme.BtnWhiteHover,
                AppleTheme.BgPure,
                13.5,
                FontWeights.SemiBold
            );
            btnRecord.Click += async (s, e) =>
            {
                if (engine.IsProcessing) return;
                if (!engine.IsRecording) engine.StartRecording(append: false);
                else await engine.StopRecordingAndTranscribeAsync();
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
                if (!engine.IsRecording) engine.StartRecording(append: true);
                else if (!engine.IsPaused) engine.PauseRecording();
                else engine.ResumeRecording();
            };

            Grid.SetColumn(btnRecord, 0);
            Grid.SetColumn(btnContinue, 2);
            actionGrid.Children.Add(btnRecord);
            actionGrid.Children.Add(btnContinue);

            // Tarjeta TranscripciÃ³n
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

            // BotÃ³n Copiar
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

        private void CollapseToPill()
        {
            AppleTheme.AnimateSpringOut(this, windowScale, () =>
            {
                this.Hide();
                parentPill.ReturnToPillFromFullWindow();
            });
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
            btnCopy.SetPalette(AppleTheme.AppleGreen, AppleTheme.AppleGreen, AppleTheme.BgPure);

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
            lblStatusSub.Text = tag == "done" ? "Listo para pegar con Ctrl + V o seguir grabando" : sub;

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

            waveform.SetState(0, engine.IsRecording && !engine.IsPaused, engine.IsProcessing);

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
                    btnContinue.SetPalette(AppleTheme.BtnWhiteBg, AppleTheme.BtnWhiteHover, AppleTheme.BgPure);
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
                btnRecord.SetPalette(AppleTheme.BtnWhiteBg, AppleTheme.BtnWhiteHover, AppleTheme.BgPure);

                btnContinue.Text = "Seguir Grabando";
                btnContinue.SetPalette(AppleTheme.BtnDarkBg, AppleTheme.BtnDarkHover, AppleTheme.TextWhite);
            }
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
            // Inicia directamente en Modo PÃ­ldora (Dynamic Island) como ventana principal
            app.Run(new PillWindow());
        }
    }
}

