using Assets.Scripts;
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
            }
            catch (System.Exception ex)
            {
                InspectorPlusPlugin.Log.LogError($"Headless force-unpause failed: {ex}");
            }
        }
    }
}
