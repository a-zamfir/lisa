from __future__ import annotations

from typing import Dict, Optional

_session_nonces: Dict[str, str] = {}


def set_session_nonce(session_id: str, nonce: Optional[str]) -> None:
    if not session_id or not nonce:
        return
    _session_nonces[session_id] = nonce


def get_session_nonce(session_id: str) -> Optional[str]:
    if not session_id:
        return None
    return _session_nonces.get(session_id)
