# LISA

Native Windows host for the LISA assistant. This repo currently includes the WPF tray/overlay shell (Host.Win) and placeholder sections for backend components.

## Components
- `Host.Win`: WPF tray + overlay host that talks to a local Python agent over HTTP.
- `MCP`: *(placeholder)* describe MCP wiring and port usage here.
- `Ollama`: *(placeholder)* describe local model setup here.
- `Assistant Features`: *(placeholder)* outline modes/LLM behaviors here.

## Prerequisites
- Windows 10/11
- .NET 8 SDK (x64). If x86 is on PATH, install x64 and ensure it’s first: `dotnet --list-sdks`.
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
- Status: live port probe to `127.0.0.1:8123` (configurable) with Ready/Offline dot.
- IPC: HTTP client for `/health` and `/intent` to the Python agent.
- Context: active window title/process and screen detection stubs; screen capture reserved for future.

## Autostart Helper
`Services/AutoStartHelper` adds/removes HKCU Run entry. Call from installer or settings UI as needed.

## Troubleshooting
- **SDK missing**: ensure .NET 8 x64 installed.
- **Hotkey conflict**: another app may own Ctrl+Space; change it in `HotkeyManager`.
- **Agent offline**: start the Python agent on `127.0.0.1:8123` or adjust `AppSettings.McpPort`.
