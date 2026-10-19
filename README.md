# ⚡ Groq Whisper Large v3 — Cyber Liquid Glass (iPhone PWA + Web + PC)

Grabadora de voz inteligente impulsada por **Groq Cloud (`whisper-large-v3`)** con estética **Cyber Dark Liquid Glass**, diseñada especialmente para **iPhone (Safari / PWA instalable)** y navegadores de escritorio, lista para desplegar en **Vercel**.

## ✨ Características

- **🎨 Diseño Cyber Dark Liquid Glass**: Paneles traslúcidos con desenfoque real (`backdrop-filter`), orbes ambientales bioluminiscentes y visualizador de onda líquida de 34 barras a 60 FPS.
- **✂️ Recorte Automático cada 2 Minutos (`< 22 MB`)**: Codifica audio en WAV (16 kHz, 16-bit, Mono, ~3.66 MB por cada 2 minutos) y envía cada tramo en segundo plano mientras sigues hablando sin perder ni un milisegundo de voz.
- **➕ Botón "Seguir Grabando" / Pausar**:
  - Mientras grabas: pausa y reanuda tu sesión.
  - Al terminar: te permite seguir grabando y **añadir el nuevo texto al final de lo que ya tenías** sin borrar lo anterior.
- **🚀 Modo Volador (Cápsula Flotante)**: Minimiza la interfaz a una píldora compacta arrastrable con el dedo (en iPhone) o el ratón (en PC) con todos los controles (`🎤`, `➕`, `📋`, `⛶`).
- **📱 Optimizado para iPhone (PWA)**:
  - Soporta **Añadir a pantalla de inicio** a pantalla completa.
  - Incluye **Wake Lock** para que la pantalla del iPhone no se apague mientras grabas.
  - Botón directo de **Copiar al Portapapeles** + **Compartir nativo de iOS (`📤`)** (WhatsApp, Notas, Telegram, etc.).
- **🔒 Cero API Keys Hardcodeadas**:
  - Funciona con la variable de entorno `GROQ_API_KEY` en Vercel (`/api/transcribe`) **o** pegando tu clave una sola vez en el botón `⚙️ API` de la web (guardada localmente en tu iPhone mediante `localStorage`).

---

## 🚀 Cómo desplegar en Vercel

1. Entra en [vercel.com/new](https://vercel.com/new) e importa este repositorio (`Miguel4950/groq-whisper-liquid-glass`).
2. *(Opcional)* En **Environment Variables**, añade:
   - Nombre: `GROQ_API_KEY`
   - Valor: Tu clave de Groq (`gsk_...`)
3. Pulsa **Deploy**.
4. ¡Listo! También puedes abrir la web en tu iPhone y pegar tu API Key en el botón **`⚙️ API`** para que quede guardada en la memoria de tu teléfono.

### 📲 Instalar como App en tu iPhone
1. Abre tu enlace de Vercel en **Safari** desde tu iPhone.
2. Toca el botón **Compartir** (abajo en el centro) → **Añadir a pantalla de inicio**.
3. Ábrela desde tu pantalla de inicio a pantalla completa como una app nativa.
