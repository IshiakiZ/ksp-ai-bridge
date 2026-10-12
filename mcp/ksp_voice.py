#!/usr/bin/env python3
"""The ship's voice for KSP AI Bridge (the voice edition).

    python3 mcp/ksp_voice.py status                  what is installed and what is running
    python3 mcp/ksp_voice.py say "All systems nominal."
    python3 mcp/ksp_voice.py narrate                 the ship's computer calls out the flight (Ctrl+C stops it)
    python3 mcp/ksp_voice.py listen                  hold Y in the game and talk: what you say goes to Claude
    python3 mcp/ksp_voice.py voices                  the voices to choose from
    python3 mcp/ksp_voice.py set voice=bm_george speed=1.05 effect=radio detail=chatty key=B
    python3 mcp/ksp_voice.py mic                     check the microphone (only the loudness is read)
    python3 mcp/ksp_voice.py prepare                 make the standard call-outs ahead of time (setup does this)

Speech is made by Kokoro and understood by Whisper once ./setup_voice.sh (setup_voice.ps1 on Windows) has
installed them; both run on this computer and nothing is sent anywhere. Without them the ship speaks with the
operating system's voice and takes typed commands. For a model on your own computer instead of Claude, see
ksp_local.py --voice. The key and the voice can also be chosen in the game's AI Bridge panel.

Needs Python 3.8+ and nothing else: the speech models live in their own environment.
"""

import atexit
import base64
import hashlib
import json
import math
import os
import queue
import re
import shutil
import subprocess
import sys
import tempfile
import threading
import time
import uuid
import wave

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)
import ksp_mcp  # noqa: E402

HOME_DIR = os.path.join(os.path.expanduser("~"), ".ksp-ai-bridge")
VOICE_DIR = os.environ.get("KSP_VOICE_DIR") or os.path.join(HOME_DIR, "voice")
SETTINGS_PATH = os.path.join(HOME_DIR, "voice.json")
MODEL_FILES = ("kokoro-v1.0.int8.onnx", "kokoro-v1.0.fp16.onnx", "kokoro-v1.0.onnx")
CLIENT = "voice-%d" % os.getpid()
# (Windows: the helper programs started here get no console window of their own, which would flash up over the game)
QUIET_START = {"creationflags": 0x08000000} if os.name == "nt" else {}      # CREATE_NO_WINDOW
SETUP = "setup_voice.ps1" if os.name == "nt" else "./setup_voice.sh"       # what installs the speech models here

DEFAULTS = {
    "voice": "bf_emma",      # a Kokoro voice; `voices` lists them
    "speed": 1.0,
    "effect": "ship",        # ship | radio | none: how it sounds inside the game
    "volume": 1.0,
    "detail": "normal",      # quiet | normal | chatty: how much the ship's computer says by itself
    "key": "Y",              # push-to-talk key in the game
    "engine": "auto",        # auto | kokoro | system
    "system_voice": "",      # name of an operating-system voice for the fallback
}

BRIEF = ("You are the ship's computer and the player is talking to you out loud. Your answers are spoken, "
         "so keep them to one or two short plain sentences: no lists, no markdown, no tool names, round the numbers. "
         "Never leave them in silence: before anything else say one short line with ksp_chat reply (it is spoken) "
         "that you heard and what you will do first, and another about every 20 seconds while you work. "
         "Do what was asked with the tools, then say what happened. Speech recognition makes mistakes: if a "
         "request makes no sense, ask instead of guessing.")


# The ship's computer's standard call-outs. They are made once and kept, so that a height called out
# on the way down is heard at that height and not a second later.
CALL_OUTS = """10,000 metres.|5,000 metres.|2,000 metres.|1,000 metres.|500 metres.|200.|100.|50.|20.|10.|Ignition.|Lift-off.|Staging.
|Supersonic.|Passing maximum dynamic pressure.|Beginning gravity turn.|Engine cut-off. Coasting out of the atmosphere.
|We have left the atmosphere.|Coasting to apoapsis.|Circularisation burn.|Ascent complete.|Burn in 60 seconds.|30 seconds to burn.
|Burn in 10 seconds.|Turning to burn attitude.|Burn started.|Burn complete.|Atmospheric entry.|Parachute out.|Parachutes out.
|Parachute fully open.|Parachutes fully open.|Braking burn.|Final descent.|Touchdown.|Splashdown.|Landing complete.|Airborne.
|Autopilot disengaged.|Warning. Propellant at 10 percent.|Warning. Propellant exhausted.|Warning. Electric charge below 10 percent.
|Warning. Impact imminent.|Warning. Landing gear is up.|Warning. Collision.|Warning. High g load.|Signal lost.|Signal restored.
|Docking confirmed.|Docking complete.|Flight plan complete.|Acknowledged.|Target range 10 km.|Target range 5 km.|Target range 1 km.
|Target range 500 m.|Target range 100 m.|Target range 50 m.|Target range 20 m.|Target range 10 m.|De-orbit burn.
|De-orbit burn complete.|Coasting to atmospheric entry.|Coasting to the braking point.|Over the landing site. Descending.
|Take-off roll.|Rotate.|Positive climb.|On final approach.|Flare.|Rolling out.|Landing complete. Full stop.|Under way.|We have arrived."""
CALL_OUTS = [line.strip() for line in CALL_OUTS.replace("\n", "").split("|") if line.strip()]


class VoiceError(Exception):
    """Something about speech that is worth telling the player."""


# --------------------------------------------------------------------------- settings


def load_settings(strict=False):
    """The settings, defaults filled in. strict: None when the file is there but cannot be read just now."""
    settings = dict(DEFAULTS)
    try:
        with open(SETTINGS_PATH, "r", encoding="utf-8") as f:
            stored = json.load(f)
        settings.update({k: v for k, v in stored.items() if k in DEFAULTS})
    except FileNotFoundError:
        pass
    except (OSError, ValueError, AttributeError):
        if strict:
            return None
    return settings


def settings_stamp():
    """Changes whenever the settings file does (the game's panel writes it too)."""
    try:
        stat = os.stat(SETTINGS_PATH)
        return (stat.st_mtime_ns, stat.st_size)
    except OSError:
        return None


def save_settings(changes):
    """Check and store changed settings; returns all settings."""
    settings = load_settings()
    for key, value in changes.items():
        if key not in DEFAULTS:
            raise VoiceError("Unknown voice setting '%s'. Settings: %s." % (key, ", ".join(sorted(DEFAULTS))))
        if key in ("speed", "volume"):
            try:
                value = float(value)
            except (TypeError, ValueError):
                raise VoiceError("%s must be a number." % key)
            low, high = (0.5, 2.0) if key == "speed" else (0.0, 1.0)
            if not low <= value <= high:
                raise VoiceError("%s must be between %g and %g." % (key, low, high))
        else:
            value = str(value).strip()
            allowed = {"effect": ("ship", "radio", "none"), "detail": ("quiet", "normal", "chatty"), "engine": ("auto", "kokoro", "system")}.get(key)
            if allowed and value not in allowed:
                raise VoiceError("%s must be one of: %s." % (key, ", ".join(allowed)))
        settings[key] = value
    os.makedirs(HOME_DIR, exist_ok=True)
    # (written whole and then put in place, so that a reader never finds half a file: the game reads it as well)
    temporary = SETTINGS_PATH + ".%d.tmp" % os.getpid()
    with open(temporary, "w", encoding="utf-8") as f:
        json.dump({k: v for k, v in settings.items() if v != DEFAULTS[k]}, f, indent=1)
    os.replace(temporary, SETTINGS_PATH)
    return settings


# --------------------------------------------------------------------------- the speech models


def venv_python():
    for parts in (("venv", "bin", "python"), ("venv", "Scripts", "python.exe")):
        path = os.path.join(VOICE_DIR, *parts)
        if os.path.exists(path):
            return path
    return None


def kokoro_installed():
    return bool(venv_python()) and os.path.exists(os.path.join(VOICE_DIR, "voices-v1.0.bin")) and \
        any(os.path.exists(os.path.join(VOICE_DIR, name)) for name in MODEL_FILES)


def whisper_installed():
    return bool(venv_python()) and os.path.isdir(os.path.join(VOICE_DIR, "whisper"))


class Worker:
    """The speech models, loaded once in their own Python environment and asked one thing at a time."""

    def __init__(self, name):
        self.name = name
        self.lock = threading.Lock()
        self.proc = None
        self.replies = None

    def _start(self):
        if self.proc is not None and self.proc.poll() is None:
            return
        python = venv_python()
        if not python:
            raise VoiceError("The speech models are not installed. Run %s in the mod's folder." % SETUP)
        log = open(os.path.join(VOICE_DIR, "worker-%s.log" % self.name), "wb")
        env = dict(os.environ, KSP_VOICE_DIR=VOICE_DIR, PYTHONUNBUFFERED="1")
        self.proc = subprocess.Popen([python, os.path.join(HERE, "voice_worker.py")], stdin=subprocess.PIPE,
                                     stdout=subprocess.PIPE, stderr=log, env=env, **QUIET_START)
        log.close()
        self.replies = queue.Queue()

        def pump(stream, replies):
            for line in stream:
                replies.put(line)
            replies.put(None)

        threading.Thread(target=pump, args=(self.proc.stdout, self.replies), daemon=True).start()

    def stop(self):
        with self.lock:
            self._kill()

    def _kill(self):
        if self.proc is not None:
            try:
                self.proc.kill()
            except OSError:
                pass
        self.proc = None

    def request(self, payload, timeout=120.0):
        with self.lock:
            self._start()
            try:
                self.proc.stdin.write((json.dumps(payload) + "\n").encode("utf-8"))
                self.proc.stdin.flush()
                line = self.replies.get(timeout=timeout)
            except queue.Empty:
                self._kill()
                raise VoiceError("The speech models did not answer within %d seconds." % timeout)
            except (OSError, ValueError):
                line = None
            if line is None:
                self._kill()
                raise VoiceError("The speech models stopped unexpectedly; see %s." % os.path.join(VOICE_DIR, "worker-%s.log" % self.name))
            answer = json.loads(line.decode("utf-8"))
            if not answer.get("ok"):
                raise VoiceError(answer.get("error") or "the speech models reported an error")
            return answer


SPEAK = Worker("speak")      # Kokoro
HEAR = Worker("hear")        # the microphone and Whisper; separate so listening never holds up speaking


# --------------------------------------------------------------------------- text for the ear

UNITS = [
    (r"(?<=\d)\s*km/s\b", " kilometres per second"), (r"(?<=\d)\s*m/s(?:²|\^2)", " metres per second squared"),
    (r"(?<=\d)\s*m/s\b", " metres per second"), (r"(?<=\d)\s*km/h\b", " kilometres per hour"),
    (r"(?<=\d)\s*km\b", " kilometres"), (r"(?<=\d)\s*m\b", " metres"), (r"(?<=\d)\s*kN\b", " kilonewtons"),
    (r"(?<=\d)\s*kPa\b", " kilopascals"), (r"(?<=\d)\s*kg\b", " kilograms"), (r"(?<=\d)\s*t\b", " tonnes"),
    (r"(?<=\d)\s*s\b", " seconds"), (r"(?<=\d)\s*°", " degrees"), (r"(?<=\d)\s*%", " percent"),
]
WORDS = [
    (r"Δ\s*v\b|\bdelta-?v\b|\bdv\b", "delta vee"), (r"\bTWR\b", "thrust to weight ratio"), (r"\bIsp\b", "specific impulse"),
    (r"\bSOI\b", "sphere of influence"), (r"\bAp\b", "apoapsis"), (r"\bPe\b", "periapsis"), (r"\bRCS\b", "R C S"),
    (r"\bSAS\b", "S A S"), (r"\bEVA\b", "E V A"), (r"\bKSC\b", "K S C"), (r"\bVAB\b", "V A B"), (r"\bSPH\b", "S P H"),
    (r"\bMET\b", "mission time"), (r"\bUT\b", "universal time"), (r"(?<=\d)\s*x\s*(?=\d)", " by "), (r"\s*→\s*", " to "),
    (r"≈|~(?=\d)", "about "), (r"×", " by "), (r"&", " and "), (r"(?<![\w.])-(?=\d)", "minus "),
]


def tidy(text):
    """Plain text from whatever a model wrote: no markdown, links or pictograms, single spaces."""
    text = re.sub(r"```.*?```", " ", text or "", flags=re.S)
    text = re.sub(r"<think>.*?</think>", " ", text, flags=re.S)
    text = re.sub(r"\[([^\]]+)\]\([^)]+\)", r"\1", text)
    text = re.sub(r"https?://\S+", " ", text)
    text = re.sub(r"^\s*([-*•]|\d+[.)])\s+", "", text, flags=re.M)
    text = re.sub(r"[*_`#>|]", "", text)
    text = "".join(ch for ch in text if not (0x2300 <= ord(ch) < 0x2400 or 0x2600 <= ord(ch) < 0x27C0 or 0xFE00 <= ord(ch) < 0xFE10 or ord(ch) >= 0x1F000))
    return re.sub(r"\s+", " ", text).strip()


def pronounce(text):
    """The same words spelled the way a speech engine says them right: units written out, initials spaced."""
    for pattern, spoken in UNITS + WORDS:
        text = re.sub(pattern, spoken, text)
    text = re.sub(r"(?<=\d)\.(\d+)", lambda m: " point " + " ".join(m.group(1)), text)
    text = re.sub(r"(?<![\d.,])1 (kilometre|metre|second|degree|tonne|kilogram|kilonewton|kilopascal)s\b", r"1 \1", text)
    return re.sub(r"\s+", " ", text).strip()


def sentences(text, limit=160):
    """Split a longer reply into pieces, at sentence ends where possible, so the first words are heard
    while the rest is still being made."""
    if len(text) <= 100:
        return [text]
    pieces, current = [], ""
    for part in re.split(r"(?<=[.!?])\s+", text):
        while len(part) > limit:
            cut = part.rfind(" ", 0, limit)
            cut = cut if cut > 40 else limit
            pieces.append((current + " " + part[:cut]).strip())
            current, part = "", part[cut:].strip()
        if current and (len(current) >= 50 or len(current) + len(part) + 1 > limit):
            pieces.append(current)
            current = part
        else:
            current = (current + " " + part).strip()
    if current:
        pieces.append(current)
    return pieces


# --------------------------------------------------------------------------- speaking


def system_voice_available():
    if sys.platform == "darwin":
        return bool(shutil.which("say"))
    if os.name == "nt":
        return bool(shutil.which("powershell"))
    return bool(shutil.which("espeak-ng") or shutil.which("espeak"))


def system_speak(text, path, settings):
    """Speech from the operating system's own voice, as a 16-bit WAV file."""
    if sys.platform == "darwin":
        command = ["say", "-o", path, "--file-format=WAVE", "--data-format=LEI16@24000", "-r", str(int(round(185 * float(settings["speed"]))))]
        if settings.get("system_voice"):
            command += ["-v", settings["system_voice"]]
        command.append(text if not text.startswith("-") else " " + text)
        done = subprocess.run(command, stdout=subprocess.DEVNULL, stderr=subprocess.PIPE)
    elif os.name == "nt":
        # Windows PowerShell's own speech synthesiser (System.Speech, part of every Windows 10 and 11). Untried:
        # nothing here has been run on Windows. Everything it is given goes in environment variables, so no
        # text needs quoting. Its rate runs from -10 to 10, about a third and three times the normal speed.
        script = ("Add-Type -AssemblyName System.Speech; $s = New-Object System.Speech.Synthesis.SpeechSynthesizer; "
                  "if ($env:KSP_VOICE_NAME) { try { $s.SelectVoice($env:KSP_VOICE_NAME) } catch { } }; "
                  "$s.Rate = [int]$env:KSP_VOICE_RATE; "
                  "$f = New-Object System.Speech.AudioFormat.SpeechAudioFormatInfo(22050, [System.Speech.AudioFormat.AudioBitsPerSample]::Sixteen, "
                  "[System.Speech.AudioFormat.AudioChannel]::Mono); $s.SetOutputToWaveFile($env:KSP_VOICE_OUT, $f); "
                  "$s.Speak($env:KSP_VOICE_TEXT); $s.Dispose()")
        rate = max(-10, min(10, int(round(10 * math.log(max(0.1, float(settings["speed"]))) / math.log(3)))))
        done = subprocess.run(["powershell", "-NoProfile", "-NonInteractive", "-Command", script], stdout=subprocess.DEVNULL, stderr=subprocess.PIPE,
                              env=dict(os.environ, KSP_VOICE_OUT=path, KSP_VOICE_TEXT=text, KSP_VOICE_RATE=str(rate),
                                       KSP_VOICE_NAME=settings.get("system_voice") or ""), **QUIET_START)
    else:
        # Linux and the rest: espeak-ng (or the older espeak). system_voice is one of its voices, e.g. en-gb.
        program = shutil.which("espeak-ng") or shutil.which("espeak")
        command = [program, "-w", path, "-s", str(int(round(165 * float(settings["speed"]))))]
        if settings.get("system_voice"):
            command += ["-v", settings["system_voice"]]
        done = subprocess.run(command + ["--", text], stdout=subprocess.DEVNULL, stderr=subprocess.PIPE)
    if done.returncode != 0 or not os.path.exists(path):
        raise VoiceError("The system voice failed: %s" % done.stderr.decode(errors="replace").strip()[:200])


def play_here(path, wait=True):
    """Play a WAV file on this computer's speakers (used when the game is not running)."""
    if sys.platform == "darwin":
        command = ["afplay", path]
    elif os.name == "nt":
        command = ["powershell", "-NoProfile", "-NonInteractive", "-Command", "(New-Object System.Media.SoundPlayer '%s').PlaySync()" % path.replace("'", "''")]
    else:
        player = shutil.which("paplay") or shutil.which("aplay")
        if not player:
            return False
        command = [player, path]
    if not shutil.which(command[0]):
        return False
    process = subprocess.Popen(command, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL, **QUIET_START)
    if wait:
        process.wait()
    return True


def wav_seconds(data):
    try:
        import io
        with wave.open(io.BytesIO(data), "rb") as f:
            return f.getnframes() / float(f.getframerate())
    except (wave.Error, EOFError, ZeroDivisionError):
        return max(1.0, len(data) / 48000.0)


class Speaker:
    """Turns text into speech with the best engine available and plays it in the game."""

    def __init__(self, settings=None):
        self.stamp = settings_stamp()
        self.settings = settings or load_settings()
        self.lock = threading.Lock()
        self.busy_until = 0.0            # when the lines already sent to the game will have been said
        self.kokoro_broken = None
        self.recent = {}                 # the last few lines made, in case they are asked for again at once

    def refresh(self):
        """Take up settings changed elsewhere since the last line: in the game's panel (the voice), or by another
        ksp_voice.py. Cheap enough to ask before every line."""
        stamp = settings_stamp()
        if stamp == self.stamp:
            return
        fresh = load_settings(strict=True)
        if fresh is not None:            # (a file caught half written is read again next time)
            self.stamp, self.settings = stamp, fresh

    def engine(self):
        self.refresh()
        wanted = self.settings.get("engine", "auto")
        if wanted in ("auto", "kokoro") and kokoro_installed() and not self.kokoro_broken:
            return "kokoro"
        if system_voice_available():
            return "system"
        return "none"

    def _cache(self, engine, spoken):
        s = self.settings
        tag = "%s|%s|%s|%s" % (engine, s["voice"] if engine == "kokoro" else s.get("system_voice", ""), s["speed"], spoken)
        return os.path.join(VOICE_DIR, "cache", hashlib.sha1(tag.encode("utf-8")).hexdigest() + ".wav")

    def ready(self, text):
        """True when this line has been made before and can be said without delay."""
        engine = self.engine()
        return engine == "none" or os.path.exists(self._cache(engine, pronounce(text)))

    def synthesize(self, text):
        """WAV bytes for the text, or None when this computer has no voice at all."""
        engine = self.engine()
        if engine == "none":
            return None
        spoken = pronounce(text)
        s = self.settings
        cache = self._cache(engine, spoken)
        short = len(spoken) <= 80        # call-outs repeat; whole sentences from a model do not
        if cache in self.recent:
            return self.recent[cache]
        if short and os.path.exists(cache):
            with open(cache, "rb") as f:
                return f.read()
        with tempfile.TemporaryDirectory(prefix="ksp-voice-") as folder:
            path = os.path.join(folder, "line.wav")
            if engine == "kokoro":
                try:
                    SPEAK.request({"op": "tts", "text": spoken, "voice": s["voice"], "speed": float(s["speed"]), "out": path}, timeout=90)
                except VoiceError as e:
                    if not system_voice_available():
                        raise
                    self.kokoro_broken = str(e)          # fall back for the rest of this run, and say why in status
                    system_speak(spoken, path, s)
            else:
                system_speak(spoken, path, s)
            with open(path, "rb") as f:
                data = f.read()
        self.recent[cache] = data
        while len(self.recent) > 8:
            self.recent.pop(next(iter(self.recent)))
        if short:
            try:
                os.makedirs(os.path.dirname(cache), exist_ok=True)
                with open(cache, "wb") as f:
                    f.write(data)
                self._prune(os.path.dirname(cache))
            except OSError:
                pass
        return data

    @staticmethod
    def _prune(folder, keep=400):
        names = [os.path.join(folder, n) for n in os.listdir(folder) if n.endswith(".wav")]
        if len(names) <= keep + 40:
            return
        names.sort(key=os.path.getmtime)
        for old in names[:len(names) - keep]:
            try:
                os.remove(old)
            except OSError:
                pass

    def say(self, text, effect=None, interrupt=False, caption=True, where="auto", wait=False, by=None):
        """Say something. where: game (inside KSP), here (this computer's speakers) or auto (the game when it is running).
        The intercom tone sounds only when the ship has been silent for a moment, not before every call-out.
        by: "ai" when the line is the AI's own words to the player (the game then knows the AI has spoken)."""
        text = tidy(text)
        if not text:
            raise VoiceError("Nothing to say.")
        in_game = where == "game" or (where == "auto" and ksp_mcp.game_running())
        total, sound, engine = 0.0, False, self.engine()
        for n, piece in enumerate(sentences(text)):
            data = self.synthesize(piece)
            seconds = wav_seconds(data) if data else max(1.5, len(piece) * 0.065)
            if in_game:
                chosen = effect or self.settings["effect"]
                params = {"text": piece, "effect": chosen, "volume": float(self.settings["volume"]), "caption": bool(caption),
                          "interrupt": bool(interrupt and n == 0), "chirp": chosen != "none" and time.time() - self.busy_until > 3.0}
                if data:
                    params["wav_base64"] = base64.b64encode(data).decode("ascii")
                if by:
                    params["by"] = by
                reply = ksp_mcp.rpc("voice_play", params, timeout=20)
                seconds = float(reply.get("seconds") or seconds)
                with self.lock:
                    now = time.time()
                    self.busy_until = (now if interrupt and n == 0 else max(now, self.busy_until)) + seconds + 0.35
            elif data:
                with tempfile.TemporaryDirectory(prefix="ksp-voice-") as folder:
                    path = os.path.join(folder, "line.wav")
                    with open(path, "wb") as f:
                        f.write(data)
                    if not play_here(path):
                        print(piece)
            else:
                print(piece)
            sound = sound or bool(data)
            total += seconds
        if wait and in_game:
            time.sleep(max(0.0, self.busy_until - time.time()))
        return {"said": text, "seconds": round(total, 1), "where": "game" if in_game else "here", "engine": engine, "sound": sound}

    def backlog(self):
        """Seconds of speech already handed to the game and not yet said."""
        return max(0.0, self.busy_until - time.time())


# --------------------------------------------------------------------------- the ship's own commentary


class Narration(threading.Thread):
    """Collects the lines the ship's computer wants said and speaks them one at a time: warnings at once,
    then milestones before call-outs, and nothing that has gone stale while it waited its turn."""

    def __init__(self, speaker, log=None):
        threading.Thread.__init__(self, daemon=True)
        self.speaker = speaker
        self.stop_flag = threading.Event()
        self.log = log
        self.spoken = 0
        self.dropped = 0
        self.problem = None
        self.pending = []                    # [priority, expires at, order of arrival, text]
        self.wake = threading.Condition()

    def stop(self):
        self.stop_flag.set()
        with self.wake:
            self.wake.notify_all()

    def collect(self):
        """Long-poll the game for new lines and put them on the pending list."""
        since, order = None, 0
        while not self.stop_flag.is_set():
            params = {"timeout_s": 20, "client": CLIENT, "detail": self.speaker.settings["detail"]}
            if since is not None:
                params["since"] = since
            try:
                feed = ksp_mcp.rpc("voice_feed", params, timeout=30)
            except ksp_mcp.BridgeError as e:
                self.problem = str(e)
                since = None                 # the game went away; start from "now" when it is back
                self.stop_flag.wait(4.0)
                continue
            if feed.get("busy"):
                self.problem = "another narrator (%s) is already speaking for this game" % feed.get("holder")
                self.stop_flag.wait(6.0)
                continue
            self.problem = None
            since = feed.get("last_id", since)
            now = time.time()
            with self.wake:
                for line in feed.get("lines") or []:
                    order += 1
                    expires = now + float(line.get("ttl_s") or 10) - float(line.get("age_s") or 0)
                    self.pending.append([int(line.get("priority") or 1), expires, order, line.get("text") or ""])
                self.wake.notify_all()

    def best(self):
        """The most important line still worth saying (the oldest among equals); stale ones are thrown out."""
        now = time.time()
        fresh = [line for line in self.pending if line[1] > now and line[3]]
        self.dropped += len(self.pending) - len(fresh)
        self.pending[:] = fresh
        return min(fresh, key=lambda line: (-line[0], line[2])) if fresh else None

    def run(self):
        threading.Thread(target=self.collect, daemon=True).start()
        prepared_for, to_prepare = None, []
        while not self.stop_flag.is_set():
            voice = (self.speaker.engine(), self.speaker.settings["voice"], self.speaker.settings["speed"], self.speaker.settings["system_voice"])
            if voice != prepared_for:
                # At the start, and whenever the voice has been changed (in the game's panel, say): the standard
                # call-outs not yet made in this voice are made while there is nothing else to say.
                prepared_for = voice
                to_prepare = [line for line in CALL_OUTS if not self.speaker.ready(line)]
            with self.wake:
                line = self.best()
                if line is None:
                    self.wake.wait(0.1 if to_prepare else 1.0)
                    line = self.best()
            try:
                if line is None:
                    if to_prepare:           # nothing to say: make a standard call-out that is not ready yet
                        self.speaker.synthesize(to_prepare.pop(0))
                    continue
                urgent = line[0] >= 3
                if not urgent and self.speaker.backlog() > 0.05:
                    # The voice is busy. Make the line meanwhile, then look again: something more
                    # important may have come in by the time the voice is free.
                    self.speaker.synthesize(line[3])
                    with self.wake:
                        self.wake.wait(min(0.25, self.speaker.backlog()))
                    continue
                with self.wake:
                    if line in self.pending:
                        self.pending.remove(line)
                self.speaker.say(line[3], interrupt=urgent, where="game")
                self.spoken += 1
                if self.log:
                    self.log(line[3])
            except (VoiceError, ksp_mcp.BridgeError) as e:
                self.problem = str(e)
                with self.wake:
                    if line is not None and line in self.pending:
                        self.pending.remove(line)
                self.stop_flag.wait(1.0)


# --------------------------------------------------------------------------- listening

NOISE = {"", "you", "thank you", "thanks", "thanks for watching", "thank you for watching", "bye", "okay", "ok", "so", "the", "uh", "um", "hmm"}


def worth_hearing(text):
    """Speech recognisers invent a few stock phrases out of silence and coughs; ignore those."""
    plain = re.sub(r"[^a-z' ]", "", (text or "").lower()).strip()
    return plain not in NOISE and len(plain) >= 2 and not text.strip().startswith(("[", "("))


def transcribe(path):
    if not whisper_installed():
        raise VoiceError("Speech recognition is not installed. Run %s in the mod's folder (or type your commands)." % SETUP)
    return (HEAR.request({"op": "stt", "wav": path}, timeout=120).get("text") or "").strip()


class Listener(threading.Thread):
    """The player's microphone: waits for the push-to-talk key (or Enter, or a wake word), records, and
    hands the words to on_heard(text)."""

    def __init__(self, on_heard, trigger="key", wake="computer", settings=None, log=None):
        threading.Thread.__init__(self, daemon=True)
        self.on_heard = on_heard
        self.trigger = trigger
        self.wake = (wake or "").lower().strip()
        self.settings = settings or load_settings()
        self.log = log or (lambda message: None)
        self.stop_flag = threading.Event()
        self.problem = None
        self.heard = 0
        self.folder = tempfile.mkdtemp(prefix="ksp-voice-")

    def stop(self):
        self.stop_flag.set()

    def run(self):
        try:
            if not whisper_installed():
                raise VoiceError("Speech recognition is not installed. Run %s in the mod's folder." % SETUP)
            if self.trigger == "key":
                try:
                    ksp_mcp.rpc("voice_config", {"key": self.settings["key"]}, timeout=10)
                except ksp_mcp.BridgeError as e:
                    if "Unknown key" in str(e):
                        raise VoiceError(str(e))
            HEAR.request({"op": "ping"}, timeout=60)
            while not self.stop_flag.is_set():
                text = {"key": self.by_key, "enter": self.by_enter, "open": self.by_wake_word}[self.trigger]()
                if text and worth_hearing(text):
                    self.heard += 1
                    self.log('heard: "%s"' % text)
                    try:
                        self.on_heard(text)
                    except (VoiceError, ksp_mcp.BridgeError) as e:
                        self.log("could not pass that on: %s" % e)
        except VoiceError as e:
            self.problem = str(e)
            self.log("listening stopped: %s" % e)
        finally:
            shutil.rmtree(self.folder, ignore_errors=True)

    def _check(self, result):
        if result.get("silent"):
            raise VoiceError("The microphone gives only silence. Allow microphone access for the app this runs in "
                             "(macOS: System Settings, Privacy & Security, Microphone; Windows: Settings, Privacy & security, "
                             "Microphone, with 'Let desktop apps access your microphone' on), then start listening again.")
        return result

    def by_key(self):
        """Push-to-talk: record while the key is held in the game."""
        seen = getattr(self, "_seen", -1)
        try:
            state = ksp_mcp.rpc("voice_key", {"seen": seen, "timeout_s": 20}, timeout=30)
        except ksp_mcp.BridgeError as e:
            self.problem = str(e)
            self.stop_flag.wait(4.0)
            return None
        self.problem = None
        self._seen = state.get("changes", seen)
        if not state.get("down"):
            return None
        wav, stop_file = os.path.join(self.folder, "heard.wav"), os.path.join(self.folder, "stop")
        if os.path.exists(stop_file):
            os.remove(stop_file)
        result = {}

        def record():
            try:
                result.update(HEAR.request({"op": "record", "mode": "hold", "stop_file": stop_file, "out": wav, "max_seconds": 30}, timeout=60))
            except VoiceError as e:
                result["error"] = str(e)

        recorder = threading.Thread(target=record, daemon=True)
        recorder.start()
        while recorder.is_alive() and not self.stop_flag.is_set():
            try:
                state = ksp_mcp.rpc("voice_key", {"seen": self._seen, "timeout_s": 5}, timeout=15)
                self._seen = state.get("changes", self._seen)
                if not state.get("down"):
                    break
            except ksp_mcp.BridgeError:
                break
        open(stop_file, "w").close()
        recorder.join(timeout=10)
        if result.get("error"):
            raise VoiceError(result["error"])
        self._check(result)
        if not result.get("heard") or float(result.get("seconds") or 0) < 0.4:
            return None
        return transcribe(wav)

    def by_enter(self):
        try:
            input("\nPress Enter, then speak. ")
        except EOFError:
            self.stop_flag.set()
            return None
        return self.until_quiet(wait_seconds=8)

    def by_wake_word(self):
        text = self.until_quiet(wait_seconds=20)
        if not text or not self.wake:
            return text
        plain = re.sub(r"^[^a-z]+", "", text.lower())
        if not plain.startswith(self.wake):
            return None
        return re.sub(r"^[\s,.:;!?-]+", "", text[text.lower().index(self.wake) + len(self.wake):]) or None

    def until_quiet(self, wait_seconds):
        wav = os.path.join(self.folder, "heard.wav")
        result = self._check(HEAR.request({"op": "record", "out": wav, "max_seconds": 20, "wait_seconds": wait_seconds}, timeout=wait_seconds + 40))
        return transcribe(wav) if result.get("heard") else None


def pass_to_claude_app(text):
    """Leave what was said with the game, where the Claude app collects it (the ksp_voice tool, action listen)."""
    ksp_mcp.rpc("voice_heard", {"text": text}, timeout=10)


class ClaudeCode:
    """Claude through the Claude Code command line, signed in with a Claude account (no API key)."""

    def __init__(self):
        self.program = shutil.which("claude")
        self.session = str(uuid.uuid4())
        self.started = False
        self.servers = None

    def ask(self, text):
        if not self.program:
            raise VoiceError("The 'claude' command was not found. Install Claude Code and sign in with `claude`, or use the Claude app.")
        if self.servers is None:
            # The tools go in a file and what was said goes in on stdin, not on the command line: on Windows the
            # 'claude' command may be a batch file (claude.cmd), and cmd.exe would read quotes, % and & in either.
            folder = tempfile.mkdtemp(prefix="ksp-voice-")
            atexit.register(shutil.rmtree, folder, True)
            self.servers = os.path.join(folder, "servers.json")
            with open(self.servers, "w", encoding="utf-8") as f:
                json.dump({"mcpServers": {"ksp": {"command": sys.executable, "args": [os.path.join(HERE, "ksp_mcp.py")]}}}, f)
        command = [self.program, "-p", "--mcp-config", self.servers, "--strict-mcp-config", "--allowedTools", "mcp__ksp",
                   "--tools", "", "--append-system-prompt", BRIEF, "--output-format", "text"]
        command += ["--resume", self.session] if self.started else ["--session-id", self.session]
        try:
            done = subprocess.run(command, input=text.encode("utf-8"), stdout=subprocess.PIPE, stderr=subprocess.PIPE,
                                  cwd=os.path.dirname(HERE), timeout=3600, **QUIET_START)
        except subprocess.TimeoutExpired:
            raise VoiceError("Claude Code did not answer within an hour.")
        except OSError as e:
            raise VoiceError("Could not start Claude Code: %s" % e)
        reply = done.stdout.decode("utf-8", errors="replace").strip()
        if done.returncode != 0:
            raise VoiceError("Claude Code said: %s" % (reply or done.stderr.decode("utf-8", errors="replace").strip())[:300])
        self.started = True
        return reply


# --------------------------------------------------------------------------- one place for everything (CLI and MCP tool)

STATE = {"speaker": None, "narration": None, "listener": None}


def speaker():
    if STATE["speaker"] is None:
        STATE["speaker"] = Speaker()
    return STATE["speaker"]


def narration(on, log=None):
    current = STATE["narration"]
    if on and (current is None or not current.is_alive()):
        STATE["narration"] = Narration(speaker(), log)
        STATE["narration"].start()
    elif not on and current is not None:
        current.stop()
        STATE["narration"] = None
    return STATE["narration"]


def game_voice():
    try:
        return ksp_mcp.rpc("voice_status", timeout=10)
    except ksp_mcp.BridgeError:
        return None


def in_use():
    """Whether the ship's voice is in use just now: somebody is narrating, or listening for the player's voice."""
    for one in (STATE["narration"], STATE["listener"]):
        if one is not None and one.is_alive():
            return True
    game = game_voice()
    return bool(game and (game.get("narrating") or game.get("listening")))


def status():
    s = speaker()
    out = {
        "voice_engine": s.engine(),
        "kokoro_installed": kokoro_installed(),
        "speech_recognition_installed": whisper_installed(),
        "settings": {k: v for k, v in s.settings.items() if k != "system_voice" or v},
    }
    if s.kokoro_broken:
        out["kokoro_problem"] = s.kokoro_broken
    if not kokoro_installed():
        out["note"] = ("Using the operating system's voice." if s.engine() == "system" else "No voice on this computer: lines appear as captions only.") + \
            " %s in the mod's folder installs the Kokoro voice and speech recognition (about 0.6 GB)." % SETUP
    n = STATE["narration"]
    if n is not None and n.is_alive():
        out["narrating_here"] = {"lines_spoken": n.spoken, "skipped_as_stale": n.dropped}
        if n.problem:
            out["narrating_here"]["problem"] = n.problem
    listener = STATE["listener"]
    if listener is not None:
        out["listening_here"] = {"alive": listener.is_alive(), "heard": listener.heard}
        if listener.problem:
            out["listening_here"]["problem"] = listener.problem
    game = game_voice()
    out["game"] = game if game is not None else "KSP is not running: speech plays on this computer's speakers instead."
    return out


def listen_for_app(budget_s):
    """Wait for the player to say something; used by the Claude app through the ksp_voice tool."""
    game = game_voice()
    if game is None:
        raise VoiceError("KSP is not running, so there is no push-to-talk key to hold. Start the game first.")
    mine = STATE["listener"]
    if not game.get("listening") and (mine is None or not mine.is_alive()):
        # Nobody has the microphone: listen from here. A `ksp_voice.py listen` in a terminal takes precedence.
        mine = STATE["listener"] = Listener(pass_to_claude_app, trigger="key")
        mine.start()
        time.sleep(1.5)
    deadline = time.time() + max(5.0, budget_s)
    c = ksp_mcp.ctx()
    while True:
        if mine is not None and mine.problem and not mine.is_alive():
            problem = mine.problem
            STATE["listener"] = None
            raise VoiceError(problem + " Or have the player run `%s \"%s\" listen` in a terminal, which uses the terminal's microphone permission."
                             % ("py" if os.name == "nt" else "python3", os.path.abspath(__file__)))
        if c.cancelled.is_set():
            raise VoiceError("Listening was cancelled.")
        left = deadline - time.time()
        reply = ksp_mcp.rpc("voice_next", {"timeout_s": max(0.5, min(25.0, left))}, timeout=35)
        if reply.get("heard"):
            return {"heard": reply["heard"], "more_waiting": reply.get("waiting", 0),
                    "next": "Do what was asked, tell the player the result with action=say, then call action=listen again."}
        if deadline - time.time() <= 1.0:
            return {"heard": None, "talk_key": reply.get("talk_key"),
                    "note": "Nothing was said yet. Call action=listen again to keep listening (the player holds the talk key in the game while speaking)."}
        c.progress("listening")


def tool(a, budget_s=45.0):
    """The ksp_voice MCP tool."""
    action = a.get("action") or "status"
    try:
        if action == "say":
            if not a.get("text"):
                raise VoiceError("Give the text to say.")
            return speaker().say(a["text"], effect=a.get("effect"), interrupt=bool(a.get("interrupt")), by="ai")
        if action in ("on", "off"):
            changes = {k: a[k] for k in ("detail",) if a.get(k) is not None}
            if changes:
                speaker().settings = save_settings(changes)
            narration(action == "on")
            if action == "off":
                try:
                    ksp_mcp.rpc("voice_stop", timeout=10)
                except ksp_mcp.BridgeError:
                    pass
            out = status()
            out["narration"] = "on: the ship's computer now calls out the flight by itself" if action == "on" else "off"
            return out
        if action == "listen":
            return listen_for_app(budget_s)
        if action == "set":
            changes = {k: a[k] for k in ("voice", "speed", "effect", "volume", "detail", "key", "engine") if a.get(k) is not None}
            if not changes:
                raise VoiceError("Give at least one of: voice, speed, effect, volume, detail, key, engine.")
            speaker().settings = save_settings(changes)
            config = {k: changes[k] for k in ("key", "detail") if k in changes}
            if config:
                try:
                    ksp_mcp.rpc("voice_config", config, timeout=10)
                except ksp_mcp.BridgeError as e:
                    if "Unknown key" in str(e):
                        raise VoiceError(str(e))
            return status()
        if action == "voices":
            if not kokoro_installed():
                return {"voices": [], "note": "Kokoro is not installed (%s); the operating system's voice is used." % SETUP}
            names = SPEAK.request({"op": "voices"}, timeout=90)["voices"]
            return {"current": speaker().settings["voice"], "english": [n for n in names if n[:1] in "ab"],
                    "other_languages": [n for n in names if n[:1] not in "ab"],
                    "key": "first letter: a American, b British; second letter: f female, m male"}
        if action == "stop":
            return ksp_mcp.rpc("voice_stop", timeout=10)
        if action == "status":
            return status()
    except VoiceError as e:
        raise ksp_mcp.BridgeError(str(e))
    raise ksp_mcp.BridgeError("Unknown action '%s'. Use status, on, off, say, listen, set, voices or stop." % action)


# --------------------------------------------------------------------------- command line


def main(argv):
    command = argv[1] if len(argv) > 1 else "status"
    rest = argv[2:]
    os.environ.setdefault("KSP_MCP_MAX_CALL_S", "3600")
    for stream in (sys.stdout, sys.stderr):
        if hasattr(stream, "reconfigure"):
            stream.reconfigure(errors="replace")    # (output in a narrow code page, as on Windows, must not stop it over one character)
    try:
        if command in ("-h", "--help", "help"):
            print(__doc__)
        elif command == "status":
            print(json.dumps(status(), indent=2))
        elif command == "say":
            where = "auto"
            if rest and rest[0] in ("--here", "--game"):
                where, rest = rest[0][2:], rest[1:]
            print(json.dumps(speaker().say(" ".join(rest) or "All systems nominal.", where=where, wait=True)))
        elif command == "voices":
            print(json.dumps(tool({"action": "voices"}), indent=2))
        elif command == "set":
            changes = dict(item.split("=", 1) for item in rest if "=" in item)
            if not changes:
                sys.exit("usage: ksp_voice.py set voice=bf_emma speed=1.0 effect=ship volume=1.0 detail=normal key=Y engine=auto")
            print(json.dumps(tool(dict(changes, action="set"))["settings"], indent=2))
        elif command == "prepare":
            waiting = [line for line in CALL_OUTS if not speaker().ready(line)]
            print("Preparing %d standard call-outs with the %s voice (%d are ready)..." % (len(waiting), speaker().engine(), len(CALL_OUTS) - len(waiting)))
            for line in waiting:
                speaker().synthesize(line)
            print("Done.")
        elif command == "mic":
            if not whisper_installed():
                sys.exit("Speech recognition is not installed. Run %s first." % SETUP)
            result = HEAR.request({"op": "mic", "seconds": 2.0}, timeout=60)
            print(json.dumps(result))
            print("The microphone gives only silence: allow microphone access for this terminal." if result.get("silent")
                  else "The microphone works. Speech is picked up above a level of about 260.")
        elif command == "narrate":
            print("The ship's computer is narrating (%s voice, detail %s). Ctrl+C stops it." % (speaker().engine(), speaker().settings["detail"]))
            thread = narration(True, log=lambda line: print("  " + line))
            while thread.is_alive():
                time.sleep(1.0)
                if thread.problem and thread.problem != getattr(thread, "_shown", None):
                    thread._shown = thread.problem
                    print("  (%s)" % thread.problem)
        elif command == "listen":
            trigger, brain, wake = "key", "app", "computer"
            while rest:
                option = rest.pop(0)
                if option == "--trigger" and rest:
                    trigger = rest.pop(0)
                elif option == "--brain" and rest:
                    brain = rest.pop(0)
                elif option == "--wake" and rest:
                    wake = rest.pop(0)
                else:
                    sys.exit("usage: ksp_voice.py listen [--trigger key|enter|open] [--wake computer] [--brain app|claude]")
            if trigger not in ("key", "enter", "open") or brain not in ("app", "claude"):
                sys.exit("usage: ksp_voice.py listen [--trigger key|enter|open] [--wake computer] [--brain app|claude]")
            if brain == "claude":
                claude = ClaudeCode()

                def on_heard(text):
                    ksp_mcp.rpc("voice_heard", {"text": text, "queue": False}, timeout=10)
                    reply = claude.ask(text)
                    print("  Claude: " + reply)
                    speaker().say(reply, by="ai")
            else:
                told = [0.0]

                def nobody_answered():
                    # The words wait in the game until Claude collects them. If Claude is not listening
                    # the player would otherwise hear nothing at all and take the microphone for broken.
                    game = game_voice()
                    if game and game.get("heard_waiting") and time.time() - told[0] > 60:
                        told[0] = time.time()
                        print("  (nobody collected that: Claude is not listening)")
                        speaker().say("I heard you, but Claude is not listening yet. In the Claude app, ask Claude to listen for voice commands.")

                def on_heard(text):
                    pass_to_claude_app(text)
                    check = threading.Timer(10.0, nobody_answered)
                    check.daemon = True
                    check.start()
            how = {"key": "Hold %s in the game while you talk." % load_settings()["key"], "enter": "Press Enter here before each command.",
                   "open": 'Say "%s" and then the command.' % wake}[trigger]
            print("Listening. " + how + (" In the Claude app, ask Claude to listen for your voice commands." if brain == "app" else "") + " Ctrl+C stops it.")
            listener = Listener(on_heard, trigger=trigger, wake=wake, log=lambda line: print("  " + line))
            listener.start()
            while listener.is_alive():
                time.sleep(0.5)
            if listener.problem:
                sys.exit(1)
        else:
            sys.exit(__doc__)
    except (VoiceError, ksp_mcp.BridgeError) as e:
        sys.exit("error: %s" % e)
    except KeyboardInterrupt:
        print()
    finally:
        SPEAK.stop()
        HEAR.stop()


if __name__ == "__main__":
    main(sys.argv)
