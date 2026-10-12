using System;
using System.Collections.Generic;
using CommNet;
using UnityEngine;

namespace AIBridge.Api
{
    /// <summary>
    /// Two things an AI flying or designing a craft could not see before: what the air is doing to the craft,
    /// and how far its radio reaches.
    ///
    /// The air. A rocket or an aeroplane flies straight because the air, when the craft strays from the way it
    /// is going, pushes it back; it tumbles when the air pushes it further instead. Which of the two a craft
    /// gets is where the air's push acts against where its mass is, and in the game both move during a
    /// flight (fuel burns off, stages go). So in flight it is measured: every part's drag and lift as the game
    /// applied them in its last step, added up as a force and as a turning about the centre of mass, and said
    /// plainly: the air is steadying the craft, or upsetting it, and by how much against what the craft has to
    /// steer with. The worst of the flight is kept, since the moment that matters (the greatest pressure of the
    /// air, a few seconds in an ascent) is one an AI looking once a minute will miss. In the editor the figures
    /// for an aeroplane are in craft_info; this adds the same check for a rocket's fins.
    ///
    /// The radio. The game's rule is short: two aerials reach each other out to the square root of the product
    /// of their powers, and several on one craft add up to less than their sum. What an AI needs from it is
    /// what that means: whether this craft will still be heard at Duna, at its nearest and its farthest, and
    /// in flight how strong the signal is now and by what path it gets home.
    /// </summary>
    public static class AirApi
    {
        public static void Register(BridgeHost h)
        {
            Rpc.Sync("air", "What the air is doing to the craft. In flight, measured: speed, Mach number, the pressure of the air, the angle the craft meets it at, drag and lift, and whether the air is steadying the craft or upsetting it (with what the craft has to steer against it), now and at the worst of the flight so far. In the editor: where the lift of wings or fins acts against the centre of mass. params: {reset: true forgets the flight's worst so far}", Air).AlwaysAllowed = true;
            Rpc.Sync("comms", "The craft's radio: its aerials and what they add up to, how far that reaches a ground station or a craft like itself, and which worlds it would still be heard from at their nearest and farthest. In flight also the signal now and the path it takes home. Works in the editor and in flight.", a => Comms()).AlwaysAllowed = true;
        }

        static double R(double v, int digits = 1) => double.IsNaN(v) || double.IsInfinity(v) ? double.NaN : Math.Round(v, digits);

        // ---------------------------------------------------------------- the air

        sealed class Worst
        {
            public double Pressure, PressureAt, PressureSpeed, PressureAltitude;     // the greatest pressure of the air (kPa), when, and how fast and high the craft was
            public double Off, OffAt, OffPressure;                                    // the furthest the nose was from the way of flight (degrees) while the air pressed, when, and how hard it pressed
            public double Upset, UpsetAt;                                             // the strongest turning by the air away from the way of flight (kN m), and when
        }

        static Worst worst = new Worst();
        static Guid watched;
        static int tick;

        /// <summary>Called every physics step: the worst of the flight is kept (every third step is looked at).</summary>
        public static void FixedTick()
        {
            if (HighLogic.LoadedScene != GameScenes.FLIGHT || !FlightGlobals.ready) return;
            if (++tick % 3 != 0) return;
            Vessel v = FlightGlobals.ActiveVessel;
            if (v == null || !v.loaded || v.packed || v.isEVA) return;
            if (v.id != watched) { watched = v.id; worst = new Worst(); }
            double q = v.dynamicPressurekPa;
            if (q < 0.05 || v.srfSpeed < 5.0) return;
            double now = v.missionTime;
            if (q > worst.Pressure) { worst.Pressure = q; worst.PressureAt = now; worst.PressureSpeed = v.srfSpeed; worst.PressureAltitude = v.altitude; }
            if (q < 2.0 || v.ReferenceTransform == null) return;
            Vector3 way = ((Vector3)v.srf_velocity).normalized;
            Vector3 nose = v.ReferenceTransform.up;
            double off = Vector3.Angle(nose, way);
            if (off > worst.Off) { worst.Off = off; worst.OffAt = now; worst.OffPressure = q; }
            if (off < 1.5) return;
            Sum(v, out Vector3 _, out Vector3 turning);
            float back = Vector3.Dot(turning, Vector3.Cross(nose, way).normalized);
            if (-back > worst.Upset) { worst.Upset = -back; worst.UpsetAt = now; }
        }

        /// <summary>
        /// Every part's drag and lift as the game applied them in its last physics step (kN), added up, and their turning
        /// about the craft's centre of mass (kN m). The game works these out part by part: drag against the way each part
        /// moves, at the part's centre of pressure; the lift of a body at an angle; and the lift and drag of wings and fins.
        /// </summary>
        static void Sum(Vessel v, out Vector3 force, out Vector3 turning)
        {
            force = Vector3.zero; turning = Vector3.zero;
            Vector3 centre = v.CoM;
            List<Part> parts = v.parts;
            for (int n = 0; n < parts.Count; n++)
            {
                Part p = parts[n];
                if (p == null || p.partTransform == null) continue;
                if (p.dragScalar > 0f)
                {
                    Vector3 f = -p.dragVectorDir * p.dragScalar;
                    force += f; turning += Vector3.Cross(p.partTransform.TransformPoint(p.CoPOffset) - centre, f);
                }
                if (p.bodyLiftScalar != 0f)
                {
                    Vector3 f = p.partTransform.TransformDirection(p.bodyLiftLocalVector);
                    force += f; turning += Vector3.Cross(p.partTransform.TransformPoint(p.bodyLiftLocalPosition) - centre, f);
                }
                for (int m = 0; m < p.Modules.Count; m++)
                {
                    if (!(p.Modules[m] is ModuleLiftingSurface wing)) continue;
                    force += wing.liftForce; turning += Vector3.Cross(p.partTransform.TransformPoint(p.CoLOffset) - centre, wing.liftForce);
                    if (wing.useInternalDragModel) { force += wing.dragForce; turning += Vector3.Cross(p.partTransform.TransformPoint(p.CoPOffset) - centre, wing.dragForce); }
                }
            }
        }

        /// <summary>What the craft has to turn itself with, about its centre of mass (kN m): reaction wheels, and engines that swivel.</summary>
        static void Steering(Vessel v, out double wheels, out double engines, out int surfaces, out int thrusters)
        {
            wheels = 0; engines = 0; surfaces = 0; thrusters = 0;
            Vector3 centre = v.CoM;
            foreach (Part p in v.parts)
            {
                if (p == null) continue;
                foreach (PartModule m in p.Modules)
                {
                    if (m is ModuleReactionWheel wheel) { if (wheel.wheelState == ModuleReactionWheel.WheelState.Active) wheels += wheel.PitchTorque * wheel.authorityLimiter / 100.0; }
                    else if (m is ModuleControlSurface) surfaces++;
                    else if (m is ModuleRCS rcs) { if (rcs.rcsEnabled && rcs.isEnabled) thrusters++; }
                    else if (m is ModuleGimbal gimbal && !gimbal.gimbalLock)
                    {
                        // (an engine swung through its whole angle pushes sideways by its thrust times the sine of that, at its distance from the centre of mass)
                        double thrust = 0;
                        foreach (ModuleEngines e in p.FindModulesImplementing<ModuleEngines>()) if (e.EngineIgnited && !e.flameout) thrust += e.finalThrust;
                        if (thrust <= 0) continue;
                        double arm = (p.transform.position - centre).magnitude;
                        engines += thrust * Math.Sin(gimbal.gimbalRange * gimbal.gimbalLimiter / 100.0 * Math.PI / 180.0) * arm;
                    }
                }
            }
        }

        static object Air(Args a)
        {
            if (HighLogic.LoadedScene == GameScenes.EDITOR) return EditorAir();
            Vessel v = FlightApi.Active();
            if (a.Bool("reset", false)) { worst = new Worst(); watched = v.id; }
            var o = new JObj().Add("vessel", v.GetDisplayName()).Add("situation", v.situation.ToString());
            CelestialBody body = v.mainBody;
            bool air = body != null && body.atmosphere && v.altitude < body.atmosphereDepth && v.atmDensity > 1e-7;
            o.Add("in_air", air);
            var notes = new List<string>();
            if (!air)
            {
                o.Add("measured", false);
                notes.Add(body != null && body.atmosphere ? "Above the air of " + body.displayName.LocalizeRemoveGender() + " (it ends at " + R(body.atmosphereDepth / 1000.0, 0) + " km): nothing aerodynamic is acting."
                    : "No air here: nothing aerodynamic is acting.");
            }
            else if (v.packed || !v.loaded)
            {
                o.Add("measured", false);
                notes.Add("The craft is on rails (time warp, or just loaded): the air's forces are not being worked out. Ask again at normal speed.");
            }
            else
            {
                Vector3 velocity = v.srf_velocity;
                double speed = v.srfSpeed, q = v.dynamicPressurekPa;
                o.Add("measured", true);
                o.Add("speed_ms", R(speed)).Add("mach", R(v.mach, 2)).Add("air_pressure_on_craft_kPa", R(q, 2)).Add("air_density_kg_m3", R(v.atmDensity, 4)).Add("altitude_m", R(v.altitude, 0));
                if (speed < 5.0 || v.ReferenceTransform == null) notes.Add("Hardly moving through the air: there is next to no force from it.");
                else
                {
                    Vector3 way = velocity.normalized;
                    Transform frame = v.ReferenceTransform;
                    Vector3 nose = frame.up;
                    // (as the craft's own controls have it: along the nose, to the right, and towards the belly)
                    Vector3 local = frame.InverseTransformDirection(way);
                    double off = Vector3.Angle(nose, way);
                    o.Add("nose_off_the_way_it_flies_deg", R(off));
                    o.Add("angle_of_attack_deg", R(Math.Atan2(local.z, local.y) * 180.0 / Math.PI)).Add("sideslip_deg", R(Math.Atan2(local.x, local.y) * 180.0 / Math.PI));
                    Sum(v, out Vector3 force, out Vector3 turning);
                    double drag = -Vector3.Dot(force, way), lift = Vector3.ProjectOnPlane(force, way).magnitude;
                    o.Add("drag_kN", R(drag)).Add("lift_kN", R(lift));
                    if (drag > 0.5) o.Add("lift_to_drag", R(lift / drag, 2));
                    double weight = v.totalMass * FlightGlobals.getGeeForceAtPosition(v.CoM).magnitude;
                    if (weight > 0) o.Add("drag_as_part_of_weight", R(drag / weight, 2));
                    Steering(v, out double wheels, out double engines, out int surfaces, out int thrusters);
                    var steer = new JObj().Add("reaction_wheels_kNm", R(wheels)).Add("swivelling_engines_kNm", R(engines)).Add("control_surfaces", surfaces).Add("rcs_thrusters_on", thrusters);
                    if (off >= 1.5)
                    {
                        // (the turning that would bring the nose back to the way of flight is about the line square to both)
                        float back = Vector3.Dot(turning, Vector3.Cross(nose, way).normalized);
                        o.Add("air_is", back > 0.05f ? "steadying" : back < -0.05f ? "upsetting" : "doing neither");
                        o.Add("air_turns_the_nose_kNm", R(Math.Abs(back)));
                        o.Add("air_turns_the_nose", back >= 0 ? "back towards the way it is flying" : "further from the way it is flying");
                        if (back < -0.05f)
                        {
                            double have = wheels + engines;
                            if (surfaces == 0 && -back > have * 0.8)
                                notes.Add("The air is turning the craft away from the way it flies with " + R(-back) + " kN m, and its reaction wheels and swivelling engines can give " + R(have) + ": it is at the edge of tumbling. Bring the nose back to prograde now, throttle down to lessen the air's pressure, and next time keep closer to prograde while the pressure is high.");
                            else notes.Add("The air is pushing the nose further from the way it flies (the craft is unstable here): the steering is holding it. Keep the angle small while the air presses this hard.");
                        }
                    }
                    else o.Add("air_is", "not turning it: the nose is on the way it flies");
                    o.Add("steering", steer);
                    if (q > 20.0 && off > 8.0) notes.Add("The nose is " + R(off) + " degrees off the way it flies at " + R(q) + " kPa: that bends a tall craft and can break it (see structure). Turn more gently, or later, when the air is thinner.");
                    try
                    {
                        Ray lifts = EditorMarker_CoL.FindCoL(v.rootPart, velocity, v.altitude, v.staticPressurekPa, v.atmDensity, out bool none);
                        if (!none) o.Add("lift_of_wings_and_fins_behind_mass_centre_m", R(Vector3.Dot((Vector3)v.CoM - lifts.origin, way), 2));
                    }
                    catch (Exception) { }
                }
            }
            if (worst.Pressure > 0 && v.id == watched)
            {
                var w = new JObj().Add("greatest_air_pressure_kPa", R(worst.Pressure, 1)).Add("at_mission_time_s", R(worst.PressureAt, 0)).Add("at_speed_ms", R(worst.PressureSpeed, 0)).Add("at_altitude_m", R(worst.PressureAltitude, 0));
                if (worst.Off > 0) w.Add("nose_furthest_off_deg", R(worst.Off)).Add("that_at_mission_time_s", R(worst.OffAt, 0)).Add("with_air_pressure_kPa", R(worst.OffPressure, 1));
                if (worst.Upset > 0) w.Add("strongest_upsetting_turn_kNm", R(worst.Upset)).Add("upset_at_mission_time_s", R(worst.UpsetAt, 0));
                o.Add("worst_so_far", w);
            }
            if (notes.Count > 0) o.Add("notes", notes);
            return o;
        }

        /// <summary>In the editor: where the lift of wings or fins acts against the centre of mass, by the game's own marker.</summary>
        static object EditorAir()
        {
            ShipConstruct ship = EditorLogic.fetch != null ? EditorLogic.fetch.ship : null;
            if (ship == null || ship.parts.Count == 0) throw new ApiException("The editor is empty.");
            var o = new JObj().Add("editor_craft", ship.shipName).Add("measured", false);
            var notes = new List<string>();
            Part root = EditorLogic.RootPart;
            CelestialBody home = Planetarium.fetch != null ? Planetarium.fetch.Home : null;
            int wings = 0, surfaces = 0;
            foreach (Part p in ship.parts)
                foreach (PartModule m in p.Modules)
                {
                    if (m is ModuleControlSurface) surfaces++;
                    else if (m is ModuleLiftingSurface) wings++;
                }
            o.Add("wings_and_fins", wings + surfaces).Add("of_them_control_surfaces", surfaces);
            if (wings + surfaces == 0 || root == null || home == null)
            {
                notes.Add("No wings or fins: the air does not steady this craft. A rocket without fins is steered by engines that swivel and by reaction wheels alone, which is usual and enough if it stays close to prograde while the air is thick.");
            }
            else
            {
                try
                {
                    if (!EditorLift(ship, out double behind, out double behindEmpty)) notes.Add("Its wings or fins give no lift flying nose first.");
                    else
                    {
                        o.Add("lift_behind_mass_centre_m", R(behind, 2)).Add("with_tanks_empty_m", R(behindEmpty, 2));
                        o.Add("stable", behind > 0 && behindEmpty > 0);
                        string said = Unsteady(behind, behindEmpty);
                        if (said != null) notes.Add(said);
                    }
                }
                catch (Exception ex) { notes.Add("The centre of lift could not be worked out (" + ex.Message + ")."); }
            }
            o.Add("for_an_aeroplane_see", "craft_info: aerodynamics (lift rating, take-off speed, wheels)");
            if (notes.Count > 0) o.Add("notes", notes);
            return o;
        }

        /// <summary>
        /// For the craft in the editor, flying nose first: how far behind its centre of mass the lift of its wings and fins
        /// acts (metres; negative is ahead), with its tanks full and with them empty. By the game's own centre-of-lift
        /// marker, as it takes a craft to fly: a degree off, at 70 m/s, a hundred metres up. False if nothing gives lift.
        /// </summary>
        internal static bool EditorLift(ShipConstruct ship, out double behind, out double behindEmpty)
        {
            behind = behindEmpty = 0;
            Part root = EditorLogic.RootPart;
            CelestialBody home = Planetarium.fetch != null ? Planetarium.fetch.Home : null;
            if (root == null || home == null) return false;
            Vector3 way = EditorLogic.VesselRotation * Vector3.up;
            Vector3 mass = Vector3.zero, dry = Vector3.zero; float all = 0f, empty = 0f;
            foreach (Part p in ship.parts)
            {
                Vector3 at = p.transform.position + p.transform.rotation * p.CoMOffset;
                float full = p.mass + p.GetResourceMass();
                mass += at * full; all += full; dry += at * p.mass; empty += p.mass;
            }
            if (all <= 0 || empty <= 0) return false;
            mass /= all; dry /= empty;
            double pressure = FlightGlobals.getStaticPressure(100.0, home), density = FlightGlobals.getAtmDensity(pressure, FlightGlobals.getExternalTemperature(100.0, home), home);
            Vector3 reference = EditorLogic.VesselRotation * (Quaternion.AngleAxis(1f, root.transform.right) * Vector3.up) * 70f;
            Ray lifts = EditorMarker_CoL.FindCoL(root, reference, 100.0, pressure, density, out bool none);
            if (none) return false;
            behind = Vector3.Dot(mass - lifts.origin, way); behindEmpty = Vector3.Dot(dry - lifts.origin, way);
            return true;
        }

        /// <summary>What to say of a craft whose lift is ahead of its mass, or gets there as the tanks empty; nothing if it is neither.</summary>
        internal static string Unsteady(double behind, double behindEmpty)
        {
            if (behind <= 0) return "The lift of its wings or fins acts " + R(-behind, 2) + " m AHEAD of the centre of mass: the air will turn this craft round. Move them back (on a rocket, to the bottom), or the weight forward.";
            if (behindEmpty <= 0) return "Stable with full tanks, but with them empty the centre of mass is " + R(-behindEmpty, 2) + " m behind the lift: it will become unstable as it burns its fuel. Move the wings or fins back.";
            return null;
        }

        // ---------------------------------------------------------------- the radio

        sealed class Aerial
        {
            public string Part, Kind;
            public double Power, Exponent;
            public bool Combines, Works;
        }

        /// <summary>What several aerials on one craft come to, by the game's rule: the strongest, times (the sum over the strongest) to a power that is the aerials' own, weighted by their strength. Only those made to be combined add up.</summary>
        static double Together(List<Aerial> aerials, bool relayOnly)
        {
            double best = 0, strongest = 0, sum = 0, weighted = 0;
            foreach (Aerial a in aerials)
            {
                if (!a.Works || a.Kind == "internal" && relayOnly || relayOnly && a.Kind != "relay") continue;
                best = Math.Max(best, a.Power);
                if (!a.Combines) continue;
                strongest = Math.Max(strongest, a.Power); sum += a.Power; weighted += a.Power * a.Exponent;
            }
            double combined = strongest > 0 ? strongest * Math.Pow(sum / strongest, weighted / sum) : 0;
            return Math.Max(best, combined);
        }

        static string Far(double metres)
        {
            if (double.IsNaN(metres) || double.IsInfinity(metres)) return "nothing";
            if (metres >= 1e9) return R(metres / 1e9, 2) + " Gm";
            if (metres >= 1e6) return R(metres / 1e6, 1) + " Mm";
            if (metres >= 1e3) return R(metres / 1e3, 1) + " km";
            return R(metres, 0) + " m";
        }

        /// <summary>How strong a signal is at this distance when the two ends reach each other out to that one: the game's curve (none at the end of the range, nearly full over the first third).</summary>
        static double Signal(double distance, double reach)
        {
            if (reach <= 0 || distance >= reach) return 0;
            double x = 1.0 - distance / reach;
            return x * x * (3.0 - 2.0 * x);
        }

        /// <summary>What an end of a link is called (the game keeps some of these as tags to be looked up in the player's language).</summary>
        static string Named(CommNode node)
        {
            if (node == null) return "?";
            string name = node.displayName;
            if (string.IsNullOrEmpty(name)) name = node.name;
            try { if (!string.IsNullOrEmpty(name) && name.StartsWith("#")) name = KSP.Localization.Localizer.Format(name); } catch (Exception) { }
            return name ?? "?";
        }

        static object Comms()
        {
            bool editor = HighLogic.LoadedScene == GameScenes.EDITOR;
            var o = new JObj();
            IEnumerable<Part> parts;
            Vessel v = null;
            if (editor)
            {
                ShipConstruct ship = EditorLogic.fetch != null ? EditorLogic.fetch.ship : null;
                if (ship == null || ship.parts.Count == 0) throw new ApiException("The editor is empty.");
                o.Add("editor_craft", ship.shipName);
                parts = ship.parts;
            }
            else
            {
                v = FlightApi.Active();
                o.Add("vessel", v.GetDisplayName());
                parts = v.parts;
            }
            var notes = new List<string>();
            bool needed = false; double rangeBy = 1, stationBy = 1; bool stations = true;
            try
            {
                needed = CommNetScenario.CommNetEnabled;
                CommNetParams rules = HighLogic.CurrentGame.Parameters.CustomParams<CommNetParams>();
                rangeBy = rules.rangeModifier; stationBy = rules.DSNModifier; stations = rules.enableGroundStations;
                o.Add("rules", new JObj().Add("signal_needed", needed).Add("needed_to_steer_a_probe", needed && rules.requireSignalForControl).Add("range_multiplier", R(rangeBy, 2)).Add("ground_station_multiplier", R(stationBy, 2)).Add("extra_ground_stations", stations));
            }
            catch (Exception) { }
            if (!needed) notes.Add("This game does not need a signal (CommNet is switched off): every craft can be steered and can send science from anywhere. The figures below are what they would be with it on.");

            // ---- the aerials
            var aerials = new List<Aerial>();
            var list = new List<object>();
            foreach (Part p in parts)
            {
                if (p == null) continue;
                foreach (PartModule m in p.Modules)
                {
                    if (!(m is ModuleDataTransmitter t)) continue;
                    var one = new Aerial
                    {
                        Part = p.partInfo != null ? p.partInfo.title : p.name,
                        Kind = t.antennaType == AntennaType.RELAY ? "relay" : t.antennaType == AntennaType.INTERNAL ? "internal" : "direct",
                        Power = t.antennaPower * rangeBy, Exponent = t.antennaCombinableExponent, Combines = t.antennaCombinable, Works = true,
                    };
                    if (!editor) { try { one.Works = t.CanComm(); } catch (Exception) { } }
                    aerials.Add(one);
                    var row = new JObj().Add("part", one.Part).Add("kind", one.Kind).Add("power", t.antennaPower).Add("adds_up_with_others", one.Combines);
                    if (!editor && !one.Works) row.Add("working", false);
                    if (t.antennaType != AntennaType.INTERNAL) row.Add("sends_science_mits_per_s", R(t.DataRate, 2));
                    list.Add(row);
                }
            }
            o.Add("aerials", list);
            double power = Together(aerials, false), relay = Together(aerials, true);
            if (aerials.Count == 0) notes.Add("No aerial at all, not even a pod's own: with a signal needed, this craft cannot be steered without a pilot aboard and cannot send science.");
            foreach (Aerial a in aerials) if (!a.Works) { notes.Add("An aerial is not working (folded away, broken or without power): extend it."); break; }
            o.Add("power_together", R(power, 0));
            if (relay > 0) o.Add("power_as_a_relay", R(relay, 0));

            // ---- how far that reaches
            double level = 1.0;
            try { level = ScenarioUpgradeableFacilities.GetFacilityLevel(SpaceCenterFacility.TrackingStation); } catch (Exception) { }
            double station = GameVariables.Instance.GetDSNRange((float)level) * stationBy;
            IRangeModel model = CommNetScenario.RangeModel;
            double home = model.GetMaximumRange(power, station);
            var reach = new JObj().Add("to_the_tracking_station", Far(home)).Add("to_the_tracking_station_m", R(home, 0)).Add("tracking_station_power", station);
            if (level < 0.99) reach.Add("with_the_tracking_station_fully_upgraded", Far(model.GetMaximumRange(power, GameVariables.Instance.GetDSNRange(1f) * stationBy)));
            reach.Add("to_a_craft_like_itself", Far(model.GetMaximumRange(power, Math.Max(relay, power))));
            o.Add("reach", reach);

            // ---- which worlds that covers, from the home world
            CelestialBody kerbin = Planetarium.fetch != null ? Planetarium.fetch.Home : null;
            if (kerbin != null && power > 0)
            {
                var worlds = new List<object>();
                foreach (CelestialBody b in FlightGlobals.Bodies)
                {
                    if (b == null || b == kerbin || b.orbit == null) continue;
                    double near, far;
                    if (b.referenceBody == kerbin) { near = b.orbit.PeR - kerbin.Radius; far = b.orbit.ApR + kerbin.Radius; }
                    else if (b.referenceBody == kerbin.referenceBody && kerbin.orbit != null)
                    {
                        // (two worlds round the same sun: nearest when they are on the same side and the outer one is at its closest
                        // to the sun, farthest across the sun from each other)
                        Orbit mine = kerbin.orbit, its = b.orbit;
                        near = its.semiMajorAxis > mine.semiMajorAxis ? its.PeR - mine.ApR : mine.PeR - its.ApR;
                        far = its.ApR + mine.ApR;
                        if (near < 0) near = 0;
                    }
                    else continue;                                      // (the moons of other worlds are as far as their world, near enough)
                    double nearly = Signal(near, home), farthest = Signal(far, home);
                    worlds.Add(new JObj().Add("world", b.displayName.LocalizeRemoveGender()).Add("nearest", Far(near)).Add("signal_at_nearest", R(nearly, 2)).Add("farthest", Far(far)).Add("signal_at_farthest", R(farthest, 2))
                        .Add("heard", farthest > 0 ? "always" : nearly > 0 ? "only when near" : "never"));
                }
                o.Add("heard_from", worlds);
                o.Add("heard_from_note", "Straight to the tracking station, from the world itself or close by it (a moon of it counts as its world). A relay nearer by, or more aerials that add up, changes it. The game also loses the signal when a world is in the way.");
            }

            // ---- in flight: the signal now
            if (v != null)
            {
                CommNetVessel link = v.connection;
                if (link == null) notes.Add("The game has no signal record for this craft yet.");
                else
                {
                    var now = new JObj().Add("connected", link.IsConnected).Add("connected_home", link.IsConnectedHome).Add("control", link.ControlState.ToString());
                    if (link.IsConnected && link.ControlPath != null)
                    {
                        now.Add("signal", R(link.SignalStrength, 2));
                        var hops = new List<object>();
                        foreach (CommLink hop in link.ControlPath)
                        {
                            if (hop == null) continue;
                            hops.Add(new JObj().Add("from", Named(hop.start)).Add("to", Named(hop.end)).Add("distance", Far(hop.cost)).Add("signal", R(hop.signalStrength, 2)));
                        }
                        now.Add("path_home", hops);
                    }
                    if (kerbin != null)
                    {
                        double away = (v.GetWorldPos3D() - kerbin.position).magnitude - kerbin.Radius;
                        now.Add("distance_from_home", Far(away));
                        if (home > 0) now.Add("of_its_reach_to_the_tracking_station", R(away / home, 2));
                    }
                    o.Add("now", now);
                    if (needed && !link.IsConnected) notes.Add(link.ControlState == VesselControlState.KerbalNone || link.ControlState == VesselControlState.Kerbal ? "No signal: the crew can still fly it, but science cannot be sent." : "No signal: a probe cannot be steered until it is back in reach or a relay is in sight.");
                }
            }
            if (notes.Count > 0) o.Add("notes", notes);
            return o;
        }
    }
}
