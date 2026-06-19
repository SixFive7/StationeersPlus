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

        internal const ushort OverloadedValue = LogicTypeNumbers.Overloaded;
        internal static readonly LogicType Overloaded = (LogicType)OverloadedValue;

        internal const ushort CycleFaultValue = LogicTypeNumbers.CycleFault;
        internal static readonly LogicType CycleFault = (LogicType)CycleFaultValue;

        internal const ushort VariableVoltageFaultValue = LogicTypeNumbers.VariableVoltageFault;
        internal static readonly LogicType VariableVoltageFault = (LogicType)VariableVoltageFaultValue;

        internal const ushort MaxChargeSpeedValue = LogicTypeNumbers.MaxChargeSpeed;
        internal static readonly LogicType MaxChargeSpeed = (LogicType)MaxChargeSpeedValue;

        internal const ushort MaxDischargeSpeedValue = LogicTypeNumbers.MaxDischargeSpeed;
        internal static readonly LogicType MaxDischargeSpeed = (LogicType)MaxDischargeSpeedValue;

        internal const ushort ChargeSpeedValue = LogicTypeNumbers.ChargeSpeed;
        internal static readonly LogicType ChargeSpeed = (LogicType)ChargeSpeedValue;

        internal const ushort DischargeSpeedValue = LogicTypeNumbers.DischargeSpeed;
        internal static readonly LogicType DischargeSpeed = (LogicType)DischargeSpeedValue;

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
                Description = "Writable. 0 = the device breaks the logic network the same way it breaks the power network (vanilla). 1 = the device is logic-transparent: IC10 / logic readers on any of its cable connections see devices on its other connections (power input, power output, and a dedicated data port), and the device's own logic ports are visible from all of them. Applies to transformers, stationary batteries, Area Power Controllers, and linked power transmitter / receiver dishes. Persists across save / load. Default 1 on stationary batteries, Area Power Controllers, transmitter / receiver dishes, and the small transformer, its reversed variant, and the rocket small transformer; default 0 on every other transformer.",
            },
            new CustomLogicType
            {
                Name = "Priority",
                Value = PriorityValue,
                Description = "Writable. Per-transformer dispatch priority (non-negative integer, default 100). When transformers compete for limited supply on the same input cable network, the highest-priority transformer gets first dibs; the leftover goes to the next priority. A transformer that cannot get its share of the input network sheds (turns off, flashes its on/off button orange, surfaces a hover error) for 60 seconds, then re-engages automatically. The in-world knob and the Labeller write Priority; IC10 Setting writes are redirected here too. Persists across save / load.",
            },
            new CustomLogicType
            {
                Name = "Shedding",
                Value = SheddingValue,
                Description = "Read-only. Returns 1 when the transformer is currently in shed lockout (upstream-side protection: input cable network cannot supply the transformer's share), 0 otherwise. Server-derived; the value clears automatically when the 60-second lockout window elapses. Read this from an IC10 chip or logic reader to drive downstream alerts (siren, LED) when a low-priority sub-network drops.",
            },
            new CustomLogicType
            {
                Name = "Overloaded",
                Value = OverloadedValue,
                Description = "Read-only. Returns 1 when the transformer is currently in overload lockout (downstream-side protection: output cable network demands more than the transformer can deliver), 0 otherwise. Server-derived; the value clears automatically when the 60-second lockout window elapses. Read this from an IC10 chip or logic reader to drive downstream alerts when a sub-network is dropped due to over-current.",
            },
            new CustomLogicType
            {
                Name = "CycleFault",
                Value = CycleFaultValue,
                Description = "Read-only. Returns 1 while this device is in cycle-fault lockout: it is part of a closed power loop (a cable / wireless ring through transformers, batteries, APCs, or transmitter / receiver pairs). Every segmenting device on the loop is faulted so the loop dissolves; no cable is burned. Auto-clears after 60 seconds, then re-checks; toggling the device off and on clears it instantly. Server-derived; replicated to clients.",
            },
            new CustomLogicType
            {
                Name = "VariableVoltageFault",
                Value = VariableVoltageFaultValue,
                Description = "Read-only. Returns 1 while this power producer is in variable-voltage-fault lockout: it is wired to a rigid consumer without a transformer in between. Power producers must connect to a transformer (or only to other producers). Auto-clears after 60 seconds. Producers without an on/off button (solar panels, wind turbines, RTGs) cannot be faulted this way; their cable is burned instead. Server-derived; replicated to clients.",
            },
            new CustomLogicType
            {
                Name = "MaxChargeSpeed",
                Value = MaxChargeSpeedValue,
                Description = "Read-only. The configured per-prefab charge-rate cap in Watts for this battery, Area Power Controller, or rocket umbilical. The upper bound on how fast the device's internal cell can charge from its input network.",
            },
            new CustomLogicType
            {
                Name = "MaxDischargeSpeed",
                Value = MaxDischargeSpeedValue,
                Description = "Read-only. The configured per-prefab discharge-rate cap in Watts for this battery, Area Power Controller, or rocket umbilical. The upper bound on how fast the device's internal cell can discharge to its output network.",
            },
            new CustomLogicType
            {
                Name = "ChargeSpeed",
                Value = ChargeSpeedValue,
                Description = "Read-only. The ACTUAL charge rate this tick in Watts, after Power Grid Plus allocates the input network's surplus. Lower than MaxChargeSpeed when supply is tight or other devices compete for the same surplus; this is by design (the elastic soft-power system). 0 when not charging.",
            },
            new CustomLogicType
            {
                Name = "DischargeSpeed",
                Value = DischargeSpeedValue,
                Description = "Read-only. The ACTUAL discharge rate this tick in Watts, after Power Grid Plus allocates the output network's shortfall. Stays at 0 while generators or upstream transformers cover downstream demand; approaches MaxDischargeSpeed when this device alone must cover the shortfall. This is by design (the elastic soft-power system).",
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
