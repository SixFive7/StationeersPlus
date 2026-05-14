using System;
using System.Collections.Generic;
using System.Reflection;
using Assets.Scripts.Objects.Motherboards;
using Assets.Scripts.Objects.Pipes;
using HarmonyLib;

namespace PowerGridPlus.Patches
{
    // Append our custom LogicType values + names into the static arrays the
    // configuration tablet UI uses to populate its dropdowns. Logicable holds
    // parallel LogicType[] / string[] arrays plus a redirect index for binary
    // search lookup; we extend all three. Idempotent (_injected) so subsequent
    // Logicable.Initialize calls are no-ops.
    //
    // Pattern lifted from PowerTransmitterPlus's LogicableInitializePatch.
    [HarmonyPatch(typeof(Logicable), "Initialize")]
    public static class LogicableInitializePatch
    {
        private static bool _injected;

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
                    Plugin.Log?.LogWarning("Logicable.LogicTypes / LogicTypeNames not found, tablet dropdown disabled");
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

                TryRebuildRedirects(newNames);
                ExtendEnumCollection(additions);
                ExtendScreenDropdownBase(additions);

                Plugin.Log?.LogInfo($"Injected {additions.Count} entries into Logicable arrays");
            }
            catch (Exception e)
            {
                Plugin.Log?.LogError($"LogicableInitializePatch failed: {e}");
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
                    Plugin.Log?.LogWarning("EnumCollections.LogicTypes not found, tablet dropdown will not list custom entries");
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
            }
            catch (Exception e)
            {
                Plugin.Log?.LogError($"ExtendEnumCollection failed: {e}");
            }
        }

        private static void ExtendScreenDropdownBase(List<LogicTypeRegistry.CustomLogicType> additions)
        {
            try
            {
                var sdbType = AccessTools.TypeByName("Assets.Scripts.UI.Motherboard.ScreenDropdownBase");
                if (sdbType == null) return;

                var typesField = sdbType.GetField("LogicTypes",
                    BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
                var namesField = sdbType.GetField("LogicTypeNames",
                    BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
                if (typesField == null || namesField == null) return;

                var oldTypes = (LogicType[])typesField.GetValue(null);
                var oldNames = (string[])namesField.GetValue(null);

                int n = oldTypes.Length + additions.Count;
                var newTypes = new LogicType[n];
                var newNames = new string[n];
                Array.Copy(oldTypes, newTypes, oldTypes.Length);
                Array.Copy(oldNames, newNames, oldNames.Length);

                for (int i = 0; i < additions.Count; i++)
                {
                    int idx = oldTypes.Length + i;
                    newTypes[idx] = additions[i].AsLogicType;
                    newNames[idx] = additions[i].Name;
                }

                typesField.SetValue(null, newTypes);
                namesField.SetValue(null, newNames);
            }
            catch (Exception e)
            {
                Plugin.Log?.LogError($"ExtendScreenDropdownBase failed: {e}");
            }
        }

        private static void TryRebuildRedirects(string[] names)
        {
            var redirField = AccessTools.Field(typeof(Logicable), "LogicTypeNamesRedirects");
            if (redirField == null) return;

            var indices = new int[names.Length];
            for (int i = 0; i < indices.Length; i++) indices[i] = i;
            Array.Sort(indices, (a, b) => string.Compare(names[a], names[b], StringComparison.Ordinal));
            redirField.SetValue(null, indices);
        }
    }
}
