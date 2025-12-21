from __future__ import annotations

import os
from typing import Any, Dict, List, Optional

from agent_mcp.services.ps import parse_uptime, run_powershell, run_ps_json


def get_tool_docs() -> List[Dict[str, Any]]:
    return [
        {
            "name": tool["name"],
            "description": tool["description"],
            "category": tool.get("category"),
            "args": tool.get("args", {}),
        }
        for tool in TOOL_REGISTRY
    ]


def system_overview() -> Dict[str, Any]:
    cpu = run_ps_json(
        "Get-CimInstance Win32_Processor | Measure-Object -Property LoadPercentage -Average | "
        "Select-Object -ExpandProperty Average"
    )
    os_info = run_ps_json(
        "Get-CimInstance Win32_OperatingSystem | Select-Object TotalVisibleMemorySize, FreePhysicalMemory, LastBootUpTime"
    )
    disks = run_ps_json(
        "Get-CimInstance Win32_LogicalDisk -Filter \"DriveType=3\" | "
        "Select-Object DeviceID, Size, FreeSpace"
    )
    battery = run_ps_json(
        "Get-CimInstance Win32_Battery | Select-Object BatteryStatus, EstimatedChargeRemaining"
    )
    power_scheme = run_powershell("powercfg /GetActiveScheme")

    uptime = parse_uptime(os_info.get("LastBootUpTime") if isinstance(os_info, dict) else None)

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


def top_processes(limit: int = 8) -> Dict[str, Any]:
    limit = max(1, min(limit, 20))
    processes = run_ps_json(
        f"Get-Process | Sort-Object CPU -Descending | Select-Object -First {limit} "
        "Name, Id, CPU, WorkingSet"
    )
    return {"processes": processes, "read_only": True}


def network_usage() -> Dict[str, Any]:
    adapters = run_ps_json("Get-NetAdapter | Select-Object Name, Status, LinkSpeed")
    stats = run_ps_json("Get-NetAdapterStatistics | Select-Object Name, ReceivedBytes, SentBytes")
    vpn = run_ps_json("Get-VpnConnection -AllUserConnection | Select-Object Name, ConnectionStatus, ServerAddress")
    return {"adapters": adapters, "stats": stats, "vpn": vpn, "read_only": True}


def power_state() -> Dict[str, Any]:
    battery = run_ps_json(
        "Get-CimInstance Win32_Battery | Select-Object BatteryStatus, EstimatedChargeRemaining"
    )
    power_scheme = run_powershell("powercfg /GetActiveScheme")
    sleep_states = run_powershell("powercfg /a")
    last_wake = run_powershell("powercfg /lastwake")
    return {
        "battery": battery,
        "power_mode": power_scheme.get("stdout") if isinstance(power_scheme, dict) else power_scheme,
        "sleep_states": sleep_states.get("stdout") if isinstance(sleep_states, dict) else sleep_states,
        "last_wake": last_wake.get("stdout") if isinstance(last_wake, dict) else last_wake,
        "read_only": True,
    }


def process_inspector(process_name: Optional[str] = None, pid: Optional[int] = None) -> Dict[str, Any]:
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
    payload = run_ps_json(ps.replace("\r\n", "\n"))
    return {"result": payload, "read_only": True}


def startup_entries(filter_text: Optional[str] = None, limit: int = 20) -> Dict[str, Any]:
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
    payload = run_ps_json(ps.replace("\r\n", "\n") + f"\n -Filter \"{filter_text}\" -Limit {limit}")
    return {"result": payload, "read_only": True}


def large_files(root: Optional[str] = None, limit: int = 10) -> Dict[str, Any]:
    limit = max(1, min(limit, 50))
    root = root or os.path.expandvars(r"%USERPROFILE%")
    ps = r"""
param($Root, $Limit)
Get-ChildItem -Path $Root -Recurse -File -ErrorAction SilentlyContinue |
  Sort-Object Length -Descending |
  Select-Object -First $Limit FullName, Length, LastWriteTime
"""
    payload = run_ps_json(ps.replace("\r\n", "\n") + f"\n -Root \"{root}\" -Limit {limit}")
    return {"result": payload, "read_only": True}


def find_duplicates(root: Optional[str] = None, limit: int = 10) -> Dict[str, Any]:
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
    payload = run_ps_json(ps.replace("\r\n", "\n") + f"\n -Root \"{root}\" -Limit {limit}")
    return {"result": payload, "read_only": True}


def disk_usage_by_extension(root: Optional[str] = None, limit: int = 10) -> Dict[str, Any]:
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
    payload = run_ps_json(ps.replace("\r\n", "\n") + f"\n -Root \"{root}\" -Limit {limit}")
    return {"result": payload, "read_only": True}


def recent_changes(root: Optional[str] = None, days: int = 1, limit: int = 20) -> Dict[str, Any]:
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
    payload = run_ps_json(ps.replace("\r\n", "\n") + f"\n -Root \"{root}\" -Days {days} -Limit {limit}")
    return {"result": payload, "read_only": True}


def file_metadata(path: str) -> Dict[str, Any]:
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
    payload = run_ps_json(ps.replace("\r\n", "\n") + f"\n -Path \"{path}\"")
    return {"result": payload, "read_only": True}


TOOL_REGISTRY: List[Dict[str, Any]] = [
    {"name": "system_overview", "description": "CPU/RAM/Disk usage, uptime, battery, and power mode.", "args": {}, "category": "system_state"},
    {"name": "top_processes", "description": "Top processes by CPU usage.", "args": {"limit": "int (1-20)"}, "category": "system_state"},
    {"name": "network_usage", "description": "Network adapter stats and VPN status.", "args": {}, "category": "system_state"},
    {"name": "power_state", "description": "Battery status, active power scheme, and sleep capability.", "args": {}, "category": "system_state"},
    {"name": "process_inspector", "description": "Active window process details, args, parent/child, signature, startup origins.", "args": {"process_name": "string", "pid": "int"}, "category": "process_app"},
    {"name": "startup_entries", "description": "Startup origins from registry and task scheduler.", "args": {"filter_text": "string", "limit": "int"}, "category": "process_app"},
    {"name": "large_files", "description": "Locate large files under a root path.", "args": {"root": "string", "limit": "int"}, "category": "file_disk"},
    {"name": "find_duplicates", "description": "Find duplicate files by hash.", "args": {"root": "string", "limit": "int"}, "category": "file_disk"},
    {"name": "disk_usage_by_extension", "description": "Explain disk usage by file extension.", "args": {"root": "string", "limit": "int"}, "category": "file_disk"},
    {"name": "recent_changes", "description": "Recent file changes under a root path.", "args": {"root": "string", "days": "int", "limit": "int"}, "category": "file_disk"},
    {"name": "file_metadata", "description": "File metadata and zone info.", "args": {"path": "string"}, "category": "file_disk"},
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
