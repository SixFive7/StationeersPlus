using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace ClientDriver
{
    /// <summary>
    /// Synthetic keyboard, mouse-button and mouse-wheel state, injected at the
    /// <c>UnityEngine.Input</c> layer.
    ///
    /// Why here and not at <c>KeyManager</c>: every KeyManager query bottoms out in
    /// <c>Input.GetKey</c> / <c>GetKeyDown</c> / <c>GetKeyUp</c> (verified in the
    /// 0.2.6403.27689 decompile: <c>KeyManager.GetButton</c> is a console-open guard
    /// plus <c>Input.GetKey(key)</c>, and <c>GetMouse("Primary")</c> is
    /// <c>GetButton(KeyMap.PrimaryAction)</c> which defaults to
    /// <c>KeyCode.Mouse0</c>). A large amount of game code also calls
    /// <c>Input.GetKey(KeyMap.X)</c> directly, bypassing KeyManager entirely.
    /// Patching the Unity layer covers both, so "Shift is held" means the same thing
    /// to every consumer.
    ///
    /// Frame model: everything is expressed as an absolute <c>Time.frameCount</c>
    /// window, never as a countdown ticked from Update. MonoBehaviour Update order is
    /// not defined, so a countdown could expire before the frame's real consumer ran.
    /// A window scheduled to open on frame N+1 is visible for the whole of every
    /// frame in the window no matter what order things tick in.
    /// </summary>
    internal static class VirtualInput
    {
        private sealed class KeyWindow
        {
            public int StartFrame;   // first frame the key reads as down
            public int EndFrame;     // last frame the key reads as down
        }

        private static readonly object _gate = new object();
        private static readonly Dictionary<KeyCode, KeyWindow> _keys = new Dictionary<KeyCode, KeyWindow>();

        private static Vector2 _scroll;
        private static int _scrollStartFrame = -1;
        private static int _scrollEndFrame = -2;

        private static Vector3? _mousePositionOverride;

        internal static bool Enabled = true;

        internal static long KeyQueries;
        internal static long KeyOverrides;
        internal static long ScrollQueries;
        internal static long ScrollOverrides;

        // ---- scheduling (main thread) ----------------------------------------

        /// <summary>
        /// Schedules a key to read as held for <paramref name="frames"/> frames,
        /// starting on the next frame. Returns the last frame of the window.
        /// </summary>
        internal static int PressKey(KeyCode key, int frames)
        {
            if (frames < 1) frames = 1;
            int start = Time.frameCount + 1;
            int end = start + frames - 1;
            lock (_gate) { _keys[key] = new KeyWindow { StartFrame = start, EndFrame = end }; }
            return end;
        }

        /// <summary>Holds a key indefinitely until <see cref="ReleaseKey"/>.</summary>
        internal static int HoldKey(KeyCode key)
        {
            int start = Time.frameCount + 1;
            lock (_gate) { _keys[key] = new KeyWindow { StartFrame = start, EndFrame = int.MaxValue }; }
            return start;
        }

        /// <summary>Ends an indefinite hold. GetKeyUp reads true on the following frame.</summary>
        internal static int ReleaseKey(KeyCode key)
        {
            int end = Time.frameCount;
            lock (_gate)
            {
                KeyWindow w;
                if (_keys.TryGetValue(key, out w)) w.EndFrame = end;
                else _keys[key] = new KeyWindow { StartFrame = end, EndFrame = end };
            }
            return end;
        }

        internal static void ReleaseAll()
        {
            int end = Time.frameCount;
            lock (_gate)
            {
                foreach (var w in _keys.Values) if (w.EndFrame > end) w.EndFrame = end;
            }
        }

        /// <summary>
        /// Injects a mouse-wheel delta for <paramref name="frames"/> frames starting
        /// next frame. Unity reports the wheel in notches; the game divides by 10 into
        /// <c>InventoryManager.newScrollData</c>, so 1.0 here is one notch of the real
        /// wheel and lands as 0.1 in newScrollData, exactly like a physical scroll.
        /// </summary>
        internal static int Scroll(float notches, int frames)
        {
            if (frames < 1) frames = 1;
            lock (_gate)
            {
                _scroll = new Vector2(0f, notches);
                _scrollStartFrame = Time.frameCount + 1;
                _scrollEndFrame = _scrollStartFrame + frames - 1;
                return _scrollEndFrame;
            }
        }

        internal static void ClearScroll()
        {
            lock (_gate) { _scrollStartFrame = -1; _scrollEndFrame = -2; }
        }

        internal static void SetMousePosition(Vector3? position)
        {
            lock (_gate) { _mousePositionOverride = position; }
        }

        internal static void ClearAll()
        {
            lock (_gate)
            {
                _keys.Clear();
                _scrollStartFrame = -1;
                _scrollEndFrame = -2;
                _mousePositionOverride = null;
            }
        }

        // ---- queries (called from Harmony prefixes, main thread) -------------

        internal static bool TryGetKey(KeyCode key, out bool result)
        {
            result = false;
            if (!Enabled) return false;
            KeyQueries++;
            lock (_gate)
            {
                KeyWindow w;
                if (!_keys.TryGetValue(key, out w)) return false;
                int f = Time.frameCount;
                if (f < w.StartFrame || f > w.EndFrame) return false;
            }
            KeyOverrides++;
            result = true;
            return true;
        }

        internal static bool TryGetKeyDown(KeyCode key, out bool result)
        {
            result = false;
            if (!Enabled) return false;
            lock (_gate)
            {
                KeyWindow w;
                if (!_keys.TryGetValue(key, out w)) return false;
                if (Time.frameCount != w.StartFrame) return false;
            }
            KeyOverrides++;
            result = true;
            return true;
        }

        internal static bool TryGetKeyUp(KeyCode key, out bool result)
        {
            result = false;
            if (!Enabled) return false;
            lock (_gate)
            {
                KeyWindow w;
                if (!_keys.TryGetValue(key, out w)) return false;
                if (w.EndFrame == int.MaxValue) return false;
                if (Time.frameCount != w.EndFrame + 1) return false;
            }
            KeyOverrides++;
            result = true;
            return true;
        }

        internal static bool TryGetScroll(out Vector2 result)
        {
            result = Vector2.zero;
            if (!Enabled) return false;
            ScrollQueries++;
            lock (_gate)
            {
                int f = Time.frameCount;
                if (f < _scrollStartFrame || f > _scrollEndFrame) return false;
                result = _scroll;
            }
            ScrollOverrides++;
            return true;
        }

        internal static bool TryGetMousePosition(out Vector3 result)
        {
            result = Vector3.zero;
            if (!Enabled) return false;
            lock (_gate)
            {
                if (!_mousePositionOverride.HasValue) return false;
                result = _mousePositionOverride.Value;
            }
            return true;
        }

        internal static string DescribeHeld()
        {
            var parts = new List<string>();
            lock (_gate)
            {
                int f = Time.frameCount;
                foreach (var kv in _keys)
                {
                    if (f >= kv.Value.StartFrame && f <= kv.Value.EndFrame)
                        parts.Add(kv.Key.ToString());
                }
            }
            return string.Join(",", parts.ToArray());
        }

        // ---- key name resolution --------------------------------------------

        /// <summary>
        /// Resolves a key name to a KeyCode. Accepts a raw KeyCode name
        /// ("LeftShift", "Mouse0", "F3"), or a KeyMap action name ("PrimaryAction",
        /// "SwapHands", "ToggleConsole") which is resolved against the live, possibly
        /// rebound KeyMap field rather than a hardcoded default.
        /// </summary>
        internal static bool TryResolveKey(string name, out KeyCode key, out string how)
        {
            key = KeyCode.None;
            how = null;
            if (string.IsNullOrEmpty(name)) return false;

            try
            {
                key = (KeyCode)Enum.Parse(typeof(KeyCode), name, true);
                how = "KeyCode";
                return true;
            }
            catch { }

            var field = AccessTools.Field(typeof(KeyMap), name);
            if (field != null && field.FieldType == typeof(KeyCode))
            {
                key = (KeyCode)field.GetValue(null);
                how = "KeyMap." + field.Name;
                return true;
            }

            // Case-insensitive KeyMap field search.
            foreach (var f in typeof(KeyMap).GetFields(BindingFlags.Public | BindingFlags.Static))
            {
                if (f.FieldType != typeof(KeyCode)) continue;
                if (!string.Equals(f.Name, name, StringComparison.OrdinalIgnoreCase)) continue;
                key = (KeyCode)f.GetValue(null);
                how = "KeyMap." + f.Name;
                return true;
            }

            return false;
        }

        internal static List<string> ListKeyMapActions()
        {
            var list = new List<string>();
            foreach (var f in typeof(KeyMap).GetFields(BindingFlags.Public | BindingFlags.Static))
            {
                if (f.FieldType != typeof(KeyCode)) continue;
                KeyCode v;
                try { v = (KeyCode)f.GetValue(null); } catch { continue; }
                list.Add(f.Name + "=" + v);
            }
            list.Sort(StringComparer.OrdinalIgnoreCase);
            return list;
        }
    }

    // ---- Harmony patches on the Unity input layer ---------------------------
    //
    // All prefixes. Returning false skips the native call and hands back the
    // synthetic value; returning true lets the real hardware state through
    // untouched, which is what happens for every key the driver is not currently
    // holding. That keeps the developer's own keyboard working while the driver is
    // loaded.

    [HarmonyPatch(typeof(Input), nameof(Input.GetKey), new[] { typeof(KeyCode) })]
    internal static class InputGetKeyPatch
    {
        internal static bool Prepare() => Plugin.PatchUnityInputValue;

        internal static bool Prefix(KeyCode key, ref bool __result)
        {
            bool synthetic;
            if (!VirtualInput.TryGetKey(key, out synthetic)) return true;
            __result = synthetic;
            return false;
        }
    }

    [HarmonyPatch(typeof(Input), nameof(Input.GetKeyDown), new[] { typeof(KeyCode) })]
    internal static class InputGetKeyDownPatch
    {
        internal static bool Prepare() => Plugin.PatchUnityInputValue;

        internal static bool Prefix(KeyCode key, ref bool __result)
        {
            bool synthetic;
            if (!VirtualInput.TryGetKeyDown(key, out synthetic)) return true;
            __result = synthetic;
            return false;
        }
    }

    [HarmonyPatch(typeof(Input), nameof(Input.GetKeyUp), new[] { typeof(KeyCode) })]
    internal static class InputGetKeyUpPatch
    {
        internal static bool Prepare() => Plugin.PatchUnityInputValue;

        internal static bool Prefix(KeyCode key, ref bool __result)
        {
            bool synthetic;
            if (!VirtualInput.TryGetKeyUp(key, out synthetic)) return true;
            __result = synthetic;
            return false;
        }
    }

    /// <summary>
    /// Mouse buttons reach the game both as KeyCode.Mouse0/1 (which is how KeyMap
    /// defines PrimaryAction and SecondaryAction) and as Input.GetMouseButton(int).
    /// Bridge the int form onto the same KeyCode state so one press covers both.
    /// </summary>
    [HarmonyPatch(typeof(Input), nameof(Input.GetMouseButton), new[] { typeof(int) })]
    internal static class InputGetMouseButtonPatch
    {
        internal static bool Prepare() => Plugin.PatchUnityInputValue;

        internal static bool Prefix(int button, ref bool __result)
        {
            bool synthetic;
            if (!VirtualInput.TryGetKey(KeyCode.Mouse0 + button, out synthetic)) return true;
            __result = synthetic;
            return false;
        }
    }

    [HarmonyPatch(typeof(Input), nameof(Input.GetMouseButtonDown), new[] { typeof(int) })]
    internal static class InputGetMouseButtonDownPatch
    {
        internal static bool Prepare() => Plugin.PatchUnityInputValue;

        internal static bool Prefix(int button, ref bool __result)
        {
            bool synthetic;
            if (!VirtualInput.TryGetKeyDown(KeyCode.Mouse0 + button, out synthetic)) return true;
            __result = synthetic;
            return false;
        }
    }

    [HarmonyPatch(typeof(Input), nameof(Input.GetMouseButtonUp), new[] { typeof(int) })]
    internal static class InputGetMouseButtonUpPatch
    {
        internal static bool Prepare() => Plugin.PatchUnityInputValue;

        internal static bool Prefix(int button, ref bool __result)
        {
            bool synthetic;
            if (!VirtualInput.TryGetKeyUp(KeyCode.Mouse0 + button, out synthetic)) return true;
            __result = synthetic;
            return false;
        }
    }

    [HarmonyPatch(typeof(Input), "get_mouseScrollDelta")]
    internal static class InputScrollPatch
    {
        internal static bool Prepare() => Plugin.PatchUnityInputValue;

        internal static bool Prefix(ref Vector2 __result)
        {
            Vector2 synthetic;
            if (!VirtualInput.TryGetScroll(out synthetic)) return true;
            __result = synthetic;
            return false;
        }
    }

    [HarmonyPatch(typeof(Input), "get_mousePosition")]
    internal static class InputMousePositionPatch
    {
        internal static bool Prepare() => Plugin.PatchUnityInputValue;

        internal static bool Prefix(ref Vector3 __result)
        {
            Vector3 synthetic;
            if (!VirtualInput.TryGetMousePosition(out synthetic)) return true;
            __result = synthetic;
            return false;
        }
    }

    /// <summary>
    /// Belt and braces for the mouse wheel. <c>InventoryManager.CheckDisplaySlotInput</c>
    /// caches <c>Input.mouseScrollDelta.y / 10f</c> into the public field
    /// <c>newScrollData</c>, and that field is what SprayPaintPlus's ColorCyclerPatch
    /// reads. If the <c>get_mouseScrollDelta</c> prefix ever fails to apply (a Unity
    /// version where the property is a direct extern rather than a managed wrapper),
    /// this postfix still lands the value in the field the game actually consumes.
    /// Assignment, not accumulation, so having both paths work is harmless.
    /// </summary>
    [HarmonyPatch]
    internal static class ScrollDataBackstopPatch
    {
        internal static MethodBase TargetMethod()
        {
            return AccessTools.Method(typeof(Assets.Scripts.Inventory.InventoryManager), "CheckDisplaySlotInput");
        }

        internal static bool Prepare() => TargetMethod() != null;

        internal static void Postfix(Assets.Scripts.Inventory.InventoryManager __instance)
        {
            Vector2 synthetic;
            if (!VirtualInput.TryGetScroll(out synthetic)) return;
            __instance.newScrollData = synthetic.y / 10f;
        }
    }
}
