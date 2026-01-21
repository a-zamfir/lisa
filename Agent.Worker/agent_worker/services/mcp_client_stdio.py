"""MCP Client using stdio transport for communicating with MCP servers."""
from __future__ import annotations

import asyncio
import logging
import os
import sys
from pathlib import Path
from typing import Any, Dict, List, Optional

import orjson
from mcp import ClientSession, StdioServerParameters, stdio_client

logger = logging.getLogger(__name__)

SETTINGS_PATH = Path(
    os.environ.get(
        "LISA_SETTINGS_PATH",
        Path(os.getenv("LOCALAPPDATA", ".")) / "LISA" / "host-settings.json",
    )
)


class McpServerConfig:
    """Configuration for a single MCP server."""

    def __init__(
        self,
        name: str,
        command: str,
        args: List[str],
        env: Optional[Dict[str, str]] = None,
        enabled: bool = True,
    ):
        self.name = name
        self.command = command
        self.args = args
        self.env = env or {}
        self.enabled = enabled


class StdioMcpClient:
    """
    MCP Client that manages multiple stdio-based MCP servers.

    This client:
    1. Launches MCP servers as subprocesses
    2. Communicates via stdio using JSON-RPC
    3. Aggregates tools from all servers
    4. Routes tool calls to the appropriate server
    """

    def __init__(self):
        self._sessions: Dict[str, ClientSession] = {}
        self._servers: Dict[str, McpServerConfig] = {}
        self._tool_to_server: Dict[str, str] = {}  # tool_name → server_name
        self._cached_tools: Optional[List[Dict[str, Any]]] = None
        self._initialized = False
        self._stdio_contexts: Dict[str, Any] = {}  # Keep reference to stdio context managers

    async def initialize(self) -> None:
        """Load server configuration and start all enabled MCP servers."""
        if self._initialized:
            return

        # Load server configurations from settings
        self._servers = self._load_server_configs()

        # Start all enabled servers
        for server_name, config in self._servers.items():
            if config.enabled:
                try:
                    await self._start_server(server_name, config)
                    logger.info(f"Started MCP server: {server_name}")
                except Exception as ex:
                    logger.error(f"Failed to start MCP server {server_name}: {ex}")

        # Discover tools from all servers
        await self._discover_tools()

        self._initialized = True
        logger.info(f"MCP Client initialized with {len(self._sessions)} servers")

    def _load_server_configs(self) -> Dict[str, McpServerConfig]:
        """Load MCP server configurations from host-settings.json."""
        servers = {}

        if not SETTINGS_PATH.exists():
            logger.warning(f"Settings file not found: {SETTINGS_PATH}")
            return servers

        try:
            data = orjson.loads(SETTINGS_PATH.read_text(encoding="utf-8"))
            mcp_servers = data.get("mcpServers", {})

            for server_name, server_config in mcp_servers.items():
                # Extract server configuration
                command = server_config.get("command")
                args = server_config.get("args", [])
                env = server_config.get("env", {})
                enabled = server_config.get("enabled", True)

                if not command:
                    logger.warning(f"Server {server_name} has no command, skipping")
                    continue

                servers[server_name] = McpServerConfig(
                    name=server_name,
                    command=command,
                    args=args,
                    env=env,
                    enabled=enabled,
                )

        except Exception as ex:
            logger.error(f"Failed to load server configs: {ex}")

        return servers

    async def _start_server(
        self, server_name: str, config: McpServerConfig
    ) -> None:
        """Start a single MCP server via stdio."""
        # Build environment variables
        env = os.environ.copy()
        env.update(config.env)

        # Add MCP auth token if available
        if "MCP_AUTH_TOKEN" in os.environ:
            env["MCP_AUTH_TOKEN"] = os.environ["MCP_AUTH_TOKEN"]

        # Create server parameters
        server_params = StdioServerParameters(
            command=config.command,
            args=config.args,
            env=env,
        )

        # Connect to the server via stdio
        stdio = stdio_client(server_params)
        read_stream, write_stream = await stdio.__aenter__()

        # Create client session and enter its context to start receive loop
        session = ClientSession(read_stream, write_stream)
        await session.__aenter__()  # Start the receive loop
        await session.initialize()

        # Store the session and stdio context
        self._sessions[server_name] = session
        self._stdio_contexts[server_name] = stdio

    async def _discover_tools(self) -> None:
        """Discover all tools from all connected MCP servers."""
        all_tools = []
        self._tool_to_server.clear()

        for server_name, session in self._sessions.items():
            try:
                # List tools from this server
                response = await session.list_tools()

                for tool in response.tools:
                    # Store tool metadata (minimal format to reduce token usage)
                    # Only name and description - inputSchema is large and inflates context
                    tool_dict = {
                        "name": tool.name,
                        "description": tool.description or "",
                    }
                    all_tools.append(tool_dict)

                    # Map tool name to server
                    self._tool_to_server[tool.name] = server_name

                logger.info(
                    f"Discovered {len(response.tools)} tools from {server_name}"
                )

            except Exception as ex:
                logger.error(f"Failed to discover tools from {server_name}: {ex}")

        self._cached_tools = all_tools
        logger.info(f"Total tools discovered: {len(all_tools)}")

    async def list_tools(self, force_refresh: bool = False) -> Dict[str, Any]:
        """Get list of all available tools from all servers."""
        if not self._initialized:
            await self.initialize()

        if force_refresh or self._cached_tools is None:
            await self._discover_tools()

        return {
            "tools": self._cached_tools or [],
            "read_only": True,
        }

    def get_cached_tools(
        self, active_servers: Optional[List[str]] = None
    ) -> Optional[Dict[str, Any]]:
        """Get cached tools without refreshing.

        Args:
            active_servers: Optional list of server names to filter by.
                           If None, returns all tools.
                           If empty list, returns no tools.
        """
        if self._cached_tools is None:
            return None

        # If no filter specified (None), return all tools
        if active_servers is None:
            return {
                "tools": self._cached_tools,
                "read_only": True,
            }

        # Filter tools by active servers (empty list = no tools)
        filtered_tools = []
        for tool in self._cached_tools:
            tool_name = tool.get("name")
            if tool_name:
                server_name = self._tool_to_server.get(tool_name)
                if server_name and server_name in active_servers:
                    filtered_tools.append(tool)

        return {
            "tools": filtered_tools,
            "read_only": True,
        }

    async def list_tool_docs(self, force_refresh: bool = False) -> Dict[str, Any]:
        """Get detailed documentation for all tools."""
        # For stdio MCP, tools already include full schema
        tools_response = await self.list_tools(force_refresh=force_refresh)
        return tools_response

    # Tools that only read data - don't require approval
    READ_ONLY_TOOLS = {
        # Outlook read tools
        "outlook_get_calendar_events",
        "outlook_get_contacts",
        "outlook_get_emails",
        "outlook_search_emails",
        # Windows read tools
        "system_overview",
        "top_processes",
        "network_usage",
        "power_state",
        "process_inspector",
        "startup_entries",
        "large_files",
        "find_duplicates",
        "disk_usage_by_extension",
        "recent_changes",
        "file_metadata",
    }

    # Friendly descriptions for tools
    TOOL_FRIENDLY_DESCS = {
        "outlook_get_calendar_events": "checking your calendar",
        "outlook_create_calendar_event": "creating a calendar event",
        "outlook_get_contacts": "looking up your contacts",
        "outlook_get_emails": "checking your emails",
        "outlook_send_email": "sending an email",
        "outlook_search_emails": "searching your emails",
        "system_overview": "checking system specs",
        "top_processes": "checking running processes",
        "network_usage": "checking network status",
        "power_state": "checking battery status",
        "process_inspector": "inspecting a process",
        "startup_entries": "checking startup programs",
        "large_files": "scanning for large files",
        "find_duplicates": "scanning for duplicates",
        "disk_usage_by_extension": "analyzing disk usage",
        "recent_changes": "checking recent file changes",
        "file_metadata": "checking file info",
    }

    def get_tool_meta(self, name: str) -> Optional[Dict[str, Any]]:
        """Get metadata for a specific tool."""
        if self._cached_tools is None:
            return None

        for tool in self._cached_tools:
            if tool["name"] == name:
                # Read-only tools don't require approval
                approval_required = name not in self.READ_ONLY_TOOLS
                friendly_desc = self.TOOL_FRIENDLY_DESCS.get(name, f"running {name}")
                return {
                    **tool,
                    "approval_required": approval_required,
                    "friendly_desc": friendly_desc,
                }

        return None

    async def call_tool(
        self, tool: str, args: Optional[Dict[str, Any]] = None
    ) -> Dict[str, Any]:
        """Call a tool by routing to the appropriate MCP server."""
        if not self._initialized:
            await self.initialize()

        # Find which server has this tool
        server_name = self._tool_to_server.get(tool)
        if not server_name:
            return {
                "error": f"Tool not found: {tool}",
                "isError": True,
            }

        # Get the session for this server
        session = self._sessions.get(server_name)
        if not session:
            return {
                "error": f"Server not available: {server_name}",
                "isError": True,
            }

        # Call the tool
        try:
            response = await session.call_tool(tool, arguments=args or {})

            # Extract result from MCP response
            if response.isError:
                return {
                    "error": str(response.content),
                    "isError": True,
                }

            # Success - extract content
            content_parts = []
            for content_item in response.content:
                if hasattr(content_item, "text"):
                    content_parts.append(content_item.text)
                elif hasattr(content_item, "data"):
                    content_parts.append(str(content_item.data))

            result_text = "\n".join(content_parts) if content_parts else ""

            return {
                "content": [{"type": "text", "text": result_text}],
                "isError": False,
            }

        except Exception as ex:
            logger.error(f"Tool call failed for {tool}: {ex}")
            return {
                "error": f"Tool call failed: {ex}",
                "isError": True,
            }

    async def warm_client(self) -> None:
        """Warm up the client (no-op for stdio, included for compatibility)."""
        pass

    async def close(self) -> None:
        """Close all MCP server connections."""
        for server_name in list(self._sessions.keys()):
            try:
                # Exit session context (stops receive loop)
                session = self._sessions.get(server_name)
                if session:
                    await session.__aexit__(None, None, None)

                # Close stdio context
                stdio = self._stdio_contexts.get(server_name)
                if stdio:
                    await stdio.__aexit__(None, None, None)

                logger.info(f"Closed MCP server: {server_name}")
            except Exception as ex:
                logger.error(f"Error closing {server_name}: {ex}")

        self._sessions.clear()
        self._stdio_contexts.clear()
        self._cached_tools = None
        self._tool_to_server.clear()
        self._initialized = False


# Global client instance
_client_instance: Optional[StdioMcpClient] = None


def get_mcp_client() -> StdioMcpClient:
    """Get or create the global MCP client instance."""
    global _client_instance
    if _client_instance is None:
        _client_instance = StdioMcpClient()
    return _client_instance
