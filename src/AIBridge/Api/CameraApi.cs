using System;
using System.Collections;
using UnityEngine;

namespace AIBridge.Api
{
    /// <summary>Camera control, so a vision-capable AI can choose what its screenshots show.</summary>
    public static class CameraApi
    {
        public static void Register(BridgeHost h)
        {
            Rpc.Async("camera", "Move the camera. params: {view: flight|map, distance, pitch, heading, height, focus, fit}", Camera);
        }

        static IEnumerator Camera(Call c)
        {
            Apply(c.Args);
            for (int n = 0; n < 12; n++) yield return null;
            c.Result = Describe();
        }

        /// <summary>Apply whichever camera settings are present; unknown ones for the current scene are ignored.</summary>
        public static bool Apply(Args a)
        {
            bool any = false;
            foreach (string key in new[] { "view", "distance", "pitch", "heading", "height", "focus", "fit" }) any |= a.Has(key);
            if (!any) return false;

            if (HighLogic.LoadedScene == GameScenes.EDITOR) ApplyEditor(a);
            else if (HighLogic.LoadedScene == GameScenes.FLIGHT) ApplyFlight(a);
            else if (HighLogic.LoadedScene == GameScenes.TRACKSTATION) ApplyMap(a);
            else throw new ApiException("The camera can be moved in the editor, in flight and in the tracking station.");
            return true;
        }

        static void ApplyEditor(Args a)
        {
            ShipConstruct ship = EditorLogic.fetch != null ? EditorLogic.fetch.ship : null;
            Vector3 focus = new Vector3(0f, 9f, 0f);
            float fitDistance = 12f;
            if (ship != null && ship.parts.Count > 0)
            {
                Vector3 size = ShipConstruction.CalculateCraftSize(ship);
                focus = ShipConstruction.FindCraftCenter(ship, true);
                fitDistance = Mathf.Clamp(Mathf.Max(size.y, Mathf.Max(size.x, size.z)) * 1.25f + 3f, 4f, 60f);
            }
            float pitch = a.Has("pitch") ? (float)a.Num("pitch") * Mathf.Deg2Rad : float.NaN;
            float heading = a.Has("heading") ? (float)a.Num("heading") * Mathf.Deg2Rad : float.NaN;
            bool place = a.Bool("fit", false) || a.Has("distance") || a.Has("height");
            float distance = a.Has("distance") ? (float)a.Num("distance") : fitDistance;
            if (a.Has("height")) focus.y = (float)a.Num("height");

            VABCamera vab = UnityEngine.Object.FindObjectOfType<VABCamera>();
            if (vab != null && vab.isActiveAndEnabled)
            {
                vab.maxDistance = Mathf.Max(vab.maxDistance, distance + 1f);
                if (!float.IsNaN(pitch)) vab.camPitch = pitch;
                if (!float.IsNaN(heading)) vab.camHdg = heading;
                if (place) { vab.scrollHeight = focus.y; vab.PlaceCamera(focus, distance); }
                return;
            }
            SPHCamera sph = UnityEngine.Object.FindObjectOfType<SPHCamera>();
            if (sph != null && sph.isActiveAndEnabled)
            {
                sph.maxDistance = Mathf.Max(sph.maxDistance, distance + 1f);
                if (!float.IsNaN(pitch)) sph.camPitch = pitch;
                if (!float.IsNaN(heading)) sph.camHdg = heading;
                if (place) { sph.scrollHeight = focus.y; sph.PlaceCamera(focus, distance); }
            }
        }

        static void ApplyFlight(Args a)
        {
            string view = (a.Str("view") ?? "").ToLowerInvariant();
            if (view == "map" && !MapView.MapIsEnabled) MapView.EnterMapView();
            else if ((view == "flight" || view == "vessel") && MapView.MapIsEnabled) MapView.ExitMapView();

            if (MapView.MapIsEnabled) { ApplyMap(a); return; }
            FlightCamera cam = FlightCamera.fetch;
            if (cam == null) return;
            if (a.Has("pitch")) cam.camPitch = Mathf.Clamp((float)a.Num("pitch") * Mathf.Deg2Rad, cam.minPitch, cam.maxPitch);
            if (a.Has("heading")) cam.camHdg = (float)a.Num("heading") * Mathf.Deg2Rad;
            if (a.Has("distance")) cam.SetDistanceImmediate(Mathf.Clamp((float)a.Num("distance"), cam.minDistance, cam.maxDistance));
            else if (a.Bool("fit", false) && FlightGlobals.ActiveVessel != null)
            {
                Vector3 size = FlightGlobals.ActiveVessel.vesselSize;
                cam.SetDistanceImmediate(Mathf.Clamp(Mathf.Max(size.x, Mathf.Max(size.y, size.z)) * 1.6f + 6f, cam.minDistance, 500f));
            }
        }

        static void ApplyMap(Args a)
        {
            PlanetariumCamera cam = PlanetariumCamera.fetch;
            if (cam == null) return;
            string focus = a.Str("focus");
            if (!string.IsNullOrEmpty(focus))
            {
                CelestialBody body = Places.Body(focus);
                if (body != null) cam.SetTarget(body);
                else if (focus.Equals("vessel", StringComparison.OrdinalIgnoreCase) && FlightGlobals.ActiveVessel != null && FlightGlobals.ActiveVessel.mapObject != null)
                    cam.SetTarget(FlightGlobals.ActiveVessel.mapObject);
                else throw new ApiException("Map focus must be a body name or 'vessel'.");
            }
            if (a.Has("pitch")) cam.camPitch = Mathf.Clamp((float)a.Num("pitch") * Mathf.Deg2Rad, cam.minPitch, cam.maxPitch);
            if (a.Has("heading")) cam.camHdg = (float)a.Num("heading") * Mathf.Deg2Rad;
            if (a.Has("distance")) cam.SetDistance(Mathf.Clamp((float)a.Num("distance"), cam.minDistance, cam.maxDistance));
        }

        static JObj Describe()
        {
            var o = new JObj().Add("scene", GameApi.SceneName(HighLogic.LoadedScene));
            if (HighLogic.LoadedScene == GameScenes.FLIGHT)
            {
                o.Add("view", MapView.MapIsEnabled ? "map" : "flight");
                if (MapView.MapIsEnabled && PlanetariumCamera.fetch != null) o.Add("distance", Math.Round(PlanetariumCamera.fetch.Distance, 1));
                else if (FlightCamera.fetch != null) o.Add("distance", Math.Round(FlightCamera.fetch.Distance, 1));
            }
            return o;
        }
    }
}
