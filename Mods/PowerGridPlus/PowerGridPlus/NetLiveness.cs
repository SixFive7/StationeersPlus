using System.Collections.Generic;
using Assets.Scripts.Networks;

namespace PowerGridPlus
{
    /// <summary>
    ///     Per-net LIVE / DEAD verdict for the consumer Powered-ownership layer, published by
    ///     ALLOCATE's tail every tick (volatile reference swap, the ShortfallDiagnostics /
    ///     PoweredPresentation pattern) and consumed the same tick by ENFORCE: the
    ///     SetPowerFromThread false-edge block, the dead-net advertise zeroing, and the
    ///     PoweredOwnership sweep.
    ///
    ///     <para><b>The formula</b> (computed in PowerAllocator's publish tail, post-clawback):
    ///     LIVE iff the net's rigid demand is fully funded (<c>Unmet &lt;= Eps</c>) AND an
    ///     energized feed exists (<c>GenSupply + InflowCommitted + AvailableElastic &gt; Eps</c>).
    ///     A net that fails the first term is DEAD_UNMET (demand the allocator could not fund:
    ///     generation-short root nets, the step-up and cable-limited partial-delivery carve-outs);
    ///     a net that fails the second is DEAD_NOSUPPLY (nothing energized feeds it: night-time
    ///     solar islands, Served-by-vacuity idle nets, nets behind a shed / locked feed). The
    ///     supply term is the same expression the dead-input cue and the healthy-set gate already
    ///     use, so the verdict is definitionally aligned with what vanilla ApplyState can deliver
    ///     after the mod's enforcement caps: a DEAD net advertises nothing (the producer / seg
    ///     zeroing keyed off this verdict), so its Providers array is empty, ConsumePower delivers
    ///     nothing and never calls ReceivePower, per-tick accumulator debts freeze exactly, and
    ///     the power-ON branch cannot fire (decompile 271782-271792, 271820-271840; the
    ///     zero-Potential corollary on Research/GameClasses/PowerTick.md).</para>
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
    ///     <para><b>Unclassified</b> (a net id absent from the map) is not dead: fresh nets minted
    ///     by a mid-tick cable split are unclassified for one tick, and forcing them dark would
    ///     cancel prints on ordinary cable edits. The PoweredOwnership sweep freezes unclassified
    ///     devices (no write) and only fail-safes to dark after a persistent-unclassified streak.
    ///     The advertise zeroing likewise treats unclassified as not-dead.</para>
    ///
    ///     <para>Threading: the map is built and the hold table mutated only on the power worker
    ///     inside ALLOCATE; readers (ENFORCE patches, the sweep) run later on the same worker.
    ///     The volatile swap keeps any stray cross-thread reader coherent. Cleared on world load
    ///     (FaultRegistryLoadPatches).</para>
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

        /// <summary>
        ///     True when the net carries an explicit DEAD verdict this tick. Null and unclassified
        ///     nets are NOT dead (fresh splits keep vanilla behavior for a tick).
        /// </summary>
        internal static bool IsDead(CableNetwork net)
        {
            if (net == null) return false;
            return _byNet.TryGetValue(net.ReferenceId, out byte v) && v != Live;
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
