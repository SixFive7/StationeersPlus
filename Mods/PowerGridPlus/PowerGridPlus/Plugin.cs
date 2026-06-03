using System;
using System.Collections.Generic;
using Assets.Scripts.Networking;
using Assets.Scripts.Objects;
using BepInEx;
using BepInEx.Logging;
using HarmonyLib;
using LaunchPadBooster;
using LaunchPadBooster.Networking;
using PowerGridPlus.Patches;

namespace PowerGridPlus
{
    /// <summary>
    ///     Power Grid Plus -- a pure-patch overhaul of the Stationeers power simulation, derived from
    ///     Sukasa's Re-Volt (MIT), plus a three-tier transmission-voltage backbone.
    /// </summary>
    [BepInDependency("stationeers.launchpad", BepInDependency.DependencyFlags.HardDependency)]
    [BepInPlugin(PluginGuid, PluginName, PluginVersion)]
    public class Plugin : BaseUnityPlugin, IJoinSuffixSerializer
    {
        public const string PluginGuid = "net.powergridplus";
        public const string PluginName = "Power Grid Plus";
        public const string PluginVersion = "0.1.0";

        internal static readonly Mod MOD = new Mod(PluginName, PluginVersion);
        internal static ManualLogSource Log;

        private void Awake()
        {
            Log = Logger;
            // Marshals the device-list refresh cascade (CableNetworkPatches) back to the main thread; it
            // can be triggered from the UniTask power-worker thread. Shared helper linked from
            // Patterns/Threading. See Research/Patterns/MainThreadDispatcher.md.
            StationeersPlus.Shared.MainThreadDispatcher.Init(
                "PowerGridPlus_MainThreadDispatcher", msg => Log?.LogError(msg));
            Settings.Bind(Config);
            // Wire the host-side broadcast of the (server-authoritative) passthrough toggles now that the
            // ConfigEntries exist; a live toggle then refreshes locally and replicates to clients.
            PassthroughSettingsSync.HookHostBroadcast();
            Prefab.OnPrefabsLoaded += OnPrefabsLoaded;
            Log.LogInfo($"{PluginName} v{PluginVersion} loaded; patches deferred to prefab load");
        }

        private void OnPrefabsLoaded()
        {
            Prefab.OnPrefabsLoaded -= OnPrefabsLoaded;

            if (TryFindIncompatibleMod(out var modName))
            {
                Log.LogFatal($"{PluginName} refuses to load: incompatible mod '{modName}' is also loaded. " +
                             "Both mods rewrite or extend the same vanilla power-tick / cable-type surface and would " +
                             "silently fight or guess. Disable one of them. No patches applied.");
                return;
            }

            try
            {
                MOD.Networking.Required = true;
                // F: replicate per-device LogicPassthroughMode to clients. Live changes go out via
                // PassthroughModeMessage; the full current state to a joining client via the
                // IJoinSuffixSerializer below. Without this a client computes the data-device-list merge
                // with default modes and its motherboard dropdowns diverge from the host.
                MOD.Networking.RegisterMessage<PassthroughModeMessage>();
                MOD.Networking.RegisterMessage<PassthroughSettingsMessage>();
                MOD.Networking.RegisterMessage<PriorityMessage>();
                MOD.Networking.RegisterMessage<ShedStateMessage>();
                MOD.Networking.JoinSuffixSerializer = this;

                var harmony = new Harmony(PluginGuid);
                harmony.PatchAll();

                // Run the runtime recipe override AFTER the game's GameData XML pipeline has processed
                // every mod's overlays, so the configured multiplier always wins over any shipped overlay
                // (including this mod's own GameData/cable-recipes.xml). Plugin.OnPrefabsLoaded runs before
                // WorldManager.LoadGameDataAsync iterates mod GameData folders, so calling ApplyRecipeCost
                // directly here lets the overlay clobber the runtime value.
                WorldManager.OnGameDataLoaded += CableCostPatches.ApplyRecipeCost;
                Ic10ConstantsPatcher.Apply();

                Log.LogInfo($"{PluginName} patches applied");
            }
            catch (Exception e)
            {
                Log.LogFatal($"{PluginName} failed to apply patches: {e}");
            }
        }

        // IJoinSuffixSerializer: ship the full set of explicit per-device LogicPassthroughMode overrides
        // to a joining client as part of the world snapshot, so the client computes the same merged data
        // device lists as the host from its first read. Fires only on a remote join (host PackageJoinData /
        // client ProcessJoinData after ProcessThings); not in single-player or host-own-world load. Live
        // post-join changes are handled by PassthroughModeMessage. Field order must match between the two
        // methods. See Research/Protocols/LaunchPadBoosterNetworking.md.
        public void SerializeJoinSuffix(RocketBinaryWriter writer)
        {
            var entries = new List<KeyValuePair<long, int>>(PassthroughModeStore.SnapshotEntries());
            writer.WriteInt32(entries.Count);
            foreach (var entry in entries)
            {
                writer.WriteInt64(entry.Key);
                writer.WriteInt32(entry.Value);
            }

            // Then the four server-authoritative Enable*LogicPassthrough toggles (order must match
            // DeserializeJoinSuffix), so the joining client computes the merge with the host's values.
            writer.WriteBoolean(Settings.EnableTransformerLogicPassthrough.Value);
            writer.WriteBoolean(Settings.EnableBatteryLogicPassthrough.Value);
            writer.WriteBoolean(Settings.EnableAreaPowerControlLogicPassthrough.Value);
            writer.WriteBoolean(Settings.EnablePowerTransmitterLogicPassthrough.Value);

            // Then the per-Transformer Priority overrides + the EnableTransformerShedding toggle. The
            // priority defaults to PriorityStore.DefaultPriority for any transformer without an entry;
            // overrides are server-authoritative and persist via PrioritySideCar. Order must match
            // DeserializeJoinSuffix.
            var priorityEntries = new List<KeyValuePair<long, int>>(PriorityStore.SnapshotEntries());
            writer.WriteInt32(priorityEntries.Count);
            foreach (var entry in priorityEntries)
            {
                writer.WriteInt64(entry.Key);
                writer.WriteInt32(entry.Value);
            }
            writer.WriteBoolean(Settings.EnableTransformerShedding.Value);

            // Currently-shedding transformer IDs (transient host-only state). The client uses these
            // to populate its BrownoutRegistry.ClientShedding mirror so a freshly-joined player sees
            // existing sheds immediately rather than waiting for the next transition.
            var shedIds = new List<long>();
            foreach (var id in BrownoutRegistry.CurrentlyLockedOut(ElectricityTickCounter.CurrentTick))
                shedIds.Add(id);
            writer.WriteInt32(shedIds.Count);
            foreach (var id in shedIds) writer.WriteInt64(id);
        }

        public void DeserializeJoinSuffix(RocketBinaryReader reader)
        {
            int count = reader.ReadInt32();
            for (int i = 0; i < count; i++)
            {
                long referenceId = reader.ReadInt64();
                int mode = reader.ReadInt32();
                PassthroughModeStore.SetModeByReference(referenceId, mode);
            }

            // Same order as SerializeJoinSuffix. SetSyncedValues (no refresh) suffices at join: the device
            // lists build fresh once the join completes, using these effective values.
            bool transformer = reader.ReadBoolean();
            bool battery = reader.ReadBoolean();
            bool apc = reader.ReadBoolean();
            bool powerTransmitter = reader.ReadBoolean();
            PassthroughSettingsSync.SetSyncedValues(transformer, battery, apc, powerTransmitter);

            // Per-Transformer priority + EnableTransformerShedding master toggle.
            int priorityCount = reader.ReadInt32();
            for (int i = 0; i < priorityCount; i++)
            {
                long referenceId = reader.ReadInt64();
                int priority = reader.ReadInt32();
                PriorityStore.SetPriorityByReference(referenceId, priority);
            }
            bool sheddingEnabled = reader.ReadBoolean();
            ShedSettingsSync.SetSyncedValue(sheddingEnabled);

            // Currently-shedding transformer IDs at join time.
            int shedCount = reader.ReadInt32();
            for (int i = 0; i < shedCount; i++)
            {
                long shedId = reader.ReadInt64();
                BrownoutRegistry.SetClientShedding(shedId, true);
            }
        }

        /// <summary>
        ///     Detects mods whose patch surface overlaps Power Grid Plus and would silently fight or guess.
        ///     Matches by loaded-assembly name because StationeersMods-loaded mods do not show up in
        ///     <c>BepInEx.Bootstrap.Chainloader.PluginInfos</c> (Re-Volt loads through StationeersMods, not
        ///     BepInEx directly).
        /// </summary>
        private static bool TryFindIncompatibleMod(out string modName)
        {
            var assemblies = AppDomain.CurrentDomain.GetAssemblies();
            for (int i = 0; i < assemblies.Length; i++)
            {
                var n = assemblies[i].GetName().Name;
                // Re-Volt (Sukasa) ships its plugin assembly as "ReVolt.dll" with namespace "ReVolt".
                if (string.Equals(n, "ReVolt", StringComparison.OrdinalIgnoreCase))
                {
                    modName = "Re-Volt";
                    return true;
                }
                // MoreCables (spacebuilder2020) is not subscribed locally; match the most likely
                // assembly-name variants without overreaching.
                if (string.Equals(n, "MoreCables", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(n, "MoreCablesMod", StringComparison.OrdinalIgnoreCase))
                {
                    modName = "MoreCables";
                    return true;
                }
            }
            modName = null;
            return false;
        }
    }
}
