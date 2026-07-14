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
    ///       a supplier seg that is lockout-locked / deprioritized / overloaded or has zero effective
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

        // Ratio-contract scope, published in the same ALLOCATE tail as the classification
        // snapshot: the net ids where a delivery shortfall could deprive a member of granted
        // power (at least one non-segmenter power device, or a storage charge request this
        // tick). Bridge-only nets (every power member a routed segmenter: wireless carrier
        // nets, tower-top hop nets) are absent, because their bills and advertises are
        // cache-governed and their Powered state is reconciled at the ENFORCE tail, so a sub-1
        // delivery ratio there is inert. Consumed via reflection by the ScenarioRunner census;
        // same swap / clear lifecycle as the snapshot.
        private static volatile HashSet<long> _ratioScope = new HashSet<long>();

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

        /// <summary>Swap in this tick's ratio-contract scope set (end of ALLOCATE, allocator
        /// worker). The set is immutable after publish.</summary>
        internal static void PublishRatioScope(HashSet<long> ratioDeprivableNetIds)
        {
            _ratioScope = ratioDeprivableNetIds;
        }

        /// <summary>
        ///     True when a delivery shortfall on the network could deprive a member of granted
        ///     power this tick (the ratio-contract membership gate). Consumed via reflection by
        ///     the ScenarioRunner injection scenario's target hunt.
        /// </summary>
        internal static bool InRatioScope(long cableNetworkId)
        {
            return _ratioScope.Contains(cableNetworkId);
        }

        /// <summary>World-load reset: drop the previous world's snapshot and scope set.</summary>
        internal static void Clear()
        {
            _byNet = new Dictionary<long, byte>();
            _ratioScope = new HashSet<long>();
        }
    }
}
