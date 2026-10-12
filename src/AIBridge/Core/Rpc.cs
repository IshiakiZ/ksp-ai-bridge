using System;
using System.Collections;
using System.Collections.Generic;
using System.Threading;

namespace AIBridge
{
    /// <summary>One request travelling from a network thread to the Unity main thread and back.</summary>
    public sealed class Call
    {
        public string Method;
        public Args Args;
        public object Result;
        public string Error;
        public readonly ManualResetEvent Done = new ManualResetEvent(false);
        /// <summary>Set when the HTTP side gave up waiting; long routines may check it to stop early.</summary>
        public volatile bool Abandoned;

        public void Fail(string message) { Error = message; }
    }

    public delegate object SyncHandler(Args a);
    public delegate IEnumerator AsyncHandler(Call c);

    /// <summary>Yield instruction for the bridge's own coroutine runner (real time, unaffected by warp or pause).</summary>
    public sealed class WaitSec
    {
        public readonly float Seconds;
        public WaitSec(float seconds) { Seconds = seconds; }
    }

    /// <summary>Yield this to resume at the end of the current rendered frame (needed for screen capture).</summary>
    public sealed class WaitFrameEnd
    {
        public static readonly WaitFrameEnd Instance = new WaitFrameEnd();
    }

    public sealed class RpcEntry
    {
        public string Name;
        public string Help;
        public SyncHandler Sync;
        public AsyncHandler Async;
        /// <summary>True for calls that still work while the player has paused AI control.</summary>
        public bool AlwaysAllowed;
    }

    public static class Rpc
    {
        static readonly Dictionary<string, RpcEntry> entries = new Dictionary<string, RpcEntry>(StringComparer.OrdinalIgnoreCase);

        public static void Clear() => entries.Clear();

        public static RpcEntry Sync(string name, string help, SyncHandler handler)
        {
            var e = new RpcEntry { Name = name, Help = help, Sync = handler };
            entries[name] = e;
            return e;
        }

        public static RpcEntry Async(string name, string help, AsyncHandler handler)
        {
            var e = new RpcEntry { Name = name, Help = help, Async = handler };
            entries[name] = e;
            return e;
        }

        public static RpcEntry Find(string name) => name != null && entries.TryGetValue(name, out RpcEntry e) ? e : null;

        public static IEnumerable<RpcEntry> All => entries.Values;
    }
}
