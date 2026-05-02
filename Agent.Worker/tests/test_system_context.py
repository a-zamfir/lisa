from agent_worker.services import system_context


def test_collect_system_context_contains_expected_sections():
    context = system_context.collect_system_context()

    assert "System context:" in context
    assert "- Current time:" in context
    assert "- User's name:" in context
    assert "- Machine:" in context
    assert "- OS:" in context
    assert "- Locale:" in context
    assert "- Home:" in context


def test_collect_system_context_includes_base_prompt_when_present(monkeypatch):
    monkeypatch.setattr(system_context, "_BASE_PROMPT", "Prompt header")

    context = system_context.collect_system_context()

    assert context.startswith("Prompt header\nSystem context:")
