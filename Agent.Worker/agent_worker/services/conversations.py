from __future__ import annotations

from typing import Dict, List, Optional


class ConversationStore:
    def __init__(self, max_tokens: int = 4000, token_ratio: int = 4, max_messages: int = 200) -> None:
        self._conversations: Dict[str, List[dict]] = {}
        self._system_messages: Dict[str, List[dict]] = {}
        self._tool_versions: Dict[str, int] = {}
        self._max_chars = max_tokens * token_ratio
        self._max_messages = max_messages

    def get_history(self, session_id: str) -> List[dict]:
        return self._conversations.setdefault(session_id, [])

    def get_all(self, session_id: str) -> List[dict]:
        system = self._system_messages.get(session_id, [])
        history = self._conversations.setdefault(session_id, [])
        return list(system) + list(history)

    def ensure_system(
        self,
        session_id: str,
        system_content: str,
        tool_content: Optional[str] = None,
        tool_version: int = 0,
    ) -> None:
        existing = self._system_messages.get(session_id)
        if existing is None or self._tool_versions.get(session_id) != tool_version:
            messages = [{"role": "system", "content": system_content}]
            if tool_content:
                messages.append({"role": "system", "content": tool_content})
            self._system_messages[session_id] = messages
            self._tool_versions[session_id] = tool_version

    def append(self, session_id: str, role: str, content: str) -> None:
        history = self._conversations.setdefault(session_id, [])
        history.append({"role": role, "content": content})
        self._trim_history(history)

    def _trim_history(self, history: List[dict]) -> None:
        while len(history) > self._max_messages:
            history.pop(0)
        total_chars = 0
        for message in history:
            total_chars += len(str(message.get("content", "")))
        while total_chars > self._max_chars and history:
            removed = history.pop(0)
            total_chars -= len(str(removed.get("content", "")))
