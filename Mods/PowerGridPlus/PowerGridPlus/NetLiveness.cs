using System.Collections.Generic;

namespace PowerGridPlus
{
    /// <summary>
    ///     Per-net LIVE / DEAD verdict for the consumer Powered-ownership layer, published by
    ///     ALLOCATE's tail every tick (volatile reference swap, the ShortfallDiagnostics /
    ///     PoweredPresentation pattern) and consumed the same tick by the write-back (energy
    ///     settlement and the accumulator drains skip DEAD nets) and the PoweredOwnership sweep.
    ///
    ///     <para><b>The formula</b> (computed in PowerAllocator's publish tail, post-clawback):
    ///     LIVE iff an energized feed exists (<c>GenSupply + InflowCommitted + AvailableElastic
    ///     &gt; Eps</c>) AND the net's rigid demand is fully funded (<c>Unmet &lt;= Eps</c>).
    ///     Supply-absence is tested FIRST (decision-33 hold fix): a net that nothing energized
    ///     feeds is DEAD_NOSUPPLY even when it carries rigid demand (night-time solar islands,
    ///     Served-by-vacuity idle nets, nets behind a deprioritized / locked feed, and unfed
    ///     under-construction stubs, which previously classified DEAD_UNMET and armed the hold
    ///     every tick they existed); a FED net whose rigid demand the allocator could not fully
    ///     fund is DEAD_UNMET (generation-short root nets, the partial-delivery carve-outs). The
    ///     supply term is the same expression the dead-input cue and the presentation health gate
    ///     already use. A DEAD net receives nothing at the settlement layer by construction (the
    ///     write-back plan, the published caches, and the audit grants are all dead-zeroed), so
    ///     accumulator debts freeze exactly and are billed in full on revival.</para>
    ///
    ///     <para><b>Hold-down.</b> A DEAD_UNMET verdict arms a 120-tick (60 s) hold on the net.
    ///     While held, a formula-LIVE result is forced back to DEAD_UNMET. Without it, a net whose
    ///     demand is work-dependent (a satellite dish that drops to idle draw when its contact is
    ///     lost, a furnace that self-switched off) collapses its demand as soon as it goes dark,
    ///     reads Served next tick, re-powers, re-raises demand, and flaps at 2 Hz. 60 s matches
    ///     the mod's deprioritization / overload lockout cadence, so a genuinely undersized net strobes at the
    ///     same player-legible rhythm as every other fault. DEAD_NOSUPPLY deliberately does NOT
    ///     hold: supply-side recovery (sunrise, a battery cohort re-arming, a deprioritization lockout
    ///     expiring) must re-power the net the tick it returns.</para>
    ///
    ///     <para><b>Merge boundary.</b> Holds are keyed by net ReferenceId, and a cable-network
    ///     MERGE changes the identity under that key: the surviving object absorbs members the
    ///     held verdict never judged (the fresh-device 60 s darkness rode exactly that, an unfed
    ///     stub's ever-refreshed hold surviving the connecting merge onto the whole powered
    ///     trunk). The merge patch therefore enqueues every merging net's id from the main thread
    ///     and the tick head drains them out of the hold table on the worker before any verdict is
    ///     computed; a merged net is a new topology and the next tick decides it fresh. Splits
    ///     need nothing: a split spawns a FRESH ReferenceId that cannot be in the table.</para>
    ///
    ///     <para><b>Unclassified</b> (a net id absent from the map) is not dead. The verdict map is
    ///     computed from the same GridSnapshot the consumers iterate, so in practice every consumed
    ///     net has a same-tick verdict; a miss is a no-write fail-safe.</para>
    ///
    ///     <para>Threading: the map is built and the hold table mutated only on the power worker
    ///     inside ALLOCATE; readers (the write-back, the sweep) run later on the same worker.
    ///     The volatile swap keeps any stray cross-thread reader coherent. Cleared at the load
    ///     boundary (Core/LoadBoundary, on the worker).</para>
    /// </summary>
    internal static class NetLiveness
    {
        internal const byte Live = 1;
        internal const byte DeadUnmet = 2;
        internal const byte DeadNoSupply = 3;

        private const int DeadUnmetHoldTicks = 120;   // 60 s at 2 Hz, the lockout cadence
        private const int PruneEveryTicks = 600;

        private static volatile Dictionary<long, byte> _byNet = new Dictionary<long, byte>();
        // Worker-only (ALLOCATE) state; never read outside ApplyHold/Publish/Clear.
        private static readonly Dictionary<long, int> _deadUnmetHoldUntil = new Dictionary<long, int>();
        private static readonly List<long> _pruneScratch = new List<long>();
        // Main-thread producer (the merge patch), worker consumer (the tick-head drain); the list
        // itself is the lock. See the class doc, "Merge boundary".
        private static readonly List<long> _pendingMergeClears = new List<long>();
        private static volatile int _publishedTick = -1;

        /// <summary>Tick the current map was published for; -1 before the first atomic tick.</summary>
        internal static int PublishedTick => _publishedTick;

        /// <summary>
        ///     Main-thread note from the merge-determinism patch: this net participated in a merge,
        ///     so its hold entry (if any) is stale under its id and must be dropped at the next
        ///     tick head. Survivor and consumed nets alike.
        /// </summary>
        internal static void NoteMergedNet(long netId)
        {
            lock (_pendingMergeClears)
            {
                _pendingMergeClears.Add(netId);
            }
        }

        /// <summary>
        ///     Tick-head drain (power worker, HOUSEKEEPING, before any verdict is computed or any
        ///     hold armed this tick): drop the hold entries of every net that merged since the
        ///     last tick. Removing an absent key is a no-op, so consumed nets cost nothing.
        /// </summary>
        internal static void DrainMergeClears()
        {
            lock (_pendingMergeClears)
            {
                for (int i = 0; i < _pendingMergeClears.Count; i++)
                    _deadUnmetHoldUntil.Remove(_pendingMergeClears[i]);
                _pendingMergeClears.Clear();
            }
        }

        /// <summary>
        ///     Fold the hold-down into a formula verdict for one net (ALLOCATE worker only).
        ///     DEAD_UNMET arms / refreshes the hold; a formula-LIVE result while held is forced
        ///     back to DEAD_UNMET so demand-collapse cannot flap the net at 2 Hz.
        /// </summary>
        internal static byte ApplyHold(long netId, byte formulaVerdict, int currentTick)
        {
            if (formulaVerdict == DeadUnmet)
            {
                _deadUnmetHoldUntil[netId] = currentTick + DeadUnmetHoldTicks;
                return DeadUnmet;
            }
            if (formulaVerdict == Live && _deadUnmetHoldUntil.TryGetValue(netId, out int until))
            {
                if (until >= currentTick) return DeadUnmet;
                _deadUnmetHoldUntil.Remove(netId);
            }
            return formulaVerdict;
        }

        /// <summary>Swap in this tick's verdict map (end of ALLOCATE, allocator worker).</summary>
        internal static void Publish(Dictionary<long, byte> verdicts, int currentTick)
        {
            _byNet = verdicts;
            _publishedTick = currentTick;
            if (currentTick % PruneEveryTicks == 0 && _deadUnmetHoldUntil.Count > 0)
            {
                _pruneScratch.Clear();
                foreach (var kv in _deadUnmetHoldUntil)
                    if (kv.Value < currentTick) _pruneScratch.Add(kv.Key);
                for (int i = 0; i < _pruneScratch.Count; i++)
                    _deadUnmetHoldUntil.Remove(_pruneScratch[i]);
            }
        }

        internal static bool TryGetVerdict(long netId, out byte verdict)
        {
            return _byNet.TryGetValue(netId, out verdict);
        }

        /// <summary>World-load reset: drop the previous world's verdicts, holds, and merge notes.</summary>
        internal static void Clear()
        {
            _byNet = new Dictionary<long, byte>();
            _deadUnmetHoldUntil.Clear();
            lock (_pendingMergeClears)
            {
                _pendingMergeClears.Clear();
            }
            _publishedTick = -1;
        }
    }
}
