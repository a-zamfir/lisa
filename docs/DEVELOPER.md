# Developer Guide

## Prerequisites

- .NET 8 SDK (x64)
- Windows 10/11
- Python 3.11-3.13 (x64) for `Agent.Worker`
- local provider runtime:
  - LM Studio, or
  - Ollama

Optional local dependencies:

- `whisper.cpp` for preferred STT
- faster-whisper as fallback STT path
- Chatterbox / Piper-compatible local TTS setup depending on your config

## Solution Layout

- `Host.Win/`
  - WPF host application
  - overlay, tray, settings, chat UI, playback, capture
- `Agent.Worker/`
  - FastAPI agent
  - provider integration
  - memory
  - skills runner
  - tests
- `skills/`
  - repo-local skills
  - currently `windows-os` and `outlook`
- `docs/`
  - architecture, developer notes, plans

## Build & Run

```powershell
dotnet build Host.Win/Host.Win.csproj -c Release
dotnet run --project Host.Win/Host.Win.csproj
```

The host bootstraps `Agent.Worker` automatically.

## Active Tool Architecture

The active tool system is skills-first.

- `Agent.Worker` discovers skills from `skills/*/SKILL.md`
- callable tool metadata is loaded from `tools.json`
- tool execution happens through the skill runner
- the skill runner launches repo-local Python CLI entrypoints

This is the current runtime model.

## Host Settings

Settings are stored in:

`%LOCALAPPDATA%/LISA/host-settings.json`

Key live fields include:

- `agentHost`
- `agentPort`
- `providerType`
- `providerMode`
- `providerHost`
- `providerPort`
- `providerApiKey`
- `providerModel`
- `providerTemperature`
- `providerThink`
- `voiceRate`
- `voiceVolume`
- `ttsEnabledInChat`
- `verboseLogging`
- `memoryEnabled`
- `ttsEngine`
- `chatterboxPythonPath`
- `chatterboxWorkingDir`
- `chatterboxRefAudio`
- Piper-related compatibility fields where still used by the host TTS path

## Health / Readiness

The host shows three runtime chips:

- Skills
- Agent
- Provider

Readiness is based on:

- skill folders/manifests available
- agent reachable on `/health`
- provider host/port reachable

## Skills

Each skill should contain:

- `SKILL.md`
- `tools.json`

Each tool definition should describe:

- `id`
- `description`
- `command`
- `args_schema`
- `requires_approval`
- `timeout_ms`
- `max_output_kb`

V1 policy:

- Python entrypoints only
- repo-local command only
- normalized JSON output

## Test Workflow

Run worker tests from the worker venv:

```powershell
cd Agent.Worker
.\.venv\Scripts\python.exe -m pytest -q
```

Relevant current coverage areas:

- text routes
- memory integration
- skills client / skill runner behavior

## Troubleshooting

- Agent offline:
  - verify `http://127.0.0.1:5050/health`
- Provider offline:
  - verify your configured provider host/port
- Skills not ready:
  - verify `skills/` exists
  - verify each skill has valid `SKILL.md` and `tools.json`
- Talk not working:
  - verify STT dependencies are installed for your chosen backend
- TTS not working:
  - verify your local Chatterbox/Piper-compatible configuration

## Current Cleanup Direction

When changing architecture or runtime behavior:

1. change the runtime first
2. update tests
3. update docs
4. remove stale compatibility code

Do not add legacy transport paths back into the active runtime.
