# Limits

* Built and tested on KSP 1.12.5 for macOS with both expansions, in a stock install.
* **Aiming in an atmosphere** needs to start from orbit. With parachutes alone it predicts the drag and has
  been flown once, with a capsule on Kerbin (252 m off). Where the engine has to do the braking it has been
  flown on Duna with one lander (43 m); Eve and Laythe, heavier craft and weaker engines are untried.
* **Thin-air landings** are safe rather than frugal: about 730 m/s on Duna, aimed, for a lander a careful
  pilot could put down for less.
* **Landing a big upper stage on its engine.** With the stock Kerbal X in an 80 km orbit, `land` (aimed at the
  space centre, no `stage`) kept the whole upper stage, since it has landing legs, and planned to brake on its
  engine from 942 m/s to a stop 2.5 km up; it hit the ground at 384 m/s (Windows, 2026-10-10). Why the braking
  fell short (fuel, thrust in thick air, or the plan) was not looked into. With `stage: true` it dropped to the
  capsule and landed under the parachute.
* **Time warp on the ground** now always runs on rails. Before that was fixed, a long 4x physics warp
  tipped the Duna lander over where it stood.
* **The voice edition** has been run on macOS and, since 2026-10-10, on Windows 11: its setup script
  (`setup_voice.ps1`) installed the speech models there; the ship said lines in the game with Windows' own
  voice and with Kokoro; and lines Kokoro spoke into a file were written down by Whisper word for word (about
  0.7 s a line) and taken by the game as heard speech. On Windows the microphone was opened and read (levels
  only); nobody spoke to it. Nothing of it has run on Linux, where the fallback voice is written and was checked by
  reading. With the Claude Code command line
  as the brain for the terminal listener (`ksp_voice.py listen --brain claude`), two questions asked one
  after the other were each answered from the game in 3 to 5 seconds, the second as part of the same
  conversation. The wake word (`--trigger open`) was checked on a recording of the phrase, not through a
  microphone. Nobody has listened to it: that the game plays the lines and
  that a recogniser understands them was checked, how it sounds was not.
* **The planner for small local models** was tried with one model, `qwen3:8b`, on eleven requests in made-up
  situations, and flown in the game for one of them (see
  [A model on your own computer](A-model-on-your-own-computer.md)). Other models and longer missions are
  untried.
* **Aircraft** means fixed wings and wheels: no helicopters, VTOL or seaplanes. The terrain watch looks
  about three minutes ahead along the track; it warns, but cannot help, when the ground rises faster than
  the aircraft can climb. Runway data for the KSC and the island airfield was measured in the game;
  other strips need their coordinates and heading.
* **Rovers** find their way round what they can see a few tens of metres ahead. There is no map, so a
  large enclosed area can still box one in; it then stops and reports.
* **Docking** was tested with small craft and Clamp-O-Tron ports. A target turning faster than about
  12 degrees a second is refused.
* **EVA**: boarding on foot needs rungs all the way from the ground to the hatch (the test craft's first
  ladder stopped 0.9 m short, and the kerbal said so). Construction mode and repairs are not covered.
* The builder shapes fairings (a shell that follows its payload's outline, and interstages) but does not
  set up robotic parts. Parts a mod reshapes while the game runs (procedural parts, B9PartSwitch) are
  placed by the shape they come in: none is installed here but the plume switch StockWaterfallEffects adds
  to the Mammoth and the Vector through B9PartSwitch, which changes only the look of the exhaust.
* **How long one tool call may run** was measured with the tools attached to Claude Code in the Claude
  desktop app (the client names itself `claude-code`): single calls of 100, 300, 600 and 1200 seconds all ran
  to the end, so there is no limit there up to at least twenty minutes. Other clients have not been timed; the
  server assumes 45 seconds for a client it has not seen and measures the real figure on first use
  (`ksp_call_limit` shows it). Flight plans do not depend on it either way.
