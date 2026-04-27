using Assets.Scripts.Networking;
using Assets.Scripts.Objects;
using Assets.Scripts.Objects.Items;
using HarmonyLib;
using JetBrains.Annotations;
using System.Reflection;

namespace EquipmentPlus
{
    internal static class SensorLensesSync
    {
        // Custom NetworkUpdateFlag (16-bit). Values up to 0x0800 are used by
        // vanilla Thing/DynamicThing/Item. 0x4000 is unused and safe for our use.
        internal const ushort ActiveSensorFlag = 0x4000;
    }

    // NOTE ON __instance TYPING:
    // When TargetMethod returns an inherited MethodInfo (i.e. the method is not
    // declared on SensorLenses itself), Harmony patches the base class's method
    // body and the Postfix fires for *every* call on any instance of that base.
    // Declaring __instance as SensorLenses causes Harmony to emit a castclass
    // to SensorLenses; when the actual instance is any other Thing subclass the
    // cast throws InvalidCastException, which surfaces during load as a crash.
    // Using Thing as the declared type avoids the cast; we filter with `is`.

    [HarmonyPatch]
    public class SensorLensesBuildUpdatePatch
    {
        [UsedImplicitly]
        static MethodBase TargetMethod() =>
            typeof(SensorLenses).GetMethod("BuildUpdate",
                BindingFlags.Public | BindingFlags.Instance);

        [UsedImplicitly]
        public static void Postfix(Thing __instance,
            RocketBinaryWriter writer, ushort networkUpdateType)
        {
            if (!(__instance is SensorLenses lenses)) return;
            if (!Thing.IsNetworkUpdateRequired(SensorLensesSync.ActiveSensorFlag, networkUpdateType))
                return;
            writer.WriteInt64(lenses.Sensor?.ReferenceId ?? 0L);
        }
    }

    [HarmonyPatch]
    public class SensorLensesProcessUpdatePatch
    {
        [UsedImplicitly]
        static MethodBase TargetMethod() =>
            typeof(SensorLenses).GetMethod("ProcessUpdate",
                BindingFlags.Public | BindingFlags.Instance);

        [UsedImplicitly]
        public static void Postfix(Thing __instance,
            RocketBinaryReader reader, ushort networkUpdateType)
        {
            if (!(__instance is SensorLenses lenses)) return;
            if (!Thing.IsNetworkUpdateRequired(SensorLensesSync.ActiveSensorFlag, networkUpdateType))
                return;
            long refId = reader.ReadInt64();
            lenses.Sensor = refId == 0L
                ? null
                : Referencable.Find<SensorProcessingUnit>(refId);
        }
    }

    [HarmonyPatch]
    public class SensorLensesSerializeOnJoinPatch
    {
        [UsedImplicitly]
        static MethodBase TargetMethod() =>
            typeof(SensorLenses).GetMethod("SerializeOnJoin",
                BindingFlags.Public | BindingFlags.Instance);

        [UsedImplicitly]
        public static void Postfix(Thing __instance, RocketBinaryWriter writer)
        {
            if (!(__instance is SensorLenses lenses)) return;
            writer.WriteInt64(lenses.Sensor?.ReferenceId ?? 0L);
        }
    }

    [HarmonyPatch]
    public class SensorLensesDeserializeOnJoinPatch
    {
        [UsedImplicitly]
        static MethodBase TargetMethod() =>
            typeof(SensorLenses).GetMethod("DeserializeOnJoin",
                BindingFlags.Public | BindingFlags.Instance);

        [UsedImplicitly]
        public static void Postfix(Thing __instance, RocketBinaryReader reader)
        {
            if (!(__instance is SensorLenses lenses)) return;
            long refId = reader.ReadInt64();
            if (refId == 0L)
            {
                lenses.Sensor = null;
                return;
            }
            var target = Referencable.Find<SensorProcessingUnit>(refId);
            if (target != null)
                lenses.Sensor = target;
        }
    }
}
