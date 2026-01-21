from __future__ import annotations

import os
import sqlite3
import threading
import time
from dataclasses import dataclass
from pathlib import Path
from typing import Any, Dict, Iterable, List, Optional, Tuple


def _default_memory_path() -> Path:
    root = Path(os.getenv("LOCALAPPDATA", ".")) / "LISA"
    return root / "memory.db"


def _now_ts() -> float:
    return time.time()


@dataclass(frozen=True)
class MemoryItem:
    key: str
    kind: str
    value: str
    confidence: float
    created_at: float
    updated_at: float


class MemoryStore:
    def __init__(self, db_path: Optional[str] = None) -> None:
        self._path = Path(db_path) if db_path else _default_memory_path()
        self._lock = threading.Lock()
        self._initialized = False
        self._fts_available: Optional[bool] = None

    @property
    def path(self) -> Path:
        return self._path

    def configure_path(self, db_path: Optional[str]) -> None:
        if not db_path:
            return
        new_path = Path(db_path)
        if new_path == self._path:
            return
        with self._lock:
            self._path = new_path
            self._initialized = False
            self._fts_available = None

    def ensure_initialized(self) -> None:
        with self._lock:
            if self._initialized:
                return
            self._path.parent.mkdir(parents=True, exist_ok=True)
            with self._connect() as conn:
                conn.execute(
                    """
                    CREATE TABLE IF NOT EXISTS memories (
                        id INTEGER PRIMARY KEY AUTOINCREMENT,
                        key TEXT NOT NULL UNIQUE,
                        kind TEXT NOT NULL,
                        value TEXT NOT NULL,
                        confidence REAL NOT NULL DEFAULT 0.0,
                        created_at REAL NOT NULL,
                        updated_at REAL NOT NULL,
                        source_session_id TEXT,
                        source_turn_id TEXT
                    );
                    """
                )
                self._fts_available = self._try_init_fts(conn)
                conn.commit()
            self._initialized = True

    def _connect(self) -> sqlite3.Connection:
        conn = sqlite3.connect(str(self._path), timeout=3.0)
        conn.row_factory = sqlite3.Row
        conn.execute("PRAGMA journal_mode=WAL;")
        conn.execute("PRAGMA synchronous=NORMAL;")
        return conn

    def _try_init_fts(self, conn: sqlite3.Connection) -> bool:
        try:
            conn.execute("CREATE VIRTUAL TABLE IF NOT EXISTS memories_fts USING fts5(key, kind, value, memory_id UNINDEXED);")
            conn.execute(
                """
                CREATE TRIGGER IF NOT EXISTS memories_ai AFTER INSERT ON memories BEGIN
                    INSERT INTO memories_fts(rowid, key, kind, value, memory_id)
                    VALUES (new.id, new.key, new.kind, new.value, new.id);
                END;
                """
            )
            conn.execute(
                """
                CREATE TRIGGER IF NOT EXISTS memories_ad AFTER DELETE ON memories BEGIN
                    DELETE FROM memories_fts WHERE rowid = old.id;
                END;
                """
            )
            conn.execute(
                """
                CREATE TRIGGER IF NOT EXISTS memories_au AFTER UPDATE ON memories BEGIN
                    UPDATE memories_fts SET key=new.key, kind=new.kind, value=new.value, memory_id=new.id
                    WHERE rowid = old.id;
                END;
                """
            )
            return True
        except Exception:
            return False

    def upsert(
        self,
        *,
        key: str,
        kind: str,
        value: str,
        confidence: float = 0.0,
        source_session_id: Optional[str] = None,
        source_turn_id: Optional[str] = None,
    ) -> None:
        self.ensure_initialized()
        now = _now_ts()
        with self._lock, self._connect() as conn:
            row = conn.execute("SELECT id, created_at FROM memories WHERE key = ?;", (key,)).fetchone()
            if row is None:
                conn.execute(
                    """
                    INSERT INTO memories (key, kind, value, confidence, created_at, updated_at, source_session_id, source_turn_id)
                    VALUES (?, ?, ?, ?, ?, ?, ?, ?);
                    """,
                    (key, kind, value, float(confidence), now, now, source_session_id, source_turn_id),
                )
            else:
                conn.execute(
                    """
                    UPDATE memories
                    SET kind = ?, value = ?, confidence = ?, updated_at = ?, source_session_id = ?, source_turn_id = ?
                    WHERE key = ?;
                    """,
                    (kind, value, float(confidence), now, source_session_id, source_turn_id, key),
                )
            conn.commit()

    def delete(self, key: str) -> bool:
        self.ensure_initialized()
        with self._lock, self._connect() as conn:
            cur = conn.execute("DELETE FROM memories WHERE key = ?;", (key,))
            conn.commit()
            return cur.rowcount > 0

    def clear_all(self) -> int:
        self.ensure_initialized()
        with self._lock, self._connect() as conn:
            count = conn.execute("SELECT COUNT(1) AS n FROM memories;").fetchone()
            n = int(count["n"] if count is not None else 0)
            conn.execute("DELETE FROM memories;")
            try:
                conn.execute("DELETE FROM memories_fts;")
            except Exception:
                pass
            conn.commit()
            return n

    def export_all(self) -> List[Dict[str, Any]]:
        self.ensure_initialized()
        with self._lock, self._connect() as conn:
            rows = conn.execute(
                "SELECT key, kind, value, confidence, created_at, updated_at FROM memories ORDER BY updated_at DESC;"
            ).fetchall()
        return [
            {
                "key": row["key"],
                "kind": row["kind"],
                "value": row["value"],
                "confidence": float(row["confidence"] or 0.0),
                "created_at": float(row["created_at"] or 0.0),
                "updated_at": float(row["updated_at"] or 0.0),
            }
            for row in rows
        ]

    def list_recent(self, limit: int = 50) -> List[MemoryItem]:
        self.ensure_initialized()
        limit = max(1, min(int(limit), 200))
        with self._lock, self._connect() as conn:
            rows = conn.execute(
                "SELECT key, kind, value, confidence, created_at, updated_at FROM memories ORDER BY updated_at DESC LIMIT ?;",
                (limit,),
            ).fetchall()
        return [
            MemoryItem(
                key=row["key"],
                kind=row["kind"],
                value=row["value"],
                confidence=float(row["confidence"] or 0.0),
                created_at=float(row["created_at"] or 0.0),
                updated_at=float(row["updated_at"] or 0.0),
            )
            for row in rows
        ]

    def search(self, query: str, *, limit: int = 6, kinds: Optional[Iterable[str]] = None) -> List[MemoryItem]:
        self.ensure_initialized()
        q = (query or "").strip()
        if not q:
            return []

        limit = max(1, min(int(limit), 20))
        kinds_list = [k for k in (kinds or []) if k]

        # Lightweight tokenization for both FTS and LIKE fallback.
        cleaned_chars = [(ch if (ch.isalnum() or ch == "_") else " ") for ch in q]
        tokens = [t.lower() for t in "".join(cleaned_chars).split() if len(t) >= 2][:12]
        if not tokens:
            tokens = [q.lower()[:32]] if len(q) >= 2 else []

        with self._lock, self._connect() as conn:
            if self._fts_available:
                safe_query = " OR ".join(tokens)
                sql = (
                    "SELECT m.key, m.kind, m.value, m.confidence, m.created_at, m.updated_at "
                    "FROM memories_fts f JOIN memories m ON m.id = f.rowid "
                    "WHERE f MATCH ? "
                )
                args: List[Any] = [safe_query]
                if kinds_list:
                    placeholders = ",".join(["?"] * len(kinds_list))
                    sql += f" AND m.kind IN ({placeholders}) "
                    args.extend(kinds_list)
                sql += " ORDER BY bm25(f) LIMIT ?;"
                args.append(limit)
                try:
                    rows = conn.execute(sql, tuple(args)).fetchall()
                except Exception:
                    # If FTS query parsing fails, fall back to LIKE.
                    rows = []
            else:
                rows = []

            if not rows:
                # LIKE fallback (also used when FTS exists but returned nothing).
                like_terms = [f"%{t}%" for t in tokens] if tokens else [f"%{q}%"]
                clauses = " OR ".join(["key LIKE ? OR value LIKE ?"] * len(like_terms))
                sql = (
                    "SELECT key, kind, value, confidence, created_at, updated_at FROM memories "
                    f"WHERE ({clauses}) "
                )
                args: List[Any] = []
                for term in like_terms:
                    args.extend([term, term])
                if kinds_list:
                    placeholders = ",".join(["?"] * len(kinds_list))
                    sql += f" AND kind IN ({placeholders}) "
                    args.extend(kinds_list)
                sql += " ORDER BY updated_at DESC LIMIT ?;"
                args.append(limit)
                rows = conn.execute(sql, tuple(args)).fetchall()

        return [
            MemoryItem(
                key=row["key"],
                kind=row["kind"],
                value=row["value"],
                confidence=float(row["confidence"] or 0.0),
                created_at=float(row["created_at"] or 0.0),
                updated_at=float(row["updated_at"] or 0.0),
            )
            for row in rows
        ]
