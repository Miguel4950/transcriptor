// Vercel Serverless Function — Proxy seguro para Transcriptor (Groq Whisper Large v3)
// Cero API keys hardcodeadas: usa process.env.GROQ_API_KEY o la cabecera x-groq-api-key.

export const config = {
  api: {
    bodyParser: false
  },
  maxDuration: 60
};

function readRawBody(req) {
  return new Promise((resolve, reject) => {
    const chunks = [];
    req.on('data', (chunk) => chunks.push(Buffer.isBuffer(chunk) ? chunk : Buffer.from(chunk)));
    req.on('end', () => resolve(Buffer.concat(chunks)));
    req.on('error', reject);
  });
}

export default async function handler(req, res) {
  res.setHeader('Access-Control-Allow-Origin', '*');
  res.setHeader('Access-Control-Allow-Methods', 'GET, POST, OPTIONS');
  res.setHeader('Access-Control-Allow-Headers', 'Content-Type, x-groq-api-key');

  if (req.method === 'OPTIONS') {
    return res.status(200).end();
  }

  if (req.method === 'GET') {
    const hasServerKey = Boolean(process.env.GROQ_API_KEY && process.env.GROQ_API_KEY.trim().length > 10);
    return res.status(200).json({
      hasServerKey,
      model: 'whisper-large-v3'
    });
  }

  if (req.method !== 'POST') {
    return res.status(405).json({ error: 'Método no permitido' });
  }

  try {
    const headerKey = req.headers['x-groq-api-key'];
    const apiKey = (headerKey && headerKey.trim()) || (process.env.GROQ_API_KEY && process.env.GROQ_API_KEY.trim());

    if (!apiKey) {
      return res.status(401).json({
        error: 'Configura GROQ_API_KEY en Vercel o pega tu clave en el botón Clave API de la píldora.'
      });
    }

    const audioBuffer = await readRawBody(req);
    if (!audioBuffer || audioBuffer.length < 100) {
      return res.status(400).json({ error: 'Audio vacío o demasiado corto.' });
    }

    if (audioBuffer.length > 22 * 1024 * 1024) {
      return res.status(413).json({ error: 'El tramo supera el límite seguro de 22 MB.' });
    }

    const boundary = '----TranscriptorBoundary' + Date.now().toString(16);
    const crlf = '\r\n';

    const partFileHeader = Buffer.from(
      `--${boundary}${crlf}` +
      `Content-Disposition: form-data; name="file"; filename="chunk.wav"${crlf}` +
      `Content-Type: audio/wav${crlf}${crlf}`,
      'utf-8'
    );

    const partFields = Buffer.from(
      `${crlf}--${boundary}${crlf}` +
      `Content-Disposition: form-data; name="model"${crlf}${crlf}` +
      `whisper-large-v3` +
      `${crlf}--${boundary}${crlf}` +
      `Content-Disposition: form-data; name="temperature"${crlf}${crlf}` +
      `0` +
      `${crlf}--${boundary}${crlf}` +
      `Content-Disposition: form-data; name="response_format"${crlf}${crlf}` +
      `json` +
      `${crlf}--${boundary}--${crlf}`,
      'utf-8'
    );

    const multipartBody = Buffer.concat([partFileHeader, audioBuffer, partFields]);

    let lastStatus = 500;
    let lastError = 'Error al contactar con Groq Whisper';

    for (let attempt = 1; attempt <= 3; attempt++) {
      const groqResponse = await fetch('https://api.groq.com/openai/v1/audio/transcriptions', {
        method: 'POST',
        headers: {
          'Authorization': `Bearer ${apiKey}`,
          'Content-Type': `multipart/form-data; boundary=${boundary}`,
          'Content-Length': String(multipartBody.length)
        },
        body: multipartBody
      });

      const responseText = await groqResponse.text();
      let data = null;
      try {
        data = JSON.parse(responseText);
      } catch {
        data = null;
      }

      if (groqResponse.ok) {
        return res.status(200).json({
          text: (data && data.text) ? data.text : ''
        });
      }

      lastStatus = groqResponse.status;
      lastError = (data && data.error && data.error.message) || responseText || `Error HTTP ${groqResponse.status}`;

      if (attempt < 3 && (lastStatus === 429 || lastStatus >= 500)) {
        const retryAfterSec = parseInt(groqResponse.headers.get('retry-after') || '0', 10);
        const waitMs = retryAfterSec > 0 ? Math.min(10000, retryAfterSec * 1000) : attempt * 1500;
        await new Promise((r) => setTimeout(r, waitMs));
        continue;
      }
      break;
    }

    return res.status(lastStatus).json({ error: lastError });
  } catch (err) {
    return res.status(500).json({
      error: err && err.message ? err.message : 'Error interno procesando el audio'
    });
  }
}
