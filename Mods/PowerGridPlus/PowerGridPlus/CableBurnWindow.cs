using System.Collections.Generic;

namespace PowerGridPlus
{
    /// <summary>
    ///     Per-network sliding 20-tick window of direct-generator power, for the §5.7 deterministic
    ///     cable-burn rule (see <see cref="Patches.PowerTickPatches"/>). A cable burns when the 20-tick
    ///     running average of the generator power flowing on a network exceeds the weakest cable's cap;
    ///     the victim is the cable at the output of the generator that produced the most over that same
    ///     window. No randomness, no settings -- fully deterministic and reproducible.
    ///
    ///     <para>20 ticks = 10 seconds at the 2 Hz electricity tick: a network may run over its cable
    ///     rating for up to that long before a burn, and a single spike must be countered by an
    ///     equivalent dip within the window or the average crosses the cap. The window is reset on a
    ///     burn so one sustained overload cannot burn a second cable before the split lands and the
    ///     (now smaller) network re-accumulates a fresh window.</para>
    ///
    ///     <para>State is in-memory and host-only (the burn check runs on the power-tick worker, one
    ///     network at a time, so a plain Dictionary is safe; the same pattern as
    ///     <see cref="VoltageTierEnforcer"/>'s tier cache). Tracking is created lazily and only persists
    ///     for networks that actually carry generator power, so load / transformer networks cost nothing.
    ///     Cleared on world load.</para>
    /// </summary>
    internal static class CableBurnWindow
    {
        internal const int WindowTicks = 20;        // 10 s at the 2 Hz electricity tick
        private const float Epsilon = 0.0001f;

        // A 20-slot ring with a maintained running sum. Push overwrites the slot at the shared window
        // position, so all rings on a network evict in lockstep.
        private sealed class Ring
        {
            public readonly float[] Slots = new float[WindowTicks];
            public float Sum;

            public void Push(int pos, float value)
            {
                Sum -= Slots[pos];
                Slots[pos] = value;
                Sum += value;
                if (Sum < 0f) Sum = 0f;   // guard accumulated float drift
            }
        }

        private sealed class NetWindow
        {
            public readonly Ring Trigger = new Ring();                 // generator power flowing this tick
            public readonly Dictionary<long, Ring> Producers = new Dictionary<long, Ring>();  // per-device raw production
            public int Pos;
            public int Filled;
        }

        // netRefId -> window. Only networks that carry generator power get an entry.
        private static readonly Dictionary<long, NetWindow> _windows = new Dictionary<long, NetWindow>();

        /// <summary>
        ///     Advance this network's window by one tick. <paramref name="triggerValue"/> is the
        ///     generator power flowing on the network this tick (min of total generator supply and the
        ///     network's real throughput, so idle or transformer-fed overflow does not count); it drives
        ///     the burn average. <paramref name="producersThisTick"/> is each generator's raw production
        ///     this tick, used only to rank the top producer for victim selection. Producers tracked
        ///     from earlier ticks that did not produce this tick get a 0 pushed so they age out.
        /// </summary>
        internal static void Observe(long netRefId, Dictionary<long, float> producersThisTick, float triggerValue)
        {
            if (!_windows.TryGetValue(netRefId, out var w))
            {
                if (triggerValue <= 0f && producersThisTick.Count == 0) return;   // never track a generatorless net
                w = new NetWindow();
                _windows[netRefId] = w;
            }

            int pos = w.Pos;
            w.Trigger.Push(pos, triggerValue);

            // Age every tracked producer (0 if it did not produce this tick).
            foreach (var kv in w.Producers)
            {
                producersThisTick.TryGetValue(kv.Key, out var v);
                kv.Value.Push(pos, v);
            }
            // First sighting of a producer: fresh ring (zeros in the slots before it appeared).
            foreach (var kv in producersThisTick)
            {
                if (!w.Producers.ContainsKey(kv.Key))
                {
                    var ring = new Ring();
                    ring.Push(pos, kv.Value);
                    w.Producers[kv.Key] = ring;
                }
            }

            w.Pos = (pos + 1) % WindowTicks;
            if (w.Filled < WindowTicks) w.Filled++;

            // Drop producers that have fully aged out, and the whole window once the net stops carrying
            // any generator power (keeps the dictionaries bounded to live generator networks).
            List<long> dead = null;
            foreach (var kv in w.Producers)
                if (kv.Value.Sum <= Epsilon) (dead ??= new List<long>()).Add(kv.Key);
            if (dead != null)
                foreach (var k in dead) w.Producers.Remove(k);
            if (w.Producers.Count == 0 && w.Trigger.Sum <= Epsilon)
                _windows.Remove(netRefId);
        }

        /// <summary>The window has a full 20 ticks of history (the settling period before any burn).</summary>
        internal static bool IsFull(long netRefId)
            => _windows.TryGetValue(netRefId, out var w) && w.Filled >= WindowTicks;

        /// <summary>Running average of the generator power flowing on the network over the window.</summary>
        internal static float AverageFlow(long netRefId)
            => _windows.TryGetValue(netRefId, out var w) && w.Filled > 0 ? w.Trigger.Sum / w.Filled : 0f;

        /// <summary>
        ///     ReferenceId of the generator that produced the most over the window (ties broken by lowest
        ///     ReferenceId for determinism), or 0 if none.
        /// </summary>
        internal static long TopProducer(long netRefId)
        {
            if (!_windows.TryGetValue(netRefId, out var w)) return 0L;
            long best = 0L;
            float bestSum = -1f;
            foreach (var kv in w.Producers)
            {
                if (kv.Value.Sum > bestSum || (kv.Value.Sum == bestSum && (best == 0L || kv.Key < best)))
                {
                    bestSum = kv.Value.Sum;
                    best = kv.Key;
                }
            }
            return best;
        }

        /// <summary>Reset a network's window to zero (called on a burn).</summary>
        internal static void Reset(long netRefId) => _windows.Remove(netRefId);

        /// <summary>Clear all windows. Called on world load.</summary>
        internal static void ClearAll() => _windows.Clear();
    }
}
