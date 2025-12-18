from __future__ import annotations

import json
import os
import socket
from typing import Any, Dict, List, Optional

from fastapi import APIRouter

from agent_worker.models import AgentMessage, AgentResponse, RetryInput, TextInput
from agent_worker.services.conversations import ConversationStore
from agent_worker.services.mcp_client import McpClient
from agent_worker.services.provider import call_provider
from agent_worker.services.settings import load_settings


router = APIRouter()
_conversations = ConversationStore()
_mcp_client = McpClient()


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
                    },
                },
            }
        )
    return formatted or None


def _parse_tool_args(args: Any) -> Dict[str, Any]:
    if isinstance(args, dict):
        return args
    if isinstance(args, str) and args.strip():
        try:
            parsed = json.loads(args)
            if isinstance(parsed, dict):
                return parsed
        except json.JSONDecodeError:
            return {}
    return {}


def _send_tool_callback(session_id: str, turn_id: str, phase: str, tool_names: List[str]) -> None:
    port_text = os.environ.get("LISA_CALLBACK_UDP_PORT", "")
    if not port_text.isdigit():
        return
    port = int(port_text)
    payload = {
        "session_id": session_id,
        "turn_id": turn_id,
        "phase": phase,
        "tool_calls": tool_names,
    }
    try:
        sock = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
        sock.sendto(json.dumps(payload).encode("utf-8"), ("127.0.0.1", port))
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
async def handle_text(req: TextInput):
    text = req.text.strip()
    if text.lower().startswith("/mcp"):
        parts = text.split(maxsplit=2)
        if len(parts) == 1 or parts[1].lower() == "tools":
            tools = await _mcp_client.list_tools()
            if "error" in tools:
                return AgentResponse(
                    session_id=req.session_id,
                    turn_id=req.turn_id,
                    messages=[AgentMessage(role="assistant", content=str(tools["error"]))],
                )
            return AgentResponse(
                session_id=req.session_id,
                turn_id=req.turn_id,
                messages=[AgentMessage(role="assistant", content=json.dumps(tools, indent=2))],
            )
        tool_name = parts[1]
        result = await _mcp_client.call_tool(tool_name)
        return AgentResponse(
            session_id=req.session_id,
            turn_id=req.turn_id,
            messages=[AgentMessage(role="assistant", content=json.dumps(result, indent=2))],
        )

    history = _conversations.get(req.session_id)

    tools_payload = await _mcp_client.list_tools()
    if isinstance(tools_payload, dict) and tools_payload.get("error"):
        tools_payload = None
    provider_tools = _build_tools_payload(tools_payload)

    provider_messages = list(history) + [{"role": "user", "content": req.text}]

    result = await call_provider(provider_messages, tools=provider_tools)
    tool_calls = result.get("tool_calls", [])
    completion = result.get("content") or result.get("error", "")
    used_tools = []

    if tool_calls:
        for tool_call in tool_calls:
            func = tool_call.get("function") or {}
            tool_name = func.get("name") or tool_call.get("name")
            if tool_name:
                used_tools.append(tool_name)
        _send_tool_callback(req.session_id, req.turn_id, "awaiting_tool", used_tools)
        for tool_call in tool_calls:
            func = tool_call.get("function") or {}
            tool_name = func.get("name") or tool_call.get("name")
            tool_args = _parse_tool_args(func.get("arguments") or tool_call.get("args"))
            if tool_name:
                tool_result = await _mcp_client.call_tool(tool_name, tool_args)
                provider_messages.append(
                    {"role": "tool", "name": tool_name, "content": json.dumps(tool_result, indent=2)}
                )
        _send_tool_callback(req.session_id, req.turn_id, "tool_response", used_tools)
        follow = await call_provider(provider_messages, tools=provider_tools)
        completion = follow.get("content") or follow.get("error", "")
        _send_tool_callback(req.session_id, req.turn_id, "tool_complete", used_tools)

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
        speak=False,
        tts_text=None,
    )


@router.post("/input/retry", response_model=AgentResponse)
async def handle_retry(req: RetryInput):
    history = _conversations.get(req.session_id)
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

    result = await call_provider(list(history))
    completion = result.get("content") or result.get("error", "")
    used_tools = []
    messages = [AgentMessage(role="assistant", content=completion)]
    if not completion or completion == "{}" or completion == "{ }":
        messages = [
            AgentMessage(role="assistant", content="(empty content from provider)"),
            AgentMessage(role="assistant", content=f"(debug payload) {completion}"),
        ]

    _conversations.append(req.session_id, "assistant", completion)

    return AgentResponse(
        session_id=req.session_id,
        turn_id=req.turn_id,
        messages=messages,
        tool_calls=used_tools if used_tools else None,
        speak=False,
        tts_text=None,
    )
