using System.Collections.Concurrent;

namespace PowerGridPlus
{
    /// <summary>
    ///     Per-contributor EXACT in-tick presentation totals (Watts) the allocator decides each tick
    ///     (POWER.md, the allocator section), written for EVERY routed seg kind (Transformer, wireless
    ///     PT/PR pair, APC). Two figures per device, each the sum of BOTH flow classes (rigid demand
    ///     passthrough + granted storage-charge flow):
    ///
    ///     <list type="bullet">
    ///       <item><c>OutThroughput</c>: the total power the contributor actually delivers on its OUTPUT
    ///       network this tick (its converged backward/forward-sweep rigid throughput plus its granted
    ///       soft-charge flow). The <see cref="Patches.TransformerExploitPatches"/>
    ///       <c>GetGeneratedPower</c> prefix reports this verbatim (the wireless pair through the
    ///       <see cref="Patches.PowerTransmitterDrawPatches"/> delivery-gate clamp), so a
    ///       contributor-fed network's Potential equals its exact delivered supply (no headroom). The
    ///       <c>&gt;=</c> power-met boundary fix then keeps a fully served network powered at
    ///       supply == demand.</item>
    ///       <item><c>InDraw</c>: the total power the contributor draws from its INPUT network this tick
    ///       (total throughput times the PT-pair distance factor for a wireless link, plus the device's
    ///       own quiescent <c>UsedPower</c> when it conducts). The <c>GetUsedPower</c> prefix reports
    ///       this so the input network is billed exactly what flows downstream plus the conversion
    ///       loss -- conservation holds and the free-power exploit stays closed, replacing the vanilla
    ///       <c>_powerProvided</c> handshake. Billing the SOFT component here is what lets storage
    ///       charge behind a transformer or wireless link: the charge flow exists on both terminals of
    ///       the segment in the same tick.</item>
    ///     </list>
    ///
    ///     <para>Inactive contributors (deprioritized / overloaded / cycle-faulted this tick) get (0, 0). The APC
    ///     entry exists for surface uniformity; the APC billing patches read
    ///     <see cref="ApcPassthroughCache"/> / <see cref="SoftSupplyShareCache"/> instead.</para>
    ///
    ///     <para>Freshness-stamped, in-memory only, self-cleaning, exactly like
    ///     <see cref="SoftSupplyShareCache"/>: entries carry the tick they were written; a read older than
    ///     one tick reports "no fresh value" so the reporting patches report 0 until the allocator roster
    ///     includes the device again. The allocator writes in ALLOCATE; ENFORCE (same tick)
    ///     reads the current value, OBSERVE (before ALLOCATE) reads last tick's -- both within the
    ///     one-tick window, and OBSERVE's transformer output does not feed the allocator's own model.</para>
    /// </summary>
    internal static class TransformerSupplyCache
    {
        private static readonly ConcurrentDictionary<long, (long tickWritten, float outThroughput, float inDraw)> _byRef =
            new ConcurrentDictionary<long, (long, float, float)>();

        internal static void Set(long referenceId, float outThroughput, float inDraw)
        {
            _byRef[referenceId] = (ElectricityTickCounter.CurrentTick, outThroughput, inDraw);
        }

        // True + the delivered output throughput if a fresh entry exists (written this tick or last).
        internal static bool TryGetOutput(long referenceId, out float outThroughput)
        {
            if (_byRef.TryGetValue(referenceId, out var e)
                && e.tickWritten >= ElectricityTickCounter.CurrentTick - 1)
            {
                outThroughput = e.outThroughput;
                return true;
            }
            outThroughput = 0f;
            return false;
        }

        // True + the input-side draw if a fresh entry exists (written this tick or last).
        internal static bool TryGetInputDraw(long referenceId, out float inDraw)
        {
            if (_byRef.TryGetValue(referenceId, out var e)
                && e.tickWritten >= ElectricityTickCounter.CurrentTick - 1)
            {
                inDraw = e.inDraw;
                return true;
            }
            inDraw = 0f;
            return false;
        }
    }
}
