from agent_worker.services.provider import (
    BaseProvider,
    LmStudioProvider,
    OllamaProvider,
    OpenAiProvider,
    _extract_content,
    _get_provider,
    _merge_tool_calls,
    _tool_calls_ready,
)
from agent_worker.services.settings import ProviderConfig


def test_get_provider_maps_known_types():
    assert isinstance(_get_provider(ProviderConfig(provider_type="OpenAI")), OpenAiProvider)
    assert isinstance(_get_provider(ProviderConfig(provider_type="LM Studio")), LmStudioProvider)
    assert isinstance(_get_provider(ProviderConfig(provider_type="Ollama")), OllamaProvider)


def test_openai_provider_adds_authorization_header():
    provider = OpenAiProvider(ProviderConfig(provider_type="OpenAI", api_key="secret"))

    headers = provider.build_headers()

    assert headers["Authorization"] == "Bearer secret"
    assert headers["Content-Type"] == "application/json"


def test_base_provider_build_payload_includes_tools_when_present():
    provider = BaseProvider(ProviderConfig(model="test-model"))

    payload = provider.build_payload(
        [{"role": "user", "content": "hi"}],
        [{"function": {"name": "tool"}}],
        stream=True,
    )

    assert payload["model"] == "test-model"
    assert payload["stream"] is True
    assert payload["tool_choice"] == "auto"


def test_extract_content_prefers_message_content_and_choices():
    assert _extract_content({"message": {"content": "hello"}}) == "hello"
    assert _extract_content({"choices": [{"message": {"content": "world"}}]}) == "world"


def test_merge_tool_calls_concatenates_partial_arguments():
    merged = _merge_tool_calls(
        [],
        [
            {"function": {"name": "tool", "arguments": '{"a":'}},
            {"function": {"name": "tool2", "arguments": "{}"}},
        ],
    )
    merged = _merge_tool_calls(
        merged,
        [
            {"function": {"arguments": '"b"}'}},
        ],
    )

    assert merged[0]["name"] == "tool"
    assert merged[0]["arguments"] == '{"a":"b"}'
    assert merged[1]["name"] == "tool2"


def test_tool_calls_ready_requires_complete_json_or_dict():
    assert _tool_calls_ready([]) is False
    assert _tool_calls_ready([{"name": "tool", "arguments": '{"x":1}'}]) is True
    assert _tool_calls_ready([{"name": "tool", "arguments": {"x": 1}}]) is True
    assert _tool_calls_ready([{"name": "tool", "arguments": '{"x":'}]) is False
