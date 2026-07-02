using Assets.Scripts.Objects.Electrical;
using HarmonyLib;

namespace PowerGridPlus.Patches
{
    /// <summary>
    ///     Powered presentation for routed segmenters (Stage 3), the False-edge half of
    ///     <see cref="PoweredPresentation"/>. Vanilla <c>PowerTick.ApplyState</c> is the game's
    ///     only caller of <c>Device.AllowSetPower</c> and uses it exclusively to gate the un-power
    ///     path: a device that consumed nothing this tick (an idle healthy charger bills a fresh
    ///     pull of 0 under the allocator) falls into ApplyState's else-branch and gets
    ///     Powered=False when AllowSetPower(net) returns true. These postfixes flip that gate to
    ///     false for any segmenter the allocator published as HEALTHY this tick, so vanilla can
    ///     never un-power a healthy segmenter; everything else (faulted, locked-out, dead-input,
    ///     switched-off, not enrolled) passes through and keeps exact vanilla behavior.
    ///
    ///     <para>The vanilla per-class gates being postfixed: Transformer / AreaPowerControl /
    ///     PowerTransmitter allow only their InputNetwork; PowerReceiver allows only its wireless
    ///     input network. The True edge (asserting Powered on idle healthy segmenters) lives in
    ///     <see cref="PoweredPresentation.ReconcileEnforceTail"/>.</para>
    ///
    ///     <para>Multiplayer: server-authoritative by construction. ApplyState runs only inside
    ///     the host's atomic tick; on client peers the healthy set is never published (empty), so
    ///     these postfixes no-op and clients keep mirroring the host's replicated Powered
    ///     state.</para>
    ///
    ///     <para>Threading: runs inside ApplyState on the power worker; the healthy-set read is a
    ///     lock-free lookup against the immutable snapshot ALLOCATE swapped in earlier this
    ///     tick.</para>
    /// </summary>
    [HarmonyPatch(typeof(Transformer), nameof(Transformer.AllowSetPower))]
    public static class TransformerAllowSetPowerPatch
    {
        [HarmonyPostfix]
        public static void Postfix(Transformer __instance, ref bool __result)
        {
            if (__result && PoweredPresentation.IsHealthy(__instance.ReferenceId))
                __result = false;
        }
    }

    /// <summary>Healthy-segmenter un-power block for the APC (see TransformerAllowSetPowerPatch).</summary>
    [HarmonyPatch(typeof(AreaPowerControl), nameof(AreaPowerControl.AllowSetPower))]
    public static class AreaPowerControlAllowSetPowerPatch
    {
        [HarmonyPostfix]
        public static void Postfix(AreaPowerControl __instance, ref bool __result)
        {
            if (__result && PoweredPresentation.IsHealthy(__instance.ReferenceId))
                __result = false;
        }
    }

    /// <summary>
    ///     Healthy-segmenter un-power block for the wireless transmitter half. The pair's health is
    ///     published under both halves' ReferenceIds (anchor + partner), so transmitter and
    ///     receiver present the same verdict.
    /// </summary>
    [HarmonyPatch(typeof(PowerTransmitter), nameof(PowerTransmitter.AllowSetPower))]
    public static class PowerTransmitterAllowSetPowerPatch
    {
        [HarmonyPostfix]
        public static void Postfix(PowerTransmitter __instance, ref bool __result)
        {
            if (__result && PoweredPresentation.IsHealthy(__instance.ReferenceId))
                __result = false;
        }
    }

    /// <summary>Healthy-segmenter un-power block for the wireless receiver half.</summary>
    [HarmonyPatch(typeof(PowerReceiver), nameof(PowerReceiver.AllowSetPower))]
    public static class PowerReceiverAllowSetPowerPatch
    {
        [HarmonyPostfix]
        public static void Postfix(PowerReceiver __instance, ref bool __result)
        {
            if (__result && PoweredPresentation.IsHealthy(__instance.ReferenceId))
                __result = false;
        }
    }
}
