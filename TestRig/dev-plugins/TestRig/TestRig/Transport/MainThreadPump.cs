using System;
using System.Collections;
using System.Collections.Generic;
using System.Threading;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace TestRig
{
    /// <summary>
    ///     Marshals work from the HTTP accept thread onto Unity's main thread and blocks the
    ///     caller until it completes, so every endpoint stays a synchronous request/response.
    ///
    ///     <para>
    ///     This is the one component the merge could not carry across unchanged. ClientDriver
    ///     drained the queue from whichever hook called it and executed the work on that thread.
    ///     On a game client that is harmless. On the dedicated server the pump ClientDriver fell
    ///     back to, a postfix on <c>ElectricityManager.ElectricityTick</c>, runs on a UniTask
    ///     ThreadPool worker: measured across three runs its thread id rotated through 20, 25,
    ///     42, 50, 9, 58, 44, 45 and 57 and was never 1. Draining there would have executed every
    ///     <c>Main(...)</c>-wrapped route off the main thread, where
    ///     <c>UnityEngine.Object.FindObjectsOfType</c> crashes the engine native side
    ///     intermittently, which is why every scenario body iterates
    ///     <c>OcclusionManager.AllThings</c> instead.
    ///     </para>
    ///
    ///     <para><b>Two rules, both measured.</b></para>
    ///     <list type="number">
    ///     <item><description>
    ///         <see cref="Drain"/> executes nothing unless it is running on the captured Unity
    ///         main thread. A thread-identity check, not a host check, so it also closes the same
    ///         hole on a client whose per-frame hooks have stopped. A pump firing on a worker only
    ///         advances counters, and <see cref="OffThreadPumpsRefused"/> is reported rather than
    ///         swallowed: on the dedicated server it climbs once per simulation tick and is the
    ///         evidence the guard is in force.
    ///     </description></item>
    ///     <item><description>
    ///         How a queued item REACHES the main thread is a selectable strategy, chosen at load
    ///         and reported on <c>/ping</c>, <c>/instance</c> and <c>/status</c>. A strategy that
    ///         cannot reach the main thread reports itself unavailable with a reason, and never
    ///         silently accepts work nothing will run.
    ///     </description></item>
    ///     </list>
    ///
    ///     <para><b>Which hooks actually pump, measured on 0.2.6428.27798.</b></para>
    ///     <list type="bullet">
    ///     <item><description>
    ///         <c>Assets.Scripts.GameManager.Update</c> is the PRIMARY on both builds. It exists
    ///         in both assemblies, runs on thread 1, ticks at about 24 Hz, and is independent of
    ///         pause state: with no client attached and force-unpause off, it kept running for
    ///         287 s while <c>GameTickCount</c> stayed at 0 the whole time.
    ///     </description></item>
    ///     <item><description>
    ///         <c>ImGuiManager.LateUpdate</c> is CLIENT ONLY, and not because it is never called:
    ///         <b>the method does not exist in the dedicated server assembly</b>. Mono.Cecil
    ///         metadata shows 19 methods and 17 fields on the client build against 1 method
    ///         (<c>.ctor</c>) and 0 fields on the server build, neither base in
    ///         <c>Singleton&lt;T&gt; -&gt; ManagerBase -&gt; MonoBehaviour</c> declares it, live
    ///         resolution returned null in all three runs, and <c>FindObjectsOfType</c> found 0
    ///         instances. It is kept as a supplementary client drain for the splash window before
    ///         GameManager is up, and its <c>Prepare()</c> resolves to false headless on its own.
    ///     </description></item>
    ///     <item><description>
    ///         <c>UnityMainThreadDispatcher</c> is the BACKSTOP. It executes while the world is
    ///         paused: 51 of 52 accepted items ran on thread 1, none threw, 4-37 ms latency once
    ///         the game is up. It cannot be earlier than the primary, because it drains from
    ///         <c>ManagerUpdate</c> and <c>GameManager.Update</c> is the sole caller of
    ///         <c>ManagerBase.ManagerUpdate</c> in the assembly. It is retained because it is the
    ///         independently proven path and because it costs nothing to keep.
    ///     </description></item>
    ///     <item><description>
    ///         <c>MonoBehaviour.Update</c> on a plugin-owned GameObject fires ONLY on an object
    ///         recreated after the first scene load, and it is what covers BOOT. A plugin's own
    ///         object never ticks at all: measured across four runs, the component and everything
    ///         it created in <c>Awake</c> were destroyed 135-219 ms later, at
    ///         <c>Time.frameCount == 0</c>, before the first scene loads, having received zero
    ///         <c>Update</c> calls and never reaching <c>Start()</c>. <c>DontDestroyOnLoad</c>
    ///         does not save it: the call appears to succeed (scene name "DontDestroyOnLoad",
    ///         handle -12) but does not bind, because no scene is loaded at that moment, and the
    ///         DDOL object and a plain one beside it die 1 ms apart. So the host here is created
    ///         from the first <c>SceneManager.sceneLoaded</c> callback (282 ms, still frame 0),
    ///         after which it survives indefinitely and misses nothing: Update 5867 at
    ///         <c>Time.frameCount</c> 5867, no gap.
    ///     </description></item>
    ///     </list>
    ///
    ///     <para><b>Why boot needs its own pump.</b> <c>GameManager.Update</c> is the steady-state
    ///     primary, but it is unusable during boot: measured at 0.11-0.16 calls per second until
    ///     <c>GameState.Running</c>, while frames were advancing at 25 Hz the whole time. Relying
    ///     on it alone would leave the control plane effectively frozen for the whole 80-90 s
    ///     boot, which is exactly the window in which a caller is polling for readiness. The
    ///     recreated host's <c>Update</c> runs at ~25 Hz from frame 0 and covers it. Recreating
    ///     later than the first <c>sceneLoaded</c> does not work: at the Base scene load the
    ///     object appears at frame 1925 and everything before it is lost.</para>
    ///
    ///     <para><b>Do not use <c>FixedUpdate</c> for anything the control plane depends on.</b>
    ///     <c>Update</c> and <c>LateUpdate</c> are unaffected by pause (24.85-25.06 per second in
    ///     every regime), but <c>FixedUpdate</c> is gated on <c>Time.timeScale</c>, not on
    ///     <c>IsGamePaused</c>, and the two disagree on a headless server. <c>GameManager.StartGame()</c>
    ///     assigns <c>Time.timeScale = 1f</c> while <c>IsGamePaused</c> is already true, so
    ///     <c>DelayedStartupPause</c>'s <c>SetGamePause(true)</c> hits the
    ///     <c>if (IsGamePaused != pauseGame)</c> guard and never drops the scale. A nominally
    ///     paused server therefore still runs FixedUpdate, and a real <c>SetGamePause(true)</c>
    ///     transition stops it dead. Neither emits a log line.</para>
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

        /// <summary>
        ///     How a work item gets from the calling thread onto the Unity main thread. Named
        ///     rather than boolean because the name appears in every timeout message and on
        ///     /status, and "which pump was I actually using" was unanswerable before.
        /// </summary>
        private interface IPumpStrategy
        {
            string Name { get; }

            /// <summary>Can this strategy reach the main thread right now.</summary>
            bool Available { get; }

            /// <summary>Why not, when <see cref="Available"/> is false. Rides the 504 body.</summary>
            string Unavailable { get; }

            /// <summary>Hand the item over. Returns false if it could not be scheduled at all.</summary>
            bool Submit(Action run);
        }

        /// <summary>
        ///     The primary on both builds: park the item on a queue and let the next main-thread
        ///     hook drain it.
        ///
        ///     <para>
        ///     The hook that matters is the <c>GameManager.Update</c> postfix, which exists and
        ///     runs on thread 1 in both assemblies at about 24 Hz regardless of pause state. On a
        ///     client the <c>ImGuiManager.LateUpdate</c> postfix drains the same queue as well,
        ///     which covers the splash window before GameManager is up.
        ///     </para>
        ///
        ///     <para>
        ///     <see cref="Available"/> is a measurement, not an assumption: it is true only once a
        ///     drain has actually run on the main thread. On the dedicated server this used to be
        ///     silently dead, because the only hook wired there was ImGui's, which does not exist
        ///     in that assembly at all. Reporting unavailable with the resolved-hook list is what
        ///     turns that into an answer.
        ///     </para>
        /// </summary>
        private sealed class MainThreadDrainStrategy : IPumpStrategy
        {
            public string Name => "mainThreadDrain";

            public bool Available => MainThreadDrains > 0;

            public string Unavailable =>
                "no main-thread drain has run yet (mainThreadDrains=0). Hooks resolved: " + HookReport() + ".";

            public bool Submit(Action run)
            {
                // LastPumpSource is left to Drain, which knows which hook the item came out on.
                lock (_queue) { _queue.Enqueue(new QueuedAction(run)); }
                return true;
            }
        }

        /// <summary>
        ///     The backstop: hand the item to the game's own main-thread marshal.
        ///
        ///     <c>UnityMainThreadDispatcher</c> is a GAME type (<c>Assets.Scripts.Util</c>), not
        ///     one this plugin owns, so it survives the BepInEx plugin component being destroyed
        ///     partway through boot. The game itself uses it from <c>Cable</c> when
        ///     <c>ThreadedManager.IsThread</c>, which is the same problem this has.
        ///
        ///     <para>
        ///     It has no <c>Update</c>. Its twelve methods are <c>.cctor</c>, <c>.ctor</c>,
        ///     <c>ActionWrapper</c>, <c>ClearAll</c>, three <c>Enqueue</c> overloads,
        ///     <c>Exists</c>, <c>Instance</c>, <c>ManagerAwake</c>, <c>ManagerUpdate</c> and
        ///     <c>OnDestroy</c>. It drains from <c>ManagerUpdate</c>. Nothing here patches that:
        ///     the primary strategy patches <c>GameManager.Update</c>, which is the sole caller of
        ///     <c>ManagerBase.ManagerUpdate</c>, so the drain cannot be later than this marshal.
        ///     </para>
        ///
        ///     <para>
        ///     <c>Exists()</c> is checked before every submit, not once: it returned false for the
        ///     first ~35 s of boot, and <c>Instance()</c> THROWS in that window rather than
        ///     returning null.
        ///     </para>
        ///
        ///     Resolved reflectively so a game update that moves or renames the type degrades to a
        ///     reported-unavailable strategy and a 504 that names it, instead of a plugin that
        ///     will not load at all.
        /// </summary>
        private sealed class DispatcherMarshalStrategy : IPumpStrategy
        {
            private readonly Func<bool> _exists;
            private readonly Action<Action> _enqueue;
            private readonly string _resolveError;

            internal DispatcherMarshalStrategy(Func<bool> exists, Action<Action> enqueue, string resolveError)
            {
                _exists = exists;
                _enqueue = enqueue;
                _resolveError = resolveError;
            }

            public string Name => "unityMainThreadDispatcher";

            public bool Available
            {
                get
                {
                    if (_enqueue == null) return false;
                    try { return _exists(); }
                    catch { return false; }
                }
            }

            public string Unavailable =>
                _enqueue == null
                    ? "Assets.Scripts.Util.UnityMainThreadDispatcher could not be resolved: " + _resolveError
                    : "UnityMainThreadDispatcher.Exists() is false. Measured, that is normal for the first " +
                      "~35 seconds of boot and means the game has not created its main-thread marshal yet.";

            public bool Submit(Action run)
            {
                if (_enqueue == null) return false;
                try
                {
                    if (!_exists()) return false;
                    // There is no Drain on this path, so the strategy records the source itself.
                    _enqueue(() => { LastPumpSource = Name; run(); });
                    return true;
                }
                catch
                {
                    return false;
                }
            }
        }

        /// <summary>
        ///     Tries the drain, then the marshal, and reports both reasons when neither can run.
        ///
        ///     <para>
        ///     The order is not arbitrary. The marshal drains from <c>ManagerUpdate</c>, whose
        ///     sole caller in the assembly is <c>GameManager.Update</c>, which is the hook the
        ///     drain is wired to. So the drain is available whenever the marshal is, and
        ///     potentially earlier. The marshal stays because it is the path that was
        ///     independently measured executing under a paused world with no client, and because
        ///     a second route to thread 1 costs nothing.
        ///     </para>
        /// </summary>
        private sealed class CompositePumpStrategy : IPumpStrategy
        {
            private readonly IPumpStrategy _first;
            private readonly IPumpStrategy _second;

            internal CompositePumpStrategy(IPumpStrategy first, IPumpStrategy second)
            {
                _first = first;
                _second = second;
            }

            public string Name => _first.Name + "+" + _second.Name;

            public bool Available => _first.Available || _second.Available;

            public string Unavailable =>
                "nothing can reach the Unity main thread right now. " +
                _first.Name + ": " + _first.Unavailable + " " +
                _second.Name + ": " + _second.Unavailable;

            public bool Submit(Action run)
            {
                if (_first.Available && _first.Submit(run)) return true;
                if (_second.Submit(run)) return true;
                // Neither route is live. Park it on the queue anyway rather than failing
                // immediately: a request that arrives one frame before the first drain should
                // answer, not 504. The wait below still bounds it.
                return _first.Submit(run);
            }

            internal IPumpStrategy Drain => _first;
            internal IPumpStrategy Marshal => _second;
        }

        private sealed class QueuedAction
        {
            internal readonly Action Run;
            internal QueuedAction(Action run) { Run = run; }
        }

        private static readonly Queue<QueuedAction> _queue = new Queue<QueuedAction>();
        private static MainThreadPump _pumpHost;
        private static int _mainThreadId = -1;
        private static CompositePumpStrategy _strategy;
        private static bool _sceneHookInstalled;

        /// <summary>Latest observed Time.frameCount, readable from any thread.</summary>
        internal static volatile int FrameCount;

        /// <summary>Main-thread hook invocations. Was frame-pump-only; now both main-thread hooks.</summary>
        internal static long FramesSeen;

        internal static long ItemsRun;
        internal static string LastPumpSource = "none";

        /// <summary>True once the ElectricityTick postfix has fired: the simulation-liveness signal.</summary>
        internal static bool FallbackPumpUsed;

        /// <summary>Times <see cref="Drain"/> ran on the main thread. The availability measurement.</summary>
        internal static long MainThreadDrains;

        /// <summary>
        ///     How many times a pump fired on a thread that is not the Unity main thread and was
        ///     therefore refused the drain. On a dedicated server this climbs once per simulation
        ///     tick and is not an error; it is the counter that PROVES the fix is in force, so it
        ///     is reported rather than swallowed.
        /// </summary>
        internal static long OffThreadPumpsRefused;

        /// <summary>How many times the pump host GameObject has been created. Expect 1.</summary>
        internal static int InstanceCreations;

        /// <summary>sceneLoaded callbacks seen. The first one is what creates the pump host.</summary>
        internal static int ScenesLoaded;

        /// <summary>Time.frameCount when the pump host was created. Expect 0, from the first scene load.</summary>
        internal static int PumpHostCreatedAtFrame = -1;

        /// <summary>Drains that came from the pump host's own Update. This is the boot coverage.</summary>
        internal static long HostUpdateDrains;

        // Which hooks resolved at patch time. Set by the patch classes so the unavailable text can
        // say "ImGuiManager.LateUpdate: absent" rather than leaving a caller to guess.
        internal static bool GameManagerUpdateHooked;
        internal static bool ImGuiLateUpdateHooked;
        internal static bool SimTickHooked;

        internal static string HookReport()
        {
            return "GameManager.Update=" + (GameManagerUpdateHooked ? "patched" : "ABSENT") +
                   " (steady state only: measured 0.11-0.16/s until GameState.Running)" +
                   ", pumpHost.Update=" + (_pumpHost != null
                        ? "live from frame " + PumpHostCreatedAtFrame + " (boot coverage, ~25 Hz)"
                        : (_sceneHookInstalled ? "waiting for the first sceneLoaded" : "NOT INSTALLED")) +
                   ", ImGuiManager.LateUpdate=" + (ImGuiLateUpdateHooked ? "patched" : "absent (expected on the dedicated server: the method is not in that assembly)") +
                   ", ElectricityManager.ElectricityTick=" + (SimTickHooked ? "patched (liveness only, never drains)" : "ABSENT");
        }

        internal static string StrategyName => _strategy == null ? "none" : _strategy.Name;
        internal static bool MarshalAvailable => _strategy != null && _strategy.Available;
        internal static bool DrainReady => _strategy != null && _strategy.Drain.Available;
        internal static bool GameMarshalReady => _strategy != null && _strategy.Marshal.Available;
        internal static string StrategyNote => _strategy == null ? "not initialised"
                                             : (_strategy.Available ? "ready" : _strategy.Unavailable);

        internal static void Initialize()
        {
            // Awake runs on the Unity main thread on both halves. This is the authoritative
            // capture, and it must happen before any pump can fire.
            _mainThreadId = Thread.CurrentThread.ManagedThreadId;
            SelectStrategy();

            // No GameObject here, deliberately. One created during Awake is destroyed 135-219 ms
            // later, at Time.frameCount == 0, before the first scene loads, having received zero
            // Update calls and never reaching Start(). DontDestroyOnLoad does not save it: the
            // call appears to succeed (scene "DontDestroyOnLoad", handle -12) but does not bind,
            // because no scene is loaded yet, and a DDOL object and a plain one beside it die 1 ms
            // apart. Creating one here is what produced the "Update does not fire headless" lore.
            //
            // The subscription below is static state, which is exactly what DOES survive: Harmony
            // patches, sceneLoaded subscriptions and background threads registered in Awake all
            // keep working after the component is gone.
            InstallSceneLoadedHost();
        }

        /// <summary>
        ///     Arranges for the pump host to be created at the first scene load.
        ///
        ///     <para>
        ///     This is what covers BOOT, and boot is not a small window: <c>GameManager.Update</c>
        ///     is the steady-state primary but was measured at 0.11-0.16 calls per second until
        ///     <c>GameState.Running</c>, while frames advanced at 25 Hz throughout. Without a
        ///     second pump the control plane would be effectively frozen for the whole 80-90 s
        ///     boot, which is precisely when a caller is polling for readiness.
        ///     </para>
        ///
        ///     <para>
        ///     The FIRST callback is the one that matters: it arrives at 282 ms, still at
        ///     <c>Time.frameCount == 0</c>, and the object created there survives indefinitely and
        ///     misses nothing (Update 5867 at frame 5867, no gap). Waiting for the Base scene
        ///     instead puts the object at frame 1925 and loses everything before it.
        ///     </para>
        ///
        ///     <para>
        ///     The handler stays subscribed rather than unsubscribing after the first hit, so a
        ///     later scene load re-creates the host if it ever does go away. <see cref="EnsurePumpHost"/>
        ///     is idempotent, so the extra calls cost a null check.
        ///     </para>
        /// </summary>
        private static void InstallSceneLoadedHost()
        {
            if (_sceneHookInstalled) return;
            try
            {
                SceneManager.sceneLoaded += OnSceneLoaded;
                _sceneHookInstalled = true;
            }
            catch (Exception ex)
            {
                Plugin.Log?.LogError("could not subscribe to SceneManager.sceneLoaded, so the boot-window " +
                                     "pump will not exist and the control plane will be slow until " +
                                     "GameState.Running: " + ex.Message);
            }
        }

        private static void OnSceneLoaded(Scene scene, LoadSceneMode mode)
        {
            ScenesLoaded++;
            try { EnsurePumpHost(); }
            catch (Exception ex) { Plugin.Log?.LogError("pump host creation failed at scene load: " + ex.Message); }
        }

        /// <summary>
        ///     Picks the marshal, once, at load.
        ///
        ///     Both hosts get the same shape now: drain first, game marshal as backstop. The
        ///     shape is kept selectable because the two routes fail differently and a caller
        ///     needs to know which one is carrying its request.
        /// </summary>
        private static void SelectStrategy()
        {
            _strategy = new CompositePumpStrategy(new MainThreadDrainStrategy(), BuildDispatcherStrategy());
            Plugin.Log?.LogInfo("pump strategy: " + _strategy.Name + " (" + StrategyNote + ")");
        }

        private static IPumpStrategy BuildDispatcherStrategy()
        {
            try
            {
                var type = HarmonyLib.AccessTools.TypeByName("Assets.Scripts.Util.UnityMainThreadDispatcher")
                           ?? HarmonyLib.AccessTools.TypeByName("UnityMainThreadDispatcher");
                if (type == null)
                    return new DispatcherMarshalStrategy(null, null, "type not found in any loaded assembly");

                var exists = HarmonyLib.AccessTools.Method(type, "Exists");
                var instance = HarmonyLib.AccessTools.Method(type, "Instance");
                var enqueue = HarmonyLib.AccessTools.Method(type, "Enqueue", new[] { typeof(Action) });

                if (exists == null || instance == null || enqueue == null)
                    return new DispatcherMarshalStrategy(null, null,
                        "found the type but not Exists()/Instance()/Enqueue(Action)");

                Func<bool> existsFn = () => (bool)exists.Invoke(null, null);
                Action<Action> enqueueFn = work =>
                {
                    // Exists() first, always. Instance() throws rather than returning null before
                    // the game has built the marshal, which is the first ~35 s of boot.
                    if (!existsFn()) throw new InvalidOperationException("UnityMainThreadDispatcher does not exist yet");
                    object dispatcher = instance.Invoke(null, null);
                    if (dispatcher == null) throw new InvalidOperationException("UnityMainThreadDispatcher.Instance() returned null");
                    enqueue.Invoke(dispatcher, new object[] { work });
                };

                return new DispatcherMarshalStrategy(existsFn, enqueueFn, null);
            }
            catch (Exception ex)
            {
                return new DispatcherMarshalStrategy(null, null, ex.Message);
            }
        }

        /// <summary>
        ///     Creates the pump host if it is not there. Idempotent.
        ///
        ///     <para>
        ///     Two jobs: its <c>Update</c> drains the queue at about 25 Hz from frame 0, which is
        ///     the only thing covering the 80-90 s boot window, and it hosts the coroutine
        ///     <c>/screenshot</c> needs. Created on BOTH hosts, because the boot problem is worse
        ///     on the dedicated server, not absent from it.
        ///     </para>
        ///
        ///     <para>
        ///     Called from the first <c>SceneManager.sceneLoaded</c> callback, and opportunistically
        ///     from the <c>GameManager.Update</c> and <c>ImGuiManager.LateUpdate</c> postfixes as a
        ///     backstop. All three are static or game-owned. Never from <c>Awake</c>, where the
        ///     object dies at frame 0 before a scene exists, and never from this object itself,
        ///     because a destroyed object cannot recreate anything.
        ///     </para>
        /// </summary>
        internal static void EnsurePumpHost()
        {
            if (_pumpHost != null) return;

            var go = new GameObject("TestRig_MainThreadPump");
            // Valid here and not in Awake: DontDestroyOnLoad needs a loaded scene to move the
            // object into, and at Awake there is none, so the call silently fails to bind.
            DontDestroyOnLoad(go);
            _pumpHost = go.AddComponent<MainThreadPump>();
            InstanceCreations++;
            try { PumpHostCreatedAtFrame = Time.frameCount; }
            catch { }
            Plugin.Log?.LogInfo("pump host created at frame " + PumpHostCreatedAtFrame +
                                " (scene load " + ScenesLoaded + "); this is what pumps the control " +
                                "plane during boot, where GameManager.Update runs at well under 1 Hz");
        }

        internal static bool OnMainThread => Thread.CurrentThread.ManagedThreadId == _mainThreadId;

        /// <summary>Is there anything that can run main-thread work right now.</summary>
        internal static bool Alive => _strategy != null && _strategy.Available;

        /// <summary>
        ///     Reconciles the captured main-thread id from a callback that is main-thread by
        ///     construction. Awake is the primary capture and is correct on both halves, but if it
        ///     ever were not, every Unity-touching route would silently run on the wrong thread
        ///     and the drain guard would refuse forever. One log line and a correction is cheaper
        ///     than that failure.
        /// </summary>
        private static void NoteMainThread(string source)
        {
            int id = Thread.CurrentThread.ManagedThreadId;
            if (_mainThreadId == id) return;
            int previous = _mainThreadId;
            _mainThreadId = id;
            Plugin.Log?.LogWarning("main-thread id corrected from " + previous + " to " + id +
                                   " by " + source + "; the Awake capture was wrong");
        }

        private static object Run(Func<object> work, int timeoutMs, out bool timedOut, out string failure)
        {
            timedOut = false;
            failure = null;
            if (OnMainThread) return work();

            var item = new WorkItem { Work = work };

            if (_strategy == null)
            {
                failure = "the pump was never initialised";
                timedOut = true;
                return null;
            }

            if (!_strategy.Submit(() => Execute(item, _strategy.Name)))
            {
                failure = _strategy.Unavailable;
                timedOut = true;
                return null;
            }

            if (!item.Done.WaitOne(timeoutMs))
            {
                timedOut = true;
                if (!_strategy.Available) failure = _strategy.Unavailable;
                return null;
            }

            if (item.Error != null) throw item.Error;
            return item.Result;
        }

        private static void Execute(WorkItem item, string source)
        {
            // Reached only from a strategy that has put us on the main thread, so this is a
            // trustworthy sample of what the main thread's id actually is.
            NoteMainThread(source);
            ItemsRun++;
            try { item.Result = item.Work(); }
            catch (Exception ex) { item.Error = ex; }
            finally { try { item.Done.Set(); } catch { } }
        }

        /// <summary>
        ///     Runs <paramref name="work"/> on the Unity main thread and returns its response.
        ///     On timeout returns a 504 rather than hanging the harness.
        /// </summary>
        internal static HttpResponse RunSync(Func<HttpResponse> work, int timeoutMs)
        {
            if (work == null) return HttpResponse.Error("no work");
            try
            {
                bool timedOut;
                string failure;
                var r = Run(() => (object)work(), timeoutMs, out timedOut, out failure);
                if (timedOut) return TimeoutResponse(timeoutMs, failure);
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
            string failure;
            var r = Run(() => (object)work(), timeoutMs, out timedOut, out failure);
            if (timedOut)
                throw new TimeoutException("main thread did not run the work within " + timeoutMs + " ms" +
                                           (failure == null ? "" : ": " + failure));
            return r == null ? default(T) : (T)r;
        }

        internal static HttpResponse TimeoutResponse(int timeoutMs)
        {
            return TimeoutResponse(timeoutMs, null);
        }

        internal static HttpResponse TimeoutResponse(int timeoutMs, string failure)
        {
            // The strategy is named because the two routes fail differently and the remedies
            // differ. This is NOT a "the world is paused" message: measured, the main thread keeps
            // running at about 24 Hz and the game's marshal keeps executing enqueued work while
            // the world is paused with no client attached. A timeout here means the main thread
            // itself is not returning, not that the simulation is parked.
            string body =
                "timed out after " + timeoutMs + " ms waiting for the Unity main thread. " +
                (failure ?? "The main thread is not returning: the game may be on a modal dialog, " +
                            "generating a world, or loading.") +
                " host=" + HostProfile.Name +
                " pump=" + StrategyName +
                " drainReady=" + (DrainReady ? "true" : "false") +
                " marshalReady=" + (GameMarshalReady ? "true" : "false") +
                " hooks=[" + HookReport() + "]" +
                " mainThreadDrains=" + MainThreadDrains +
                " hostUpdateDrains=" + HostUpdateDrains +
                " scenesLoaded=" + ScenesLoaded +
                " hookCalls=" + FramesSeen +
                " itemsRun=" + ItemsRun +
                " offThreadRefused=" + OffThreadPumpsRefused +
                " lastPump=" + LastPumpSource +
                ". World generation is the known slow case: single-item latency was measured at " +
                "4238 ms and 4650 ms there against 4-37 ms once the game is up, so budget seconds " +
                "for a call that can land during a world create.";

            return HttpResponse.Error(body, 504);
        }

        /// <summary>Queues work with no result and does not wait.</summary>
        internal static void Post(Action work)
        {
            if (work == null) return;
            if (_strategy == null) return;
            var item = new WorkItem { Work = () => { work(); return null; } };
            _strategy.Submit(() => Execute(item, _strategy.Name));
        }

        internal static Coroutine RunCoroutine(IEnumerator routine)
        {
            if (_pumpHost == null) EnsurePumpHost();
            if (_pumpHost == null)
                throw new InvalidOperationException(
                    "no pump host. It is created at the first SceneManager.sceneLoaded callback, " +
                    "which arrives about 282 ms into the process, so this means the game has not " +
                    "loaded a scene yet. GET /status reports pumpHostCreatedAtFrame and scenesLoaded.");
            return _pumpHost.StartCoroutine(routine);
        }

        /// <summary>
        ///     Blocks the calling (non-main) thread until Time.frameCount reaches
        ///     <paramref name="target"/>. Used so an input endpoint only answers once the game has
        ///     actually had the frames in which to consume the synthetic input.
        ///
        ///     Frames, not simulation ticks. The two do not correspond on the dedicated server:
        ///     measured, GameManager.Update ran at about 24 Hz for 287 s while the tick count
        ///     stayed at 0. Anything scenario-shaped waits on <c>Dispatcher.TicksSeen</c> instead.
        ///     See Routes.Scenarios.cs.
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

        /// <summary>
        ///     The boot-window drain, and the only one that runs at frame rate before
        ///     <c>GameState.Running</c>.
        ///
        ///     This fires only because the object was created at the first scene load rather than
        ///     in <c>Awake</c>. An Awake-created object receives this callback exactly zero times
        ///     in its whole life, in every measured run, on the dedicated server.
        /// </summary>
        private void Update()
        {
            HostUpdateDrains++;
            FramesSeen++;
            FrameCount = Time.frameCount;
            Drain("HostUpdate");
        }

        private void OnDestroy()
        {
            // Nulled so the next sceneLoaded, GameManager.Update or ImGuiManager.LateUpdate
            // recreates it. The recreation always comes from outside this object, because a
            // destroyed one cannot run anything.
            if (ReferenceEquals(_pumpHost, this)) _pumpHost = null;
        }

        /// <summary>
        ///     The primary pump on both builds. Fires from a postfix on
        ///     <c>Assets.Scripts.GameManager.Update</c>.
        /// </summary>
        internal static void PumpFromGameManagerUpdate() => PumpFromMainThreadHook("GameManagerUpdate");

        /// <summary>
        ///     Supplementary client drain, from a postfix on <c>ImGuiManager.LateUpdate</c>. It
        ///     covers the splash window before GameManager is up. Never fires on the dedicated
        ///     server: the method is absent from that assembly.
        /// </summary>
        internal static void PumpFromFrame() => PumpFromMainThreadHook("Frame");

        private static void PumpFromMainThreadHook(string source)
        {
            if (_mainThreadId < 0) _mainThreadId = Thread.CurrentThread.ManagedThreadId;
            // Backstop only. The first sceneLoaded callback is what normally creates it, well
            // before either of these hooks is running at any useful rate.
            EnsurePumpHost();
            FramesSeen++;
            FrameCount = Time.frameCount;
            Drain(source);
        }

        /// <summary>
        ///     Fires from the ElectricityManager.ElectricityTick postfix. LIVENESS ONLY.
        ///
        ///     It deliberately does not set the main-thread id and cannot drain: this hook runs on
        ///     a UniTask ThreadPool worker whose id rotated across nine different values in three
        ///     measured runs and was never 1. Its only jobs are to record that the simulation is
        ///     actually ticking and to keep the frame counter warm.
        /// </summary>
        internal static void PumpFromFallback()
        {
            FallbackPumpUsed = true;
            // Time.frameCount off the main thread is tolerated here because it is a plain int read
            // used only for reporting and for WaitForFrame's coarse comparison. It was read from
            // this same hook in ClientDriver and is carried across unchanged.
            try { FrameCount = Time.frameCount; } catch { }
            Drain("Fallback");
        }

        /// <summary>
        ///     Runs whatever is queued, and ONLY when this is the Unity main thread.
        ///
        ///     The thread check is the fix described in the class comment, and the measurement
        ///     backs it: the ElectricityTick postfix never once ran on thread 1.
        /// </summary>
        private static void Drain(string source)
        {
            if (!OnMainThread)
            {
                OffThreadPumpsRefused++;
                LastPumpSource = source + "(offThread)";
                return;
            }

            MainThreadDrains++;

            while (true)
            {
                QueuedAction item;
                lock (_queue)
                {
                    if (_queue.Count == 0) return;
                    item = _queue.Dequeue();
                }

                LastPumpSource = source;
                try { item.Run(); }
                catch (Exception ex) { Plugin.Log?.LogError("queued work threw outside its own handler: " + ex); }
            }
        }
    }
}
