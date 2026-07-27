using Assets.Scripts;
using Assets.Scripts.Objects;
using Assets.Scripts.Objects.Entities;
using Assets.Scripts.Objects.Items;
using HarmonyLib;
using JetBrains.Annotations;

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
    /// Drops the server's per-player state for a client that disconnects: its
    /// client-half preference mask and its blocked-notice budget.
    ///
    /// Runs as a Prefix because NetworkServer.ClientDisconnected calls
    /// NetworkBase.RemoveClient before returning, making the Client record
    /// unreachable to a Postfix. We look up the disconnecting client's
    /// registered Human and remove the rows keyed by its ReferenceId.
    ///
    /// Targeted removal, never a bulk clear. PlayerModifiers doubles as the
    /// send-dedupe record for PaintModifierMessage (ColorCyclerPatch only sends when
    /// the freshly packed mask differs from the row stored here), so wiping the whole
    /// dictionary would make every remaining player resend theirs every frame.
    /// </summary>
    [HarmonyPatch(typeof(NetworkServer), nameof(NetworkServer.ClientDisconnected))]
    public class ClientDisconnectCleanupPatch
    {
        [UsedImplicitly]
        public static void Prefix(long connectionId)
        {
            Client client = Client.Find(connectionId);
            Human human = client?.RegisteredHuman;
            if (human == null)
                return;

            SprayPaintHelpers.PlayerModifiers.Remove(human.ReferenceId);

            // The player's blocked-notice budget leaves with them. Rejoining is a new
            // session and starts a fresh three per function, which is exactly what the
            // display cap on their own machine does.
            SprayPaintHelpers.BlockedNoticeCounts.Remove(human.ReferenceId);
        }
    }
}
