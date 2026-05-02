# Skills Migration Plan

This document is the working migration plan for LISA's CLI-first skills architecture.

The goal is not just to "make skills work". The goal is to make skills the only tool execution model across host, agent, settings, docs, logs, and tests.

## Target End State

- Tool execution is CLI-based only.
- Every callable capability lives under `skills/`.
- Each skill category is self-contained and repo-local.
- The agent discovers skills from `skills/*/SKILL.md`.
- The agent exposes tool metadata from per-skill `tools.json`.
- Tool execution is performed by a single hardened skill runner inside `Agent.Worker`.
- The agent supports progressive disclosure through an `activate_skill` tool that returns the full `SKILL.md` body on demand.
- `Host.Win` only knows about skills and skill categories.
- Legacy server/runtime terms, settings, and transport code are removed from the active runtime.

## Decisions Locked In

- Skills live at the project root under `skills/`.
- Execution is repo-only: no command outside the repo may be launched by the skill runner.
- Windows and Windows apps only for v1.
- Python is the default implementation language for v1 skills.
- A second explicit `native-cli` execution mode is allowed for trusted repo-local CLI tools when a Python wrapper is not appropriate.
- Skill discovery uses `SKILL.md` as the source of truth.
- LISA uses `tools.json` as a local runtime registry for callable tools.
- Validation is permissive for v1, with basic schema/type checks only.
- Internal request/runtime naming uses `active_skills_categories`.
- User-facing wording should be `Active Skills`.
- All installed skills are active by default.
- Skills share one normalized JSON output envelope.
- Existing approval UX remains in place for now; only the underlying runtime/terminology changes.
- Skills are grouped by category, starting with:
  - `windows-os`
  - `outlook`
  - `agent-browser`

## Standard Skill Shape

Each skill should follow the standard skill layout, with LISA-specific runtime metadata added alongside it:

```text
skills/
  windows-os/
    SKILL.md
    tools.json
    scripts/
  outlook/
    SKILL.md
    tools.json
    scripts/
  agent-browser/
    SKILL.md
    tools.json
    package.json
    scripts/
```

Optional directories remain valid:

- `references/`
- `assets/`
- `agents/openai.yaml`

## CLI Contract

All skill tools should be executable through a local CLI contract.

### Invocation Model

- The agent selects a tool from `tools.json`.
- The skill runner launches the declared repo-local command.
- The runner passes a JSON payload on stdin.
- The skill returns JSON on stdout.
- Non-zero exit codes and stderr are treated as failures unless explicitly normalized by the tool.
- `SKILL.md` remains the human-readable instruction surface; `tools.json` is only LISA's local callable-tool registry.

### Input Payload

```json
{
  "args": {
    "example": "value"
  }
}
```

### Output Payload

```json
{
  "success": true,
  "data": {},
  "error": null,
  "meta": {}
}
```

Recommended common fields:

- `success`
- `data`
- `error`
- `meta`

The agent runner may enrich the final result with:

- `duration_ms`
- `isError`

### Manifest Defaults

`tools.json` may define a top-level `defaults` object. Its values are merged into each tool entry so common runtime configuration can be declared once per skill.

Recommended uses:

- shared `execution_mode`
- shared repo-local `command`
- shared `env`
- shared `ensure_dirs`
- shared `timeout_ms`
- shared `max_output_kb`

Special merge behavior:

- `env` and `arg_flags` merge as dictionaries
- `ensure_dirs` is deduplicated and combined
- `args_schema.properties` and `args_schema.required` are merged
- other fields are overridden per tool

## Alignment With Agent Skills Guidance

- Progressive disclosure: the agent first sees skill names/descriptions and can call `activate_skill` to load the full `SKILL.md` body when needed.
- Relative script paths stay inside each skill directory and are documented directly in `SKILL.md`.
- CLI execution is repo-local. Python remains the default mode, while trusted repo-local native CLI tools can opt into `native-cli`. One-off package launchers such as `uvx` are intentionally not used in LISA's current runtime.
- `native-cli` tools may define fixed environment variables plus precreated runtime directories in `tools.json` so stateful CLIs can be pinned to a known writable LISA-managed runtime (for example `agent-browser` socket/profile/download paths under `%LOCALAPPDATA%`).
- Skill scripts expose `--help`, accept structured JSON on stdin, and return one normalized JSON envelope on stdout.

## Migration Phases

## Phase 1: Freeze The Runtime Boundary

Lock the architecture boundary before more skill work is added.

- Keep `skills/` as the only source of callable tools.
- Keep tool invocation subprocess-based.
- Stop adding new legacy tool-runtime codepaths.
- Treat superseded gateway/server files as deprecated immediately.

## Phase 2: Remove Legacy Runtime From Agent

Refactor `Agent.Worker` so it is skills-only.

- Remove legacy fallback tool clients from active paths.
- Remove legacy transport/auth propagation from request handling.
- Replace server-style category translation with direct skill category filtering.
- Make `SkillsClient` the only tool discovery and invocation path.

Expected result:

- No request path in the agent depends on any superseded transport or auth concept.

## Phase 3: Rename The Data Model

Refactor request, settings, and UI model names to match the new architecture.

- Replace any server-era naming with `active_skills_categories`.
- Remove obsolete host/server settings from the host configuration model.
- Replace old server item models with skill category models if category toggling remains user-controlled.
- Remove legacy wording from logs, events, comments, and bindings.

Expected result:

- No active runtime concept uses superseded server/runtime naming.

## Phase 4: Harden The Skill Runner

The current skill runner is close, but it still needs explicit hardening.

- Replace inherited environment execution with a sanitized allowlist environment.
- Add explicit executable allow rules:
  - repo-local script/exe only
  - no arbitrary shell string execution
- Normalize stdout/stderr/error handling across all skills.
- Add size limits, timeout handling, and structured failure envelopes consistently.
- Keep `native-cli` constrained to explicit repo-local commands and array-based arg mapping, never free-form shell strings.

Expected result:

- Skills are secure enough to be the default tool path.

## Phase 5: Migrate Skill Implementations Fully

Ensure each skill is self-contained and does not depend on deprecated legacy directories.

- Move any remaining reusable logic into `skills/.../scripts/`.
- Keep each skill category internally coherent:
  - `windows-os`: system inspection and automation
  - `outlook`: mail/calendar tasks
- Remove imports from superseded modules once migrated.

Expected result:

- `skills/` contains the complete implementation surface for tool calling.

## Phase 6: UI And Approval Alignment

Bring host UX in line with the skills architecture.

- Replace any hidden legacy selector UI with skill/category UI or remove it.
- Keep readiness based on:
  - skill folders present
  - valid manifests present
  - runner available
- Keep all installed skills active by default.
- Keep approval flow, but label it in skill/tool terms only.
- Ensure host status chips, logs, and tool banners reflect skills terminology.

Expected result:

- The overlay no longer exposes legacy server/runtime vocabulary or old server-shaped controls.

## Phase 7: Tests And Verification

Move verification to the skills architecture.

- Rewrite tests that still assume superseded runtime clients.
- Add tests for:
  - skill discovery
  - tool filtering by active skill/category
  - approval-required tools
  - malformed skill output
  - runner timeout and output size limits
  - repo-boundary enforcement
- Run parity checks for:
  - Windows OS read tools
  - Outlook read tools
  - Outlook write tools

Expected result:

- Tool calling behavior is verifiable without superseded-runtime assumptions.

## Phase 8: Documentation Cutover

After runtime parity is confirmed:

- Rewrite `README.md`
- Rewrite `docs/ARCHITECTURE.md`
- Rewrite `docs/DEVELOPER.md`
- Rewrite `docs/AGENTS.md`
- Remove obsolete legacy troubleshooting and port docs

Expected result:

- The docs describe the codebase that actually exists.

## Current Status

The active runtime is already skills-first:

- `Host.Win` no longer starts local gateway/server processes.
- `Agent.Worker` uses `SkillsClient` as the only active tool client.
- Request metadata uses `active_skills_categories`.
- Host settings no longer carry legacy transport fields.
- Core docs (`README.md`, `docs/ARCHITECTURE.md`, `docs/DEVELOPER.md`, `docs/AGENTS.md`) describe the skills-first runtime.

## Remaining Gaps

### Runtime Hardening

- Keep tightening the skill runner:
  - repo-boundary enforcement
  - sanitized environment
  - timeout and output-size limits
  - one normalized JSON envelope
- Ensure all write-capable tools return stable, explicit success/error payloads.
- Consider trust-gating project-local skill loading if LISA is ever pointed at arbitrary or newly cloned repositories, so untrusted repos cannot silently inject skill instructions.

### Skill Implementation Migration

- Move any remaining reusable logic fully under `skills/.../scripts/`.
- Remove any remaining superseded gateway/server directories once code, tests, and docs are fully clear.
- Keep Python as the default skill entrypoint style, with `native-cli` used narrowly for trusted local CLIs such as `agent-browser`.
- Repo-pinned non-Python dependencies are allowed inside an individual skill directory when invoked through a repo-local Python wrapper, as with `skills/agent-browser`.

### Host / UX Cleanup

- Keep the readiness/status model and labels in skill/tool terms only.
- Revisit skill/category visibility in the UI later if manual toggling returns.
- Keep approval UX unchanged for now.

### Tests

- Expand `Agent.Worker` coverage around:
  - skill discovery
  - category filtering
  - trailer stripping
  - runner safety rules
  - malformed tool output
  - timeout and repo-escape behavior

### Repo / Docs Hygiene

- Remove deprecated gateway/server directories only after all remaining references are gone from code, tests, and docs.
- Keep this document focused on remaining migration work rather than already-completed legacy tasks.

## Verification Criteria

The migration is complete only when all of the following are true:

- A clean host startup does not reference superseded gateway/server startup, ports, or readiness.
- A clean agent startup does not initialize any superseded tool client.
- Tool requests use only the skills registry and skill runner.
- The settings file contains no deprecated transport fields.
- Tests do not patch or import superseded clients in active runtime coverage.
- Docs do not describe any superseded runtime path as supported.
- Deprecated gateway/server directories can be deleted without breaking build or runtime.

## Proposed Execution Order

1. Remove legacy runtime assumptions from `Agent.Worker` request/runtime paths.
2. Rename request/settings/UI models from legacy naming to skills.
3. Harden the skill runner and finalize the CLI contract.
4. Migrate remaining skill logic fully into `skills/`.
5. Rewrite tests around `SkillsClient`.
6. Rewrite docs and remove deprecated legacy dependencies.

## Decisions Confirmed On 2026-04-02

1. Internal request/runtime field: `active_skills_categories`
2. User-facing label: `Active Skills`
3. All installed skills active by default
4. Python-default tool entrypoints for v1, with an explicit `native-cli` escape hatch for trusted repo-local CLIs
5. One shared JSON output envelope across skills
6. Approval UX stays as-is for now
