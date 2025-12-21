from __future__ import annotations

import argparse
import os

from faster_whisper import download_model


def main() -> int:
    parser = argparse.ArgumentParser(description="Download faster-whisper model into local folder.")
    parser.add_argument("--model", default="small", help="Model size or HF repo id (default: small).")
    parser.add_argument(
        "--output",
        default=os.path.join(os.path.dirname(__file__), "models", "whisper-small"),
        help="Output directory for model files.",
    )
    args = parser.parse_args()

    output_dir = os.path.abspath(args.output)
    os.makedirs(output_dir, exist_ok=True)

    print(f"Downloading faster-whisper model '{args.model}' to {output_dir}")
    download_model(args.model, output_dir=output_dir, local_files_only=False)
    print("Download complete.")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
