using System.Collections.Concurrent;

namespace PowerGridPlus
{
    /// <summary>
    ///     Per-APC CELL-ONLY discharge share (Watts) for the <c>DischargeSpeed</c> LogicType display
    ///     (POWER.md §7.3 / deviation P9). An APC's <see cref="SoftSupplyShareCache"/> entry is the
    ///     BUNDLED supply (total passthrough + cell), because the APC's <c>GetGeneratedPower</c>
    ///     surface bundles passthrough with the cell (vanilla <c>AvailablePower</c>). But
    ///     <c>DischargeSpeed</c> means the same thing on every storage device: the cell's discharge rate.
    ///     The allocator already computes the APC cell's elastic share separately, so it stamps the
    ///     cell-only figure here and the APC <c>DischargeSpeed</c> read uses it instead of the bundled
    ///     cache. Batteries and umbilicals are pure elastic suppliers, so their bundled cache IS already
    ///     cell-only and they keep reading <see cref="SoftSupplyShareCache"/> directly.
    ///
    ///     <para>Display-only, in-memory, tick-freshness-stamped exactly like
    ///     <see cref="SoftSupplyShareCache"/>: a read older than one tick falls back to 0, so a stale
    ///     entry from a cable break or reassignment ages out without explicit invalidation.</para>
    /// </summary>
    internal static class ApcCellDischargeCache
    {
        private static readonly ConcurrentDictionary<long, (long tickWritten, float share)> _shareByRef =
            new ConcurrentDictionary<long, (long, float)>();

        internal static void SetShare(long referenceId, float share)
        {
            _shareByRef[referenceId] = (ElectricityTickCounter.CurrentTick, share);
        }

        // The cached cell-only share if fresh (written this tick or last), else 0.
        internal static float GetActualOrZero(long referenceId)
        {
            if (_shareByRef.TryGetValue(referenceId, out var e)
                && e.tickWritten >= ElectricityTickCounter.CurrentTick - 1)
                return e.share;
            return 0f;
        }
    }
}
