using BepInEx.Configuration;
using System;
using System.Collections.Generic;
using System.Text;
using ConsoleWindow = Assets.Scripts.ConsoleWindow;   // the in-game console (F3). An alias,
                                                      // not `using Assets.Scripts;`, so the
                                                      // namespace's own Settings type cannot
                                                      // collide with anything here (CS0104).

namespace SprayPaintPlus
{
    /// <summary>
    /// Every user-facing message about a setting that did not take effect.
    ///
    /// Three channels, deliberately kept apart:
    ///
    ///   LogEffectiveSettings()    one Info line, BepInEx log only, never the
    ///                             console. The support tool: a player pastes it and
    ///                             the client, server and merged value of every
    ///                             function is right there.
    ///   OnJoinPayloadReceived()   one console line at join, listing everything the
    ///                             player enabled that this server does not allow.
    ///                             One message for all of them, never one each.
    ///   WarnBlocked(function)     one console line at the moment a function is
    ///                             actually blocked, three times per function per
    ///                             session and then silent. This is the channel that
    ///                             matters: the join line gets scrolled past and the
    ///                             failure happens minutes later.
    ///
    /// Console rules, all counter-intuitive enough to be worth stating:
    ///
    /// - `aged` is inverted from its name. ConsoleWindow.Print defaults to
    ///   aged: true, which means the line is NOT on the bottom-left overlay and only
    ///   shows once the player opens the console. Anything meant to be seen passes
    ///   aged: false. PrintAction already defaults that way.
    /// - PrintAction is the yellow channel. There is no PrintWarning, and PrintError
    ///   is red and dumps Environment.StackTrace unless suppressed. These messages
    ///   are informational: the player did nothing wrong.
    /// - Plain text only. The console renders through ImGui.TextUnformatted, so rich
    ///   text shows as literal characters, and a dedicated server silently discards
    ///   any line containing "&lt;color=".
    /// - Never pair a print with Debug.LogError or Debug.LogException. ConsoleWindow
    ///   subscribes to Unity's error stream and re-prints them itself, so the player
    ///   would see the same text twice. BepInEx Log.* is fine; it never reaches the
    ///   console.
    /// - Main thread only. Print shifts a 1024-entry static array with no lock while
    ///   the draw loop reads it, every print is O(1024), and nothing anywhere rate
    ///   limits it. Self-limiting is not optional, which is what the three-per-
    ///   function cap is for.
    /// </summary>
    internal static class WarningNotifier
    {
        private const string Prefix = "[SprayPaintPlus] ";

        /// <summary>
        /// How many console notices one function may produce per session. Internal
        /// because the send side reads it too: SettingBlockedNotice caps what the
        /// server puts on the wire at the same number, and the two must not drift.
        /// </summary>
        internal const int MaxNoticesPerFunction = 3;

        /// <summary>
        /// Canonical function names. These are the settings-panel entry names on
        /// purpose: a player told "Network Paint Pipes is turned off on this server"
        /// can go straight to the setting and see it. They are also the dictionary
        /// keys for the three-per-function cap and the payload of
        /// SettingBlockedNotice, so a caller inventing its own wording gets its own
        /// counter and wording that does not match the join line.
        /// </summary>
        internal static class Functions
        {
            internal const string ColorCycling = "Color Cycling";
            internal const string ColorPicking = "Color Picking";
            internal const string UnlimitedUses = "Unlimited Spray Paint Uses";
            internal const string GlowPaint = "Glow Paint";
            internal const string NetworkPainting = "Network Painting";
            internal const string Pipes = "Network Paint Pipes";
            internal const string Cables = "Network Paint Cables";
            internal const string Chutes = "Network Paint Chutes";
            internal const string Walls = "Network Paint Walls";
            internal const string Rails = "Network Paint Rails";
            internal const string LargeStructures = "Network Paint Large Structures";
            internal const string Elevators = "Network Paint Elevators";
            internal const string Ladders = "Network Paint Ladders";
            internal const string Stairs = "Network Paint Stairs";
            internal const string Stairwells = "Network Paint Stairwells";
        }

        // Function name -> how many console notices it has already produced this
        // session. Ordinal comparison: these are fixed identifiers, not player text.
        private static readonly Dictionary<string, int> NoticeCounts =
            new Dictionary<string, int>(StringComparer.Ordinal);

        /// <summary>
        /// Clears the per-function notice counters. Called from LeaveGameResetPatch
        /// alongside SettingsMerge.ClearSynced, so a rejoin starts the player's three
        /// notices over.
        /// </summary>
        internal static void ResetSession()
        {
            NoticeCounts.Clear();
        }

        /// <summary>
        /// The support line. One Info entry in the BepInEx log listing every paired
        /// function with its client half, its server half and the merged result.
        /// Never printed to the console: it is long, and it is for whoever reads the
        /// log after a bug report, not for the player mid-game.
        /// </summary>
        internal static void LogEffectiveSettings()
        {
            var log = SprayPaintPlusPlugin.Log;
            if (log == null) return;

            var sb = new StringBuilder(1024);
            sb.Append("Effective settings (client/server -> result) as ")
              .Append(SettingsMerge.IsAuthority
                  ? "authority, both halves local"
                  : "remote client, server half from the host")
              .Append(": ");

            // Color cycling is the only non-boolean, so it is formatted by hand. The
            // result column comes from SettingsMerge rather than being recomputed
            // here, so the number in the log is the number the game actually used.
            sb.Append(Functions.ColorCycling).Append('=')
              .Append(ClientCycling()).Append('/').Append(ServerCycling())
              .Append(" -> ").Append(SettingsMerge.EffectiveColorCycling.ToString());

            // Color picking has an extra rule inside SettingsMerge (a can that cannot
            // change color cannot be eyedropped onto either), so its result column is
            // taken from the accessor instead of being recomputed as client AND server.
            Append(sb, Functions.ColorPicking,
                SprayPaintPlusPlugin.ClientColorPicking,
                SprayPaintPlusPlugin.ServerColorPicking,
                SettingsMerge.SyncedColorPicking,
                SettingsMerge.EffectiveColorPicking);

            Append(sb, Functions.UnlimitedUses,
                SprayPaintPlusPlugin.ClientUnlimitedSprayPaintUses,
                SprayPaintPlusPlugin.ServerUnlimitedSprayPaintUses,
                SettingsMerge.SyncedUnlimitedUses);

            Append(sb, Functions.GlowPaint,
                SprayPaintPlusPlugin.ClientGlowPaint,
                SprayPaintPlusPlugin.ServerGlowPaint,
                SettingsMerge.SyncedGlowPaint,
                SettingsMerge.EffectiveGlowPaint);

            Append(sb, Functions.NetworkPainting,
                SprayPaintPlusPlugin.ClientNetworkPainting,
                SprayPaintPlusPlugin.ServerNetworkPainting,
                SettingsMerge.SyncedNetworkPainting);
            Append(sb, Functions.Pipes,
                SprayPaintPlusPlugin.ClientNetworkPaintPipes,
                SprayPaintPlusPlugin.ServerNetworkPaintPipes,
                SettingsMerge.SyncedNetworkPaintPipes);
            Append(sb, Functions.Cables,
                SprayPaintPlusPlugin.ClientNetworkPaintCables,
                SprayPaintPlusPlugin.ServerNetworkPaintCables,
                SettingsMerge.SyncedNetworkPaintCables);
            Append(sb, Functions.Chutes,
                SprayPaintPlusPlugin.ClientNetworkPaintChutes,
                SprayPaintPlusPlugin.ServerNetworkPaintChutes,
                SettingsMerge.SyncedNetworkPaintChutes);
            Append(sb, Functions.Walls,
                SprayPaintPlusPlugin.ClientNetworkPaintWalls,
                SprayPaintPlusPlugin.ServerNetworkPaintWalls,
                SettingsMerge.SyncedNetworkPaintWalls);
            Append(sb, Functions.Rails,
                SprayPaintPlusPlugin.ClientNetworkPaintRails,
                SprayPaintPlusPlugin.ServerNetworkPaintRails,
                SettingsMerge.SyncedNetworkPaintRails);
            Append(sb, Functions.LargeStructures,
                SprayPaintPlusPlugin.ClientNetworkPaintLargeStructures,
                SprayPaintPlusPlugin.ServerNetworkPaintLargeStructures,
                SettingsMerge.SyncedNetworkPaintLargeStructures);
            Append(sb, Functions.Elevators,
                SprayPaintPlusPlugin.ClientNetworkPaintElevators,
                SprayPaintPlusPlugin.ServerNetworkPaintElevators,
                SettingsMerge.SyncedNetworkPaintElevators);
            Append(sb, Functions.Ladders,
                SprayPaintPlusPlugin.ClientNetworkPaintLadders,
                SprayPaintPlusPlugin.ServerNetworkPaintLadders,
                SettingsMerge.SyncedNetworkPaintLadders);
            Append(sb, Functions.Stairs,
                SprayPaintPlusPlugin.ClientNetworkPaintStairs,
                SprayPaintPlusPlugin.ServerNetworkPaintStairs,
                SettingsMerge.SyncedNetworkPaintStairs);
            Append(sb, Functions.Stairwells,
                SprayPaintPlusPlugin.ClientNetworkPaintStairwells,
                SprayPaintPlusPlugin.ServerNetworkPaintStairwells,
                SettingsMerge.SyncedNetworkPaintStairwells);

            // Unpaired, but listed anyway: a bug report that turns out to be
            // "Paint Single Item By Default was on" is otherwise a wasted round trip.
            sb.Append("; Paint Single Item By Default=")
              .Append(OnOff(SprayPaintPlusPlugin.PaintSingleItemByDefault?.Value ?? false))
              .Append(" (client only); Invert Color Scroll Direction=")
              .Append(OnOff(SprayPaintPlusPlugin.InvertColorScrollDirection?.Value ?? false))
              .Append(" (client only); Suppress Spray Paint Pollution=")
              .Append(OnOff(SprayPaintPlusPlugin.ServerSuppressSprayPaintPollution?.Value ?? true))
              .Append(" (server only)");

            log.LogInfo(sb.ToString());
        }

        /// <summary>
        /// Called by SettingsConfigSync once the host's values have landed. Logs the
        /// support line, then, if the host forbids anything the player asked for,
        /// prints exactly one console message naming all of it.
        /// </summary>
        internal static void OnJoinPayloadReceived()
        {
            LogEffectiveSettings();

            var blocked = new List<string>();

            // The ladder is ordered strictest-first, so "server is lower" is
            // "server is stricter". Equal or more permissive is not a mismatch.
            var clientCycling = ClientCyclingValue();
            var serverCycling = ServerCyclingValue();
            if ((int)serverCycling < (int)clientCycling)
                blocked.Add($"{Functions.ColorCycling} (this server allows: {ModeLabel(serverCycling)})");

            AddIfBlocked(blocked, Functions.ColorPicking,
                SprayPaintPlusPlugin.ClientColorPicking,
                SprayPaintPlusPlugin.ServerColorPicking,
                SettingsMerge.SyncedColorPicking);
            AddIfBlocked(blocked, Functions.UnlimitedUses,
                SprayPaintPlusPlugin.ClientUnlimitedSprayPaintUses,
                SprayPaintPlusPlugin.ServerUnlimitedSprayPaintUses,
                SettingsMerge.SyncedUnlimitedUses);
            AddIfBlocked(blocked, Functions.GlowPaint,
                SprayPaintPlusPlugin.ClientGlowPaint,
                SprayPaintPlusPlugin.ServerGlowPaint,
                SettingsMerge.SyncedGlowPaint);
            AddIfBlocked(blocked, Functions.NetworkPainting,
                SprayPaintPlusPlugin.ClientNetworkPainting,
                SprayPaintPlusPlugin.ServerNetworkPainting,
                SettingsMerge.SyncedNetworkPainting);
            AddIfBlocked(blocked, Functions.Pipes,
                SprayPaintPlusPlugin.ClientNetworkPaintPipes,
                SprayPaintPlusPlugin.ServerNetworkPaintPipes,
                SettingsMerge.SyncedNetworkPaintPipes);
            AddIfBlocked(blocked, Functions.Cables,
                SprayPaintPlusPlugin.ClientNetworkPaintCables,
                SprayPaintPlusPlugin.ServerNetworkPaintCables,
                SettingsMerge.SyncedNetworkPaintCables);
            AddIfBlocked(blocked, Functions.Chutes,
                SprayPaintPlusPlugin.ClientNetworkPaintChutes,
                SprayPaintPlusPlugin.ServerNetworkPaintChutes,
                SettingsMerge.SyncedNetworkPaintChutes);
            AddIfBlocked(blocked, Functions.Walls,
                SprayPaintPlusPlugin.ClientNetworkPaintWalls,
                SprayPaintPlusPlugin.ServerNetworkPaintWalls,
                SettingsMerge.SyncedNetworkPaintWalls);
            AddIfBlocked(blocked, Functions.Rails,
                SprayPaintPlusPlugin.ClientNetworkPaintRails,
                SprayPaintPlusPlugin.ServerNetworkPaintRails,
                SettingsMerge.SyncedNetworkPaintRails);
            AddIfBlocked(blocked, Functions.LargeStructures,
                SprayPaintPlusPlugin.ClientNetworkPaintLargeStructures,
                SprayPaintPlusPlugin.ServerNetworkPaintLargeStructures,
                SettingsMerge.SyncedNetworkPaintLargeStructures);
            AddIfBlocked(blocked, Functions.Elevators,
                SprayPaintPlusPlugin.ClientNetworkPaintElevators,
                SprayPaintPlusPlugin.ServerNetworkPaintElevators,
                SettingsMerge.SyncedNetworkPaintElevators);
            AddIfBlocked(blocked, Functions.Ladders,
                SprayPaintPlusPlugin.ClientNetworkPaintLadders,
                SprayPaintPlusPlugin.ServerNetworkPaintLadders,
                SettingsMerge.SyncedNetworkPaintLadders);
            AddIfBlocked(blocked, Functions.Stairs,
                SprayPaintPlusPlugin.ClientNetworkPaintStairs,
                SprayPaintPlusPlugin.ServerNetworkPaintStairs,
                SettingsMerge.SyncedNetworkPaintStairs);
            AddIfBlocked(blocked, Functions.Stairwells,
                SprayPaintPlusPlugin.ClientNetworkPaintStairwells,
                SprayPaintPlusPlugin.ServerNetworkPaintStairwells,
                SettingsMerge.SyncedNetworkPaintStairwells);

            if (blocked.Count == 0) return;

            // One line for all of them. Phrased as information: nothing here is the
            // player's fault and nothing about their own settings has been changed.
            Print("This server does not allow " + string.Join(", ", blocked.ToArray()) +
                  ". Your own settings are untouched, they just have no effect here.");
        }

        /// <summary>
        /// A function the player had enabled was blocked at the moment they used it.
        /// Prints the first three times per function per session, then goes quiet,
        /// because ConsoleWindow has no rate limiting of its own and every print
        /// shifts a 1024-entry array.
        ///
        /// Called on the client from SettingBlockedNotice.Process for the functions
        /// the server evaluates, and directly for the ones the client evaluates
        /// itself (color cycling, color picking).
        /// </summary>
        internal static void WarnBlocked(string function)
        {
            if (string.IsNullOrEmpty(function)) return;

            NoticeCounts.TryGetValue(function, out int seen);
            if (seen >= MaxNoticesPerFunction) return;
            NoticeCounts[function] = seen + 1;

            var message = function + " is turned off on this server, so it had no effect.";
            if (seen + 1 == MaxNoticesPerFunction)
                message += " No more notices about this one until you rejoin.";
            Print(message);
        }

        // ---- Internals ----------------------------------------------------------

        // A local copy of SettingsMerge's private ServerHalf. Duplicated rather than
        // exposed because the two callers here want the server half on its own for a
        // report column, which is not something any consumer of SettingsMerge should
        // ever be doing. Keep the two in step: authority reads its own entry, a
        // remote client reads the synced host value and falls back to its own until
        // the join payload lands.
        private static bool ServerHalf(ConfigEntry<bool> local, bool? synced)
        {
            if (local == null) return true;
            if (SettingsMerge.IsAuthority) return local.Value;
            return synced ?? local.Value;
        }

        private static ColorCyclingMode ClientCyclingValue() =>
            SprayPaintPlusPlugin.ClientColorCycling?.Value ?? ColorCyclingMode.AllColors;

        private static ColorCyclingMode ServerCyclingValue()
        {
            var local = SprayPaintPlusPlugin.ServerColorCycling;
            if (local == null) return ColorCyclingMode.AllColors;
            if (SettingsMerge.IsAuthority) return local.Value;
            return SettingsMerge.SyncedColorCycling ?? local.Value;
        }

        private static string ClientCycling() => ClientCyclingValue().ToString();
        private static string ServerCycling() => ServerCyclingValue().ToString();

        // The player-facing labels, matching the [Display] attributes on
        // ColorCyclingMode. Hand-written rather than read back off the attribute:
        // the reflection would be one more thing to get wrong for three strings.
        // If a member label changes there, change it here.
        private static string ModeLabel(ColorCyclingMode mode)
        {
            switch (mode)
            {
                case ColorCyclingMode.CannotChange: return "Can cannot change color";
                case ColorCyclingMode.WithinFamily: return "Cycles within paint family";
                default: return "Cycles through all colors";
            }
        }

        private static string OnOff(bool value) => value ? "on" : "off";

        private static void Append(StringBuilder sb, string name, ConfigEntry<bool> clientEntry,
            ConfigEntry<bool> serverEntry, bool? synced, bool? mergedOverride = null)
        {
            bool client = clientEntry?.Value ?? true;
            bool server = ServerHalf(serverEntry, synced);
            sb.Append("; ").Append(name).Append('=')
              .Append(OnOff(client)).Append('/').Append(OnOff(server))
              .Append(" -> ").Append(OnOff(mergedOverride ?? (client && server)));
        }

        private static void AddIfBlocked(List<string> into, string name, ConfigEntry<bool> clientEntry,
            ConfigEntry<bool> serverEntry, bool? synced)
        {
            // Only a real mismatch counts: the player wants it, the server refuses.
            // A function the player turned off themselves is not worth a line.
            if ((clientEntry?.Value ?? true) && !ServerHalf(serverEntry, synced))
                into.Add(name);
        }

        private static void Print(string message)
        {
            // PrintAction, not PrintError: yellow, no stack trace, and already
            // aged: false so the line lands on the overlay instead of waiting for
            // the player to open the console. The argument is passed explicitly
            // anyway so a future default change cannot silently hide these.
            // The catch is for prints that fire before the console UI exists;
            // ConsoleWindow queues those itself, but the guard costs nothing.
            try { ConsoleWindow.PrintAction(Prefix + message, aged: false); }
            catch { }

            // BepInEx only. Deliberately no Debug.LogError or Debug.LogException
            // here: ConsoleWindow re-prints Unity's error stream, so the player
            // would get the same text twice, the second time in red with a trace.
            SprayPaintPlusPlugin.Log?.LogWarning(message);
        }
    }
}
