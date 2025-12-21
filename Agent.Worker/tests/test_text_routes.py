import asyncio
from typing import Any, Dict, List

import pytest
from fastapi import FastAPI
from fastapi.testclient import TestClient

from agent_worker.routers import text as text_router


@pytest.fixture()
def app():
    app = FastAPI()
    app.include_router(text_router.router)
    return app


@pytest.fixture()
def client(app):
    return TestClient(app)


def test_health(client, monkeypatch):
    monkeypatch.setattr(
        text_router,
        "load_settings",
        lambda: type("Cfg", (), {"host": "127.0.0.1", "port": 11434, "model": "test-model"})(),
    )
    resp = client.get("/health")
    assert resp.status_code == 200
    data = resp.json()
    assert data["status"] == "ok"
    assert data["model"] == "test-model"


def test_text_basic_no_tools(client, monkeypatch):
    async def fake_stream(messages, tools=None, on_thinking_chunk=None, on_content_chunk=None):
        return {"content": "Hello", "tool_calls": [], "thinking": ""}

    monkeypatch.setattr(text_router, "call_provider_stream", fake_stream)
    monkeypatch.setattr(text_router, "call_provider", fake_stream)
    monkeypatch.setattr(text_router._mcp_client, "list_tools", lambda *args, **kwargs: {"tools": []})

    payload = {
        "session_id": "s1",
        "turn_id": "t1",
        "text": "hi",
        "input_meta": {"active_app": None, "window_title": None, "clipboard": None},
    }
    resp = client.post("/input/text", json=payload)
    assert resp.status_code == 200
    data = resp.json()
    assert data["messages"][0]["content"] == "Hello"


def test_text_tool_call_flow(client, monkeypatch):
    async def fake_stream(messages, tools=None, on_thinking_chunk=None, on_content_chunk=None):
        return {
            "content": "",
            "tool_calls": [{"function": {"name": "system_overview", "arguments": "{}"}}],
            "thinking": "",
        }

    async def fake_follow(messages, tools=None, attempts=3):
        return {"content": "Done", "tool_calls": [], "thinking": ""}

    monkeypatch.setattr(text_router, "call_provider_stream", fake_stream)
    monkeypatch.setattr(text_router, "call_provider", fake_follow)
    monkeypatch.setattr(text_router._mcp_client, "list_tools", lambda *args, **kwargs: {"tools": [{"name": "system_overview", "description": "x"}]})
    monkeypatch.setattr(text_router._mcp_client, "call_tool", lambda *args, **kwargs: {"result": {"ok": True}})

    payload = {
        "session_id": "s2",
        "turn_id": "t2",
        "text": "run a system overview",
        "input_meta": {"active_app": None, "window_title": None, "clipboard": None},
    }
    resp = client.post("/input/text", json=payload)
    assert resp.status_code == 200
    data = resp.json()
    assert data["messages"][0]["content"] == "Done"
    assert data["tool_calls"] == ["system_overview"]


def test_retry_flow(client, monkeypatch):
    async def fake_stream(messages, tools=None, on_thinking_chunk=None, on_content_chunk=None):
        return {"content": "Retry ok", "tool_calls": [], "thinking": ""}

    monkeypatch.setattr(text_router, "call_provider_stream", fake_stream)
    monkeypatch.setattr(text_router, "call_provider", fake_stream)
    monkeypatch.setattr(text_router._mcp_client, "list_tools", lambda *args, **kwargs: {"tools": []})

    # Prime history
    text_router._conversations.append("s3", "user", "hi")
    text_router._conversations.append("s3", "assistant", "hello")

    payload = {"session_id": "s3", "turn_id": "t3"}
    resp = client.post("/input/retry", json=payload)
    assert resp.status_code == 200
    data = resp.json()
    assert data["messages"][0]["content"] == "Retry ok"
