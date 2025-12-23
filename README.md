# Kuroko (黒衣) - Stealth Interview Co-Pilot

> **"To exist without being seen. To help without being heard."**

Kuroko is a high-performance, stealth-focused desktop assistant designed for technical interviews. It leverages local hardware for audio processing and RAG (Retrieval Augmented Generation) while orchestrating cloud-based LLMs for intelligence, ensuring privacy, low latency, and zero detection.

## 🎥 Demo

> *The interface visible above is seen ONLY by the user. To an interviewer viewing your screen via Zoom, Teams, or OBS, the desktop appears completely empty.*

## 🧠 Engineering Methodology

This project was built with three core pillars: **Invisibility**, **Latency**, and **Privacy**.

### 1. The Stealth Architecture (Anti-Detection)

Unlike browser extensions or meeting bots, Kuroko runs entirely outside the meeting context.

* **Visual Invisibility**: Uses the Win32 `SetWindowDisplayAffinity` API with `WDA_EXCLUDEFROMCAPTURE`. This renders the UI strictly to the user's physical monitor, bypassing the DWM (Desktop Window Manager) bitstream sent to screen-sharing software (Zoom, Teams, OBS).
* **Process Camouflage**: Implements `WS_EX_TOOLWINDOW` extended window styles to hide the application from the Taskbar and the "Apps" section of Task Manager, masquerading as a background system process.
* **Decoy Strategy**: Features a configurable "Masquerade Mode" that alters the process title and icon (e.g., to "Calculator" or "Runtime Broker") at runtime to evade manual inspection.

### 2. Latency Optimization (The <200ms Goal)

Speed is critical for natural conversation. Every millisecond of friction was engineered out:

* **Local Transcription**: Uses `Whisper.net` with quantization (Ggml) to run speech-to-text locally on the CPU/GPU. This eliminates the network round-trip for audio data.
* **Network Tuning**: The `AiService` enforces **HTTP/2** and maintains a warmed-up connection pool (`SocketsHttpHandler`) to eliminate the expensive SSL/TLS handshake overhead on triggers.
* **Streaming Pipeline**: Implements `IAsyncEnumerable` with Server-Sent Events (SSE) to render tokens to the UI the instant they arrive, rather than waiting for full generation.
* **SIMD Vector Search**: Uses `System.Numerics.Tensors` for hardware-accelerated Cosine Similarity calculations, allowing instant retrieval from the local RAG database.

### 3. Local-First Privacy (RAG)

Sensitive data (Resumes, CVs) never leaves the user's machine permanently.

* **Ingestion**: `PdfPig` extracts text from PDF documents.
* **Vector Store**: Data is chunked and stored in a local **SQLite** database.
* **Retrieval**: Embeddings are generated on-the-fly. Only the specific, relevant chunks of text needed to answer a question are sent to the cloud LLM. The full resume is never exposed to the provider's training data.

## 🛠 Tech Stack

* **Language**: C# / .NET 10
* **UI Framework**: WPF (Windows Presentation Foundation)
* **Audio Engine**: NAudio (WASAPI Loopback Capture)
* **Transcription**: Whisper.net (Local C++ bindings)
* **AI Orchestration**: Semantic Kernel / Custom OpenRouter Client
* **Database**: SQLite (w/ Vector Logic)

## 🚀 Getting Started

### Prerequisites

* **OS**: Windows 10/11 (x64)
* **Runtime**: .NET 10 SDK
* **Hardware**: Microphone + Speakers

### Installation

1. Clone the repository:
```bash
git clone https://github.com/hyowonbernabe/Kuroko.git
```


2. Open `Kuroko.sln` in **Visual Studio**.
3. Restore NuGet packages.
4. Create a `.env` file in the root directory (see Configuration).
5. Build and Run (`Ctrl + F5`).

### Configuration (.env)

Create a `.env` file in the project root to store your secrets and settings:

```ini
OPENROUTER_API_KEY=sk-or-v1-your-key-here
OPENROUTER_MODEL=google/gemma-3-27b-it:free

# Window Behavior
WINDOW_TOPMOST=True
DEEP_STEALTH=False

# Hotkeys (Format: Modifiers + Key)
HOTKEY_TRIGGER_TXT=Alt + S
HOTKEY_PANIC_TXT=Alt + Q

# Masquerade (Optional)
DECOY_TITLE=Host Process
DECOY_ICON=
```

## 🎮 Usage Guide

### 1. Initialization

* Launch Kuroko. It will appear as a small toolbar at the bottom-left of your screen.
* Click **INIT** to start the audio engine. The status will change to **ACTIVE**.
* *Note: Ensure you are playing audio (or speaking) to verify the "Vol" meter moves.*

### 2. Knowledge Base (RAG)

* Open **SETTINGS** -> **KNOWLEDGE BASE**.
* Click **UPLOAD PDF** and select your Resume or Technical CV.
* Kuroko will parse, chunk, and vectorize your data into `kuroko_rag.db`.

### 3. The Interview

* **Trigger**: When asked a question, press **Alt + S**.
* **Response**: Kuroko will silently capture the last 30 seconds of context + your resume data, send it to the LLM, and stream the answer to a floating overlay.
* **Stealth**: If you need to share your screen, toggle **DEEP STEALTH** in settings. The app will vanish from the taskbar and screen-share streams immediately.

### 4. Panic Mode

* Press **Alt + Q** to immediately terminate the application process.

## 🛡 Disclaimer

This software is intended for educational purposes and personal assistance. Users are responsible for adhering to the terms of service of any interviewing platforms or agreements they have entered into.