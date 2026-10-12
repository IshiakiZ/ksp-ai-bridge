#!/bin/bash
# Voice edition: installs the speech models the ship's voice and spoken commands use. (macOS and Linux;
# on Windows it is setup_voice.ps1, which does the same.)
#
#   ./setup_voice.sh          asks before downloading
#   ./setup_voice.sh --yes    no questions
#
# Everything goes into one folder (default ~/.ksp-ai-bridge/voice, or $KSP_VOICE_DIR) that can simply be
# deleted to uninstall. Nothing is sent anywhere: both models run on this computer.
#   * Kokoro text-to-speech: kokoro-v1.0.int8.onnx (114 MB) and voices-v1.0.bin (28 MB)
#     from https://github.com/thewh1teagle/kokoro-onnx/releases (model-files-v1.1)
#   * Whisper speech recognition, base.en (about 145 MB) from https://huggingface.co/Systran/faster-whisper-base.en
#   * Python packages from PyPI in their own environment: kokoro-onnx, faster-whisper, sounddevice
#     and what they depend on (onnxruntime, numpy, ctranslate2, phonemizer ...), about 300 MB.
# Without it the voice edition still works with the operating system's built-in voice and typed commands.
set -euo pipefail
cd "$(dirname "$0")"
DIR="${KSP_VOICE_DIR:-$HOME/.ksp-ai-bridge/voice}"
MODELS="https://github.com/thewh1teagle/kokoro-onnx/releases/download/model-files-v1.1"

if [ "${1:-}" != "--yes" ]; then
  echo "This downloads about 0.6 GB of speech models and Python packages into:"
  echo "  $DIR"
  read -r -p "Go ahead? [y/N] " answer
  case "$answer" in y|Y|yes|YES) ;; *) echo "Nothing was downloaded."; exit 0;; esac
fi

# Kokoro's package needs Python 3.10 to 3.13.
PYTHON=""
for candidate in python3.13 python3.12 python3.11 python3.10 python3; do
  if command -v "$candidate" >/dev/null 2>&1 && "$candidate" -c 'import sys; sys.exit(0 if (3, 10) <= sys.version_info[:2] <= (3, 13) else 1)' 2>/dev/null; then
    PYTHON="$(command -v "$candidate")"; break
  fi
done
mkdir -p "$DIR"
if [ -n "$PYTHON" ]; then
  [ -x "$DIR/venv/bin/python" ] || "$PYTHON" -m venv "$DIR/venv"
elif command -v uv >/dev/null 2>&1; then
  echo "No Python 3.10-3.13 found; asking uv for Python 3.12."
  [ -x "$DIR/venv/bin/python" ] || uv venv --python 3.12 --seed "$DIR/venv"
else
  echo "error: need Python 3.10 to 3.13 (the Kokoro package does not support older or newer ones yet)." >&2
  echo "       Install one (python.org, Homebrew 'brew install python@3.12', or 'uv') and run this again." >&2
  exit 1
fi
PY="$DIR/venv/bin/python"

echo "== Python packages"
"$PY" -m pip install --quiet --upgrade pip
"$PY" -m pip install --quiet "kokoro-onnx>=0.6" "faster-whisper>=1.1" sounddevice

echo "== Kokoro voice model"
for file in kokoro-v1.0.int8.onnx voices-v1.0.bin; do
  if [ ! -s "$DIR/$file" ]; then
    curl -L --fail --progress-bar -o "$DIR/$file.part" "$MODELS/$file"
    mv "$DIR/$file.part" "$DIR/$file"
  fi
done

echo "== Whisper speech recognition model"
"$PY" - "$DIR" <<'PY'
import sys
from faster_whisper import WhisperModel
WhisperModel("base.en", device="cpu", compute_type="int8", download_root=sys.argv[1] + "/whisper")
print("ok")
PY

# (the microphone library brings its own PortAudio on macOS and Windows, but on Linux it uses the system's)
if ! "$PY" -c "import sounddevice" >/dev/null 2>&1; then
  echo "note: the microphone library (sounddevice) would not load, so spoken commands will not work yet."
  if [ "$(uname -s)" = "Linux" ]; then
    echo "      It needs PortAudio: sudo apt install libportaudio2 (or your system's package of it)."
  fi
fi

echo "== Standard call-outs"
"$(command -v python3)" mcp/ksp_voice.py prepare || true

echo "Done. Try:  python3 mcp/ksp_voice.py say \"All systems nominal.\""
