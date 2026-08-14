using System;
using UnityEngine;
using GameManager = Assets.Scripts.GameManager;

namespace TestRig
{
    /// <summary>
    ///     Which of the two processes this assembly landed in, and what that process can do.
    ///
    ///     One plugin now loads into a windowed game client and into a headless dedicated
    ///     server. Almost everything is shared, but a handful of capabilities exist in only
    ///     one of them, and the difference is not a matter of degree: <c>Human.LocalHuman</c>
    ///     is null on the dedicated server because that process has no player character at
    ///     all, and no amount of waiting changes it. An endpoint that needs one has to say so
    ///     rather than return a null-reference or an empty object.
    ///
    ///     The discriminator is <c>GameManager.IsBatchMode</c>. It is the same single boolean
    ///     <c>StateReporter.Role()</c> already uses to tell <c>dedicated</c> from
    ///     <c>listenHost</c>, and it has to be, or the two would disagree: both are
    ///     <c>NetworkRole.Server</c>.
    /// </summary>
    internal static class HostProfile
    {
        internal enum HostKind
        {
            /// <summary>Not determined yet. Only visible in the first moments of boot.</summary>
            Unknown = 0,

            /// <summary>A windowed (or at least rendering) game client, including a listen host.</summary>
            GameClient = 1,

            /// <summary>The headless dedicated server, launched with -batchmode.</summary>
            DedicatedServer = 2,
        }

        private static HostKind _kind = HostKind.Unknown;
        private static bool _settled;
        private static string _evidence = "not probed";

        /// <summary>How the answer was reached. Reported on /status and /instance.</summary>
        internal static string Evidence => _evidence;

        /// <summary>
        ///     True once the answer came from the game itself rather than from the command line.
        ///     A provisional answer is still correct in practice, but a caller deciding whether
        ///     to trust a capability report deserves to know which it got.
        /// </summary>
        internal static bool Settled => _settled;

        internal static HostKind Kind
        {
            get
            {
                if (_settled) return _kind;
                return Probe();
            }
        }

        internal static bool IsDedicatedServer => Kind == HostKind.DedicatedServer;
        internal static bool IsGameClient => Kind == HostKind.GameClient;

        /// <summary>The short name used in refusal text and in every response that reports it.</summary>
        internal static string Name
        {
            get
            {
                switch (Kind)
                {
                    case HostKind.DedicatedServer: return "dedicated";
                    case HostKind.GameClient: return "client";
                    default: return "unknown";
                }
            }
        }

        /// <summary>
        ///     Called once from Awake so the log carries the decision before anything acts on it.
        ///
        ///     At Awake the game's own statics may not be populated yet, so the first answer comes
        ///     from the process command line: the dedicated server is launched with -batchmode and
        ///     nothing else in this rig is. That answer is provisional and is replaced by
        ///     <c>GameManager.IsBatchMode</c> the moment that reads true or the game reports
        ///     itself initialised.
        /// </summary>
        internal static HostKind Probe()
        {
            // Preferred source: the game's own answer. Wrapped because reading a static on a type
            // whose class constructor has not run yet throws, and this is called from Awake.
            try
            {
                if (GameManager.IsBatchMode)
                {
                    _kind = HostKind.DedicatedServer;
                    _evidence = "GameManager.IsBatchMode=true";
                    _settled = true;
                    return _kind;
                }

                // IsBatchMode false is only conclusive once the game is up. Before that it can
                // simply mean the static has not been assigned.
                if (GameManager.IsInitialized)
                {
                    _kind = HostKind.GameClient;
                    _evidence = "GameManager.IsBatchMode=false, IsInitialized=true";
                    _settled = true;
                    return _kind;
                }
            }
            catch
            {
                // Fall through to the command line.
            }

            // Unity's own answer is the next best thing and is valid from process start.
            try
            {
                if (Application.isBatchMode)
                {
                    _kind = HostKind.DedicatedServer;
                    _evidence = "Application.isBatchMode=true (provisional)";
                    return _kind;
                }
            }
            catch
            {
            }

            // Last resort, and the one that is definitely readable at Awake.
            try
            {
                foreach (string arg in Environment.GetCommandLineArgs())
                {
                    if (string.Equals(arg, "-batchmode", StringComparison.OrdinalIgnoreCase))
                    {
                        _kind = HostKind.DedicatedServer;
                        _evidence = "command line carries -batchmode (provisional)";
                        return _kind;
                    }
                }
            }
            catch
            {
            }

            _kind = HostKind.GameClient;
            _evidence = "no batch-mode signal (provisional)";
            return _kind;
        }

        // ---- per-call capability probes ------------------------------------------
        //
        // Kind alone is not enough. A game client at the main menu also has no local player,
        // and refusing there with "the dedicated server has no player character" would be a
        // lie. So the guard asks these, and the refusal text differs by host.

        internal static bool HasLocalPlayer()
        {
            try { return Assets.Scripts.Objects.Entities.Human.LocalHuman != null; }
            catch { return false; }
        }

        internal static bool HasInventoryManager()
        {
            try { return Assets.Scripts.Inventory.InventoryManager.Instance != null; }
            catch { return false; }
        }

        // Resolved reflectively rather than by a direct call. CursorManager lives in
        // Assets.Scripts, not Assets.Scripts.Inventory, and this probe is pure reporting: a game
        // update that moves or renames it should degrade to "not reported" rather than break the
        // build of a plugin whose actual cursor code resolves the same type by AccessTools anyway.
        private static System.Reflection.PropertyInfo _cursorInstance;
        private static bool _cursorInstanceResolved;

        internal static bool HasCursorManager()
        {
            try
            {
                if (!_cursorInstanceResolved)
                {
                    _cursorInstanceResolved = true;
                    var type = HarmonyLib.AccessTools.TypeByName("Assets.Scripts.CursorManager")
                               ?? HarmonyLib.AccessTools.TypeByName("CursorManager");
                    if (type != null) _cursorInstance = HarmonyLib.AccessTools.Property(type, "Instance");
                }
                if (_cursorInstance == null) return false;
                var value = _cursorInstance.GetValue(null, null);
                return !ReferenceEquals(value, null) && !((UnityEngine.Object)value == null);
            }
            catch { return false; }
        }

        /// <summary>
        ///     Is there a backbuffer to capture. Screen dimensions are zero under
        ///     -batchmode -nographics, which is what <c>/screenshot</c> needs to know before it
        ///     schedules a coroutine and waits 30 seconds for a texture that cannot arrive.
        /// </summary>
        internal static bool HasBackbuffer()
        {
            if (IsDedicatedServer) return false;
            try { return Screen.width > 0 && Screen.height > 0; }
            catch { return false; }
        }

        /// <summary>
        ///     A one-line summary for the log at load, so the first thing in the file says which
        ///     profile is in force and how it was decided.
        /// </summary>
        internal static string Describe()
        {
            return "host=" + Name + " (" + _evidence + ")";
        }

        /// <summary>
        ///     The block that rides /status, /instance, /help and /ping.
        ///
        ///     The capability probes read Unity statics and <c>Screen</c>, so they are only
        ///     sampled when this is the main thread. <c>/help</c> and <c>/ping</c> deliberately
        ///     never hop, and a probe that throws off-thread would be caught and reported as
        ///     <c>false</c>, which is a lie rather than a gap: a healthy client would claim to
        ///     have no backbuffer. <c>capabilitiesSampled</c> says which you got.
        /// </summary>
        internal static string Json()
        {
            var o = new Json.Obj()
                .Str("kind", Name)
                .Bit("settled", _settled)
                .Str("evidence", _evidence)
                .Str("pumpStrategy", MainThreadPump.StrategyName)
                .Bit("pumpMarshalAvailable", MainThreadPump.MarshalAvailable)
                // Both routes reported separately, because they fail for different reasons and a
                // caller chasing a 504 needs to know which one is dead.
                .Bit("pumpDrainReady", MainThreadPump.DrainReady)
                .Bit("pumpGameMarshalReady", MainThreadPump.GameMarshalReady)
                .Str("pumpHooks", MainThreadPump.HookReport())
                .Str("pumpNote", MainThreadPump.StrategyNote);

            bool sampled = MainThreadPump.OnMainThread;
            o.Bit("capabilitiesSampled", sampled);
            if (sampled)
            {
                o.Bit("hasLocalPlayer", HasLocalPlayer())
                 .Bit("hasInventoryManager", HasInventoryManager())
                 .Bit("hasCursorManager", HasCursorManager())
                 .Bit("hasBackbuffer", HasBackbuffer());
            }
            else
            {
                o.Str("capabilitiesNote",
                    "this response was produced without a main-thread hop, so the per-capability " +
                    "probes were not run rather than being guessed. GET /status or GET /instance " +
                    "carries them.");
            }

            return o.ToString();
        }
    }
}
