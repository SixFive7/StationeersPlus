using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using Assets.Scripts.Inventory;
using HarmonyLib;
using UnityEngine;

namespace ClientDriver
{
    /// <summary>
    ///     Input routes.
    ///
    ///     THE RULE THIS FILE EXISTS TO ENFORCE: an input endpoint answers with what the GAME did,
    ///     not with what the driver did.
    ///
    ///     The previous shape could only report the second. <c>POST /input/key</c> answered
    ///     <c>{"ok":true,"resolvedVia":"KeyMap.SwapHands","settled":true}</c> for a keypress that
    ///     never happened, because <c>settled</c> only ever meant "the frames we asked for elapsed",
    ///     which is true whether or not anything read the key. An entire acceptance test was built
    ///     on that answer and came out confidently wrong, and unpicking it cost a session. A driver
    ///     that lies about input is worse than a driver with no input at all, because every result
    ///     downstream of it is plausible and false.
    ///
    ///     So each endpoint here reports three separate facts and does not conflate them:
    ///
    ///       <list type="bullet">
    ///         <item><c>delivered</c>: something in the game actually READ the synthetic value.
    ///               Counted at the moment the value is handed back to a caller, so a non-zero
    ///               count is evidence of delivery rather than of intent.</item>
    ///         <item><c>gate</c>: whether the per-frame consumer was even running, with the chain
    ///               counters that prove it either way.</item>
    ///         <item><c>consumed</c>: both of the above. THIS is the field to assert on.</item>
    ///       </list>
    ///
    ///     <c>settled</c> survives with its old meaning and the response says what that meaning is,
    ///     so nothing silently reinterprets it.
    ///
    ///     And the default is strict: <c>requireConsumed</c> defaults to TRUE, so an endpoint whose
    ///     input the game did not consume answers 409 rather than 200. A caller that does nothing
    ///     special cannot get a success for input that did not happen, which is the whole defect
    ///     turned into a default. Pass <c>requireConsumed=false</c> for genuinely fire-and-forget
    ///     input, such as a key nothing polls at the current phase.
    /// </summary>
    internal static partial class Router
    {
        private static HttpResponse InputKey(IDictionary body)
        {
            string keyName = Json.GetStr(body, "key");
            if (string.IsNullOrEmpty(keyName)) return HttpResponse.Error("missing 'key'", 400);

            KeyCode key;
            string how;
            if (!VirtualInput.TryResolveKey(keyName, out key, out how))
                return HttpResponse.Error("unknown key '" + keyName + "'. GET /input/keymap lists the action names.", 400);

            string mode = (Json.GetStr(body, "mode", "tap") ?? "tap").ToLowerInvariant();
            int frames = Json.GetInt(body, "frames", 3);
            bool wait = Json.GetBool(body, "wait", true);
            bool requireConsumed = Json.GetBool(body, "requireConsumed", true);

            // Baselines for the read-back, taken before anything is scheduled.
            long inventoryBefore = ChainProbe.Enters(ChainProbe.InventoryUpdate);
            long normalModeBefore = ChainProbe.Enters(ChainProbe.NormalMode);
            long keyPollBefore = ChainProbe.Enters(ChainProbe.KeyMapPoll);

            int endFrame = 0;
            var scheduled = Main(() =>
            {
                VirtualInput.ResetObservation(key);
                switch (mode)
                {
                    case "down": endFrame = VirtualInput.HoldKey(key); break;
                    case "up": endFrame = VirtualInput.ReleaseKey(key); break;
                    default: endFrame = VirtualInput.PressKey(key, frames); break;
                }
                return OkJson();
            });
            if (scheduled.Status != 200) return scheduled;

            // A held key never ends, so waiting for its window to close would hang; wait only for
            // the press to become visible.
            int target = mode == "down" ? endFrame + 1 : endFrame + 2;
            bool settled = !wait || MainThreadPump.WaitForFrame(target, 15000);

            var obs = VirtualInput.GetObservation(key);
            long inventoryRan = ChainProbe.Enters(ChainProbe.InventoryUpdate) - inventoryBefore;
            long normalModeRan = ChainProbe.Enters(ChainProbe.NormalMode) - normalModeBefore;
            long keyPollRan = ChainProbe.Enters(ChainProbe.KeyMapPoll) - keyPollBefore;

            bool delivered = obs.Total > 0;
            bool gateOpen = GameplayGate.GateOpen;
            bool consumed = delivered && gateOpen;

            var o = new Json.Obj()
                .Bit("ok", consumed || !requireConsumed)
                .Str("instance", InstanceManifest.Name)
                .Str("key", key.ToString()).Str("resolvedVia", how)
                .Str("mode", mode).Int("frames", frames)
                .Bit("consumed", consumed)
                .Bit("delivered", delivered)
                .Raw("observed", new Json.Obj()
                    .Int("getKey", obs.GetKey)
                    .Int("getKeyDown", obs.GetKeyDown)
                    .Int("getKeyUp", obs.GetKeyUp)
                    .ToString())
                .Raw("gate", new Json.Obj()
                    .Bit("open", gateOpen)
                    .Str("shutReason", GameplayGate.ShutReason())
                    .Bit("cursorVisible", GameplayGate.CursorVisible)
                    .Bit("consoleOpen", GameplayGate.ConsoleOpen)
                    .Str("keyInputState", GameplayGate.InputState)
                    .Int("keyMapPollRan", keyPollRan)
                    .Int("inventoryManagerUpdateRan", inventoryRan)
                    .Int("normalModeRan", normalModeRan)
                    .ToString())
                .Bit("settled", settled)
                .Str("settledMeans", "the frames requested elapsed; it says nothing about whether " +
                                     "the game read the key. Assert on 'consumed'.")
                .Str("note", Explain(delivered, gateOpen));

            return HttpResponse.Json(o.ToString(), (consumed || !requireConsumed) ? 200 : 409);
        }

        private static string Explain(bool delivered, bool gateOpen)
        {
            if (delivered && gateOpen) return null;
            if (delivered)
                return "delivered to the engine but the gameplay gate was shut, so the per-frame " +
                       "consumer never ran: " + GameplayGate.ShutReason();
            if (!gateOpen)
                return "nothing read this key, and the gameplay gate was shut, which is the likely " +
                       "reason: " + GameplayGate.ShutReason();
            return "the gate was open but nothing in the game read this key during the window. " +
                   "Either no consumer polls it at the current phase, or the window was too short. " +
                   "GET /diag/input shows which links of the chain are running.";
        }

        private static HttpResponse InputScroll(IDictionary body)
        {
            float notches = Json.GetFloat(body, "notches", 1f);
            // One frame by default, and that matters. Consumers act on the wheel once per frame, so
            // a two-frame window is two notches: a spray can's colour cycler advances two swatches
            // per request instead of one. Verified in world.
            int frames = Json.GetInt(body, "frames", 1);
            int repeat = Math.Max(1, Json.GetInt(body, "repeat", 1));
            bool wait = Json.GetBool(body, "wait", true);
            int gapFrames = Math.Max(1, Json.GetInt(body, "gapFrames", 3));
            bool requireConsumed = Json.GetBool(body, "requireConsumed", true);

            // Baselines. CheckDisplaySlotInput is the ONLY writer of InventoryManager.newScrollData
            // in the whole assembly, and it sits below the Cursor.visible early-return in
            // InventoryManager.ManagerUpdate, so if it did not run the wheel was never sampled no
            // matter what the driver injected. That counter is the honest number for the wheel.
            long checkBefore = ChainProbe.Enters(ChainProbe.CheckDisplaySlotInput);
            long normalModeBefore = ChainProbe.Enters(ChainProbe.NormalMode);
            Main(() => { VirtualInput.ResetScrollObservation(); return OkJson(); });

            for (int i = 0; i < repeat; i++)
            {
                int endFrame = 0;
                var scheduled = Main(() => { endFrame = VirtualInput.Scroll(notches, frames); return OkJson(); });
                if (scheduled.Status != 200) return scheduled;
                if (wait && !MainThreadPump.WaitForFrame(endFrame + gapFrames, 15000))
                    return HttpResponse.Error("scroll " + (i + 1) + "/" + repeat + " did not settle", 504);
            }

            long scrollReads = VirtualInput.ScrollReads;
            long checkRan = ChainProbe.Enters(ChainProbe.CheckDisplaySlotInput) - checkBefore;
            long normalModeRan = ChainProbe.Enters(ChainProbe.NormalMode) - normalModeBefore;

            // Leave no residual wheel state behind: a stale window would keep cycling on the next
            // frame the game happens to poll.
            Main(() => { VirtualInput.ClearScroll(); return OkJson(); });

            bool delivered = scrollReads > 0;
            bool gateOpen = GameplayGate.GateOpen;
            bool consumed = delivered && checkRan > 0;

            var o = new Json.Obj()
                .Bit("ok", consumed || !requireConsumed)
                .Str("instance", InstanceManifest.Name)
                .Flt("notches", notches).Int("frames", frames).Int("repeat", repeat)
                .Bit("consumed", consumed)
                .Bit("delivered", delivered)
                .Int("scrollReads", scrollReads)
                .Raw("gate", new Json.Obj()
                    .Bit("open", gateOpen)
                    .Str("shutReason", GameplayGate.ShutReason())
                    .Bit("cursorVisible", GameplayGate.CursorVisible)
                    .Bit("consoleOpen", GameplayGate.ConsoleOpen)
                    .Int("checkDisplaySlotInputRan", checkRan)
                    .Int("normalModeRan", normalModeRan)
                    .ToString())
                .Str("note", checkRan > 0
                    ? (delivered ? null : "CheckDisplaySlotInput ran but never read a synthetic wheel " +
                                          "value; the window may have missed its frame.")
                    : "InventoryManager.CheckDisplaySlotInput never ran during the window, so " +
                      "newScrollData was never written and no wheel consumer could possibly have " +
                      "seen this. " + (GameplayGate.ShutReason() ?? ""));

            return HttpResponse.Json(o.ToString(), (consumed || !requireConsumed) ? 200 : 409);
        }

        private static HttpResponse InputMouse(IDictionary body)
        {
            int button = Json.GetInt(body, "button", 0);
            var alias = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase)
            {
                { "key", (KeyCode.Mouse0 + button).ToString() },
                { "mode", Json.GetStr(body, "mode", "tap") },
                { "frames", (double)Json.GetInt(body, "frames", 3) },
                { "wait", Json.GetBool(body, "wait", true) },
                { "requireConsumed", Json.GetBool(body, "requireConsumed", true) },
            };
            return InputKey(alias);
        }

        /// <summary>
        ///     Sets or clears the mouse position override, and reports whether the game read it.
        ///     The override is passive (it only answers <c>Input.mousePosition</c> queries), so
        ///     "consumed" here means a read happened during the settle window rather than that any
        ///     particular consumer acted on it.
        /// </summary>
        private static HttpResponse InputMousePosition(IDictionary body)
        {
            bool clear = Json.GetBool(body, "clear", false);
            float x = Json.GetFloat(body, "x", 0f);
            float y = Json.GetFloat(body, "y", 0f);
            int frames = Math.Max(1, Json.GetInt(body, "frames", 2));
            bool wait = Json.GetBool(body, "wait", true);

            int endFrame = 0;
            var scheduled = Main(() =>
            {
                VirtualInput.ResetMousePositionObservation();
                VirtualInput.SetMousePosition(clear ? (Vector3?)null : new Vector3(x, y, 0f));
                endFrame = Time.frameCount + frames;
                return OkJson();
            });
            if (scheduled.Status != 200) return scheduled;

            bool settled = !wait || MainThreadPump.WaitForFrame(endFrame, 15000);
            long reads = VirtualInput.MousePositionReads;

            return HttpResponse.Json(new Json.Obj()
                .Bit("ok", true)
                .Str("instance", InstanceManifest.Name)
                .Bit("cleared", clear)
                .Flt("x", x).Flt("y", y)
                .Bit("settled", settled)
                .Bit("delivered", reads > 0)
                .Int("reads", reads)
                .Str("note", clear || reads > 0 ? null
                    : "nothing read Input.mousePosition during the window, so the override has not " +
                      "reached any consumer yet. It stays in force until cleared.")
                .ToString());
        }

        /// <summary>
        ///     Everything needed to answer "why did that input not do anything", in one request.
        ///     Four layers, top to bottom: are the Unity patches applied, is the per-frame chain
        ///     running, is the gameplay gate open, and where is this window relative to the
        ///     developer's desktop.
        ///
        ///     A link whose <c>enter</c> stops advancing is not being reached. A link whose
        ///     <c>enter</c> outruns its <c>exit</c> is throwing. <c>gate.open</c> false with the
        ///     chain advancing is the ordinary shut-gate case, and <c>gate.shutReason</c> names it.
        /// </summary>
        private static string InputDiagnostics()
        {
            var o = new Json.Obj();
            o.Bit("ok", true);
            o.Str("instance", InstanceManifest.Name);
            o.Int("frame", Time.frameCount);

            o.Raw("patches", new Json.Obj()
                .Bit("patchUnityInput", Plugin.PatchUnityInputValue)
                .Bit("inputInjectionEnabled", VirtualInput.Enabled)
                .Bit("getKey", IsPatchedByUs(AccessTools.Method(
                    typeof(UnityEngine.Input), nameof(UnityEngine.Input.GetKey), new[] { typeof(KeyCode) })))
                .Bit("getKeyDown", IsPatchedByUs(AccessTools.Method(
                    typeof(UnityEngine.Input), nameof(UnityEngine.Input.GetKeyDown), new[] { typeof(KeyCode) })))
                .Bit("mouseScrollDelta", IsPatchedByUs(AccessTools.Method(
                    typeof(UnityEngine.Input), "get_mouseScrollDelta")))
                .ToString());

            o.Raw("chain", ChainProbe.DescribeJson());
            o.Raw("gate", GameplayGate.DescribeJson());
            o.Raw("window", WindowMode.DescribeJson());
            o.Raw("foreground", NativeWindow.DescribeJson());

            try { o.Flt("newScrollData", InventoryManager.Instance == null ? 0f : InventoryManager.Instance.newScrollData); } catch { }
            o.Int("keyOverrides", VirtualInput.KeyOverrides);
            o.Int("scrollOverrides", VirtualInput.ScrollOverrides);
            o.Str("heldKeys", VirtualInput.DescribeHeld());
            return o.ToString();
        }

        private static bool IsPatchedByUs(MethodBase target)
        {
            if (target == null) return false;
            try
            {
                var info = Harmony.GetPatchInfo(target);
                if (info == null) return false;
                foreach (var owner in info.Owners) if (owner == Plugin.PluginGuid) return true;
                return false;
            }
            catch { return false; }
        }
    }
}
