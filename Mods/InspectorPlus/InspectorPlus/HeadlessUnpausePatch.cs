using System;
using System.Diagnostics;
using System.Reflection;
using Assets.Scripts;
using Assets.Scripts.GridSystem;
using Assets.Scripts.Serialization;
using Cysharp.Threading.Tasks;
using HarmonyLib;
using UnityEngine;

namespace InspectorPlus
{
    // Optional, opt-in (default off). On a headless batch-mode dedicated server,
    // force the simulation to run with no client connected so automated tooling
    // can capture request-file snapshots without a player joining.
    //
    // A vanilla dedicated server leaves the world paused until the first client
    // connects, and on headless neither Update() nor coroutines fire, so the
    // request pump (RequestPollOnTickPatch, driven by ElectricityTick) never runs
    // while paused. Unpausing lets the tick, and therefore request processing,
    // proceed.
    //
    // The one-shot unpause below is NOT sufficient on its own. StartGame is an
    // async UniTask method, so this postfix fires when the method returns its
    // task at the first await (NetworkServer.Host), not when the game is fully
    // started; late world-start code (or another mod) can silently re-pause
    // afterwards. WorldManager.SetGamePause logs nothing, and the prefab-wired
    // panel pause paths (e.g. Stationpedia.PauseGameToggle, gated on
    // Clients.Count == 0, which is exactly the headless no-client state) have no
    // code callers to patch reliably. HeadlessTickWatchdog below therefore
    // re-checks every few seconds and re-unpauses, and the [PauseTrace] patches
    // at the bottom (behind their own Enable Pause Trace Logging toggle, default
    // off) log a stack trace for every pause-state transition so a re-pauser is
    // identifiable from LogOutput.log.
    //
    // Gated twice so a normal player is never affected: the config toggle defaults
    // to false, and the unpause only runs under Application.isBatchMode. A client
    // or single-player session is never touched.
    [HarmonyPatch(typeof(GameManager), nameof(GameManager.StartGame))]
    public static class HeadlessUnpausePatch
    {
        public static void Postfix()
        {
            if (!InspectorPlusPlugin.ForceUnpauseWhenHeadless.Value) return;
            if (!Application.isBatchMode) return;
            try
            {
                global::WorldManager.SetGamePause(pauseGame: false);
                GameManager.UnpauseGameTick();
                InspectorPlusPlugin.Log.LogInfo("Headless force-unpause applied (no-client snapshot testing).");
                HeadlessTickWatchdog.EnsureStarted();
            }
            catch (System.Exception ex)
            {
                InspectorPlusPlugin.Log.LogError($"Headless force-unpause failed: {ex}");
            }
        }
    }

    // Periodic tick watchdog for headless batch-mode servers. Every 5 seconds it
    // logs the pause-relevant state (GameState, IsGamePaused, GameTickPaused,
    // RunSimulation, GameTickCount, client count), and if the simulation is
    // parked while the world is running with no client connected, it re-applies
    // the unpause and logs the before/after flags.
    //
    // Pump choice: MonoBehaviour.Update and coroutines do not fire reliably on
    // the headless server (see RequestPollOnTickPatch), and the ElectricityTick
    // pump only runs while the simulation is NOT parked, which is exactly the
    // state this watchdog must recover from. The UniTask player loop does run
    // headless (the game's own GameTick pause loop parks in UniTask.Delay on
    // PlayerLoopTiming.Update), so a UniTask.Delay loop is a reliable main-thread
    // pump that works while the tick is parked.
    //
    // Safety gates, checked every cycle:
    //   - config toggle on, batch mode only (same double gate as the one-shot),
    //   - GameState must be Running (never unpause mid world load; LoadWorld and
    //     NewAsync legitimately hold SetGamePause(true) while Joining),
    //   - no client connected (a joining client legitimately pauses the world;
    //     with a player in the world pause handling belongs to them),
    //   - SaveHelper must not be mid-save (PrepareToSave parks the tick on
    //     purpose while GetWorldData snapshots the world; unpausing there would
    //     race the save; IsSaving is private, read once via reflection).
    internal static class HeadlessTickWatchdog
    {
        private const int IntervalMs = 5000;

        private static bool _started;

        // SaveHelper.IsSaving is a private static property; resolve its getter
        // once. Null if the game ever renames it, in which case the watchdog
        // errs on the side of skipping the save gate (logged at start).
        private static readonly PropertyInfo IsSavingProperty =
            AccessTools.Property(typeof(SaveHelper), "IsSaving");

        internal static void EnsureStarted()
        {
            if (_started) return;
            _started = true;
            if (IsSavingProperty == null)
            {
                InspectorPlusPlugin.Log.LogWarning("[TickWatchdog] SaveHelper.IsSaving not found; save gate disabled.");
            }
            InspectorPlusPlugin.Log.LogInfo($"[TickWatchdog] started (every {IntervalMs / 1000}s).");
            WatchLoop().Forget();
        }

        private static async UniTaskVoid WatchLoop()
        {
            while (true)
            {
                await UniTask.Delay(IntervalMs, DelayType.UnscaledDeltaTime, PlayerLoopTiming.Update);
                try
                {
                    Tick();
                }
                catch (System.Exception ex)
                {
                    InspectorPlusPlugin.Log.LogError($"[TickWatchdog] cycle failed: {ex}");
                }
            }
        }

        private static void Tick()
        {
            if (!InspectorPlusPlugin.ForceUnpauseWhenHeadless.Value) return;
            if (!Application.isBatchMode) return;

            bool isGamePaused = global::WorldManager.IsGamePaused;
            bool gameTickPaused = GameManager.GameTickPaused;
            int clients = NetworkBase.Clients.Count;
            InspectorPlusPlugin.Log.LogInfo(
                $"[TickWatchdog] GameState={GameManager.GameState} IsGamePaused={isGamePaused} " +
                $"GameTickPaused={gameTickPaused} RunSimulation={GameManager.RunSimulation} " +
                $"GameTickCount={GameManager.GameTickCount} Clients={clients}");

            if (GameManager.GameState != GameState.Running) return;
            if (clients > 0) return;
            if (!isGamePaused && !gameTickPaused) return;
            if (IsSavingProperty != null && (bool)IsSavingProperty.GetValue(null))
            {
                InspectorPlusPlugin.Log.LogInfo("[TickWatchdog] parked but SaveHelper.IsSaving; leaving alone.");
                return;
            }

            global::WorldManager.SetGamePause(pauseGame: false);
            GameManager.UnpauseGameTick();
            InspectorPlusPlugin.Log.LogWarning(
                $"[TickWatchdog] re-unpaused: IsGamePaused {isGamePaused}->{(global::WorldManager.IsGamePaused)}, " +
                $"GameTickPaused {gameTickPaused}->{GameManager.GameTickPaused}");
        }
    }

    // The identified re-pauser (0.2.6403.27689): the DEDICATED SERVER build of
    // GameManager.StartGame ends with DelayedStartupPause().Forget(), and
    //
    //     private static async UniTaskVoid DelayedStartupPause()
    //     {
    //         await UniTask.Delay(5000, DelayType.UnscaledDeltaTime);
    //         if (NetworkBase.Clients.Count <= 0)
    //         {
    //             WorldManager.SetGamePause(pauseGame: true);
    //         }
    //     }
    //
    // pauses the world 5 seconds after StartGame whenever no client is connected,
    // ignoring AutoPauseServer. The method exists ONLY in the server assembly
    // (rocketstation_DedicatedServer_Data), not in the client build, hence the
    // Prepare() guard: on a client install the target is absent and the patch
    // class is skipped cleanly instead of failing PatchAll. Skipping the stub of
    // an async UniTaskVoid method prevents the state machine from ever starting;
    // the caller's .Forget() on the default struct is a no-op.
    //
    // The watchdog above stays as the safety net for any OTHER silent pauser
    // (console pause command, prefab-wired panel pause paths, third-party mods).
    [HarmonyPatch]
    public static class DelayedStartupPauseSkipPatch
    {
        private static System.Reflection.MethodBase TargetMethod()
        {
            return AccessTools.Method(typeof(GameManager), "DelayedStartupPause");
        }

        private static bool Prepare()
        {
            return AccessTools.Method(typeof(GameManager), "DelayedStartupPause") != null;
        }

        public static bool Prefix()
        {
            if (!InspectorPlusPlugin.ForceUnpauseWhenHeadless.Value) return true;
            if (!Application.isBatchMode) return true;
            InspectorPlusPlugin.Log.LogInfo("[TickWatchdog] skipped GameManager.DelayedStartupPause (headless force-unpause active).");
            return false;
        }
    }

    // Evidence tracer: WorldManager.SetGamePause logs nothing in vanilla, so a
    // silent re-pause is invisible in LogOutput.log. Log every actual transition
    // (the method self-no-ops when the value does not change) with a stack trace
    // naming the caller. Batch-mode gated like everything else here, but on its
    // own Enable Pause Trace Logging toggle (default off) instead of the
    // force-unpause toggle: the traces are a developer diagnostic and get noisy
    // around autosaves (every save parks and resumes the tick), so the
    // force-unpause can run quietly and this flips on only while hunting a
    // re-pauser.
    [HarmonyPatch(typeof(global::WorldManager), nameof(global::WorldManager.SetGamePause))]
    public static class SetGamePauseTracePatch
    {
        public static void Prefix(bool pauseGame)
        {
            if (!InspectorPlusPlugin.EnablePauseTraceLogging.Value) return;
            if (!Application.isBatchMode) return;
            if (global::WorldManager.IsGamePaused == pauseGame) return;
            var trace = new StackTrace(1, false).ToString();
            if (pauseGame)
            {
                InspectorPlusPlugin.Log.LogWarning($"[PauseTrace] SetGamePause(true) from:\n{trace}");
            }
            else
            {
                InspectorPlusPlugin.Log.LogInfo($"[PauseTrace] SetGamePause(false) from:\n{trace}");
            }
        }
    }

    // Same tracer, same Enable Pause Trace Logging + batch-mode gate, for the
    // tick-level pause latch. PauseGameTick only schedules the pause (the
    // GameTick loop latches it at the next pass), but the caller is what
    // matters for evidence, so log every call.
    [HarmonyPatch(typeof(GameManager), nameof(GameManager.PauseGameTick))]
    public static class PauseGameTickTracePatch
    {
        public static void Prefix()
        {
            if (!InspectorPlusPlugin.EnablePauseTraceLogging.Value) return;
            if (!Application.isBatchMode) return;
            InspectorPlusPlugin.Log.LogWarning($"[PauseTrace] PauseGameTick() from:\n{new StackTrace(1, false)}");
        }
    }

    [HarmonyPatch(typeof(GameManager), nameof(GameManager.UnpauseGameTick))]
    public static class UnpauseGameTickTracePatch
    {
        public static void Prefix()
        {
            if (!InspectorPlusPlugin.EnablePauseTraceLogging.Value) return;
            if (!Application.isBatchMode) return;
            InspectorPlusPlugin.Log.LogInfo($"[PauseTrace] UnpauseGameTick() from:\n{new StackTrace(1, false)}");
        }
    }
}
