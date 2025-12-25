# Developer Guide

## Prerequisites

- .NET 8 SDK (x64). If `dotnet --list-sdks` shows only x86, install x64 and ensure it is first on PATH.
- Windows 10/11 with WPF/Win32 available.
- Visual Studio 2022 (Community or higher) or VS Code with C# extension.
- Python 3.11-3.13 (x64) recommended for `Agent.Worker` and `Agent.MCP` (venvs are created on first run; requirements install is skipped unless hashes change).
  - Talk/STT dependencies are split into `Agent.Worker/requirements-speech.txt` because some packages may require native build tooling on very new Python versions.

## Solution Layout

- `Host.Win.sln` - solution entry.
- `Host.Win/` - WPF tray/overlay app.
  - `App.xaml` / `App.xaml.cs` - startup, wiring, DI-lite.
  - `Views/OverlayWindow.xaml` - overlay UI surface (chat UI, status chips, theming).
  - `ViewModels/` - `OverlayViewModel` and base helpers.
  - `Services/` - hotkey, tray, theme, port health, context collector, agent client, agent process host, MCP process host.
  - `Themes/` - `LightTheme.xaml`, `DarkTheme.xaml`.
  - `Models/` - DTOs/enums (assistant modes, settings, text input/response, chat messages, metadata).
  - `Commands/` - `RelayCommand`, `RelayCommand<T>`, `AsyncRelayCommand`.
- `Agent.Worker/` - FastAPI agent service (chat + tool calls).
  - `agent_worker/routers/` - HTTP endpoints.
  - `agent_worker/services/` - provider + MCP clients, settings loader, conversation state, system context builder.
  - `agent_worker/models/` - request/response models.
  - `agent_worker/prompts/` - system prompts injected per request alongside system metadata (time, user, machine, OS, locale/region, home).
- `Agent.MCP/` - MCP tool server (system state, process/app, file/disk).

## Build & Run

1. Install .NET 8 SDK (x64).
2. From repo root:
   ```powershell
   dotnet build Host.Win/Host.Win.csproj -c Release
   ```
3. Run:
   ```powershell
   dotnet run --project Host.Win/Host.Win.csproj
   ```
4. On launch, the app lives in the system tray. Use tray menu or Ctrl+Space (global) to toggle overlay.

## Key Behaviors

- Tray app: `TrayIconManager` sets up icon/menu; `App` wires lifecycle.
- Global hotkey: `HotkeyManager` registers Ctrl+Space via Win32; released on exit.
- Overlay: `OverlayController` positions bottom-right with 16px margin, always on top; `OverlayWindow` shows non-intrusive animations (fade/slide) on show/hide.
- Theme: `ThemeService` reads Windows AppsUseLightTheme and merges `LightTheme.xaml` or `DarkTheme.xaml`. User toggle overrides for session.
- Modes: segmented buttons bound to `OverlayViewModel.SelectedMode` (Talk, Chat, Share, Settings) swap content panes.
- Share (one-shot): Share is armed via the Share mode button; the next Chat/Talk prompt captures a one-shot screenshot (primary monitor), the host briefly hides/collapses the overlay (currently a few ms to reduce capture), uploads it to `Agent.Worker` (`POST /visual-context/frame`), then auto-disarms.
- Status: three health chips. MCP checks `McpHost:McpPort`; Agent checks `AgentHost:AgentPort` via `/health`; Provider checks `ProviderHost:ProviderPort`. Backoff when down (up to ~10s).
- Chat:
  - Sending: `AsyncRelayCommand` bound to button/Enter; disables during in-flight send.
  - Request: POST `/input/text` JSON `{ session_id, turn_id, text, input_meta }` via `AgentClient.SendTextAsync` to agent on `AgentHost:AgentPort` (default 127.0.0.1:5050).
  - Response: streamed content chunks into the bubble live; reasoning/thinking streamed separately; inline typing dots while streaming; copy/retry/stop actions per message.
  - Tool calls: agent sends TCP callbacks with phase updates (`awaiting_tool`, `tool_response`, `tool_complete`) and validates token + per-session nonce. Overlay updates the tool label in the bubble header.
  - Content streaming: agent emits `content_chunk` / `content_done`; overlay appends to message live.
- Context: `ContextCollector` exposes active window title/process and primary screen.
- Autostart helper: `AutoStartHelper` sets/removes HKCU Run entry (call from settings/installer).
- Logging & Observability:
  - `LoggingService` writes JSONL to `%LOCALAPPDATA%/LISA/logs/host.log` and also logs to Trace.
  - When `verboseLogging` is enabled, additional detail is written to `%LOCALAPPDATA%/LISA/logs/verbose-trace.log` (and some subsystems log additional structured fields).
  - `AgentClient` traces request/response lifecycle; includes audio (`/input/audio`) and visual frame uploads (`/visual-context/frame`).

## Settings

- Stored at `%LOCALAPPDATA%/LISA/host-settings.json` via `SettingsService` (JSON, pretty printed; provider API key encrypted at rest via DPAPI CurrentUser).
- Fields: `mcpHost`, `mcpPort`, `agentHost`, `agentPort`, `providerType` (LM Studio|Ollama|OpenAI), `providerMode` (Local|Hosted), `providerHost`, `providerPort`, `providerApiKey`, `providerModel`, `providerTemperature`, `providerThink`, `voiceType`, `voiceRate`, `voiceVolume`, `ttsEnabledInChat`, `verboseLogging`, `piperMode` (exe|python), `piperExePath`, `piperPythonPath`, `piperVoiceModelPath`, `piperVoiceConfigPath`, `piperSpeakerId`.
- UI note: `voiceRate` and `voiceVolume` are persisted as floats, but the UI edits them as integer percentages.
- UX note: `ttsEnabledInChat` controls auto-play for Chat messages; Talk mode still plays TTS for responses.

## TTS (Host)

- Piper is GPL (`piper-tts` / OHF-Voice/piper1-gpl): keep it behind a subprocess boundary, do not embed/link it, and avoid redistributing Piper binaries in our app to prevent GPL contamination.
- `TtsService` invokes Piper as an external process in one of two modes:
  - `piper.exe` via `PiperExePath`
  - `python -m piper` via `PiperPythonPath` when Piper is installed in a venv/system
- Input text is passed via stdin; output is a temp WAV file read into memory for playback (NAudio) and replay.
- If Piper fails, `TtsService` falls back to Windows SAPI when available.

## Theming Notes

- Theme dictionaries are merged at runtime; keep palette keys in both light/dark files in sync.
- `OverlayWindow` and mode buttons use Segoe UI + Segoe MDL2 Assets icons.
- Scrollbar thumb is restyled for compact look.

## Animations

- Show: fade + slight slide up (180ms, ease out).
- Hide: fade + slight slide down (140ms, ease in).
- Implemented in `OverlayWindow.PlayShowAnimation/PlayHideAnimation`; invoked by `OverlayController`.

## Hotkey & Focus

- Hidden message-only window handles WM_HOTKEY; overlay does not force activation to avoid focus theft.
- If registration fails (another app uses Ctrl+Space), tray remains usable; warning logged.

## Troubleshooting

- SDK missing: ensure x64 .NET 8 SDK installed; run `dotnet --list-sdks`.
- Hotkey not working: another app may own Ctrl+Space; change hotkey in `HotkeyManager` if needed.
- Agent offline: verify Python agent `/health` on `127.0.0.1:<AgentPort>` (default 5050); MCP on `127.0.0.1:8123`; provider on `127.0.0.1:<ProviderPort>` (default 1234 for LM Studio; 11434 for Ollama).
- Chat send disabled: button is disabled while send is in-flight; ensure input not empty. If streaming shows no text, check `AgentClient.SendTextAsync` connectivity.
- Theme resource errors: ensure both theme dictionaries define the same resource keys.

## Agent Worker Notes

- FastAPI endpoints: `/health`, `/input/text`, `/input/retry`, `/input/audio`, `/visual-context/frame`.
- Provider integration:
  - LM Studio / OpenAI: OpenAI-compatible chat completions (`POST /v1/chat/completions`) with SSE streaming.
  - Ollama: `POST /api/chat` streaming.
- Streaming: provider stream is parsed; partial content and thinking are forwarded via TCP callbacks to Host.Win for live display. Tool calls are detected and executed against MCP; phases are sent as callbacks.
- Conversations: in-memory store with approximate token cap + max message count trimming.
- Settings: read from `%LOCALAPPDATA%/LISA/host-settings.json` (agent/provider hosts, ports, mode, model, temperature, think flag, API key, voice params).

