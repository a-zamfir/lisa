"""
FastAPI worker for LISA: text/chat endpoint with LangGraph placeholder orchestration.
Launch with: uvicorn main:app --host 127.0.0.1 --port 5050
"""
from __future__ import annotations

from fastapi import FastAPI

from agent_worker.routers.text import init_tool_cache, start_tool_cache_retry, warm_services, router as text_router
from agent_worker.routers.audio import router as audio_router
from agent_worker.services.settings import DEFAULT_PORT
from agent_worker.services.stt import load_model
from starlette.concurrency import run_in_threadpool


app = FastAPI(title="LISA Agent Worker", version="0.1.0")
app.include_router(text_router)
app.include_router(audio_router)


@app.on_event("startup")
async def startup() -> None:
    ok = await init_tool_cache()
    if not ok:
        start_tool_cache_retry()
    await warm_services()
    try:
        await run_in_threadpool(load_model)
    except Exception as exc:
        print(f"[stt] Model load failed: {exc}")


if __name__ == "__main__":
    import uvicorn

    uvicorn.run("main:app", host="127.0.0.1", port=DEFAULT_PORT, reload=False)
