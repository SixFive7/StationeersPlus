---
title: Wall
type: GameClasses
created_in: 0.2.6228.27061
verified_in: 0.2.6228.27061
verified_at: 2026-04-28
sources:
  - Mods/SprayPaintPlus/RESEARCH.md:185-187
  - $(StationeersPath)\rocketstation_Data\Managed\Assembly-CSharp.dll :: Wall
related:
  - ./Structure.md
tags: [prefab]
---

# Wall

Vanilla game class for wall structures. Inherits from `LargeStructure`. Wall-painting flow must honor inheritance ordering when dispatching.

## Wall extends LargeStructure inheritance ordering
<!-- verified: 0.2.6228.27061 @ 2026-04-20 -->

Source: F0029f.

`Wall` extends `LargeStructure`. The wall branch in `PaintNetwork` must come first. If walls-painting is disabled for a wall target, the method returns early rather than falling through to the large-structure grid flood.

## Visual wall variants share the Wall C# class
<!-- verified: 0.2.6228.27061 @ 2026-04-28 -->

Source: `Assembly-CSharp.dll` decompile, full `: Wall` subclass scan.

The visual wall variants the player sees in Stationpedia ("Wall", "Wall Flat", "Wall Arched", "Wall Iron", "Wall Padded", "Wall Composite", "Wall Glass", and so on) are not separate C# classes. They are Unity prefab variants of the same `Wall` class, distinguished by:

- A different prefab `PrefabHash` and `PrefabName` (e.g. `StructureWall`, `StructureWallFlat`, `StructureWallArched`).
- A different `Wall.WallMaterialType` enum value (`Default`, `Metal`, `Composite`, `Glass`) for the four material families.
- A different mesh assigned to `_wallMesh`.

The complete set of C# classes that derive from `Wall` is exactly four:

| Class | Inheritance | Notes |
|---|---|---|
| `RocketCrewUmbilicalDoor` | `: Wall` | Crew umbilical door for rockets. |
| `ShutteredWindowConnector` | `: Wall, IWindowShutter` | Shutter connector. |
| `WallTransparent` | `: Wall` | Glass / window walls. `ShutteredWindow : WallTransparent` extends this further. |
| `Floor` | `: Wall` | Floor tiles. `LadderPlatform : Floor` extends this further. |

There is no `FlatWall`, `ArchedWall`, `WallShort`, `WallTall`, or per-visual-style subclass anywhere in `Assembly-CSharp.dll`.

**Implication for type-keyed flood-fill code.** Code that filters by `Thing.GetType()` (exact type, not `is`) treats every visual wall variant as the same target. `originalWall.GetType() == typeof(Wall)` holds for a Wall, a Flat Wall, an Arched Wall, an Iron Wall, etc., all at once. A flood-fill keyed on `s.GetType() != targetType` does not separate them; it paints every `Wall` instance bounding the same `Room`, regardless of visual variant. Distinguishing visual variants requires comparing `PrefabHash` (or `PrefabName`), not `GetType()`.

## Verification history
<!-- verified: 0.2.6228.27061 @ 2026-04-28 -->

- 2026-04-20: page created from the Research migration; verbatim content lifted from F0029f. No conflicts.
- 2026-04-28: added "Visual wall variants share the Wall C# class" section after a SprayPaintPlus bug report ("wall painting is spilling over to different types of walls"). Confirmed via decompile scan that the visual variants are prefabs of one C# class, not distinct types. Additive content; no conflict.

## Open questions

None at creation.
