import os
import uvicorn

from mcp_gateway.app import app

if __name__ == "__main__":
    port = int(os.environ.get("MCP_GATEWAY_PORT", os.environ.get("MCP_PORT", "8123")))
    verbose = os.environ.get("LISA_VERBOSE_LOGGING", "0") == "1"

    log_level = "debug" if verbose else "info"

    uvicorn.run(
        app,
        host="127.0.0.1",
        port=port,
        log_level=log_level,
    )
