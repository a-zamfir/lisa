from __future__ import annotations

import asyncio
from typing import Any, Dict, List, Optional

from fastapi import APIRouter
from pydantic import BaseModel

from agent_worker.services.memory_store import MemoryStore
from agent_worker.services.settings import load_settings


router = APIRouter(prefix="/memory", tags=["memory"])
_store = MemoryStore()


class MemorySearchRequest(BaseModel):
    query: str
    limit: int = 6
    kinds: Optional[List[str]] = None


class MemoryDeleteRequest(BaseModel):
    key: str


@router.get("/recent")
async def recent(limit: int = 50) -> Dict[str, Any]:
    cfg = load_settings()
    if not cfg.memory_enabled:
        return {"enabled": False, "items": []}
    _store.configure_path(cfg.memory_path)
    items = await asyncio.to_thread(_store.list_recent, limit)
    return {"enabled": True, "items": [item.__dict__ for item in items]}


@router.post("/search")
async def search(req: MemorySearchRequest) -> Dict[str, Any]:
    cfg = load_settings()
    if not cfg.memory_enabled:
        return {"enabled": False, "items": []}
    _store.configure_path(cfg.memory_path)
    items = await asyncio.to_thread(_store.search, req.query, limit=req.limit, kinds=req.kinds)
    return {"enabled": True, "items": [item.__dict__ for item in items]}


@router.post("/delete")
async def delete(req: MemoryDeleteRequest) -> Dict[str, Any]:
    cfg = load_settings()
    if not cfg.memory_enabled:
        return {"enabled": False, "deleted": False}
    _store.configure_path(cfg.memory_path)
    deleted = await asyncio.to_thread(_store.delete, req.key)
    return {"enabled": True, "deleted": deleted}


@router.post("/clear")
async def clear() -> Dict[str, Any]:
    cfg = load_settings()
    if not cfg.memory_enabled:
        return {"enabled": False, "cleared": 0}
    _store.configure_path(cfg.memory_path)
    cleared = await asyncio.to_thread(_store.clear_all)
    return {"enabled": True, "cleared": cleared}


@router.get("/export")
async def export() -> Dict[str, Any]:
    cfg = load_settings()
    if not cfg.memory_enabled:
        return {"enabled": False, "items": []}
    _store.configure_path(cfg.memory_path)
    items = await asyncio.to_thread(_store.export_all)
    return {"enabled": True, "items": items}

