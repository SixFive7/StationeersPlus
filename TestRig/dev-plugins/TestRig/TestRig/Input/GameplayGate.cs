using System;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
using ConsoleWindow = Assets.Scripts.ConsoleWindow;
using GameManager = Assets.Scripts.GameManager;

namespace TestRig
{
    /// <summary>
    ///     Opens the gameplay-input gate in a driven client, and reports whether it is open.
    ///
    ///     THE PROBLEM. Synthetic input at the <c>UnityEngine.Input</c> layer is the right layer:
    ///     <c>InputSystem.KeyWrap.PollForInput</c>, <c>KeyManager.GetButton</c> / <c>GetButtonDown</c>
    ///     / <c>GetButtonUp</c> and about 139 direct call sites all bottom out in
    ///     <c>Input.GetKey</c> and friends, with no cached per-frame key state anywhere to sit under.
    ///     So the driver hands the game exactly the value it asked for. What throws the value away
    ///     is a gate further up, in <c>InventoryManager.ManagerUpdate</c>:
    ///
    ///     <code>
    ///         if (Cursor.visible || Parent.IsUnresponsive || ConsoleWindow.IsOpen) { return; }
    ///         CheckDisplaySlotInput();          // the only writer of newScrollData
    ///         ...
    ///         switch (CurrentMode) { case Mode.Normal: NormalMode(); ... }
    ///     </code>
    ///
    ///     Everything input-driven sits below that early return. <c>CheckDisplaySlotInput</c> is the
    ///     sole assignment of <c>newScrollData</c>, so no wheel is sampled at all, and
    ///     <c>NormalMode()</c> is where mods hang their per-frame gameplay hooks, so those never run
    ///     either. The same <c>Cursor.visible</c> term gates movement.
    ///
    ///     And <c>Cursor.visible</c> is exactly what an unfocused Unity window ends up with: Unity
    ///     releases the cursor lock on focus loss and <c>MouseModeController.SetState</c> cannot take
    ///     it back while the window is in the background. So a background client silently loses every
    ///     per-frame gameplay input consumer while direct method calls keep working perfectly.
    ///
    ///     THE FIX is in-process and needs no window focus: assert the cursor state in a prefix on
    ///     the very method whose gate reads it, on the same frame, a few instructions before the
    ///     read. Nothing here touches the OS window, so the never-focus rule in README.md holds.
    ///
    ///     SCOPE. The gate is asserted only while the client is in a world
    ///     (<c>GameState.Running</c>) and no confirmation dialog is up. The first version asserted
    ///     unconditionally on every <c>ManagerUpdate</c>, including at the main menu, which is a
    ///     blunt instrument for two reasons. It holds the cursor hidden and locked in menus where
    ///     the cursor is the only way to interact, so a test that genuinely needs a mouse-driven
    ///     panel fights it and loses; and it hides the cursor during boot on a machine where the
    ///     developer may be doing something else. In a world, hiding the cursor is what the game
    ///     itself does, so the assertion is invisible. Set
    ///     <c>Force Gameplay Input Everywhere</c> for the old behaviour when a test needs the gate
    ///     open before the world exists.
    ///
    ///     Off by default. Forcing the cursor hidden and locked under a real player would fight them
    ///     for control, so this is only correct for a client nobody is sitting at.
    /// </summary>
    internal static class GameplayGate
    {
        /// <summary>Master switch. Off means this class never writes anything.</summary>
        internal static bool Force;

        /// <summary>
        ///     Assert the gate outside a loaded world too (menu, loading, joining). The pre-scoping
        ///     behaviour. Only worth setting for a test that drives the menu through synthetic input
        ///     rather than through the HTTP endpoints.
        /// </summary>
        internal static bool Everywhere;

        internal static long GateAsserts;
        internal static long GateWouldHaveClosed;   // times Cursor.visible was true and we cleared it
        internal static long SkippedNotInWorld;
        internal static long SkippedModalUp;
        internal static string LastError;

        private static PropertyInfo _inputStateSetter;
        private static bool _inputStateResolved;
        private static Type _keyManagerType;

        internal static Type KeyManagerType
        {
            get
            {
                if (_keyManagerType == null)
                    _keyManagerType = AccessTools.TypeByName("KeyManager")
                                      ?? AccessTools.TypeByName("Assets.Scripts.KeyManager");
                return _keyManagerType;
            }
        }

        // ---- scope ---------------------------------------------------------------

        private static PropertyInfo _gameStateProp;
        private static object _runningValue;        // the boxed GameState.Running constant
        private static bool _gameStateResolved;
        private static int _scopeFrame = -1;
        private static bool _scopeInWorld;

        /// <summary>
        ///     True when the client is in a loaded world. Resolved reflectively so a game update that
        ///     moves the GameState enum degrades to "the gate never asserts" rather than to a plugin
        ///     that does not load, and cached per frame because the gate is evaluated once per
        ///     <c>ManagerUpdate</c>.
        /// </summary>
        internal static bool InWorld
        {
            get
            {
                int frame;
                try { frame = Time.frameCount; }
                catch { return false; }
                if (frame == _scopeFrame) return _scopeInWorld;
                _scopeFrame = frame;
                _scopeInWorld = ComputeInWorld();
                return _scopeInWorld;
            }
        }

        private static bool ComputeInWorld()
        {
            try
            {
                if (!_gameStateResolved)
                {
                    _gameStateResolved = true;
                    _gameStateProp = AccessTools.Property(typeof(GameManager), "GameState");
                    if (_gameStateProp != null)
                    {
                        try { _runningValue = Enum.Parse(_gameStateProp.PropertyType, "Running"); }
                        catch (Exception ex) { LastError = "GameState.Running: " + ex.Message; }
                    }
                }
                if (_gameStateProp == null || _runningValue == null) return false;
                return _runningValue.Equals(_gameStateProp.GetValue(null, null));
            }
            catch (Exception ex) { LastError = "scope: " + ex.Message; return false; }
        }

        // ---- the assertion --------------------------------------------------------

        /// <summary>
        ///     Called from a prefix on <c>InventoryManager.ManagerUpdate</c>, a few instructions
        ///     before the gate is evaluated. Doing it here rather than from a frame pump means
        ///     nothing can land in between and re-show the cursor.
        /// </summary>
        internal static void OpenGate()
        {
            if (!Force) return;
            try
            {
                if (!Everywhere && !InWorld) { SkippedNotInWorld++; return; }

                // A confirmation dialog needs the cursor. Holding it hidden over a modal leaves the
                // client with a dialog nobody can dismiss by hand, which is exactly the wedge state
                // /modal exists to recover from.
                if (Modal.IsShowing()) { SkippedModalUp++; return; }

                if (Cursor.visible)
                {
                    Cursor.visible = false;
                    GateWouldHaveClosed++;
                }
                if (Cursor.lockState != CursorLockMode.Locked)
                    Cursor.lockState = CursorLockMode.Locked;

                ForceKeyInputStateGame();
                GateAsserts++;
            }
            catch (Exception ex) { LastError = ex.Message; }
        }

        /// <summary>
        ///     <c>KeyManager.InputState</c> is a public getter with a private setter and it defaults
        ///     to <c>Game</c>, so this is normally a no-op. It matters when a panel pushed a
        ///     different state and nobody popped it, because <c>KeyWrapBindings.KeyWrapOnEvent</c>
        ///     filters every KeyWrap callback on <c>item.inputState.HasFlag(KeyManager.InputState)</c>
        ///     and a stuck state makes bound actions (SwapHands among them) silently inert.
        /// </summary>
        private static void ForceKeyInputStateGame()
        {
            if (!_inputStateResolved)
            {
                _inputStateResolved = true;
                Type t = KeyManagerType;
                if (t != null) _inputStateSetter = AccessTools.Property(t, "InputState");
            }
            if (_inputStateSetter == null) return;

            var setter = _inputStateSetter.GetSetMethod(true);
            if (setter == null) return;

            object current = _inputStateSetter.GetValue(null, null);
            object game = Enum.ToObject(_inputStateSetter.PropertyType, 2);   // KeyInputState.Game
            if (Equals(current, game)) return;
            setter.Invoke(null, new[] { game });
        }

        // ---- gate observation, used by the /input/* read-back ---------------------

        internal static bool CursorVisible
        {
            get { try { return Cursor.visible; } catch { return false; } }
        }

        internal static bool ConsoleOpen
        {
            get { try { return ConsoleWindow.IsOpen; } catch { return false; } }
        }

        internal static string InputState
        {
            get
            {
                try
                {
                    Type t = KeyManagerType;
                    var p = t == null ? null : AccessTools.Property(t, "InputState");
                    return p == null ? null : Convert.ToString(p.GetValue(null, null));
                }
                catch { return null; }
            }
        }

        /// <summary>
        ///     The gate as the game evaluates it, minus the parts that cannot be read cheaply. True
        ///     means a per-frame gameplay input consumer would actually run.
        /// </summary>
        internal static bool GateOpen
        {
            get { return !CursorVisible && !ConsoleOpen; }
        }

        /// <summary>
        ///     Why the gate is shut, in words, or null when it is open. This is what an input
        ///     endpoint quotes back to a caller instead of leaving them to work it out.
        /// </summary>
        internal static string ShutReason()
        {
            if (GateOpen) return null;
            if (ConsoleOpen)
                return "the in-game console is open, and InventoryManager.ManagerUpdate early-returns " +
                       "on ConsoleWindow.IsOpen. Close it with /input/key key=ToggleConsole, or avoid " +
                       "leaving /console/exec with the window up.";
            if (!Force)
                return "the cursor is visible and Client - Gameplay Input / Force Gameplay Input is off, " +
                       "so InventoryManager.ManagerUpdate early-returns on Cursor.visible and no " +
                       "per-frame input consumer runs. Set that setting to true for a client nobody " +
                       "is sitting at.";
            if (!Everywhere && !InWorld)
                return "the gate is scoped to GameState.Running and this client is not in a world " +
                       "(current phase is outside it). Load or join a world first, or set " +
                       "Client - Gameplay Input / Force Gameplay Input Everywhere.";
            if (Modal.IsShowing())
                return "a confirmation dialog is up and the gate deliberately yields to it so the " +
                       "dialog stays clickable. Read it with GET /modal and dismiss it with " +
                       "POST /modal/click.";
            return "the cursor is visible for a reason this build does not recognise; see /diag/input.";
        }

        internal static string DescribeJson()
        {
            var o = new Json.Obj();
            o.Bit("forceGameplayInput", Force);
            o.Bit("everywhere", Everywhere);
            o.Bit("inWorld", InWorld);
            o.Bit("gateOpen", GateOpen);
            o.Str("shutReason", ShutReason());
            o.Bit("cursorVisible", CursorVisible);
            o.Bit("consoleOpen", ConsoleOpen);
            o.Bit("modalUp", Modal.IsShowing());
            o.Str("keyInputState", InputState);
            try { o.Str("cursorLockState", Cursor.lockState.ToString()); } catch { }
            o.Int("gateAsserts", GateAsserts);
            o.Int("cursorForcedHiddenCount", GateWouldHaveClosed);
            o.Int("skippedNotInWorld", SkippedNotInWorld);
            o.Int("skippedModalUp", SkippedModalUp);
            o.Str("lastError", LastError);
            return o.ToString();
        }
    }

    /// <summary>
    ///     The gate opener. Target is resolved reflectively and the class skips itself if the method
    ///     is gone, so a game update that renames it degrades to "input does not land, and
    ///     /diag/input says exactly why" rather than to a failed plugin load.
    /// </summary>
    [HarmonyPatch]
    internal static class InventoryManagerGatePatch
    {
        private static MethodBase Resolve()
        {
            var t = AccessTools.TypeByName("Assets.Scripts.Inventory.InventoryManager");
            return t == null ? null : AccessTools.Method(t, "ManagerUpdate");
        }

        internal static bool Prepare() => Plugin.ClientOnlyPatches && Resolve() != null;
        internal static MethodBase TargetMethod() => Resolve();

        internal static void Prefix()
        {
            try { GameplayGate.OpenGate(); } catch { }
        }
    }
}
