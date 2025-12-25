from __future__ import annotations

import asyncio
from typing import Dict


class ToolApprovalManager:
    def __init__(self) -> None:
        self._pending: Dict[str, asyncio.Future[bool]] = {}

    def create(self, approval_id: str) -> asyncio.Future[bool]:
        loop = asyncio.get_running_loop()
        future: asyncio.Future[bool] = loop.create_future()
        self._pending[approval_id] = future
        return future

    def resolve(self, approval_id: str, approved: bool) -> bool:
        future = self._pending.pop(approval_id, None)
        if future is None or future.done():
            return False
        future.set_result(approved)
        return True

    async def wait_for(self, approval_id: str, timeout_s: int) -> bool:
        future = self._pending.get(approval_id)
        if future is None:
            return False
        try:
            return await asyncio.wait_for(future, timeout=timeout_s)
        except asyncio.TimeoutError:
            self._pending.pop(approval_id, None)
            return False


approval_manager = ToolApprovalManager()
