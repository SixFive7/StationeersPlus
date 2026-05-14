using Assets.Scripts;
using Assets.Scripts.Inventory;
using Assets.Scripts.Networking;
using Assets.Scripts.Objects;
using Assets.Scripts.Objects.Entities;
using Assets.Scripts.Objects.Items;
using Assets.Scripts.UI;
using HarmonyLib;
using JetBrains.Annotations;
using LaunchPadBooster.Networking;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace EquipmentPlus
{
    /// <summary>
    /// Diagnostic flag for the scroll dispatcher. When true, every non-zero
    /// scroll event with a recognized modifier set logs one [scroll] line
    /// describing the inputs and the dispatched path (or the bail reason).
    /// Mirrors the ClickTrace pattern in ConfigCartridgeState. Leave on while
    /// iterating on B; flip to false in the eventual mod-wide diagnostic
    /// strip per TODO Pending work.
    /// </summary>
    internal static class ScrollDispatchState
    {
        internal const bool ScrollTrace = true;
    }

    /// <summary>
    /// Modifier+scroll dispatcher. Patches InventoryManager.NormalMode as a
    /// Prefix (mirrors Better Headlamp's HeadlampScrollPatch entry point;
    /// reference verbatim in Research/GameSystems/ScrollInputHandling.md
    /// "Better Headlamp pattern (for reference)") and reads
    /// __instance.newScrollData (set earlier in CheckDisplaySlotInput) for the
    /// wheel delta. Routes by exact-modifier match:
    ///
    ///   Ctrl alone  -> tablet cartridge cycle (held tablet only in Phase 1)
    ///   Shift alone -> worn-lens chip cycle
    ///   Alt alone   -> helmet beam adjust
    ///   any other combo -> no-op
    ///
    /// Bare Shift+scroll is suppressed at CameraController.CacheCameraPosition
    /// by CameraZoomSuppressPatch so vanilla camera zoom doesn't fight our
    /// lens cycle. All other Shift-bearing combos (including the explicit
    /// Ctrl+Alt+Shift+scroll) fall through to vanilla camera zoom.
    ///
    /// Cycle rules (tablet, lens):
    ///   - Wheel-up = next occupied slot, wheel-down = previous, skip empty.
    ///   - Down past first occupied turns OFF (selection preserved).
    ///   - Up from OFF turns ON at preserved selection (no reset).
    ///   - No wrap on either end.
    ///
    /// Direction convention: wheel-up = -1, wheel-down = +1 (matches the
    /// existing Cartridge.OnScroll line-select sign).
    /// </summary>
    [HarmonyPatch(typeof(InventoryManager), "NormalMode")]
    public class ScrollDispatchPatch
    {
        [UsedImplicitly]
        public static void Prefix(InventoryManager __instance)
        {
            if (__instance == null) return;
            float scroll = __instance.newScrollData;
            if (scroll == 0f) return;

            // KeyManager.GetButton(KeyCode) distinguishes LeftShift / RightShift
            // specifically (Research/GameSystems/KeyBinding.md). We use that:
            // bare LeftShift = OUR lens cycle; bare RightShift falls through to
            // vanilla camera zoom (whose ThirdPersonControl was auto-rebound to
            // RightShift on mod load by Plugin.EnsureCameraKeyDoesNotConflict).
            bool ctrl       = KeyManager.GetButton(KeyCode.LeftControl)
                           || KeyManager.GetButton(KeyCode.RightControl);
            bool leftShift  = KeyManager.GetButton(KeyCode.LeftShift);
            bool rightShift = KeyManager.GetButton(KeyCode.RightShift);

            // Wheel-up = +1 (advance to next slot in cycle / tighten beam).
            // Wheel-down = -1 (retreat to previous / widen beam / eventually OFF).
            int direction = scroll > 0f ? 1 : -1;

            if (ScrollDispatchState.ScrollTrace)
                EquipmentPlusPlugin.Log.LogInfo(
                    $"[EquipmentPlus.scroll] fired: newScrollData={scroll:F3} dir={direction} ctrl={ctrl} L-shift={leftShift} R-shift={rightShift}");

            // ALT is omitted entirely: vanilla captures ALT for its own
            // mouse-input mode toggle, so ALT+scroll never reaches our prefix.
            // Headlamp restricted to Ctrl+LeftShift (NOT Ctrl+RightShift) so
            // it never collides with a player who might be holding RightShift
            // alongside Ctrl while operating the camera.
            if (ctrl && !leftShift && !rightShift)
            {
                if (ScrollDispatchState.ScrollTrace)
                    EquipmentPlusPlugin.Log.LogInfo("[EquipmentPlus.scroll] dispatch: tablet (Ctrl alone)");
                DispatchTablet(direction);
            }
            else if (leftShift && !ctrl && !rightShift)
            {
                if (ScrollDispatchState.ScrollTrace)
                    EquipmentPlusPlugin.Log.LogInfo("[EquipmentPlus.scroll] dispatch: lens (LeftShift alone)");
                DispatchLens(direction);
            }
            else if (ctrl && leftShift && !rightShift)
            {
                if (ScrollDispatchState.ScrollTrace)
                    EquipmentPlusPlugin.Log.LogInfo("[EquipmentPlus.scroll] dispatch: headlamp (Ctrl+LeftShift)");
                DispatchHeadlamp(direction);
            }
            else if (ScrollDispatchState.ScrollTrace)
            {
                EquipmentPlusPlugin.Log.LogInfo("[EquipmentPlus.scroll] no-op: modifier combo unbound (RightShift -> vanilla camera; other combos -> nothing)");
            }
            // Vanilla camera zoom fires when KeyMap.ThirdPersonControl is held
            // (= RightShift after our auto-rebind). We do not suppress; vanilla
            // handles it directly with no patch needed.
        }

        private static void DispatchTablet(int direction)
        {
            var held = InventoryManager.Instance?.ActiveHand?.Slot?.Get();
            if (ScrollDispatchState.ScrollTrace)
            {
                string role = NetworkManager.IsServer ? "HOST"
                            : NetworkManager.IsClient ? "CLIENT"
                            : "SINGLEPLAYER";
                EquipmentPlusPlugin.Log.LogInfo(
                    $"[EquipmentPlus.scroll] tablet dispatch role={role} active={DescribeSlot(InventoryManager.Instance?.ActiveHand?.Slot)} inactive={DescribeSlot(InventoryManager.Instance?.InactiveHand?.Slot)}");
            }
            if (held is AdvancedTablet tablet)
            {
                CycleTablet(tablet, direction);
                return;
            }

            // Phase 2: hand-switch / auto-equip path. Returns true if it took
            // any action (hand-switch, equip in progress, or even logged a
            // "no tablet found anywhere"). No cycle this scroll regardless;
            // a subsequent scroll after the tablet lands in active hand
            // cycles via the held-AdvancedTablet branch above.
            TryAutoEquipTablet(held);
        }

        // Diagnostic-only: returns a one-line tag describing the slot
        // identity. Format: "[parent#netId/slotIdx hand=L|R|? occupant=Type]"
        // Used by the auto-equip trace to capture which physical slot we are
        // sending things to vs from on a remote client.
        private static string DescribeSlot(Slot s)
        {
            if (s == null) return "null";
            string hand = "?";
            try
            {
                if (s.StringHash == Slot.LeftHandHash) hand = "L";
                else if (s.StringHash == Slot.RightHandHash) hand = "R";
                else hand = "n/a";
            }
            catch { }
            string parentName = s.Parent != null ? s.Parent.GetType().Name : "(null parent)";
            long parentId = s.Parent != null ? s.Parent.ReferenceId : 0L;
            string occ = s.Get() != null ? s.Get().GetType().Name : "(empty)";
            return $"[{parentName}#{parentId}/{s.SlotIndex} hand={hand} occ={occ}]";
        }

        // Hand-slot interactable types SmartStow excludes when stowing FROM a
        // hand (mirrors InventoryManager._excludeHandSlots). We reuse the same
        // exclusion when probing "is there inventory room for this occupant?"
        // so the probe agrees with what SmartStow will actually do.
        private static readonly List<InteractableType> ExcludeHandSlots =
            new List<InteractableType> { InteractableType.Slot1, InteractableType.Slot2 };

        // Phase 2: when active hand does NOT hold a tablet, try to put one
        // there. Decision tree:
        //   1. Off-hand has tablet -> SwapHands (no item moves; the now-active
        //      tablet cycles on the next scroll).
        //   2. Search inventory (Toolbelt -> Backpack -> Suit, no nested) for
        //      the first AdvancedTablet. None found -> no-op.
        //   3. Active hand empty -> OnServer.MoveToSlot(tablet, activeHand).
        //   4. Active hand occupied -> fire TWO ordered moves: stow the
        //      occupant, then put the tablet in the freed hand. The server
        //      processes a client's messages in send order, so the stow lands
        //      before the equip; on the host both are synchronous and still
        //      ordered. No wait, no poll, no coroutine. Stow target:
        //        a. an inventory slot via SmartStow (which keeps its
        //           "put it back" bookkeeping), if the occupant has somewhere
        //           to go that isn't a hand;
        //        b. else the off-hand if it's empty (SmartStow never uses a
        //           hand slot when stowing from a hand);
        //        c. else nothing fits -> tell the player.
        //
        // Multiplayer correctness: every move goes through OnServer.MoveToSlot
        // (universal entry point per Research/Patterns/InventoryAutoEquip.md),
        // which is a direct call on the host and an ordered network request on
        // a client. Two such calls in sequence therefore execute in order on
        // the authoritative side, which is all the ordering this needs.
        private static void TryAutoEquipTablet(DynamicThing currentlyHeld)
        {
            var human = InventoryManager.ParentHuman;
            if (human == null)
            {
                if (ScrollDispatchState.ScrollTrace)
                    EquipmentPlusPlugin.Log.LogInfo("[EquipmentPlus.scroll] tablet auto-equip: ParentHuman null, no-op");
                return;
            }

            // Step 1: off-hand has tablet -> swap hands.
            var inactiveSlot = InventoryManager.Instance?.InactiveHand?.Slot;
            if (inactiveSlot?.Get() is AdvancedTablet)
            {
                if (ScrollDispatchState.ScrollTrace)
                    EquipmentPlusPlugin.Log.LogInfo("[EquipmentPlus.scroll] tablet auto-equip: hand-switch (off-hand has tablet)");
                Human.LocalHuman?.HumanHandsBehaviour?.SwapHands();
                return;
            }

            // Step 2: search inventory.
            if (!FindTabletInInventory(human, out var tablet, out var sourceSlot))
            {
                if (ScrollDispatchState.ScrollTrace)
                    EquipmentPlusPlugin.Log.LogInfo("[EquipmentPlus.scroll] tablet auto-equip: no tablet found anywhere");
                return;
            }

            var activeHandSlot = InventoryManager.ActiveHandSlot;
            if (activeHandSlot == null)
            {
                if (ScrollDispatchState.ScrollTrace)
                    EquipmentPlusPlugin.Log.LogInfo("[EquipmentPlus.scroll] tablet auto-equip: ActiveHandSlot null, no-op");
                return;
            }

            // Step 3: active hand empty -> direct equip.
            if (currentlyHeld == null)
            {
                if (ScrollDispatchState.ScrollTrace)
                    EquipmentPlusPlugin.Log.LogInfo(
                        $"[EquipmentPlus.scroll] tablet auto-equip: active hand empty, equipping {tablet.GetType().Name}#{tablet.ReferenceId} from src={DescribeSlot(sourceSlot)} -> active={DescribeSlot(activeHandSlot)}");
                OnServer.MoveToSlot(tablet, activeHandSlot);
                return;
            }

            // Step 4: active hand occupied -> stow occupant, then equip tablet.
            var offHandSlot   = InventoryManager.Instance?.InactiveHand?.Slot;
            bool offHandFree  = offHandSlot != null && offHandSlot.Get() == null;
            bool inventoryRoom = human.GetFreeSlot(currentlyHeld.SlotType, ExcludeHandSlots) != null;

            if (inventoryRoom)
            {
                if (ScrollDispatchState.ScrollTrace)
                    EquipmentPlusPlugin.Log.LogInfo(
                        $"[EquipmentPlus.scroll] tablet auto-equip: active occupied by {currentlyHeld.GetType().Name}; SmartStow to inventory then equip {tablet.GetType().Name}#{tablet.ReferenceId} (active={DescribeSlot(activeHandSlot)})");
                // SmartStow(Slot) is the static InventoryManager method (NOT
                // the no-arg InventoryWindowManager overload bound to the
                // keybind). It resolves a destination slot client-side,
                // records the "put it back" original-slot mapping, and fires
                // one OnServer.MoveToSlot. We then fire the tablet equip; the
                // authoritative side runs the stow first, freeing the hand.
                InventoryManager.SmartStow(activeHandSlot);
                OnServer.MoveToSlot(tablet, activeHandSlot);
            }
            else if (offHandFree)
            {
                if (ScrollDispatchState.ScrollTrace)
                    EquipmentPlusPlugin.Log.LogInfo(
                        $"[EquipmentPlus.scroll] tablet auto-equip: inventory full, parking {currentlyHeld.GetType().Name} in off-hand {DescribeSlot(offHandSlot)} then equip {tablet.GetType().Name}#{tablet.ReferenceId}");
                OnServer.MoveToSlot(currentlyHeld, offHandSlot);
                OnServer.MoveToSlot(tablet, activeHandSlot);
            }
            else
            {
                if (ScrollDispatchState.ScrollTrace)
                    EquipmentPlusPlugin.Log.LogInfo("[EquipmentPlus.scroll] tablet auto-equip: no room to stow occupant (inventory full, off-hand occupied)");
                ConsoleWindow.PrintError("[EquipmentPlus] No room to swap. Manually stow or drop the item in your active hand to equip the tablet.");
            }
        }

        // Inventory search helper. Scans the slots inside the player's
        // Toolbelt occupant first, then Backpack occupant, then Suit
        // occupant. No nested scan (a tablet inside a backpack-inside-a-
        // toolbelt is not found). Field name gotcha:
        // Human.ToolbeltSlot is one word lowercase 'b' (distinct from
        // KeyMap.ToolBeltSlot). See Research/Patterns/InventoryAutoEquip.md
        // "Human slot fields".
        private static bool FindTabletInInventory(Human human, out AdvancedTablet tablet, out Slot sourceSlot)
        {
            tablet = null;
            sourceSlot = null;
            if (TryFindIn(human?.ToolbeltSlot?.Get() as DynamicThing, out tablet, out sourceSlot)) return true;
            if (TryFindIn(human?.BackpackSlot?.Get() as DynamicThing, out tablet, out sourceSlot)) return true;
            if (TryFindIn(human?.SuitSlot?.Get() as DynamicThing, out tablet, out sourceSlot)) return true;
            return false;
        }

        private static bool TryFindIn(DynamicThing container, out AdvancedTablet tablet, out Slot sourceSlot)
        {
            tablet = null;
            sourceSlot = null;
            if (container?.Slots == null) return false;
            foreach (var slot in container.Slots)
            {
                if (slot?.Get() is AdvancedTablet t)
                {
                    tablet = t;
                    sourceSlot = slot;
                    return true;
                }
            }
            return false;
        }

        private static void DispatchLens(int direction)
        {
            var human = InventoryManager.ParentHuman;
            if (human == null)
            {
                if (ScrollDispatchState.ScrollTrace)
                    EquipmentPlusPlugin.Log.LogInfo("[EquipmentPlus.scroll] lens bail: InventoryManager.ParentHuman null");
                return;
            }
            var lens = human.GlassesSlot?.Get() as SensorLenses;
            if (lens == null)
            {
                if (ScrollDispatchState.ScrollTrace)
                    EquipmentPlusPlugin.Log.LogInfo("[EquipmentPlus.scroll] lens bail: GlassesSlot empty or not SensorLenses");
                return;
            }
            CycleLens(lens, direction);
        }

        private static void DispatchHeadlamp(int direction)
        {
            var human = Human.LocalHuman;
            if (human == null)
            {
                if (ScrollDispatchState.ScrollTrace)
                    EquipmentPlusPlugin.Log.LogInfo("[EquipmentPlus.scroll] headlamp bail: Human.LocalHuman null");
                return;
            }
            HelmetBeamPatches.HandleScroll(human, direction);
        }

        // -------------------- tablet cycle --------------------

        private static void CycleTablet(AdvancedTablet tablet, int direction)
        {
            // Build the list of occupied cartridge slot indices.
            var occupied = new List<int>();
            int currentRank = -1;
            for (int i = 0; i < tablet.CartridgeSlots.Count; i++)
            {
                if (tablet.CartridgeSlots[i].Get() != null)
                {
                    if (i == tablet.Mode) currentRank = occupied.Count;
                    occupied.Add(i);
                }
            }
            if (occupied.Count == 0)
            {
                if (ScrollDispatchState.ScrollTrace)
                    EquipmentPlusPlugin.Log.LogInfo("[EquipmentPlus.scroll] tablet: no occupied cartridge slots, no-op");
                return;
            }

            if (!tablet.OnOff)
            {
                // Currently OFF. Wheel-up turns on at preserved Mode (or first
                // occupied if Mode is invalid). Wheel-down is no-op (already
                // at the bottom of the cycle).
                if (direction == 1)
                {
                    int targetMode = (currentRank < 0) ? occupied[0] : tablet.Mode;
                    if (ScrollDispatchState.ScrollTrace)
                        EquipmentPlusPlugin.Log.LogInfo($"[EquipmentPlus.scroll] tablet: OFF + wheel-up -> ON, Mode={targetMode}");
                    tablet.Mode = targetMode;
                    tablet.OnOff = true;
                    ForceCartridgeRefresh(tablet);
                }
                else if (ScrollDispatchState.ScrollTrace)
                {
                    EquipmentPlusPlugin.Log.LogInfo("[EquipmentPlus.scroll] tablet: OFF + wheel-down, no-op (already below first cartridge)");
                }
                return;
            }

            // Currently ON.
            if (currentRank < 0)
            {
                // Mode points to an empty slot. Treat as before-first.
                if (direction == 1)
                {
                    if (ScrollDispatchState.ScrollTrace)
                        EquipmentPlusPlugin.Log.LogInfo($"[EquipmentPlus.scroll] tablet: invalid Mode + wheel-up -> Mode={occupied[0]}");
                    tablet.Mode = occupied[0];
                    ForceCartridgeRefresh(tablet);
                }
                else
                {
                    if (ScrollDispatchState.ScrollTrace)
                        EquipmentPlusPlugin.Log.LogInfo("[EquipmentPlus.scroll] tablet: invalid Mode + wheel-down -> OFF");
                    tablet.OnOff = false;
                }
                return;
            }

            int newRank = currentRank + direction;
            if (newRank < 0)
            {
                // Wheel-down past first occupied: turn OFF, preserve Mode.
                if (ScrollDispatchState.ScrollTrace)
                    EquipmentPlusPlugin.Log.LogInfo($"[EquipmentPlus.scroll] tablet: wheel-down past first cartridge -> OFF (Mode preserved at {tablet.Mode})");
                tablet.OnOff = false;
                return;
            }
            if (newRank >= occupied.Count)
            {
                // Wheel-up past last occupied: no-op (no wrap).
                if (ScrollDispatchState.ScrollTrace)
                    EquipmentPlusPlugin.Log.LogInfo($"[EquipmentPlus.scroll] tablet: wheel-up past last cartridge, no-op (Mode={tablet.Mode})");
                return;
            }

            int newMode = occupied[newRank];
            if (ScrollDispatchState.ScrollTrace)
                EquipmentPlusPlugin.Log.LogInfo($"[EquipmentPlus.scroll] tablet: Mode {tablet.Mode} -> {newMode} (rank {currentRank} -> {newRank})");
            tablet.Mode = newMode;
            ForceCartridgeRefresh(tablet);
        }

        private static void ForceCartridgeRefresh(AdvancedTablet tablet)
        {
            // Thing.set_Mode propagates
            // via the networked Interactable state, but does NOT call
            // GetCartridge() which refreshes the displayed cartridge screen.
            // Vanilla's Next-button InteractWith path calls both; so do we.
            AccessTools.Method(typeof(AdvancedTablet), "GetCartridge")
                ?.Invoke(tablet, null);
        }

        // -------------------- lens cycle --------------------

        private static void CycleLens(SensorLenses lens, int direction)
        {
            // Build the list of occupied chip slots in order.
            var chips = new List<SensorProcessingUnit>();
            int currentRank = -1;
            for (int i = 0; i < lens.Slots.Count; i++)
            {
                if (lens.Slots[i].Get() is SensorProcessingUnit chip)
                {
                    if (chip == lens.Sensor) currentRank = chips.Count;
                    chips.Add(chip);
                }
            }
            if (chips.Count == 0)
            {
                if (ScrollDispatchState.ScrollTrace)
                    EquipmentPlusPlugin.Log.LogInfo("[EquipmentPlus.scroll] lens: no occupied chip slots, no-op");
                return;
            }

            if (!lens.OnOff)
            {
                // Currently OFF. Wheel-up turns on at preserved Sensor (or
                // first chip if Sensor is null/stale). Wheel-down is no-op.
                if (direction == 1)
                {
                    var target = (currentRank < 0) ? chips[0] : lens.Sensor;
                    if (ScrollDispatchState.ScrollTrace)
                        EquipmentPlusPlugin.Log.LogInfo($"[EquipmentPlus.scroll] lens: OFF + wheel-up -> ON, Sensor={target?.GetType().Name ?? "(null)"}");
                    SetLensState(lens, target, true);
                }
                else if (ScrollDispatchState.ScrollTrace)
                {
                    EquipmentPlusPlugin.Log.LogInfo("[EquipmentPlus.scroll] lens: OFF + wheel-down, no-op");
                }
                return;
            }

            // Currently ON.
            if (currentRank < 0)
            {
                if (direction == 1)
                {
                    if (ScrollDispatchState.ScrollTrace)
                        EquipmentPlusPlugin.Log.LogInfo("[EquipmentPlus.scroll] lens: invalid Sensor + wheel-up -> first chip");
                    SetLensState(lens, chips[0], true);
                }
                else
                {
                    if (ScrollDispatchState.ScrollTrace)
                        EquipmentPlusPlugin.Log.LogInfo("[EquipmentPlus.scroll] lens: invalid Sensor + wheel-down -> OFF (Sensor preserved)");
                    SetLensState(lens, lens.Sensor, false);
                }
                return;
            }

            int newRank = currentRank + direction;
            if (newRank < 0)
            {
                // Wheel-down past first chip: turn OFF, PRESERVE Sensor.
                // The scroll path keeps Sensor set even when OnOff is cleared
                // so a later wheel-up returns to the same chip
                // (per TODO B "State preservation across OFF / ON").
                if (ScrollDispatchState.ScrollTrace)
                    EquipmentPlusPlugin.Log.LogInfo("[EquipmentPlus.scroll] lens: wheel-down past first chip -> OFF (Sensor preserved)");
                SetLensState(lens, lens.Sensor, false);
                return;
            }
            if (newRank >= chips.Count)
            {
                if (ScrollDispatchState.ScrollTrace)
                    EquipmentPlusPlugin.Log.LogInfo("[EquipmentPlus.scroll] lens: wheel-up past last chip, no-op");
                return;
            }

            if (ScrollDispatchState.ScrollTrace)
                EquipmentPlusPlugin.Log.LogInfo($"[EquipmentPlus.scroll] lens: chip rank {currentRank} -> {newRank}");
            SetLensState(lens, chips[newRank], true);
        }

        // Independent OnOff/Sensor parameters. The existing
        // SetActiveSensorMessage carries both fields independently in its
        // wire format, so OFF + non-null Sensor is representable for
        // remote-client writes.
        private static void SetLensState(SensorLenses lens, SensorProcessingUnit sensor, bool powerOn)
        {
            // Optimistic local update for responsive rendering on every role.
            lens.Sensor = sensor;
            if (lens.OnOff != powerOn)
                lens.OnOff = powerOn;

            if (NetworkManager.IsActive && !NetworkManager.IsServer)
            {
                new SetActiveSensorMessage
                {
                    LensesReferenceId = lens.ReferenceId,
                    SensorReferenceId = sensor != null ? sensor.ReferenceId : 0L,
                    PowerOn = powerOn
                }.SendToHost();
                return;
            }

            if (NetworkManager.IsServer)
                lens.NetworkUpdateFlags |= SensorLensesSync.ActiveSensorFlag;
        }
    }

    // Suppress the vanilla scroll-driven hotbar advance whenever a modifier is
    // held. The hotbar advance is what makes a Ctrl+scroll or Shift+scroll
    // bleed into "next inventory slot" alongside our modifier-scroll dispatch
    // (tablet / lens / headlamp). Vanilla calls
    // InventoryWindowManager.NextButton / PreviousButton from
    // InventoryManager.CheckDisplaySlotInput when newScrollData != 0, BEFORE
    // our NormalMode prefix runs (Research/GameSystems/ScrollInputHandling.md
    // "InventoryManager.NormalMode is NOT a scroll consumer"), so a false
    // return from NormalMode cannot suppress it. Patching the destination
    // methods is the surgical fix.
    //
    // Caller-distinguishing trick: NextButton / PreviousButton are also
    // called by KeyMap.NextItem / KeyMap.PreviousItem keyboard hotkeys.
    // We only want to suppress the scroll-driven path. When the call is
    // scroll-driven this frame, Input.mouseScrollDelta.y != 0; when it is
    // keyboard-driven, mouseScrollDelta.y == 0. The check below uses that
    // signal so keyboard hotbar navigation still works even with a modifier
    // held. See Research/GameSystems/ScrollInputHandling.md
    // "Caller-distinguishing trick for selective suppression".
    internal static class InventoryScrollSuppressHelper
    {
        // Shared gate: returns true (suppress) only when this call is on the
        // scroll-driven path AND any of (Ctrl, LeftShift, RightShift) is held.
        internal static bool ShouldSuppressInventoryScroll()
        {
            if (Input.mouseScrollDelta.y == 0f) return false;
            bool ctrl       = KeyManager.GetButton(KeyCode.LeftControl)
                           || KeyManager.GetButton(KeyCode.RightControl);
            bool leftShift  = KeyManager.GetButton(KeyCode.LeftShift);
            bool rightShift = KeyManager.GetButton(KeyCode.RightShift);
            return ctrl || leftShift || rightShift;
        }
    }

    [HarmonyPatch(typeof(InventoryWindowManager), nameof(InventoryWindowManager.NextButton))]
    public class InventoryNextButtonScrollSuppressPatch
    {
        [UsedImplicitly]
        public static bool Prefix() => !InventoryScrollSuppressHelper.ShouldSuppressInventoryScroll();
    }

    [HarmonyPatch(typeof(InventoryWindowManager), nameof(InventoryWindowManager.PreviousButton))]
    public class InventoryPreviousButtonScrollSuppressPatch
    {
        [UsedImplicitly]
        public static bool Prefix() => !InventoryScrollSuppressHelper.ShouldSuppressInventoryScroll();
    }
}
