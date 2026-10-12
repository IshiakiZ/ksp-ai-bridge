# Structure and strain

`ksp_structure` tells the AI how well a craft is holding together: measured in flight, estimated in the
editor. A short form of the estimate comes back with every `ksp_build`, so the AI sees a weak design
before it launches it.

## In flight: measured

The game joins each part to its parent with one physics joint (three, round the rim, where the joint is
2.5 m or wider). Those joints are not rigid. As the game sets them up they are springs: very stiff ones
along the joint, softer ones against bending, inside a hard stop a metre out. So a loaded joint gives a
little: the two parts are pulled apart and bent out of line, more the harder they are loaded.

That is what is measured, for every joint, about seventeen times a second:

* **pulled_apart_m**, and as **percent_of_breaking** how far that is towards the joint's stop;
* **bent_deg**: how far the part is turned against its parent from the way the two were put together;
* **out_of_place_m**: how far the part is from where it should be, seen from the root part.

With them: the hottest parts against the temperature that destroys them, g-force and pressure against the
weakest part's limit, whether this save enforces those limits at all, and how the craft is stiffened
(struts, autostruts, rigid attachments, launch clamps).

**worst_so_far** keeps the worst of each for the whole flight, with the mission time it happened at. A
rocket is strained hardest for a few seconds (lift-off, maximum dynamic pressure, staging) that an AI
looking once a minute would miss. `reset` starts it again. Joints that break under load are listed in
**broken_in_this_flight** with how far they had been pulled when they went.

What the figures mean, from the craft flown so far:

| Craft | Pulled apart | Bent | What happened |
| --- | --- | --- | --- |
| An ordinary two-stage rocket with two side boosters, 22 t | 1% on the pad, 2.9% under the boosters | 0.5° (the boosters, leaning in) | flew normally |
| A 36 t tank on one 0.625 m tank over a Mainsail | 1.2 to 1.7% | 0° | flew straight for the 12 seconds it was watched |
| The same tank on a neck of six 0.625 m tanks | 2.5% on the pad; 7% a second after ignition | 0.9° on the pad; 4.5° a second after ignition | folded and destroyed itself 6 seconds after ignition, at 41% and 9° |
| The thin-necked craft again (2026-10-09), climbing straight with nothing to disturb it | 7 to 8% | 0° | climbed 1.8 km in 14 seconds without bending at all |
| The same, given a short steering input 1.5 s after lift-off | 6 to 9% | 3 to 3.7°, back and forth | stopped climbing at 405 m and went over |
| The same with four struts from the big tank to the engine, and the same input | 0% at every joint of the neck | 0° | climbed straight to 2.2 km in 14 seconds |
| The same with one autostrut instead ("heaviest", on the engine), and the same input | 0% at every joint of the neck | 0° | climbed straight to 2.25 km in 14 seconds |

So a few percent and under a degree is ordinary. Several percent and several degrees is a craft folding
up, and it is destroyed by its own parts hitting one another long before any joint reaches its stop. A
craft on the edge of folding does not start by itself: a perfectly straight climb has nothing to start it.
Steering, a gust or the engine's gimbal is what starts it, and then it grows.

Struts and autostruts take the load off the joints they cross: with four struts from the heavy tank to the
engine, or one autostrut from the engine to it, every joint of the thin neck read nothing at all, where
without them each read 7%.

**Why not the force on each joint?** Unity gives a figure for the force on a joint, and each joint has a
force and a torque at which it breaks, so "percent of breaking force" looks like the obvious measure. It
was tried first and reads zero in flight: Unity leaves the force of a joint's springs out of that figure,
and the game's joints hold by their springs alone until the stop is reached. (By the same token a joint
in this game cannot break from load until it has been pulled out to its stop. That last is read off how
the joints are set up; no joint has yet been seen to break that way here. Two tries on 2026-10-09, with a
36 t tank on one side of a core on the weakest radial decoupler, its joint loaded to 118% of what it holds,
fell over into the launch pad within a second of lighting up, one with the joints left soft and one with
them rigid; in the crash one joint was pulled 65% of the way to its stop.)

## In the editor: estimated

Nothing is under load in the editor, so it is worked out for the craft as it stands, stage by stage:

* **pull_percent**: what each joint must pass on when the engines light (everything on its far side is
  pushed along in proportion to its mass, less the thrust it makes itself), against what the game will
  let that joint hold. Stock joints are strong: a 0.625 m stack joint holds 5,000 kN (the game's
  physics settings multiply every part's listed strength by 50).
* **bends_deg_per_g_sideways**: how far the craft would bend at that joint under a sideways push of one
  g, from the stiffness the game gives the joint's spring (60,000 kN m a radian for a 1.25 m stack joint,
  24,000 for a part on a surface; a 2.5 m joint is three joints round the rim and does not bend) and
  whatever struts cross it.
* **bends_deg_under_thrust**: the bend from thrust alone where something pushes off to one side of where
  it is fixed, as a booster does. For the test rocket's boosters this says 0.3°; in flight they measured
  0.4 to 0.5°.
* **folding_under_thrust**: whether the craft folds up under its own thrust. A craft tilted a little is
  tipped further by the push that goes through each joint, while the joint's spring holds it back; each
  joint's share is the pull times the distance to the mass it pushes, over the spring's stiffness, and a
  row of joints whose shares add up to 1 or more folds. The thin-necked craft above scores 1.4 and folds
  (once something starts it); the single-necked one scores 0.33 and flew; the ordinary rocket 0.08, the
  stock Kerbal X 0.17. `worst_at` says which stage, and when in its burn, comes nearest.

The reply carries notes saying what to do: wider joints, a shorter thin section, the heavy things lower,
struts across the weak joint, autostrut on the parts beyond it, or less thrust.

### Struts, autostruts and closed fairings

The game makes a strut, an autostrut, and the joint a closed fairing makes between its base and the part
it holds, all in the same way: one more joint, between two parts that are already joined through others,
which pins them together at one point and puts a spring between them (24,000 kN m a radian against
turning, the stiffness of a small part on a surface). The estimate puts each one in as the game makes it,
and works out the joints it crosses together:

* Two parts pinned together at two places cannot turn against each other at all. So a strut pinned beside
  a joint it crosses stops that joint turning, and struts from a heavy tank to the engine under a thin
  neck lock the tank to the engine outright.
* One strut over a long run of joints fixes only its two ends: the joints between can still zigzag, held
  by their own springs.
* An autostrut is pinned at the middle of the part it is set on, which is usually on the line of the
  joints it crosses, so the estimate gives it less credit than a strut beside them (in flight the one
  tried did as well as four struts: see the table).

(Where the game pins a strut is a quirk of its own: it takes the far end's place on the target, as the
target sees it, in the frame of the part the strut is on, which can put the pin metres from either end.
It is a pin all the same, and the estimate puts it where the game does.)

The thin-necked craft (a 36 t tank on a neck of six 0.625 m tanks over a Mainsail):

| Stiffening | Folding ratio estimated | In flight, after the same steering input |
| --- | --- | --- |
| none | 1.4 | bent 3 to 3.7° back and forth, stopped climbing at 405 m and went over |
| one strut from the tank to the engine | 0.11 | not flown |
| four struts from the tank to the engine | 0.07 | straight to 2.2 km; the neck's joints carried nothing |
| autostrut ("heaviest") on the engine, which runs it to the big tank | 0.21 | straight to 2.25 km; the neck's joints carried nothing |

`stiffening.in_the_estimate` (and `stiffening_counted` in a build's reply) says what was counted, and
each joint a link crosses lists it under `crossed_by`.

### Stage by stage

Every stage that lights an engine or drops something is taken in turn: the parts still on the craft after
it has fired, the engines burning then, at the start of the stage's burn and at its end, with the tanks as
the game's own delta-v reckoning has them. `stages` lists each, with its thrust, its mass at the start and
at burnout, the folding ratio at both, its hardest-pulled joint and the joint with the largest share of
folding. A stage pushes harder on a heavy load it carries as its own tanks empty, but its own tanks are
also less mass to push: which way it goes depends on the craft, so both ends are looked at. The stock
Kerbal X, for one, scores 0.17 at lift-off (as the estimate always said) and 0.23 at the end of its first
stage's burn, its worst; its later stages 0.21, 0.18 and nothing.

## Limits

* The folding check has two unstrutted test craft behind it, one each side of the line, both simple
  stacks, and the strutted ones above. Where the line really falls between 0.33 and 1.4 is not known, and
  "close to folding" (0.5 to 1) is a guess.
* The folding ratio is a sum over the ways a craft can bend (Dunkerley's), which is never smaller than the
  real figure. With struts it is on the high side: the estimate still has the joints a strut crosses carry
  the whole push, where in the game the strut takes it from them (the four-strut craft scores 0.07, and in
  flight its neck carried nothing). `pull_percent` likewise does not take a strut's share off the joints it
  crosses.
* Thrust is the engines' rated vacuum thrust; a stage is taken as starting with its tanks as the game's
  delta-v reckoning has them (full, if that is not ready yet).
* Sideways loads in the estimate are per g: how many g a given turn puts on a craft is not worked out.
* Parts meant to move against their parent (hinges, pistons, rotors, wheels and legs) are left out of the
  bending figures. More than two hundred joints crossed by struts are taken as rigid.
* Tried on 2026-10-08 and 2026-10-09 with stock parts on Kerbin, in flights of a few seconds to two
  minutes.
