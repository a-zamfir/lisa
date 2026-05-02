from __future__ import annotations

import getpass
import locale
import os
import platform
import socket
from datetime import datetime
from pathlib import Path
from typing import Optional


def _get_display_name() -> Optional[str]:
    # Prefer Windows display name if available; fallback to username.
    try:
        import ctypes

        NameDisplay = 3  # EXTENDED_NAME_FORMAT.NameDisplay
        size = ctypes.pointer(ctypes.c_ulong(0))
        ctypes.windll.secur32.GetUserNameExW(NameDisplay, None, size)
        name_buffer = ctypes.create_unicode_buffer(size.contents.value + 1)
        if ctypes.windll.secur32.GetUserNameExW(NameDisplay, name_buffer, size):
            value = name_buffer.value
            return value if value else None
    except Exception:
        pass
    return None


def _load_prompt_text() -> str:
    prompt_path = Path(__file__).resolve().parent.parent / "prompts" / "lisa_assistant.md"
    try:
        return prompt_path.read_text(encoding="utf-8").strip()
    except Exception:
        return ""


_BASE_PROMPT = _load_prompt_text()


def collect_system_context() -> str:
    now = datetime.now().astimezone()
    username = os.environ.get("USERNAME") or getpass.getuser() or "unknown"
    domain = os.environ.get("USERDOMAIN") or ""
    display = _get_display_name() or username
    account = f"{domain}\\{username}" if domain else username
    hostname = socket.gethostname()
    os_version = platform.platform()
    locale_code = locale.getlocale()[0]
    encoding = locale.getpreferredencoding(False)
    tz = now.tzname() or "UTC"
    tz_offset = now.strftime("%z")
    region_env = (
        os.environ.get("LANG")
        or os.environ.get("LC_ALL")
        or os.environ.get("LC_CTYPE")
        or os.environ.get("USER_LOCALE")
        or ""
    )
    home = Path.home()

    lines = [
        _BASE_PROMPT if _BASE_PROMPT else "",
        "System context:",
        f"- Current time: {now.isoformat()} ({tz}{f' {tz_offset}' if tz_offset else ''})",
        f"- User's name: {display} (account: {account})",
        f"- Machine: {hostname}",
        f"- OS: {os_version}",
        f"- Locale: {locale_code or 'unknown'}; Encoding: {encoding}",
        f"- Region: {region_env or 'unknown'}",
        f"- Home: {home}",
    ]

    return "\n".join(lines)
