using Assets.Scripts.Networks;
using BepInEx.Logging;
using HarmonyLib;

namespace PgpVerifyHelper
{
    /// <summary>
    ///     Postfix on <c>ElectricityManager.ElectricityTick</c>. This is the reliable
    ///     main-thread pump on a headless dedicated server, where <c>MonoBehaviour.Update</c>
    ///     does not fire after world load. Same pattern InspectorPlus uses for its
    ///     request poller. Pumping <see cref="ScenarioRunner"/> from here means scenario
    ///     code runs on the simulation thread when game state is in a coherent post-tick
    ///     state, which is what diagnostic snapshots want.
    /// </summary>
    [HarmonyPatch(typeof(ElectricityManager), nameof(ElectricityManager.ElectricityTick))]
    public static class ElectricityTickHook
    {
        public static void Postfix()
        {
            ScenarioRunner.OnElectricityTick();
        }
    }
}
