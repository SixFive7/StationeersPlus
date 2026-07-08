using Assets.Scripts.Networks;
using Assets.Scripts.Objects.Electrical;
using HarmonyLib;
using Objects.Rockets;
using UnityEngine;

namespace PowerGridPlus.Patches
{
    /// <summary>
    ///     Observation brackets for the charge-delivery audit (<see cref="ChargeDeliveryAudit"/>).
    ///     The credited amount is ARGUMENT-DERIVED, not a PowerStored field diff: the store fields
    ///     are float32, so at a 230 MJ nuclear bank one ulp is 16 J and a genuine trickle credit
    ///     quantizes to multiples of 16 in the field (39.37 W landed as 32, 25.58 as 32, 0.67 as
    ///     0 in the 13c soak), which is storage physics, not a delivery seam. The truthful credit
    ///     is what the implementation adds before the store rounds it:
    ///     <c>credited = min(delivered-after-adjustments, headroom-before)</c>, where the min is
    ///     the vanilla Clamp at PowerMaximum (a clamp truncation on a near-full store is
    ///     legitimate and reported as the truthful credit, never as a seam). headroom-before is
    ///     the Priority.First prefix's snapshot; the Priority.Last postfix reads the FINAL
    ///     argument value (after any prefix modification, e.g. the umbilical quiescent burn) and
    ///     replicates the implementation's own credit gates, cited per site below.
    ///
    ///     <para>Only calls on the store's INPUT network are recorded (NaN __state marks "do not
    ///     record"): the umbilical phase-2 crossing passes a null network, and discharge /
    ///     atmos-drain are different methods. The APC has no bracket here: its credit is computed
    ///     by PowerGridPlus's own ReceivePowerPatch, which records the exact amount at the source
    ///     (AreaPowerControlPatches); when that fix master is off, vanilla owns the APC path and
    ///     the audit skips APC entries.</para>
    /// </summary>
    [HarmonyPatch]
    public static class ChargeDeliveryObservationPatches
    {
        // ---- Battery (station / rocket variants share the class) ----
        //
        // Credit gates replicated from 0.2.6403: vanilla Battery.ReceivePower credits
        // Clamp(powerAdded + PowerStored, 0, PowerMaximum) iff Error != 1 && OnOff &&
        // cableNetwork == InputNetwork && IsOperable (decompile 392152-392158);
        // StationaryBatteryPatches.ChargeEfficiencyControl (EnableBatteryLimits) uses the same
        // gates and credits eff * powerAdded, falling back to the full amount below 500 W
        // (its vanilla-derived small-chunk exemption).

        [HarmonyPrefix, HarmonyPatch(typeof(Battery), nameof(Battery.ReceivePower)), HarmonyPriority(Priority.First)]
        public static void BatteryBefore(Battery __instance, CableNetwork cableNetwork, out float __state)
        {
            __state = cableNetwork != null && cableNetwork == __instance.InputNetwork
                ? __instance.PowerStored
                : float.NaN;
        }

        [HarmonyPostfix, HarmonyPatch(typeof(Battery), nameof(Battery.ReceivePower)), HarmonyPriority(Priority.Last)]
        public static void BatteryAfter(Battery __instance, float powerAdded, float __state)
        {
            if (float.IsNaN(__state) || powerAdded <= 0f) return;
            if (__instance.Error == 1 || !__instance.OnOff
                || !StationaryBatteryPatches.GetIsOperable(__instance)) return;

            float credited = powerAdded;
            if (Settings.EnableBatteryLimits != null && Settings.EnableBatteryLimits.Value)
            {
                float charged = Settings.BatteryChargeEfficiency.Value * powerAdded;
                credited = charged < 500f ? powerAdded : charged;
            }
            float headroom = Mathf.Max(0f, __instance.PowerMaximum - __state);
            ChargeDeliveryAudit.RecordCredit(__instance.ReferenceId, ChargeDeliveryAudit.KindBattery,
                Mathf.Min(credited, headroom));
        }

        // ---- Rocket umbilical halves (each half is its own buffered store) ----
        //
        // Credit gates replicated from 0.2.6403: the Male credits unless Error == 1 || !OnOff
        // (158639-158648); the Female credits unconditionally (158149-158153); both are bare
        // Clamp adds and both ignore the cableNetwork argument, so the input-network filter here
        // is what scopes the sum to grid charge. powerAdded is read AFTER the quiescent-burn
        // prefix (RocketUmbilicalPatches) shrank it: the burned quiescent is consumption, not
        // charge.

        [HarmonyPrefix, HarmonyPatch(typeof(RocketPowerUmbilicalMale), nameof(RocketPowerUmbilicalMale.ReceivePower)), HarmonyPriority(Priority.First)]
        public static void UmbilicalMaleBefore(RocketPowerUmbilicalMale __instance, CableNetwork cableNetwork, out float __state)
        {
            __state = cableNetwork != null && cableNetwork == __instance.InputNetwork
                ? __instance.PowerStored
                : float.NaN;
        }

        [HarmonyPostfix, HarmonyPatch(typeof(RocketPowerUmbilicalMale), nameof(RocketPowerUmbilicalMale.ReceivePower)), HarmonyPriority(Priority.Last)]
        public static void UmbilicalMaleAfter(RocketPowerUmbilicalMale __instance, float powerAdded, float __state)
        {
            if (float.IsNaN(__state) || powerAdded <= 0f) return;
            if (__instance.Error == 1 || !__instance.OnOff) return;   // vanilla Male credit gate
            float headroom = Mathf.Max(0f, __instance.PowerMaximum - __state);
            ChargeDeliveryAudit.RecordCredit(__instance.ReferenceId, ChargeDeliveryAudit.KindUmbilical,
                Mathf.Min(powerAdded, headroom));
        }

        [HarmonyPrefix, HarmonyPatch(typeof(RocketPowerUmbilicalFemale), nameof(RocketPowerUmbilicalFemale.ReceivePower)), HarmonyPriority(Priority.First)]
        public static void UmbilicalFemaleBefore(RocketPowerUmbilicalFemale __instance, CableNetwork cableNetwork, out float __state)
        {
            __state = cableNetwork != null && cableNetwork == __instance.InputNetwork
                ? __instance.PowerStored
                : float.NaN;
        }

        [HarmonyPostfix, HarmonyPatch(typeof(RocketPowerUmbilicalFemale), nameof(RocketPowerUmbilicalFemale.ReceivePower)), HarmonyPriority(Priority.Last)]
        public static void UmbilicalFemaleAfter(RocketPowerUmbilicalFemale __instance, float powerAdded, float __state)
        {
            if (float.IsNaN(__state) || powerAdded <= 0f) return;
            float headroom = Mathf.Max(0f, __instance.PowerMaximum - __state);
            ChargeDeliveryAudit.RecordCredit(__instance.ReferenceId, ChargeDeliveryAudit.KindUmbilical,
                Mathf.Min(powerAdded, headroom));
        }
    }
}
