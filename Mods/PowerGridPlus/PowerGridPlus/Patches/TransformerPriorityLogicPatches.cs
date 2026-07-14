using Assets.Scripts.Networking;
using Assets.Scripts.Objects;
using Assets.Scripts.Objects.Electrical;
using Assets.Scripts.Objects.Motherboards;
using HarmonyLib;
using LaunchPadBooster.Networking;

namespace PowerGridPlus.Patches
{
    // Wires the writable LogicType.Priority slot and the read-only fault slots
    // (DeprioritizedFault / DeviceOverloadedFault / CableOverloadedFault /
    // CycleFault) on Transformer. All five slots are always active (the Priority +
    // Deprioritization system and overload protection have no toggles).
    //
    // LogicType.Setting and LogicType.Ratio are PURE VANILLA (POWER.md §5.3 /
    // §17.36): IC10 reads return the live Setting; IC10 writes update Setting,
    // clamped [0, OutputMaximum] by the vanilla property. No redirect. Only the
    // in-world knob (TransformerInteractWithPatches) and the Labeller
    // (TransformerLabellerPatches) write Priority instead of Setting.
    //
    // Slot behaviour:
    //   - CanLogicRead:   Priority, DeprioritizedFault, DeviceOverloadedFault,
    //                     CableOverloadedFault, CycleFault => true.
    //   - CanLogicWrite:  Priority => true. DeprioritizedFault / DeviceOverloadedFault /
    //                     CableOverloadedFault / CycleFault => false (read-only).
    //   - GetLogicValue:  Priority => PriorityStore.GetPriority(__instance).
    //                     DeprioritizedFault / DeviceOverloadedFault /
    //                     CableOverloadedFault / CycleFault => 1 while the
    //                     transformer is in the matching lockout, 0 otherwise.
    //                     DeviceOverloadedFault covers only the capacity lockout
    //                     (OverloadRegistry); CableOverloadedFault covers only
    //                     the cable-overflow lockout (CableOverloadRegistry).
    //   - SetLogicValue:  Priority writes go through PriorityStore + broadcast
    //                     PriorityMessage to clients (clamped >= 0, no upper cap).
    //                     Writes to the read-only slots are silently dropped.
    //
    // Trap avoidance (HarmonyLogicableInheritedMethodTrap): Transformer overrides
    // CanLogicRead / CanLogicWrite / GetLogicValue / SetLogicValue directly, so
    // attribute-style [HarmonyPatch(typeof(Transformer), ...)] attaches cleanly.
    [HarmonyPatch(typeof(Transformer))]
    public static class TransformerPriorityLogicPatches
    {
        [HarmonyPrefix, HarmonyPatch(nameof(Transformer.CanLogicRead))]
        public static bool CanLogicReadPatch(LogicType logicType, ref bool __result)
        {
            if (logicType == LogicTypeRegistry.Priority
                || logicType == LogicTypeRegistry.DeprioritizedFault
                || logicType == LogicTypeRegistry.DeviceOverloadedFault
                || logicType == LogicTypeRegistry.CableOverloadedFault
                || logicType == LogicTypeRegistry.CycleFault)
            {
                __result = true;
                return false;
            }
            return true;
        }

        [HarmonyPrefix, HarmonyPatch(nameof(Transformer.CanLogicWrite))]
        public static bool CanLogicWritePatch(LogicType logicType, ref bool __result)
        {
            if (logicType == LogicTypeRegistry.Priority)
            {
                __result = true;
                return false;
            }
            if (logicType == LogicTypeRegistry.DeprioritizedFault
                || logicType == LogicTypeRegistry.DeviceOverloadedFault
                || logicType == LogicTypeRegistry.CableOverloadedFault
                || logicType == LogicTypeRegistry.CycleFault)
            {
                __result = false;     // read-only
                return false;
            }
            return true;
        }

        [HarmonyPrefix, HarmonyPatch(nameof(Transformer.GetLogicValue))]
        public static bool GetLogicValuePatch(Transformer __instance, LogicType logicType, ref double __result)
        {
            if (logicType == LogicTypeRegistry.Priority)
            {
                __result = PriorityStore.GetPriority(__instance.ReferenceId);
                return false;
            }
            if (logicType == LogicTypeRegistry.DeprioritizedFault)
            {
                __result = DeprioritizedRegistry.IsDeprioritized(__instance.ReferenceId, ElectricityTickCounter.CurrentTick)
                    ? 1.0 : 0.0;
                return false;
            }
            if (logicType == LogicTypeRegistry.DeviceOverloadedFault)
            {
                __result = OverloadRegistry.IsOverloaded(__instance.ReferenceId, ElectricityTickCounter.CurrentTick)
                    ? 1.0 : 0.0;
                return false;
            }
            if (logicType == LogicTypeRegistry.CableOverloadedFault)
            {
                __result = CableOverloadRegistry.IsCableOverloaded(__instance.ReferenceId, ElectricityTickCounter.CurrentTick)
                    ? 1.0 : 0.0;
                return false;
            }
            if (logicType == LogicTypeRegistry.CycleFault)
            {
                __result = CycleFaultRegistry.IsCycleFaulted(__instance.ReferenceId, ElectricityTickCounter.CurrentTick)
                    ? 1.0 : 0.0;
                return false;
            }
            return true;
        }

        [HarmonyPrefix, HarmonyPatch(nameof(Transformer.SetLogicValue))]
        public static bool SetLogicValuePatch(Transformer __instance, LogicType logicType, double value)
        {
            // LogicType.Setting writes are NOT intercepted: vanilla Transformer.SetLogicValue
            // applies them to Setting with its own [0, OutputMaximum] clamp (POWER.md §5.3).

            if (logicType == LogicTypeRegistry.Priority)
            {
                if (!NetworkManager.IsServer) return false;
                WritePriority(__instance, value);
                return false;
            }

            // DeprioritizedFault / DeviceOverloadedFault / CableOverloadedFault /
            // CycleFault are read-only; silently swallow any write.
            if (logicType == LogicTypeRegistry.DeprioritizedFault
                || logicType == LogicTypeRegistry.DeviceOverloadedFault
                || logicType == LogicTypeRegistry.CableOverloadedFault
                || logicType == LogicTypeRegistry.CycleFault)
                return false;

            return true;
        }

        // Server-side priority write. Quantizes to int, clamps to >= 0, stores,
        // and broadcasts to clients if the value actually changed.
        internal static void WritePriority(Transformer transformer, double value)
        {
            if (transformer == null) return;
            int newPriority = (int)System.Math.Round(value);
            if (newPriority < 0) newPriority = 0;

            int oldPriority = PriorityStore.GetPriority(transformer.ReferenceId);
            if (oldPriority == newPriority) return;
            PriorityStore.SetPriority(transformer, newPriority);

            new PriorityMessage
            {
                DeviceId = transformer.ReferenceId,
                Priority = newPriority,
            }.SendAll(0L);
        }
    }
}
