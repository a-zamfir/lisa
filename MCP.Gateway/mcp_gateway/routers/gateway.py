from __future__ import annotations

import hmac
import os
from typing import Any, Dict

from fastapi import APIRouter, Header, HTTPException, status

from mcp_gateway.services.server_registry import ServerRegistry

router = APIRouter()

# Global registry instance (will be initialized in app.py)
_registry: ServerRegistry | None = None


def set_registry(registry: ServerRegistry) -> None:
    """Set the global registry instance."""
    global _registry
    _registry = registry


def get_registry() -> ServerRegistry:
    """Get the global registry instance."""
    if _registry is None:
        raise RuntimeError("Registry not initialized")
    return _registry


def _require_token(token: str | None) -> None:
    """Validate MCP auth token using constant-time comparison."""
    expected = os.environ.get("MCP_AUTH_TOKEN", "")
    if not expected or not hmac.compare_digest(token or "", expected):
        raise HTTPException(
            status_code=status.HTTP_401_UNAUTHORIZED, detail="Unauthorized"
        )


@router.get("/health")
def health() -> Dict[str, Any]:
    """Health check endpoint - no auth required for monitoring."""
    from datetime import datetime

    return {
        "status": "healthy",
        "timestamp": datetime.utcnow().isoformat(),
        "service": "mcp-gateway",
    }


@router.get("/ready")
async def ready(x_mcp_token: str | None = Header(default=None)) -> Dict[str, Any]:
    """Readiness check - verifies backend servers are available."""
    _require_token(x_mcp_token)
    registry = get_registry()

    health_status = await registry.health_check_all()
    all_healthy = all(health_status.values()) if health_status else False

    return {
        "status": "ready" if all_healthy else "degraded",
        "servers": health_status,
    }


@router.get("/servers")
async def list_servers(
    x_mcp_token: str | None = Header(default=None),
) -> Dict[str, Any]:
    """List all registered MCP servers and their health status."""
    _require_token(x_mcp_token)
    registry = get_registry()

    return {
        "servers": [
            {
                "name": name,
                "enabled": True,  # Only enabled servers are in registry
                "base_url": server.base_url,
                "description": server.description,
                "tool_prefix": server.tool_prefix,
                "healthy": registry.health_status.get(name, False),
                "tool_count": len(
                    [t for t in registry.tool_cache.values() if t["server"] == name]
                ),
            }
            for name, server in registry.servers.items()
        ]
    }


@router.get("/tools")
async def list_tools(
    x_mcp_token: str | None = Header(default=None),
) -> Dict[str, Any]:
    """List all available tools from all servers (minimal format)."""
    _require_token(x_mcp_token)
    registry = get_registry()

    try:
        tools_by_server = await registry.discover_tools()

        # Flatten all tools
        all_tools = []
        for server_name, server_tools in tools_by_server.items():
            for tool in server_tools:
                all_tools.append({"name": tool["name"], "description": tool["description"]})

        # Log if no tools found
        if not all_tools:
            print(f"WARNING: Gateway /tools endpoint discovered 0 tools from {len(tools_by_server)} servers")
            print(f"WARNING: Servers: {list(registry.servers.keys())}")
            print(f"WARNING: Health status: {registry.health_status}")
        else:
            print(f"INFO: Gateway /tools returning {len(all_tools)} tools")

        return {"tools": all_tools, "read_only": True}
    except Exception as ex:
        print(f"ERROR: Gateway /tools failed: {ex}")
        import traceback
        traceback.print_exc()
        return {"tools": [], "read_only": True, "error": str(ex)}


@router.get("/tools/docs")
async def list_tool_docs(
    x_mcp_token: str | None = Header(default=None),
) -> Dict[str, Any]:
    """List all available tools with full documentation."""
    _require_token(x_mcp_token)
    registry = get_registry()

    tools_by_server = await registry.discover_tools()

    # Flatten all tools with full metadata
    all_tools = []
    for server_tools in tools_by_server.values():
        all_tools.extend(server_tools)

    return {"tools": all_tools, "read_only": True}


@router.post("/call")
async def call_tool(
    payload: Dict[str, Any], x_mcp_token: str | None = Header(default=None)
) -> Dict[str, Any]:
    """Proxy tool call to appropriate MCP server."""
    _require_token(x_mcp_token)
    registry = get_registry()

    tool_name = payload.get("tool")
    args = payload.get("args") or {}

    if not tool_name:
        raise HTTPException(
            status_code=status.HTTP_400_BAD_REQUEST, detail="Missing 'tool' parameter"
        )

    try:
        result = await registry.route_tool_call(tool_name, args)
        return result
    except ValueError as ex:
        raise HTTPException(status_code=status.HTTP_404_NOT_FOUND, detail=str(ex))
    except Exception as ex:
        raise HTTPException(
            status_code=status.HTTP_500_INTERNAL_SERVER_ERROR, detail=str(ex)
        )


@router.post("/servers/{server_name}/reload")
async def reload_server(
    server_name: str, x_mcp_token: str | None = Header(default=None)
) -> Dict[str, Any]:
    """Reload tools from a specific server (hot reload)."""
    _require_token(x_mcp_token)
    registry = get_registry()

    if server_name not in registry.servers:
        raise HTTPException(
            status_code=status.HTTP_404_NOT_FOUND, detail="Server not found"
        )

    server = registry.servers[server_name]

    try:
        tools = await registry._fetch_server_tools(server_name, server)
        return {
            "server": server_name,
            "tools_loaded": len(tools),
            "status": "success",
        }
    except Exception as ex:
        raise HTTPException(
            status_code=status.HTTP_500_INTERNAL_SERVER_ERROR, detail=str(ex)
        )
