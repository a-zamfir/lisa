# LISA

LISA is a local-first Windows assistant with a native tray/overlay host, a local Python agent, and repo-local CLI skills.

## Current Runtime

- `Host.Win`: WPF tray + overlay UI
- `Agent.Worker`: local FastAPI agent
- `skills/`: repo-local tool/automation skills
- local provider: LM Studio by default, Ollama supported, OpenAI-compatible hosted mode optional

All runtime communication stays on `127.0.0.1`.

## Product Shape

- Chat
- Talk
- Share
- Settings
- Long-term memory
- Tool approval flow

## Tool System

LISA is now skills-first.

- Tool discovery comes from `skills/*/SKILL.md`
- Callable tool metadata comes from per-skill `tools.json`
- Tools are launched as repo-local CLI commands by `Agent.Worker`
- V1 skills use Python entrypoints only

Current skill categories:

- `windows-os`
- `outlook`

### Phase 4 (Done)
- Memory: opt-in local storage (SQLite + FTS when available) with key/value strings, updated asynchronously.

### Phase 5 (Current)
- Installer and polish: UX refinement, reliability hardening, and setup smoothness.

---

## Memory

Long-term memory is local and opt-in.

- stored in SQLite under `%LOCALAPPDATA%/LISA/`
- retrieved per prompt using lexical search
- updated asynchronously so chat latency is not blocked by writes

## Share Mode

Share is one-shot.

1. Arm Share
2. Send the next prompt
3. Host captures a single screenshot
4. Agent attaches it to that request
5. Share auto-disarms

## Build & Run

```powershell
dotnet build Host.Win/Host.Win.csproj -c Release
dotnet run --project Host.Win/Host.Win.csproj
```

On launch, LISA lives in the tray. Use the tray menu or `Ctrl+Space` to toggle the overlay.

## Default Ports

- `Agent.Worker`: `127.0.0.1:5050`
- `LM Studio`: `127.0.0.1:1234`
- `Ollama`: `127.0.0.1:11434`

## Troubleshooting

- Agent offline: verify `http://127.0.0.1:5050/health`
- Provider offline: verify your configured provider host/port
- Skills offline: verify `skills/` exists, contains valid `SKILL.md` + `tools.json`, and the agent can start
- Vision not working: use a vision-capable model and arm Share before sending the prompt
- Talk not working: verify STT/TTS local dependencies are installed for your chosen setup

---

## Roadmap

- Phase 1: Chat + MCP tools (local LLM)
- Phase 2: Talk mode (local STT + VAD + tools)
- Phase 3: Share mode (screen context)
- Phase 4: Local memory (opt-in, on-disk)
- Phase 5 (current): Installer, polish

- [Architecture](docs/ARCHITECTURE.md)
- [Developer Guide](docs/DEVELOPER.md)
- [Agent Guidelines](docs/AGENTS.md)
- [Skills Plan](docs/skills.md)
