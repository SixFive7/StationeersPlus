---
title: NetworkUpdateFlags
type: GameSystems
created_in: 0.2.6228.27061
verified_in: 0.2.6228.27061
verified_at: 2026-04-20
sources:
  - Mods/SprayPaintPlus/RESEARCH.md:116-118
  - Mods/SprayPaintPlus/RESEARCH.md:219-221
  - Plans/EquipmentPlus/RESEARCH.md:177-182
  - Mods/SprayPaintPlus/SprayPaintPlus/SprayPaintHelpers.cs:11-12
related:
  - ../GameClasses/Consumable.md
  - ../Protocols/SprayPaintPlusNetworking.md
  - ../Protocols/EquipmentPlusNetworking.md
tags: [network, save-load]
---

# NetworkUpdateFlags

The `Thing.NetworkUpdateFlags` 16-bit bitmask and how vanilla serialization uses the low 12 bits, leaving a small band of free bits that mods can piggyback on to add custom per-thing sync data.

## Bitmask semantics
<!-- verified: 0.2.6228.27061 @ 2026-04-20 -->

`Thing.NetworkUpdateFlags` is a bitmask. Setting a bit causes the game's next network tick to include that object in the update broadcast. SprayPaintPlus uses bit 12 (`0x1000`, `GenericFlag2`) for spray can color updates. This piggybacks on the existing `Consumable.BuildUpdate`/`ProcessUpdate` serialization.

## Vanilla bit usage
<!-- verified: 0.2.6228.27061 @ 2026-04-20 -->

16-bit bitmask. Values through 0x0800 are used by Thing/DynamicThing/Item for standard state (position, rotation, damage, color, access, etc.). EquipmentPlus uses 0x4000 for active-sensor sync. `BuildUpdate` and `ProcessUpdate` are called by the network layer; each flag bit causes the corresponding data block to be written/read.

## GenericFlag2 (bit 12) for SprayPaintPlus color sync
<!-- verified: 0.2.6228.27061 @ 2026-04-20 -->

Bit 12 of `NetworkUpdateFlags` (`GenericFlag2`) was chosen because it is unused by `Consumable`'s vanilla serialization. Setting this flag triggers a network update that includes the spray can's data, and the postfix patches append the color index to that data.

From `SprayPaintHelpers.cs`:

```
// Network flag for custom spray can color sync (bit 12 = GenericFlag2).
```

## Verification history
<!-- verified: 0.2.6228.27061 @ 2026-04-20 -->

- 2026-04-20: page created from the Research migration; verbatim content lifted from F0029a (primary), F0026, F0126a, and F0387.

## Open questions

None at creation.
