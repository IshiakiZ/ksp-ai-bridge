using System;
using System.IO;
using System.Reflection;
using UnityEngine;

namespace AIBridgeDev
{
    /// <summary>
    /// Development-only host. Watches GameData/AIBridge/PluginData/dev for the newest build of the bridge
    /// and swaps it in while the game is running, so code changes do not need a KSP restart.
    /// Release installs do not include this assembly.
    /// </summary>
    [KSPAddon(KSPAddon.Startup.Instantly, true)]
    public sealed class AIBridgeDevLoader : MonoBehaviour
    {
        string watchDir;
        string loadedPath;
        GameObject hostObject;
        float nextCheck;

        void Awake()
        {
            DontDestroyOnLoad(gameObject);
            watchDir = KSPUtil.ApplicationRootPath + "GameData/AIBridge/PluginData/dev/";
            Debug.Log("[AIBridgeDev] watching " + watchDir);
        }

        void Update()
        {
            if (Time.realtimeSinceStartup < nextCheck) return;
            nextCheck = Time.realtimeSinceStartup + 0.5f;
            string newest = Newest();
            if (newest == null || newest == loadedPath) return;
            try { Load(newest); }
            catch (Exception ex)
            {
                Debug.LogError("[AIBridgeDev] failed to load " + newest + ": " + ex);
                loadedPath = newest;
            }
        }

        string Newest()
        {
            if (!Directory.Exists(watchDir)) return null;
            string best = null;
            foreach (string f in Directory.GetFiles(watchDir, "AIBridge.Core.*.bin"))
                if (best == null || string.CompareOrdinal(f, best) > 0) best = f;
            return best;
        }

        void Load(string path)
        {
            if (hostObject != null)
            {
                DestroyImmediate(hostObject);
                hostObject = null;
            }
            Assembly assembly = Assembly.Load(File.ReadAllBytes(path));
            Type hostType = assembly.GetType("AIBridge.BridgeHost", true);
            loadedPath = path;
            hostObject = new GameObject("AIBridge (dev)");
            hostObject.AddComponent(hostType);
            Debug.Log("[AIBridgeDev] loaded " + Path.GetFileName(path));
        }
    }
}
