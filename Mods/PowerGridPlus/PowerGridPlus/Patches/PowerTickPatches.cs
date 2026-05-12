// Derived from Re-Volt by Sukasa (https://github.com/sukasa/revolt), MIT License (Copyright (c) 2025 Sukasa).

using Assets.Scripts.Networks;
using HarmonyLib;
using PowerGridPlus.Power;

namespace PowerGridPlus.Patches
{
    /// <summary>
    ///     Routes the three vanilla power-tick phases to <see cref="PowerGridTick"/> when the network has
    ///     one injected, and exposes the two private <see cref="PowerTick"/> helpers it still needs.
    /// </summary>
    [HarmonyPatch(typeof(PowerTick))]
    public static class PowerTickPatches
    {
        [HarmonyPrefix, HarmonyPatch(nameof(PowerTick.Initialise))]
        public static bool InitialisePatch(PowerTick __instance, CableNetwork cableNetwork)
        {
            if (!(__instance is PowerGridTick tick))
                return true;
            tick.Initialize_New(cableNetwork);
            return false;
        }

        [HarmonyPrefix, HarmonyPatch(nameof(PowerTick.CalculateState))]
        public static bool CalculateStatePatch(PowerTick __instance)
        {
            if (!(__instance is PowerGridTick tick))
                return true;
            tick.CalculateState_New();
            return false;
        }

        [HarmonyPrefix, HarmonyPatch(nameof(PowerTick.ApplyState))]
        public static bool ApplyStatePatch(PowerTick __instance)
        {
            if (!(__instance is PowerGridTick tick))
                return true;
            tick.ApplyState_New();
            return false;
        }

        [HarmonyReversePatch, HarmonyPatch("CacheState")]
        public static void CacheState(PowerTick _)
        {
            // Reverse patch -- body replaced with the original PowerTick.CacheState at runtime.
        }

        [HarmonyReversePatch, HarmonyPatch("CheckForRecursiveProviders")]
        public static void CheckForRecursiveProviders(PowerTick _)
        {
            // Reverse patch -- body replaced with the original PowerTick.CheckForRecursiveProviders at runtime.
        }
    }
}
