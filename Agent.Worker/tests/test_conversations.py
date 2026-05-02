from agent_worker.services.conversations import ConversationStore


def test_conversation_store_trims_by_message_count():
    store = ConversationStore(max_messages=2)

    store.append("s1", "user", "one")
    store.append("s1", "assistant", "two")
    store.append("s1", "user", "three")

    history = store.get_history("s1")
    assert [item["content"] for item in history] == ["two", "three"]


def test_conversation_store_trims_by_character_budget():
    store = ConversationStore(max_tokens=1, token_ratio=5, max_messages=10)

    store.append("s2", "user", "1234")
    store.append("s2", "assistant", "5678")

    history = store.get_history("s2")
    assert [item["content"] for item in history] == ["5678"]


def test_conversation_store_refreshes_system_message_when_tool_version_changes():
    store = ConversationStore()

    store.ensure_system("s3", "base prompt", tool_version=1)
    first = store.get_all("s3")
    store.ensure_system("s3", "updated prompt", tool_version=2)
    second = store.get_all("s3")

    assert first[0]["content"] == "base prompt"
    assert second[0]["content"] == "updated prompt"
