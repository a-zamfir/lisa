from __future__ import annotations

import io
import logging
import os
import time
import wave
from typing import Tuple

import numpy as np

logger = logging.getLogger("agent_worker.stt")
_MODEL = None
_LOAD_ERROR: str | None = None
_MODEL_DIR = os.path.abspath(
    os.environ.get(
        "FASTER_WHISPER_MODEL_DIR",
        os.path.join(os.path.dirname(__file__), "..", "speech", "models", "whisper-small"),
    )
)


def _load_model():
    global _MODEL, _LOAD_ERROR
    if _MODEL is not None:
        return _MODEL
    if _LOAD_ERROR is not None:
        raise RuntimeError(_LOAD_ERROR)

    if not os.path.isdir(_MODEL_DIR):
        _LOAD_ERROR = f"Whisper model directory not found: {_MODEL_DIR}"
        raise RuntimeError(_LOAD_ERROR)

    model_name = os.environ.get("FASTER_WHISPER_MODEL", "base")
    try:
        from faster_whisper import WhisperModel  # type: ignore

        print(f"[stt] loading model from {_MODEL_DIR} (gpu int8_float16)")
        _MODEL = WhisperModel(_MODEL_DIR, device="cuda", compute_type="int8_float16")
    except Exception:
        try:
            from faster_whisper import WhisperModel  # type: ignore

            print(f"[stt] loading model from {_MODEL_DIR} (gpu float16)")
            _MODEL = WhisperModel(_MODEL_DIR, device="cuda", compute_type="float16")
        except Exception:
            try:
                from faster_whisper import WhisperModel  # type: ignore

                print(f"[stt] loading model from {_MODEL_DIR} (cpu int8)")
                _MODEL = WhisperModel(_MODEL_DIR, device="cpu", compute_type="int8")
            except Exception as exc:
                _LOAD_ERROR = f"faster-whisper init failed for model '{model_name}': {exc}"
                raise RuntimeError(_LOAD_ERROR) from exc
    return _MODEL


def load_model():
    return _load_model()


def _decode_audio(audio_bytes: bytes) -> Tuple[np.ndarray, int]:
    try:
        import soundfile as sf  # type: ignore

        data, sample_rate = sf.read(io.BytesIO(audio_bytes), dtype="float32")
        if data.ndim > 1:
            data = data.mean(axis=1)
        return data, int(sample_rate)
    except Exception:
        pass

    try:
        from scipy.io import wavfile  # type: ignore

        sample_rate, data = wavfile.read(io.BytesIO(audio_bytes))
        if data.ndim > 1:
            data = data.mean(axis=1)
        data = _convert_to_float32(data)
        return data, int(sample_rate)
    except Exception:
        pass

    with wave.open(io.BytesIO(audio_bytes), "rb") as wf:
        sample_rate = wf.getframerate()
        channels = wf.getnchannels()
        sample_width = wf.getsampwidth()
        frames = wf.readframes(wf.getnframes())
        if sample_width == 2:
            data = np.frombuffer(frames, dtype=np.int16).astype(np.float32) / 32768.0
        elif sample_width == 4:
            data = np.frombuffer(frames, dtype=np.int32).astype(np.float32) / float(2**31)
        else:
            data = np.frombuffer(frames, dtype=np.int16).astype(np.float32) / 32768.0
        if channels > 1:
            data = data.reshape(-1, channels).mean(axis=1)
        return data, int(sample_rate)


def _convert_to_float32(data: np.ndarray) -> np.ndarray:
    if data.dtype == np.float32:
        return data
    if np.issubdtype(data.dtype, np.integer):
        max_val = np.iinfo(data.dtype).max
        return data.astype(np.float32) / float(max_val)
    return data.astype(np.float32)


def _resample_audio(audio: np.ndarray, sample_rate: int, target_rate: int = 16000) -> np.ndarray:
    if sample_rate == target_rate:
        return audio.astype(np.float32)
    if len(audio) == 0:
        return audio.astype(np.float32)
    duration = len(audio) / float(sample_rate)
    new_length = max(1, int(duration * target_rate))
    x_old = np.linspace(0, len(audio), num=len(audio), endpoint=False)
    x_new = np.linspace(0, len(audio), num=new_length, endpoint=False)
    return np.interp(x_new, x_old, audio).astype(np.float32)


def transcribe_audio(audio_bytes: bytes) -> Tuple[str, int, int]:
    decode_start = time.monotonic()
    audio, sample_rate = _decode_audio(audio_bytes)
    audio = _resample_audio(audio, sample_rate, 16000)
    decode_ms = int((time.monotonic() - decode_start) * 1000)
    print(f"[stt] decode bytes={len(audio_bytes)} samples={len(audio)} sr={sample_rate} decode_ms={decode_ms}")

    model = _load_model()
    stt_start = time.monotonic()
    segments, _info = model.transcribe(
        audio,
        language="en",
        task="transcribe",
        beam_size=1,
        word_timestamps=False,
    )
    transcript = " ".join(seg.text.strip() for seg in segments).strip()
    stt_ms = int((time.monotonic() - stt_start) * 1000)

    logger.info("stt decode_ms=%s stt_ms=%s sample_rate=%s", decode_ms, stt_ms, sample_rate)
    return transcript, decode_ms, stt_ms
