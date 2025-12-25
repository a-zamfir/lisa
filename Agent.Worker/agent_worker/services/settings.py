from __future__ import annotations

import orjson
import os
from pathlib import Path
from typing import Optional

from pydantic import BaseModel


DEFAULT_PORT = int(os.environ.get("AGENT_PORT", "5050"))
SETTINGS_PATH = Path(
    os.environ.get(
        "LISA_SETTINGS_PATH",
        Path(os.getenv("LOCALAPPDATA", ".")) / "LISA" / "host-settings.json",
    )
)


class ProviderConfig(BaseModel):
    provider_type: str = "LM Studio"
    host: str = "127.0.0.1"
    port: int = 1234
    api_key: Optional[str] = None
    model: str = "mistralai/ministral-3-3b"
    temperature: float = 0.0
    stream: bool = False
    think: bool = True


def load_settings() -> ProviderConfig:
    if SETTINGS_PATH.exists():
        try:
            data = orjson.loads(SETTINGS_PATH.read_text(encoding="utf-8"))

            def pick(*keys, default=None):
                for key in keys:
                    if key in data:
                        return data[key]
                    # Host settings are written by .NET and may be PascalCase; treat keys as case-insensitive.
                    key_l = str(key).lower()
                    for existing in data.keys():
                        try:
                            if str(existing).lower() == key_l:
                                return data[existing]
                        except Exception:
                            continue
                return default

            return ProviderConfig(
                provider_type=pick("provider_type", "providerType", "ProviderType", default="Ollama"),
                host=pick("provider_host", "providerHost", "ProviderHost", default="127.0.0.1"),
                port=int(pick("provider_port", "providerPort", "ProviderPort", default=11434)),
                api_key=os.environ.get("PROVIDER_API_KEY") or pick("provider_api_key", "providerApiKey", "ProviderApiKey") or None,
                model=pick("provider_model", "providerModel", "ProviderModel", default="llama3.2"),
                temperature=float(pick("provider_temperature", "providerTemperature", "ProviderTemperature", default=0.7)),
                think=bool(pick("provider_think", "providerThink", "ProviderThink", default=True)),
            )
        except Exception:
            pass
    return ProviderConfig()
