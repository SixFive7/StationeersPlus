# SaveFixPrototype: Stationeers Terrain Reset Tool

## Read this first

You are picking up a project mid-stream. Here is what exists, what the goal is, and what to do next.

**What exists right now:**
- `terrain_reset.py` in this directory: a working Python CLI tool that selectively resets terrain in a Stationeers .save file while preserving sealed rooms. It has been tested against a real save and produces correct output.
- A full decompilation of the game's Assembly-CSharp.dll was done at `/tmp/stationeers_decompile/` but that directory is ephemeral and may be gone. The game DLL is at `E:\Steam\steamapps\common\Stationeers\rocketstation_Data\Managed\Assembly-CSharp.dll`. You can re-decompile with `ilspycmd` (installed globally via dotnet tools) using `ilspycmd <dll> -p -o /tmp/stationeers_decompile`.

**What the user wants (the full arc):**

1. **Phase 1 (DONE):** Python prototype that edits .save files offline. This is `terrain_reset.py`.
2. **Phase 2 (NEXT):** Port the logic to a C# mod for Stationeers using the Stationeers.Addons framework. The mod runs inside a live multiplayer server. Instead of editing files, it calls `VoxelTerrain.Octree.SetDensity()` at runtime to revert voxels to their base-terrain values. The reset is spread over many ticks (a few hundred voxels per tick) to avoid freezing the server, and is wrapped in a lore-flavored in-game event (seismic reclamation, nanite reconstruction, time anomaly, or similar) with visual/audio effects. Players see the terrain gradually filling back in around them while their sealed rooms remain untouched.

**Build conventions:** read `C:\Source\SixFive7\StationeersPlus\CLAUDE.md` before writing any code. It defines MSBuild property rules, README/About.xml sync requirements, preview image dimensions, and a strict no-AI-tells writing style for user-facing text.

**Test save:** `C:\Users\jori\Downloads\Luna.save` (Lunar world, game version 0.2.6228.27061). A backup of the original is at `Luna.save.bak`. The save has been edited multiple times during prototyping and may not match the original.

---

## How the game saves terrain

Stationeers is a voxel-based space engineering game (by RocketWerkz, Unity/C#). Terrain is a 3D voxel grid stored as an octree.

### The dual-octree system (post v0.2.5499.24517)

The game keeps two octrees in memory simultaneously:

1. **ReadOnlyOctree** (`OctTreeCluster` containing `ReadonlyVoxelOctree[]`): the procedural baseline. Generated deterministically from the world seed. Loaded from static game-data files (`Terrain0.dat`, `Terrain1.dat`, ...) that ship with the world definition. Never modified at runtime. Uses a compact flat `NativeArray<byte>` representation.

2. **Octree** (`VoxelOctree`): the mutable delta. Starts empty on a new game. Every player action that changes terrain (digging, terrain manipulator) writes to this tree. Only this delta is saved to disk in `terrain.dat`.

When the game reads a voxel (`VoxelTerrain.Get()`):
- Traverse the mutable octree down to the leaf at that position.
- If the leaf's `IsModified` flag is true: return its density and type.
- If false: fall through to `ReadOnlyOctree.Get(node.PartnerNode, x, y, z)`, which descends the readonly tree at full detail to return the base terrain value.

**This fallthrough is the key insight that makes selective reset work.** Replacing any modified subtree with a single unmodified leaf causes the game to read base terrain for that entire region, effectively "resetting" it without needing to know what the base values actually are.

### Save file structure

A `.save` file is a ZIP archive containing:

| File | Contents |
|---|---|
| `world_meta.xml` | Header: world name, game version, counts |
| `world.xml` | All game state: every structure, item, atmosphere, pipe network, room, and terrain checksums |
| `terrain.dat` | Binary delta voxel octree + vein data |
| `preview.png` | Map preview screenshot |
| `screenshot.png` | In-game screenshot |

---

## terrain.dat binary format (fully reverse-engineered)

This format was determined by decompiling `VoxelOctree.SerializeDeltaTerrain()` and `DeserializeDeltaTerrain()` from Assembly-CSharp.dll and confirmed by parsing a real save.

### File layout

```
Bytes 0-3:    INT32 LE  MaxDepth
Bytes 4..N:   DFS pre-order octree nodes (variable length)
Bytes N+1..:  Vein/minable delta data
```

The file is NOT compressed internally. The outer ZIP provides compression.

### MaxDepth and world size

`MaxDepth` determines the world's voxel resolution: `Size = 2^MaxDepth` voxels per axis.

| MaxDepth | World size | Base terrain files |
|---|---|---|
| 10 | 1024^3 | 1 (Terrain0.dat) |
| 11 | 2048^3 | 8 |
| 12 | 4096^3 | 64 |

The Lunar world uses MaxDepth=12 (4096^3).

### Coordinate system

```
octreePos = worldPos + (Size/2, 0, Size/2)
worldPos  = octreePos - (Size/2, 0, Size/2)
```

Note: Y offset is always 0. Only X and Z are shifted.

For MaxDepth=12: `OriginOffset = (2048, 0, 2048)`.

### Node serialization (DFS stream format)

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

### Vein data (appended immediately after the last octree node)

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

---

## TerrainChunkChecksums in world.xml

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

---

## Room data in world.xml

Rooms are sealed pressurized volumes. The game tracks every airtight cell. Sealed tunnels connecting rooms are rooms themselves.

```xml
<Rooms>
  <Room>
    <RoomId>42</RoomId>
    <Grids>
      <Grid><x>-12930</x><y>2050</y><z>-6930</z></Grid>
      <!-- one entry per sealed cell -->
    </Grids>
  </Room>
</Rooms>
```

**Grid coordinate scale:** stored at 10x. Divide by 10 for world coords. Grid `(-12930, 2050, -6930)` = world `(-1293, 205, -693)`.

**Cell size:** each cell is a 2x2x2 voxel block. Cells are spaced 2 apart on each axis.

---

## Key game classes (from decompiled Assembly-CSharp.dll)

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

---

## Pipe network atmosphere editing (for reference)

Each pipe network's gas mix is an `<AtmosphereSaveData>` in world.xml keyed by `<NetworkReferenceId>`. All gas species are individual float XML elements (Oxygen, Nitrogen, CarbonDioxide, Volatiles, Chlorine, Water, PollutedWater, NitrousOxide, LiquidNitrogen, LiquidOxygen, LiquidVolatiles, Steam, LiquidCarbonDioxide, LiquidPollutant, LiquidNitrousOxide, Hydrogen, LiquidHydrogen, Hydrazine, LiquidHydrazine, LiquidAlcohol, Helium, LiquidSodiumChloride, Silanol, LiquidSilanol, HydrochloricAcid, LiquidHydrochloricAcid, Ozone, LiquidOzone). Set unwanted species to 0. Energy scales with total moles; small changes relative to dominant gas have negligible thermal impact.

---

## Difficulty setting

`<DifficultySetting Id="Normal" />` near the top of world.xml. Values: `Easy`, `Normal`, `Hard`, `Stationeer`.

---

## The prototype: terrain_reset.py

### What it does

1. Opens the .save ZIP, reads world.xml and terrain.dat.
2. Parses all `<Room>` grid cells from world.xml, converts to world coordinates.
3. Expands each 2x2x2 cell by a configurable margin (default 3 voxels) for wall structures. Produces a bounding box.
4. Walks the terrain.dat DFS octree. For each node, tests whether its spatial volume overlaps the keep bounding box.
   - **No overlap:** if it's a branch, skips all children in the input stream and writes a single unmodified leaf (`01 00 00`), collapsing the entire subtree. If it's a modified leaf, replaces it with an unmodified leaf.
   - **Overlap:** writes the node through unchanged. If it's a branch, recurses into all 8 children.
5. Filters vein records: keeps only those whose VeinWorldPosition falls inside the keep bounding box.
6. Zeros all 64 TerrainChunkChecksums.
7. Repacks the ZIP. Creates a .save.bak backup if one doesn't exist.

### Usage

```
python terrain_reset.py path/to/save.save              # apply
python terrain_reset.py path/to/save.save --dry-run     # analyze only
python terrain_reset.py path/to/save.save --margin 5    # wider wall buffer
```

### Design decisions and known limitations

- **Bounding box not per-voxel:** overlap test uses the rectangular bounding box of the keep set, not the per-voxel set. This over-preserves slightly at corners of L-shaped rooms. The per-voxel set is computed but not used for leaf-level filtering. A future improvement could check individual leaves against the set.
- **Conservative leaf handling:** a modified leaf that partially overlaps the keep zone is kept entirely. Splitting would require base-terrain values we don't have in the save file.
- **No branch re-collapse:** after rewriting, a branch might have all-identical children that could be collapsed back to a leaf. This optimization is skipped; the game handles it fine.
- **Unmodified leaf bytes:** replacement leaf is `01 00 00` (IS_LEAF, density=0, nodeType=None). The values are irrelevant because IS_MODIFIED is not set, so the game reads from base terrain instead.

### Tested against Luna.save

```
Room cells: 26 (2 rooms: 1-cell airlock + 25-cell 5x5 main room, all at Y=205)
Keep bounding box: X[-1300,-1285] Y[202,209] Z[-696,-679]  (14 x 8 x 18 voxels)
MaxDepth: 12, world: 4096^3, origin: 2048

Result: 42,257 modified voxels reset, 0 kept
        38 branches collapsed, 59 nodes passthrough
        95 veins stripped, 0 kept
        terrain.dat: 212,347 bytes -> 275 bytes
```

Zero voxels were kept because these rooms sit at/above the Moon's natural surface. An underground base with rooms excavated from solid terrain would show nonzero kept voxels.

---

## Phase 2: C# mod for live multiplayer

### Goal

A Stationeers mod that performs the same selective terrain reset at runtime during a live multiplayer session, wrapped in a lore-based event with visual effects.

### Architecture

```
TerrainReclamationMod/
  RoomProtectionMap       Build keep set from Room grid cells + margin at runtime
  ReclamationEvent        State machine: Scan -> Warn -> Execute -> Complete
    ScanPhase             Walk VoxelTerrain.Octree, find modified nodes outside keep set
    WarnPhase             Alert players via chat, optional screen effects
    ExecutePhase          Each tick: pop N voxels from queue, call SetDensity with base value
  VoxelResetter           For each voxel: read base density from ReadOnlyOctree, write via SetDensity
  EffectsController       Screen shake, dust particles, rumble sounds, chat narration
  TriggerSystem           Console command, craftable in-game item, or timed cron
```

### Key runtime approach

For each voxel to reset:
1. Convert world position to octree space.
2. Read base-terrain density: `VoxelTerrain.ReadOnlyOctree.Get(octreePos)` returns `NodeInfo` with `.density` and `.nodeType`.
3. Write it back: `VoxelTerrain.Octree.SetDensity(worldPos, baseDensity, ...)`.
4. This overwrites the delta with the base value. On next save, the node collapses (or at least is no longer meaningfully modified).

Process a few hundred voxels per tick to avoid lag. Sort the work queue by distance from base center (furthest first) for a dramatic "closing in" visual effect.

### Open questions to resolve before implementing

1. **Network sync:** does `SetDensity` propagate changes to multiplayer clients automatically, or is there a separate network notification needed? Trace the call chain in the decompiled `VoxelOctree.SetDensity()`.
2. **Mesh rebuild:** does `SetDensity` trigger chunk mesh regeneration internally, or must the mod call a separate dirty/rebuild method?
3. **Room access:** how to get the list of `Room` objects and their `Grids` at runtime? Find the room manager singleton (likely on `WorldManager` or similar).
4. **Tick budget:** how many `SetDensity` calls per frame before causing visible stutter? Needs empirical testing. Start conservative (100/tick) and tune.
5. **Addon lifecycle:** does Stationeers.Addons provide `Update()`, coroutine, or timer hooks for per-frame work?
6. **Effects API:** can the mod trigger screen shake, particle effects, and sound through public game APIs?

### Lore concepts (user's preference TBD)

- **Seismic Reclamation:** the planet's geology naturally closes excavations over time. Triggered by a craftable "Seismic Charge" item or console command. Rumbling sounds and screen shake escalate, then terrain fills in wave by wave from the edges.
- **Nanite Reconstruction:** deployed nanobots restore the surface. Craftable "Nanite Beacon" item. Subtle particle effects as terrain rebuilds.
- **Temporal Anomaly:** a time distortion reverts the landscape. Visual distortion shader, then terrain snaps back in concentric rings. Rooms protected by atmosphere pressure differential.

---

## Reference: existing community tools and resources

| Resource | URL |
|---|---|
| StationeersTools (old terrain editor) | https://github.com/PsychoNineSix/StationeersTools |
| StationeersStructureMover (world.xml editor with Room model) | https://github.com/jhugard/StationeersStructureMover |
| stationeers-save-editor (Python XML editor) | https://github.com/aproposmath/stationeers-save-editor |
| StationeersMapTrimmer (closed-source chunk trimmer) | https://github.com/allsyst3msg0/StationeersMapTrimmer |
| Stationeers Save Transformer (web migration tool) | https://sst.jxsn.dev |
| Stationeers.Addons modding framework | https://github.com/StationeersAddons/Stationeers.Addons |
| Terrain overhaul patch notes | https://stationeers-wiki.com/Update_v0.2.5499.24517 |
| Terrain checksum discussion | https://steamcommunity.com/app/544550/discussions/0/686358202788403328/ |

---

## Test save details (Luna.save)

For context if you need to test against the same save.

- **World:** Lunar, seed 6770294, day 65, version 0.2.6228.27061
- **MaxDepth:** 12 (4096^3, 64 base terrain octrees)
- **Players:** 2 (Steam IDs 76561197970372584, 76561197965752767)
- **Rooms:** 2 total. Room 1: single cell at (-1293, 205, -693). Room 2: 25 cells, 5x5 grid at Y=205, X[-1297,-1289], Z[-691,-683].
- **Base structures:** 820 structures within 20m of rooms, X[-1304.5,-1273.5] Y[201,209] Z[-697,-682]
- **Full structure extent:** 1,209 structures total, all within X[-1304.5,-1272] Y[201,209] Z[-697,-682]
- **Terrain delta:** 69,505 nodes total, 42,257 modified leaves. Spans X[-1496,-852] Y[128,269] Z[-960,-385]. Excavation (air voxels, density<128) near base: 6,093 voxels spanning X[-1372,-1202] Y[182,218] Z[-750,-607].
- **Veins:** 95 modified vein records in terrain.dat
- **Pipe network 67553:** O2 line. Was cleaned of trace contaminants (N2, volatiles, H2, liquid N2O) in an earlier edit session.
- **Earlier edits:** checksums were zeroed twice (game regenerated and re-saved each time). Pipe 67553 gas was cleaned. Current file state may reflect any combination of these.
- **Backup:** `C:\Users\jori\Downloads\Luna.save.bak` is the pre-edit original.
