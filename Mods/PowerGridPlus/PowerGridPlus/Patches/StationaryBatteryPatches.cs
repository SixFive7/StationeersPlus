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
    ///     bounded by the respective cable's MaxVoltage. The charge-cost loss itself is applied at
    ///     settlement in Core/WriteBack.
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
                case "StructureBatteryMedium":
                    return Settings.RocketBatteryMediumChargeRate.Value;
                case "StructureBatterySmall":
                    return Settings.RocketBatterySmallChargeRate.Value;
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
                case "StructureBatteryMedium":
                    return Settings.RocketBatteryMediumDischargeRate.Value;
                case "StructureBatterySmall":
                    return Settings.RocketBatterySmallDischargeRate.Value;
                default:
                    return float.PositiveInfinity;
            }
        }

        /// <summary>
        ///     Effective per-tick charge cap = min(configured per-prefab cap, input cable tier cap).
        ///     A battery has no pass-through, so the cable spare is just the tier cap. Cable caps come
        ///     from <see cref="CableMax"/> (runtime per-tier settings, NOT the serialized per-instance
        ///     MaxVoltage field; POWER.md §0.2 non-mutating decision).
        /// </summary>
        internal static float EffectiveChargeCap(Battery battery)
        {
            float cap = GetChargeCap(battery);
            if (cap <= 0f) return 0f;
            return Mathf.Min(cap, CableMax.For(battery.InputConnection?.GetCable()));
        }

        /// <summary>
        ///     Effective per-tick discharge cap = min(configured per-prefab cap, output cable tier cap).
        /// </summary>
        internal static float EffectiveDischargeCap(Battery battery)
        {
            float cap = GetDischargeCap(battery);
            if (cap <= 0f) return 0f;
            return Mathf.Min(cap, CableMax.For(battery.OutputConnection?.GetCable()));
        }

        [HarmonyPostfix, HarmonyPatch(nameof(Battery.GetUsedPower))]
        public static void LimitMaxChargeRate(Battery __instance, ref float __result)
        {
            // Clamp a non-finite value at the source before the Min logic (Mathf.Min(NaN, cap) would
            // silently return the cap instead of flagging the broken device).
            __result = DeviceOutputSanitizer.Sanitize(__result, __instance, generated: false);
            __result = Mathf.Min(__result, EffectiveChargeCap(__instance));
            // Soft charge demand (POWER.md §7.4): the allocator grants each battery a per-tick charge
            // share; the reported demand caps to it so vanilla never sees soft demand beyond what the
            // grid's firm residual covers. Falls back to the rate-capped value when no fresh share
            // exists (first ticks after load).
            if (__result > 0f && SoftDemandShareCache.TryGetShare(__instance.ReferenceId, out var share))
                __result = Mathf.Min(__result, share);
        }

        [HarmonyPostfix, HarmonyPatch(nameof(Battery.GetGeneratedPower))]
        public static void LimitMaxDischargeRate(Battery __instance, ref float __result)
        {
            __result = DeviceOutputSanitizer.Sanitize(__result, __instance, generated: true);
            __result = Mathf.Min(__result, EffectiveDischargeCap(__instance));
            // Elastic discharge (POWER.md §7.3.0.1): clamp to the allocator's per-tick share so the
            // battery delivers only the rigid shortfall it was allocated, never raw PowerStored.
            // GetShare returns float.MaxValue when no fresh share exists (fallback to vanilla).
            if (__result > 0f)
                __result = Mathf.Min(__result, SoftSupplyShareCache.GetShare(__instance.ReferenceId));
        }

        // The old ReceivePower charge-efficiency prefix is retired with vanilla ApplyState: the
        // write-back (Core/WriteBack) credits the battery its granted share with the same
        // charge-cost / sub-500 W trickle rule, and no vanilla delivery path calls
        // Battery.ReceivePower during the tick any more.

        [HarmonyReversePatch, HarmonyPatch("get_IsOperable")]
        public static bool GetIsOperable(Battery __instance)
        {
            throw new NotImplementedException("Reverse patch -- replaced with Battery.get_IsOperable at runtime.");
        }
    }
}
