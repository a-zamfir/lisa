from __future__ import annotations

from dataclasses import dataclass
from typing import Any, Dict, Optional, Tuple

import orjson


START_TAG = "<lisa_memory>"
END_TAG = "</lisa_memory>"


def extract_memory_trailer(text: str) -> Tuple[str, Optional[Dict[str, Any]]]:
    if not text:
        return text, None

    start = text.rfind(START_TAG)
    if start == -1:
        return text, None

    end = text.find(END_TAG, start + len(START_TAG))
    payload_end = end if end != -1 else len(text)

    payload_text = text[start + len(START_TAG) : payload_end].strip()
    cleaned = (text[:start] + (text[end + len(END_TAG) :] if end != -1 else "")).rstrip()
    if not payload_text:
        return cleaned, None

    try:
        payload = orjson.loads(payload_text)
    except Exception:
        return cleaned, None

    if not isinstance(payload, dict):
        return cleaned, None

    return cleaned, payload


@dataclass
class StreamTrailerFilter:
    """
    Filters a streamed text sequence to hide an optional <lisa_memory>...</lisa_memory> trailer.

    This is designed to be safe during streaming: it never infinite-loops and hides the trailer
    as soon as the start tag is seen. If the trailer is incomplete (missing end tag), it is still
    hidden and ignored rather than shown to the user.
    """

    _carry: str = ""
    _in_trailer: bool = False
    _trailer_buf: str = ""

    def feed(self, delta: str) -> str:
        if not delta:
            return ""

        if self._in_trailer:
            self._trailer_buf += delta
            return ""

        buf = self._carry + delta
        start_idx = buf.find(START_TAG)
        if start_idx != -1:
            out = buf[:start_idx]
            self._trailer_buf = buf[start_idx:]
            self._carry = ""
            self._in_trailer = True
            return out

        hold = max(1, len(START_TAG) - 1)
        if len(buf) <= hold:
            self._carry = buf
            return ""

        out = buf[:-hold]
        self._carry = buf[-hold:]
        return out

    def finalize(self) -> Tuple[str, Optional[Dict[str, Any]]]:
        if not self._in_trailer:
            tail = self._carry
            self._carry = ""
            return tail, None

        combined = self._trailer_buf
        full_text = self._carry + combined
        cleaned, payload = extract_memory_trailer(full_text)
        self._carry = ""
        self._trailer_buf = ""
        self._in_trailer = False
        # Everything after START_TAG is hidden from streaming output; only emit any non-trailer tail.
        return cleaned, payload
