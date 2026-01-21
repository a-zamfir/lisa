from __future__ import annotations

import re
from dataclasses import dataclass
from typing import Any, Dict, List, Optional, Tuple


_SECRET_KEY_HINT = re.compile(r"(password|passwd|token|api[_-]?key|secret|bearer)", re.IGNORECASE)
_SECRET_VALUE_HINT = re.compile(
    r"(?i)(sk-[a-z0-9]{10,}|bearer\s+[a-z0-9\.\-_]{10,}|-----BEGIN\s+PRIVATE\s+KEY-----)"
)


@dataclass(frozen=True)
class MemoryOp:
    op: str
    key: str
    kind: Optional[str] = None
    value: Optional[str] = None
    confidence: float = 0.0


def parse_ops(payload: Dict[str, Any]) -> List[MemoryOp]:
    ops_raw = payload.get("ops")
    if not isinstance(ops_raw, list):
        return []
    ops: List[MemoryOp] = []
    for entry in ops_raw:
        if not isinstance(entry, dict):
            continue
        op = str(entry.get("op") or "").strip().lower()
        key = str(entry.get("key") or "").strip()
        kind = entry.get("kind")
        value = entry.get("value")
        confidence = entry.get("confidence", 0.0)
        try:
            conf = float(confidence)
        except Exception:
            conf = 0.0
        ops.append(
            MemoryOp(
                op=op,
                key=key,
                kind=str(kind).strip() if isinstance(kind, str) else None,
                value=str(value).strip() if isinstance(value, str) else None,
                confidence=conf,
            )
        )
    return ops


def _normalize_kind(raw_kind: Optional[str]) -> str:
    """
    Memory is stored as string/string. `kind` is optional metadata used only for grouping.
    We keep this permissive to avoid brittle schemas.
    """

    if not raw_kind:
        return "misc"
    kind = str(raw_kind).strip().lower()
    if not kind:
        return "misc"
    if len(kind) > 40:
        return "misc"
    if not re.fullmatch(r"[a-z0-9_\-\.]+", kind):
        return "misc"
    return kind


def _normalize_key(key: str) -> str:
    return str(key or "").strip()


def _normalize_value(value: str) -> str:
    return str(value or "").strip()


def validate_ops(ops: List[MemoryOp]) -> Tuple[List[MemoryOp], List[str]]:
    accepted: List[MemoryOp] = []
    reasons: List[str] = []

    for op in ops:
        if op.op not in {"upsert", "delete"}:
            reasons.append(f"skip op={op.op} key={op.key}: unsupported op")
            continue

        key = _normalize_key(op.key)
        if not key:
            reasons.append("skip: missing key")
            continue
        if len(key) > 120:
            reasons.append(f"skip key={key}: key too long")
            continue
        if "\n" in key or "\r" in key or "\t" in key:
            reasons.append(f"skip key={key}: key contains whitespace control characters")
            continue
        if _SECRET_KEY_HINT.search(key):
            reasons.append(f"skip key={key}: key looks sensitive")
            continue

        if op.op == "delete":
            accepted.append(MemoryOp(op=op.op, key=key, kind=None, value=None, confidence=op.confidence))
            continue

        kind = _normalize_kind(op.kind)
        normalized_value = _normalize_value(op.value or "")

        if not normalized_value:
            reasons.append(f"skip key={key}: missing value")
            continue
        if len(normalized_value) > 800:
            reasons.append(f"skip key={key}: value too long")
            continue
        if _SECRET_VALUE_HINT.search(normalized_value):
            reasons.append(f"skip key={key}: value looks sensitive")
            continue

        accepted.append(
            MemoryOp(
                op=op.op,
                key=key,
                kind=kind,
                value=normalized_value,
                confidence=op.confidence,
            )
        )

    return accepted, reasons
