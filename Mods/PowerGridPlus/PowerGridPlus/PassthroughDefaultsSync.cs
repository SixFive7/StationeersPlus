using Assets.Scripts.Networking;
using BepInEx.Configuration;

namespace PowerGridPlus
{
    // Server-authoritative read accessors for the six per-kind passthrough DEFAULT settings
    // (the mode a device effectively has while its LogicPassthroughMode has never been
    // explicitly set: newly built devices, and devices from a save running this mod for the
    // first time). PassthroughModeStore.GetDefaultMode reads the Effective* properties here --
    // not Settings.*PassthroughDefault.Value directly -- so the host's values drive every peer:
    //   - host / single-player: the local BepInEx config value.
    //   - client: the value pushed by the host during the join handshake; falls back to
    //     local config until the join-suffix arrives.
    //
    // The sync matters because GetDefaultMode is consulted on clients for every device with no
    // explicit mode entry, and the merged data-device lists (motherboard dropdowns, logic
    // readers) must match the host's; a client whose local defaults differed would fold a
    // different set of networks together.
    //
    // Why there is no live "host changed a default mid-session, broadcast it now" path:
    // StationeersLaunchPad does not expose per-mod ConfigEntry values to any UI surface
    // while a save is loaded. The in-game pause-menu "Settings" button renders only
    // LaunchPad's own Configs.Sorted entries; per-mod entries are only reachable from
    // the main-menu WorkshopMenu and the pre-load ManualLoadWindow. A SettingChanged
    // handler subscribed to Settings.*PassthroughDefault therefore cannot fire from
    // a host UI toggle while a multiplayer session is active. Full evidence:
    // Research/Patterns/StationeersLaunchPadSettingsGrouping.md "Mid-session mutability".
    //
    // The join-suffix snapshot (Plugin.SerializeJoinSuffix) is the sole sync path. If a
    // future requirement adds a custom in-world UI for live tuning, both the writer
    // (which already calls Plugin.SerializeJoinSuffix at join time) AND a fresh
    // host->client live-broadcast message would need to be re-introduced here.
    internal static class PassthroughDefaultsSync
    {
        private static bool? _smallTransformer;
        private static bool? _otherTransformer;
        private static bool? _battery;
        private static bool? _apc;
        private static bool? _powerTransmitter;
        private static bool? _umbilical;

        internal static bool EffectiveSmallTransformer => Effective(_smallTransformer, Settings.SmallTransformerPassthroughDefault);
        internal static bool EffectiveOtherTransformer => Effective(_otherTransformer, Settings.OtherTransformerPassthroughDefault);
        internal static bool EffectiveBattery => Effective(_battery, Settings.BatteryPassthroughDefault);
        internal static bool EffectiveApc => Effective(_apc, Settings.ApcPassthroughDefault);
        internal static bool EffectivePowerTransmitter => Effective(_powerTransmitter, Settings.PowerTransmitterPassthroughDefault);
        internal static bool EffectiveUmbilical => Effective(_umbilical, Settings.UmbilicalPassthroughDefault);

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
        internal static void SetSyncedValues(bool smallTransformer, bool otherTransformer, bool battery,
            bool apc, bool powerTransmitter, bool umbilical)
        {
            _smallTransformer = smallTransformer;
            _otherTransformer = otherTransformer;
            _battery = battery;
            _apc = apc;
            _powerTransmitter = powerTransmitter;
            _umbilical = umbilical;
        }
    }
}
