# Talking to the AI

While the AI is flying you can talk to it from inside the game, without going back to the chat window it
runs in. The AI Bridge panel has a box to type in and two buttons:

![The panel with a question and an instruction in it](img/chat-panel.jpg)

*(A staged exchange, to show the panel: the numbers in it are not a real rocket's.)*

| | |
| --- | --- |
| **Ask** | A question. The AI answers and goes on with exactly what it was doing: nothing that is running in the game stops or changes. |
| **Tell** | An instruction. The AI does it, changing its plan and what the ship is doing if need be, and says what it is doing about it. |
| Enter | Either: the AI judges from your words which you meant. |

The AI's answer appears in the panel's conversation (its last five lines are shown) and across the top of
the screen. In the voice edition you can still speak instead: hold the talk key.

## Never silent

An AI takes seconds to answer and minutes to do what it was asked, and through most of that it says nothing.
Since 0.7.0 you are not left looking at a game in which nobody seems to have heard:

* **At once.** The moment you say something (typed, or spoken), the bridge itself answers with what is true:
  `Got it: the AI has your message now`, or that it will see it when its present command returns, or
  `Got it, but no AI is connected just now`. That line is the bridge's, not the AI's, and is shown as such:
  dimmer, with a dot in front, where the AI's lines begin `AI:`.
* **The AI is told to talk.** With your words it is told to say one short line first, before doing anything.
  After that, whenever it has said nothing to you for 20 seconds while it works, the result of its next
  command tells it that a line is due: what it found, what it is doing, what comes next. The same when you
  gave it the job in its own chat window: the result of its first command asks for a line.
* **And when it says nothing all the same** (it is thinking, or inside one long command such as a wait), the
  bridge says every 20 seconds what it can see: `The AI has been reading the ship's instruments and planning
  a manoeuvre, and is now waiting on the ship`, `The AI is still waiting on the ship: 40 seconds`, with the
  program that is flying; or, when nothing at all comes back, `Still nothing from the AI: 40 seconds`, and
  after a minute that it may have stopped. It never says what it cannot see, and it stops when the AI has
  answered and gone quiet, or has plainly gone.

In the voice edition these lines are spoken as well: the AI's `ksp_chat reply` in the ship's voice, the
bridge's own by the ship's computer.

The switch is in the panel: **never silent**. Off, the bridge says nothing of its own and the AI is not
reminded. The 20 seconds can be changed (8 to 120) with the bridge command `chat_talk {"every_s": 30}`; both
are kept in `GameData/AIBridge/PluginData/talk.txt`.

Only an AI counts as one: the MCP server tells the game when one of its tools begins and ends. Commands sent
to the bridge any other way (a script) start none of this.

While you are typing in the box the game's own keys are held off, so an M does not open the map; Escape
or a click anywhere else gives them back.

## How it reaches the AI

An AI only "hears" while one of its tool calls is running, or when it makes its next one. So what you say
is handed to it three ways:

* `ksp_wait` and `ksp_warp`, where an AI spends most of a flight, end at once with your words in their
  result (`player_messages`). A warp on rails carries on by itself meanwhile.
* Every other tool has your words added to the end of its result.
* `ksp_chat` with `listen_s` waits for you to say something, for when the AI has nothing else to do.

Each message says what kind it is (question, instruction, or message) and what to do with it. The AI
answers with `ksp_chat reply`. A question changes nothing because the flying is done by the game: an
autopilot program or a flight plan goes on running while the AI looks something up and answers.

The panel says which case you are in under what you typed: `the AI will see this in a moment` while its
commands are arriving, or `the AI is not connected just now: it will see this when it next does something`.

## Limits

* **If the AI has finished its turn** (it has stopped and is waiting for you in its own chat window) nothing
  is listening, and your words wait until you prompt it there. A flight plan or an autopilot it left
  running goes on regardless. Ask it at the start to "keep listening in the game" and it will wait with
  `ksp_chat listen_s` between jobs.
* A message waits at most as long as the AI's current tool call lasts if that call is neither a wait nor
  a warp (a scene loading, a craft being built).
* Never silent was tried on 2026-10-11 through the real tool protocol, with a script in the AI's place: the
  answer at once, the line asked of the AI on its first result and after 20 quiet seconds, the bridge's own
  lines through a 50-second wait and through an AI that took the message and never answered, and silence once
  the AI had answered and stopped. Not yet tried with a real model at the other end, nor heard in the voice
  edition.
* Tried on 2026-10-08 through the real tool protocol, with a test command standing in for the typing: a
  question ended a 40-second wait in the same frame and came back marked as a question; an instruction said
  between two calls came back on the end of the next one; `listen_s` returned what was said 2.5 seconds
  later. Typing in the box itself, and a whole flight with a conversation going on, have not been tried yet.
