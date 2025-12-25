from __future__ import annotations

import threading
import time
from dataclasses import dataclass
from typing import Dict, Optional


@dataclass(frozen=True)
class VisualFrame:
    mime_type: str
    data_base64: str
    width: int
    height: int
    timestamp: float


_lock = threading.Lock()
_latest_by_session: Dict[str, VisualFrame] = {}


def set_latest_frame(
    session_id: str,
    *,
    mime_type: str,
    data_base64: str,
    width: int,
    height: int,
    timestamp: Optional[float] = None,
) -> None:
    if not session_id:
        return
    ts = time.time() if timestamp is None else float(timestamp)
    frame = VisualFrame(
        mime_type=mime_type or "image/jpeg",
        data_base64=data_base64,
        width=int(width or 0),
        height=int(height or 0),
        timestamp=ts,
    )
    with _lock:
        _latest_by_session[session_id] = frame


def pop_latest_frame(session_id: str) -> Optional[VisualFrame]:
    if not session_id:
        return None
    with _lock:
        return _latest_by_session.pop(session_id, None)


def peek_latest_frame(session_id: str) -> Optional[VisualFrame]:
    if not session_id:
        return None
    with _lock:
        return _latest_by_session.get(session_id)

