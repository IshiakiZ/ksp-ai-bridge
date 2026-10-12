# The air and the radio

Two things the AI could not see before 2026-10-08: what the air is doing to the craft it is flying, and
how far the craft's radio reaches. `ksp_air` and `ksp_comms` tell it. Both read only; both work in the
editor and in flight.

## The air: `ksp_air`

A rocket or an aeroplane flies straight because the air, when the craft strays from the way it is going,
pushes it back. It tumbles when the air pushes it further instead. Which of the two a craft gets is where
the air's push acts against where its mass is, and both move during a flight: fuel burns off, stages go.

**In flight it is measured.** The game works the air out part by part: drag against the way each part
moves, at that part's centre of pressure; the lift of a body at an angle; the lift and drag of wings and
fins. Those are read as the game applied them in its last physics step, and added up:

* **drag_kN** and **lift_kN**, and drag as a part of the craft's weight;
* **nose_off_the_way_it_flies_deg**, with the same split into **angle_of_attack_deg** and
  **sideslip_deg** as the craft's own controls have it;
* **air_is**: `steadying` if the turning the air puts on the craft, about its centre of mass, brings the
  nose back towards the way it is flying, `upsetting` if it takes it further off; with
  **air_turns_the_nose_kNm**, how hard;
* **steering**: what the craft has to turn itself with, in the same units: its reaction wheels, and its
  engines swung through their whole angle;
* **speed_ms**, **mach**, **air_pressure_on_craft_kPa** (the dynamic pressure) and the air's density.

Where the air is upsetting the craft with more than its wheels and engines can give, a note says so in
plain words, and what to do.

**worst_so_far** keeps, for the whole flight, the greatest pressure of the air (with the time, speed and
height), how far off the nose got while the air pressed, and the strongest upsetting turn. The moment
that matters in an ascent is a few seconds long, and an AI looking once a minute would miss it. `reset`
starts it again.

Tried on 2026-10-08 on one ascent of the test rocket: at 14.8 km and Mach 1.93 the air pressed with
18 kPa, the nose was 4 degrees off, drag was 11.8 kN (a ninth of the weight), and the air was steadying
the craft with 10 kN m against 64 it could steer with; the greatest pressure so far had been 64 kPa at
24 seconds, 5.2 km up. Nothing has been flown to a tumble with it yet.

**In the editor** nothing is moving, so it is the game's own centre-of-lift marker that is asked, for a
craft flying nose first: **lift_behind_mass_centre_m** with the tanks full, **with_tanks_empty_m**, and
**stable** if both are behind. A craft whose lift is ahead of its mass, or gets ahead of it as the tanks
empty, is told so, and the same warning comes back with every `ksp_build` (tried with a rocket given
four fins on its pod: "acts 4.37 m AHEAD of the centre of mass"). For an aeroplane `ksp_craft info` has
more (lift rating, take-off speed, where the wheels are).

## The radio: `ksp_comms`

The game's rule is short. Two aerials reach each other out to the square root of the product of their
powers. Several aerials on one craft that are made to be combined add up to less than their sum: the
strongest, times the sum over the strongest raised to a power of about three quarters. And the signal is
nearly full over the first third of the reach and falls to nothing at its end.

What comes back is what that means for this craft:

* **aerials**: each one, its kind (the pod's own, direct, relay), its power, whether it adds up with
  others, and how fast it sends science; **power_together**;
* **reach**: to the tracking station as it is now (and fully upgraded, if it is not), and to a craft like
  itself;
* **heard_from**: for every world, its nearest and farthest distance from the home world, the signal a
  craft there would have straight to the tracking station at each, and `always`, `only when near` or
  `never`;
* in flight, **now**: connected or not, the signal, what control the craft has, how far it is from home
  as a part of its reach, and the path the signal takes, hop by hop;
* **rules**: whether this save needs a signal at all, and whether a probe needs one to be steered.

Checked against the game on 2026-10-08: a Mk1 Command Pod alone (power 5000) reaches 35.4 Mm to a fully
upgraded tracking station, is heard from the Mun with a signal of 0.71 to 0.76, and is never heard from
Minmus.

What it leaves out: worlds in the way (the game loses the signal behind a planet), relays already in
orbit, and the moons of other worlds, which are counted as their world.
