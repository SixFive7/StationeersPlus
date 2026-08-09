using System;
using System.Collections;
using System.Collections.Generic;
using System.Threading;
using UnityEngine;

namespace ClientDriver
{
    /// <summary>
    /// Marshals work from the HTTP accept thread onto Unity's main thread and blocks
    /// the caller until it completes, so every endpoint is a synchronous
    /// request/response.
    ///
    /// Primary drain is <see cref="Update"/> on a DontDestroyOnLoad GameObject. A
    /// windowed client always ticks Update, unlike the headless dedicated server
    /// where InspectorPlus and ScenarioRunner both had to fall back to a
    /// simulation-tick postfix. <see cref="PumpFromFallback"/> exists so such a
    /// postfix can drain the same queue if Update ever stops firing; see
    /// <c>FallbackPumpPatch</c>.
    /// </summary>
    internal sealed class MainThreadPump : MonoBehaviour
    {
        private sealed class WorkItem
        {
            public Func<object> Work;
            public object Result;
            public Exception Error;
            public readonly ManualResetEvent Done = new ManualResetEvent(false);
        }

        private static readonly Queue<WorkItem> _queue = new Queue<WorkItem>();
        private static MainThreadPump _instance;
        private static int _mainThreadId = -1;

        /// <summary>Latest observed Time.frameCount, readable from any thread.</summary>
        internal static volatile int FrameCount;

        internal static long FramesSeen;
        internal static long ItemsRun;
        internal static string LastPumpSource = "none";
        internal static bool FallbackPumpUsed;

        internal static void Initialize()
        {
            _mainThreadId = Thread.CurrentThread.ManagedThreadId;
            EnsureInstance();
        }

        /// <summary>
        /// Recreates the pump GameObject if it has gone away. Called from the
        /// ImGuiManager.LateUpdate postfix, which is the one per-frame main-thread
        /// hook that survives independently of anything this plugin owns.
        /// </summary>
        internal static void EnsureInstance()
        {
            if (_instance != null) return;
            var go = new GameObject("ClientDriver_MainThreadPump");
            DontDestroyOnLoad(go);
            _instance = go.AddComponent<MainThreadPump>();
            InstanceCreations++;
        }

        internal static int InstanceCreations;

        internal static bool OnMainThread => Thread.CurrentThread.ManagedThreadId == _mainThreadId;
        internal static bool Alive => _instance != null;

        private static object Run(Func<object> work, int timeoutMs, out bool timedOut)
        {
            timedOut = false;
            if (OnMainThread) return work();

            var item = new WorkItem { Work = work };
            lock (_queue) { _queue.Enqueue(item); }

            if (!item.Done.WaitOne(timeoutMs))
            {
                timedOut = true;
                return null;
            }
            if (item.Error != null) throw item.Error;
            return item.Result;
        }

        /// <summary>
        /// Runs <paramref name="work"/> on the Unity main thread and returns its
        /// response. On timeout returns a 504 rather than hanging the harness.
        /// </summary>
        internal static HttpResponse RunSync(Func<HttpResponse> work, int timeoutMs)
        {
            if (work == null) return HttpResponse.Error("no work");
            try
            {
                bool timedOut;
                var r = Run(() => (object)work(), timeoutMs, out timedOut);
                if (timedOut) return TimeoutResponse(timeoutMs);
                return (HttpResponse)r ?? HttpResponse.Error("work produced no response");
            }
            catch (Exception ex)
            {
                return HttpResponse.Error(ex.ToString());
            }
        }

        /// <summary>Runs work on the main thread and returns its value. Throws on failure.</summary>
        internal static T RunValue<T>(Func<T> work, int timeoutMs)
        {
            bool timedOut;
            var r = Run(() => (object)work(), timeoutMs, out timedOut);
            if (timedOut) throw new TimeoutException("main thread did not drain within " + timeoutMs + " ms");
            return r == null ? default(T) : (T)r;
        }

        internal static HttpResponse TimeoutResponse(int timeoutMs)
        {
            return HttpResponse.Error(
                "timed out after " + timeoutMs + " ms waiting for the Unity main thread. " +
                "The game may be minimised with rendering stalled, sitting on a modal dialog, or still loading. " +
                "framesSeen=" + FramesSeen + " itemsRun=" + ItemsRun + " lastPump=" + LastPumpSource, 504);
        }

        /// <summary>Queues work with no result and does not wait.</summary>
        internal static void Post(Action work)
        {
            if (work == null) return;
            var item = new WorkItem { Work = () => { work(); return null; } };
            lock (_queue) { _queue.Enqueue(item); }
        }

        internal static Coroutine RunCoroutine(IEnumerator routine)
        {
            if (_instance == null) Initialize();
            return _instance.StartCoroutine(routine);
        }

        /// <summary>
        /// Blocks the calling (non-main) thread until Time.frameCount reaches
        /// <paramref name="target"/>. Used so an input endpoint only answers once the
        /// game has actually had the frames in which to consume the synthetic input.
        /// </summary>
        internal static bool WaitForFrame(int target, int timeoutMs)
        {
            var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
            while (FrameCount < target)
            {
                if (DateTime.UtcNow > deadline) return false;
                Thread.Sleep(2);
            }
            return true;
        }

        private void Update()
        {
            FramesSeen++;
            FrameCount = Time.frameCount;
            Drain("Update");
        }

        private void OnDestroy()
        {
            if (ReferenceEquals(_instance, this)) _instance = null;
        }

        /// <summary>
        /// Primary pump. Fires from a postfix on ImGuiManager.LateUpdate, which runs
        /// every frame from the splash screen onwards regardless of scene, world, or
        /// whether any of this plugin's own objects still exist.
        /// </summary>
        internal static void PumpFromFrame()
        {
            if (_mainThreadId < 0) _mainThreadId = Thread.CurrentThread.ManagedThreadId;
            EnsureInstance();
            FramesSeen++;
            FrameCount = Time.frameCount;
            Drain("Frame");
        }

        internal static void PumpFromFallback()
        {
            FallbackPumpUsed = true;
            FrameCount = Time.frameCount;
            Drain("Fallback");
        }

        private static void Drain(string source)
        {
            while (true)
            {
                WorkItem item;
                lock (_queue)
                {
                    if (_queue.Count == 0) return;
                    item = _queue.Dequeue();
                }

                LastPumpSource = source;
                ItemsRun++;
                try { item.Result = item.Work(); }
                catch (Exception ex) { item.Error = ex; }
                finally { try { item.Done.Set(); } catch { } }
            }
        }
    }
}
