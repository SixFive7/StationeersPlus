using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using BepInEx;
using BepInEx.Logging;

namespace TestRig.Scenarios
{
    /// <summary>
    ///     Owns what is armed, where that decision came from, and the catalogue that makes a
    ///     disarmed probe a positive answer instead of silence.
    ///
    ///     <para><b>Why this type exists.</b> Arming a scenario by setting a config value and
    ///     then grepping a log failed four times for four unrelated reasons. Three of them are
    ///     addressed here and the fourth is addressed by the HTTP routes:</para>
    ///     <list type="number">
    ///     <item><description>
    ///         <b>The rig's state reset deliberately blanks the config value at session
    ///         boundaries</b>, with the stated reason that a scenario left armed injects itself
    ///         into an unrelated test's log. That reasoning is correct and the reset should keep
    ///         doing it. So the armed set no longer lives in <c>BepInEx/config</c>: it lives in
    ///         <see cref="ArmedFile"/>, under <c>BepInEx/testrig/</c>, which neither the config
    ///         blanking nor the per-instance config re-copy touches. The config entry survives as
    ///         a fallback and its value is reported alongside the file's, so if the two ever
    ///         disagree the conflict is visible rather than silently resolved.
    ///     </description></item>
    ///     <item><description>
    ///         <b>Arming required a restart, and a restart ends the session under test.</b>
    ///         <c>Dispatcher.SetArmed</c> takes effect on the next simulation tick, and
    ///         <c>POST /scenario/arm</c> reaches it live.
    ///     </description></item>
    ///     <item><description>
    ///         <b>A typo, a missing mod assembly, and an unreached settle counter all produced
    ///         exactly one log line and then silence.</b> <see cref="Json"/> reports the armed
    ///         set, what has been dispatched, what the switch did not recognise, and what was
    ///         refused for a missing assembly, so all three are answerable without reading a log
    ///         at all.
    ///     </description></item>
    ///     </list>
    ///     <para>The fourth reason (the simulation not ticking with no client connected, so no
    ///     pump fires) cannot be fixed here. It is reported instead:
    ///     <c>ticksSeen</c> not advancing is the signal, and it rides every
    ///     <c>GET /scenarios</c> answer.</para>
    /// </summary>
    internal static class ScenarioHost
    {
        private static ManualLogSource _log;
        private static string _armedFile;
        private static string _configValue = "";
        private static string _fileValue = "";
        private static string _effective = "";
        private static string _source = "none";
        private static string _conflict;
        private static string _fileError;
        private static int _delayTicks = 5;
        private static bool _logInventory = true;
        private static bool _armed;

        /// <summary>
        ///     Where the armed set is persisted. Deliberately NOT under <c>BepInEx/config</c>.
        ///
        ///     On the dedicated server this resolves to
        ///     <c>install/BepInEx/testrig/scenarios.armed</c>; on a client instance to that
        ///     instance's own <c>BepInEx/testrig/scenarios.armed</c>. The rig's reset re-copies
        ///     <c>BepInEx/config</c> and blanks the scenario entry inside it; it does not touch
        ///     this folder. <c>update-mods</c> wipes <c>data/mods/</c>, which is also not this
        ///     folder.
        /// </summary>
        internal static string ArmedFile => _armedFile;

        internal static string Effective => _effective;
        internal static string Source => _source;
        internal static bool IsArmedAtBoot => _armed;

        internal static void Initialize(ManualLogSource log, string configValue, int delayTicks, bool logInventory)
        {
            _log = log;
            _configValue = (configValue ?? "").Trim();
            _delayTicks = Math.Max(0, delayTicks);
            _logInventory = logInventory;

            try
            {
                string dir = Path.Combine(Paths.BepInExRootPath, "testrig");
                Directory.CreateDirectory(dir);
                _armedFile = Path.Combine(dir, "scenarios.armed");
                if (File.Exists(_armedFile)) _fileValue = ReadArmedFile(_armedFile);
            }
            catch (Exception ex)
            {
                _fileError = ex.Message;
            }

            if (!string.IsNullOrEmpty(_fileValue))
            {
                _effective = _fileValue;
                _source = "file";
                if (!string.IsNullOrEmpty(_configValue) &&
                    !string.Equals(_configValue, _fileValue, StringComparison.Ordinal))
                {
                    _conflict = "the config entry says '" + _configValue + "' and " + _armedFile +
                                " says '" + _fileValue + "'. The file wins because it is what the last " +
                                "POST /scenario/arm wrote and the rig's state reset does not blank it.";
                    _log?.LogWarning("[ScenarioRunner] armed-set conflict: " + _conflict);
                }
            }
            else if (!string.IsNullOrEmpty(_configValue))
            {
                _effective = _configValue;
                _source = "config";
            }
            else
            {
                _effective = "";
                _source = "none";
            }

            // One line, always, naming the decision and the file. The old plugin logged the
            // config value only, so a session that had been silently disarmed by the reset saw a
            // line reading scenario='' and had nothing to compare it against.
            _log?.LogInfo("[ScenarioRunner] armed='" + _effective + "' source=" + _source +
                          " file=" + (_armedFile ?? "(unavailable)") +
                          (_fileError == null ? "" : " fileError=" + _fileError) +
                          " delayTicks=" + _delayTicks);
        }

        /// <summary>
        ///     Hands the decision to the dispatcher. Called at <c>Prefab.OnPrefabsLoaded</c>,
        ///     because several scenarios read <c>Prefab.AllPrefabs</c> on their first tick and it
        ///     is empty before that.
        /// </summary>
        internal static void Arm()
        {
            Dispatcher.Initialize(_log, _effective, _delayTicks, _logInventory);
            _armed = true;
            _log?.LogInfo("[ScenarioRunner] dispatcher armed for '" + _effective + "'");
        }

        /// <summary>
        ///     Re-arms live and, unless <paramref name="persist"/> is false, writes the file so
        ///     the choice survives the next boot. Returns the new effective string.
        /// </summary>
        internal static string SetArmed(string scenarios, bool persist)
        {
            _effective = Normalize(scenarios);
            _source = persist ? "http (persisted)" : "http (this run only)";
            Dispatcher.SetArmed(_effective);

            if (persist && _armedFile != null)
            {
                try
                {
                    var sb = new StringBuilder();
                    sb.AppendLine("# TestRig armed scenarios. One id per line, or comma separated.");
                    sb.AppendLine("# Written by POST /scenario/arm. Read at plugin load.");
                    sb.AppendLine("# Deliberately outside BepInEx/config, which the rig's state reset blanks.");
                    sb.AppendLine(_effective);
                    File.WriteAllText(_armedFile, sb.ToString());
                    _fileValue = _effective;
                    _fileError = null;
                }
                catch (Exception ex)
                {
                    _fileError = ex.Message;
                    _log?.LogError("[ScenarioRunner] could not persist the armed set to " + _armedFile + ": " + ex.Message);
                }
            }

            _log?.LogInfo("[ScenarioRunner] re-armed to '" + _effective + "' (" + _source + ")");
            return _effective;
        }

        private static string ReadArmedFile(string path)
        {
            var ids = new List<string>();
            foreach (string raw in File.ReadAllLines(path))
            {
                string line = raw.Trim();
                if (line.Length == 0 || line.StartsWith("#", StringComparison.Ordinal)) continue;
                foreach (string part in line.Split(',', ';'))
                {
                    string id = part.Trim();
                    if (id.Length > 0) ids.Add(id);
                }
            }
            return string.Join(",", ids.ToArray());
        }

        private static string Normalize(string scenarios)
        {
            if (string.IsNullOrEmpty(scenarios)) return "";
            var ids = new List<string>();
            foreach (string part in scenarios.Split(',', ';'))
            {
                string id = part.Trim();
                if (id.Length > 0) ids.Add(id);
            }
            return string.Join(",", ids.ToArray());
        }

        // ---- the catalogue -------------------------------------------------------

        internal sealed class Entry
        {
            internal string Id;
            /// <summary>Must be armed before the world loads. An HTTP call cannot be timed against a load.</summary>
            internal bool BootOrdered;
            /// <summary>Sits armed for a whole session doing nothing until a request file arrives.</summary>
            internal bool Poller;
            /// <summary>Emits on a cadence forever rather than firing once.</summary>
            internal bool Continuous;
            /// <summary>Simulation ticks POST /scenario/run gives it when the caller names none.</summary>
            internal int SuggestedTicks;
            /// <summary>Mod assembly the scenario refuses without, or null.</summary>
            internal string RequiresAssembly;
        }

        private const string PGP = "PowerGridPlus";
        private const string PTP = "PowerTransmitterPlus";
        private const string SPP = "SprayPaintPlus";

        private static Entry E(string id, int ticks = 12, string asm = null,
                               bool boot = false, bool poller = false, bool continuous = false)
        {
            return new Entry
            {
                Id = id,
                SuggestedTicks = ticks,
                RequiresAssembly = asm,
                BootOrdered = boot,
                Poller = poller,
                Continuous = continuous,
            };
        }

        /// <summary>
        ///     Every id <c>Dispatcher.TickOne</c> switches on, with the metadata a caller needs
        ///     to invoke it correctly. Hand-maintained, exactly like the endpoint catalogue in
        ///     <c>Help.cs</c>, and cross-checked at runtime: an id that reaches the switch and is
        ///     not recognised is recorded and reported by <see cref="Json"/>, so a catalogue that
        ///     drifts from the switch shows up as an unknown rather than as a wrong answer.
        ///
        ///     The BootOrdered flags come from the set that genuinely cannot be started by an
        ///     HTTP call: a light freeze that must be in force before anything measures light,
        ///     the construction-event traces, the write halves of two-phase save/load pairs, and
        ///     the multi-tick state machines that need an uninterrupted tick stream from a known
        ///     start. Everything else is a one-shot probe over settled state.
        /// </summary>
        internal static readonly Entry[] Catalogue =
        {
            E("inventory"),
            E("sun-noon", 6, boot: true, continuous: true),
            E("battery-charge-snapshot", 20, continuous: true),
            E("power-prefab-dump"),
            E("connector-dump"),
            E("paintable-prefab-dump"),
            E("device-port-dump"),
            E("config-set", 1, poller: true),
            E("give-item", 1, poller: true),

            E("spp-color-swatch-probe"),
            E("spp-dlc-gate-verify", 12, SPP),
            E("spp-settings-merge-verify", 12, SPP),

            E("merge-long-variant-num4"),
            E("clamp-merge-quantity"),

            E("pgp-mixedwire-fixture", 300, PGP, boot: true),
            E("pgp-rocket-battery-probe", 30, PGP),
            E("pgp-fresh-device-trace", 300, PGP, boot: true),
            E("pgp-mixedwire-survey", 30, PGP, continuous: true),
            E("pgp-passthrough-port-probe", 12, PGP),
            E("pgp-apc-bridge-probe", 25, PGP),
            E("pgp-dataport-tier-diag", 12, PGP),
            E("pgp-dataport-cursor-proxy", 12, PGP),
            E("pgp-rocket-parity-probe", 12, PGP),
            E("pgp-umbilical-passthrough-probe", 12, PGP),
            E("pgp-umbilical-saveload-set", 12, PGP, boot: true),
            E("pgp-umbilical-saveload-verify", 12, PGP),
            E("pgp-transformer-conservation", 20, PGP, continuous: true),
            E("pgp-battery-efficiency-probe", 12, PGP),
            E("pgp-apc-idle-probe", 20, PGP, continuous: true),
            E("pgp-cable-burn-probe", 40, PGP, continuous: true),
            E("pgp-cable-burn-window-probe", 12, PGP),
            E("pgp-tooltip-filter-probe", 12, PGP),
            E("pgp-rate-cap-probe", 12, PGP),
            E("pgp-stationpedia-page-probe", 12, PGP),
            E("pgp-priority-deprioritization-probe", 12, PGP, boot: true),
            E("pgp-priority-deprioritization-persist-probe", 12, PGP, boot: true),
            E("pgp-priority-deprioritization-network-breakdown", 12, PGP),
            E("pgp-priority-deprioritization-knob-probe", 12, PGP),
            E("pgp-priority-deprioritization-flash-probe", 12, PGP),
            E("pgp-priority-deprioritization-hover-probe", 12, PGP),
            E("pgp-priority-deprioritization-labeller-probe", 12, PGP),
            E("pgp-priority-deprioritization-mp-probe", 12, PGP),
            E("pgp-priority-deprioritization-saveload-probe", 12, PGP),
            E("pgp-priority-deprioritization-topology-probe", 15, PGP),
            E("pgp-priority-deprioritization-all", 20, PGP),
            E("pgp-r1-prepare", 12, PGP),
            E("pgp-power-flow-diagnose", 12, PGP),
            E("pgp-net-consumer-dump", 25, PGP),
            E("pgp-deprioritization-trace", 40, PGP),
            E("pgp-atomic-probe", 12, PGP),
            E("pgp-overload-probe", 12, PGP),
            E("pgp-fault-state-probe", 25, PGP, continuous: true),
            E("pgp-shortfall-net-probe", 25, PGP),
            E("pgp-reversed-transformer-probe", 30),
            E("pgp-deprioritization-multilevel", 60, PGP, boot: true),
            E("pgp-2cycle-freeze", 60, PGP, boot: true),
            E("pgp-deprioritization-victim-fixture", 12, PGP),
            E("pgp-chain-fixture", 40, PGP, boot: true),
            E("pgp-overload-split-fixture", 12, PGP),
            E("pgp-desire-split-fixture", 12, PGP),
            E("pgp-fault-hover-fixture", 12, PGP),
            E("pgp-rearch-suite", 150, PGP, boot: true),
            E("pgp-atomic-all", 40, PGP),
            E("pgp-pt-hover-all", 12, PGP),
            E("pgp-pt-flash-all", 12, PGP),
            E("pgp-pt-logic-all", 12, PGP),
            E("pgp-pt-onoff-table", 12, PGP),
            E("pgp-pt-synthetic-all", 12, PGP),
            E("pgp-pt-topology-all", 12, PGP),
            E("pgp-pt-extra-all", 12, PGP),
            E("pgp-pt-crossmod-all", 12, PGP),
            E("pgp-pt-burnreason", 12, PGP),
            E("pgp-pt-fixverify", 12, PGP),

            E("ptp-autoaim-cache-probe", 12, PTP),
            E("ptp-long-distance-link-probe", 12, PTP),
            E("ptp-beam-predicate-probe", 12, PTP),
            E("ptp-standalone-suite", 330, PTP, boot: true),
            E("ptp-all", 15, PTP),
        };

        internal static Entry Find(string id)
        {
            if (string.IsNullOrEmpty(id)) return null;
            foreach (var e in Catalogue)
                if (string.Equals(e.Id, id, StringComparison.Ordinal)) return e;
            return null;
        }

        /// <summary>Catalogue ids sharing a prefix with an unknown one, for the 400 body.</summary>
        internal static List<string> Suggest(string id)
        {
            var hits = new List<string>();
            if (string.IsNullOrEmpty(id)) return hits;
            string head = id.Length < 4 ? id : id.Substring(0, 4);
            foreach (var e in Catalogue)
                if (e.Id.StartsWith(head, StringComparison.OrdinalIgnoreCase)) hits.Add(e.Id);
            return hits;
        }

        /// <summary>The GET /scenarios body.</summary>
        internal static string Json()
        {
            var armedSet = new HashSet<string>(StringComparer.Ordinal);
            foreach (string part in (_effective ?? "").Split(',', ';'))
            {
                string id = part.Trim();
                if (id.Length > 0) armedSet.Add(id);
            }

            var dispatched = new HashSet<string>(Dispatcher.DispatchedIds(), StringComparer.Ordinal);
            var blocked = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var kv in Dispatcher.BlockedByAssembly()) blocked[kv.Key] = kv.Value;

            var rows = new List<string>();
            foreach (var e in Catalogue)
            {
                var row = new Json.Obj()
                    .Str("id", e.Id)
                    .Bit("armed", armedSet.Contains(e.Id))
                    .Bit("dispatched", dispatched.Contains(e.Id))
                    .Bit("bootOrdered", e.BootOrdered)
                    .Bit("poller", e.Poller)
                    .Bit("continuous", e.Continuous)
                    .Int("suggestedTicks", e.SuggestedTicks);
                if (e.RequiresAssembly != null) row.Str("requiresAssembly", e.RequiresAssembly);
                string why;
                if (blocked.TryGetValue(e.Id, out why))
                    row.Str("blocked", "the '" + why + "' assembly is not loaded, so this scenario returns " +
                                       "without emitting anything");
                rows.Add(row.ToString());
            }

            var o = new Json.Obj()
                .Bit("ok", true)
                .Str("host", HostProfile.Name)
                .Bit("dispatcherArmed", Dispatcher.Armed)
                .Str("armed", _effective ?? "")
                .Str("armedSource", _source)
                .Str("armedFile", _armedFile)
                .Str("configValue", _configValue)
                .Str("fileValue", _fileValue)
                .Int("delayTicks", Dispatcher.Armed ? Dispatcher.DelayTicks : _delayTicks)
                .Int("ticksSeen", Dispatcher.TicksSeen)
                .Int("count", Catalogue.Length)
                .StrArray("unknownArmed", Dispatcher.UnknownIds())
                .RawArray("scenarios", rows);

            if (_conflict != null) o.Str("conflict", _conflict);
            if (_fileError != null) o.Str("fileError", _fileError);

            if (Dispatcher.Armed && Dispatcher.TicksSeen == 0)
                o.Str("warning",
                    "ticksSeen is 0: the simulation has not ticked once, so no armed scenario can have " +
                    "fired and none ever will until it does. On a dedicated server with no client " +
                    "attached this is the NORMAL state and it is total, not partial: measured on a " +
                    "server started with -new Lunar and Force Unpause Without Client off, the tick " +
                    "count stayed at 0 for the whole 287-second run and SetGamePause fired twice, both " +
                    "before any tick. Do not wait for 'a few ticks then a pause'; there are none. " +
                    "Connect a client, or set Force Unpause Without Client. Note this is a SIMULATION " +
                    "signal only: the Unity main thread keeps running at about 24 Hz throughout, so " +
                    "every HTTP endpoint still answers.");

            o.Str("note",
                "armedSource names where the armed set came from. It is deliberately NOT the BepInEx " +
                "config entry by default: the rig's state reset blanks that entry at session boundaries, " +
                "which silently disarmed probes four times. armedFile is outside BepInEx/config and the " +
                "reset does not touch it. dispatched means the id reached the switch, NOT that it emitted " +
                "anything: a one-shot whose guard already tripped, a settle-gated probe short of its " +
                "settle tick, and a probe blocked on a missing assembly are all dispatched and all silent.");

            return o.ToString();
        }
    }
}
