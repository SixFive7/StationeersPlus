using Assets.Scripts.Networks;
using HarmonyLib;

namespace PowerGridPlus.Patches
{
    // Drives the global per-tick counter (ElectricityTickCounter) used by
    // TransformerAllocator's allocation cache and BrownoutRegistry's lockout-
    // expiry comparison.
    //
    // Harmony prefix on ElectricityManager.ElectricityTick fires once per
    // electricity tick (2 Hz at default tick speed). The prefix runs before any
    // CableNetwork.OnPowerTick, so the counter is advanced before any
    // TransformerAllocator.GetAllocatedSupply call this tick.
    [HarmonyPatch(typeof(ElectricityManager))]
    public static class ElectricityTickPatches
    {
        [HarmonyPrefix, HarmonyPatch(nameof(ElectricityManager.ElectricityTick))]
        public static void ElectricityTickPrefix()
        {
            ElectricityTickCounter.Advance();
            TransformerAllocator.TrimCache(ElectricityTickCounter.CurrentTick);
            TransformerAllocator.SyncShedTransitions(ElectricityTickCounter.CurrentTick);
        }
    }
}
