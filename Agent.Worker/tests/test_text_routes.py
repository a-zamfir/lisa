import asyncio
from typing import Any, Dict, List

import pytest
from fastapi import FastAPI
from fastapi.testclient import TestClient

from agent_worker.routers import audio as audio_router
from agent_worker.routers import text as text_router
from agent_worker.services.conversations import ConversationStore


@pytest.fixture()
def app():
    app = FastAPI()
    app.include_router(text_router.router)
    app.include_router(audio_router.router)
    text_router._conversations = ConversationStore()
    text_router._tool_context = None
    text_router._tool_context_version = 0
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
    monkeypatch.setattr(text_router._mcp_client, "get_cached_tools", lambda: {"tools": []})

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
    calls = {"count": 0}

    async def fake_stream(messages, tools=None, on_thinking_chunk=None, on_content_chunk=None):
        calls["count"] += 1
        if calls["count"] == 1:
            return {
                "content": "",
                "tool_calls": [{"function": {"name": "system_overview", "arguments": "{}"}}],
                "thinking": "",
            }
        return {"content": "Done", "tool_calls": [], "thinking": ""}

    monkeypatch.setattr(text_router, "call_provider_stream", fake_stream)
    monkeypatch.setattr(text_router, "call_provider", fake_stream)
    monkeypatch.setattr(
        text_router._mcp_client,
        "get_cached_tools",
        lambda: {"tools": [{"name": "system_overview", "description": "x"}]},
    )

    async def fake_call_tool(*args, **kwargs):
        return {"result": {"ok": True}}

    monkeypatch.setattr(text_router._mcp_client, "call_tool", fake_call_tool)

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
    monkeypatch.setattr(text_router._mcp_client, "get_cached_tools", lambda: {"tools": []})

    # Prime history
    text_router._conversations.append("s3", "user", "hi")
    text_router._conversations.append("s3", "assistant", "hello")

    payload = {"session_id": "s3", "turn_id": "t3"}
    resp = client.post("/input/retry", json=payload)
    assert resp.status_code == 200
    data = resp.json()
    assert data["messages"][0]["content"] == "Retry ok"


def test_audio_input_basic(client, monkeypatch):
    def fake_transcribe_audio(_audio: bytes):
        return ("hello", 1, 2, "cpu", "int8")

    monkeypatch.setattr(audio_router, "transcribe_audio", fake_transcribe_audio)

    resp = client.post(
        "/input/audio",
        data={"meta": '{"session_id":"s-audio"}'},
        files={"audio": ("input.wav", b"\x00\x01", "audio/wav")},
    )
    assert resp.status_code == 200
    data = resp.json()
    assert data["session_id"] == "s-audio"
    assert data["transcript"] == "hello"
    assert data["stt_device"] == "cpu"
    assert data["stt_compute"] == "int8"
