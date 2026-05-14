using System.Collections.Generic;
using Assets.Scripts.Objects.Motherboards;

namespace PowerGridPlus
{
    // 6577 is the next compact slot in the SixFive7 LogicType catalogue (6571 onward,
    // PowerTransmitterPlus owns 6571-6576). The catalogue is centralised under
    // Patterns/Logic/ at the repo root; do not pick a new value here without checking
    // there first.
    internal static class LogicTypeRegistry
    {
        internal const ushort LogicPassthroughModeValue = 6577;
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
