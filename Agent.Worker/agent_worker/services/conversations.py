from __future__ import annotations

from collections import deque
from typing import Dict, Deque, List, Optional


class ConversationStore:
    def __init__(self, max_tokens: int = 4000, token_ratio: int = 4, max_messages: int = 200) -> None:
        self._conversations: Dict[str, Deque[dict]] = {}
        self._char_counts: Dict[str, int] = {}
        self._system_messages: Dict[str, List[dict]] = {}
        self._tool_versions: Dict[str, int] = {}
        self._max_chars = max_tokens * token_ratio
        self._max_messages = max_messages

    def get_history(self, session_id: str) -> List[dict]:
        history = self._conversations.setdefault(session_id, deque())
        return list(history)

    def get_all(self, session_id: str) -> List[dict]:
        system = self._system_messages.get(session_id, [])
        history = self._conversations.setdefault(session_id, deque())
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
            # Only include system content - tools are passed separately in the tools[] array
            # Adding tool_content here would duplicate tokens unnecessarily
            messages = [{"role": "system", "content": system_content}]
            self._system_messages[session_id] = messages
            self._tool_versions[session_id] = tool_version

    def append(self, session_id: str, role: str, content: str) -> None:
        history = self._conversations.setdefault(session_id, deque())
        char_count = self._char_counts.setdefault(session_id, 0)

        # Add new message
        message = {"role": role, "content": content}
        message_len = len(str(content))
        history.append(message)
        char_count += message_len

        # Trim by message count (FIFO)
        while len(history) > self._max_messages:
            removed = history.popleft()
            char_count -= len(str(removed.get("content", "")))

        # Trim by character count (FIFO)
        while char_count > self._max_chars and history:
            removed = history.popleft()
            char_count -= len(str(removed.get("content", "")))

        # Update stored character count
        self._char_counts[session_id] = char_count
