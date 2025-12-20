# LISA

Native Windows host for the LISA assistant. This repo includes the WPF tray/overlay host, a local Python agent worker, and a local MCP tool server.

## Components
- `Host.Win`: WPF tray + overlay host that talks to the local Python agent over HTTP and starts local services.
- `Agent.Worker`: FastAPI agent worker (chat + tool calling).
- `Agent.MCP`: Local MCP tool server (system state, process/app inspection, file/disk tools).
- `Provider`: Local or hosted LLM backend (Ollama default).

## Prerequisites
- Windows 10/11
- .NET 8 SDK (x64). If x86 is on PATH, install x64 and ensure it is first: `dotnet --list-sdks`.
- Visual Studio 2022 (Community or higher) or VS Code with C# extension.

## Build & Run (Host.Win)
```powershell
dotnet build Host.Win/Host.Win/Host.Win.csproj -c Release
dotnet run --project Host.Win/Host.Win/Host.Win.csproj
```
On launch, the app lives in the system tray. Use the tray menu or Ctrl+Space (global hotkey) to toggle the overlay.

## Key Host.Win Features
- Tray icon with Open/Settings/Quit.
- Global hotkey (Ctrl+Space) via Win32; robust unregister on exit.
- Overlay: borderless, rounded, topmost, bottom-right position with subtle show/hide animations.
- Theme: auto-detect Windows light/dark; in-app toggle overrides for session.
- Modes: Talk / Chat / Share / Settings segmented control bound to `OverlayViewModel`.
- Status: live probes for MCP (`<McpHost>:<McpPort>`), Agent (`<AgentHost>:<AgentPort>`), and Provider (`<ProviderHost>:<ProviderPort>`).
- Chat UX: streaming assistant replies (content + reasoning), tool-call labels, copy/retry/stop actions per bubble, markdown rendering.
- IPC: HTTP client for `/health`, `/input/text`, and `/input/retry` to the Python agent; UDP callbacks for thinking/tool/content streaming.
- Context: active window title/process and screen detection stubs; agent system prompt includes local metadata (user, time, OS, locale).

## Autostart Helper
`Services/AutoStartHelper` adds/removes HKCU Run entry. Call from installer or settings UI as needed.

## Troubleshooting
- **SDK missing**: ensure .NET 8 x64 installed.
- **Hotkey conflict**: another app may own Ctrl+Space; change it in `HotkeyManager`.
- **Agent offline**: ensure `Agent.Worker` is running on `AgentHost:AgentPort` (default `127.0.0.1:5050`).
- **MCP offline**: ensure `Agent.MCP` is running on `McpHost:McpPort` (default `127.0.0.1:8123`).

