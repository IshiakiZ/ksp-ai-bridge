#if !DEV
using UnityEngine;

namespace AIBridge
{
    /// <summary>Entry point KSP discovers at startup. Creates the persistent bridge object once.</summary>
    [KSPAddon(KSPAddon.Startup.Instantly, true)]
    public sealed class AIBridgeBootstrap : MonoBehaviour
    {
        void Awake()
        {
            if (BridgeHost.Instance == null) new GameObject("AIBridge").AddComponent<BridgeHost>();
        }
    }
}
#endif
