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
        public const string PluginVersion = "0.2.0";

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
            // Server-authoritative ConfigEntry values are replicated to clients via the
            // join-suffix snapshot only (see PassthroughSettingsSync.cs). StationeersLaunchPad
            // exposes no per-mod ConfigEntry UI surface mid-session, so a host-side
            // SettingChanged subscription would never fire from a UI toggle. The host's
            // current value rides Plugin.SerializeJoinSuffix when a client joins.
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
                MOD.Networking.RegisterMessage<PriorityMessage>();
                // One snapshot message covers all four fault registries (per-tick full sync,
                // POWER.md §13); the former four per-transition messages are gone.
                MOD.Networking.RegisterMessage<FaultRegistrySnapshotMessage>();
                MOD.Networking.JoinSuffixSerializer = this;

                var harmony = new Harmony(PluginGuid);
                harmony.PatchAll();

                // Option B (POWER.md §4.3): catch a wrong-tier junction the instant any topology
                // mutation creates one. CableNetwork.OnNetworkChanged is a main-thread-only event fired
                // by every membership change (placement, merge, split, load, device add), so the handler
                // re-checks and burns synchronously before the next tick ever sees the violation. The
                // per-tick worker sweep (VoltageTierEnforcer.Run) remains the backstop for any path this
                // does not cover. Host-only: the handler early-outs on !RunSimulation. Subscribed once
                // (OnPrefabsLoaded runs a single time); never unsubscribed -- the plugin lives for the
                // process and the static event is harmless on a client (burns are RunSimulation-gated).
                Assets.Scripts.Networks.CableNetwork.OnNetworkChanged += VoltageTierEnforcer.RequestRecheck;

                // Run the runtime recipe override AFTER the game's GameData XML pipeline has processed
                // every mod's overlays, so the configured multiplier always wins over any shipped overlay
                // (including this mod's own GameData/cable-recipes.xml). Plugin.OnPrefabsLoaded runs before
                // WorldManager.LoadGameDataAsync iterates mod GameData folders, so calling ApplyRecipeCost
                // directly here lets the overlay clobber the runtime value.
                WorldManager.OnGameDataLoaded += CableCostPatches.ApplyRecipeCost;
                // Cable Watts caps and APC charge rate are enforced at runtime via CableMax and the
                // AreaPowerControl patches; no prefab or instance field is rewritten (POWER.md §0.2
                // non-mutating decision), so removing the mod reverts cables to vanilla ratings.
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

            // Then the five server-authoritative Enable*LogicPassthrough toggles (order must match
            // DeserializeJoinSuffix), so the joining client computes the merge with the host's values.
            writer.WriteBoolean(Settings.EnableTransformerLogicPassthrough.Value);
            writer.WriteBoolean(Settings.EnableBatteryLogicPassthrough.Value);
            writer.WriteBoolean(Settings.EnableAreaPowerControlLogicPassthrough.Value);
            writer.WriteBoolean(Settings.EnablePowerTransmitterLogicPassthrough.Value);
            writer.WriteBoolean(Settings.EnableUmbilicalLogicPassthrough.Value);

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

            // Fault-registry join handshake (POWER.md §13 mid-cooldown join): all four registries
            // ship their current (ReferenceId, remainingTicks) pairs -- plus violator names for the
            // VVF entries -- so a joining client lands mid-lockout with correct flash + countdown
            // state before the first per-tick snapshot arrives.
            writer.WriteBoolean(Settings.EnableTransformerOverloadProtection.Value);
            int tick = ElectricityTickCounter.CurrentTick;
            WriteRemaining(writer, BrownoutRegistry.SnapshotRemaining(tick));
            WriteRemaining(writer, OverloadRegistry.SnapshotRemaining(tick));
            WriteRemaining(writer, CycleFaultRegistry.SnapshotRemaining(tick));
            var vvf = new List<(long refId, int remainingTicks, string violators)>(
                VariableVoltageFaultRegistry.SnapshotRemaining(tick));
            writer.WriteInt32(vvf.Count);
            foreach (var entry in vvf)
            {
                writer.WriteInt64(entry.refId);
                writer.WriteInt32(entry.remainingTicks);
                writer.WriteString(entry.violators ?? string.Empty);
            }
        }

        private static void WriteRemaining(RocketBinaryWriter writer, IEnumerable<KeyValuePair<long, int>> snapshot)
        {
            var entries = new List<KeyValuePair<long, int>>(snapshot);
            writer.WriteInt32(entries.Count);
            foreach (var entry in entries)
            {
                writer.WriteInt64(entry.Key);
                writer.WriteInt32(entry.Value);
            }
        }

        private static List<KeyValuePair<long, int>> ReadRemaining(RocketBinaryReader reader)
        {
            int count = reader.ReadInt32();
            var entries = new List<KeyValuePair<long, int>>(count);
            for (int i = 0; i < count; i++)
            {
                long refId = reader.ReadInt64();
                int remaining = reader.ReadInt32();
                entries.Add(new KeyValuePair<long, int>(refId, remaining));
            }
            return entries;
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
            bool umbilical = reader.ReadBoolean();
            PassthroughSettingsSync.SetSyncedValues(transformer, battery, apc, powerTransmitter, umbilical);

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

            // Fault-registry join handshake: remaining-ticks snapshots for all four registries.
            bool overloadEnabled = reader.ReadBoolean();
            OverloadSettingsSync.SetSyncedValue(overloadEnabled);
            BrownoutRegistry.ReplaceClientSnapshot(ReadRemaining(reader));
            OverloadRegistry.ReplaceClientSnapshot(ReadRemaining(reader));
            CycleFaultRegistry.ReplaceClientSnapshot(ReadRemaining(reader));
            int vvfCount = reader.ReadInt32();
            var vvf = new List<(long, int, string)>(vvfCount);
            for (int i = 0; i < vvfCount; i++)
            {
                long refId = reader.ReadInt64();
                int remaining = reader.ReadInt32();
                string violators = reader.ReadString();
                vvf.Add((refId, remaining, violators));
            }
            VariableVoltageFaultRegistry.ReplaceClientSnapshot(vvf);
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
