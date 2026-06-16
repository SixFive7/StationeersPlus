using Assets.Scripts.Objects.Electrical;
using Assets.Scripts.Objects.Pipes;
using Objects.Rockets;
using HarmonyLib;

namespace PowerGridPlus.Patches
{
    /// <summary>
    ///     Zeroes a segmenting device's power contribution on BOTH terminals while it is in ANY power
    ///     fault lockout -- CYCLE_FAULT (POWER.md §4.5), SHED, or OVERLOAD (§8.0.0.1: every segmenting
    ///     device class needs the lockout zero, not just Transformer). Postfixes
    ///     <c>GetGeneratedPower</c> and <c>GetUsedPower</c> on each of the seven concrete segmenting
    ///     classes; the base virtual is overridden per class so each override needs its own patch.
    ///     Late priority so this is the final word over any other power postfix
    ///     (PowerTransmitterPlus's distance-loss math, the transformer exploit fix, etc.).
    ///
    ///     <para>This is uniform across all seven classes (POWERTODO 1.7 Q2): no class is exempt. The
    ///     RocketPowerUmbilicalFemale has no OnOff button but still zeroes its power when faulted; its
    ///     fault is surfaced by hover text only. The shed / overload checks use the client-aware reads
    ///     (IsShedding / IsOverloaded) so a client peer mirrors the host's zero on the same tick. This
    ///     postfix also delivers the PT/PR pair lockout enforcement (POWER.md §6.4): the registries key
    ///     the pair on the PT's ReferenceId, so the PT zeroes here and the PR side goes quiet because
    ///     nothing feeds its wireless input.</para>
    /// </summary>
    [HarmonyPatch]
    public static class CycleFaultEnforcementPatches
    {
        private static void ZeroIfFaulted(long referenceId, ref float result)
        {
            if (result == 0f) return;
            int tick = ElectricityTickCounter.CurrentTick;
            if (CycleFaultRegistry.IsCycleFaulted(referenceId, tick)
                || BrownoutRegistry.IsShedding(referenceId, tick)
                || OverloadRegistry.IsOverloaded(referenceId, tick))
                result = 0f;
        }

        // --- Transformer ---
        [HarmonyPostfix, HarmonyPriority(Priority.Last), HarmonyPatch(typeof(Transformer), nameof(Transformer.GetGeneratedPower))]
        public static void Transformer_Gen(Transformer __instance, ref float __result) => ZeroIfFaulted(__instance.ReferenceId, ref __result);
        [HarmonyPostfix, HarmonyPriority(Priority.Last), HarmonyPatch(typeof(Transformer), nameof(Transformer.GetUsedPower))]
        public static void Transformer_Used(Transformer __instance, ref float __result) => ZeroIfFaulted(__instance.ReferenceId, ref __result);

        // --- Battery (covers StationaryBattery + StationBatteryLarge + nuclear via the base override) ---
        [HarmonyPostfix, HarmonyPriority(Priority.Last), HarmonyPatch(typeof(Battery), nameof(Battery.GetGeneratedPower))]
        public static void Battery_Gen(Battery __instance, ref float __result) => ZeroIfFaulted(__instance.ReferenceId, ref __result);
        [HarmonyPostfix, HarmonyPriority(Priority.Last), HarmonyPatch(typeof(Battery), nameof(Battery.GetUsedPower))]
        public static void Battery_Used(Battery __instance, ref float __result) => ZeroIfFaulted(__instance.ReferenceId, ref __result);

        // --- AreaPowerControl ---
        [HarmonyPostfix, HarmonyPriority(Priority.Last), HarmonyPatch(typeof(AreaPowerControl), nameof(AreaPowerControl.GetGeneratedPower))]
        public static void Apc_Gen(AreaPowerControl __instance, ref float __result) => ZeroIfFaulted(__instance.ReferenceId, ref __result);
        [HarmonyPostfix, HarmonyPriority(Priority.Last), HarmonyPatch(typeof(AreaPowerControl), nameof(AreaPowerControl.GetUsedPower))]
        public static void Apc_Used(AreaPowerControl __instance, ref float __result) => ZeroIfFaulted(__instance.ReferenceId, ref __result);

        // --- PowerTransmitter (late so PowerTransmitterPlus computes its distance number first) ---
        [HarmonyPostfix, HarmonyPriority(Priority.Last), HarmonyPatch(typeof(PowerTransmitter), nameof(PowerTransmitter.GetGeneratedPower))]
        public static void Pt_Gen(PowerTransmitter __instance, ref float __result) => ZeroIfFaulted(__instance.ReferenceId, ref __result);
        [HarmonyPostfix, HarmonyPriority(Priority.Last), HarmonyPatch(typeof(PowerTransmitter), nameof(PowerTransmitter.GetUsedPower))]
        public static void Pt_Used(PowerTransmitter __instance, ref float __result) => ZeroIfFaulted(__instance.ReferenceId, ref __result);

        // --- PowerReceiver ---
        [HarmonyPostfix, HarmonyPriority(Priority.Last), HarmonyPatch(typeof(PowerReceiver), nameof(PowerReceiver.GetGeneratedPower))]
        public static void Pr_Gen(PowerReceiver __instance, ref float __result) => ZeroIfFaulted(__instance.ReferenceId, ref __result);
        [HarmonyPostfix, HarmonyPriority(Priority.Last), HarmonyPatch(typeof(PowerReceiver), nameof(PowerReceiver.GetUsedPower))]
        public static void Pr_Used(PowerReceiver __instance, ref float __result) => ZeroIfFaulted(__instance.ReferenceId, ref __result);

        // --- RocketPowerUmbilicalMale ---
        [HarmonyPostfix, HarmonyPriority(Priority.Last), HarmonyPatch(typeof(RocketPowerUmbilicalMale), nameof(RocketPowerUmbilicalMale.GetGeneratedPower))]
        public static void RumM_Gen(RocketPowerUmbilicalMale __instance, ref float __result) => ZeroIfFaulted(__instance.ReferenceId, ref __result);
        [HarmonyPostfix, HarmonyPriority(Priority.Last), HarmonyPatch(typeof(RocketPowerUmbilicalMale), nameof(RocketPowerUmbilicalMale.GetUsedPower))]
        public static void RumM_Used(RocketPowerUmbilicalMale __instance, ref float __result) => ZeroIfFaulted(__instance.ReferenceId, ref __result);

        // --- RocketPowerUmbilicalFemale ---
        [HarmonyPostfix, HarmonyPriority(Priority.Last), HarmonyPatch(typeof(RocketPowerUmbilicalFemale), nameof(RocketPowerUmbilicalFemale.GetGeneratedPower))]
        public static void RumF_Gen(RocketPowerUmbilicalFemale __instance, ref float __result) => ZeroIfFaulted(__instance.ReferenceId, ref __result);
        [HarmonyPostfix, HarmonyPriority(Priority.Last), HarmonyPatch(typeof(RocketPowerUmbilicalFemale), nameof(RocketPowerUmbilicalFemale.GetUsedPower))]
        public static void RumF_Used(RocketPowerUmbilicalFemale __instance, ref float __result) => ZeroIfFaulted(__instance.ReferenceId, ref __result);
    }
}
