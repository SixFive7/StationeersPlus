using Assets.Scripts.Networking;

namespace PowerGridPlus
{
    // Server-authoritative sync of the EnableTransformerShedding toggle. Mirrors
    // PassthroughSettingsSync's shape (one feature toggle instead of four).
    // TransformerAllocator reads the Effective accessor here, not Settings.Enable*.Value
    // directly, so the host's value drives every peer:
    //   - host / single-player: the local BepInEx config value.
    //   - client: the value pushed by the host (join snapshot via the plugin's
    //     IJoinSuffixSerializer; live changes via a future broadcast if added);
    //     falls back to local config until the first sync arrives.
    //
    // Without this, a client whose local config differed from the host would
    // diverge in allocation: the host would shed transformers while the client
    // would not, or vice versa, causing the in-world flashing button to disagree.
    internal static class ShedSettingsSync
    {
        private static bool? _synced;

        internal static bool Effective
        {
            get
            {
                var local = Settings.EnableTransformerShedding;
                if (!NetworkManager.IsActive || NetworkManager.IsServer)
                    return local?.Value ?? false;
                return _synced ?? local?.Value ?? false;
            }
        }

        internal static void SetSyncedValue(bool sheddingEnabled)
        {
            _synced = sheddingEnabled;
        }
    }
}
