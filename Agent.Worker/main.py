"""
FastAPI worker for LISA: text/chat endpoint with LangGraph placeholder orchestration.
Launch with: uvicorn main:app --host 127.0.0.1 --port 5050
"""
from __future__ import annotations

from fastapi import FastAPI

from agent_worker.routers.text import init_tool_cache, start_tool_cache_refresh, router as text_router
from agent_worker.services.settings import DEFAULT_PORT


app = FastAPI(title="LISA Agent Worker", version="0.1.0")
app.include_router(text_router)


@app.on_event("startup")
async def startup() -> None:
    await init_tool_cache()
    start_tool_cache_refresh()


if __name__ == "__main__":
    import uvicorn

    uvicorn.run("main:app", host="127.0.0.1", port=DEFAULT_PORT, reload=False)
