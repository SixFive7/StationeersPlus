using Assets.Scripts.Networks;
using Assets.Scripts.Objects.Electrical;
using HarmonyLib;
using Objects.Rockets;
using UnityEngine;

namespace PowerGridPlus.Patches
{
    /// <summary>
    ///     Observation brackets for the discharge-delivery audit (<see cref="DischargeDeliveryAudit"/>),
    ///     the UsePower mirror of <see cref="ChargeDeliveryObservationPatches"/>. The drained amount
    ///     is ARGUMENT-DERIVED, never a PowerStored field diff (same float32 quantization argument
    ///     as the charge side): <c>drained = min(powerUsed, PowerStored-before)</c>, replicating
    ///     vanilla's Clamp at 0 (a drain request beyond the store empties it and no more; the clamp
    ///     truncation is the truthful drain, never a seam by itself). The Priority.First prefix
    ///     snapshots the pre-call store; the Priority.Last postfix replicates the vanilla drain
    ///     gates, cited per site below, and records the result.
    ///
    ///     <para>Only calls on the store's OUTPUT network are recorded (NaN __state marks "do not
    ///     record"): vanilla's sole drain caller is PowerProvider.ApplyPower with the network the
    ///     provider advertised on, and the umbilical phase-2 cell-to-cell crossing mutates
    ///     PowerStored directly (Male 158629 / Female 158139 area: <c>base.PowerStored -= num</c>,
    ///     never a networked UsePower), so it can never enter a sum. Charge (ReceivePower) and the
    ///     battery atmos self-drain are different methods entirely: same-tick charge + discharge
    ///     on distinct networks disambiguates BY METHOD, by construction.</para>
    ///
    ///     <para>The APC has no bracket here: its cell drain is vanilla deferred ledger settlement
    ///     (see the DischargeDeliveryAudit class doc) and is deliberately out of the audit's
    ///     scope; ALLOCATE publishes no APC discharge grants either.</para>
    /// </summary>
    [HarmonyPatch]
    public static class DischargeDeliveryObservationPatches
    {
        // ---- Battery (station / rocket variants share the class) ----
        //
        // Drain gates replicated from 0.2.6403: vanilla Battery.UsePower drains
        // Clamp(PowerStored - powerUsed, 0, PowerMaximum) iff Error != 1 && OnOff &&
        // cableNetwork == OutputNetwork && IsOperable (decompile 392144-392150). PowerGridPlus
        // leaves Battery.UsePower unpatched, so the vanilla body is the only mutator.

        [HarmonyPrefix, HarmonyPatch(typeof(Battery), nameof(Battery.UsePower)), HarmonyPriority(Priority.First)]
        public static void BatteryBefore(Battery __instance, CableNetwork cableNetwork, out float __state)
        {
            __state = cableNetwork != null && cableNetwork == __instance.OutputNetwork
                ? __instance.PowerStored
                : float.NaN;
        }

        [HarmonyPostfix, HarmonyPatch(typeof(Battery), nameof(Battery.UsePower)), HarmonyPriority(Priority.Last)]
        public static void BatteryAfter(Battery __instance, float powerUsed, float __state)
        {
            if (float.IsNaN(__state) || powerUsed <= 0f) return;
            if (__instance.Error == 1 || !__instance.OnOff
                || !StationaryBatteryPatches.GetIsOperable(__instance)) return;   // vanilla drain gate
            ChargeGuardedRecord(__instance.ReferenceId, ChargeDeliveryAudit.KindBattery, powerUsed, __state);
        }

        // ---- Rocket umbilical halves (each half is its own buffered store) ----
        //
        // Drain gates replicated from 0.2.6403: the Male drains unless !OnOff (early return with
        // LastPowerRemoved = 0, 158628-158637; note: NO Error gate on the Male's UsePower); the
        // Female drains unconditionally (158143-158147). Both ignore the cableNetwork argument in
        // their bodies, so the output-network filter here is what scopes the sum to grid drains.

        [HarmonyPrefix, HarmonyPatch(typeof(RocketPowerUmbilicalMale), nameof(RocketPowerUmbilicalMale.UsePower)), HarmonyPriority(Priority.First)]
        public static void UmbilicalMaleBefore(RocketPowerUmbilicalMale __instance, CableNetwork cableNetwork, out float __state)
        {
            __state = cableNetwork != null && cableNetwork == __instance.OutputNetwork
                ? __instance.PowerStored
                : float.NaN;
        }

        [HarmonyPostfix, HarmonyPatch(typeof(RocketPowerUmbilicalMale), nameof(RocketPowerUmbilicalMale.UsePower)), HarmonyPriority(Priority.Last)]
        public static void UmbilicalMaleAfter(RocketPowerUmbilicalMale __instance, float powerUsed, float __state)
        {
            if (float.IsNaN(__state) || powerUsed <= 0f) return;
            if (!__instance.OnOff) return;   // vanilla Male drain gate (no Error gate on UsePower)
            ChargeGuardedRecord(__instance.ReferenceId, ChargeDeliveryAudit.KindUmbilical, powerUsed, __state);
        }

        [HarmonyPrefix, HarmonyPatch(typeof(RocketPowerUmbilicalFemale), nameof(RocketPowerUmbilicalFemale.UsePower)), HarmonyPriority(Priority.First)]
        public static void UmbilicalFemaleBefore(RocketPowerUmbilicalFemale __instance, CableNetwork cableNetwork, out float __state)
        {
            __state = cableNetwork != null && cableNetwork == __instance.OutputNetwork
                ? __instance.PowerStored
                : float.NaN;
        }

        [HarmonyPostfix, HarmonyPatch(typeof(RocketPowerUmbilicalFemale), nameof(RocketPowerUmbilicalFemale.UsePower)), HarmonyPriority(Priority.Last)]
        public static void UmbilicalFemaleAfter(RocketPowerUmbilicalFemale __instance, float powerUsed, float __state)
        {
            if (float.IsNaN(__state) || powerUsed <= 0f) return;
            // Female UsePower carries no gates at all (0.2.6403 decompile 158143-158147).
            ChargeGuardedRecord(__instance.ReferenceId, ChargeDeliveryAudit.KindUmbilical, powerUsed, __state);
        }

        private static void ChargeGuardedRecord(long refId, byte kind, float powerUsed, float storedBefore)
        {
            float stored = Mathf.Max(0f, storedBefore);
            DischargeDeliveryAudit.RecordDrain(refId, kind, Mathf.Min(powerUsed, stored));
        }
    }
}
