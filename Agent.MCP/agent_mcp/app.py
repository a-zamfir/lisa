import os
from fastapi import FastAPI

from agent_mcp.routers import tools

DEFAULT_PORT = int(os.environ.get("MCP_PORT", "8123"))


def create_app() -> FastAPI:
    app = FastAPI()
    app.include_router(tools.router)
    return app


app = create_app()


if __name__ == "__main__":
    import uvicorn

    uvicorn.run(app, host="127.0.0.1", port=DEFAULT_PORT)
