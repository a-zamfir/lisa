from __future__ import annotations

import os
from typing import Any, Dict, Optional

from ps import escape_ps_string, parse_uptime, run_powershell, run_ps_json, validate_path


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
    if pid is not None and (pid < 0 or pid > 999999):
        return {"error": "Invalid PID", "read_only": True}

    if process_name:
        import re
        if not re.match(r'^[a-zA-Z0-9_\-\.]+$', process_name):
            return {"error": "Invalid process name format. Only alphanumeric, underscore, dash, and dot allowed.", "read_only": True}

    ps = r"""
param($Pid, $Name)
$proc = $null
$windowTitle = $null
if ($Pid) {
  $proc = Get-CimInstance Win32_Process -Filter "ProcessId=$Pid" | Select-Object ProcessId, Name, ExecutablePath, CommandLine, ParentProcessId
} elseif ($Name) {
  $proc = Get-CimInstance Win32_Process | Where-Object { $_.Name -eq $Name } | Select-Object ProcessId, Name, ExecutablePath, CommandLine, ParentProcessId | Select-Object -First 1
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
    cmd_parts = [ps.replace("\r\n", "\n")]
    if pid is not None:
        cmd_parts.append(f" -Pid {pid}")
    if process_name:
        cmd_parts.append(f" -Name {escape_ps_string(process_name)}")

    payload = run_ps_json("".join(cmd_parts))
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
    payload = run_ps_json(ps.replace("\r\n", "\n") + f"\n -Filter {escape_ps_string(filter_text)} -Limit {limit}")
    return {"result": payload, "read_only": True}


def large_files(root: Optional[str] = None, limit: int = 10, max_depth: int = 3) -> Dict[str, Any]:
    limit = max(1, min(limit, 50))
    max_depth = max(1, min(max_depth, 10))
    root = root or os.path.expandvars(r"%USERPROFILE%")

    try:
        root = validate_path(root, allow_network=False)
    except ValueError as ex:
        return {"error": str(ex), "read_only": True}

    ps = r"""
param($Root, $Limit, $Depth)
$exclude = @('node_modules', 'AppData', '.git', '.venv', 'venv', '__pycache__', 'Windows', 'ProgramData', '$Recycle.Bin')
Get-ChildItem -LiteralPath $Root -Recurse -File -Depth $Depth -ErrorAction SilentlyContinue |
  Where-Object {
    $excluded = $false
    foreach ($pattern in $exclude) {
      if ($_.FullName -like "*\$pattern\*") { $excluded = $true; break }
    }
    -not $excluded
  } |
  Sort-Object Length -Descending |
  Select-Object -First $Limit FullName, Length, LastWriteTime
"""
    payload = run_ps_json(ps.replace("\r\n", "\n") + f"\n -Root {escape_ps_string(root)} -Limit {limit} -Depth {max_depth}")
    return {"result": payload, "read_only": True}


def find_duplicates(root: Optional[str] = None, limit: int = 10, max_depth: int = 2) -> Dict[str, Any]:
    limit = max(1, min(limit, 50))
    max_depth = max(1, min(max_depth, 5))
    root = root or os.path.expandvars(r"%USERPROFILE%")

    try:
        root = validate_path(root, allow_network=False)
    except ValueError as ex:
        return {"error": str(ex), "read_only": True}

    ps = r"""
param($Root, $Limit, $Depth)
$exclude = @('node_modules', 'AppData', '.git', '.venv', 'venv', '__pycache__', 'Windows', 'ProgramData', '$Recycle.Bin')
$files = Get-ChildItem -LiteralPath $Root -Recurse -File -Depth $Depth -ErrorAction SilentlyContinue |
  Where-Object {
    $excluded = $false
    foreach ($pattern in $exclude) {
      if ($_.FullName -like "*\$pattern\*") { $excluded = $true; break }
    }
    -not $excluded
  }
$hashes = $files | ForEach-Object {
  try {
    $h = Get-FileHash -Path $_.FullName -Algorithm SHA256
    [pscustomobject]@{ Hash=$h.Hash; Path=$_.FullName; Length=$_.Length }
  } catch { }
}
$dupes = $hashes | Group-Object Hash | Where-Object { $_.Count -gt 1 } | Select-Object -First $Limit
$dupes
"""
    payload = run_ps_json(ps.replace("\r\n", "\n") + f"\n -Root {escape_ps_string(root)} -Limit {limit} -Depth {max_depth}")
    return {"result": payload, "read_only": True}


def disk_usage_by_extension(root: Optional[str] = None, limit: int = 10, max_depth: int = 3) -> Dict[str, Any]:
    limit = max(1, min(limit, 50))
    max_depth = max(1, min(max_depth, 10))
    root = root or os.path.expandvars(r"%USERPROFILE%")

    try:
        root = validate_path(root, allow_network=False)
    except ValueError as ex:
        return {"error": str(ex), "read_only": True}

    ps = r"""
param($Root, $Limit, $Depth)
$exclude = @('node_modules', 'AppData', '.git', '.venv', 'venv', '__pycache__', 'Windows', 'ProgramData', '$Recycle.Bin')
Get-ChildItem -LiteralPath $Root -Recurse -File -Depth $Depth -ErrorAction SilentlyContinue |
  Where-Object {
    $excluded = $false
    foreach ($pattern in $exclude) {
      if ($_.FullName -like "*\$pattern\*") { $excluded = $true; break }
    }
    -not $excluded
  } |
  Group-Object Extension |
  ForEach-Object {
    [pscustomobject]@{ Extension=$_.Name; Count=$_.Count; Size=($_.Group | Measure-Object Length -Sum).Sum }
  } |
  Sort-Object Size -Descending |
  Select-Object -First $Limit
"""
    payload = run_ps_json(ps.replace("\r\n", "\n") + f"\n -Root {escape_ps_string(root)} -Limit {limit} -Depth {max_depth}")
    return {"result": payload, "read_only": True}


def recent_changes(root: Optional[str] = None, days: int = 1, limit: int = 20, max_depth: int = 3) -> Dict[str, Any]:
    limit = max(1, min(limit, 50))
    days = max(1, min(days, 30))
    max_depth = max(1, min(max_depth, 10))
    root = root or os.path.expandvars(r"%USERPROFILE%")

    try:
        root = validate_path(root, allow_network=False)
    except ValueError as ex:
        return {"error": str(ex), "read_only": True}

    ps = r"""
param($Root, $Days, $Limit, $Depth)
$exclude = @('node_modules', 'AppData', '.git', '.venv', 'venv', '__pycache__', 'Windows', 'ProgramData', '$Recycle.Bin')
$cutoff = (Get-Date).AddDays(-1 * [int]$Days)
Get-ChildItem -LiteralPath $Root -Recurse -File -Depth $Depth -ErrorAction SilentlyContinue |
  Where-Object {
    $excluded = $false
    foreach ($pattern in $exclude) {
      if ($_.FullName -like "*\$pattern\*") { $excluded = $true; break }
    }
    (-not $excluded) -and ($_.LastWriteTime -ge $cutoff)
  } |
  Sort-Object LastWriteTime -Descending |
  Select-Object -First $Limit FullName, LastWriteTime, Length
"""
    payload = run_ps_json(ps.replace("\r\n", "\n") + f"\n -Root {escape_ps_string(root)} -Days {days} -Limit {limit} -Depth {max_depth}")
    return {"result": payload, "read_only": True}


def file_metadata(path: str) -> Dict[str, Any]:
    try:
        path = validate_path(path, allow_network=False)
    except ValueError as ex:
        return {"error": str(ex), "read_only": True}

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
    payload = run_ps_json(ps.replace("\r\n", "\n") + f"\n -Path {escape_ps_string(path)}")
    return {"result": payload, "read_only": True}


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
