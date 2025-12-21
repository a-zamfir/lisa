# LISA

**LISA (Local Intelligent Systems Assistant)** is a fully local, privacy-first Windows assistant that can *actually operate your OS*, the kind of things Siri and Cortana never reliably delivered.

No remote hosted inference.  
No cloud dependencies.  
You own the model, the tools, and the data.

This repository contains:
- a **native Windows tray + overlay host**
- a **local Python agent worker**
- a **local MCP tool server**
- a **local, user-configured LLM provider** (BYOM, Ollama by default)

---

## Goals

- **Fully local**  
  All inference, automation, and data processing run on your machine.

- **Privacy & data ownership**  
  Prompts, responses, logs, audio, and screen context never leave your system.

- **OS automation**  
  LISA is designed to *inspect, explain, and operate* Windows through tools — not just chat.

- **Fast, minimal UX**  
  Always available via hotkey and a small overlay. No heavyweight UI.

- **Extensible by design**  
  MCP tool servers allow new capabilities without changing the assistant core.

---

## What LISA Can Do

### Phase 1 (Done)
- Chat with a **local LLM** (Ollama provider).
- Call **MCP tools** for Windows inspection and automation.
- Stream responses for CLI-like responsiveness.
- Operate entirely offline.

### Phase 2 (Current)
- **Talk mode**: local STT with VAD + spoken responses.

### Near Term
- **Share mode**: local screen capture → reasoning → actions.
- **Memory**: opt-in local storage for preferences and context.

---

## Architecture Overview

LISA is split into clear, local components:

- **Host.Win (C# / WPF)**  
  Native Windows “body”: overlay UI, tray, hotkeys, mic/screen capture, audio playback, settings.

- **Agent.Worker (Python / FastAPI)**  
  “Brain”: conversation state, orchestration, tool routing, and provider calls.

- **Agent.MCP (Python)**  
  Local MCP server exposing Windows tools (system, processes, files, network).

- **Provider (Local LLM)**  
  Bring-your-own model. Default is **Ollama**. LISA does not ship or host models.

All services communicate over `127.0.0.1`. No remote inference.

---

## Repository Structure

- `Host.Win`  
  WPF tray + overlay host.

- `Agent.Worker`  
  FastAPI agent worker (chat + tool calling).

- `Agent.MCP`  
  Local MCP tool server for Windows automation.

- `Provider`  
  Local or optional hosted model backends (Ollama by default).

---

## Key Host.Win Features

- Tray icon with **Open / Settings / Quit**
- Global hotkey (default **Ctrl+Space**) via Win32
- Overlay:
  - borderless, rounded, topmost
  - bottom-right anchored
  - subtle show/hide animations
- Theme:
  - auto-detect Windows light/dark
  - in-app override per session
- Modes:
  - **Talk / Chat / Share / Settings**
- Live status indicators:
  - Agent health
  - MCP health
  - Provider (LLM) health
- Streaming chat UX with tool-call indicators
- Local context injection (active window title/process)

---

## MCP Tool Server

MCP tools are where LISA goes beyond chat.

Examples:
- System state (CPU, RAM, disk, network)
- Process and app inspection
- File and disk analysis
- Network diagnostics

Design principles:
- Read-only first
- Explicit confirmation for actions
- No silent or destructive operations

---

## Local-Only Policy

- Services bind to **127.0.0.1**
- No telemetry by default
- No remote inference
- Models are user-installed (BYOM)
- Logs and data remain on disk

---

## Prerequisites

- Windows 10 / 11
- .NET 8 SDK (x64)  
  Ensure x64 `dotnet` is first on PATH: `dotnet --list-sdks`

---

## Build & Run (Host.Win)

```powershell
dotnet build Host.Win/Host.Win/Host.Win.csproj -c Release
dotnet run --project Host.Win/Host.Win/Host.Win.csproj
```

On launch, LISA runs in the system tray.  
Use the tray menu or **Ctrl+Space** to toggle the overlay.

---

## Default Ports

- **Agent.Worker**: `127.0.0.1:5050`  
- **Agent.MCP**: `127.0.0.1:8123`  
- **Ollama**: `127.0.0.1:11434`

---

## Autostart

`Services/AutoStartHelper` manages an **HKCU Run** entry so LISA can start with Windows.  
This can be toggled from Settings or handled by an installer.

---

## Local-Only Policy

- All services bind to **127.0.0.1** by default.
- No remote inference.
- No telemetry unless explicitly enabled.
- Models are **BYOM** (bring your own model via Ollama).
- Prompts, responses, audio, screen data, and logs remain on your machine.

---

## MCP Tooling Philosophy

MCP tools are where LISA goes beyond chat.

Design principles:
- **Read-first**: inspection and diagnostics before actions.
- **Explicit confirmation** for state-changing operations.
- **Allowlists** instead of arbitrary command execution.
- **Explain before acting** when an action could affect system state.

Examples:
- System state (CPU, RAM, disk, network)
- Process and app inspection
- File and disk analysis
- Network diagnostics

---

## Troubleshooting

- **.NET SDK issues**  
  Ensure .NET 8 **x64** is installed and first on PATH.

- **Hotkey not working**  
  Another app may own Ctrl+Space. Change it in Settings.

- **Agent offline**  
  Verify `Agent.Worker` is running and reachable on localhost.

- **MCP offline**  
  Verify `Agent.MCP` is running and reachable.

- **Local model feels slower than CLI**  
  Ensure streaming is enabled and HTTP clients are reused.

---

## Roadmap

- **Phase 1**: Chat + MCP tools (local LLM)
- **Phase 2**: Talk mode (local STT + VAD + tools)
- **Phase 3**: Share mode (screen context → actions)
- **Phase 4**: Local memory (opt-in, on-disk)
- **Phase 5**: Installer, sane defaults, polish

---

LISA aims to be a practical, local-first assistant that understands your system and helps you operate it, reliably, privately, and fast.
