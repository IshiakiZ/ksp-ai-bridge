#!/usr/bin/env python3
"""Fly KSP with a model running on your own computer: no account and no API key.

This is the same set of tools Claude gets through ksp_mcp.py, handed instead to a local
model through Ollama (the default) or any server that speaks the OpenAI chat format
(LM Studio, llama.cpp's server, vLLM...). The model must support tool calling.

    python3 mcp/ksp_local.py                         chat, using the first model Ollama has
    python3 mcp/ksp_local.py "launch my rocket to an 80 km orbit"
    python3 mcp/ksp_local.py --model qwen3:8b --tools all
    python3 mcp/ksp_local.py --url http://localhost:1234/v1 --model my-model      LM Studio and friends
    python3 mcp/ksp_local.py --voice                 talk to it (hold Y in the game) and hear the ship answer
    python3 mcp/ksp_local.py --planner off           a small model chooses one tool at a time, as a big one does

Small models (under 30 billion parameters, and any whose size cannot be told) plan first: the request
becomes a whole flight plan in one answer, which is checked against the game's own commands and then
flown by the game in one go. Anything that is not a flight (a question, building a craft) is worked
through one tool at a time.

Start KSP first (or ask the model to). Type "exit" to leave the chat.
Needs Python 3.8+ and nothing else; --voice also wants the speech models from ./setup_voice.sh
(setup_voice.ps1 on Windows).
"""

import argparse
import json
import os
import queue
import re
import sys
import threading
import time
import urllib.error
import urllib.request

# A local runner has no client cutting calls short, so waits may run as long as they are asked to.
os.environ.setdefault("KSP_MCP_MAX_CALL_S", "3600")
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import ksp_mcp  # noqa: E402

# Small models cope better with fewer tools. "core" leaves out screenshots (most local models cannot
# see them) and the rarely needed odds and ends; "all" is everything Claude gets.
CORE = ["ksp_status", "ksp_start_game", "ksp_saves", "ksp_goto", "ksp_parts", "ksp_part_info", "ksp_build", "ksp_launch",
        "ksp_vessel", "ksp_vessels", "ksp_events", "ksp_control", "ksp_attitude", "ksp_autopilot", "ksp_part_action",
        "ksp_ascent", "ksp_plan", "ksp_execute_node", "ksp_land", "ksp_fly", "ksp_drive", "ksp_dock", "ksp_eva",
        "ksp_terrain", "ksp_wait", "ksp_warp", "ksp_flight_plan", "ksp_flight_save", "ksp_science", "ksp_chat", "ksp_structure", "ksp_air", "ksp_weather", "ksp_comms"]
FLIGHT = ["ksp_status", "ksp_vessel", "ksp_vessels", "ksp_events", "ksp_control", "ksp_attitude", "ksp_autopilot",
          "ksp_ascent", "ksp_plan", "ksp_execute_node", "ksp_land", "ksp_wait", "ksp_warp", "ksp_flight_save", "ksp_chat"]

EXTRA = """
You are running on the player's own computer and operate the game only through the tools.
Work in small steps: call a tool, read what comes back, decide the next step. Do not invent tool
results or part names. Use the numbers the player gave you, and leave out any tool argument you have no
reason to set: the defaults are good. A program is finished when ksp_wait says so, not before. When you
report figures, copy them from the last tool result. When the job is done, or you need something from the
player, say so briefly. If a tool result contains "player_messages", the player has spoken to you from
inside the game: answer with ksp_chat reply, and only change what you are doing if they asked you to.
If a tool result says SAY A LINE TO THE PLAYER NOW, call ksp_chat reply with one short sentence about
what you are doing before your next step: they are watching the game and must not be left in silence."""


def post(url, payload, timeout):
    request = urllib.request.Request(url, data=json.dumps(payload).encode(), headers={"Content-Type": "application/json"})
    with urllib.request.urlopen(request, timeout=timeout) as response:
        return json.loads(response.read())


def get(url, timeout=10):
    with urllib.request.urlopen(url, timeout=timeout) as response:
        return json.loads(response.read())


class Backend:
    """One of the two wire formats: Ollama's own, or the OpenAI chat-completions shape."""

    def __init__(self, url, model, context, think, timeout):
        self.url = url.rstrip("/")
        self.openai = self.url.endswith("/v1")
        self.model = model or self.first_model()
        self.context = context
        self.think = think
        self.timeout = timeout

    def first_model(self):
        try:
            if self.openai:
                models = [m["id"] for m in get(self.url + "/models").get("data", [])]
            else:
                models = [m["name"] for m in get(self.url + "/api/tags").get("models", [])]
        except (urllib.error.URLError, OSError) as e:
            sys.exit("Cannot reach a model server at %s (%s).\nStart Ollama (`ollama serve`) or pass --url for another server." % (self.url, e))
        if not models:
            sys.exit("The server at %s has no models. With Ollama: `ollama pull qwen3:8b` (or any model with tool support)." % self.url)
        return models[0]

    def chat(self, messages, tools):
        """Returns (text, [(call_id, name, arguments)]) and appends the assistant turn to messages."""
        if self.openai:
            reply = post(self.url + "/chat/completions", {"model": self.model, "messages": messages, "tools": tools, "tool_choice": "auto"}, self.timeout)
            message = reply["choices"][0]["message"]
            calls = []
            for call in message.get("tool_calls") or []:
                raw = call["function"].get("arguments") or "{}"
                try:
                    arguments = json.loads(raw) if isinstance(raw, str) else raw
                except ValueError:
                    arguments = {"_unparsed": raw}
                calls.append((call.get("id"), call["function"]["name"], arguments))
            messages.append({"role": "assistant", "content": message.get("content") or "", "tool_calls": message.get("tool_calls") or None})
            return message.get("content") or "", calls
        payload = {"model": self.model, "messages": messages, "tools": tools, "stream": False, "options": {"num_ctx": self.context}}
        if self.think is not None:
            payload["think"] = self.think
        try:
            reply = post(self.url + "/api/chat", payload, self.timeout)
        except urllib.error.HTTPError as e:
            detail = e.read().decode(errors="replace")
            if "think" in payload and "think" in detail:      # this model has no thinking switch
                payload.pop("think")
                self.think = None
                reply = post(self.url + "/api/chat", payload, self.timeout)
            elif "does not support tools" in detail:
                sys.exit("The model '%s' cannot call tools, so it cannot fly anything. Pick one that can (qwen3, llama3.1, mistral-nemo...)." % self.model)
            else:
                raise RuntimeError("model server said: %s" % detail[:400])
        message = reply.get("message", {})
        calls = []
        for call in message.get("tool_calls") or []:
            function = call.get("function", {})
            arguments = function.get("arguments") or {}
            if isinstance(arguments, str):
                try:
                    arguments = json.loads(arguments)
                except ValueError:
                    arguments = {"_unparsed": arguments}
            calls.append((None, function.get("name"), arguments))
        messages.append(message)
        return message.get("content") or "", calls

    def tool_result(self, call_id, name, content):
        if self.openai:
            return {"role": "tool", "tool_call_id": call_id, "content": content}
        return {"role": "tool", "tool_name": name, "content": content}

    def complete(self, messages, schema=None, temperature=0.3):
        """One answer with no tools offered (the planner's calls). With a JSON schema, servers that can hold the
        model to it do (Ollama's "format", OpenAI-style "response_format"); the answer is checked afterwards anyway."""
        if self.openai:
            payload = {"model": self.model, "messages": messages, "temperature": temperature}
            if schema:
                payload["response_format"] = {"type": "json_schema", "json_schema": {"name": "answer", "schema": schema}}
            try:
                reply = post(self.url + "/chat/completions", payload, self.timeout)
            except urllib.error.HTTPError:
                if not schema:
                    raise
                payload.pop("response_format")          # (a server that cannot hold to a schema: ask plainly)
                reply = post(self.url + "/chat/completions", payload, self.timeout)
            return reply["choices"][0]["message"].get("content") or ""
        payload = {"model": self.model, "messages": messages, "stream": False,
                   "options": {"num_ctx": self.context, "temperature": temperature}}
        if schema:
            payload["format"] = schema
        if self.think is not None:
            payload["think"] = self.think
        try:
            reply = post(self.url + "/api/chat", payload, self.timeout)
        except urllib.error.HTTPError as e:
            detail = e.read().decode(errors="replace")
            if "think" not in payload or "think" not in detail:
                raise RuntimeError("model server said: %s" % detail[:400])
            payload.pop("think")
            self.think = None
            reply = post(self.url + "/api/chat", payload, self.timeout)
        return (reply.get("message") or {}).get("content") or ""

    def size_b(self):
        """The model's size in billions of parameters, or None when it cannot be told."""
        if not self.openai:
            try:
                info = post(self.url + "/api/show", {"model": self.model}, 20)
                count = (info.get("model_info") or {}).get("general.parameter_count")
                if count:
                    return float(count) / 1e9
                size = billions((info.get("details") or {}).get("parameter_size") or "")
                if size:
                    return size
            except (urllib.error.URLError, OSError, ValueError, TypeError):
                pass
        return billions(self.model)         # names often say: qwen3:8b, Meta-Llama-3.1-8B-Instruct


def billions(text):
    """"8b", "8.2B", "500M" in a model's name or size, as billions of parameters."""
    m = re.search(r"(\d+(?:\.\d+)?)\s*([bBmM])(?![a-zA-Z])", text or "")
    if not m:
        return None
    return float(m.group(1)) / (1000.0 if m.group(2) in "mM" else 1.0)


def tool_schemas(which, brief):
    wanted = None if which == "all" else (FLIGHT if which == "flight" else CORE)
    out = []
    for t in ksp_mcp.TOOLS:
        if wanted is not None and t["name"] not in wanted:
            continue
        description = t["description"]
        if brief and len(description) > brief:
            description = description[:brief].rsplit(". ", 1)[0] + "."
        out.append({"type": "function", "function": {"name": t["name"], "description": description, "parameters": t["inputSchema"]}})
    return out


def run_tool(name, arguments, limit):
    arguments = arguments if isinstance(arguments, dict) else {}
    # Waiting for a program or a flight plan to end: nothing cuts calls short here, so let the wait run to
    # the end instead of coming back at every staging event, each of which is a chance to lose the thread.
    until = arguments.get("until") if name == "ksp_wait" else None
    if isinstance(until, dict) and (until.get("program_done") or until.get("sequence_done")) and "stop_on_event" not in arguments:
        arguments = dict(arguments, stop_on_event=False)
        if float(arguments.get("timeout_s") or 0) < 1800:
            arguments["timeout_s"] = 1800
    result = ksp_mcp.call_tool(name, arguments)
    parts = []
    for item in result.get("content", []):
        if item.get("type") == "text":
            parts.append(item["text"])
        else:
            parts.append("[a %s was returned; this model cannot look at it]" % item.get("type", "file"))
    text = "\n".join(parts)
    if len(text) > limit:
        text = text[: limit * 2 // 3] + "\n... (%d characters left out) ...\n" % (len(text) - limit) + text[-limit // 3:]
    return ("ERROR: " if result.get("isError") else "") + text


def strip_thinking(text):
    return re.sub(r"<think>.*?</think>", "", text, flags=re.S).strip()


# --------------------------------------------------------------------------- the planner, for small models
#
# A small model choosing one tool at a time loses the thread over a long job: it re-sends commands, stops
# early, reports figures it never read. Planning first asks it for one thing it does well: the whole request
# as a flight plan, in a single answer. The plan is checked against the game's own list of commands and
# their parameters (and handed back with what is wrong, at most twice), then the game flies all of it by
# itself (ksp_flight_plan) and the model only reports how it went. What is not a flight (a question,
# building a craft, looking something up) is worked through one tool at a time, as before.

SMALL_MODEL_B = 30          # billions of parameters: below this (or when it cannot be told) the planner is on
# The commands a plan may use: the flight-plan commands, and nothing that loads a save or leaves the flight.
PLAN_COMMANDS = ["launch", "ascent", "set_target", "plan", "execute_node", "warp", "land", "quicksave", "control",
                 "attitude", "autostage", "part_do", "science_run", "dock", "undock", "drive", "fly", "eva", "recover", "wait"]
NEEDED = {"set_target": ["name"], "plan": ["maneuver"], "attitude": ["mode"], "fly": ["mode"], "eva": ["action"],
          "part_do": ["part", "action"]}
CLOSED = {("plan", "maneuver"), ("warp", "to"), ("eva", "action"), ("fly", "mode")}     # values the help lists in full
STEP_KEYS = ("do", "until", "timeout_s", "optional")
NEEDS_NO_SHIP = ("launch", "wait")
AUTOPILOT = ("ascent", "execute_node", "land", "fly", "drive", "dock", "eva")       # each ends only when its program has
# The tool whose arguments carry the same names and types as each command's parameters (for checking types).
TYPED_BY = {"launch": "ksp_launch", "ascent": "ksp_ascent", "plan": "ksp_plan", "execute_node": "ksp_execute_node", "warp": "ksp_warp",
            "land": "ksp_land", "control": "ksp_control", "attitude": "ksp_attitude", "drive": "ksp_drive", "fly": "ksp_fly",
            "dock": "ksp_dock", "eva": "ksp_eva"}
# What the game's one-line help leaves out, and a small model got wrong without it (qwen3:8b, in tests).
HINTS = {
    "launch": "Only when the player asks for a launch or no ship is being flown. site: LaunchPad or Runway.",
    "ascent": "From the ground to a circular orbit; it circularizes by itself.",
    "set_target": "name: a world (Mun, Minmus, Duna...) or a vessel.",
    "plan": "at: apoapsis or periapsis. transfer: to the target. return: from a moon's orbit back to its planet.",
    "execute_node": "Flies the maneuver node made by plan, warping to it by itself.",
    "warp": "Give one of to, seconds, ut, rate. to=soi after a transfer or return burn, to reach the next world's sphere of influence.",
    "land": "Nothing given: come down below the ship's path, on whatever world it is at. lat and lon in degrees (south and west "
            "negative) for a spot. target: a landed vessel to land beside, never a world. place: only on Kerbin (launchpad, "
            "runway). Not for aircraft.",
    "fly": "Aircraft only: mode takeoff (from the runway it is on), goto (place: island_runway, runway; or lat and lon), "
           "land (runway: ksc or island).",
    "science_run": "Leave experiment out: every experiment that can run here.",
    "part_do": 'part: a part\'s name, or "all"; action: the button, e.g. "Extend Solar Panel", "Deploy Chute", "Extend Antenna".',
    "quicksave": "name: what to call the save.",
}
WORLDS = ["Sun", "Kerbol", "Moho", "Eve", "Gilly", "Kerbin", "Mun", "Minmus", "Duna", "Ike", "Dres", "Jool", "Laythe", "Vall",
          "Tylo", "Bop", "Pol", "Eeloo"]      # (the game's own list replaces these where it can be had)
# Parameters that tune how a program flies. Shown them, a small model fills every one with a made-up value
# (max_q_kpa=1000, turn_end_altitude_km=85: an ascent flown that way can fail), so they are left out unless the
# player's own words give the value. The defaults are good.
TUNING = {"ascent": ["turn_start_speed", "turn_end_altitude_km", "turn_shape", "max_q_kpa", "max_aoa", "warp"],
          "execute_node": ["warp", "autostage", "tolerance"],
          "land": ["offset_m", "clearance_m", "touchdown_speed", "safety_factor", "warp", "autostage", "periapsis_km", "stage",
                   "chutes", "engines", "deorbit"],
          "plan": ["periapsis_km", "in_seconds"], "warp": ["lead_s"], "launch": ["clear_site"],
          "fly": ["speed", "altitude_m", "approach_speed", "glideslope", "final_km", "rotate_speed", "pitch", "bank_limit", "radius_m",
                  "terrain_clearance_m"],
          "drive": ["speed", "stop_distance_m"], "dock": ["speed", "roll"]}

PLAN_SCHEMA = {
    "type": "object",
    "properties": {
        "kind": {"type": "string", "enum": ["plan", "other"]},
        "name": {"type": "string"},
        "steps": {"type": "array", "items": {"type": "object"}},
        "say": {"type": "string"},
    },
    "required": ["kind", "name", "steps", "say"],
}

PLANNER = """You plan flights in Kerbal Space Program for the player. Turn the player's request into a flight plan:
a list of steps the game carries out by itself, in order, each one starting when the one before has finished
(including any autopilot program it started).

Answer with one JSON object and nothing else:
{"kind": "plan", "name": "<two or three words>", "steps": [<steps>], "say": "<one short sentence telling the player what you are about to do>"}
When the request is not something to fly with the commands below (a question, building or changing a craft,
looking something up, anything these commands cannot do from where the ship is now), answer:
{"kind": "other", "name": "", "steps": [], "say": ""}

A step is {"do": "<command>", <its parameters>}. Any step may also have "until": {<conditions>} to hold until
they are true, "timeout_s" (seconds, default 1800) and "optional": true (carry on if it fails).
The commands and their parameters (use only these; leave out every parameter you have no reason to set):
%(commands)s
Conditions for "until": %(conditions)s

Rules:
- Use the player's numbers. Altitudes are in km where the parameter ends in _km.
- A command that starts an autopilot program (ascent, execute_node, land, fly, drive, dock, eva walk, fly
  or board) has finished only when the program has: never add a wait step after one.
- "plan" only makes a maneuver node; follow it with "execute_node", which flies it.
- ascent only climbs from the ground to orbit. To change an orbit: plan (set_apoapsis, set_periapsis,
  circularize) and execute_node.
- Launch a craft only when the player asks for a launch, or when there is no ship to fly yet.
- Add no step the player did not ask for and the request does not need. Telling the player figures needs no
  step: you are given the results when the plan has ended.

Examples (the ship's state in brackets):
[on the launch pad] put us in a 100 km orbit and tell me the apoapsis
{"kind": "plan", "name": "orbit 100 km", "steps": [{"do": "ascent", "altitude_km": 100}], "say": "Climbing to a 100 kilometre orbit."}
[an aircraft on the runway] fly to the island airfield and land there
{"kind": "plan", "name": "to the island", "steps": [{"do": "fly", "mode": "takeoff"}, {"do": "fly", "mode": "goto", "place": "island_runway"}, {"do": "fly", "mode": "land", "runway": "island"}], "say": "Taking off for the island airfield."}
[in orbit around Kerbin] take us to the Mun
{"kind": "plan", "name": "to the Mun", "steps": [{"do": "set_target", "name": "Mun"}, {"do": "plan", "maneuver": "transfer"}, {"do": "execute_node"}, {"do": "warp", "to": "soi"}, {"do": "plan", "maneuver": "circularize", "at": "periapsis"}, {"do": "execute_node"}], "say": "Setting course for the Mun."}
[in orbit around Kerbin] raise the periapsis to 120 km
{"kind": "plan", "name": "periapsis 120 km", "steps": [{"do": "plan", "maneuver": "set_periapsis", "altitude_km": 120}, {"do": "execute_node"}], "say": "Raising the periapsis to 120 kilometres."}
[anywhere] what is our altitude?
{"kind": "other", "name": "", "steps": [], "say": ""}"""

REPORT = """You are flying Kerbal Space Program for the player, and a flight plan you made for them has just ended.
Tell them in one to three short plain sentences what happened and where the ship is now, answering what
they asked. Copy every figure from the results below; never make one up. No lists, no markdown."""


def command_table():
    """{command: (its one-line help, {parameter: the values it may take, or None})}, from the game's own help."""
    table = {}
    for entry in ksp_mcp.rpc("help", timeout=15) or []:
        name = entry.get("method")
        if name in PLAN_COMMANDS:
            text = entry.get("help") or ""
            accepted = parameters(text)
            if name == "part_do":                    # (in a plan "do" names the command, so the button is "action")
                accepted.pop("do", None)
                accepted.setdefault("action", None)
            table[name] = (text, accepted)
    table["wait"] = ('Hold until the step\'s "until" conditions are met, or pause for its timeout_s seconds.', {})
    return table


def parameters(help_text):
    """The parameters named in a command's help, "params: {lat, lon | target: vessel name, maneuver: a|b}", with
    the values each may take where the help lists them."""
    found = {}
    m = re.search(r"params:\s*\{(.*)\}", help_text)
    if not m:
        return found
    for piece in re.split(r",|\s\|\s", re.sub(r"\([^)]*\)", "", m.group(1))):
        name, _, values = piece.partition(":")
        name, values = name.strip(), values.strip()
        if re.match(r"^[a-z_][a-z0-9_]*$", name):
            found[name] = values.split("|") if re.match(r"^[a-z0-9_]+(\|[a-z0-9_]+)+$", values) else None
    return found


def wait_conditions():
    return [c.strip() for c in re.sub(r"\([^)]*\)", "", ksp_mcp.WAIT_CONDITIONS).replace(".", "").split(",") if c.strip()]


def km(metres):
    try:
        return "%.1f km" % (float(metres) / 1000.0)
    except (TypeError, ValueError):
        return "?"


def game_state():
    """A few plain lines on where the game is (for the planner and the report), and the facts the check needs."""
    facts = {"scene": None, "situation": None}
    try:
        status = ksp_mcp.rpc("status", timeout=15)
    except ksp_mcp.BridgeError as e:
        return "The game cannot be reached: %s" % e, facts
    facts["scene"] = status.get("scene")
    lines = ["Scene: %s%s." % (status.get("scene"), (", save '%s'" % status["save"]) if status.get("save") else "")]
    if status.get("editor_craft"):
        lines.append("In the editor: the craft '%s'." % status["editor_craft"].get("name"))
    if status.get("open_dialogs"):
        lines.append("A dialog is open: %s." % ", ".join(status["open_dialogs"]))
    if status.get("scene") == "flight":
        try:
            v = ksp_mcp.rpc("vessel", timeout=15)
        except ksp_mcp.BridgeError:
            v = status.get("vessel") or {}
        facts["situation"], facts["vessel"] = v.get("situation"), v.get("name")
        where = {"PRELAUNCH": "on the launch site, not launched yet", "LANDED": "landed", "SPLASHED": "in the water",
                 "FLYING": "flying in the air", "SUB_ORBITAL": "on a sub-orbital path", "ORBITING": "in orbit",
                 "ESCAPING": "escaping", "DOCKED": "docked"}.get(v.get("situation"), v.get("situation"))
        lines.append("Flying '%s' at %s: %s, altitude %s." % (v.get("name"), v.get("body"), where, km(v.get("altitude_m"))))
        orbit = v.get("orbit") or {}
        if orbit and v.get("situation") not in ("PRELAUNCH", "LANDED", "SPLASHED"):
            lines.append("Orbit around %s: apoapsis %s, periapsis %s." % (orbit.get("body"), km(orbit.get("apoapsis_m")), km(orbit.get("periapsis_m"))))
        propulsion = v.get("propulsion") or {}
        if propulsion.get("dv_total_vac") is not None:
            lines.append("Delta-v left: %s m/s in vacuum." % propulsion["dv_total_vac"])
        if v.get("target"):
            lines.append("Target: %s." % (v["target"].get("name") if isinstance(v["target"], dict) else v["target"]))
        if v.get("maneuver_nodes"):
            facts["nodes"] = v["maneuver_nodes"].get("count") or 0
            lines.append("Maneuver nodes waiting to be flown: %s." % facts["nodes"])
        pilot = v.get("autopilot") or {}
        if pilot.get("program"):
            lines.append("Autopilot program running: %s." % pilot.get("program"))
    try:
        plan = ksp_mcp.rpc("sequence_status", timeout=10)
        if plan.get("state") == "running":
            facts["plan_running"] = True
            lines.append("A flight plan is running: '%s', step %s." % (plan.get("name"), plan.get("step")))
    except ksp_mcp.BridgeError:
        pass
    try:
        facts["worlds"] = [b["name"] for b in ksp_mcp.rpc("bodies", timeout=10) if b.get("name")]
    except (ksp_mcp.BridgeError, TypeError, KeyError):
        pass
    return "\n".join(lines), facts


def json_object(text):
    """The JSON object in a model's answer, or None."""
    text = strip_thinking(text or "")
    text = re.sub(r"^```(?:json)?\s*|\s*```$", "", text.strip())
    start, end = text.find("{"), text.rfind("}")
    if start < 0 or end <= start:
        return None
    try:
        found = json.loads(text[start:end + 1])
    except ValueError:
        return None
    return found if isinstance(found, dict) else None


def argument_types():
    """{(command, parameter): "number" | "boolean" | "string"}, from the tools that take the same arguments."""
    schemas = {t["name"]: t["inputSchema"].get("properties", {}) for t in ksp_mcp.TOOLS}
    types = {}
    for command, tool in TYPED_BY.items():
        for name, schema in schemas.get(tool, {}).items():
            if schema.get("type") in ("number", "boolean", "string"):
                types[(command, name)] = schema["type"]
    return types


def typed(value, kind):
    """The value as the type wanted, or None when it is not one: "85" will do for 85, "true" for true."""
    if kind == "boolean":
        if isinstance(value, bool):
            return value
        return {"true": True, "false": False}.get(str(value).strip().lower())
    if kind == "number":
        if isinstance(value, bool):
            return None
        if isinstance(value, (int, float)):
            return value
        try:
            return float(str(value).strip())
        except ValueError:
            return None
    return value if isinstance(value, str) else None


def check_plan(plan, commands, facts):
    """What is wrong with a plan, as sentences the model can act on ([] when it can run). What needs no asking
    (a tool's name for a command's, "85" for 85) is put right in place."""
    steps = plan.get("steps")
    if not isinstance(steps, list) or not steps:
        return ['"steps" must be a list of at least one step, like [{"do": "ascent", "altitude_km": 80}].']
    problems = []
    if len(steps) > 60:
        problems.append("A plan can have at most 60 steps.")
    conditions = wait_conditions()
    types = argument_types()
    worlds = [w.lower() for w in facts.get("worlds") or WORLDS]
    launched = False
    grounded = facts.get("situation") in ("PRELAUNCH", "LANDED", "SPLASHED")
    node = bool(facts.get("nodes"))                  # a maneuver node is waiting to be flown
    for n, step in enumerate(steps, 1):
        if not isinstance(step, dict):
            problems.append('Step %d is not an object like {"do": "ascent", "altitude_km": 80}.' % n)
            continue
        what = step.get("do")
        if isinstance(what, str) and what.startswith("ksp_") and what[4:] in commands:
            what = step["do"] = what[4:]
        if what not in commands:
            problems.append('Step %d: "%s" is not a command; "do" is always one of: %s.%s' % (n, what, ", ".join(sorted(commands)),
                            ' A part\'s button is pressed with {"do": "part_do", "part": "<part>", "action": "%s"}.' % what
                            if "part" in step or (isinstance(what, str) and " " in what) else ""))
            continue
        accepted = commands[what][1]
        for key, value in list(step.items()):
            if key in STEP_KEYS:
                continue
            if key not in accepted:
                problems.append('Step %d (%s): "%s" is not one of its parameters (%s).' % (n, what, key, ", ".join(accepted) or "it takes none"))
                continue
            if (what, key) in CLOSED and accepted[key] and str(value) not in accepted[key]:
                problems.append('Step %d (%s): %s must be one of %s, not "%s".' % (n, what, key, ", ".join(accepted[key]), value))
            kind = types.get((what, key))
            if kind:
                fixed = typed(value, kind)
                if fixed is None:
                    problems.append("Step %d (%s): %s must be %s, not %s." % (n, what, key, {"boolean": "true or false", "number": "a number",
                                                                                     "string": "text"}[kind], json.dumps(value)))
                else:
                    step[key] = fixed
            if key == "target" and what in ("land", "drive", "dock", "fly") and str(value).strip().lower() in worlds:
                problems.append("Step %d (%s): target is a vessel, and %s is a world. %s" % (n, what, value, "To land on the world the ship is "
                                "at, give no target (or lat and lon for a spot)." if what == "land" else "Leave it out."))
        if what == "warp" and len([k for k in ("to", "seconds", "ut", "rate") if k in step]) > 1:
            problems.append("Step %d (warp): give only one of to, seconds, ut and rate." % n)
        if what == "plan" and step.get("maneuver") == "circularize" and n > 1 and isinstance(steps[n - 2], dict) and steps[n - 2].get("do") == "ascent":
            problems.append("Step %d (plan circularize): ascent already ends in a circular orbit; leave this step out (and its execute_node)." % n)
        if what == "fly":
            mode = step.get("mode")
            if grounded and mode in ("cruise", "goto", "land"):
                problems.append('Step %d (fly %s): the aircraft is still on the ground; a {"do": "fly", "mode": "takeoff"} step comes first.' % (n, mode))
            if mode in ("takeoff", "cruise", "goto"):
                grounded = False
            elif mode == "land":
                grounded = True
        if what == "plan":
            node = True
        elif what == "execute_node":
            if not node:
                problems.append("Step %d (execute_node): no maneuver node is waiting to be flown there; a plan step makes one." % n)
            node = False
        elif what == "warp" and step.get("to") == "node" and not node:
            problems.append("Step %d (warp to node): no maneuver node is waiting there (execute_node has flown it, or none was made)." % n)
        for key in NEEDED.get(what, []):
            if step.get(key) in (None, ""):
                problems.append('Step %d (%s) needs "%s".' % (n, what, key))
        until = step.get("until")
        if until is not None:
            if not isinstance(until, dict) or not until:
                problems.append('Step %d: "until" must be an object of conditions, like {"altitude_above": 70000}.' % n)
            else:
                unknown = [k for k in until if k not in conditions]
                if unknown:
                    problems.append("Step %d: %s %s not a condition. The conditions are: %s." % (n, ", ".join(unknown), "is" if len(unknown) == 1 else "are", ", ".join(conditions)))
        if what == "wait" and until is None and "timeout_s" not in step:
            problems.append('Step %d: a wait step needs "until": {conditions}, or "timeout_s" for a plain pause.' % n)
        if what == "launch":
            if facts.get("situation") == "PRELAUNCH" and not launched and str(step.get("craft") or "").strip().lower() == str(facts.get("vessel") or "").strip().lower():
                problems.append("Step %d (launch): '%s' is already on the launch site, ready to go; leave the launch out and start with "
                                "what it is to do (ascent, or fly mode takeoff for an aircraft)." % (n, facts.get("vessel")))
            launched = grounded = True
        elif what == "land":
            grounded = True
        elif facts.get("scene") != "flight" and not launched and what not in NEEDS_NO_SHIP:
            problems.append("Step %d (%s): no ship is being flown (the game is in the %s), so the plan must start with a launch step."
                            % (n, what, facts.get("scene") or "main menu"))
            launched = True                      # (said once is enough)
        if what == "ascent" and facts.get("scene") == "flight" and not grounded:
            problems.append("Step %d (ascent): the ship is already off the ground; ascent only climbs from the ground to orbit. "
                            "Change an orbit with plan (set_apoapsis, set_periapsis or circularize) and execute_node." % n)
        elif what == "ascent":
            grounded = False
    if facts.get("plan_running"):
        problems.append("A flight plan is already running in the game; a new one cannot start until it ends. "
                        'If the player wants it stopped or changed, answer {"kind": "other"}.')
    return problems


def numbers_in(text):
    """The numbers in what the player said ("1,000 m" is 1000), without their signs."""
    return [abs(float(x)) for x in re.findall(r"\d+(?:\.\d+)?", re.sub(r"(?<=\d),(?=\d{3}\b)", "", text or ""))]


def tidy_plan(steps, request):
    """Put right, without asking, what small models add unasked: tuning values the player never gave (left out:
    the defaults are good) and waits for what has already happened. Returns notes on what was left out."""
    left, words, notes = numbers_in(request), (request or "").lower(), []

    def figure(value):
        try:
            return None if isinstance(value, bool) else abs(float(value))
        except (TypeError, ValueError):
            return None

    def take(number):
        """Use up one of the player's figures (km or m alike); False when none is left that matches."""
        for i, x in enumerate(left):
            if abs(number - x) < 1e-6 or abs(number - 1000 * x) < 1e-3 or abs(1000 * number - x) < 1e-3:
                del left[i]
                return True
        return False

    dicts = [s for s in steps if isinstance(s, dict)]
    # (the player's figures go first to what is not tuning: "85 km orbit" is the orbit's 85, not the turn's as well)
    for step in dicts:
        for key, value in step.items():
            if key not in STEP_KEYS and key not in TUNING.get(step.get("do"), []) and figure(value) is not None:
                take(figure(value))
    for n, step in enumerate(steps, 1):
        if not isinstance(step, dict):
            continue
        for key in TUNING.get(step.get("do"), []):
            if key not in step:
                continue
            value = step[key]
            if isinstance(value, bool) or str(value).lower() in ("true", "false"):
                wanted = key.rstrip("s") in words                 # ("no warp", "without the parachutes")
            else:
                wanted = figure(value) is not None and take(figure(value))
            if not wanted:
                notes.append("%s=%s (step %d)" % (key, json.dumps(value), n))
                del step[key]
    kept = []
    for step in steps:
        until = step.get("until") if isinstance(step, dict) else None
        pointless = isinstance(until, dict) and until and (
            "sequence_done" in until or     # (inside a plan that never comes: the plan is still running)
            (set(until) <= {"program_done", "scene"} and kept and isinstance(kept[-1], dict) and kept[-1].get("do") in AUTOPILOT))
        if isinstance(step, dict) and step.get("do") == "wait" and pointless:
            notes.append("a wait for %s" % ", ".join(until))
            continue
        kept.append(step)
    steps[:] = kept
    return notes


def describe_steps(steps):
    def one(step):
        rest = ", ".join("%s=%s" % (k, json.dumps(v)) for k, v in step.items() if k != "do")
        return "%s%s" % (step.get("do"), " " + rest if rest else "")
    return "; ".join(one(s) for s in steps if isinstance(s, dict))


def make_plan(backend, request, commands, state, facts, args):
    """Ask for the plan, check it, and hand it back with what is wrong until it is right (or give up).
    Returns the plan, or None when the request is not a flight or no runnable plan came."""
    listing = "\n".join("%s: %s%s" % (name, commands[name][0], (" " + HINTS[name]) if name in HINTS else "")
                        for name in PLAN_COMMANDS if name in commands)
    talk = [{"role": "system", "content": PLANNER % {"commands": listing, "conditions": ", ".join(wait_conditions())}},
            {"role": "user", "content": "Where the game is now:\n%s\n\nThe player's request: %s" % (state, request)}]
    for attempt in range(1 + args.plan_retries):
        started = time.time()
        answer = backend.complete(talk, schema=PLAN_SCHEMA)
        plan = json_object(answer)
        if plan is None:
            print("  [planner: the answer was not a plan (%.0f s)]" % (time.time() - started))
            return None
        if plan.get("kind") != "plan":
            print("  [planner: not a flight, working step by step (%.0f s)]" % (time.time() - started))
            return None
        notes = tidy_plan(plan["steps"], request) if isinstance(plan.get("steps"), list) else []
        problems = check_plan(plan, commands, facts)
        print("  plan%s (%.0f s): %s" % (" (corrected)" if attempt else "", time.time() - started, describe_steps(plan.get("steps") or [])))
        if notes:
            print("    (left out, as nothing the player said asked for it: %s)" % "; ".join(notes))
        if not problems:
            return plan
        for problem in problems:
            print("    x " + problem)
        talk.append({"role": "assistant", "content": json.dumps(plan)})
        talk.append({"role": "user", "content": "That plan cannot run as it is:\n- %s\nAnswer with the whole plan again, corrected, as one JSON object."
                     % "\n- ".join(problems)})
    print("  [planner: no plan that can run; working step by step instead]")
    return None


def wait_for_plan(name, backend, messages, tools, args, meanwhile=None):
    """Wait for the flight plan to end. What the player says meanwhile (in the game's panel, or by voice through
    meanwhile()) is answered step by step while the plan flies on. Returns the plan's final status."""
    note = ("(A flight plan of yours, '%s', is running in the game and keeps running while you answer. Do only what "
            "this asks; to stop the plan use ksp_flight_plan action=abort.)" % name)
    while True:
        result = ksp_mcp.wait_in_game({"sequence_done": True}, 25.0, stop_on_event=False)
        said = result.get("player_messages") or []
        if said:
            messages.append({"role": "user", "content": ksp_mcp.PLAYER_SAID + json.dumps(said, indent=1) + "\n" + note})
            turn(backend, messages, tools, args)
        spoken = meanwhile() if meanwhile else None
        if spoken:
            messages.append({"role": "user", "content": spoken + "\n" + note})
            reply = turn(backend, messages, tools, args)
            if reply and args.speak:
                args.speak(reply)
        status = ksp_mcp.rpc("sequence_status", timeout=15)
        if status.get("state") != "running":
            return status


def planned_turn(backend, messages, tools, args, request, meanwhile=None):
    """The request as a flight plan, flown by the game. Returns what to tell the player, or None when the
    request is not a flight (or no runnable plan came): the caller then works step by step."""
    try:
        commands = command_table()
    except ksp_mcp.BridgeError:
        return None                              # (no game to ask: step by step, which can start it)
    state, facts = game_state()
    try:
        plan = make_plan(backend, request, commands, state, facts, args)
    except (urllib.error.URLError, OSError, RuntimeError, KeyError, ValueError) as e:
        print("\n[the planner could not get an answer from the model: %s]" % e)
        return None
    if plan is None:
        return None
    name = (plan.get("name") or "flight plan").strip()[:60] or "flight plan"
    steps = plan["steps"]
    if plan.get("say"):
        print("\n" + plan["say"])
        if args.speak:
            args.speak(plan["say"])
    print("\n  -> ksp_flight_plan start %s" % json.dumps({"name": name, "steps": steps}, ensure_ascii=False)[:300])
    try:
        # (what ksp_flight_plan does, called directly so that nothing the player has typed in the panel is taken on the way)
        ksp_mcp.rpc("sequence_start", {"name": name, "steps": steps}, timeout=30)
    except ksp_mcp.BridgeError as e:
        # The game's own check found something the planner's did not: the request is worked step by step instead.
        print("     the game refused it: %s" % str(e)[:300])
        return None
    # (in the conversation from now on, so that whatever the player says while it flies is answered knowing it)
    record = {"role": "assistant", "content": "(I am flying this as a flight plan, '%s': %s. It is running in the game.)" % (name, describe_steps(steps))}
    messages.append({"role": "user", "content": request})
    messages.append(record)
    started = time.time()
    try:
        status = wait_for_plan(name, backend, messages, tools, args, meanwhile)
    except ksp_mcp.BridgeError as e:
        status = {"state": "unknown", "error": "lost contact with the game: %s" % e}
    except KeyboardInterrupt:
        print("\n[stopped watching: the flight plan flies on in the game; ksp_flight_plan action=abort stops it]")
        return "The flight plan is still flying in the game."
    print("     flight plan %s after %.0f s%s" % (status.get("state"), time.time() - started, (": " + status["error"]) if status.get("error") else ""))
    after, _ = game_state()
    outcome = {k: status.get(k) for k in ("state", "error", "steps") if status.get(k) is not None}
    report_request = [{"role": "system", "content": REPORT},
                      {"role": "user", "content": "The player asked: %s\nThe flight plan: %s\nHow it ended: %s\nWhere the ship is now:\n%s"
                       % (request, json.dumps(steps), json.dumps(outcome), after)}]
    try:
        words = strip_thinking(backend.complete(report_request))
    except (urllib.error.URLError, OSError, RuntimeError, KeyError, ValueError):
        words = ""
    if not words:
        words = "The flight plan %s. %s" % (status.get("state"), after.replace("\n", " "))
    print("\n" + words)
    record["content"] = "(I flew this as a flight plan: %s. It %s.) %s" % (describe_steps(steps), status.get("state"), words)
    return words


def answer(backend, messages, tools, args, request, meanwhile=None):
    """Work on one request from the player: as a flight plan when the planner is on and it is a flight, else
    one tool at a time. Returns what to tell the player."""
    if args.plan_first:
        words = planned_turn(backend, messages, tools, args, request, meanwhile)
        if words is not None:
            return words
    messages.append({"role": "user", "content": request})
    return turn(backend, messages, tools, args)


def turn(backend, messages, tools, args):
    """Let the model work on the last user message until it stops calling tools. Returns its last words."""
    said = ""
    for _ in range(args.max_steps):
        try:
            text, calls = backend.chat(messages, tools)
        except (urllib.error.URLError, OSError, RuntimeError) as e:
            print("\n[could not get an answer from the model: %s]" % e)
            return "I could not reach the model."
        text = strip_thinking(text)
        if text:
            print("\n" + text)
            said = text
        if not calls:
            return said
        for call_id, name, arguments in calls:
            shown = json.dumps(arguments, ensure_ascii=False)
            print("\n  -> %s %s" % (name, shown if len(shown) < 300 else shown[:300] + "..."))
            output = run_tool(name, arguments, args.result_chars)
            first = output.strip().splitlines()[0] if output.strip() else ""
            print("     %s" % (first[:200] + ("..." if len(first) > 200 or "\n" in output.strip() else "")))
            messages.append(backend.tool_result(call_id, name, output))
    print("\n[stopped after %d tool calls in one go; say 'continue' to carry on]" % args.max_steps)
    return said or "I have stopped after %d steps. Say continue to carry on." % args.max_steps


def voice_chat(backend, messages, tools, args):
    """Spoken commands in, the ship's voice out. Typed lines work as well."""
    import ksp_voice
    speaker = ksp_voice.speaker()
    print("Voice: %s, speech recognition %s." % (speaker.engine(), "ready" if ksp_voice.whisper_installed() else "not installed (type instead; %s adds it)" % ksp_voice.SETUP))
    if not args.no_narrate:
        ksp_voice.narration(True)
    jobs = queue.Queue()

    def heard(text):
        try:
            ksp_mcp.rpc("voice_heard", {"text": text, "queue": False}, timeout=10)     # shows the words in the game
        except ksp_mcp.BridgeError:
            pass
        jobs.put(text)

    def typed():
        while True:
            try:
                line = input().strip()
            except (EOFError, KeyboardInterrupt):
                jobs.put(None)
                return
            if line:
                jobs.put(None if line.lower() in ("exit", "quit") else line)

    def speak(words):
        try:
            speaker.say(words, wait=bool(args.voice_file))
        except (ksp_voice.VoiceError, ksp_mcp.BridgeError) as e:
            print("[could not speak: %s]" % e)

    def meanwhile():
        """Something said while a flight plan flies: answered at once (the plan flies on)."""
        try:
            request = jobs.get_nowait()
        except queue.Empty:
            return None
        if request is None:                          # ("exit": after the plan)
            jobs.put(None)
            return None
        print("\n> " + request)
        speak("Acknowledged.")
        return request

    args.speak = speak
    if args.voice_file:
        # Recordings instead of a microphone: each file is something the player said.
        for path in args.voice_file:
            words = ksp_voice.transcribe(path)
            print('\nheard in %s: "%s"' % (os.path.basename(path), words))
            heard(words)
        jobs.put(None)
    else:
        if ksp_voice.whisper_installed():
            how = 'Say "%s", then the command.' % args.wake if args.trigger == "open" else "Hold %s in the game while you talk." % speaker.settings["key"]
            ksp_voice.Listener(heard, trigger=args.trigger, wake=args.wake, log=lambda line: print("  " + line)).start()
            print(how + ' You can also type here ("exit" to quit).')
        else:
            print('Type what to do ("exit" to quit).')
        threading.Thread(target=typed, daemon=True).start()

    while True:
        request = jobs.get()
        if request is None:
            break
        print("\n> " + request)
        speak("Acknowledged.")
        # (recordings are a script, one request after another; a microphone may be spoken into at any time)
        reply = answer(backend, messages, tools, args, request, None if args.voice_file else meanwhile)
        if reply:
            speak(reply)
    ksp_voice.narration(False)
    ksp_voice.SPEAK.stop()
    ksp_voice.HEAR.stop()


def main():
    parser = argparse.ArgumentParser(description="Fly KSP with a local model (no account or API key).")
    parser.add_argument("request", nargs="*", help="What to do. Leave empty to chat.")
    parser.add_argument("--url", default=os.environ.get("KSP_LOCAL_URL", "http://localhost:11434"),
                        help="Model server. Ollama by default; give an address ending in /v1 for OpenAI-style servers.")
    parser.add_argument("--model", default=os.environ.get("KSP_LOCAL_MODEL"), help="Model name (default: the server's first).")
    parser.add_argument("--tools", choices=["core", "flight", "all"], default="core",
                        help="How many tools to offer: flight (14), core (29, default) or all (38).")
    parser.add_argument("--brief", type=int, default=0, metavar="N", help="Cut tool descriptions to N characters for models with little memory.")
    parser.add_argument("--context", type=int, default=32768, help="Context window to ask Ollama for, in tokens (default 32768).")
    parser.add_argument("--think", action="store_true", help="Let reasoning models think before each step (slower).")
    parser.add_argument("--max-steps", type=int, default=40, help="Most tool calls per request (default 40).")
    parser.add_argument("--result-chars", type=int, default=6000, help="Longest tool result passed back to the model.")
    parser.add_argument("--timeout", type=int, default=900, help="Seconds to wait for the model to answer.")
    parser.add_argument("--voice", action="store_true", help="Voice edition: speak your commands and hear the ship answer.")
    parser.add_argument("--trigger", choices=["key", "open"], default="key",
                        help="With --voice: 'key' listens while you hold the talk key in the game (default), 'open' listens all the time for the wake word.")
    parser.add_argument("--wake", default="computer", help="Wake word for --trigger open (default: computer).")
    parser.add_argument("--no-narrate", action="store_true", help="With --voice: do not let the ship's computer call out the flight by itself.")
    parser.add_argument("--voice-file", nargs="+", metavar="WAV", help="With --voice: recordings to treat as spoken commands, instead of the microphone.")
    parser.add_argument("--planner", choices=["auto", "on", "off"], default="auto",
                        help="Plan first: each request that is a flight becomes a whole flight plan in one answer, checked against the "
                             "game's commands and flown by the game. auto (default): on for models under %d billion parameters "
                             "and for any whose size cannot be told; off: one tool at a time, always." % SMALL_MODEL_B)
    parser.add_argument("--plan-retries", type=int, default=2, help="How many times a plan with mistakes goes back to the model to be put right (default 2).")
    args = parser.parse_args()
    for stream in (sys.stdout, sys.stderr):
        if hasattr(stream, "reconfigure"):
            stream.reconfigure(errors="replace")    # (a model's words in a narrow code page, as on Windows, must not stop it)

    backend = Backend(args.url, args.model, args.context, True if args.think else False, args.timeout)
    tools = tool_schemas(args.tools, args.brief)
    messages = [{"role": "system", "content": ksp_mcp.INSTRUCTIONS + "\n" + EXTRA}]
    print("KSP AI Bridge, local model: %s at %s, %d tools" % (backend.model, backend.url, len(tools)))
    size = backend.size_b() if args.planner == "auto" else None
    args.plan_first = args.planner == "on" or (args.planner == "auto" and (size is None or size < SMALL_MODEL_B))
    args.speak = None
    print("Planner: %s" % ("on" if args.plan_first else "off") + (
        "" if args.planner != "auto" else " (%s)" % ("size unknown" if size is None else "%.1f billion parameters" % size)))

    if args.voice or args.voice_file:
        import ksp_voice
        messages[0]["content"] += "\n" + ksp_voice.BRIEF
        # The voice loop speaks for the model; the tool would only let it talk to itself.
        tools = [t for t in tools if t["function"]["name"] != "ksp_voice"]
        voice_chat(backend, messages, tools, args)
        return

    if args.request:
        answer(backend, messages, tools, args, " ".join(args.request))
        return
    print('Tell it what to do ("exit" to quit).')
    while True:
        try:
            line = input("\n> ").strip()
        except (EOFError, KeyboardInterrupt):
            print()
            return
        if line.lower() in ("exit", "quit"):
            return
        if not line:
            continue
        answer(backend, messages, tools, args, line)


if __name__ == "__main__":
    main()
