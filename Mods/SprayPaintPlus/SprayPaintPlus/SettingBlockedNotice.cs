using Assets.Scripts;
using Assets.Scripts.Networking;
using Assets.Scripts.Objects;
using Assets.Scripts.Objects.Entities;
using LaunchPadBooster.Networking;
using System;
using System.Collections.Generic;

namespace SprayPaintPlus
{
    /// <summary>
    /// Server -> one client: a function the acting player had enabled was refused by
    /// this server's half of the setting, at the moment they tried to use it.
    ///
    /// This message exists because two decisions collide. Warnings are precise (they
    /// fire only when a disabled function actually changed the outcome), and the
    /// network-type classification runs server-side, so for the server-evaluated
    /// functions (network painting and its ten types, glow paint, unlimited uses) the
    /// client has no way to know a suppression happened. ConsoleWindow is
    /// process-local and not networked, so a print on the server reaches nobody.
    ///
    /// The three-per-function-per-session cap is enforced on both sides, and it has to
    /// be. WarningNotifier caps what the receiving player SEES, which is the right
    /// number for a console the game never rate limits, but it caps nothing the server
    /// puts on the wire: the block is detected once per stroke and painting repeats
    /// several times a second, so an uncapped sender kept messaging a player about the
    /// same disabled function for as long as they held the button. NotifyBlocked
    /// therefore keeps its own per-player, per-function count and stops at the same
    /// three. A rejoin resets both, the display side through WarningNotifier.
    /// ResetSession and the send side through the disconnect and leave-game cleanups.
    /// </summary>
    public class SettingBlockedNotice : INetworkMessage
    {
        /// <summary>
        /// The function that was refused, in the player's own words. Use a
        /// WarningNotifier.Functions constant: it doubles as the key for the
        /// three-per-function cap, so ad-hoc wording gets its own counter and
        /// disagrees with the join-time message about the same setting.
        /// </summary>
        public string Function;

        public void Serialize(RocketBinaryWriter writer)
        {
            writer.WriteString(Function ?? string.Empty);
        }

        public void Deserialize(RocketBinaryReader reader)
        {
            Function = reader.ReadString();
        }

        public void Process(long hostId)
        {
            WarningNotifier.WarnBlocked(Function);
        }

        /// <summary>
        /// The single entry point for a server-detected block. Routes to the console
        /// directly when the acting player is the one sitting at this machine, and
        /// over the wire when they are not.
        ///
        /// Callers pass the acting player's Human ReferenceId (the same key
        /// SprayPaintHelpers.PlayerModifiers uses) and do not branch on who that is:
        /// a caller that also calls WarningNotifier.WarnBlocked for the local player
        /// would double-print and burn the three-notice budget twice as fast.
        /// </summary>
        internal static void NotifyBlocked(long playerHumanId, string function)
        {
            if (playerHumanId == 0L || string.IsNullOrEmpty(function)) return;

            // Authority only. This is the "server detected it" half of the warning
            // split; a remote client that works out its own block calls
            // WarningNotifier.WarnBlocked directly. IsActive is false in
            // single-player, so solo falls through here and is handled below, which
            // is the same shape SettingsMerge.IsAuthority uses.
            if (NetworkManager.IsActive && !NetworkManager.IsServer) return;

            // Ahead of every delivery route below, and ahead of the Thing.Find work,
            // so a player who has already had their three notices about this function
            // costs nothing at all on the fourth stroke. A notice the routing below
            // then drops still spends its budget, which is fine: the only way to reach
            // those two branches is a player who has just left.
            if (!TakeNoticeBudget(playerHumanId, function)) return;

            // The acting player is us: single-player, or a listen host painting in
            // their own world. Nothing to send, so print it here.
            var localHuman = Human.LocalHuman;
            if (localHuman != null && localHuman.ReferenceId == playerHumanId)
            {
                WarningNotifier.WarnBlocked(function);
                return;
            }

            // Human ReferenceId -> Client. Going via Thing.Find and OwnerClientId is
            // order-independent; scanning NetworkBase.Clients for a matching
            // RegisteredHuman would miss the listen host (which is not in that list)
            // and depends on Client.Register having already run for the joiner.
            //
            // OwnerClientId is a ulong Steam id and must stay one. Client.Find has a
            // long overload keyed on connection id and a ulong overload keyed on
            // Steam id, so casting it "for tidiness" silently looks up the wrong
            // thing and returns either null or somebody else.
            var human = Thing.Find(playerHumanId) as Human;
            if (human == null)
            {
                SprayPaintPlusPlugin.Log?.LogDebug(
                    $"Blocked notice for '{function}': human {playerHumanId} not found, dropping.");
                return;
            }

            var client = Client.Find(human.OwnerClientId);
            if (client == null)
            {
                // Unowned human, or the player left between the paint and this call.
                SprayPaintPlusPlugin.Log?.LogDebug(
                    $"Blocked notice for '{function}': no client for human {playerHumanId}, dropping.");
                return;
            }

            // The host's own Human resolves to NetworkManager.HostClient, which is
            // this process; sending would round-trip a message to ourselves. The
            // LocalHuman branch normally catches this already, but LocalHuman is null
            // during a respawn or character swap, so cover it here too.
            if (client.IsHost)
            {
                WarningNotifier.WarnBlocked(function);
                return;
            }

            new SettingBlockedNotice { Function = function }.SendToClient(client);
        }

        /// <summary>
        /// Claims one notice out of this player's budget for this function, or returns
        /// false when their three are gone. The count is per player and per function,
        /// mirroring WarningNotifier's per-function count on the receiving side and
        /// reading the same constant so the two can never disagree.
        ///
        /// Rows live in SprayPaintHelpers.BlockedNoticeCounts next to the rest of the
        /// server's per-player state, and are pruned per player by the disconnect
        /// cleanup in CleanupPatches. The inner comparer is ordinal: function names are
        /// fixed identifiers, never player text.
        ///
        /// For the acting player who is sitting at this machine both counters advance,
        /// this one and WarningNotifier's, in lockstep and against the same limit. That
        /// is not a double count: three notices still means three notices.
        /// </summary>
        private static bool TakeNoticeBudget(long playerHumanId, string function)
        {
            if (!SprayPaintHelpers.BlockedNoticeCounts.TryGetValue(
                    playerHumanId, out Dictionary<string, int> perFunction))
            {
                perFunction = new Dictionary<string, int>(StringComparer.Ordinal);
                SprayPaintHelpers.BlockedNoticeCounts[playerHumanId] = perFunction;
            }

            perFunction.TryGetValue(function, out int sent);
            if (sent >= WarningNotifier.MaxNoticesPerFunction) return false;
            perFunction[function] = sent + 1;
            return true;
        }
    }
}
