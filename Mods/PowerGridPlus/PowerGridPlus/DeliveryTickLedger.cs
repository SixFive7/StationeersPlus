using System.Collections.Concurrent;

namespace PowerGridPlus
{
    /// <summary>
    ///     Per-tick delivery-shaping allowances for store-crediting ReceivePower paths. Vanilla
    ///     ApplyState serves a device's bill in one ReceivePower call PER CONTRIBUTING PROVIDER
    ///     (PowerTick.ConsumePower, 0.2.6403 decompile 271820-271840), so any per-delivery
    ///     adjustment written as "subtract X every call" over-applies X on multi-provider networks.
    ///     This ledger turns such adjustments into per-tick allowances consumed across chunks:
    ///
    ///     <list type="bullet">
    ///       <item><see cref="TakeQuiescentBurn"/>: the device's own idle draw is burned out of the
    ///       delivered stream at most once per tick, and only up to the FUNDED amount the caller
    ///       derives from the published allocator totals (the grant-vs-delivery alignment rule:
    ///       every subtraction matches what was actually billed and funded).</item>
    ///       <item><see cref="TakeShareCredit"/>: the store credit is capped cumulatively at the
    ///       allocator's granted charge share for the tick, so a store can never absorb passthrough
    ///       or ledger watts as charge (credit == grant on served networks).</item>
    ///     </list>
    ///
    ///     <para>Both allowances seed lazily on the first call of a tick and expire by tick number
    ///     (entries from earlier ticks are re-seeded on touch, the TransformerSupplyCache freshness
    ///     pattern). Callers run inside ApplyState on the power worker, one tick at a time, so the
    ///     read-modify-write below is single-threaded per key; the ConcurrentDictionary covers the
    ///     cross-tick pool-thread handoffs like every other per-tick cache here. Cleared on world
    ///     load (FaultRegistryLoadPatches).</para>
    /// </summary>
    internal static class DeliveryTickLedger
    {
        private static readonly ConcurrentDictionary<long, (int tick, float left)> _quiescent =
            new ConcurrentDictionary<long, (int, float)>();
        private static readonly ConcurrentDictionary<long, (int tick, float left)> _share =
            new ConcurrentDictionary<long, (int, float)>();

        /// <summary>
        ///     Burn up to the device's funded quiescent out of a delivered chunk. Seeds the tick's
        ///     allowance with <paramref name="fundedQuiescent"/> on the first call, then hands out
        ///     at most <paramref name="chunk"/> per call until the allowance is gone. Returns the
        ///     amount burned (subtract it from the chunk before crediting anything).
        /// </summary>
        internal static float TakeQuiescentBurn(long refId, float fundedQuiescent, float chunk)
        {
            return Take(_quiescent, refId, fundedQuiescent, chunk);
        }

        /// <summary>
        ///     Consume store-credit allowance for a delivered chunk: at most the allocator's granted
        ///     charge share per tick, cumulatively across chunks. Seeds with
        ///     <paramref name="grantedShare"/> on the first call of the tick; returns how much of
        ///     <paramref name="want"/> may be credited.
        /// </summary>
        internal static float TakeShareCredit(long refId, float grantedShare, float want)
        {
            return Take(_share, refId, grantedShare, want);
        }

        private static float Take(ConcurrentDictionary<long, (int tick, float left)> map,
            long refId, float seed, float want)
        {
            if (want <= 0f) return 0f;
            int now = ElectricityTickCounter.CurrentTick;
            float left = map.TryGetValue(refId, out var e) && e.tick == now ? e.left : seed;
            if (left <= 0f)
            {
                map[refId] = (now, 0f);
                return 0f;
            }
            float take = want < left ? want : left;
            map[refId] = (now, left - take);
            return take;
        }

        /// <summary>World-load reset: drop the previous world's allowances.</summary>
        internal static void Clear()
        {
            _quiescent.Clear();
            _share.Clear();
        }
    }
}
