from agent_worker.services.memory_trailer import StreamTrailerFilter, extract_memory_trailer


def test_extract_memory_trailer_removes_payload_from_completion():
    cleaned, payload = extract_memory_trailer(
        'Hello there.<lisa_memory>{"ops":[{"op":"upsert","key":"user.name","value":"Andrei"}]}</lisa_memory>'
    )

    assert cleaned == "Hello there."
    assert payload["ops"][0]["key"] == "user.name"


def test_stream_trailer_filter_suppresses_everything_after_trailer_start():
    filt = StreamTrailerFilter()

    first = filt.feed("Part 1 with enough text ")
    second = filt.feed('<lisa_memory>{"ops":[]}</lisa_memory>')
    third = filt.feed("SHOULD_NOT_APPEAR")
    tail, payload = filt.finalize()
    visible = first + second + third + tail

    assert visible == "Part 1 with enough text "
    assert third == ""
    assert "<lisa_memory>" not in visible
    assert "SHOULD_NOT_APPEAR" not in visible
    assert payload == {"ops": []}
