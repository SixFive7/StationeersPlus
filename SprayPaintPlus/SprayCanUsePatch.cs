using Assets.Scripts.Networking;
using Assets.Scripts.Objects.Items;
using HarmonyLib;
using JetBrains.Annotations;

namespace SprayPaintPlus
{
    /// <summary>
    /// Patches SprayCan.OnUseItem for configurable infinite paint and pollution suppression.
    /// Only modifies behavior when running on the server — clients defer to server authority.
    /// </summary>
    [HarmonyPatch(typeof(SprayCan), nameof(SprayCan.OnUseItem))]
    public class SprayCanUsePatch
    {
        [UsedImplicitly]
        public static bool Prefix(SprayCan __instance, ref bool __result, ref float quantity)
        {
            // Fix #6: Only apply on server. Clients should not modify quantity locally —
            // the server's authoritative state will be synced to them. This prevents
            // visual flicker where the client briefly shows paint consumed then gets
            // corrected by the server.
            if (!NetworkManager.IsServer)
                return true;

            bool infinite = SprayPaintPlusPlugin.UnlimitedSprayPaintUses.Value;
            bool suppressPollution = SprayPaintPlusPlugin.SuppressSprayPaintPollution.Value;

            // The two flags are independent. The four combinations:
            //   infinite=T, suppress=T → no consumption, no pollution (skip vanilla)
            //   infinite=T, suppress=F → no consumption, pollution still emits (vanilla runs with quantity=0)
            //   infinite=F, suppress=T → normal consumption, no pollution (skip vanilla, apply quantity manually)
            //   infinite=F, suppress=F → normal consumption, normal pollution (vanilla runs unmodified)

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
