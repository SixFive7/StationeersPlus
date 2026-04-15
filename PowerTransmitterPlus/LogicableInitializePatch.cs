using Assets.Scripts.Objects.Pipes;
using Assets.Scripts.Objects.Motherboards;
using HarmonyLib;
using JetBrains.Annotations;
using System;
using System.Collections.Generic;
using System.Reflection;

namespace PowerTransmitterPlus
{
    // Appends our custom LogicType values + names into the static arrays the
    // configuration tablet UI uses to populate its dropdowns. Logicable holds
    // parallel LogicType[] / string[] arrays plus a redirect index for binary
    // search lookup; we extend all three.
    //
    // Pattern lifted from Stationeers Logic Extended (ThunderDuck).
    [HarmonyPatch(typeof(Logicable), "Initialize")]
    public static class LogicableInitializePatch
    {
        private static bool _injected;

        [UsedImplicitly]
        public static void Postfix()
        {
            if (_injected) return;
            _injected = true;

            try
            {
                var typesField = AccessTools.Field(typeof(Logicable), "LogicTypes");
                var namesField = AccessTools.Field(typeof(Logicable), "LogicTypeNames");
                if (typesField == null || namesField == null)
                {
                    PowerTransmitterPlusPlugin.Log.LogWarning(
                        "Logicable.LogicTypes / LogicTypeNames not found — tablet dropdown disabled");
                    return;
                }

                var existingTypes = (LogicType[])typesField.GetValue(null);
                var existingNames = (string[])namesField.GetValue(null);

                var additions = LogicTypeRegistry.All;
                var newTypes = new LogicType[existingTypes.Length + additions.Count];
                var newNames = new string[existingNames.Length + additions.Count];
                Array.Copy(existingTypes, newTypes, existingTypes.Length);
                Array.Copy(existingNames, newNames, existingNames.Length);
                for (int i = 0; i < additions.Count; i++)
                {
                    newTypes[existingTypes.Length + i] = additions[i].AsLogicType;
                    newNames[existingTypes.Length + i] = additions[i].Name;
                }
                typesField.SetValue(null, newTypes);
                namesField.SetValue(null, newNames);

                // Some game versions maintain a binary-search redirect array
                // (LogicTypeNamesRedirects). Best-effort rebuild if present;
                // otherwise the tablet will fall back to linear scans.
                TryRebuildRedirects(newNames);

                // ConfigCartridge (and other tablet UI paths) iterate
                // EnumCollections.LogicTypes instead of Logicable.LogicTypes.
                // That collection wraps Enum.GetValues, so our custom values
                // are invisible to the tablet dropdown unless we also extend
                // its Values / ValuesAsInts / Names / PaddedNames / Length.
                ExtendEnumCollection(additions);

                PowerTransmitterPlusPlugin.Log.LogInfo(
                    $"Injected {additions.Count} entries into Logicable arrays");
            }
            catch (Exception e)
            {
                PowerTransmitterPlusPlugin.Log.LogError($"LogicableInitializePatch failed: {e}");
            }
        }

        private static void ExtendEnumCollection(List<LogicTypeRegistry.CustomLogicType> additions)
        {
            try
            {
                var enumCollectionsType = AccessTools.TypeByName("Assets.Scripts.EnumCollections");
                var logicTypesField = enumCollectionsType?.GetField("LogicTypes",
                    BindingFlags.Static | BindingFlags.Public);
                var collection = logicTypesField?.GetValue(null);
                if (collection == null)
                {
                    PowerTransmitterPlusPlugin.Log.LogWarning(
                        "EnumCollections.LogicTypes not found — tablet dropdown will not list custom entries");
                    return;
                }

                var collectionType = collection.GetType();
                var valuesField = collectionType.GetField("Values");
                var valuesAsIntsField = collectionType.GetField("ValuesAsInts");
                var namesField = collectionType.GetField("Names");
                var paddedField = collectionType.GetField("PaddedNames");
                var lengthBacking = collectionType.GetField("<Length>k__BackingField",
                    BindingFlags.Instance | BindingFlags.NonPublic);

                var oldValues = (LogicType[])valuesField.GetValue(collection);
                var oldInts = (ushort[])valuesAsIntsField.GetValue(collection);
                var oldNames = (string[])namesField.GetValue(collection);
                var oldPadded = (string[])paddedField.GetValue(collection);

                int n = oldValues.Length + additions.Count;
                var newValues = new LogicType[n];
                var newInts = new ushort[n];
                var newNames = new string[n];
                var newPadded = new string[n];
                Array.Copy(oldValues, newValues, oldValues.Length);
                Array.Copy(oldInts, newInts, oldInts.Length);
                Array.Copy(oldNames, newNames, oldNames.Length);
                Array.Copy(oldPadded, newPadded, oldPadded.Length);

                for (int i = 0; i < additions.Count; i++)
                {
                    int idx = oldValues.Length + i;
                    newValues[idx] = additions[i].AsLogicType;
                    newInts[idx] = additions[i].Value;
                    newNames[idx] = additions[i].Name;
                    newPadded[idx] = additions[i].Name;
                }

                valuesField.SetValue(collection, newValues);
                valuesAsIntsField.SetValue(collection, newInts);
                namesField.SetValue(collection, newNames);
                paddedField.SetValue(collection, newPadded);
                lengthBacking?.SetValue(collection, n);

                PowerTransmitterPlusPlugin.Log.LogInfo(
                    $"Extended EnumCollections.LogicTypes ({oldValues.Length} -> {n})");
            }
            catch (Exception e)
            {
                PowerTransmitterPlusPlugin.Log.LogError($"ExtendEnumCollection failed: {e}");
            }
        }

        private static void TryRebuildRedirects(string[] names)
        {
            var redirField = AccessTools.Field(typeof(Logicable), "LogicTypeNamesRedirects");
            if (redirField == null) return;

            var indices = new int[names.Length];
            for (int i = 0; i < indices.Length; i++) indices[i] = i;
            Array.Sort(indices, (a, b) =>
                string.Compare(names[a], names[b], StringComparison.Ordinal));
            redirField.SetValue(null, indices);
        }
    }
}
