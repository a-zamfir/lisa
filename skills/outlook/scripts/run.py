from __future__ import annotations

import argparse
import asyncio
import json
import sys
from pathlib import Path
from typing import Any, Callable, Dict


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


def _tool_map() -> Dict[str, Callable[..., Any]]:
    script_dir = Path(__file__).resolve().parent
    sys.path.insert(0, str(script_dir))
    import logic

    return {
        "outlook_get_calendar_events": logic.outlook_get_calendar_events,
        "outlook_create_calendar_event": logic.outlook_create_calendar_event,
        "outlook_get_contacts": logic.outlook_get_contacts,
        "outlook_get_emails": logic.outlook_get_emails,
        "outlook_send_email": logic.outlook_send_email,
        "outlook_search_emails": logic.outlook_search_emails,
    }


def main() -> int:
    parser = argparse.ArgumentParser(description="Outlook skill runner")
    parser.add_argument("--tool", required=True, help="Tool id to execute")
    args = parser.parse_args()

    tool = _tool_map().get(args.tool)
    if tool is None:
        print(json.dumps({"success": False, "data": None, "error": f"Unknown tool: {args.tool}", "meta": {"tool": args.tool}}))
        return 1

    try:
        tool_args = _load_args()
        if asyncio.iscoroutinefunction(tool):
            result = asyncio.run(tool(**tool_args))
        else:
            result = tool(**tool_args)
        print(json.dumps({"success": True, "data": result, "error": None, "meta": {"tool": args.tool}}))
        return 0
    except Exception as exc:
        print(json.dumps({"success": False, "data": None, "error": str(exc), "meta": {"tool": args.tool}}))
        return 1


if __name__ == "__main__":
    raise SystemExit(main())
