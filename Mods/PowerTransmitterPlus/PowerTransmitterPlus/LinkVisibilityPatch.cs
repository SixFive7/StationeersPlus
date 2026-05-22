using Assets.Scripts.Objects.Electrical;
using HarmonyLib;
using JetBrains.Annotations;

namespace PowerTransmitterPlus
{
    // Re-evaluate beam visibility whenever a transmitter's LinkedReceiver
    // changes. Vanilla's LinkedReceiver property setter fires on every link
    // transition: on the server via TryContactReceiver and OnDestroy, on
    // clients via ProcessUpdate (which assigns through the property when the
    // NetworkUpdateType.WirelessPower.Receiver bit is set). One postfix here
    // covers every path.
    //
    // BeamManager.ReevaluateVisibility handles both directions: when value
    // is null the predicate fails (LinkedReceiver == null) and the beam
    // hides; when value is non-null the full predicate runs (link + both
    // dishes' OnOff + aim) and the beam shows iff every condition is met.
    //
    // The postfix runs even when the setter body is skipped by the
    // value != _linkedReceiver short-circuit (Harmony postfixes always run),
    // so the same value may be re-published multiple times.
    // ReevaluateVisibility is idempotent.
    [HarmonyPatch(typeof(PowerTransmitter), nameof(PowerTransmitter.LinkedReceiver), MethodType.Setter)]
    public static class LinkedReceiverSetterPatch
    {
        [UsedImplicitly]
        public static void Postfix(PowerTransmitter __instance)
        {
            BeamManager.ReevaluateVisibility(__instance, "Link");
        }
    }
}
