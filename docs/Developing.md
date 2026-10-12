# Developing

```bash
./build.sh check      # compile only (both the release and the development variant)
./build.sh dev        # hot-reload build: a running KSP swaps the new code in within a second
./build.sh release    # normal build, installed to GameData (needs a KSP restart)
./package.sh          # the two downloads, as zip files in dist/
```

```bash
tools/rpc status      # call a plugin command from the shell
tools/mcp_call.py ksp_parts '{"search": "terrier"}'   # call an MCP tool exactly as Claude would
tools/trace 200       # the autopilot's flight recorder: attitude and control outputs, five samples a second
```

`./build.sh dev` installs a tiny loader that watches `GameData/AIBridge/PluginData/dev` and reloads the
bridge without restarting the game; run `./build.sh release` to go back to the normal single DLL.
(The commands whose names begin `dev_fx_` and `dev_mods_`, and `dev_set`, `dev_call`, `dev_explode` and `dev_destroy`, are for
the sister mods: they do something only in a development build that has those mods folded into it, which is made in the
workspace all of these repositories come from.)
The development build also has a few testing aids that the release build leaves out (`dev_set_orbit`
puts the vessel into orbit round any body, and `dev_set_position` sets it down anywhere on one (by how
high the sun stands there, and on dry land, if asked), as the game's debug menu does; `dev_entry_model` and
`dev_atmo` compare the entry prediction with the game; `dev_voice_tap` records what the ship's voice
plays, `dev_voice_key` presses the talk key and `dev_voice_panel` does what the panel's voice and talk key
buttons do; `dev_explode`, `dev_destroy`, `dev_fx_stats`,
`dev_fx_set`, `dev_fx_call`, `dev_timescale`, `dev_fps` and `dev_frames` are for looking at explosions
and timing them; `dev_record` writes a scripted
stretch of the game to numbered pictures and a sound file, which `tools/fxvideo.py` turns into the
explosions video, or with `live` keeps every frame the game draws while it runs normally;
`dev_antialiasing` shows which kind of smoothing a visual mod's post-processing is using; `dev_camera`
says where the flight camera really is, and `dev_raycast` lists everything solid on a line straight down
from a spot).
The game's log is `~/Library/Logs/Squad/KSP/Player.log`.

Source layout:

* `src/AIBridge/Core` – server, JSON, main-thread dispatch, in-game panel
* `src/AIBridge/Api` – commands: game, editor and craft builder, flight, navigation, planner, parts, science,
  surface travel and aircraft, docking, EVA, flight plans, voice
* `src/AIBridge/Pilot` – attitude controller, auto-stager, the ship's-computer commentary, and the programs:
  ascent, node, landing (powered, targeted, atmospheric), docking, aircraft (hold, take-off, fly-to, runway
  landing), rover, EVA
* `mcp/ksp_mcp.py` – the MCP server and the operating notes Claude receives
* `mcp/ksp_local.py` – the same tools for a locally run model, and the planner for small ones
* `mcp/ksp_voice.py`, `mcp/voice_worker.py`, `setup_voice.sh`, `setup_voice.ps1` – the voice edition:
  speaking, listening, and installing the speech models (the second script is for Windows)
