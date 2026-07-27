using Assets.Scripts.Networking;
using Assets.Scripts.Objects.Items;
using HarmonyLib;
using JetBrains.Annotations;

namespace SprayPaintPlus
{
    /// <summary>
    /// Patches SprayCan.OnUseItem for configurable infinite paint and pollution suppression.
    /// Only modifies behavior when running on the server. Clients defer to server authority.
    ///
    /// Unlimited uses is a paired setting, so it is merged per acting player: this
    /// body runs on the authority, which owns the server half but is not
    /// necessarily the player swinging the can, so the server half is ANDed with
    /// that player's client half out of SprayPaintHelpers.PlayerModifiers.
    ///
    /// Pollution suppression has no client half by design. The atmosphere is
    /// shared, so one player opting out would change the air for everybody; it
    /// stays a plain server read.
    /// </summary>
    [HarmonyPatch(typeof(SprayCan), nameof(SprayCan.OnUseItem))]
    public class SprayCanUsePatch
    {
        [UsedImplicitly]
        public static bool Prefix(SprayCan __instance, ref bool __result, ref float quantity)
        {
            // Skip only on multiplayer remote clients. Their authoritative
            // quantity is broadcast by the server, so running this locally
            // would briefly show paint consumed before the sync corrects it.
            // Single-player has NetworkRole.None (IsActive=false, IsServer=false),
            // which the earlier `!IsServer` guard conflated with remote clients
            // and accidentally disabled infinite spray in solo play.
            if (NetworkManager.IsActive && !NetworkManager.IsServer)
                return true;

            // The acting player, parked here by PaintAttackerTracker_Local /
            // _Remote before Thing.AttackWith runs. The read is safe on this
            // path: ISprayer.DoSpray calls OnUseItem BEFORE it calls
            // OnServer.SetCustomColor, so we are inside the tracker's window and
            // ahead of NetworkPainterPatch's read-and-reset. Deliberately not
            // reset here, because that prefix still needs it a moment later.
            //
            // Any other route into OnUseItem leaves it at -1, and PlayerPrefs.Has
            // reads an unknown player as "no preference reported" and falls back
            // to the server half alone.
            long humanId = SprayPaintHelpers.CurrentPaintingHumanId;

            bool infinite = SettingsMerge.ServerAllows(
                SprayPaintPlusPlugin.ServerUnlimitedSprayPaintUses,
                SettingsMerge.SyncedUnlimitedUses,
                humanId,
                SettingsMerge.PlayerPrefs.UnlimitedUses);

            // Tell the player only when the SERVER half is what stopped them. One
            // who switched their own copy off asked for depleting cans and gets
            // them; nothing to report. OnUseItem runs once per real use on the
            // authority, so this fires exactly when a can was actually consumed
            // against the player's wishes.
            if (!infinite
                && humanId >= 0
                && SettingsMerge.PlayerPrefs.Has(humanId, SettingsMerge.PlayerPrefs.UnlimitedUses))
            {
                SettingBlockedNotice.NotifyBlocked(humanId, WarningNotifier.Functions.UnlimitedUses);
            }

            bool suppressPollution = SprayPaintPlusPlugin.ServerSuppressSprayPaintPollution.Value;

            // The two flags are independent. The four combinations:
            //   infinite=T, suppress=T -> no consumption, no pollution (skip vanilla)
            //   infinite=T, suppress=F -> no consumption, pollution still emits (vanilla runs with quantity=0)
            //   infinite=F, suppress=T -> normal consumption, no pollution (skip vanilla, apply quantity manually)
            //   infinite=F, suppress=F -> normal consumption, normal pollution (vanilla runs unmodified)

            if (infinite)
                quantity = 0f;

            if (suppressPollution)
            {
                __instance.Quantity -= quantity;
                __result = true;
                return false;
            }

            return true;
        }
    }
}
