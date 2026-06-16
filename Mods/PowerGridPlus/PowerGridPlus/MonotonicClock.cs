using System.Diagnostics;

namespace PowerGridPlus
{
    /// <summary>
    ///     Process-wide monotonic milliseconds. Client-side fault mirrors store their expiry in this
    ///     domain (host remaining-ticks are converted on receive), because the electricity-tick
    ///     counter only advances on the simulating peer, so until-tick comparisons are meaningless on
    ///     a client. Stopwatch-based: thread-safe, no wrap (long), no Unity API (safe off the main
    ///     thread, unlike Time.realtimeSinceStartup).
    /// </summary>
    internal static class MonotonicClock
    {
        private static readonly Stopwatch _clock = Stopwatch.StartNew();

        internal static long NowMs => _clock.ElapsedMilliseconds;
    }
}
