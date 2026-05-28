using System;
using Assets.Scripts.Networking;
using BepInEx.Configuration;
using LaunchPadBooster.Networking;

namespace PowerGridPlus
{
    // Server-authoritative sync of the four Enable*LogicPassthrough toggles. The merge in
    // PassthroughTopology reads the Effective* accessors here, not Settings.Enable*.Value directly, so the
    // host's values drive every peer:
    //   - host / single-player: the local BepInEx config value.
    //   - client: the value pushed by the host (join snapshot via the plugin's IJoinSuffixSerializer, live
    //     changes via PassthroughSettingsMessage); falls back to local config until the first sync arrives.
    // Without this, each peer computed the merge with its own local config, so a client whose config
    // differed from the host produced a divergent data device list. Mirrors PowerTransmitterPlus's
    // DistanceConfigSync pattern.
    internal static class PassthroughSettingsSync
    {
        private static bool? _transformer;
        private static bool? _battery;
        private static bool? _apc;
        private static bool? _powerTransmitter;

        internal static bool EffectiveTransformer => Effective(_transformer, Settings.EnableTransformerLogicPassthrough);
        internal static bool EffectiveBattery => Effective(_battery, Settings.EnableBatteryLogicPassthrough);
        internal static bool EffectiveApc => Effective(_apc, Settings.EnableAreaPowerControlLogicPassthrough);
        internal static bool EffectivePowerTransmitter => Effective(_powerTransmitter, Settings.EnablePowerTransmitterLogicPassthrough);

        private static bool Effective(bool? synced, ConfigEntry<bool> local)
        {
            // Host / single-player: local config is authoritative. Client: the host's pushed value,
            // falling back to local until the first sync arrives.
            if (!NetworkManager.IsActive || NetworkManager.IsServer) return local?.Value ?? false;
            return synced ?? local?.Value ?? false;
        }

        // Wire the host-side SettingChanged -> broadcast + refresh. Call once after Settings.Bind.
        internal static void HookHostBroadcast()
        {
            Settings.EnableTransformerLogicPassthrough.SettingChanged += OnLocalChanged;
            Settings.EnableBatteryLogicPassthrough.SettingChanged += OnLocalChanged;
            Settings.EnableAreaPowerControlLogicPassthrough.SettingChanged += OnLocalChanged;
            Settings.EnablePowerTransmitterLogicPassthrough.SettingChanged += OnLocalChanged;
        }

        private static void OnLocalChanged(object sender, EventArgs e)
        {
            // Only the host's change is authoritative; a client editing its own config must not drive the
            // merge or it would diverge from the host.
            if (NetworkManager.IsActive && !NetworkManager.IsServer) return;
            Broadcast();
            Patches.CableNetworkPatches.RefreshAllNetworks();
        }

        // Host -> all clients. Only sends from an active host; no-op in single-player (no clients) and on
        // a client. Avoids touching the message channel before a multiplayer session exists.
        internal static void Broadcast()
        {
            if (!NetworkManager.IsActive || !NetworkManager.IsServer) return;
            new PassthroughSettingsMessage
            {
                Transformer = Settings.EnableTransformerLogicPassthrough.Value,
                Battery = Settings.EnableBatteryLogicPassthrough.Value,
                Apc = Settings.EnableAreaPowerControlLogicPassthrough.Value,
                PowerTransmitter = Settings.EnablePowerTransmitterLogicPassthrough.Value,
            }.SendAll(0L);
        }

        // Client: store the host's values WITHOUT a refresh (join time; the device lists build fresh after
        // the join completes, so no cascade is needed and the UI is not up yet).
        internal static void SetSyncedValues(bool transformer, bool battery, bool apc, bool powerTransmitter)
        {
            _transformer = transformer;
            _battery = battery;
            _apc = apc;
            _powerTransmitter = powerTransmitter;
        }

        // Client: store the host's values AND refresh (live PassthroughSettingsMessage after a host toggle).
        internal static void ApplyFromHost(bool transformer, bool battery, bool apc, bool powerTransmitter)
        {
            SetSyncedValues(transformer, battery, apc, powerTransmitter);
            Patches.CableNetworkPatches.RefreshAllNetworks();
        }
    }
}
