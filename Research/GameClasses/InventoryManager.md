---
title: InventoryManager
type: GameClasses
created_in: 0.2.6228.27061
verified_in: 0.2.6228.27061
verified_at: 2026-04-20
sources:
  - Plans/EquipmentPlus/RESEARCH.md:211-219
  - Plans/EquipmentPlus/EquipmentPlus/ClickCyclePatch.cs:14-30
  - $(StationeersPath)\rocketstation_Data\Managed\Assembly-CSharp.dll :: InventoryManager
related:
  - ./AdvancedTablet.md
  - ./SensorLenses.md
tags: [equipment, slots]
---

# InventoryManager

Vanilla game class routing per-frame primary-use clicks against held items. The correct hook point for intercepting click-based item interactions, because neither `SensorLenses` nor `AdvancedTablet` set `AllowSelfUse = true`.

## HandlePrimaryUse sequence
<!-- verified: 0.2.6228.27061 @ 2026-04-20 -->

Source: F0116.

Called every frame from input processing. Sequence for a held item:
1. If `CursorManager.CursorThing` is targetable and the held item supports `AttackWith` on it, call `AttackWith` (item-on-thing interaction).
2. Otherwise call `UseItemOnSelf(item)`, which early-returns when `item.AllowSelfUse == false`.
3. For `AllowSelfUse == true` items, `UseItemOnSelf` dispatches to `item.OnUsePrimary()`.

`AdvancedTablet.AllowSelfUse == false`, so patching `Item.OnUsePrimary` never fires for a plain tablet. `InventoryManager.HandlePrimaryUse` is the correct hook for tablet-level click intercepts because it runs regardless of `AllowSelfUse`. This is why the pre-refactor `CartridgeCyclePatch` on `Item.OnUsePrimary` never worked in practice, and why the current `ClickCyclePatch` targets `InventoryManager.HandlePrimaryUse`.

`KeyManager.GetMouseDown("Primary")` is a thin forwarder to `Input.GetKeyDown(KeyMap.PrimaryAction)`, frame-stable, idempotent across multiple reads within a single Update tick. Multiple patches can read it in the same frame without interference.

### ClickCyclePatch class header (F0334)
<!-- verified: 0.2.6228.27061 @ 2026-04-20 -->

Source comment from `ClickCyclePatch.cs:14-30`:

```
/// <summary>
/// Ctrl+left-click while holding a SensorLenses or AdvancedTablet cycles
/// to the next loaded chip / cartridge.
///
/// We patch <c>InventoryManager.HandlePrimaryUse</c> because neither
/// vanilla prefab sets <c>AllowSelfUse = true</c>, so clicks never reach
/// <c>Item.OnUsePrimary</c> via <c>UseItemOnSelf</c>. HandlePrimaryUse is
/// the per-frame click handler and runs on the clicking peer regardless
/// of the item's AttackWithEvent. Detecting modifier + click there fires
/// identically in single-player, on the host, and on a remote client --
/// no server-side dispatch concerns, no modifier-state mirroring needed.
///
/// When the cycle fires we return <c>false</c> to suppress the rest of
/// HandlePrimaryUse; vanilla would otherwise try an AttackWith on the
/// cursor target with our item, which for the tablet/lenses does nothing
/// useful anyway but we don't want the side effect.
/// </summary>
```

## Verification history
<!-- verified: 0.2.6228.27061 @ 2026-04-20 -->

- 2026-04-20: page created from the Research migration; verbatim content lifted from F0116, F0334. No conflicts.

## Open questions

None at creation.
