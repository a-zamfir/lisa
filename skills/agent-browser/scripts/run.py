from __future__ import annotations

import argparse
import json
import sys
from typing import Any, Dict

from browser_cli import run_agent_browser


def _load_args() -> Dict[str, Any]:
    raw = sys.stdin.read().strip()
    if not raw:
        return {}
    try:
        payload = json.loads(raw)
    except json.JSONDecodeError as exc:
        raise ValueError("Invalid JSON input") from exc
    if isinstance(payload, dict) and "args" in payload and isinstance(payload["args"], dict):
        return payload["args"]
    if isinstance(payload, dict):
        return payload
    raise ValueError("Input JSON must be an object")


def _tool_session(tool_args: Dict[str, Any]) -> str | None:
    session = tool_args.get("session")
    if session is None:
        return None
    if not isinstance(session, str) or not session.strip():
        raise ValueError("session must be a non-empty string")
    return session.strip()


def _agent_browser_open(tool_args: Dict[str, Any]) -> Dict[str, Any]:
    url = tool_args.get("url")
    if not isinstance(url, str) or not url.strip():
        raise ValueError("url is required")
    return run_agent_browser(["open", url.strip()], session=_tool_session(tool_args))


def _agent_browser_snapshot(tool_args: Dict[str, Any]) -> Dict[str, Any]:
    cmd = ["snapshot", "-i", "--json"]
    selector = tool_args.get("selector")
    if isinstance(selector, str) and selector.strip():
        cmd.extend(["-s", selector.strip()])
    if tool_args.get("compact") is True:
        cmd.append("-c")
    depth = tool_args.get("depth")
    if isinstance(depth, int) and depth > 0:
        cmd.extend(["-d", str(depth)])
    return run_agent_browser(cmd, session=_tool_session(tool_args))


def _agent_browser_click(tool_args: Dict[str, Any]) -> Dict[str, Any]:
    ref = tool_args.get("ref")
    if not isinstance(ref, str) or not ref.strip():
        raise ValueError("ref is required")
    return run_agent_browser(["click", ref.strip()], session=_tool_session(tool_args))


def _agent_browser_fill(tool_args: Dict[str, Any]) -> Dict[str, Any]:
    ref = tool_args.get("ref")
    text = tool_args.get("text")
    if not isinstance(ref, str) or not ref.strip():
        raise ValueError("ref is required")
    if not isinstance(text, str):
        raise ValueError("text is required")
    return run_agent_browser(["fill", ref.strip(), text], session=_tool_session(tool_args))


def _agent_browser_press(tool_args: Dict[str, Any]) -> Dict[str, Any]:
    key = tool_args.get("key")
    if not isinstance(key, str) or not key.strip():
        raise ValueError("key is required")
    return run_agent_browser(["press", key.strip()], session=_tool_session(tool_args))


def _agent_browser_wait(tool_args: Dict[str, Any]) -> Dict[str, Any]:
    ref = tool_args.get("ref")
    milliseconds = tool_args.get("milliseconds")
    text = tool_args.get("text")
    url = tool_args.get("url")
    load = tool_args.get("load")

    modes = [ref is not None, milliseconds is not None, text is not None, url is not None, load is not None]
    if sum(1 for mode in modes if mode) != 1:
        raise ValueError("Provide exactly one of ref, milliseconds, text, url, or load")

    if isinstance(ref, str) and ref.strip():
        cmd = ["wait", ref.strip()]
    elif isinstance(milliseconds, int) and milliseconds >= 0:
        cmd = ["wait", str(milliseconds)]
    elif isinstance(text, str) and text.strip():
        cmd = ["wait", "--text", text.strip()]
    elif isinstance(url, str) and url.strip():
        cmd = ["wait", "--url", url.strip()]
    elif isinstance(load, str) and load.strip():
        cmd = ["wait", "--load", load.strip()]
    else:
        raise ValueError("Invalid wait arguments")

    return run_agent_browser(cmd, session=_tool_session(tool_args), timeout_s=35)


def _agent_browser_get_text(tool_args: Dict[str, Any]) -> Dict[str, Any]:
    ref = tool_args.get("ref")
    if not isinstance(ref, str) or not ref.strip():
        raise ValueError("ref is required")
    return run_agent_browser(["get", "text", ref.strip(), "--json"], session=_tool_session(tool_args))


def _agent_browser_get_attr(tool_args: Dict[str, Any]) -> Dict[str, Any]:
    ref = tool_args.get("ref")
    attribute = tool_args.get("attribute")
    if not isinstance(ref, str) or not ref.strip():
        raise ValueError("ref is required")
    if not isinstance(attribute, str) or not attribute.strip():
        raise ValueError("attribute is required")
    return run_agent_browser(
        ["get", "attr", ref.strip(), attribute.strip(), "--json"],
        session=_tool_session(tool_args),
    )


def _agent_browser_close(tool_args: Dict[str, Any]) -> Dict[str, Any]:
    return run_agent_browser(["close"], session=_tool_session(tool_args))


TOOL_MAP = {
    "agent_browser_open": _agent_browser_open,
    "agent_browser_snapshot": _agent_browser_snapshot,
    "agent_browser_click": _agent_browser_click,
    "agent_browser_fill": _agent_browser_fill,
    "agent_browser_press": _agent_browser_press,
    "agent_browser_wait": _agent_browser_wait,
    "agent_browser_get_text": _agent_browser_get_text,
    "agent_browser_get_attr": _agent_browser_get_attr,
    "agent_browser_close": _agent_browser_close,
}


def main() -> int:
    parser = argparse.ArgumentParser(description="agent-browser skill runner")
    parser.add_argument("--tool", required=True, help="Tool id to execute")
    args = parser.parse_args()

    tool = TOOL_MAP.get(args.tool)
    if tool is None:
        print(json.dumps({"success": False, "data": None, "error": f"Unknown tool: {args.tool}", "meta": {"tool": args.tool}}))
        return 1

    try:
        tool_args = _load_args()
        result = tool(tool_args)
        print(json.dumps({"success": True, "data": result, "error": None, "meta": {"tool": args.tool}}))
        return 0
    except Exception as exc:
        print(json.dumps({"success": False, "data": None, "error": str(exc), "meta": {"tool": args.tool}}))
        return 1


if __name__ == "__main__":
    raise SystemExit(main())
