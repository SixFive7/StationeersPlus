using System.Collections.Concurrent;
using Assets.Scripts;
using Assets.Scripts.Networks;
using HarmonyLib;
using Objects;

namespace PowerGridPlus.Patches
{
    /// <summary>
    ///     Tick-scoped first-read latch on <see cref="WindTurbineGenerator.GetGeneratedPower"/>
    ///     (covers <c>LargeWindTurbineGenerator</c> too: it does not override the method), the wind
    ///     analog of <see cref="SolarOutputLatchPatches"/>: the first read of a turbine inside an
    ///     atomic electricity tick computes and caches the value; every later read in the SAME tick
    ///     returns the cached value, so OBSERVE, GATHER/ALLOCATE and ENFORCE all see one number per
    ///     turbine per tick.
    ///
    ///     <para><b>Why (per-class decompile verdict, 0.2.6403.27689).</b> Unlike the fuel
    ///     generators, the wind turbine's GetGeneratedPower RECOMPUTES its output on every call
    ///     (<c>CalculateGenerationRate()</c>, decompile 146959), and every factor of that formula
    ///     is mutable while the power tick runs on the worker: the static
    ///     <c>WindTurbineGenerator.WindStrength</c> is rewritten by <c>UpdateWind()</c> from
    ///     <c>GameManager.Update</c> EVERY FRAME on the main thread (call site 205218; simplex
    ///     noise over NetworkTime.time, so it moves continuously), the
    ///     <c>WeatherManager.CurrentWeatherEvent</c> storm state is main-thread too, and the world
    ///     atmosphere pressure read is another thread's data. Any of these stepping between
    ///     ALLOCATE's read and ENFORCE's re-read tears the tick exactly like the solar
    ///     efficiency step did (the transition-dip partial-power class): a downward step leaves
    ///     vanilla Potential below the granted Required on a served net. The latch pins the tick
    ///     to its first read; the real wind drift lands in the NEXT tick's allocation.</para>
    ///
    ///     <para>The other producer classes need NO latch (same decompile pass): gas / solid-fuel
    ///     generators and the Stirling engine return a plain field (<c>_energyAsPower</c> /
    ///     <c>PowerGenerated</c> gated on <c>PoweredTicks</c> / <c>EnergyAsPower</c>) written only
    ///     in <c>OnAtmosphericTick</c>, which runs sequentially in the same GameTick chain as the
    ///     electricity tick (never concurrent); the wall turbine (TurbineGenerator) likewise
    ///     returns a field written in its OnAtmosphericTick; the RTG returns a constant field. A
    ///     field that cannot change during the electricity tick needs no latch, so none is
    ///     installed (a latch is not free: it swallows the method's side effects on repeat
    ///     reads).</para>
    ///
    ///     <para><b>Scope.</b> Only reads for the turbine's real network latch (the
    ///     <c>cableNetwork == PowerCableNetwork</c> guard mirrors vanilla's early-out; mismatched
    ///     calls run vanilla, which writes 0 and returns it), and only while
    ///     <c>GameManager.RunSimulation</c> is true (on a client peer the tick counter never
    ///     advances, so latching there would freeze a stale value). Other postfixes on the method
    ///     (the VVF producer-fault zeroing, the NaN sanitizer) still run when the prefix serves
    ///     the cached value, so their verdicts stay the final word. The first read each tick still
    ///     executes the vanilla body, so the <c>_generatedPower</c> field the logic surface reads
    ///     updates once per tick (a cadence change only, same note as the solar latch).</para>
    ///
    ///     <para>Threading and lifecycle mirror the solar latch: a ConcurrentDictionary whose
    ///     value tuple swaps atomically per entry (a racing main-thread reader sees old or new,
    ///     never torn); cleared on world load by FaultRegistryLoadPatches.</para>
    /// </summary>
    [HarmonyPatch(typeof(WindTurbineGenerator), nameof(WindTurbineGenerator.GetGeneratedPower))]
    public static class WindTurbineOutputLatchPatches
    {
        private static readonly ConcurrentDictionary<long, (int tick, float value)> _latch =
            new ConcurrentDictionary<long, (int, float)>();

        [HarmonyPrefix]
        public static bool Prefix(WindTurbineGenerator __instance, CableNetwork cableNetwork, ref float __result, out bool __state)
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
        public static void Postfix(WindTurbineGenerator __instance, ref float __result, bool __state)
        {
            if (!__state) return;
            _latch[__instance.ReferenceId] = (ElectricityTickCounter.CurrentTick, __result);
        }

        /// <summary>World-load reset: drop the previous world's turbine entries.</summary>
        internal static void Clear()
        {
            _latch.Clear();
        }
    }
}
