from __future__ import annotations

from typing import Any, Dict, List, Optional

from pydantic import BaseModel, Field


class ToolDoc(BaseModel):
    name: str
    description: str
    friendly_desc: Optional[str] = None
    approval_required: bool = False
    category: Optional[str] = None
    args: Dict[str, Any] = Field(default_factory=dict)


class ToolDocsResponse(BaseModel):
    tools: List[ToolDoc]
    read_only: bool = True
