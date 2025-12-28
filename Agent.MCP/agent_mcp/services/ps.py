from __future__ import annotations

import os
import subprocess
from datetime import datetime, timezone
from typing import Any, Dict, Optional

import orjson


def escape_ps_string(value: str) -> str:
    """
    Escape a string for safe use as a PowerShell parameter value.
    Wraps in single quotes and escapes embedded single quotes.
    Single quotes prevent variable expansion and command injection.

    Args:
        value: The string to escape

    Returns:
        Escaped string safe for PowerShell parameter passing

    Example:
        >>> escape_ps_string("test'file")
        "'test''file'"
    """
    if value is None:
        return "''"
    # Replace single quotes with doubled single quotes (PowerShell escape)
    escaped = value.replace("'", "''")
    # Wrap in single quotes (prevents variable expansion and most injection)
    return f"'{escaped}'"


def validate_path(path: str, allow_network: bool = False) -> str:
    """
    Validate and sanitize a path parameter for PowerShell usage.

    Args:
        path: The path to validate
        allow_network: Whether to allow network paths (UNC paths)

    Returns:
        Normalized, validated path

    Raises:
        ValueError: If path contains suspicious characters or patterns

    Security:
        - Blocks command injection characters
        - Blocks network paths by default
        - Normalizes path to prevent traversal tricks
    """
    if not path:
        raise ValueError("Path cannot be empty")

    # Expand environment variables for validation
    expanded = os.path.expandvars(path)

    # Block network paths unless explicitly allowed
    if not allow_network and (expanded.startswith("\\\\") or expanded.startswith("//")):
        raise ValueError("Network paths are not allowed")

    # Block suspicious PowerShell command injection characters
    suspicious = [";", "&", "|", "`", "$", "(", ")", "{", "}", "\n", "\r"]
    for char in suspicious:
        if char in path:
            raise ValueError(f"Invalid character in path: {char}")

    # Normalize path to prevent directory traversal tricks
    try:
        normalized = os.path.normpath(expanded)
        return normalized
    except Exception as ex:
        raise ValueError(f"Invalid path format: {ex}")


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
