using Assets.Scripts.Objects;
using Assets.Scripts.Objects.Electrical;
using Assets.Scripts.Objects.Items;
using Assets.Scripts.Objects.Motherboards;
using HarmonyLib;

namespace PowerGridPlus.Patches
{
    // Redirects the Labeller's "Set value" input panel from LogicType.Setting
    // to LogicType.Priority when the target is a Transformer and the Priority +
    // Shedding feature is enabled.
    //
    // Vanilla flow (decompile L329782 + L403408-403440):
    //   Transformer.HandleButtonSetting -> labeller.Set(this)  // default logicType = Setting
    //   Labeller.Set(ISetable, LogicType=Setting) opens InputWindow with current
    //                                              value = setable.GetLogicValue(Setting)
    //   InputWindow.OnSubmit -> Labeller.InputSetting(input, setable, logicType)
    //                          which writes via SetLogicFromClient (id 37) or
    //                          direct SetLogicValue (server).
    //
    // Without this patch, the input box shows the result of our GetLogicValue
    // rewire (OutputMaximum, e.g. "5000") instead of the actual Priority value,
    // and the submitted value goes through SetLogicFromClient with LogicType.Setting,
    // which our SetLogicValue prefix DOES correctly redirect to Priority -- but
    // the user-visible default in the box is misleading.
    //
    // Fix: prefix Labeller.Set with `ref LogicType logicType` and swap Setting ->
    // Priority when the target is a Transformer. The OnSubmit delegate captures
    // the (post-swap) logicType, so the entire flow runs against Priority cleanly.
    [HarmonyPatch(typeof(Labeller))]
    public static class TransformerLabellerPatches
    {
        [HarmonyPrefix, HarmonyPatch(nameof(Labeller.Set))]
        public static void Set_Prefix(ISetable setable, ref LogicType logicType)
        {
            if (!ShedSettingsSync.Effective) return;
            if (setable is Transformer && logicType == LogicType.Setting)
            {
                logicType = LogicTypeRegistry.Priority;
            }
        }

        [HarmonyPrefix, HarmonyPatch(nameof(Labeller.InputSetting))]
        public static void InputSetting_Prefix(ISetable settable, ref LogicType logicType)
        {
            if (!ShedSettingsSync.Effective) return;
            if (settable is Transformer && logicType == LogicType.Setting)
            {
                logicType = LogicTypeRegistry.Priority;
            }
        }
    }
}
