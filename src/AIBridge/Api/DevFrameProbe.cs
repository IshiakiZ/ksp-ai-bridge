#if DEV
using System;
using System.Collections;
using System.Diagnostics;
using System.Text;
using UnityEngine;

namespace AIBridge.Api
{
    /// <summary>
    /// (Development build: where a frame's time goes, coarsely, for the performance mod: measured before anything is
    /// changed.) Markers at the very start and the very end of each of the game's script phases (the physics step's,
    /// Update's, LateUpdate's), and at the end of the frame's drawing; the gaps between them are what the engine does
    /// itself: the physics engine after the physics step's scripts, the drawing after LateUpdate, and, after the drawing,
    /// handing the picture over (where the processor waits for the graphics card when that is the slower of the two).
    ///
    /// Through dev_call: Start("seconds"), then Report() (which also stops it). Nothing is left in the game afterwards.
    /// </summary>
    public static class DevFrameProbe
    {
        static GameObject holder;
        static readonly Stopwatch clock = new Stopwatch();
        static double Now => clock.Elapsed.TotalMilliseconds;

        // this frame's marks (ms on the clock), and what has been summed over the frames measured
        static double frameEnd = -1, fixedFirst, fixedLast = -1, updateFirst, updateLast, lateFirst, lateLast, camFirst = -1, camLast;
        static double sumFrame, sumFixedScripts, sumPhysics, sumUpdate, sumBetween, sumLate, sumDraw, sumCams, sumHandOver, worst;
        static int frames, steps;
        static bool inFrame;

        public static string Start(string arg)
        {
            Stop();
            clock.Reset(); clock.Start();
            frameEnd = -1; fixedLast = -1; camFirst = -1;
            sumFrame = sumFixedScripts = sumPhysics = sumUpdate = sumBetween = sumLate = sumDraw = sumCams = sumHandOver = worst = 0;
            frames = steps = 0; inFrame = false;
            holder = new GameObject("AIBridge frame probe");
            UnityEngine.Object.DontDestroyOnLoad(holder);
            holder.AddComponent<ProbeFirst>();
            holder.AddComponent<ProbeLast>();
            holder.AddComponent<ProbeEnd>();
            Camera.onPreCull += PreCull;
            Camera.onPostRender += PostRender;
            return "measuring";
        }

        static void Stop()
        {
            Camera.onPreCull -= PreCull;
            Camera.onPostRender -= PostRender;
            if (holder != null) UnityEngine.Object.Destroy(holder);
            holder = null;
        }

        public static string Report()
        {
            Stop();
            if (frames < 1) return "no whole frame measured";
            double n = frames;
            var b = new StringBuilder();
            b.Append(frames).Append(" frames, ").Append((sumFrame / n).ToString("F2")).Append(" ms a frame on average (").Append((1000.0 * n / sumFrame).ToString("F1")).Append(" fps), the slowest ").Append(worst.ToString("F1")).Append(" ms; ")
             .Append((steps / n).ToString("F2")).Append(" physics steps a frame\n");
            void Line(string what, double sum) => b.Append("  ").Append(what.PadRight(46)).Append((sum / n).ToString("F2").PadLeft(7)).Append(" ms  ").Append((100.0 * sum / sumFrame).ToString("F0").PadLeft(3)).Append("%\n");
            Line("physics step: the scripts (FixedUpdate)", sumFixedScripts);
            Line("physics step: the physics engine", sumPhysics);
            Line("Update scripts", sumUpdate);
            Line("between Update and LateUpdate (engine)", sumBetween);
            Line("LateUpdate scripts", sumLate);
            Line("drawing, on the processor", sumDraw);
            Line("  of which the cameras, culling to rendered", sumCams);
            Line("after drawing (hand-over, waiting on the GPU)", sumHandOver);
            return b.ToString();
        }

        // ---- the marks
        internal static void FixedFirst()
        {
            double t = Now;
            BeginFrame(t);
            if (fixedLast >= 0) sumPhysics += t - fixedLast;    // (the engine's physics after the last step's scripts)
            fixedFirst = t;
        }
        internal static void FixedLast() { double t = Now; sumFixedScripts += t - fixedFirst; fixedLast = t; steps++; }
        internal static void UpdateFirst()
        {
            double t = Now;
            BeginFrame(t);
            if (fixedLast >= 0) { sumPhysics += t - fixedLast; fixedLast = -1; }
            updateFirst = t;
        }
        internal static void UpdateLast() { updateLast = Now; sumUpdate += updateLast - updateFirst; }
        internal static void LateFirst() { lateFirst = Now; sumBetween += lateFirst - updateLast; }
        internal static void LateLast() { lateLast = Now; sumLate += lateLast - lateFirst; }
        static void PreCull(Camera c) { if (camFirst < 0) camFirst = Now; }
        static void PostRender(Camera c) { camLast = Now; }
        internal static void EndOfFrame()
        {
            double t = Now;
            if (!inFrame) return;
            sumDraw += t - lateLast;
            if (camFirst >= 0) sumCams += camLast - camFirst;
            camFirst = -1;
            frameEnd = t;
            inFrame = false;
        }

        /// <summary>A frame's first mark: what lay between the last frame's end of drawing and here was the hand-over.</summary>
        static void BeginFrame(double t)
        {
            if (inFrame) return;
            if (frameEnd >= 0) sumHandOver += t - frameEnd;
            // (the whole frame: from the last one's first mark to this one's)
            if (lastBegin >= 0 && frameEnd >= 0)
            {
                double whole = t - lastBegin;
                sumFrame += whole; frames++;
                if (whole > worst) worst = whole;
            }
            lastBegin = t;
            inFrame = true;
        }
        static double lastBegin = -1;

        internal static IEnumerator Ends()
        {
            var wait = new WaitForEndOfFrame();
            while (true) { yield return wait; EndOfFrame(); }
        }
    }

    [DefaultExecutionOrder(-32000)]
    sealed class ProbeFirst : MonoBehaviour
    {
        void FixedUpdate() => DevFrameProbe.FixedFirst();
        void Update() => DevFrameProbe.UpdateFirst();
        void LateUpdate() => DevFrameProbe.LateFirst();
    }

    [DefaultExecutionOrder(32000)]
    sealed class ProbeLast : MonoBehaviour
    {
        void FixedUpdate() => DevFrameProbe.FixedLast();
        void Update() => DevFrameProbe.UpdateLast();
        void LateUpdate() => DevFrameProbe.LateLast();
    }

    sealed class ProbeEnd : MonoBehaviour
    {
        void Start() => StartCoroutine(DevFrameProbe.Ends());
    }
}
#endif
