from __future__ import annotations

import orjson
import os
from pathlib import Path
from typing import Any, Dict


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
        enabled: bool,
        port: int,
        host: str = "127.0.0.1",
        description: str = "",
        tool_prefix: str = "",
    ):
        self.name = name
        self.enabled = enabled
        self.port = port
        self.host = host
        self.description = description
        self.tool_prefix = tool_prefix or name

    @property
    def base_url(self) -> str:
        return f"http://{self.host}:{self.port}"


class GatewayConfig:
    """Gateway configuration loaded from host-settings.json."""

    def __init__(self, servers: Dict[str, McpServerConfig], gateway_port: int = 8123):
        self.servers = servers
        self.gateway_port = gateway_port


def load_gateway_config() -> GatewayConfig:
    """Load gateway configuration from settings file."""
    if SETTINGS_PATH.exists():
        try:
            data = orjson.loads(SETTINGS_PATH.read_text(encoding="utf-8"))

            # Load MCP servers configuration
            servers_config = data.get("mcpServers", {})
            servers = {}

            for name, server_data in servers_config.items():
                if not isinstance(server_data, dict):
                    continue

                servers[name] = McpServerConfig(
                    name=name,
                    enabled=server_data.get("enabled", False),
                    port=server_data.get("port", 8124),
                    host=server_data.get("host", "127.0.0.1"),
                    description=server_data.get("description", ""),
                    tool_prefix=server_data.get("toolPrefix", ""),
                )

            gateway_port = int(data.get("mcpGatewayPort", data.get("mcpPort", 8123)))

            return GatewayConfig(servers=servers, gateway_port=gateway_port)

        except Exception as ex:
            print(f"WARNING: Failed to load gateway config: {ex}")

    # Return default configuration
    return GatewayConfig(
        servers={
            "windows_automation": McpServerConfig(
                name="windows_automation",
                enabled=True,
                port=8124,
                description="Windows system automation and diagnostics",
                tool_prefix="win",
            )
        },
        gateway_port=8123,
    )
