from __future__ import annotations

import asyncio
import orjson
import os
import socket
import time
from typing import Any, Dict, List, Optional

from fastapi import APIRouter, Header

from agent_worker.models import AgentMessage, AgentResponse, RetryInput, TextInput
from agent_worker.services.conversations import ConversationStore
from agent_worker.services.mcp_client import McpClient
from agent_worker.services.provider import call_provider, call_provider_stream, warm_provider_client
from agent_worker.services.settings import load_settings
from agent_worker.services.system_context import collect_system_context


router = APIRouter()
_conversations = ConversationStore()
_mcp_client = McpClient()
_tool_context: Optional[str] = None
_tool_context_version = 0
_SYSTEM_CONTEXT = collect_system_context()


async def init_tool_cache() -> bool:
    global _tool_context, _tool_context_version
    tools_payload = await _mcp_client.list_tools(force_refresh=True)
    if isinstance(tools_payload, dict) and not tools_payload.get("error"):
        _update_tool_context(tools_payload)
        return True
    return False


async def _retry_tool_cache() -> None:
    while True:
        tools_payload = await _mcp_client.list_tools(force_refresh=True)
        if isinstance(tools_payload, dict) and not tools_payload.get("error"):
            _update_tool_context(tools_payload)
            return
        await asyncio.sleep(10)


def start_tool_cache_retry() -> None:
    asyncio.create_task(_retry_tool_cache())


async def warm_services() -> None:
    await _mcp_client.warm_client()
    await warm_provider_client()


def _update_tool_context(tools_payload: Dict[str, Any]) -> None:
    global _tool_context, _tool_context_version
    new_context = f"Available tools:\n{orjson.dumps(tools_payload, option=orjson.OPT_INDENT_2).decode('utf-8')}"
    if new_context != _tool_context:
        _tool_context = new_context
        _tool_context_version += 1


def _build_tools_payload(tools_payload: Optional[Dict[str, Any]]) -> Optional[List[Dict[str, Any]]]:
    if not tools_payload:
        return None
    tools = tools_payload.get("tools", [])
    formatted = []
    for tool in tools:
        name = tool.get("name")
        if not name:
            continue
        formatted.append(
            {
                "type": "function",
                "function": {
                    "name": name,
                    "description": tool.get("description", ""),
                    "parameters": {
                        "type": "object",
                        "properties": {},
                        "additionalProperties": True,
                    },
                },
            }
        )
    return formatted or None


def _build_provider_messages(
    history: List[dict],
    user_text: Optional[str],
) -> List[dict]:
    messages: List[dict] = []
    messages.extend(history)
    if user_text is not None:
        messages.append({"role": "user", "content": user_text})
    return messages


def _parse_tool_args(args: Any) -> Dict[str, Any]:
    if isinstance(args, dict):
        return args
    if isinstance(args, str) and args.strip():
        try:
            parsed = orjson.loads(args)
            if isinstance(parsed, dict):
                return parsed
        except orjson.JSONDecodeError:
            return {}
    return {}


def _send_tool_callback(session_id: str, turn_id: str, phase: str, tool_names: List[str]) -> None:
    port_text = os.environ.get("LISA_CALLBACK_UDP_PORT", "")
    token = os.environ.get("LISA_CALLBACK_TOKEN", "")
    if not port_text.isdigit():
        return
    if not token:
        return
    port = int(port_text)
    payload = {
        "session_id": session_id,
        "turn_id": turn_id,
        "phase": phase,
        "tool_calls": tool_names,
        "token": token,
    }
    try:
        sock = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
        sock.sendto(orjson.dumps(payload), ("127.0.0.1", port))
    except Exception:
        pass
    finally:
        try:
            sock.close()
        except Exception:
            pass


def _send_thinking_callback(session_id: str, turn_id: str, phase: str, delta: Optional[str] = None) -> None:
    port_text = os.environ.get("LISA_CALLBACK_UDP_PORT", "")
    token = os.environ.get("LISA_CALLBACK_TOKEN", "")
    if not port_text.isdigit():
        return
    if not token:
        return
    port = int(port_text)
    payload: Dict[str, Any] = {
        "session_id": session_id,
        "turn_id": turn_id,
        "phase": phase,
        "token": token,
    }
    if delta:
        payload["thinking_delta"] = delta
    try:
        sock = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
        sock.sendto(orjson.dumps(payload), ("127.0.0.1", port))
    except Exception:
        pass
    finally:
        try:
            sock.close()
        except Exception:
            pass


def _send_content_callback(session_id: str, turn_id: str, phase: str, delta: Optional[str] = None) -> None:
    port_text = os.environ.get("LISA_CALLBACK_UDP_PORT", "")
    token = os.environ.get("LISA_CALLBACK_TOKEN", "")
    if not port_text.isdigit():
        return
    if not token:
        return
    port = int(port_text)
    payload: Dict[str, Any] = {
        "session_id": session_id,
        "turn_id": turn_id,
        "phase": phase,
        "token": token,
    }
    if delta:
        payload["content_delta"] = delta
    try:
        sock = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
        sock.sendto(orjson.dumps(payload), ("127.0.0.1", port))
    except Exception:
        pass
    finally:
        try:
            sock.close()
        except Exception:
            pass


@router.get("/health")
async def health():
    cfg = load_settings()
    return {"status": "ok", "provider": f"{cfg.host}:{cfg.port}", "model": cfg.model}


@router.post("/input/text", response_model=AgentResponse)
async def handle_text(req: TextInput, x_mcp_token: str | None = Header(default=None)):
    if x_mcp_token:
        os.environ["MCP_AUTH_TOKEN"] = x_mcp_token
    start_time = time.monotonic()
    text = req.text.strip()

    history = _conversations.get_history(req.session_id)

    tools_payload = _mcp_client.get_cached_tools()
    if isinstance(tools_payload, dict) and tools_payload.get("error"):
        tools_payload = None
    elif isinstance(tools_payload, dict):
        _update_tool_context(tools_payload)
    provider_tools = _build_tools_payload(tools_payload)

    # Tool registry is injected once per session to avoid repeated prompt bloat.
    _conversations.ensure_system(req.session_id, _SYSTEM_CONTEXT, _tool_context, _tool_context_version)
    provider_messages = _build_provider_messages(_conversations.get_all(req.session_id), req.text)

    streamed_content = {"seen": False}

    def _on_content(chunk: str) -> None:
        streamed_content["seen"] = True
        _send_content_callback(req.session_id, req.turn_id, "content_chunk", chunk)

    result = await call_provider_stream(
        provider_messages,
        tools=provider_tools,
        on_thinking_chunk=lambda chunk: _send_thinking_callback(req.session_id, req.turn_id, "thinking_chunk", chunk),
        on_content_chunk=_on_content,
    )
    if streamed_content["seen"]:
        _send_content_callback(req.session_id, req.turn_id, "content_done")
    if not result.get("tool_calls") and not result.get("content"):
        result = await call_provider(provider_messages, tools=provider_tools)
    _send_thinking_callback(req.session_id, req.turn_id, "thinking_done")
    tool_calls = result.get("tool_calls", [])
    completion = result.get("content") or result.get("error", "")
    thinking = result.get("thinking", "")
    used_tools = []

    if tool_calls:
        for tool_call in tool_calls:
            func = tool_call.get("function") or tool_call
            tool_name = func.get("name") or tool_call.get("name")
            if tool_name:
                used_tools.append(tool_name)
        _send_tool_callback(req.session_id, req.turn_id, "awaiting_tool", used_tools)
        for tool_call in tool_calls:
            func = tool_call.get("function") or tool_call
            tool_name = func.get("name") or tool_call.get("name")
            tool_args = _parse_tool_args(func.get("arguments") or tool_call.get("args"))
            if tool_name:
                tool_result = await _mcp_client.call_tool(tool_name, tool_args)
                provider_messages.append(
                    {"role": "tool", "name": tool_name, "content": orjson.dumps(tool_result, option=orjson.OPT_INDENT_2).decode("utf-8")}
                )
        _send_tool_callback(req.session_id, req.turn_id, "tool_response", used_tools)
        follow_streamed = {"seen": False}

        def _on_follow_content(chunk: str) -> None:
            follow_streamed["seen"] = True
            _send_content_callback(req.session_id, req.turn_id, "content_chunk", chunk)

        follow = await call_provider_stream(
            provider_messages,
            tools=provider_tools,
            on_thinking_chunk=lambda chunk: _send_thinking_callback(req.session_id, req.turn_id, "thinking_chunk", chunk),
            on_content_chunk=_on_follow_content,
        )
        if follow_streamed["seen"]:
            _send_content_callback(req.session_id, req.turn_id, "content_done")
        completion = follow.get("content") or follow.get("error", "")
        _send_tool_callback(req.session_id, req.turn_id, "tool_complete", used_tools)

    thinking_ms = int((time.monotonic() - start_time) * 1000)
    reasoning = ""
    if thinking:
        reasoning = thinking
    elif used_tools:
        reasoning = f"Tool calls: {', '.join(used_tools)}\nAwaited tool responses."

    messages = [AgentMessage(role="assistant", content=completion)]
    if not completion or completion == "{}" or completion == "{ }":
        messages = [
            AgentMessage(role="assistant", content="(empty content from provider)"),
            AgentMessage(role="assistant", content=f"(debug payload) {completion}"),
        ]

    _conversations.append(req.session_id, "user", req.text)
    _conversations.append(req.session_id, "assistant", completion)

    return AgentResponse(
        session_id=req.session_id,
        turn_id=req.turn_id,
        messages=messages,
        tool_calls=used_tools if used_tools else None,
        reasoning=reasoning,
        thinking_ms=thinking_ms,
        speak=False,
        tts_text=completion or None,
    )


@router.post("/input/retry", response_model=AgentResponse)
async def handle_retry(req: RetryInput, x_mcp_token: str | None = Header(default=None)):
    if x_mcp_token:
        os.environ["MCP_AUTH_TOKEN"] = x_mcp_token
    start_time = time.monotonic()
    history = _conversations.get_history(req.session_id)
    if not history:
        return AgentResponse(
            session_id=req.session_id,
            turn_id=req.turn_id,
            messages=[AgentMessage(role="assistant", content="(no prior message to retry)")],
        )

    for idx in range(len(history) - 1, -1, -1):
        if history[idx].get("role") == "assistant":
            history.pop(idx)
            break

    if not history:
        return AgentResponse(
            session_id=req.session_id,
            turn_id=req.turn_id,
            messages=[AgentMessage(role="assistant", content="(no prior message to retry)")],
        )

    tools_payload = _mcp_client.get_cached_tools()
    if isinstance(tools_payload, dict) and tools_payload.get("error"):
        tools_payload = None
    elif isinstance(tools_payload, dict):
        _update_tool_context(tools_payload)

    streamed_content = {"seen": False}

    def _on_content(chunk: str) -> None:
        streamed_content["seen"] = True
        _send_content_callback(req.session_id, req.turn_id, "content_chunk", chunk)

    _conversations.ensure_system(req.session_id, _SYSTEM_CONTEXT, _tool_context, _tool_context_version)
    provider_messages = _build_provider_messages(_conversations.get_all(req.session_id), None)

    result = await call_provider_stream(
        provider_messages,
        tools=_build_tools_payload(tools_payload),
        on_thinking_chunk=lambda chunk: _send_thinking_callback(req.session_id, req.turn_id, "thinking_chunk", chunk),
        on_content_chunk=_on_content,
    )
    if streamed_content["seen"]:
        _send_content_callback(req.session_id, req.turn_id, "content_done")
    if not result.get("tool_calls") and not result.get("content"):
        result = await call_provider(provider_messages, tools=_build_tools_payload(tools_payload))
    _send_thinking_callback(req.session_id, req.turn_id, "thinking_done")
    completion = result.get("content") or result.get("error", "")
    used_tools = []
    messages = [AgentMessage(role="assistant", content=completion)]
    if not completion or completion == "{}" or completion == "{ }":
        messages = [
            AgentMessage(role="assistant", content="(empty content from provider)"),
            AgentMessage(role="assistant", content=f"(debug payload) {completion}"),
        ]

    _conversations.append(req.session_id, "assistant", completion)

    thinking_ms = int((time.monotonic() - start_time) * 1000)
    reasoning = thinking if (thinking := result.get("thinking", "")) else ""

    return AgentResponse(
        session_id=req.session_id,
        turn_id=req.turn_id,
        messages=messages,
        tool_calls=used_tools if used_tools else None,
        reasoning=reasoning,
        thinking_ms=thinking_ms,
        speak=False,
        tts_text=completion or None,
    )
