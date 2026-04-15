using Assets.Scripts.Objects.Electrical;
using HarmonyLib;
using System;
using System.Collections.Generic;
using System.Reflection;

namespace PowerTransmitterPlus
{
    // Teaches the IC10 / MIPS compiler to recognize our LogicType names.
    // The compiler resolves tokens like "MicrowaveSourceDraw" to a numeric
    // constant by scanning ProgrammableChip.AllConstants — a public static
    // Constant[] array where each Constant has (Literal, Description, Value).
    //
    // Pattern lifted from Stationeers Logic Extended (ThunderDuck): one-time
    // reflection write that appends our entries to the existing array. No
    // Harmony patch needed for this step — IC10 reads the array dynamically.
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
                    PowerTransmitterPlusPlugin.Log.LogError(
                        "ProgrammableChip.AllConstants not found — IC10 names disabled");
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

                PowerTransmitterPlusPlugin.Log.LogInfo(
                    $"Registered {additions.Count} IC10 constants ({existing.Length} -> {merged.Length})");
            }
            catch (Exception e)
            {
                PowerTransmitterPlusPlugin.Log.LogError($"Ic10ConstantsPatcher failed: {e}");
            }
        }
    }
}
