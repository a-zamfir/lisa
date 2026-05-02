# Testing Strategy

This document tracks LISA's automated test surface across `Agent.Worker` and `Host.Win`.

The goal is not just "have tests". The goal is:

- clear ownership of what is covered
- repeatable commands for local verification
- visible gaps by layer
- incremental expansion without losing track of status

## Principles

- Prefer fast unit and service tests first.
- Cover runtime-critical seams before broad UI coverage.
- Every new bug fix should either add a test or update this document with the missing seam.
- Keep Python and C# suites runnable independently.
- Avoid tests that depend on live providers, Outlook, Chrome, audio devices, or external services unless explicitly marked as integration/manual.

## Test Layers

### Agent.Worker

- `unit`
  - pure service logic
  - state containers
  - memory parsing/policy
  - skills manifest/runner contract
- `router/service`
  - FastAPI route behavior
  - tool approval flow
  - request shaping and response handling
- `integration-lite`
  - repo-local skills manifests
  - runner timeout/output/repo-boundary behavior

### Host.Win

- `unit`
  - models
  - serialization/persistence helpers
  - logging/redaction
  - HTTP client behavior
- `viewmodel/service`
  - cancellation paths
  - message state transitions
  - settings persistence and migration
- `manual / later`
  - WPF window composition
  - overlay interaction
  - audio capture/playback
  - tray/hotkey behavior

## Commands

### Python

From `Agent.Worker/`:

```powershell
$env:PYTHONDONTWRITEBYTECODE='1'
.\.venv\Scripts\python.exe -m pytest tests -q --basetemp=..\.artifacts\pytest_tmp -p no:cacheprovider
```

### C#

From repo root:

```powershell
dotnet test Host.Win.Tests\Host.Win.Tests.csproj
```

Solution-level test invocation is currently not the recommended entrypoint on this machine because `dotnet test Host.Win.sln` can stall with no useful output. Use the dedicated host test project command above until that is diagnosed.

If a solution-wide pass is specifically needed:

```powershell
dotnet test Host.Win.sln --logger "console;verbosity=minimal"
```

## Coverage Matrix

Legend:

- `done`
- `partial`
- `missing`

### Agent.Worker

| Area | Status | Notes |
|---|---|---|
| text routes | done | base happy path, tool flow, approval flow, filtering |
| skills runner | done | python/native-cli, repo boundary, malformed output, timeout, defaults/env |
| memory trailer | done | trailer stripping and parsing |
| memory integration | done | memory scheduling and prompt integration |
| conversation store | done | trimming and system/tool version refresh covered |
| tool approval manager | done | direct timeout/resolve tests covered |
| visual context store | done | set/peek/pop covered |
| system context | done | output sections and base prompt inclusion covered |
| provider service | done | provider selection, payload shaping, tool-call merge, readiness covered |
| session state | done | round-trip, ignore blanks, overwrite behavior covered |

### Host.Win

| Area | Status | Notes |
|---|---|---|
| AgentClient | done | cancellation and fallback behavior covered |
| ChatMessage / content segments | done | segment appends, markdown, tool-label filtering covered |
| SettingsService | done | save/load, migration, DPAPI round-trip covered |
| LoggingService | done | redaction and rotation covered |
| TraceLogUtil | done | append and summary behavior covered |
| OverlayViewModel cancellation | partial | host crash fixed indirectly through AgentClient; no direct viewmodel test yet |
| AgentProcessHost bootstrap | missing | no tests yet |
| overlay/window rendering | missing | manual only |

## Current Implementation Plan

### Phase 1

- Add this tracking document.
- Expand `Agent.Worker` service-level coverage:
  - `ConversationStore`
  - `ToolApprovalManager`
  - `visual_context`
- Create `Host.Win.Tests`.
- Add first host unit tests for:
  - `AgentClient`
  - `ChatMessage`
  - `SettingsService`
  - `LoggingService`
  - `TraceLogUtil`

### Phase 2

- Add host tests around cancellation/state cleanup in `OverlayViewModel`.
- Add Python tests for:
  - `system_context`
  - `provider`
  - `session_state`
- Split smoke/integration tests from pure unit tests if suite size grows.

### Phase 3

- Add manual verification checklist for:
  - overlay rendering
  - screen share lifecycle
  - TTS/STT
  - tool approval UX
  - skill execution with `agent-browser`

## Status Log

### 2026-04-05

- Created `docs/testing.md`.
- Expanded Python coverage for:
  - `ConversationStore`
  - `ToolApprovalManager`
  - `visual_context`
  - `provider`
  - `system_context`
  - `session_state`
- Added `Host.Win.Tests` to the solution.
- Added host coverage for:
  - `AgentClient`
  - `ChatMessage`
  - `SettingsService`
  - `LoggingService`
  - `TraceLogUtil`
- Current automated results:
  - `Agent.Worker`: `41 passed`
  - `Host.Win.Tests`: `14 passed`
- `dotnet test Host.Win.sln` still needs follow-up because it can stall even though the dedicated host test project runs cleanly.

## Exit Criteria

This document is in a good state when:

- `Agent.Worker` has direct tests for each core service module
- `Host.Win` has a real test project in the solution
- cancellation/fallback bugs in the host are covered by tests
- logging/settings/network seams are unit-tested
- the remaining gaps are explicitly manual-only and documented
