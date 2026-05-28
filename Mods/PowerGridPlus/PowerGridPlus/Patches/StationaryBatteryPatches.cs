// Derived from Re-Volt by Sukasa (https://github.com/sukasa/revolt), MIT License (Copyright (c) 2025 Sukasa).
// Power Grid Plus adjustments (2026-05-28):
//   1. Replaced the fraction-of-PowerMaximum rate caps with per-prefab absolute wattage caps
//      (Settings.StationBatteryChargeRate etc.) so the player sees the same number in-game as in
//      the BepInEx config.
//   2. Added a cable-headroom cap on charge and discharge: a single battery's contribution cannot
//      exceed its respective cable's MaxVoltage. Pass-through is zero for batteries (they only
//      store, never relay), so the cable cap is just the cable's MaxVoltage. In practice this is
//      vacuous at vanilla cable tiers (heavy = 100 kW vs the configured caps of 5..50 kW) but the
//      structure is there so a future low-tier cable or a player retune cannot blow the cable.

using System;
using Assets.Scripts.Networks;
using Assets.Scripts.Objects.Electrical;
using HarmonyLib;
using UnityEngine;

namespace PowerGridPlus.Patches
{
    /// <summary>
    ///     Per-prefab absolute charge / discharge wattage caps on stationary batteries, further
    ///     bounded by the respective cable's MaxVoltage. Plus the optional charge-efficiency loss.
    /// </summary>
    [HarmonyPatch(typeof(Battery))]
    public static class StationaryBatteryPatches
    {
        /// <summary>
        ///     Returns the per-prefab charge cap (W displayed) for this battery's PrefabName.
        ///     Unknown prefabs pass through uncapped (Infinity) so third-party Battery subclasses
        ///     are not silently throttled. The user-visible cap only ever tightens the result;
        ///     never widens it.
        /// </summary>
        internal static float GetChargeCap(Battery battery)
        {
            switch (battery.PrefabName)
            {
                case "StructureBattery":
                    return Settings.StationBatteryChargeRate.Value;
                case "StructureBatteryLarge":
                    return Settings.LargeBatteryChargeRate.Value;
                case "StationBatteryNuclear":
                    return Settings.NuclearBatteryChargeRate.Value;
                default:
                    return float.PositiveInfinity;
            }
        }

        /// <summary>
        ///     Returns the per-prefab discharge cap (W displayed) for this battery's PrefabName.
        ///     Unknown prefabs pass through uncapped (Infinity) so third-party Battery subclasses
        ///     are not silently throttled.
        /// </summary>
        internal static float GetDischargeCap(Battery battery)
        {
            switch (battery.PrefabName)
            {
                case "StructureBattery":
                    return Settings.StationBatteryDischargeRate.Value;
                case "StructureBatteryLarge":
                    return Settings.LargeBatteryDischargeRate.Value;
                case "StationBatteryNuclear":
                    return Settings.NuclearBatteryDischargeRate.Value;
                default:
                    return float.PositiveInfinity;
            }
        }

        /// <summary>
        ///     Effective per-tick charge cap = min(configured per-prefab cap, input cable MaxVoltage).
        ///     A battery has no pass-through, so the cable spare is just MaxVoltage.
        /// </summary>
        internal static float EffectiveChargeCap(Battery battery)
        {
            float cap = GetChargeCap(battery);
            if (cap <= 0f) return 0f;

            var cable = battery.InputConnection?.GetCable();
            if (cable == null) return cap;
            float maxVoltage = cable.MaxVoltage;
            if (maxVoltage <= 0f) return 0f;

            return Mathf.Min(cap, maxVoltage);
        }

        /// <summary>
        ///     Effective per-tick discharge cap = min(configured per-prefab cap, output cable MaxVoltage).
        /// </summary>
        internal static float EffectiveDischargeCap(Battery battery)
        {
            float cap = GetDischargeCap(battery);
            if (cap <= 0f) return 0f;

            var cable = battery.OutputConnection?.GetCable();
            if (cable == null) return cap;
            float maxVoltage = cable.MaxVoltage;
            if (maxVoltage <= 0f) return 0f;

            return Mathf.Min(cap, maxVoltage);
        }

        [HarmonyPostfix, HarmonyPatch(nameof(Battery.GetUsedPower))]
        public static void LimitMaxChargeRate(Battery __instance, ref float __result)
        {
            if (!Settings.EnableBatteryLimits.Value) return;
            __result = Mathf.Min(__result, EffectiveChargeCap(__instance));
        }

        [HarmonyPostfix, HarmonyPatch(nameof(Battery.GetGeneratedPower))]
        public static void LimitMaxDischargeRate(Battery __instance, ref float __result)
        {
            if (!Settings.EnableBatteryLimits.Value) return;
            __result = Mathf.Min(__result, EffectiveDischargeCap(__instance));
        }

        [HarmonyPrefix, HarmonyPatch(nameof(Battery.ReceivePower))]
        public static bool ChargeEfficiencyControl(Battery __instance, CableNetwork cableNetwork, float powerAdded)
        {
            // Master toggle off: let vanilla ReceivePower run unmodified.
            if (!Settings.EnableBatteryLimits.Value) return true;

            if (__instance.Error != 1 && __instance.OnOff && cableNetwork == __instance.InputNetwork && GetIsOperable(__instance))
            {
                float charged = Settings.BatteryChargeEfficiency.Value * powerAdded;
                if (charged < 500f)
                    charged = powerAdded;

                __instance.PowerStored = Mathf.Clamp(charged + __instance.PowerStored, 0f, __instance.PowerMaximum);
            }

            return false;
        }

        [HarmonyReversePatch, HarmonyPatch("get_IsOperable")]
        public static bool GetIsOperable(Battery __instance)
        {
            throw new NotImplementedException("Reverse patch -- replaced with Battery.get_IsOperable at runtime.");
        }
    }
}
