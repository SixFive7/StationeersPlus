using Assets.Scripts.Networks;
using Assets.Scripts.Objects.Electrical;
using HarmonyLib;
using JetBrains.Annotations;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

namespace PowerTransmitterPlus
{
    // Replaces vanilla's distance-based capacity derate on PowerTransmitter.
    //
    // Vanilla model:
    //   delivered_max = 5000 - distance * 10
    //   source_draw   = delivered
    //
    // New model (this mod):
    //   delivered_max = Max Transfer Capacity config  (0 = unlimited; default unlimited)
    //   source_draw   = delivered * (1 + k * distance_m / 1000)
    //
    // Where k is the configurable per-km overhead factor and the delivery cap is
    // a separate server-authoritative setting (MaxCapacityConfigSync). The vanilla
    // PowerTransmitter.MaxPowerTransmission constant (5000) is no longer used as the
    // delivery ceiling; it survives as the beam visualizer's full-brightness
    // reference in ReceivePower (4) below and as the debt-ceiling fallback when
    // the cap is unlimited, so removing the cap does not dim beams.
    //
    // Implementation hinges on PowerTransmitter._powerProvided, the private
    // float "debt accumulator" between the wireless-output tick and the
    // source-input tick:
    //   - UsePower(WirelessOutputNetwork, delivered):  _powerProvided += delivered
    //   - GetUsedPower(InputNetwork):                  returns _powerProvided
    //   - ReceivePower(InputNetwork, paid):            _powerProvided -= paid
    //
    // We inflate the debt at UsePower time so that the source pays
    // delivered * multiplier instead of just delivered. We also lift the
    // MaxPowerTransmission cap on GetUsedPower so the inflated debt can be
    // settled in one tick. Finally, we override VisualizerIntensity in
    // ReceivePower to reflect *delivered*, not *source_draw*, so the visualizer
    // remains a meaningful "throughput" indicator instead of saturating at
    // 1/multiplier on long beams.
    //
    // Cross-mod billing ownership (ModApi): a power-allocator mod can claim
    // wireless billing via ModApi.ClaimBillingOwnership. While a claim is
    // held, the two debt-billing patches (2) and (3) and the standalone debt
    // ceiling in (1) stand down so the owner bills the source itself; the
    // advertise value in (1) (the capacity definition other mods clamp), the
    // receiver drain-cap lift (5), the visualizer fix (4), and the link
    // patches stay active regardless of ownership.
    //
    // Standalone debt ceiling (active only with no billing owner): whenever
    // the source network cannot cover delivered * multiplier, the unpaid debt
    // grows every tick and the lifted bill (3) browns out co-located
    // consumers while the link keeps delivering. The advertise prefix (1)
    // therefore pauses delivery (advertises 0) while the transmitter's debt
    // is at or above ceiling = effectiveCap * multiplier * 4, and resumes
    // once the source pays it down. Stateless self-limiting duty cycle; the
    // same bound also keeps the lump bill after an OnOff cycle finite.
    //
    // Receiver drain-cap lift (5) (ALWAYS active): vanilla
    // PowerReceiver.GetUsedPower bills the wireless network
    // Min(MaxPowerTransmission + UsedPower, _powerProvided). With deliveries
    // above 5 kW (possible once (1) lifts the advertise) the receiver's debt
    // grows without bound and the excess never reaches the transmitter, so
    // the source is never billed for it (free energy). The lift keeps the
    // receiver's wireless drain in step with the effective delivery cap.
    public static class DistanceCostShared
    {
        internal static readonly FieldInfo PowerProvidedField =
            AccessTools.Field(typeof(PowerTransmitter), "_powerProvided");

        // PowerReceiver declares its own private _powerProvided (the two
        // halves do not share a base-class field), so the receiver needs a
        // separate FieldInfo.
        internal static readonly FieldInfo ReceiverPowerProvidedField =
            AccessTools.Field(typeof(PowerReceiver), "_powerProvided");

        internal static readonly FieldInfo LinkedDistanceField =
            AccessTools.Field(typeof(PowerTransmitter), "_linkedReceiverDistance");

        // Picks the _powerProvided FieldInfo matching the concrete half of a
        // wireless link. Null for anything that is not a transmitter or a
        // receiver.
        internal static FieldInfo PowerProvidedFieldFor(WirelessPower half)
        {
            if (half is PowerTransmitter) return PowerProvidedField;
            if (half is PowerReceiver) return ReceiverPowerProvidedField;
            return null;
        }

        // WirelessOutputNetwork lives on WirelessPower (or PowerTransmitter).
        // Field-or-property; either path works via Traverse, which is what
        // we use at the call site below.
        internal static CableNetwork GetWirelessOutputNetwork(PowerTransmitter t)
        {
            return Traverse.Create(t).Field("WirelessOutputNetwork").GetValue<CableNetwork>()
                ?? Traverse.Create(t).Property("WirelessOutputNetwork").GetValue<CableNetwork>();
        }

        internal static float GetMultiplier(PowerTransmitter t)
        {
            if (t == null || LinkedDistanceField == null) return 1f;
            var distance = (float)LinkedDistanceField.GetValue(t);
            // GetEffectiveK() returns local config on host/single-player and
            // the host-pushed synced value on clients. Simulation patches only
            // run server-side so they always see the host's value; logic
            // readouts on clients see the synced value too, so display matches.
            var k = DistanceConfigSync.GetEffectiveK();
            if (k <= 0f) return 1f;
            var m = 1f + k * distance / 1000f;
            return m < 1f ? 1f : m;
        }

        // Legacy public cross-mod accessor, kept because released PowerGridPlus
        // builds resolve it via reflection to model the inflated source-side
        // draw of a transmitter pair (input_draw = delivered * factor). New
        // integrations should use ModApi (ModApi.SourceDrawMultiplier and
        // friends), which this forwards to; the forward includes the
        // unlinked -> 1 gate, so a stale cached distance left behind by a
        // dropped link no longer leaks into the multiplier. Returns >= 1; 1
        // means no distance overhead (unlinked, vanilla-equivalent, or k <= 0).
        // Stable API: do not rename or remove.
        public static float SourceDrawMultiplier(PowerTransmitter t) => ModApi.SourceDrawMultiplier(t);
    }

    // (1) Drop the distance-based capacity derate. Returns the un-derated cap,
    //     or 0 while the standalone debt ceiling is pausing the link.
    [HarmonyPatch(typeof(PowerTransmitter), nameof(PowerTransmitter.GetGeneratedPower))]
    public static class GeneratedPowerNoDistanceDeratePatch
    {
        // One LogWarning per transmitter per pause episode. Membership means
        // "the pause warning for the current episode has been logged"; the
        // episode ends (and the id is dropped) once the debt falls below half
        // the ceiling, so the next pause logs again. Guarded by a lock: the
        // advertise runs on the power-tick worker and the set is process-wide.
        private static readonly object DebtPauseLock = new object();
        private static readonly HashSet<long> DebtPauseWarned = new HashSet<long>();

        [UsedImplicitly]
        public static bool Prefix(PowerTransmitter __instance, CableNetwork cableNetwork, ref float __result)
        {
            if (__instance.OutputNetwork == null
                || __instance.Error == 1
                || cableNetwork != __instance.OutputNetwork)
            {
                __result = 0f;
                return false;
            }
            if (!__instance.OnOff || __instance.InputNetwork == null)
            {
                __result = 0f;
                return false;
            }

            // Delivery cap. 0 = unlimited (default): deliver whatever the input
            // network can supply, no artificial ceiling. A positive value clamps
            // delivered watts to the configured cap. We intentionally do NOT use
            // PowerTransmitter.MaxPowerTransmission here any more; it stays the beam
            // visualizer's full-brightness reference in ReceivePower (4).
            var cap = MaxCapacityConfigSync.GetEffectiveMaxCapacity();

            // Standalone debt ceiling: pause delivery while the unpaid debt is
            // at or above four times the per-tick worst case, so an
            // insufficient source or an OnOff lump bill can never run the debt
            // (and the lifted bill it produces) away without bound. Stands
            // down while an external allocator owns billing; the owner settles
            // the ledger itself.
            if (ModApi.BillingOwner == null && DistanceCostShared.PowerProvidedField != null)
            {
                var debt = (float)DistanceCostShared.PowerProvidedField.GetValue(__instance);
                var effectiveCap = cap > 0f ? cap : PowerTransmitter.MaxPowerTransmission;
                var multiplier = Mathf.Max(ModApi.SourceDrawMultiplier(__instance), 1f);
                var ceiling = effectiveCap * multiplier * 4f;
                if (debt >= ceiling)
                {
                    WarnPauseOnce(__instance, debt, ceiling);
                    __result = 0f;
                    return false;
                }
                if (debt < ceiling * 0.5f) EndPauseEpisode(__instance);
            }

            var available = __instance.InputNetwork.PotentialLoad;
            __result = cap > 0f ? Mathf.Min(cap, available) : available;
            return false;
        }

        private static void WarnPauseOnce(PowerTransmitter transmitter, float debt, float ceiling)
        {
            lock (DebtPauseLock)
            {
                if (!DebtPauseWarned.Add(transmitter.ReferenceId)) return;
            }
            PowerTransmitterPlusPlugin.Log?.LogWarning(
                $"Transmitter {transmitter.ReferenceId}: unpaid transfer debt {debt:F0} W reached the safety ceiling {ceiling:F0} W; pausing wireless delivery until the source pays it down");
        }

        private static void EndPauseEpisode(PowerTransmitter transmitter)
        {
            lock (DebtPauseLock)
            {
                DebtPauseWarned.Remove(transmitter.ReferenceId);
            }
        }
    }

    // (2) Inflate _powerProvided at UsePower time so the source-input tick will
    //     end up paying multiplier * delivered watts.
    [HarmonyPatch(typeof(PowerTransmitter), nameof(PowerTransmitter.UsePower))]
    public static class UsePowerInflateDebtPatch
    {
        [UsedImplicitly]
        public static void Postfix(PowerTransmitter __instance, CableNetwork cableNetwork, float powerUsed)
        {
            // Cross-mod handshake: while another mod owns wireless billing it
            // computes and bills the source-side draw itself; our debt
            // inflation stands down so the ledger is not charged twice.
            if (ModApi.BillingOwner != null) return;

            if (powerUsed <= 0f) return;
            if (__instance.Error == 1 || !__instance.OnOff) return;

            // Mirror vanilla's network check: we only inflate when the wireless
            // output network is the consumer. Other paths (none expected) skip.
            var wireless = DistanceCostShared.GetWirelessOutputNetwork(__instance);
            if (wireless == null || cableNetwork != wireless) return;

            var multiplier = DistanceCostShared.GetMultiplier(__instance);
            if (multiplier <= 1f) return;

            var field = DistanceCostShared.PowerProvidedField;
            if (field == null) return;
            var current = (float)field.GetValue(__instance);
            field.SetValue(__instance, current + powerUsed * (multiplier - 1f));
        }
    }

    // (3) Lift the MaxPowerTransmission cap on the source-side demand so the
    //     inflated debt can be paid in a single tick. Without this the debt
    //     would dribble down across many ticks and the source draw would feel
    //     wrong (capped artificially at 5000W).
    [HarmonyPatch(typeof(PowerTransmitter), nameof(PowerTransmitter.GetUsedPower))]
    public static class GetUsedPowerLiftCapPatch
    {
        [UsedImplicitly]
        public static void Postfix(PowerTransmitter __instance, CableNetwork cableNetwork, ref float __result)
        {
            // Cross-mod handshake: while another mod owns wireless billing it
            // presents the source-side bill itself; our cap lift stands down.
            if (ModApi.BillingOwner != null) return;

            if (__instance.Error == 1 || !__instance.OnOff) return;
            if (__instance.InputNetwork == null || cableNetwork != __instance.InputNetwork) return;

            var field = DistanceCostShared.PowerProvidedField;
            if (field == null) return;
            var debt = (float)field.GetValue(__instance);
            // Vanilla returned Min(MaxPowerTransmission, debt). If debt > cap,
            // surface the full debt so the source pays it all this tick.
            if (debt > __result) __result = debt;
        }
    }

    // (4) Keep VisualizerIntensity meaningful as a "throughput" indicator.
    //     Vanilla sets it to powerAdded / MaxPowerTransmission. After our
    //     multiplier, powerAdded is the *source draw*, not the delivered
    //     amount, so without this fix the visualizer would saturate at
    //     1/multiplier on any non-trivial beam.
    [HarmonyPatch(typeof(PowerTransmitter), nameof(PowerTransmitter.ReceivePower))]
    public static class ReceivePowerVisualizerFixPatch
    {
        [UsedImplicitly]
        public static void Postfix(PowerTransmitter __instance, CableNetwork cableNetwork, float powerAdded)
        {
            if (powerAdded <= 0f) return;
            if (__instance.Error == 1 || !__instance.OnOff) return;
            if (__instance.InputNetwork == null || cableNetwork != __instance.InputNetwork) return;

            var multiplier = DistanceCostShared.GetMultiplier(__instance);
            if (multiplier <= 1f) return;  // vanilla formula already correct

            var delivered = powerAdded / multiplier;
            __instance.VisualizerIntensity = delivered / PowerTransmitter.MaxPowerTransmission;
        }
    }

    // (5) Lift the receiver's wireless drain cap in step with the delivery cap.
    //     Vanilla PowerReceiver.GetUsedPower bills the wireless network
    //     Min(MaxPowerTransmission + UsedPower, _powerProvided). Under the
    //     vanilla 5 kW delivery ceiling that cap was unreachable; with the
    //     advertise lifted above 5 kW by (1), a link delivering more than
    //     5 kW leaves the excess stuck as receiver debt that never crosses
    //     back to the transmitter, so the source is never billed for it
    //     (free energy). Lift the vanilla result to
    //     Min(max(5000, cap) + UsedPower, debt) when a positive cap is
    //     configured, and to the full debt when the cap is 0 (unlimited).
    //     Mirrors the vanilla guards (Error / OnOff / wireless-net identity)
    //     and never lowers the vanilla result. ALWAYS active, including while
    //     a billing owner holds the ModApi handshake: it corrects vanilla
    //     relay accounting and is harmless under an external allocator.
    [HarmonyPatch(typeof(PowerReceiver), nameof(PowerReceiver.GetUsedPower))]
    public static class ReceiverDrainCapLiftPatch
    {
        [UsedImplicitly]
        public static void Postfix(PowerReceiver __instance, CableNetwork cableNetwork, ref float __result)
        {
            if (__instance.Error == 1 || !__instance.OnOff) return;
            var wireless = __instance.WirelessInputNetwork;
            if (wireless == null || cableNetwork != wireless) return;

            var field = DistanceCostShared.ReceiverPowerProvidedField;
            if (field == null) return;
            var debt = (float)field.GetValue(__instance);

            var cap = MaxCapacityConfigSync.GetEffectiveMaxCapacity();
            if (cap <= 0f)
            {
                // Unlimited delivery: surface the full debt so it settles as
                // fast as the wireless network can carry it.
                if (debt > __result) __result = debt;
                return;
            }
            // A cap at or below the vanilla 5000 is already covered by the
            // vanilla term; only lift when the cap exceeds it.
            if (cap <= PowerTransmitter.MaxPowerTransmission) return;
            var lifted = Mathf.Min(cap + __instance.UsedPower, debt);
            if (lifted > __result) __result = lifted;
        }
    }
}
