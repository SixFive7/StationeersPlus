using Assets.Scripts.Networks;
using Assets.Scripts.Objects.Electrical;
using HarmonyLib;
using JetBrains.Annotations;
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
    //   delivered_max = 5000  (uncapped by distance)
    //   source_draw   = delivered * (1 + k * distance_m / 1000)
    //
    // Where k is the configurable per-km overhead factor.
    //
    // Implementation hinges on PowerTransmitter._powerProvided — the private
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
    internal static class DistanceCostShared
    {
        internal static readonly FieldInfo PowerProvidedField =
            AccessTools.Field(typeof(PowerTransmitter), "_powerProvided");

        internal static readonly FieldInfo LinkedDistanceField =
            AccessTools.Field(typeof(PowerTransmitter), "_linkedReceiverDistance");

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
    }

    // (1) Drop the distance-based capacity derate. Returns the un-derated cap.
    [HarmonyPatch(typeof(PowerTransmitter), nameof(PowerTransmitter.GetGeneratedPower))]
    public static class GeneratedPowerNoDistanceDeratePatch
    {
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
            __result = Mathf.Min(PowerTransmitter.MaxPowerTransmission, __instance.InputNetwork.PotentialLoad);
            return false;
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
}
