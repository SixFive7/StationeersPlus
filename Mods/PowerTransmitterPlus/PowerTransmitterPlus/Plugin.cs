using Assets.Scripts.Networking;
using Assets.Scripts.Objects;
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
    public class PowerTransmitterPlusPlugin : BaseUnityPlugin, IJoinValidator
    {
        public const string PluginGuid = "net.powertransmitterplus";
        public const string PluginName = "PowerTransmitterPlus";
        public const string PluginVersion = "1.3.1";

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

            // Capture once, here, before any code path reads LogicTypeRegistry.
            // AutoAimPatched is the authoritative flag for the rest of the
            // process; the ConfigEntry itself can still be toggled in the
            // main-menu settings panel but toggles only affect the NEXT run.
            AutoAimPatched = EnableAutoAim.Value;

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

                var harmony = new Harmony(PluginGuid);
                harmony.PatchAll();

                // After Harmony patches are live, inject our IC10 named constants
                // so MIPS source can refer to MicrowaveSourceDraw etc. by name.
                Ic10ConstantsPatcher.Apply();

                Log.LogInfo("Patches applied successfully");
            }
            catch (Exception e)
            {
                Log.LogFatal($"Failed to apply patches: {e}");
            }
        }

        // IJoinValidator: per-mod handshake invoked by LaunchPadBooster's
        // VerifyPlayer / VerifyPlayerRequest postfixes on the remote-join path.
        // Both sides serialize their AutoAimPatched value; the read side rejects
        // the connection with a readable error if the values disagree. Does NOT
        // fire for single-player or host-own-world load; those paths rely on
        // the RequireRestart banner + boot-time capture instead.
        public void SerializeJoinValidate(RocketBinaryWriter writer)
        {
            writer.WriteBoolean(AutoAimPatched);
        }

        public bool ProcessJoinValidate(RocketBinaryReader reader, out string error)
        {
            var remote = reader.ReadBoolean();
            if (remote == AutoAimPatched)
            {
                error = null;
                return true;
            }
            error = remote
                ? "PowerTransmitterPlus: server has Enable Auto-Aim on, your game has it off. Enable it in the mod settings panel and restart Stationeers before joining."
                : "PowerTransmitterPlus: server has Enable Auto-Aim off, your game has it on. Disable it in the mod settings panel and restart Stationeers before joining.";
            return false;
        }
    }
}
