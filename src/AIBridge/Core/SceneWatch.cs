using System.Collections;
using UnityEngine;

namespace AIBridge
{
    /// <summary>Tracks scene transitions so multi-step requests can wait for the game to be usable again.</summary>
    public static class SceneWatch
    {
        static bool loading;
        static Listener listener;

        // KSP's GameEvents dereference the delegate's target, so handlers must be instance methods.
        sealed class Listener
        {
            public void OnLoadRequested(GameScenes scene)
            {
                loading = true;
                // The end-of-flight summary survives a revert or scene change started from code and would
                // sit on top of the next flight, still announcing the old crash.
                try { if (KSP.UI.Dialogs.FlightResultsDialog.isDisplaying) KSP.UI.Dialogs.FlightResultsDialog.Close(); } catch { }
            }
            public void OnLoaded(GameScenes scene) { loading = false; }
        }

        public static void Hook()
        {
            if (listener != null) return;
            listener = new Listener();
            GameEvents.onGameSceneLoadRequested.Add(listener.OnLoadRequested);
            GameEvents.onLevelWasLoadedGUIReady.Add(listener.OnLoaded);
        }

        public static void Unhook()
        {
            if (listener == null) return;
            GameEvents.onGameSceneLoadRequested.Remove(listener.OnLoadRequested);
            GameEvents.onLevelWasLoadedGUIReady.Remove(listener.OnLoaded);
            listener = null;
        }

        static LoadingBufferMask mask;

        /// <summary>The "Loading..." overlay stays up for a while after a scene reports loaded.</summary>
        static bool OverlayShowing
        {
            get
            {
                if (mask == null) mask = Object.FindObjectOfType<LoadingBufferMask>();
                return mask != null && mask.camera != null && mask.camera.enabled;
            }
        }

        public static bool Loading => loading || HighLogic.LoadedScene == GameScenes.LOADING || HighLogic.LoadedScene == GameScenes.LOADINGBUFFER || OverlayShowing;

        /// <summary>True once the current scene can take commands.</summary>
        public static bool Ready
        {
            get
            {
                if (Loading) return false;
                switch (HighLogic.LoadedScene)
                {
                    case GameScenes.FLIGHT:
                        return FlightGlobals.ready && FlightGlobals.ActiveVessel != null;
                    case GameScenes.EDITOR:
                        return EditorLogic.fetch != null && EditorLogic.fetch.ship != null;
                    default:
                        return true;
                }
            }
        }

        /// <summary>Wait until <paramref name="target"/> is loaded and ready, or throw after <paramref name="timeout"/> seconds.</summary>
        public static IEnumerator WaitFor(GameScenes target, float timeout)
        {
            float deadline = Time.realtimeSinceStartup + timeout;
            // Let the load request register before polling for completion.
            yield return null;
            yield return null;
            int settled = 0;
            while (settled < 5)
            {
                if (Time.realtimeSinceStartup > deadline)
                    throw new ApiException("Timed out waiting for the " + target + " scene (currently " + HighLogic.LoadedScene + ").");
                settled = (HighLogic.LoadedScene == target && Ready) ? settled + 1 : 0;
                yield return null;
            }
        }
    }
}
