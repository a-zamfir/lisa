# Chatterbox TTS Local Player

Small local Python project that turns a text string into speech and plays it.

## Requirements

- Python 3.11
- Optional: CUDA (NVIDIA GPU) for GPU inference
- Note: AMD GPUs are not supported on Windows (ROCm is Linux-only)

## Setup

```shell
python -m venv .venv
.\.venv\Scripts\Activate.ps1
pip install -r requirements.txt
```

## Usage

Base model (default):

```shell
python app.py "Hello from Chatterbox."
```

Turbo (requires a reference clip):

```shell
python app.py "Hello from Chatterbox Turbo." --model turbo --audio-prompt path\to\ref.wav
```

Multilingual:

```shell
python app.py "Bonjour tout le monde." --model multilingual --language fr
```

By default the audio is saved as `output.wav` and played back on Windows.
The app keeps the model loaded and prompts for text repeatedly. If you pass a text argument, it runs once and then continues prompting.

## Embedding / Integration

This app is designed to preload the model once, then accept text lines over stdin and emit a WAV file for each line.

Protocol:
- Start the process with the model you want and `--no-play`.
- Wait for `Model loaded in ...` then the prompt `Enter text to synthesize`.
- Send one line of text per request (newline-terminated).
- Read the WAV file at `--output` after each request. The file is overwritten each time.
- Send a blank line (or close stdin) to exit.

Example (Python subprocess):

```python
import subprocess
from pathlib import Path

out_path = Path("output.wav")
proc = subprocess.Popen(
    [r".\.venv\Scripts\python.exe", "app.py", "--model", "turbo", "--audio-prompt", "female_ref.wav", "--no-play", "--output", str(out_path)],
    stdin=subprocess.PIPE,
    text=True,
)

proc.stdin.write("Hello from Chatterbox.\n")
proc.stdin.flush()
# Wait until output.wav updates, then consume it in your app.

proc.stdin.write("\n")  # blank line to quit
proc.stdin.flush()
proc.wait()
```

python app.py "Hello Andrei" --model turbo --audio-prompt .\female_ref.wav

## Notes

- For Turbo, a short (around 10 seconds) reference clip is recommended for voice cloning.
- Use `--device cpu` if you do not have a GPU or have an AMD GPU on Windows.
- By default (`--device auto`), the app will use NVIDIA CUDA if available, otherwise CPU.
- The first run will download model weights (several GB) and can take a while; subsequent runs are faster.
- Sampling flags use the model defaults unless you explicitly pass them.
- For faster base/multilingual inference, use `--fast` (uses lower CFG weight).
