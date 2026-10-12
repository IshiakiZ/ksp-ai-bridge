# A model on your own computer

No account or API key is needed for this route either. `mcp/ksp_local.py` hands the same tools to a
model you have downloaded and run yourself, through [Ollama](https://ollama.com) by default or any server
that speaks the OpenAI chat format (LM Studio, llama.cpp's server and the like). The model has to
support tool calling.

```bash
python3 mcp/ksp_local.py "put the ship on the pad into an 80 km orbit"
```

```bash
python3 mcp/ksp_local.py --model qwen3:8b --tools all
```

Without a request it opens a chat. `--tools flight`, `core` (the default) or `all` sets how many of the
tools the model is offered, since small models lose the thread when given too many; `--url` points it at
another server; `--voice` (voice edition) lets you speak to it and hear the ship answer.

## Small models plan first

A small model choosing one tool at a time loses the thread over a long job. So for models under 30
billion parameters (and any whose size cannot be told) `ksp_local.py` asks for one thing instead: the
whole request as a flight plan, in a single answer. That plan is checked before anything moves: every
command and parameter against the game's own list, the types, the conditions to wait for, and whether it
makes sense from where the ship is (a launch when nothing is being flown, an ascent from orbit, a
maneuver node flown that was never made, a world given as the vessel to land beside, an aircraft told to
cruise while still on the runway, the ship on the pad launched again). What is wrong goes back to the model
in plain words, at most twice; tuning values the player gave no figure for (a turn shape, a pressure
limit) are simply left out, since the defaults are good. Then the game flies the whole plan by itself
(`ksp_flight_plan`), and the model is asked only to report how it went, copying the figures.

Anything that is not a flight (a question, building a craft, looking something up) is worked through one
tool at a time as before, and so is a plan that never came right. What the player says while a plan
flies, typed in the game's panel or spoken, is answered at once and the plan flies on. `--planner off`
makes a small model work one tool at a time; `--planner on` makes a big one plan.

It was tried with one model, `qwen3:8b` under Ollama. In the game, with a Kerbal X on the pad, asked to
"put the rocket on the pad into an 85 km orbit, then tell me the apoapsis and periapsis": its first plan
had a needless circularisation after the ascent, the check sent it back, the second was the one ascent,
and the game flew it. "The apoapsis is 84.8 km and the periapsis is 83.5 km", which is what the game
said, 3 minutes 21 seconds from the request to the answer, at the first attempt (one tool at a time,
the same request had taken three attempts; see below).

It also planned eleven requests in made-up situations (on the pad, in orbit around Kerbin, in orbit
around the Mun, an aircraft on the runway, in the space centre), without the game flying them. In the
last round all eleven came out right: the questions and "build me a rocket" went step by step, and the
nine flights became plans the game can run, such as "go to the Mun and land there" from Kerbin orbit
(target, transfer, burn, warp to the Mun, circularise, burn, land) and "take us home to Kerbin and land"
from the Mun (return, burn, warp, land). Two needed the check's words to come right ("deploy the solar
panels" and "take off, fly to the island airfield and land there"). Earlier rounds, before the checks
and hints that came of them, sent a lander to land on the Mun as if it were a vessel, warped to a node
that had already been flown, launched a plane onto the launch pad and filled every tuning value with
inventions. A plan took 2 to 8 seconds to make on this Mac when it was quiet, and minutes when the game
and other programs had its memory. Longer missions, landings and aircraft have not been flown this way
yet, and no other model has been tried.

## Earlier, one tool at a time

The same model, before the planner, read the game's state and made a quicksave when asked, answered a
spoken question (see [Voice edition](Voice-edition.md)), and, asked to "fly the rocket on the pad into an
85 km orbit, then tell me the final apoapsis and periapsis", reached 84.9 x 83.5 km and reported exactly
that, in 3 minutes 15 seconds. That last one took three tries, and what went wrong is worth knowing: the
first time it flew to 80 km and called it 85; the second time it kept re-sending the ascent command,
restarting the climb each time, and announced success with the periapsis 339 km underground. What fixed it
was on the tool side, not a better prompt alone: a repeated ascent command now leaves the running one
alone, waiting for a program now waits to the end instead of returning at every staging event, and the
model is told to use the player's numbers and copy figures from the tool results. Expect a small local
model to manage single commands and short sequences like this; designing a working rocket from the parts
list is a lot to ask of one.
