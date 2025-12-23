from __future__ import annotations

import orjson
from typing import Any, Callable, Dict, List, Optional

import httpx

from .settings import ProviderConfig, load_settings


_shared_client: Optional[httpx.AsyncClient] = None


class BaseProvider:
    def __init__(self, cfg: ProviderConfig) -> None:
        self.cfg = cfg

    @property
    def is_sse(self) -> bool:
        return False

    def endpoint_path(self) -> str:
        return "/api/chat"

    def build_url(self) -> str:
        return f"http://{self.cfg.host}:{self.cfg.port}{self.endpoint_path()}"

    def build_headers(self) -> Dict[str, str]:
        return {"Content-Type": "application/json"}

    def build_payload(self, messages: List[dict], tools: Optional[List[Dict[str, Any]]], stream: bool) -> Dict[str, Any]:
        payload: Dict[str, Any] = {
            "model": self.cfg.model,
            "messages": messages,
            "stream": stream,
        }
        if tools:
            payload["tools"] = tools
            payload["tool_choice"] = "auto"
        return payload


class OllamaProvider(BaseProvider):
    def endpoint_path(self) -> str:
        return "/api/chat"

    def build_payload(self, messages: List[dict], tools: Optional[List[Dict[str, Any]]], stream: bool) -> Dict[str, Any]:
        payload: Dict[str, Any] = {
            "model": self.cfg.model,
            "messages": messages,
            "options": {"temperature": self.cfg.temperature},
            "stream": stream,
            "think": self.cfg.think,
        }
        if tools:
            payload["tools"] = tools
            payload["tool_choice"] = "auto"
        return payload


class LmStudioProvider(BaseProvider):
    @property
    def is_sse(self) -> bool:
        return True

    def endpoint_path(self) -> str:
        return "/v1/chat/completions"

    def build_payload(self, messages: List[dict], tools: Optional[List[Dict[str, Any]]], stream: bool) -> Dict[str, Any]:
        payload: Dict[str, Any] = {
            "model": self.cfg.model,
            "messages": messages,
            "temperature": self.cfg.temperature,
            "stream": stream,
        }
        if tools:
            payload["tools"] = tools
            payload["tool_choice"] = "auto"
        return payload


class OpenAiProvider(LmStudioProvider):
    def build_headers(self) -> Dict[str, str]:
        headers = super().build_headers()
        if self.cfg.api_key:
            headers["Authorization"] = f"Bearer {self.cfg.api_key}"
        return headers


def _get_provider(cfg: ProviderConfig) -> BaseProvider:
    kind = (cfg.provider_type or "ollama").strip().lower()
    if kind in {"lmstudio", "lm studio", "lm-studio"}:
        return LmStudioProvider(cfg)
    if kind in {"openai", "open ai"}:
        return OpenAiProvider(cfg)
    return OllamaProvider(cfg)


def _get_client() -> httpx.AsyncClient:
    global _shared_client
    if _shared_client is None:
        limits = httpx.Limits(max_keepalive_connections=10, max_connections=20)
        _shared_client = httpx.AsyncClient(limits=limits)
    return _shared_client


async def warm_provider_client() -> None:
    _get_client()


def _extract_content(data: object) -> str:
    if isinstance(data, dict):
        if "response" in data and data["response"]:
            return str(data["response"]).strip()
        if "message" in data and isinstance(data["message"], dict):
            content = data["message"].get("content")
            if content:
                return str(content).strip()
        if "choices" in data and isinstance(data["choices"], list):
            parts = []
            for choice in data["choices"]:
                msg = choice.get("message", {})
                if isinstance(msg, dict):
                    content = msg.get("content")
                    if content:
                        parts.append(str(content).strip())
            if parts:
                return "\n".join(parts)
        for value in data.values():
            if isinstance(value, str) and value.strip():
                return value.strip()
        return orjson.dumps(data).decode("utf-8")
    if isinstance(data, list):
        parts = [str(item).strip() for item in data if str(item).strip()]
        return "\n".join(parts) if parts else ""
    return str(data).strip()


def _extract_tool_calls(data: object) -> List[Dict[str, Any]]:
    if isinstance(data, dict):
        if "message" in data and isinstance(data["message"], dict):
            msg = data["message"]
            tool_calls = msg.get("tool_calls")
            if isinstance(tool_calls, list) and tool_calls:
                return tool_calls
            function_call = msg.get("function_call")
            if isinstance(function_call, dict):
                return [{"function": function_call}]
        if "choices" in data and isinstance(data["choices"], list) and data["choices"]:
            msg = data["choices"][0].get("message", {})
            tool_calls = msg.get("tool_calls")
            if isinstance(tool_calls, list) and tool_calls:
                return tool_calls
            function_call = msg.get("function_call")
            if isinstance(function_call, dict):
                return [{"function": function_call}]
    return []


def _extract_thinking(data: object) -> str:
    if isinstance(data, dict):
        message = data.get("message")
        if isinstance(message, dict):
            thinking = message.get("thinking")
            if thinking:
                return str(thinking)
        if "thinking" in data and data["thinking"]:
            return str(data["thinking"])
        choices = data.get("choices")
        if isinstance(choices, list) and choices:
            msg = choices[0].get("message", {})
            if isinstance(msg, dict):
                thinking = msg.get("thinking")
                if thinking:
                    return str(thinking)
    return ""


def _extract_stream_content(data: object) -> str:
    if isinstance(data, dict):
        message = data.get("message")
        if isinstance(message, dict):
            content = message.get("content")
            if content:
                return str(content)
        choices = data.get("choices")
        if isinstance(choices, list) and choices:
            delta = choices[0].get("delta", {})
            if isinstance(delta, dict):
                content = delta.get("content")
                if content:
                    return str(content)
    return ""


def _extract_stream_thinking(data: object) -> str:
    if isinstance(data, dict):
        message = data.get("message")
        if isinstance(message, dict):
            thinking = message.get("thinking")
            if thinking:
                return str(thinking)
        choices = data.get("choices")
        if isinstance(choices, list) and choices:
            delta = choices[0].get("delta", {})
            if isinstance(delta, dict):
                thinking = delta.get("thinking")
                if thinking:
                    return str(thinking)
    return ""


def _extract_stream_tool_calls(data: object) -> List[Dict[str, Any]]:
    if isinstance(data, dict):
        message = data.get("message")
        if isinstance(message, dict):
            tool_calls = message.get("tool_calls")
            if isinstance(tool_calls, list) and tool_calls:
                return tool_calls
        choices = data.get("choices")
        if isinstance(choices, list) and choices:
            delta = choices[0].get("delta", {})
            if isinstance(delta, dict):
                tool_calls = delta.get("tool_calls")
                if isinstance(tool_calls, list) and tool_calls:
                    return tool_calls
    return []


def _merge_tool_calls(state: List[Dict[str, Any]], incoming: List[Dict[str, Any]]) -> List[Dict[str, Any]]:
    merged = list(state)
    for idx, call in enumerate(incoming):
        while len(merged) <= idx:
            merged.append({})
        current = merged[idx]
        func = call.get("function") or {}
        if "name" in func:
            current["name"] = func.get("name")
        if "arguments" in func:
            args = func.get("arguments")
            if isinstance(args, str):
                prev = current.get("arguments", "")
                current["arguments"] = f"{prev}{args}"
            else:
                current["arguments"] = args
        if "name" in call and "name" not in current:
            current["name"] = call.get("name")
        if "args" in call and "arguments" not in current:
            current["arguments"] = call.get("args")
        merged[idx] = current
    return merged


def _tool_calls_ready(tool_calls: List[Dict[str, Any]]) -> bool:
    if not tool_calls:
        return False
    for call in tool_calls:
        name = call.get("name")
        args = call.get("arguments")
        if not name:
            continue
        if isinstance(args, dict):
            return True
        if isinstance(args, str):
            try:
                orjson.loads(args)
                return True
            except orjson.JSONDecodeError:
                continue
        if args is None:
            return True
    return False


async def call_provider(messages: List[dict], tools: Optional[List[Dict[str, Any]]] = None, attempts: int = 3) -> Dict[str, Any]:
    provider_cfg: ProviderConfig = load_settings()
    provider = _get_provider(provider_cfg)
    payload = provider.build_payload(messages, tools, stream=False)

    last_error: Optional[str] = None
    timeout = httpx.Timeout(60.0, connect=10.0)
    for _ in range(attempts):
        try:
            client = _get_client()
            url = provider.build_url()
            headers = provider.build_headers()
            resp = await client.post(
                url,
                content=orjson.dumps(payload),
                headers=headers,
                timeout=timeout,
            )
            resp.raise_for_status()
            raw_text = resp.content.decode(resp.encoding or "utf-8", errors="ignore")
            try:
                data = orjson.loads(resp.content)
            except Exception:
                data = raw_text
            tool_calls = _extract_tool_calls(data)
            content = _extract_content(data)
            thinking = _extract_thinking(data)
            if content or tool_calls or thinking:
                return {
                    "content": content,
                    "tool_calls": tool_calls,
                    "thinking": thinking,
                    "raw": data,
                }
            last_error = f"(provider returned empty) raw={raw_text}"
        except httpx.HTTPError as ex:
            response = getattr(ex, "response", None)
            detail = response.text if response is not None else str(ex)
            last_error = f"(provider http error) {detail}"
        except Exception as ex:
            last_error = f"(provider error) {ex}"

    return {"content": "", "tool_calls": [], "thinking": "", "error": last_error or "(provider unavailable)"}


async def call_provider_stream(
    messages: List[dict],
    tools: Optional[List[Dict[str, Any]]] = None,
    on_thinking_chunk: Optional[Callable[[str], None]] = None,
    on_content_chunk: Optional[Callable[[str], None]] = None,
) -> Dict[str, Any]:
    provider_cfg: ProviderConfig = load_settings()
    provider = _get_provider(provider_cfg)
    payload = provider.build_payload(messages, tools, stream=True)
    timeout = httpx.Timeout(60.0, connect=10.0)
    url = provider.build_url()
    headers = provider.build_headers()

    content_parts: List[str] = []
    tool_calls: List[Dict[str, Any]] = []
    thinking_parts: List[str] = []
    try:
        client = _get_client()
        async with client.stream(
            "POST",
            url,
            content=orjson.dumps(payload),
            headers=headers,
            timeout=timeout,
        ) as resp:
            resp.raise_for_status()
            async for line in resp.aiter_lines():
                if not line:
                    continue
                if provider.is_sse:
                    if not line.startswith("data:"):
                        continue
                    line = line[5:].strip()
                    if not line or line == "[DONE]":
                        break
                try:
                    data = orjson.loads(line)
                except orjson.JSONDecodeError:
                    continue
                chunk = _extract_stream_content(data)
                if chunk:
                    content_parts.append(chunk)
                    if on_content_chunk is not None:
                        on_content_chunk(chunk)
                thinking_chunk = _extract_stream_thinking(data)
                if thinking_chunk:
                    thinking_parts.append(thinking_chunk)
                    if on_thinking_chunk is not None:
                        on_thinking_chunk(thinking_chunk)
                incoming = _extract_stream_tool_calls(data)
                if incoming:
                    tool_calls = _merge_tool_calls(tool_calls, incoming)
                    if _tool_calls_ready(tool_calls):
                        break
                if isinstance(data, dict) and data.get("done") is True:
                    break
    except httpx.HTTPError as ex:
        response = getattr(ex, "response", None)
        detail = response.text if response is not None else str(ex)
        return {"content": "", "tool_calls": [], "error": f"(provider http error) {detail}"}
    except Exception as ex:
        return {"content": "", "tool_calls": [], "error": f"(provider error) {ex}"}

    return {
        "content": "".join(content_parts).strip(),
        "tool_calls": tool_calls,
        "thinking": "".join(thinking_parts).strip(),
    }
