using Assets.Scripts.Networking;
using Assets.Scripts.Objects;
using HarmonyLib;
using JetBrains.Annotations;

namespace SprayPaintPlus
{
    // Network sync for per-Thing glow state. Piggybacks on bit 13
    // (GenericFlag3, 0x2000) of Thing.NetworkUpdateFlags; see
    // Research/GameSystems/NetworkUpdateFlags.md. The server raises the bit
    // when SetGlow changes a Thing's state; vanilla's network tick fires
    // Thing.BuildUpdate, our postfix writes one byte, and the corresponding
    // ProcessUpdate postfix on each client reads and re-applies.
    //
    // SerializeOnJoin and DeserializeOnJoin unconditionally write and read
    // the byte so late joiners receive the state of every Thing without
    // relying on NetworkUpdateFlags timing at join time.
    //
    // No try-catch around the binary read / write, per
    // Research/Patterns/BinaryStreamSafety.md: a swallowed exception would
    // leave the RocketBinaryReader at an unknown offset, corrupting every
    // subsequent field.

    [HarmonyPatch(typeof(Thing), nameof(Thing.BuildUpdate))]
    public class ThingBuildUpdateGlowPatch
    {
        [UsedImplicitly]
        public static void Postfix(Thing __instance, RocketBinaryWriter writer, ushort networkUpdateType)
        {
            if (!Thing.IsNetworkUpdateRequired(GlowPaintHelpers.GlowNetworkFlag, networkUpdateType))
                return;
            writer.WriteByte((byte)(GlowPaintHelpers.IsGlowing(__instance) ? 1 : 0));
        }
    }

    [HarmonyPatch(typeof(Thing), nameof(Thing.ProcessUpdate))]
    public class ThingProcessUpdateGlowPatch
    {
        [UsedImplicitly]
        public static void Postfix(Thing __instance, RocketBinaryReader reader, ushort networkUpdateType)
        {
            if (!Thing.IsNetworkUpdateRequired(GlowPaintHelpers.GlowNetworkFlag, networkUpdateType))
                return;
            bool glowing = reader.ReadByte() != 0;
            ThingDeserializeOnJoinGlowPatch.ApplyGlowFromSync(__instance, glowing);
        }
    }

    [HarmonyPatch(typeof(Thing), nameof(Thing.SerializeOnJoin))]
    public class ThingSerializeOnJoinGlowPatch
    {
        [UsedImplicitly]
        public static void Postfix(Thing __instance, RocketBinaryWriter writer)
        {
            writer.WriteByte((byte)(GlowPaintHelpers.IsGlowing(__instance) ? 1 : 0));
        }
    }

    [HarmonyPatch(typeof(Thing), nameof(Thing.DeserializeOnJoin))]
    public class ThingDeserializeOnJoinGlowPatch
    {
        [UsedImplicitly]
        public static void Postfix(Thing __instance, RocketBinaryReader reader)
        {
            bool glowing = reader.ReadByte() != 0;
            ApplyGlowFromSync(__instance, glowing);
        }

        internal static void ApplyGlowFromSync(Thing thing, bool glowing)
        {
            if (thing == null) return;
            GlowPaintHelpers.SetGlow(thing, glowing);
            if (thing.CustomColor == null) return;
            // Re-apply in both directions so the renderer state matches the
            // incoming sync even if the vanilla pipeline already ran a
            // SetCustomColor(..., false) earlier this tick.
            GlowPaintHelpers.ReapplyEmissive(thing, glowing);
        }
    }

    // Shared ApplyGlowFromSync exposed via ThingDeserializeOnJoinGlowPatch
    // (needs to be accessible from the ProcessUpdate patch above).
    internal static class GlowSyncShared
    {
        public static void ApplyGlowFromSync(Thing thing, bool glowing)
            => ThingDeserializeOnJoinGlowPatch.ApplyGlowFromSync(thing, glowing);
    }
}
