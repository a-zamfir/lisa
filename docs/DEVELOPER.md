# Developer Guide

## Prerequisites
- .NET 8 SDK (x64). If `dotnet --list-sdks` shows only x86, install x64 and ensure it is first on PATH.
- Windows 10/11 with WPF/Win32 available.
- Visual Studio 2022 (Community or higher) or VS Code with C# extension.
- Python 3.11+ for `Agent.Worker` and `Agent.MCP` (venvs are created on first run).

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
  - `agent_worker/routers/` - text endpoints.
  - `agent_worker/services/` - provider + MCP clients, settings loader, conversation state, system context builder.
  - `agent_worker/models/` - request/response models.
  - `agent_worker/prompts/` - system prompts (e.g., `lisa_assistant.md`) injected per request alongside system metadata (time, user, machine, OS, locale/region, home).
- `Agent.MCP/` - MCP tool server (system state, process/app, file/disk).

## Build & Run
1. Install .NET 8 SDK (x64).
2. From repo root:
   ```powershell
   dotnet build Host.Win/Host.Win/Host.Win.csproj -c Release
   ```
3. Run:
   ```powershell
   dotnet run --project Host.Win/Host.Win/Host.Win.csproj
   ```
4. On launch, the app lives in the system tray. Use tray menu or Ctrl+Space (global) to toggle overlay.

## Key Behaviors
- **Tray app**: `TrayIconManager` sets up icon/menu; `App` wires lifecycle.
- **Global hotkey**: `HotkeyManager` registers Ctrl+Space via Win32; released on exit.
- **Overlay**: `OverlayController` positions bottom-right with 16px margin, always on top; `OverlayWindow` shows non-intrusive animations (fade/slide) on show/hide.
- **Theme**: `ThemeService` reads Windows AppsUseLightTheme and merges `LightTheme.xaml` or `DarkTheme.xaml`. User toggle overrides for session.
- **Modes**: Segmented buttons bound to `OverlayViewModel.SelectedMode` (Talk, Chat, Share, Settings) swap content panes.
- **Status**: Three health chips. MCP checks `McpHost:McpPort`; Agent checks `AgentHost:AgentPort` via `/health`; Provider checks `ProviderHost:ProviderPort`. Updates every ~2s.
- **Chat**:
  - UI: bubble list, rounded input, styled send button, slim scrollbar with padding gap.
  - Sending: `AsyncRelayCommand` bound to button/Enter; disables during in-flight send.
  - Request: POST `/input/text` JSON `{ session_id, turn_id, text, input_meta }` via `AgentClient.SendTextAsync` to agent on `AgentHost:AgentPort` (default 127.0.0.1:5050).
  - Response: streamed content chunks (UDP) into the bubble live; reasoning/thinking streamed separately; inline typing dots while streaming; copy/retry/stop actions per message.
  - Tool calls: agent sends UDP callbacks to `127.0.0.1:5052` with phase updates (`awaiting_tool`, `tool_response`, `tool_complete`). Overlay updates the tool label in the bubble header.
  - Content streaming: agent emits `content_chunk` / `content_done`; overlay appends to message live. Reasoning panel auto-collapses when done.
  - Autoscroll: message collection change in `OverlayWindow` scrolls to end on new/streamed messages.
- **Context**: `ContextCollector` exposes active window title/process and primary screen.
- **Autostart helper**: `AutoStartHelper` sets/removes HKCU Run entry (call from settings/installer).
- **Logging & Observability**:
  - `LoggingService` writes JSONL to `%LOCALAPPDATA%/LISA/logs/host.log` and Trace. Each entry includes UTC timestamp, `event_type`, and payload.
  - Chat sends log `request.text.send` with `session_id`, `turn_id`, `input_type=text`, `text`, and `input_meta`; responses log `response.text` (or `response.text.missing` on fallback).
  - Agent emits UDP callbacks for thinking, tool phases, and content streaming; overlay updates reasoning panel and tool labels live.
  - `AgentClient` traces request/response lifecycle; extend similarly for audio/image endpoints when added.
- **Settings**:
  - Stored at `%LOCALAPPDATA%/LISA/host-settings.json` via `SettingsService` (JSON, pretty printed).
  - Fields: `mcp_host`, `mcp_port`, `agent_host`, `agent_port`, `provider_mode` (Local|Hosted), `provider_host`, `provider_port`, `provider_api_key`, `provider_model`, `provider_temperature`, `voice_type`, `voice_rate`, `voice_volume`.
  - Settings pane in overlay allows editing and saving; provider section switches between Local/Hosted (API key enabled only for Hosted); save reloads health checkers and agent base URL live and updates provider API key header for requests.

## Theming Notes
- Theme dictionaries are merged at runtime; keep palette keys in both light/dark files in sync.
- `OverlayWindow` and mode buttons use Segoe UI + Segoe MDL2 Assets icons.
- Scrollbar thumb restyled for compact look; chat input/send use rounded styles.

## Animations
- Show: fade + slight slide up (180ms, ease out).
- Hide: fade + slight slide down (140ms, ease in).
- Implemented in `OverlayWindow.PlayShowAnimation/PlayHideAnimation`; invoked by `OverlayController`.

## Hotkey & Focus
- Hidden message-only window handles WM_HOTKEY; overlay does not force activation to avoid focus theft.
- If registration fails (another app uses Ctrl+Space), tray remains usable; warning logged.

## Logging
- Uses `Trace` with a console listener added in `App.OnStartup`. Attach a debugger or configure listeners as needed.

## Troubleshooting
- **SDK missing**: Ensure x64 .NET 8 SDK installed; run `dotnet --list-sdks`.
- **Hotkey not working**: Another app may own Ctrl+Space; change hotkey in `HotkeyManager` if needed.
- **Agent Offline**: Verify Python agent `/health` on `127.0.0.1:<AgentPort>` (default 5050); MCP on `127.0.0.1:8123`; Provider on `127.0.0.1:11434`.
- **Chat send disabled**: Button is disabled while send is in-flight; ensure input not empty. If streaming shows no text, check `AgentClient.SendTextAsync` connectivity; mock response still returns text.
- **Theme resource errors**: Ensure both theme dictionaries define the same resource keys.

## Extending
- Add real content panes per mode by expanding `OverlayViewModel` and binding DataTemplates.
- Persist theme override or window state via user settings if needed.
- Add telemetry/log sinks by extending Trace listeners at startup.

- **Agent Worker**:
  - FastAPI, endpoints /health, /input/text, /input/retry.
  - On startup: fetch MCP tools (cached) and start refresh loop (6h cadence; retry every 30s until success). Build system context (LISA prompt + system metadata: time, user/account, machine, OS, locale/region, home) once and prepend to every provider request.
  - Tool calls: LLM uses tools property; server calls MCP /tools (list) and /call (execute). Tool phases send UDP callbacks.
  - Streaming: uses Ollama /api/chat streaming; partial content and reasoning forwarded via UDP to Host.Win for live display. 	ool_calls parsed from stream; early exit to call MCP. Content streaming forwarded via content_chunk / content_done callbacks for UI.
  - Settings: read from %LOCALAPPDATA%/LISA/host-settings.json (agent/provider hosts, ports, provider mode, model, temperature, API key, voice params).
