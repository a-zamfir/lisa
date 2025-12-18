from __future__ import annotations

from typing import Dict, List


class ConversationStore:
    def __init__(self) -> None:
        self._conversations: Dict[str, List[dict]] = {}

    def get(self, session_id: str) -> List[dict]:
        return self._conversations.setdefault(session_id, [])

    def append(self, session_id: str, role: str, content: str) -> None:
        history = self._conversations.setdefault(session_id, [])
        history.append({"role": role, "content": content})
