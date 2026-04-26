using Assets.Scripts.Networking;
using Assets.Scripts.Objects;
using Assets.Scripts.Objects.Electrical;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using LaunchPadBooster;
using LaunchPadBooster.Networking;
using System;
using System.Collections.Generic;

namespace PowerTransmitterPlus
{
    [BepInDependency("stationeers.launchpad", BepInDependency.DependencyFlags.HardDependency)]
    [BepInPlugin(PluginGuid, PluginName, PluginVersion)]
    public class PowerTransmitterPlusPlugin : BaseUnityPlugin, IJoinValidator, IJoinSuffixSerializer
    {
        public const string PluginGuid = "net.powertransmitterplus";
        public const string PluginName = "PowerTransmitterPlus";
        public const string PluginVersion = "1.7.0";

        internal static readonly Mod MOD = new Mod(PluginName, PluginVersion);
        internal static ManualLogSource Log;

        // Defaults extracted from the Stationeers game, matching what the
        // player actually sees on a live transmitter's dish stub.
        // PowerTransmitterVisualiser.Activate() DOTweens the MonoBehaviour's
        // EmissionColor field onto the line material's _EmissionColor,
        // overriding the material's baked orange. The live value is
        // HDR cyan-blue: (R=0, G=0.4915, B=10, A=10). Split here into a
        // normalized hex "000DFF" and an intensity 10.0 so either can be
        // tweaked independently. Width matches prefab widthMultiplier = 0.1.
        internal static ConfigEntry<float> BeamWidth;
        internal static ConfigEntry<string> BeamColorHex;
        internal static ConfigEntry<float> EmissionIntensity;

        // Pulse train (power indicator). World-space so stripes look consistent
        // on beams of any length. See BeamPulseTrain for rendering detail.
        internal static ConfigEntry<float> StripeWavelength;
        internal static ConfigEntry<float> ScrollSpeed;
        internal static ConfigEntry<float> StripeTroughBrightness;

        // Distance cost: replaces vanilla's distance-based capacity derate with
        // a source-draw multiplier. See DistanceCostPatches for the math + the
        // four Harmony patches that implement it. Server-authoritative.
        internal static ConfigEntry<float> DistanceCostFactor;

        // Master toggle for the auto-aim feature. Captured into AutoAimPatched at
        // boot; that bool is what every gated surface reads. Changing the config
        // value mid-process does not flip the patched state (Harmony Prepare and
        // the LogicTypeRegistry filter run once), hence RequireRestart. In
        // multiplayer, mismatches between client and host are caught at join
        // time by the IJoinValidator implementation below.
        internal static ConfigEntry<bool> EnableAutoAim;
        internal static bool AutoAimPatched;

        // Master toggle for ceiling and wall placement of the dish prefabs. Captured
        // into NonFloorPlacementPatched at boot; PlacementPatcher reads that flag and
        // mutates AllowedRotations on the SourcePrefab and any already-cloned
        // ConstructionCursor entries. RequireRestart because Harmony Prepare and the
        // prefab-mutation pass run once at OnAllModsLoaded; flipping the toggle mid-
        // process would not undo or redo either side. Mismatches between client and
        // host are caught at join time by the IJoinValidator implementation below.
        internal static ConfigEntry<bool> EnableNonFloorPlacement;
        internal static bool NonFloorPlacementPatched;


        void Awake()
        {
            Log = Logger;

            BeamColorHex = Config.Bind(
                "Server - Visual", "Beam Color", "000DFF",
                new ConfigDescription(
                    "(Server-authoritative) Hex RGB color of the beam (no '#', no alpha). Default 000DFF is the normalized cyan-blue the game actually applies to the beam material at runtime. In multiplayer, only the host's value is used: broadcast to all clients on connect and on every change.",
                    null,
                    new KeyValuePair<string, int>("Order", 10)));

            BeamWidth = Config.Bind(
                "Server - Visual", "Beam Width", 0.1f,
                new ConfigDescription(
                    "(Server-authoritative) Thickness of the laser beam in world units. 0.1 matches the game's built-in dish beam width. In multiplayer, only the host's value is used: broadcast to all clients on connect and on every change.",
                    null,
                    new KeyValuePair<string, int>("Order", 20)));

            EmissionIntensity = Config.Bind(
                "Server - Visual", "Emission Intensity", 10.0f,
                new ConfigDescription(
                    "(Server-authoritative) HDR brightness multiplier applied to the beam color. 10.0 matches the game's built-in beam emission intensity. Raise for more glow, lower for subtlety. In multiplayer, only the host's value is used: broadcast to all clients on connect and on every change.",
                    null,
                    new KeyValuePair<string, int>("Order", 30)));

            StripeWavelength = Config.Bind(
                "Server - Pulse", "Stripe Wavelength", 2.0f,
                new ConfigDescription(
                    "(Server-authoritative) Distance in world meters between one bright pulse and the next. Same physical spacing on 5m beams and 200m beams. In multiplayer, only the host's value is used: broadcast to all clients on connect and on every change.",
                    null,
                    new KeyValuePair<string, int>("Order", 10)));

            ScrollSpeed = Config.Bind(
                "Server - Pulse", "Scroll Speed", 25.0f,
                new ConfigDescription(
                    "(Server-authoritative) Pulse scroll speed in world meters per second at full power (5 kW delivered). Scales with sqrt(intensity), so a 1 kW load runs at about 45% of this, and draws above 5 kW (possible with the distance-cost model) exceed it. In multiplayer, only the host's value is used: broadcast to all clients on connect and on every change.",
                    null,
                    new KeyValuePair<string, int>("Order", 20)));

            StripeTroughBrightness = Config.Bind(
                "Server - Pulse", "Trough Brightness", 0.5f,
                new ConfigDescription(
                    "(Server-authoritative) Beam brightness between pulses, 0..1. 1 = no visible pulsing (beam flat). 0 = troughs fully dark. Default 0.5 keeps the link clearly visible between peaks. In multiplayer, only the host's value is used: broadcast to all clients on connect and on every change.",
                    null,
                    new KeyValuePair<string, int>("Order", 30)));

            DistanceCostFactor = Config.Bind(
                "Server - Distance", "Cost Factor (k)", 5f,
                new ConfigDescription(
                    "(Server-authoritative) Per-kilometer overhead on transmitter source draw. Source pulls (1 + k * distance_m / 1000) watts for every watt delivered. k=0 = no overhead. k=5 (default) = 1km doubles to 6:1, 5km is 26:1. k=10 = 1km is 11:1. ONLY THE HOST'S VALUE AFFECTS GAMEPLAY in multiplayer. Clients' values are ignored for simulation but used for tablet/IC10 display until the host's value is pushed at connect time. Changing this on the host live-broadcasts the new value to all clients.",
                    null,
                    new KeyValuePair<string, int>("Order", 10)));

            EnableAutoAim = Config.Bind(
                "Server - Features", "Enable Auto-Aim", true,
                new ConfigDescription(
                    "(Server-authoritative) Master toggle for the auto-aim feature (MicrowaveAutoAimTarget). When off, the writable LogicType, its IC10 named constant, its tablet dropdown entry, Stationpedia page, and screen syntax highlighting are not registered at all: the feature is not just hidden, it does not exist in the process. Requires a full Stationeers restart to take effect: toggling in the settings panel while running does not re-apply Harmony patches. In multiplayer, clients whose Enable Auto-Aim value does not match the host's are rejected at join time with a clear error message.",
                    null,
                    new KeyValuePair<string, int>("Order", 10),
                    new KeyValuePair<string, bool>("RequireRestart", true)));

            EnableNonFloorPlacement = Config.Bind(
                "Server - Placement", "Allow Non-Floor Placement", true,
                new ConfigDescription(
                    "(Server-authoritative) When on, the Microwave Power Transmitter and Receiver dishes can be built on walls and ceilings as well as on the floor; the placement cursor cycles through every face the player aims at and the post-placement rotate keys cover all axes. When off, vanilla floor-only placement is preserved. Requires a full Stationeers restart to take effect; the prefab's AllowedRotations field is mutated once at boot. In multiplayer, clients whose Allow Non-Floor Placement value does not match the host's are rejected at join time with a clear error message.",
                    null,
                    new KeyValuePair<string, int>("Order", 10),
                    new KeyValuePair<string, bool>("RequireRestart", true)));

            // Capture once, here, before any code path reads LogicTypeRegistry.
            // AutoAimPatched is the authoritative flag for the rest of the
            // process; the ConfigEntry itself can still be toggled in the
            // main-menu settings panel but toggles only affect the NEXT run.
            AutoAimPatched = EnableAutoAim.Value;
            NonFloorPlacementPatched = EnableNonFloorPlacement.Value;

            MainThreadDispatcher.Init();
            DistanceConfigSync.HookHostBroadcast();
            BeamVisualConfigSync.HookHostBroadcast();

            Prefab.OnPrefabsLoaded += OnAllModsLoaded;
        }

        private void OnAllModsLoaded()
        {
            Prefab.OnPrefabsLoaded -= OnAllModsLoaded;

            try
            {
                MOD.Networking.Required = true;
                MOD.Networking.RegisterMessage<DistanceConfigMessage>();
                MOD.Networking.RegisterMessage<BeamVisualConfigMessage>();

                // Reject joins where the client's and host's boot-time AutoAim
                // patched state disagree. RequireRestart on the ConfigEntry
                // prompts the host visually in the main-menu settings panel,
                // but nothing enforces that on the remote-join path; this
                // validator is what prevents a mismatched client from entering
                // a world where the LogicType / IC10 constant / tablet UI
                // surface does not match what the host has loaded.
                MOD.Networking.JoinValidator = this;

                // Embed the per-dish auto-aim cache in the world-snapshot
                // transmission to joining clients. NetworkManager.PlayerConnected
                // fires before NetworkBase.AddClient runs (the joiner is added
                // only after VerifyConnection succeeds), so an INetworkMessage
                // broadcast from a PlayerConnected postfix never reaches the
                // joiner. IJoinSuffixSerializer injects directly into the join
                // payload between ProcessThings and AtmosphericsManager.DeserializeOnJoin
                // on the client side, so all Things are already deserialized
                // and Thing.Find resolves. See
                // Research/Protocols/PlayerConnectedThingFindTiming.md.
                MOD.Networking.JoinSuffixSerializer = this;

                var harmony = new Harmony(PluginGuid);
                harmony.PatchAll();

                // After Harmony patches are live, inject our IC10 named constants
                // so MIPS source can refer to MicrowaveSourceDraw etc. by name.
                Ic10ConstantsPatcher.Apply();

                // Lift the dish prefabs' floor-only AllowedRotations to All so the
                // construction cursor cycles between every face and the placed
                // Structure's post-placement Rotate covers every axis. No-op when
                // the toggle is off; client/host mismatches are caught above by the
                // join validator.
                if (NonFloorPlacementPatched) PlacementPatcher.Apply();

                Log.LogInfo("Patches applied successfully");
            }
            catch (Exception e)
            {
                Log.LogFatal($"Failed to apply patches: {e}");
            }
        }

        // IJoinValidator: per-mod handshake invoked by LaunchPadBooster's
        // VerifyPlayer / VerifyPlayerRequest postfixes on the remote-join path.
        // Both sides serialize their boot-time RequireRestart toggles in the same
        // order; the read side rejects the connection with a per-toggle readable
        // error on the first mismatch. Does NOT fire for single-player or
        // host-own-world load; those paths rely on the RequireRestart banner +
        // boot-time capture instead. Wire format additions go at the END to keep
        // the read order stable for older builds (LaunchPadBooster's mod-version
        // handshake rejects truly old builds first; this is belt-and-suspenders).
        public void SerializeJoinValidate(RocketBinaryWriter writer)
        {
            writer.WriteBoolean(AutoAimPatched);
            writer.WriteBoolean(NonFloorPlacementPatched);
        }

        public bool ProcessJoinValidate(RocketBinaryReader reader, out string error)
        {
            var remoteAutoAim = reader.ReadBoolean();
            var remoteNonFloorPlacement = reader.ReadBoolean();

            if (remoteAutoAim != AutoAimPatched)
            {
                error = remoteAutoAim
                    ? "PowerTransmitterPlus: server has Enable Auto-Aim on, your game has it off. Enable it in the mod settings panel and restart Stationeers before joining."
                    : "PowerTransmitterPlus: server has Enable Auto-Aim off, your game has it on. Disable it in the mod settings panel and restart Stationeers before joining.";
                return false;
            }

            if (remoteNonFloorPlacement != NonFloorPlacementPatched)
            {
                error = remoteNonFloorPlacement
                    ? "PowerTransmitterPlus: server has Allow Non-Floor Placement on, your game has it off. Enable it in the mod settings panel and restart Stationeers before joining."
                    : "PowerTransmitterPlus: server has Allow Non-Floor Placement off, your game has it on. Disable it in the mod settings panel and restart Stationeers before joining.";
                return false;
            }

            error = null;
            return true;
        }

        // IJoinSuffixSerializer: writes (and reads) two payloads as part of the
        // world-snapshot transmission to a joining client:
        //   1. Per-dish auto-aim cache (since v1.6.1).
        //   2. Seven host-authoritative config values (since v1.7.0): Cost Factor
        //      (k), Beam Width, Beam Color, Emission Intensity, Stripe Wavelength,
        //      Scroll Speed, Trough Brightness. Earlier versions tried to push
        //      these via a NetworkManager.PlayerConnected rebroadcast which fired
        //      before the joiner entered NetworkBase.Clients; the rebroadcast was
        //      removed in v1.7.0.
        //
        // Runs on the server inside NetworkServer.PackageJoinData (LPB-injected
        // into the join writer per Research/Protocols/PlayerConnectedThingFindTiming.md);
        // runs on the client inside NetworkClient.ProcessJoinData at the position
        // of the original AtmosphericsManager.DeserializeOnJoin call, which is
        // AFTER ProcessThings, so Thing.Find resolves dish ids.
        //
        // LaunchPadBooster's SectionedWriter/Reader length-prefixes our payload
        // per-mod, so a future schema change won't desync neighboring mods'
        // sections. Field order MUST match between Serialize and Deserialize.
        public void SerializeJoinSuffix(RocketBinaryWriter writer)
        {
            // 1. Auto-aim cache. Empty when AutoAimPatched is false at boot
            // because SnapshotEntries() returns no entries; receiver reads count=0
            // and skips the loop.
            var entries = new List<KeyValuePair<long, long>>();
            foreach (var pair in AutoAimState.SnapshotEntries())
            {
                entries.Add(pair);
            }
            writer.WriteInt32(entries.Count);
            foreach (var entry in entries)
            {
                writer.WriteInt64(entry.Key);
                writer.WriteInt64(entry.Value);
            }

            // 2. Host config snapshot. Same defaults as DistanceConfigSync /
            // BeamVisualConfigSync use as their fallbacks so the wire format
            // stays predictable when the BepInEx config has not been bound yet.
            writer.WriteSingle(DistanceCostFactor?.Value ?? 5f);
            writer.WriteSingle(BeamWidth?.Value ?? 0.1f);
            writer.WriteString(BeamColorHex?.Value ?? "000DFF");
            writer.WriteSingle(EmissionIntensity?.Value ?? 10f);
            writer.WriteSingle(StripeWavelength?.Value ?? 2f);
            writer.WriteSingle(ScrollSpeed?.Value ?? 25f);
            writer.WriteSingle(StripeTroughBrightness?.Value ?? 0.5f);
        }

        public void DeserializeJoinSuffix(RocketBinaryReader reader)
        {
            // 1. Auto-aim cache.
            int count = reader.ReadInt32();
            int applied = 0, missing = 0;
            for (int i = 0; i < count; i++)
            {
                long dishId = reader.ReadInt64();
                long targetId = reader.ReadInt64();
                var dish = Thing.Find(dishId) as WirelessPower;
                if (dish == null) { missing++; continue; }
                AutoAimState.RestoreCache(dish, targetId);
                applied++;
            }
            if (count > 0)
            {
                Log?.LogInfo(
                    $"Restored auto-aim cache from host join: {applied} applied, {missing} dishes not found locally");
            }

            // 2. Host config snapshot. Apply via the existing OnHostConfigReceived
            // helpers so live SettingChanged updates and join-time updates take
            // the same code branch (logging, BeamManager.InvalidateAllBeams, etc.).
            var k = reader.ReadSingle();
            var beamWidth = reader.ReadSingle();
            var beamColorHex = reader.ReadString();
            var emissionIntensity = reader.ReadSingle();
            var stripeWavelength = reader.ReadSingle();
            var scrollSpeed = reader.ReadSingle();
            var stripeTroughBrightness = reader.ReadSingle();
            DistanceConfigSync.OnHostConfigReceived(k);
            BeamVisualConfigSync.OnHostConfigReceived(new BeamVisualConfigMessage
            {
                BeamWidth = beamWidth,
                BeamColorHex = beamColorHex,
                EmissionIntensity = emissionIntensity,
                StripeWavelength = stripeWavelength,
                ScrollSpeed = scrollSpeed,
                StripeTroughBrightness = stripeTroughBrightness,
            });
        }
    }
}
