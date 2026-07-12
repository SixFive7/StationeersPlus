using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Reflection;
using Assets.Scripts;
using Assets.Scripts.Networks;
using Assets.Scripts.Objects.Electrical;
using Assets.Scripts.Objects.Pipes;
using Assets.Scripts.Objects.Structures;
using HarmonyLib;
using VanillaVolumePump = global::Objects.Pipes.VolumePump;
using VanillaTurboVolumePump = global::Objects.Pipes.TurboVolumePump;
using VanillaFermenter = global::Objects.Electrical.Fermenter;

namespace PowerGridPlus.Patches
{
    /// <summary>
    ///     Tick-scoped first-read latches for EVERY remaining per-tick-variable
    ///     <c>GetUsedPower(CableNetwork)</c> override (the 2026-07-09 whole-decompile census;
    ///     Research/GameSystems/PowerTickThreading.md carries the census tables). Same semantics
    ///     as <see cref="ConsumerDemandLatchPatches"/> (the SimpleFabricatorBase latch, kept as
    ///     its own class because it documents the original root-cause analysis): the first
    ///     own-network read of a device inside an atomic electricity tick computes and caches the
    ///     value, and every later read in the SAME tick serves the cache, so OBSERVE, ALLOCATE's
    ///     GATHER, and ENFORCE all see one demand number per device per tick.
    ///
    ///     <para><b>The thirteen targets and their mid-tick main-thread mutators</b> (decompile
    ///     0.2.6403.27689): Fabricator (a SIBLING of SimpleFabricatorBase under FabricatorBase
    ///     with its own override at 396283; accumulator written in OnServerExportTick 396364),
    ///     ArcFurnace (365548; WaitThenSmelt coroutine 365606), IceCrusher (380296;
    ///     OnServerImportTick 380277), Fermenter (181583; OnServerTick 181666/181674), Bench
    ///     (325494; the Appliances list and each appliance's OnOff / docked-tablet charge, all
    ///     player-driven), SuitStorage (327442; slot occupants and their batteries), WallLightBattery
    ///     (327980; cell swaps), BatteryCellCharger (392271; the Batteries list / cell swaps),
    ///     AdvancedFurnace (365058; Setting / Setting2 wheels), VolumePump (176382; Setting),
    ///     TurboVolumePump (176232; its OWN override, a VolumePump patch would not dispatch to
    ///     it), SatelliteDish (418401; Setting and the trading contact state), RocketMiner
    ///     (389454; drill-head swaps). Classes whose accumulators are written only in
    ///     OnAtmosphericTick or OnPowerTick are tick-stable by construction and deliberately
    ///     carry no latch (the census's 20-class stable list).</para>
    ///
    ///     <para><b>Guard.</b> A value is stored / served ONLY when the queried network is the
    ///     device's own <c>PowerCableNetwork</c>; foreign-network calls (the per-class -1f / 0f
    ///     sentinel paths, RocketMiner's multiplied sentinel quirk included) pass through
    ///     unlatched, exactly mirroring each vanilla predicate. Latching runs only while
    ///     <c>GameManager.RunSimulation</c> is true (a client's tick counter never advances).
    ///     Under the net-liveness ownership layer these latches are demand-coherence hygiene
    ///     (the sentinel and audits stay quiet); the Powered decision itself no longer reads
    ///     demand at all.</para>
    ///
    ///     <para>One shared cache keyed by ReferenceId is safe across all thirteen targets:
    ///     ReferenceIds are globally unique and each target latches only its own instances'
    ///     GetUsedPower. Cleared on world load (FaultRegistryLoadPatches).</para>
    /// </summary>
    [HarmonyPatch]
    public static class ConsumerDemandLatchesExtended
    {
        private static readonly ConcurrentDictionary<long, (int tick, float value)> _latch =
            new ConcurrentDictionary<long, (int, float)>();

        public static IEnumerable<MethodBase> TargetMethods()
        {
            var sig = new[] { typeof(CableNetwork) };
            yield return AccessTools.Method(typeof(Fabricator), "GetUsedPower", sig);
            yield return AccessTools.Method(typeof(ArcFurnace), "GetUsedPower", sig);
            yield return AccessTools.Method(typeof(IceCrusher), "GetUsedPower", sig);
            yield return AccessTools.Method(typeof(VanillaFermenter), "GetUsedPower", sig);
            yield return AccessTools.Method(typeof(Bench), "GetUsedPower", sig);
            yield return AccessTools.Method(typeof(SuitStorage), "GetUsedPower", sig);
            yield return AccessTools.Method(typeof(WallLightBattery), "GetUsedPower", sig);
            yield return AccessTools.Method(typeof(BatteryCellCharger), "GetUsedPower", sig);
            yield return AccessTools.Method(typeof(AdvancedFurnace), "GetUsedPower", sig);
            yield return AccessTools.Method(typeof(VanillaVolumePump), "GetUsedPower", sig);
            yield return AccessTools.Method(typeof(VanillaTurboVolumePump), "GetUsedPower", sig);
            yield return AccessTools.Method(typeof(SatelliteDish), "GetUsedPower", sig);
            yield return AccessTools.Method(typeof(RocketMiner), "GetUsedPower", sig);
        }

        [HarmonyPrefix]
        public static bool Prefix(Device __instance, CableNetwork cableNetwork, ref float __result, out bool __state)
        {
            __state = false;
            if (!GameManager.RunSimulation) return true;            // client peer / menu / paused: vanilla
            var ownNet = __instance.PowerCableNetwork;
            if (ownNet == null || cableNetwork != ownNet) return true;   // sentinel path: never latch it

            if (_latch.TryGetValue(__instance.ReferenceId, out var entry)
                && entry.tick == ElectricityTickCounter.CurrentTick)
            {
                __result = entry.value;
                return false;   // repeat read within the tick: serve the latched value
            }
            __state = true;     // first read this tick: compute, then store in the postfix
            return true;
        }

        [HarmonyPostfix]
        public static void Postfix(Device __instance, ref float __result, bool __state)
        {
            if (!__state) return;
            _latch[__instance.ReferenceId] = (ElectricityTickCounter.CurrentTick, __result);
        }

        /// <summary>World-load reset: drop the previous world's entries.</summary>
        internal static void Clear()
        {
            _latch.Clear();
        }
    }

    /// <summary>
    ///     Producer-side latch closing the last forwarding gap: <c>PowerConnector.GetGeneratedPower</c>
    ///     (decompile 408014) forwards a DOCKED portable generator's <c>PowerGenerated</c> to ANY
    ///     asking network, with no network guard of its own, and both the dock reference
    ///     (OnChildEnter/ExitInventory, 408023-408048) and the generator's OnOff / Powered gate
    ///     inside <c>DynamicGenerator.PowerGenerated</c> (297398-297408) are main-thread-mutable
    ///     mid-tick. The latch mirrors the vanilla shape exactly: the docked path is latched for
    ///     every asking network; the undocked path (vanilla base: -1f foreign / 0f own) passes
    ///     through untouched. DynamicComposter cannot dock as a generator (it is a DraggableThing
    ///     sibling, not a DynamicGenerator subclass), so this closes the whole forwarding-producer
    ///     question from the census. The VVF / dead-net zeroing postfix
    ///     (ProducerFaultEnforcementPatches) runs on every call, cached or computed, so its
    ///     verdicts stay the final word.
    /// </summary>
    [HarmonyPatch(typeof(PowerConnector), nameof(PowerConnector.GetGeneratedPower))]
    public static class PowerConnectorOutputLatchPatches
    {
        private static readonly ConcurrentDictionary<long, (int tick, float value)> _latch =
            new ConcurrentDictionary<long, (int, float)>();

        [HarmonyPrefix]
        public static bool Prefix(PowerConnector __instance, ref float __result, out bool __state)
        {
            __state = false;
            if (!GameManager.RunSimulation) return true;
            if (__instance.ConnectedDynamicGenerator == null) return true;   // undocked: vanilla base path

            if (_latch.TryGetValue(__instance.ReferenceId, out var entry)
                && entry.tick == ElectricityTickCounter.CurrentTick)
            {
                __result = entry.value;
                return false;
            }
            __state = true;
            return true;
        }

        [HarmonyPostfix]
        public static void Postfix(PowerConnector __instance, ref float __result, bool __state)
        {
            if (!__state) return;
            _latch[__instance.ReferenceId] = (ElectricityTickCounter.CurrentTick, __result);
        }

        /// <summary>World-load reset: drop the previous world's entries.</summary>
        internal static void Clear()
        {
            _latch.Clear();
        }
    }
}
