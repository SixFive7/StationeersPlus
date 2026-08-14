using System;
using System.Collections.Generic;
using System.IO;
using BepInEx;

namespace TestRig.Scenarios
{
    // Scenario: config-set
    //
    // Writes a live BepInEx ConfigEntry on a RUNNING host, by mod GUID + section + key. The
    // point is the "running" part: BepInEx has no config-reload path a server console can
    // reach, and restarting to change a value ends the session, which destroys any test whose
    // subject IS the session (a mid-session settings change, a join payload that is
    // deliberately not resent).
    //
    // Every other scenario fires once and reports. This one is a passive poller instead: it
    // watches a folder for request files and applies whatever it finds, so it can sit armed
    // for a whole session and do nothing until an agent drops a file in.
    //
    //   <BepInEx root>/scenariorunner/requests/<anything>.txt
    //
    // File format, one request per file, blank lines and '#' comments ignored:
    //
    //   guid=net.spraypaintplus
    //   section=Server - Glow Paint
    //   key=Glow Paint
    //   value=false
    //   mode=set          # optional: set (default) or get
    //   save=true         # optional: persist to the .cfg on disk, default TRUE (see below)
    //
    // The request file is deleted once processed, whether it succeeded or failed, so a
    // malformed request cannot be reapplied every tick for the rest of the session. The
    // outcome is in the log either way.
    //
    // ---- WHAT CHANGED IN THE MERGE ----
    //
    // This file used to carry its own implementation: its own GUID-to-ConfigFile resolution
    // (Chainloader first, then an assembly scan for a [BepInPlugin] reaching a ConfigFile
    // through a static ConfigEntry), its own TomlTypeConverter call, its own read-back check.
    // ConfigAccess in the client half implemented exactly the same thing, for exactly the same
    // stated reason, and the two had drifted: THIS one defaulted save=false and the client's
    // /config/set defaulted save=true.
    //
    // There is now one implementation, ConfigAccess, reached through /config/set, and this
    // poller is a front door onto it. The surviving default is save=TRUE, on both. A write
    // that is not persisted disappears on the next reload, which produces a test that passed
    // once and cannot be reproduced, and the failure is silent because the in-memory value was
    // correct for the whole run. The old argument for false was that persisting leaks test
    // state into the next start, and that is real, but it is already handled: both config trees
    // are tier-3 rig state and the session reset restores them at the boundary. Pass save=false
    // explicitly for the in-memory-only behaviour.
    //
    // Managed state only (file I/O plus ConfigEntry writes), so it is safe on the UniTask
    // worker the sim-tick pump uses. The route it now calls hops to the main thread itself.

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
                                  "(optional mode=get, save=false) to write a live ConfigEntry with no restart. " +
                                  "POST /config/set does the same thing without a file and is the preferred " +
                                  "route now that the control plane runs on both halves.");
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

            string guid, section, key, value, mode, saveRaw;
            kv.TryGetValue("guid", out guid);
            kv.TryGetValue("section", out section);
            kv.TryGetValue("key", out key);
            kv.TryGetValue("value", out value);
            kv.TryGetValue("mode", out mode);
            kv.TryGetValue("save", out saveRaw);

            bool get = string.Equals(mode, "get", StringComparison.OrdinalIgnoreCase);
            // Default TRUE, matching /config/set. See the header for why the two defaults were
            // reconciled this way rather than the other.
            bool save = saveRaw == null || string.Equals(saveRaw, "true", StringComparison.OrdinalIgnoreCase);

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

            var parameters = new Dictionary<string, string>(StringComparer.Ordinal) { { "guid", guid } };

            if (get)
            {
                // /config reports every entry whose "<Section> / <Key>" contains the filter, which
                // for a fully qualified pair is exactly the one entry.
                parameters["filter"] = section + " / " + key;
                Router.InvokeAsync(Contracts.Endpoints.Config, parameters, (status, bodyText) =>
                    _log?.LogInfo($"[ScenarioRunner] {CONFIG_SET_TAG} | '{name}' GET | " +
                                  $"{guid} [{section}] '{key}' http={status} {bodyText}"));
                return true;
            }

            parameters["section"] = section;
            parameters["key"] = key;
            parameters["value"] = value;
            parameters["save"] = save ? "true" : "false";

            Router.InvokeAsync(Contracts.Endpoints.ConfigSet, parameters, (status, bodyText) =>
            {
                // The route reports its own failures in band at HTTP 200 (the ConfigAccess shape),
                // so the log line keys off the body, not the status. That distinction is exactly
                // what the shared contracts assembly documents on RigOutcome.InBandFailure.
                bool ok = status == Contracts.RigStatus.Ok &&
                          bodyText != null &&
                          bodyText.IndexOf("\"ok\":true", StringComparison.Ordinal) >= 0;
                if (ok)
                    _log?.LogInfo($"[ScenarioRunner] {CONFIG_SET_TAG} | '{name}' SET OK | " +
                                  $"{guid} [{section}] '{key}' persisted={save} {bodyText}");
                else
                    _log?.LogError($"[ScenarioRunner] {CONFIG_SET_TAG} | '{name}' FAIL | " +
                                   $"{guid} [{section}] '{key}' http={status} {bodyText}");
            });

            return true;
        }
    }
}
