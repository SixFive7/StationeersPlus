using UnityEngine;

namespace PowerGridPlus
{
    // Monotonic counter incremented once per ElectricityManager.ElectricityTick
    // (i.e. once per power-sim half-second at the default 2 Hz tick rate).
    // Used by the PowerAllocator and the fault registries' lockout-expiry
    // comparisons.
    //
    // Increment happens via the Harmony prefix in AtomicElectricityTickPatch,
    // which only runs on the simulating peer (GameManager.RunSimulation). On a
    // client peer this counter does NOT advance; client-side fault state lives
    // in the registries' MonotonicClock-based mirrors instead.
    //
    // Reset on world load: not done. The counter just keeps climbing; lockout
    // values are compared with the current value so a session restart with a
    // climbing counter still works.
    internal static class ElectricityTickCounter
    {
        private static int _current;
        private static long _lastAdvanceMs;

        internal static int CurrentTick => _current;

        internal static void Advance()
        {
            _current++;
            _lastAdvanceMs = MonotonicClock.NowMs;
        }

        // Smooth host-side countdown (POWER.md §11.2): the tick remainder gives 0.5 s granularity;
        // subtracting the wall-clock time since the last tick advance makes the displayed value tick
        // down continuously at the UI poll rate. Display-only; the authoritative expiry is tick-based.
        internal static float SmoothSeconds(int ticksLeft)
        {
            float seconds = ticksLeft * 0.5f;
            long sinceAdvance = MonotonicClock.NowMs - _lastAdvanceMs;
            if (sinceAdvance > 0 && sinceAdvance < 500)
                seconds -= sinceAdvance / 1000f;
            return Mathf.Max(0f, seconds);
        }
    }
}
