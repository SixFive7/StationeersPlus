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
                Description = "Writable. 0 = transformer breaks the logic network the same way it breaks the power network (vanilla). 1 = transformer is logic-transparent: IC10 / logic readers on one side see devices on the other side, and the transformer's own logic ports are visible from both. Persists across save / load. Default 1 on the small transformer and its reversed variant; default 0 on every other transformer.",
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
