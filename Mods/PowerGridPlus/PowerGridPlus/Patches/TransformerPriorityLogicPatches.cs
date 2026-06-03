using Assets.Scripts.Networking;
using Assets.Scripts.Objects;
using Assets.Scripts.Objects.Electrical;
using Assets.Scripts.Objects.Motherboards;
using HarmonyLib;
using LaunchPadBooster.Networking;

namespace PowerGridPlus.Patches
{
    // Wires the writable LogicType.Priority slot, the read-only LogicType.Shedding
    // slot, and rewires LogicType.Setting + LogicType.Ratio on Transformer.
    //
    // Behaviour while ShedSettingsSync.Effective is true:
    //   - CanLogicRead:   Priority, Shedding => true.
    //   - CanLogicWrite:  Priority => true. Shedding => false (read-only).
    //   - GetLogicValue:  Setting => OutputMaximum (throughput is hardcoded at max).
    //                     Ratio => 1.0 (the dial is no longer a fractional cap).
    //                     Maximum => OutputMaximum (unchanged from vanilla, just
    //                       happens to coincide with the new throughput meaning).
    //                     Priority => PriorityStore.GetPriority(__instance).
    //                     Shedding => BrownoutRegistry.IsShedding ? 1 : 0.
    //   - SetLogicValue:  Setting writes are server-gated; on the server, they
    //                     redirect to Priority (legacy IC10 scripts that wrote to
    //                     Setting now write to Priority transparently).
    //                     Priority writes go through PriorityStore + broadcast
    //                     PriorityMessage to clients.
    //                     Shedding writes are silently dropped (read-only slot).
    //
    // Behaviour while ShedSettingsSync.Effective is false: all of the above
    // short-circuit to vanilla; the existing TransformerExploitPatches /
    // TransformerLogicPatches paths cover the vanilla case.
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
            if (!ShedSettingsSync.Effective) return true;
            if (logicType == LogicTypeRegistry.Priority || logicType == LogicTypeRegistry.Shedding)
            {
                __result = true;
                return false;
            }
            return true;
        }

        [HarmonyPrefix, HarmonyPatch(nameof(Transformer.CanLogicWrite))]
        public static bool CanLogicWritePatch(LogicType logicType, ref bool __result)
        {
            if (!ShedSettingsSync.Effective) return true;
            if (logicType == LogicTypeRegistry.Priority)
            {
                __result = true;
                return false;
            }
            if (logicType == LogicTypeRegistry.Shedding)
            {
                __result = false;     // read-only
                return false;
            }
            return true;
        }

        [HarmonyPrefix, HarmonyPatch(nameof(Transformer.GetLogicValue))]
        public static bool GetLogicValuePatch(Transformer __instance, LogicType logicType, ref double __result)
        {
            if (!ShedSettingsSync.Effective) return true;

            if (logicType == LogicType.Setting)
            {
                // Throughput is hardcoded at OutputMaximum; surface that to IC10
                // readers so existing scripts that read Setting expecting "the
                // throughput cap" still get a sensible answer.
                __result = __instance.OutputMaximum;
                return false;
            }
            if (logicType == LogicType.Ratio)
            {
                // Setting / OutputMaximum is meaningless when Setting is hardcoded
                // to OutputMaximum; return 1.0 instead of 1.0 derived from the
                // identity (saves IC10 scripts from a divide-by-zero on a freshly
                // placed transformer with the vanilla _outputSetting = 0).
                __result = 1.0;
                return false;
            }
            if (logicType == LogicTypeRegistry.Priority)
            {
                __result = PriorityStore.GetPriority(__instance.ReferenceId);
                return false;
            }
            if (logicType == LogicTypeRegistry.Shedding)
            {
                __result = BrownoutRegistry.IsShedding(__instance.ReferenceId, ElectricityTickCounter.CurrentTick)
                    ? 1.0 : 0.0;
                return false;
            }
            return true;
        }

        [HarmonyPrefix, HarmonyPatch(nameof(Transformer.SetLogicValue))]
        public static bool SetLogicValuePatch(Transformer __instance, LogicType logicType, double value)
        {
            if (!ShedSettingsSync.Effective) return true;

            // Setting writes redirect to Priority.
            if (logicType == LogicType.Setting)
            {
                if (!NetworkManager.IsServer) return false;
                WritePriority(__instance, value);
                return false;
            }

            if (logicType == LogicTypeRegistry.Priority)
            {
                if (!NetworkManager.IsServer) return false;
                WritePriority(__instance, value);
                return false;
            }

            // Shedding is read-only; silently swallow any write.
            if (logicType == LogicTypeRegistry.Shedding)
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
