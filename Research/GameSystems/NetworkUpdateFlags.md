---
title: NetworkUpdateFlags
type: GameSystems
created_in: 0.2.6228.27061
verified_in: 0.2.6228.27061
verified_at: 2026-05-12
sources:
  - Mods/SprayPaintPlus/RESEARCH.md:116-118
  - Mods/SprayPaintPlus/RESEARCH.md:219-221
  - Plans/EquipmentPlus/RESEARCH.md:177-182
  - Mods/SprayPaintPlus/SprayPaintPlus/SprayPaintHelpers.cs:11-12
  - $(StationeersPath)\rocketstation_Data\Managed\Assembly-CSharp.dll :: Assets.Scripts.Objects.Thing (NetworkUpdateFlags, BuildUpdate/ProcessUpdate/BuildUpdateTransform/ProcessUpdateTransform/WriteTransform, SerializeDeltaState/DeserializeDeltaState, SerializeOnJoin/DeserializeNew, transform-mirror property setters)
  - $(StationeersPath)\rocketstation_Data\Managed\Assembly-CSharp.dll :: Assets.Scripts.Objects.Structure (WriteTransform, SerializeOnJoin/DeserializeOnJoin, BuildUpdate, CurrentBuildStateIndex setter)
related:
  - ../GameClasses/Consumable.md
  - ../GameClasses/Thing.md
  - ../GameSystems/PlacementOrientation.md
  - ../GameSystems/NetworkRoles.md
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

## Thing-level bit map and the transform bit (bit 1)
<!-- verified: 0.2.6228.27061 @ 2026-05-12 -->

The `Thing` base class owns the low bits; subclasses (`Structure`, `Cable`/logic units, `Item`, `DynamicThing`, devices, etc.) add their own bits above the Thing range, each handled in that subclass's `BuildUpdate`/`ProcessUpdate` override (which calls `base.BuildUpdate`/`base.ProcessUpdate` first). From `Assembly-CSharp` (0.2.6228.27061) — `Thing.BuildUpdate` (`:303247`), `Thing.ProcessUpdate` (`:303329`), `Thing.SerializeDeltaState` (`:303187`), and the various property setters:

| Bit (decimal) | Owner | Meaning / serialized block | Set by |
|---|---|---|---|
| `1` | `Thing` | transform delta — `BuildUpdateTransform` (`:303381`) writes `WriteVector3(Position); WriteQuaternion(Rotation)` (the `Position`/`Rotation` mirror fields, kept synced by the `ThingTransformPosition`/`ThingTransformRotation` setters at `:298397`/`:298426`); client `ProcessUpdateTransform` (`:303387`) reads them and does `if (!HasAuthority) ThingTransform.SetPositionAndRotation(...)` | **never set automatically by a transform write.** Set explicitly by `Slot.Take` for `DynamicThing` slot occupants (~`Slot.cs:292640`/`:292644`) and a handful of other special cases (a `CursorManager`-area site `:56523`, vehicle/rocket classes `:141801`/`:202841`); also `SerializeOnJoin` sets `NetworkUpdateFlags |= ushort.MaxValue` (`:303033`) so the join package includes everything |
| `2` | `Thing` | interactable-state delta — `BuildInteractableUpdate` (`:303291`) writes the dirty `Interactable`s | `Interactable.IsDirty` machinery |
| `4` | `Thing` | indestructable `DamageState` | damage code |
| `8` | `Thing` | `ReagentMixture` | reagent code (`VerifyUpdateType` at `:303230` clears this bit if `ReagentMixture == null`) |
| `16` | `Thing` | `IsBurning` flag | `IsBurning` setter (`:298530`) |
| `32` | `Thing` | `CustomColor` index + `CustomName` | `CustomName` setter (`:298559`) and color setters |
| `64` | `Structure` | `CurrentBuildStateIndex` (sbyte) — `Structure.BuildUpdate` (`:295569`) | `CurrentBuildStateIndex` setter (`:295484`, server only) |
| `128` | `Thing` | `EnergyConvected` + `EnergyRadiated` | thermal code |
| `0x1000` (4096 / `GenericFlag2`) | free | — | SprayPaintPlus piggybacks here on `Consumable.BuildUpdate` (see below) |
| `0x4000` (16384) | free in vanilla | — | EquipmentPlus active-sensor sync |

(Subclasses such as `CircuitHousing`/logic units reuse bits `512`/`1024`–`32768` for their own device lists — those overlap the "free" range above only for prefab families that don't carry that subclass's serialization, which is exactly why SprayPaintPlus / EquipmentPlus picked bits unused by `Consumable` / their target type.)

**Two distinct transform-serialization paths exist** and use different fields:
- **Per-tick delta (bit 1)**: `BuildUpdateTransform` → `Position` / `Rotation` (the live-transform mirror fields). Neither `Structure` nor `Cable` overrides `BuildUpdateTransform`/`ProcessUpdateTransform`.
- **Join package (full state)**: `Thing.SerializeOnJoin` (`:303031`) calls `WriteTransform(writer)`. `Thing.WriteTransform` (`:303025`) writes `ThingTransformPosition` / `ThingTransform.rotation`; **`Structure.WriteTransform` (`:295588`) overrides it to write `RegisteredPosition` / `RegisteredRotation`** instead. The joining client applies these via `Thing.DeserializeNew` (`:303168`) → `Create<Thing>(...)` + `SnapTransform(transformPosition, transformRotation)` (`:303176`/`:303179`). `Structure.SerializeOnJoin` (`:295594`) additionally writes `RegisteredLocalGrid` + `WriteQuaternion(Direction)`, read back by `Structure.DeserializeOnJoin` (`:295602`).

Consequence for a mod that moves/rotates a placed `Structure` server-side: the visible mesh follows `ThingTransform.rotation` immediately, but for the change to (a) save it must update `Structure.RegisteredRotation` (the save reads `RegisteredRotation`, not the live transform — `Structure.InitialiseSaveData` `:295797`), (b) reach already-connected clients it must set `NetworkUpdateFlags |= 1` server-side, and (c) reach late-joiners correctly it relies on `RegisteredRotation` (the join-package transform) — and `Structure.Direction` should be set too for the join-package `Direction` field. See `../GameSystems/PlacementOrientation.md` ("Changing a placed Structure's rotation at runtime") for the full recipe and the option comparison; `MoveToWorld` is **not** usable for this (it is `DynamicThing`'s inventory-eject op, a no-op for a non-slotted `Structure`).

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
- 2026-05-12: added "Thing-level bit map and the transform bit (bit 1)" section after a decompile pass on `Thing.NetworkUpdateFlags`/`BuildUpdate`/`ProcessUpdate`/`BuildUpdateTransform`/`ProcessUpdateTransform`/`WriteTransform`/`SerializeDeltaState`/`SerializeOnJoin`/`DeserializeNew` and `Structure.WriteTransform`/`SerializeOnJoin`/`DeserializeOnJoin`/`BuildUpdate` in `Assembly-CSharp.decompiled.cs` (0.2.6228.27061), plus a whole-file grep of `NetworkUpdateFlags |= 1` sites. Findings: enumerates the Thing-owned bits (`1` transform, `2` interactables, `4` damage, `8` reagent, `16` burning, `32` colour/name, `128` thermal), `Structure`'s `64` (build state); documents that bit 1 is **never** set automatically by a transform write (only by `Slot.Take` for slot occupants and a few special cases) and that two distinct transform-serialization paths exist (per-tick delta via `BuildUpdateTransform` → `Position`/`Rotation`; join package via `WriteTransform` → for `Structure`, `RegisteredPosition`/`RegisteredRotation`). Additive; cross-linked to `PlacementOrientation.md` (which carries the "re-roll a placed structure at runtime" recipe and option comparison). Found via investigating runtime re-rolling of a placed straight `Cable`; full dump in `.work/2026-05-12-cable-rotation/notes/mutation-findings.md`.

## Open questions

None.
