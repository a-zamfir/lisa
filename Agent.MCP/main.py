"""
System State MCP server for Windows.
Runs locally with MCP stdio server on 127.0.0.1:8123 via HTTP bridge.
"""
from __future__ import annotations

import os
import subprocess
from datetime import datetime, timezone
from typing import Any, Dict, Optional

import orjson
from fastapi import FastAPI
from mcp.server.fastmcp import FastMCP


DEFAULT_PORT = int(os.environ.get("MCP_PORT", "8123"))


def _run_powershell(command: str) -> Dict[str, Any]:
    result = subprocess.run(
        ["powershell", "-NoProfile", "-ExecutionPolicy", "Bypass", "-Command", command],
        capture_output=True,
        text=True,
        timeout=15,
    )
    return {
        "stdout": result.stdout.strip(),
        "stderr": result.stderr.strip(),
        "exit_code": result.returncode,
    }


def _run_ps_json(command: str) -> Dict[str, Any]:
    payload = _run_powershell(f"{command} | ConvertTo-Json -Depth 6")
    if payload["exit_code"] != 0:
        return {"error": payload["stderr"] or "PowerShell error", "raw": payload}
    try:
        return orjson.loads(payload["stdout"]) if payload["stdout"] else {}
    except orjson.JSONDecodeError as ex:
        return {"error": f"JSON parse error: {ex}", "raw": payload["stdout"]}


def _parse_uptime(last_boot: Optional[str]) -> Optional[Dict[str, Any]]:
    if not last_boot:
        return None
    try:
        dt = datetime.strptime(last_boot, "%Y%m%d%H%M%S.%f%z")
        now = datetime.now(timezone.utc)
        uptime = now - dt.astimezone(timezone.utc)
        return {
            "last_boot_utc": dt.astimezone(timezone.utc).isoformat(),
            "uptime_seconds": int(uptime.total_seconds()),
        }
    except Exception:
        return None


mcp = FastMCP("system-state-mcp")


@mcp.tool()
def system_overview() -> Dict[str, Any]:
    """CPU/RAM/Disk usage, uptime, battery, and power mode."""
    cpu = _run_ps_json(
        "Get-CimInstance Win32_Processor | Measure-Object -Property LoadPercentage -Average | "
        "Select-Object -ExpandProperty Average"
    )
    os_info = _run_ps_json(
        "Get-CimInstance Win32_OperatingSystem | Select-Object TotalVisibleMemorySize, FreePhysicalMemory, LastBootUpTime"
    )
    disks = _run_ps_json(
        "Get-CimInstance Win32_LogicalDisk -Filter \"DriveType=3\" | "
        "Select-Object DeviceID, Size, FreeSpace"
    )
    battery = _run_ps_json(
        "Get-CimInstance Win32_Battery | Select-Object BatteryStatus, EstimatedChargeRemaining"
    )
    power_scheme = _run_powershell("powercfg /GetActiveScheme")

    uptime = _parse_uptime(os_info.get("LastBootUpTime") if isinstance(os_info, dict) else None)

    memory = None
    if isinstance(os_info, dict):
        try:
            total_kb = int(os_info.get("TotalVisibleMemorySize") or 0)
            free_kb = int(os_info.get("FreePhysicalMemory") or 0)
            memory = {
                "total_mb": round(total_kb / 1024, 2),
                "free_mb": round(free_kb / 1024, 2),
                "used_mb": round((total_kb - free_kb) / 1024, 2),
            }
        except Exception:
            memory = None

    return {
        "cpu_load_percent": cpu if isinstance(cpu, (int, float)) else cpu,
        "memory": memory,
        "disks": disks,
        "battery": battery,
        "power_mode": power_scheme.get("stdout") if isinstance(power_scheme, dict) else power_scheme,
        "uptime": uptime,
        "read_only": True,
    }


@mcp.tool()
def top_processes(limit: int = 8) -> Dict[str, Any]:
    """Top processes by CPU usage."""
    limit = max(1, min(limit, 20))
    processes = _run_ps_json(
        f"Get-Process | Sort-Object CPU -Descending | Select-Object -First {limit} "
        "Name, Id, CPU, WorkingSet"
    )
    return {"processes": processes, "read_only": True}


@mcp.tool()
def network_usage() -> Dict[str, Any]:
    """Network adapter stats and VPN status."""
    adapters = _run_ps_json(
        "Get-NetAdapter | Select-Object Name, Status, LinkSpeed"
    )
    stats = _run_ps_json(
        "Get-NetAdapterStatistics | Select-Object Name, ReceivedBytes, SentBytes"
    )
    vpn = _run_ps_json(
        "Get-VpnConnection -AllUserConnection | Select-Object Name, ConnectionStatus, ServerAddress"
    )
    return {"adapters": adapters, "stats": stats, "vpn": vpn, "read_only": True}


@mcp.tool()
def power_state() -> Dict[str, Any]:
    """Battery status, active power scheme, and sleep capability."""
    battery = _run_ps_json(
        "Get-CimInstance Win32_Battery | Select-Object BatteryStatus, EstimatedChargeRemaining"
    )
    power_scheme = _run_powershell("powercfg /GetActiveScheme")
    sleep_states = _run_powershell("powercfg /a")
    last_wake = _run_powershell("powercfg /lastwake")
    return {
        "battery": battery,
        "power_mode": power_scheme.get("stdout") if isinstance(power_scheme, dict) else power_scheme,
        "sleep_states": sleep_states.get("stdout") if isinstance(sleep_states, dict) else sleep_states,
        "last_wake": last_wake.get("stdout") if isinstance(last_wake, dict) else last_wake,
        "read_only": True,
    }


@mcp.tool()
def process_inspector(process_name: Optional[str] = None, pid: Optional[int] = None) -> Dict[str, Any]:
    """Active window -> process -> binary path, args, parent/child, signature, startup origin."""
    ps = r"""
param($Pid, $Name)
$proc = $null
$windowTitle = $null
if ($Pid) {
  $proc = Get-CimInstance Win32_Process -Filter "ProcessId=$Pid" | Select-Object ProcessId, Name, ExecutablePath, CommandLine, ParentProcessId
} elseif ($Name) {
  $proc = Get-CimInstance Win32_Process -Filter "Name='$Name'" | Select-Object ProcessId, Name, ExecutablePath, CommandLine, ParentProcessId | Select-Object -First 1
} else {
  Add-Type @"
using System;
using System.Runtime.InteropServices;
public class User32 {
  [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
  [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);
}
"@ | Out-Null
  $hWnd = [User32]::GetForegroundWindow()
  if ($hWnd -ne [IntPtr]::Zero) {
    [User32]::GetWindowThreadProcessId($hWnd, [ref]$Pid) | Out-Null
    $proc = Get-CimInstance Win32_Process -Filter "ProcessId=$Pid" | Select-Object ProcessId, Name, ExecutablePath, CommandLine, ParentProcessId
    $windowTitle = (Get-Process -Id $Pid -ErrorAction SilentlyContinue).MainWindowTitle
  }
}

$children = @()
$parent = $null
$signature = $null
if ($proc) {
  if ($proc.ParentProcessId) {
    $parent = Get-CimInstance Win32_Process -Filter "ProcessId=$($proc.ParentProcessId)" | Select-Object ProcessId, Name, ExecutablePath, CommandLine
  }
  $children = Get-CimInstance Win32_Process -Filter "ParentProcessId=$($proc.ProcessId)" | Select-Object ProcessId, Name, ExecutablePath, CommandLine
  if ($proc.ExecutablePath) {
    $signature = Get-AuthenticodeSignature -FilePath $proc.ExecutablePath | Select-Object Status, StatusMessage, SignerCertificate
  }
}

$startup = @()
foreach ($path in @('HKLM:\Software\Microsoft\Windows\CurrentVersion\Run','HKCU:\Software\Microsoft\Windows\CurrentVersion\Run')) {
  if (Test-Path $path) {
    $props = Get-ItemProperty $path
    $props.PSObject.Properties | Where-Object { $_.Name -notlike 'PS*' } | ForEach-Object {
      $startup += [pscustomobject]@{ Location=$path; Name=$_.Name; Command=$_.Value }
    }
  }
}

[pscustomobject]@{
  process = $proc
  window_title = $windowTitle
  parent = $parent
  children = $children
  signature = $signature
  startup = $startup
}
"""
    payload = _run_ps_json(ps.replace("\r\n", "\n"))
    return {"result": payload, "read_only": True}


@mcp.tool()
def startup_entries(filter_text: Optional[str] = None, limit: int = 20) -> Dict[str, Any]:
    """Startup origins from registry and scheduled tasks."""
    limit = max(1, min(limit, 50))
    filter_text = filter_text or ""
    ps = r"""
param($Filter, $Limit)
$results = @()
foreach ($path in @('HKLM:\Software\Microsoft\Windows\CurrentVersion\Run','HKCU:\Software\Microsoft\Windows\CurrentVersion\Run')) {
  if (Test-Path $path) {
    $props = Get-ItemProperty $path
    $props.PSObject.Properties | Where-Object { $_.Name -notlike 'PS*' } | ForEach-Object {
      $results += [pscustomobject]@{ Source='Registry'; Location=$path; Name=$_.Name; Command=$_.Value }
    }
  }
}

$tasks = Get-ScheduledTask | ForEach-Object {
  $action = $_.Actions | Select-Object -First 1
  [pscustomobject]@{
    Source='TaskScheduler';
    Location=$_.TaskPath;
    Name=$_.TaskName;
    Command=$action.Execute
  }
}

$results += $tasks
if ($Filter) {
  $results = $results | Where-Object { $_.Name -like "*$Filter*" -or $_.Command -like "*$Filter*" }
}
$results | Select-Object -First $Limit
"""
    payload = _run_ps_json(ps.replace("\r\n", "\n") + f"\n -Filter \"{filter_text}\" -Limit {limit}")
    return {"result": payload, "read_only": True}


@mcp.tool()
def large_files(root: Optional[str] = None, limit: int = 10) -> Dict[str, Any]:
    """Locate large files under a root path."""
    limit = max(1, min(limit, 50))
    root = root or os.path.expandvars(r"%USERPROFILE%")
    ps = r"""
param($Root, $Limit)
Get-ChildItem -Path $Root -Recurse -File -ErrorAction SilentlyContinue |
  Sort-Object Length -Descending |
  Select-Object -First $Limit FullName, Length, LastWriteTime
"""
    payload = _run_ps_json(ps.replace("\r\n", "\n") + f"\n -Root \"{root}\" -Limit {limit}")
    return {"result": payload, "read_only": True}


@mcp.tool()
def find_duplicates(root: Optional[str] = None, limit: int = 10) -> Dict[str, Any]:
    """Find duplicate files by hash."""
    limit = max(1, min(limit, 50))
    root = root or os.path.expandvars(r"%USERPROFILE%")
    ps = r"""
param($Root, $Limit)
$files = Get-ChildItem -Path $Root -Recurse -File -ErrorAction SilentlyContinue
$hashes = $files | ForEach-Object {
  try {
    $h = Get-FileHash -Path $_.FullName -Algorithm SHA256
    [pscustomobject]@{ Hash=$h.Hash; Path=$_.FullName; Length=$_.Length }
  } catch { }
}
$dupes = $hashes | Group-Object Hash | Where-Object { $_.Count -gt 1 } | Select-Object -First $Limit
$dupes
"""
    payload = _run_ps_json(ps.replace("\r\n", "\n") + f"\n -Root \"{root}\" -Limit {limit}")
    return {"result": payload, "read_only": True}


@mcp.tool()
def disk_usage_by_extension(root: Optional[str] = None, limit: int = 10) -> Dict[str, Any]:
    """Explain disk usage by file extension."""
    limit = max(1, min(limit, 50))
    root = root or os.path.expandvars(r"%USERPROFILE%")
    ps = r"""
param($Root, $Limit)
Get-ChildItem -Path $Root -Recurse -File -ErrorAction SilentlyContinue |
  Group-Object Extension |
  ForEach-Object {
    [pscustomobject]@{ Extension=$_.Name; Count=$_.Count; Size=($_.Group | Measure-Object Length -Sum).Sum }
  } |
  Sort-Object Size -Descending |
  Select-Object -First $Limit
"""
    payload = _run_ps_json(ps.replace("\r\n", "\n") + f"\n -Root \"{root}\" -Limit {limit}")
    return {"result": payload, "read_only": True}


@mcp.tool()
def recent_changes(root: Optional[str] = None, days: int = 1, limit: int = 20) -> Dict[str, Any]:
    """Recent file changes under a root path."""
    limit = max(1, min(limit, 50))
    days = max(1, min(days, 30))
    root = root or os.path.expandvars(r"%USERPROFILE%")
    ps = r"""
param($Root, $Days, $Limit)
$cutoff = (Get-Date).AddDays(-1 * [int]$Days)
Get-ChildItem -Path $Root -Recurse -File -ErrorAction SilentlyContinue |
  Where-Object { $_.LastWriteTime -ge $cutoff } |
  Sort-Object LastWriteTime -Descending |
  Select-Object -First $Limit FullName, LastWriteTime, Length
"""
    payload = _run_ps_json(ps.replace("\r\n", "\n") + f"\n -Root \"{root}\" -Days {days} -Limit {limit}")
    return {"result": payload, "read_only": True}


@mcp.tool()
def file_metadata(path: str) -> Dict[str, Any]:
    """File metadata including origin/zone info if present."""
    ps = r"""
param($Path)
$item = Get-Item -LiteralPath $Path -ErrorAction SilentlyContinue | Select-Object FullName, Length, CreationTime, LastWriteTime
$zone = $null
try { $zone = Get-Item -LiteralPath $Path -Stream Zone.Identifier -ErrorAction SilentlyContinue } catch { }
[pscustomobject]@{
  item = $item
  zone = $zone
}
"""
    payload = _run_ps_json(ps.replace("\r\n", "\n") + f"\n -Path \"{path}\"")
    return {"result": payload, "read_only": True}


TOOL_REGISTRY = [
    {
        "name": "system_overview",
        "description": "CPU/RAM/Disk usage, uptime, battery, and power mode.",
        "args": {},
        "category": "system_state",
    },
    {
        "name": "top_processes",
        "description": "Top processes by CPU usage.",
        "args": {"limit": "int (1-20)"},
        "category": "system_state",
    },
    {
        "name": "network_usage",
        "description": "Network adapter stats and VPN status.",
        "args": {},
        "category": "system_state",
    },
    {
        "name": "power_state",
        "description": "Battery status, active power scheme, and sleep capability.",
        "args": {},
        "category": "system_state",
    },
    {
        "name": "process_inspector",
        "description": "Active window process details, args, parent/child, signature, startup origins.",
        "args": {"process_name": "string", "pid": "int"},
        "category": "process_app",
    },
    {
        "name": "startup_entries",
        "description": "Startup origins from registry and task scheduler.",
        "args": {"filter_text": "string", "limit": "int"},
        "category": "process_app",
    },
    {
        "name": "large_files",
        "description": "Locate large files under a root path.",
        "args": {"root": "string", "limit": "int"},
        "category": "file_disk",
    },
    {
        "name": "find_duplicates",
        "description": "Find duplicate files by hash.",
        "args": {"root": "string", "limit": "int"},
        "category": "file_disk",
    },
    {
        "name": "disk_usage_by_extension",
        "description": "Explain disk usage by file extension.",
        "args": {"root": "string", "limit": "int"},
        "category": "file_disk",
    },
    {
        "name": "recent_changes",
        "description": "Recent file changes under a root path.",
        "args": {"root": "string", "days": "int", "limit": "int"},
        "category": "file_disk",
    },
    {
        "name": "file_metadata",
        "description": "File metadata and zone info.",
        "args": {"path": "string"},
        "category": "file_disk",
    },
]

TOOL_MAP = {
    "system_overview": system_overview,
    "top_processes": top_processes,
    "network_usage": network_usage,
    "power_state": power_state,
    "process_inspector": process_inspector,
    "startup_entries": startup_entries,
    "large_files": large_files,
    "find_duplicates": find_duplicates,
    "disk_usage_by_extension": disk_usage_by_extension,
    "recent_changes": recent_changes,
    "file_metadata": file_metadata,
}


app = FastAPI()


@app.get("/tools")
def list_tools() -> Dict[str, Any]:
    minimal = [{"name": tool["name"], "description": tool["description"]} for tool in TOOL_REGISTRY]
    return {"tools": minimal, "read_only": True}


@app.post("/call")
def call_tool(payload: Dict[str, Any]) -> Dict[str, Any]:
    tool_name = payload.get("tool")
    args = payload.get("args") or {}
    if tool_name not in TOOL_MAP:
        return {"error": f"Unknown tool: {tool_name}"}
    try:
        return {"tool": tool_name, "result": TOOL_MAP[tool_name](**args)}
    except Exception as ex:
        return {"error": str(ex)}


if __name__ == "__main__":
    import uvicorn

    uvicorn.run(app, host="127.0.0.1", port=DEFAULT_PORT)
