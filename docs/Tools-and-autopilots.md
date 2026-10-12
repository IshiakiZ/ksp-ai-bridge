# Tools and autopilots

| Area | Tools |
| --- | --- |
| Session | `ksp_status`, `ksp_start_game`, `ksp_saves`, `ksp_goto`, `ksp_screenshot`, `ksp_ui`, `ksp_message` |
| Talking with you | `ksp_chat` (answer what you typed in the game's panel, or wait for you to say something: see [Talking to the AI](Talking-to-the-AI.md)) |
| Design | `ksp_parts`, `ksp_part_info`, `ksp_build`, `ksp_craft`, `ksp_launch` |
| Telemetry | `ksp_vessel`, `ksp_vessel_parts`, `ksp_structure` (see [Structure and strain](Structure-and-strain.md)), `ksp_air` and `ksp_comms` (see [The air and the radio](The-air-and-the-radio.md)), `ksp_events`, `ksp_bodies`, `ksp_vessels`, `ksp_terrain`, `ksp_science` |
| Flying | `ksp_control`, `ksp_attitude`, `ksp_part_action`, `ksp_autopilot` |
| Autopilots | `ksp_ascent`, `ksp_plan`, `ksp_node`, `ksp_execute_node`, `ksp_land`, `ksp_dock`, `ksp_fly`, `ksp_drive`, `ksp_eva` |
| Time and long jobs | `ksp_wait`, `ksp_warp`, `ksp_flight_plan`, `ksp_call_limit`, `ksp_flight_save` (quicksave, quickload, revert, recover) |
| Voice edition | `ksp_voice` (narration on and off, say, listen, settings) |
| Anything else | `ksp_raw` calls any plugin command; `{"method": "help"}` lists them |

## The autopilots

| Tool | What it does |
| --- | --- |
| `ksp_ascent` | Gravity turn from the ground to a circular orbit, staging on the way. |
| `ksp_plan` + `ksp_execute_node` | Circularise, change an apsis, transfer to a moon, vessel or planet, correct mid-course, match planes or velocity, return home; then fly the burn. |
| `ksp_land` | Lands anywhere, at a spot you name or under the ship. **No air:** plans the de-orbit burn and plane change, brakes to a stop over the spot and descends. **Air:** de-orbit burn (timed from a drag prediction when aiming), drops the service module, holds the heat shield forward, opens parachutes when safe. **Thin air** (Duna): the engine does the braking, aimed at the spot, tail-first against the airflow, and the parachutes open for the last stretch straight down. It lands on the engine alone when parachutes are missing or too weak. |
| `ksp_dock` | Turns to face the target port, flies round to its front with RCS and closes until the ports latch, following a target that is slowly turning and matching roll. Also undocks. |
| `ksp_fly` | Fixed-wing aircraft: take-off, a heading/altitude/speed hold that stays on and climbs over high ground by itself, fly-to, and a full approach and landing at the KSC runway, the island airfield or any strip you describe. |
| `ksp_drive` | Drives a rover to a spot, finding its own way round buildings, vessels, steep banks and drops. |
| `ksp_eva` | Sends a kerbal out; walking, jetpack flight, ladders, flags, and getting back aboard. |
