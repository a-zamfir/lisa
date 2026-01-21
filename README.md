# LISA

LISA (Local Intelligent Systems Assistant) is a fully local, privacy-first Windows assistant that can operate your OS via tools.

- No remote hosted inference
- No cloud dependencies
- You own the model, the tools, and the data

This repository contains:
- a native Windows tray + overlay host (`Host.Win`)
- a local Python agent worker (`Agent.Worker`)
- a local MCP tool server (`Agent.MCP`)
- a local, user-configured LLM provider (BYOM; LM Studio default, Ollama supported)

---

## Goals

- Fully local: all inference and automation runs on your machine.
- Privacy & ownership: prompts, responses, logs, audio, and screen context never leave your system.
- OS automation: LISA uses tools to inspect and operate Windows (not just chat).
- Fast, minimal UX: always available via hotkey and a small overlay.
- Extensible: MCP tool servers allow new capabilities without changing the assistant core.

---

## What LISA Can Do

### Phase 1 (Done)
- Chat with a local LLM (LM Studio / Ollama).
- Call MCP tools for Windows inspection and automation.
- Stream responses for low-latency UX.

### Phase 2 (Done)
- Talk mode: local STT with VAD + spoken responses.

### Phase 3 (Done)
- Share mode: arm Share and the next prompt includes a one-shot screenshot (vision-capable models).

### Phase 4 (Done)
- Memory: opt-in local storage (SQLite + FTS when available) with key/value strings, updated asynchronously.

### Phase 5 (Current)
- Installer and polish: UX refinement, reliability hardening, and setup smoothness.

---

## Architecture Overview

- Host.Win (C# / WPF): overlay UI, tray/hotkeys, mic capture, screen capture, audio playback, settings.
- Agent.Worker (Python / FastAPI): conversation state, orchestration, tool routing, provider calls.
- Agent.MCP (Python): local MCP tool server exposing Windows tools.
- Provider (Local LLM): BYOM. Default is LM Studio; Ollama supported. LISA does not ship models.

All services communicate over `127.0.0.1`.

---

## Key Host.Win Features

- Tray icon with Open / Settings / Quit
- Global hotkey (default Ctrl+Space)
- Overlay with subtle show/hide animations
- Modes: Talk / Chat / Share / Settings
- Streaming chat UX with tool-call indicators + Stop button
- TTS playback controls (rate + volume) and auto-play in Chat (optional)
- Optional verbose logging to `%LOCALAPPDATA%/LISA/logs/verbose-trace.log`

---

## Build & Run (Host.Win)

```powershell
dotnet build Host.Win/Host.Win.csproj -c Release
dotnet run --project Host.Win/Host.Win.csproj
```

On launch, LISA runs in the system tray. Use the tray menu or Ctrl+Space to toggle the overlay.

---

## Default Ports

- Agent.Worker: `127.0.0.1:5050`
- Agent.MCP: `127.0.0.1:8123`
- LM Studio (default): `127.0.0.1:1234`
- Ollama (optional): `127.0.0.1:11434`

---

## Share Mode (One-Shot Screenshot)

1. Click Share to arm it.
2. Send a prompt (Chat or Talk).
3. Host captures a single screenshot (primary monitor), uploads it to the agent, and the agent attaches it to that prompt.
4. Share auto-disarms after the prompt is sent.

---

## Troubleshooting

- Hotkey not working: another app may own Ctrl+Space; change it in settings/code.
- Agent offline: verify `Agent.Worker` is reachable at `http://127.0.0.1:5050/health`.
- MCP offline: verify `Agent.MCP` is reachable on `127.0.0.1:8123`.
- Vision not working: ensure provider is LM Studio/OpenAI with a vision-capable model and Share is armed before sending a prompt.

---

## Roadmap

- Phase 1: Chat + MCP tools (local LLM)
- Phase 2: Talk mode (local STT + VAD + tools)
- Phase 3: Share mode (screen context)
- Phase 4: Local memory (opt-in, on-disk)
- Phase 5 (current): Installer, polish

