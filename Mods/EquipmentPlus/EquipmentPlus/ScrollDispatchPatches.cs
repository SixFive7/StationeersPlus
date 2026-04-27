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
                            : "SP";
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

        // Phase 2: when active hand does NOT hold a tablet, try to put one
        // there. Decision tree per Plans/EquipmentPlus/TODO.md section B
        // Phase 2:
        //   1. Off-hand has tablet -> SwapHands (no item moves; subsequent
        //      scroll cycles on the now-active tablet).
        //   2. Search inventory (Toolbelt -> Backpack -> Suit, no nested) for
        //      first AdvancedTablet.
        //   3. If found AND active hand empty -> OnServer.MoveToSlot(tablet,
        //      activeHandSlot). Done.
        //   4. If found AND active hand occupied -> SmartStow(activeHandSlot)
        //      and start AutoEquipTabletCoroutine to check after one yield
        //      whether stow succeeded (then equip) or failed (then try swap;
        //      if swap also fails, ConsoleWindow.PrintError to local F3).
        //   5. No tablet anywhere -> silent no-op (log only).
        //
        // Multiplayer correctness: every move goes through OnServer.MoveToSlot
        // (universal entry point per Research/Patterns/InventoryAutoEquip.md).
        // The coroutine yields one frame between each act and check so server
        // round-trips on remote clients have time to land before we read the
        // resulting state.
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
                        $"[EquipmentPlus.scroll] tablet auto-equip: active hand empty, sending tablet={tablet.GetType().Name}#{tablet.ReferenceId} from src={DescribeSlot(sourceSlot)} -> activeHandSlot={DescribeSlot(activeHandSlot)}");
                OnServer.MoveToSlot(tablet, activeHandSlot);
                EquipmentPlusPlugin.Instance?.StartCoroutine(
                    PostMoveSanityCoroutine(activeHandSlot, tablet, "direct-equip"));
                return;
            }

            // Step 4: active hand occupied -> SmartStow then deferred-check.
            if (ScrollDispatchState.ScrollTrace)
                EquipmentPlusPlugin.Log.LogInfo(
                    $"[EquipmentPlus.scroll] tablet auto-equip: active hand occupied, src={DescribeSlot(sourceSlot)} active={DescribeSlot(activeHandSlot)} stowing {currentlyHeld.GetType().Name} then equipping {tablet.GetType().Name}#{tablet.ReferenceId}");
            // SmartStow(Slot) is a static method on InventoryManager (NOT
            // InventoryWindowManager — that's the instance no-arg overload
            // bound to the SmartStow keybind which finds the currently
            // hovered slot). For a specific slot, call InventoryManager
            // statically.
            InventoryManager.SmartStow(activeHandSlot);
            EquipmentPlusPlugin.Instance?.StartCoroutine(
                AutoEquipTabletCoroutine(activeHandSlot, sourceSlot, tablet, currentlyHeld));
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

        // Diagnostic-only: after a direct OnServer.MoveToSlot fires from the
        // client, poll the target slot for several frames and log when the
        // tablet actually lands. This pins down whether (a) it lands in the
        // expected slot eventually, (b) it lands in some other slot, or
        // (c) it never lands. Compare against the activeHandSlot we sent
        // and the inactive-hand slot side-by-side.
        private static IEnumerator PostMoveSanityCoroutine(
            Slot expectedSlot, DynamicThing tablet, string label)
        {
            if (!ScrollDispatchState.ScrollTrace) yield break;
            for (int i = 0; i < 30; i++)
            {
                yield return null;
                var actualParent = tablet?.ParentSlot?.Parent;
                var actualSlotIdx = tablet?.ParentSlot?.SlotIndex;
                if (actualParent == expectedSlot.Parent && actualSlotIdx == expectedSlot.SlotIndex)
                {
                    EquipmentPlusPlugin.Log.LogInfo(
                        $"[EquipmentPlus.scroll] tablet post-move ({label}): tablet#{tablet.ReferenceId} landed in EXPECTED slot {DescribeSlot(expectedSlot)} after {i + 1} frame(s)");
                    yield break;
                }
                if (i == 5 || i == 15 || i == 29)
                {
                    var localActive = InventoryManager.Instance?.ActiveHand?.Slot;
                    var localInactive = InventoryManager.Instance?.InactiveHand?.Slot;
                    EquipmentPlusPlugin.Log.LogInfo(
                        $"[EquipmentPlus.scroll] tablet post-move ({label}) frame {i + 1}: tablet#{tablet?.ReferenceId} actualParent={(actualParent != null ? actualParent.GetType().Name + "#" + actualParent.ReferenceId : "null")} slotIdx={actualSlotIdx} | localActive={DescribeSlot(localActive)} localInactive={DescribeSlot(localInactive)} | expected={DescribeSlot(expectedSlot)}");
                }
            }
        }

        // Multiplayer-safe deferred-check sequence after SmartStow. On the
        // host / single-player, SmartStow's MoveToSlot applies in-frame, so
        // the yield is harmless. On remote clients, SmartStow's MoveToSlot
        // is a network request; yielding one frame lets the server's
        // broadcast arrive before we read activeHandSlot.Get() to decide
        // what happened.
        private static IEnumerator AutoEquipTabletCoroutine(
            Slot activeHandSlot, Slot sourceSlot, DynamicThing tablet, DynamicThing prevOccupant)
        {
            yield return null;

            if (activeHandSlot.Get() == null)
            {
                if (ScrollDispatchState.ScrollTrace)
                    EquipmentPlusPlugin.Log.LogInfo("[EquipmentPlus.scroll] tablet auto-equip: stow succeeded after yield, equipping");
                OnServer.MoveToSlot(tablet, activeHandSlot);
                yield break;
            }

            // Stow failed. Try 3-way swap via the off-hand as temporary
            // location. Pure 2-step swap (prevOccupant -> sourceSlot, then
            // tablet -> activeHandSlot) doesn't work because OnServer.MoveToSlot
            // does NOT auto-swap when the target is occupied — the tablet is
            // still in sourceSlot when we try to put prevOccupant there, so
            // the move is rejected. See Research/Patterns/InventoryAutoEquip.md
            // "OnServer.MoveToSlot" for the verbatim no-swap behavior.
            //
            // Sequence: (1) tablet -> off-hand (temp), (2) prevOccupant ->
            // sourceSlot, (3) tablet -> activeHandSlot. Each step has a yield
            // for multiplayer correctness. If any step fails, restore prior
            // state where possible and notify the player.
            if (prevOccupant == null)
            {
                if (ScrollDispatchState.ScrollTrace)
                    EquipmentPlusPlugin.Log.LogInfo("[EquipmentPlus.scroll] tablet auto-equip: prevOccupant became null during yield, aborting swap");
                yield break;
            }

            var inactiveHandSlot = InventoryManager.Instance?.InactiveHand?.Slot;
            if (inactiveHandSlot == null || inactiveHandSlot.Get() != null)
            {
                if (ScrollDispatchState.ScrollTrace)
                    EquipmentPlusPlugin.Log.LogInfo("[EquipmentPlus.scroll] tablet auto-equip: 3-way swap blocked, off-hand also occupied (or unavailable)");
                ConsoleWindow.PrintError("[EquipmentPlus] No room to swap. Manually stow or drop the item in your active hand to equip the tablet.");
                yield break;
            }

            // Step 1: park tablet in off-hand.
            if (ScrollDispatchState.ScrollTrace)
                EquipmentPlusPlugin.Log.LogInfo($"[EquipmentPlus.scroll] tablet auto-equip: 3-way swap step 1/3 — tablet -> off-hand (temp)");
            OnServer.MoveToSlot(tablet, inactiveHandSlot);
            yield return null;

            if (inactiveHandSlot.Get() != tablet)
            {
                if (ScrollDispatchState.ScrollTrace)
                    EquipmentPlusPlugin.Log.LogInfo("[EquipmentPlus.scroll] tablet auto-equip: step 1 failed, off-hand rejected tablet");
                ConsoleWindow.PrintError("[EquipmentPlus] No room to swap. Manually stow or drop the item in your active hand to equip the tablet.");
                yield break;
            }

            // Step 2: move prevOccupant into the freed sourceSlot.
            if (ScrollDispatchState.ScrollTrace)
                EquipmentPlusPlugin.Log.LogInfo($"[EquipmentPlus.scroll] tablet auto-equip: 3-way swap step 2/3 — {prevOccupant.GetType().Name} -> source slot");
            OnServer.MoveToSlot(prevOccupant, sourceSlot);
            yield return null;

            if (sourceSlot.Get() != prevOccupant)
            {
                // Source slot rejected prevOccupant (slot-type mismatch).
                // Restore tablet to its source slot so the player's inventory
                // returns to the pre-attempt state.
                if (ScrollDispatchState.ScrollTrace)
                    EquipmentPlusPlugin.Log.LogInfo("[EquipmentPlus.scroll] tablet auto-equip: step 2 failed, source slot rejected prevOccupant; restoring tablet");
                OnServer.MoveToSlot(tablet, sourceSlot);
                ConsoleWindow.PrintError("[EquipmentPlus] No room to swap. Manually stow or drop the item in your active hand to equip the tablet.");
                yield break;
            }

            // Step 3: pull tablet from off-hand to active hand.
            if (ScrollDispatchState.ScrollTrace)
                EquipmentPlusPlugin.Log.LogInfo("[EquipmentPlus.scroll] tablet auto-equip: 3-way swap step 3/3 — tablet -> active hand");
            OnServer.MoveToSlot(tablet, activeHandSlot);
            yield return null;

            if (activeHandSlot.Get() != tablet)
            {
                // Shouldn't happen — active hand is empty (we moved prevOccupant
                // out in step 2) and the slot accepts the tablet by construction.
                // Defensive notice if it does.
                if (ScrollDispatchState.ScrollTrace)
                    EquipmentPlusPlugin.Log.LogInfo("[EquipmentPlus.scroll] tablet auto-equip: step 3 failed unexpectedly, active hand did not receive tablet");
                ConsoleWindow.PrintError("[EquipmentPlus] Tablet equip failed unexpectedly — please report this.");
                yield break;
            }

            if (ScrollDispatchState.ScrollTrace)
                EquipmentPlusPlugin.Log.LogInfo($"[EquipmentPlus.scroll] tablet auto-equip: 3-way swap complete — tablet in active hand, {prevOccupant.GetType().Name} in source slot");
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
