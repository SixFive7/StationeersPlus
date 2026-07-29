using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using BepInEx;
using BepInEx.Configuration;

namespace ScenarioRunner
{
    // Scenario: config-set
    //
    // Writes a live BepInEx ConfigEntry on a RUNNING server, by mod GUID + section + key.
    // The point is the "running" part: BepInEx has no config-reload path a server console
    // can reach, and restarting the dedi to change a value ends the session, which destroys
    // any test whose subject IS the session (a mid-session settings change, a join payload
    // that is deliberately not resent).
    //
    // Every other scenario fires once and reports. This one is a passive poller instead: it
    // watches a folder for request files and applies whatever it finds, so it can sit armed
    // for a whole session and do nothing until an agent drops a file in. Same shape as the
    // InspectorPlus request pump, and for the same reason.
    //
    //   DedicatedServer/install/BepInEx/scenariorunner/requests/<anything>.txt
    //
    // File format, one request per file, blank lines and '#' comments ignored:
    //
    //   guid=net.spraypaintplus
    //   section=Server - Glow Paint
    //   key=Glow Paint
    //   value=false
    //   mode=set          # optional: set (default) or get
    //   save=false        # optional: also persist to the .cfg on disk, default false
    //
    // The request file is deleted once processed, whether it succeeded or failed, so a
    // malformed request cannot be reapplied every tick for the rest of the session. The
    // outcome is in the log either way.
    //
    // Values are parsed with TomlTypeConverter against the entry's own SettingType, so an
    // enum, an int and a bool all go through the same path and the caller only ever writes
    // strings. Nothing here is mod-specific: it is reflection over BepInEx, so it reaches
    // any loaded plugin's config, not just SprayPaintPlus.
    //
    // Deliberately not persisted by default. A test that flips a server-authoritative
    // setting mid-session wants the value live for this run only; writing it to the .cfg
    // would leak the test state into the next -Start and quietly poison a later run.
    //
    // Managed state only (file I/O plus ConfigEntry writes), so it is safe on the UniTask
    // worker the sim-tick pump uses.

    internal static partial class Dispatcher
    {
        private const string CONFIG_SET_TAG = "config-set";

        private static string _cfgSetDir;
        private static bool _cfgSetAnnounced;

        private static void Scenario_ConfigSet()
        {
            try
            {
                if (_cfgSetDir == null)
                {
                    _cfgSetDir = Path.Combine(Paths.BepInExRootPath, "scenariorunner", "requests");
                    Directory.CreateDirectory(_cfgSetDir);
                }

                if (!_cfgSetAnnounced)
                {
                    _cfgSetAnnounced = true;
                    _log?.LogInfo($"[ScenarioRunner] {CONFIG_SET_TAG} ARMED | polling '{_cfgSetDir}' every " +
                                  "simulation tick. Drop a file with guid= / section= / key= / value= lines " +
                                  "(optional mode=get, save=true) to write a live ConfigEntry with no restart.");
                }

                string[] files;
                try { files = Directory.GetFiles(_cfgSetDir, "*.*"); }
                catch (Exception e)
                {
                    _log?.LogError($"[ScenarioRunner] {CONFIG_SET_TAG} | cannot list '{_cfgSetDir}': {e.Message}");
                    return;
                }

                if (files.Length == 0) return;
                Array.Sort(files, StringComparer.Ordinal);

                foreach (var file in files)
                {
                    string name = Path.GetFileName(file);
                    bool consume;
                    try
                    {
                        consume = ConfigSet_ProcessRequest(file, name);
                    }
                    catch (Exception e)
                    {
                        _log?.LogError($"[ScenarioRunner] {CONFIG_SET_TAG} | request '{name}' threw: {e}");
                        consume = true;
                    }

                    // Consume on any outcome the caller has already been told about, so a
                    // malformed request cannot be reapplied every tick for the rest of the
                    // session. The one exception is a file the writer still has open: that
                    // is left alone and retried on the next tick.
                    if (!consume) continue;

                    try { File.Delete(file); }
                    catch (Exception e)
                    {
                        _log?.LogError($"[ScenarioRunner] {CONFIG_SET_TAG} | could not delete '{name}': " +
                                       $"{e.Message} (it WILL be reprocessed next tick)");
                    }
                }
            }
            catch (Exception e)
            {
                _log?.LogError($"[ScenarioRunner] {CONFIG_SET_TAG} threw: {e}");
            }
        }

        /// <summary>
        /// Returns true when the request file should be deleted, false to retry next tick.
        /// </summary>
        private static bool ConfigSet_ProcessRequest(string path, string name)
        {
            string[] lines;
            try { lines = File.ReadAllLines(path); }
            catch (IOException)
            {
                // Half-written file: the writer still has it open. Leave it on disk and
                // pick it up on the next tick rather than acting on a truncated request.
                _log?.LogInfo($"[ScenarioRunner] {CONFIG_SET_TAG} | '{name}' not readable yet, retrying next tick");
                return false;
            }

            var kv = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var raw in lines)
            {
                string line = raw.Trim();
                if (line.Length == 0 || line[0] == '#') continue;
                int eq = line.IndexOf('=');
                if (eq <= 0) continue;
                // Only the FIRST '=' splits: a section name or a value may contain one.
                kv[line.Substring(0, eq).Trim()] = line.Substring(eq + 1).Trim();
            }

            kv.TryGetValue("guid", out string guid);
            kv.TryGetValue("section", out string section);
            kv.TryGetValue("key", out string key);
            kv.TryGetValue("value", out string value);
            kv.TryGetValue("mode", out string mode);
            kv.TryGetValue("save", out string saveRaw);

            bool get = string.Equals(mode, "get", StringComparison.OrdinalIgnoreCase);
            bool save = string.Equals(saveRaw, "true", StringComparison.OrdinalIgnoreCase);

            if (string.IsNullOrEmpty(guid) || string.IsNullOrEmpty(section) || string.IsNullOrEmpty(key))
            {
                _log?.LogError($"[ScenarioRunner] {CONFIG_SET_TAG} | '{name}' FAIL | " +
                               $"need guid, section and key (guid='{guid}' section='{section}' key='{key}')");
                return true;
            }
            if (!get && value == null)
            {
                _log?.LogError($"[ScenarioRunner] {CONFIG_SET_TAG} | '{name}' FAIL | " +
                               "mode=set needs a value= line");
                return true;
            }

            var config = ConfigSet_FindConfigFile(guid);
            if (config == null)
            {
                _log?.LogError($"[ScenarioRunner] {CONFIG_SET_TAG} | '{name}' FAIL | " +
                               $"no loaded plugin with guid '{guid}', or its ConfigFile is unreachable. " +
                               $"Known guids: {string.Join(", ", ConfigSet_KnownGuids())}");
                return true;
            }

            // ConfigFile implements IDictionary explicitly, so the indexer and TryGetValue
            // are only reachable through the interface.
            var map = (IDictionary<ConfigDefinition, ConfigEntryBase>)config;
            var def = new ConfigDefinition(section, key);
            if (!map.TryGetValue(def, out ConfigEntryBase entry) || entry == null)
            {
                var sameSection = map.Keys
                    .Where(d => string.Equals(d.Section, section, StringComparison.Ordinal))
                    .Select(d => d.Key)
                    .OrderBy(k => k, StringComparer.Ordinal)
                    .ToArray();
                _log?.LogError($"[ScenarioRunner] {CONFIG_SET_TAG} | '{name}' FAIL | " +
                               $"'{guid}' has no entry [{section}] / '{key}'. " +
                               (sameSection.Length > 0
                                   ? $"Keys in that section: {string.Join(" | ", sameSection)}"
                                   : $"No such section. Sections: {string.Join(" | ", map.Keys.Select(d => d.Section).Distinct().OrderBy(s => s, StringComparer.Ordinal).ToArray())}"));
                return true;
            }

            object before = entry.BoxedValue;

            if (get)
            {
                _log?.LogInfo($"[ScenarioRunner] {CONFIG_SET_TAG} | '{name}' GET | " +
                              $"{guid} [{section}] '{key}' = {before} (type={entry.SettingType?.Name})");
                return true;
            }

            object parsed;
            try
            {
                parsed = TomlTypeConverter.ConvertToValue(value, entry.SettingType);
            }
            catch (Exception e)
            {
                string allowed = entry.SettingType != null && entry.SettingType.IsEnum
                    ? " Allowed: " + string.Join(" | ", Enum.GetNames(entry.SettingType))
                    : "";
                _log?.LogError($"[ScenarioRunner] {CONFIG_SET_TAG} | '{name}' FAIL | " +
                               $"cannot parse '{value}' as {entry.SettingType?.Name}: {e.Message}.{allowed}");
                return true;
            }

            try
            {
                entry.BoxedValue = parsed;
            }
            catch (Exception e)
            {
                _log?.LogError($"[ScenarioRunner] {CONFIG_SET_TAG} | '{name}' FAIL | " +
                               $"write threw: {e.Message}");
                return true;
            }

            object after = entry.BoxedValue;

            if (save)
            {
                try { config.Save(); }
                catch (Exception e)
                {
                    _log?.LogError($"[ScenarioRunner] {CONFIG_SET_TAG} | '{name}' | value written but " +
                                   $"ConfigFile.Save() threw: {e.Message}");
                }
            }

            bool ok = Equals(after, parsed);
            if (ok)
                _log?.LogInfo($"[ScenarioRunner] {CONFIG_SET_TAG} | '{name}' SET OK | " +
                              $"{guid} [{section}] '{key}' {before} -> {after} " +
                              $"(type={entry.SettingType?.Name} persisted={save})");
            else
                _log?.LogError($"[ScenarioRunner] {CONFIG_SET_TAG} | '{name}' SET MISMATCH | " +
                               $"{guid} [{section}] '{key}' wrote {parsed}, reads back {after} (was {before})");

            return true;
        }

        /// <summary>
        /// Resolves a plugin GUID to its live ConfigFile.
        ///
        /// Two routes, because neither is reliable alone. Chainloader.PluginInfos only lists
        /// what BepInEx itself loaded out of BepInEx/plugins/, so every StationeersLaunchPad
        /// mod is invisible to it; and the plugin component behind PluginInfos[guid].Instance
        /// is destroyed partway through boot, after which Instance is null for everything.
        /// The assembly scan has neither problem: a ConfigEntry holds a reference to its
        /// owning ConfigFile, and both outlive the MonoBehaviour.
        /// </summary>
        private static ConfigFile ConfigSet_FindConfigFile(string guid)
        {
            const BindingFlags F = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;

            try
            {
                if (BepInEx.Bootstrap.Chainloader.PluginInfos.TryGetValue(guid, out var info)
                    && info?.Instance != null)
                {
                    // BaseUnityPlugin.Config is protected.
                    var cfgProp = typeof(BaseUnityPlugin).GetProperty("Config",
                        BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                    if (cfgProp?.GetValue(info.Instance) is ConfigFile cfg) return cfg;
                }
            }
            catch { /* fall through to the scan */ }

            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type[] types;
                try { types = asm.GetTypes(); }
                catch (ReflectionTypeLoadException e) { types = e.Types.Where(t => t != null).ToArray(); }
                catch { continue; }

                foreach (var t in types)
                {
                    BepInPlugin attr;
                    try { attr = t.GetCustomAttribute<BepInPlugin>(); }
                    catch { continue; }
                    if (attr == null || !string.Equals(attr.GUID, guid, StringComparison.Ordinal)) continue;

                    // Two copies of the same mod assembly can both carry the attribute and
                    // only the one whose Awake ran has populated statics, so try every
                    // match rather than the first.
                    foreach (var f in t.GetFields(F))
                    {
                        if (!typeof(ConfigEntryBase).IsAssignableFrom(f.FieldType)) continue;
                        ConfigEntryBase e;
                        try { e = f.GetValue(null) as ConfigEntryBase; }
                        catch { continue; }
                        if (e?.ConfigFile != null) return e.ConfigFile;
                    }
                }
            }

            return null;
        }

        private static string[] ConfigSet_KnownGuids()
        {
            var found = new List<string>();
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type[] types;
                try { types = asm.GetTypes(); }
                catch (ReflectionTypeLoadException e) { types = e.Types.Where(t => t != null).ToArray(); }
                catch { continue; }

                foreach (var t in types)
                {
                    BepInPlugin attr;
                    try { attr = t.GetCustomAttribute<BepInPlugin>(); }
                    catch { continue; }
                    if (attr != null && !found.Contains(attr.GUID)) found.Add(attr.GUID);
                }
            }
            found.Sort(StringComparer.Ordinal);
            return found.ToArray();
        }
    }
}
