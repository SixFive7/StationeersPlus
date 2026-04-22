using BepInEx.Logging;
using System;
using System.Collections;
using System.Reflection;

// =============================================================================
// PLAYTEST-ONLY BRIDGE — REMOVE BEFORE v1.0.0
// =============================================================================
// This file forwards every BepInEx log entry to:
//   1. StationeersLaunchPad.Logger (visible via `slp logs` console command)
//   2. Stationeers ConsoleWindow.Print (visible directly when F3 opens)
//
// Both bridges exist solely to give us fast triage during playtest. Before
// v1.0.0 release, this entire file should be deleted (or the ConsoleWindow
// branch removed and the LaunchPad branch kept on Warning+ only). Watchdog
// and per-turn DIAG lines flood the F3 console at a rate that makes typing
// other commands miserable for end users. See plan.md "Pre-release
// checklist" / TODO.md "Playtest gate" for the full diagnostic-removal pass.
// =============================================================================

namespace MaintenanceBureauPlus
{
    // Bridge: forwards every BepInEx log entry from our ManualLogSource into
    // StationeersLaunchPad's in-game F3 log viewer.
    //
    // F3's viewer (StationeersLaunchPad.UI.LogPanel) reads from
    // StationeersLaunchPad.Logger buffers, NOT from BepInEx's logging
    // pipeline. Calling `Log.LogInfo(...)` (BepInEx ManualLogSource) writes
    // to BepInEx/LogOutput.log but never lands in F3. Each LaunchPad-loaded
    // mod gets its own Logger via `LoadedMod.Logger = Logger.Global.CreateChild(info.Name)`,
    // and child Loggers automatically forward into Global, which is what
    // F3's "All" filter shows.
    //
    // Strategy: subscribe to our ManualLogSource's LogEvent. On every log
    // entry, push the same text into LaunchPad's Logger via reflection, so
    // we don't take a hard compile-time dependency on
    // StationeersLaunchPad.dll. Falls back to Logger.Global until the
    // mod-specific Logger is resolved (post-load).
    //
    // All resolution is lazy and best-effort. If LaunchPad's API changes,
    // F3 forwarding silently no-ops; LogOutput.log keeps working unchanged.
    internal static class LaunchPadLog
    {
        private static object _logger;          // StationeersLaunchPad.Logger instance
        private static MethodInfo _logMethod;   // Logger.Log(string, LogSeverity, bool, string)
        private static Type _severityType;
        private static bool _initAttempted;
        private static bool _modLoggerResolved; // upgraded from Global to per-mod Logger
        private static Type _loggerType;
        private static Type _modLoaderType;

        // Hook our BepInEx ManualLogSource so every LogInfo/LogWarning/etc.
        // entry is mirrored into LaunchPad's Logger. Idempotent.
        public static void Hook(ManualLogSource source)
        {
            if (source == null) return;
            source.LogEvent -= OnLogEvent;
            source.LogEvent += OnLogEvent;
        }

        // Called from Plugin.OnAllModsLoaded. By then our LoadedMod entry
        // should be in ModLoader.LoadedMods. Replaces the Global logger
        // reference with our mod's child Logger so F3's per-mod filter
        // dropdown lists us.
        public static void TryUpgradeToModLogger()
        {
            if (_modLoggerResolved) return;
            EnsureInit();
            if (_loggerType == null || _modLoaderType == null) return;

            try
            {
                var loadedModsField = _modLoaderType.GetField(
                    "LoadedMods", BindingFlags.Public | BindingFlags.Static);
                var list = loadedModsField?.GetValue(null) as IEnumerable;
                if (list == null) return;

                foreach (var lm in list)
                {
                    if (lm == null) continue;
                    var lmType = lm.GetType();
                    object info = lmType.GetField("Info")?.GetValue(lm)
                               ?? lmType.GetProperty("Info")?.GetValue(lm);
                    var name = info?.GetType().GetField("Name")?.GetValue(info)?.ToString()
                            ?? info?.GetType().GetProperty("Name")?.GetValue(info)?.ToString();
                    if (string.Equals(name, MaintenanceBureauPlusPlugin.PluginName, StringComparison.Ordinal))
                    {
                        var modLogger = lmType.GetField("Logger")?.GetValue(lm)
                                     ?? lmType.GetProperty("Logger")?.GetValue(lm);
                        if (modLogger != null)
                        {
                            _logger = modLogger;
                            _modLoggerResolved = true;
                            return;
                        }
                    }
                }
            }
            catch { /* best-effort */ }
        }

        private static void OnLogEvent(object sender, LogEventArgs e)
        {
            var msg = e.Data == null ? "" : e.Data.ToString();
            ForwardToLaunchPad(msg, e.Level);
            ForwardToStationeersConsole(msg, e.Level);
        }

        private static void ForwardToLaunchPad(string msg, LogLevel level)
        {
            EnsureInit();
            if (_logger == null || _logMethod == null) return;
            try
            {
                var severity = MapSeverity(level);
                _logMethod.Invoke(_logger, new object[]
                {
                    msg,
                    severity,
                    /*unity:*/ false,
                    /*name:*/ MaintenanceBureauPlusPlugin.PluginName,
                });
            }
            catch { /* never throw out of a log handler */ }
        }

        // Mirror to Stationeers' built-in F3 console (ConsoleWindow.Print).
        // PLAYTEST ONLY — see file header. Watchdog + per-turn DIAG lines
        // flood the F3 console; remove this branch before v1.0.0.
        private static MethodInfo _consolePrintMethod;
        private static object _consoleColorWhite;
        private static object _consoleColorYellow;
        private static object _consoleColorRed;
        private static bool _consoleResolveAttempted;

        private static void ForwardToStationeersConsole(string msg, LogLevel level)
        {
            EnsureConsoleInit();
            if (_consolePrintMethod == null) return;
            try
            {
                object color = _consoleColorWhite;
                if ((level & (LogLevel.Error | LogLevel.Fatal)) != 0) color = _consoleColorRed ?? _consoleColorWhite;
                else if ((level & LogLevel.Warning) != 0) color = _consoleColorYellow ?? _consoleColorWhite;
                // Print(string, ConsoleColor, clearLine=false, aged=true, unformatted=false)
                _consolePrintMethod.Invoke(null, new object[]
                {
                    "[MBP] " + msg,
                    color,
                    /*clearLine:*/ false,
                    /*aged:*/ true,
                    /*unformatted:*/ false,
                });
            }
            catch { /* console may not be ready yet, or signature drifted; silently skip */ }
        }

        private static void EnsureConsoleInit()
        {
            if (_consoleResolveAttempted) return;
            _consoleResolveAttempted = true;

            Type consoleType = null;
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                try { consoleType = asm.GetType("Assets.Scripts.ConsoleWindow"); }
                catch { }
                if (consoleType != null) break;
                try
                {
                    foreach (var t in asm.GetTypes())
                    {
                        if (t.Name == "ConsoleWindow") { consoleType = t; break; }
                    }
                }
                catch { }
                if (consoleType != null) break;
            }
            if (consoleType == null) return;

            // Pick the 5-arg overload Print(string, ConsoleColor, bool, bool, bool).
            foreach (var m in consoleType.GetMethods(BindingFlags.Public | BindingFlags.Static))
            {
                if (m.Name != "Print") continue;
                var ps = m.GetParameters();
                if (ps.Length != 5) continue;
                if (ps[0].ParameterType != typeof(string)) continue;
                if (!ps[1].ParameterType.IsEnum) continue;
                _consolePrintMethod = m;
                var colorType = ps[1].ParameterType;
                try { _consoleColorWhite = Enum.Parse(colorType, "White"); } catch { }
                try { _consoleColorYellow = Enum.Parse(colorType, "Yellow"); } catch { }
                try { _consoleColorRed = Enum.Parse(colorType, "Red"); } catch { }
                break;
            }
        }

        private static void EnsureInit()
        {
            if (_initAttempted) return;
            _initAttempted = true;

            try
            {
                foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                {
                    try
                    {
                        if (_loggerType == null) _loggerType = asm.GetType("StationeersLaunchPad.Logger");
                        if (_modLoaderType == null) _modLoaderType = asm.GetType("StationeersLaunchPad.Loading.ModLoader");
                        if (_severityType == null) _severityType = asm.GetType("StationeersLaunchPad.LogSeverity");
                    }
                    catch { }
                    if (_loggerType != null && _modLoaderType != null && _severityType != null) break;
                }
                if (_loggerType == null || _severityType == null) return;

                _logMethod = _loggerType.GetMethod(
                    "Log",
                    BindingFlags.Public | BindingFlags.Instance,
                    null,
                    new[] { typeof(string), _severityType, typeof(bool), typeof(string) },
                    null);

                var globalField = _loggerType.GetField(
                    "Global", BindingFlags.Public | BindingFlags.Static);
                _logger = globalField?.GetValue(null);
            }
            catch { /* swallow */ }
        }

        // Map BepInEx LogLevel flags to StationeersLaunchPad.LogSeverity by
        // case-insensitive name (Debug, Information, Warning, Error, Fatal).
        // Several BepInEx levels share names with SLP severities; for the
        // ones that don't (None) we default to Information.
        private static object MapSeverity(LogLevel level)
        {
            string name;
            if ((level & LogLevel.Fatal) != 0) name = "Fatal";
            else if ((level & LogLevel.Error) != 0) name = "Error";
            else if ((level & LogLevel.Warning) != 0) name = "Warning";
            else if ((level & LogLevel.Debug) != 0) name = "Debug";
            else name = "Information";

            try { return Enum.Parse(_severityType, name, ignoreCase: true); }
            catch { return Activator.CreateInstance(_severityType); }
        }
    }
}
