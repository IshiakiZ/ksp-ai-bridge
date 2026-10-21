using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace AIBridge.Api
{
    /// <summary>Game-level commands: status, saves, scene changes, screenshots, on-screen messages.</summary>
    public static class GameApi
    {
        static BridgeHost host;
        static readonly HashSet<string> reservedSaves = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "scenarios", "training", "missions", "test_missions" };

        static readonly List<Renderer> menuWordsHidden = new List<Renderer>();

        public static void Register(BridgeHost h)
        {
            host = h;
#if DEV
            // Testing aids, present only in the hot-reload development build.
            Rpc.Sync("dev_set_orbit", "Development build only: put the vessel being flown into a circular orbit, like the game's debug menu. params: {body, altitude_km, inclination}", a =>
            {
                Vessel v = FlightApi.Active();
                if (HighLogic.CurrentGame == null || HighLogic.CurrentGame.Mode != Game.Modes.SANDBOX) throw new ApiException("Only in sandbox saves.");
                string name = a.Str("body") ?? v.mainBody.bodyName;
                int index = FlightGlobals.Bodies.IndexOf(Places.Body(name));
                if (index < 0) throw new ApiException("No body named '" + name + "'.");
                CelestialBody body = FlightGlobals.Bodies[index];
                double altitude = a.Num("altitude_km", (body.atmosphere ? body.atmosphereDepth + 10000 : 15000) / 1000.0) * 1000.0;
                Pilot.Autopilot.I.Release("orbit set for testing", true);
                FlightGlobals.fetch.SetShipOrbit(index, 0.0, body.Radius + altitude, a.Num("inclination", 0), 0, 0, 0, 0);
                return new JObj().Add("body", Places.Name(body)).Add("altitude_m", altitude);
            });
            Rpc.Sync("dev_set_position", "Development build only: put the vessel being flown at a place on (or over) any body, like the game's debug menu. params: {body, lat, lon, altitude_m (above the ground there, or above sea level with asl: true), asl: bool, ease: bool (default true: let down gently onto the surface), heading: the compass bearing it is set down facing (default 0, north). Kerbals standing near by are found a metre or two off a moment later (the game pins a standing kerbal to the world, and the pins do not move with the world when it is shifted): to set kerbals out side by side use eva put, sun_deg: instead of lon, the place on that latitude where the sun stands so high, land: bool (on a body with seas, the nearest dry spot to that)}", a =>
            {
                Vessel v = FlightApi.Active();
                if (HighLogic.CurrentGame == null || HighLogic.CurrentGame.Mode != Game.Modes.SANDBOX) throw new ApiException("Only in sandbox saves.");
                string name = a.Str("body") ?? v.mainBody.bodyName;
                int index = FlightGlobals.Bodies.IndexOf(Places.Body(name));
                if (index < 0) throw new ApiException("No body named '" + name + "'.");
                CelestialBody body = FlightGlobals.Bodies[index];
                Pilot.Autopilot.I.Release("position set for testing", true);
                double altitude = a.Num("altitude_m", 20), lat = a.Num("lat", 0), lon = a.Num("lon", 0);
                bool asl = a.Bool("asl", false);
                Vector3d toSun = (Planetarium.fetch.Sun.position - body.position).normalized;
                if (a.Has("sun_deg"))
                {
                    // (the place on that latitude, in the morning, where the sun stands nearest to so high)
                    double wanted = a.Num("sun_deg"), best = double.MaxValue;
                    for (int n = 0; n < 720; n++)
                    {
                        double at = -180 + n * 0.5, high = Math.Asin(Vector3d.Dot(body.GetSurfaceNVector(lat, at), toSun)) * 180 / Math.PI;
                        double later = Math.Asin(Vector3d.Dot(body.GetSurfaceNVector(lat, at - 1), toSun)) * 180 / Math.PI;
                        if (Math.Abs(high - wanted) < best && (a.Bool("evening", false) ? later < high : later > high) == (body.angularVelocity.y >= 0)) { best = Math.Abs(high - wanted); lon = at; }
                    }
                }
                if (a.Bool("land", false) && body.ocean && body.pqsController != null)
                {
                    // (on a body with seas: the dry spot, between forty degrees north and south, where the sun stands nearest to as high as at the place found so far)
                    double wanted = Math.Asin(Vector3d.Dot(body.GetSurfaceNVector(lat, lon), toSun)) * 180 / Math.PI, best = double.MaxValue;
                    for (double la = -40; la <= 40; la += 2)
                        for (double lo = -180; lo < 180; lo += 2)
                        {
                            double high = Math.Asin(Vector3d.Dot(body.GetSurfaceNVector(la, lo), toSun)) * 180 / Math.PI;
                            if (Math.Abs(high - wanted) >= best || body.TerrainAltitude(la, lo, true) < 40) continue;
                            best = Math.Abs(high - wanted); lat = la; lon = lo;
                        }
                }
                FlightGlobals.fetch.SetVesselPosition(index, lat, lon, altitude, 0.0, a.Num("heading", 0.0), asl, a.Bool("ease", true));
                return new JObj().Add("body", Places.Name(body)).Add("lat", lat).Add("lon", lon).Add("altitude_m", altitude).Add("above", asl ? "sea level" : "the ground")
                    .Add("sun_deg", Math.Round(Math.Asin(Vector3d.Dot(body.GetSurfaceNVector(lat, lon), toSun)) * 180 / Math.PI, 1));
            });
            Rpc.Sync("dev_entry_model", "Development build only: the entry drag model against the parts as they are now. params: {drop}", a =>
            {
                Vessel v = FlightApi.Active();
                bool drop = a.Bool("drop", true);
                Pilot.EntryModel model = Pilot.EntryModel.For(v, drop);
                return new JObj().Add("model_area_mach3", model.Area(3)).Add("model_area_mach1", model.Area(1)).Add("model_area_mach6", model.Area(6)).Add("mass", model.Mass).Add("parts", model.Parts)
                    .Add("now", Pilot.EntryModel.Breakdown(v, Pilot.EntryModel.KeptParts(v, drop), 3f));
            });
            Rpc.Sync("dev_explode", "Development build only: set off explosion effects near the vessel; nothing is damaged. params: {power: 0..1, east, north, up: metres from the vessel, surface: bool (up is then above the ground or roof there), count, spread_m, what: words describing what blew up, e.g. \"fuel=1800 oxidizer=2200 cause=crash speed=60 east=40\"}", DevExplode);
            Rpc.Sync("dev_fx_stats", "Development build only: what the explosions mod is doing now (particles, sites, cost) and how it read the last explosion.", a =>
            {
                Type air = System.Reflection.Assembly.GetExecutingAssembly().GetType("VolumetricExplosions.Air");
                if (air == null) throw new ApiException("This build does not contain the explosions mod. Build it with FXDEV=1 ./build.sh dev.");
                return new JObj().Add("stats", air.GetMethod("Stats").Invoke(null, null) as string).Add("last", air.GetField("LastPlan").GetValue(null) as string)
                    .Add("fps", Math.Round(1f / Mathf.Max(1e-4f, Time.smoothDeltaTime), 1)).Add("frame_ms", Math.Round(Time.unscaledDeltaTime * 1000f, 1));
            });
            Rpc.Sync("dev_fx_inside", "Development build only: how many of the explosions mod's smoke particles (one in three is looked at) lie inside something solid just now.", a =>
            {
                Type air = System.Reflection.Assembly.GetExecutingAssembly().GetType("VolumetricExplosions.Air");
                if (air == null) throw new ApiException("This build does not contain the explosions mod. Build it with FXDEV=1 ./build.sh dev.");
                return new JObj().Add("inside", air.GetMethod("Inside").Invoke(null, null) as string);
            });
            Rpc.Sync("dev_fx_call", "Development build only: call one of the explosions mod's own reporting methods (a static method of its Air class that takes nothing and returns text). params: {method}", a =>
            {
                Type air = System.Reflection.Assembly.GetExecutingAssembly().GetType("VolumetricExplosions.Air");
                System.Reflection.MethodInfo method = air?.GetMethod(a.Require("method"), Type.EmptyTypes);
                if (method == null || !method.IsStatic || method.ReturnType != typeof(string)) throw new ApiException("No such method.");
                return new JObj().Add("result", method.Invoke(null, null) as string);
            });
            Rpc.Sync("dev_fx_set", "Development build only: change one of the explosions mod's settings while it runs. params: {name: a field of its Settings (Quality, Smoke, Size, Wind, MaxParticles, Threads, Push, Light, ...), value}", a =>
            {
                Type settings = System.Reflection.Assembly.GetExecutingAssembly().GetType("VolumetricExplosions.Settings");
                System.Reflection.FieldInfo field = settings?.GetField(a.Require("name"));
                if (field == null) throw new ApiException("No such setting.");
                if (field.FieldType == typeof(bool)) field.SetValue(null, a.Bool("value"));
                else if (field.FieldType == typeof(int)) field.SetValue(null, a.Int("value"));
                else field.SetValue(null, (float)a.Num("value"));
                return new JObj().Add(field.Name, field.GetValue(null).ToString());
            });
            Rpc.Sync("dev_fx_reload", "Development build only (built with FXDEV=1): replace the running Volumetric Explosions with the copy in this build.", DevFxReload);
            Rpc.Sync("dev_mods_reload", "Development build only (built with MODSDEV=1): replace the running copies of Keystone and the mods that stand on it with the ones in this build.", DevModsReload);
            Rpc.Sync("dev_call", "Development build only: call a static method of this build that returns text, and takes nothing or (with arg) one piece of text. params: {type: full name, e.g. SmoothPortraits.Portraits, method, arg}", a =>
            {
                Type type = System.Reflection.Assembly.GetExecutingAssembly().GetType(a.Str("type") ?? "");
                if (type == null) throw new ApiException("This build has no type '" + a.Str("type") + "'.");
                bool given = a.Has("arg");
                System.Reflection.MethodInfo method = type.GetMethod(a.Str("method") ?? "Stats", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static, null, given ? new[] { typeof(string) } : Type.EmptyTypes, null);
                if (method == null) throw new ApiException(type.FullName + " has no such method" + (given ? " that takes a piece of text." : "."));
                return new JObj().Add("result", method.Invoke(null, given ? new object[] { a.Str("arg") } : null) as string);
            });
            Rpc.Sync("dev_window", "Development build only: the window of whichever Keystone is running (the installed one too, which has no development commands of its own), opened on a mod's page. params: {page: a mod's name (left out: the list), x, y: where its top left goes, in the interface's units, open: false closes it, scroll: how far down its page (or the list) is scrolled, in the same units, scale: the size of the game's whole interface, for this run only (the answer says what it was: give that back afterwards)}", a =>
            {
                float scaleWas = GameSettings.UI_SCALE;
                if (a.Has("scale"))
                {
                    float now = (float)Pilot.OrbitMath.Clamp(a.Num("scale", 1), 0.5, 3);
                    GameSettings.UI_SCALE = now;
                    try { KSP.UI.UIMasterController.Instance.SetScale(now); KSP.UI.UIMasterController.Instance.SetAppScale(GameSettings.UI_SCALE_APPS * now); } catch (Exception) { }
                }
                MonoBehaviour host = null;
                foreach (MonoBehaviour b in UnityEngine.Object.FindObjectsOfType<MonoBehaviour>())
                    if (b != null && b.GetType().FullName == "Keystone.Host") { host = b; break; }
                if (host == null) throw new ApiException("Keystone is not running in this scene.");
                Type kind = host.GetType();
                const System.Reflection.BindingFlags any = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic;
                kind.GetProperty("Open", any).SetValue(host, a.Bool("open", true), null);
                string page = a.Str("page");
                object found = null;
                if (!string.IsNullOrEmpty(page))
                {
                    var all = kind.Assembly.GetType("Keystone.Listing").GetProperty("All", any).GetValue(null, null) as IEnumerable;
                    foreach (object one in all)
                        if (string.Equals(one.GetType().GetField("Name").GetValue(one) as string, page, StringComparison.OrdinalIgnoreCase)) { found = one; break; }
                    if (found == null) throw new ApiException("No mod named '" + page + "' is listed.");
                }
                kind.GetMethod("Go", any).Invoke(host, new[] { found });
                if (a.Has("x") || a.Has("y"))
                {
                    System.Reflection.FieldInfo field = kind.GetField("window", any);
                    Rect at = (Rect)field.GetValue(host);
                    at.x = (float)a.Num("x", at.x); at.y = (float)a.Num("y", at.y);
                    field.SetValue(host, at);
                }
                bool scrolled = false;
                if (a.Has("scroll"))
                {
                    System.Reflection.FieldInfo field = kind.GetField(found != null ? "pageScroll" : "listScroll", any);
                    if (field != null) { field.SetValue(host, new Vector2(0f, (float)a.Num("scroll", 0))); scrolled = true; }
                }
                return new JObj().Add("keystone", kind.Assembly.GetName().Name + " " + kind.Assembly.GetName().Version).Add("page", page ?? "the list").Add("scale_was", Math.Round(scaleWas, 3)).Add("scrolled", scrolled);
            });
            Rpc.Sync("dev_menu_words", "Development build only, at the main menu: the menu's own words (its entries, the game's name, the version in the corner) put away for a picture, or shown again. They are things in the scene, not interface, and hiding the interface leaves them. params: {show: bool (default false)}", a =>
            {
                if (HighLogic.LoadedScene != GameScenes.MAINMENU) throw new ApiException("Only at the main menu.");
                bool show = a.Bool("show", false);
                var names = new List<object>();
                if (show)
                {
                    foreach (Renderer r in menuWordsHidden) if (r != null) { r.enabled = true; names.Add(r.gameObject.name); }
                    menuWordsHidden.Clear();
                    return new JObj().Add("shown", names);
                }
                foreach (Renderer r in UnityEngine.Object.FindObjectsOfType<Renderer>())
                {
                    if (r == null || !r.enabled) continue;
                    bool words = r.GetComponent<TMPro.TMP_Text>() != null || r.GetComponent<TextMesh>() != null || r.GetComponentInParent<TextButton3D>() != null;
                    string name = r.gameObject.name.ToLowerInvariant();
                    if (!words && !name.Contains("logo")) continue;
                    r.enabled = false;
                    menuWordsHidden.Add(r);
                    names.Add(r.gameObject.name);
                }
                return new JObj().Add("hidden", names);
            });
            Rpc.Sync("dev_keep", "Development build only: keep a piece of text in a Keystone mod's settings file, as the mod itself would (its own lists). params: {mod, key, value: text, or nothing to take it out}", a =>
            {
                Type kit = System.Reflection.Assembly.GetExecutingAssembly().GetType("Keystone.Kit");
                if (kit == null) throw new ApiException("This build does not contain Keystone. Build it with MODSDEV=1 ./build.sh dev.");
                object mod = kit.GetMethod("Find").Invoke(null, new object[] { a.Str("mod") });
                if (mod == null) throw new ApiException("No mod named '" + a.Str("mod") + "' has registered.");
                mod.GetType().GetMethod("Keep").Invoke(mod, new object[] { a.Str("key"), a.Str("value") });
                return new JObj().Add("mod", a.Str("mod")).Add("key", a.Str("key")).Add("value", a.Str("value"));
            });
            Rpc.Sync("dev_set", "Development build only: set a setting of one of the mods that stand on Keystone. params: {mod: its name, key, value: text as the settings file has it}", a =>
            {
                Type kit = System.Reflection.Assembly.GetExecutingAssembly().GetType("Keystone.Kit");
                if (kit == null) throw new ApiException("This build does not contain Keystone. Build it with MODSDEV=1 ./build.sh dev.");
                object mod = kit.GetMethod("Find").Invoke(null, new object[] { a.Str("mod") });
                if (mod == null) throw new ApiException("No mod named '" + a.Str("mod") + "' has registered.");
                foreach (object setting in (System.Collections.IEnumerable)mod.GetType().GetProperty("Settings").GetValue(mod, null))
                {
                    Type kind = setting.GetType();
                    if ((kind.GetField("Key").GetValue(setting) as string) != a.Str("key")) continue;
                    System.Reflection.PropertyInfo value = kind.GetProperty("Value");
                    string text = a.Str("value") ?? "";
                    object parsed = value.PropertyType == typeof(bool) ? (object)bool.Parse(text) : value.PropertyType == typeof(float) ? (object)float.Parse(text, System.Globalization.CultureInfo.InvariantCulture)
                                  : value.PropertyType == typeof(int) ? (object)DevChoice(setting, text) : value.PropertyType == typeof(Color) ? (object)DevColour(text) : null;
                    if (parsed == null) throw new ApiException("That kind of setting cannot be set this way.");
                    value.SetValue(setting, parsed, null);
                    return new JObj().Add("mod", a.Str("mod")).Add("key", a.Str("key")).Add("value", value.GetValue(setting, null).ToString());
                }
                throw new ApiException("That mod has no setting '" + a.Str("key") + "'.");
            });
            Rpc.Sync("dev_fx_probe", "Development build only: what the particle systems of the explosions now alive are doing.", a =>
            {
                Vessel v = FlightGlobals.ActiveVessel;
                Vector3d up = v != null ? FlightGlobals.getUpAxis(v.mainBody, v.CoMD) : Vector3d.up;
                var list = new List<object>();
                var buffer = new ParticleSystem.Particle[64];
                foreach (GameObject root in UnityEngine.Object.FindObjectsOfType<GameObject>())
                {
                    bool ours = root.name.StartsWith("Volumetric Explosion", StringComparison.Ordinal), theirs = root.name.StartsWith("Explosion ", StringComparison.Ordinal) && root.name.EndsWith("(Clone)", StringComparison.Ordinal);
                    if (!(ours || theirs) || root.transform.parent != null) continue;
                    var ro = new JObj().Add("name", root.name);
                    AudioSource sound = root.GetComponent<AudioSource>();
                    AudioListener ears = null;
                    foreach (AudioListener l in UnityEngine.Object.FindObjectsOfType<AudioListener>()) if (l.isActiveAndEnabled) { ears = l; break; }
                    if (sound != null)
                        ro.Add("audio", new JObj().Add("playing", sound.isPlaying).Add("volume", sound.volume).Add("pitch", Math.Round(sound.pitch, 2)).Add("rolloff", sound.rolloffMode.ToString())
                            .Add("min", sound.minDistance).Add("max", sound.maxDistance).Add("blend", sound.spatialBlend).Add("virtual", sound.isVirtual)
                            .Add("to_listener_m", ears != null ? Math.Round(Vector3.Distance(ears.transform.position, root.transform.position), 1) : -1)
                            .Add("sources_on_object", root.GetComponents<AudioSource>().Length));
                    if (v != null) ro.Add("up_of_vessel_m", Math.Round(Vector3d.Dot((Vector3d)root.transform.position - v.CoMD, up), 2)).Add("root_up_dot_up", Math.Round(Vector3.Dot(root.transform.up, (Vector3)up), 3));
                    var systems = new List<object>();
                    foreach (ParticleSystem ps in root.GetComponentsInChildren<ParticleSystem>())
                    {
                        if (ps.particleCount == 0) continue;
                        var r = ps.GetComponent<ParticleSystemRenderer>();
                        int got = ps.GetParticles(buffer);
                        float lowest = float.MaxValue, highest = float.MinValue, biggest = 0f, alpha = 0f;
                        for (int n = 0; n < got; n++)
                        {
                            Vector3 world = ps.transform.TransformPoint(buffer[n].position);
                            float h = v != null ? (float)Vector3d.Dot((Vector3d)world - v.CoMD, up) : world.y;
                            lowest = Mathf.Min(lowest, h); highest = Mathf.Max(highest, h);
                            biggest = Mathf.Max(biggest, buffer[n].GetCurrentSize(ps));
                            alpha = Mathf.Max(alpha, buffer[n].GetCurrentColor(ps).a / 255f);
                        }
                        systems.Add(new JObj().Add("system", ps.gameObject.name).Add("count", ps.particleCount).Add("visible", r.isVisible).Add("playing", ps.isPlaying)
                            .Add("height_range_m", Math.Round(lowest, 1) + " .. " + Math.Round(highest, 1)).Add("largest_m", Math.Round(biggest, 2)).Add("most_opaque", Math.Round(alpha, 2)));
                    }
                    ro.Add("systems", systems);
                    list.Add(ro);
                }
                return list;
            });
            Rpc.Async("dev_destroy", "Development build only: blow up parts of the vessel being flown, the way a crash does (the game's own explode call for each part). params: {count (default all), over_s (default 1), match: only parts whose name contains this}", DevDestroy);
            Rpc.Sync("dev_fps", "Development build only: frame rate and how many particles are alive.", a =>
            {
                int particles = 0, systems = 0;
                foreach (ParticleSystem ps in UnityEngine.Object.FindObjectsOfType<ParticleSystem>())
                    if (ps.particleCount > 0) { particles += ps.particleCount; systems++; }
                return new JObj().Add("fps", Math.Round(1f / Mathf.Max(1e-4f, Time.smoothDeltaTime), 1)).Add("frame_ms", Math.Round(Time.unscaledDeltaTime * 1000f, 1))
                    .Add("particles", particles).Add("particle_systems", systems).Add("time_scale", Time.timeScale).Add("lights", UnityEngine.Object.FindObjectsOfType<Light>().Length);
            });
            Rpc.Sync("dev_frames", "Development build only: how many frames the game has drawn and its clock, for working out a frame rate exactly between two calls. params: {uncap: bool (true: let the game draw as fast as it can, for measuring what something costs; false: as it was. Not kept: it is back as it was at the next start), cap: frames a second to hold the game to (as a busy game runs: every frame long; 0 or less: as it was)}", a =>
            {
                if (a.Has("cap"))
                {
                    int cap = a.Int("cap", 0);
                    if (cap > 0)
                    {
                        if (devVsync < 0) { devVsync = QualitySettings.vSyncCount; devTarget = Application.targetFrameRate; }
                        QualitySettings.vSyncCount = 0; Application.targetFrameRate = cap;
                    }
                    else if (devVsync >= 0) { QualitySettings.vSyncCount = devVsync; Application.targetFrameRate = devTarget; devVsync = -1; }
                }
                if (a.Has("uncap"))
                {
                    if (a.Bool("uncap", false))
                    {
                        if (devVsync < 0) { devVsync = QualitySettings.vSyncCount; devTarget = Application.targetFrameRate; }
                        QualitySettings.vSyncCount = 0; Application.targetFrameRate = -1;
                    }
                    else if (devVsync >= 0) { QualitySettings.vSyncCount = devVsync; Application.targetFrameRate = devTarget; devVsync = -1; }
                }
                return new JObj().Add("frame", Time.frameCount).Add("time", Math.Round(Time.realtimeSinceStartup, 4)).Add("vsync", QualitySettings.vSyncCount).Add("target", Application.targetFrameRate);
            }).AlwaysAllowed = true;
            Rpc.Sync("dev_camera", "Development build only: where the flight camera really is: metres east, north and up of the vessel, its altitude, and how far it is from what it looks at.", a =>
            {
                FlightCamera cam = FlightCamera.fetch;
                Vessel v = FlightGlobals.ActiveVessel;
                if (cam == null || v == null) throw new ApiException("No flight camera.");
                Vector3d up = FlightGlobals.getUpAxis(v.mainBody, v.CoMD);
                Vector3d east = Vector3d.Cross(up, v.mainBody.transform.up).normalized, north = Vector3d.Cross(east, up);
                Vector3d at = (Vector3d)cam.transform.position - v.CoMD, pivot = (Vector3d)cam.GetPivot().position - v.CoMD;
                return new JObj().Add("east", Math.Round(Vector3d.Dot(at, east), 1)).Add("north", Math.Round(Vector3d.Dot(at, north), 1)).Add("up", Math.Round(Vector3d.Dot(at, up), 1))
                    .Add("altitude_m", Math.Round(v.mainBody.GetAltitude(cam.transform.position), 1))
                    .Add("looks_at_east", Math.Round(Vector3d.Dot(pivot, east), 1)).Add("looks_at_up", Math.Round(Vector3d.Dot(pivot, up), 1))
                    .Add("looks_at_altitude_m", Math.Round(v.mainBody.GetAltitude(cam.GetPivot().position), 1))
                    .Add("distance", Math.Round(cam.Distance, 1)).Add("pitch_deg", Math.Round(cam.camPitch * Mathf.Rad2Deg, 1)).Add("vessel_altitude_m", Math.Round(v.altitude, 2))
                    .Add("field_of_view", Math.Round(cam.FieldOfView, 1));
            });
            Rpc.Sync("dev_raycast", "Development build only: everything solid on a line straight down from a spot. params: {east, north, up: metres from the vessel (where the line starts), down_m (300), layers: bit mask (default every layer)}", a =>
            {
                Vector3d from = DevSpot(a, out Vector3d _, out Vector3d _, out Vector3d up);
                Vessel v = FlightGlobals.ActiveVessel;
                var found = new System.Collections.Generic.List<object>();
                RaycastHit[] hits = Physics.RaycastAll((Vector3)from, -(Vector3)up, (float)a.Num("down_m", 300), a.Has("layers") ? (int)a.Num("layers") : ~0, QueryTriggerInteraction.Collide);
                Array.Sort(hits, (x, y) => x.distance.CompareTo(y.distance));
                foreach (RaycastHit hit in hits)
                {
                    string path = hit.collider.name;
                    for (Transform t = hit.collider.transform.parent; t != null && path.Length < 160; t = t.parent) path = t.name + "/" + path;
                    found.Add(new JObj().Add("down_m", Math.Round(hit.distance, 1)).Add("altitude_m", Math.Round(v.mainBody.GetAltitude(hit.point), 1)).Add("layer", hit.collider.gameObject.layer)
                        .Add("trigger", hit.collider.isTrigger).Add("kind", hit.collider.GetType().Name).Add("object", path));
                }
                return new JObj().Add("from_altitude_m", Math.Round(v.mainBody.GetAltitude(from), 1)).Add("hits", found);
            });
            Rpc.Sync("dev_antialiasing", "Development build only: what kind of antialiasing each camera's post-processing (from a visual mod such as TUFX) uses, and optionally change it for testing. params: {mode: none|fxaa|smaa|taa|off|on (omit to only list)}", a =>
            {
                string mode = (a.Str("mode") ?? "").ToLowerInvariant();
                int wanted = mode == "none" ? 0 : mode == "fxaa" ? 1 : mode == "smaa" ? 2 : mode == "taa" ? 3 : -1;
                var found = new System.Collections.Generic.List<object>();
                foreach (Camera camera in Camera.allCameras)
                    foreach (Component part in camera.GetComponents<Component>())
                    {
                        if (part == null || part.GetType().Name != "PostProcessLayer") continue;
                        System.Reflection.FieldInfo field = part.GetType().GetField("antialiasingMode");
                        if (field == null) continue;
                        string before = field.GetValue(part).ToString();
                        if (wanted >= 0 && camera.name == "Camera 00") field.SetValue(part, Enum.ToObject(field.FieldType, wanted));
                        // ("off" switches that camera's post-processing off altogether, "on" back on)
                        if ((mode == "off" || mode == "on") && camera.name == "Camera 00" && part is Behaviour layer) { layer.enabled = mode == "on"; if (mode == "off") camera.ResetProjectionMatrix(); }
                        found.Add(new JObj().Add("camera", camera.name).Add("was", before).Add("now", field.GetValue(part).ToString()));
                    }
                return new JObj().Add("layers", found);
            });
            Rpc.Sync("dev_timescale", "Development build only: slow the game down or speed it up (1 is normal). params: {scale}", a =>
            {
                Time.timeScale = (float)Pilot.OrbitMath.Clamp(a.Num("scale", 1), 0.01, 4);
                return new JObj().Add("time_scale", Time.timeScale);
            });
            Rpc.Sync("dev_fx_info", "Development build only: the particle shaders the game has loaded and how the stock explosion effects are built.", a =>
            {
                var shaders = new List<object>();
                foreach (Shader sh in Resources.FindObjectsOfTypeAll<Shader>())
                    if (sh.name.IndexOf("article", StringComparison.OrdinalIgnoreCase) >= 0 || sh.name.IndexOf("Unlit", StringComparison.OrdinalIgnoreCase) >= 0 || sh.name.IndexOf("FX", StringComparison.Ordinal) >= 0)
                        shaders.Add(sh.name + (sh.isSupported ? "" : " (unsupported)"));
                shaders.Sort((x, y) => string.CompareOrdinal((string)x, (string)y));
                var o = new JObj().Add("shaders", shaders);
                FXMonger monger = UnityEngine.Object.FindObjectOfType<FXMonger>();
                if (monger == null) return o.Add("fxmonger", null);
                var sets = new JObj();
                foreach (var pair in new[] { new KeyValuePair<string, GameObject[]>("explosions", monger.explosions), new KeyValuePair<string, GameObject[]>("debrisExplosion", monger.debrisExplosion) })
                {
                    var list = new List<object>();
                    if (pair.Value != null)
                        foreach (GameObject prefab in pair.Value)
                        {
                            if (prefab == null) { list.Add(null); continue; }
                            var po = new JObj().Add("name", prefab.name).Add("active", prefab.activeSelf).Add("scale", prefab.transform.localScale.ToString());
                            AudioSource sound = prefab.GetComponent<AudioSource>();
                            if (sound != null)
                                po.Add("audio", new JObj().Add("volume", sound.volume).Add("spatial_blend", sound.spatialBlend).Add("rolloff", sound.rolloffMode.ToString())
                                    .Add("min", sound.minDistance).Add("max", sound.maxDistance).Add("spread", sound.spread).Add("doppler", sound.dopplerLevel)
                                    .Add("priority", sound.priority).Add("pitch", sound.pitch).Add("mute", sound.mute).Add("enabled", sound.enabled)
                                    .Add("bypass_effects", sound.bypassEffects).Add("bypass_listener", sound.bypassListenerEffects).Add("reverb_mix", sound.reverbZoneMix)
                                    .Add("mixer", sound.outputAudioMixerGroup != null ? sound.outputAudioMixerGroup.name : null)
                                    .Add("rolloff_keys", sound.GetCustomCurve(AudioSourceCurveType.CustomRolloff).length)
                                    .Add("blend_keys", sound.GetCustomCurve(AudioSourceCurveType.SpatialBlend).length)
                                    .Add("rolloff_at_100m", sound.rolloffMode == AudioRolloffMode.Custom ? Math.Round(sound.GetCustomCurve(AudioSourceCurveType.CustomRolloff).Evaluate(100f / Mathf.Max(1f, sound.maxDistance)), 3) : -1));
                            var comps = new List<object>();
                            foreach (Component c in prefab.GetComponentsInChildren<Component>(true))
                                if (!(c is Transform) && !(c is ParticleSystem) && !(c is ParticleSystemRenderer)) comps.Add(c.GetType().Name + "@" + c.gameObject.name);
                            po.Add("other_components", comps);
                            var systems = new List<object>();
                            foreach (ParticleSystem ps in prefab.GetComponentsInChildren<ParticleSystem>(true))
                            {
                                ParticleSystem.MainModule m = ps.main;
                                ParticleSystemRenderer r = ps.GetComponent<ParticleSystemRenderer>();
                                Material mat = r != null ? r.sharedMaterial : null;
                                var so = new JObj().Add("object", ps.gameObject.name)
                                    .Add("duration", m.duration).Add("loop", m.loop)
                                    .Add("lifetime", m.startLifetime.constantMin + ".." + m.startLifetime.constantMax + " (" + m.startLifetime.mode + ")")
                                    .Add("size", m.startSize.constantMin + ".." + m.startSize.constantMax)
                                    .Add("speed", m.startSpeed.constantMin + ".." + m.startSpeed.constantMax)
                                    .Add("max", m.maxParticles).Add("space", m.simulationSpace.ToString()).Add("stop_action", m.stopAction.ToString())
                                    .Add("gravity", m.gravityModifier.constant)
                                    .Add("bursts", ps.emission.burstCount).Add("rate", ps.emission.rateOverTime.constant)
                                    .Add("render", r != null ? r.renderMode.ToString() : null)
                                    .Add("shader", mat != null && mat.shader != null ? mat.shader.name : null)
                                    .Add("texture", mat != null && mat.mainTexture != null ? mat.mainTexture.name + " " + mat.mainTexture.width + "x" + mat.mainTexture.height : null)
                                    .Add("sheet", ps.textureSheetAnimation.enabled ? ps.textureSheetAnimation.numTilesX + "x" + ps.textureSheetAnimation.numTilesY : null);
                                systems.Add(so);
                            }
                            po.Add("particle_systems", systems);
                            list.Add(po);
                        }
                    sets.Add(pair.Key, list);
                }
                o.Add("fxmonger", sets).Add("quality_soft_particles", QualitySettings.softParticles).Add("pixel_lights", QualitySettings.pixelLightCount)
                 .Add("camera_depth_mode", FlightCamera.fetch != null && FlightCamera.fetch.mainCamera != null ? FlightCamera.fetch.mainCamera.depthTextureMode.ToString() : null)
                 .Add("camera_hdr", FlightCamera.fetch != null && FlightCamera.fetch.mainCamera != null && FlightCamera.fetch.mainCamera.allowHDR)
                 .Add("color_space", QualitySettings.activeColorSpace.ToString());
                return o;
            });
            Rpc.Sync("dev_gfx", "Development build only: what the graphics set-up can do. params: {shaders: [names to look for]}", a =>
            {
                var o = new JObj();
                o.Add("api", SystemInfo.graphicsDeviceType.ToString()).Add("max_texture", SystemInfo.maxTextureSize).Add("textures_3d", SystemInfo.supports3DTextures)
                 .Add("compute", SystemInfo.supportsComputeShaders).Add("texture_limit", QualitySettings.masterTextureLimit).Add("soft_particles", QualitySettings.softParticles)
                 .Add("anti_aliasing", QualitySettings.antiAliasing).Add("colour_space", QualitySettings.activeColorSpace.ToString()).Add("ambient", RenderSettings.ambientLight.ToString());
                var cameras = new List<object>();
                foreach (Camera c in Camera.allCameras)
                    cameras.Add(new JObj().Add("name", c.name).Add("depth", c.depth).Add("depth_texture", c.depthTextureMode.ToString()).Add("hdr", c.allowHDR).Add("msaa", c.allowMSAA)
                        .Add("path", c.actualRenderingPath.ToString()).Add("near", c.nearClipPlane).Add("far", c.farClipPlane).Add("mask", c.cullingMask).Add("fov", c.fieldOfView));
                o.Add("cameras", cameras);
                var found = new List<object>();
                List<object> names = a.List("shaders");
                if (names != null)
                    foreach (object name in names)
                    {
                        Shader s = Shader.Find(name.ToString());
                        if (s == null) { found.Add(new JObj().Add("name", name.ToString()).Add("found", false)); continue; }
                        var props = new List<object>();
                        for (int n = 0; n < s.GetPropertyCount(); n++) props.Add(s.GetPropertyName(n) + ":" + s.GetPropertyType(n));
                        found.Add(new JObj().Add("name", s.name).Add("found", true).Add("supported", s.isSupported).Add("queue", s.renderQueue).Add("passes", new Material(s).passCount).Add("properties", props));
                    }
                o.Add("shaders", found);
                Light sun = Sun.Instance != null ? Sun.Instance.GetComponent<Light>() : null;
                if (sun != null) o.Add("sun", new JObj().Add("colour", sun.color.ToString()).Add("intensity", sun.intensity).Add("enabled", sun.enabled));
                return o;
            });
            Rpc.Sync("dev_atmo", "Development build only: atmosphere figures at an altitude under the vessel. params: {altitude_m}", a =>
            {
                Vessel v = FlightApi.Active();
                CelestialBody body = v.mainBody;
                double alt = a.Num("altitude_m", 30000);
                Vector3d pos = body.position + (v.CoMD - body.position).normalized * (body.Radius + alt);
                if (!a.Has("altitude_m")) alt = v.altitude;
                double offset = Pilot.EntryPredictor.TemperatureOffset(body, v.latitude, v.longitude);
                double p = body.GetPressure(alt), t0 = body.GetTemperature(alt), t1 = Pilot.EntryPredictor.Temperature(body, alt, offset);
                return new JObj().Add("altitude_m", alt).Add("pressure_kpa", p).Add("standard_temperature", t0).Add("model_temperature", t1).Add("model_density", body.GetDensity(p, t1))
                    .Add("game_temperature", v.atmosphericTemperature).Add("game_density", v.atmDensity).Add("offset", offset)
                    .Add("model_area_drag", Pilot.EntryModel.For(v, false).Area(v.mach)).Add("mach", v.mach).Add("speed", v.srfSpeed)
                    .Add("game_drag_accel", v.geeForce * 9.80665).Add("model_drag_accel", 0.0005 * v.atmDensity * v.srfSpeed * v.srfSpeed * Pilot.EntryModel.For(v, false).Area(v.mach) * PhysicsGlobals.DragCubeMultiplier * PhysicsGlobals.DragMultiplier * PhysicsGlobals.DragCurvePseudoReynolds.Evaluate((float)(v.atmDensity * v.srfSpeed)) / v.totalMass);
            });
#endif
            Rpc.Sync("help", "List every command with a one-line description.", Help).AlwaysAllowed = true;
            Rpc.Sync("status", "Where the game is right now: scene, save, time, active vessel or editor craft.", Status).AlwaysAllowed = true;
            Rpc.Sync("activity", "Recent AI Bridge activity log. params: {count}", Activity).AlwaysAllowed = true;
            Rpc.Sync("list_saves", "List save games.", ListSaves).AlwaysAllowed = true;
            Rpc.Async("load_save", "Load a save game. params: {name}", LoadSave);
            Rpc.Async("new_save", "Create and start a new save. params: {name, mode: sandbox|science|career}", NewSave);
            Rpc.Async("goto", "Change scene. params: {scene: space_center|vab|sph|tracking_station|main_menu}", Goto);
            Rpc.Sync("navball", "Minimise the navball (the tab that folds it away at the bottom of the flight screen) and keep it minimised, at every new flight and after the map, until told otherwise or the game is closed; or bring it back. params: {down: bool (default true)}. With no params it only says how it is.", a =>
            {
                if (a.Has("down"))
                {
                    BridgeHost.NavballDown = a.Bool("down", true);
                    KSP.UI.Screens.Flight.NavBallToggle ball = KSP.UI.Screens.Flight.NavBallToggle.Instance;
                    if (!BridgeHost.NavballDown && HighLogic.LoadedSceneIsFlight && ball != null && ball.panel != null && ball.panel.collapsed) ball.panel.Expand();
                }
                return new JObj().Add("down", BridgeHost.NavballDown);
            }).AlwaysAllowed = true;
            Rpc.Async("screenshot", "Capture the game view as a JPEG, optionally moving the camera first. params: {max_width, quality, hide_ui: bool (without the game's interface; where it is put away already, by F2 or a mod, it is left so), view, distance, pitch, heading, height, focus, fit}", Screenshot).AlwaysAllowed = true;
            Rpc.Sync("message", "Show a message on the player's screen. params: {text, duration}", Message);
            Rpc.Sync("pause", "Pause or resume the flight simulation. params: {on}", Pause);
            Rpc.Sync("panel", "Show the in-game AI Bridge panel or put it away into its toolbar button. params: {shown: bool (left out: only say whether it is showing)}", a =>
            {
                if (a.Has("shown")) BridgeHost.Instance.ShowPanel(a.Bool("shown", true));
                return new JObj().Add("shown", BridgeHost.Instance.PanelShown);
            }).AlwaysAllowed = true;
            Rpc.Sync("quit", "Save (when possible) and close KSP.", Quit);
        }

#if DEV
        /// <summary>A point given as metres east, north and up of the vessel; with surface set, 'up' counts from the ground or roof there.</summary>
        internal static Vector3d DevSpot(Args a, out Vector3d east, out Vector3d north, out Vector3d up)
        {
            Vessel v = FlightGlobals.ActiveVessel;
            if (v == null) throw new ApiException("No vessel is being flown.");
            up = FlightGlobals.getUpAxis(v.mainBody, v.CoMD);
            east = Vector3d.Cross(up, v.mainBody.transform.up).normalized;        // (Unity's axes are left-handed: pole x up would be west)
            north = Vector3d.Cross(east, up);
            Vector3d centre = v.CoMD + east * a.Num("east", 0) + north * a.Num("north", 0) + up * a.Num("up", 0);
            if (a.Bool("surface", false))
            {
                Vector3d above = v.CoMD + east * a.Num("east", 0) + north * a.Num("north", 0) + up * 400.0;
                if (Physics.Raycast((Vector3)above, -(Vector3)up, out RaycastHit hit, 3000f, 1 << 15, QueryTriggerInteraction.Ignore))
                    centre = (Vector3d)hit.point + up * a.Num("up", 1);
            }
            // (alt: so high above the sea at that place, whatever `up` says. East and north are laid out on the level plane
            // through the vessel, and the planet falls away under it: 21 m at 5 km on Kerbin.)
            if (a.Has("alt"))
            {
                CelestialBody body = v.mainBody;
                centre = body.GetWorldSurfacePosition(body.GetLatitude(centre), body.GetLongitude(centre), a.Num("alt", 0));
            }
            return centre;
        }

        internal static object DevExplode(Args a) => DevExplode(a, null);

        /// <summary>(at: a place in the scene to set it off at, instead of one given from the vessel)</summary>
        internal static object DevExplode(Args a, Vector3d? at0)
        {
            Vector3d centre = DevSpot(a, out Vector3d east, out Vector3d north, out Vector3d up);
            if (at0.HasValue) centre = at0.Value;
            int count = (int)Pilot.OrbitMath.Clamp(a.Num("count", 1), 1, 80);
            double spread = a.Num("spread_m", 0), power = a.Num("power", 0.5);
            var random = new System.Random(7);
            for (int n = 0; n < count; n++)
            {
                Vector3d at = centre;
                if (count > 1) at += (east * (random.NextDouble() - 0.5) + north * (random.NextDouble() - 0.5) + up * random.NextDouble() * 0.5) * spread * 2.0;
                // what: a made-up part that "dies" there this frame, so the explosions mod takes it for a real one (see its Records.Fake).
                if (a.Has("what"))
                    System.Reflection.Assembly.GetExecutingAssembly().GetType("VolumetricExplosions.Records")?.GetMethod("Fake")?.Invoke(null, new object[] { a.Str("what"), at });
                FXMonger.Explode(null, at, count > 1 ? power * (0.5 + 0.5 * random.NextDouble()) : power);
            }
            return new JObj().Add("explosions", count).Add("power", power);
        }

        internal static object DevFxReload(Args a)
        {
            Type fresh = System.Reflection.Assembly.GetExecutingAssembly().GetType("VolumetricExplosions.Addon");
            if (fresh == null) throw new ApiException("This build does not contain the explosions mod. Build it with FXDEV=1 ./build.sh dev.");
            int removed = 0;
            foreach (MonoBehaviour behaviour in UnityEngine.Object.FindObjectsOfType<MonoBehaviour>())
                if (behaviour != null && behaviour.GetType().FullName == "VolumetricExplosions.Addon") { UnityEngine.Object.DestroyImmediate(behaviour.gameObject); removed++; }
            // off: only take the mod out, which hands the game its own explosions back (for comparing the two).
            if (a.Bool("off", false)) return new JObj().Add("removed", removed).Add("now_running", "the stock explosions");
            new GameObject("VolumetricExplosions (dev)").AddComponent(fresh);
            return new JObj().Add("replaced", removed).Add("now_running", fresh.Assembly.GetName().Name);
        }

        /// <summary>
        /// Swap in this build's copies of Keystone and the mods that stand on it: every running addon of theirs
        /// (whichever build it came from) is taken out, and those that belong in the scene the game is in are
        /// started afresh from this build, the base first.
        /// </summary>
        /// <summary>One of a few named things, given as its number or as its name (as the settings file has it).</summary>
        static int DevChoice(object setting, string text)
        {
            // (an option's own name first: a choice of frame rates has options named "24" and "25", which are not their places in it)
            string[] options = setting.GetType().GetField("Options")?.GetValue(setting) as string[];
            int at = options != null ? Array.IndexOf(options, text) : -1;
            if (at >= 0) return at;
            if (int.TryParse(text, out int n)) return n;
            throw new ApiException("'" + text + "' is not one of: " + (options != null ? string.Join(", ", options) : "a number"));
        }

        static Color DevColour(string text)
        {
            string[] parts = text.Split(',');
            return new Color(float.Parse(parts[0], System.Globalization.CultureInfo.InvariantCulture), float.Parse(parts[1], System.Globalization.CultureInfo.InvariantCulture), float.Parse(parts[2], System.Globalization.CultureInfo.InvariantCulture), 1f);
        }

        internal static object DevModsReload(Args a)
        {
            System.Reflection.Assembly here = System.Reflection.Assembly.GetExecutingAssembly();
            var fresh = new List<Type>();
            foreach (Type type in here.GetTypes())
            {
                if (!typeof(MonoBehaviour).IsAssignableFrom(type) || type.Namespace == null || type.Namespace.StartsWith("AIBridge", StringComparison.Ordinal) || type.Namespace == "VolumetricExplosions") continue;
                if (type.GetCustomAttributes(typeof(KSPAddon), false).Length > 0) fresh.Add(type);
            }
            if (fresh.Count == 0) throw new ApiException("This build does not contain the mods. Build it with MODSDEV=1 ./build.sh dev.");
            fresh.Sort((x, y) => (x.Namespace == "Keystone" ? 0 : 1).CompareTo(y.Namespace == "Keystone" ? 0 : 1));
            var names = new HashSet<string>();
            foreach (Type type in fresh) names.Add(type.FullName);
            int removed = 0;
            foreach (MonoBehaviour behaviour in UnityEngine.Object.FindObjectsOfType<MonoBehaviour>())
                if (behaviour != null && names.Contains(behaviour.GetType().FullName)) { UnityEngine.Object.DestroyImmediate(behaviour.gameObject); removed++; }
            var started = new List<object>();
            foreach (Type type in fresh)
            {
                var addon = (KSPAddon)type.GetCustomAttributes(typeof(KSPAddon), false)[0];
                bool belongs = addon.startup == KSPAddon.Startup.Instantly || addon.startup == KSPAddon.Startup.EveryScene || addon.startup == KSPAddon.Startup.AllGameScenes
                    || (addon.startup == KSPAddon.Startup.Flight && HighLogic.LoadedSceneIsFlight) || (addon.startup == KSPAddon.Startup.FlightAndEditor && (HighLogic.LoadedSceneIsFlight || HighLogic.LoadedSceneIsEditor))
                    || (addon.startup == KSPAddon.Startup.SpaceCentre && HighLogic.LoadedScene == GameScenes.SPACECENTER) || (addon.startup == KSPAddon.Startup.EditorAny && HighLogic.LoadedSceneIsEditor)
                    || (addon.startup == KSPAddon.Startup.FlightAndKSC && (HighLogic.LoadedSceneIsFlight || HighLogic.LoadedScene == GameScenes.SPACECENTER))
                    || (addon.startup == KSPAddon.Startup.FlightEditorAndKSC && (HighLogic.LoadedSceneIsFlight || HighLogic.LoadedSceneIsEditor || HighLogic.LoadedScene == GameScenes.SPACECENTER))
                    || (addon.startup == KSPAddon.Startup.TrackingStation && HighLogic.LoadedScene == GameScenes.TRACKSTATION)
                    || (addon.startup == KSPAddon.Startup.EditorVAB && HighLogic.LoadedSceneIsEditor && EditorDriver.editorFacility == EditorFacility.VAB)
                    || (addon.startup == KSPAddon.Startup.EditorSPH && HighLogic.LoadedSceneIsEditor && EditorDriver.editorFacility == EditorFacility.SPH)
                    || (addon.startup == KSPAddon.Startup.MainMenu && HighLogic.LoadedScene == GameScenes.MAINMENU);
                if (!belongs) continue;
                new GameObject(type.FullName + " (dev)").AddComponent(type);
                started.Add(type.FullName);
            }
            return new JObj().Add("removed", removed).Add("started", started).Add("now_running", here.GetName().Name);
        }

        internal static IEnumerator DevDestroy(Call c)
        {
            Vessel v = FlightGlobals.ActiveVessel;
            if (v == null) throw new ApiException("No vessel is being flown.");
            var parts = new List<Part>(v.parts);
            parts.Reverse();                                   // outermost parts first, the command part last
            string match = c.Args.Str("match");
            if (!string.IsNullOrEmpty(match))
                parts.RemoveAll(p => p == null || p.partInfo == null || (p.partInfo.title ?? "").IndexOf(match, StringComparison.OrdinalIgnoreCase) < 0);
            if (parts.Count == 0) throw new ApiException("No part matches.");
            int count = (int)Pilot.OrbitMath.Clamp(c.Args.Num("count", parts.Count), 1, parts.Count);
            float gap = (float)Pilot.OrbitMath.Clamp(c.Args.Num("over_s", 1.0), 0, 20) / count;
            int done = 0;
            for (int n = 0; n < count; n++)
            {
                Part p = parts[n];
                if (p != null && p.State != PartStates.DEAD) { p.explode(); done++; }
                if (gap > 0) yield return new WaitSec(gap * Time.timeScale > 0 ? gap / Mathf.Max(0.01f, Time.timeScale) : gap);
                else if (n % 4 == 3) yield return null;
            }
            c.Result = new JObj().Add("exploded", done);
        }
#endif

        static object Help(Args a)
        {
            var list = new List<object>();
            var names = new List<string>();
            foreach (RpcEntry e in Rpc.All) names.Add(e.Name);
            names.Sort(StringComparer.Ordinal);
            foreach (string n in names) list.Add(new JObj().Add("method", n).Add("help", Rpc.Find(n).Help));
            return list;
        }

        public static JObj Status(Args a)
        {
            var o = new JObj();
            o.Add("mod_version", BridgeHost.Version);
            o.Add("ksp_version", Versioning.version_major + "." + Versioning.version_minor + "." + Versioning.Revision);
            o.Add("scene", SceneName(HighLogic.LoadedScene));
            o.Add("scene_ready", SceneWatch.Ready);
            o.Add("ai_control_enabled", host.AiEnabled);
            if (HighLogic.LoadedSceneIsGame && HighLogic.CurrentGame != null)
            {
                o.Add("save", HighLogic.SaveFolder);
                o.Add("game_mode", HighLogic.CurrentGame.Mode.ToString().ToLowerInvariant());
                o.Add("ut", Math.Round(Planetarium.GetUniversalTime(), 2));
                o.Add("date", KSPUtil.PrintDateCompact(Planetarium.GetUniversalTime(), true));
                if (Funding.Instance != null) o.Add("funds", Math.Round(Funding.Instance.Funds));
                if (ResearchAndDevelopment.Instance != null && HighLogic.CurrentGame.Mode != Game.Modes.SANDBOX) o.Add("science", Math.Round(ResearchAndDevelopment.Instance.Science, 1));
                if (Reputation.Instance != null && HighLogic.CurrentGame.Mode == Game.Modes.CAREER) o.Add("reputation", Math.Round(Reputation.Instance.reputation, 1));
            }
            List<string> dialogs = DialogApi.Titles();
            if (dialogs.Count > 0) o.Add("open_dialogs", dialogs);
            if (HighLogic.LoadedScene == GameScenes.FLIGHT && FlightGlobals.ready && FlightGlobals.ActiveVessel != null)
            {
                Vessel v = FlightGlobals.ActiveVessel;
                o.Add("vessel", new JObj().Add("name", v.GetDisplayName()).Add("situation", v.situation.ToString()).Add("body", Places.Name(v.mainBody)).Add("altitude_m", Math.Round(v.altitude)));
            }
            else if (HighLogic.LoadedScene == GameScenes.EDITOR && EditorLogic.fetch != null && EditorLogic.fetch.ship != null)
                o.Add("editor_craft", new JObj().Add("name", EditorLogic.fetch.ship.shipName).Add("parts", EditorLogic.fetch.ship.parts.Count));
            // (the weather in a line, where the Natural Weather mod is installed: the "weather" call has all of it)
            if (HighLogic.LoadedScene == GameScenes.FLIGHT || HighLogic.LoadedScene == GameScenes.SPACECENTER)
            {
                string weather = WeatherApi.Brief();
                if (!string.IsNullOrEmpty(weather)) o.Add("weather", weather);
            }
            return o;
        }

        public static string SceneName(GameScenes scene)
        {
            switch (scene)
            {
                case GameScenes.MAINMENU: return "main_menu";
                case GameScenes.SPACECENTER: return "space_center";
                case GameScenes.EDITOR: return EditorDriver.editorFacility == EditorFacility.SPH ? "sph" : "vab";
                case GameScenes.FLIGHT: return "flight";
                case GameScenes.TRACKSTATION: return "tracking_station";
                case GameScenes.LOADING:
                case GameScenes.LOADINGBUFFER: return "loading";
                default: return scene.ToString().ToLowerInvariant();
            }
        }

        static object Activity(Args a)
        {
            int count = Math.Max(1, Math.Min(300, a.Int("count", 30)));
            IList<string> all = host.Activity;
            var list = new List<object>();
            for (int n = Math.Max(0, all.Count - count); n < all.Count; n++) list.Add(all[n]);
            return list;
        }

        // ---- saves ----

        static string SavesRoot => KSPUtil.ApplicationRootPath + "saves/";

        static List<string> SaveNames()
        {
            var names = new List<string>();
            if (!Directory.Exists(SavesRoot)) return names;
            foreach (string dir in Directory.GetDirectories(SavesRoot))
            {
                string name = Path.GetFileName(dir);
                if (reservedSaves.Contains(name)) continue;
                if (File.Exists(dir + "/persistent.sfs")) names.Add(name);
            }
            names.Sort(StringComparer.OrdinalIgnoreCase);
            return names;
        }

        static object ListSaves(Args a)
        {
            var list = new List<object>();
            foreach (string name in SaveNames())
            {
                var o = new JObj().Add("name", name);
                try
                {
                    ConfigNode meta = ConfigNode.Load(SavesRoot + name + "/persistent.loadmeta");
                    if (meta != null)
                    {
                        if (meta.HasValue("gameMode")) o.Add("mode", meta.GetValue("gameMode").ToLowerInvariant());
                        if (meta.HasValue("vesselCount")) o.Add("vessels", meta.GetValue("vesselCount"));
                        if (meta.HasValue("UT")) o.Add("ut", meta.GetValue("UT"));
                    }
                }
                catch { }
                o.Add("current", HighLogic.LoadedSceneIsGame && string.Equals(HighLogic.SaveFolder, name, StringComparison.OrdinalIgnoreCase));
                list.Add(o);
            }
            return list;
        }

        static void RequireMenuOrIdleGame(string action)
        {
            if (SceneWatch.Loading) throw new ApiException("The game is still loading; try again in a few seconds.");
            if (HighLogic.LoadedScene == GameScenes.FLIGHT)
                throw new ApiException("Cannot " + action + " during a flight. Use goto space_center first (the flight is saved).");
        }

        static IEnumerator LoadSave(Call c)
        {
            string name = c.Args.Require("name");
            RequireMenuOrIdleGame("load a save");
            string match = SaveNames().Find(s => string.Equals(s, name, StringComparison.OrdinalIgnoreCase));
            if (match == null) throw new ApiException("No save named '" + name + "'. Saves: " + string.Join(", ", SaveNames().ToArray()));

            ConfigNode node = GamePersistence.LoadSFSFile("persistent", match);
            if (node == null) throw new ApiException("Could not read saves/" + match + "/persistent.sfs");
            bool finished = false, failed = false;
            KSPUpgradePipeline.Process(node, match, SaveUpgradePipeline.LoadContext.SFS,
                n => { node = n; finished = true; },
                (opt, n) => { failed = true; finished = true; });
            while (!finished) yield return null;
            if (failed) throw new ApiException("KSP could not upgrade save '" + match + "' to this game version.");

            Game game = GamePersistence.LoadGameCfg(node, match, true, false);
            if (game == null) throw new ApiException("Save '" + match + "' is not compatible with this version of KSP.");
            HighLogic.CurrentGame = game;
            // (as the game's own main menu does: a newly installed mod's scenarios are added, the save is written with them
            // before the game starts, and the mods that read a save as it loads are told; without the writing, what was
            // added was lost when the space centre loaded the save from disk)
            GamePersistence.UpdateScenarioModules(game);
            GamePersistence.SaveGame(game, "persistent", match, SaveMode.OVERWRITE);
            GameEvents.onGameStatePostLoad.Fire(node);
            HighLogic.SaveFolder = match;
            game.startScene = GameScenes.SPACECENTER;
            host.Note("loading save '" + match + "'");
            game.Start();
            yield return SceneWatch.WaitFor(GameScenes.SPACECENTER, 180f);
            c.Result = Status(null);
        }

        static IEnumerator NewSave(Call c)
        {
            string name = KSPUtil.SanitizeString(c.Args.Require("name"), '_', true);
            RequireMenuOrIdleGame("create a save");
            if (reservedSaves.Contains(name)) throw new ApiException("'" + name + "' is a reserved folder name; pick another.");
            if (Directory.Exists(SavesRoot + name)) throw new ApiException("A save named '" + name + "' already exists. Use load_save, or choose a different name.");

            Game.Modes mode;
            switch ((c.Args.Str("mode", "sandbox") ?? "sandbox").ToLowerInvariant())
            {
                case "sandbox": mode = Game.Modes.SANDBOX; break;
                case "science": case "science_sandbox": mode = Game.Modes.SCIENCE_SANDBOX; break;
                case "career": mode = Game.Modes.CAREER; break;
                default: throw new ApiException("mode must be sandbox, science or career");
            }
            GameParameters parameters = GameParameters.GetDefaultParameters(mode, GameParameters.Preset.Normal);
            HighLogic.CurrentGame = GamePersistence.CreateNewGame(name, mode, parameters, "Squad/Flags/default", GameScenes.SPACECENTER, EditorFacility.None);
            GameEvents.onGameNewStart.Fire();
            host.Note("created " + mode.ToString().ToLowerInvariant() + " save '" + name + "'");
            HighLogic.CurrentGame.Start();
            yield return SceneWatch.WaitFor(GameScenes.SPACECENTER, 180f);
            if (c.Args.Bool("skip_intro", true))
            {
                // The welcome tutorial opens a moment after the scene; close it so it cannot block later commands.
                float until = Time.realtimeSinceStartup + 4f;
                while (Time.realtimeSinceStartup < until && DialogApi.Titles().Count == 0) yield return null;
                DialogApi.SkipIntro();
                yield return new WaitSec(0.3f);
            }
            GamePersistence.SaveGame("persistent", HighLogic.SaveFolder, SaveMode.OVERWRITE);
            c.Result = Status(null);
        }

        // ---- scenes ----

        static IEnumerator Goto(Call c)
        {
            string scene = (c.Args.Require("scene") ?? "").ToLowerInvariant().Replace(" ", "_");
            if (SceneWatch.Loading) throw new ApiException("The game is still loading; try again in a few seconds.");
            if (scene == "main_menu")
            {
                if (HighLogic.LoadedScene == GameScenes.MAINMENU) { c.Result = Status(null); yield break; }
                yield return WaitClearToSave();
                SaveBeforeLeaving();
                HighLogic.LoadScene(GameScenes.MAINMENU);
                yield return SceneWatch.WaitFor(GameScenes.MAINMENU, 120f);
                c.Result = Status(null);
                yield break;
            }
            if (!HighLogic.LoadedSceneIsGame || HighLogic.CurrentGame == null)
                throw new ApiException("No game is loaded. Use load_save or new_save first.");

            GameScenes target;
            switch (scene)
            {
                case "space_center": case "ksc": target = GameScenes.SPACECENTER; break;
                case "vab": case "sph": case "editor": target = GameScenes.EDITOR; break;
                case "tracking_station": target = GameScenes.TRACKSTATION; break;
                default: throw new ApiException("scene must be one of: space_center, vab, sph, tracking_station, main_menu");
            }
            EditorFacility facility = scene == "sph" ? EditorFacility.SPH : EditorFacility.VAB;
            bool alreadyThere = HighLogic.LoadedScene == target && (target != GameScenes.EDITOR || EditorDriver.editorFacility == facility);
            if (!alreadyThere)
            {
                yield return WaitClearToSave();
                SaveBeforeLeaving();
                host.Note("going to " + scene);
                // (started once the frame's updates are over, as launch is: started inside the bridge's Update, on the way from
                // flight to the editor the game's part menus had their update in a scene being taken down, and one threw, in
                // UIPartActionControllerInventory.UpdateCursorOverPAWs)
                string failed = null;
                bool started = false;
                host.StartCoroutine(EditorApi.AfterUpdates(() =>
                {
                    try
                    {
                        if (target == GameScenes.EDITOR)
                        {
                            EditorDriver.StartupBehaviour = EditorDriver.StartupBehaviours.START_CLEAN;
                            EditorDriver.StartEditor(facility);
                        }
                        else HighLogic.LoadScene(target);
                    }
                    catch (Exception ex) { failed = ex.Message; }
                    started = true;
                }));
                while (!started) yield return null;
                if (failed != null) throw new ApiException("The scene did not change: " + failed);
                yield return SceneWatch.WaitFor(target, 120f);
            }
            c.Result = Status(null);
        }

        /// <summary>
        /// Just after a flight loads, KSP reports the vessel as "under acceleration" for a few seconds and
        /// refuses to save. Give it a moment before treating that as a real refusal.
        /// </summary>
        public static IEnumerator WaitClearToSave(float seconds = 6f)
        {
            if (HighLogic.LoadedScene != GameScenes.FLIGHT) yield break;
            float deadline = Time.realtimeSinceStartup + seconds;
            while (Time.realtimeSinceStartup < deadline)
            {
                ClearToSaveStatus status = FlightGlobals.ClearToSave(false);
                if (status == ClearToSaveStatus.CLEAR) yield break;
                Vessel v = FlightGlobals.ActiveVessel;
                bool transient = status == ClearToSaveStatus.NOT_UNDER_ACCELERATION && v != null && v.ctrlState.mainThrottle <= 0f;
                if (!transient) yield break;
                yield return null;
            }
        }

        /// <summary>Persist the game the same way the stock UI does before leaving a scene.</summary>
        public static void SaveBeforeLeaving()
        {
            if (!HighLogic.LoadedSceneIsGame || HighLogic.CurrentGame == null) return;
            if (HighLogic.LoadedScene == GameScenes.FLIGHT)
            {
                ClearToSaveStatus clear = FlightGlobals.ClearToSave();
                if (clear != ClearToSaveStatus.CLEAR)
                    throw new ApiException("KSP will not save and leave this flight right now (" + clear + "). Stabilise the vessel (landed or in a steady orbit, engines off), or revert/recover it.");
            }
            GamePersistence.SaveGame("persistent", HighLogic.SaveFolder, SaveMode.OVERWRITE);
        }

        // ---- misc ----

#if DEV
        static int devVsync = -1, devTarget;
#endif

        static IEnumerator Screenshot(Call c)
        {
            int maxWidth = Math.Max(160, Math.Min(1920, c.Args.Int("max_width", 960)));
            int quality = Math.Max(20, Math.Min(95, c.Args.Int("quality", 70)));
            // Optional camera move first, then give it a few frames to settle before capturing.
            if (CameraApi.Apply(c.Args)) for (int n = 0; n < 20; n++) yield return null;
            // (the picture without the game's interface, as F2 does it: put back straight after. Where it is put away already, by the
            // player or by a mod such as Cinema Camera's viewfinder, it is left so: bringing it back would be taken for the player's F2)
            bool clean = c.Args.Bool("hide_ui", false) && !BridgeHost.InterfaceIsHidden;
            if (clean)
            {
                try { GameEvents.onHideUI.Fire(); } catch { }
                for (int n = 0; n < 3; n++) yield return null;
            }
            yield return WaitFrameEnd.Instance;

            int sw = Screen.width, sh = Screen.height;
            var full = new Texture2D(sw, sh, TextureFormat.RGB24, false);
            full.ReadPixels(new Rect(0, 0, sw, sh), 0, 0, false);
            full.Apply(false);

            Texture2D output = full;
            if (sw > maxWidth)
            {
                int w = maxWidth, h = Mathf.Max(1, Mathf.RoundToInt(sh * (maxWidth / (float)sw)));
                RenderTexture rt = RenderTexture.GetTemporary(w, h, 0, RenderTextureFormat.ARGB32);
                RenderTexture previous = RenderTexture.active;
                Graphics.Blit(full, rt);
                RenderTexture.active = rt;
                output = new Texture2D(w, h, TextureFormat.RGB24, false);
                output.ReadPixels(new Rect(0, 0, w, h), 0, 0, false);
                output.Apply(false);
                RenderTexture.active = previous;
                RenderTexture.ReleaseTemporary(rt);
                UnityEngine.Object.Destroy(full);
            }
            if (clean) try { GameEvents.onShowUI.Fire(); } catch { }
            byte[] jpg = output.EncodeToJPG(quality);
            int ow = output.width, oh = output.height;
            UnityEngine.Object.Destroy(output);
            c.Result = new JObj().Add("mime", "image/jpeg").Add("width", ow).Add("height", oh).Add("base64", Convert.ToBase64String(jpg));
        }

        static object Message(Args a)
        {
            string text = a.Require("text");
            float duration = (float)Math.Max(1, Math.Min(30, a.Num("duration", 6)));
            ScreenMessages.PostScreenMessage("<color=#9fd8ff>AI:</color> " + text, duration, ScreenMessageStyle.UPPER_CENTER);
            host.Note("said: " + text);
            Company.Spoke();
            return true;
        }

        static object Pause(Args a)
        {
            if (HighLogic.LoadedScene != GameScenes.FLIGHT) throw new ApiException("Pause only applies in flight.");
            bool on = a.Bool("on", !FlightDriver.Pause);
            FlightDriver.SetPause(on, true);
            return new JObj().Add("paused", FlightDriver.Pause);
        }

        static object Quit(Args a)
        {
            try { if (HighLogic.LoadedSceneIsGame && HighLogic.LoadedScene != GameScenes.FLIGHT) GamePersistence.SaveGame("persistent", HighLogic.SaveFolder, SaveMode.OVERWRITE); }
            catch (Exception ex) { BridgeHost.LogWarn("save before quit failed: " + ex.Message); }
            host.Note("closing KSP");
            Application.Quit();
            return true;
        }
    }
}
