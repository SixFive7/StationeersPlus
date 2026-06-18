using Assets.Scripts.Networking;

namespace PowerGridPlus
{
    // Server-authoritative read accessor for the EnableSweepAllocator toggle. Mirrors
    // ShedSettingsSync's shape. PowerAllocator and the supply-reporting / boundary patches read
    // SweepAllocatorSync.Effective, not Settings.EnableSweepAllocator.Value directly, so the host's
    // value drives every peer:
    //   - host / single-player: the local BepInEx config value.
    //   - client: the value pushed by the host during the join handshake
    //     (Plugin.SerializeJoinSuffix / DeserializeJoinSuffix); falls back to local config until the
    //     join-suffix arrives.
    //
    // Why this matters: the allocator runs on every peer's local simulation. If a client computed
    // the Legacy distribution while the host computed Sweep, the two would disagree on which
    // transformers are powered, shed, or overloaded, and the in-world flash / hover state would
    // diverge. The toggle is therefore synced exactly like EnableTransformerShedding.
    internal static class SweepAllocatorSync
    {
        private static bool? _synced;

        internal static bool Effective
        {
            get
            {
                var local = Settings.EnableSweepAllocator;
                if (!NetworkManager.IsActive || NetworkManager.IsServer)
                    return local?.Value ?? false;
                return _synced ?? local?.Value ?? false;
            }
        }

        internal static void SetSyncedValue(bool sweepEnabled)
        {
            _synced = sweepEnabled;
        }
    }
}
