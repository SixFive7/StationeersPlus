// Derived from Re-Volt by Sukasa (https://github.com/sukasa/revolt), MIT License (Copyright (c) 2025 Sukasa).

using System;
using Assets.Scripts.Networks;
using Assets.Scripts.Objects.Electrical;
using HarmonyLib;
using UnityEngine;

namespace PowerGridPlus.Patches
{
    /// <summary>
    ///     Charge- and discharge-rate limits for stationary batteries, plus an optional charge-efficiency loss.
    /// </summary>
    [HarmonyPatch(typeof(Battery))]
    public static class StationaryBatteryPatches
    {
        [HarmonyPostfix, HarmonyPatch(nameof(Battery.GetUsedPower))]
        public static void LimitMaxChargeRate(Battery __instance, ref float __result)
        {
            if (Settings.EnableBatteryLimits.Value)
                __result = Mathf.Min(__result, __instance.PowerMaximum * Settings.MaxBatteryChargeRate.Value);
        }

        [HarmonyPostfix, HarmonyPatch(nameof(Battery.GetGeneratedPower))]
        public static void LimitMaxDischargeRate(Battery __instance, ref float __result)
        {
            if (Settings.EnableBatteryLimits.Value)
                __result = Mathf.Min(__result, __instance.PowerMaximum * Settings.MaxBatteryDischargeRate.Value);
        }

        [HarmonyPrefix, HarmonyPatch(nameof(Battery.ReceivePower))]
        public static bool ChargeEfficiencyControl(Battery __instance, CableNetwork cableNetwork, float powerAdded)
        {
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
