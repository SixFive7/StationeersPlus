using System.Collections.Generic;
using System.Reflection;
using Assets.Scripts.Networks;
using Assets.Scripts.Objects.Electrical;
using Assets.Scripts.Objects.Pipes;
using HarmonyLib;

namespace PowerGridPlus.Patches
{
    /// <summary>
    ///     Zeroes a producer's generated power while it is in VARIABLE_VOLTAGE_FAULT lockout, so the
    ///     faulted producer visibly stops generating (POWER.md §8.5). Replaces the central
    ///     CalculateState producer-zero that lived in the deleted PowerGridTick: with the vanilla
    ///     PowerTick running OBSERVE and ENFORCE, enforcement moves to per-class GetGeneratedPower
    ///     postfixes.
    ///
    ///     <para>Patch targets: every producer class DECLARES its own GetGeneratedPower override
    ///     (verified, Research/GameSystems/PowerSegmentingDevices.md "GetGeneratedPower override
    ///     map"), so the eight declared methods below cover the full producer set including the
    ///     subclass variants (LargeWindTurbineGenerator via WindTurbineGenerator, GasFuelGenerator
    ///     via PowerGeneratorPipe, SolidFuelGenerator via PowerGeneratorSlot). There is no
    ///     inherited-method trap here because none of these inherit the method from an intermediate
    ///     base.</para>
    /// </summary>
    [HarmonyPatch]
    public static class ProducerFaultEnforcementPatches
    {
        public static IEnumerable<MethodBase> TargetMethods()
        {
            // The eight declaring producer classes (0.2.6228.27061 override map). WindTurbineGenerator
            // lives in the bare Objects namespace; the rest are Assets.Scripts.Objects.Electrical.
            yield return AccessTools.Method(typeof(SolarPanel), nameof(SolarPanel.GetGeneratedPower));
            yield return AccessTools.Method(typeof(global::Objects.WindTurbineGenerator), "GetGeneratedPower");
            yield return AccessTools.Method(typeof(RadioscopicThermalGenerator), nameof(RadioscopicThermalGenerator.GetGeneratedPower));
            yield return AccessTools.Method(typeof(PowerGeneratorPipe), nameof(PowerGeneratorPipe.GetGeneratedPower));
            yield return AccessTools.Method(typeof(PowerGeneratorSlot), nameof(PowerGeneratorSlot.GetGeneratedPower));
            yield return AccessTools.Method(typeof(StirlingEngine), nameof(StirlingEngine.GetGeneratedPower));
            yield return AccessTools.Method(typeof(TurbineGenerator), nameof(TurbineGenerator.GetGeneratedPower));
            yield return AccessTools.Method(typeof(PowerConnector), nameof(PowerConnector.GetGeneratedPower));
        }

        [HarmonyPostfix]
        public static void Postfix(Device __instance, ref float __result)
        {
            // Clamp a non-finite generator output at the source (NaN <= 0 is false, so it would slip
            // past the early-out below). Covers all eight producer classes.
            __result = DeviceOutputSanitizer.Sanitize(__result, __instance, generated: true);
            if (__result <= 0f) return;
            if (!VariableVoltageFaultRegistry.IsVariableVoltageFaulted(
                    __instance.ReferenceId, ElectricityTickCounter.CurrentTick)) return;
            __result = 0f;
        }
    }
}
