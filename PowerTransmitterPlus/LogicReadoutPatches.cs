using Assets.Scripts.Objects.Electrical;
using Assets.Scripts.Objects.Motherboards;
using HarmonyLib;
using JetBrains.Annotations;
using UnityEngine;

namespace PowerTransmitterPlus
{
    // Make CanLogicRead return true for our three slots on both transmitter and
    // receiver, and intercept GetLogicValue to compute the values on the fly
    // from already-synced game state (no caching, no per-tick tracking).
    //
    // Computation:
    //   delivered  = transmitter.OutputNetwork.CurrentLoad
    //                  (the wireless network's actual throughput this tick)
    //   distance   = transmitter._linkedReceiverDistance
    //   k          = DistanceConfigSync.GetEffectiveK()
    //                  (host's value when on a client; local config otherwise)
    //   multiplier = 1 + k * distance / 1000
    //   sourceDraw = delivered * multiplier
    //   loss       = delivered * (multiplier - 1)
    //
    // Receiver path resolves to its LinkedPowerTransmitter and reads from there.
    // When unlinked or no transmission is happening, all values fall to 0
    // naturally because delivered is 0.
    internal static class LogicReadoutCompute
    {
        internal static float GetSourceDraw(PowerTransmitter t) => GetDelivered(t) * GetMultiplier(t);
        internal static float GetDestinationDraw(PowerTransmitter t) => GetDelivered(t);
        internal static float GetTransmissionLoss(PowerTransmitter t) => GetDelivered(t) * (GetMultiplier(t) - 1f);

        private static float GetDelivered(PowerTransmitter t)
        {
            if (t == null || !t.OnOff || t.Error == 1) return 0f;
            if (t.LinkedReceiver == null) return 0f;
            if (t.OutputNetwork == null) return 0f;
            // OutputNetwork on a transmitter is the wireless network it's pumping
            // power into. CurrentLoad is what the receiver's downstream cable
            // network is actually consuming this tick — i.e., delivered.
            return (float)t.OutputNetwork.CurrentLoad;
        }

        private static float GetMultiplier(PowerTransmitter t)
        {
            if (t == null || DistanceCostShared.LinkedDistanceField == null) return 1f;
            var distance = (float)DistanceCostShared.LinkedDistanceField.GetValue(t);
            var k = DistanceConfigSync.GetEffectiveK();
            if (k <= 0f) return 1f;
            var m = 1f + k * distance / 1000f;
            return m < 1f ? 1f : m;
        }
    }

    // ---- Transmitter side ----

    [HarmonyPatch(typeof(PowerTransmitter), nameof(PowerTransmitter.CanLogicRead))]
    public static class TransmitterCanLogicReadPatch
    {
        [UsedImplicitly]
        public static void Postfix(LogicType logicType, ref bool __result)
        {
            if (LogicTypeRegistry.IsCustom(logicType)) __result = true;
        }
    }

    [HarmonyPatch(typeof(PowerTransmitter), nameof(PowerTransmitter.GetLogicValue))]
    public static class TransmitterGetLogicValuePatch
    {
        [UsedImplicitly]
        public static bool Prefix(PowerTransmitter __instance, LogicType logicType, ref double __result)
        {
            switch ((ushort)logicType)
            {
                case LogicTypeRegistry.SourceDrawValue:
                    __result = LogicReadoutCompute.GetSourceDraw(__instance);
                    return false;
                case LogicTypeRegistry.DestinationDrawValue:
                    __result = LogicReadoutCompute.GetDestinationDraw(__instance);
                    return false;
                case LogicTypeRegistry.TransmissionLossValue:
                    __result = LogicReadoutCompute.GetTransmissionLoss(__instance);
                    return false;
                default:
                    return true; // run vanilla
            }
        }
    }

    // ---- Receiver side ----
    // Receivers expose the same numbers by resolving the linked transmitter.
    // If unlinked, returns 0.

    [HarmonyPatch(typeof(PowerReceiver), nameof(PowerReceiver.CanLogicRead))]
    public static class ReceiverCanLogicReadPatch
    {
        [UsedImplicitly]
        public static void Postfix(LogicType logicType, ref bool __result)
        {
            if (LogicTypeRegistry.IsCustom(logicType)) __result = true;
        }
    }

    [HarmonyPatch(typeof(PowerReceiver), nameof(PowerReceiver.GetLogicValue))]
    public static class ReceiverGetLogicValuePatch
    {
        [UsedImplicitly]
        public static bool Prefix(PowerReceiver __instance, LogicType logicType, ref double __result)
        {
            if (!LogicTypeRegistry.IsCustom(logicType)) return true;
            var t = __instance != null ? __instance.LinkedPowerTransmitter : null;
            switch ((ushort)logicType)
            {
                case LogicTypeRegistry.SourceDrawValue:
                    __result = LogicReadoutCompute.GetSourceDraw(t);
                    return false;
                case LogicTypeRegistry.DestinationDrawValue:
                    __result = LogicReadoutCompute.GetDestinationDraw(t);
                    return false;
                case LogicTypeRegistry.TransmissionLossValue:
                    __result = LogicReadoutCompute.GetTransmissionLoss(t);
                    return false;
                default:
                    return true;
            }
        }
    }
}
