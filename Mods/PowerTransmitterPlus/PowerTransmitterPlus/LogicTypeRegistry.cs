using Assets.Scripts.Objects.Motherboards;
using StationeersPlus.Shared;
using System.Collections.Generic;

namespace PowerTransmitterPlus
{
    // LogicType ushort values come from the centralised SixFive7 catalogue at
    // Patterns/Logic/LogicTypeNumbers.cs (table + reservation rules in
    // Patterns/Logic/README.md). Linked into this csproj as
    // Patterns/LogicTypeNumbers.cs. Do not redeclare integer literals here; do not
    // pick a new number without first updating the central catalogue.
    internal static class LogicTypeRegistry
    {
        internal const ushort SourceDrawValue = LogicTypeNumbers.MicrowaveSourceDraw;
        internal const ushort DestinationDrawValue = LogicTypeNumbers.MicrowaveDestinationDraw;
        internal const ushort TransmissionLossValue = LogicTypeNumbers.MicrowaveTransmissionLoss;
        internal const ushort EfficiencyValue = LogicTypeNumbers.MicrowaveEfficiency;
        internal const ushort AutoAimTargetValue = LogicTypeNumbers.MicrowaveAutoAimTarget;
        internal const ushort LinkedPartnerValue = LogicTypeNumbers.MicrowaveLinkedPartner;

        internal static readonly LogicType MicrowaveSourceDraw = (LogicType)SourceDrawValue;
        internal static readonly LogicType MicrowaveDestinationDraw = (LogicType)DestinationDrawValue;
        internal static readonly LogicType MicrowaveTransmissionLoss = (LogicType)TransmissionLossValue;
        internal static readonly LogicType MicrowaveEfficiency = (LogicType)EfficiencyValue;
        internal static readonly LogicType MicrowaveAutoAimTarget = (LogicType)AutoAimTargetValue;
        internal static readonly LogicType MicrowaveLinkedPartner = (LogicType)LinkedPartnerValue;

        internal class CustomLogicType
        {
            public string Name;
            public ushort Value;
            public string Description;

            public LogicType AsLogicType => (LogicType)Value;
        }

        // Order is preserved for tablet display. Built lazily on first access so
        // PowerTransmitterPlusPlugin.AutoAimPatched (captured at boot in Awake)
        // is already set by the time we decide whether to include
        // MicrowaveAutoAimTarget. Filtering here cascades to every consumer that
        // iterates this list (IC10 constants, tablet UI arrays, ScreenDropdownBase,
        // EnumCollections, Stationpedia) and to ByValue / IsCustom / TryGetName
        // lookups, so the auto-aim surface disappears cleanly when the host has
        // EnableAutoAim = false at process start.
        internal static readonly List<CustomLogicType> All = BuildAll();

        private static List<CustomLogicType> BuildAll()
        {
            var list = new List<CustomLogicType>
            {
                new CustomLogicType
                {
                    Name = "MicrowaveSourceDraw",
                    Value = SourceDrawValue,
                    Description = "Watts the microwave power transmitter is pulling from its source cable network. Equals delivered power times the per-kilometer cost multiplier (server-authoritative; clients see the host's configured value).",
                },
                new CustomLogicType
                {
                    Name = "MicrowaveDestinationDraw",
                    Value = DestinationDrawValue,
                    Description = "Watts being delivered to the receiver's downstream cable network. Equals the wireless link's actual throughput. Mirrors PowerActual but is named for clarity in the microwave context.",
                },
                new CustomLogicType
                {
                    Name = "MicrowaveTransmissionLoss",
                    Value = TransmissionLossValue,
                    Description = "Watts lost to distance overhead. Equals MicrowaveSourceDraw minus MicrowaveDestinationDraw. Zero when the link is at zero distance (or when k=0).",
                },
                new CustomLogicType
                {
                    Name = "MicrowaveEfficiency",
                    Value = EfficiencyValue,
                    Description = "Ratio of delivered power to source draw, 0..1. Equals 1/(1 + k * distance_km). 1.0 at zero distance or k=0; drops toward 0 as distance grows. Returns 0 when no transmission is happening.",
                },
                new CustomLogicType
                {
                    Name = "MicrowaveAutoAimTarget",
                    Value = AutoAimTargetValue,
                    Description = "Writable. Set to a Thing's ReferenceId to aim the dish at that thing; the dish slews via its built-in servo and the base-game line-of-sight link raycast decides when the pairing actually forms. Set to 0 to disable auto-aim. Writing an invalid or unresolved id is a no-op. Manually adjusting Horizontal or Vertical cancels auto-aim. Reading returns the current target id, or 0 when auto-aim is disabled.",
                },
                new CustomLogicType
                {
                    Name = "MicrowaveLinkedPartner",
                    Value = LinkedPartnerValue,
                    Description = "Read-only. Returns the ReferenceId of the currently linked partner dish: on a transmitter this is the linked receiver, on a receiver this is the linked transmitter. Returns 0 when unlinked.",
                },
            };
            if (!PowerTransmitterPlusPlugin.AutoAimPatched)
                list.RemoveAll(t => t.Value == AutoAimTargetValue);
            return list;
        }

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
