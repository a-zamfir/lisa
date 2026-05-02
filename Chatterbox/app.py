import argparse
import sys
import time
from pathlib import Path

import torch
import torchaudio as ta


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(
        description="Generate speech from text and play it locally."
    )
    parser.add_argument(
        "text",
        nargs="?",
        help="Text to synthesize. If omitted, you will be prompted.",
    )
    parser.add_argument(
        "--model",
        choices=["turbo", "base", "multilingual"],
        default="base",
        help="Which Chatterbox model to use.",
    )
    parser.add_argument(
        "--audio-prompt",
        dest="audio_prompt_path",
        help="Path to a reference audio clip for voice cloning.",
    )
    parser.add_argument(
        "--language",
        default="en",
        help="Language id for the multilingual model (default: en).",
    )
    parser.add_argument(
        "--device",
        choices=["auto", "cpu", "cuda"],
        default="auto",
        help="Inference device selection (auto, cpu, or cuda for NVIDIA).",
    )
    parser.add_argument(
        "--output",
        default="output.wav",
        help="Output wav path (default: output.wav).",
    )
    parser.add_argument(
        "--no-play",
        action="store_true",
        help="Do not play the output audio.",
    )
    parser.add_argument(
        "--fast",
        action="store_true",
        help="Faster inference preset for base/multilingual.",
    )
    parser.add_argument(
        "--cfg-weight",
        type=float,
        default=None,
        help="CFG weight for base/multilingual (default: model default).",
    )
    parser.add_argument(
        "--exaggeration",
        type=float,
        default=None,
        help="Exaggeration for base/multilingual (default: model default).",
    )
    parser.add_argument(
        "--temperature",
        type=float,
        default=None,
        help="Sampling temperature (default: model default).",
    )
    parser.add_argument(
        "--repetition-penalty",
        type=float,
        default=None,
        help="Penalty for repetition (default: model default).",
    )
    parser.add_argument(
        "--min-p",
        type=float,
        default=None,
        help="Minimum p for sampling (default: model default).",
    )
    parser.add_argument(
        "--top-p",
        type=float,
        default=None,
        help="Top-p for sampling (default: model default).",
    )
    return parser.parse_args()


def resolve_device(device_arg: str) -> str:
    if device_arg == "auto":
        # Try CUDA (NVIDIA GPUs, or ROCm on Linux)
        if torch.cuda.is_available():
            return "cuda"
        # Fallback to CPU
        return "cpu"
    return device_arg


def get_text(text_arg: str | None) -> str:
    if text_arg:
        return text_arg
    try:
        return input("Enter text: ").strip()
    except EOFError:
        return ""


def describe_device(device: str) -> str:
    if device == "cuda":
        name = torch.cuda.get_device_name(0)
        # Check if this is ROCm (AMD) or CUDA (NVIDIA)
        if hasattr(torch.version, 'hip') and torch.version.hip is not None:
            return f"rocm ({name})"
        return f"cuda ({name})"
    return device


def main() -> int:
    args = parse_args()
    device = resolve_device(args.device)
    if args.device == "cuda" and device != "cuda":
        print("CUDA was requested but is not available.", file=sys.stderr)
        return 1
    print(f"Using device: {describe_device(device)}")

    audio_prompt_path = None
    if args.audio_prompt_path:
        audio_prompt_path = Path(args.audio_prompt_path)
        if not audio_prompt_path.exists():
            print(f"Audio prompt not found: {audio_prompt_path}", file=sys.stderr)
            return 1

    load_start = time.perf_counter()
    if args.model == "turbo":
        from chatterbox.tts_turbo import ChatterboxTurboTTS

        model = ChatterboxTurboTTS.from_pretrained(device=device)
        gen_kwargs = {}
        if audio_prompt_path:
            gen_kwargs["audio_prompt_path"] = str(audio_prompt_path)
    elif args.model == "multilingual":
        from chatterbox.mtl_tts import ChatterboxMultilingualTTS

        model = ChatterboxMultilingualTTS.from_pretrained(device=device)
        gen_kwargs = {"language_id": args.language}
        if args.fast and args.cfg_weight is None:
            gen_kwargs["cfg_weight"] = 0.05
        if args.cfg_weight is not None:
            gen_kwargs["cfg_weight"] = args.cfg_weight
        if "cfg_weight" in gen_kwargs and gen_kwargs["cfg_weight"] <= 0.0:
            print(
                "cfg_weight=0 is not supported by the base/multilingual model; using 0.01.",
                file=sys.stderr,
            )
            gen_kwargs["cfg_weight"] = 0.01
        if args.exaggeration is not None:
            gen_kwargs["exaggeration"] = args.exaggeration
        if args.temperature is not None:
            gen_kwargs["temperature"] = args.temperature
        if args.repetition_penalty is not None:
            gen_kwargs["repetition_penalty"] = args.repetition_penalty
        if args.min_p is not None:
            gen_kwargs["min_p"] = args.min_p
        if args.top_p is not None:
            gen_kwargs["top_p"] = args.top_p
        if audio_prompt_path:
            gen_kwargs["audio_prompt_path"] = str(audio_prompt_path)
    else:
        from chatterbox.tts import ChatterboxTTS

        model = ChatterboxTTS.from_pretrained(device=device)
        gen_kwargs = {}
        if args.fast and args.cfg_weight is None:
            gen_kwargs["cfg_weight"] = 0.05
        if args.cfg_weight is not None:
            gen_kwargs["cfg_weight"] = args.cfg_weight
        if "cfg_weight" in gen_kwargs and gen_kwargs["cfg_weight"] <= 0.0:
            print(
                "cfg_weight=0 is not supported by the base/multilingual model; using 0.01.",
                file=sys.stderr,
            )
            gen_kwargs["cfg_weight"] = 0.01
        if args.exaggeration is not None:
            gen_kwargs["exaggeration"] = args.exaggeration
        if args.temperature is not None:
            gen_kwargs["temperature"] = args.temperature
        if args.repetition_penalty is not None:
            gen_kwargs["repetition_penalty"] = args.repetition_penalty
        if args.min_p is not None:
            gen_kwargs["min_p"] = args.min_p
        if args.top_p is not None:
            gen_kwargs["top_p"] = args.top_p
        if audio_prompt_path:
            gen_kwargs["audio_prompt_path"] = str(audio_prompt_path)
    load_elapsed = time.perf_counter() - load_start
    print(f"Model loaded in {load_elapsed:.2f}s")

    try:
        import winsound
    except ImportError:
        winsound = None

    def run_once(text: str) -> None:
        start = time.perf_counter()
        try:
            with torch.inference_mode():
                wav = model.generate(text, **gen_kwargs)
        except Exception as exc:
            print(f"Failed to generate audio: {exc}", file=sys.stderr)
            return
        elapsed = time.perf_counter() - start
        print(f"Generated audio in {elapsed:.2f}s")

        output_path = Path(args.output)
        output_path.parent.mkdir(parents=True, exist_ok=True)
        if isinstance(wav, torch.Tensor) and wav.is_cuda:
            wav = wav.cpu()
        ta.save(str(output_path), wav, model.sr)
        print(f"Wrote {output_path}")

        if args.no_play:
            return
        if winsound is None:
            print("Playback is only supported on Windows.", file=sys.stderr)
            return
        winsound.PlaySound(str(output_path), winsound.SND_FILENAME)

    if args.text:
        run_once(args.text)

    print("Enter text to synthesize (blank line to quit).")
    while True:
        text = get_text(None)
        if not text:
            return 0
        run_once(text)


if __name__ == "__main__":
    raise SystemExit(main())
