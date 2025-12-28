from __future__ import annotations

from typing import Any, Dict

import asyncio
import hmac
import os

from fastapi import APIRouter, Header, HTTPException, status
from fastapi.concurrency import run_in_threadpool

from agent_mcp.models import ToolDocsResponse
from agent_mcp.services.tools_registry import TOOL_MAP, TOOL_REGISTRY, get_tool_docs

router = APIRouter()

# Rate limiting: prevent resource exhaustion from concurrent tool calls
_GLOBAL_TOOL_SEMAPHORE = asyncio.Semaphore(5)  # Max 5 concurrent tool executions


def _require_token(token: str | None) -> None:
    expected = os.environ.get("MCP_AUTH_TOKEN", "")
    # Use constant-time comparison to prevent timing attacks
    if not expected or not hmac.compare_digest(token or "", expected):
        raise HTTPException(status_code=status.HTTP_401_UNAUTHORIZED, detail="Unauthorized")


@router.get("/health")
def health() -> Dict[str, Any]:
    """Health check endpoint - no auth required for monitoring."""
    from datetime import datetime
    return {
        "status": "healthy",
        "timestamp": datetime.utcnow().isoformat(),
        "service": "mcp"
    }


@router.get("/ready")
def ready(x_mcp_token: str | None = Header(default=None)) -> Dict[str, Any]:
    """Readiness check - verifies dependencies are available."""
    _require_token(x_mcp_token)
    # Check if tools are loaded
    tools_available = len(TOOL_REGISTRY) > 0
    return {
        "status": "ready" if tools_available else "not_ready",
        "dependencies": {
            "tools_loaded": tools_available,
            "tool_count": len(TOOL_REGISTRY)
        }
    }


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

    # Rate limiting: check if semaphore is available
    if _GLOBAL_TOOL_SEMAPHORE.locked() and _GLOBAL_TOOL_SEMAPHORE._value == 0:
        raise HTTPException(
            status_code=status.HTTP_429_TOO_MANY_REQUESTS,
            detail="Too many concurrent tool calls. Please try again in a moment."
        )

    async with _GLOBAL_TOOL_SEMAPHORE:
        tool_name = payload.get("tool")
        args = payload.get("args") or {}
        if tool_name not in TOOL_MAP:
            return {"error": f"Unknown tool: {tool_name}"}
        try:
            result = await run_in_threadpool(TOOL_MAP[tool_name], **args)
            return {"tool": tool_name, "result": result}
        except Exception as ex:
            return {"error": str(ex)}
