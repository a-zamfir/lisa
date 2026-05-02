from __future__ import annotations

import argparse
import json
import sys
from pathlib import Path
from typing import Any, Dict


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


def main() -> int:
    parser = argparse.ArgumentParser(description="Windows OS skill runner")
    parser.add_argument("--tool", required=True, help="Tool id to execute")
    args = parser.parse_args()

    script_dir = Path(__file__).resolve().parent
    sys.path.insert(0, str(script_dir))
    from tools import TOOL_MAP  # type: ignore

    tool = TOOL_MAP.get(args.tool)
    if tool is None:
        print(json.dumps({"success": False, "data": None, "error": f"Unknown tool: {args.tool}", "meta": {"tool": args.tool}}))
        return 1

    try:
        tool_args = _load_args()
        result = tool(**tool_args)
        print(json.dumps({"success": True, "data": result, "error": None, "meta": {"tool": args.tool}}))
        return 0
    except Exception as exc:
        print(json.dumps({"success": False, "data": None, "error": str(exc), "meta": {"tool": args.tool}}))
        return 1


if __name__ == "__main__":
    raise SystemExit(main())
