# KSP AI Bridge

A Kerbal Space Program 1 mod that lets an AI design rockets, launch them, fly them and land them for you.

You talk to Claude; Claude plays the game. It can look up parts, put a rocket, aircraft or rover together
part by part in front of you, fly it to orbit, plan and fly maneuvers, go to the Mun, land on a spot you
name, dock two ships, send a kerbal out to plant a flag and come home again, while you watch, ask it
things, and take over whenever you like.

It is self-contained: no kOS, kRPC or MechJeb. The plugin has its own attitude controller, staging logic,
craft builder and autopilots. No API key is needed: the AI is the Claude app, or a model you run on your
own computer. A second download, the **voice edition**, adds a ship's computer that speaks and takes
spoken orders.

![A craft built by the AI in the VAB](docs/img/mun-lander-vab.jpg)

```
Claude  ──MCP (stdio)──►  mcp/ksp_mcp.py  ──HTTP on 127.0.0.1──►  AIBridge.dll inside KSP
```

Anything that must react faster than an AI can think (holding an attitude, staging at burnout, a suicide
burn) runs in the plugin every physics frame; the AI only decides what to do next.

## Install

From a download (needs KSP 1.12.x and Python 3.8 or later):

```bash
python3 install.py
```

Then start KSP, open a new Claude session in this folder, approve the `ksp` server and ask for something.
[Installing from source, the desktop app, and the rest](docs/Installing.md).

## Things to ask

* "Start KSP, make a sandbox save called Test and build me a rocket that can reach orbit."
* "Design a crewed Mun lander that can get home again, then fly the whole mission."
* "Land next to my Mun base." / "Put the lander down at 5°N 55°W."
* "Build a small jet, fly it to the island airfield and land there."
* "Dock with the station." / "Send Valentina out to plant a flag, then bring her back in."

## You stay in charge

* **Talk to it in the game.** The AI Bridge panel has a box to type in: **Ask** a question and the AI
  answers without changing what it is doing; **Tell** it something and it does that instead.
  [More](docs/Talking-to-the-AI.md).
* **Never silent.** What you say is answered at once, the AI says a line of its own first and then about
  every 20 seconds while it works, and when it cannot (it is thinking, or inside a long wait) the bridge
  says what it can see the AI doing. A switch in the panel turns it off.
* **Pause AI** refuses every further command until you press Resume. **Take controls** stops the autopilot
  and hands the ship back.
* The panel's **x** puts it away into an **AI** button on the game's toolbar; `Alt+F10` brings it back.
* The server listens only on `127.0.0.1`, needs a token that changes at every game start, and rejects
  requests from web browsers.

![The panel, with a question and an instruction in it](docs/img/chat-panel.jpg)

## What the AI can do

42 tools (43 in the voice edition). The autopilots: `ksp_ascent` (ground to orbit), `ksp_plan` and
`ksp_execute_node` (transfers and burns), `ksp_land` (anywhere, at a chosen spot, with or without air),
`ksp_dock`, `ksp_fly` (aircraft, take-off to landing), `ksp_drive` (rovers) and `ksp_eva`. `ksp_build`
makes a craft from a parts list and reads back the game's own delta-v figures and whether the craft will
hold together; `ksp_structure` measures how hard it is being strained in flight, `ksp_air` whether the
air is steadying it or upsetting it, and `ksp_comms` how far its aerials reach.

* [Tools and autopilots](docs/Tools-and-autopilots.md)
* [Describing a craft](docs/Describing-a-craft.md)
* [Structure and strain](docs/Structure-and-strain.md)
* [The air and the radio](docs/The-air-and-the-radio.md)
* [Jobs longer than one tool call](docs/Jobs-longer-than-one-tool-call.md)
* [Voice edition: the ship talks, and listens](docs/Voice-edition.md)
* [A model on your own computer, instead of Claude](docs/A-model-on-your-own-computer.md)

## What has been flown

All in a real KSP 1.12.5 sandbox with no human input: a Mun landing and return; a landing 18 m from chosen
coordinates on the Mun and 43 m from them on Duna, after flying there; a capsule brought down 252 m from
the launch pad from orbit; docking; aircraft flown to another runway; a rover driven round buildings; a
flag planted on an EVA. [The whole list](docs/What-has-been-flown.md), and
[the limits](docs/Limits.md).

Made and tested on KSP 1.12.5 for macOS. It is plain C# and Python with nothing in it that belongs to one system.
On Windows 11 the plugin and its commands, called with `tools/rpc`, drove the tests of the other mods on
2026-10-09 and 10 (saves, launches, the controls, the camera, pictures); the MCP server answered there (the game's
state, a picture, the parts); the flight programs flew Kerbal X to an 80 km orbit and brought its capsule down
within 700 m of the space centre every time (414, 557, 588, 691 and 643 m), the last three in a row on the release
build; and the voice edition's models spoke and understood lines, and its microphone was opened (nobody spoke to
it). What 0.7.0 added (never silent, and the panel carried by hand) has been tried on the Mac only so far.
Nothing has been run on Linux.

## Licence

[CC BY 4.0](LICENSE): use it, change it and pass it on as you like, as long as you credit Ishiaki and link back here.

## Developing

`./build.sh check` compiles, `./build.sh dev` makes a hot-reload build that a running KSP swaps in within a
second, `./build.sh release` installs the normal one and `./package.sh` makes the two downloads.
[More](docs/Developing.md).
