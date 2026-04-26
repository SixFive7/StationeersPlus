using Assets.Scripts.Networking;
using LaunchPadBooster.Networking;

namespace PowerTransmitterPlus
{
    // Server-authoritative distance-cost (k) sync.
    //
    // - Local config value is the source of truth ON THE HOST (and in single-player).
    // - On a multiplayer client, the host's value is delivered via two paths:
    //     1. Join-time snapshot via PowerTransmitterPlusPlugin.SerializeJoinSuffix /
    //        DeserializeJoinSuffix (so a fresh joiner has the right value before any
    //        IC10 read or tablet display fires).
    //     2. Live updates via DistanceConfigMessage on every SettingChanged event
    //        while a client is connected (host changes the setting in the in-game
    //        settings panel; clients adopt immediately).
    // - GetEffectiveK() returns the right value for the current side: host config
    //   on host or single-player; synced (or local fallback) on client.
    //
    // Earlier versions also rebroadcast on a NetworkManager.PlayerConnected postfix,
    // but per Research/Protocols/PlayerConnectedThingFindTiming.md that hook fires
    // BEFORE the joiner is in NetworkBase.Clients, so the broadcast went to existing
    // clients only and never reached the new joiner. Removed in v1.7.0; the
    // IJoinSuffixSerializer payload covers the joiner case correctly.
    //
    // The simulation patches (UsePower / GetUsedPower / ReceivePower / GetGeneratedPower)
    // run only on the server, so the gameplay number is always the host's. Clients
    // need this only so their tablet/IC10 readouts compute matching display values.
    internal static class DistanceConfigSync
    {
        // Last value broadcast by the host. Null until the first message arrives.
        // Falls back to local config when null so client display still works
        // before the first sync completes.
        private static float? _syncedHostK;

        internal static float GetEffectiveK()
        {
            var local = PowerTransmitterPlusPlugin.DistanceCostFactor?.Value ?? 0f;
            // Single-player or host: local config is authoritative.
            if (!NetworkManager.IsActive || NetworkManager.IsServer) return local;
            // Client: prefer the host's pushed value; fall back to local until it arrives.
            return _syncedHostK ?? local;
        }

        internal static void OnHostConfigReceived(float k)
        {
            if (_syncedHostK != k)
            {
                _syncedHostK = k;
                PowerTransmitterPlusPlugin.Log?.LogInfo(
                    $"Received DistanceCostFactor (k) from host: {k:F2}");
            }
        }

        // Called from Plugin.Awake after the BepInEx config is bound. Wires the
        // host-side broadcast: any time the host changes k, push to all clients.
        internal static void HookHostBroadcast()
        {
            var entry = PowerTransmitterPlusPlugin.DistanceCostFactor;
            if (entry == null) return;
            entry.SettingChanged += (_, __) => BroadcastIfHost();
        }

        // Called when a client connects (server-side) AND when the host changes
        // k mid-game. Safe to call on non-host; it short-circuits.
        internal static void BroadcastIfHost()
        {
            if (!NetworkManager.IsServer) return;
            var k = PowerTransmitterPlusPlugin.DistanceCostFactor?.Value ?? 0f;
            new DistanceConfigMessage { K = k }.SendAll(0L);
            PowerTransmitterPlusPlugin.Log?.LogDebug(
                $"Broadcast DistanceCostFactor (k) to clients: {k:F2}");
        }
    }

}
