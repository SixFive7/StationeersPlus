using System.Collections.Generic;
using UnityEngine;
using ConsoleWindow = Assets.Scripts.ConsoleWindow;

namespace EquipmentPlus
{
    /// <summary>
    /// Player-facing messages in the in-game console (the panel F3 opens; recent lines also show
    /// bottom-left while it is closed).
    ///
    /// What reaches that console, and what does not:
    ///   - ConsoleWindow.Print* calls: always.
    ///   - UnityEngine.Debug.LogError / LogException: ALSO printed. ConsoleWindow subscribes to
    ///     Application.logMessageReceivedThreaded and re-prints LogType.Error and LogType.Exception
    ///     itself, lowercased, in red, with a stack trace. Pairing Debug.LogError with a ConsoleWindow
    ///     call for the same text shows it to the player twice.
    ///   - UnityEngine.Debug.Log / LogWarning: never printed.
    ///   - BepInEx Logger.Log*: never printed (file log only).
    ///
    /// Three traps this helper exists to avoid:
    ///   - There is no PrintWarning. Yellow is PrintAction; PrintError is red and reads as a crash.
    ///   - PrintError dumps a full Environment.StackTrace into the player's console unless it is
    ///     passed suppressStacktrace: true.
    ///   - `aged` is inverted from its name. aged: true (the Print default) sets activeTime 0, so the
    ///     line is NOT shown on the closed-console overlay and only appears once F3 is opened.
    ///     Anything meant to be seen without opening the console must pass aged: false.
    ///
    /// Main thread only: ConsoleWindow shifts an unlocked 1024-entry static array while the draw loop
    /// reads it, and the cooldown below reads UnityEngine.Time. Every current caller is a Harmony patch
    /// on an input path, so this holds. Marshal first if that ever stops being true.
    ///
    /// The console has no rate limiting of its own and every print costs a full 1024-entry array shift,
    /// so self-limiting is the caller's job. Both current call sites sit on the scroll wheel, where one
    /// flick is 10-20 notches, hence the per-message cooldown.
    ///
    /// See Research/Patterns/InGameConsoleOutput.md.
    /// </summary>
    internal static class PlayerNotice
    {
        /// <summary>Minimum gap between two identical messages reaching the console.</summary>
        private const float RepeatCooldownSeconds = 5f;

        /// <summary>Last unscaled time each distinct message was printed. Main-thread only.</summary>
        private static readonly Dictionary<string, float> LastShown = new Dictionary<string, float>();

        /// <summary>
        /// An informational notice: yellow, visible without opening the console, no stack trace.
        /// Use this for "the game did not do what you just asked, and here is why" hints. Repeats of
        /// the same text inside the cooldown are dropped.
        /// </summary>
        internal static void Show(string message)
        {
            if (!PassesCooldown(message)) return;
            try { ConsoleWindow.PrintAction($"[EquipmentPlus] {message}", aged: false); } catch { }
        }

        /// <summary>
        /// A genuine error: red, visible without opening the console, stack trace suppressed (the
        /// player's console is not the place for a managed stack trace; the BepInEx log is).
        /// Repeats of the same text inside the cooldown are dropped.
        /// </summary>
        internal static void Error(string message)
        {
            if (!PassesCooldown(message)) return;
            try { ConsoleWindow.PrintError($"[EquipmentPlus] {message}", suppressStacktrace: true); } catch { }
        }

        private static bool PassesCooldown(string message)
        {
            float now = Time.unscaledTime;
            if (LastShown.TryGetValue(message, out float last) && now - last < RepeatCooldownSeconds)
                return false;
            LastShown[message] = now;
            return true;
        }
    }
}
