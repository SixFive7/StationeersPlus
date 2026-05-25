using Assets.Scripts.Networks;
using HarmonyLib;

namespace ScenarioRunner
{
    /// <summary>
    ///     Pumps the ScenarioRunner dispatcher from a Harmony postfix on
    ///     <c>ElectricityManager.ElectricityTick</c>.
    ///
    ///     Why ElectricityTick and not a more general hook: on a headless dedicated
    ///     server <c>MonoBehaviour.Update</c> does not reliably fire after world
    ///     load, and the only top-level <c>GameManager.GameTick</c> is an async
    ///     UniTask state machine that switches to a ThreadPool worker (which crashes
    ///     <c>UnityEngine.Object.FindObjectsOfType</c> calls; see
    ///     Research/Patterns/ThingEnumerationOffMainThread.md). The subsystem ticks
    ///     that GameTick fans out to are public static methods on the manager
    ///     classes (<c>ElectricityManager.ElectricityTick</c>,
    ///     <c>AtmosphericsManager</c> equivalent, etc), and they fire whenever
    ///     <c>GameManager.RunSimulation</c> is true. Patching ElectricityTick is
    ///     the convention <c>InspectorPlus</c> already uses for its request poller;
    ///     ScenarioRunner follows the same convention so the two cohabit cleanly.
    ///
    ///     If a future scenario needs to fire on an atmospheric tick instead (e.g.
    ///     to inspect Atmosphere or gas state between solver passes), add a second
    ///     Harmony patch class targeting that manager's tick driver and call
    ///     <see cref="Dispatcher.OnSimTick"/> from its postfix. The dispatcher
    ///     dedupes by frame, so multiple pumps converge to one probe call per
    ///     simulation frame.
    /// </summary>
    [HarmonyPatch(typeof(ElectricityManager), nameof(ElectricityManager.ElectricityTick))]
    public static class SimTickPump
    {
        public static void Postfix()
        {
            Dispatcher.OnSimTick();
        }
    }
}
