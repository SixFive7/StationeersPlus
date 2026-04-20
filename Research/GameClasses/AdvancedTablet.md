---
title: AdvancedTablet
type: GameClasses
created_in: 0.2.6228.27061
verified_in: 0.2.6228.27061
verified_at: 2026-04-20
sources:
  - Plans/EquipmentPlus/RESEARCH.md:168-174
  - Plans/EquipmentPlus/RESEARCH.md:242-243
  - Plans/EquipmentPlus/EquipmentPlus/ClickCyclePatch.cs:160-164
  - Plans/EquipmentPlus/EquipmentPlus/DynamicSlots.cs:202-215
  - $(StationeersPath)\rocketstation_Data\Managed\Assembly-CSharp.dll :: AdvancedTablet
related:
  - ./Thing.md
  - ./InventoryManager.md
  - ./Cartridge.md
  - ./ConfigCartridge.md
  - ../Patterns/HarmonyInheritedMethods.md
tags: [equipment, ic10, slots]
---

# AdvancedTablet

Vanilla game class representing the player's cartridge-driven tablet. Inherits from `Item` (via `DynamicThing`). Holds a cached `CartridgeSlots` list built once at Awake.

## Fields
<!-- verified: 0.2.6228.27061 @ 2026-04-20 -->

Source: F0114.

- Inherits from Item (via DynamicThing).
- `CartridgeSlots`: `List<Slot>` cached at Awake by scanning `Slots` for `Type == Cartridge`. Never rebuilt after Awake.
- `Mode`: index into `CartridgeSlots`. Propagated via `Interactable.State`. Not saved.
- `Cartridge`: property returning the occupant of `CartridgeSlots[Mode]`.
- `GetCartridge()`: private method that refreshes the cartridge display. Called by vanilla's Next/Prev InteractWith but not by `Mode` setter.
- Vanilla ships with Battery, ProgrammableChip, and 2 Cartridge slots.

## CartridgeSlots cache staleness
<!-- verified: 0.2.6228.27061 @ 2026-04-20 -->

Source: F0120.

`AdvancedTablet.CartridgeSlots` is built once at Awake. Runtime slot-type changes (Blocked to Cartridge or vice versa) do not update the cache. Vanilla's `GetCartridge`, `Next`, `Prev`, and `InteractWith` all iterate the cache. Must rebuild it explicitly after every slot state change (`DynamicSlots.SyncTabletCartridgeSlots`).

### Mode does not call GetCartridge (F0337)
<!-- verified: 0.2.6228.27061 @ 2026-04-20 -->

Source comment from `ClickCyclePatch.cs:160-164`:

```
// Thing.set_Mode propagates via the networked Interactable state,
// but it does NOT call GetCartridge() -- which is the method that
// refreshes the displayed cartridge screen. Vanilla's Next-button
// InteractWith path calls both; so do we. GetCartridge is private,
// hence reflection via AccessTools.
```

### SyncTabletCartridgeSlots rationale (F0341)
<!-- verified: 0.2.6228.27061 @ 2026-04-20 -->

Source comment from `DynamicSlots.cs:202-215`:

```
/// <summary>
/// AdvancedTablet caches a `CartridgeSlots` list built once in Awake()
/// by scanning Slots for Type==Cartridge. Vanilla's Next/Prev/Mode
/// cycling, the Cartridge getter, GetExtendedText, and InteractWith
/// all iterate the cache rather than Slots. Runtime Type changes
/// don't touch the cache, which orphans our unlocked slots from
/// vanilla's cartridge machinery.
///
/// Rebuild the cache from scratch after every slot-state change,
/// preserving Mode by re-indexing the currently-displayed Cartridge
/// into the new list. If that cartridge is no longer in a Cartridge
/// slot (shouldn't happen, but defensive), Mode falls back to 0.
/// </summary>
```

## Verification history
<!-- verified: 0.2.6228.27061 @ 2026-04-20 -->

- 2026-04-20: page created from the Research migration; verbatim content lifted from F0114, F0120, F0337, F0341. No conflicts.

## Open questions

None at creation.
