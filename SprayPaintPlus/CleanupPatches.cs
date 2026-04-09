using Assets.Scripts.Objects;
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
    /// Cleans up PlayerModifiers dictionary when a client disconnects.
    /// </summary>
    [HarmonyPatch(typeof(Assets.Scripts.NetworkServer), nameof(Assets.Scripts.NetworkServer.ClientDisconnected))]
    public class ClientDisconnectCleanupPatch
    {
        [UsedImplicitly]
        public static void Postfix(long connectionId)
        {
            SprayPaintHelpers.PlayerModifiers.Remove(connectionId);
        }
    }
}
