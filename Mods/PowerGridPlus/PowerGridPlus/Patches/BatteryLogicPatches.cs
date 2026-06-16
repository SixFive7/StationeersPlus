// Derived from Re-Volt by Sukasa (https://github.com/sukasa/revolt), MIT License (Copyright (c) 2025 Sukasa).
// Power Grid Plus (2026-06-09): the prior repurpose of vanilla ExportQuantity (31) / ImportQuantity (29)
// is REMOVED (breaking change for legacy IC10 scripts that read those slots on a battery). Replaced with
// the four dedicated PGP soft-power LogicTypes: MaxChargeSpeed / MaxDischargeSpeed (configured caps) and
// ChargeSpeed / DischargeSpeed (actual rate this tick after elastic allocation). See POWERTODO 0.2.7.2.

using Assets.Scripts.Objects.Electrical;
using Assets.Scripts.Objects.Motherboards;
using HarmonyLib;

namespace PowerGridPlus.Patches
{
    /// <summary>
    ///     Exposes a stationary battery's configured charge/discharge-rate caps (MaxChargeSpeed /
    ///     MaxDischargeSpeed) and the actual per-tick rates after elastic allocation (ChargeSpeed /
    ///     DischargeSpeed) as read-only logic values, in watts. ChargeSpeed / DischargeSpeed read the
    ///     soft-power caches; until the allocator's elastic pass populates them they report 0 (see
    ///     POWER_DEVIATIONS.md).
    /// </summary>
    [HarmonyPatch(typeof(Battery))]
    public static class BatteryLogicPatches
    {
        [HarmonyPrefix, HarmonyPatch(nameof(Battery.CanLogicRead))]
        public static bool CanLogicReadPatch(LogicType logicType, ref bool __result)
        {
            if (!Settings.EnableBatteryLogicAdditions.Value)
                return true;

            if (logicType == LogicTypeRegistry.MaxChargeSpeed
                || logicType == LogicTypeRegistry.MaxDischargeSpeed
                || logicType == LogicTypeRegistry.ChargeSpeed
                || logicType == LogicTypeRegistry.DischargeSpeed)
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

            if (logicType == LogicTypeRegistry.MaxChargeSpeed)
            {
                __result = StationaryBatteryPatches.GetChargeCap(__instance);
                return false;
            }
            if (logicType == LogicTypeRegistry.MaxDischargeSpeed)
            {
                __result = StationaryBatteryPatches.GetDischargeCap(__instance);
                return false;
            }
            if (logicType == LogicTypeRegistry.ChargeSpeed)
            {
                __result = SoftDemandShareCache.GetActualOrZero(__instance.ReferenceId);
                return false;
            }
            if (logicType == LogicTypeRegistry.DischargeSpeed)
            {
                __result = SoftSupplyShareCache.GetActualOrZero(__instance.ReferenceId);
                return false;
            }

            return true;
        }
    }
}
