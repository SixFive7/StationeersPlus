using Assets.Scripts.Networking;
using Assets.Scripts.Objects;
using BepInEx;
using BepInEx.Logging;
using HarmonyLib;
using LaunchPadBooster;
using LaunchPadBooster.Networking;
using System;
using System.Linq;
using System.Reflection;

namespace NetworkPuristPlus
{
    // Network Purist Plus removes the "long" straight pipe/cable/chute variants (the 3-, 5-, and
    // 10-segment pieces). It hides them from the build-kit mouse wheel and the Stationpedia, and
    // when a save loads it replaces every already-placed long run with the equivalent single-tile
    // pieces so existing networks keep working; a long piece that turns up mid-game (a blueprint paste,
    // another mod) is expanded into single tiles the moment it is built. It also aligns straight cables
    // to one consistent orientation: existing runs on world load, freshly built ones as they are placed
    // (so adjacent cables no longer show the band misaligned). Cosmetic only; nothing functional changes.
    //
    // v1.1: a settings panel (a master enable toggle, per-family long-piece toggles, a cable-alignment
    // toggle -- all server-authoritative; see Settings.cs) and network-version + settings enforcement on
    // join via LaunchPadBooster (the prefab-time strip/hide/cursor effects run before any join, so each
    // machine first applies its own config -- a JoinValidator turns a mismatch into a clean rejection
    // rather than a silent desync of the build-kit option lists; see SettingsConfigSync.cs and the
    // IJoinValidator implementation below).
    [BepInDependency("stationeers.launchpad", BepInDependency.DependencyFlags.HardDependency)]
    [BepInPlugin(PluginGuid, PluginName, PluginVersion)]
    public class NetworkPuristPlusPlugin : BaseUnityPlugin, IJoinValidator, IJoinSuffixSerializer
    {
        public const string PluginGuid = "net.networkpuristplus";
        public const string PluginName = "NetworkPuristPlus";
        public const string PluginVersion = "1.1.0";

        // The LaunchPadBooster mod handle. Registering it (and setting Networking.Required = true) makes
        // LaunchPadBooster reject a joining client that does not have NetworkPuristPlus, or has a different
        // version. The JoinValidator below adds the per-family / master settings to that check.
        internal static readonly Mod MOD = new Mod(PluginName, PluginVersion);

        // BepInEx log source (-> BepInEx\LogOutput.log, also mirrored into Player.log by StationeersLaunchPad).
        internal static ManualLogSource Log;

        // The plugin's MonoBehaviour instance (for StartCoroutine).
        internal static NetworkPuristPlusPlugin Instance;

        // --- In-game `~` console (Util.Commands.ConsoleWindow) ---------------------------------------
        // The in-game console does NOT show plain UnityEngine.Debug.Log output -- only ConsoleWindow.Print*
        // calls. We resolve ConsoleWindow.PrintAction / PrintError by reflection so the build does not
        // depend on the exact namespace and it degrades gracefully if the type ever moves.
        private static bool _consoleResolved;
        private static MethodInfo _consolePrintAction;   // PrintAction(string output, bool aged)
        private static MethodInfo _consolePrintError;    // PrintError(string output, bool suppressStacktrace)

        private static void ResolveConsole()
        {
            if (_consoleResolved) return;
            _consoleResolved = true;
            try
            {
                Type t = AccessTools.TypeByName("Util.Commands.ConsoleWindow")
                      ?? AccessTools.TypeByName("Assets.Scripts.ConsoleWindow")
                      ?? AccessTools.TypeByName("ConsoleWindow")
                      ?? AppDomain.CurrentDomain.GetAssemblies()
                           .SelectMany(a => { try { return a.GetTypes(); } catch { return Type.EmptyTypes; } })
                           .FirstOrDefault(x => x.Name == "ConsoleWindow" && x.IsClass && x.IsAbstract && x.IsSealed); // static class
                if (t == null) return;
                _consolePrintAction = AccessTools.Method(t, "PrintAction", new[] { typeof(string), typeof(bool) })
                                   ?? AccessTools.Method(t, "PrintAction", new[] { typeof(string) });
                _consolePrintError = AccessTools.Method(t, "PrintError", new[] { typeof(string), typeof(bool) })
                                  ?? AccessTools.Method(t, "PrintError", new[] { typeof(string) });
            }
            catch { }
        }

        private static void ConsolePrint(MethodInfo m, string text, bool flag)
        {
            if (m == null) return;
            try { m.Invoke(null, m.GetParameters().Length >= 2 ? new object[] { text, flag } : new object[] { text }); }
            catch { }
        }

        // A "player-visible" log line. Three channels:
        //   - BepInEx log (BepInEx\LogOutput.log; StationeersLaunchPad also mirrors it into Player.log)
        //   - Unity's Player.log via Debug.Log
        //   - the in-game `~` console via ConsoleWindow (the channel a player actually sees while playing)
        internal static void PlayerLog(string message)
        {
            Log?.LogInfo(message);
            UnityEngine.Debug.Log($"[{PluginName}] {message}");
            ResolveConsole();
            ConsolePrint(_consolePrintAction, $"[{PluginName}] {message}", false);
        }

        internal static void PlayerWarn(string message)
        {
            Log?.LogWarning(message);
            UnityEngine.Debug.LogWarning($"[{PluginName}] {message}");
            ResolveConsole();
            ConsolePrint(_consolePrintError, $"[{PluginName}] {message}", true);
        }

        internal static void PlayerError(string message)
        {
            Log?.LogError(message);
            UnityEngine.Debug.LogError($"[{PluginName}] {message}");
            ResolveConsole();
            ConsolePrint(_consolePrintError, $"[{PluginName}] {message}", true);
        }

        private void Awake()
        {
            Log = Logger;
            Instance = this;

            // Bind the settings panel (master + per-family long-piece toggles + cable-alignment toggle,
            // all in "Server - *" sections). Done in Awake so LongVariantRegistry.Build() and the patches
            // see the values; the host's values are authoritative and a mismatched joiner is rejected.
            Settings.Bind(Config);
            SettingsConfigSync.HookHostBroadcast();   // push live changes to connected clients

            // StationeersLaunchPad loads mods progressively. Prefab.OnPrefabsLoaded fires once,
            // after every mod's content is registered and every prefab's OnPrefabLoad has run, so:
            //   - the long-variant scan sees mod-added prefabs (e.g. a mod that clones the pipe kit);
            //   - the kit Constructables we strip have already been through MultiConstructor.OnPrefabLoad's
            //     own null-strip, so our RemoveAll is the last word;
            //   - it runs before the Stationpedia is built, so HideInStationpedia takes effect there;
            //   - it is the documented place to finish LaunchPadBooster networking setup (Required,
            //     RegisterMessage, JoinValidator) -- matches PowerTransmitterPlus.
            Prefab.OnPrefabsLoaded += OnPrefabsLoaded;
        }

        private void OnPrefabsLoaded()
        {
            Prefab.OnPrefabsLoaded -= OnPrefabsLoaded;

            try
            {
                // LaunchPadBooster: require the mod (rejects a joiner without it / with a different
                // version), register the settings-sync message, and install the JoinValidator that also
                // enforces a settings match (the master + per-family + cable-alignment values decide which
                // Constructables get stripped at prefab-load time, which happens before any join, so a
                // value mismatch desyncs the build-kit option lists -- a clean rejection is the fix). The
                // IJoinSuffixSerializer pushes the host's settings into the joiner's world snapshot for
                // its own logging; the joiner already has matching values (the validator guaranteed it).
                MOD.Networking.Required = true;
                MOD.Networking.RegisterMessage<SettingsConfigMessage>();
                MOD.Networking.JoinValidator = this;
                MOD.Networking.JoinSuffixSerializer = this;

                LongVariantRegistry.Build();          // map long -> base (per-family gated), hide from Stationpedia, strip kit wheels, set Straight on straight pieces / align cursors
                new Harmony(PluginGuid).PatchAll();   // World.OnLoadingFinished (rebuild long pieces + align cables), Cable.OnRegistered (align as built), Constructor.SpawnConstruct (rewrite long placements), SPDADataHandler.HandleThingPageOverrides (re-hide)

                if (!Settings.MasterEnabled)
                    PlayerWarn($"v{PluginVersion}: the master toggle (Enable Network Purist Plus) is OFF -- the mod does nothing this session. (All players on a server must have the same value; a joining client whose value differs from the host's is rejected.)");
                else if (LongVariantRegistry.LongToBase.Count == 0 && !Settings.CableAlignmentEnabled)
                    PlayerWarn($"v{PluginVersion}: no long-variant prefabs to remove (every family toggle off, or the game version changed) and cable alignment is off -- nothing to do.");
                else if (LongVariantRegistry.LongToBase.Count == 0)
                    PlayerWarn($"v{PluginVersion}: no long-variant prefabs found to remove (every family toggle off, or the game version changed). Cable alignment still active.");
                else
                    PlayerLog($"v{PluginVersion} active: {LongVariantRegistry.LongToBase.Count} long-variant prefab(s) removed from build menus and the Stationpedia; {LongVariantRegistry.StrippedKitCount} kit(s) cleaned. Long pieces are rebuilt from single tiles on load and rewritten at build time" + (Settings.CableAlignmentEnabled ? "; straight cables are aligned to a consistent orientation on load and as they are built." : " (cable alignment is off).") + " Server-authoritative; all players must run the same version and settings.");
            }
            catch (Exception e)
            {
                PlayerError($"failed to initialise: {e}");
            }
        }

        // ---------------------------------------------------------------------------------------------
        // IJoinValidator: per-mod handshake invoked by LaunchPadBooster's VerifyPlayer / VerifyPlayerRequest
        // postfixes on the remote-join path (see Research/Protocols/LaunchPadBoosterNetworking.md). Both
        // sides serialize their settings in the same order; the read side rejects on the first mismatch
        // with a readable per-setting error. Does NOT fire for single-player or host-own-world load (those
        // paths apply each machine's local config and there is no peer to disagree with). The mod-present /
        // version check is LaunchPadBooster's own Networking.Required handshake; this validator adds the
        // settings on top. Wire-format additions go at the END to keep the read order stable.
        public void SerializeJoinValidate(RocketBinaryWriter writer)
        {
            writer.WriteBoolean(Settings.Enabled?.Value ?? true);
            writer.WriteBoolean(Settings.RemoveLongGasPipes?.Value ?? true);
            writer.WriteBoolean(Settings.RemoveLongLiquidPipes?.Value ?? true);
            writer.WriteBoolean(Settings.RemoveLongInsulatedPipes?.Value ?? true);
            writer.WriteBoolean(Settings.RemoveLongChutes?.Value ?? true);
            writer.WriteBoolean(Settings.RemoveLongSuperHeavyCables?.Value ?? true);
            writer.WriteBoolean(Settings.AlignStraightCables?.Value ?? true);
        }

        public bool ProcessJoinValidate(RocketBinaryReader reader, out string error)
        {
            bool remoteEnabled = reader.ReadBoolean();
            bool remoteGas = reader.ReadBoolean();
            bool remoteLiquid = reader.ReadBoolean();
            bool remoteInsulated = reader.ReadBoolean();
            bool remoteChutes = reader.ReadBoolean();
            bool remoteSuperHeavy = reader.ReadBoolean();
            bool remoteAlign = reader.ReadBoolean();

            if ((error = Mismatch("Enable Network Purist Plus", remoteEnabled, Settings.Enabled?.Value ?? true)) != null) return false;
            if ((error = Mismatch("Remove Long Gas Pipes", remoteGas, Settings.RemoveLongGasPipes?.Value ?? true)) != null) return false;
            if ((error = Mismatch("Remove Long Liquid Pipes", remoteLiquid, Settings.RemoveLongLiquidPipes?.Value ?? true)) != null) return false;
            if ((error = Mismatch("Remove Long Insulated Pipes", remoteInsulated, Settings.RemoveLongInsulatedPipes?.Value ?? true)) != null) return false;
            if ((error = Mismatch("Remove Long Chutes", remoteChutes, Settings.RemoveLongChutes?.Value ?? true)) != null) return false;
            if ((error = Mismatch("Remove Long Super-Heavy Cables", remoteSuperHeavy, Settings.RemoveLongSuperHeavyCables?.Value ?? true)) != null) return false;
            if ((error = Mismatch("Align Straight Cables", remoteAlign, Settings.AlignStraightCables?.Value ?? true)) != null) return false;

            error = null;
            return true;
        }

        // Returns a readable rejection message if the remote value differs from the local one, else null.
        // "Local" here is whichever side runs ProcessJoinValidate: the server reading the client's payload,
        // or the client reading the server's. The message tells the player which way to change it; in
        // practice the server's value is the one that wins, so a client should match the server.
        private static string Mismatch(string settingName, bool remote, bool local)
        {
            if (remote == local) return null;
            return $"NetworkPuristPlus: setting '{settingName}' does not match -- this side has it {(local ? "on" : "off")}, the other side has it {(remote ? "on" : "off")}. Set it the same in the mod settings panel on both ends (the server's value is the one that takes effect) and restart Stationeers before joining.";
        }

        // ---------------------------------------------------------------------------------------------
        // IJoinSuffixSerializer: writes (server side) / reads (client side) the host's settings as part of
        // the world-snapshot transmission to a joining client. PowerTransmitterPlus uses this same hook
        // because a NetworkManager.PlayerConnected rebroadcast fires before the joiner enters the broadcast
        // list (see Research/Protocols/PlayerConnectedThingFindTiming.md). The joiner already has matching
        // values (the JoinValidator above guaranteed it); this just confirms them into the client-side
        // mirror used for logging. Field order MUST match between Serialize and Deserialize.
        public void SerializeJoinSuffix(RocketBinaryWriter writer)
        {
            SettingsConfigMessage.FromLocalConfig().Serialize(writer);
        }

        public void DeserializeJoinSuffix(RocketBinaryReader reader)
        {
            var msg = new SettingsConfigMessage();
            msg.Deserialize(reader);
            SettingsConfigSync.OnHostConfigReceived(msg);
        }
    }
}
