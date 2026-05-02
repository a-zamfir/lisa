from __future__ import annotations

import asyncio
import shutil
import sys
import uuid
from pathlib import Path

import pytest

from agent_worker.routers import text as text_router


ROOT = Path(__file__).resolve().parents[1]
if str(ROOT) not in sys.path:
    sys.path.insert(0, str(ROOT))


@pytest.fixture()
def workspace_temp_dir() -> Path:
    base = ROOT / ".artifacts" / "test-temp"
    base.mkdir(parents=True, exist_ok=True)
    path = base / uuid.uuid4().hex
    path.mkdir(parents=True, exist_ok=True)
    try:
        yield path
    finally:
        shutil.rmtree(path, ignore_errors=True)


@pytest.fixture(autouse=True)
def reset_text_router_callback_state():
    existing_task = text_router._callback_sender_task
    if existing_task is not None and not existing_task.done():
        existing_task.cancel()
    text_router._close_callback_socket()
    text_router._callback_queue = asyncio.Queue(maxsize=8192)
    text_router._callback_sender_task = None
    text_router._callback_socket = None
    text_router._callback_port = None
    text_router._send_thinking_callback = lambda *args, **kwargs: None

    async def _noop_async(*args, **kwargs):
        return None

    text_router._send_tool_callback = _noop_async
    text_router._send_memory_callback = _noop_async
    text_router._send_tool_approval_callback = _noop_async
    text_router._send_tool_auto_approved_callback = _noop_async
    yield
    existing_task = text_router._callback_sender_task
    if existing_task is not None and not existing_task.done():
        existing_task.cancel()
    text_router._close_callback_socket()
