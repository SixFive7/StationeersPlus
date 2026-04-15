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

                PowerTransmitterPlusPlugin.Log.LogInfo(
                    $"Injected {additions.Count} entries into Logicable arrays");
            }
            catch (Exception e)
            {
                PowerTransmitterPlusPlugin.Log.LogError($"LogicableInitializePatch failed: {e}");
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
