from __future__ import annotations

import subprocess
from datetime import datetime, timezone
from typing import Any, Dict, Optional

import orjson


def run_powershell(command: str) -> Dict[str, Any]:
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


def run_ps_json(command: str) -> Dict[str, Any]:
    payload = run_powershell(f"{command} | ConvertTo-Json -Depth 6")
    if payload["exit_code"] != 0:
        return {"error": payload["stderr"] or "PowerShell error", "raw": payload}
    try:
        return orjson.loads(payload["stdout"]) if payload["stdout"] else {}
    except orjson.JSONDecodeError as ex:
        return {"error": f"JSON parse error: {ex}", "raw": payload["stdout"]}


def parse_uptime(last_boot: Optional[str]) -> Optional[Dict[str, Any]]:
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
