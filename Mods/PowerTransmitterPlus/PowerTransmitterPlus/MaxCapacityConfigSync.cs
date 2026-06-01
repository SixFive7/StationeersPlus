using Assets.Scripts.Networking;
using LaunchPadBooster.Networking;

namespace PowerTransmitterPlus
{
    // Server-authoritative Max Transfer Capacity sync.
    //
    // - Local config value is the source of truth ON THE HOST (and in single-player).
    // - On a multiplayer client, the host's value is delivered via two paths,
    //   mirroring DistanceConfigSync:
    //     1. Join-time snapshot via PowerTransmitterPlusPlugin.SerializeJoinSuffix /
    //        DeserializeJoinSuffix (so a fresh joiner has the right value immediately).
    //     2. Live updates via MaxCapacityConfigMessage on every SettingChanged event
    //        while a client is connected (host changes the setting in the in-game
    //        settings panel; clients adopt immediately).
    // - GetEffectiveMaxCapacity() returns host config on host or single-player;
    //   synced (or local fallback) on a client.
    //
    // The delivery clamp (GeneratedPowerNoDistanceDeratePatch) runs only on the
    // server, so the gameplay number is always the host's. Clients need this only
    // so planned client-side beam visuals can tell when a link is at or over the
    // cap. 0 = unlimited.
    internal static class MaxCapacityConfigSync
    {
        // Last value broadcast by the host. Null until the first message arrives;
        // falls back to local config when null so a client still has a sane value
        // before the first sync completes.
        private static float? _syncedHostMaxCapacity;

        internal static float GetEffectiveMaxCapacity()
        {
            var local = PowerTransmitterPlusPlugin.MaxTransferCapacity?.Value ?? 0f;
            // Single-player or host: local config is authoritative.
            if (!NetworkManager.IsActive || NetworkManager.IsServer) return local;
            // Client: prefer the host's pushed value; fall back to local until it arrives.
            return _syncedHostMaxCapacity ?? local;
        }

        internal static void OnHostConfigReceived(float maxCapacity)
        {
            if (_syncedHostMaxCapacity != maxCapacity)
            {
                _syncedHostMaxCapacity = maxCapacity;
                PowerTransmitterPlusPlugin.Log?.LogInfo(
                    $"Received Max Transfer Capacity from host: {maxCapacity:F0} W"
                    + (maxCapacity <= 0f ? " (unlimited)" : ""));
            }
        }

        // Called from Plugin.Awake after the BepInEx config is bound. Wires the
        // host-side broadcast: any time the host changes the cap, push to all clients.
        internal static void HookHostBroadcast()
        {
            var entry = PowerTransmitterPlusPlugin.MaxTransferCapacity;
            if (entry == null) return;
            entry.SettingChanged += (_, __) => BroadcastIfHost();
        }

        // Called when the host changes the cap mid-game. Safe to call on non-host;
        // it short-circuits.
        internal static void BroadcastIfHost()
        {
            if (!NetworkManager.IsServer) return;
            var cap = PowerTransmitterPlusPlugin.MaxTransferCapacity?.Value ?? 0f;
            new MaxCapacityConfigMessage { MaxCapacity = cap }.SendAll(0L);
            PowerTransmitterPlusPlugin.Log?.LogDebug(
                $"Broadcast Max Transfer Capacity to clients: {cap:F0} W");
        }
    }
}
