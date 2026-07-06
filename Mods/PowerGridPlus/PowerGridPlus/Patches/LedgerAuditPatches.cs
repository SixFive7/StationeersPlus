using Assets.Scripts.Objects.Electrical;
using HarmonyLib;

namespace PowerGridPlus.Patches
{
    /// <summary>
    ///     Ledger-audit observation wrappers (Stage 3 exact audit, layer A+). Every legitimate
    ///     in-tick mutation of the vanilla <c>_powerProvided</c> ledger on the settle-owned device
    ///     kinds goes through one of six vanilla methods; each gets a Priority.First prefix that
    ///     captures the field value BEFORE anything else runs and a Priority.Last postfix that
    ///     captures it AFTER everything else ran. The postfix feeds the (before, after) pair to
    ///     <see cref="LedgerAdoption.NoteMutation"/>, which accumulates the delta into a per-device
    ///     double shadow sum and checks bracket continuity (this call's BEFORE must equal the last
    ///     recorded AFTER). Because the wrapper observes the FIELD rather than modelling the method
    ///     logic, it captures the net effect no matter which inner prefix skipped or rewrote the
    ///     vanilla body. Always-on, no config entry: cost is two injected field reads, one
    ///     dictionary lookup, and a couple of double adds per mutation.
    ///
    ///     <para><b>Write-site census for <c>_powerProvided</c></b> (decompile 0.2.6403.27689; the
    ///     audit's exactness argument). Vanilla declares four independent private fields
    ///     (AreaPowerControl L390592, PowerReceiver L408071, PowerTransmitter L408287, Transformer
    ///     L424621); an exhaustive token search finds every touch. The complete write list:</para>
    ///
    ///     <list type="bullet">
    ///       <item>Transformer.UsePower L424761 (+= powerUsed): INSTRUMENTED below.</item>
    ///       <item>Transformer.ReceivePower L424769 (-= powerAdded): INSTRUMENTED below.</item>
    ///       <item>PowerTransmitter.UsePower L408428 (+= powerUsed): INSTRUMENTED below.</item>
    ///       <item>PowerTransmitter.ReceivePower L408442 (-= powerAdded): INSTRUMENTED below.</item>
    ///       <item>PowerReceiver.UsePower L408188 (+= powerUsed): INSTRUMENTED below.</item>
    ///       <item>PowerReceiver.ReceivePower L408202 (-= powerAdded): INSTRUMENTED below.</item>
    ///       <item>AreaPowerControl.UsePower L391004-391010 and ReceivePower L391018-391023: OUT OF
    ///       SCOPE. The APC is never settled (its ledger is load-bearing, see the PowerAllocator
    ///       publish tail), so it is never tracked and its wrappers are not installed.</item>
    ///       <item>Deserialization: NO vanilla write site exists in this game version.
    ///       TransformerSaveData carries only Setting, WirelessPowerSaveData only dish rotation,
    ///       and no SerializeOnJoin / DeserializeOnJoin member touches the field, so a save cannot
    ///       write it on load. The world-load sweep still zeroes all four classes before the first
    ///       OBSERVE (SWEEP-COVERED for anything an older game version or another mod might have
    ///       persisted), and the sweep clears the audit tracking map, so a load never trips the
    ///       boundary check.</item>
    ///       <item>No subclass of the four classes exists in Assembly-CSharp, so no override can
    ///       reach the private fields some other way. The only virtual-dispatch call sites into
    ///       UsePower / ReceivePower are PowerProvider.ApplyPower (L271694, one UsePower per
    ///       drained provider) and PowerTick.ConsumePower (L271832, one ReceivePower per provider
    ///       chunk), both inside ApplyState on the power worker.</item>
    ///     </list>
    ///
    ///     <para><b>Mod-side writers</b> (complete for the two mods in this repo):
    ///     TransformerExploitPatches.ReceivePowerPatch (prefix, Priority.Normal) skips the vanilla
    ///     body under mitigation and writes the field itself: INSIDE the bracket.
    ///     PowerTransmitterPlus UsePowerInflateDebtPatch (postfix, Priority.Normal) adds
    ///     powerUsed * (m - 1) on PowerTransmitter.UsePower: dormant while the PowerGridPlus
    ///     billing-ownership handshake is held, and INSIDE the bracket when it does run (legacy
    ///     tier), so the shadow sum captures the net effect either way. PowerTransmitterPlus
    ///     ReceivePowerVisualizerFixPatch writes only VisualizerIntensity. LedgerAdoption's own
    ///     sweep and settle (via cached FieldInfo or PowerTransmitterPlus ModApi.SetTransferDebt)
    ///     are the owner's reference writes: the settle records its result and the sweep clears
    ///     tracking, so neither counts as an anomaly. Every remaining patch in both mods on these
    ///     classes (GetUsedPower / GetGeneratedPower / logic surfaces / VoltageTier's conducting
    ///     probe) only reads the field.</para>
    ///
    ///     <para><b>Bracketing evidence</b> (HarmonyX 0Harmony shipped with BepInEx, decompiled
    ///     alongside the game): prefixes and postfixes are each sorted descending by priority
    ///     (PatchInfo.PriorityComparer, <c>-priority.CompareTo(value)</c>, ties by insertion
    ///     index), so the Priority.First prefix runs before every other prefix and the
    ///     Priority.Last postfix after every other postfix. Prefixes run unconditionally in that
    ///     order (a bool prefix only ANDs into __runOriginal; MethodCreator.WritePrefixes emits a
    ///     single skip branch AFTER all prefixes), and for a void original the skip branch targets
    ///     the return label that WritePostfixes marks at the START of the postfix block, so the
    ///     postfix runs even when another prefix skips the vanilla body. The pair therefore always
    ///     observes the field before and after the ENTIRE remaining patch pipeline on the method.
    ///     The <c>__state</c> float carrying BEFORE from prefix to postfix is shared per declaring
    ///     patch class per patched method (MethodCreator.WriteImpl), and this class contributes
    ///     exactly one pair per method.</para>
    ///
    ///     <para><b>Threading</b>: every instrumented method runs inside ApplyState on the UniTask
    ///     power worker (the atomic tick calls it there, and vanilla did the same); the tracking
    ///     map these wrappers touch lives in LedgerAdoption statics owned by that worker. On a
    ///     client peer the map is empty (no settle ever runs), so the wrappers degrade to a failed
    ///     dictionary lookup.</para>
    /// </summary>
    [HarmonyPatch]
    public static class LedgerAuditPatches
    {
        // ------------------------------------------------------------------
        // Transformer (vanilla UsePower L424757-424763, ReceivePower L424765-424771).
        // ------------------------------------------------------------------

        [HarmonyPrefix, HarmonyPriority(Priority.First),
         HarmonyPatch(typeof(Transformer), nameof(Transformer.UsePower))]
        public static void TransformerUsePowerBefore(float ____powerProvided, out float __state)
            => __state = ____powerProvided;

        [HarmonyPostfix, HarmonyPriority(Priority.Last),
         HarmonyPatch(typeof(Transformer), nameof(Transformer.UsePower))]
        public static void TransformerUsePowerAfter(Transformer __instance, float ____powerProvided, float __state)
            => LedgerAdoption.NoteMutation(__instance.ReferenceId,
                LedgerAdoption.Site.TransformerUsePower, __state, ____powerProvided);

        [HarmonyPrefix, HarmonyPriority(Priority.First),
         HarmonyPatch(typeof(Transformer), nameof(Transformer.ReceivePower))]
        public static void TransformerReceivePowerBefore(float ____powerProvided, out float __state)
            => __state = ____powerProvided;

        [HarmonyPostfix, HarmonyPriority(Priority.Last),
         HarmonyPatch(typeof(Transformer), nameof(Transformer.ReceivePower))]
        public static void TransformerReceivePowerAfter(Transformer __instance, float ____powerProvided, float __state)
            => LedgerAdoption.NoteMutation(__instance.ReferenceId,
                LedgerAdoption.Site.TransformerReceivePower, __state, ____powerProvided);

        // ------------------------------------------------------------------
        // PowerTransmitter (vanilla UsePower L408424-408430, ReceivePower L408432-408444).
        // ------------------------------------------------------------------

        [HarmonyPrefix, HarmonyPriority(Priority.First),
         HarmonyPatch(typeof(PowerTransmitter), nameof(PowerTransmitter.UsePower))]
        public static void TransmitterUsePowerBefore(float ____powerProvided, out float __state)
            => __state = ____powerProvided;

        [HarmonyPostfix, HarmonyPriority(Priority.Last),
         HarmonyPatch(typeof(PowerTransmitter), nameof(PowerTransmitter.UsePower))]
        public static void TransmitterUsePowerAfter(PowerTransmitter __instance, float ____powerProvided, float __state)
            => LedgerAdoption.NoteMutation(__instance.ReferenceId,
                LedgerAdoption.Site.TransmitterUsePower, __state, ____powerProvided);

        [HarmonyPrefix, HarmonyPriority(Priority.First),
         HarmonyPatch(typeof(PowerTransmitter), nameof(PowerTransmitter.ReceivePower))]
        public static void TransmitterReceivePowerBefore(float ____powerProvided, out float __state)
            => __state = ____powerProvided;

        [HarmonyPostfix, HarmonyPriority(Priority.Last),
         HarmonyPatch(typeof(PowerTransmitter), nameof(PowerTransmitter.ReceivePower))]
        public static void TransmitterReceivePowerAfter(PowerTransmitter __instance, float ____powerProvided, float __state)
            => LedgerAdoption.NoteMutation(__instance.ReferenceId,
                LedgerAdoption.Site.TransmitterReceivePower, __state, ____powerProvided);

        // ------------------------------------------------------------------
        // PowerReceiver (vanilla UsePower L408184-408190, ReceivePower L408192-408204).
        // ------------------------------------------------------------------

        [HarmonyPrefix, HarmonyPriority(Priority.First),
         HarmonyPatch(typeof(PowerReceiver), nameof(PowerReceiver.UsePower))]
        public static void ReceiverUsePowerBefore(float ____powerProvided, out float __state)
            => __state = ____powerProvided;

        [HarmonyPostfix, HarmonyPriority(Priority.Last),
         HarmonyPatch(typeof(PowerReceiver), nameof(PowerReceiver.UsePower))]
        public static void ReceiverUsePowerAfter(PowerReceiver __instance, float ____powerProvided, float __state)
            => LedgerAdoption.NoteMutation(__instance.ReferenceId,
                LedgerAdoption.Site.ReceiverUsePower, __state, ____powerProvided);

        [HarmonyPrefix, HarmonyPriority(Priority.First),
         HarmonyPatch(typeof(PowerReceiver), nameof(PowerReceiver.ReceivePower))]
        public static void ReceiverReceivePowerBefore(float ____powerProvided, out float __state)
            => __state = ____powerProvided;

        [HarmonyPostfix, HarmonyPriority(Priority.Last),
         HarmonyPatch(typeof(PowerReceiver), nameof(PowerReceiver.ReceivePower))]
        public static void ReceiverReceivePowerAfter(PowerReceiver __instance, float ____powerProvided, float __state)
            => LedgerAdoption.NoteMutation(__instance.ReferenceId,
                LedgerAdoption.Site.ReceiverReceivePower, __state, ____powerProvided);
    }
}
