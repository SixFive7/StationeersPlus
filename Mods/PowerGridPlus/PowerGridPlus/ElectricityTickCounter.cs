namespace PowerGridPlus
{
    // Monotonic counter incremented once per ElectricityManager.ElectricityTick
    // (i.e. once per power-sim half-second at the default 2 Hz tick rate).
    // Used by TransformerAllocator's per-tick cache key and BrownoutRegistry's
    // lockout-expiry comparison.
    //
    // Increment happens via the Harmony prefix in ElectricityTickPatches. Both
    // host and clients tick this counter at the same rate (every peer drives its
    // own electricity tick), so referenced tick values match across peers as long
    // as both peers entered the world at the same simulation epoch. Drift between
    // a host and a freshly-joined client is bounded by the join handshake.
    //
    // Reset on world load: not done. The counter just keeps climbing; lockout
    // values are compared with the current value so a session restart with a
    // climbing counter still works.
    internal static class ElectricityTickCounter
    {
        private static int _current;

        internal static int CurrentTick => _current;

        internal static void Advance() => _current++;
    }
}
