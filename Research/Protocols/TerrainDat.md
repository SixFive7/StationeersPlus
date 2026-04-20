---
title: terrain.dat binary format
type: Protocols
created_in: 0.2.6228.27061
verified_in: 0.2.6228.27061
verified_at: 2026-04-20
sources:
  - Plans/SaveFixPrototype/plan.md:60-68
  - Plans/SaveFixPrototype/plan.md:92-118
  - Plans/SaveFixPrototype/plan.md:120-140
  - Plans/SaveFixPrototype/plan.md:68-78
related:
  - ./SaveFileStructure.md
  - ./TerrainChunkChecksums.md
  - ../GameSystems/TerrainOctree.md
tags: [save-format, save-edit]
---

# terrain.dat binary format

Byte-level layout of a Stationeers `terrain.dat` base-terrain file. One file per base-terrain octree (world-size dependent, see reference table below). The file stores an `INT32 LE MaxDepth` header followed by a DFS pre-order walk of the octree and then per-vein delta data. The file is NOT internally compressed; the outer save ZIP provides compression.

## File layout
<!-- verified: 0.2.6228.27061 @ 2026-04-20 -->

```
Bytes 0-3:    INT32 LE  MaxDepth
Bytes 4..N:   DFS pre-order octree nodes (variable length)
Bytes N+1..:  Vein/minable delta data
```

The file is NOT compressed internally. The outer ZIP provides compression.

## Node serialization
<!-- verified: 0.2.6228.27061 @ 2026-04-20 -->

Every node is one of two forms:

**Leaf (3 bytes):**
```
BYTE  flags      bit 0 set (IS_LEAF=0x01), bit 1 optionally set (IS_MODIFIED=0x02)
BYTE  density    0-255 (0 = air, 255 = fully solid)
BYTE  nodeType   VoxelNodeType flags: None=0, Crust=1, Dirt=2, Macro=4, Bedrock=8
```

**Branch (1 byte header, then 8 recursive children):**
```
BYTE  flags      bit 0 clear (not a leaf), bit 1 optionally set (IS_MODIFIED=0x02)
[child 0]        (X low,  Y low,  Z low)
[child 1]        (X high, Y low,  Z low)
[child 2]        (X low,  Y high, Z low)
[child 3]        (X high, Y high, Z low)
[child 4]        (X low,  Y low,  Z high)
[child 5]        (X high, Y low,  Z high)
[child 6]        (X low,  Y high, Z high)
[child 7]        (X high, Y high, Z high)
```

Child index bit encoding: `bit 0 = X >= halfSize, bit 1 = Y >= halfSize, bit 2 = Z >= halfSize`.

At depth D, each node covers `2^(MaxDepth - D)` voxels per axis. The root (depth 0) covers the whole world. Depth=MaxDepth nodes are single voxels.

## Vein data (after octree nodes)
<!-- verified: 0.2.6228.27061 @ 2026-04-20 -->

```
INT32  veinCount
For each vein (veinCount times):
  INT16  VeinWorldPosition.x
  INT16  VeinWorldPosition.y
  INT16  VeinWorldPosition.z
  INT16  ClusterPosition.x
  INT16  ClusterPosition.y
  INT16  ClusterPosition.z
  INT32  Data.IdHash       (identifies the vein generation parameters)
  BYTE   minablesLength
  For each minable (minablesLength times):
    BYTE  X
    BYTE  Y
    BYTE  Z
    BYTE  ParentIndex
    BYTE  IsActive         (0x00 or 0x01)
```

## World size reference
<!-- verified: 0.2.6228.27061 @ 2026-04-20 -->

Short reference copy. Full treatment on `../GameSystems/TerrainOctree.md`.

| MaxDepth | World size | Base terrain files |
|---|---|---|
| 10 | 1024^3 | 1 (Terrain0.dat) |
| 11 | 2048^3 | 8 |
| 12 | 4096^3 | 64 |

The Lunar world uses MaxDepth=12 (4096^3).

## Verification history
<!-- verified: 0.2.6228.27061 @ 2026-04-20 -->

- 2026-04-20: page created from the Research migration. Sources: F0232 (top-level layout), F0233 (node serialization), F0234 (vein data), F0230 (MaxDepth / world size table; primary lives on `../GameSystems/TerrainOctree.md` with a short reference copy here per MigrationMap).

## Open questions

None at creation.
