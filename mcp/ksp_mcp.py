#!/usr/bin/env python3
"""
MCP server for KSP AI Bridge.

Speaks the Model Context Protocol over stdio and forwards every tool call to the AI Bridge plugin
running inside Kerbal Space Program (a loopback HTTP endpoint the plugin opens while the game runs).
Standard library only, so it needs nothing installed beyond Python 3.8+.

    python3 ksp_mcp.py                      run as an MCP server (what Claude launches)
    python3 ksp_mcp.py call status          call one bridge command from a shell, for debugging
    python3 ksp_mcp.py tools                print the tool list

Set KSP_DIR if the game is not in the default Steam location.
"""
import base64
import json
import os
import subprocess
import sys
import threading
import time
import urllib.error
import urllib.request

# Run as a script this file is "__main__"; give it its real name too, so helper modules that import
# ksp_mcp (the voice edition) share this server's state instead of loading a second copy.
sys.modules.setdefault("ksp_mcp", sys.modules[__name__])

SERVER_NAME = "ksp-ai-bridge"
SERVER_VERSION = "0.8.0"
PROTOCOL_VERSIONS = ["2025-11-25", "2025-06-18", "2025-03-26", "2024-11-05"]

# --------------------------------------------------------------------------- locating the game


def candidate_dirs():
    env = os.environ.get("KSP_DIR")
    if env:
        yield os.path.expanduser(env)
    home = os.path.expanduser("~")
    yield os.path.join(home, "Library/Application Support/Steam/steamapps/common/Kerbal Space Program")
    yield os.path.join(home, ".steam/steam/steamapps/common/Kerbal Space Program")
    yield os.path.join(home, ".local/share/Steam/steamapps/common/Kerbal Space Program")
    for drive in ("C:", "D:", "E:"):
        yield drive + "\\Program Files (x86)\\Steam\\steamapps\\common\\Kerbal Space Program"
        yield drive + "\\SteamLibrary\\steamapps\\common\\Kerbal Space Program"
        yield drive + "\\Program Files\\Epic Games\\KerbalSpaceProgram\\English"


def ksp_dir():
    for d in candidate_dirs():
        if os.path.isdir(os.path.join(d, "GameData")):
            return d
    return None


class BridgeError(Exception):
    """An error worth showing to the model as the tool result."""


def bridge_info():
    d = ksp_dir()
    if not d:
        raise BridgeError("Kerbal Space Program was not found. Set the KSP_DIR environment variable for this MCP server to the game folder (the one that contains GameData).")
    if not os.path.exists(os.path.join(d, "GameData", "AIBridge")):
        raise BridgeError("The AI Bridge mod is not installed in " + d + "/GameData. Run ./build.sh in the mod's project folder to build and install it.")
    path = os.path.join(d, "GameData", "AIBridge", "PluginData", "bridge.json")
    try:
        with open(path, "r", encoding="utf-8") as f:
            return json.load(f)
    except (OSError, ValueError):
        raise BridgeError("KSP is not running (or is still starting up). Use ksp_start_game to launch it, then ksp_status.")


# --------------------------------------------------------------------------- how long may one call take?
#
# MCP clients differ: some abandon a tool call after 60 seconds, others allow hours or move long calls to
# the background. Rather than assume, the server measures. It starts with a budget every known client
# accepts, lets side-effect-free waits run a little past what has been proven, and remembers the outcome
# per client: a call that completes raises the budget, a cancelled or abandoned call pins the limit.

SAFE_CALL_S = 45.0          # accepted by every client seen so far
MAX_WAIT_S = 3600.0         # never wait longer than this in one call, whatever the client allows
RPC_SLICE_S = 30.0          # how long one request to the game may block; long waits are made of slices


class Limits:
    """Per-client record of the longest call that worked and the shortest that was cut off."""

    def __init__(self):
        self.lock = threading.Lock()
        self.client = "unknown"
        self.data = {}
        self.fixed = None
        env = os.environ.get("KSP_MCP_MAX_CALL_S")
        if env:
            try:
                self.fixed = max(5.0, float(env))
            except ValueError:
                pass
        self._load()

    def _path(self):
        d = ksp_dir()
        if d and os.path.isdir(os.path.join(d, "GameData", "AIBridge")):
            return os.path.join(d, "GameData", "AIBridge", "PluginData", "mcp_call_limits.json")
        return os.path.join(os.path.expanduser("~"), ".ksp_ai_bridge_call_limits.json")

    def _load(self):
        try:
            with open(self._path(), "r", encoding="utf-8") as f:
                self.data = json.load(f)
        except (OSError, ValueError):
            self.data = {}

    def _save(self):
        try:
            path = self._path()
            os.makedirs(os.path.dirname(path), exist_ok=True)
            # Merge into whatever is on disk so two servers (two sessions) do not erase each other's findings.
            merged = {}
            try:
                with open(path, "r", encoding="utf-8") as f:
                    merged = json.load(f)
            except (OSError, ValueError):
                merged = {}
            if self.client in self.data:
                merged[self.client] = self.data[self.client]
            else:
                merged.pop(self.client, None)
            with open(path, "w", encoding="utf-8") as f:
                json.dump(merged, f, indent=1)
        except OSError:
            pass

    def _entry(self):
        return self.data.setdefault(self.client, {})

    def set_client(self, info):
        name = (info or {}).get("name") or "unknown"
        with self.lock:
            self.client = str(name)

    def proven(self):
        """Longest duration known to work with this client."""
        with self.lock:
            return max(SAFE_CALL_S, float(self._entry().get("ok_max", 0)))

    def ceiling(self):
        """A duration known to be too long, or None."""
        with self.lock:
            v = self._entry().get("cut_min")
            return float(v) if v else None

    def budget(self, explore=False):
        """Seconds a call may take. With explore, go past what is proven to find out whether more works."""
        if self.fixed:
            return self.fixed
        proven, ceiling = self.proven(), self.ceiling()
        if ceiling is not None:
            return max(15.0, min(proven, ceiling - 12.0))
        if explore:
            return min(MAX_WAIT_S, proven * 2.0)
        return proven

    def record_ok(self, seconds):
        with self.lock:
            e = self._entry()
            if seconds > float(e.get("ok_max", 0)):
                e["ok_max"] = round(seconds, 1)
                # A proven duration at or above an old ceiling means the client was reconfigured or updated.
                if e.get("cut_min") and seconds >= float(e["cut_min"]) - 1.0:
                    e.pop("cut_min", None)
                self._save()

    def record_cut(self, seconds, how):
        with self.lock:
            e = self._entry()
            if not e.get("cut_min") or seconds < float(e["cut_min"]):
                e["cut_min"] = round(seconds, 1)
                e["cut_how"] = how
                if float(e.get("ok_max", 0)) >= seconds:
                    e["ok_max"] = round(max(0.0, seconds - 15.0), 1)
                self._save()

    def reset(self):
        with self.lock:
            self.data.pop(self.client, None)
            self._save()

    def describe(self):
        if self.fixed:
            return {"call_budget_s": self.fixed, "source": "KSP_MCP_MAX_CALL_S"}
        out = {"client": self.client, "call_budget_s": round(self.budget()), "longest_call_that_worked_s": round(self.proven())}
        ceiling = self.ceiling()
        if ceiling is not None:
            out["client_cut_a_call_at_s"] = round(ceiling)
        else:
            out["note"] = "no client limit seen yet; waits are allowed to run up to %d s to find out" % round(self.budget(explore=True))
        return out


LIMITS = Limits()


class CallContext:
    """State of one tools/call request: lets a long tool notice cancellation and report progress."""

    def __init__(self, server, request_id, progress_token, name):
        self.server = server
        self.id = request_id
        self.progress_token = progress_token
        self.name = name
        self.started = time.time()
        self.cancelled = threading.Event()
        self.exploring = False
        self.last_progress = 0.0

    def elapsed(self):
        return time.time() - self.started

    def progress(self, message):
        if self.server is None or self.progress_token is None:
            return
        if time.time() - self.last_progress < 8.0:
            return
        self.last_progress = time.time()
        self.server.send({"jsonrpc": "2.0", "method": "notifications/progress",
                          "params": {"progressToken": self.progress_token, "progress": round(self.elapsed()), "message": message}})


_local = threading.local()


def ctx():
    """The call context of the current thread (a detached one when run from the command line)."""
    c = getattr(_local, "ctx", None)
    if c is None:
        c = CallContext(None, None, None, "cli")
        _local.ctx = c
    return c


def rpc(method, params=None, timeout=30.0):
    """Call one bridge command; returns its result or raises BridgeError with the game's explanation."""
    timeout = min(float(timeout), SAFE_CALL_S)
    info = bridge_info()
    body = json.dumps({"method": method, "params": params or {}, "timeout": timeout}).encode("utf-8")
    req = urllib.request.Request(
        "http://127.0.0.1:%d/rpc" % int(info["port"]),
        data=body,
        headers={"Content-Type": "application/json", "X-AIBridge-Token": info["token"]},
    )
    try:
        with urllib.request.urlopen(req, timeout=timeout + 15) as resp:
            reply = json.loads(resp.read().decode("utf-8"))
    except urllib.error.HTTPError as e:
        raise BridgeError("The game rejected the request (HTTP %d). If KSP was restarted, try again." % e.code)
    except (urllib.error.URLError, ConnectionError, TimeoutError, OSError) as e:
        raise BridgeError("Could not reach KSP: %s. The game may have closed or may be frozen on a loading screen; check with ksp_status, or launch it with ksp_start_game." % getattr(e, "reason", e))
    if not reply.get("ok"):
        raise BridgeError(reply.get("error") or "The game reported an unspecified error.")
    return reply.get("result")


def game_running():
    try:
        info = bridge_info()
        with urllib.request.urlopen("http://127.0.0.1:%d/health" % int(info["port"]), timeout=3) as resp:
            return resp.status == 200
    except Exception:
        return False


EVENT_CURSOR = {"id": None}


def note_events(result):
    """Remember the newest flight event seen so the next wait reports everything after it."""
    if isinstance(result, dict) and isinstance(result.get("last_event_id"), (int, float)):
        EVENT_CURSOR["id"] = int(result["last_event_id"])
    return result


def wait_in_game(until, total_s, stop_on_event=True):
    """Wait for conditions in slices short enough for any client, up to total_s, honouring cancellation."""
    c = ctx()
    deadline = time.time() + total_s
    first = True
    while True:
        remaining = deadline - time.time()
        if c.cancelled.is_set():
            raise BridgeError("The wait was cancelled by the client.")
        slice_s = max(0.5, min(RPC_SLICE_S, remaining))
        params = {"timeout_s": slice_s, "stop_on_event": stop_on_event}
        if until:
            params["until"] = until
        if EVENT_CURSOR["id"] is not None:
            params["since_event"] = EVENT_CURSOR["id"]
        result = rpc("wait", params, timeout=slice_s + 8)
        first = False
        note_events(result)
        if result.get("stopped_because") != "timeout" or deadline - time.time() <= 0.5:
            return result
        v = result.get("vessel") or {}
        c.progress(str(v.get("autopilot") or v.get("flight_plan") or v.get("situation") or "waiting"))


def settle_scene(error_text, budget_s):
    """A scene change outlasted one request to the game: keep watching until it is ready or time is up."""
    if "Still working" not in error_text:
        raise BridgeError(error_text)
    c = ctx()
    deadline = c.started + budget_s
    while time.time() < deadline - 2:
        time.sleep(2.0)
        try:
            st = rpc("status", timeout=10)
        except BridgeError:
            continue
        if st.get("scene_ready") and st.get("scene") != "loading":
            st["note"] = "The operation took longer than one request; this is the state after it finished."
            return st
        c.progress("game is loading")
    raise BridgeError("KSP is still busy loading. Call ksp_status in a few seconds to see the result before repeating the command.")


def slow_rpc(method, params=None):
    """Commands that may load a scene: wait for the game within the call budget instead of failing early."""
    budget = LIMITS.budget()
    try:
        return rpc(method, params, timeout=min(SAFE_CALL_S, budget - 3))
    except BridgeError as e:
        return settle_scene(str(e), budget - 3)


# --------------------------------------------------------------------------- formatting helpers


def dumps(obj):
    return json.dumps(obj, ensure_ascii=False, separators=(", ", ": "))


def text(obj):
    return [{"type": "text", "text": obj if isinstance(obj, str) else dumps(obj)}]


def clean(args, *names):
    """Pick the named arguments that were actually supplied."""
    return {n: args[n] for n in names if n in args and args[n] is not None}


def parts_table(result):
    lines = ["%d parts match, showing %d." % (result.get("matched", 0), result.get("returned", 0))]
    if "categories" in result:
        lines.append("Categories: " + ", ".join("%s (%d)" % kv for kv in result["categories"].items()))
    if "from" in result:
        lines.append("Parts come from: " + ", ".join("%s (%d)" % kv for kv in result["from"].items())
                     + ". Anything not stock or an expansion is a mod's; search with mod=<name> to see only its parts.")
    if "themes" in result:
        lines.append("Looks parts come in (give one as 'theme' to ksp_build, or as a part's 'variant'): " + ", ".join("%s (%d parts)" % kv for kv in result["themes"].items()))
    for p in result.get("parts", []):
        bits = [p["name"], '"%s"' % p["title"], p["category"]]
        if p.get("from"):
            bits.append("from the mod " + p["from"] if p["from"] not in ("Making History", "Breaking Ground") else p["from"])
        if p.get("variants"):
            bits.append("%s looks" % p["variants"])
        mass = "%st" % p["dry_mass_t"]
        if "wet_mass_t" in p:
            mass = "%st full / %st empty" % (p["wet_mass_t"], p["dry_mass_t"])
        bits.append(mass)
        bits.append("cost %s" % p.get("cost"))
        if p.get("size"):
            bits.append("size " + p["size"])
        engines = [p["engine"]] if "engine" in p else p.get("engine_modes", [])
        for e in engines:
            s = "engine%s: %s kN vac / %s kN sea level, Isp %s / %s, %s" % (
                " (" + e["mode"] + ")" if e.get("mode") else "", e["thrust_vac_kn"], e["thrust_asl_kn"], e["isp_vac"], e["isp_asl"], e["propellants"])
            if "gimbal_deg" in e:
                s += ", gimbal %s°" % e["gimbal_deg"]
            if e.get("throttleable") is False:
                s += ", no throttle"
            bits.append(s)
        if "resources" in p:
            bits.append("holds " + ", ".join("%s %s" % kv for kv in p["resources"].items()))
        if "crew" in p:
            bits.append("crew %s" % p["crew"])
        if "command" in p:
            bits.append(p["command"])
        if "decoupler" in p:
            d = p["decoupler"]
            bits.append("decoupler (%s%s)" % (d["kind"], ", releases its '%s' side" % d["releases_node"] if d.get("releases_node") else ""))
        for key, label in (("sas_level", "SAS level"), ("reaction_wheel_torque", "reaction wheel"), ("rcs_thrust_kn", "RCS kN"), ("solar_ec_per_s", "solar EC/s")):
            if key in p:
                bits.append("%s %s" % (label, p[key]))
        if "antenna" in p:
            bits.append("antenna %s (%s)" % (p["antenna"]["power"], p["antenna"]["type"]))
        if p.get("traits"):
            bits.append(", ".join(p["traits"]))
        nodes = p.get("stack_nodes") or []
        attach = "stack nodes: " + ("/".join(nodes) if nodes else "none")
        if p.get("attaches_radially"):
            attach += "; can mount radially"
        if p.get("accepts_radial_children") is False:
            attach += "; nothing can mount on its side"
        bits.append(attach)
        if "locked" in p:
            bits.append("LOCKED: " + p["locked"])
        lines.append(" | ".join(str(b) for b in bits))
    return "\n".join(lines)


# --------------------------------------------------------------------------- tools

TOOLS = []


def tool(name, description, properties=None, required=None, read_only=False, title=None):
    def register(fn):
        schema = {"type": "object", "properties": properties or {}, "additionalProperties": False}
        if required:
            schema["required"] = required
        entry = {
            "name": name,
            "description": " ".join(description.split()),
            "inputSchema": schema,
            "annotations": {"title": title or name, "readOnlyHint": read_only, "openWorldHint": False},
            "_fn": fn,
        }
        TOOLS.append(entry)
        return fn
    return register


NUM = {"type": "number"}
STR = {"type": "string"}
BOOL = {"type": "boolean"}


def enum(*values):
    return {"type": "string", "enum": list(values)}


def desc(schema, description):
    out = dict(schema)
    out["description"] = description
    return out


# ---- session ---------------------------------------------------------------

@tool("ksp_status",
      """Start here. Reports whether KSP is running and where the game is: scene (main_menu, space_center, vab,
      sph, flight, tracking_station), current save, game time, the active vessel or the craft in the editor,
      and any popup dialog that is blocking the screen. all=true also gives, in the same reply, the flown
      vessel's telemetry (as ksp_vessel) and the whole weather report (as ksp_weather): everything at once.""",
      {"all": BOOL}, read_only=True, title="KSP status")
def t_status(a):
    if not game_running():
        d = ksp_dir()
        if not d:
            raise BridgeError("Kerbal Space Program was not found. Set KSP_DIR for this MCP server.")
        return text({"ksp_running": False, "ksp_dir": d, "next": "Call ksp_start_game to launch KSP (takes one to two minutes)."})
    status = note_events(rpc("status"))
    if not a.get("all"):
        return text(status)
    # (everything at once: the craft's telemetry in flight, the weather in flight and at the space centre)
    out = {"status": status}
    scene = status.get("scene") if isinstance(status, dict) else None
    if scene == "flight":
        try:
            out["vessel"] = rpc("vessel", {})
        except BridgeError as problem:
            out["vessel"] = {"error": str(problem)}
    if scene in ("flight", "space_center"):
        try:
            out["weather"] = rpc("weather", {})
        except BridgeError as problem:
            out["weather"] = {"error": str(problem)}
    return text(out)


def launch_command(d):
    if sys.platform == "darwin":
        return ["open", os.path.join(d, "KSP.app")]
    if os.name == "nt":
        return [os.path.join(d, "KSP_x64.exe")]
    return [os.path.join(d, "KSP.x86_64")]


@tool("ksp_start_game",
      """Launch Kerbal Space Program if it is not running. Loading takes one to two minutes, longer than one
      call can wait, so this usually returns "still loading": poll ksp_status every 30 seconds until the
      scene is main_menu, then load a save with ksp_saves (or pass 'save' here when KSP is already up).""",
      {"save": desc(STR, "Name of a save to load once the main menu is up.")}, title="Start KSP")
def t_start(a):
    note = "KSP was already running."
    if not game_running():
        d = ksp_dir()
        if not d:
            raise BridgeError("Kerbal Space Program was not found. Set KSP_DIR for this MCP server.")
        try:
            subprocess.Popen(launch_command(d), cwd=d, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
        except OSError as e:
            raise BridgeError("Could not start KSP: %s" % e)
        note = "KSP launched."
        deadline = time.time() + LIMITS.budget() - 5
        ready = False
        while time.time() < deadline:
            time.sleep(4)
            ctx().progress("KSP is loading")
            if ctx().cancelled.is_set():
                break
            if game_running():
                try:
                    if rpc("status", timeout=10).get("scene") != "loading":
                        ready = True
                        break
                except BridgeError:
                    pass
        if not ready:
            return text(note + " It takes one to two minutes to load. Call ksp_status again in about 30 seconds; once it reports scene main_menu, load a save with ksp_saves.")
    if a.get("save"):
        return text({"note": note, "status": slow_rpc("load_save", {"name": a["save"]})})
    return text({"note": note, "status": rpc("status")})


@tool("ksp_saves",
      """Save games. action=list shows them; load opens one (ends at the Space Center); new creates a save
      (mode sandbox, science or career; sandbox has every part unlocked and no money) and opens it.""",
      {"action": enum("list", "load", "new"), "name": STR, "mode": enum("sandbox", "science", "career")},
      ["action"], title="Save games")
def t_saves(a):
    action = a["action"]
    if action == "list":
        return text(rpc("list_saves"))
    if not a.get("name"):
        raise BridgeError("Give the save's name.")
    if action == "load":
        return text(slow_rpc("load_save", {"name": a["name"]}))
    return text(slow_rpc("new_save", clean(a, "name", "mode")))


@tool("ksp_goto",
      """Change scene: space_center, vab (rocket editor), sph (plane editor), tracking_station or main_menu.
      Leaving a flight only works when KSP can save it (landed or in a stable orbit with engines off);
      otherwise use ksp_flight_save with action recover or revert.""",
      {"scene": enum("space_center", "vab", "sph", "tracking_station", "main_menu")}, ["scene"], title="Change scene")
def t_goto(a):
    return text(slow_rpc("goto", {"scene": a["scene"]}))


CAMERA_PROPS = {
    "view": desc(enum("flight", "map"), "In flight: the normal chase view or the orbital map."),
    "fit": desc(BOOL, "Frame the whole craft (editor and flight view)."),
    "distance": desc(NUM, "Camera distance in metres (map view: larger numbers, e.g. 5000 shows a low orbit, 100000 the Mun's orbit)."),
    "pitch": desc(NUM, "Camera elevation in degrees."),
    "heading": desc(NUM, "Camera compass direction in degrees."),
    "focus": desc(STR, "Map view: a body name or 'vessel' to centre on."),
}


@tool("ksp_screenshot",
      """Look at the game. Returns the current frame as an image; optionally moves the camera first. Use it to
      check a craft you built, confirm an orbit in the map view, or see what a popup says. Telemetry tools
      are cheaper and more precise for numbers.""",
      dict(CAMERA_PROPS, max_width=desc(NUM, "Image width in pixels, default 800.")), read_only=True, title="Screenshot")
def t_screenshot(a):
    params = clean(a, "view", "fit", "distance", "pitch", "heading", "focus", "max_width")
    params.setdefault("max_width", 800)
    r = rpc("screenshot", params, timeout=40)
    return [
        {"type": "image", "data": r["base64"], "mimeType": r.get("mime", "image/jpeg")},
        {"type": "text", "text": "Screenshot %dx%d." % (r.get("width", 0), r.get("height", 0))},
    ]


@tool("ksp_ui",
      """Deal with on-screen UI. action=dialogs lists open popups with their text and buttons; click_dialog
      presses a popup button by its label; buttons lists visible labelled buttons anywhere on screen;
      click presses one of those by label. Use this when ksp_status reports open_dialogs.""",
      {"action": enum("dialogs", "click_dialog", "buttons", "click"), "label": desc(STR, "Button text to press, or a filter for 'buttons'.")},
      ["action"], title="Game UI")
def t_ui(a):
    action = a["action"]
    if action == "dialogs":
        return text(rpc("dialogs"))
    if action == "buttons":
        return text(rpc("ui_buttons", {"search": a.get("label") or ""}))
    if not a.get("label"):
        raise BridgeError("Give 'label': the text on the button.")
    if action == "click_dialog":
        return text(rpc("dialog_click", {"button": a["label"]}))
    return text(rpc("ui_click", {"label": a["label"]}))


@tool("ksp_message",
      "Show a short message on the player's screen, e.g. to say what you are about to do.",
      {"text": STR}, ["text"], title="On-screen message")
def t_message(a):
    return text(rpc("message", {"text": a["text"]}))


def chat_look(wait_s=0.0):
    """What the player has typed to the AI in the game's panel and the model has not been given yet (given once, then
    gone), and whether the model should say something to the player now (see Company in the game)."""
    try:
        reply = rpc("chat_next", {"timeout_s": wait_s}, timeout=wait_s + 8) or {}
        return reply.get("messages") or [], reply.get("talk") or {}
    except Exception:
        return [], {}


def player_messages(wait_s=0.0):
    return chat_look(wait_s)[0]


PLAYER_SAID = ("THE PLAYER HAS SAID SOMETHING TO YOU in the game (typed in the AI Bridge panel). Each entry says what to do "
               "with it. Answer with ksp_chat reply; nothing that is running in the game stops while you do.\n")

# (these give the model what the player said themselves, or run when there is no game to ask)
NO_CHAT_AFTER = ("ksp_chat", "ksp_start_game", "ksp_call_limit")

# ---- not leaving the player in silence
# The game is told when one of the model's tools begins and ends, and what it is in a few words: it then knows an AI is
# at work, tells the model (on what it reads next) when a line to the player is due, and says itself what it can see
# the AI doing if the AI stays quiet. Speaking and listening are not work, and are not marked.
NOT_WORK = ("ksp_chat", "ksp_message", "ksp_voice", "ksp_call_limit", "ksp_start_game")
DOING = {
    "ksp_status": "checking where the game is", "ksp_saves": "looking through the saved games", "ksp_goto": "moving to another part of the game",
    "ksp_screenshot": "taking a look through the camera", "ksp_ui": "working the game's menus", "ksp_parts": "searching the parts catalogue",
    "ksp_part_info": "reading up on parts", "ksp_build": "building a craft", "ksp_craft": "looking at the craft in the editor",
    "ksp_launch": "putting the craft on the pad", "ksp_vessel": "reading the ship's instruments", "ksp_vessel_parts": "looking over the ship's parts",
    "ksp_structure": "checking how the craft holds together", "ksp_air": "checking how the craft flies", "ksp_weather": "checking the weather",
    "ksp_comms": "checking the radio", "ksp_part_action": "working a part of the ship", "ksp_events": "reading the flight's events",
    "ksp_control": "working the controls", "ksp_attitude": "pointing the ship", "ksp_autopilot": "checking the autopilot",
    "ksp_ascent": "starting the climb to orbit", "ksp_plan": "planning a manoeuvre", "ksp_node": "working on the manoeuvre",
    "ksp_execute_node": "starting the burn", "ksp_land": "starting the landing", "ksp_terrain": "studying the ground",
    "ksp_drive": "setting the rover on its way", "ksp_fly": "setting the aircraft's autopilot", "ksp_dock": "working on the docking",
    "ksp_eva": "working with the kerbal outside", "ksp_wait": "waiting on the ship", "ksp_warp": "warping time ahead",
    "ksp_flight_save": "saving or restoring the flight", "ksp_bodies": "looking up the worlds", "ksp_vessels": "looking over the ships in flight",
    "ksp_science": "seeing to the science", "ksp_flight_plan": "running a flight plan", "ksp_raw": "using one of the bridge's own commands",
}
SAY_NOW = ("SAY A LINE TO THE PLAYER NOW. %s They are watching the game, not this chat, and silence looks to them like nothing "
           "happening. Call ksp_chat reply with one short plain sentence (what you just found or did, and what you are doing next), "
           "then carry on with the work. Do the same about every %d seconds until you are done, and end with the result.")
_work = {"n": 0, "lock": threading.Lock()}


def work_mark(name, begins, mark=None):
    """Tell the game that one of the model's tools has begun (returns its mark) or ended. Never fails a tool."""
    if name in NOT_WORK:
        return None
    try:
        if begins:
            with _work["lock"]:
                _work["n"] += 1
                mark = "%d-%d" % (os.getpid(), _work["n"])
        if mark is not None:
            rpc("chat_work", {"id": mark, "tool": name, "doing": DOING.get(name, "using " + name), "begins": bool(begins)}, timeout=5)
    except Exception:
        pass
    return mark


def speak_reply(words):
    """In the voice edition, while the ship's voice is in use, what the model says to the player is said aloud too."""
    if not VOICE_EDITION:
        return False
    try:
        import ksp_voice
        if not ksp_voice.in_use():
            return False
        ksp_voice.speaker().say(words, caption=False, by="ai")
        return True
    except Exception:
        return False


@tool("ksp_chat",
      """Talk with the player. They can type to you in the game's AI Bridge panel at any time, as a question
      (Ask), an instruction (Tell) or just a message, and it reaches you by itself: ksp_wait and ksp_warp end
      at once with it in "player_messages", and it is added to the end of any other tool's result. 'reply'
      answers: it appears in the panel's conversation and on their screen, so keep it to a sentence or three,
      plain words, real numbers. 'listen_s' waits that long for their next message when you have nothing else
      to do. A QUESTION is answered without changing anything: autopilot programs and flight plans keep
      running in the game while you answer, so look up what you need (ksp_vessel, ksp_structure, ...), reply,
      and go back to exactly what you were doing (usually the same ksp_wait). An INSTRUCTION is carried out,
      and overrides your plan where the two conflict: say in a reply what you are going to do, then do it.
      Do not leave them in silence: reply at once with a first short line when you are given something to
      do, and again about every 20 seconds while you work on it (a tool result says when one is due). With
      the ship's voice in use a reply is spoken as well as shown.""",
      {"reply": desc(STR, "What to say to the player."),
       "listen_s": desc(NUM, "Wait up to this many real seconds for the player's next message (default 0: do not wait).")},
      title="Talk with the player")
def t_chat(a):
    out = {}
    if a.get("reply"):
        rpc("chat_reply", {"text": a["reply"]})
        out["shown_to_player"] = True
        if speak_reply(a["reply"]):
            out["spoken_too"] = True
    c = ctx()
    wanted = max(0.0, min(MAX_WAIT_S, float(a.get("listen_s") or 0)))
    allowed = max(0.0, min(wanted, LIMITS.budget(explore=True) - c.elapsed() - 4))
    c.exploring = allowed > LIMITS.proven()
    deadline = time.time() + allowed
    messages = player_messages()
    while not messages and time.time() < deadline - 0.2:
        if c.cancelled.is_set():
            raise BridgeError("Listening was cancelled by the client.")
        messages = player_messages(max(0.5, min(RPC_SLICE_S, deadline - time.time())))
        if not messages:
            c.progress("listening for the player")
    out["player_messages"] = messages
    if wanted and not messages:
        out["note"] = "Nothing from the player yet." + (" (Returned at this client's call budget; call again to keep listening.)" if allowed < wanted - 1 else "")
    return text(out)


# ---- building --------------------------------------------------------------

@tool("ksp_parts",
      """Search the parts catalog of this install: the game's own parts and those of every mod the player
      has installed, which are used exactly like the game's own. Returns real numbers (mass, thrust, Isp, fuel
      capacity, attach nodes) and the exact part 'name' that ksp_build needs. With no arguments it lists the
      categories, which mods have put parts in, and the looks (themes) parts come in. Never guess part names
      or statistics; look them up here.""",
      {"search": desc(STR, "Words to look for in the part name or title, e.g. 'terrier', 'FL-T', 'decoupler 2.5'."),
       "mod": desc(STR, "Only parts from this mod (its folder name in GameData), or 'stock', 'Making History', 'Breaking Ground'."),
       "category": desc(STR, "Pods, FuelTank, Engine, Control, Structural, Coupling, Payload, Aero, Ground, Thermal, Electrical, Communication, Science, Cargo, Robotics, Utility."),
       "limit": desc(NUM, "Maximum parts to return (default 40)."),
       "include_locked": desc(BOOL, "Career/science saves: also show parts that are not researched yet.")},
      read_only=True, title="Parts catalog")
def t_parts(a):
    params = clean(a, "search", "category", "mod", "include_locked")
    params["limit"] = int(a.get("limit") or 40)
    return text(parts_table(rpc("list_parts", params)))


@tool("ksp_part_info",
      "Full details for specific parts: node positions, dimensions, modules, description.",
      {"names": {"type": "array", "items": STR, "description": "Part names from ksp_parts."}}, ["names"], read_only=True, title="Part details")
def t_part_info(a):
    return text(rpc("part_info", {"names": a["names"]}))


BUILD_DESCRIPTION = """Design and assemble a craft, save it and open it in the editor. Returns mass, cost and the stock
delta-v and thrust-to-weight for every stage, plus design warnings. Build, read the numbers, adjust, build again.

'parts' is a list; the FIRST entry is the root and should be the topmost command pod or probe core (for an
aircraft: the cockpit at the nose). Every later entry attaches to an earlier one:
  {"id": "tank1", "part": "fuelTank", "on": "pod"}                  stacked under 'pod' (its free bottom node)
  {"id": "chute", "part": "parachuteSingle", "on": "pod", "node": "top"}      on a named node of the parent
  {"id": "dec", "part": "radialDecoupler", "on": "tank1", "radial": {"count": 2}}   on the SIDE, 2-fold symmetry
  {"id": "srb", "part": "solidBooster.v2", "on": "dec", "radial": {}}        on each decoupler, pointing outward
  {"id": "fins", "part": "basicFin", "on": "tank1", "radial": {"count": 4, "height": -1.2, "angle": 45}}
Fields: part (exact name from ksp_parts), id (your label), on (parent id), node (parent's stack node, default
its free 'bottom'), my_node (which of this part's nodes to use), rotate (degrees), resources
{"LiquidFuel": 90} to part-fill a tank, fields {"thrustPercentage": 60} for tweakables, autostrut
(root | heaviest | grandparent; also accepted once at the top level for every part), rigid (true).
radial: {count (copies round the parent), angle (degrees round the parent), height (metres along the parent's
own length from its centre), side (right | left | top | bottom | front | back, in the craft's axes),
right / up / forward (metres from the parent's origin in the craft's axes, to slide the mount point),
mirror (true: a left/right pair, as in the SPH), orient (down, up... swings the part about its mount),
incidence and dihedral (degrees, for wings: leading edge up, tips up), shift, outset}.
Anything attached to a part that exists in N copies (radial or mirrored) is itself copied, so describe the
right-hand side only. Landing legs are stowed for launch; add "deployed": true to keep them out.

Struts and fuel lines: {"part": "strutConnector", "on": "booster", "to": "core"} runs from the side of 'on'
to 'to' (optional radial.height on 'on', and to_height on 'to'); "fuelLine" the same way, and fuel flows
from 'on' to 'to' (asparagus staging: line from each booster tank to the core). Reach is 10 m.

Aircraft (facility "SPH", nose forward): stack cockpit, fuselage tanks, intake and jet engine front to back;
wings {"radial": {"mirror": true, "side": "right", "up": -0.4, "forward": -0.9, "incidence": 2}} on a
fuselage part; control surfaces on a wing's trailing edge {"on": "wing", "radial": {"side": "back", "right": 1.0}};
a tail fin {"radial": {"side": "top", "forward": -0.4}}; landing gear {"radial": {"side": "bottom", ...}} under
the nose and, mirrored, under the wings just behind the centre of mass. Wheels and gear are always set upright.
Keep the centre of lift a little behind the centre of mass. Rovers: wheels mirrored on the sides of a body
or beam ({"radial": {"mirror": true, "side": "right", "forward": 0.6}}), a probe core or seat, a battery.
Ladders for crew: {"part": "ladder1", "on": "tank", "radial": {"angle": A, "height": h}} in a column under the
hatch, where A is the pod's hatch angle from ksp_part_info; keep the hatch itself clear.

How it looks. Many parts come in several looks (ksp_part_info lists a part's 'variants'; ksp_parts with no
arguments lists the themes there are). "theme": "Dark" at the top level gives every part that has that look
its own version of it, and "variant": "Orange" on one part overrides it for that part. Pick one theme for
the whole craft, or one for the core and another for the boosters, rather than leaving each part as it
comes. A craft that looks right is usually one that is right: one diameter down each stack, with an adapter
where the diameter changes instead of a step; a nose cone on everything that has a blunt top; boosters and
fins in matched pairs or rings at one height; engines the width of the tank they are under; nothing
sticking out that does not need to. A payload narrower than the stage under it goes inside a fairing.

Fairings: put a fairing base (ksp_parts search "fairing": fairingSize1 is 1.25 m, fairingSize2 2.5 m,
fairingSize3 3.75 m) under the payload, where the upper stage begins:
  {"id": "probe", "part": "probeCoreOcto.v2"}, ... the payload ..., {"id": "shell", "part": "fairingSize1", "on": "battery"},
  {"id": "tank", "part": "fuelTank", "on": "shell"}, ...
The shell is made for you: it follows the payload's outline (at each height as wide as the payload is there
or anywhere above it, with a little clearance) and closes in a rounded nose over its top. It is thrown off
with the first stage that separates.
Interstage: a fairing base between two stages, with the upper stage's engine on a decoupler on the base,
gets a shell round the decoupler and the engine that ends on the upper stage's tank, and is thrown off when
that decoupler fires:
  ... {"id": "tank2", ...}, {"id": "eng2", "part": "liquidEngine3.v2", "on": "tank2"}, {"id": "dec", "part": "Decoupler.1", "on": "eng2"},
  {"id": "shell", "part": "fairingSize2", "on": "dec"}, {"id": "tank1", "part": "Rockomax32.BW", "on": "shell"}, ...
That is recognised by itself (an engine standing bell-down on the base under a part nearly as wide as the
base); "fairing": {"interstage": "tank2"} names the part to end on, {"interstage": false} closes the shell
over everything above instead. The base's interstage nodes can hold the upper stage instead: the base
"on" the upper tank with "my_node": "interstage02b", the engine "on" the base with "node": "interstage02a"
(hanging inside), and a decoupler under the base; the base then stays with the upper stage. The reply's "fairings" says what was made. "fairing": {"height": 4.0,
"radius": 1.1} sets a plain shell by hand (metres above the base, and from its middle); "fairing": false
leaves the base bare.

Staging is worked out automatically (engines with nothing under them fire at lift-off, each decoupler fires
with the engine above it, parachutes last). Override with 'stages': a list of lists of ids in firing order,
e.g. [["eng1","srb"],["dec"],["dec2","eng2"],["chute"]]. ksp_part_info gives a part's size, nodes and wing data."""


@tool("ksp_build", BUILD_DESCRIPTION,
      {"name": desc(STR, "Craft name."),
       "parts": {"type": "array", "items": {"type": "object"}, "description": "Part list as described."},
       "stages": {"type": "array", "items": {"type": "array", "items": STR}, "description": "Optional explicit staging, first list fires first."},
       "description": STR,
       "theme": desc(STR, "A look for the whole craft: every part that comes in this look gets it (e.g. Dark, White, Orange; ksp_parts with no arguments lists them)."),
       "facility": desc(enum("VAB", "SPH"), "VAB builds upright rockets (default), SPH builds horizontally for the runway."),
       "show": desc(BOOL, "The player watches the craft being put together part by part, in the order of your list (default true; it takes a few seconds). false opens it whole at once: use that only when rebuilding the same design repeatedly."),
       "show_s": desc(NUM, "How many seconds the putting together is shown for (default about 0.4 a step, 14 at most).")},
      ["name", "parts"], title="Build craft")
def t_build(a):
    return text(slow_rpc("build_craft", clean(a, "name", "parts", "stages", "description", "facility", "theme", "show", "show_s")))


@tool("ksp_craft",
      """Work with saved craft. action=info gives stats and the part list of the craft open in the editor
      (with breakdown=true also where its mass and its money are: the parts grouped by kind, heaviest
      first, which is what to look at when a craft is too heavy or too dear); list shows saved craft
      (including the stock ones); load opens one in the editor.""",
      {"action": enum("info", "list", "load"), "name": STR,
       "breakdown": desc(BOOL, "With info: also list the parts by kind with each kind's mass, cost and share of the whole.")},
      ["action"], title="Craft files")
def t_craft(a):
    action = a["action"]
    if action == "info":
        return text(rpc("craft_info", {"breakdown": True} if a.get("breakdown") else {}, timeout=60))
    if action == "list":
        return text(rpc("list_crafts"))
    if not a.get("name"):
        raise BridgeError("Give the craft's name.")
    return text(slow_rpc("load_craft", {"name": a["name"]}))


@tool("ksp_launch",
      """Roll a craft out and start the flight. With no craft name it launches whatever is open in the editor.
      Anything already sitting on the launch site is recovered first. Returns telemetry on the pad; nothing
      is ignited yet.""",
      {"craft": desc(STR, "Saved craft name; omit to use the editor's craft."),
       "site": desc(STR, "LaunchPad (default for VAB craft) or Runway."),
       "crew": {"type": "array", "items": STR, "description": "Kerbal names to put aboard. Default: KSP's automatic crew."},
       "crew_count": desc(NUM, "Instead of names: so many kerbals aboard, the free ones first. Where too few are free more are hired (not in a career, where hiring costs)."),
       "uncrewed": desc(BOOL, "Launch with empty seats.")},
      title="Launch craft")
def t_launch(a):
    params = clean(a, "craft", "site", "crew")
    if a.get("crew_count") and "crew" not in params:
        params["crew"] = int(a["crew_count"])
    if a.get("uncrewed"):
        params["crew"] = "none"
    return text(note_events(slow_rpc("launch", params)))


# ---- flight: telemetry -----------------------------------------------------

@tool("ksp_vessel",
      """Telemetry for the vessel being flown: situation, altitude, speeds, attitude, orbit (apoapsis,
      periapsis, times, inclination, upcoming encounters), thrust, TWR, remaining delta-v per stage,
      resources, control states, target and next maneuver node. full=true adds crew, biome, comms and more.""",
      {"full": BOOL}, read_only=True, title="Vessel telemetry")
def t_vessel(a):
    return text(note_events(rpc("vessel", clean(a, "full"))))


@tool("ksp_vessel_parts",
      """Parts of the vessel in flight (or of the craft open in the editor) with their state: engine running
      or flamed out, parachute stowed or deployed, panels extended, fuel in each tank, and the id used by
      ksp_part_action.""",
      {"search": STR, "kind": desc(STR, "engine, tank, decoupler, parachute, solar_panel, antenna, experiment, docking_port, landing_leg, wheel, rcs, light, command, fairing...")},
      read_only=True, title="Vessel parts")
def t_vessel_parts(a):
    return text(rpc("vessel_parts", clean(a, "search", "kind")))


@tool("ksp_structure",
      """How well the craft is holding together. IN FLIGHT it is measured: each joint's pull and twist against
      what breaks it (percent_of_breaking), how far the craft is bent at each joint, the hottest parts, and
      g-force and pressure against the weakest part's limit, with the worst of the whole flight so far
      (worst_so_far: read that, since a joint is strained hardest for a few seconds you were not looking).
      Check it after lift-off, after maximum dynamic pressure and after a hard burn; if a joint has passed
      60 percent, fly more gently and strengthen that joint in the next design. IN THE EDITOR it is an
      estimate of the same for the craft as built, for every stage (its thrust on what it carries, as it
      lights and at the end of its burn), with struts, autostruts and closed fairings counted as the game
      makes them: use it after ksp_build to find the weak joint before launching, add struts, a wider
      decoupler or autostrut there, and build again to see that it worked.""",
      {"limit": desc(NUM, "How many of the worst joints to list (default 6)."),
       "part": desc(STR, "Only joints of parts whose name contains this."),
       "reset": desc(BOOL, "Forget the worst of the flight so far and start again from now.")},
      read_only=True, title="Structure and strain")
def t_structure(a):
    return text(rpc("structure", clean(a, "limit", "part", "reset")))


@tool("ksp_air",
      """What the air is doing to the craft. IN FLIGHT it is measured: speed, Mach number, the pressure of the
      air on the craft, the angle the nose is off the way it flies, drag and lift, and above all air_is:
      "steadying" (the air pushes the nose back towards the way of flight) or "upsetting" (it pushes it
      further off, and only the steering is holding it), with what the craft has to steer with. worst_so_far
      keeps the greatest air pressure of the flight and how far off the nose got while the air pressed.
      Read it when an ascent wobbles or tumbles, before turning hard low down, and on the way back into
      the air. IN THE EDITOR it says where the lift of wings or fins acts against the centre of mass, with
      full tanks and with empty ones (for an aeroplane ksp_craft info has more).""",
      {"reset": desc(BOOL, "Forget the worst of the flight so far and start again from now.")},
      read_only=True, title="The air on the craft")
def t_air(a):
    return text(rpc("air", clean(a, "reset")))


@tool("ksp_weather",
      """The weather where the craft is (at the space centre outside flight), from the Natural Weather mod
      when it is installed (without it the air is still and nothing here matters): the sky, rain or snow,
      storms, fog, dust, how far one can see, the clouds in layers, the wind at 10 m and aloft (to the jet
      stream), the gusts, and in flight the wind on the craft this moment with its airspeed (which is not
      its ground speed in a wind). Also the LAUNCH go / no go for the pad, rule by rule (wind at the pad,
      shear aloft, lightning within 18.5 km in 30 minutes, anvils, precipitation, thick or towering cold
      cloud) with a three-hour forecast and when the next go is likely. Read it before a launch (wait for a
      go), before landing a plane (land into the wind), and before a parachute descent (the capsule drifts
      downwind at about the wind's speed for as long as it hangs). Lightning can strike a craft in a storm.""",
      {}, read_only=True, title="The weather")
def t_weather(a):
    return text(rpc("weather", {}))


@tool("ksp_comms",
      """The craft's radio, in the editor or in flight: its aerials and what they add up to, how far that
      reaches the tracking station or a craft like itself, and heard_from: for every world, whether a
      craft there would still be heard at its nearest and at its farthest ("always", "only when near",
      "never"). In flight also the signal now and the path it takes home. Use it in the editor before
      sending a probe anywhere: a probe that is never heard cannot be steered (where the save needs a
      signal for that) and cannot send its science home. The reply says whether this save needs a signal
      at all.""",
      {}, read_only=True, title="Radio and its reach")
def t_comms(a):
    return text(rpc("comms"))


@tool("ksp_part_action",
      """Use a part's right-click menu. action=menu shows the buttons, actions and readouts the part offers
      right now; do presses one (e.g. "Extend Solar Panel", "Deploy Chute", "Shutdown Engine", "Decouple",
      "Toggle Light"); set changes a tweakable field (e.g. thrustPercentage). 'part' is an id from
      ksp_vessel_parts, part of a part's name, or "all" to apply to every part that offers it. In the
      editor it works on the open craft and shows the editor's own buttons.""",
      {"action": enum("menu", "do", "set"), "part": STR, "do": desc(STR, "Button or action label."),
       "field": STR, "value": {}, "type": desc(enum("activate", "deactivate", "toggle"), "For actions with on/off behaviour.")},
      ["action", "part"], title="Part actions")
def t_part_action(a):
    action = a["action"]
    if action == "menu":
        return text(rpc("part_menu", {"part": a["part"]}))
    if action == "do":
        if not a.get("do"):
            raise BridgeError("Give 'do': the label of the button or action.")
        return text(rpc("part_do", clean(a, "part", "do", "type")))
    if not a.get("field") or "value" not in a:
        raise BridgeError("Give 'field' and 'value'.")
    return text(rpc("part_set", clean(a, "part", "field", "value")))


@tool("ksp_events",
      """Things that happened in flight since an event id: staging, lift-off, parts destroyed, crashes,
      situation changes, sphere-of-influence changes, autopilot program results. Telemetry includes
      last_event_id; pass it back as 'since' to get only what is new.""",
      {"since": NUM}, read_only=True, title="Flight events")
def t_events(a):
    return text(rpc("events", clean(a, "since")))


# ---- flight: control -------------------------------------------------------

@tool("ksp_control",
      """Direct controls for the active vessel. Any combination of: throttle (0 to 1, held until changed),
      stage=true (fire the next stage), sas / rcs / gear / lights / brakes (true or false), sas_mode
      (stock SAS: StabilityAssist, Prograde, Retrograde, Normal, Antinormal, RadialIn, RadialOut, Target,
      AntiTarget, Maneuver), abort=true, action_group (1 to 10), and raw stick input pitch / yaw / roll and
      RCS translation x / y / z or wheel_steer / wheel_throttle (each -1 to 1) held for 'seconds'.
      Prefer ksp_attitude for pointing and the autopilot tools for whole maneuvers.""",
      {"throttle": NUM, "stage": BOOL, "sas": BOOL, "sas_mode": STR, "rcs": BOOL, "gear": BOOL, "lights": BOOL, "brakes": BOOL,
       "abort": BOOL, "action_group": NUM, "pitch": NUM, "yaw": NUM, "roll": NUM, "x": NUM, "y": NUM, "z": NUM,
       "wheel_steer": NUM, "wheel_throttle": NUM, "seconds": NUM},
      title="Flight controls")
def t_control(a):
    return text(rpc("control", a))


@tool("ksp_attitude",
      """Point the vessel and keep it there using the bridge's own attitude controller (works on any
      controllable vessel, whatever its SAS level). mode: prograde, retrograde, normal, anti_normal,
      radial_out, radial_in, surface_prograde, surface_retrograde, up, target, anti_target,
      target_prograde, target_retrograde (use this to cancel speed relative to the target), maneuver,
      pitch_heading (give pitch above the horizon and compass heading; 90 is east), or off.""",
      {"mode": STR, "pitch": NUM, "heading": NUM, "roll": NUM},
      ["mode"], title="Hold attitude")
def t_attitude(a):
    return text(rpc("attitude", clean(a, "mode", "pitch", "heading", "roll")))


@tool("ksp_autopilot",
      """Manage the autopilot. action=status shows what it is doing; abort stops the running program and cuts
      the throttle; release drops every hold (attitude, throttle, auto-staging) and hands the controls back;
      autostage_on / autostage_off switch automatic staging when engines burn out.""",
      {"action": enum("status", "abort", "release", "autostage_on", "autostage_off")}, ["action"], title="Autopilot")
def t_autopilot(a):
    action = a["action"]
    if action == "status":
        return text(rpc("pilot_status"))
    if action == "abort":
        return text(rpc("abort_program"))
    if action == "release":
        return text(rpc("release"))
    return text(rpc("autostage", {"on": action == "autostage_on"}))


@tool("ksp_ascent",
      """Autopilot: fly from the launch pad (or the surface of any body) to a circular orbit. Handles
      ignition, the gravity turn, staging, coasting and the circularisation burn. Returns at once; follow
      it with ksp_wait until={"program_done": true}. Needs roughly 3400 m/s of vacuum delta-v from Kerbin
      and a lift-off TWR above about 1.2.""",
      {"altitude_km": desc(NUM, "Orbit altitude; default is just above the atmosphere (80 km for Kerbin)."),
       "inclination": desc(NUM, "Orbit inclination in degrees; 0 is equatorial eastward, negative launches southward."),
       "turn_start_speed": desc(NUM, "Speed in m/s at which to start pitching over (default 60). Raise for low-TWR rockets."),
       "turn_end_altitude_km": desc(NUM, "Altitude where the pitch reaches horizontal (default about 50 on Kerbin)."),
       "turn_shape": desc(NUM, "0.3 turns early and aggressively, 0.6 stays steep longer (default 0.45)."),
       "max_q_kpa": desc(NUM, "Throttle back above this dynamic pressure (default 45; 0 disables)."),
       "warp": desc(BOOL, "Use time warp while coasting (default true).")},
      title="Ascent to orbit")
def t_ascent(a):
    return text(rpc("ascent", clean(a, "altitude_km", "inclination", "turn_start_speed", "turn_end_altitude_km", "turn_shape", "max_q_kpa", "warp")))


@tool("ksp_plan",
      """Create a maneuver node for a standard maneuver and report where it leads.
      circularize (at apoapsis or periapsis); set_apoapsis / set_periapsis (altitude_km, burning at the
      opposite apsis); transfer (to the current target: a moon of the body you orbit such as the Mun,
      another vessel in the same orbit system, or another planet when you are in a parking orbit; waits
      for the right departure time and tunes the burn for an encounter with periapsis_km at the target);
      rendezvous (with a target craft on nearly the same orbit, a few km away: a phasing burn so that
      it is met after one to eight orbits; the result's meet_in_s says when: plan
      match_velocity with in_seconds then; transfer would wait days for the phase to come round);
      correct (a small mid-course burn that restores periapsis_km at the target body, or at 'body' such
      as Kerbin on the way home; use it after any long transfer burn); return (from a moon's orbit back
      to its planet with periapsis_km there, default a reentry altitude); match_planes (with the target,
      at the next crossing); match_velocity (with the target at closest approach). Replaces existing
      nodes. Then fly it with ksp_execute_node.""",
      {"maneuver": enum("circularize", "set_apoapsis", "set_periapsis", "transfer", "rendezvous", "correct", "return", "match_planes", "match_velocity"),
       "at": desc(STR, "apoapsis or periapsis (circularize, set_*), or 'highest' for match_planes."),
       "altitude_km": NUM, "periapsis_km": NUM, "in_seconds": NUM, "body": desc(STR, "For correct: the body to arrive at, if it is not the target.")},
      ["maneuver"], title="Plan maneuver")
def t_plan(a):
    return text(rpc("plan", clean(a, "maneuver", "at", "altitude_km", "periapsis_km", "in_seconds", "body"), timeout=45))


@tool("ksp_node",
      """Maneuver nodes by hand. action=list shows each node and the trajectory after it (including
      encounters and closest approach to the target); add creates one at 'at' (apoapsis, periapsis, now)
      or in_seconds / ut with prograde, normal and radial delta-v in m/s; edit changes node 'index'
      (absolute prograde/normal/radial/ut, or add_prograde/add_normal/add_radial/shift_seconds to nudge);
      remove deletes node 'index', or all nodes if omitted.""",
      {"action": enum("list", "add", "edit", "remove"), "at": STR, "in_seconds": NUM, "ut": NUM, "index": NUM,
       "prograde": NUM, "normal": NUM, "radial": NUM, "add_prograde": NUM, "add_normal": NUM, "add_radial": NUM, "shift_seconds": NUM},
      ["action"], title="Maneuver nodes")
def t_node(a):
    action = a["action"]
    if action == "list":
        return text(rpc("nodes"))
    if action == "add":
        return text(rpc("add_node", clean(a, "at", "in_seconds", "ut", "prograde", "normal", "radial")))
    if action == "edit":
        return text(rpc("edit_node", clean(a, "index", "ut", "prograde", "normal", "radial", "add_prograde", "add_normal", "add_radial", "shift_seconds")))
    return text(rpc("remove_nodes", clean(a, "index")))


@tool("ksp_execute_node",
      """Autopilot: fly the next maneuver node. Turns to the burn direction, time-warps to the start, burns
      (staging if a stage runs dry) and removes the node. Returns at once; follow it with ksp_wait
      until={"program_done": true}.""",
      {"warp": BOOL, "autostage": BOOL}, title="Execute node")
def t_execute(a):
    return text(rpc("execute_node", clean(a, "warp", "autostage")))


@tool("ksp_land",
      """Autopilot: land the vessel, whatever the world. Returns at once; follow it with ksp_wait
      until={"program_done": true} (or put it in a flight plan). Give lat and lon, a landed vessel as
      target, a place, or heading + distance_m to land at a chosen spot; give nothing to come down under
      the track. Check a spot first with ksp_terrain (slope).
      Airless bodies (Mun, Minmus...): powered descent with the gear out, from a low orbit (10 to 20 km; the
      Mun needs about 750 m/s). Aimed, it plans and flies the de-orbit burn including the plane change,
      coasts, brakes to a stop over the spot and descends: expect to land within a few tens of metres.
      Bodies with air (Kerbin, Duna, Eve, Laythe): flies the de-orbit burn, drops everything staged before
      the parachutes (a capsule sheds its service module; a craft with legs or wheels keeps everything
      unless stage=true), holds the heat shield into the wind and opens the parachutes when it is safe.
      Where the air is too thin to slow the vessel in time it burns the engine until the parachutes can
      open, and it lands on the engine when parachutes are missing or too weak, so a lander for Duna
      needs parachutes plus roughly 600 m/s, and a craft with no parachutes needs a strong engine.
      Aimed (from orbit only), it times the de-orbit burn from a drag prediction, separates sideways-on and
      holds the parachutes until 3.5 km up; a capsule has landed 250 m from its target this way, but allow
      a kilometre or two, and more when the engine has to help on the way down. Aircraft land with ksp_fly.""",
      {"lat": NUM, "lon": NUM, "target": desc(STR, "A landed vessel to come down beside (name or id)."),
       "place": desc(STR, "On Kerbin: launchpad, runway, island_runway..."),
       "heading": NUM, "distance_m": NUM, "offset_m": desc(NUM, "How far from the target vessel to land (default 80)."),
       "clearance_m": desc(NUM, "Airless, aimed: height above the spot at which to arrive and brake (default 2500)."),
       "touchdown_speed": NUM, "safety_factor": desc(NUM, "1.2 default; larger starts the final burn earlier."),
       "periapsis_km": desc(NUM, "Atmosphere: where the de-orbit burn puts the periapsis (default 42% of the atmosphere's height, 30% when aiming)."),
       "stage": desc(BOOL, "Atmosphere: drop the stages before the parachutes at entry. Default: yes for capsules, no for craft with legs or wheels."),
       "chutes": desc(BOOL, "Use parachutes (default true)."), "engines": desc(BOOL, "Allow the engine to help (default true)."),
       "deorbit": desc(BOOL, "Fly the de-orbit burn automatically when in orbit (default true)."),
       "warp": BOOL},
      title="Land")
def t_land(a):
    return text(rpc("land", clean(a, "lat", "lon", "target", "place", "heading", "distance_m", "offset_m", "clearance_m", "touchdown_speed", "safety_factor",
                              "periapsis_km", "stage", "chutes", "engines", "deorbit", "warp")))


@tool("ksp_terrain",
      """What the ground is like at a spot: terrain height, slope (and whether that is fit to land on), biome,
      distance and bearing from the vessel, and, near the vessel, any building or runway standing there.
      Give lat and lon (and body when no vessel is being flown), a landed vessel as target, a place
      (launchpad, runway, runway_west, runway_east, island_runway), or heading + distance_m from the vessel;
      nothing at all means the ground under the vessel. Use it to choose a landing site or check a route.""",
      {"lat": NUM, "lon": NUM, "body": STR, "target": STR, "place": STR, "heading": NUM, "distance_m": NUM},
      read_only=True, title="Terrain at a spot")
def t_terrain(a):
    return text(rpc("terrain", clean(a, "lat", "lon", "body", "target", "place", "heading", "distance_m")))


@tool("ksp_drive",
      """Autopilot: drive a rover over the ground to a spot and stop there with the brakes on. Give lat and
      lon, a landed vessel as target, a place (launchpad, runway...), or heading + distance_m. It learns
      which way this rover's wheels drive and steer, holds 'speed' (default 8 m/s; rovers flip when turned
      fast), and finds its own way round buildings, other vessels, steep banks and drops, backing up when
      it has to. If it ends up boxed in it stops and says what is in the way. Returns at once; ksp_wait
      until={"program_done": true}. Needs powered wheels and electric charge.""",
      {"lat": NUM, "lon": NUM, "target": STR, "place": STR, "heading": NUM, "distance_m": NUM,
       "speed": desc(NUM, "m/s, default 8."), "stop_distance_m": desc(NUM, "How close counts as arrived (default 5; 25 for a vessel).")},
      title="Drive rover")
def t_drive(a):
    return text(rpc("drive", clean(a, "lat", "lon", "target", "place", "heading", "distance_m", "speed", "stop_distance_m")))


@tool("ksp_fly",
      """Aircraft autopilot (winged craft in an atmosphere). mode:
      takeoff - from a runway or flat ground: holds the centreline, rotates, climbs to altitude_m (above sea
        level, default 1000 m up), retracts the gear, then holds heading and speed. Optional heading to turn
        to after lift-off, speed, rotate_speed, pitch.
      cruise - set or change what is held: heading, altitude_m, speed (m/s) or throttle (0 to 1). The hold
        stays on after every other mode finishes, so the aircraft never flies unattended. It watches the
        ground ahead and climbs above the set altitude to stay terrain_clearance_m (default 300) over
        high ground, coming back down afterwards; 0 switches that off.
      goto - fly to lat/lon, a place (island_runway, runway...), a landed vessel, or heading + distance_m;
        finishes when it gets there (radius_m, default 1500) and keeps flying straight on.
      land - a full approach and landing: runway "ksc" (default; picks the end to approach from), ksc_09,
        ksc_27, island, island_09, island_27, or your own strip {"lat","lon","heading","altitude_m","length_m"}
        on any flat ground. It flies to a point on the extended centreline, follows the glide slope,
        flares, touches down and brakes to a stop; it goes around by itself if badly lined up.
        Optional approach_speed, glideslope (degrees, default 4), final_km (default 8).
      off - hand the controls back. No mode: report what is being held.
      Programs return at once; ksp_wait until={"program_done": true}. Speeds are airspeeds (in a weather
      mod's wind they differ from the speed over the ground; the nose crabs into a crosswind), picked from
      the wing area when you give none. Start take-offs from the runway (ksp_launch site Runway).""",
      {"mode": enum("takeoff", "cruise", "goto", "land", "off", "status"),
       "heading": NUM, "altitude_m": NUM, "speed": NUM, "throttle": NUM,
       "lat": NUM, "lon": NUM, "target": STR, "place": STR, "distance_m": NUM, "radius_m": NUM,
       "runway": {"description": "For land: a name (ksc, ksc_09, ksc_27, island, island_09, island_27) or {lat, lon, heading, altitude_m, length_m, width_m}."},
       "approach_speed": NUM, "glideslope": NUM, "final_km": NUM, "rotate_speed": NUM, "pitch": NUM, "bank_limit": NUM,
       "terrain_clearance_m": NUM},
      title="Fly aircraft")
def t_fly(a):
    params = clean(a, "mode", "heading", "altitude_m", "speed", "throttle", "lat", "lon", "target", "place", "distance_m", "radius_m",
                   "runway", "approach_speed", "glideslope", "final_km", "rotate_speed", "pitch", "bank_limit", "terrain_clearance_m")
    return text(rpc("fly", params))


@tool("ksp_dock",
      """Docking. action=ports lists the docking ports of this vessel and of the target with their sizes and
      states. action=dock flies the docking with RCS: it turns this vessel's port to face the target's,
      flies round to a point straight out from the target port, then closes at a crawl until the ports
      latch; the two vessels become one. Rendezvous first (ksp_plan transfer, or rendezvous for a craft a few
      km away on nearly your orbit, then match_velocity at closest approach) so that the target is within about
      2 km, ideally 200 m, and nearly at rest relative to you.
      Needs RCS thrusters, monopropellant and a free port of the same size on each vessel. A target that is
      turning slowly is followed round (up to about 12 degrees a second; steadier is cheaper), and roll is
      matched then, or whenever you give roll (degrees between the two ports' "up" marks). Returns at
      once; ksp_wait until={"program_done": true}. action=undock releases a docked port (the vessel becomes
      two; the reply names the other half).""",
      {"action": enum("ports", "dock", "undock"),
       "target": desc(STR, "Vessel to dock with (name or id); default the current target."),
       "my_port": desc(STR, "Which of this vessel's ports to use (id or name from action=ports)."),
       "target_port": STR, "port": desc(STR, "For undock: which port to release."),
       "speed": desc(NUM, "Top closing speed near the target, m/s (default 2)."),
       "roll": desc(NUM, "Match roll: degrees between the ports' up marks (0 lines them up).")},
      ["action"], title="Docking")
def t_dock(a):
    action = a["action"]
    if action == "ports":
        return text(rpc("ports", clean(a, "target")))
    if action == "undock":
        return text(note_events(rpc("undock", clean(a, "port"), timeout=30)))
    return text(rpc("dock", clean(a, "target", "my_port", "target_port", "speed", "roll")))


@tool("ksp_eva",
      """Kerbals outside the ship. action:
      status - who is aboard or outside, what the kerbal is doing, jetpack propellant, hatches and ladders
        in reach, nearby vessels.
      start - send a kerbal out of the vessel being flown (kerbal: a name; default the first aboard). The
        kerbal becomes the vessel you control, usually holding the ladder by the hatch.
      let_go - release the ladder. get_up - stand a fallen kerbal up. jump. jetpack (on: true/false).
      walk - on the ground, walk or run to lat/lon, a vessel (target), or heading + distance_m.
      fly - by jetpack to a vessel's hatch or a named part (target, part), in space or in low gravity.
      board - go back to a ship (target, default the nearest with a free seat) and climb in: by jetpack where
        it can lift a kerbal, otherwise on foot and up a ladder (the ship needs rungs from the ground to the
        hatch; ksp_part_info gives a pod's hatch angle for placing them).
      flag - plant a flag (name: the site name). stop - cancel the movement.
      Walk, fly and board return at once; ksp_wait until={"program_done": true}. EVA reports and surface
      samples: ksp_science run while the kerbal is the vessel being flown. Switch back to the ship with
      ksp_vessels switch.""",
      {"action": enum("status", "start", "walk", "fly", "board", "let_go", "get_up", "jump", "jetpack", "flag", "stop"),
       "kerbal": STR, "target": STR, "part": STR, "lat": NUM, "lon": NUM, "heading": NUM, "distance_m": NUM,
       "radius_m": NUM, "run": BOOL, "on": BOOL, "name": STR, "speed": NUM},
      ["action"], title="EVA")
def t_eva(a):
    params = clean(a, "action", "kerbal", "target", "part", "lat", "lon", "heading", "distance_m", "radius_m", "run", "on", "name", "speed")
    return text(note_events(rpc("eva", params, timeout=40)))


WAIT_CONDITIONS = """program_done (true), ut, altitude_above, altitude_below, apoapsis_above, apoapsis_below,
periapsis_above, periapsis_below, time_to_apoapsis_below, time_to_periapsis_below, vertical_speed_above,
vertical_speed_below, speed_above, speed_below (surface speed), height_above_ground_below,
dynamic_pressure_below, situation (e.g. "LANDED" or ["LANDED","SPLASHED"]), body, node_in_below,
fuel_below (true: engines flamed out), distance_to_target_below, scene, sequence_done (true: the flight
plan has ended)."""


@tool("ksp_wait",
      """Wait in real time until something happens, instead of polling. Returns as soon as any condition in
      'until' holds, a flight event occurs (staging, explosion, SOI change, a program or flight-plan step
      ending), or timeout_s passes; the reply says which, with brief telemetry and every event since your
      last call. Conditions: """ + WAIT_CONDITIONS + """
      Example: {"until": {"program_done": true}, "timeout_s": 600}. Ask for as long as you like (up to an
      hour): the server knows how long this client lets a call run and returns "timeout" when that
      budget ends, so simply call again. The budget grows as longer calls prove to work.""",
      {"until": {"type": "object", "description": "Conditions, any of which ends the wait."},
       "timeout_s": desc(NUM, "Real seconds to wait at most (default 120). The reply's call_budget_s tells you how long one call can currently last."),
       "stop_on_event": desc(BOOL, "Return early on any new flight event (default true).")},
      read_only=True, title="Wait")
def t_wait(a):
    wanted = max(1.0, min(MAX_WAIT_S, float(a.get("timeout_s") or 120)))
    c = ctx()
    budget = LIMITS.budget(explore=True)
    allowed = max(1.0, min(wanted, budget - c.elapsed() - 4))
    c.exploring = allowed > LIMITS.proven()
    result = wait_in_game(a.get("until"), allowed, a.get("stop_on_event", True) is not False)
    if result.get("stopped_because") == "timeout" and allowed < wanted - 1:
        result["call_budget_s"] = round(budget)
        result["note"] = "Returned at this client's current call budget, not at your timeout. Call again to keep waiting."
    return text(result)


@tool("ksp_warp",
      """Time warp. Give one of: to (apoapsis, periapsis, node, soi), seconds, ut, or rate (0 to 7, where 0
      is normal speed). Warping to a node stops a minute before it by default (lead_s). It returns when the
      time is reached or a flight event occurs; a warp that outlasts this client's call budget reports
      "still warping" and keeps going by itself, so call it again with the same arguments until it says
      "arrived". On-rails warp is impossible in atmosphere or under thrust; 4x physics warp is used there.""",
      {"to": enum("apoapsis", "periapsis", "node", "soi"), "seconds": NUM, "ut": NUM, "lead_s": NUM, "rate": NUM},
      title="Time warp")
def t_warp(a):
    params = clean(a, "to", "seconds", "ut", "lead_s", "rate")
    c = ctx()
    budget = LIMITS.budget()
    result = None
    while True:
        remaining = budget - c.elapsed() - 6
        params["timeout_s"] = max(5, min(RPC_SLICE_S, remaining))
        result = note_events(rpc("warp", params, timeout=params["timeout_s"] + 8))
        # (what the player has said goes to the model now; the warp carries on by itself and the model calls again)
        if result.get("stopped_because") != "still warping" or remaining <= RPC_SLICE_S or c.cancelled.is_set() or result.get("player_messages"):
            break
        c.progress("warping, %s s of game time to go" % result.get("remaining_s"))
    return text(result)


@tool("ksp_flight_save",
      """Save points and endings for a flight. quicksave / quickload store and restore the flight under
      'name' (do this before anything risky); revert_to_launch puts the craft back on the pad;
      revert_to_editor returns to the VAB with it; recover retrieves a vessel that has landed or splashed
      down on Kerbin and returns to the Space Center.""",
      {"action": enum("quicksave", "quickload", "revert_to_launch", "revert_to_editor", "recover"), "name": STR},
      ["action"], title="Save, revert, recover")
def t_flight_save(a):
    action = a["action"]
    if action == "quicksave":
        return text(rpc("quicksave", clean(a, "name")))
    if action == "quickload":
        return text(note_events(slow_rpc("quickload", clean(a, "name"))))
    if action == "revert_to_launch":
        return text(note_events(slow_rpc("revert", {"to": "launch"})))
    if action == "revert_to_editor":
        return text(slow_rpc("revert", {"to": "editor"}))
    return text(slow_rpc("recover", {}))


# ---- world -----------------------------------------------------------------

@tool("ksp_bodies",
      """Planets and moons with the numbers needed for planning: radius, surface gravity, atmosphere height,
      sphere of influence, a sensible low-orbit altitude and its speed, and orbital elements. Give 'name'
      for one body in more detail.""",
      {"name": STR}, read_only=True, title="Celestial bodies")
def t_bodies(a):
    return text(rpc("bodies", clean(a, "name")))


@tool("ksp_vessels",
      """Other vessels. action=list shows every vessel in the save with an id; target sets the navigation
      target to a vessel or body ('none' clears it); switch takes control of another vessel. 'name' may be
      the vessel's name or, when several share a name, its id from the list.""",
      {"action": enum("list", "target", "switch"), "name": STR, "include_debris": BOOL}, ["action"], title="Vessels and targets")
def t_vessels(a):
    action = a["action"]
    if action == "list":
        return text(rpc("vessels", clean(a, "include_debris")))
    if not a.get("name"):
        raise BridgeError("Give 'name'.")
    if action == "target":
        return text(rpc("set_target", {"name": a["name"]}))
    return text(note_events(slow_rpc("switch_vessel", {"name": a["name"]})))


@tool("ksp_science",
      """Science experiments on the active vessel. action=status lists experiments and stored data with their
      value; run performs every experiment possible in the current situation (or the named one) without
      popups; transmit sends stored data home by antenna; reset clears used experiments.""",
      {"action": enum("status", "run", "transmit", "reset"), "experiment": STR}, ["action"], title="Science")
def t_science(a):
    action = a["action"]
    params = clean(a, "experiment")
    if action == "status":
        return text(rpc("science"))
    if action == "run":
        return text(rpc("science_run", params, timeout=40))
    if action == "transmit":
        return text(rpc("science_transmit", params))
    return text(rpc("science_reset", params))


FLIGHT_PLAN_DESCRIPTION = """Hand the game a whole sequence of commands to carry out by itself. Each step runs
when the one before has finished, including any autopilot program it started, so a mission leg needs one
call instead of dozens, and it keeps running even if you are not waiting on it.
action=start with 'steps': a list of {"do": <command>, ...its parameters}. Commands are the bridge's own
(list them with ksp_raw method="help"): launch, ascent, set_target, plan, execute_node, warp, land,
quicksave, control, attitude, part_do, science_run, dock, undock, drive, fly, eva, recover, and "wait". A step may
add "until": {conditions as in ksp_wait} to hold until they are met, "timeout_s" (default 1800), and
"optional": true to carry on if it fails. The plan stops at the first failed step. For part_do give the
button as "action" (e.g. {"do":"part_do","part":"all","action":"Extend"}), since "do" names the command.
Example to reach Mun orbit:
[{"do":"launch","craft":"Mun Lander I"},{"do":"ascent","altitude_km":80},{"do":"set_target","name":"Mun"},
 {"do":"plan","maneuver":"transfer"},{"do":"execute_node"},{"do":"warp","to":"soi"},
 {"do":"plan","maneuver":"circularize","at":"periapsis"},{"do":"execute_node"},{"do":"quicksave","name":"mun_orbit"}]
Then ksp_wait until={"sequence_done": true}. action=status shows each step's result; abort stops it."""


@tool("ksp_flight_plan", FLIGHT_PLAN_DESCRIPTION,
      {"action": enum("start", "status", "abort"),
       "name": STR,
       "steps": {"type": "array", "items": {"type": "object"}, "description": "Steps for action=start."}},
      ["action"], title="Flight plan")
def t_flight_plan(a):
    action = a["action"]
    if action == "status":
        return text(rpc("sequence_status"))
    if action == "abort":
        return text(rpc("sequence_abort"))
    if not a.get("steps"):
        raise BridgeError("Give 'steps'.")
    return text(rpc("sequence_start", clean(a, "name", "steps")))


@tool("ksp_call_limit",
      """How long this client lets one tool call run, as measured by the server. action=status reports the
      current budget and what it is based on. action=measure makes one deliberately long, harmless call of
      'seconds' (default twice the proven duration): if you receive its answer, calls of that length work
      and the budget is raised; if the client reports a timeout instead, that is the measurement, and the
      budget is lowered to stay under it. action=reset forgets what was learned.""",
      {"action": enum("status", "measure", "reset"), "seconds": NUM}, ["action"], read_only=True, title="Call limit")
def t_call_limit(a):
    action = a["action"]
    if action == "reset":
        LIMITS.reset()
        return text(LIMITS.describe())
    if action == "status":
        return text(LIMITS.describe())
    c = ctx()
    seconds = max(5.0, min(MAX_WAIT_S, float(a.get("seconds") or LIMITS.proven() * 2.0)))
    c.exploring = True
    while c.elapsed() < seconds:
        if c.cancelled.wait(timeout=min(5.0, seconds - c.elapsed())):
            raise BridgeError("The client cancelled the call after %.0f s." % c.elapsed())
        c.progress("measuring: %d of %d s" % (c.elapsed(), seconds))
    out = LIMITS.describe()
    out["this_call_lasted_s"] = round(c.elapsed())
    out["result"] = "You received this answer, so calls of %d s work with this client." % round(c.elapsed())
    out["longest_call_that_worked_s"] = max(out.get("longest_call_that_worked_s", 0), round(c.elapsed()))
    return text(out)


VOICE_EDITION = os.path.exists(os.path.join(os.path.dirname(os.path.abspath(__file__)), "ksp_voice.py"))


def voice_tool(*args, **kwargs):
    """Register the tool only in the voice edition (the standard download has no speech code)."""
    return tool(*args, **kwargs) if VOICE_EDITION else (lambda fn: fn)


@voice_tool("ksp_voice",
      """The ship's voice (voice edition). action=on makes the ship's computer call out the flight by itself in
      a spoken voice inside the game (lift-off, staging, orbit, burn countdowns, height call-outs, warnings),
      whoever is flying; detail quiet|normal|chatty sets how much it says; action=off stops it. action=say
      speaks 'text' in the same voice: use it for what the automatic call-outs cannot know (a plan, an answer
      to the player, a decision), one or two short sentences. action=listen waits for the player to say
      something (they hold the talk key in the game, Y by default) and returns the words; it returns
      "nothing yet" when this client's call budget ends, so call it again. action=set changes voice, speed,
      effect (ship|radio|none), volume, detail or the talk key; action=voices lists the voices; action=stop
      silences it; action=status shows what is installed. Speech runs on the player's computer (Kokoro and
      Whisper after ./setup_voice.sh, otherwise the operating system's voice and no listening).""",
      {"action": enum("status", "on", "off", "say", "listen", "set", "voices", "stop"),
       "text": desc(STR, "What to say (action=say)."),
       "interrupt": desc(BOOL, "Cut off whatever is being said (action=say)."),
       "detail": enum("quiet", "normal", "chatty"), "voice": desc(STR, "A voice name from action=voices."),
       "speed": desc(NUM, "0.5 to 2, default 1."), "effect": enum("ship", "radio", "none"), "volume": desc(NUM, "0 to 1."),
       "key": desc(STR, "Push-to-talk key in the game, a Unity key name such as Y, B, F8 or RightControl."),
       "engine": enum("auto", "kokoro", "system")},
      ["action"], title="Ship's voice")
def t_voice(a):
    import ksp_voice
    c = ctx()
    return text(ksp_voice.tool(a, budget_s=LIMITS.budget(explore=a.get("action") == "listen") - c.elapsed() - 4))


@tool("ksp_raw",
      """Escape hatch: call any AI Bridge command directly. method="help" lists every command with its
      parameters. Use the dedicated tools when one fits.""",
      {"method": STR, "params": {"type": "object"}, "timeout_s": NUM}, ["method"], title="Raw bridge call")
def t_raw(a):
    return text(rpc(a["method"], a.get("params") or {}, timeout=float(a.get("timeout_s") or 60)))


INSTRUCTIONS = """KSP AI Bridge lets you play Kerbal Space Program 1 for the user: design craft, launch, fly, land.

How to work
- Begin with ksp_status. If the game is not running use ksp_start_game. If no save is loaded, ask which
  save to use or create a sandbox save with ksp_saves; do not experiment in a career save without asking.
- You are slow compared with a rocket. Never try to steer by rapidly polling telemetry. Start an autopilot
  program (ksp_ascent, ksp_execute_node, ksp_land, ksp_fly, ksp_drive, ksp_dock, ksp_eva) or set a hold
  (ksp_attitude, throttle via ksp_control),
  then block with ksp_wait until a condition or program_done. Read the events and telemetry it returns.
  Prefer ksp_flight_plan for anything with more than two or three steps: the game runs it unattended.
  If a slow operation (loading a scene) reports that it is still working, check ksp_status before
  repeating it.
- The player can see everything and has a panel with "Pause AI" and "Take controls". If a command is refused
  because AI control is paused, stop and tell the user.
- The player can talk to you from that panel while you fly (see ksp_chat). Their words arrive as
  "player_messages" in tool results, each marked question, instruction or message. Always answer with
  ksp_chat reply. A question changes nothing: answer it and carry on exactly as before, the ship flies
  itself meanwhile. An instruction is followed, and you say what you are doing about it.
- Never leave the player in silence. They watch the game, not this chat, and a quiet AI looks like a
  stopped one. The moment you are given something to do (here, or by them from the game) say one short
  line with ksp_chat reply: what you understood and what you will do first. While you work, say another
  about every 20 seconds: what you found, what you are doing, what comes next. A tool result tells you
  when one is due (SAY A LINE TO THE PLAYER NOW). Before a command that takes long (ksp_wait, ksp_warp,
  an autopilot program) say what you are waiting for; the game itself tells them what it can see you
  doing while you are inside it. End with the result. One plain sentence each time. (The player can
  switch this off in the panel; the reminders then stop.)
- Units: metres, m/s, seconds, kN, tonnes, degrees. Altitudes are above sea level unless called height above
  ground. 'ut' is game time in seconds. KSP stage numbers count down: the highest fires first.
- ksp_flight_save quicksave before a risky step (landing, aerobraking, a long burn) and quickload if it fails.

Designing rockets (ksp_parts, ksp_build)
- Look parts up; names like "liquidEngine3.v2" cannot be guessed. Build top-down from the command pod.
- After ksp_build read the stage table. Rough vacuum delta-v needs from the Kerbin launch pad: low Kerbin
  orbit 3400; then Mun flyby +860, Mun orbit +310 more, Mun landing +580 (the ksp_land program uses about
  750), back to Kerbin from the Mun surface 580 + 310. Minmus: transfer 930, capture 160, landing 180.
  Add 10 to 15 percent margin. Returning to Kerbin needs a heat shield and a parachute but almost no fuel.
- After ksp_build read "structure" in the reply too. If folding_under_thrust says the craft folds or is
  close to folding (in any stage: worst_at says where), or a joint bends several degrees, fix it before
  launching: wider joints, a shorter thin section, the heavy things lower, struts across the weak joint
  ("strutConnector" from one part to another), or "autostrut" on the parts beyond it. Struts and autostruts
  are counted in the estimate, so build again and read it. ksp_structure lists every joint, and in flight
  it measures the real strain (read worst_so_far after lift-off and after maximum dynamic pressure).
- A probe that is to go further than the Mun needs a real aerial: ksp_comms in the editor says which
  worlds it would still be heard from. Fins belong at the bottom of a rocket (ksp_air in the editor says
  where the lift acts against the centre of mass, full and empty).
- Lift-off TWR 1.3 to 1.8; upper stages work above about 0.7; a lander needs TWR above 2 on its target body.
  Sea-level Isp matters for the first stage, vacuum Isp above.
- Crewed capsule recipe, top to bottom: parachute, pod, heat shield, decoupler, tank, vacuum engine,
  decoupler, big tanks, lift-off engine, optional radial decouplers with boosters. Probes need a probe core,
  a battery, solar panels and an antenna. Landers need landing legs mounted radially low on the body.

Flying
- Launch: ksp_launch, then ksp_ascent, then ksp_wait for program_done. On failure read the reason, fix the
  design or the settings (revert_to_editor or revert_to_launch) and try again. If a craft wobbled or
  tumbled on the way up, ksp_air says whether the air was upsetting it and how hard the air pressed.
- Orbit changes: ksp_plan creates a node, ksp_execute_node flies it, ksp_wait for program_done.
- To the Mun: orbit Kerbin at 80 to 100 km, ksp_vessels target Mun, ksp_plan transfer, execute, ksp_warp
  to soi, ksp_plan circularize at periapsis, execute. Other planets work the same way from a parking
  orbit (the departure may be hundreds of days away: ksp_warp to node), but after the departure burn
  always ksp_plan correct and execute it, and again after entering the target's sphere of influence
  if the periapsis is still off. Planets with air (Duna, Eve, Laythe) allow aerobraking.
- Landing: ksp_land does it everywhere, at a chosen spot if you give one (check it with ksp_terrain).
  Airless: from a low orbit (ksp_plan set_periapsis then circularize at about 15 km), quicksave, ksp_land.
  With air: ksp_land flies the de-orbit burn, staging, heat-shield attitude and parachutes for a capsule,
  and uses the engine where the air is thin or there are no parachutes. To come home from a moon:
  ksp_ascent to a 15 km orbit, ksp_plan return, execute, warp to soi, ksp_land (place "launchpad" to
  come down at the space centre). After splashdown or landing on Kerbin: ksp_flight_save recover.
- Aircraft: ksp_launch to the Runway, ksp_fly takeoff, then goto / cruise, and ksp_fly land to come back.
  The hold keeps flying between commands; change it with ksp_fly cruise.
- Rovers: ksp_drive to a spot; it goes round obstacles by itself.
- Docking: get within a couple of hundred metres at low relative speed (ksp_plan transfer to the target
  vessel, or ksp_plan rendezvous when it is a few km away on nearly your orbit; then ksp_plan match_velocity
  at the meeting, execute), then ksp_dock dock. ksp_dock undock to separate.
- EVA: ksp_eva start, then let_go / walk / fly / flag / board; science with ksp_science run.
- In space, deploy solar panels and antennas with ksp_part_action part="all" do="Extend".
- Electric charge running out makes probes uncontrollable; watch resources.

When something looks wrong, take a ksp_screenshot or read ksp_events before acting."""


# --------------------------------------------------------------------------- MCP plumbing


def public_tools():
    return [{k: v for k, v in t.items() if not k.startswith("_")} for t in TOOLS]


def call_tool(name, arguments):
    for t in TOOLS:
        if t["name"] == name:
            arguments = arguments or {}
            # Say plainly what is wrong with the arguments; models (small ones above all) recover from that.
            accepted = t["inputSchema"].get("properties", {})
            missing = [k for k in t["inputSchema"].get("required", []) if arguments.get(k) is None]
            unknown = [k for k in arguments if k not in accepted]
            if missing or unknown:
                problem = []
                if missing:
                    problem.append("missing: " + ", ".join(missing))
                if unknown:
                    problem.append("not an argument of this tool: " + ", ".join(unknown))
                return {"content": text("%s cannot run (%s). It takes: %s." % (name, "; ".join(problem), ", ".join(accepted) or "no arguments")), "isError": True}
            mark = work_mark(name, True)
            try:
                content, failed = t["_fn"](arguments), False
            except BridgeError as e:
                content, failed = text(str(e)), True
            except Exception as e:  # never let one bad call take the server down
                work_mark(name, False, mark)
                return {"content": text("Internal error in the KSP bridge server: %s: %s" % (type(e).__name__, e)), "isError": True}
            work_mark(name, False, mark)
            # Whatever the player has typed to the model in the game meanwhile goes on the end of what it reads next,
            # and so does word that a line to the player is due.
            if name not in NO_CHAT_AFTER:
                said, talk = chat_look()
                if said:
                    content = list(content) + text(PLAYER_SAID + json.dumps(said, indent=1))
                elif talk.get("say_now") and name not in NOT_WORK:
                    content = list(content) + text(SAY_NOW % (talk.get("why") or "", int(round(float(talk.get("every_s") or 20)))))
            return {"content": content, "isError": failed}
    return {"content": text("Unknown tool: %s" % name), "isError": True}


def background_wait_hint():
    """Command line that waits without any call limit, for agents that can run shell commands in the background."""
    return '"%s" "%s" wait \'{"program_done": true}\' 3600' % (sys.executable, os.path.abspath(__file__))


VOICE_NOTES = """
The ship's voice (ksp_voice)
- If the player asks for the ship to talk, or mentions the voice edition: ksp_voice on. The ship's computer
  then announces events by itself; add ksp_voice say for what only you know (the plan, your answer, a
  choice you made). Keep it short and plain; it is heard, not read.
- If the player wants to give orders by voice: loop on ksp_voice listen. Each time it returns their words,
  carry them out, report with ksp_voice say, and listen again. Stop looping when they say so.
- While the ship's voice is in use, a ksp_chat reply is spoken as well as shown: do not say the same
  line again with ksp_voice say."""


def instructions():
    return INSTRUCTIONS + (VOICE_NOTES if VOICE_EDITION else "") + "\n\n" + (
        "Long waits\n"
        "- A flight takes minutes; one tool call may not be allowed to. Three things make that a non-issue:\n"
        "  1. ksp_flight_plan runs a whole list of steps inside the game from a single call.\n"
        "  2. ksp_wait accepts long timeouts and returns \"timeout\" when this client's measured call budget ends;\n"
        "     just call it again (events are never lost between calls). ksp_call_limit shows or measures the budget.\n"
        "  3. If you have a shell tool that can run a command in the background and tell you when it exits, run\n"
        "       " + background_wait_hint() + "\n"
        "     there instead of polling (the JSON is the same 'until' object ksp_wait takes, the number is the\n"
        "     maximum seconds). It prints the result and exits when the condition is met.")


class Server:
    """JSON-RPC over stdio. Tool calls run on worker threads so a long wait never blocks cancellation or other calls."""

    def __init__(self, stdin=None, stdout=None):
        self.stdin = stdin or sys.stdin
        self.stdout = stdout or sys.stdout
        self.out_lock = threading.Lock()
        self.inflight = {}
        self.inflight_lock = threading.Lock()

    def send(self, message):
        data = json.dumps(message, ensure_ascii=False) + "\n"
        with self.out_lock:
            try:
                self.stdout.write(data)
                self.stdout.flush()
            except (OSError, ValueError):
                pass

    def handle_simple(self, message):
        method = message.get("method")
        params = message.get("params") or {}
        if method == "initialize":
            LIMITS.set_client(params.get("clientInfo"))
            wanted = params.get("protocolVersion")
            return {
                "protocolVersion": wanted if wanted in PROTOCOL_VERSIONS else PROTOCOL_VERSIONS[0],
                "capabilities": {"tools": {"listChanged": False}},
                "serverInfo": {"name": SERVER_NAME, "title": "KSP AI Bridge", "version": SERVER_VERSION},
                "instructions": instructions(),
            }
        if method == "ping":
            return {}
        if method == "tools/list":
            return {"tools": public_tools()}
        if method == "resources/list":
            return {"resources": []}
        if method == "resources/templates/list":
            return {"resourceTemplates": []}
        if method == "prompts/list":
            return {"prompts": []}
        raise LookupError(method)

    def notification(self, message):
        if message.get("method") != "notifications/cancelled":
            return
        request_id = (message.get("params") or {}).get("requestId")
        with self.inflight_lock:
            c = self.inflight.get(request_id)
        if c is None:
            return
        # The client gave up on this call: that is a direct measurement of its limit.
        if c.elapsed() > 20:
            LIMITS.record_cut(c.elapsed(), "client sent a cancellation")
        c.cancelled.set()

    def note_overlap(self):
        """A new call arriving while an over-long one is still open means the client stopped waiting for it."""
        with self.inflight_lock:
            stale = [c for c in self.inflight.values() if c.exploring and c.elapsed() > LIMITS.proven() + 5 and not c.cancelled.is_set()]
        for c in stale:
            LIMITS.record_cut(max(SAFE_CALL_S, c.elapsed() - 3.0), "a new call arrived while this one was still open")
            c.cancelled.set()

    def run_tool(self, message):
        params = message.get("params") or {}
        token = (params.get("_meta") or {}).get("progressToken")
        c = CallContext(self, message["id"], token, params.get("name"))
        with self.inflight_lock:
            self.inflight[message["id"]] = c
        _local.ctx = c
        try:
            result = call_tool(params.get("name"), params.get("arguments"))
        finally:
            with self.inflight_lock:
                self.inflight.pop(message["id"], None)
        if c.cancelled.is_set():
            return  # nobody is listening for this answer any more
        if not result.get("isError") and c.elapsed() > SAFE_CALL_S:
            LIMITS.record_ok(c.elapsed())
        self.send({"jsonrpc": "2.0", "id": message["id"], "result": result})

    def serve(self):
        for line in self.stdin:
            line = line.strip()
            if not line:
                continue
            try:
                message = json.loads(line)
            except ValueError:
                self.send({"jsonrpc": "2.0", "id": None, "error": {"code": -32700, "message": "Parse error"}})
                continue
            if not isinstance(message, dict):
                continue
            if "id" not in message:
                self.notification(message)
                continue
            if "method" not in message:
                continue  # a response to something we never asked
            if message.get("method") == "tools/call":
                self.note_overlap()
                threading.Thread(target=self.run_tool, args=(message,), daemon=True).start()
                continue
            try:
                reply = {"jsonrpc": "2.0", "id": message["id"], "result": self.handle_simple(message)}
            except LookupError:
                reply = {"jsonrpc": "2.0", "id": message["id"], "error": {"code": -32601, "message": "Method not found: %s" % message.get("method")}}
            except Exception as e:
                reply = {"jsonrpc": "2.0", "id": message["id"], "error": {"code": -32603, "message": "%s: %s" % (type(e).__name__, e)}}
            self.send(reply)
        # stdin closed: let calls that are still running answer (matters when requests are piped in), then leave
        deadline = time.time() + LIMITS.budget() + 10
        while time.time() < deadline:
            with self.inflight_lock:
                if not self.inflight:
                    break
            time.sleep(0.05)


def cli_wait(argv):
    """ksp_mcp.py wait '<until json>' [max seconds]: block until a condition holds. No call limit applies here."""
    until = json.loads(argv[2]) if len(argv) > 2 and argv[2].strip() else {}
    max_s = float(argv[3]) if len(argv) > 3 else 3600.0
    stop_on_event = False
    if isinstance(until, dict) and "stop_on_event" in until:
        stop_on_event = bool(until.pop("stop_on_event"))
    started = time.time()
    events = []
    result = {}
    failures = 0
    while time.time() - started < max_s:
        try:
            result = wait_in_game(until or None, min(600.0, max_s - (time.time() - started)), stop_on_event)
            failures = 0
        except BridgeError as e:
            failures += 1
            if failures >= 5:
                print(json.dumps({"stopped_because": "lost contact with KSP", "error": str(e), "events": events}, ensure_ascii=False))
                return 1
            time.sleep(5)
            continue
        events.extend(result.get("events") or [])
        if result.get("stopped_because") != "timeout":
            break
    else:
        result["stopped_because"] = "gave up after %d s" % max_s
    if events:
        result["events"] = events[-60:]
    result["waited_real_s"] = round(time.time() - started, 1)
    print(json.dumps(result, ensure_ascii=False))
    return 0


def main(argv):
    if len(argv) >= 2 and argv[1] == "tools":
        for t in public_tools():
            print("%-18s %s" % (t["name"], t["description"][:110]))
        return 0
    if len(argv) >= 2 and argv[1] == "wait":
        return cli_wait(argv)
    if len(argv) >= 3 and argv[1] == "call":
        params = json.loads(argv[3]) if len(argv) > 3 else {}
        try:
            result = rpc(argv[2], params, timeout=float(argv[4]) if len(argv) > 4 else 45)
        except BridgeError as e:
            print("error:", e, file=sys.stderr)
            return 1
        if isinstance(result, dict) and "base64" in result:
            path = os.environ.get("SHOT", "ksp_screenshot.jpg")
            with open(path, "wb") as f:
                f.write(base64.b64decode(result.pop("base64")))
            result["saved"] = path
        print(json.dumps(result, indent=1, ensure_ascii=False))
        return 0
    # MCP speaks UTF-8. Python opens a pipe in the system's code page (cp1252 on most Windows), where any other
    # character would fail to be written and the client would wait for that answer for ever.
    for stream in (sys.stdin, sys.stdout):
        if hasattr(stream, "reconfigure") and (stream.encoding or "").lower().replace("-", "") != "utf8":
            stream.reconfigure(encoding="utf-8")
    Server().serve()
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv))
