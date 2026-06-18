using System.Collections.Concurrent;

namespace PowerGridPlus
{
    /// <summary>
    ///     Per-transformer EXACT in-tick throughput (Watts) the Sweep allocator decides each tick
    ///     (POWER.md §8.0, Direction C). Two figures per device:
    ///
    ///     <list type="bullet">
    ///       <item><c>OutThroughput</c>: the power the transformer actually delivers on its OUTPUT
    ///       network this tick (its converged backward/forward-sweep throughput). The
    ///       <see cref="Patches.TransformerExploitPatches"/> <c>GetGeneratedPower</c> prefix reports this
    ///       verbatim in Sweep mode instead of the Legacy <c>min(Setting, InputNetwork.PotentialLoad)</c>
    ///       capacity figure, so a transformer-fed network's Potential equals its exact delivered supply
    ///       (no headroom). The <c>&gt;=</c> power-met boundary fix then keeps a fully served network
    ///       powered at supply == demand.</item>
    ///       <item><c>InDraw</c>: the power the transformer draws from its INPUT network this tick
    ///       (throughput + the device's own quiescent <c>UsedPower</c>, times the PT-pair distance factor
    ///       for a wireless link). The <c>GetUsedPower</c> prefix reports this in Sweep mode so the input
    ///       network is billed exactly what flows downstream plus the conversion loss -- conservation
    ///       holds and the free-power exploit stays closed, replacing the vanilla <c>_powerProvided</c>
    ///       handshake.</item>
    ///     </list>
    ///
    ///     <para>Inactive transformers (shed / overloaded / cycle-faulted this tick) get (0, 0).</para>
    ///
    ///     <para>Freshness-stamped, in-memory only, self-cleaning, exactly like
    ///     <see cref="SoftSupplyShareCache"/>: entries carry the tick they were written; a read older than
    ///     one tick falls back to "no fresh value" so the reporting patches use their Legacy / vanilla
    ///     formula. The allocator writes in Phase 2 DECIDE; Phase 3 ENFORCE (same tick) reads the current
    ///     value, Phase 1 OBSERVE (before DECIDE) reads last tick's -- both within the one-tick window,
    ///     and Phase 1's transformer output does not feed the allocator's own model.</para>
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
