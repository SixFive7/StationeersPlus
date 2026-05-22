using Assets.Scripts.Objects.Electrical;
using HarmonyLib;
using JetBrains.Annotations;

namespace PowerTransmitterPlus
{
    // Re-evaluate beam visibility when EITHER dish in a linked pair takes a
    // slew step. The WirelessPower.Horizontal / Vertical setters fire on
    // every micro-step of RotatableBehaviour.DoMoveTask on every peer (the
    // slew is simulated on host, single-player, and remote clients alike;
    // see Research/GameClasses/RotatableBehaviour.md). So a beam drops out
    // at the first step where the dish's forward leaves the aim tolerance,
    // and re-shows at the first step it returns within tolerance. Idle
    // dishes do not call these setters; cost is zero when nothing is
    // moving.
    //
    // Resolves both PowerTransmitter (self) and PowerReceiver (via
    // LinkedPowerTransmitter) to the transmitter that owns the beam, so a
    // receiver rotating away from a linked transmitter also drops the beam.

    [HarmonyPatch(typeof(WirelessPower), "Horizontal", MethodType.Setter)]
    public static class WirelessPowerHorizontalSetterPatch
    {
        [UsedImplicitly]
        public static void Postfix(WirelessPower __instance)
        {
            if (__instance == null) return;

            PowerTransmitter tx;
            if (__instance is PowerTransmitter t) tx = t;
            else if (__instance is PowerReceiver r) tx = r.LinkedPowerTransmitter;
            else return;

            if (tx == null) return;

            BeamManager.ReevaluateVisibility(tx, "Slew");
        }
    }

    [HarmonyPatch(typeof(WirelessPower), "Vertical", MethodType.Setter)]
    public static class WirelessPowerVerticalSetterPatch
    {
        [UsedImplicitly]
        public static void Postfix(WirelessPower __instance)
        {
            if (__instance == null) return;

            PowerTransmitter tx;
            if (__instance is PowerTransmitter t) tx = t;
            else if (__instance is PowerReceiver r) tx = r.LinkedPowerTransmitter;
            else return;

            if (tx == null) return;

            BeamManager.ReevaluateVisibility(tx, "Slew");
        }
    }
}
