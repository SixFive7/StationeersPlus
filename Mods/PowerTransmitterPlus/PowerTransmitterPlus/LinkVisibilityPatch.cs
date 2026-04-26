using Assets.Scripts.Objects.Electrical;
using HarmonyLib;
using JetBrains.Annotations;

namespace PowerTransmitterPlus
{
    // Drive beam show/hide off the actual link state, not power flow. Vanilla's
    // LinkedReceiver property setter fires on every link transition: on the
    // server via TryContactReceiver and OnDestroy, on clients via ProcessUpdate
    // (which assigns through the property when NetworkUpdateType.WirelessPower
    // .Receiver bit is set). One Postfix here covers every path.
    //
    // Postfix runs even when the setter body is skipped (the `value !=
    // _linkedReceiver` short-circuit only skips the body, not Harmony postfixes),
    // so the same value may be re-published multiple times. BeamManager.SetLinked
    // is idempotent.
    [HarmonyPatch(typeof(PowerTransmitter), nameof(PowerTransmitter.LinkedReceiver), MethodType.Setter)]
    public static class LinkedReceiverSetterPatch
    {
        [UsedImplicitly]
        public static void Postfix(PowerTransmitter __instance, PowerReceiver value)
        {
            BeamManager.SetLinked(__instance, value != null);
        }
    }
}
