from __future__ import annotations

from typing import Any, Dict

from fastapi import APIRouter
from fastapi.concurrency import run_in_threadpool

from agent_mcp.models import ToolDocsResponse
from agent_mcp.services.tools_registry import TOOL_MAP, TOOL_REGISTRY, get_tool_docs

router = APIRouter()


@router.get("/tools")
def list_tools() -> Dict[str, Any]:
    minimal = [{"name": tool["name"], "description": tool["description"]} for tool in TOOL_REGISTRY]
    return {"tools": minimal, "read_only": True}


@router.get("/tools/docs", response_model=ToolDocsResponse)
def list_tool_docs() -> ToolDocsResponse:
    return ToolDocsResponse(tools=get_tool_docs(), read_only=True)


@router.post("/call")
async def call_tool(payload: Dict[str, Any]) -> Dict[str, Any]:
    tool_name = payload.get("tool")
    args = payload.get("args") or {}
    if tool_name not in TOOL_MAP:
        return {"error": f"Unknown tool: {tool_name}"}
    try:
        result = await run_in_threadpool(TOOL_MAP[tool_name], **args)
        return {"tool": tool_name, "result": result}
    except Exception as ex:
        return {"error": str(ex)}
