using System;
using System.Globalization;

namespace PowerGridPlus
{
    /// <summary>
    ///     Tick-duration watchdog: measures the wall-clock cost of every atomic electricity tick
    ///     (the whole AtomicElectricityTickPatch body) plus the PowerAllocator.RunAtomic span
    ///     inside it, and counts ticks whose duration crosses a derived threshold. The atomic tick
    ///     replaces vanilla's per-network relaxation with a global solve, so a pathological
    ///     regression here (an allocator loop that stops converging early, a roster explosion, a
    ///     diagnostic gone quadratic) degrades the whole simulation cadence; this auditor makes
    ///     that visible as an exact counter instead of a vague "the game feels slow".
    ///
    ///     <para><b>Threshold derivation.</b> The electricity tick runs at 2 Hz, so each tick owns
    ///     a 500 ms period; sustained consumption beyond the period means backlog, and a large
    ///     fraction of it means visible stutter. But a fixed budget alone misses regressions on
    ///     small saves (2 ms to 40 ms is a 20x regression that never threatens 500 ms), so the
    ///     threshold adapts to the save:
    ///     <c>threshold = min(max(8 * rollingMedian, 50 ms), 400 ms)</c>.
    ///     The 8x median multiple flags decisively pathological ticks while sitting far above
    ///     organic jitter (a GC pause or autosave hitch doubles or triples a tick; observed suite
    ///     ticks run low single-digit milliseconds). The 50 ms floor (10 percent of the period)
    ///     keeps OS scheduling noise on sub-millisecond medians from flagging (8x of 1 ms is
    ///     normal timeslice noise, and nothing under 50 ms can threaten the budget). The 400 ms
    ///     ceiling (80 percent of the period) trips unconditionally even when the median itself
    ///     has grown huge, because 8x of a 100 ms median would otherwise exceed the period and
    ///     never fire. The median comes from a 256-tick ring recomputed every 64 ticks; until 64
    ///     samples exist (world-load warm-up, where load spikes are expected) only the 400 ms
    ///     ceiling applies.</para>
    ///
    ///     <para><b>Zero per-tick allocation.</b> Timestamps are Stopwatch.GetTimestamp() longs;
    ///     the ring and its sort scratch are preallocated arrays (Array.Sort on long[] is
    ///     in-place); counters are scalar fields; the only string work happens inside the
    ///     throttled warning.</para>
    ///
    ///     <para><b>Always-on, no config entry</b> (the ledger-audit posture). Counts are exact
    ///     and never throttled; one aggregated warning at most once per 600 ticks while new
    ///     violations arrive, carrying totals since load, the high-water captures for both spans,
    ///     and the latest violation. Zero violations produce zero lines. Cleared on world load
    ///     (the ring re-warms against the new save). The ScenarioRunner rearch suite
    ///     reflection-drives <see cref="ComputeThresholdMicros"/> with synthetic medians and reads
    ///     the counters across its window; keep the member names stable.</para>
    ///
    ///     <para>Threading: RecordTick is called once per tick from the atomic tick body on the
    ///     power worker; the suite reads counters from the sim-tick pump. Plain fields suffice for
    ///     the writer; readers tolerate a one-tick-stale value.</para>
    /// </summary>
    internal static class TickDurationWatchdog
    {
        private const long FloorMicros = 50_000;      // 50 ms: 10 percent of the 500 ms tick period
        private const long CeilingMicros = 400_000;   // 400 ms: 80 percent of the period, unconditional trip
        private const int MedianMultiple = 8;         // decisively pathological vs organic jitter
        private const int RingSize = 256;             // ~2 minutes of history at 2 Hz
        private const int WarmupSamples = 64;         // ceiling-only until the ring has this many samples
        private const int RecomputeEvery = 64;        // median refresh cadence (ticks)
        private const int WarnCooldownTicks = 600;    // one aggregated warning per ~5 minutes at 2 Hz

        private static readonly long[] _ring = new long[RingSize];
        private static readonly long[] _scratch = new long[RingSize];
        private static int _ringCount;
        private static int _ringNext;
        private static int _sinceRecompute;
        private static long _thresholdMicros = CeilingMicros;

        /// <summary>
        ///     The pure threshold formula: min(max(8 * median, 50 ms), 400 ms), in microseconds.
        ///     Reflection-driven by the ScenarioRunner rearch suite with synthetic medians; keep
        ///     the signature stable.
        /// </summary>
        internal static long ComputeThresholdMicros(long medianMicros)
        {
            long adaptive = medianMicros * MedianMultiple;
            if (adaptive < FloorMicros) adaptive = FloorMicros;
            if (adaptive > CeilingMicros) adaptive = CeilingMicros;
            return adaptive;
        }

        // Exact running totals since load; reflection surface for the rearch suite.
        internal static long ViolationTicks { get; private set; }       // ticks over the active threshold
        internal static long ThresholdMicrosNow => _thresholdMicros;    // the currently active threshold
        internal static long MaxTickMicros { get; private set; }        // high-water: whole atomic tick
        internal static int MaxTickAt { get; private set; }
        internal static long MaxAllocatorMicros { get; private set; }   // high-water: RunAtomic span
        internal static int MaxAllocatorAt { get; private set; }
        internal static long LastViolationMicros { get; private set; }
        internal static long LastViolationAllocatorMicros { get; private set; }
        internal static long LastViolationThreshold { get; private set; }
        internal static int LastViolationTick { get; private set; }
        internal static int WarningsEmitted { get; private set; }
        internal static string LastWarning { get; private set; }

        private static long _violationsAtLastWarn;
        private static int _lastWarnTick = -WarnCooldownTicks;

        /// <summary>Convert a Stopwatch timestamp delta to microseconds (no allocation).</summary>
        internal static long TimestampDeltaToMicros(long startTimestamp, long endTimestamp)
        {
            long delta = endTimestamp - startTimestamp;
            if (delta <= 0) return 0;
            // Multiply before divide keeps microsecond precision; delta * 1e6 fits a long for any
            // realistic tick duration (frequency is 1e7 on Windows; a full day of ticks fits).
            return delta * 1_000_000L / System.Diagnostics.Stopwatch.Frequency;
        }

        /// <summary>
        ///     Record one atomic tick's measured spans. Called at the end of the atomic tick body,
        ///     once per tick, on the power worker.
        /// </summary>
        internal static void RecordTick(int currentTick, long tickMicros, long allocatorMicros)
        {
            if (tickMicros > MaxTickMicros) { MaxTickMicros = tickMicros; MaxTickAt = currentTick; }
            if (allocatorMicros > MaxAllocatorMicros) { MaxAllocatorMicros = allocatorMicros; MaxAllocatorAt = currentTick; }

            bool violated = _ringCount >= WarmupSamples
                ? tickMicros >= _thresholdMicros
                : tickMicros >= CeilingMicros;
            if (violated)
            {
                ViolationTicks++;
                LastViolationMicros = tickMicros;
                LastViolationAllocatorMicros = allocatorMicros;
                LastViolationThreshold = _ringCount >= WarmupSamples ? _thresholdMicros : CeilingMicros;
                LastViolationTick = currentTick;
                EmitWarningIfDue(currentTick);
            }

            // Ring update AFTER the comparison, so a violating tick never softens the very
            // threshold it was judged against; it still enters the history (a genuinely slower
            // save re-baselines within one recompute window instead of warning forever).
            _ring[_ringNext] = tickMicros;
            _ringNext = (_ringNext + 1) % RingSize;
            if (_ringCount < RingSize) _ringCount++;

            if (++_sinceRecompute >= RecomputeEvery && _ringCount >= WarmupSamples)
            {
                _sinceRecompute = 0;
                Array.Copy(_ring, _scratch, _ringCount);
                Array.Sort(_scratch, 0, _ringCount);
                _thresholdMicros = ComputeThresholdMicros(_scratch[_ringCount / 2]);
            }
        }

        private static void EmitWarningIfDue(int currentTick)
        {
            if (ViolationTicks == _violationsAtLastWarn) return;
            if (currentTick - _lastWarnTick < WarnCooldownTicks) return;
            _lastWarnTick = currentTick;
            _violationsAtLastWarn = ViolationTicks;
            WarningsEmitted++;
            LastWarning =
                "[PowerGridPlus] Tick-duration watchdog: the atomic electricity tick exceeded its derived threshold on "
                + ViolationTicks.ToString(CultureInfo.InvariantCulture)
                + " tick(s) since load (latest: " + (LastViolationMicros / 1000.0).ToString("F1", CultureInfo.InvariantCulture)
                + " ms, allocator " + (LastViolationAllocatorMicros / 1000.0).ToString("F1", CultureInfo.InvariantCulture)
                + " ms, threshold " + (LastViolationThreshold / 1000.0).ToString("F1", CultureInfo.InvariantCulture)
                + " ms, tick " + LastViolationTick.ToString(CultureInfo.InvariantCulture)
                + "; high-water tick " + (MaxTickMicros / 1000.0).ToString("F1", CultureInfo.InvariantCulture)
                + " ms at " + MaxTickAt.ToString(CultureInfo.InvariantCulture)
                + ", allocator " + (MaxAllocatorMicros / 1000.0).ToString("F1", CultureInfo.InvariantCulture)
                + " ms at " + MaxAllocatorAt.ToString(CultureInfo.InvariantCulture)
                + "). The 2 Hz power tick owns a 500 ms period; sustained overruns back the simulation up."
                + " This is a performance regression in the mod; please report it.";
            Plugin.Log?.LogWarning(LastWarning);
        }

        /// <summary>World-load reset: drop the previous world's history and counters; re-warm.</summary>
        internal static void Clear()
        {
            _ringCount = 0;
            _ringNext = 0;
            _sinceRecompute = 0;
            _thresholdMicros = CeilingMicros;
            ViolationTicks = 0;
            MaxTickMicros = 0;
            MaxTickAt = 0;
            MaxAllocatorMicros = 0;
            MaxAllocatorAt = 0;
            LastViolationMicros = 0;
            LastViolationAllocatorMicros = 0;
            LastViolationThreshold = 0;
            LastViolationTick = 0;
            WarningsEmitted = 0;
            LastWarning = null;
            _violationsAtLastWarn = 0;
            _lastWarnTick = -WarnCooldownTicks;
        }
    }
}
