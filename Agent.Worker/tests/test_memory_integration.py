import time
from pathlib import Path

import pytest
from fastapi import FastAPI
from fastapi.testclient import TestClient

from agent_worker.routers import text as text_router
from agent_worker.services.conversations import ConversationStore
from agent_worker.services.memory_store import MemoryStore


@pytest.fixture()
def temp_dir(workspace_temp_dir: Path):
    yield workspace_temp_dir


@pytest.fixture()
def app(temp_dir: Path):
    app = FastAPI()
    app.include_router(text_router.router)
    text_router._conversations = ConversationStore()
    text_router._tool_context = None
    text_router._tool_context_version = 0
    text_router._memory_store = MemoryStore(str(temp_dir / "memory.db"))
    return app


@pytest.fixture()
def client(app):
    return TestClient(app)


def test_memory_trailer_is_stripped_and_saved(client, monkeypatch, temp_dir: Path):
    scheduled = []

    async def fake_stream(messages, tools=None, on_thinking_chunk=None, on_content_chunk=None):
        content = (
            "Hello there.\n"
            "<lisa_memory>{\"ops\":[{\"op\":\"upsert\",\"kind\":\"user_info\",\"key\":\"user.name\",\"value\":\"Andrei\",\"confidence\":0.9}]}</lisa_memory>"
        )
        return {"content": content, "tool_calls": [], "thinking": ""}

    async def fake_call(messages, tools=None):
        return {"content": "fallback", "tool_calls": [], "thinking": ""}

    monkeypatch.setattr(text_router, "call_provider_stream", fake_stream)
    monkeypatch.setattr(text_router, "call_provider", fake_call)
    monkeypatch.setattr(text_router._tool_client, "get_cached_tools", lambda active_categories=None: {"tools": []})

    cfg = type(
        "Cfg",
        (),
        {
            "provider_type": "LM Studio",
            "host": "127.0.0.1",
            "port": 1234,
            "model": "test",
            "memory_enabled": True,
            "memory_path": str(temp_dir / "memory.db"),
        },
    )()
    monkeypatch.setattr(text_router, "load_settings", lambda: cfg)

    # Silence callback side effects.
    async def noop(*args, **kwargs):
        return None

    monkeypatch.setattr(text_router, "_send_memory_callback", noop)

    def capture_task(coro):
        scheduled.append(coro)

        class _DummyTask:
            def cancel(self):
                return False

            def done(self):
                return True

        return _DummyTask()

    monkeypatch.setattr(text_router.asyncio, "create_task", capture_task)

    payload = {"session_id": "s-mem", "turn_id": "t-mem", "text": "hi", "input_meta": {}}
    resp = client.post("/input/text", json=payload)
    assert resp.status_code == 200
    data = resp.json()
    assert "<lisa_memory>" not in data["messages"][0]["content"]
    assert data["messages"][0]["content"].strip() == "Hello there."
    assert scheduled, "memory update should have been scheduled"

    for coro in scheduled:
        time.sleep(0)
        import asyncio

        asyncio.run(coro)

    store = MemoryStore(str(temp_dir / "memory.db"))
    items = store.search("Andrei", limit=10)
    assert any(item.key == "user.name" and item.value == "Andrei" for item in items)


def test_stream_filter_hides_trailer_from_callbacks(client, monkeypatch):
    seen_chunks = []

    def capture_content(session_id, turn_id, phase, delta=None):
        if phase == "content_chunk" and delta:
            seen_chunks.append(delta)

    monkeypatch.setattr(text_router, "_send_content_callback", capture_content)
    monkeypatch.setattr(text_router._tool_client, "get_cached_tools", lambda active_categories=None: {"tools": []})

    async def fake_stream(messages, tools=None, on_thinking_chunk=None, on_content_chunk=None):
        if on_content_chunk:
            on_content_chunk("Part 1 ")
            on_content_chunk("<lisa_memory>{\"ops\":[]}</lisa_memory>")
            on_content_chunk("SHOULD_NOT_APPEAR")
        return {"content": "Part 1 <lisa_memory>{\"ops\":[]}</lisa_memory>", "tool_calls": [], "thinking": ""}

    monkeypatch.setattr(text_router, "call_provider_stream", fake_stream)
    monkeypatch.setattr(text_router, "call_provider", fake_stream)

    cfg = type(
        "Cfg",
        (),
        {"provider_type": "LM Studio", "host": "127.0.0.1", "port": 1234, "model": "test", "memory_enabled": False, "memory_path": None},
    )()
    monkeypatch.setattr(text_router, "load_settings", lambda: cfg)

    payload = {"session_id": "s-x", "turn_id": "t-x", "text": "hi", "input_meta": {}}
    resp = client.post("/input/text", json=payload)
    assert resp.status_code == 200
    combined = "".join(seen_chunks)
    assert "<lisa_memory>" not in combined
    assert "SHOULD_NOT_APPEAR" not in combined
