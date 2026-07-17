using System;
using System.Globalization;

namespace PowerGridPlus
{
    /// <summary>
    ///     Tick-duration watchdog: measures the wall-clock cost of every atomic electricity tick
    ///     (the whole AtomicElectricityTickPatch body) plus the PowerAllocator.RunAtomic span
    ///     inside it, and counts ticks whose duration crosses a derived threshold AND whose overrun
    ///     is attributable to the allocator. The atomic tick replaces vanilla's per-network
    ///     relaxation with a global solve, so a pathological regression here (an allocator loop that
    ///     stops converging early, a roster explosion, a diagnostic gone quadratic) degrades the
    ///     whole simulation cadence; this auditor makes that visible as an exact counter instead of
    ///     a vague "the game feels slow".
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
    ///     <para><b>Allocator-attribution gate.</b> Crossing the total-duration threshold means the
    ///     tick was slow; it does NOT mean the power code was the reason. On a host whose tick
    ///     median is small (single-digit ms), an autosave serialization stall, a GC pause, or OS
    ///     scheduling can push a tick past 8x median while the allocator ran its normal few
    ///     milliseconds, so gating on total time alone false-fires on environmental noise (the
    ///     gate-14 soak produced exactly this: 150 ms ticks with the allocator at 4 to 6 ms). The
    ///     watchdog therefore emits its warning only when the allocator is the dominant cause of the
    ///     overrun: the allocator's EXCESS over its own rolling median must account for at least
    ///     <see cref="MinAllocatorOverrunSharePercent"/> percent of the amount by which the whole
    ///     tick overran its threshold (<see cref="IsAllocatorAttributable"/>). The allocator span
    ///     carries its own 256-tick median alongside the tick ring, so the baseline self-calibrates
    ///     to save size exactly as the tick threshold does. A slow tick that is not the allocator's
    ///     doing is silent; a genuine allocator blow-up (an unconverged solve, a quadratic
    ///     diagnostic) trips it even when the environment is also noisy, because the excess is
    ///     measured against the allocator's own norm, not the tick's. This is what makes the
    ///     "performance regression in the mod" claim in the warning text accurate.</para>
    ///
    ///     <para><b>Zero per-tick allocation.</b> Timestamps are Stopwatch.GetTimestamp() longs;
    ///     both rings and their sort scratch are preallocated arrays (Array.Sort on long[] is
    ///     in-place); counters are scalar fields; the only string work happens inside the
    ///     throttled warning.</para>
    ///
    ///     <para><b>Cold-start grace.</b> The first tick after a load boundary does guaranteed
    ///     one-time work: Mono JIT-compiles the whole allocator pipeline on its first invocation
    ///     and the pending load sweeps (censuses, wreckage cleanup, boundary drain) all fire.
    ///     Measured near a full second on any save, allocator-dominant, every load; that is
    ///     cold start, not a regression, and judging it would emit the "please report it"
    ///     warning on every world load. The first <see cref="ColdStartGraceTicks"/> recorded
    ///     ticks after <see cref="Clear"/> (and after process start) are therefore never judged
    ///     and do not set the high-water captures; they still enter the rings, where one outlier
    ///     among 256 cannot move a median. A genuine pathology persists past the grace and fires
    ///     from the third tick on.</para>
    ///
    ///     <para><b>Always-on, no config entry</b> (the ledger-audit posture). Counts are exact
    ///     and never throttled; one aggregated warning at most once per 600 ticks while new
    ///     violations arrive, carrying totals since load, the high-water captures for both spans,
    ///     and the latest violation. Zero violations produce zero lines. Cleared on world load
    ///     (the rings re-warm against the new save). The ScenarioRunner rearch suite
    ///     reflection-drives <see cref="ComputeThresholdMicros"/> and <see cref="IsAllocatorAttributable"/>
    ///     with synthetic values and reads the counters across its window; keep the member names
    ///     stable.</para>
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
        private const int MinAllocatorOverrunSharePercent = 50; // allocator excess must explain >= this share of the overrun
        private const int ColdStartGraceTicks = 2;    // first ticks after a load boundary: one-time JIT + load sweeps, never judged

        private static readonly long[] _ring = new long[RingSize];
        private static readonly long[] _scratch = new long[RingSize];
        private static readonly long[] _allocRing = new long[RingSize];     // allocator-span history, indexed in lockstep with _ring
        private static readonly long[] _allocScratch = new long[RingSize];
        private static int _ringCount;
        private static int _ringNext;
        private static int _sinceRecompute;
        private static long _thresholdMicros = CeilingMicros;
        private static long _allocMedianMicros;       // 0 until the first recompute (excess = raw allocator time during warm-up)

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

        /// <summary>
        ///     The allocator-attribution gate. Given a tick that already crossed its total-duration
        ///     threshold, decide whether the ALLOCATOR is the dominant cause and the overrun is
        ///     therefore a mod regression rather than environmental noise. True iff the allocator's
        ///     excess over its own rolling median accounts for at least
        ///     <see cref="MinAllocatorOverrunSharePercent"/> percent of the amount by which the tick
        ///     overran its threshold. All arguments in microseconds. Pure function; reflection-driven
        ///     by the rearch suite (TDW fixtures); keep the signature stable.
        /// </summary>
        internal static bool IsAllocatorAttributable(long tickMicros, long allocatorMicros, long allocatorMedianMicros, long thresholdMicros)
        {
            long overrun = tickMicros - thresholdMicros;
            if (overrun <= 0) return false;                      // did not actually overrun
            long allocatorExcess = allocatorMicros - allocatorMedianMicros;
            if (allocatorExcess <= 0) return false;              // allocator was at or below its own norm: not the cause
            // allocatorExcess / overrun >= MinShare/100, rearranged to avoid a divide and stay in longs.
            return allocatorExcess * 100L >= overrun * MinAllocatorOverrunSharePercent;
        }

        // Exact running totals since load; reflection surface for the rearch suite.
        internal static long ViolationTicks { get; private set; }       // ticks over threshold AND allocator-attributable
        internal static long ThresholdMicrosNow => _thresholdMicros;    // the currently active threshold
        internal static long AllocatorMedianMicrosNow => _allocMedianMicros; // the currently active allocator baseline
        internal static long MaxTickMicros { get; private set; }        // high-water: whole atomic tick
        internal static int MaxTickAt { get; private set; }
        internal static long MaxAllocatorMicros { get; private set; }   // high-water: RunAtomic span
        internal static int MaxAllocatorAt { get; private set; }
        internal static long LastViolationMicros { get; private set; }
        internal static long LastViolationAllocatorMicros { get; private set; }
        internal static long LastViolationAllocatorMedianMicros { get; private set; }
        internal static long LastViolationThreshold { get; private set; }
        internal static int LastViolationTick { get; private set; }
        internal static int WarningsEmitted { get; private set; }
        internal static string LastWarning { get; private set; }

        private static long _violationsAtLastWarn;
        private static int _lastWarnTick = -WarnCooldownTicks;
        private static int _recordedSinceClear;

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
            bool coldStart = _recordedSinceClear < ColdStartGraceTicks;
            if (coldStart) _recordedSinceClear++;

            if (!coldStart)
            {
                if (tickMicros > MaxTickMicros) { MaxTickMicros = tickMicros; MaxTickAt = currentTick; }
                if (allocatorMicros > MaxAllocatorMicros) { MaxAllocatorMicros = allocatorMicros; MaxAllocatorAt = currentTick; }
            }

            long activeThreshold = _ringCount >= WarmupSamples ? _thresholdMicros : CeilingMicros;
            // Three conditions: past the cold-start grace, the whole tick overran its budget (sim
            // could back up), AND the allocator is the dominant cause (the mod's fault, not the
            // environment's).
            bool violated = !coldStart
                && tickMicros >= activeThreshold
                && IsAllocatorAttributable(tickMicros, allocatorMicros, _allocMedianMicros, activeThreshold);
            if (violated)
            {
                ViolationTicks++;
                LastViolationMicros = tickMicros;
                LastViolationAllocatorMicros = allocatorMicros;
                LastViolationAllocatorMedianMicros = _allocMedianMicros;
                LastViolationThreshold = activeThreshold;
                LastViolationTick = currentTick;
                EmitWarningIfDue(currentTick);
            }

            // Ring updates AFTER the comparison (both rings in lockstep), so a violating tick never
            // softens the thresholds it was judged against; both still enter history so a genuinely
            // slower save re-baselines within one recompute window instead of warning forever.
            _ring[_ringNext] = tickMicros;
            _allocRing[_ringNext] = allocatorMicros;
            _ringNext = (_ringNext + 1) % RingSize;
            if (_ringCount < RingSize) _ringCount++;

            if (++_sinceRecompute >= RecomputeEvery && _ringCount >= WarmupSamples)
            {
                _sinceRecompute = 0;
                Array.Copy(_ring, _scratch, _ringCount);
                Array.Sort(_scratch, 0, _ringCount);
                _thresholdMicros = ComputeThresholdMicros(_scratch[_ringCount / 2]);
                Array.Copy(_allocRing, _allocScratch, _ringCount);
                Array.Sort(_allocScratch, 0, _ringCount);
                _allocMedianMicros = _allocScratch[_ringCount / 2];
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
                "[PowerGridPlus] Tick-duration watchdog: the atomic electricity tick overran its derived threshold with the allocator as the dominant cause on "
                + ViolationTicks.ToString(CultureInfo.InvariantCulture)
                + " tick(s) since load (latest: " + (LastViolationMicros / 1000.0).ToString("F1", CultureInfo.InvariantCulture)
                + " ms, allocator " + (LastViolationAllocatorMicros / 1000.0).ToString("F1", CultureInfo.InvariantCulture)
                + " ms vs allocator median " + (LastViolationAllocatorMedianMicros / 1000.0).ToString("F1", CultureInfo.InvariantCulture)
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
            _allocMedianMicros = 0;
            ViolationTicks = 0;
            MaxTickMicros = 0;
            MaxTickAt = 0;
            MaxAllocatorMicros = 0;
            MaxAllocatorAt = 0;
            LastViolationMicros = 0;
            LastViolationAllocatorMicros = 0;
            LastViolationAllocatorMedianMicros = 0;
            LastViolationThreshold = 0;
            LastViolationTick = 0;
            WarningsEmitted = 0;
            LastWarning = null;
            _violationsAtLastWarn = 0;
            _lastWarnTick = -WarnCooldownTicks;
            _recordedSinceClear = 0;
        }
    }
}
