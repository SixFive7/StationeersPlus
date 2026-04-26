using Assets.Scripts.Networking;
using LaunchPadBooster.Networking;

namespace PowerTransmitterPlus
{
    // Server-authoritative visual config sync. The host's beam visual settings
    // always override client-local config in multiplayer. Two delivery paths,
    // mirroring DistanceConfigSync:
    //   1. Join-time snapshot via PowerTransmitterPlusPlugin.SerializeJoinSuffix /
    //      DeserializeJoinSuffix, so a fresh joiner sees the host's visuals from
    //      the first beam render.
    //   2. Live BeamVisualConfigMessage broadcasts on every SettingChanged event
    //      while a client is connected.
    // Clients store the received values and return them from GetEffective*. The
    // earlier NetworkManager.PlayerConnected rebroadcast hook was removed in
    // v1.7.0 (see DistanceConfigSync.cs class doc for the timing rationale).
    internal static class BeamVisualConfigSync
    {
        private static bool _received;
        private static float _syncedBeamWidth;
        private static string _syncedBeamColorHex;
        private static float _syncedEmissionIntensity;
        private static float _syncedStripeWavelength;
        private static float _syncedScrollSpeed;
        private static float _syncedStripeTroughBrightness;

        private static bool UseHostValues =>
            _received && NetworkManager.IsActive && !NetworkManager.IsServer;

        internal static float GetEffectiveBeamWidth()
        {
            if (UseHostValues) return _syncedBeamWidth;
            return PowerTransmitterPlusPlugin.BeamWidth?.Value ?? 0.1f;
        }

        internal static string GetEffectiveBeamColorHex()
        {
            if (UseHostValues) return _syncedBeamColorHex ?? "000DFF";
            return PowerTransmitterPlusPlugin.BeamColorHex?.Value ?? "000DFF";
        }

        internal static float GetEffectiveEmissionIntensity()
        {
            if (UseHostValues) return _syncedEmissionIntensity;
            return PowerTransmitterPlusPlugin.EmissionIntensity?.Value ?? 10f;
        }

        internal static float GetEffectiveStripeWavelength()
        {
            if (UseHostValues) return _syncedStripeWavelength;
            return PowerTransmitterPlusPlugin.StripeWavelength?.Value ?? 2f;
        }

        internal static float GetEffectiveScrollSpeed()
        {
            if (UseHostValues) return _syncedScrollSpeed;
            return PowerTransmitterPlusPlugin.ScrollSpeed?.Value ?? 25f;
        }

        internal static float GetEffectiveStripeTroughBrightness()
        {
            if (UseHostValues) return _syncedStripeTroughBrightness;
            return PowerTransmitterPlusPlugin.StripeTroughBrightness?.Value ?? 0.5f;
        }

        internal static void OnHostConfigReceived(BeamVisualConfigMessage msg)
        {
            _received = true;
            _syncedBeamWidth = msg.BeamWidth;
            _syncedBeamColorHex = msg.BeamColorHex;
            _syncedEmissionIntensity = msg.EmissionIntensity;
            _syncedStripeWavelength = msg.StripeWavelength;
            _syncedScrollSpeed = msg.ScrollSpeed;
            _syncedStripeTroughBrightness = msg.StripeTroughBrightness;

            PowerTransmitterPlusPlugin.Log?.LogInfo(
                $"Received host visual config: width={msg.BeamWidth:F2}, color={msg.BeamColorHex}, " +
                $"emission={msg.EmissionIntensity:F1}, wavelength={msg.StripeWavelength:F2}, " +
                $"scroll={msg.ScrollSpeed:F1}, trough={msg.StripeTroughBrightness:F2}");

            BeamManager.InvalidateAllBeams();
        }

        internal static void HookHostBroadcast()
        {
            PowerTransmitterPlusPlugin.BeamWidth.SettingChanged += (_, __) => BroadcastIfHost();
            PowerTransmitterPlusPlugin.BeamColorHex.SettingChanged += (_, __) => BroadcastIfHost();
            PowerTransmitterPlusPlugin.EmissionIntensity.SettingChanged += (_, __) => BroadcastIfHost();
            PowerTransmitterPlusPlugin.StripeWavelength.SettingChanged += (_, __) => BroadcastIfHost();
            PowerTransmitterPlusPlugin.ScrollSpeed.SettingChanged += (_, __) => BroadcastIfHost();
            PowerTransmitterPlusPlugin.StripeTroughBrightness.SettingChanged += (_, __) => BroadcastIfHost();
        }

        internal static void BroadcastIfHost()
        {
            if (!NetworkManager.IsServer) return;
            new BeamVisualConfigMessage
            {
                BeamWidth = PowerTransmitterPlusPlugin.BeamWidth?.Value ?? 0.1f,
                BeamColorHex = PowerTransmitterPlusPlugin.BeamColorHex?.Value ?? "000DFF",
                EmissionIntensity = PowerTransmitterPlusPlugin.EmissionIntensity?.Value ?? 10f,
                StripeWavelength = PowerTransmitterPlusPlugin.StripeWavelength?.Value ?? 2f,
                ScrollSpeed = PowerTransmitterPlusPlugin.ScrollSpeed?.Value ?? 25f,
                StripeTroughBrightness = PowerTransmitterPlusPlugin.StripeTroughBrightness?.Value ?? 0.5f,
            }.SendAll(0L);
            PowerTransmitterPlusPlugin.Log?.LogDebug("Broadcast visual config to clients");
        }
    }
}
