from __future__ import annotations

import json
import os
from pathlib import Path
from typing import Any, Dict, Optional

import httpx


SETTINGS_PATH = Path(
    os.environ.get(
        "LISA_SETTINGS_PATH",
        Path(os.getenv("LOCALAPPDATA", ".")) / "LISA" / "host-settings.json",
    )
)


class McpConfig:
    def __init__(self, host: str = "127.0.0.1", port: int = 8123) -> None:
        self.host = host
        self.port = port


def load_mcp_settings() -> McpConfig:
    if SETTINGS_PATH.exists():
        try:
            data = json.loads(SETTINGS_PATH.read_text(encoding="utf-8"))
            host = data.get("mcpHost") or data.get("mcp_host") or "127.0.0.1"
            port = int(data.get("mcpPort") or data.get("mcp_port") or 8123)
            return McpConfig(host=host, port=port)
        except Exception:
            pass
    return McpConfig()


class McpClient:
    async def list_tools(self) -> Dict[str, Any]:
        cfg = load_mcp_settings()
        url = f"http://{cfg.host}:{cfg.port}/tools"
        async with httpx.AsyncClient(timeout=5) as client:
            try:
                resp = await client.get(url)
                resp.raise_for_status()
                return resp.json()
            except httpx.HTTPError as ex:
                return {"error": f"MCP unavailable: {ex}"}

    async def call_tool(self, tool: str, args: Optional[Dict[str, Any]] = None) -> Dict[str, Any]:
        cfg = load_mcp_settings()
        url = f"http://{cfg.host}:{cfg.port}/call"
        payload = {"tool": tool, "args": args or {}}
        async with httpx.AsyncClient(timeout=15) as client:
            try:
                resp = await client.post(url, json=payload)
                resp.raise_for_status()
                return resp.json()
            except httpx.HTTPError as ex:
                return {"error": f"MCP unavailable: {ex}"}
