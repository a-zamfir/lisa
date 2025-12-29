from __future__ import annotations

import logging
import os

from mcp_server.app import app

DEFAULT_PORT = int(os.environ.get("MCP_PORT", "8123"))
VERBOSE = os.environ.get("LISA_VERBOSE_LOGGING", "").strip().lower() in {"1", "true", "yes", "on"}


if __name__ == "__main__":
    import uvicorn

    logging.basicConfig(level=logging.DEBUG if VERBOSE else logging.INFO)
    uvicorn.run(app, host="127.0.0.1", port=DEFAULT_PORT, log_level="info" if VERBOSE else "warning", access_log=VERBOSE)
