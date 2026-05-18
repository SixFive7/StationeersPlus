using Assets.Scripts.Networks;
using HarmonyLib;

namespace InspectorPlus
{
    // Pumps the InspectorPlus request-file scan from a Harmony postfix on
    // ElectricityManager.ElectricityTick.
    //
    // The dedicated server's headless main loop does NOT reliably drive
    // MonoBehaviour.Update() or Unity coroutines:
    //
    //   - Plugin.Update() never fires post-load. Its Poll() never runs.
    //   - MainThreadDispatcher (a DontDestroyOnLoad MonoBehaviour) does not get
    //     its Update() called either, so its coroutine PollLoop never advances
    //     past its first WaitForSecondsRealtime yield and the enqueued work
    //     submitted by the FileSystemWatcher never executes.
    //
    // ElectricityTick, in contrast, is driven by the game's own simulation
    // tick (see DishProbe.ElectricityTickProbe -- once StartGameUnpauseProbe
    // force-unpauses after StartGame, ElectricityTick fires every tick of the
    // simulation: empirically Tick #1, #2, #3, #60, #120, #180, ... on a
    // dedicated server with no client connected). It is on the main thread,
    // so request processing (which calls into Unity / scene objects via
    // ObjectWalker) is safe.
    //
    // We throttle the scan to every PollEveryNTicks ticks to keep overhead
    // negligible (a Directory.GetFiles is cheap, but no need to do it every
    // simulation tick when human-driven request drops are seconds apart).
    [HarmonyPatch(typeof(ElectricityManager), nameof(ElectricityManager.ElectricityTick))]
    public static class RequestPollOnTickPatch
    {
        private const int PollEveryNTicks = 2;
        private static int _tickCounter;

        public static void Postfix()
        {
            _tickCounter++;
            if (_tickCounter < PollEveryNTicks) return;
            _tickCounter = 0;
            InspectorPlusPlugin.ProcessPendingRequests();
        }
    }
}
