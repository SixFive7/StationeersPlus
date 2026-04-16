using Assets.Scripts.Networking;
using Assets.Scripts.Objects.Items;
using HarmonyLib;
using JetBrains.Annotations;

namespace SprayPaintPlus
{
    /// <summary>
    /// Patches SprayCan.OnUseItem for configurable infinite paint and pollution suppression.
    /// Only modifies behavior when running on the server. Clients defer to server authority.
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

            bool infinite = SprayPaintPlusPlugin.UnlimitedSprayPaintUses.Value;
            bool suppressPollution = SprayPaintPlusPlugin.SuppressSprayPaintPollution.Value;

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
