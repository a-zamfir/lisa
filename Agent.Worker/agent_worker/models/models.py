from __future__ import annotations

from typing import List, Optional

from pydantic import BaseModel, Field


class InputMeta(BaseModel):
    active_app: Optional[str] = None
    window_title: Optional[str] = None
    clipboard: Optional[str] = None


class TextInput(BaseModel):
    session_id: str
    turn_id: str
    text: str
    input_meta: Optional[InputMeta] = None


class AgentMessage(BaseModel):
    role: str = "assistant"
    content: str


class AgentResponse(BaseModel):
    session_id: str
    turn_id: str
    messages: List[AgentMessage] = Field(default_factory=list)
    speak: bool = False
    tts_text: Optional[str] = None
    tts_audio_b64: Optional[str] = None
    tool_calls: Optional[List[str]] = None


class RetryInput(BaseModel):
    session_id: str
    turn_id: str
