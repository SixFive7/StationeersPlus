---
title: TerrainOctree
type: GameSystems
created_in: 0.2.6228.27061
verified_in: 0.2.6228.27061
verified_at: 2026-04-20
sources:
  - Plans/SaveFixPrototype/plan.md:29-39
  - Plans/SaveFixPrototype/plan.md:182-208
  - Plans/SaveFixPrototype/plan.md:82-90
  - Plans/SaveFixPrototype/plan.md:68-78
related:
  - ../Protocols/TerrainDat.md
tags: [terrain, save-format, save-edit]
---

# TerrainOctree

The dual-octree system underlying Stationeers terrain: a read-only procedurally-generated `ReadOnlyOctree` baseline paired with a mutable `VoxelOctree` delta. The game writes only the delta to `terrain.dat`; unmodified regions fall through to the readonly tree at read time. Understanding this fallthrough is the key to implementing selective-reset tooling without needing the base terrain values on hand.

## Dual-octree system
<!-- verified: 0.2.6228.27061 @ 2026-04-20 -->

The game keeps two octrees in memory simultaneously:

1. **ReadOnlyOctree** (`OctTreeCluster` containing `ReadonlyVoxelOctree[]`): the procedural baseline. Generated deterministically from the world seed. Loaded from static game-data files (`Terrain0.dat`, `Terrain1.dat`, ...) that ship with the world definition. Never modified at runtime. Uses a compact flat `NativeArray<byte>` representation.

2. **Octree** (`VoxelOctree`): the mutable delta. Starts empty on a new game. Every player action that changes terrain (digging, terrain manipulator) writes to this tree. Only this delta is saved to disk in `terrain.dat`.

When the game reads a voxel (`VoxelTerrain.Get()`):
- Traverse the mutable octree down to the leaf at that position.
- If the leaf's `IsModified` flag is true: return its density and type.
- If false: fall through to `ReadOnlyOctree.Get(node.PartnerNode, x, y, z)`, which descends the readonly tree at full detail to return the base terrain value.

**This fallthrough is the key insight that makes selective reset work.** Replacing any modified subtree with a single unmodified leaf causes the game to read base terrain for that entire region, effectively "resetting" it without needing to know what the base values actually are.

## Voxel / terrain classes
<!-- verified: 0.2.6228.27061 @ 2026-04-20 -->

All in `Assembly-CSharp.dll`. No separate terrain DLL.

| Class | Role |
|---|---|
| `VoxelTerrain` | Static facade. `Octree` (mutable), `ReadOnlyOctree` (base), `MaxDepth`, `Get()`, `WorldToOctreeSpace()`, `Serialize()`/`Deserialize()` |
| `VoxelOctree` | Mutable tree of `Node` objects. `SetDensity()`, `Get()`, `SerializeDeltaTerrain()`, `DeserializeDeltaTerrain()` |
| `Node` | Tree node. `byte _density`, `VoxelNodeType _nodeType`, `bool _isModified`, `Node[] Children` (null=leaf), `PartnerNode PartnerNode`, `byte Depth` |
| `OctTreeCluster` | Holds `ReadonlyVoxelOctree[]`. `GetOctreeCount(maxDepth) = 8^max(0, maxDepth-10)`. `GetTerrainChecksums()` |
| `ReadonlyVoxelOctree` | Flat `NativeArray<byte>`. `Get()`, `CheckSum` property |
| `PartnerNode` | `readonly struct { ushort OctTreeIndex; int NodeIndex; sbyte Depth; }` Links mutable node to its readonly counterpart |
| `NodeInfo` | `readonly struct { byte density; VoxelNodeType nodeType; sbyte depth; }` Return type of all Get() calls |

### VoxelNodeType

```csharp
[Flags] enum VoxelNodeType : byte { None=0, Crust=1, Dirt=2, Macro=4, Bedrock=8 }
```

### NodeFlags

```
IS_LEAF=0x01  IS_MODIFIED=0x02  OFFSET_SIZE_ZERO=0x04  OFFSET_SIZE_ONE=0x08  OFFSET_SIZE_TWO=0x10  OFFSET_SIZE_FOUR=0x20
```

OFFSET_SIZE_* flags are only used in the `ReadonlyVoxelOctree` flat-array in-memory format, never in terrain.dat.

## Coordinate system
<!-- verified: 0.2.6228.27061 @ 2026-04-20 -->

```
octreePos = worldPos + (Size/2, 0, Size/2)
worldPos  = octreePos - (Size/2, 0, Size/2)
```

Note: Y offset is always 0. Only X and Z are shifted.

For MaxDepth=12: `OriginOffset = (2048, 0, 2048)`.

## MaxDepth / world size
<!-- verified: 0.2.6228.27061 @ 2026-04-20 -->

| MaxDepth | World size | Base terrain files |
|---|---|---|
| 10 | 1024^3 | 1 (Terrain0.dat) |
| 11 | 2048^3 | 8 |
| 12 | 4096^3 | 64 |

The Lunar world uses MaxDepth=12 (4096^3).

## Verification history
<!-- verified: 0.2.6228.27061 @ 2026-04-20 -->

- 2026-04-20: page created from the Research migration; F0231 is the primary source per MigrationMap. Additional sources: F0237 (classes), F0243 (coordinates), and F0230 (world-size table, which also lives on `Research/Protocols/TerrainDat.md` per MigrationMap).

## Open questions

None at creation.
