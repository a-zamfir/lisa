from agent_worker.services import session_state


def test_session_nonce_round_trip():
    session_state.set_session_nonce("session-a", "nonce-a")

    assert session_state.get_session_nonce("session-a") == "nonce-a"


def test_session_nonce_ignores_blank_values_and_missing_session():
    session_state.set_session_nonce("", "nonce-a")
    session_state.set_session_nonce("session-b", None)

    assert session_state.get_session_nonce("") is None
    assert session_state.get_session_nonce("session-b") is None


def test_session_nonce_overwrites_existing_value():
    session_state.set_session_nonce("session-c", "nonce-1")
    session_state.set_session_nonce("session-c", "nonce-2")

    assert session_state.get_session_nonce("session-c") == "nonce-2"
