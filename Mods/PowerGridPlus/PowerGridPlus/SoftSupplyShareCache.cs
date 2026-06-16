using System.Collections.Concurrent;

namespace PowerGridPlus
{
    /// <summary>
    ///     Per-supplier elastic-discharge share (Watts) the allocator decides each tick for batteries,
    ///     APCs, and rocket umbilicals (POWER.md §7.3 / POWERTODO 0.2.5). The supply-side
    ///     <c>GetGeneratedPower</c> postfixes clamp to this so a battery delivers only the network shortfall
    ///     it was allocated, not its raw <c>PowerStored</c> (which would otherwise inflate Potential and, at
    ///     low charge, partial-power the rigid loads on its output net).
    ///
    ///     <para>Freshness-stamped, in-memory only, self-cleaning (POWERTODO Q4): entries carry the tick
    ///     they were written; a read older than one tick is distrusted and falls back to vanilla. No explicit
    ///     invalidation. Stale entries from a cable break or supplier reassignment age out naturally.</para>
    ///
    ///     <para>Populated every tick by PowerAllocator's elastic pass (step 4). For APCs the stored
    ///     share is passthrough + grant-through + cell share, because the APC's GetGeneratedPower
    ///     surface bundles passthrough with the cell (vanilla AvailablePower).</para>
    /// </summary>
    internal static class SoftSupplyShareCache
    {
        private static readonly ConcurrentDictionary<long, (long tickWritten, float share)> _shareByRef =
            new ConcurrentDictionary<long, (long, float)>();

        internal static void SetShare(long referenceId, float share)
        {
            _shareByRef[referenceId] = (ElectricityTickCounter.CurrentTick, share);
        }

        // For the GetGeneratedPower postfix: the cached share if fresh (written this tick or last), else
        // float.MaxValue so Mathf.Min(__result, GetShare(...)) leaves the vanilla value untouched.
        internal static float GetShare(long referenceId)
        {
            if (_shareByRef.TryGetValue(referenceId, out var e)
                && e.tickWritten >= ElectricityTickCounter.CurrentTick - 1)
                return e.share;
            return float.MaxValue;
        }

        // For the DischargeSpeed LogicType display: the cached share if fresh, else 0.
        internal static float GetActualOrZero(long referenceId)
        {
            if (_shareByRef.TryGetValue(referenceId, out var e)
                && e.tickWritten >= ElectricityTickCounter.CurrentTick - 1)
                return e.share;
            return 0f;
        }
    }
}
