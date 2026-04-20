---
title: Stationeers namespace pitfalls
type: Patterns
created_in: 0.2.6228.27061
verified_in: 0.2.6228.27061
verified_at: 2026-04-20
sources:
  - Mods/PowerTransmitterPlus/RESEARCH.md:613-619 (F0049, primary)
  - Mods/SprayPaintPlus/SprayPaintPlus/ConsumableSyncPatch.cs:6 (F0388)
related: []
tags: []
---

# Stationeers namespace pitfalls

Reference table of game types whose namespace is easy to guess wrong. All verified against game version 0.2.6228.27061. Compile errors of the form `using <namespace>;` followed by "type not found" usually trace back to one of these.

## Reference
<!-- verified: 0.2.6228.27061 @ 2026-04-20 -->

F0049 (Mods/PowerTransmitterPlus/RESEARCH.md:613-619, primary):

| Type | Namespace | Common mistake |
|---|---|---|
| `EnumCollection<,>` | `Assets.Scripts` | Not `Assets.Scripts.Util` |
| `ProgrammableChip` | `Assets.Scripts.Objects.Electrical` | Not `Motherboards` |
| `ProgrammableChip.Constant` | nested in `ProgrammableChip` | Must qualify as `ProgrammableChip.Constant` |
| `LogicType` | `Assets.Scripts.Objects.Motherboards` | (Most Logic types ARE in Motherboards, just not the chip) |
| `PowerTransmitterVisualiser` | global namespace | NOT `Assets.Scripts.Objects.Electrical` despite the dish being there |

F0388 (Mods/SprayPaintPlus/SprayPaintPlus/ConsumableSyncPatch.cs:6) adds:

- `Consumable` (used for spray cans, fuel canisters, welding torches) resides in `Assets.Scripts.Objects.Items`.

## When to consult this
<!-- verified: 0.2.6228.27061 @ 2026-04-20 -->

- After a compile error immediately following a freshly added `using Assets.Scripts.<guess>;`.
- Before writing a Harmony patch against a game type whose namespace you inferred from file-system layout or a file name.
- When decompiled code elides the namespace: ILSpy and dnSpy both show `LogicType` without its enclosing namespace in some views.

Add entries here as new namespace surprises are discovered. The table is small by design; add only types that have actually tripped someone.

## Cited verifications
<!-- verified: 0.2.6228.27061 @ 2026-04-20 -->

- F0049: primary table covering the five highest-surprise entries encountered in PowerTransmitterPlus.
- F0388: `Consumable` namespace note from SprayPaintPlus code comment.

## Verification history
<!-- verified: 0.2.6228.27061 @ 2026-04-20 -->

- 2026-04-20: page created from the Research migration; F0049 primary, F0388 additional.

## Open questions

None at creation.
