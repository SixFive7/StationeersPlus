using System;
using System.Text;
using Assets.Scripts;
using Assets.Scripts.Networking;
using UnityEngine;
using GameManager = Assets.Scripts.GameManager;

namespace TestRig
{
    /// <summary>
    ///     The stamp that says WHICH instance a state reading came from and WHEN it was valid.
    ///     Every route that reports state carries it.
    ///
    ///     It exists because of one specific wrong answer. A live run reported "the joiner has no
    ///     UDP socket at all" as decisive; the reading had been taken after <c>/connect</c>'s own
    ///     <c>Cancel()</c> had already torn the socket down, and nothing in the response said when
    ///     it was valid. Two readings that straddle a world entry, a role change or a host teardown
    ///     are not comparable, and until now nothing in a response let a caller notice that.
    ///
    ///     <see cref="Session"/> is the detector: a monotonic counter that increments whenever this
    ///     process changes world or network state. Two readings carrying the same session number
    ///     describe one continuous stretch of this process's life; two readings whose numbers differ
    ///     do not, whatever else they say. It never decreases and never resets, so a caller can
    ///     compare it across a request that failed or timed out.
    ///
    ///     Five things move it, and nothing else does: <c>GameManager.GameState</c>,
    ///     <c>NetworkManager.NetworkRole</c>, <c>NetworkManager.NetworkState</c>,
    ///     <c>NetworkServer.IsHosting</c>, and the world id. The connected-client roster size is
    ///     reported beside the counter and deliberately does NOT move it: a joiner arriving is not a
    ///     change of THIS process's world or network state, and counting it would make almost every
    ///     host-side paired assertion straddle a boundary during a normal join. A caller that cares
    ///     reads <c>clients</c> and decides for itself.
    ///
    ///     Everything is sampled once per frame from the frame pump and cached, so rendering the
    ///     block reads plain statics and is safe from any thread. That matters for <c>/ping</c>,
    ///     which never touches the main thread precisely so it can answer while the game is wedged:
    ///     <c>sampledSecondsAgo</c> is wall clock, so a frozen main thread shows up as a stamp that
    ///     is minutes old rather than as a fresh-looking lie.
    /// </summary>
    internal static class Epoch
    {
        /// <summary>
        ///     Monotonic. Starts at 1 and increments on every observed world or network transition.
        /// </summary>
        internal static long Session { get; private set; } = 1;

        // Numeric copies of the watched enums. Stored as int and compared as int so the steady
        // state costs five integer compares per frame and allocates nothing: ToString() on an enum
        // allocates, so the text forms below are rebuilt only when something actually moved.
        private static int _gameState = int.MinValue;
        private static int _networkRole = int.MinValue;
        private static int _networkState = int.MinValue;
        private static int _hosting = -1;
        private static string _worldId;

        private static string _gameStateText = "unknown";
        private static string _networkRoleText = "unknown";
        private static string _networkStateText = "unknown";
        private static string _roleText = "unknown";
        private static string _lastChange = "(nothing has moved since this process started)";

        private static int _clients;
        private static int _hostPort;
        private static int _frame;
        private static bool _authoritative;
        private static bool _primed;

        private static DateTime _sampledUtc = DateTime.MinValue;
        private static DateTime _changedUtc = DateTime.UtcNow;
        private static int _changedAtFrame;

        /// <summary>
        ///     Samples the watched state. Called once per frame from the frame pump, beside
        ///     <c>JoinTrace.Tick</c>, so a transition between two HTTP reads is caught even when
        ///     nothing asks. Every read is guarded: at the main menu and during boot most of these
        ///     singletons are null, and the one stamp every response carries must never throw.
        /// </summary>
        internal static void Tick()
        {
            int gameState = _gameState, role = _networkRole, netState = _networkState, hosting = _hosting;
            string worldId = _worldId;

            try { gameState = (int)GameManager.GameState; } catch { }
            try { role = (int)NetworkManager.NetworkRole; } catch { }
            try { netState = (int)NetworkManager.NetworkState; } catch { }
            try { hosting = NetworkServer.IsHosting ? 1 : 0; } catch { }
            // CurrentWorldId, not CurrentWorldName: the id is a plain string off WorldSetting while
            // the name goes through a LocalizedStringReference conversion, and this runs every frame.
            try { worldId = WorldManager.CurrentWorldId ?? ""; } catch { }

            bool moved = gameState != _gameState || role != _networkRole || netState != _networkState ||
                         hosting != _hosting || !string.Equals(worldId, _worldId, StringComparison.Ordinal);

            // Cheap and always changing, so these refresh every frame regardless.
            try { _frame = Time.frameCount; } catch { }
            try { _authoritative = GameManager.RunSimulation; } catch { _authoritative = false; }
            try { _hostPort = hosting == 1 ? NetworkServer.HostPort : 0; } catch { _hostPort = 0; }
            try
            {
                var clients = NetworkManager.IsServer ? NetworkBase.Clients : null;
                _clients = clients == null ? 0 : clients.Count;
            }
            catch { _clients = 0; }
            _sampledUtc = DateTime.UtcNow;

            if (!moved) return;

            // Everything below here allocates, and only runs on a real transition.
            string wasGameState = _gameStateText, wasRole = _networkRoleText;
            string wasNetState = _networkStateText, wasWorld = _worldId;
            bool wasHosting = _hosting == 1;
            bool first = !_primed;

            _gameState = gameState;
            _networkRole = role;
            _networkState = netState;
            _hosting = hosting;
            _worldId = worldId;

            try { _gameStateText = GameManager.GameState.ToString(); } catch { _gameStateText = "unknown"; }
            try { _networkRoleText = NetworkManager.NetworkRole.ToString(); } catch { _networkRoleText = "unknown"; }
            try { _networkStateText = NetworkManager.NetworkState.ToString(); } catch { _networkStateText = "unknown"; }
            try { _roleText = StateReporter.Role(); } catch { _roleText = "unknown"; }

            // The first sample is the process being observed for the first time, not a transition.
            // Counting it would make every instance start at session 2 for no reason.
            if (first)
            {
                _primed = true;
                _changedAtFrame = _frame;
                _changedUtc = DateTime.UtcNow;
                _lastChange = "first sample: gameState=" + _gameStateText + " networkRole=" + _networkRoleText;
                return;
            }

            var sb = new StringBuilder();
            if (!string.Equals(wasGameState, _gameStateText, StringComparison.Ordinal))
                Append(sb, "gameState " + wasGameState + " -> " + _gameStateText);
            if (!string.Equals(wasRole, _networkRoleText, StringComparison.Ordinal))
                Append(sb, "networkRole " + wasRole + " -> " + _networkRoleText);
            if (!string.Equals(wasNetState, _networkStateText, StringComparison.Ordinal))
                Append(sb, "networkState " + wasNetState + " -> " + _networkStateText);
            if (wasHosting != (hosting == 1))
                Append(sb, "hosting " + (wasHosting ? "true" : "false") + " -> " + (hosting == 1 ? "true" : "false"));
            if (!string.Equals(wasWorld, worldId, StringComparison.Ordinal))
                Append(sb, "world '" + (wasWorld ?? "") + "' -> '" + (worldId ?? "") + "'");

            _lastChange = sb.Length == 0 ? "(unnamed transition)" : sb.ToString();
            _changedAtFrame = _frame;
            _changedUtc = DateTime.UtcNow;
            Session++;
        }

        private static void Append(StringBuilder sb, string part)
        {
            if (sb.Length > 0) sb.Append("; ");
            sb.Append(part);
        }

        /// <summary>
        ///     The block every state-reporting response carries. Pure cache reads, so it costs
        ///     nothing and is callable from the HTTP accept thread.
        /// </summary>
        internal static string Json()
        {
            double sampledAgo = _sampledUtc == DateTime.MinValue
                ? -1
                : Math.Round((DateTime.UtcNow - _sampledUtc).TotalSeconds, 2);
            double changedAgo = Math.Round((DateTime.UtcNow - _changedUtc).TotalSeconds, 2);

            var o = new Json.Obj()
                .Str("instance", string.IsNullOrEmpty(InstanceManifest.Name) ? "(unnamed)" : InstanceManifest.Name)
                .Int("port", Plugin.EffectivePort)
                .Int("session", Session)
                .Str("phase", Router.PhaseOf(_gameStateText))
                .Str("gameState", _gameStateText)
                .Str("role", _roleText)
                .Str("networkRole", _networkRoleText)
                .Str("networkState", _networkStateText)
                .Bit("hosting", _hosting == 1)
                .Int("hostPort", _hostPort)
                .Bit("authoritative", _authoritative)
                .Str("worldId", string.IsNullOrEmpty(_worldId) ? null : _worldId)
                .Int("clients", _clients)
                .Int("frame", _frame)
                .Dbl("sampledSecondsAgo", sampledAgo)
                .Bit("stale", sampledAgo < 0 || sampledAgo > 5)
                .Int("sessionChangedAtFrame", _changedAtFrame)
                .Dbl("sessionChangedSecondsAgo", changedAgo)
                .Str("lastChange", _lastChange);
            if (sampledAgo < 0)
                o.Str("warning", "the frame pump has never sampled, so this stamp describes nothing. " +
                                 "The plugin loaded but the game has not reached a frame yet.");
            else if (sampledAgo > 5)
                o.Str("warning", "this stamp is " + sampledAgo.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) +
                                 " s old, so the Unity main thread is not running frames. Every field " +
                                 "beside it describes the past, not now.");
            return o.ToString();
        }

        /// <summary>
        ///     True while the two readings can be compared: same instance, same session counter.
        ///     Not used inside the plugin, and here because the rule belongs next to the counter
        ///     rather than in a caller's head: a harness that reads this block twice compares
        ///     <c>instance</c> and <c>session</c> before it compares anything else.
        /// </summary>
        internal static string ComparabilityRule =>
            "two readings are comparable only when their epoch.instance and epoch.session match. " +
            "A different session number means this process changed world or network state between " +
            "them, so a before/after diff across the pair is measuring two different situations.";
    }
}
