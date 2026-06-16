using Assets.Scripts.Networking;

namespace PowerGridPlus
{
    // Server-authoritative sync of the EnableTransformerOverloadProtection
    // toggle. Mirrors ShedSettingsSync's shape. TransformerAllocator reads the
    // Effective accessor here so the host's value drives every peer:
    //   - host / single-player: the local BepInEx config value.
    //   - client: the value pushed by the host (join snapshot via the plugin's
    //     IJoinSuffixSerializer); falls back to local config until the first
    //     sync arrives.
    internal static class OverloadSettingsSync
    {
        private static bool? _synced;

        internal static bool Effective
        {
            get
            {
                var local = Settings.EnableTransformerOverloadProtection;
                if (!NetworkManager.IsActive || NetworkManager.IsServer)
                    return local?.Value ?? false;
                return _synced ?? local?.Value ?? false;
            }
        }

        internal static void SetSyncedValue(bool overloadEnabled)
        {
            _synced = overloadEnabled;
        }
    }
}
