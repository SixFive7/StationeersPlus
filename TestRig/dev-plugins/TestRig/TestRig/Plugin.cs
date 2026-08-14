using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Threading;
using Assets.Scripts.Objects;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;
using TestRig.Scenarios;

namespace TestRig
{
    /// <summary>
    ///     TestRig is the in-process half of the Stationeers test rig, and it is ONE plugin that
    ///     loads into both halves: the game client and the headless dedicated server.
    ///
    ///     <para>
    ///     It replaces two: ClientDriver, which stood up the loopback HTTP control plane inside a
    ///     game client, and ScenarioRunner, which ran in-process probes on the dedicated server.
    ///     The split existed because ScenarioRunner predates the control plane, not because a
    ///     headless process cannot host a listener. Nothing in the listener touches Unity, the
    ///     game, or a graphics device: it is System.Net.Sockets and System.Text on the same Mono
    ///     runtime both halves already run.
    ///     </para>
    ///
    ///     <para>
    ///     What genuinely differs between the hosts is the PUMP behind the listener, and the set
    ///     of capabilities a request can rely on. Both are handled explicitly:
    ///     <see cref="HostProfile"/> decides which process this is at load,
    ///     <see cref="MainThreadPump"/> picks a marshal to match, and <see cref="HostGuard"/>
    ///     refuses an endpoint the host cannot serve with the same teaching shape the launcher
    ///     uses. There is no third mechanism and nothing is inferred at the call site.
    ///     </para>
    ///
    ///     <para>
    ///     NOT a release mod. WorkshopHandle is 0 and stays 0. It is a remote control plane for
    ///     the game and must never be published to the Steam Workshop.
    ///     </para>
    /// </summary>
    [BepInPlugin(PluginGuid, PluginName, PluginVersion)]
    // SOFT, not hard, and this is a deliberate deviation from ScenarioRunner.
    //
    // ScenarioRunner took a hard dependency because it is loaded BY StationeersLaunchPad out of
    // data/mods/Local_ScenarioRunner, so StationeersLaunchPad is present by definition there. On
    // the client half this same assembly is loaded by the BepInEx Chainloader out of the
    // instance's own BepInEx/plugins, where a hard dependency that cannot be satisfied means the
    // plugin does not load at all: no listener, no control plane, and a rig that is simply gone
    // with one line in a log nobody reads. Soft gives the load ordering where the Chainloader is
    // in charge and costs nothing where it is not. The one scenario that actually reads
    // LaunchPadBooster (device-port-dump) already degrades on its own.
    [BepInDependency("stationeers.launchpad", BepInDependency.DependencyFlags.SoftDependency)]
    public class Plugin : BaseUnityPlugin
    {
        public const string PluginGuid = "net.sixfive7.testrig";
        public const string PluginName = "TestRig";
        public const string PluginVersion = "0.3.0";

        /// <summary>
        ///     AppDomain slot used to detect a double load. See <see cref="ClaimSingleLoad"/>.
        /// </summary>
        private const string LoadMarkerKey = "net.sixfive7.testrig.loaded";

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

        internal static ConfigEntry<string> BootScenarios;
        internal static ConfigEntry<int> ScenarioDelayTicks;
        internal static ConfigEntry<bool> ScenarioLogInventory;

        /// <summary>
        ///     The port actually bound, after the manifest has had its say. Every response that
        ///     names a port names this one, so a caller never has to work out whether the config
        ///     or the manifest won.
        /// </summary>
        internal static int EffectivePort = ClientDefaultPort;

        /// <summary>
        ///     Default control-plane port on a game client. Clear of Steam (27000-27050), the
        ///     Stationeers client (27015/27016) and the rig's dedicated server (28015/28016).
        ///     Client instances are handed 27700, 27701, ... by their manifests.
        /// </summary>
        internal const int ClientDefaultPort = 27700;

        /// <summary>
        ///     Default control-plane port on the dedicated server, and the reason it is not
        ///     27700: one assembly now ships to both halves and both would otherwise bind the
        ///     same port from the same default. A TCP double-bind fails loudly, so this would be
        ///     visible rather than silent, but the second binder would still be dead. 27750 sits
        ///     above the whole client instance band and below the game's own 28015/28016.
        /// </summary>
        internal const int ServerDefaultPort = 27750;

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

        /// <summary>
        ///     Gate for every Harmony patch that only means something in a rendering game client.
        ///
        ///     A patch whose target exists on both halves but whose effect is client-only
        ///     (the input layer, the cursor gate, the window asserts, the cookie suppressor)
        ///     applies pointlessly on the dedicated server, and every applied patch is one more
        ///     thing that can throw inside PatchAll and take the ones after it down with it.
        /// </summary>
        internal static bool ClientOnlyPatches = true;

        // Static, and deliberately never torn down from OnDestroy.
        //
        // BepInEx hosts plugin components on its manager GameObject, and this game's boot
        // sequence destroys that component almost immediately: measured, 135-219 ms after Awake,
        // at Time.frameCount == 0, before the first scene loads and before Start() is reached,
        // while the process keeps running. That is what silently killed the listener in an early
        // build. Static state is what survives it, so the control plane belongs to the AppDomain
        // rather than to the MonoBehaviour, and only a real application quit stops it. A watchdog
        // thread re-binds if the socket ever does go away.
        //
        // The same fact drives MainThreadPump: everything a plugin creates in Awake dies with the
        // component, which is why the pump host is created from the first sceneLoaded callback.
        internal static HttpServer Server;
        private static Thread _watchdog;
        private static volatile bool _shuttingDown;
        internal static int DestroyCount;

        private void Awake()
        {
            // The duplicate check comes before the statics are claimed, so a second load cannot
            // repoint Log and Instance at itself on its way out.
            if (Log == null) Log = Logger;

            // Before anything else: refuse a second load outright.
            //
            // The same DLL present in both install/BepInEx/plugins/ and data/mods/Local_TestRig/
            // makes the BepInEx Chainloader and StationeersLaunchPad each load it. Awake fires
            // twice, every Harmony patch registers twice, and side-effecting patches double. That
            // trap produced delta=10000 instead of 5000 during a battery-efficiency verification,
            // and a log grep cannot see it: the output looks entirely plausible. ClientDriver had
            // 32 patched methods, so doubling this assembly would be considerably worse than
            // doubling ScenarioRunner's one. A loud refusal is the only detection that works.
            if (!ClaimSingleLoad()) return;

            Instance = this;

            // Which process is this. Everything below branches on the answer, so it is decided
            // once, logged once, and never re-derived at a call site.
            HostProfile.Probe();
            Log.LogInfo(PluginName + " " + PluginVersion + " loading: " + HostProfile.Describe());
            ClientOnlyPatches = !HostProfile.IsDedicatedServer;

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

            VirtualInput.Enabled = AllowInputInjection.Value && !HostProfile.IsDedicatedServer;
            // Input patches are client-only in effect: the methods exist on the dedicated server
            // but nothing polls them, so patching there is pure risk with no upside.
            PatchUnityInputValue = PatchUnityInput.Value && ClientOnlyPatches;
            if (PatchUnityInput.Value && !ClientOnlyPatches)
                Log.LogInfo("Unity input patches skipped: nothing polls UnityEngine.Input headless.");
            else if (!PatchUnityInputValue)
                Log.LogWarning("Patch Unity Input is off; /input/* will do nothing.");

            MainThreadPump.Initialize();
            ConsoleTap.ResetEpoch();
            ConsoleTap.AttachBepInExListener();

            ApplyPatches();

            // Scenario arming has to be decided before the world loads, because roughly seven of
            // the probes are load-ordered and cannot be started by an HTTP call timed against a
            // load. Reading it here, then handing it to the dispatcher at OnPrefabsLoaded, is the
            // same shape ScenarioRunner used; what changed is WHERE the armed set comes from.
            // See ScenarioHost.
            ScenarioHost.Initialize(Log, BootScenarios.Value, ScenarioDelayTicks.Value, ScenarioLogInventory.Value);
            Prefab.OnPrefabsLoaded += OnPrefabsLoaded;

            StartServer();

            // Ask the siblings who they are, once the rig has had a moment to come up. A duplicate
            // ClientId is silent and damaging, so it is worth knowing before a join rather than
            // after a test has produced meaningless results. Meaningless on the dedicated server,
            // which has no cookie and joins nothing.
            if (!HostProfile.IsDedicatedServer && InstanceManifest.PeerPorts.Count > 1) PeerProbe.ScanAsync(0);

            Log.LogInfo(PluginName + " " + PluginVersion + " ready on http://127.0.0.1:" +
                        EffectivePort.ToString(CultureInfo.InvariantCulture) + "/ as instance '" +
                        (string.IsNullOrEmpty(InstanceManifest.Name) ? "(unnamed)" : InstanceManifest.Name) +
                        "' [" + HostProfile.Name + ", pump=" + MainThreadPump.StrategyName + "]");
        }

        /// <summary>
        ///     Records this assembly as loaded in an AppDomain slot and refuses a second Awake.
        ///     Returns false when this is the duplicate.
        /// </summary>
        private static bool ClaimSingleLoad()
        {
            try
            {
                object existing = AppDomain.CurrentDomain.GetData(LoadMarkerKey);
                if (existing != null)
                {
                    Log.LogError(
                        "REFUSING TO LOAD A SECOND TIME. " + PluginName + " is already loaded in this process " +
                        "(first load recorded at " + existing + "). This happens when the same DLL sits in both " +
                        "install/BepInEx/plugins/ and data/mods/Local_" + PluginName + "/: the BepInEx Chainloader " +
                        "and StationeersLaunchPad each load it, every Harmony patch registers twice, and " +
                        "side-effecting patches double their effect while the log still reads as if all is well. " +
                        "Remove one copy. This instance has patched nothing and opened no socket.");
                    return false;
                }

                AppDomain.CurrentDomain.SetData(LoadMarkerKey,
                    DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture));
                return true;
            }
            catch (Exception ex)
            {
                // A failure to read the marker must not stop the plugin from loading; it only
                // means the duplicate check is unavailable this run.
                Log.LogWarning("double-load check unavailable: " + ex.Message);
                return true;
            }
        }

        private void BindConfig()
        {
            // The default DIFFERS BY HOST. One assembly ships to both halves out of one source,
            // and two processes cannot bind one TCP port. The client band starts at 27700 and is
            // handed out per instance by the manifest; the server sits above it.
            int defaultPort = HostProfile.IsDedicatedServer ? ServerDefaultPort : ClientDefaultPort;

            Port = Config.Bind(
                "Client - Control Plane", "Port", defaultPort,
                new ConfigDescription(
                    "(Client-local) TCP port the control plane listens on, bound to 127.0.0.1 only. " +
                    "Defaults to " + ClientDefaultPort + " in a game client and " + ServerDefaultPort +
                    " in the dedicated server, because one assembly loads into both and two processes " +
                    "cannot bind one port. Both are clear of Steam (27000-27050), the Stationeers client " +
                    "(27015/27016) and the rig's dedicated server (28015/28016). An instance manifest " +
                    "overrides this.",
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
                    "through to real hardware, so the driver can never fight the developer's keyboard. " +
                    "Ignored in the dedicated server, which polls no input.",
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
            // 12.75 GB working set with a frozen pump. See ConsoleTap. The bound matters more on
            // the dedicated server, which logs harder than a client does.
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
                    "it during boot. Off by default so a normal client is untouched. Ignored in the " +
                    "dedicated server. An instance manifest overrides this.",
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

            // Scenarios. Server-authoritative because a scenario runs where the simulation runs.
            //
            // This entry is a FALLBACK, not the arming mechanism. ScenarioRunner's Scenario entry
            // was the only way in, and the rig's state reset deliberately blanks it at session
            // boundaries, so a session that armed a probe and then took the lock found it silently
            // disarmed. The armed set now lives in a file outside BepInEx/config that the reset
            // does not touch, and it can be changed live over HTTP without a restart. See
            // ScenarioHost.
            BootScenarios = Config.Bind(
                "Server - Scenarios", "Boot Scenarios", "",
                new ConfigDescription(
                    "(Server-authoritative) Comma or semicolon separated scenario ids to arm at boot, " +
                    "for the load-ordered probes that cannot be started by an HTTP call timed against a " +
                    "world load. FALLBACK ONLY: the armed set is normally the file named by " +
                    "GET /scenarios (armedFile), which the rig's state reset does not blank. Whatever " +
                    "wins is reported by GET /scenarios, so a disarmed probe is a positive answer " +
                    "rather than silence.",
                    null,
                    new KeyValuePair<string, int>("Order", 10)));

            ScenarioDelayTicks = Config.Bind(
                "Server - Scenarios", "Delay Ticks", 5,
                new ConfigDescription(
                    "(Server-authoritative) How many simulation ticks to wait after world load before " +
                    "an armed scenario fires. A handful of ticks lets the simulation settle so initial " +
                    "transients do not pollute the snapshot.",
                    null,
                    new KeyValuePair<string, int>("Order", 20)));

            ScenarioLogInventory = Config.Bind(
                "Server - Scenarios", "Log Inventory On First Tick", true,
                new ConfigDescription(
                    "(Server-authoritative) On the first scenario tick, log a one-line inventory of power " +
                    "entities (counts of Battery / Transformer / AreaPowerControl / CableNetwork / " +
                    "CableFuse). Runs regardless of which scenario is armed and is cheap.",
                    null,
                    new KeyValuePair<string, int>("Order", 30)));
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
            // Client only. WindowMode rewrites Settings.CurrentData and calls Screen.SetResolution,
            // neither of which means anything with no display, and the patches it needs are gated
            // off headless anyway.
            WindowMode.ForceWindowed = ForceWindowed.Value && ClientOnlyPatches;
            if (WindowWidth.Value > 0) WindowMode.Width = WindowWidth.Value;
            if (WindowHeight.Value > 0) WindowMode.Height = WindowHeight.Value;
            InstanceManifest.RecordSource("window", "config");
            if (InstanceManifest.HasWindow && ClientOnlyPatches)
            {
                WindowMode.ForceWindowed = InstanceManifest.ForceWindowed;
                if (InstanceManifest.WindowWidth > 0) WindowMode.Width = InstanceManifest.WindowWidth;
                if (InstanceManifest.WindowHeight > 0) WindowMode.Height = InstanceManifest.WindowHeight;
                InstanceManifest.RecordSource("window", "manifest");
            }
            if (WindowMode.ForceWindowed)
                Log.LogWarning("forcing windowed " + WindowMode.Width + "x" + WindowMode.Height);

            // ---- gameplay input gate ----
            GameplayGate.Force = ForceGameplayInput.Value && ClientOnlyPatches;
            GameplayGate.Everywhere = ForceGameplayInputEverywhere.Value;
            InstanceManifest.RecordSource("gameplayInput", "config");
            if (InstanceManifest.HasGameplayInput && ClientOnlyPatches)
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

        /// <summary>
        ///     Arms the scenario dispatcher once the prefab registry is filled.
        ///
        ///     ScenarioRunner did this from the same event for the same reason: several scenarios
        ///     read <c>Prefab.AllPrefabs</c> on their first tick, and it is empty before this
        ///     fires. Harmony patching stays in <see cref="ApplyPatches"/> rather than moving
        ///     here, because the console tap and the pump have to exist long before prefabs load.
        /// </summary>
        private void OnPrefabsLoaded()
        {
            Prefab.OnPrefabsLoaded -= OnPrefabsLoaded;
            try
            {
                ScenarioHost.Arm();
            }
            catch (Exception ex)
            {
                Log.LogError("scenario arming failed: " + ex);
            }
        }

        private void ApplyPatches()
        {
            try
            {
                var harmony = new Harmony(PluginGuid);

                // Explicit assembly, not the parameterless overload. ScenarioRunner called
                // PatchAll() with no argument, which walks the CALLING assembly and happened to
                // be the same thing; naming it removes the ambiguity now that one assembly
                // carries both plugins' patch classes.
                harmony.PatchAll(typeof(Plugin).Assembly);

                // Enter/exit counters on the per-frame input chain. Patched manually rather than
                // by attribute because one prefix/postfix pair serves every link and tells them
                // apart through __originalMethod. Client only: the counters stay 0 headless.
                if (ClientOnlyPatches) ChainProbe.Install(harmony);

                // Report exactly which of the load-bearing patches took. A silently unpatched
                // console tap would make every later test unverifiable, so it is worth a line in
                // the log either way.
                ConsoleTap.ConsolePatchApplied = IsPatched(harmony, ConsolePrintPatch.TargetMethod());
                Log.LogInfo("console tap patched: " + ConsoleTap.ConsolePatchApplied);

                // Record which pump hooks actually resolved, so an unavailable pump can name the
                // reason instead of leaving a caller to guess. All three are reported on
                // /status.driver.pumpHooks and in every 504 body.
                MainThreadPump.GameManagerUpdateHooked = GameManagerUpdatePatch.TargetMethod() != null;
                MainThreadPump.ImGuiLateUpdateHooked = FramePumpPatch.TargetMethod() != null;
                MainThreadPump.SimTickHooked = SimTickPatch.TargetMethod() != null;

                Log.LogInfo("pump hooks: " + MainThreadPump.HookReport());

                // Losing the steady-state pump is not a degraded mode a caller should discover
                // through a 504 twenty seconds into its first request. Loud, at load.
                if (!MainThreadPump.GameManagerUpdateHooked)
                    Log.LogError("GameManager.Update could not be resolved. That is the steady-state " +
                                 "main-thread pump on BOTH builds. The pump host created at the first " +
                                 "scene load still covers boot, and the game's UnityMainThreadDispatcher " +
                                 "is still a backstop, so this may not be fatal, but check whether the " +
                                 "game update renamed Assets.Scripts.GameManager.Update.");

                if (ClientOnlyPatches)
                {
                    Log.LogInfo("Input.GetKey patched: " + IsPatched(harmony, AccessTools.Method(
                        typeof(Input), nameof(Input.GetKey), new[] { typeof(KeyCode) })));
                    Log.LogInfo("Input.mouseScrollDelta patched: " + IsPatched(harmony, AccessTools.Method(
                        typeof(Input), "get_mouseScrollDelta")));

                    // Installed after PatchAll and separately from it, one hook at a time, so a
                    // bad target in the diagnostic can never take the console tap or the input
                    // chain down with it. An unpatched join trace produces an empty log that reads
                    // exactly like "nothing happened", which is the one answer a diagnostic must
                    // never give by accident, so the outcome is reported either way.
                    JoinTrace.Install(harmony);
                    Log.LogInfo("join trace patched: " + JoinTrace.PatchesApplied +
                                " (" + string.Join("; ", JoinTrace.InstallReport.ToArray()) + ")");
                }
                else
                {
                    Log.LogInfo("client-only patches skipped: input layer, cursor gate, window asserts, " +
                                "cookie suppressor, chain probe, join trace.");
                }
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
            _watchdog = new Thread(WatchdogLoop) { IsBackground = true, Name = "TestRig-Watchdog" };
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
    ///     THE PRIMARY PUMP, on both builds.
    ///
    ///     <para>
    ///     <c>Assets.Scripts.GameManager.Update</c> exists in the client assembly and in the
    ///     dedicated server assembly, runs on thread 1, and ticks at about 24 Hz independent of
    ///     pause state: measured with no client attached and force-unpause off, it kept running
    ///     for 287 s while <c>GameTickCount</c> stayed at 0 for the entire run. It is also the
    ///     sole caller of <c>ManagerBase.ManagerUpdate</c> in the assembly, which is what drains
    ///     the game's own <c>UnityMainThreadDispatcher</c>, so a drain hooked here cannot be later
    ///     than that marshal.
    ///     </para>
    ///
    ///     <para>
    ///     <b>Steady state only.</b> It is unusable during boot: measured at 0.11-0.16 calls per
    ///     second until <c>GameState.Running</c>, while frames were advancing at 25 Hz throughout.
    ///     The 80-90 s boot window is covered instead by the pump host GameObject that
    ///     <c>MainThreadPump</c> creates at the first <c>SceneManager.sceneLoaded</c> callback,
    ///     which is exactly when a caller is polling for readiness. Neither pump alone is enough.
    ///     </para>
    ///
    ///     <para>
    ///     This postfix also serves as a backstop for creating that host, which is safe because a
    ///     Harmony postfix on a game type has none of the plugin component's lifetime: the
    ///     component and everything it created in <c>Awake</c> are destroyed 135-219 ms in, at
    ///     <c>Time.frameCount == 0</c>, before the first scene loads and before <c>Start()</c> is
    ///     ever reached.
    ///     </para>
    /// </summary>
    [HarmonyPatch]
    internal static class GameManagerUpdatePatch
    {
        private static MethodBase Resolve()
        {
            var type = AccessTools.TypeByName("Assets.Scripts.GameManager") ?? AccessTools.TypeByName("GameManager");
            return type == null ? null : AccessTools.Method(type, "Update");
        }

        internal static MethodBase TargetMethod() => Resolve();

        internal static bool Prepare() => Resolve() != null;

        internal static void Postfix()
        {
            try { MainThreadPump.PumpFromGameManagerUpdate(); }
            catch { }
            PerFrameTicks.Run();
        }
    }

    /// <summary>
    ///     Supplementary CLIENT drain, and only that.
    ///
    ///     <para>
    ///     <c>ImGuiManager.LateUpdate</c> draws the in-game console, the loading screen and the
    ///     menu overlays, so on a client it is live from the splash screen onwards, which is
    ///     earlier than GameManager. It stays for that window.
    ///     </para>
    ///
    ///     <para>
    ///     It does NOT exist on the dedicated server, and not in the sense of never being called:
    ///     the class is gutted in that assembly. Mono.Cecil metadata shows 19 methods and 17
    ///     fields on the client build against 1 method (<c>.ctor</c>) and 0 fields on the server
    ///     build; the base chain <c>Singleton&lt;T&gt; -&gt; ManagerBase -&gt; MonoBehaviour</c>
    ///     declares no <c>LateUpdate</c> either; live resolution returned null in all three
    ///     measured runs and <c>FindObjectsOfType</c> found 0 instances at every sample. So
    ///     <see cref="Prepare"/> returns false headless on its own, with no host check needed, and
    ///     nothing that used to ride this hook may be left riding it alone.
    ///     </para>
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
            PerFrameTicks.Run();
        }
    }

    /// <summary>
    ///     The per-frame sampling that used to live inside the ImGui postfix.
    ///
    ///     It moved out because that postfix does not exist on the dedicated server, which meant
    ///     <c>Epoch</c> was never sampled there: <c>epoch.sampledSecondsAgo</c> would have been -1
    ///     and <c>epoch.stale</c> permanently true on every dedicated-server response, and the
    ///     epoch block is the thing that tells a caller whether two readings describe the same
    ///     situation. Driven from both main-thread hooks now, and deduped by
    ///     <c>Time.frameCount</c> so a client running both hooks samples once per frame rather
    ///     than twice.
    /// </summary>
    internal static class PerFrameTicks
    {
        private static int _lastFrame = -1;

        internal static void Run()
        {
            int frame;
            try { frame = Time.frameCount; }
            catch { return; }
            if (frame == _lastFrame) return;
            _lastFrame = frame;

            // Sample the epoch every frame. It has to be here rather than inside the routes that
            // report it, for the same reason JoinTrace does: a transition between two HTTP reads is
            // exactly the thing the session counter exists to catch, and nothing asks in between.
            // Five static reads and five integer compares in the steady state, allocating nothing.
            try { Epoch.Tick(); }
            catch { }
            // Sample the join state while a connect is in flight. It has to run per frame from
            // here rather than from the endpoint's own poll loop, because everything worth seeing
            // (RakNet's connection state, the peer going away) happens and is undone between two
            // polls. Rate-limited and no-op unless /connect armed it.
            try { JoinTrace.Tick(); }
            catch { }
            // Re-assert the window size about once a second. The game applies its own video
            // settings twice during boot and a mod or an options panel can move the window later;
            // a driven instance going fullscreen mid-session over the developer's desktop is
            // exactly what this prevents. No-op unless configured, and configured off headless.
            try { WindowMode.Tick(); }
            catch { }
        }
    }

    /// <summary>
    ///     The simulation-tick hook. One patch, two consumers.
    ///
    ///     ClientDriver and ScenarioRunner each patched
    ///     <c>ElectricityManager.ElectricityTick</c> separately, for the pump and for the scenario
    ///     dispatcher. Merged, that would be two Harmony patches on one method for no reason, so
    ///     the postfix drives both.
    ///
    ///     <para>
    ///     ClientDriver resolved the type as <c>Assets.Scripts.Atmospherics.ElectricityManager</c>
    ///     first. That namespace does not exist; the real type is
    ///     <c>Assets.Scripts.Networks.ElectricityManager</c>, which ScenarioRunner compiled
    ///     against directly. The wrong candidate only ever resolved through the bare-name
    ///     fallback, which would have quietly picked any type called ElectricityManager in any
    ///     loaded assembly. Fixed here: the correct namespace is tried first and the bare name is
    ///     kept as the last resort, so a rename still degrades rather than fails.
    ///     </para>
    ///
    ///     <para>
    ///     <b>This is NOT a pump and must never become one.</b> The postfix runs on a UniTask
    ///     ThreadPool worker: across three measured runs its thread id rotated through 20, 25, 42,
    ///     50, 9, 58, 44, 45 and 57, and was never 1. That is the whole reason
    ///     <c>MainThreadPump.Drain</c> checks the thread before executing anything, and the reason
    ///     every scenario body iterates <c>OcclusionManager.AllThings</c> instead of
    ///     <c>FindObjectsOfType</c>. Its jobs here are to record that the simulation is actually
    ///     ticking and to dispatch scenarios.
    ///     </para>
    ///
    ///     <para>
    ///     Scenario dispatch is deliberately left on this thread rather than marshalled: roughly
    ///     85 scenario bodies were written against the worker-thread contract, and quietly moving
    ///     them to the main thread would change what they measure.
    ///     </para>
    ///
    ///     <para>
    ///     It is also the only honest simulation-liveness signal. Measured on a dedicated server
    ///     started with <c>-new Lunar</c> and force-unpause off, this postfix never fired once in
    ///     287 s: the world ran ZERO ticks, not "a few ticks and then parked".
    ///     </para>
    /// </summary>
    [HarmonyPatch]
    internal static class SimTickPatch
    {
        private static MethodBase Resolve()
        {
            var type = AccessTools.TypeByName("Assets.Scripts.Networks.ElectricityManager")
                       ?? AccessTools.TypeByName("ElectricityManager");
            return type == null ? null : AccessTools.Method(type, "ElectricityTick");
        }

        internal static MethodBase TargetMethod() => Resolve();

        internal static bool Prepare() => Resolve() != null;

        internal static void Postfix()
        {
            // Counters and FrameCount only: the drain itself refuses to run work off the main
            // thread, which is exactly what this thread is.
            try { MainThreadPump.PumpFromFallback(); }
            catch { }
            try { Dispatcher.OnSimTick(); }
            catch { }
        }
    }
}
