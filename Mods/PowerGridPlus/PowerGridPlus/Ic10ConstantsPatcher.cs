using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using Assets.Scripts.Objects.Electrical;
using Assets.Scripts.Objects.Motherboards;

namespace PowerGridPlus
{
    // Teaches the IC10 / MIPS compiler to recognize our LogicType names so player
    // scripts can write `s d0 LogicPassthroughMode 1` instead of a raw integer.
    // Also extends the syntax-highlighting entries in ProgrammableChip.InternalEnums.
    //
    // Pattern lifted from PowerTransmitterPlus's Ic10ConstantsPatcher (which lifted
    // it from Stationeers Logic Extended). Idempotent via _applied flag.
    internal static class Ic10ConstantsPatcher
    {
        private static bool _applied;

        internal static void Apply()
        {
            if (_applied) return;
            _applied = true;

            try
            {
                var field = typeof(ProgrammableChip).GetField("AllConstants",
                    BindingFlags.Static | BindingFlags.Public);
                if (field == null)
                {
                    Plugin.Log?.LogError("ProgrammableChip.AllConstants not found, IC10 names disabled");
                    return;
                }

                var existing = (ProgrammableChip.Constant[])field.GetValue(null);
                var additions = new List<ProgrammableChip.Constant>();
                foreach (var t in LogicTypeRegistry.All)
                {
                    additions.Add(new ProgrammableChip.Constant(t.Name, t.Description, (double)t.Value, true));
                }

                var merged = new ProgrammableChip.Constant[existing.Length + additions.Count];
                existing.CopyTo(merged, 0);
                for (int i = 0; i < additions.Count; i++)
                    merged[existing.Length + i] = additions[i];

                field.SetValue(null, merged);

                Plugin.Log?.LogInfo($"Registered {additions.Count} IC10 constants ({existing.Length} -> {merged.Length})");
            }
            catch (Exception e)
            {
                Plugin.Log?.LogError($"Ic10ConstantsPatcher failed: {e}");
            }

            ExtendSyntaxHighlighting();
        }

        private static void ExtendSyntaxHighlighting()
        {
            try
            {
                var enumsField = typeof(ProgrammableChip).GetField("InternalEnums",
                    BindingFlags.Static | BindingFlags.Public);
                if (enumsField == null)
                {
                    Plugin.Log?.LogWarning("ProgrammableChip.InternalEnums not found, screen syntax highlighting won't cover custom types");
                    return;
                }

                var list = (IList)enumsField.GetValue(null);
                int patched = 0;
                foreach (var entry in list)
                {
                    var entryType = entry.GetType();
                    if (!entryType.IsGenericType) continue;
                    var genericArgs = entryType.GetGenericArguments();
                    if (genericArgs.Length != 1 || genericArgs[0] != typeof(LogicType)) continue;
                    bool isBasicEnum = entryType.Name.StartsWith("BasicEnum");
                    ExtendScriptEnumEntry(entry, entryType, isBasicEnum);
                    patched++;
                }
                if (patched > 0)
                    Plugin.Log?.LogInfo($"Extended {patched} InternalEnums entries for syntax highlighting");
            }
            catch (Exception e)
            {
                Plugin.Log?.LogError($"ExtendSyntaxHighlighting failed: {e}");
            }
        }

        private static void ExtendScriptEnumEntry(object entry, Type entryType, bool isBasicEnum)
        {
            var typesField = entryType.GetField("_types",
                BindingFlags.Instance | BindingFlags.NonPublic);
            var namesField = entryType.GetField("_names",
                BindingFlags.Instance | BindingFlags.NonPublic);
            if (typesField == null || namesField == null) return;

            var oldTypes = (LogicType[])typesField.GetValue(entry);
            var oldNames = (string[])namesField.GetValue(entry);
            var additions = LogicTypeRegistry.All;

            int n = oldTypes.Length + additions.Count;
            var newTypes = new LogicType[n];
            var newNames = new string[n];
            Array.Copy(oldTypes, newTypes, oldTypes.Length);
            Array.Copy(oldNames, newNames, oldNames.Length);

            for (int i = 0; i < additions.Count; i++)
            {
                int idx = oldTypes.Length + i;
                newTypes[idx] = additions[i].AsLogicType;
                newNames[idx] = isBasicEnum
                    ? "LogicType." + additions[i].Name
                    : additions[i].Name;
            }

            typesField.SetValue(entry, newTypes);
            namesField.SetValue(entry, newNames);
        }
    }
}
