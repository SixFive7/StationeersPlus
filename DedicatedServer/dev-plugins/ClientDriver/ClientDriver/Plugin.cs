using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;
using ConsoleWindow = Assets.Scripts.ConsoleWindow;

namespace ClientDriver
{
    /// <summary>
    ///     ClientDriver is a developer plugin that turns the Stationeers GAME CLIENT
    ///     into something an agent can drive without touching the keyboard. It stands
    ///     up a loopback HTTP control plane and exposes: the in-game console as a
    ///     readable stream, Direct Connect and disconnect, a full state report,
    ///     synthetic keyboard / mouse / wheel input applied inside the engine,
    ///     spawning, screenshots, and live BepInEx config reads and writes.
    ///
    ///     Everything is applied by calling the game's own methods or by patching the
    ///     Unity input layer, never by faking OS input. That is deterministic and it
    ///     works while the window is unfocused, which is the whole point: a driven
    ///     client sits in the background while the agent works.
    ///
    ///     The plugin is NOT a release mod. It lives under
    ///     <c>DedicatedServer/dev-plugins/</c> alongside ScenarioRunner, ships as a
    ///     single DLL, and never gets a Workshop handle. Unlike ScenarioRunner it
    ///     deploys to the CLIENT install, not the dedicated server, so the launcher's
    ///     <c>-DeployMods</c> path does not apply; see <c>README.md</c> next to this
    ///     file for the install steps and the endpoint catalogue.
    /// </summary>
    [BepInPlugin(PluginGuid, PluginName, PluginVersion)]
    public class Plugin : BaseUnityPlugin
    {
        public const string PluginGuid = "net.clientdriver";
        public const string PluginName = "ClientDriver";
        public const string PluginVersion = "0.1.0";

        internal static ManualLogSource Log;
        internal static Plugin Instance;

        internal static ConfigEntry<int> Port;
        internal static ConfigEntry<bool> Enabled;
        internal static ConfigEntry<bool> AllowInputInjection;
        internal static ConfigEntry<bool> PatchUnityInput;
        internal static bool PatchUnityInputValue = true;

        // Static, and deliberately never torn down from OnDestroy.
        //
        // BepInEx hosts plugin components on its manager GameObject. Something in
        // this game's boot sequence destroys that component after the mod load
        // finishes (observed 2026-07-27 on 0.2.6403.27689: OnDestroy fired while the
        // process kept running, which silently killed the listener a minute after
        // startup). The control plane must outlive that, so it belongs to the
        // AppDomain, not to the MonoBehaviour, and only a real application quit
        // stops it. A watchdog thread re-binds if the socket ever does go away.
        internal static HttpServer Server;
        private static Thread _watchdog;
        private static volatile bool _shuttingDown;
        internal static int DestroyCount;

        private void Awake()
        {
            Log = Logger;
            Instance = this;

            Port = Config.Bind(
                "Client - Control Plane", "Port", 27700,
                new ConfigDescription(
                    "(Client-local) TCP port the control plane listens on, bound to 127.0.0.1 only. " +
                    "27700 is clear of Steam (27000-27050), the Stationeers client (27015/27016) and " +
                    "this repo's dedicated server (28015/28016).",
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

            Log.LogInfo(PluginName + " " + PluginVersion + " ready on http://127.0.0.1:" + Port.Value + "/");
        }

        private void ApplyPatches()
        {
            try
            {
                var harmony = new Harmony(PluginGuid);
                harmony.PatchAll(typeof(Plugin).Assembly);

                // Report exactly which of the load-bearing patches took. A silently
                // unpatched console tap would make every later test unverifiable, so
                // it is worth a line in the log either way.
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
                    Port.Value,
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

            // Application.quitting is the only teardown signal that actually means
            // "the process is going away". OnDestroy does not.
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
                        Port.Value, Router.Handle, m => Log.LogInfo(m), m => Log.LogError(m));
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
    /// Primary drain for the main-thread queue.
    ///
    /// <c>ImGuiManager.LateUpdate</c> runs every frame from the splash screen
    /// onwards: it is what draws the in-game console, the loading screen and the
    /// menu overlays, so it is live at every phase of the client's life and does not
    /// depend on any object this plugin owns. That last part matters, because the
    /// BepInEx plugin component itself gets destroyed partway through this game's
    /// boot (see the note on <c>Plugin.Server</c>), which would otherwise take the
    /// pump down with it.
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
        }
    }

    /// <summary>
    /// Backup drain for the main-thread queue.
    ///
    /// A windowed client ticks MonoBehaviour.Update every frame, so the primary pump
    /// is enough. The headless dedicated server does not, which is why InspectorPlus
    /// and ScenarioRunner both drive their work from an ElectricityTick postfix. The
    /// same postfix is wired here so a client that somehow stops ticking Update (a
    /// minimised window with rendering suspended is the realistic case) still drains
    /// queued work. Patch target is resolved reflectively and the class skips itself
    /// if the method is absent, so a game update that renames it degrades to
    /// Update-only rather than failing to load.
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
