using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using BepInEx.Logging;
using ChatMessage = Assets.Scripts.Networking.ChatMessage;
using ConsoleWindow = Assets.Scripts.ConsoleWindow;
using GameManager = Assets.Scripts.GameManager;
using GameState = Assets.Scripts.GridSystem.GameState;
using NetworkChannel = Assets.Scripts.Networking.NetworkChannel;
using NetworkManager = Assets.Scripts.Networking.NetworkManager;
using NetworkServer = Assets.Scripts.NetworkServer;

namespace StationeersPlus.Shared
{
    /// <summary>
    /// How often a given message key is allowed through. Every call site must choose one; there is
    /// no default and no un-throttled overload, because the in-game console has no rate limiting of
    /// its own and every print costs a full 1024-entry array shift. Picking the policy is the
    /// caller's job, and so is picking the key: only the caller knows whether "the same message"
    /// means the same text, the same device, or the same logical operation.
    /// </summary>
    internal readonly struct Throttle
    {
        internal enum Kind { Never, Cooldown, Once, MaxTimes, CapWithSummary }

        internal readonly Kind Policy;
        internal readonly int Milliseconds;
        internal readonly int Limit;

        private Throttle(Kind policy, int milliseconds, int limit)
        {
            Policy = policy;
            Milliseconds = milliseconds;
            Limit = limit;
        }

        /// <summary>
        /// Prints every single time. Not a default and not a bypass: it is a policy you have to type,
        /// which means you decided this line is genuinely once-per-event and self-limiting. Correct
        /// for a message that answers one deliberate player action, and for anything a test asserts
        /// exactly-once (the enforcement broadcasts ScenarioRunner checks are the in-repo example).
        /// Wrong for anything on a per-tick, per-frame, or per-input-notch path.
        /// </summary>
        internal static readonly Throttle Never = new Throttle(Kind.Never, 0, 0);

        /// <summary>
        /// First occurrence of this key only, for the rest of the session (or until ResetSession).
        /// Use when the key identifies a specific subject that stays broken, so the player is told
        /// about each subject exactly once. Key on the subject, not the text: keying on a device
        /// reference id gives one line per broken device, keying on the message gives one line total.
        /// </summary>
        internal static readonly Throttle Once = new Throttle(Kind.Once, 0, 1);

        /// <summary>
        /// Minimum real-time gap between repeats of this key. Use on input paths, where one physical
        /// gesture produces many events: a single mouse-wheel flick is 10-20 notches and every notch
        /// can reach the same branch.
        /// </summary>
        internal static Throttle Cooldown(float seconds)
        {
            return new Throttle(Kind.Cooldown, (int)(seconds * 1000f), 0);
        }

        /// <summary>
        /// At most <paramref name="times"/> lines for this key, then silence. Use for a blocked action
        /// the player keeps attempting: say it enough to be understood, then stop nagging.
        /// </summary>
        internal static Throttle MaxTimes(int times)
        {
            return new Throttle(Kind.MaxTimes, 0, times);
        }

        /// <summary>
        /// At most <paramref name="times"/> lines for this key, then one summary line naming how many
        /// more were suppressed. Use for a loop over world content, where the count is the part the
        /// player can act on. The summary is emitted by <see cref="PlayerMessage.FlushSummary"/>, which
        /// the caller should invoke when the sweep ends; <see cref="PlayerMessage.ResetSession"/> also
        /// flushes any still-pending summaries so a forgotten call delays the line to the next world
        /// boundary rather than losing it.
        /// </summary>
        internal static Throttle CapWithSummary(int times)
        {
            return new Throttle(Kind.CapWithSummary, 0, times);
        }
    }

    /// <summary>
    /// The single entry point for everything a mod says to a player or writes to a log.
    ///
    /// WHY THIS EXISTS. The rules below are counter-intuitive, and the repo has shipped the same
    /// defects more than once because each mod re-derived them by hand. They are encoded here so a
    /// call site cannot get them wrong. See Research/Patterns/InGameConsoleOutput.md and
    /// Research/Patterns/GameLoggingSinks.md.
    ///
    ///   - `aged` is INVERTED from its name. aged: true sets activeTime 0, so the line is NOT drawn on
    ///     the closed-console overlay and appears only once F3 is opened. Plain Print defaults to
    ///     aged: true; anything meant to be seen without opening the console needs aged: false.
    ///   - There is no PrintWarning. Yellow is PrintAction. Info and warning are therefore visually
    ///     identical in the console; the severity survives only in the log files.
    ///   - PrintError dumps a full Environment.StackTrace as a second line unless it is passed
    ///     suppressStacktrace: true. On an ordinary "you cannot do that" message that reads as a crash.
    ///   - NEVER call UnityEngine.Debug.LogError or LogException. ConsoleWindow subscribes to
    ///     Application.logMessageReceivedThreaded and re-prints Error and Exception itself, lowercased,
    ///     with an unsuppressible stack trace. Pairing it with a console call shows the player the same
    ///     thing two or three times. This file calls no Debug.* method at all, which is what makes that
    ///     entire class of bug unreachable.
    ///   - The BepInEx log is the useful sink, not Debug. One ManualLogSource call reaches BOTH
    ///     BepInEx\LogOutput.log (via DiskLogListener) AND Player.log (via BepInEx's UnityLogListener,
    ///     which writes through Unity's native log writer and so never re-enters the console bridge).
    ///     Debug.Log* reaches only Player.log, because [Logging.Disk] WriteUnityLog defaults to false.
    ///   - Plain text only. The console renders through ImGui TextUnformatted, so TextMeshPro tags show
    ///     as literal characters, and a dedicated server launched without -logFile silently drops any
    ///     line containing "&lt;color=".
    ///   - Main thread only. Print shifts an unlocked 1024-entry static array while the draw loop reads
    ///     it. Calls from a worker are dropped from the console leg here rather than racing the
    ///     renderer; the log legs still run, so nothing is lost from the files.
    ///
    /// PER-MOD COPY. This file is linked into each mod (&lt;Compile Include&gt;), not shared at runtime, so
    /// every consuming assembly gets its own statics. Throttle state is therefore per-mod: it bounds
    /// what one mod contributes to the console, never the aggregate across mods. Init must be called
    /// once per mod.
    /// </summary>
    internal static class PlayerMessage
    {
        private enum Level { Info, Warning, Error }

        private sealed class KeyState
        {
            internal int Count;
            internal int LastShownTicks;
            internal bool HasShown;
            internal int Suppressed;
            internal Level PendingLevel;
            internal string PendingSubject;
        }

        private static readonly object Gate = new object();
        private static readonly Dictionary<string, KeyState> Keys = new Dictionary<string, KeyState>();

        private static string _prefix = "[Mod] ";
        private static ManualLogSource _log;
        private static bool _initialised;

        /// <summary>
        /// Wire the helper up once, from the plugin's Awake.
        /// </summary>
        /// <param name="displayName">
        /// The mod's DISPLAY name, with spaces, as a player should read it ("Power Grid Plus", not
        /// "PowerGridPlus"). It becomes the bracketed prefix on every player-facing line. The code name
        /// is for machine-facing identifiers; this string is neither.
        /// </param>
        /// <param name="log">The plugin's BepInEx Logger. Reaches both log files; see the class remarks.</param>
        internal static void Init(string displayName, ManualLogSource log)
        {
            _prefix = string.IsNullOrEmpty(displayName) ? "[Mod] " : "[" + displayName + "] ";
            _log = log;
            _initialised = true;
        }

        /// <summary>Informational. Yellow in the console, visible without opening it, no stack trace.</summary>
        internal static void Info(string key, Throttle throttle, string message)
        {
            Emit(Level.Info, key, throttle, message);
        }

        /// <summary>
        /// A warning. Yellow, same as Info, because the game has no PrintWarning. The distinction is
        /// real in the log files only; if a player needs to see the difference, say so in the text.
        /// </summary>
        internal static void Warn(string key, Throttle throttle, string message)
        {
            Emit(Level.Warning, key, throttle, message);
        }

        /// <summary>A genuine error. Red, visible without opening the console, stack trace suppressed.</summary>
        internal static void Error(string key, Throttle throttle, string message)
        {
            Emit(Level.Error, key, throttle, message);
        }

        /// <summary>
        /// An error carrying an exception. The full exception goes to the log files; the player's
        /// console gets only the type and message. Interpolating a bare exception into a console line
        /// dumps a managed stack trace, complete with compiler-generated frame names, at someone who
        /// cannot act on any of it.
        /// </summary>
        internal static void Error(string key, Throttle throttle, string message, Exception e)
        {
            EmitWithException(Level.Error, key, throttle, message, e);
        }

        /// <summary>
        /// A warning carrying an exception. Same split as the Error overload: full exception to the log
        /// files, type and message only to the console. Exists because severity and "do I have an
        /// exception" are independent questions, and forcing an exception-bearing line to Error just to
        /// get the formatting would repaint a deliberately yellow line red.
        /// </summary>
        internal static void Warn(string key, Throttle throttle, string message, Exception e)
        {
            EmitWithException(Level.Warning, key, throttle, message, e);
        }

        /// <summary>
        /// Test seam: ScenarioRunner asserts the last broadcast text by reflection. Changing a
        /// broadcast's wording breaks those assertions. Written without synchronisation, which is fine
        /// only because Broadcast is main-thread-only.
        /// </summary>
        internal static string LastBroadcast;

        /// <summary>
        /// A networked announcement, for events the whole server needs to know about (enforcement
        /// actions, things the server did to a player's base). Unlike every other method here this
        /// leaves the machine: it sends a vanilla ChatMessage, which prints locally and replicates to
        /// every client. A plain ConsoleWindow print on a server is invisible to clients.
        ///
        /// It carries NO severity, because ChatMessage has no colour field. Everything arrives looking
        /// the same, so put any urgency in the words. Server-authoritative and main-thread only.
        /// </summary>
        internal static void Broadcast(string key, Throttle throttle, string message)
        {
            if (!Allow(key, throttle, Level.Info, message)) return;
            LastBroadcast = message;
            SafeLog(Level.Info, message);
            if (OnWorkerThread())
            {
                SafeLog(Level.Warning, "broadcast skipped: called off the main thread (" + message + ")");
                return;
            }
            try
            {
                var chatMessage = new ChatMessage
                {
                    ChatText = message,
                    DisplayName = "Server",
                    HumanId = -1
                };
                chatMessage.PrintToConsole();
                if (NetworkManager.IsServer)
                    NetworkServer.SendToClients(chatMessage, NetworkChannel.GeneralTraffic, -1L);
            }
            catch (Exception ex)
            {
                SafeLog(Level.Warning, "console broadcast failed: " + ex.Message);
            }
        }

        /// <summary>
        /// Emit the pending "and N more" line for a CapWithSummary key and clear its state. Call this
        /// when the sweep that produced the lines is finished. Harmless if nothing is pending.
        /// </summary>
        internal static void FlushSummary(string key)
        {
            string subject;
            int suppressed;
            Level level;
            lock (Gate)
            {
                if (!Keys.TryGetValue(key, out KeyState state) || state.Suppressed <= 0) return;
                suppressed = state.Suppressed;
                level = state.PendingLevel;
                subject = state.PendingSubject;
                state.Suppressed = 0;
                state.PendingSubject = null;
            }
            string line = suppressed + " more not shown" + (string.IsNullOrEmpty(subject) ? "." : " (" + subject + ").");
            SafeLog(level, line);
            WriteConsole(level, line);
            MirrorToBootLog(level, line);
        }

        /// <summary>
        /// Forget all throttle state, flushing any pending summaries first. Call on world load and on
        /// rejoin, so a player entering a new world is told about that world's problems.
        /// </summary>
        internal static void ResetSession()
        {
            List<string> pending = new List<string>();
            lock (Gate)
            {
                foreach (KeyValuePair<string, KeyState> pair in Keys)
                    if (pair.Value.Suppressed > 0) pending.Add(pair.Key);
            }
            foreach (string key in pending) FlushSummary(key);
            lock (Gate) { Keys.Clear(); }
        }

        // ---------------------------------------------------------------------------------------------

        private static void Emit(Level level, string key, Throttle throttle, string message)
        {
            if (!Allow(key, throttle, level, message)) return;
            SafeLog(level, message);
            WriteConsole(level, message);
            MirrorToBootLog(level, message);
        }

        private static void EmitWithException(Level level, string key, Throttle throttle, string message, Exception e)
        {
            if (!Allow(key, throttle, level, message)) return;
            SafeLog(level, message + ": " + e);
            string brief = message + ": " + e.GetType().Name + ": " + e.Message;
            WriteConsole(level, brief);
            MirrorToBootLog(level, brief);
        }

        private static bool Allow(string key, Throttle throttle, Level level, string subject)
        {
            if (string.IsNullOrEmpty(key)) return true;   // misuse; do not silently swallow the message
            if (throttle.Policy == Throttle.Kind.Never) return true;

            lock (Gate)
            {
                if (!Keys.TryGetValue(key, out KeyState state))
                {
                    state = new KeyState();
                    Keys[key] = state;
                }

                switch (throttle.Policy)
                {
                    case Throttle.Kind.Cooldown:
                        int now = Environment.TickCount;
                        if (state.HasShown && unchecked(now - state.LastShownTicks) < throttle.Milliseconds)
                            return false;
                        state.HasShown = true;
                        state.LastShownTicks = now;
                        return true;

                    case Throttle.Kind.Once:
                        if (state.HasShown) return false;
                        state.HasShown = true;
                        return true;

                    case Throttle.Kind.MaxTimes:
                        if (state.Count >= throttle.Limit) return false;
                        state.Count++;
                        return true;

                    case Throttle.Kind.CapWithSummary:
                        if (state.Count >= throttle.Limit)
                        {
                            state.Suppressed++;
                            state.PendingLevel = level;
                            state.PendingSubject = subject;
                            return false;
                        }
                        state.Count++;
                        return true;

                    default:
                        return true;
                }
            }
        }

        private static void WriteConsole(Level level, string message)
        {
            if (OnWorkerThread()) return;   // the log legs above already recorded it
            try
            {
                if (level == Level.Error)
                    ConsoleWindow.PrintError(_prefix + message, suppressStacktrace: true);
                else
                    ConsoleWindow.PrintAction(_prefix + message, aged: false);
            }
            catch
            {
                // Fires before the console UI exists (a print from OnPrefabsLoaded). ConsoleWindow has
                // a premature-log queue so this should not happen, but the catch costs nothing.
            }
        }

        private static void SafeLog(Level level, string message)
        {
            if (!_initialised) return;
            try
            {
                if (level == Level.Error) _log?.LogError(message);
                else if (level == Level.Warning) _log?.LogWarning(message);
                else _log?.LogInfo(message);
            }
            catch { }
        }

        /// <summary>
        /// True while no game is running, i.e. during boot and at the main menu. `GameManager.GameState`
        /// is `None` until a world starts loading or joining, and returns to `None` on a trip back to the
        /// menu.
        ///
        /// Do NOT reach for `GameManager.RunSimulation` here. It is only `!NetworkManager.IsClient`, so it
        /// is already true at the main menu and during boot; it answers "am I the host", not "is a game
        /// running". That distinction has bitten this repo more than once, which is why the check lives
        /// here instead of at each call site. See Research/GameSystems/ModLoadSequence.md.
        /// </summary>
        internal static bool AtBootOrMenu
        {
            get
            {
                try { return GameManager.GameState == GameState.None; }
                catch { return true; }
            }
        }

        /// <summary>
        /// Yields until a world is actually running.
        ///
        /// Put this at the top of any coroutine that wants a player to READ something, when the coroutine
        /// is started from `Prefab.OnPrefabsLoaded`. That event fires at boot, during
        /// `LoadGameDataAsync`, with the loading screen up and before the main menu appears, so anything
        /// it prints has aged off the closed-console overlay long before the player reaches a world. A
        /// startup banner anchored there plays to an empty room.
        ///
        /// Pair it with `WaitForSecondsRealtime` rather than `WaitForSeconds` for any pacing afterwards:
        /// `WaitForSeconds` is timeScale-scaled and pausing sets timeScale to 0, which would stall the
        /// sequence mid-run once a world is up.
        /// </summary>
        internal static IEnumerator WaitForWorld()
        {
            while (AtBootOrMenu) yield return null;
        }

        private static bool InBootPhase()
        {
            return AtBootOrMenu;
        }

        private static bool OnWorkerThread()
        {
            // GameManager.IsThread is inverted from its name: true means NOT the main thread.
            try { return GameManager.IsThread; }
            catch { return false; }
        }

        // --- StationeersLaunchPad boot log, by reflection --------------------------------------------
        //
        // Resolved reflectively and never referenced at compile time. A hard reference to
        // StationeersLaunchPad turns "not installed" into a TypeLoadException thrown at the JIT of
        // whichever method mentions the type, which takes down the caller rather than degrading. Mods
        // loaded by the BepInEx chainloader rather than by StationeersLaunchPad hit exactly that case.
        //
        // The four-argument Log overload is bound specifically because it is the only one exposing
        // `unity`, and it MUST be passed false: the convenience wrappers default to unity: true, which
        // maps Error and Fatal onto Debug.LogError, which the in-game console re-prints lowercased with
        // an unsuppressible stack trace. That would undo the entire point of this file.

        private static MethodInfo _bootLog;
        private static object _bootLogger;
        private static object _sevInfo, _sevWarning, _sevError;
        private static bool _bootResolved;

        private static void ResolveBootLog()
        {
            _bootResolved = true;
            try
            {
                Type loggerType = null, severityType = null;
                foreach (Assembly asm in AppDomain.CurrentDomain.GetAssemblies())
                {
                    if (loggerType == null) loggerType = asm.GetType("StationeersLaunchPad.Logger", false);
                    if (severityType == null) severityType = asm.GetType("StationeersLaunchPad.LogSeverity", false);
                    if (loggerType != null && severityType != null) break;
                }
                if (loggerType == null || severityType == null) return;

                FieldInfo global = loggerType.GetField("Global", BindingFlags.Public | BindingFlags.Static);
                _bootLogger = global?.GetValue(null);
                if (_bootLogger == null) return;

                _bootLog = loggerType.GetMethod("Log", new[] { typeof(string), severityType, typeof(bool), typeof(string) });
                if (_bootLog == null) { _bootLogger = null; return; }

                _sevInfo = Enum.Parse(severityType, "Information", true);
                _sevWarning = Enum.Parse(severityType, "Warning", true);
                _sevError = Enum.Parse(severityType, "Error", true);
            }
            catch
            {
                _bootLog = null;
                _bootLogger = null;
            }
        }

        private static void MirrorToBootLog(Level level, string message)
        {
            if (!InBootPhase()) return;
            if (!_bootResolved) ResolveBootLog();
            if (_bootLog == null || _bootLogger == null) return;
            try
            {
                object severity = level == Level.Error ? _sevError : level == Level.Warning ? _sevWarning : _sevInfo;
                string name = _prefix.Trim().TrimStart('[').TrimEnd(']');
                _bootLog.Invoke(_bootLogger, new[] { message, severity, false, (object)name });
            }
            catch { }
        }
    }
}
