// Derived from Re-Volt by Sukasa (https://github.com/sukasa/revolt), MIT License (Copyright (c) 2025 Sukasa).
// Re-Volt's per-class load-gating (circuit breakers / load centers) is removed; this is the plain
// re-implementation of Device.AssessPower that cooperates with PowerGridTick's batch distribution.

using Assets.Scripts;
using Assets.Scripts.Networks;
using Assets.Scripts.Objects;
using Assets.Scripts.Objects.Pipes;
using HarmonyLib;
using PowerGridPlus.Power;
using UnityEngine;

namespace PowerGridPlus.Patches
{
    [HarmonyPatch(typeof(Device))]
    public static class DevicePatches
    {
        [HarmonyPrefix, HarmonyPatch("AssessPower")]
        public static bool AssessPower(CableNetwork cableNetwork, bool isOn, Device __instance)
        {
            if (cableNetwork == null || !isOn)
            {
                if (__instance.Powered)
                    SetPower(__instance, cableNetwork, false);
                return false;
            }

            // Only take over when the network is running our tick; otherwise leave the device untouched.
            if (!(cableNetwork.PowerTick is PowerGridTick))
                return false;

            float usedPower = __instance.GetUsedPower(cableNetwork);
            if (usedPower <= 0.0f)
                return false;

            if (usedPower > cableNetwork.EstimatedRemainingLoad)
            {
                cableNetwork.DuringTickLoad += Mathf.Min(usedPower, cableNetwork.EstimatedRemainingLoad);
                if (__instance.Powered)
                    SetPower(__instance, cableNetwork, false);
            }
            else
            {
                cableNetwork.DuringTickLoad += usedPower;
                if (!__instance.Powered)
                    SetPower(__instance, cableNetwork, true);
            }

            return false;
        }

        [HarmonyReversePatch, HarmonyPatch("SetPower")]
        public static void SetPower(Device instance, CableNetwork cableNetwork, bool hasPower)
        {
            // Reverse patch -- body replaced with the original Device.SetPower at runtime.
        }
    }
}
