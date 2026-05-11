---
title: ConveyorBeltCutContent
type: GameSystems
created_in: 0.2.6228.27061
verified_in: 0.2.6228.27061
verified_at: 2026-05-11
sources:
  - .work/decomp/0.2.6228.27061/Assembly-CSharp.decompiled.cs (grep -i conveyor; lines 232931, 244057, 276966, 277069-277079, 280063, 281986, 352844, 354516)
  - E:\Steam\steamapps\common\Stationeers\rocketstation_Data\StreamingAssets\Language\english.xml lines 955-956, 1436-1437, 2183-2210
  - E:\Steam\steamapps\common\Stationeers\rocketstation_Data\StreamingAssets\Language\en.resx lines 1226-1228, 1968-1974, 3138+
  - E:\Steam\steamapps\common\Stationeers\rocketstation_Data\StreamingAssets\Data\paints.xml lines 176, 253
  - Mods/NetworkPuristPlus/RESEARCH.md:18, TODO.md:5
related:
  - ../GameClasses/MultiMergeConstructor.md
  - ../GameClasses/Structure.md
tags: [prefab, dead-end]
---

# ConveyorBeltCutContent

The `StructureConveyor*` "Flexi-belt" conveyor prefabs are cut content from very early Stationeers (the predecessor of the chute / pneumatic network). The prefabs and their kit still ship registered, with full names and Stationpedia flavor text, but no conveyor-specific C# class exists anymore. They are not in any tech tree or working build kit; the only way to instantiate one is the creative-mode prefab spawner, and "building" one throws because the construction wiring is gone. Relevant to Network Purist Plus TODO item "decide whether to also remove `StructureConveyorStraightLong`": removing it is a "hide a vestige" task, not the "neuter a working feature" task the long pipe/chute/cable variants are.

## The conveyor prefab family
<!-- verified: 0.2.6228.27061 @ 2026-05-11 -->

Five conveyor structure prefabs plus one kit item plus one supply crate, all present in the language tables (`english.xml`, `en.resx`) and `paints.xml` (the paintable-things list):

| Prefab name | Display name (`english.xml`) |
|---|---|
| `StructureConveyorStraight` | Conveyor (Straight) |
| `StructureConveyorStraightLong` | Conveyor (Straight Long) |
| `StructureConveyorStraightShort` | Conveyor (Straight Short) |
| `StructureConveyorCorner` | Conveyor (Corner) |
| `StructureConveyorRiser` | Conveyor (Riser) |
| `ItemKitConveyor` | Kit (Conveyors) |
| `DynamicCrateConveyorSupplies` | Crate (Conveyor Supplies) |

Shared Stationpedia description on every `StructureConveyor*` entry (verbatim from `english.xml`):

> Originally designed by {LINK:Recurso;Recurso} for ore-shifting, Flexi-belt conveyor solutions can be customized to specific uses for facilitating efficient transportation workflows. In other words, they move stuff between places.
> An recurring {LINK:Stationeers;Stationeer} complaint focuses on the fact that every stage of a conveyor system must be individually powered. In the words of Recurso customer service agents through the ages: 'We're working on that.'

`ItemKitConveyor` description (`en.resx`): "This kit produces conveyor elements, such as the {THING:StructureConveyorStraight}." `Recurso` is a value in `enum LoreFactions` (Assembly-CSharp ~line 232931), not a structure class.

There is no `class StructureConveyor`, `class StructureConveyorStraightLong`, `class ItemKitConveyor`, `class Conveyor`, or `class Conveyer` anywhere in `Assembly-CSharp.dll`. `Prefab.Find<Item>("ItemKitConveyor")` is cast to plain `Item`. So the kit prefab is a bare `Item` (or `Constructor`/`MultiConstructor` shell with nothing valid in `Constructables`) and the structure prefabs are bare `Structure` instances; the asset YAML in the assetbundles, not the DLL, holds whatever component config remains. None of it is exercised by any working code path.

## What remains in code (the leftover inventory)
<!-- verified: 0.2.6228.27061 @ 2026-05-11 -->

An exhaustive `grep -i conveyor` of `Assembly-CSharp.decompiled.cs` (v0.2.6228.27061) returns only these references, and none of them is a conveyor structure class:

- `LoreFactions.Recurso` (~line 232931) - the lore faction the flavor text credits. Not conveyor-specific.
- `new AudioClipsConcurrency("Conveyors", 6, canPause: true, ConcurrencyResolutionRule.StopFarthest)` (~line 244057) - a registered audio-concurrency bucket named "Conveyors". No code plays into it.
- `CrateType.ConveyorSupplies` (enum, ~line 276966) and its spawn case (~lines 277069-277079): `case CrateType.ConveyorSupplies` adds two `Prefab.Find<Item>("ItemKitConveyor")` to the crate contents. So the supply crate works, but it dispenses a kit item that builds nothing.
- `DynamicThing.OnConveyor` (`public bool`, ~line 280063), read once in the physics tick (~line 281986): `if (!OnConveyor) { RigidBody.angularDrag = _angularDrag; }`. Nothing in the DLL ever sets `OnConveyor = true`; it is a vestige of the era when items rode belts.
- `DeviceImportExport.ImportConveyorPosition` (`Transform`, ~line 354516) and `DeviceImportExport2.ExportConveyorPosition2` (`Transform`, ~line 352844). Misleading name: `DeviceImportExport` is the machine import/export-slot system (autolathe, furnace, etc.) and these fields sit alongside `Chute ImportChute` / `Chute ExportChute2` - they are chute drop-point transforms, not belt-system anything. (`MiningBelt` / `ToolBelt` near lines 288137 / 304608 are wearable items, also unrelated.)

No `[Obsolete]` marker, no deprecation comment - the prefabs were simply orphaned when the chute network replaced the conveyor belt; the C# was deleted, the assets and string keys were not.

## Why creative-mode spawn works but building errors
<!-- verified: 0.2.6228.27061 @ 2026-05-11 -->

The prefabs are still registered with `Prefab.AllPrefabs` (otherwise old saves and the prefab tables would break), so the creative-mode "spawn thing" UI can instantiate `StructureConveyorStraightLong` directly into the world as a placed `Structure`. But attempting to *construct* one (the right-mouse build/commit action, whether via the kit or the build cursor) runs the normal `Constructor` / `MultiConstructor` / `Structure` placement pipeline against a prefab whose build-state / kit / component wiring was never finished or has rotted, so it throws partway through (null component refs, empty `Constructables`, missing build states). Treat conveyors as non-functional: spawnable for screenshots, not buildable, not part of progression. The exact exception is asset-data dependent and not visible in the DLL.

Implication for mods (Network Purist Plus and similar): `StructureConveyorStraightLong` is not in any `MultiMergeConstructor.Constructables` list the way the long pipe/chute/cable variants are, is not merge-able, and has no working build kit. "Removing" it from a mod means hiding the orphaned prefabs from the creative spawner and the Stationpedia (set `Thing.HideInStationpedia = true`, optionally drop them from any creative-spawn list), not stripping a kit option or rejecting a merge. Lower value and lower risk than the long-variant work.

## Verification history
<!-- verified: 0.2.6228.27061 @ 2026-05-11 -->

- 2026-05-11: page created. Triggered by a question about `StructureConveyorStraightLong` (only spawnable from creative mode, errors on build). Verified by exhaustive `grep -i conveyor` of `Assembly-CSharp.decompiled.cs` (v0.2.6228.27061) - only the five leftover references listed above, no conveyor structure class - plus direct reads of `english.xml`, `en.resx`, `paints.xml` for the prefab/kit/crate string entries and the Recurso flavor text.

## Open questions
<!-- verified: 0.2.6228.27061 @ 2026-05-11 -->

- The concrete component class on each `StructureConveyor*` prefab and on `ItemKitConveyor` (bare `Structure` / `Item`, or a `Constructor` / `MultiConstructor` shell) is in the assetbundle YAML, not the DLL; not confirmed here. The "no conveyor-specific C# class" claim is confirmed from the DLL regardless.
- The exact exception thrown when building a conveyor is not captured (asset-data dependent). Only the high-level cause - incomplete/rotted construction wiring on an orphaned prefab - is established.
