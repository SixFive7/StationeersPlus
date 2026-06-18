using System.Collections.Concurrent;

namespace PowerGridPlus
{
    /// <summary>
    ///     Per-Area-Power-Control FRESH passthrough draw (Watts) the Sweep allocator decides each tick:
    ///     the power flowing INPUT-&gt;OUTPUT through the APC this tick (its committed rigid passthrough
    ///     <c>seg.Throughput</c> plus any soft-charge grant flowing through it, <c>seg.GrantThrough</c>).
    ///
    ///     <para>Vanilla / Legacy <c>AreaPowerControl.GetUsedPower(InputNetwork)</c> bills this passthrough
    ///     from <c>_powerProvided</c>, the accumulator filled during the PREVIOUS tick's ApplyState, so the
    ///     input-side draw lags the output-side delivery by one tick (see
    ///     <see cref="Research"/> Device.md "_powerProvided one-tick lag"). In Sweep the allocator sizes
    ///     upstream supply to the APC's CURRENT pull, so billing a stale <c>_powerProvided</c> on the input
    ///     leaves the input network short by the one-tick demand change (the net-503288 flicker). The
    ///     <see cref="Patches.AreaPowerControlPatches"/> <c>GetUsedPower</c> prefix reads this fresh figure
    ///     instead, so input == output and the input network's Potential matches its Required exactly.</para>
    ///
    ///     <para>Tick-freshness-stamped and self-cleaning, exactly like <see cref="SoftSupplyShareCache"/>:
    ///     a read older than one tick falls back to the vanilla <c>_powerProvided</c> path.</para>
    /// </summary>
    internal static class ApcPassthroughCache
    {
        private static readonly ConcurrentDictionary<long, (long tickWritten, float passthrough)> _byRef =
            new ConcurrentDictionary<long, (long, float)>();

        internal static void Set(long referenceId, float passthrough)
        {
            _byRef[referenceId] = (ElectricityTickCounter.CurrentTick, passthrough);
        }

        internal static bool TryGet(long referenceId, out float passthrough)
        {
            if (_byRef.TryGetValue(referenceId, out var e)
                && e.tickWritten >= ElectricityTickCounter.CurrentTick - 1)
            {
                passthrough = e.passthrough;
                return true;
            }
            passthrough = 0f;
            return false;
        }
    }
}
