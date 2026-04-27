---
title: Scroll Input Handling
type: GameSystems
created_in: 0.2.6228.27061
verified_in: 0.2.6228.27061
verified_at: 2026-04-27c
sources:
  - rocketstation_Data/Managed/Assembly-CSharp.dll :: Assets.Scripts.Inventory.InventoryManager.CheckDisplaySlotInput
  - rocketstation_Data/Managed/Assembly-CSharp.dll :: Assets.Scripts.Inventory.InventoryManager.NormalMode
  - rocketstation_Data/Managed/Assembly-CSharp.dll :: Assets.Scripts.Inventory.InventoryManager.PrecisionPlacementMode
  - rocketstation_Data/Managed/Assembly-CSharp.dll :: Assets.Scripts.UI.InventoryWindowManager (NextButton, PreviousButton)
  - rocketstation_Data/Managed/Assembly-CSharp.dll :: CameraController.CacheCameraPosition (covered in ../GameClasses/CameraController.md)
  - workshop/content/544550/3616788907/BetterHeadlampMod.dll :: BetterHeadlampMod.Patches.HeadlampScrollPatch
related:
  - ../GameClasses/CameraController.md
tags: [ui]
---

# Scroll Input Handling

The mouse scroll wheel feeds several vanilla Stationeers consumers in parallel. Each consumer reads `Input.mouseScrollDelta` (or a derived field) independently in the same frame, with its own gating logic. There is no central scroll dispatcher, so a mod that wants to bind a modifier+scroll combo without bleeding into vanilla behavior must understand each consumer's read site and gate condition.

## Vanilla scroll consumers
<!-- verified: 0.2.6228.27061 @ 2026-04-27 -->

| Consumer | Method | Source | Read expression | Gate |
|---|---|---|---|---|
| Inventory hotbar advance | `InventoryManager.CheckDisplaySlotInput` | line 269443-269454 | `__instance.newScrollData` (set at 269412 from `Input.mouseScrollDelta.y / 10f`) | `CurrentMode != Mode.Placement`. Calls `InventoryWindowManager.NextButton/PreviousButton`. |
| Construction panel select | `InventoryManager.CheckDisplaySlotInput` | line 269415-269429 | `newScrollData` | `!KeyManager.GetButton(KeyMap.QuantityModifier)` AND `ConstructionPanel.IsVisible`. Calls `ConstructionPanel.SelectUp/Down`. |
| Camera zoom + viewpoint toggle | `CameraController.CacheCameraPosition` | line 185660-185674 | `Input.mouseScrollDelta.y` | `!Cursor.visible`, active hand not a `Tablet`, `KeyManager.GetButton(KeyMap.ThirdPersonControl)` (default LeftShift, inclusive), `Settings.CurrentData.MouseWheelZoom`, not over UI. See `../GameClasses/CameraController.md` "Camera zoom and first / third-person toggle". |
| Precision placement zoom | `InventoryManager.PrecisionPlacementMode` | line 270301 | `Input.mouseScrollDelta.y` | Active mode = `Mode.PrecisionPlacement` |
| Cartridge UI line-select | `Cartridge.OnScroll` | (Unity EventSystem) | `Vector2 scrollDelta` parameter | Cursor over the cartridge `ScrollPanel`. EquipmentPlus's `ConfigCartridgeScrollPatch` prefixes this. |
| Ore detector mode cycle | `OreDetector.HandleScreenInput` | line 166064-166119 (per agent earlier) | `Input.mouseScrollDelta` | Active hand holds OreDetector |

`InventoryManager.newScrollData` is a `float` field on the inventory manager instance, set every frame at the top of `CheckDisplaySlotInput` from `Input.mouseScrollDelta.y / 10f`. It is read both within that method (the consumers in rows 1-2) and externally by other code that wants the post-divided value.

## InventoryManager.NormalMode is NOT a scroll consumer
<!-- verified: 0.2.6228.27061 @ 2026-04-27 -->

`InventoryManager.NormalMode` (line 270043) is the cursor-mode handler: mining, primary-use, secondary-use, item pickup. It does not read `Input.mouseScrollDelta` and does not branch on `newScrollData`. The handler runs every frame from `ManagerUpdate` (line 269292) when `CurrentMode == Mode.Normal`.

`ManagerUpdate` ordering is `CheckDisplaySlotInput()` → switch on `CurrentMode` (which calls `NormalMode()` for normal mode). So `newScrollData` has already been read AND the inventory consumers (rows 1-2 above) have already fired by the time `NormalMode` starts.

Implication for mods: a Harmony prefix on `NormalMode` that reads `__instance.newScrollData` observes the scroll value, but cannot prevent the vanilla inventory advance that already happened in the same frame. This is the "scroll bleeds into inventory" effect that affects [`BetterHeadlampMod.Patches.HeadlampScrollPatch`](#better-headlamp-pattern-for-reference).

### Cursor-visible gate (and CurrentMode dispatch)
<!-- verified: 0.2.6228.27061 @ 2026-04-27 -->

Both `CheckDisplaySlotInput()` and the `switch (CurrentMode)` dispatch sit inside an early-return guard at the top of `ManagerUpdate` (line 269276):

```csharp
if (Cursor.visible || Parent.IsUnresponsive || ConsoleWindow.IsOpen)
{
    return;
}
CheckDisplaySlotInput();
// ... slot-key handling ...
if (Parent.State == EntityState.Alive && IsAllowedToLook() && IsParentSafe() && !Stationpedia.IsOpenAndLocked)
{
    switch (CurrentMode)
    {
    case Mode.Normal:           NormalMode();           break;
    case Mode.Placement:        PlacementMode();        break;
    case Mode.PrecisionPlacement: PrecisionPlacementMode(); break;
    }
}
```

`Cursor.visible == true` happens whenever the player is in mouse-control mode (e.g. Alt held to release cursor), the inventory window is open, the Stationpedia is up, etc. While the cursor is visible, NEITHER scroll consumer (CheckDisplaySlotInput, NormalMode/Placement/PrecisionPlacement) runs. A mod prefixing `InventoryManager.NormalMode` therefore never fires during mouse-mode, regardless of whether the user scrolls.

Same-frame implication: even if a vanilla binding (e.g. `KeyMap.MouseInspect`, default LeftShift) is checked while in mouse-mode, a `Mode.Normal`-only mod cannot be running concurrently. See [`./KeyBinding.md`](./KeyBinding.md) "MouseInspect: a vanilla LeftShift binding that does NOT conflict with bare-Shift mods" for one such example.

## Patch entry points for scroll capture
<!-- verified: 0.2.6228.27061 @ 2026-04-27 -->

Three places a mod can capture mouse-scroll input, each with different observe / suppress trade-offs:

| Entry point | Observes? | Can suppress vanilla? | Notes |
|---|---|---|---|
| `InventoryManager.NormalMode` Prefix | yes (`__instance.newScrollData`) | no for inventory advance (already happened) | Better Headlamp uses this; bleeds into inventory |
| `InventoryManager.CheckDisplaySlotInput` Prefix | yes (`Input.mouseScrollDelta` directly) | yes if Prefix returns false | Heavy: also suppresses hotkey checks (`KeyMap.NextItem/PreviousItem`) and per-slot interaction-key polling. Use only if the entire method's other behaviors are also acceptable to skip |
| `InventoryWindowManager.NextButton` / `PreviousButton` Prefix | indirect (via `Input.mouseScrollDelta.y != 0f` heuristic) | yes if Prefix returns false | Surgical: only suppresses the scroll-driven inventory advance, not the keyboard-hotkey path. See "Caller-distinguishing trick" below |

## InventoryWindowManager._visibleSlots cycle (the plain-scroll target)
<!-- verified: 0.2.6228.27061 @ 2026-04-27 -->

`InventoryWindowManager.NextButton()` / `PreviousButton()` (the methods called from `CheckDisplaySlotInput` on plain scroll) do NOT cycle through every inventory slot in the game. They cycle through a static `private static List<SlotDisplayButton> _visibleSlots` (line 225387), advancing `CurrentSlotIndex` and updating `CurrentScollButton` to the new entry.

`_visibleSlots` only contains buttons from windows that are CURRENTLY VISIBLE. Population path:

1. `InventoryWindow.SetSlots()` (line 225166) iterates `Parent.Slots`. For each slot where `slot.IsInteractable == true`, instantiates a `SlotDisplayButton`, adds it to the window's local `DisplayedSlots` list, and calls `InventoryWindowManager.Register(button)` which adds it to the global `AllButtons` master list. **Slots with `IsInteractable == false` are not represented at all** (no button instantiated, no entry anywhere).

2. `InventoryWindow.SetVisible(true)` (line 225260) calls `InventoryWindowManager.Instance.RegisterVisibleSlots(this)` (line 225317). `RegisterVisibleSlots` (line 225481) appends the window's `DisplayedSlots` and `DisplayedInteractions` to `_visibleSlots`. **This is the only path that adds to `_visibleSlots`** — `Register(button)` does not.

Closing a window (`SetVisible(false)`) calls `UnregisterVisibleSlots(this)` which removes the window's buttons from `_visibleSlots`.

Implication: The scroll cycle only includes slots from windows the player has explicitly opened. A child inventory window (e.g. the tablet's cartridge window or the worn-lens chip window) is by default NOT open — the player must click the parent slot's "open" button or otherwise toggle the window visible. Until they do, scrolling through the main inventory cycle skips those slots entirely. This is vanilla behavior, not a mod side effect.

### HandleOccupantChange teardown semantics
<!-- verified: 0.2.6228.27061 @ 2026-04-27 -->

`InventoryWindow.HandleOccupantChange()` (line 225211) is the canonical "rebuild this window's slot widgets" entry point. Sequence:

1. `ClearSlots()` — for each button in `DisplayedSlots`, calls `InventoryWindowManager.Deregister(button)` (which removes from BOTH `AllButtons` AND `_visibleSlots`, line 225528) then destroys the button GameObject.
2. `ClearInteractions()` — same for `DisplayedInteractions`.
3. `ClearChildWindows()` — destroys nested windows.
4. `SetSlots()` — rebuilds DisplayedSlots (each new button → `Register(button)` → adds to `AllButtons` only).
5. `SetInteractions()` — rebuilds DisplayedInteractions.

**Critical asymmetry**: teardown removes buttons from `_visibleSlots`, but rebuild does NOT re-add them. After a `HandleOccupantChange` call, `DisplayedSlots` and `AllButtons` are populated, but `_visibleSlots` is missing the new buttons unless `SetVisible(true)` is invoked afterwards (which is the only path that calls `RegisterVisibleSlots`).

A mod that invokes `HandleOccupantChange` directly (e.g. to refresh the widget set after dynamic-slot state changes while the window is open) must follow up with `InventoryWindowManager.Instance.RegisterVisibleSlots(window)` to keep the buttons in the scroll cycle. Otherwise the window's slots become invisible to plain-scroll cycling for the rest of the time the window is open.

## Caller-distinguishing trick for selective suppression
<!-- verified: 0.2.6228.27061 @ 2026-04-27 -->

`InventoryWindowManager.NextButton` and `PreviousButton` are called from two places per frame in vanilla:

1. `InventoryManager.CheckDisplaySlotInput` line 269443-269454 (scroll-driven, conditional on `newScrollData != 0`).
2. `InventoryManager.CheckDisplaySlotInput` line 269435-269442 (keyboard-hotkey-driven, conditional on `KeyManager.GetButtonDown(KeyMap.NextItem)` / `KeyMap.PreviousItem`).

To suppress only the scroll-driven call, a Prefix on `NextButton`/`PreviousButton` can check `Input.mouseScrollDelta.y != 0f`: if true, the call is scroll-driven this frame; if false, it is keyboard-hotkey driven (or no-op). Returning false on the scroll path with a modifier-active gate suppresses scroll-driven inventory advance without breaking keyboard hotkeys.

## Better Headlamp pattern (for reference)
<!-- verified: 0.2.6228.27061 @ 2026-04-27 -->

Verbatim Prefix from `BetterHeadlampMod.Patches.HeadlampScrollPatch` (BetterHeadlampMod.dll line 262-291):

```csharp
[HarmonyPatch(typeof(InventoryManager), "NormalMode")]
public static class HeadlampScrollPatch
{
    [UsedImplicitly]
    public static void Prefix(InventoryManager __instance)
    {
        bool flag = __instance.newScrollData > 0f;
        bool flag2 = __instance.newScrollData < 0f;
        if ((!flag && !flag2)
            || (!KeyManager.GetButton((KeyCode)306) && !KeyManager.GetButton((KeyCode)305)))
        {
            return;
        }
        Human localHuman = Human.LocalHuman;
        if ((Object)(object)localHuman == (Object)null
            || !HeadLampAdjustableBeamPatch.HasActiveHelmetLight(localHuman))
        {
            return;
        }
        int direction = ((!flag) ? 1 : (-1));
        HeadLampAdjustableBeamPatch.AdjustFocusExternal(direction);
        SlotDisplay activeHand = __instance.ActiveHand;
        if (activeHand != null)
        {
            Slot slot = activeHand.Slot;
            if (slot != null)
            {
                slot.RefreshSlotDisplay();
            }
        }
    }
}
```

Key facts:

- Return type is `void`, not `bool`. The patch cannot return false to suppress vanilla.
- Modifier read uses `KeyManager.GetButton(KeyCode)` with raw key codes 305 (RightShift) and 306 (LeftShift). This is **inclusive matching**: the Shift branch fires whenever LeftShift OR RightShift is held, regardless of other modifiers (Ctrl, Alt) being also held.
- Direction convention: wheel-up (`flag = newScrollData > 0`) maps to `direction = -1`; wheel-down to `+1`. Same convention as EquipmentPlus's `Cartridge.OnScroll` line-select scroll.
- Side effect: calls `__instance.ActiveHand.Slot.RefreshSlotDisplay()` after every adjust. Probably to refresh any held-item visual state (e.g. a held tool's icon update); for our scroll handlers in `EquipmentPlus`, this call may not be needed.

## Verification history
<!-- verified: 0.2.6228.27061 @ 2026-04-27 -->

- 2026-04-27: page created. Verbatim findings from a fresh `ilspycmd` decompile of `E:/Steam/steamapps/common/Stationeers/rocketstation_Data/Managed/Assembly-CSharp.dll` and `E:/Steam/steamapps/workshop/content/544550/3616788907/BetterHeadlampMod.dll` at game version 0.2.6228.27061. Triggered by EquipmentPlus's planned scroll-modifier rebinds (`Plans/EquipmentPlus/TODO.md` item B). The "InventoryManager.NormalMode is NOT a scroll consumer" section corrects an earlier subagent-produced report that recommended `NormalMode` as the suppression entry point; the actual scroll-read site is `CheckDisplaySlotInput`. The `CameraController.CacheCameraPosition` row in the consumers table is a pointer to `../GameClasses/CameraController.md` "Camera zoom and first / third-person toggle (Shift+scroll handler)" (curated 2026-04-27 in the same investigation).
- 2026-04-27b: added "Cursor-visible gate (and CurrentMode dispatch)" subsection under "InventoryManager.NormalMode is NOT a scroll consumer". Documents the early-return guard at line 269276 that suppresses BOTH scroll read and dispatch when `Cursor.visible || Parent.IsUnresponsive || ConsoleWindow.IsOpen`, plus the `switch (CurrentMode)` that routes to NormalMode / PlacementMode / PrecisionPlacementMode. Explains why a `Mode.Normal`-only scroll mod never runs concurrently with mouse-mode-gated vanilla bindings (such as `KeyMap.MouseInspect`). Triggered by the same EquipmentPlus question that produced the MouseInspect section in `./KeyBinding.md`. Additive subsection; no prior content changed.
- 2026-04-27c: added "InventoryWindowManager._visibleSlots cycle (the plain-scroll target)" section plus "HandleOccupantChange teardown semantics" subsection. Documents how plain-scroll inventory cycling chooses its target — namely the static `_visibleSlots` list populated via `RegisterVisibleSlots(window)` only on `InventoryWindow.SetVisible(true)`, never on `Register(button)` alone. Triggered by an EquipmentPlus user observation that scrolling skipped tablet/lens child-window contents; verified that this is vanilla behavior (child windows not visible by default → their slots not in `_visibleSlots`), and additionally identified an asymmetric teardown/rebuild bug: `HandleOccupantChange` calls `Deregister` (which removes from `_visibleSlots`) during teardown but only `Register` (which only touches `AllButtons`) during rebuild — so any direct invocation of `HandleOccupantChange` orphans the new buttons from the scroll cycle until `RegisterVisibleSlots` is called again. Verbatim from `Assembly-CSharp.dll` lines 225211-225231 and 225260-225323. Additive section; no prior content changed.

## Open questions

None at creation.
