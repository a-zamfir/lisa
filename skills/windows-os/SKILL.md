---
name: windows-os
description: Windows system diagnostics and file/process inspection. Use when the user asks for system status (CPU/RAM/disk/battery), running processes, network state, startup entries, disk usage, large files, duplicates, recent changes, or file metadata on Windows.
---

# Windows OS

Use this skill to inspect Windows system state and local files/processes.

## Run

Run a tool by invoking the runner and pass arguments as JSON on stdin:

- Command: `python scripts/run.py --tool <tool-id>`
- Help: `python scripts/run.py --help`
- Input JSON: `{"args": { ... }}` (object)
- Output JSON: `{ "success": true, "data": { ... }, "error": null, "meta": { ... } }` or `{ "success": false, "data": null, "error": "..." }`

## Available scripts

- `scripts/run.py` - skill entrypoint that dispatches to a specific tool id.
- `scripts/tools.py` - Windows OS tool implementations.
- `scripts/ps.py` - PowerShell execution helpers used by the tools.

## Workflow

1. Activate this skill when the user asks for Windows system or filesystem inspection.
2. Choose the smallest matching tool from `tools.json`.
3. Run `python scripts/run.py --tool <tool-id>` and pass only the needed JSON args on stdin.
4. Use the returned structured JSON to answer the user directly; if the tool fails, surface the error briefly and suggest the next diagnostic step.

## Tools

- `system_overview`: CPU/memory/disk/battery overview and uptime.
- `top_processes`: Top processes by CPU and memory.
- `network_usage`: Network adapter status and counters.
- `power_state`: Battery status, power plan, sleep capabilities.
- `process_inspector`: Inspect a process by name, PID, or foreground app.
- `startup_entries`: Registry + scheduled task startup entries.
- `large_files`: Largest files within a folder.
- `find_duplicates`: Duplicate files by hash within a folder.
- `disk_usage_by_extension`: Aggregate sizes by file extension.
- `recent_changes`: Recent file changes within a folder.
- `file_metadata`: File metadata and Zone.Identifier info.

## Notes

- Require Windows PowerShell and local filesystem access.
- Keep all execution inside the repo; use the runner to enforce safety.
- See `tools.json` for argument schemas and approval requirements.
