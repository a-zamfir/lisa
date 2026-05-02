import asyncio

from agent_worker.services.tool_approval import ToolApprovalManager


def test_tool_approval_resolve_unblocks_waiter():
    manager = ToolApprovalManager()
    
    async def scenario():
        manager.create("approval-1")

        async def resolver():
            await asyncio.sleep(0)
            assert manager.resolve("approval-1", True) is True

        waiter = asyncio.create_task(manager.wait_for("approval-1", timeout_s=1))
        await asyncio.sleep(0)
        await resolver()
        return await waiter

    assert asyncio.run(scenario()) is True


def test_tool_approval_timeout_returns_false_and_clears_pending():
    manager = ToolApprovalManager()

    async def scenario():
        manager.create("approval-2")
        result = await manager.wait_for("approval-2", timeout_s=0)
        return result

    result = asyncio.run(scenario())
    assert result is False
    assert manager.resolve("approval-2", True) is False
