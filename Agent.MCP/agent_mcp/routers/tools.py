from __future__ import annotations

from typing import Any, Dict

import os

from fastapi import APIRouter, Header, HTTPException, status
from fastapi.concurrency import run_in_threadpool

from agent_mcp.models import ToolDocsResponse
from agent_mcp.services.tools_registry import TOOL_MAP, TOOL_REGISTRY, get_tool_docs

router = APIRouter()


def _require_token(token: str | None) -> None:
    expected = os.environ.get("MCP_AUTH_TOKEN", "")
    if not expected or token != expected:
        raise HTTPException(status_code=status.HTTP_401_UNAUTHORIZED, detail="Unauthorized")


@router.get("/tools")
def list_tools(x_mcp_token: str | None = Header(default=None)) -> Dict[str, Any]:
    _require_token(x_mcp_token)
    minimal = [{"name": tool["name"], "description": tool["description"]} for tool in TOOL_REGISTRY]
    return {"tools": minimal, "read_only": True}


@router.get("/tools/docs", response_model=ToolDocsResponse)
def list_tool_docs(x_mcp_token: str | None = Header(default=None)) -> ToolDocsResponse:
    _require_token(x_mcp_token)
    return ToolDocsResponse(tools=get_tool_docs(), read_only=True)


@router.post("/call")
async def call_tool(payload: Dict[str, Any], x_mcp_token: str | None = Header(default=None)) -> Dict[str, Any]:
    _require_token(x_mcp_token)
    tool_name = payload.get("tool")
    args = payload.get("args") or {}
    if tool_name not in TOOL_MAP:
        return {"error": f"Unknown tool: {tool_name}"}
    try:
        result = await run_in_threadpool(TOOL_MAP[tool_name], **args)
        return {"tool": tool_name, "result": result}
    except Exception as ex:
        return {"error": str(ex)}
