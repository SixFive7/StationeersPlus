---
title: TerrainChunkChecksums
type: Protocols
created_in: 0.2.6228.27061
verified_in: 0.2.6228.27061
verified_at: 2026-04-20
sources:
  - Plans/SaveFixPrototype/plan.md:144-156
related:
  - ./TerrainDat.md
  - ./WorldXml.md
  - ../GameSystems/TerrainOctree.md
tags: [save-format, save-edit]
---

# TerrainChunkChecksums

Stationeers stores one rolling XOR-multiply checksum per base-terrain octree in world.xml. On load, each stored value is compared to the corresponding `ReadonlyVoxelOctree.CheckSum`. Any mismatch triggers an offer to regenerate ALL terrain (all-or-nothing, no per-chunk reset).

## Format and algorithm
<!-- verified: 0.2.6228.27061 @ 2026-04-20 -->

64 `<int>` values inside `<TerrainChunkChecksums>`, one per base-terrain octree. On load, the game compares each against the corresponding `ReadonlyVoxelOctree.CheckSum`. If ANY mismatch, it offers to regenerate ALL terrain. This is all-or-nothing; you cannot reset one chunk.

The checksum algorithm is a rolling XOR-multiply with prime 257:
```
checksum = 0
for each node in DFS pre-order:
    checksum = (checksum ^ flags) * 257
    if leaf: checksum = (checksum ^ density) * 257; checksum = (checksum ^ nodeType) * 257
    if branch: for i in 0..7: checksum = (checksum ^ i) * 257; recurse child i
```

When rewriting terrain.dat, zero all checksums so the game detects a mismatch and rebuilds its base-terrain state cleanly on load.

## Verification history
<!-- verified: 0.2.6228.27061 @ 2026-04-20 -->

- 2026-04-20: page created from the Research migration. Source: F0235 (TerrainChunkChecksums rolling XOR algorithm).

## Open questions

None at creation.
