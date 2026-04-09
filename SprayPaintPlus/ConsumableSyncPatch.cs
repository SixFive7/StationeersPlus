using Assets.Scripts.Networking;
using Assets.Scripts.Objects;
using Assets.Scripts.Objects.Items;
using HarmonyLib;
using JetBrains.Annotations;
using Objects.Items; // Consumable lives here, SprayCan in Assets.Scripts.Objects.Items — both needed

namespace SprayPaintPlus
{
    // No try-catch around BuildUpdate/ProcessUpdate read/write operations.
    // If WriteInt32 succeeds but ReadInt32 fails (or vice versa), catching
    // the exception would leave the RocketBinaryReader/Writer at the wrong
    // position, silently corrupting ALL subsequent data for that object.
    // Letting the exception propagate is safer — the game's own error
    // handling can reset the connection.
    //
    // The code inside each Postfix is a single WriteInt32/ReadInt32 call
    // behind two guard checks, so the chance of an exception here is
    // effectively zero under normal conditions.

    [HarmonyPatch(typeof(Consumable), nameof(Consumable.BuildUpdate))]
    public class ConsumableBuildUpdatePatch
    {
        [UsedImplicitly]
        public static void Postfix(Consumable __instance, RocketBinaryWriter writer, ushort networkUpdateType)
        {
            if (!(__instance is SprayCan sprayCan))
                return;
            if (!Thing.IsNetworkUpdateRequired(SprayPaintHelpers.PaintColorNetworkFlag, networkUpdateType))
                return;

            writer.WriteInt32(SprayPaintHelpers.GetSprayCanColorIndex(sprayCan));
        }
    }

    [HarmonyPatch(typeof(Consumable), nameof(Consumable.ProcessUpdate))]
    public class ConsumableProcessUpdatePatch
    {
        [UsedImplicitly]
        public static void Postfix(Consumable __instance, RocketBinaryReader reader, ushort networkUpdateType)
        {
            if (!(__instance is SprayCan sprayCan))
                return;
            if (!Thing.IsNetworkUpdateRequired(SprayPaintHelpers.PaintColorNetworkFlag, networkUpdateType))
                return;

            int colorIndex = reader.ReadInt32();
            SprayPaintHelpers.UpdateSprayCanVisual(sprayCan, colorIndex);
        }
    }

    [HarmonyPatch(typeof(Consumable), nameof(Consumable.SerializeOnJoin))]
    public class ConsumableSerializeOnJoinPatch
    {
        [UsedImplicitly]
        public static void Postfix(Consumable __instance, RocketBinaryWriter writer)
        {
            if (!(__instance is SprayCan sprayCan))
                return;

            writer.WriteInt32(SprayPaintHelpers.GetSprayCanColorIndex(sprayCan));
        }
    }

    [HarmonyPatch(typeof(Consumable), nameof(Consumable.DeserializeOnJoin))]
    public class ConsumableDeserializeOnJoinPatch
    {
        [UsedImplicitly]
        public static void Postfix(Consumable __instance, RocketBinaryReader reader)
        {
            if (!(__instance is SprayCan sprayCan))
                return;

            int colorIndex = reader.ReadInt32();
            SprayPaintHelpers.UpdateSprayCanVisual(sprayCan, colorIndex);
        }
    }
}
