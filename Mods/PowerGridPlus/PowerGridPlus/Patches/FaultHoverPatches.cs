using System.Collections.Generic;
using System.Reflection;
using Assets.Scripts;
using Assets.Scripts.Objects;
using Assets.Scripts.Objects.Electrical;
using HarmonyLib;
using UnityEngine;

namespace PowerGridPlus.Patches
{
    /// <summary>
    ///     Appends the fault line + live countdown (POWER.md §11.1 via <see cref="FaultHover"/>) to
    ///     the body hover of every power device that can be in a fault lockout. The UI re-polls
    ///     GetPassiveTooltip every frame while the player hovers, so the countdown ticks smoothly.
    ///
    ///     <para>Patch targets (the virtual-dispatch trap, POWERTODO 0.2): Thing.GetPassiveTooltip is
    ///     virtual and overridden along the hierarchy, so the postfix must attach to the override
    ///     that actually RUNS for each class. The seven targets below cover every faultable class:
    ///     Device (WindTurbineGenerator + LargeWindTurbineGenerator, RadioscopicThermalGenerator,
    ///     PowerGeneratorSlot + SolidFuelGenerator, TurbineGenerator, and any modded producer with no
    ///     own override), ElectricalInputOutput (Battery, Transformer, PowerTransmitter,
    ///     PowerReceiver, both RocketPowerUmbilical halves), and the five classes with their own
    ///     override: AreaPowerControl, SolarPanel, PowerGeneratorPipe (GasFuelGenerator),
    ///     StirlingEngine, PowerConnector. Burned-cable hovers live in BurnReasonPatches
    ///     (Structure.GetPassiveTooltip), not here.</para>
    ///
    ///     <para>Exactly one fault line per hover -- the highest-precedence active fault (CYCLE &gt;
    ///     VVF &gt; OVERLOAD &gt; SHED, §11.5); devices with no active fault are untouched (idle
    ///     hover stays pure vanilla, the RocketPowerUmbilicalFemale rule in §5.0.2 generalised).</para>
    /// </summary>
    [HarmonyPatch]
    public static class FaultHoverPatches
    {
        public static IEnumerable<MethodBase> TargetMethods()
        {
            const string name = "GetPassiveTooltip";
            yield return AccessTools.Method(typeof(Assets.Scripts.Objects.Pipes.Device), name);
            yield return AccessTools.Method(typeof(ElectricalInputOutput), name);
            yield return AccessTools.Method(typeof(AreaPowerControl), name);
            yield return AccessTools.Method(typeof(SolarPanel), name);
            yield return AccessTools.Method(typeof(PowerGeneratorPipe), name);
            yield return AccessTools.Method(typeof(StirlingEngine), name);
            yield return AccessTools.Method(typeof(PowerConnector), name);
        }

        [HarmonyPostfix]
        public static void Postfix(Thing __instance, ref PassiveTooltip __result)
        {
            if (__instance == null) return;
            long refId = FaultHover.ResolveFaultRefId(__instance);
            // The highest-precedence active fault line, then (for a throttled transformer, §5.3 / P13)
            // the neutral throttle info line below it. Both are optional and stack independently.
            if (FaultHover.TryGetLine(refId, ElectricityTickCounter.CurrentTick, __instance, out var faultLine, out _))
                __result.Extended = AppendLine(__result.Extended, faultLine);
            if (TransformerThrottleHover.TryGetLine(__instance, out var throttleLine))
                __result.Extended = AppendLine(__result.Extended, throttleLine);
        }

        private static string AppendLine(string existing, string line)
            => string.IsNullOrEmpty(existing) ? line : existing + "\n" + line;
    }
}
