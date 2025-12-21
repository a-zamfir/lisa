from __future__ import annotations

import os

from agent_mcp.app import app

DEFAULT_PORT = int(os.environ.get("MCP_PORT", "8123"))


if __name__ == "__main__":
    import uvicorn

    uvicorn.run(app, host="127.0.0.1", port=DEFAULT_PORT, log_level="warning", access_log=False)
