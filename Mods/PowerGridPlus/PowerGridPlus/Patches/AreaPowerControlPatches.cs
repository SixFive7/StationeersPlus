// Derived from Re-Volt by Sukasa (https://github.com/sukasa/revolt), MIT License (Copyright (c) 2025 Sukasa).
// Note: Re-Volt's source labels the first patch below as targeting AreaPowerControl.GetUsedPower while
// taking a `powerUsed` parameter, which only matches AreaPowerControl.UsePower. Power Grid Plus targets
// UsePower here, which is what the body implements.

using Assets.Scripts.Networks;
using Assets.Scripts.Objects.Electrical;
using HarmonyLib;
using UnityEngine;

namespace PowerGridPlus.Patches
{
    /// <summary>
    ///     Stops the Area Power Controller leaking a small amount of power and slowly draining its
    ///     battery when nothing downstream is drawing.
    /// </summary>
    [HarmonyPatch(typeof(AreaPowerControl))]
    public static class AreaPowerControlPatches
    {
        [HarmonyPrefix, HarmonyPatch(nameof(AreaPowerControl.ReceivePower))]
        public static bool ReceivePowerPatch(CableNetwork cableNetwork, float powerAdded, AreaPowerControl __instance, ref float ____powerProvided)
        {
            if (!Settings.EnableAreaPowerControlFix.Value)
                return true;

            if (__instance.InputNetwork == null || cableNetwork != __instance.InputNetwork)
                return false;

            powerAdded -= __instance.UsedPower;
            if (powerAdded <= 0.0f)
                return false;

            ____powerProvided -= powerAdded;

            if (____powerProvided >= 0.0f || !(bool)__instance.Battery || __instance.Battery.IsCharged)
                return false;

            float num = Mathf.Min(__instance.Battery.PowerDelta, __instance.BatteryChargeRate, powerAdded);
            __instance.Battery.PowerStored += num;
            ____powerProvided += num;
            return false;
        }

        [HarmonyPrefix, HarmonyPatch(nameof(AreaPowerControl.UsePower))]
        public static bool UsePowerPatch(CableNetwork cableNetwork, float powerUsed, AreaPowerControl __instance, ref float ____powerProvided)
        {
            if (!Settings.EnableAreaPowerControlFix.Value)
                return true;

            if (cableNetwork != __instance.OutputNetwork)
                return false;

            if ((bool)__instance.Battery && !__instance.Battery.IsEmpty)
            {
                float num = Mathf.Min(__instance.Battery.PowerStored, powerUsed);
                __instance.Battery.PowerStored -= num;
                powerUsed -= num;
            }

            ____powerProvided += powerUsed;
            return false;
        }

        [HarmonyPrefix, HarmonyPatch(nameof(AreaPowerControl.GetUsedPower))]
        public static bool GetUsedPowerPatch(CableNetwork cableNetwork, AreaPowerControl __instance, ref float __result, float ____powerProvided)
        {
            if (!Settings.EnableAreaPowerControlFix.Value)
                return true;

            __result = 0.0f;

            if (__instance.InputNetwork == null || cableNetwork != __instance.InputNetwork)
                return false;

            float usedPower = 0.0f;
            if (__instance.OnOff)
            {
                usedPower += __instance.UsedPower;

                if (__instance.OutputNetwork != null)
                    usedPower += ____powerProvided;

                if ((bool)__instance.Battery && !__instance.Battery.IsCharged)
                    usedPower += Mathf.Min(__instance.BatteryChargeRate, __instance.Battery.PowerDelta);
            }

            __result = usedPower;
            return false;
        }
    }
}
