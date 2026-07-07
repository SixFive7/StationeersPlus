using System.Collections.Concurrent;
using Assets.Scripts;
using Assets.Scripts.Networks;
using Assets.Scripts.Objects.Electrical;
using HarmonyLib;

namespace PowerGridPlus.Patches
{
    /// <summary>
    ///     Tick-scoped first-read latch on <see cref="SolarPanel.GetGeneratedPower"/>: the first
    ///     read of a panel inside an atomic electricity tick computes and caches the value; every
    ///     later read in the SAME tick returns the cached value. OBSERVE, GATHER/ALLOCATE and
    ///     ENFORCE therefore all see one number per panel per tick.
    ///
    ///     <para><b>Why.</b> A panel's output is derived from state the MAIN thread rewrites while
    ///     the power tick runs on the worker: <c>GenerationEfficiency</c> is recomputed by
    ///     <c>ElectricityManager.SolarProcessing</c> one radiator per FixedUpdate frame (a
    ///     round-robin over every solar radiator, so on a big map each panel's output is a step
    ///     function stepping once per many ticks), and <c>OrbitalSimulation.SolarIrradiance</c>
    ///     moves continuously. When a step lands between ALLOCATE's read and ENFORCE's re-read,
    ///     vanilla's Potential diverges from the supply the allocator granted against, and on a
    ///     net whose firm residual was fully granted (daytime battery charging) any downward step
    ///     drops the vanilla ratio below 1 on a served network: the transition-clustered
    ///     partial-power dips (deepest at the day/night terminator, where a single step can be a
    ///     panel's whole output). The latch pins the tick to its first read, so the real solar
    ///     drift lands in the NEXT tick's allocation instead of tearing the current one.</para>
    ///
    ///     <para><b>Scope.</b> Only reads for the panel's real network latch (the
    ///     <c>cableNetwork == PowerCableNetwork</c> guard mirrors vanilla's early-out; mismatched
    ///     calls return vanilla's 0 unlatched), and only while <c>GameManager.RunSimulation</c> is
    ///     true: on a client peer the tick counter never advances, so latching there would freeze
    ///     a stale value; clients and paused/menu states stay on vanilla. Other postfixes on the
    ///     same method (the VVF producer-fault zeroing, the NaN sanitizer) still run when the
    ///     prefix serves the cached value, so their verdicts stay the final word either way. The
    ///     vanilla <c>OnPowerGenerateRate</c> event fires only on the computing read (once per
    ///     tick instead of once per walk), which is at worst a cadence change for listeners.</para>
    ///
    ///     <para><b>Threading.</b> Call-site census: the power worker reads in OBSERVE / ENFORCE
    ///     CalculateState (plus the PowerProvider constructor's second read), the allocator's
    ///     GATHER, and the section 5.7 burn check; tooltips, logic surfaces, and third-party mods
    ///     can call <c>Device.GetGeneratedPower</c> from the main thread at any time. Cross-thread
    ///     concurrency is therefore real, so the latch is a <see cref="ConcurrentDictionary{K,V}"/>
    ///     whose value tuple is swapped atomically per entry: a racing reader sees the old or the
    ///     new (tick, value) pair, never a torn one.</para>
    ///
    ///     <para>Cleared on world load (FaultRegistryLoadPatches) so no stale ReferenceId entries
    ///     leak across a hot-swapped save. Entries are one small struct per panel; no eviction
    ///     needed between loads.</para>
    /// </summary>
    [HarmonyPatch(typeof(SolarPanel), nameof(SolarPanel.GetGeneratedPower))]
    public static class SolarOutputLatchPatches
    {
        private static readonly ConcurrentDictionary<long, (int tick, float value)> _latch =
            new ConcurrentDictionary<long, (int, float)>();

        [HarmonyPrefix]
        public static bool Prefix(SolarPanel __instance, CableNetwork cableNetwork, ref float __result, out bool __state)
        {
            __state = false;
            if (!GameManager.RunSimulation) return true;                       // client peer / menu / paused: vanilla
            if (cableNetwork == null || __instance.PowerCableNetwork == null
                || cableNetwork != __instance.PowerCableNetwork) return true;  // vanilla's early-0 path: never latch it

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
        public static void Postfix(SolarPanel __instance, ref float __result, bool __state)
        {
            if (!__state) return;
            _latch[__instance.ReferenceId] = (ElectricityTickCounter.CurrentTick, __result);
        }

        /// <summary>World-load reset: drop the previous world's panel entries.</summary>
        internal static void Clear()
        {
            _latch.Clear();
        }
    }
}
