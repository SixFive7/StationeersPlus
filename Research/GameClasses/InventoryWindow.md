---
title: InventoryWindow
type: GameClasses
created_in: 0.2.6228.27061
verified_in: 0.2.6228.27061
verified_at: 2026-04-20
sources:
  - Plans/EquipmentPlus/RESEARCH.md:245-248
  - Plans/EquipmentPlus/EquipmentPlus/WindowOpenRefreshPatch.cs:10-30
  - $(StationeersPath)\rocketstation_Data\Managed\Assembly-CSharp.dll :: InventoryWindow
related:
  - ./AdvancedTablet.md
  - ./SensorLenses.md
tags: [ui, slots]
---

# InventoryWindow

Vanilla game class rendering the inventory window for a held multi-slot item (tablet, lenses). Instantiates `SlotDisplayButton` widgets only once, at `SetSlots` time, based on each slot's `IsInteractable` at that instant.

## Widget staleness
<!-- verified: 0.2.6228.27061 @ 2026-04-20 -->

Source: F0121.

`InventoryWindow.SetSlots` only instantiates `SlotDisplayButton` widgets for slots where `IsInteractable == true` at the time it runs. Later changes to `IsInteractable` do not create or remove widgets. Must force `HandleOccupantChange` after any slot state change for the widget list to match.

When the window is closed (GameObject inactive), `FindObjectsOfType<InventoryWindow>()` skips it, so rebuilds during close are silently lost. `WindowOpenRefreshPatch` catches the re-open and forces a rebuild.

### ToggleVisibility stale widgets comment (F0343)
<!-- verified: 0.2.6228.27061 @ 2026-04-20 -->

Source comment from `WindowOpenRefreshPatch.cs:10-30`:

```
/// <summary>
/// InventoryWindow.ToggleVisibility only flips the canvas on/off; it does
/// not re-run HandleOccupantChange or SetSlots. Widgets the user sees are
/// whatever SetSlots built during the last Assign (when the lens/tablet
/// first went into its parent slot) or the last time the parent slot's
/// OnOccupantChange fired.
///
/// While the window is closed, our OnChildEnterInventory Postfix does
/// normalise dynamic-slot state, but its <c>RebuildInventoryWindowFor</c>
/// uses <c>FindObjectsOfType&lt;InventoryWindow&gt;()</c> which skips
/// inactive GameObjects, so a closed window never receives the rebuild.
/// When the user later opens the window it shows the stale widget set:
/// if FixingTheControls "Clear Hands" filled two chip slots while closed,
/// the user sees one slot less than the number of chips present.
///
/// Hook ToggleVisibility as a Postfix so that the moment the window
/// becomes visible, we normalise slot state and force a full
/// HandleOccupantChange to rebuild the widget list against the
/// current slots. HandleOccupantChange is private; reached via
/// AccessTools reflection.
/// </summary>
```

## Verification history
<!-- verified: 0.2.6228.27061 @ 2026-04-20 -->

- 2026-04-20: page created from the Research migration; verbatim content lifted from F0121, F0343. No conflicts.

## Open questions

None at creation.
