from __future__ import annotations

import uuid
from typing import Any, Dict

import orjson
from fastapi import APIRouter, File, Form, UploadFile
from fastapi.responses import ORJSONResponse
from starlette.concurrency import run_in_threadpool

from agent_worker.services.session_state import set_session_nonce
from agent_worker.services.stt import transcribe_audio


router = APIRouter()


@router.post("/input/audio")
async def handle_audio(audio: UploadFile = File(...), meta: str = Form(...)) -> ORJSONResponse:
    meta_payload: Dict[str, Any] = {}
    try:
        meta_payload = orjson.loads(meta) if meta else {}
    except orjson.JSONDecodeError:
        meta_payload = {}

    session_id = meta_payload.get("session_id") or str(uuid.uuid4())
    session_nonce = meta_payload.get("session_nonce")
    if session_nonce:
        set_session_nonce(session_id, session_nonce)
    audio_bytes = await audio.read()
    print(f"[audio] request session={session_id} bytes={len(audio_bytes)}")
    if not audio_bytes:
        payload = {
            "session_id": session_id,
            "transcript": "",
            "assistant_message": "Got it.",
            "stt_ms": 0,
        }
        return ORJSONResponse(payload)

    try:
        transcript, _decode_ms, stt_ms, device, compute = await run_in_threadpool(transcribe_audio, audio_bytes)
        print(f"[audio] done session={session_id} transcript_len={len(transcript)} stt_ms={stt_ms}")
        payload = {
            "session_id": session_id,
            "transcript": transcript,
            "assistant_message": "Got it.",
            "stt_ms": stt_ms,
            "stt_device": device,
            "stt_compute": compute,
        }
        return ORJSONResponse(payload)
    except Exception as exc:
        print(f"[audio] error session={session_id} {exc}")
        payload = {
            "session_id": session_id,
            "transcript": "",
            "assistant_message": f"(stt unavailable) {exc}",
            "stt_ms": 0,
        }
        return ORJSONResponse(payload)
