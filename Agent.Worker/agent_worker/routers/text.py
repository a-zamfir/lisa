from __future__ import annotations

import asyncio
import orjson
import os
import socket
import threading
import time
import uuid
import logging
from typing import Any, Dict, List, Optional

from fastapi import APIRouter

from agent_worker.models import AgentMessage, AgentResponse, RetryInput, TextInput, ToolApprovalDecision
from agent_worker.services.conversations import ConversationStore
from agent_worker.services.memory_policy import parse_ops, validate_ops
from agent_worker.services.memory_store import MemoryStore
from agent_worker.services.memory_trailer import StreamTrailerFilter, extract_memory_trailer
from agent_worker.services.provider import call_provider, call_provider_stream, warm_provider_client
from agent_worker.services.session_state import get_session_nonce, set_session_nonce
from agent_worker.services.settings import load_settings
from agent_worker.services.skills_client import SkillsClient
from agent_worker.services.system_context import collect_system_context
from agent_worker.services.tool_approval import approval_manager
from agent_worker.services.visual_context import pop_latest_frame, peek_latest_frame


router = APIRouter()
_conversations = ConversationStore()
_log = logging.getLogger("agent_worker.text")

_skills_client = SkillsClient()
_tool_client = _skills_client
_tool_client_name = "skills"
_log.info("Using skills tool client")

_memory_store = MemoryStore()
_tool_context: Optional[str] = None
_tool_context_version = 0
_SYSTEM_CONTEXT = collect_system_context()
_callback_socket: Optional[socket.socket] = None
_callback_port: Optional[int] = None
_callback_lock = threading.Lock()
_callback_queue: "asyncio.Queue[Dict[str, Any]]" = asyncio.Queue(maxsize=8192)
_callback_sender_task: Optional[asyncio.Task] = None
_APPROVAL_TIMEOUT_S = 30

# Rate limiting: prevent resource exhaustion from concurrent requests
_GLOBAL_SEMAPHORE = asyncio.Semaphore(3)  # Max 3 concurrent requests globally
_SESSION_SEMAPHORES: Dict[str, asyncio.Semaphore] = {}  # Max 1 request per session
_SESSION_SEMAPHORES_LOCK = asyncio.Lock()


async def _get_session_semaphore(session_id: str) -> asyncio.Semaphore:
    """Get or create a semaphore for a session (max 1 concurrent request per session)."""
    async with _SESSION_SEMAPHORES_LOCK:
        if session_id not in _SESSION_SEMAPHORES:
            _SESSION_SEMAPHORES[session_id] = asyncio.Semaphore(1)
        return _SESSION_SEMAPHORES[session_id]


def _get_cached_tools(active_categories: Optional[List[str]] = None) -> Optional[Dict[str, Any]]:
    return _tool_client.get_cached_tools(active_categories=active_categories)


def _normalize_tool_schema(schema: Any) -> Dict[str, Any]:
    if not isinstance(schema, dict) or not schema:
        return {"type": "object", "properties": {}, "additionalProperties": True}
    if "type" not in schema:
        return {"type": "object", "properties": schema, "additionalProperties": True}
    normalized = dict(schema)
    normalized.setdefault("properties", {})
    normalized.setdefault("additionalProperties", True)
    return normalized


def _sanitize_tool_context_payload(tools_payload: Dict[str, Any]) -> Dict[str, Any]:
    skills = tools_payload.get("skills", [])
    sanitized_skills = []
    for skill in skills:
        name = skill.get("name")
        if not name:
            continue
        sanitized_skills.append(
            {
                "name": name,
                "description": skill.get("description", ""),
            }
        )

    tools = tools_payload.get("tools", [])
    sanitized = []
    for tool in tools:
        name = tool.get("name")
        if not name:
            continue
        entry = {
            "name": name,
            "description": tool.get("description", ""),
        }
        schema = tool.get("args_schema") or tool.get("input_schema") or {}
        if isinstance(schema, dict) and schema:
            entry["args_schema"] = schema
        category = tool.get("category")
        if category:
            entry["category"] = category
        sanitized.append(entry)
    payload: Dict[str, Any] = {"tools": sanitized}
    if sanitized_skills:
        payload["skills"] = sanitized_skills
        payload["skill_activation"] = (
            "If a task clearly matches a skill description, call activate_skill with the skill name "
            "to load the skill's full instructions before using its tools."
        )
    return payload


async def init_tool_cache() -> bool:
    global _tool_context, _tool_context_version

    try:
        _log.info("Initializing tool client (%s)...", _tool_client_name)
        await _tool_client.initialize()
        _log.info("Tool client initialized (%s)", _tool_client_name)
    except Exception as ex:
        _log.error("Failed to initialize tool client (%s): %s", _tool_client_name, ex)
        return False

    tools_payload = await _tool_client.list_tools(force_refresh=True)
    await _tool_client.list_tool_docs(force_refresh=True)
    if isinstance(tools_payload, dict) and not tools_payload.get("error"):
        _update_tool_context(tools_payload)
        return True
    return False


async def _retry_tool_cache() -> None:
    while True:
        tools_payload = await _tool_client.list_tools(force_refresh=True)
        await _tool_client.list_tool_docs(force_refresh=True)
        if isinstance(tools_payload, dict) and not tools_payload.get("error"):
            _update_tool_context(tools_payload)
            return
        await asyncio.sleep(10)


def start_tool_cache_retry() -> None:
    asyncio.create_task(_retry_tool_cache())


async def warm_services() -> None:
    await _tool_client.warm_client()
    await warm_provider_client()


def _update_tool_context(tools_payload: Dict[str, Any]) -> None:
    global _tool_context, _tool_context_version
    safe_payload = _sanitize_tool_context_payload(tools_payload)
    new_context = f"Available tools:\n{orjson.dumps(safe_payload, option=orjson.OPT_INDENT_2).decode('utf-8')}"
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
        schema = _normalize_tool_schema(tool.get("args_schema") or tool.get("input_schema") or {})
        formatted.append(
            {
                "type": "function",
                "function": {
                    "name": name,
                    "description": tool.get("description", ""),
                    "parameters": schema,
                },
            }
        )
    return formatted or None


def _build_provider_messages(
    history: List[dict],
    user_text: Optional[str],
    *,
    frame: object | None = None,
    memory_context: Optional[str] = None,
) -> List[dict]:
    messages: List[dict] = []
    messages.extend(history)
    if memory_context:
        messages.append({"role": "system", "content": memory_context})
    if user_text is not None:
        if frame is not None:
            # Keep the vision prompt tight: we only add this when an image is attached.
            messages.append(
                {
                    "role": "system",
                    "content": "A screenshot is attached. It may include a small LISA overlay UI; ignore the overlay and focus on the underlying app/content.",
                }
            )
            mime = getattr(frame, "mime_type", "image/jpeg")
            b64 = getattr(frame, "data_base64", "")
            messages.append(
                {
                    "role": "user",
                    "content": [
                        {"type": "text", "text": user_text},
                        {"type": "image_url", "image_url": {"url": f"data:{mime};base64,{b64}"}},
                    ],
                }
            )
        else:
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


def _close_callback_socket() -> None:
    global _callback_socket, _callback_port
    if _callback_socket is not None:
        try:
            _callback_socket.close()
        except Exception:
            pass
    _callback_socket = None
    _callback_port = None


def _get_callback_socket(port: int) -> Optional[socket.socket]:
    global _callback_socket, _callback_port
    if _callback_socket is not None and _callback_port == port:
        return _callback_socket
    _close_callback_socket()
    try:
        sock = socket.create_connection(("127.0.0.1", port), timeout=0.5)
        sock.setsockopt(socket.IPPROTO_TCP, socket.TCP_NODELAY, 1)
        _callback_socket = sock
        _callback_port = port
        return sock
    except Exception:
        _close_callback_socket()
        return None


def _write_callback_payload(payload: Dict[str, Any]) -> None:
    port_text = os.environ.get("LISA_CALLBACK_TCP_PORT", "")
    token = os.environ.get("LISA_CALLBACK_TOKEN", "")
    debug = os.environ.get("LISA_CALLBACK_DEBUG", "")
    if not port_text.isdigit() or not token:
        return
    payload["token"] = token
    session_nonce = get_session_nonce(payload.get("session_id", ""))
    if session_nonce:
        payload["session_nonce"] = session_nonce
    data = orjson.dumps(payload)
    length = len(data).to_bytes(4, "little", signed=False)
    port = int(port_text)
    with _callback_lock:
        sock = _get_callback_socket(port)
        if sock is None:
            return
        try:
            sock.sendall(length + data)
            if debug:
                print(f"[callback] sent phase={payload.get('phase')} turn_id={payload.get('turn_id')}")
        except Exception as exc:
            _close_callback_socket()
            try:
                sock = _get_callback_socket(port)
                if sock is None:
                    return
                sock.sendall(length + data)
                if debug:
                    print(f"[callback] sent phase={payload.get('phase')} turn_id={payload.get('turn_id')}")
            except Exception as exc2:
                _close_callback_socket()
                if debug:
                    print(f"[callback] send failed: {exc2}")


async def _send_pipe_payload(payload: Dict[str, Any]) -> None:
    _ensure_callback_sender()
    try:
        _callback_queue.put_nowait(payload)
    except asyncio.QueueFull:
        await _callback_queue.put(payload)


def _ensure_callback_sender() -> None:
    global _callback_sender_task
    try:
        asyncio.get_running_loop()
    except RuntimeError:
        return

    if _callback_sender_task is None or _callback_sender_task.done():
        _callback_sender_task = asyncio.create_task(_callback_sender_loop())


async def _callback_sender_loop() -> None:
    while True:
        payload = await _callback_queue.get()
        try:
            await asyncio.to_thread(_write_callback_payload, payload)
        except Exception:
            # Best-effort: callbacks should not crash the agent.
            pass
        finally:
            _callback_queue.task_done()


async def _send_tool_callback(session_id: str, turn_id: str, phase: str, tool_names: List[str]) -> None:
    payload = {
        "session_id": session_id,
        "turn_id": turn_id,
        "phase": phase,
        "tool_calls": tool_names,
    }
    await _send_pipe_payload(payload)


async def _send_tool_approval_callback(
    session_id: str,
    turn_id: str,
    approval_id: str,
    tool_name: str,
    tool_args: Dict[str, Any],
    friendly_desc: str,
) -> None:
    args_text = "None"
    if tool_args:
        args_text = orjson.dumps(tool_args, option=orjson.OPT_INDENT_2).decode("utf-8")
    payload = {
        "session_id": session_id,
        "turn_id": turn_id,
        "phase": "tool_approval_required",
        "tool_name": tool_name,
        "tool_args": args_text,
        "friendly_desc": friendly_desc,
        "approval_id": approval_id,
        "timeout_s": _APPROVAL_TIMEOUT_S,
    }
    await _send_pipe_payload(payload)


async def _send_tool_auto_approved_callback(
    session_id: str,
    turn_id: str,
    tool_name: str,
    friendly_desc: str,
) -> None:
    """Send callback to UI when a tool is auto-approved (no user interaction needed)."""
    payload = {
        "session_id": session_id,
        "turn_id": turn_id,
        "phase": "tool_auto_approved",
        "tool_name": tool_name,
        "friendly_desc": friendly_desc,
    }
    await _send_pipe_payload(payload)


async def _await_tool_approval(session_id: str, turn_id: str, tool_name: str, tool_args: Dict[str, Any]) -> bool:
    meta = _tool_client.get_tool_meta(tool_name) if hasattr(_tool_client, "get_tool_meta") else {}
    meta = meta or {}
    if not meta and hasattr(_tool_client, "list_tool_docs"):
        await _tool_client.list_tool_docs(force_refresh=True)
        meta = _tool_client.get_tool_meta(tool_name) if hasattr(_tool_client, "get_tool_meta") else {}
        meta = meta or {}
    friendly_desc = meta.get("friendly_desc") or f"running {tool_name}"
    if meta and not meta.get("approval_required"):
        # Auto-approved - send callback to show label in UI
        await _send_tool_auto_approved_callback(session_id, turn_id, tool_name, friendly_desc)
        return True
    approval_id = str(uuid.uuid4())
    approval_manager.create(approval_id)
    await _send_tool_approval_callback(session_id, turn_id, approval_id, tool_name, tool_args, friendly_desc)
    return await approval_manager.wait_for(approval_id, _APPROVAL_TIMEOUT_S)


async def _send_memory_callback(session_id: str, turn_id: str, phase: str, op_count: int = 0, error: str = "") -> None:
    payload: Dict[str, Any] = {
        "session_id": session_id,
        "turn_id": turn_id,
        "phase": phase,
    }
    if op_count:
        payload["memory_op_count"] = op_count
    if error:
        payload["memory_error"] = error
    await _send_pipe_payload(payload)


async def _build_memory_context(cfg, query: str) -> str:
    if not getattr(cfg, "memory_enabled", False):
        return ""

    try:
        items = await asyncio.wait_for(
            asyncio.to_thread(_memory_store.search, query, limit=6),
            timeout=0.02,
        )
    except Exception:
        items = []

    if items:
        lines = [
            "Long-term memory (user-provided; may be outdated). Use this if relevant; if it seems wrong, ask the user:"
        ]
        for item in items:
            lines.append(f"[mem] {item.key} = {item.value}")
        text = "\n".join(lines).strip()
        return text[:1200]

    # If nothing is found, explicitly tell the model to avoid guessing personal facts.
    return (
        "Long-term memory: none found relevant for this prompt. "
        "If the user asks about personal facts/preferences and you can't find them in memory, ask a clarifying question rather than guessing."
    )


def _extract_and_schedule_memory_update(cfg, session_id: str, turn_id: str, completion: str) -> str:
    cleaned, payload = extract_memory_trailer(completion)
    if not payload:
        if cleaned != completion and "<lisa_memory>" in completion:
            _log.info("memory trailer stripped but invalid/unparseable session=%s turn=%s", session_id, turn_id)
        return cleaned

    ops = parse_ops(payload)
    accepted, rejected_reasons = validate_ops(ops)
    if not accepted:
        if rejected_reasons:
            _log.info("memory trailer rejected: %s", "; ".join(rejected_reasons[:10]))
        return cleaned

    if not getattr(cfg, "memory_enabled", False):
        _log.debug("memory trailer present but memory disabled; skipping")
        return cleaned

    _memory_store.configure_path(getattr(cfg, "memory_path", None))
    _log.info(
        "memory update scheduled session=%s turn=%s ops=%s db=%s",
        session_id,
        turn_id,
        len(accepted),
        str(_memory_store.path),
    )

    async def _apply() -> None:
        await _send_memory_callback(session_id, turn_id, "memory_update_started", op_count=len(accepted))
        try:
            await asyncio.to_thread(_memory_store.ensure_initialized)
            for op in accepted:
                if op.op == "delete":
                    await asyncio.to_thread(_memory_store.delete, op.key)
                else:
                    await asyncio.to_thread(
                        _memory_store.upsert,
                        key=op.key,
                        kind=op.kind or "misc_fact",
                        value=op.value or "",
                        confidence=op.confidence,
                        source_session_id=session_id,
                        source_turn_id=turn_id,
                    )
            await _send_memory_callback(session_id, turn_id, "memory_update_done", op_count=len(accepted))
            _log.info("memory update applied session=%s turn=%s ops=%s", session_id, turn_id, len(accepted))
        except Exception as ex:
            await _send_memory_callback(session_id, turn_id, "memory_update_failed", error=str(ex))
            _log.warning("memory update failed session=%s turn=%s err=%s", session_id, turn_id, str(ex))

    asyncio.create_task(_apply())
    return cleaned


def _send_thinking_callback(session_id: str, turn_id: str, phase: str, delta: Optional[str] = None) -> None:
    payload: Dict[str, Any] = {
        "session_id": session_id,
        "turn_id": turn_id,
        "phase": phase,
    }
    if delta:
        payload["thinking_delta"] = delta
    try:
        _ensure_callback_sender()
        _callback_queue.put_nowait(payload)
    except RuntimeError:
        _write_callback_payload(payload)
    except asyncio.QueueFull:
        try:
            loop = asyncio.get_running_loop()
            loop.create_task(_send_pipe_payload(payload))
        except RuntimeError:
            _write_callback_payload(payload)


def _send_content_callback(session_id: str, turn_id: str, phase: str, delta: Optional[str] = None) -> None:
    payload: Dict[str, Any] = {
        "session_id": session_id,
        "turn_id": turn_id,
        "phase": phase,
    }
    if delta:
        payload["content_delta"] = delta
    try:
        _ensure_callback_sender()
        _callback_queue.put_nowait(payload)
    except RuntimeError:
        _write_callback_payload(payload)
    except asyncio.QueueFull:
        try:
            loop = asyncio.get_running_loop()
            loop.create_task(_send_pipe_payload(payload))
        except RuntimeError:
            _write_callback_payload(payload)


@router.get("/health")
async def health():
    """Health check endpoint - no auth required for monitoring."""
    from datetime import UTC, datetime
    cfg = load_settings()
    return {
        "status": "healthy",
        "timestamp": datetime.now(UTC).isoformat(),
        "service": "agent",
        "provider": f"{cfg.host}:{cfg.port}",
        "model": cfg.model
    }


@router.get("/ready")
async def ready():
    """Readiness check - verifies dependencies are available."""
    from datetime import UTC, datetime
    cfg = load_settings()

    tool_ready = _tool_client.available

    # Check if tools are cached
    tools_cached = _tool_context is not None

    # Check provider configuration
    provider_configured = bool(cfg.host and cfg.port and cfg.model)

    all_ready = tool_ready and tools_cached and provider_configured

    return {
        "status": "ready" if all_ready else "not_ready",
        "timestamp": datetime.now(UTC).isoformat(),
        "dependencies": {
            "tool_client": tool_ready,
            "tool_client_type": _tool_client_name,
            "tools_cached": tools_cached,
            "provider_configured": provider_configured
        }
    }


@router.post("/input/text", response_model=AgentResponse)
async def handle_text(req: TextInput):
    # Rate limiting: acquire global and per-session semaphores
    session_sem = await _get_session_semaphore(req.session_id)

    # Try to acquire session semaphore (non-blocking)
    if not session_sem.locked():
        # Session is available
        pass
    else:
        # Session busy - return 429
        from fastapi import HTTPException, status
        raise HTTPException(
            status_code=status.HTTP_429_TOO_MANY_REQUESTS,
            detail=f"Session {req.session_id} already has an active request. Please wait."
        )

    # Try to acquire global semaphore (non-blocking check)
    if _GLOBAL_SEMAPHORE.locked() and _GLOBAL_SEMAPHORE._value == 0:
        from fastapi import HTTPException, status
        raise HTTPException(
            status_code=status.HTTP_429_TOO_MANY_REQUESTS,
            detail="Too many concurrent requests. Please try again in a moment."
        )

    async with session_sem:
        async with _GLOBAL_SEMAPHORE:
            return await _handle_text_impl(req)


async def _handle_text_impl(req: TextInput):
    provider_cfg = load_settings()
    start_time = time.monotonic()
    text = req.text.strip()
    if req.input_meta and req.input_meta.session_nonce:
        set_session_nonce(req.session_id, req.input_meta.session_nonce)

    history = _conversations.get_history(req.session_id)

    # Get active tool categories from request (if specified)
    active_categories = None
    if req.input_meta:
        _log.info("input_meta received: active_skills_categories=%s", req.input_meta.active_skills_categories)
        if req.input_meta.active_skills_categories is not None:  # Explicit None check, empty list is valid
            active_categories = req.input_meta.active_skills_categories
            _log.info("Filtering tools by active categories: %s", active_categories)
    else:
        _log.info("No input_meta in request")

    tools_payload = _get_cached_tools(active_categories)
    if isinstance(tools_payload, dict) and tools_payload.get("error"):
        tools_payload = None
    elif isinstance(tools_payload, dict):
        _update_tool_context(tools_payload)
    provider_tools = _build_tools_payload(tools_payload)

    # Tool registry is injected once per session to avoid repeated prompt bloat.
    _conversations.ensure_system(req.session_id, _SYSTEM_CONTEXT, _tool_context, _tool_context_version)

    provider_kind = (provider_cfg.provider_type or "").strip().lower()
    supports_vision = provider_kind in {"lmstudio", "lm studio", "lm-studio", "openai", "open ai"}
    frame = None
    if supports_vision and peek_latest_frame(req.session_id) is not None:
        frame = pop_latest_frame(req.session_id)
        if frame is not None:
            _log.info(
                "attaching visual frame session=%s mime=%s %sx%s",
                req.session_id,
                getattr(frame, "mime_type", ""),
                getattr(frame, "width", 0),
                getattr(frame, "height", 0),
            )

    _memory_store.configure_path(getattr(provider_cfg, "memory_path", None))
    memory_context = await _build_memory_context(provider_cfg, req.text)
    provider_messages = _build_provider_messages(
        _conversations.get_all(req.session_id),
        req.text,
        frame=frame,
        memory_context=memory_context,
    )

    streamed_content = {"seen": False}
    stream_filter = StreamTrailerFilter()

    def _on_content(chunk: str) -> None:
        streamed_content["seen"] = True
        safe = stream_filter.feed(chunk)
        if safe:
            _send_content_callback(req.session_id, req.turn_id, "content_chunk", safe)

    result = await call_provider_stream(
        provider_messages,
        tools=provider_tools,
        on_thinking_chunk=lambda chunk: _send_thinking_callback(req.session_id, req.turn_id, "thinking_chunk", chunk),
        on_content_chunk=_on_content,
    )
    if streamed_content["seen"]:
        tail, _ = stream_filter.finalize()
        if tail:
            _send_content_callback(req.session_id, req.turn_id, "content_chunk", tail)
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

        approved_tools: List[str] = []
        rejected_tools: List[str] = []
        approved_calls: List[tuple[str, Dict[str, Any]]] = []

        for tool_call in tool_calls:
            func = tool_call.get("function") or tool_call
            tool_name = func.get("name") or tool_call.get("name")
            tool_args = _parse_tool_args(func.get("arguments") or tool_call.get("args"))
            if not tool_name:
                continue
            approved = await _await_tool_approval(req.session_id, req.turn_id, tool_name, tool_args)
            if not approved:
                rejected_tools.append(tool_name)
                provider_messages.append(
                    {
                        "role": "tool",
                        "name": tool_name,
                        "content": "Tool call rejected by the user or timed out. Do not proceed; ask for clarification or an alternative.",
                    }
                )
                continue
            approved_tools.append(tool_name)
            approved_calls.append((tool_name, tool_args))

            if approved_tools:
                await _send_tool_callback(req.session_id, req.turn_id, "awaiting_tool", approved_tools)
            for tool_name, tool_args in approved_calls:
                tool_result = await _tool_client.call_tool(tool_name, tool_args)
                provider_messages.append(
                    {"role": "tool", "name": tool_name, "content": orjson.dumps(tool_result, option=orjson.OPT_INDENT_2).decode("utf-8")}
                )
            await _send_tool_callback(req.session_id, req.turn_id, "tool_response", approved_tools)
        if rejected_tools:
            await _send_tool_callback(req.session_id, req.turn_id, "tool_rejected", rejected_tools)

        follow_streamed = {"seen": False}
        follow_filter = StreamTrailerFilter()

        def _on_follow_content(chunk: str) -> None:
            follow_streamed["seen"] = True
            safe = follow_filter.feed(chunk)
            if safe:
                _send_content_callback(req.session_id, req.turn_id, "content_chunk", safe)

        follow = await call_provider_stream(
            provider_messages,
            tools=provider_tools,
            on_thinking_chunk=lambda chunk: _send_thinking_callback(req.session_id, req.turn_id, "thinking_chunk", chunk),
            on_content_chunk=_on_follow_content,
        )
        if follow_streamed["seen"]:
            tail, _ = follow_filter.finalize()
            if tail:
                _send_content_callback(req.session_id, req.turn_id, "content_chunk", tail)
            _send_content_callback(req.session_id, req.turn_id, "content_done")
        if not follow_streamed["seen"] and not follow.get("content") and not follow.get("error"):
            follow = await call_provider(provider_messages, tools=provider_tools)
        completion = follow.get("content") or follow.get("error", "")
        await _send_tool_callback(req.session_id, req.turn_id, "tool_complete", used_tools)

    had_memory_trailer = "<lisa_memory>" in completion
    completion = _extract_and_schedule_memory_update(provider_cfg, req.session_id, req.turn_id, completion)

    if (not completion or completion == "{}" or completion == "{ }") and had_memory_trailer:
        _log.warning("provider returned only a memory trailer; retrying once for user-visible content")
        retry_messages = list(provider_messages) + [
            {
                "role": "system",
                "content": "Your previous reply contained no user-visible content. Reply with a short acknowledgement to the user. Do NOT include <lisa_memory>.",
            }
        ]
        retry = await call_provider(retry_messages, tools=provider_tools)
        retry_text = retry.get("content") or retry.get("error", "")
        retry_cleaned, _ = extract_memory_trailer(retry_text or "")
        completion = retry_cleaned.strip() or "Noted."

    thinking_ms = int((time.monotonic() - start_time) * 1000)
    reasoning = ""
    if thinking:
        reasoning = thinking
    elif used_tools:
        reasoning = f"Tool calls: {', '.join(used_tools)}\nAwaited tool responses."

    if not completion or completion == "{}" or completion == "{ }":
        _log.warning("provider returned empty completion; returning a short fallback message")
        completion = "Sorry — I didn’t get a response from the provider. Can you retry?"
    messages = [AgentMessage(role="assistant", content=completion)]

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
async def handle_retry(req: RetryInput):
    # Rate limiting: acquire global and per-session semaphores
    session_sem = await _get_session_semaphore(req.session_id)

    # Try to acquire session semaphore (non-blocking)
    if session_sem.locked():
        from fastapi import HTTPException, status
        raise HTTPException(
            status_code=status.HTTP_429_TOO_MANY_REQUESTS,
            detail=f"Session {req.session_id} already has an active request. Please wait."
        )

    # Try to acquire global semaphore (non-blocking check)
    if _GLOBAL_SEMAPHORE.locked() and _GLOBAL_SEMAPHORE._value == 0:
        from fastapi import HTTPException, status
        raise HTTPException(
            status_code=status.HTTP_429_TOO_MANY_REQUESTS,
            detail="Too many concurrent requests. Please try again in a moment."
        )

    async with session_sem:
        async with _GLOBAL_SEMAPHORE:
            return await _handle_retry_impl(req)


async def _handle_retry_impl(req: RetryInput):
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

    tools_payload = _get_cached_tools()
    if isinstance(tools_payload, dict) and tools_payload.get("error"):
        tools_payload = None
    elif isinstance(tools_payload, dict):
        _update_tool_context(tools_payload)

    streamed_content = {"seen": False}
    stream_filter = StreamTrailerFilter()

    def _on_content(chunk: str) -> None:
        streamed_content["seen"] = True
        safe = stream_filter.feed(chunk)
        if safe:
            _send_content_callback(req.session_id, req.turn_id, "content_chunk", safe)

    _conversations.ensure_system(req.session_id, _SYSTEM_CONTEXT, _tool_context, _tool_context_version)
    provider_messages = _build_provider_messages(_conversations.get_all(req.session_id), None)

    result = await call_provider_stream(
        provider_messages,
        tools=_build_tools_payload(tools_payload),
        on_thinking_chunk=lambda chunk: _send_thinking_callback(req.session_id, req.turn_id, "thinking_chunk", chunk),
        on_content_chunk=_on_content,
    )
    if streamed_content["seen"]:
        tail, _ = stream_filter.finalize()
        if tail:
            _send_content_callback(req.session_id, req.turn_id, "content_chunk", tail)
        _send_content_callback(req.session_id, req.turn_id, "content_done")
    if not result.get("tool_calls") and not result.get("content"):
        result = await call_provider(provider_messages, tools=_build_tools_payload(tools_payload))
    _send_thinking_callback(req.session_id, req.turn_id, "thinking_done")
    completion = result.get("content") or result.get("error", "")
    had_memory_trailer = "<lisa_memory>" in completion
    completion = _extract_and_schedule_memory_update(load_settings(), req.session_id, req.turn_id, completion)
    if (not completion or completion == "{}" or completion == "{ }") and had_memory_trailer:
        _log.warning("retry returned only a memory trailer; retrying once for user-visible content")
        retry_messages = list(provider_messages) + [
            {
                "role": "system",
                "content": "Your previous reply contained no user-visible content. Reply with a short acknowledgement to the user. Do NOT include <lisa_memory>.",
            }
        ]
        retry = await call_provider(retry_messages, tools=_build_tools_payload(tools_payload))
        retry_text = retry.get("content") or retry.get("error", "")
        retry_cleaned, _ = extract_memory_trailer(retry_text or "")
        completion = retry_cleaned.strip() or "Noted."
    used_tools = []
    if not completion or completion == "{}" or completion == "{ }":
        _log.warning("provider returned empty completion (retry); returning a short fallback message")
        completion = "Sorry — I didn’t get a response from the provider. Can you retry?"
    messages = [AgentMessage(role="assistant", content=completion)]

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


@router.post("/tool/approval")
async def handle_tool_approval(decision: ToolApprovalDecision):
    handled = approval_manager.resolve(decision.approval_id, decision.approved)
    return {"ok": handled}
