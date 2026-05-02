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

Agents should reason in terms of skills and tools only.

## Performance Rules

- keep prompts bounded
- avoid repeated registry/context bloat
- stream whenever possible
- do not reload heavy dependencies per request
- prefer deterministic tool flows over speculative retries

## Memory Rules

Long-term memory must stay:

- local
- inspectable
- bounded
- opt-in

If relevant memory is not found, the agent should ask instead of guessing.

## Failure Rules

Agents must not:

- silently ignore a required skill/tool call
- guess tool results
- hide failed tool execution
- block for long periods without progress signals

If a tool cannot run, the agent should surface that clearly and continue safely.

## Engineering Rules

- prefer standard formats and predictable contracts
- keep runtime boundaries explicit
- keep tool execution testable in isolation
- document technical debt instead of normalizing it

## Related Documents

- `README.md`
- `docs/ARCHITECTURE.md`
- `docs/DEVELOPER.md`
- `docs/skills.md`
