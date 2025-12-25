# AGENTS.md

## Purpose

LISA must:
- operate fully locally
- be low-latency and resource-efficient
- prefer standard, composable patterns
- avoid hidden coupling with the host or tools
- remain safe, predictable, and debuggable

---

## Core Principles

### 1. Local-first, edge-first
- All agent logic must run locally by default.
- No remote hosted inference is assumed.
- Network calls must be explicit, optional, and configurable, and local IPC must be authenticated.

### 2. Clear separation of responsibilities
- Agent: reasoning, planning, tool selection, state.
- Host (Windows): UX, OS APIs, audio/screen capture, playback.
- MCP tools: system inspection and automation primitives.

---

## Agent Responsibilities

An agent may:
- interpret user intent
- maintain conversation state
- decide when tools are required
- call MCP tools
- synthesize responses (text-first)
- return structured outputs to the host

An agent must not:
- block on long-running tasks without progress signals
- embed UI logic
- embed platform-specific assumptions
- silently fail or guess when a tool call is required

---

## Latency & Performance Constraints

Agents are designed for edge devices.

Guidelines:
- avoid unnecessary prompt bloat
- keep context windows tight and bounded
- stream outputs when possible
- reuse provider connections
- never reload models per request

Any change that increases latency or memory usage must be justified.

---

## State & Memory

- Conversation state lives in the agent.
- The host only keeps short-term UI cache.
- Long-term memory (if enabled) must be:
  - explicit
  - local
  - inspectable
  - opt-in

Memory strategies must be documented in `docs/ARCHITECTURE.md`.

---

## Temporary Workarounds & Technical Debt

Technical debt is not ignored - it is tracked.

Rules:
- Any temporary workaround must be:
  - clearly marked in code (`TODO`, `TEMP`, or equivalent)
  - documented in `pending_implementation.md`
- No silent hacks.

Untracked debt is considered a bug.

---

## Standards & Engineering Practices

- Prefer industry-standard libraries and protocols.
- Avoid bespoke formats unless justified.
- Favor readability and debuggability over cleverness.
- Deterministic behavior > creative behavior for automation.

Agent logic should be testable in isolation.

---

## Related Documents

- `docs/ARCHITECTURE.md` - system architecture and design decisions
- `docs/DEVELOPER.md` - developer-facing feature and integration guide
- `features.md` (gitignored) - product-level features, ideas, and roadmap
- `pending_implementation.md` (gitignored) - known issues, gaps, workarounds, and technical debt

