using System.Collections.Concurrent;

namespace PowerGridPlus
{
    /// <summary>
    ///     Per-soft-demander charge share (Watts) the allocator's forward sweep grants each tick for
    ///     batteries / APCs / rocket umbilicals (POWER.md §7.4 / POWERTODO 3). The soft-demand
    ///     <c>GetUsedPower</c> postfixes cap the reported charge demand to this allocated share so a battery
    ///     charges only its fair slice of the input network's firm residual.
    ///
    ///     <para>Freshness-stamped, in-memory only, self-cleaning: entries carry the tick they were
    ///     written; a read older than one tick is distrusted and falls back to vanilla. The one-tick
    ///     window means OBSERVE reads last tick's share (smooth) and ENFORCE reads
    ///     this tick's fresh allocation.</para>
    /// </summary>
    internal static class SoftDemandShareCache
    {
        private static readonly ConcurrentDictionary<long, (long tickWritten, float share)> _shareByRef =
            new ConcurrentDictionary<long, (long, float)>();

        internal static void SetShare(long referenceId, float share)
        {
            _shareByRef[referenceId] = (ElectricityTickCounter.CurrentTick, share);
        }

        // For the GetUsedPower postfix: the cached share if fresh, else false (pass vanilla through).
        internal static bool TryGetShare(long referenceId, out float share)
        {
            if (_shareByRef.TryGetValue(referenceId, out var e)
                && e.tickWritten >= ElectricityTickCounter.CurrentTick - 1)
            {
                share = e.share;
                return true;
            }
            share = 0f;
            return false;
        }

        // For the ChargeSpeed LogicType display: the cached share if fresh, else 0.
        internal static float GetActualOrZero(long referenceId)
        {
            if (_shareByRef.TryGetValue(referenceId, out var e)
                && e.tickWritten >= ElectricityTickCounter.CurrentTick - 1)
                return e.share;
            return 0f;
        }
    }
}
