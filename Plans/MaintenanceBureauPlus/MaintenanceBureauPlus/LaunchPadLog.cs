using BepInEx.Logging;
using System;
using System.Collections;
using System.Reflection;

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
            EnsureInit();
            if (_logger == null || _logMethod == null) return;
            try
            {
                var msg = e.Data == null ? "" : e.Data.ToString();
                var severity = MapSeverity(e.Level);
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
