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
    ///     LIVE iff the net's rigid demand is fully funded (<c>Unmet &lt;= Eps</c>) AND an
    ///     energized feed exists (<c>GenSupply + InflowCommitted + AvailableElastic &gt; Eps</c>).
    ///     A net that fails the first term is DEAD_UNMET (demand the allocator could not fund:
    ///     generation-short root nets, the step-up and cable-limited partial-delivery carve-outs);
    ///     a net that fails the second is DEAD_NOSUPPLY (nothing energized feeds it: night-time
    ///     solar islands, Served-by-vacuity idle nets, nets behind a shed / locked feed). The
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
    ///     the mod's shed / overload lockout cadence, so a genuinely undersized net strobes at the
    ///     same player-legible rhythm as every other fault. DEAD_NOSUPPLY deliberately does NOT
    ///     hold: supply-side recovery (sunrise, a battery cohort re-arming, a shed lockout
    ///     expiring) must re-power the net the tick it returns.</para>
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
        private static volatile int _publishedTick = -1;

        /// <summary>Tick the current map was published for; -1 before the first atomic tick.</summary>
        internal static int PublishedTick => _publishedTick;

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

        /// <summary>World-load reset: drop the previous world's verdicts and holds.</summary>
        internal static void Clear()
        {
            _byNet = new Dictionary<long, byte>();
            _deadUnmetHoldUntil.Clear();
            _publishedTick = -1;
        }
    }
}
