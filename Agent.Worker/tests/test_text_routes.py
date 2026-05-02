import asyncio

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
    assert data["status"] == "healthy"
    assert data["model"] == "test-model"


def test_text_basic_no_tools(client, monkeypatch):
    async def fake_stream(messages, tools=None, on_thinking_chunk=None, on_content_chunk=None):
        return {"content": "Hello", "tool_calls": [], "thinking": ""}

    monkeypatch.setattr(text_router, "call_provider_stream", fake_stream)
    monkeypatch.setattr(text_router, "call_provider", fake_stream)
    monkeypatch.setattr(text_router._tool_client, "get_cached_tools", lambda active_categories=None: {"tools": []})

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
        text_router._tool_client,
        "get_cached_tools",
        lambda active_categories=None: {"tools": [{"name": "system_overview", "description": "x"}]},
    )

    async def fake_call_tool(*args, **kwargs):
        return {"result": {"ok": True}}

    monkeypatch.setattr(text_router._tool_client, "call_tool", fake_call_tool)
    monkeypatch.setattr(text_router, "_await_tool_approval", lambda *args, **kwargs: asyncio.sleep(0, result=True))

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


def test_text_tool_call_requires_approval_end_to_end(client, monkeypatch):
    calls = {"count": 0}
    approval = {}

    async def fake_stream(messages, tools=None, on_thinking_chunk=None, on_content_chunk=None):
        calls["count"] += 1
        if calls["count"] == 1:
            return {
                "content": "",
                "tool_calls": [
                    {
                        "function": {
                            "name": "outlook_create_calendar_event",
                            "arguments": '{"subject":"PTO","start":"2026-01-07T09:00:00","end":"2026-01-07T18:00:00"}',
                        }
                    }
                ],
                "thinking": "",
            }
        return {"content": "Approved and completed", "tool_calls": [], "thinking": ""}

    async def fake_call_tool(*args, **kwargs):
        return {"success": True, "data": {"created": True}}

    async def fake_send_tool_approval_callback(session_id, turn_id, approval_id, tool_name, tool_args, friendly_desc):
        approval["callback"] = {
            "session_id": session_id,
            "turn_id": turn_id,
            "approval_id": approval_id,
            "tool_name": tool_name,
            "tool_args": tool_args,
            "friendly_desc": friendly_desc,
        }

    def fake_create(approval_id):
        approval["created"] = approval_id
        return None

    async def fake_wait_for(approval_id, timeout_s):
        approval["waited"] = {"approval_id": approval_id, "timeout_s": timeout_s}
        return True

    monkeypatch.setattr(text_router, "call_provider_stream", fake_stream)
    monkeypatch.setattr(text_router, "call_provider", fake_stream)
    monkeypatch.setattr(
        text_router._tool_client,
        "get_cached_tools",
        lambda active_categories=None: {
            "tools": [
                {
                    "name": "outlook_create_calendar_event",
                    "description": "create event",
                    "args_schema": {"type": "object", "properties": {"subject": {"type": "string"}}},
                }
            ]
        },
    )
    monkeypatch.setattr(
        text_router._tool_client,
        "get_tool_meta",
        lambda name: {
            "friendly_desc": "would like to create a calendar event.",
            "approval_required": True,
        },
    )
    monkeypatch.setattr(text_router._tool_client, "call_tool", fake_call_tool)
    monkeypatch.setattr(text_router, "_send_tool_approval_callback", fake_send_tool_approval_callback)
    monkeypatch.setattr(text_router.approval_manager, "create", fake_create)
    monkeypatch.setattr(text_router.approval_manager, "wait_for", fake_wait_for)

    payload = {
        "session_id": "s-approval",
        "turn_id": "t-approval",
        "text": "mark tomorrow as pto",
        "input_meta": {"active_app": None, "window_title": None, "clipboard": None},
    }
    resp = client.post("/input/text", json=payload)
    assert resp.status_code == 200
    data = resp.json()
    assert data["messages"][0]["content"] == "Approved and completed"
    assert data["tool_calls"] == ["outlook_create_calendar_event"]
    assert approval["callback"]["tool_name"] == "outlook_create_calendar_event"
    assert approval["callback"]["friendly_desc"] == "would like to create a calendar event."
    assert approval["callback"]["tool_args"]["subject"] == "PTO"
    assert approval["created"] == approval["callback"]["approval_id"]
    assert approval["waited"]["approval_id"] == approval["callback"]["approval_id"]


def test_text_filters_tools_by_active_skill_categories(client, monkeypatch):
    seen = {}

    async def fake_stream(messages, tools=None, on_thinking_chunk=None, on_content_chunk=None):
        seen["provider_tools"] = tools
        return {"content": "Filtered", "tool_calls": [], "thinking": ""}

    def fake_get_cached_tools(active_categories=None):
        seen["active_categories"] = active_categories
        return {
            "skills": [{"name": "outlook", "description": "Outlook operations"}],
            "tools": [
                {
                    "name": "activate_skill",
                    "description": "Load the full skill instructions.",
                    "args_schema": {
                        "type": "object",
                        "properties": {"skill_name": {"type": "string"}},
                    },
                }
            ],
        }

    monkeypatch.setattr(text_router, "call_provider_stream", fake_stream)
    monkeypatch.setattr(text_router, "call_provider", fake_stream)
    monkeypatch.setattr(text_router._tool_client, "get_cached_tools", fake_get_cached_tools)

    payload = {
        "session_id": "s-filter",
        "turn_id": "t-filter",
        "text": "check outlook",
        "input_meta": {
            "active_skills_categories": ["outlook"]
        },
    }
    resp = client.post("/input/text", json=payload)
    assert resp.status_code == 200
    assert seen["active_categories"] == ["outlook"]
    assert [tool["function"]["name"] for tool in seen["provider_tools"]] == ["activate_skill"]


def test_retry_flow(client, monkeypatch):
    async def fake_stream(messages, tools=None, on_thinking_chunk=None, on_content_chunk=None):
        return {"content": "Retry ok", "tool_calls": [], "thinking": ""}

    monkeypatch.setattr(text_router, "call_provider_stream", fake_stream)
    monkeypatch.setattr(text_router, "call_provider", fake_stream)
    monkeypatch.setattr(text_router._tool_client, "get_cached_tools", lambda active_categories=None: {"tools": []})

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
