using Assets.Scripts;
using Assets.Scripts.Networking;
using HarmonyLib;
using JetBrains.Annotations;
using LaunchPadBooster.Networking;
using System;

namespace SprayPaintPlus
{
    /// <summary>
    /// Ships the sixteen server-half settings down to a joining client, so
    /// SettingsMerge has a host value to merge against instead of falling back to
    /// the client's own server-half entries.
    ///
    /// Runs on the server inside NetworkServer.PackageJoinData and on the client
    /// inside NetworkClient.ProcessJoinData, at the position of the original
    /// AtmosphericsManager.DeserializeOnJoin call, which is after ProcessThings.
    /// LaunchPadBooster length-prefixes each mod's section, so a schema change here
    /// cannot desync a neighbouring mod. Write order MUST equal read order.
    ///
    /// It fires only on a remote join: never for a host loading its own world, and
    /// never in single-player. That is exactly the gating SettingsMerge wants, since
    /// on the authority the local server-half entry is already the right answer.
    /// </summary>
    internal sealed class SettingsConfigSync : IJoinSuffixSerializer
    {
        internal static readonly SettingsConfigSync Instance = new SettingsConfigSync();

        // Every value goes out through "?.Value ?? default". An unbound entry
        // throwing halfway through the write would leave the reader expecting
        // sixteen values and finding fewer, and since the reader has no way to know
        // where the truncation happened, every field after it would be garbage.
        // The defaults match the ones in BindConfig.
        public void SerializeJoinSuffix(RocketBinaryWriter writer)
        {
            writer.WriteInt32((int)(SprayPaintPlusPlugin.ServerColorCycling?.Value ?? ColorCyclingMode.AllColors));
            writer.WriteBoolean(SprayPaintPlusPlugin.ServerColorPicking?.Value ?? true);
            writer.WriteBoolean(SprayPaintPlusPlugin.ServerGlowPaint?.Value ?? true);
            writer.WriteBoolean(SprayPaintPlusPlugin.ServerUnlimitedSprayPaintUses?.Value ?? true);
            writer.WriteBoolean(SprayPaintPlusPlugin.ServerExtraPaintableStructures?.Value ?? true);
            writer.WriteBoolean(SprayPaintPlusPlugin.ServerNetworkPainting?.Value ?? true);
            writer.WriteBoolean(SprayPaintPlusPlugin.ServerNetworkPaintPipes?.Value ?? true);
            writer.WriteBoolean(SprayPaintPlusPlugin.ServerNetworkPaintCables?.Value ?? true);
            writer.WriteBoolean(SprayPaintPlusPlugin.ServerNetworkPaintChutes?.Value ?? true);
            writer.WriteBoolean(SprayPaintPlusPlugin.ServerNetworkPaintWalls?.Value ?? true);
            writer.WriteBoolean(SprayPaintPlusPlugin.ServerNetworkPaintRails?.Value ?? true);
            writer.WriteBoolean(SprayPaintPlusPlugin.ServerNetworkPaintLargeStructures?.Value ?? true);
            writer.WriteBoolean(SprayPaintPlusPlugin.ServerNetworkPaintElevators?.Value ?? true);
            writer.WriteBoolean(SprayPaintPlusPlugin.ServerNetworkPaintLadders?.Value ?? true);
            writer.WriteBoolean(SprayPaintPlusPlugin.ServerNetworkPaintStairs?.Value ?? true);
            writer.WriteBoolean(SprayPaintPlusPlugin.ServerNetworkPaintStairwells?.Value ?? true);
        }

        public void DeserializeJoinSuffix(RocketBinaryReader reader)
        {
            // All sixteen reads happen unconditionally and in order, with no branch
            // and no try/catch around them. A conditional or swallowed read would
            // leave the stream position wrong for every field after it.
            int rawColorCycling = reader.ReadInt32();
            bool colorPicking = reader.ReadBoolean();
            bool glowPaint = reader.ReadBoolean();
            bool unlimitedUses = reader.ReadBoolean();
            bool extraPaintable = reader.ReadBoolean();
            bool networkPainting = reader.ReadBoolean();
            bool pipes = reader.ReadBoolean();
            bool cables = reader.ReadBoolean();
            bool chutes = reader.ReadBoolean();
            bool walls = reader.ReadBoolean();
            bool rails = reader.ReadBoolean();
            bool largeStructures = reader.ReadBoolean();
            bool elevators = reader.ReadBoolean();
            bool ladders = reader.ReadBoolean();
            bool stairs = reader.ReadBoolean();
            bool stairwells = reader.ReadBoolean();

            // Clamp to the ladder rather than casting blind. The merge is a
            // Math.Min, so a value below CannotChange would silently freeze every
            // can in the session and a value above AllColors would defeat a client
            // that had chosen a stricter mode. MOD.Networking.Required makes a
            // version mismatch impossible in practice; this covers the rest.
            if (rawColorCycling < (int)ColorCyclingMode.CannotChange)
                rawColorCycling = (int)ColorCyclingMode.CannotChange;
            else if (rawColorCycling > (int)ColorCyclingMode.AllColors)
                rawColorCycling = (int)ColorCyclingMode.AllColors;

            SettingsMerge.SyncedColorCycling = (ColorCyclingMode)rawColorCycling;
            SettingsMerge.SyncedColorPicking = colorPicking;
            SettingsMerge.SyncedGlowPaint = glowPaint;
            SettingsMerge.SyncedUnlimitedUses = unlimitedUses;
            SettingsMerge.SyncedExtraPaintable = extraPaintable;
            SettingsMerge.SyncedNetworkPainting = networkPainting;
            SettingsMerge.SyncedNetworkPaintPipes = pipes;
            SettingsMerge.SyncedNetworkPaintCables = cables;
            SettingsMerge.SyncedNetworkPaintChutes = chutes;
            SettingsMerge.SyncedNetworkPaintWalls = walls;
            SettingsMerge.SyncedNetworkPaintRails = rails;
            SettingsMerge.SyncedNetworkPaintLargeStructures = largeStructures;
            SettingsMerge.SyncedNetworkPaintElevators = elevators;
            SettingsMerge.SyncedNetworkPaintLadders = ladders;
            SettingsMerge.SyncedNetworkPaintStairs = stairs;
            SettingsMerge.SyncedNetworkPaintStairwells = stairwells;

            // Everything above this point is stream work and must not be guarded.
            // The notifier is not: it prints, formats and allocates, and a throw
            // from it would propagate into ProcessJoinData and abort the join.
            try
            {
                WarningNotifier.OnJoinPayloadReceived();
            }
            catch (Exception e)
            {
                SprayPaintPlusPlugin.Log?.LogWarning($"Join settings notice failed: {e.Message}");
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
    /// This is deliberately NOT the same hook as ClientDisconnectCleanupPatch in
    /// CleanupPatches.cs. That one is the server's view of one remote player leaving
    /// and clears that player's rows out of the per-player dictionaries; this one is
    /// our own machine leaving the session.
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
