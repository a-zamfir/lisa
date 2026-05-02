# Agent Guidelines

## Purpose

LISA agents must remain:

- local-first
- low-latency
- predictable
- debuggable
- safe around automation

## Core Responsibilities

The agent is responsible for:

- interpreting user intent
- maintaining conversation state
- deciding when a skill/tool is required
- invoking skills
- synthesizing responses
- returning structured outputs to the host

The host is responsible for:

- UI
- mic capture
- screen capture
- playback
- settings
- permission/approval surfaces

## Tooling Model

The active tooling model is skills-first.

- skills are repo-local
- discovery comes from `SKILL.md`
- callable tools come from `tools.json`
- execution is CLI-based
- v1 uses Python entrypoints only

## State & Memory

- Conversation state lives in the agent.
- The host only keeps short-term UI cache.

## Failure Rules

Agents must not:

## Standards & Engineering Practices

- Prefer industry-standard libraries and protocols.
- Avoid bespoke formats unless justified.
- Favor readability and debuggability over cleverness.
- Deterministic behavior > creative behavior for automation.

Agent logic should be testable in isolation.

---

## Related Documents

- `README.md`
- `docs/ARCHITECTURE.md`
- `docs/DEVELOPER.md`
- `docs/skills.md`
