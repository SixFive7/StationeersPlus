using System.Collections.Generic;

namespace PowerGridPlus
{
    /// <summary>
    ///     Per-net shortfall classification snapshot, published by <see cref="PowerAllocator"/> at
    ///     the end of ALLOCATE for the regression census (the ScenarioRunner pgp-rearch-suite joins
    ///     it via reflection). Labels every allocator net's end-of-tick RIGID state:
    ///
    ///     <list type="bullet">
    ///       <item><see cref="Served"/> (0): no unmet rigid demand (Unmet within the allocator's
    ///       epsilon).</item>
    ///       <item><see cref="Dry"/> (1): unmet, and every remaining feed was genuinely exhausted:
    ///       each active supplier seg is either saturated (no headroom above its committed flow) or
    ///       draws from an input net that retained no undelivered supply. Source-side shortage
    ///       (dead-input chains, unaimed solar islands). Honest darkness.</item>
    ///       <item><see cref="Throttled"/> (2): unmet, and some feed valve is deliberately closed:
    ///       a supplier seg that is lockout-locked / shed / overloaded or has zero effective
    ///       capacity (Setting=0 "firewall", rate-limited to zero), or a locked / overloaded
    ///       elastic on the net. Honest darkness (the player or a fault closed the valve).</item>
    ///       <item><see cref="Deadlock"/> (3): unmet any other way, notably: an open supplier had
    ///       headroom AND its input net kept undelivered supply, i.e. the allocator's own
    ///       accounting says power existed but was not routed. The invisible-deadlock regression
    ///       shape; zero on a healthy build.</item>
    ///     </list>
    ///
    ///     <para>A net ABSENT from the snapshot was outside allocator scope this tick (no power
    ///     devices gathered, allocator not yet run, client peer); the census reads absence as
    ///     "off-scope". The byte values above are a cross-assembly contract with the suite's
    ///     reflection join: renumbering them breaks the census buckets.</para>
    ///
    ///     <para>Diagnostics only: derived entirely from values the allocator already computed for
    ///     the tick. Nothing in the mod reads it back; no allocation math, ordering, or cache
    ///     content depends on it.</para>
    ///
    ///     <para>Threading: the allocator worker publishes one immutable dictionary per tick by a
    ///     single volatile reference swap, exactly like the <see cref="PoweredPresentation"/>
    ///     snapshots. The suite reads on the sim-tick pump after the atomic tick, so it always
    ///     joins the same tick's snapshot; volatile keeps any cross-thread reader coherent.</para>
    /// </summary>
    internal static class ShortfallDiagnostics
    {
        internal const byte Served = 0;
        internal const byte Dry = 1;
        internal const byte Throttled = 2;
        internal const byte Deadlock = 3;

        private static volatile Dictionary<long, byte> _byNet = new Dictionary<long, byte>();

        /// <summary>Swap in this tick's snapshot (end of ALLOCATE, allocator worker). The
        /// dictionary is immutable after publish.</summary>
        internal static void Publish(Dictionary<long, byte> classificationByNetId)
        {
            _byNet = classificationByNetId;
        }

        /// <summary>
        ///     This tick's classification for a cable network (keyed by
        ///     <c>CableNetwork.ReferenceId</c>, the allocator's net key). False when the net was
        ///     outside allocator scope this tick (off-scope). Consumed via reflection by the
        ///     ScenarioRunner census.
        /// </summary>
        internal static bool TryClassify(long cableNetworkId, out byte cls)
        {
            var snapshot = _byNet;
            return snapshot.TryGetValue(cableNetworkId, out cls);
        }

        /// <summary>World-load reset: drop the previous world's snapshot.</summary>
        internal static void Clear()
        {
            _byNet = new Dictionary<long, byte>();
        }
    }
}
