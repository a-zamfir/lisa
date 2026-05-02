from __future__ import annotations

import io
import logging
import os
import shlex
import subprocess
import tempfile
import time
import wave
from typing import Tuple

import numpy as np

logger = logging.getLogger("agent_worker.stt")
_MODEL = None
_LOAD_ERROR: str | None = None
_MODEL_DEVICE: str | None = None
_MODEL_COMPUTE: str | None = None
_WHISPERCPP_LOAD_ERROR: str | None = None
_BASE_DIR = os.path.abspath(os.path.join(os.path.dirname(__file__), "..", ".."))
_MODEL_DIR = os.path.abspath(
    os.environ.get(
        "FASTER_WHISPER_MODEL_DIR",
        os.path.join(_BASE_DIR, "speech", "models", "whisper-small"),
    )
)
_WHISPERCPP_EXE_DEFAULT = os.path.join(_BASE_DIR, "speech", "whispercpp", "whisper.exe")
_WHISPERCPP_MODEL_DEFAULT = os.path.join(_BASE_DIR, "speech", "models", "whispercpp", "ggml-small.bin")


def _get_backend() -> str:
    return os.environ.get("STT_BACKEND", "auto").strip().lower()


def _get_whispercpp_paths() -> Tuple[str, str]:
    exe = os.environ.get("WHISPERCPP_EXE", _WHISPERCPP_EXE_DEFAULT)
    model = os.environ.get("WHISPERCPP_MODEL", _WHISPERCPP_MODEL_DEFAULT)
    return os.path.abspath(exe), os.path.abspath(model)


def _whispercpp_available() -> bool:
    exe, model = _get_whispercpp_paths()
    return os.path.isfile(exe) and os.path.isfile(model)


def _load_model():
    global _MODEL, _LOAD_ERROR, _MODEL_DEVICE, _MODEL_COMPUTE
    if _MODEL is not None:
        return _MODEL
    if _LOAD_ERROR is not None:
        raise RuntimeError(_LOAD_ERROR)

    def _import_whisper_model():
        try:
            from faster_whisper import WhisperModel  # type: ignore

            return WhisperModel
        except ModuleNotFoundError as exc:
            msg = (
                "Speech-to-text is not installed (missing 'faster-whisper'). "
                "Install `Agent.Worker/requirements-speech.txt` (Python 3.11-3.13 recommended)."
            )
            global _LOAD_ERROR
            _LOAD_ERROR = msg
            raise RuntimeError(msg) from exc

    if not os.path.isdir(_MODEL_DIR):
        _LOAD_ERROR = f"Whisper model directory not found: {_MODEL_DIR}"
        raise RuntimeError(_LOAD_ERROR)

    model_name = os.environ.get("FASTER_WHISPER_MODEL", "small")
    device_pref = os.environ.get("FASTER_WHISPER_DEVICE", "auto").strip().lower()

    def _cuda_device_count() -> int:
        try:
            import ctranslate2  # type: ignore

            count = ctranslate2.get_cuda_device_count()
            return int(count) if count is not None else 0
        except Exception:
            return 0

    def _try_load(device: str, compute: str):
        WhisperModel = _import_whisper_model()
        print(f"[stt] loading model from {_MODEL_DIR} ({device} {compute})")
        return WhisperModel(_MODEL_DIR, device=device, compute_type=compute)

    cuda_count = _cuda_device_count()
    allow_gpu = device_pref in {"auto", "cuda", "gpu"}
    if allow_gpu and cuda_count > 0:
        for compute in ("int8_float16", "float16", "int8", "float32"):
            try:
                device = "cuda"
                _MODEL = _try_load(device, compute)
                _MODEL_DEVICE = device
                _MODEL_COMPUTE = compute
                logger.info(
                    "stt model_loaded device=%s compute_type=%s model_dir=%s model=%s",
                    device,
                    compute,
                    _MODEL_DIR,
                    model_name,
                )
                return _MODEL
            except Exception as exc:
                logger.info("stt gpu init skipped (%s): %s", compute, exc)
    elif allow_gpu:
        logger.info("stt gpu not available (cuda_device_count=%s); using cpu", cuda_count)

    for compute in ("int8", "float32"):
        try:
            device = "cpu"
            _MODEL = _try_load(device, compute)
            _MODEL_DEVICE = device
            _MODEL_COMPUTE = compute
            logger.info(
                "stt model_loaded device=%s compute_type=%s model_dir=%s model=%s",
                device,
                compute,
                _MODEL_DIR,
                model_name,
            )
            logger.info("stt cpu fallback active")
            return _MODEL
        except Exception as exc:
            logger.info("stt cpu init skipped (%s): %s", compute, exc)

    _LOAD_ERROR = f"faster-whisper init failed for model '{model_name}'"
    raise RuntimeError(_LOAD_ERROR)


def load_model():
    backend = _get_backend()
    exe, model = _get_whispercpp_paths()
    available = _whispercpp_available()
    logger.info(
        "stt backend=%s whispercpp exe=%s model=%s available=%s",
        backend,
        exe,
        model,
        available,
    )
    if backend == "whispercpp":
        return _load_whispercpp()
    if backend == "auto" and available:
        return _load_whispercpp()
    return _load_model()


def _load_whispercpp():
    global _MODEL_DEVICE, _MODEL_COMPUTE, _WHISPERCPP_LOAD_ERROR
    if _WHISPERCPP_LOAD_ERROR is not None:
        raise RuntimeError(_WHISPERCPP_LOAD_ERROR)

    exe, model = _get_whispercpp_paths()
    if not os.path.isfile(exe) or not os.path.isfile(model):
        _WHISPERCPP_LOAD_ERROR = (
            "whisper.cpp STT not configured. "
            "Set WHISPERCPP_EXE and WHISPERCPP_MODEL to valid paths."
        )
        raise RuntimeError(_WHISPERCPP_LOAD_ERROR)

    device = os.environ.get("WHISPERCPP_DEVICE", "auto").strip().lower() or "auto"
    _MODEL_DEVICE = device
    _MODEL_COMPUTE = "whispercpp"
    logger.info("stt whispercpp ready exe=%s model=%s device=%s", exe, model, device)
    return None


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


def transcribe_audio(audio_bytes: bytes) -> Tuple[str, int, int, str | None, str | None]:
    decode_start = time.monotonic()
    audio, sample_rate = _decode_audio(audio_bytes)
    audio = _resample_audio(audio, sample_rate, 16000)
    decode_ms = int((time.monotonic() - decode_start) * 1000)
    print(f"[stt] decode bytes={len(audio_bytes)} samples={len(audio)} sr={sample_rate} decode_ms={decode_ms}")

    backend = _get_backend()
    if backend in {"whispercpp", "auto"} and _whispercpp_available():
        try:
            transcript, stt_ms, device, compute = _transcribe_whispercpp(audio)
            logger.info("stt decode_ms=%s stt_ms=%s sample_rate=%s", decode_ms, stt_ms, sample_rate)
            return transcript, decode_ms, stt_ms, device, compute
        except Exception as exc:
            if backend == "whispercpp":
                raise
            logger.warning("whispercpp failed; falling back to faster-whisper: %s", exc)

    transcript, stt_ms, device, compute = _transcribe_faster_whisper(audio)
    logger.info("stt decode_ms=%s stt_ms=%s sample_rate=%s", decode_ms, stt_ms, sample_rate)
    return transcript, decode_ms, stt_ms, device, compute


def _transcribe_faster_whisper(audio: np.ndarray) -> Tuple[str, int, str | None, str | None]:
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
    return transcript, stt_ms, _MODEL_DEVICE, _MODEL_COMPUTE


def _write_wav(path: str, audio: np.ndarray, sample_rate: int = 16000) -> None:
    pcm = np.clip(audio, -1.0, 1.0)
    pcm16 = (pcm * 32767.0).astype(np.int16)
    with wave.open(path, "wb") as wf:
        wf.setnchannels(1)
        wf.setsampwidth(2)
        wf.setframerate(sample_rate)
        wf.writeframes(pcm16.tobytes())


def _parse_whispercpp_stdout(stdout: str) -> str:
    lines = []
    for line in stdout.splitlines():
        clean = line.strip()
        if not clean:
            continue
        if clean.startswith("[") or clean.startswith("whisper"):
            continue
        lines.append(clean)
    return " ".join(lines).strip()


def _transcribe_whispercpp(audio: np.ndarray) -> Tuple[str, int, str | None, str | None]:
    _load_whispercpp()
    exe, model = _get_whispercpp_paths()
    language = os.environ.get("WHISPERCPP_LANGUAGE", "en").strip() or "en"
    extra_args = shlex.split(os.environ.get("WHISPERCPP_ARGS", ""))

    with tempfile.TemporaryDirectory() as tmp_dir:
        wav_path = os.path.join(tmp_dir, "audio.wav")
        _write_wav(wav_path, audio, 16000)
        output_base = os.path.join(tmp_dir, "result")
        output_txt = f"{output_base}.txt"

        base_cmd = [exe, "-m", model, "-f", wav_path, "-l", language, "-nt"]
        output_cmd = base_cmd + ["-otxt", "-of", output_base] + extra_args

        stt_start = time.monotonic()
        result = subprocess.run(
            output_cmd,
            capture_output=True,
            text=True,
            cwd=os.path.dirname(exe),
            timeout=120,
        )

        if result.returncode != 0:
            logger.warning("whispercpp failed (%s); retrying without output flags", result.stderr.strip())
            result = subprocess.run(
                base_cmd + extra_args,
                capture_output=True,
                text=True,
                cwd=os.path.dirname(exe),
                timeout=120,
            )
            if result.returncode != 0:
                raise RuntimeError(result.stderr.strip() or "whispercpp failed")

        transcript = ""
        if os.path.isfile(output_txt):
            with open(output_txt, "r", encoding="utf-8", errors="ignore") as handle:
                transcript = handle.read().strip()
        if not transcript:
            transcript = _parse_whispercpp_stdout(result.stdout)

        stt_ms = int((time.monotonic() - stt_start) * 1000)
        device = _MODEL_DEVICE or "whispercpp"
        compute = _MODEL_COMPUTE or "whispercpp"
        return transcript, stt_ms, device, compute
