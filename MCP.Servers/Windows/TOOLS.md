# Agent.MCP Tool Reference

Local MCP server runs at `http://127.0.0.1:8123`.

All endpoints require `X-MCP-Token` to match `MCP_AUTH_TOKEN` (set by the Host/Agent at startup).

## Endpoints

- `GET /tools` - minimal list (name + description)
- `GET /tools/docs` - detailed list (name, description, category, args)
- `POST /call` - execute a tool

Example:
```json
{
  "tool": "system_overview",
  "args": {}
}
```

## Tools

### system_overview
- Does: CPU/RAM/Disk usage, battery, power mode, uptime.
- Args: none.
- Example prompt: "Give me a quick health check of this machine."

### top_processes
- Does: Top processes by CPU.
- Args: `limit` (int 1-20, default 8).
- Example prompt: "Which 5 processes are using the most CPU?"

### network_usage
- Does: Adapter status, link speed, RX/TX bytes, VPN status.
- Args: none.
- Example prompt: "Is my VPN connected and what is the adapter status?"

### power_state
- Does: Battery status, active power scheme, sleep capability, last wake info.
- Args: none.
- Example prompt: "What power plan am I on and how much battery is left?"

### process_inspector
- Does: Active window + process + path, args, parent/child, signature, startup origins.
- Args: `process_name` (string) or `pid` (int). If none, inspects foreground window.
- Example prompt: "What is the foreground app and where is it running from?"

### startup_entries
- Does: Startup items from registry and scheduled tasks.
- Args: `filter_text` (string), `limit` (int, default 20).
- Example prompt: "List startup items that mention update."

### large_files
- Does: Largest files under a root path.
- Args: `root` (string, default `%USERPROFILE%`), `limit` (int, default 10).
- Example prompt: "Show the top 15 largest files under Downloads."

### find_duplicates
- Does: Finds duplicate files by SHA256 hash under a root.
- Args: `root` (string, default `%USERPROFILE%`), `limit` (int, default 10).
- Example prompt: "Find duplicate files under Documents."

### disk_usage_by_extension
- Does: Disk usage grouped by file extension under a root.
- Args: `root` (string, default `%USERPROFILE%`), `limit` (int, default 10).
- Example prompt: "Which file types take the most space under Videos?"

### recent_changes
- Does: Recent file changes under a root in the last N days.
- Args: `root` (string, default `%USERPROFILE%`), `days` (int, default 1), `limit` (int, default 20).
- Example prompt: "What files changed in the last 3 days under my project folder?"

### file_metadata
- Does: File metadata and zone info if present.
- Args: `path` (string, required).
- Example prompt: "Show metadata and origin for C:\\path\\to\\file.exe."

## Usage Notes

- All tools are read-only; no system changes are made.
- If a tool requires parameters, infer from context or ask for a missing value.

