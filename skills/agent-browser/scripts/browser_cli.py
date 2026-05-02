from __future__ import annotations

import json
import os
import subprocess
from pathlib import Path
from typing import Any, Dict, List, Optional


SKILL_ROOT = Path(__file__).resolve().parents[1]
PACKAGE_ROOT = SKILL_ROOT
AGENT_BROWSER_JS = PACKAGE_ROOT / "node_modules" / "agent-browser" / "bin" / "agent-browser.js"


def _base_env(session: Optional[str] = None) -> Dict[str, str]:
    env = dict(os.environ)
    env["PYTHONDONTWRITEBYTECODE"] = "1"
    if session:
        env["AGENT_BROWSER_SESSION"] = session
    return env


def ensure_installed() -> None:
    if not AGENT_BROWSER_JS.exists():
        raise RuntimeError(
            "agent-browser is not installed for this skill. "
            "Run `npm install` in skills/agent-browser and then `node node_modules/agent-browser/bin/agent-browser.js install`."
        )


def run_agent_browser(args: List[str], *, session: Optional[str] = None, timeout_s: int = 30) -> Dict[str, Any]:
    ensure_installed()
    cmd = ["node", str(AGENT_BROWSER_JS), *args]
    result = subprocess.run(
        cmd,
        capture_output=True,
        text=True,
        cwd=str(PACKAGE_ROOT),
        env=_base_env(session=session),
        timeout=timeout_s,
    )
    stdout = (result.stdout or "").strip()
    stderr = (result.stderr or "").strip()

    parsed: Any = None
    if stdout:
        try:
            parsed = json.loads(stdout)
        except json.JSONDecodeError:
            parsed = stdout

    if result.returncode != 0:
        raise RuntimeError(stderr or (stdout if stdout else "agent-browser failed"))

    return {
        "command": cmd,
        "stdout": parsed,
        "stderr": stderr or None,
    }
