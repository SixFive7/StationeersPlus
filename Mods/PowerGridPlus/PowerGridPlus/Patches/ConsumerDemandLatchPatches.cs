using System.Collections.Concurrent;
using Assets.Scripts;
using Assets.Scripts.Networks;
using Assets.Scripts.Objects.Electrical;
using HarmonyLib;

namespace PowerGridPlus.Patches
{
    /// <summary>
    ///     Tick-scoped first-read latch on <see cref="SimpleFabricatorBase.GetUsedPower"/>: the
    ///     first read of a fabricator inside an atomic electricity tick computes and caches the
    ///     value; every later read in the SAME tick returns the cached value. OBSERVE,
    ///     GATHER/ALLOCATE and ENFORCE therefore all see one demand number per fabricator per tick.
    ///     This is the demand-side twin of <see cref="SolarOutputLatchPatches"/> and
    ///     <see cref="WindTurbineOutputLatchPatches"/>, which pin the same coherence on the supply
    ///     side.
    ///
    ///     <para><b>Why.</b> A fabricator's draw is state the MAIN thread rewrites while the power
    ///     tick runs on the worker. Vanilla returns <c>OnOff ? UsedPower + _powerUsedDuringTick :
    ///     _powerUsedDuringTick</c>, and <c>_powerUsedDuringTick</c> is accumulated per FixedUpdate
    ///     frame inside the print coroutine <c>WaitThenMake</c> and zeroed in <c>ReceivePower</c>
    ///     (called at ENFORCE). When a print starts, a frame lands between ALLOCATE's read (idle
    ///     draw, the net is marked Served with the residual fully granted) and ENFORCE's re-read
    ///     (spiked draw). ENFORCE then computes Required &gt; the Potential the allocator granted
    ///     against, the vanilla power ratio drops below 1 on an already-served network, and the
    ///     fabricator reads <c>Powered == false</c> for exactly one tick with no transformer shed
    ///     (the shed decision was made at ALLOCATE on the idle value). That is the single-tick
    ///     depower blip on the autolathe: a consumer read-coherence tear, identical in shape to the
    ///     solar terminator dips but on the load side. The latch pins the tick to its first read, so
    ///     the print spike lands in the NEXT tick's allocation (where it is granted for properly, and
    ///     a transformer sheds only if the supply genuinely cannot cover it) instead of tearing the
    ///     current one.</para>
    ///
    ///     <para><b>Scope.</b> Only reads for the fabricator's real network latch: the
    ///     <c>PowerCable.CableNetwork == cableNetwork</c> test mirrors vanilla's own predicate for
    ///     returning the real draw, so a foreign-network call (which vanilla answers with its
    ///     <c>-1f</c> "not my network" sentinel) is never latched and never poisons the cache.
    ///     Latching runs only while <c>GameManager.RunSimulation</c> is true: on a client peer the
    ///     tick counter never advances, so latching there would freeze a stale value; clients and
    ///     paused/menu states stay on vanilla.</para>
    ///
    ///     <para><b>What it does and does not touch.</b> Only the READ is latched. Vanilla still
    ///     accumulates <c>_powerUsedDuringTick</c> on the main thread and still zeroes it in
    ///     <c>ReceivePower</c> at ENFORCE; the latch changes only what in-tick callers SEE, never the
    ///     underlying field or its reset. The marginal post-first-read accumulation that vanilla
    ///     resets without billing is the same effect present before the latch, so energy
    ///     conservation is unchanged (verified: ConservationChecker and the charge/discharge
    ///     delivery audits stay silent after deploy).</para>
    ///
    ///     <para><b>Targeting.</b> Patches <see cref="SimpleFabricatorBase"/> only, the known
    ///     variable-draw consumer whose demand steps per frame during production. The Autolathe and
    ///     the other fabricators inherit this method without overriding it, so the single patch
    ///     covers all of them. Rigid, non-fabricator consumers do not accumulate a per-frame draw and
    ///     are not latched here; blanket-latching every <c>GetUsedPower</c> would tangle with the
    ///     segmenter, storage, and soft-demand accounting that PowerGridPlus manages separately.</para>
    ///
    ///     <para><b>Threading.</b> The power worker reads in OBSERVE, the allocator's GATHER, and
    ///     ENFORCE CalculateState; tooltips, logic surfaces, and third-party mods can call
    ///     <c>GetUsedPower</c> from the main thread at any time. Cross-thread concurrency is
    ///     therefore real, so the latch is a <see cref="ConcurrentDictionary{K,V}"/> whose value
    ///     tuple is swapped atomically per entry: a racing reader sees the old or the new
    ///     (tick, value) pair, never a torn one.</para>
    ///
    ///     <para>Cleared on world load (FaultRegistryLoadPatches) so no stale ReferenceId entries
    ///     leak across a hot-swapped save. Entries are one small struct per fabricator; no eviction
    ///     needed between loads.</para>
    /// </summary>
    [HarmonyPatch(typeof(SimpleFabricatorBase), nameof(SimpleFabricatorBase.GetUsedPower))]
    public static class ConsumerDemandLatchPatches
    {
        private static readonly ConcurrentDictionary<long, (int tick, float value)> _latch =
            new ConcurrentDictionary<long, (int, float)>();

        [HarmonyPrefix]
        public static bool Prefix(SimpleFabricatorBase __instance, CableNetwork cableNetwork, ref float __result, out bool __state)
        {
            __state = false;
            if (!GameManager.RunSimulation) return true;   // client peer / menu / paused: vanilla
            var cable = __instance.PowerCable;
            if ((object)cable == null || cable.CableNetwork != cableNetwork) return true;  // vanilla's -1f path: never latch it

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
        public static void Postfix(SimpleFabricatorBase __instance, ref float __result, bool __state)
        {
            if (!__state) return;
            _latch[__instance.ReferenceId] = (ElectricityTickCounter.CurrentTick, __result);
        }

        /// <summary>World-load reset: drop the previous world's fabricator entries.</summary>
        internal static void Clear()
        {
            _latch.Clear();
        }
    }
}
