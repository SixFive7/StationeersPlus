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
            // join-suffix snapshot only (see PassthroughDefaultsSync.cs). StationeersLaunchPad
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
                // One snapshot message covers all five fault registries (per-tick full sync,
                // POWER.md §13); the former per-transition messages are gone.
                MOD.Networking.RegisterMessage<FaultRegistrySnapshotMessage>();
                MOD.Networking.JoinSuffixSerializer = this;

                var harmony = new Harmony(PluginGuid);
                harmony.PatchAll();

                // Option B (POWER.md §4.3): catch a wrong-tier junction the instant any topology
                // mutation creates one. The static CableNetwork.OnNetworkChanged event this used to
                // subscribe to was removed in game version 0.2.6403; the same firing points (every
                // CableNetwork constructor and Add(Cable)) are now Harmony postfixes in
                // VoltageTierPatches, applied by PatchAll above. Each calls
                // VoltageTierEnforcer.RequestRecheck on every membership change (placement, merge,
                // split, load), so a fresh violation still re-checks and burns synchronously before the
                // next tick ever sees it. The per-tick worker sweep (VoltageTierEnforcer.Run) remains
                // the backstop for any path this does not cover. Host-only: the handler early-outs on
                // !RunSimulation, so the patches are harmless on a client (burns are RunSimulation-gated).

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

            // Then the six server-authoritative per-kind passthrough defaults, in the fixed order
            // smallTransformer, otherTransformer, battery, apc, powerTransmitter, umbilical (order
            // must match DeserializeJoinSuffix). The client's PassthroughModeStore.GetDefaultMode
            // consults these for every device with no explicit mode entry, so the merged
            // data-device lists match the host's from the first read.
            writer.WriteBoolean(Settings.SmallTransformerPassthroughDefault.Value);
            writer.WriteBoolean(Settings.OtherTransformerPassthroughDefault.Value);
            writer.WriteBoolean(Settings.BatteryPassthroughDefault.Value);
            writer.WriteBoolean(Settings.ApcPassthroughDefault.Value);
            writer.WriteBoolean(Settings.PowerTransmitterPassthroughDefault.Value);
            writer.WriteBoolean(Settings.UmbilicalPassthroughDefault.Value);

            // Then the per-Transformer Priority overrides. The priority defaults to
            // PriorityStore.DefaultPriority for any transformer without an entry; overrides are
            // server-authoritative and persist via PrioritySideCar. Order must match
            // DeserializeJoinSuffix.
            var priorityEntries = new List<KeyValuePair<long, int>>(PriorityStore.SnapshotEntries());
            writer.WriteInt32(priorityEntries.Count);
            foreach (var entry in priorityEntries)
            {
                writer.WriteInt64(entry.Key);
                writer.WriteInt32(entry.Value);
            }

            // Fault-registry join handshake (POWER.md §13 mid-cooldown join): all five registries
            // ship their current (ReferenceId, remainingTicks) entries so a joining client lands
            // mid-lockout with correct flash + countdown state before the first per-tick snapshot
            // arrives. Fixed block order, mirrored in DeserializeJoinSuffix:
            //   1. Deprioritized:   count, then per entry int64 refId + int32 remainingTicks
            //                       + single needsW + single upstreamDemandW + single
            //                       upstreamSupplyW (the deprioritized hover triple) + single
            //                       shortfallW + byte reason + int32 victimPriority (the decision).
            //   2. DeviceOverload:  count, then per entry int64 + int32 + single valueW + single
            //                       capW + single storageW (the capacity-overload hover payload).
            //   3. CycleFault:      count, then per entry int64 + int32.
            //   4. CurrentMismatch: count, then per entry int64 + int32 + string violators.
            //   5. CableOverload:   count, then per entry int64 + int32 + single flowW + single
            //                       capW (the cable-overload hover payload).
            int tick = ElectricityTickCounter.CurrentTick;
            WriteRemainingDeprioritized(writer, DeprioritizedRegistry.SnapshotRemaining(tick));
            WriteRemainingDeviceOverload(writer, OverloadRegistry.SnapshotRemaining(tick));
            WriteRemaining(writer, CycleFaultRegistry.SnapshotRemaining(tick));
            var currentMismatch = new List<(long refId, int remainingTicks, string violators)>(
                CurrentMismatchFaultRegistry.SnapshotRemaining(tick));
            writer.WriteInt32(currentMismatch.Count);
            foreach (var entry in currentMismatch)
            {
                writer.WriteInt64(entry.refId);
                writer.WriteInt32(entry.remainingTicks);
                writer.WriteString(entry.violators ?? string.Empty);
            }
            WriteRemainingWithWatts(writer, CableOverloadRegistry.SnapshotRemaining(tick));
        }

        // The deprioritized registry carries the locked hover triple (needs, upstream demand,
        // upstream supply) plus the decision fields (shortfall, reason, victim priority) per entry;
        // the join suffix ships them so a joining client's hover shows the same numbers and reason
        // as the host's. Reason travels as a byte.
        private static void WriteRemainingDeprioritized(RocketBinaryWriter writer,
            IEnumerable<(long refId, int remainingTicks, float needsW, float upstreamDemandW, float upstreamSupplyW,
                float shortfallW, DeprioritizeReason reason, int victimPriority)> snapshot)
        {
            var entries = new List<(long refId, int remainingTicks, float needsW, float upstreamDemandW, float upstreamSupplyW,
                float shortfallW, DeprioritizeReason reason, int victimPriority)>(snapshot);
            writer.WriteInt32(entries.Count);
            foreach (var entry in entries)
            {
                writer.WriteInt64(entry.refId);
                writer.WriteInt32(entry.remainingTicks);
                writer.WriteSingle(entry.needsW);
                writer.WriteSingle(entry.upstreamDemandW);
                writer.WriteSingle(entry.upstreamSupplyW);
                writer.WriteSingle(entry.shortfallW);
                writer.WriteByte((byte)entry.reason);
                writer.WriteInt32(entry.victimPriority);
            }
        }

        private static List<(long refId, int remainingTicks, float needsW, float upstreamDemandW, float upstreamSupplyW,
                float shortfallW, DeprioritizeReason reason, int victimPriority)>
            ReadRemainingDeprioritized(RocketBinaryReader reader)
        {
            int count = reader.ReadInt32();
            var entries = new List<(long refId, int remainingTicks, float needsW, float upstreamDemandW, float upstreamSupplyW,
                float shortfallW, DeprioritizeReason reason, int victimPriority)>(count);
            for (int i = 0; i < count; i++)
            {
                long refId = reader.ReadInt64();
                int remaining = reader.ReadInt32();
                float needsW = reader.ReadSingle();
                float upstreamDemandW = reader.ReadSingle();
                float upstreamSupplyW = reader.ReadSingle();
                float shortfallW = reader.ReadSingle();
                DeprioritizeReason reason = (DeprioritizeReason)reader.ReadByte();
                int victimPriority = reader.ReadInt32();
                entries.Add((refId, remaining, needsW, upstreamDemandW, upstreamSupplyW, shortfallW, reason, victimPriority));
            }
            return entries;
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

        // Cable overload carries a per-entry (flowW, capW) float pair; the join suffix ships it so a
        // joining client's hover shows the same numbers as the host's. Device overload uses the
        // storage-aware WriteRemainingDeviceOverload / ReadRemainingDeviceOverload variants below.
        private static void WriteRemainingWithWatts(RocketBinaryWriter writer,
            IEnumerable<(long refId, int remainingTicks, float valueW, float capW)> snapshot)
        {
            var entries = new List<(long refId, int remainingTicks, float valueW, float capW)>(snapshot);
            writer.WriteInt32(entries.Count);
            foreach (var entry in entries)
            {
                writer.WriteInt64(entry.refId);
                writer.WriteInt32(entry.remainingTicks);
                writer.WriteSingle(entry.valueW);
                writer.WriteSingle(entry.capW);
            }
        }

        private static List<(long refId, int remainingTicks, float valueW, float capW)> ReadRemainingWithWatts(
            RocketBinaryReader reader)
        {
            int count = reader.ReadInt32();
            var entries = new List<(long refId, int remainingTicks, float valueW, float capW)>(count);
            for (int i = 0; i < count; i++)
            {
                long refId = reader.ReadInt64();
                int remaining = reader.ReadInt32();
                float valueW = reader.ReadSingle();
                float capW = reader.ReadSingle();
                entries.Add((refId, remaining, valueW, capW));
            }
            return entries;
        }

        // Device overload additionally carries the storage (elastic/battery) slice of the cap.
        private static void WriteRemainingDeviceOverload(RocketBinaryWriter writer,
            IEnumerable<(long refId, int remainingTicks, float valueW, float capW, float storageW)> snapshot)
        {
            var entries = new List<(long refId, int remainingTicks, float valueW, float capW, float storageW)>(snapshot);
            writer.WriteInt32(entries.Count);
            foreach (var entry in entries)
            {
                writer.WriteInt64(entry.refId);
                writer.WriteInt32(entry.remainingTicks);
                writer.WriteSingle(entry.valueW);
                writer.WriteSingle(entry.capW);
                writer.WriteSingle(entry.storageW);
            }
        }

        private static List<(long refId, int remainingTicks, float valueW, float capW, float storageW)> ReadRemainingDeviceOverload(
            RocketBinaryReader reader)
        {
            int count = reader.ReadInt32();
            var entries = new List<(long refId, int remainingTicks, float valueW, float capW, float storageW)>(count);
            for (int i = 0; i < count; i++)
            {
                long refId = reader.ReadInt64();
                int remaining = reader.ReadInt32();
                float valueW = reader.ReadSingle();
                float capW = reader.ReadSingle();
                float storageW = reader.ReadSingle();
                entries.Add((refId, remaining, valueW, capW, storageW));
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
            bool smallTransformer = reader.ReadBoolean();
            bool otherTransformer = reader.ReadBoolean();
            bool battery = reader.ReadBoolean();
            bool apc = reader.ReadBoolean();
            bool powerTransmitter = reader.ReadBoolean();
            bool umbilical = reader.ReadBoolean();
            PassthroughDefaultsSync.SetSyncedValues(smallTransformer, otherTransformer, battery,
                apc, powerTransmitter, umbilical);

            // Per-Transformer priority overrides.
            int priorityCount = reader.ReadInt32();
            for (int i = 0; i < priorityCount; i++)
            {
                long referenceId = reader.ReadInt64();
                int priority = reader.ReadInt32();
                PriorityStore.SetPriorityByReference(referenceId, priority);
            }

            // Fault-registry join handshake: remaining-ticks snapshots for all five registries, in
            // the fixed block order documented in SerializeJoinSuffix (Deprioritized + hover
            // triple, DeviceOverload + watt payload, CycleFault, CurrentMismatch + violators,
            // CableOverload + watt payload).
            DeprioritizedRegistry.ReplaceClientSnapshot(ReadRemainingDeprioritized(reader));
            OverloadRegistry.ReplaceClientSnapshot(ReadRemainingDeviceOverload(reader));
            CycleFaultRegistry.ReplaceClientSnapshot(ReadRemaining(reader));
            int currentMismatchCount = reader.ReadInt32();
            var currentMismatch = new List<(long, int, string)>(currentMismatchCount);
            for (int i = 0; i < currentMismatchCount; i++)
            {
                long refId = reader.ReadInt64();
                int remaining = reader.ReadInt32();
                string violators = reader.ReadString();
                currentMismatch.Add((refId, remaining, violators));
            }
            CurrentMismatchFaultRegistry.ReplaceClientSnapshot(currentMismatch);
            CableOverloadRegistry.ReplaceClientSnapshot(ReadRemainingWithWatts(reader));
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
