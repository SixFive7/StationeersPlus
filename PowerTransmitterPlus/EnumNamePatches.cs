using Assets.Scripts;
using Assets.Scripts.Objects.Motherboards;
using HarmonyLib;
using JetBrains.Annotations;
using System;

namespace PowerTransmitterPlus
{
    // The configuration tablet and various UI paths look up the display name
    // for a given LogicType value via Enum.GetName(...) and the game's own
    // EnumCollection<LogicType, ushort>. Both return null for our 6571+ values
    // because the underlying enum has no metadata for them. These postfixes
    // substitute our names from the registry when the lookup would otherwise
    // come up empty.
    //
    // Pattern lifted from Stationeers Logic Extended (ThunderDuck).

    [HarmonyPatch(typeof(Enum), nameof(Enum.GetName), new Type[] { typeof(Type), typeof(object) })]
    public static class EnumGetNamePatch
    {
        [UsedImplicitly]
        public static void Postfix(Type enumType, object value, ref string __result)
        {
            if (__result != null) return;
            if (enumType != typeof(LogicType)) return;
            if (value == null) return;
            try
            {
                var t = (LogicType)Convert.ToUInt16(value);
                if (LogicTypeRegistry.TryGetName(t, out var name))
                    __result = name;
            }
            catch
            {
                // Non-LogicType-valued cast — ignore silently.
            }
        }
    }

    [HarmonyPatch(typeof(EnumCollection<LogicType, ushort>), "GetName")]
    public static class EnumCollectionGetNamePatch
    {
        [UsedImplicitly]
        public static void Postfix(LogicType value, ref string __result)
        {
            if (!string.IsNullOrEmpty(__result)) return;
            if (LogicTypeRegistry.TryGetName(value, out var name))
                __result = name;
        }
    }

    [HarmonyPatch(typeof(EnumCollection<LogicType, ushort>), "GetNameFromValue")]
    public static class EnumCollectionGetNameFromValuePatch
    {
        [UsedImplicitly]
        public static void Postfix(ushort value, ref string __result)
        {
            if (!string.IsNullOrEmpty(__result)) return;
            if (LogicTypeRegistry.TryGetName((LogicType)value, out var name))
                __result = name;
        }
    }
}
