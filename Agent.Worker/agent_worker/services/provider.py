from __future__ import annotations

import json
from typing import Any, Dict, List, Optional

import httpx

from .settings import ProviderConfig, load_settings


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
        return json.dumps(data, ensure_ascii=False)
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


async def call_provider(messages: List[dict], tools: Optional[List[Dict[str, Any]]] = None, attempts: int = 3) -> Dict[str, Any]:
    provider_cfg: ProviderConfig = load_settings()
    payload = {
        "model": provider_cfg.model,
        "messages": messages,
        "options": {
            "temperature": provider_cfg.temperature,
        },
        "stream": provider_cfg.stream,
    }
    if tools:
        payload["tools"] = tools

    last_error: Optional[str] = None
    timeout = httpx.Timeout(60.0, connect=10.0)
    for _ in range(attempts):
        try:
            async with httpx.AsyncClient(timeout=timeout) as client:
                url = f"http://{provider_cfg.host}:{provider_cfg.port}/api/chat"
                headers = {}
                if provider_cfg.api_key:
                    headers["X-Provider-Api-Key"] = provider_cfg.api_key
                resp = await client.post(url, json=payload, headers=headers)
                resp.raise_for_status()
                raw_text = resp.content.decode(resp.encoding or "utf-8", errors="ignore")
                try:
                    data = resp.json()
                except Exception:
                    data = raw_text
                tool_calls = _extract_tool_calls(data)
                content = _extract_content(data)
                if content or tool_calls:
                    return {
                        "content": content,
                        "tool_calls": tool_calls,
                        "raw": data,
                    }
                last_error = f"(provider returned empty) raw={raw_text}"
        except httpx.HTTPError as ex:
            response = getattr(ex, "response", None)
            detail = response.text if response is not None else str(ex)
            last_error = f"(provider http error) {detail}"
        except Exception as ex:
            last_error = f"(provider error) {ex}"

    return {"content": "", "tool_calls": [], "error": last_error or "(provider unavailable)"}
