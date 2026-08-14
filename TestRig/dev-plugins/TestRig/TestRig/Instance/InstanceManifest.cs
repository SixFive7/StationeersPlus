using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;

namespace TestRig
{
    /// <summary>
    ///     Everything that makes one driven instance different from its siblings, in one file.
    ///
    ///     The rig this plugin serves used to spread that across three unconnected places:
    ///     <c>net.clientdriver.cfg</c> held the port and the identity,
    ///     <c>stationeers.launchpad.cfg</c> held the save-path override, and the launch command line
    ///     held the rest. Nothing tied them together, nothing reported them back, and two running
    ///     instances produced <c>/status</c> blobs that were indistinguishable apart from the identity
    ///     fields. Answering "which instance am I talking to" meant remembering which port was which.
    ///
    ///     The manifest is written by the launcher when it provisions an instance and read here at
    ///     <c>Awake</c>. One file, one writer, one reader.
    ///
    ///     Precedence: the manifest WINS over the BepInEx config for every value it carries, and
    ///     <see cref="SourceOf"/> records where each value came from so the winner is never a guess.
    ///     That direction is deliberate. A <c>.cfg</c> is sticky: it survives across sessions and a
    ///     mod or a previous run can persist a value into it (observed on 2026-07-30, where an
    ///     instance came up with a setting left behind by the session before and nothing indicated
    ///     it). The manifest is rewritten by the launcher on every provision, so it describes THIS
    ///     run. When no manifest is found every value falls back to the config entry and the plugin
    ///     behaves exactly as it did before, which is what a developer running a single client on
    ///     their own install gets.
    ///
    ///     Discovery order, first hit wins:
    ///
    ///       1. <c>STATIONEERS_CLIENTRIG_MANIFEST</c> in the environment, an absolute path to the file.
    ///       2. <c>instance.json</c> in the process working directory. The launcher starts each
    ///          instance with its working directory set to that instance's own state folder.
    ///       3. <c>instance.json</c> beside the game executable, for an instance launched by hand.
    /// </summary>
    internal static class InstanceManifest
    {
        internal const string EnvVar = "STATIONEERS_CLIENTRIG_MANIFEST";
        internal const string FileName = "instance.json";

        internal static bool Loaded;
        internal static string Path;
        internal static string LoadError;

        internal static string Name;
        internal static int Port;
        internal static ulong ClientId;
        internal static string Username;

        /// <summary>
        ///     What the launcher provisioned this instance FOR: "host" or "client". Advisory. It
        ///     changes nothing about how the plugin behaves; <c>/host</c> works on any instance and
        ///     the truth about what a process is doing is <c>/status.role</c>, which reads the live
        ///     engine. This exists so a reader of <c>/instance</c>, and the launcher's own teardown
        ///     ordering, can tell what the instance was MEANT to be when the control plane is not
        ///     answering.
        /// </summary>
        internal static string Role;

        /// <summary>
        ///     The port a listen host on this instance binds RakNet to. Load-bearing, unlike
        ///     <see cref="Role"/>: two hosts on one port produce a joiner that connects to something
        ///     and a test that is confidently wrong.
        /// </summary>
        internal static int GamePort;

        internal static bool HasWindow;
        internal static bool ForceWindowed;
        internal static int WindowWidth;
        internal static int WindowHeight;

        internal static bool HasGameplayInput;
        internal static bool ForceGameplayInput;
        internal static bool ForceGameplayInputEverywhere;

        internal static string SavePath;
        internal static string Desktop;
        internal static string RigRoot;

        /// <summary>
        ///     Control-plane ports of every instance in the rig, this one included. The duplicate
        ///     identity check in <see cref="PeerProbe"/> is the only consumer: it is what lets an
        ///     instance notice that a sibling is claiming the same ClientId, which is otherwise a
        ///     silent, damaging failure (the server resolves both onto one Brain).
        /// </summary>
        internal static readonly List<int> PeerPorts = new List<int>();

        private static readonly Dictionary<string, string> _sources =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        /// <summary>Where a given setting's effective value came from: "manifest", "config", or "default".</summary>
        internal static string SourceOf(string key)
        {
            string s;
            return _sources.TryGetValue(key, out s) ? s : "default";
        }

        internal static void RecordSource(string key, string source)
        {
            _sources[key] = source;
        }

        /// <summary>
        ///     Finds and reads the manifest. Never throws: a rig with a broken manifest should come
        ///     up on its config defaults and say so through <c>/instance</c>, not fail to load.
        /// </summary>
        internal static void Load()
        {
            try
            {
                Path = Locate();
                if (Path == null)
                {
                    LoadError = null;   // absent is not an error; a lone client has no manifest
                    return;
                }

                string text = File.ReadAllText(Path);
                var root = Json.Parse(text) as IDictionary;
                if (root == null)
                {
                    LoadError = "manifest at " + Path + " is not a JSON object";
                    return;
                }

                Name = Json.GetStr(root, "instanceName");
                Port = Json.GetInt(root, "port", 0);

                // ClientId is carried as a STRING on purpose. It is a ulong, and this file's own
                // number parser goes through double, which silently loses precision above 2^53.
                // A truncated ClientId is exactly the failure the field exists to prevent.
                string rawId = Json.GetStr(root, "clientId");
                if (!string.IsNullOrEmpty(rawId))
                {
                    ulong parsed;
                    if (ulong.TryParse(rawId.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out parsed))
                        ClientId = parsed;
                    else
                        LoadError = "clientId '" + rawId + "' is not a ulong";
                }

                Username = Json.GetStr(root, "username");
                Role = Json.GetStr(root, "role");
                GamePort = Json.GetInt(root, "gamePort", 0);

                // Parsed and reported, applied by nothing. The instance's save root is moved by
                // StationeersLaunchPad's SavePathOverride at provision time, which is a different
                // file and a different mechanism; writing this value into Settings.CurrentData at
                // Awake would fight it. It stays here as a record of what the launcher intended.
                // Do not mistake it for a lever.
                SavePath = Json.GetStr(root, "savePath");
                Desktop = Json.GetStr(root, "desktop");
                RigRoot = Json.GetStr(root, "rigRoot");

                var window = Json.Has(root, "window") ? root["window"] as IDictionary : null;
                if (window != null)
                {
                    HasWindow = true;
                    ForceWindowed = Json.GetBool(window, "forceWindowed", true);
                    WindowWidth = Json.GetInt(window, "width", 0);
                    WindowHeight = Json.GetInt(window, "height", 0);
                }

                var input = Json.Has(root, "gameplayInput") ? root["gameplayInput"] as IDictionary : null;
                if (input != null)
                {
                    HasGameplayInput = true;
                    ForceGameplayInput = Json.GetBool(input, "force", false);
                    ForceGameplayInputEverywhere = Json.GetBool(input, "everywhere", false);
                }

                var peers = Json.Has(root, "peerPorts") ? root["peerPorts"] as List<object> : null;
                if (peers != null)
                {
                    foreach (var p in peers)
                    {
                        int value = 0;
                        if (p is double d) value = (int)Math.Round(d);
                        else if (p is string s) int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out value);
                        if (value > 0 && !PeerPorts.Contains(value)) PeerPorts.Add(value);
                    }
                }

                Loaded = true;
            }
            catch (Exception ex)
            {
                LoadError = ex.Message;
                Loaded = false;
            }
        }

        private static string Locate()
        {
            try
            {
                string fromEnv = Environment.GetEnvironmentVariable(EnvVar);
                if (!string.IsNullOrEmpty(fromEnv) && File.Exists(fromEnv)) return fromEnv;
            }
            catch { }

            try
            {
                string cwd = System.IO.Path.Combine(Directory.GetCurrentDirectory(), FileName);
                if (File.Exists(cwd)) return cwd;
            }
            catch { }

            try
            {
                string beside = System.IO.Path.Combine(
                    System.IO.Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location) ?? ".",
                    FileName);
                if (File.Exists(beside)) return beside;
            }
            catch { }

            return null;
        }

        /// <summary>
        ///     The instance block that every state response carries. A snapshot, a screenshot or a
        ///     log line is attributable from this alone, with no cross-reference to a port table.
        /// </summary>
        internal static string DescribeJson()
        {
            var o = new Json.Obj();
            o.Str("name", string.IsNullOrEmpty(Name) ? "(unnamed)" : Name);
            o.Int("port", Plugin.EffectivePort);
            o.Str("role", Plugin.EffectiveRole);
            o.Int("gamePort", Plugin.EffectiveGamePort);
            o.Str("clientId", Identity.OverrideClientId == 0 ? null : Identity.OverrideClientId.ToString(CultureInfo.InvariantCulture));
            o.Str("username", string.IsNullOrEmpty(Identity.OverrideUsername) ? null : Identity.OverrideUsername);
            o.Bit("manifestLoaded", Loaded);
            o.Str("manifestPath", Path);
            o.Str("manifestError", LoadError);
            o.Str("desktop", Desktop);
            o.Str("rigRoot", RigRoot);
            o.Str("savePath", SavePath);

            var sources = new Json.Obj();
            foreach (var kv in _sources) sources.Str(kv.Key, kv.Value);
            o.Raw("valueSources", sources.ToString());

            var ports = new List<string>();
            foreach (var p in PeerPorts) ports.Add(p.ToString(CultureInfo.InvariantCulture));
            o.Raw("peerPorts", "[" + string.Join(",", ports.ToArray()) + "]");
            return o.ToString();
        }
    }
}
