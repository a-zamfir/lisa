from __future__ import annotations

import orjson
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
            data = orjson.loads(SETTINGS_PATH.read_text(encoding="utf-8"))
            host = data.get("mcpHost") or data.get("mcp_host") or "127.0.0.1"
            port = int(data.get("mcpPort") or data.get("mcp_port") or 8123)
            return McpConfig(host=host, port=port)
        except Exception:
            pass
    return McpConfig()


class McpClient:
    def __init__(self) -> None:
        self._cached_tools: Optional[Dict[str, Any]] = None
        self._last_status_ok: Optional[bool] = None
        self._client: Optional[httpx.AsyncClient] = None

    def _get_client(self) -> httpx.AsyncClient:
        if self._client is None:
            self._client = httpx.AsyncClient()
        return self._client

    async def warm_client(self) -> None:
        self._get_client()

    async def list_tools(self, force_refresh: bool = False) -> Dict[str, Any]:
        if not force_refresh and self._cached_tools is not None:
            return self._cached_tools
        cfg = load_mcp_settings()
        url = f"http://{cfg.host}:{cfg.port}/tools"
        client = self._get_client()
        try:
            resp = await client.get(url, timeout=5)
            resp.raise_for_status()
            payload = orjson.loads(resp.content)
            self._cached_tools = payload
            self._last_status_ok = True
            return payload
        except httpx.HTTPError as ex:
            self._last_status_ok = False
            return {"error": f"MCP unavailable: {ex}"}

    def get_cached_tools(self) -> Optional[Dict[str, Any]]:
        return self._cached_tools

    async def call_tool(self, tool: str, args: Optional[Dict[str, Any]] = None) -> Dict[str, Any]:
        cfg = load_mcp_settings()
        url = f"http://{cfg.host}:{cfg.port}/call"
        payload = {"tool": tool, "args": args or {}}
        client = self._get_client()
        try:
            resp = await client.post(
                url,
                content=orjson.dumps(payload),
                headers={"Content-Type": "application/json"},
                timeout=15,
            )
            resp.raise_for_status()
            if self._last_status_ok is False:
                await self.list_tools(force_refresh=True)
            self._last_status_ok = True
            return orjson.loads(resp.content)
        except httpx.HTTPError as ex:
            self._last_status_ok = False
            return {"error": f"MCP unavailable: {ex}"}
