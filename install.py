#!/usr/bin/env python3
"""Install KSP AI Bridge from a download: copy the ready-built plugin into the game and tell Claude where
the tools are. Nothing is compiled, so only Python 3.8+ is needed.

    python3 install.py                     install into the KSP it finds (Steam's usual folders)
    python3 install.py "/path/to/KSP"      install into that copy of the game
    python3 install.py --mcp               only write .mcp.json again

Working from the source instead? Use ./setup.sh, which builds the plugin first.
"""

import json
import os
import shutil
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, os.path.join(HERE, "mcp"))
import ksp_mcp  # noqa: E402


def find_game(given):
    if given:
        given = os.path.expanduser(given)
        if not os.path.isdir(os.path.join(given, "GameData")):
            sys.exit("'%s' does not look like a KSP folder (it has no GameData inside)." % given)
        return given
    found = ksp_mcp.ksp_dir()
    if not found:
        sys.exit('Kerbal Space Program was not found. Give its folder: python3 install.py "/path/to/Kerbal Space Program"')
    return found


def install_plugin(game):
    """Copy every mod folder in this download's GameData into the game's."""
    source = os.path.join(HERE, "GameData")
    if not os.path.isdir(source):
        sys.exit("This folder has no GameData to install. From the source, run ./setup.sh instead.")
    for name in sorted(os.listdir(source)):
        if not os.path.isdir(os.path.join(source, name)):
            continue
        target = os.path.join(game, "GameData", name)
        os.makedirs(target, exist_ok=True)
        for folder, _, files in os.walk(os.path.join(source, name)):
            there = os.path.join(target, os.path.relpath(folder, os.path.join(source, name)))
            os.makedirs(there, exist_ok=True)
            for file in files:
                # A settings file the player has changed is left alone.
                if file == "settings.cfg" and os.path.exists(os.path.join(there, file)):
                    continue
                shutil.copy2(os.path.join(folder, file), os.path.join(there, file))
        if name == "AIBridge":
            # Left over from a development build, which would load a second copy of the plugin.
            for stale in ("AIBridgeDev.dll",):
                if os.path.exists(os.path.join(target, stale)):
                    os.remove(os.path.join(target, stale))
            shutil.rmtree(os.path.join(target, "PluginData", "dev"), ignore_errors=True)
        print("installed %s into %s" % (name, target))


def write_mcp(game, given):
    """The entry Claude Code reads from this folder; the desktop chat app wants the same thing in its own file."""
    server = {"command": sys.executable, "args": [os.path.join(HERE, "mcp", "ksp_mcp.py")], "timeout": 3600000}
    if given:
        server["env"] = {"KSP_DIR": game}
    path = os.path.join(HERE, ".mcp.json")
    config = {}
    if os.path.exists(path):
        try:
            with open(path) as f:
                config = json.load(f)
        except ValueError:
            config = {}
    config.setdefault("mcpServers", {})["ksp"] = server
    with open(path, "w") as f:
        json.dump(config, f, indent=2)
        f.write("\n")
    print("wrote %s" % path)
    return server


def main(argv):
    arguments = [a for a in argv[1:] if not a.startswith("--")]
    if any(a in ("-h", "--help") for a in argv[1:]):
        print(__doc__)
        return
    given = arguments[0] if arguments else os.environ.get("KSP_DIR")
    game = find_game(given)
    if "--mcp" not in argv:
        install_plugin(game)
    server = write_mcp(game, given)
    voice = os.path.exists(os.path.join(HERE, "mcp", "ksp_voice.py"))
    print("""
Next:
  1. Start (or restart) KSP. An "AI Bridge" panel appears when the game has loaded.
  2. Open a new Claude session in this folder (%s) and approve the "ksp" tools when asked.
     For the Claude desktop chat instead, add this to the "mcpServers" part of its claude_desktop_config.json
     and restart the app:
       "ksp": %s
  3. Ask it to fly something.%s
No Claude? `python3 mcp/ksp_local.py` flies with a model run on this computer (see the README).""" % (
        HERE, json.dumps(server),
        "\n  4. For the ship's voice and spoken commands: %s, then ask Claude to turn the ship's voice on."
        % ("powershell -ExecutionPolicy Bypass -File setup_voice.ps1" if os.name == "nt" else "./setup_voice.sh") if voice else ""))


if __name__ == "__main__":
    main(sys.argv)
