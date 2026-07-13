using System.Reflection;
using System.Text;
using Assets.Scripts;
using Assets.Scripts.Objects;
using Assets.Scripts.Objects.Electrical;
using HarmonyLib;
using LaunchPadBooster.Networking;
using UnityEngine;

namespace PowerGridPlus.Patches
{
    // Re-purposes the transformer's two screwdriver knob-step buttons (Button1 /
    // Button2) to control the new Priority value (default 100, step 10 per click
    // or 1 with Alt) and reskins the on-screen hover text.
    //
    // Vanilla InteractWith (Research/GameClasses/Transformer.md "InteractWith button
    // model and DelayedActionInstance state messages") writes Setting directly in
    // the Button1 / Button2 branches, gated on GameManager.RunSimulation
    // (host-only). The labeller / multi-tool path runs first via
    // HandleButtonSetting; if non-null, vanilla returns the labeller's result and
    // skips the switch.
    //
    // We mirror that early-out via reflection: when a labeller is held, return
    // true so vanilla handles it (LogicType.Setting writes coming back through
    // that path are routed to Priority by TransformerPriorityLogicPatches). For
    // Button1 / Button2 we drive Priority instead of Setting, broadcast the
    // change, and refresh the needle visual. For any other interactable
    // (InteractableType.OnOff), fall through to vanilla.
    //
    // The needle visual (SetKnob) lerps Setting / OutputMaximum. We patch SetKnob
    // to lerp Priority / NeedleFullScale so the in-world dial reflects priority.
    //
    // Multiplayer-safe: priority writes happen host-only; the host broadcasts via
    // PriorityMessage.
    [HarmonyPatch(typeof(Transformer))]
    public static class TransformerInteractWithPatches
    {
        // Priority -> needle rotation. Priority 0 pins at NeedleMinimum; Priority
        // 200 pins at NeedleMaximum. Default Priority = 100 sits at the midpoint
        // (0 deg) -- needle pointing straight up. Going below 100 deflects one
        // way ("less important"), above 100 deflects the other ("more important").
        // This matches user-spec "default 100 just so there is room above and
        // below. Let's not go negative." -- the visual room is symmetric.
        private const float NeedleFullScale = 200f;

        // Step sizes. Default click = +/- 10; Alt (interaction.AltKey) = +/- 1.
        private const int PriorityStepSmall = 1;
        private const int PriorityStepNormal = 10;

        private static readonly FieldInfo NeedleField =
            AccessTools.Field(typeof(Transformer), "Needle");
        private static readonly FieldInfo NeedleMinimumField =
            AccessTools.Field(typeof(Transformer), "NeedleMinimum");
        private static readonly FieldInfo NeedleMaximumField =
            AccessTools.Field(typeof(Transformer), "NeedleMaximum");
        // Vanilla SetKnob resets _needleTransform to _needleBaseRotation before
        // applying the rotation, so the prefab's mounted orientation isn't lost.
        // Without this reset our localRotation write wipes the base rotation and
        // the knob ends up tilted in the wrong plane (user-observed 90 deg
        // sideways). Both fields are private; resolve via AccessTools.
        private static readonly FieldInfo NeedleTransformField =
            AccessTools.Field(typeof(Transformer), "_needleTransform");
        private static readonly FieldInfo NeedleBaseRotationField =
            AccessTools.Field(typeof(Transformer), "_needleBaseRotation");
        private static readonly MethodInfo HandleButtonSettingMethod =
            AccessTools.Method(typeof(Transformer), "HandleButtonSetting");
        private static readonly MethodInfo SetKnobMethod =
            AccessTools.Method(typeof(Transformer), "SetKnob");

        // Thing.DelayedActionInstance is a nested class with a private
        // StringBuilder backing the state-message body. Reflecting on this
        // backing field is the only way to inject raw string lines into the
        // hover side-pop because vanilla AppendStateMessage only accepts a
        // GameString (Assets.Scripts.Localization2.GameString), and registering
        // new GameStrings at runtime is not supported. The reflected handle is
        // resolved once on type-load; if the game refactors the nested type
        // (rename or visibility change) the appends silently no-op rather than
        // crashing.
        private static readonly FieldInfo StateMessageBuilderField = ResolveStateMessageBuilderField();

        private static FieldInfo ResolveStateMessageBuilderField()
        {
            var daiType = AccessTools.TypeByName("Assets.Scripts.Objects.Thing+DelayedActionInstance")
                          ?? typeof(Thing.DelayedActionInstance);
            return AccessTools.Field(daiType, "_stateMessageBuilder");
        }

        private static void AppendRawStateMessage(Thing.DelayedActionInstance dai, string text)
        {
            if (dai == null || string.IsNullOrEmpty(text) || StateMessageBuilderField == null) return;
            if (StateMessageBuilderField.GetValue(dai) is StringBuilder sb)
            {
                sb.AppendLine(text);
            }
        }

        private static void AppendPriorityStateLines(Thing.DelayedActionInstance dai, Transformer transformer)
        {
            if (dai == null || transformer == null) return;
            int p = PriorityStore.GetPriority(transformer.ReferenceId);
            AppendRawStateMessage(dai, $"Priority <color=green>{p}</color>");
            AppendRawStateMessage(dai, $"Throughput <color=green>{(float)transformer.Setting:0} W</color> of {transformer.OutputMaximum:0} W (IC10 Setting)");
            if (BrownoutRegistry.IsShedding(transformer.ReferenceId, ElectricityTickCounter.CurrentTick))
            {
                AppendRawStateMessage(dai, "<color=#ffa500>Shedding: insufficient upstream supply this tick</color>");
            }
            AppendRawStateMessage(dai, "Hold <color=yellow>Alt</color> for fine adjustment");
        }

        [HarmonyPrefix, HarmonyPatch(nameof(Transformer.InteractWith))]
        public static bool InteractWithPatch(
            Transformer __instance,
            Interactable interactable,
            Interaction interaction,
            bool doAction,
            ref Thing.DelayedActionInstance __result)
        {
            if (interactable == null) return true;

            // Labeller / multi-tool path: vanilla checks first. If it would return
            // a non-null DelayedActionInstance (the labeller "Set value" panel is
            // up), let vanilla handle it. The LogicType.Setting write from the
            // labeller routes through SetLogicValue, which our priority logic
            // patches intercept and redirect to Priority.
            if (HandleButtonSettingMethod != null)
            {
                var labellerResult = HandleButtonSettingMethod.Invoke(__instance,
                    new object[] { interactable, interaction, doAction });
                if (labellerResult is Thing.DelayedActionInstance labellerDai && labellerDai != null)
                {
                    __result = labellerDai;
                    return false;
                }
            }

            bool isButton2 = interactable.Action == InteractableType.Button2;
            bool isButton1 = interactable.Action == InteractableType.Button1;
            if (!isButton1 && !isButton2) return true;

            // Host-side state change first so the post-write state-message lines
            // reflect the new priority.
            if (GameManager.RunSimulation && doAction)
            {
                int step = interaction.AltKey ? PriorityStepSmall : PriorityStepNormal;
                int current = PriorityStore.GetPriority(__instance.ReferenceId);
                int target = isButton2 ? current + step : current - step;
                if (target < 0) target = 0;
                if (target != current)
                {
                    PriorityStore.SetPriority(__instance, target);
                    new PriorityMessage { DeviceId = __instance.ReferenceId, Priority = target }.SendAll(0L);
                    SetKnobMethod?.Invoke(__instance, null);
                }
            }

            var dai = new Thing.DelayedActionInstance
            {
                Duration = 0f,
                ActionMessage = isButton2 ? "Increase Priority" : "Decrease Priority",
            };
            AppendPriorityStateLines(dai, __instance);

            __result = dai.Succeed();
            return false;
        }

        // Drive SetKnob during world load so the visual knob orientation
        // matches the saved Priority immediately. Vanilla Transformer's
        // OnFinishedLoad does not call SetKnob, so without this postfix the
        // knob stays at the prefab's default (0,0,0) rotation until the
        // player clicks the screwdriver buttons.
        [HarmonyPostfix, HarmonyPatch(typeof(Assets.Scripts.Objects.Thing), nameof(Assets.Scripts.Objects.Thing.OnFinishedLoad))]
        public static void OnFinishedLoadPostfix(Assets.Scripts.Objects.Thing __instance)
        {
            if (!(__instance is Transformer t)) return;
            try { SetKnobMethod?.Invoke(t, null); }
            catch (System.Exception e) { Plugin.Log?.LogWarning($"SetKnob on load failed for ref={t.ReferenceId}: {e.Message}"); }
            // Operator visibility: a saved Setting of 0 means this transformer delivers nothing
            // (Setting is the vanilla throughput cap again, POWER.md §5.3, and the in-world knob now
            // writes Priority, so the only in-game lever for Setting is IC10). Log it so a dark
            // subnet behind a zeroed transformer is explainable from the log.
            if (t.Setting <= 0.0)
                Plugin.Log?.LogInfo($"Transformer ref={t.ReferenceId} loaded with Setting = 0: it will deliver 0 W until an IC10 script raises Setting (the knob writes Priority).");
        }

        // Needle visual: lerp Priority -> [NeedleMinimum, NeedleMaximum] instead of
        // Setting -> [NeedleMinimum, NeedleMaximum] (see Research/GameClasses/Transformer.md
        // "SetKnob needle math"). Replaces vanilla SetKnob entirely.
        [HarmonyPrefix, HarmonyPatch("SetKnob")]
        public static bool SetKnobPatch(Transformer __instance)
        {
            if (__instance == null) return true;

            // Mirror vanilla SetKnob (decompile L403579-403597) exactly:
            //   _needleTransform.localRotation = _needleBaseRotation;
            //   _needleTransform.Rotate(0f, _needleRotation, 0f, Space.Self);
            // Reset to the prefab's mounted orientation first, then Rotate around
            // local Y. Doing `localRotation = Quaternion.Euler(0, angle, 0)` directly
            // wipes the base orientation and leaves the knob 90 degrees sideways.
            var needleTransform = NeedleTransformField?.GetValue(__instance) as Transform;
            if (needleTransform == null)
            {
                // Fallback to the public Needle GameObject's transform when the
                // private _needleTransform field isn't initialized yet (e.g. in
                // OnFinishedLoad before the prefab's Awake has assigned it).
                var needleGo = NeedleField?.GetValue(__instance) as GameObject;
                if (needleGo == null) return false;
                needleTransform = needleGo.transform;
            }

            float minDeg = NeedleMinimumField != null ? (float)NeedleMinimumField.GetValue(__instance) : -160f;
            float maxDeg = NeedleMaximumField != null ? (float)NeedleMaximumField.GetValue(__instance) : 160f;

            int priority = PriorityStore.GetPriority(__instance.ReferenceId);
            float t = Mathf.Clamp01(priority / NeedleFullScale);
            float angle = Mathf.Lerp(minDeg, maxDeg, t);

            if (NeedleBaseRotationField?.GetValue(__instance) is Quaternion baseRot)
            {
                needleTransform.localRotation = baseRot;
            }
            needleTransform.Rotate(0f, angle, 0f, Space.Self);
            return false;
        }
    }
}
