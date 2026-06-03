using Assets.Scripts.Networking;
using BepInEx.Configuration;

namespace PowerGridPlus
{
    // Server-authoritative read accessors for the four Enable*LogicPassthrough toggles.
    // PassthroughTopology reads the Effective* properties here -- not Settings.Enable*.Value
    // directly -- so the host's values drive every peer:
    //   - host / single-player: the local BepInEx config value.
    //   - client: the value pushed by the host during the join handshake; falls back to
    //     local config until the join-suffix arrives.
    //
    // Why there is no live "host changed a toggle mid-session, broadcast it now" path:
    // StationeersLaunchPad does not expose per-mod ConfigEntry values to any UI surface
    // while a save is loaded. The in-game pause-menu "Settings" button renders only
    // LaunchPad's own Configs.Sorted entries; per-mod entries are only reachable from
    // the main-menu WorkshopMenu and the pre-load ManualLoadWindow. A SettingChanged
    // handler subscribed to Settings.Enable*LogicPassthrough therefore cannot fire from
    // a host UI toggle while a multiplayer session is active. Full evidence:
    // Research/Patterns/StationeersLaunchPadSettingsGrouping.md "Mid-session mutability".
    //
    // The join-suffix snapshot (Plugin.SerializeJoinSuffix) is the sole sync path. If a
    // future requirement adds a custom in-world UI for live tuning, both the writer
    // (which already calls Plugin.SerializeJoinSuffix at join time) AND a fresh
    // host->client live-broadcast message would need to be re-introduced here.
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
            // Host / single-player: local config is authoritative. Client: the host's
            // pushed value, falling back to local until the join-suffix arrives.
            if (!NetworkManager.IsActive || NetworkManager.IsServer) return local?.Value ?? false;
            return synced ?? local?.Value ?? false;
        }

        // Client: store the host's values from the join-suffix snapshot. The
        // post-join data-device-list rebuild reads these via the Effective*
        // accessors above; no further refresh is necessary because the lists
        // build fresh after the join completes and the in-world UI is not yet up.
        internal static void SetSyncedValues(bool transformer, bool battery, bool apc, bool powerTransmitter)
        {
            _transformer = transformer;
            _battery = battery;
            _apc = apc;
            _powerTransmitter = powerTransmitter;
        }
    }
}
