using System.Globalization;
using Assets.Scripts.Networking;
using Assets.Scripts.Objects;
using Assets.Scripts.Objects.Electrical;
using Assets.Scripts.Objects.Motherboards; // LogicType lives here, not in Objects/Electrical
using Cysharp.Threading.Tasks;
using HarmonyLib;
using UnityEngine;

namespace KeypadModFix
{
    // Runtime fixes for KeypadMod (keypadmod.Keypad). The mod's type is resolved by name; if it
    // is absent or its shape changed, each patch is skipped with a log line instead of throwing,
    // so this plugin can never break a game that does not have a compatible KeypadMod installed.
    //
    // Nothing here clones or redistributes KeypadMod. The two prefixes below skip the broken
    // original methods and run corrected reimplementations, compiled against the CURRENT game
    // assemblies, against the live KeypadMod instance.
    //
    // Background: see Research/Patterns/UniTaskDelaySignatureDrift.md.
    internal static class KeypadPatches
    {
        // LogicType.Mode == 3: the channel KeypadMod pulses during the keypress animation.
        // LogicType.Setting == 12: the keypad's writable "memory" value (the one it exposes for
        // logic read/write). Both verified against game 0.2.6228.27061. Referenced by numeric value
        // to match KeypadMod's compiled IL exactly and to stay robust if the enum names ever move.
        private const LogicType ModeLogic = (LogicType)3;
        private const LogicType SettingLogic = (LogicType)12;

        // KeypadMod recomputes this from the same string; we recompute it too rather than reading
        // its private static field, so there is no extra reflection dependency.
        private static readonly int ConfirmSoundHash = Animator.StringToHash("LabelConfirm");

        // Bound once in Apply() against the resolved keypadmod.Keypad type.
        private static AccessTools.FieldRef<object, bool> _interactionLockedRef;
        private static AccessTools.FieldRef<object, bool> _initializedRef;

        public static void Apply(Harmony harmony)
        {
            var keypadType = AccessTools.TypeByName("keypadmod.Keypad");
            if (keypadType == null)
            {
                Plugin.Log.LogInfo("KeypadMod (keypadmod.Keypad) is not installed; KeypadMod Fix is inactive.");
                return;
            }

            int applied = 0;

            // --- Fix 1: the UniTask.Delay crash on keypress -----------------------------------------
            var pulseMode = AccessTools.Method(keypadType, "PulseMode");
            var lockField = AccessTools.Field(keypadType, "_interactionLocked");
            var initField = AccessTools.Field(keypadType, "_initialized");
            if (pulseMode != null && lockField != null && initField != null)
            {
                _interactionLockedRef = AccessTools.FieldRefAccess<object, bool>(lockField);
                _initializedRef = AccessTools.FieldRefAccess<object, bool>(initField);
                harmony.Patch(pulseMode,
                    prefix: new HarmonyMethod(typeof(KeypadPatches), nameof(PulseModePrefix)));
                applied++;
            }
            else
            {
                Plugin.Log.LogWarning(
                    "keypadmod.Keypad.PulseMode (or its _interactionLocked/_initialized fields) could not be " +
                    "resolved; the keypress-crash fix is inactive. KeypadMod may have changed.");
            }

            // --- Fix 2: multiplayer/dedicated-server screen input never applies ---------------------
            var processInputValue = AccessTools.Method(keypadType, "ProcessInputValue",
                new[] { typeof(string), typeof(string) });
            if (processInputValue != null)
            {
                harmony.Patch(processInputValue,
                    prefix: new HarmonyMethod(typeof(KeypadPatches), nameof(ProcessInputValuePrefix)));
                applied++;
            }
            else
            {
                Plugin.Log.LogWarning(
                    "keypadmod.Keypad.ProcessInputValue could not be resolved; the multiplayer screen-input " +
                    "fix is inactive. KeypadMod may have changed.");
            }

            Plugin.Log.LogInfo($"KeypadMod Fix active: {applied} of 2 patch(es) applied to keypadmod.Keypad.");
        }

        // --- Fix 1 implementation -------------------------------------------------------------------
        // KeypadMod.PulseMode() is an async UniTaskVoid whose compiled state machine calls the old
        // four-argument UniTask.Delay(int, bool, PlayerLoopTiming, CancellationToken). The current game
        // UniTask added a fifth optional parameter, so that exact call no longer exists and throws
        // MissingMethodException the moment a number button is pressed. We skip the broken original and
        // run the identical pulse here, recompiled against the current UniTask so the Delay call binds.
        private static bool PulseModePrefix(LogicUnitBase __instance)
        {
            RunPulse(__instance).Forget();
            return false; // skip the broken original; it returns a no-op default UniTaskVoid
        }

        private static async UniTaskVoid RunPulse(LogicUnitBase keypad)
        {
            // Same guard as the original: only pulse when idle, powered, and initialized.
            if (_interactionLockedRef(keypad) || !((Thing)keypad).Powered || !_initializedRef(keypad))
                return;

            _interactionLockedRef(keypad) = true;
            keypad.SetLogicValue(ModeLogic, 1.0);
            await UniTask.Delay(550, false, PlayerLoopTiming.Update,
                ((Component)keypad).GetCancellationTokenOnDestroy());
            keypad.SetLogicValue(ModeLogic, 0.0);
            await UniTask.Delay(200, false, PlayerLoopTiming.Update,
                ((Component)keypad).GetCancellationTokenOnDestroy());
            _interactionLockedRef(keypad) = false;
        }

        // --- Fix 2 implementation -------------------------------------------------------------------
        // KeypadMod.ProcessInputValue sends a SetLogicFromClient without setting its LogicType, so it
        // defaults to LogicType.None. The server gates on CanLogicWrite(None), which the keypad does not
        // allow, so the value is dropped and a multiplayer-client / dedicated-server keypad keeps showing
        // its old value. We resend with LogicType.Setting (the channel the keypad actually allows writing),
        // matching the value the host path already sets directly. Single-player / host was unaffected.
        private static bool ProcessInputValuePrefix(LogicUnitBase __instance, string input)
        {
            if (__instance == null)
                return false;
            if (!double.TryParse(input, NumberStyles.Float, CultureInfo.CurrentCulture, out double result))
                return false; // original applies nothing for an unparseable value

            if (double.IsPositiveInfinity(result))
                result = double.MaxValue;

            if (NetworkManager.IsClient)
            {
                var message = new SetLogicFromClient
                {
                    LogicId = ((Thing)__instance).NetworkId,
                    LogicType = SettingLogic, // the fix: KeypadMod left this at None, so the server rejected it
                    Value = result,
                };
                message.SendToServer(); // public, inherited from MessageBase<T>; no cast needed
            }
            else
            {
                __instance.Setting = result;
            }

            ((Thing)__instance).PlaySound(ConfirmSoundHash, 1f, 1f);
            return false; // skip the original (which omits LogicType)
        }
    }
}
