using Assets.Scripts;
using Assets.Scripts.Objects;
using Assets.Scripts.Objects.Entities;
using Assets.Scripts.Objects.Items;
using HarmonyLib;
using JetBrains.Annotations;
using System;

namespace SprayPaintPlus
{
    /// <summary>
    /// Cleans up SprayCanColors dictionary when spray cans are destroyed.
    /// </summary>
    [HarmonyPatch(typeof(Thing), nameof(Thing.OnDestroy))]
    public class ThingDestroyCleanupPatch
    {
        [UsedImplicitly]
        public static void Postfix(Thing __instance)
        {
            if (__instance is SprayCan)
                SprayPaintHelpers.SprayCanColors.Remove(__instance.ReferenceId);
        }
    }

    /// <summary>
    /// Drops the server's blocked-notice budget for a client that disconnects.
    ///
    /// Runs as a Prefix because NetworkServer.ClientDisconnected calls
    /// NetworkBase.RemoveClient before returning. RemoveClient does not clear any
    /// field on the Client, but it does take it out of NetworkBase.Clients, which
    /// is the list Client.Find scans, so connectionId stops resolving to anything
    /// the moment vanilla's body runs. A Postfix would have no way back to the
    /// disconnecting player.
    ///
    /// SprayPaintHelpers.PlayerModifiers is deliberately NOT pruned here. The row is
    /// still accurate after the player leaves, because nothing changed on either
    /// side: both machines key that state by the same Human ReferenceId, which a
    /// rejoin into the same world preserves (see
    /// Research/GameSystems/PlayerIdentityAcrossRejoin.md), so server and client stay
    /// symmetric by construction. A player who does change a setting while away
    /// repacks on their next frame, sees the difference against their own record and
    /// resends, so there is never a window where the server reads them as permissive.
    /// Pruning it here would open exactly that window: the client would still hold its
    /// unchanged row, decide it had nothing to report, and the server would treat every
    /// client-half opt-out as allowed until the player next pressed Shift or Ctrl. The
    /// cost of keeping it is one ushort per distinct player who has connected, which is
    /// bounded by the number of people who ever join and is not worth a correctness
    /// risk to reclaim.
    ///
    /// The whole body is wrapped because this patch sits on vanilla's disconnect
    /// handler. A throw out of a Harmony prefix propagates instead of running the
    /// original, so an exception here would stop NetworkBase.RemoveClient from ever
    /// running and leave a dead client in the server's list, breaking disconnects
    /// for the base game and every other mod on the server. Nothing this cleanup
    /// does is worth that, so it fails quietly.
    /// </summary>
    [HarmonyPatch(typeof(NetworkServer), nameof(NetworkServer.ClientDisconnected))]
    public class ClientDisconnectCleanupPatch
    {
        [UsedImplicitly]
        public static void Prefix(long connectionId)
        {
            try
            {
                Client client = Client.Find(connectionId);
                if (client == null)
                    return;

                // The notice budget resolves the player through Human.Find, which
                // matches on Thing.OwnerClientId. That is the value that survives a
                // rejoin, and it reads the live Human list rather than a record the
                // game only ever populates once. Client.RegisteredHuman looks like the
                // obvious route and is not one: the game writes it only from
                // Client.Register, reached from the Thing.OwnerClientId setter and only
                // when that setter actually changes the value, so a character restored
                // from a save keeps it null for the whole session. That is what left
                // the budget unpruned on a dedicated server before this was fixed.
                //
                // The zero check matters: OwnerClientId is 0 on every unowned Human in
                // the world, so Human.Find(0) would return an arbitrary one of them.
                // A Client that has not finished its handshake still has ClientId 0.
                if (client.ClientId == 0UL)
                    return;

                Human owner = Human.Find(client.ClientId);
                if (owner == null)
                    return;

                // The player's blocked-notice budget leaves with them. Rejoining is a new
                // session and starts a fresh three per function, which is exactly what the
                // display cap on their own machine does.
                SprayPaintHelpers.BlockedNoticeCounts.Remove(owner.ReferenceId);
            }
            catch (Exception e)
            {
                SprayPaintPlusPlugin.Log?.LogWarning(
                    $"Disconnect cleanup failed for connection {connectionId}: {e.Message}");
            }
        }
    }

    /// <summary>
    /// Drops the synced host values and the per-session warning counters when the
    /// player leaves the world.
    ///
    /// Without this, a player who joins a server that forbids network painting and
    /// then quits to the menu and starts a single-player world would still be
    /// carrying the host's "off" in SettingsMerge.Synced*. That does not actually
    /// change behaviour, because SettingsMerge.IsAuthority makes solo read the local
    /// entry instead, but leaving stale session state lying around is one refactor
    /// away from being a bug and the warning counters genuinely do need the reset.
    ///
    /// GameManager.LeaveGame is the hook because it is public, static, parameterless
    /// and synchronous, and it is on every exit path that matters: host quits to
    /// menu, client quits to menu, client dropped by the host
    /// (NetworkManager.PlayerDisconnected calls it), join cancelled
    /// (NetworkClient.Cancel calls it), and single-player exit. The alternatives were
    /// worse: WorldManager has no ExitGame, GameManager.ClearGameAll is private and
    /// runs from an async void several frames later, and NetworkClient.Disconnect is
    /// an async UniTaskVoid. NetworkManager.EndConnection would also work and is what
    /// LaunchPadBooster itself patches, but it fires more than once per teardown on a
    /// client, so LeaveGame keeps the reset to one call. Every reset here is idempotent
    /// regardless.
    ///
    /// This is deliberately NOT the same hook as ClientDisconnectCleanupPatch above.
    /// That one is the server's view of one remote player leaving; this one is our own
    /// machine leaving the session.
    /// </summary>
    [HarmonyPatch(typeof(GameManager), nameof(GameManager.LeaveGame))]
    public class LeaveGameResetPatch
    {
        [UsedImplicitly]
        public static void Postfix()
        {
            SettingsMerge.ClearSynced();
            WarningNotifier.ResetSession();

            // The send side of the same cap. WarningNotifier.ResetSession above gives
            // every player their three console notices back; without this the server's
            // matching send budget would carry over into the next world, and a save
            // reloaded from the menu brings its Human ReferenceIds back with it, so the
            // old rows would still be found and the player would be told nothing.
            //
            // A bulk clear is safe here and ONLY here. SprayPaintHelpers.PlayerModifiers
            // is not touched by this patch and must never be: it doubles as the
            // send-dedupe record for PaintModifierMessage, so clearing it wholesale
            // would have every client resending its mask every frame. Notice budgets
            // carry no such duty; the worst a stale clear can do is grant three more.
            SprayPaintHelpers.BlockedNoticeCounts.Clear();
        }
    }
}
