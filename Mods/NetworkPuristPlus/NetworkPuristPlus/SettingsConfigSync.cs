using Assets.Scripts.Networking;
using LaunchPadBooster.Networking;

namespace NetworkPuristPlus
{
    // Host-authoritative sync of the Network Purist Plus settings.
    //
    // - On the host (and in single-player), the local BepInEx config is the source of truth and nothing
    //   here changes that; HookHostBroadcast wires a SettingChanged handler so any live tweak is pushed
    //   to connected clients via SettingsConfigMessage.
    // - On a multiplayer client, the host's values arrive two ways:
    //     1. Join-time, via PowerTransmitterPlus-style IJoinSuffixSerializer in Plugin.cs (so the value
    //        is present before any client-side logging fires). The JoinValidator has already guaranteed
    //        the client's own config matches the host's, so this is a confirmation, not a correction.
    //     2. Live, via SettingsConfigMessage on every host-side SettingChanged while the client is
    //        connected.
    //   Either way the received snapshot only updates this mirror (used for logging / diagnostics). It
    //   does NOT re-run the prefab-time effects -- those happened at Prefab.OnPrefabsLoaded, before the
    //   join -- and it does not need to: a mismatched client was already rejected at join time.
    //
    // The world rebuild + cable realign on load, and the build-time long-piece rewrite, are all
    // host-authoritative (they run only where GameManager.RunSimulation), so the gameplay outcome is
    // always the host's regardless of what this mirror holds on a client.
    internal static class SettingsConfigSync
    {
        // The host's last-known settings, as seen on a client. Null on the host / single-player and on a
        // client before the first snapshot arrives.
        private static SettingsConfigMessage _hostSettings;

        // Apply a received host snapshot (client side). Logs only when something actually changed.
        internal static void OnHostConfigReceived(SettingsConfigMessage msg)
        {
            if (msg == null) return;
            bool changed = _hostSettings == null
                || _hostSettings.Enabled != msg.Enabled
                || _hostSettings.RemoveLongGasPipes != msg.RemoveLongGasPipes
                || _hostSettings.RemoveLongLiquidPipes != msg.RemoveLongLiquidPipes
                || _hostSettings.RemoveLongInsulatedGasPipes != msg.RemoveLongInsulatedGasPipes
                || _hostSettings.RemoveLongInsulatedLiquidPipes != msg.RemoveLongInsulatedLiquidPipes
                || _hostSettings.RemoveLongChutes != msg.RemoveLongChutes
                || _hostSettings.RemoveLongSuperHeavyCables != msg.RemoveLongSuperHeavyCables
                || _hostSettings.AlignStraightCables != msg.AlignStraightCables;
            _hostSettings = msg;
            if (changed)
                NetworkPuristPlusPlugin.Log?.LogInfo(
                    $"received host settings: master={msg.Enabled}, gasPipes={msg.RemoveLongGasPipes}, liquidPipes={msg.RemoveLongLiquidPipes}, insulatedGasPipes={msg.RemoveLongInsulatedGasPipes}, insulatedLiquidPipes={msg.RemoveLongInsulatedLiquidPipes}, chutes={msg.RemoveLongChutes}, superHeavyCable={msg.RemoveLongSuperHeavyCables}, alignCables={msg.AlignStraightCables}. (Prefab-time effects already applied at load; restart with matching settings if these differ from yours -- the join check normally prevents that.)");
        }

        // Called from Plugin.Awake after Settings.Bind. Wires the host-side broadcast: any live change to
        // any of the eight entries pushes a fresh snapshot to all connected clients.
        internal static void HookHostBroadcast()
        {
            void Hook(BepInEx.Configuration.ConfigEntryBase entry)
            {
                if (entry is BepInEx.Configuration.ConfigEntry<bool> e) e.SettingChanged += (_, __) => BroadcastIfHost();
            }
            Hook(Settings.Enabled);
            Hook(Settings.RemoveLongGasPipes);
            Hook(Settings.RemoveLongLiquidPipes);
            Hook(Settings.RemoveLongInsulatedGasPipes);
            Hook(Settings.RemoveLongInsulatedLiquidPipes);
            Hook(Settings.RemoveLongChutes);
            Hook(Settings.RemoveLongSuperHeavyCables);
            Hook(Settings.AlignStraightCables);
        }

        // Broadcast the local config to all connected clients. Short-circuits on a non-host.
        internal static void BroadcastIfHost()
        {
            if (!NetworkManager.IsServer) return;
            try
            {
                SettingsConfigMessage.FromLocalConfig().SendAll(0L);
                NetworkPuristPlusPlugin.Log?.LogDebug("broadcast Network Purist Plus settings to clients.");
            }
            catch (System.Exception e)
            {
                NetworkPuristPlusPlugin.Log?.LogWarning($"failed to broadcast settings to clients: {e.Message}");
            }
        }
    }
}
