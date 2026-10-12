#!/usr/bin/env python3
"""Speech worker for the voice edition. It runs inside the environment that setup_voice.sh (setup_voice.ps1 on
Windows) creates (it needs kokoro-onnx, faster-whisper and sounddevice) and is started by ksp_voice.py, never by hand.

One JSON request per line on stdin, one JSON answer per line on stdout:
    {"op": "tts", "text": "...", "voice": "bf_emma", "speed": 1.0, "out": "/tmp/line.wav"}
    {"op": "stt", "wav": "/tmp/heard.wav"}
    {"op": "record", "out": "/tmp/heard.wav", "max_seconds": 15, "silence_seconds": 1.1, "wait_seconds": 8}
    {"op": "record", "out": "/tmp/heard.wav", "mode": "hold", "stop_file": "/tmp/stop", "max_seconds": 30}
    {"op": "mic", "seconds": 1.5}    {"op": "voices"}    {"op": "ping"}
Models load on first use and stay in memory, so only the first line is slow.
"""

import json
import os
import sys
import wave

# Libraries like to print; the protocol owns stdout, so everything else goes to stderr.
OUT = sys.stdout
sys.stdout = sys.stderr

DIR = os.environ.get("KSP_VOICE_DIR") or os.path.join(os.path.expanduser("~"), ".ksp-ai-bridge", "voice")
HINT = ("Kerbal Space Program. Kerbin, Mun, Minmus, Duna, Eve, Jool, Laythe, apoapsis, periapsis, orbit, "
        "de-orbit, rendezvous, docking, EVA, launch pad, runway, kilometres, Jebediah, Valentina.")

_kokoro = None
_whisper = None


def kokoro():
    global _kokoro
    if _kokoro is None:
        from kokoro_onnx import Kokoro
        model = next((os.path.join(DIR, name) for name in ("kokoro-v1.0.int8.onnx", "kokoro-v1.0.fp16.onnx", "kokoro-v1.0.onnx")
                      if os.path.exists(os.path.join(DIR, name))), None)
        voices = os.path.join(DIR, "voices-v1.0.bin")
        if model is None or not os.path.exists(voices):
            raise RuntimeError("the Kokoro model files are missing from %s (run setup_voice.sh, or setup_voice.ps1 on Windows)" % DIR)
        _kokoro = Kokoro(model, voices)
    return _kokoro


def whisper():
    global _whisper
    if _whisper is None:
        from faster_whisper import WhisperModel
        name = os.environ.get("KSP_WHISPER_MODEL", "base.en")
        options = dict(device="cpu", compute_type="int8", download_root=os.path.join(DIR, "whisper"))
        try:
            _whisper = WhisperModel(name, local_files_only=True, **options)    # installed: no need to go online
        except Exception:
            _whisper = WhisperModel(name, **options)
    return _whisper


def write_wav(path, samples, rate):
    import numpy as np
    pcm = (np.clip(samples, -1.0, 1.0) * 32767.0).astype("<i2")
    with wave.open(path, "wb") as f:
        f.setnchannels(1)
        f.setsampwidth(2)
        f.setframerate(int(rate))
        f.writeframes(pcm.tobytes())
    return len(pcm) / float(rate)


def tts(request):
    text = (request.get("text") or "").strip()
    if not text:
        raise ValueError("nothing to say")
    voice = request.get("voice") or "bf_emma"
    language = "en-gb" if voice[:1] == "b" else "en-us"
    samples, rate = kokoro().create(text, voice=voice, speed=float(request.get("speed") or 1.0), lang=language)
    seconds = write_wav(request["out"], samples, rate)
    return {"seconds": round(seconds, 2), "rate": int(rate), "voice": voice}


def resample(samples, rate, wanted=16000):
    """Low-pass, then pick the new sample points. Plenty for speech, and it keeps the worker off any audio library."""
    import numpy as np
    if rate == wanted or len(samples) == 0:
        return samples.astype("float32")
    if rate > wanted:
        taps = 63
        n = np.arange(taps) - (taps - 1) / 2.0
        cutoff = 0.45 * wanted / rate                      # cycles per sample, a little under the new Nyquist
        kernel = 2 * cutoff * np.sinc(2 * cutoff * n) * np.hamming(taps)
        samples = np.convolve(samples, kernel / kernel.sum(), mode="same")
    points = np.arange(0, len(samples), rate / float(wanted))
    return np.interp(points, np.arange(len(samples)), samples).astype("float32")


def load_audio(path):
    """The recording as 16 kHz mono floats. WAV files are read directly; anything else goes through ffmpeg
    when it is installed, and through faster-whisper's own decoder when it is not."""
    import numpy as np
    try:
        with wave.open(path, "rb") as f:
            channels, width, rate, frames = f.getnchannels(), f.getsampwidth(), f.getframerate(), f.readframes(f.getnframes())
    except (wave.Error, EOFError):
        import shutil
        import subprocess
        import tempfile
        if not shutil.which("ffmpeg"):
            from faster_whisper import decode_audio
            return decode_audio(path, sampling_rate=16000)
        with tempfile.TemporaryDirectory() as folder:
            converted = os.path.join(folder, "audio.wav")
            done = subprocess.run(["ffmpeg", "-v", "error", "-y", "-i", path, "-ac", "1", "-ar", "16000", "-c:a", "pcm_s16le", converted],
                                  stdout=subprocess.DEVNULL, stderr=subprocess.PIPE)
            if done.returncode != 0:
                raise RuntimeError("ffmpeg could not read %s: %s" % (path, done.stderr.decode(errors="replace").strip()[:200]))
            return load_audio(converted)
    if width == 2:
        samples = np.frombuffer(frames, dtype="<i2").astype("float32") / 32768.0
    elif width == 1:
        samples = (np.frombuffer(frames, dtype="u1").astype("float32") - 128.0) / 128.0
    elif width == 4:
        samples = np.frombuffer(frames, dtype="<i4").astype("float32") / 2147483648.0
    elif width == 3:
        raw = np.frombuffer(frames, dtype="u1").reshape(-1, 3).astype("int32")
        samples = ((raw[:, 0] | (raw[:, 1] << 8) | (raw[:, 2] << 16)) << 8 >> 8).astype("float32") / 8388608.0
    else:
        raise ValueError("unsupported WAV sample size: %d bytes" % width)
    if channels > 1:
        samples = samples[: len(samples) // channels * channels].reshape(-1, channels).mean(axis=1)
    return resample(samples, rate)


def stt(request):
    audio = load_audio(request["wav"])
    segments, info = whisper().transcribe(audio, beam_size=5, language="en", vad_filter=True, initial_prompt=HINT)
    text = " ".join(segment.text.strip() for segment in segments).strip()
    return {"text": text, "seconds": round(len(audio) / 16000.0, 2)}


def microphone(sd, wanted=16000):
    """An open input stream in blocks of a tenth of a second, and its rate: 16 kHz, or the device's own rate
    where it will not record at 16 kHz (some devices on Windows and Linux refuse). The recording is
    resampled for Whisper either way (load_audio)."""
    try:
        return sd.InputStream(samplerate=wanted, channels=1, dtype="int16", blocksize=wanted // 10), wanted
    except Exception:
        rate = int(sd.query_devices(kind="input")["default_samplerate"])
        return sd.InputStream(samplerate=rate, channels=1, dtype="int16", blocksize=rate // 10), rate


def record(request):
    """Record from the microphone. Normally: wait for someone to start speaking and stop when they have gone
    quiet. With mode "hold" (push-to-talk): record from now until the stop file appears."""
    import numpy as np
    fake = os.environ.get("KSP_VOICE_FAKE_MIC")
    if fake:
        # For testing without a microphone: a recording stands in for what the player says.
        import time
        stop_file = request.get("stop_file")
        deadline = time.time() + 10
        while request.get("mode") == "hold" and stop_file and not os.path.exists(stop_file) and time.time() < deadline:
            time.sleep(0.05)
        seconds = write_wav(request["out"], load_audio(fake), 16000)
        return {"heard": True, "seconds": round(seconds, 2), "peak": 1000, "silent": False, "fake": True}
    import sounddevice as sd
    limit = float(request.get("max_seconds") or 15)
    quiet_for = float(request.get("silence_seconds") or 1.1)
    wait_for = float(request.get("wait_seconds") or 8)
    hold, stop_file = request.get("mode") == "hold", request.get("stop_file")
    chunks, heard, quiet, waited, floor, peak, tail = [], False, 0.0, 0.0, None, 0, None
    opened, rate = microphone(sd)
    block = rate // 10
    with opened as stream:
        while True:
            data, _ = stream.read(block)
            peak = max(peak, int(np.abs(data.astype("int32")).max()))
            level = float(np.sqrt(np.mean(data.astype("float64") ** 2)))
            if hold:
                chunks.append(data.copy())
                heard = heard or level > 260.0
                if tail is None and ((stop_file and os.path.exists(stop_file)) or len(chunks) * block / rate >= limit):
                    tail = 3                               # a moment more, so the last word is not clipped
                elif tail is not None:
                    tail -= 1
                    if tail <= 0:
                        break
                continue
            if floor is None:
                floor = level
            if level > max(260.0, floor * 3.0):
                heard, quiet = True, 0.0
            elif heard:
                quiet += block / rate
            else:
                floor = 0.9 * floor + 0.1 * level           # still learning how noisy the room is
                waited += block / rate
            chunks.append(data.copy())
            if not heard:
                chunks = chunks[-4:]                        # keep a little lead-in only
            if heard and (quiet >= quiet_for or len(chunks) * block / rate >= limit):
                break
            if not heard and waited >= wait_for:
                return {"heard": False, "seconds": 0, "peak": peak, "silent": peak == 0}
    audio = np.concatenate(chunks) if chunks else np.zeros((0, 1), dtype="int16")
    with wave.open(request["out"], "wb") as f:
        f.setnchannels(1)
        f.setsampwidth(2)
        f.setframerate(rate)
        f.writeframes(audio.astype("<i2").tobytes())
    return {"heard": heard, "seconds": round(len(audio) / rate, 2), "peak": peak, "silent": peak == 0}


def mic(request):
    """How loud the microphone is right now. Nothing is kept: only the levels leave this function."""
    import numpy as np
    import sounddevice as sd
    device = sd.query_devices(kind="input")
    peak, levels = 0, []
    opened, rate = microphone(sd)
    block = rate // 10
    with opened as stream:
        for _ in range(max(1, int(float(request.get("seconds") or 1.5) * rate / block))):
            data, _ = stream.read(block)
            peak = max(peak, int(np.abs(data.astype("int32")).max()))
            levels.append(float(np.sqrt(np.mean(data.astype("float64") ** 2))))
    return {"device": device.get("name"), "peak": peak, "level": round(sum(levels) / len(levels)), "silent": peak == 0}


def handle(request):
    op = request.get("op")
    if op == "ping":
        return {"pong": True, "dir": DIR}
    if op == "tts":
        return tts(request)
    if op == "stt":
        return stt(request)
    if op == "record":
        return record(request)
    if op == "mic":
        return mic(request)
    if op == "voices":
        return {"voices": sorted(kokoro().get_voices())}
    raise ValueError("unknown op %r" % op)


def main():
    for line in sys.stdin:
        line = line.strip()
        if not line:
            continue
        try:
            answer = handle(json.loads(line))
            answer["ok"] = True
        except Exception as e:  # report, and stay alive for the next line
            answer = {"ok": False, "error": "%s: %s" % (type(e).__name__, e)}
        OUT.write(json.dumps(answer) + "\n")
        OUT.flush()


if __name__ == "__main__":
    main()
