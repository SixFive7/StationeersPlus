// Derived from Re-Volt by Sukasa (https://github.com/sukasa/revolt), MIT License (Copyright (c) 2025 Sukasa).

using Assets.Scripts.Objects.Electrical;
using Assets.Scripts.Objects.Motherboards;
using HarmonyLib;

namespace PowerGridPlus.Patches
{
    /// <summary>
    ///     Exposes a stationary battery's charge-rate limit (Import Quantity) and discharge-rate limit
    ///     (Export Quantity) as logic values.
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
                    __result = __instance.PowerMaximum * Settings.MaxBatteryDischargeRate.Value;
                    return false;
                case LogicType.ImportQuantity:
                    __result = __instance.PowerMaximum * Settings.MaxBatteryChargeRate.Value;
                    return false;
                default:
                    return true;
            }
        }
    }
}
