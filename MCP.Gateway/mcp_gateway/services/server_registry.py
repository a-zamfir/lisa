from __future__ import annotations

import asyncio
import httpx
from typing import Any, Dict, List, Optional

from mcp_gateway.config import McpServerConfig


class ServerRegistry:
    """Manages MCP server lifecycle and tool aggregation."""

    def __init__(self, servers: Dict[str, McpServerConfig]):
        self.servers = {name: cfg for name, cfg in servers.items() if cfg.enabled}
        self.tool_cache: Dict[str, Dict[str, Any]] = {}  # tool_name → metadata
        self.health_status: Dict[str, bool] = {}

    async def discover_tools(self) -> Dict[str, List[Dict[str, Any]]]:
        """
        Fetch tool schemas from all enabled servers.
        Returns: {server_name: [tool_schemas]}
        """
        tasks = []
        for server_name, server in self.servers.items():
            tasks.append(self._fetch_server_tools(server_name, server))

        results = await asyncio.gather(*tasks, return_exceptions=True)

        tools_by_server = {}
        for server_name, result in zip(self.servers.keys(), results):
            if isinstance(result, Exception):
                print(f"ERROR: Failed to fetch tools from {server_name}: {result}")
                self.health_status[server_name] = False
            else:
                tools_by_server[server_name] = result
                self.health_status[server_name] = True

        return tools_by_server

    async def _fetch_server_tools(
        self, server_name: str, server: McpServerConfig
    ) -> List[Dict[str, Any]]:
        """Fetch tools from a single MCP server."""
        import os

        token = os.environ.get("MCP_AUTH_TOKEN", "")
        headers = {"X-Mcp-Token": token} if token else {}

        print(f"INFO: Fetching tools from {server_name} at {server.base_url}/tools/docs")
        print(f"DEBUG: Auth token present: {bool(token)}, Token length: {len(token) if token else 0}")

        async with httpx.AsyncClient() as client:
            try:
                response = await client.get(
                    f"{server.base_url}/tools/docs", headers=headers, timeout=5.0
                )
                print(f"INFO: {server_name} responded with status {response.status_code}")
                response.raise_for_status()
                data = response.json()
                tools = data.get("tools", [])

                print(f"INFO: {server_name} returned {len(tools)} tools")

                # Add namespace prefix and server metadata
                for tool in tools:
                    original_name = tool["name"]
                    # Add prefix if configured
                    if server.tool_prefix:
                        tool["name"] = f"{server.tool_prefix}_{original_name}"

                    tool["server"] = server_name
                    tool["server_description"] = server.description

                    # Update tool cache for routing
                    self.tool_cache[tool["name"]] = {
                        "server": server_name,
                        "original_name": original_name,
                    }

                return tools
            except Exception as ex:
                print(f"ERROR: Failed to fetch tools from {server_name} at {server.base_url}: {ex}")
                raise RuntimeError(
                    f"Failed to fetch tools from {server.base_url}: {ex}"
                )

    async def route_tool_call(
        self, tool_name: str, arguments: Dict[str, Any]
    ) -> Dict[str, Any]:
        """Route tool call to the appropriate MCP server."""
        if tool_name not in self.tool_cache:
            raise ValueError(f"Unknown tool: {tool_name}")

        server_name = self.tool_cache[tool_name]["server"]
        original_tool_name = self.tool_cache[tool_name]["original_name"]

        if server_name not in self.servers:
            raise ValueError(f"Server not found: {server_name}")

        server = self.servers[server_name]

        # Make HTTP call to backend server
        import os

        token = os.environ.get("MCP_AUTH_TOKEN", "")
        headers = {"X-Mcp-Token": token, "Content-Type": "application/json"}

        async with httpx.AsyncClient() as client:
            response = await client.post(
                f"{server.base_url}/call",
                json={"tool": original_tool_name, "args": arguments},
                headers=headers,
                timeout=30.0,
            )
            response.raise_for_status()
            return response.json()

    async def check_server_health(self, server_name: str) -> bool:
        """Check health of a single server."""
        if server_name not in self.servers:
            return False

        server = self.servers[server_name]

        try:
            async with httpx.AsyncClient() as client:
                response = await client.get(
                    f"{server.base_url}/health", timeout=3.0
                )
                return response.status_code == 200
        except Exception:
            return False

    async def health_check_all(self) -> Dict[str, bool]:
        """Check health of all servers."""
        tasks = [
            self.check_server_health(server_name) for server_name in self.servers.keys()
        ]
        results = await asyncio.gather(*tasks)

        health_status = {}
        for server_name, is_healthy in zip(self.servers.keys(), results):
            health_status[server_name] = is_healthy
            self.health_status[server_name] = is_healthy

        return health_status
