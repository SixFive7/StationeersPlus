using System.Collections.Generic;
using System.Reflection;
using Assets.Scripts.Objects.Electrical;       // PowerConnector (filter exclusion)
using Assets.Scripts.Objects.Motherboards;     // LogicType
using Assets.Scripts.Objects.Pipes;            // Device
using HarmonyLib;

namespace PowerGridPlus.Patches
{
    /// <summary>
    ///     Exposes the read-only <c>LogicType.CurrentMismatchFault</c> on every CURRENT-MISMATCH-capable producer that
    ///     has a device-specific logic surface (POWER.md §8.7 / deviation P10). The exposed set is
    ///     <see cref="ProducerClassifier.IsProducer"/> minus the two producers that declare no logic
    ///     surface: <c>PowerConnector</c> (a dynamic-generator dock) and <c>RadioscopicThermalGenerator</c>
    ///     (RTG, a rocket-internal component with no normal data port; see
    ///     Research/GameSystems/PowerProducerLogicReadability.md). So the exposed set is solar, both wind
    ///     turbines, the small turbine, and the gas / solid-fuel / stirling generators, each of which
    ///     declares a device-specific <c>CanLogicRead</c> override, so the read is reachable from IC10.
    ///
    ///     <para>Robustness against the inherited-method trap (P10 harden): each producer's ACTUAL runtime
    ///     <c>CanLogicRead</c> / <c>GetLogicValue</c> is resolved with <c>AccessTools</c> from the concrete
    ///     type, so a future game version that gives e.g. GasFuelGenerator its own override does not
    ///     silently drop the read. For producers that do NOT declare their own logic methods the resolved
    ///     target is a shared base (e.g. <c>Device</c>), so a per-instance <see cref="ProducerVvfLogic.IsExposed"/>
    ///     filter keeps the CURRENT-MISMATCH read from leaking onto non-producers (and onto the excluded
    ///     PowerConnector). Patching a shared base with an instance filter is the same pattern the rocket
    ///     umbilical Female and the logic-passthrough patches already use.</para>
    /// </summary>
    internal static class ProducerVvfLogic
    {
        // Concrete producer types whose logic methods carry the CurrentMismatchFault read. Resolved by
        // name across loaded assemblies (no compile-time coupling to per-class namespaces); a name that
        // does not resolve is skipped. Base classes AND concrete subclasses are listed so the patch
        // follows a future override (e.g. GasFuelGenerator) instead of staying on the base.
        private static readonly string[] TypeNames =
        {
            "SolarPanel",
            "WindTurbineGenerator",
            "LargeWindTurbineGenerator",
            "PowerGeneratorPipe",
            "GasFuelGenerator",
            "SolidFuelGenerator",
            "StirlingEngine",
            "TurbineGenerator",
            // PowerConnector deliberately excluded: no meaningful logic surface.
        };

        internal static IEnumerable<MethodBase> Targets(string methodName)
        {
            var seen = new HashSet<MethodBase>();
            foreach (var name in TypeNames)
            {
                var type = AccessTools.TypeByName(name);
                if (type == null) continue;
                var method = AccessTools.Method(type, methodName, new[] { typeof(LogicType) });
                if (method != null && seen.Add(method))
                    yield return method;
            }
        }

        // The CURRENT-MISMATCH read acts only for an exposed producer: every IsProducer device except PowerConnector.
        // Required because a target resolved to a shared base method (for producers that do not override)
        // would otherwise fire for unrelated devices.
        internal static bool IsExposed(object instance)
            => instance is Device d && ProducerClassifier.IsProducer(d)
               && !(d is PowerConnector) && !(d is RadioscopicThermalGenerator);
    }

    [HarmonyPatch]
    public static class ProducerVvfCanReadPatch
    {
        public static IEnumerable<MethodBase> TargetMethods() => ProducerVvfLogic.Targets("CanLogicRead");

        [HarmonyPostfix]
        public static void Postfix(object __instance, LogicType logicType, ref bool __result)
        {
            if (logicType == LogicTypeRegistry.CurrentMismatchFault && ProducerVvfLogic.IsExposed(__instance))
                __result = true;
        }
    }

    [HarmonyPatch]
    public static class ProducerVvfGetValuePatch
    {
        public static IEnumerable<MethodBase> TargetMethods() => ProducerVvfLogic.Targets("GetLogicValue");

        [HarmonyPostfix]
        public static void Postfix(object __instance, LogicType logicType, ref double __result)
        {
            if (logicType != LogicTypeRegistry.CurrentMismatchFault) return;
            if (!(__instance is Device d) || !ProducerVvfLogic.IsExposed(d)) return;
            __result = CurrentMismatchFaultRegistry.IsCurrentMismatchFaulted(
                d.ReferenceId, ElectricityTickCounter.CurrentTick) ? 1.0 : 0.0;
        }
    }
}
