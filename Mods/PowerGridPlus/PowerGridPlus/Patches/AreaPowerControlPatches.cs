// Derived from Re-Volt by Sukasa (https://github.com/sukasa/revolt), MIT License (Copyright (c) 2025 Sukasa).
// Power Grid Plus adjustments (2026-05-28):
//   1. Removed the UsePower patch from earlier PGP versions. That patch dropped the vanilla
//      `_powerProvided > 0` drain gate and caused the APC's internal cell to net-drain whenever
//      downstream load >= BatteryChargeRate. Vanilla UsePower is correct; we now leave it alone.
//   2. Replaced the hard-coded vanilla BatteryChargeRate field reference with the server-authoritative
//      `Settings.ApcBatteryChargeRate` config (default 1000 W displayed).
//   3. Added a cable-headroom cap on charge: this APC's charge contribution is capped at the
//      remaining MaxVoltage on the input cable after the APC's own downstream pass-through is
//      subtracted. A single APC can never blow its input cable just by charging.
//   4. Added a cable cap on output: this APC's generated power cannot exceed the output cable's
//      MaxVoltage. A single APC can never supply more than its output cable physically carries.

using System.Collections.Concurrent;
using Assets.Scripts.Networks;
using Assets.Scripts.Objects.Electrical;
using HarmonyLib;
using UnityEngine;

namespace PowerGridPlus.Patches
{
    /// <summary>
    ///     Closes the APC leak, applies the configured charge rate, and caps both charge demand and
    ///     output supply by the relevant cable's MaxVoltage so a single APC never blows the cable it
    ///     is wired to.
    /// </summary>
    [HarmonyPatch(typeof(AreaPowerControl))]
    public static class AreaPowerControlPatches
    {
        /// <summary>
        ///     Per-APC pass-through magnitude tracked from the previous power tick's UsePower call.
        ///     Vanilla UsePower(Output, powerUsed) supplies the exact downstream draw routed through
        ///     this APC last tick -- the value we need to subtract from input-cable MaxVoltage when
        ///     computing the charge headroom. CurrentLoad on the output network is wrong here
        ///     because it includes contributions from sibling suppliers on the same output bus.
        ///     ReferenceId keys the entry (stable across save/load, never reused); the dictionary
        ///     entry leaks if an APC is destroyed mid-session, but the leak is bounded by APC count.
        /// </summary>
        private static readonly ConcurrentDictionary<long, float> _lastPassthrough = new ConcurrentDictionary<long, float>();

        /// <summary>
        ///     Per-device charge cap: configured rate, further capped by the input cable's remaining
        ///     MaxVoltage after this APC's own downstream pass-through is subtracted. Returns 0 when
        ///     the cable is at or over MaxVoltage already from THIS APC's own contribution.
        /// </summary>
        internal static float ComputeChargeCap(AreaPowerControl apc)
        {
            float configCap = Settings.ApcBatteryChargeRate.Value;
            if (configCap <= 0f) return 0f;

            var inputCable = apc.InputConnection?.GetCable();
            if (inputCable == null) return configCap;
            float maxVoltage = inputCable.MaxVoltage;
            if (maxVoltage <= 0f) return 0f;

            // Per-APC pass-through: last tick's powerUsed routed through THIS APC. Falls back to 0
            // on the first tick after world load (no entry yet); that's the most permissive case
            // and self-corrects after one tick. Multi-APC output-network case: each APC tracks
            // only its own share, so the formula does not deadlock.
            float passthrough = 0f;
            _lastPassthrough.TryGetValue(apc.ReferenceId, out passthrough);
            float cableSpare = maxVoltage - passthrough;
            if (cableSpare <= 0f) return 0f;

            return Mathf.Min(configCap, cableSpare);
        }

        /// <summary>
        ///     Track per-APC downstream pass-through every tick. Vanilla UsePower(OutputNetwork, powerUsed)
        ///     is the canonical "downstream pulled this much from this APC this tick" signal.
        /// </summary>
        [HarmonyPostfix, HarmonyPatch(nameof(AreaPowerControl.UsePower))]
        public static void TrackPassthroughPatch(AreaPowerControl __instance, CableNetwork cableNetwork, float powerUsed)
        {
            if (__instance == null) return;
            if (__instance.OutputNetwork == null || cableNetwork != __instance.OutputNetwork) return;
            _lastPassthrough[__instance.ReferenceId] = powerUsed;
        }

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

            float chargeCap = ComputeChargeCap(__instance);
            float num = Mathf.Min(__instance.Battery.PowerDelta, chargeCap, powerAdded);
            if (num <= 0f) return false;
            __instance.Battery.PowerStored += num;
            ____powerProvided += num;
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
                {
                    float chargeCap = ComputeChargeCap(__instance);
                    usedPower += Mathf.Min(chargeCap, __instance.Battery.PowerDelta);
                }
            }

            __result = usedPower;
            return false;
        }

        /// <summary>
        ///     Cap APC output at the output cable's MaxVoltage. A single APC can never push more
        ///     than its output cable physically carries; if the cell + upstream potential together
        ///     could supply 50 kW but the cable is normal (5 kW), only 5 kW flows.
        /// </summary>
        [HarmonyPostfix, HarmonyPatch(nameof(AreaPowerControl.GetGeneratedPower))]
        public static void GetGeneratedPowerPatch(CableNetwork cableNetwork, AreaPowerControl __instance, ref float __result)
        {
            if (!Settings.EnableAreaPowerControlFix.Value) return;
            if (__instance.OutputNetwork == null || cableNetwork != __instance.OutputNetwork) return;
            if (__result <= 0f) return;

            var outputCable = __instance.OutputConnection?.GetCable();
            if (outputCable == null) return;
            float maxVoltage = outputCable.MaxVoltage;
            if (maxVoltage <= 0f) { __result = 0f; return; }

            if (__result > maxVoltage)
                __result = maxVoltage;
        }
    }
}
