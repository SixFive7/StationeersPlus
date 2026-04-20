---
title: Reset Terrain Live (Multiplayer)
type: Workflows
created_in: 0.2.6228.27061
verified_in: 0.2.6228.27061
verified_at: 2026-04-20
sources:
  - Plans/SaveFixPrototype/plan.md:288-299
related:
  - ../Protocols/TerrainDat.md
  - ../GameClasses/VoxelOctree.md
  - ResetTerrainOffline.md
tags: [terrain, worldgen, network]
---

# Reset Terrain Live (Multiplayer)

Runtime approach for resetting modified voxels back to base terrain during a live multiplayer session. Reach for this recipe when a mod wants to wrap a selective terrain reset inside a lore-based event (reclamation event, visual effects, chat narration) rather than shutting the server down and editing the save offline.

## When to use
<!-- verified: 0.2.6228.27061 @ 2026-04-20 -->

- A live multiplayer session wants to reclaim terrain over time, in place, without taking the save offline.
- A mod wants to pair terrain reset with screen shake, dust particles, rumble sounds, or chat narration.
- The work can be spread across many ticks to avoid frame-rate hits.

For a one-shot offline reset against a closed `.save` archive, see [ResetTerrainOffline.md](ResetTerrainOffline.md) instead.

## Prerequisites
<!-- verified: 0.2.6228.27061 @ 2026-04-20 -->

- Server-authoritative code path; `VoxelOctree.SetDensity` runs on the server and the server propagates changes to clients through the normal voxel sync mechanism.
- A source of truth for "which voxels should be reset": a queue of world positions, typically produced by a scan phase that walks `VoxelTerrain.Octree` and finds modified nodes outside a computed keep set.

## Key runtime approach
<!-- verified: 0.2.6228.27061 @ 2026-04-20 -->

For each voxel to reset:

1. Convert world position to octree space.
2. Read base-terrain density: `VoxelTerrain.ReadOnlyOctree.Get(octreePos)` returns `NodeInfo` with `.density` and `.nodeType`.
3. Write it back: `VoxelTerrain.Octree.SetDensity(worldPos, baseDensity, ...)`.
4. This overwrites the delta with the base value. On next save, the node collapses (or at least is no longer meaningfully modified).

Process a few hundred voxels per tick to avoid lag. Sort the work queue by distance from base center (furthest first) for a dramatic "closing in" visual effect.

## Verification
<!-- verified: 0.2.6228.27061 @ 2026-04-20 -->

- Snapshot `VoxelTerrain.Octree` state around a reset voxel before and after the `SetDensity` call: the delta should match the base-terrain density read from `VoxelTerrain.ReadOnlyOctree`.
- Save the world and reload it; confirm the reset region's terrain.dat shrinks and the reset voxels no longer appear as modified leaves.
- In a multiplayer session, confirm clients see the terrain update in their local view within the normal voxel sync window.

## Pitfalls
<!-- verified: 0.2.6228.27061 @ 2026-04-20 -->

- Batch work across ticks: processing the whole queue in a single tick spikes frame time. A few hundred voxels per tick is a reasonable default.
- Do not skip the `ReadOnlyOctree.Get(octreePos)` step and write a constant density; different voxel columns have different base densities (stone vs rock vs regolith) and a constant would show through as visible striping.
- Save / reload behavior depends on the node being written back to base density exactly. Near-base values that differ by float precision may still serialize as modified leaves.

## Verification history
<!-- verified: 0.2.6228.27061 @ 2026-04-20 -->

- 2026-04-20: page created from the Research migration; verbatim content lifted from F0242 (`Plans/SaveFixPrototype/plan.md:288-299`).

## Open questions

- **Network sync:** does `SetDensity` propagate changes to multiplayer clients automatically, or is there a separate network notification needed? Trace the call chain in the decompiled `VoxelOctree.SetDensity()`.
- **Mesh rebuild:** does `SetDensity` trigger chunk mesh regeneration internally, or must the mod call a separate dirty / rebuild method?
- **Room access:** how to get the list of `Room` objects and their `Grids` at runtime? Find the room manager singleton (likely on `WorldManager` or similar).
