using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Threading;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace ClientDriver
{
    /// <summary>
    ///     ClientDriver is the in-process control plane for one Stationeers GAME CLIENT. It stands
    ///     up a loopback HTTP server and exposes: the in-game console as a readable stream, Direct
    ///     Connect and disconnect, a full state report, synthetic keyboard / mouse / wheel input
    ///     applied inside the engine, spawning, screenshots, and live BepInEx config reads and
    ///     writes.
    ///
    ///     Everything is applied by calling the game's own methods or by patching the Unity input
    ///     layer, never by faking OS input. That is deterministic, and it works while the window is
    ///     unfocused or on a desktop of its own, which is the point: a driven client sits out of the
    ///     way while an agent works.
    ///
    ///     It is one half of a two-part tool. The other half is the launcher, <c>client-rig.ps1</c>,
    ///     which provisions instances, starts and stops them, and fans a single command out across
    ///     the rig. The boundary between them is process creation: the launcher owns everything
    ///     outside the process (and must keep working when a process is dead or wedged), and this
    ///     plugin owns everything inside it (which needs the Unity main thread and the game's own
    ///     types). There is no third category. See README.md.
    ///
    ///     NOT a release mod. It is a remote control plane for the game and must never be published
    ///     to the Steam Workshop.
    /// </summary>
    [BepInPlugin(PluginGuid, PluginName, PluginVersion)]
    public class Plugin : BaseUnityPlugin
    {
        public const string PluginGuid = "net.clientdriver";
        public const string PluginName = "ClientDriver";
        public const string PluginVersion = "0.2.0";

        internal static ManualLogSource Log;
        internal static Plugin Instance;

        internal static ConfigEntry<int> Port;
        internal static ConfigEntry<bool> Enabled;
        internal static ConfigEntry<bool> AllowInputInjection;
        internal static ConfigEntry<bool> PatchUnityInput;
        internal static bool PatchUnityInputValue = true;

        internal static ConfigEntry<string> ClientIdOverride;
        internal static ConfigEntry<string> UsernameOverride;
        internal static ConfigEntry<bool> LockCookieFile;
        internal static bool LockCookieFileValue;

        internal static ConfigEntry<bool> ForceWindowed;
        internal static ConfigEntry<int> WindowWidth;
        internal static ConfigEntry<int> WindowHeight;

        internal static ConfigEntry<bool> ForceGameplayInput;
        internal static ConfigEntry<bool> ForceGameplayInputEverywhere;

        internal static ConfigEntry<int> ConsoleTeeLines;
        internal static ConfigEntry<int> ConsoleTeeMaxLineChars;
        internal static ConfigEntry<int> ConsoleTeeMaxChars;

        internal static ConfigEntry<string> InstanceRole;
        internal static ConfigEntry<int> GamePort;

        /// <summary>
        ///     The port actually bound, after the manifest has had its say. Every response that
        ///     names a port names this one, so a caller never has to work out whether the config or
        ///     the manifest won.
        /// </summary>
        internal static int EffectivePort = 27700;

        /// <summary>
        ///     The RakNet port <c>/host</c> binds when the caller names none. 27016 is the game's
        ///     own client default (<c>Settings.SettingData.GamePort</c>).
        /// </summary>
        internal static int EffectiveGamePort = 27016;

        /// <summary>
        ///     What the launcher provisioned this instance for, "client" or "host". Advisory; see
        ///     <see cref="InstanceManifest.Role"/>. The live answer is <c>/status.role</c>.
        /// </summary>
        internal static string EffectiveRole = "client";

        // Static, and deliberately never torn down from OnDestroy.
        //
        // BepInEx hosts plugin components on its manager GameObject. Something in this game's boot
        // sequence destroys that component after the mod load finishes: OnDestroy fires while the
        // process keeps running, which silently killed the listener a minute after startup in the
        // first build. The control plane must outlive that, so it belongs to the AppDomain, not to
        // the MonoBehaviour, and only a real application quit stops it. A watchdog thread re-binds
        // if the socket ever does go away.
        internal static HttpServer Server;
        private static Thread _watchdog;
        private static volatile bool _shuttingDown;
        internal static int DestroyCount;

        private void Awake()
        {
            Log = Logger;
            Instance = this;

            // The manifest is read FIRST, because it decides several of the values the config
            // entries below would otherwise own. See InstanceManifest for why it wins.
            InstanceManifest.Load();

            BindConfig();
            ApplyManifestAndConfig();

            if (!Enabled.Value)
            {
                Log.LogWarning(PluginName + " " + PluginVersion + " is disabled by config; not starting.");
                return;
            }

            VirtualInput.Enabled = AllowInputInjection.Value;
            PatchUnityInputValue = PatchUnityInput.Value;
            if (!PatchUnityInputValue) Log.LogWarning("Patch Unity Input is off; /input/* will do nothing.");

            MainThreadPump.Initialize();
            ConsoleTap.ResetEpoch();
            ConsoleTap.AttachBepInExListener();

            ApplyPatches();
            StartServer();

            // Ask the siblings who they are, once the rig has had a moment to come up. A duplicate
            // ClientId is silent and damaging, so it is worth knowing before a join rather than
            // after a test has produced meaningless results.
            if (InstanceManifest.PeerPorts.Count > 1) PeerProbe.ScanAsync(0);

            Log.LogInfo(PluginName + " " + PluginVersion + " ready on http://127.0.0.1:" +
                        EffectivePort.ToString(CultureInfo.InvariantCulture) + "/ as instance '" +
                        (string.IsNullOrEmpty(InstanceManifest.Name) ? "(unnamed)" : InstanceManifest.Name) + "'");
        }

        private void BindConfig()
        {
            Port = Config.Bind(
                "Client - Control Plane", "Port", 27700,
                new ConfigDescription(
                    "(Client-local) TCP port the control plane listens on, bound to 127.0.0.1 only. " +
                    "27700 is clear of Steam (27000-27050), the Stationeers client (27015/27016) and " +
                    "this repo's dedicated server (28015/28016). An instance manifest overrides this.",
                    null,
                    new KeyValuePair<string, int>("Order", 10)));

            Enabled = Config.Bind(
                "Client - Control Plane", "Enabled", true,
                new ConfigDescription(
                    "(Client-local) Master switch. When false the plugin loads, patches nothing, and opens no socket.",
                    null,
                    new KeyValuePair<string, int>("Order", 20)));

            AllowInputInjection = Config.Bind(
                "Client - Control Plane", "Allow Input Injection", true,
                new ConfigDescription(
                    "(Client-local) When false the Unity input patches still load but every query falls " +
                    "through to real hardware, so the driver can never fight the developer's keyboard.",
                    null,
                    new KeyValuePair<string, int>("Order", 30)));

            PatchUnityInput = Config.Bind(
                "Client - Control Plane", "Patch Unity Input", true,
                new ConfigDescription(
                    "(Client-local) When false the UnityEngine.Input patches are never applied at all, " +
                    "so /input/* stops working entirely. Diagnostic only: use it to rule this plugin out " +
                    "when another mod misbehaves somewhere on the input path.",
                    null,
                    new KeyValuePair<string, int>("Order", 40)));

            // Console tee. Bounded on three axes because an unbounded tee once took a client to a
            // 12.75 GB working set with a frozen pump. See ConsoleTap.
            ConsoleTeeLines = Config.Bind(
                "Client - Console Tee", "Max Lines Per Source", 2000,
                new ConfigDescription(
                    "(Client-local) Ring capacity for each of the two tee sources (game console, BepInEx log). " +
                    "Oldest lines are evicted first and counted in the 'dropped' field of /console/log.",
                    null,
                    new KeyValuePair<string, int>("Order", 10)));

            ConsoleTeeMaxLineChars = Config.Bind(
                "Client - Console Tee", "Max Characters Per Line", 4000,
                new ConfigDescription(
                    "(Client-local) Longer lines are truncated with a marker and counted in 'truncated'. " +
                    "A line count alone does not bound memory: during an exception storm the arriving " +
                    "lines are stack traces and one can be megabytes. 0 disables truncation.",
                    null,
                    new KeyValuePair<string, int>("Order", 20)));

            ConsoleTeeMaxChars = Config.Bind(
                "Client - Console Tee", "Max Characters Per Source", 4 * 1024 * 1024,
                new ConfigDescription(
                    "(Client-local) Total character budget per source. Oldest lines are evicted until the " +
                    "ring is back under it. This is the cap that actually holds when lines are large. " +
                    "0 disables the budget.",
                    null,
                    new KeyValuePair<string, int>("Order", 30)));

            // Identity. Only meaningful when several clients run on one machine: each instance must
            // present a distinct ClientId or the server resolves the second joiner onto the first
            // joiner's Brain. See Identity.cs for why this is a cookie value and not Steam.
            ClientIdOverride = Config.Bind(
                "Client - Identity", "Client Id", "",
                new ConfigDescription(
                    "(Client-local) Decimal ulong to present as this client's ClientId, replacing " +
                    "whatever PlayerCookie-v2.xml holds. Leave empty to use the real identity. " +
                    "Every concurrent instance needs a different value. An instance manifest overrides this.",
                    null,
                    new KeyValuePair<string, int>("Order", 10)));

            UsernameOverride = Config.Bind(
                "Client - Identity", "Username", "",
                new ConfigDescription(
                    "(Client-local) Player name to present, replacing the cookie's. Leave empty to " +
                    "use the real one. An instance manifest overrides this.",
                    null,
                    new KeyValuePair<string, int>("Order", 20)));

            LockCookieFile = Config.Bind(
                "Client - Identity", "Lock Cookie File", false,
                new ConfigDescription(
                    "(Client-local) Suppress PlayerCookie.Save() even when no override is set. " +
                    "persistentDataPath is per-Windows-user and cannot be separated, so every instance " +
                    "shares one cookie file; Save() fires on triggers as innocuous as pressing Esc in a " +
                    "running world. An identity override already implies this.",
                    null,
                    new KeyValuePair<string, int>("Order", 30)));

            // Window. A driven instance must not come up fullscreen over the developer's desktop,
            // and the launch flags cannot achieve that on their own: the game re-applies its own
            // saved FullScreen setting twice during boot and wins. See WindowMode.cs.
            ForceWindowed = Config.Bind(
                "Client - Window", "Force Windowed", false,
                new ConfigDescription(
                    "(Client-local) Keep this instance in a window of the configured size, overriding " +
                    "the game's own saved FullScreen setting. Unity's -screen-fullscreen 0 is not enough " +
                    "on its own because Settings.LoadSettings and Settings.ApplyVideoSettings overwrite " +
                    "it during boot. Off by default so a normal client is untouched. An instance " +
                    "manifest overrides this.",
                    null,
                    new KeyValuePair<string, int>("Order", 10)));

            WindowWidth = Config.Bind(
                "Client - Window", "Window Width", 800,
                new ConfigDescription(
                    "(Client-local) Window width in pixels when Force Windowed is on.",
                    null,
                    new KeyValuePair<string, int>("Order", 20)));

            WindowHeight = Config.Bind(
                "Client - Window", "Window Height", 600,
                new ConfigDescription(
                    "(Client-local) Window height in pixels when Force Windowed is on.",
                    null,
                    new KeyValuePair<string, int>("Order", 30)));

            // Gameplay input gate. InventoryManager.ManagerUpdate early-returns on Cursor.visible,
            // and an unfocused window is stuck with a visible cursor, so every per-frame gameplay
            // input consumer stops running while direct method calls keep working. See GameplayGate.
            ForceGameplayInput = Config.Bind(
                "Client - Gameplay Input", "Force Gameplay Input", false,
                new ConfigDescription(
                    "(Client-local) Hold the cursor locked and hidden so the game's per-frame gameplay " +
                    "input consumers keep running in an unfocused window. Without it /input/key and " +
                    "/input/scroll are delivered to the engine and then discarded by the gate in " +
                    "InventoryManager.ManagerUpdate. Only correct for a client nobody is sitting at: " +
                    "it takes the mouse cursor away from a real player.",
                    null,
                    new KeyValuePair<string, int>("Order", 10)));

            ForceGameplayInputEverywhere = Config.Bind(
                "Client - Gameplay Input", "Force Gameplay Input Everywhere", false,
                new ConfigDescription(
                    "(Client-local) Assert the gate outside a loaded world too (menu, loading, joining). " +
                    "By default the gate is scoped to GameState.Running and yields to confirmation " +
                    "dialogs, because holding the cursor hidden in a menu leaves nothing clickable. " +
                    "Only set this for a test that drives menus through synthetic input rather than " +
                    "through the HTTP endpoints.",
                    null,
                    new KeyValuePair<string, int>("Order", 20)));

            // Hosting. Only meaningful for an instance that will run POST /host: a listen host binds
            // a real RakNet socket, and two of them on one port is a joiner that connects to
            // something and a test that is confidently wrong.
            InstanceRole = Config.Bind(
                "Client - Hosting", "Role", "client",
                new ConfigDescription(
                    "(Client-local) What this instance is provisioned for: 'client' or 'host'. " +
                    "Advisory only. It gates nothing, because POST /host works on any instance and " +
                    "the live answer is /status.role. It is here so a reader can tell what the " +
                    "instance was meant to be. An instance manifest overrides this.",
                    null,
                    new KeyValuePair<string, int>("Order", 10)));

            GamePort = Config.Bind(
                "Client - Hosting", "Game Port", 27016,
                new ConfigDescription(
                    "(Client-local) The RakNet port POST /host binds when the request names none. " +
                    "27016 is the game's own client default. Every concurrent host needs a distinct " +
                    "value, clear of the dedicated server (28015/28016) and of any other instance. " +
                    "An instance manifest overrides this.",
                    null,
                    new KeyValuePair<string, int>("Order", 20)));
        }

        /// <summary>
        ///     Folds the manifest and the config together and records which one won for each value.
        ///     The manifest wins wherever it carries a value, because it is rewritten by the
        ///     launcher on every provision and therefore describes THIS run, whereas a .cfg is
        ///     sticky across sessions and can be persisted behind your back.
        /// </summary>
        private void ApplyManifestAndConfig()
        {
            EffectivePort = Port.Value;
            InstanceManifest.RecordSource("port", "config");
            if (InstanceManifest.Port > 0)
            {
                EffectivePort = InstanceManifest.Port;
                InstanceManifest.RecordSource("port", "manifest");
            }
            // The manifest's peer list should always contain this instance, so a rig provisioned
            // with one entry per instance needs no special case here.
            if (InstanceManifest.PeerPorts.Count == 0) InstanceManifest.PeerPorts.Add(EffectivePort);

            // ---- hosting ----
            EffectiveRole = string.IsNullOrEmpty(InstanceRole.Value) ? "client" : InstanceRole.Value.Trim();
            InstanceManifest.RecordSource("role", "config");
            if (!string.IsNullOrEmpty(InstanceManifest.Role))
            {
                EffectiveRole = InstanceManifest.Role.Trim();
                InstanceManifest.RecordSource("role", "manifest");
            }

            EffectiveGamePort = GamePort.Value > 0 ? GamePort.Value : 27016;
            InstanceManifest.RecordSource("gamePort", "config");
            if (InstanceManifest.GamePort > 0)
            {
                EffectiveGamePort = InstanceManifest.GamePort;
                InstanceManifest.RecordSource("gamePort", "manifest");
            }

            // ---- console tee caps ----
            ConsoleTap.ApplyLimits(ConsoleTeeLines.Value, ConsoleTeeMaxLineChars.Value, ConsoleTeeMaxChars.Value);

            // ---- window ----
            WindowMode.ForceWindowed = ForceWindowed.Value;
            if (WindowWidth.Value > 0) WindowMode.Width = WindowWidth.Value;
            if (WindowHeight.Value > 0) WindowMode.Height = WindowHeight.Value;
            InstanceManifest.RecordSource("window", "config");
            if (InstanceManifest.HasWindow)
            {
                WindowMode.ForceWindowed = InstanceManifest.ForceWindowed;
                if (InstanceManifest.WindowWidth > 0) WindowMode.Width = InstanceManifest.WindowWidth;
                if (InstanceManifest.WindowHeight > 0) WindowMode.Height = InstanceManifest.WindowHeight;
                InstanceManifest.RecordSource("window", "manifest");
            }
            if (WindowMode.ForceWindowed)
                Log.LogWarning("forcing windowed " + WindowMode.Width + "x" + WindowMode.Height);

            // ---- gameplay input gate ----
            GameplayGate.Force = ForceGameplayInput.Value;
            GameplayGate.Everywhere = ForceGameplayInputEverywhere.Value;
            InstanceManifest.RecordSource("gameplayInput", "config");
            if (InstanceManifest.HasGameplayInput)
            {
                GameplayGate.Force = InstanceManifest.ForceGameplayInput;
                GameplayGate.Everywhere = InstanceManifest.ForceGameplayInputEverywhere;
                InstanceManifest.RecordSource("gameplayInput", "manifest");
            }
            if (GameplayGate.Force)
                Log.LogWarning("forcing the gameplay input gate open" +
                               (GameplayGate.Everywhere ? " everywhere" : " while in a world") +
                               "; the cursor will be locked and hidden");

            // ---- identity ----
            LockCookieFileValue = LockCookieFile.Value;
            InstanceManifest.RecordSource("identity", "config");

            ulong parsedId;
            if (!string.IsNullOrEmpty(ClientIdOverride.Value) &&
                ulong.TryParse(ClientIdOverride.Value.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out parsedId))
            {
                Identity.OverrideClientId = parsedId;
            }
            else if (!string.IsNullOrEmpty(ClientIdOverride.Value))
            {
                Log.LogError("Client Id override '" + ClientIdOverride.Value + "' is not a ulong; ignoring.");
            }
            Identity.OverrideUsername = UsernameOverride.Value;

            if (InstanceManifest.ClientId != 0 || !string.IsNullOrEmpty(InstanceManifest.Username))
            {
                if (InstanceManifest.ClientId != 0) Identity.OverrideClientId = InstanceManifest.ClientId;
                if (!string.IsNullOrEmpty(InstanceManifest.Username)) Identity.OverrideUsername = InstanceManifest.Username;
                InstanceManifest.RecordSource("identity", "manifest");
            }

            if (Identity.HasOverride)
            {
                Log.LogWarning("identity override configured: ClientId=" + Identity.OverrideClientId +
                               " Username='" + Identity.OverrideUsername + "'");
            }

            if (InstanceManifest.Loaded)
                Log.LogInfo("instance manifest loaded from " + InstanceManifest.Path);
            else if (InstanceManifest.LoadError != null)
                Log.LogError("instance manifest at " + InstanceManifest.Path + " could not be read: " +
                             InstanceManifest.LoadError + "; falling back to config values");
        }

        private void ApplyPatches()
        {
            try
            {
                var harmony = new Harmony(PluginGuid);
                harmony.PatchAll(typeof(Plugin).Assembly);

                // Enter/exit counters on the per-frame input chain. Patched manually rather than by
                // attribute because one prefix/postfix pair serves every link and tells them apart
                // through __originalMethod.
                ChainProbe.Install(harmony);

                // Report exactly which of the load-bearing patches took. A silently unpatched
                // console tap would make every later test unverifiable, so it is worth a line in
                // the log either way.
                ConsoleTap.ConsolePatchApplied = IsPatched(harmony, ConsolePrintPatch.TargetMethod());
                Log.LogInfo("console tap patched: " + ConsoleTap.ConsolePatchApplied);
                Log.LogInfo("Input.GetKey patched: " + IsPatched(harmony, AccessTools.Method(
                    typeof(Input), nameof(Input.GetKey), new[] { typeof(KeyCode) })));
                Log.LogInfo("Input.mouseScrollDelta patched: " + IsPatched(harmony, AccessTools.Method(
                    typeof(Input), "get_mouseScrollDelta")));
            }
            catch (Exception ex)
            {
                Log.LogError("Harmony patching failed: " + ex);
            }
        }

        private static bool IsPatched(Harmony harmony, MethodBase target)
        {
            if (target == null) return false;
            try
            {
                var info = Harmony.GetPatchInfo(target);
                if (info == null) return false;
                foreach (var p in info.Owners) if (p == PluginGuid) return true;
                return false;
            }
            catch { return false; }
        }

        private void StartServer()
        {
            if (Server != null && Server.Running) return;
            try
            {
                var server = new HttpServer(
                    EffectivePort,
                    Router.Handle,
                    m => Log.LogInfo(m),
                    m => Log.LogError(m));
                Server = server.Start() ? server : null;
            }
            catch (Exception ex)
            {
                Log.LogError("control plane failed to start: " + ex);
                Server = null;
            }

            if (_watchdog != null) return;
            _watchdog = new Thread(WatchdogLoop) { IsBackground = true, Name = "ClientDriver-Watchdog" };
            _watchdog.Start();

            // Application.quitting is the only teardown signal that actually means "the process is
            // going away". OnDestroy does not.
            Application.quitting += () =>
            {
                _shuttingDown = true;
                try { Server?.Stop(); } catch { }
            };
        }

        private static void WatchdogLoop()
        {
            while (!_shuttingDown)
            {
                Thread.Sleep(5000);
                if (_shuttingDown) return;
                try
                {
                    if (Server != null && Server.Running) continue;
                    Log.LogWarning("control plane socket is down; re-binding");
                    var server = new HttpServer(
                        EffectivePort, Router.Handle, m => Log.LogInfo(m), m => Log.LogError(m));
                    Server = server.Start() ? server : null;
                }
                catch (Exception ex)
                {
                    Log.LogError("watchdog re-bind failed: " + ex.Message);
                }
            }
        }

        private void OnDestroy()
        {
            // Log it, do not act on it. See the note on Server above.
            DestroyCount++;
            Log.LogWarning("plugin component destroyed (count=" + DestroyCount +
                           "); control plane deliberately left running");
        }
    }

    /// <summary>
    ///     Primary drain for the main-thread queue.
    ///
    ///     <c>ImGuiManager.LateUpdate</c> runs every frame from the splash screen onwards: it is
    ///     what draws the in-game console, the loading screen and the menu overlays, so it is live
    ///     at every phase of the client's life and does not depend on any object this plugin owns.
    ///     That last part matters, because the BepInEx plugin component itself gets destroyed
    ///     partway through this game's boot (see the note on <c>Plugin.Server</c>), which would
    ///     otherwise take the pump down with it.
    /// </summary>
    [HarmonyPatch]
    internal static class FramePumpPatch
    {
        private static MethodBase Resolve()
        {
            var type = AccessTools.TypeByName("Assets.Scripts.UI.ImGuiManager") ?? AccessTools.TypeByName("ImGuiManager");
            return type == null ? null : AccessTools.Method(type, "LateUpdate");
        }

        internal static MethodBase TargetMethod() => Resolve();

        internal static bool Prepare() => Resolve() != null;

        internal static void Postfix()
        {
            try { MainThreadPump.PumpFromFrame(); }
            catch { }
            // Re-assert the window size about once a second. The game applies its own video
            // settings twice during boot and a mod or an options panel can move the window later;
            // a driven instance going fullscreen mid-session over the developer's desktop is
            // exactly what this prevents. No-op unless configured.
            try { WindowMode.Tick(); }
            catch { }
        }
    }

    /// <summary>
    ///     Backup drain for the main-thread queue.
    ///
    ///     A windowed client ticks MonoBehaviour.Update every frame, so the primary pump is enough.
    ///     The headless dedicated server does not, which is why InspectorPlus and ScenarioRunner
    ///     both drive their work from an ElectricityTick postfix. The same postfix is wired here so
    ///     a client that somehow stops ticking Update (a minimised window with rendering suspended
    ///     is the realistic case) still drains queued work. The patch target is resolved
    ///     reflectively and the class skips itself if the method is absent, so a game update that
    ///     renames it degrades to Update-only rather than failing to load.
    /// </summary>
    [HarmonyPatch]
    internal static class FallbackPumpPatch
    {
        private static MethodBase Resolve()
        {
            var type = AccessTools.TypeByName("Assets.Scripts.Atmospherics.ElectricityManager")
                       ?? AccessTools.TypeByName("ElectricityManager");
            return type == null ? null : AccessTools.Method(type, "ElectricityTick");
        }

        internal static MethodBase TargetMethod() => Resolve();

        internal static bool Prepare() => Resolve() != null;

        internal static void Postfix()
        {
            try { MainThreadPump.PumpFromFallback(); }
            catch { }
        }
    }
}
