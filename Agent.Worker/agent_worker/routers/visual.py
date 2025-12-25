from __future__ import annotations

import logging
from typing import Optional

from fastapi import APIRouter
from pydantic import BaseModel, Field

from agent_worker.services.visual_context import set_latest_frame


router = APIRouter()
logger = logging.getLogger("agent_worker.visual")


class VisualFrameIn(BaseModel):
    session_id: str = Field(..., min_length=1)
    mime_type: str = "image/jpeg"
    data_base64: str = Field(..., min_length=1)
    width: int = 0
    height: int = 0
    timestamp: Optional[float] = None


@router.post("/visual-context/frame")
async def post_frame(payload: VisualFrameIn):
    set_latest_frame(
        payload.session_id,
        mime_type=payload.mime_type,
        data_base64=payload.data_base64,
        width=payload.width,
        height=payload.height,
        timestamp=payload.timestamp,
    )
    size = len(payload.data_base64) if payload.data_base64 else 0
    logger.info(
        "frame received session=%s mime=%s b64=%s %sx%s",
        payload.session_id,
        payload.mime_type,
        size,
        payload.width,
        payload.height,
    )
    return {"ok": True}
