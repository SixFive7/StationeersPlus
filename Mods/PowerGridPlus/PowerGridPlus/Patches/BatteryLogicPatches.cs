// Derived from Re-Volt by Sukasa (https://github.com/sukasa/revolt), MIT License (Copyright (c) 2025 Sukasa).
// Power Grid Plus adjustments (2026-05-28): ExportQuantity / ImportQuantity now return the effective
// per-prefab caps from StationaryBatteryPatches (absolute W, possibly reduced by cable MaxVoltage)
// rather than the old fraction-of-PowerMaximum value.

using Assets.Scripts.Objects.Electrical;
using Assets.Scripts.Objects.Motherboards;
using HarmonyLib;

namespace PowerGridPlus.Patches
{
    /// <summary>
    ///     Exposes a stationary battery's effective charge-rate limit (Import Quantity) and
    ///     discharge-rate limit (Export Quantity) as logic values, in displayed watts.
    /// </summary>
    [HarmonyPatch(typeof(Battery))]
    public static class BatteryLogicPatches
    {
        [HarmonyPrefix, HarmonyPatch(nameof(Battery.CanLogicRead))]
        public static bool CanLogicReadPatch(LogicType logicType, ref bool __result)
        {
            if (!Settings.EnableBatteryLogicAdditions.Value)
                return true;

            if (logicType == LogicType.ExportQuantity || logicType == LogicType.ImportQuantity)
            {
                __result = true;
                return false;
            }

            return true;
        }

        [HarmonyPrefix, HarmonyPatch(nameof(Battery.GetLogicValue))]
        public static bool GetLogicValuePatch(LogicType logicType, Battery __instance, ref double __result)
        {
            if (!Settings.EnableBatteryLogicAdditions.Value)
                return true;

            switch (logicType)
            {
                case LogicType.ExportQuantity:
                    // Configured cap (intent), not effective (capacity). A broken cable temporarily
                    // forces effective to 0; the IC10 reading should still reflect what the battery
                    // is designed to do so automation doesn't see a phantom 0.
                    __result = StationaryBatteryPatches.GetDischargeCap(__instance);
                    return false;
                case LogicType.ImportQuantity:
                    __result = StationaryBatteryPatches.GetChargeCap(__instance);
                    return false;
                default:
                    return true;
            }
        }
    }
}
