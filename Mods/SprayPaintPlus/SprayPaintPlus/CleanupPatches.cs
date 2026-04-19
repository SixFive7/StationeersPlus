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
    /// Cleans up PlayerModifiers dictionary when a client disconnects.
    /// Runs as a Prefix because NetworkServer.ClientDisconnected calls
    /// NetworkBase.RemoveClient before returning, making the Client record
    /// unreachable to a Postfix. We look up the disconnecting client's
    /// registered Human and remove the modifiers entry keyed by its ReferenceId.
    /// </summary>
    [HarmonyPatch(typeof(NetworkServer), nameof(NetworkServer.ClientDisconnected))]
    public class ClientDisconnectCleanupPatch
    {
        [UsedImplicitly]
        public static void Prefix(long connectionId)
        {
            Client client = Client.Find(connectionId);
            Human human = client?.RegisteredHuman;
            if (human != null)
                SprayPaintHelpers.PlayerModifiers.Remove(human.ReferenceId);
        }
    }
}
