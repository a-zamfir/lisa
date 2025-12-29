import os
from fastapi import FastAPI

from mcp_gateway.config import load_gateway_config
from mcp_gateway.routers import gateway
from mcp_gateway.services.server_registry import ServerRegistry

DEFAULT_PORT = int(os.environ.get("MCP_GATEWAY_PORT", os.environ.get("MCP_PORT", "8123")))


def create_app() -> FastAPI:
    """Create and configure the FastAPI application."""
    app = FastAPI(title="LISA MCP Gateway", version="0.1.0")

    # Load configuration
    config = load_gateway_config()

    # Initialize server registry
    registry = ServerRegistry(config.servers)
    gateway.set_registry(registry)

    # Include routers
    app.include_router(gateway.router)

    # Register startup event to pre-fetch tools
    @app.on_event("startup")
    async def startup_prefetch():
        """Pre-fetch tools from all backend servers on startup."""
        import asyncio

        # Wait a bit for backend servers to start (if launching together)
        await asyncio.sleep(2)

        try:
            print("INFO: Gateway pre-fetching tools from backend servers...")
            tools_by_server = await registry.discover_tools()
            total_tools = sum(len(tools) for tools in tools_by_server.values())
            print(f"INFO: Gateway discovered {total_tools} tools from {len(tools_by_server)} servers")
        except Exception as ex:
            print(f"WARNING: Gateway pre-fetch failed: {ex}")
            print("WARNING: Tools will be fetched on first request")

    return app


app = create_app()


if __name__ == "__main__":
    import uvicorn

    uvicorn.run(app, host="127.0.0.1", port=DEFAULT_PORT)
