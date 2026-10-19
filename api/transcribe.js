// Vercel Serverless Function — Proxy seguro para Groq Whisper Large v3
// Nunca contiene API keys hardcodeadas: usa process.env.GROQ_API_KEY o la cabecera x-groq-api-key.

export const config = {
  api: {
    bodyParser: false
  }
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
  // CORS headers
  res.setHeader('Access-Control-Allow-Origin', '*');
  res.setHeader('Access-Control-Allow-Methods', 'GET, POST, OPTIONS');
  res.setHeader('Access-Control-Allow-Headers', 'Content-Type, x-groq-api-key');

  if (req.method === 'OPTIONS') {
    return res.status(200).end();
  }

  // Endpoint de verificación rápida: ¿Tiene Vercel configurada la variable GROQ_API_KEY?
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
        error: 'No hay API Key configurada. Configura GROQ_API_KEY en Vercel o ingrésala en el botón ⚙️ de la web.'
      });
    }

    const audioBuffer = await readRawBody(req);
    if (!audioBuffer || audioBuffer.length < 100) {
      return res.status(400).json({ error: 'El archivo de audio está vacío o es demasiado corto.' });
    }

    // Construir multipart/form-data manualmente con Buffer nativo para máxima compatibilidad en Node.js
    const boundary = '----GroqWhisperBoundary' + Date.now().toString(16);
    const crlf = '\r\n';

    const partFileHeader = Buffer.from(
      `--${boundary}${crlf}` +
      `Content-Disposition: form-data; name="file"; filename="chunk.wav"${crlf}` +
      `Content-Type: audio/wav${crlf}${crlf}`,
      'utf-8'
    );

    const partModel = Buffer.from(
      `${crlf}--${boundary}${crlf}` +
      `Content-Disposition: form-data; name="model"${crlf}${crlf}` +
      `whisper-large-v3` +
      `${crlf}--${boundary}${crlf}` +
      `Content-Disposition: form-data; name="response_format"${crlf}${crlf}` +
      `json` +
      `${crlf}--${boundary}--${crlf}`,
      'utf-8'
    );

    const multipartBody = Buffer.concat([partFileHeader, audioBuffer, partModel]);

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
    let data;
    try {
      data = JSON.parse(responseText);
    } catch {
      data = null;
    }

    if (!groqResponse.ok) {
      const msg = (data && data.error && data.error.message) || responseText || `Error HTTP ${groqResponse.status}`;
      return res.status(groqResponse.status).json({ error: msg });
    }

    return res.status(200).json({
      text: (data && data.text) ? data.text : ''
    });
  } catch (err) {
    return res.status(500).json({
      error: err && err.message ? err.message : 'Error interno procesando el audio'
    });
  }
}
