using System.Collections.Generic;
using Assets.Scripts.Objects.Motherboards;
using StationeersPlus.Shared;

namespace PowerGridPlus
{
    // LogicType ushort values come from the centralised SixFive7 catalogue at
    // Patterns/Logic/LogicTypeNumbers.cs (table + reservation rules in
    // Patterns/Logic/README.md). Linked into this csproj as
    // Patterns/LogicTypeNumbers.cs. Do not redeclare integer literals here; do not
    // pick a new number without first updating the central catalogue.
    internal static class LogicTypeRegistry
    {
        internal const ushort LogicPassthroughModeValue = LogicTypeNumbers.LogicPassthroughMode;
        internal static readonly LogicType LogicPassthroughMode = (LogicType)LogicPassthroughModeValue;

        internal const ushort PriorityValue = LogicTypeNumbers.Priority;
        internal static readonly LogicType Priority = (LogicType)PriorityValue;

        internal const ushort SheddingValue = LogicTypeNumbers.Shedding;
        internal static readonly LogicType Shedding = (LogicType)SheddingValue;

        internal class CustomLogicType
        {
            public string Name;
            public ushort Value;
            public string Description;
            public LogicType AsLogicType => (LogicType)Value;
        }

        internal static readonly List<CustomLogicType> All = new List<CustomLogicType>
        {
            new CustomLogicType
            {
                Name = "LogicPassthroughMode",
                Value = LogicPassthroughModeValue,
                Description = "Writable. 0 = the device breaks the logic network the same way it breaks the power network (vanilla). 1 = the device is logic-transparent: IC10 / logic readers on one side see devices on the other side, and the device's own logic ports are visible from both. Applies to transformers, stationary batteries, Area Power Controllers, and linked power transmitter / receiver dishes. Persists across save / load. Default 1 on stationary batteries, Area Power Controllers, transmitter / receiver dishes, and the small transformer and its reversed variant; default 0 on every other transformer.",
            },
            new CustomLogicType
            {
                Name = "Priority",
                Value = PriorityValue,
                Description = "Writable. Per-transformer dispatch priority (non-negative integer, default 100). Strict-priority allocation: when transformers compete for limited supply on the same input cable network, the highest-priority transformer gets first dibs up to its OutputMaximum; the leftover goes to the next priority. A transformer that cannot get its full OutputMaximum from the input network sheds (turns off, flashes its on/off button, surfaces a hover error) for 10 seconds, then re-engages automatically. While Power Grid Plus's transformer shedding feature is on, Setting writes are redirected here so legacy IC10 scripts that wrote to Setting now write to Priority. Persists across save / load.",
            },
            new CustomLogicType
            {
                Name = "Shedding",
                Value = SheddingValue,
                Description = "Read-only. Returns 1 when the transformer is currently shed (browned out by the strict-priority allocation), 0 otherwise. Server-derived; the value clears automatically when the 10-second lockout window elapses. Read this from an IC10 chip or logic reader to drive downstream alerts (siren, LED) when a low-priority sub-network drops.",
            },
        };

        internal static readonly Dictionary<ushort, CustomLogicType> ByValue = BuildIndex();

        private static Dictionary<ushort, CustomLogicType> BuildIndex()
        {
            var d = new Dictionary<ushort, CustomLogicType>();
            foreach (var t in All) d[t.Value] = t;
            return d;
        }

        internal static bool IsCustom(LogicType t) => ByValue.ContainsKey((ushort)t);

        internal static bool TryGetName(LogicType t, out string name)
        {
            if (ByValue.TryGetValue((ushort)t, out var info))
            {
                name = info.Name;
                return true;
            }
            name = null;
            return false;
        }
    }
}
