from agent_worker.services import visual_context


def test_visual_context_set_peek_pop_roundtrip():
    visual_context.set_latest_frame(
        "session-1",
        mime_type="image/png",
        data_base64="abc123",
        width=640,
        height=480,
        timestamp=123.45,
    )

    frame = visual_context.peek_latest_frame("session-1")
    assert frame is not None
    assert frame.mime_type == "image/png"
    assert frame.width == 640
    assert frame.height == 480
    assert frame.timestamp == 123.45

    popped = visual_context.pop_latest_frame("session-1")
    assert popped == frame
    assert visual_context.peek_latest_frame("session-1") is None


def test_visual_context_ignores_empty_session():
    visual_context.set_latest_frame(
        "",
        mime_type="image/jpeg",
        data_base64="ignored",
        width=1,
        height=1,
    )

    assert visual_context.peek_latest_frame("") is None
    assert visual_context.pop_latest_frame("") is None
