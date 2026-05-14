# SaveFixPrototype (Plans): Research Reference

SaveFixPrototype is an in-progress prototype. Phase 1 is `terrain_reset.py`, a Python CLI that rewrites `.save` files offline to selectively reset terrain while preserving sealed rooms. Phase 2 ports the same logic to a runtime C# mod that calls `VoxelTerrain.Octree.SetDensity()` during a live multiplayer session. Durable game-internals research (terrain.dat format, dual-octree system, TerrainChunkChecksums algorithm, Room grid coordinate scale, AtmosphereSaveData schema, the live-multiplayer reset recipe) has been lifted to central pages; this file holds only mod-local content: the test-save inventory used to validate the prototype, the concrete numbers from the tested-against run, and the open questions that still need answering before Phase 2 implementation starts.

## 1. Architecture

The prototype has two phases sharing one algorithm:

- **Phase 1 (existing).** `terrain_reset.py` in the mod folder. Edits `.save` ZIPs offline by walking the `terrain.dat` octree, collapsing subtrees outside a keep bounding box derived from `<Room>` grid cells. See the central workflow page for the algorithm.
- **Phase 2 (planned).** A BepInEx + StationeersLaunchPad C# mod that performs the same selective reset at runtime. Conceptual layout (subject to change during implementation):

  ```
  TerrainReclamationMod/
    RoomProtectionMap       Build keep set from Room grid cells + margin at runtime
    ReclamationEvent        State machine: Scan -> Warn -> Execute -> Complete
    VoxelResetter           Read base density from ReadOnlyOctree, write via SetDensity
    EffectsController       Screen shake, dust particles, rumble, chat narration
    TriggerSystem           Console command, craftable item, or timed trigger
  ```

Neither phase ships a plugin DLL yet; `terrain_reset.py` is the only usable artifact at time of writing.

## 2. Design decisions

### 2.1. Applied

- **Bounding-box overlap, not per-voxel (Phase 1).** The offline tool's overlap test uses a rectangular bounding box around the keep set, not the per-voxel set. Over-preserves slightly at the corners of L-shaped rooms. A future improvement could check individual leaves against the per-voxel set; the set is already computed.
- **Conservative leaf handling (Phase 1).** A modified leaf that partially overlaps the keep zone is kept entirely. Splitting it would require base-terrain values the save file does not carry.
- **No post-rewrite branch re-collapse (Phase 1).** After rewriting, a branch may have all-identical children that could be collapsed back to a leaf. Skipped; the game handles it fine.
- **Unmodified replacement leaf bytes `01 00 00` (Phase 1).** The values are irrelevant because `IS_MODIFIED` is clear, so the game reads from base terrain instead.
- **Zero all 64 TerrainChunkChecksums after rewrite (Phase 1).** Forces the game to detect mismatch and rebuild its base-terrain state cleanly on load.

### 2.2. Rejected or deferred

- **Per-voxel keep-set check at leaf level.** Deferred; the bounding-box approach is good enough for the tested save and the set is already computed if we want it later.

## 3. Test observations

Specific numeric observations measured against the `Luna.save` test save. These are evidence from one run, not durable research; they validate the prototype but do not generalize.

### 3.1. Luna.save test save inventory

Details of the save used for prototyping. A backup of the pre-edit original lives at `Luna.save.bak` next to the working copy.

- **World:** Lunar, seed 6770294, day 65, version 0.2.6228.27061.
- **MaxDepth:** 12 (4096^3 voxels, 64 base terrain octrees).
- **Players:** 2 (Steam IDs 76561197970372584, 76561197965752767).
- **Rooms:** 2 total. Room 1: single cell at (-1293, 205, -693). Room 2: 25 cells, 5x5 grid at Y=205, X[-1297,-1289], Z[-691,-683].
- **Base structures:** 820 structures within 20m of rooms, X[-1304.5,-1273.5] Y[201,209] Z[-697,-682].
- **Full structure extent:** 1,209 structures total, all within X[-1304.5,-1272] Y[201,209] Z[-697,-682].
- **Terrain delta:** 69,505 nodes total, 42,257 modified leaves. Spans X[-1496,-852] Y[128,269] Z[-960,-385]. Excavation (air voxels, density<128) near base: 6,093 voxels spanning X[-1372,-1202] Y[182,218] Z[-750,-607].
- **Veins:** 95 modified vein records in `terrain.dat`.
- **Pipe network 67553:** O2 line. Cleaned of trace contaminants (N2, volatiles, H2, liquid N2O) in an earlier edit session.
- **Earlier edits:** checksums were zeroed twice (game regenerated and re-saved each time). Pipe 67553 gas was cleaned. Current file state may reflect any combination of these.

### 3.2. Tested-against Luna.save run (Phase 1 prototype)

Result of running `terrain_reset.py` against `Luna.save` with default settings:

```
Room cells: 26 (2 rooms: 1-cell airlock + 25-cell 5x5 main room, all at Y=205)
Keep bounding box: X[-1300,-1285] Y[202,209] Z[-696,-679]  (14 x 8 x 18 voxels)
MaxDepth: 12, world: 4096^3, origin: 2048

Result: 42,257 modified voxels reset, 0 kept
        38 branches collapsed, 59 nodes passthrough
        95 veins stripped, 0 kept
        terrain.dat: 212,347 bytes -> 275 bytes
```

Zero voxels were kept because these rooms sit at or above the Moon's natural surface. An underground base with rooms excavated from solid terrain would show nonzero kept voxels.

## 4. Harmony patches catalog

None at prototype stage. Phase 1 is a Python CLI; Phase 2 will install patches when implementation begins.

## 5. Relevant central pages

### 5.1. GameSystems

- [../../Research/GameSystems/TerrainOctree.md](../../Research/GameSystems/TerrainOctree.md) - Dual-octree system, MaxDepth / world size, octree coordinate system, and voxel/terrain class inventory the prototype relies on.

### 5.2. Protocols

- [../../Research/Protocols/SaveFileStructure.md](../../Research/Protocols/SaveFileStructure.md) - `.save` ZIP archive layout the Phase 1 tool reads and rewrites.
- [../../Research/Protocols/TerrainDat.md](../../Research/Protocols/TerrainDat.md) - terrain.dat binary format: MaxDepth header, DFS node stream, leaf / branch shapes, vein block. The Phase 1 rewrite walks this format directly.
- [../../Research/Protocols/TerrainChunkChecksums.md](../../Research/Protocols/TerrainChunkChecksums.md) - Rolling XOR-multiply checksum algorithm. Phase 1 zeros these to force base-terrain rebuild on load.
- [../../Research/Protocols/WorldXml.md](../../Research/Protocols/WorldXml.md) - `<Rooms>` / `<Grids>` schema the prototype parses for the keep set, plus the `<DifficultySetting>` enum.
- [../../Research/Protocols/AtmosphereSaveData.md](../../Research/Protocols/AtmosphereSaveData.md) - Pipe-network gas-species list used when scrubbing contaminants in earlier edit sessions.

### 5.3. Workflows

- [../../Research/Workflows/ResetTerrainOffline.md](../../Research/Workflows/ResetTerrainOffline.md) - `terrain_reset.py` algorithm and design decisions; the Phase 1 reference.
- [../../Research/Workflows/ResetTerrainLive.md](../../Research/Workflows/ResetTerrainLive.md) - Live-multiplayer runtime reset recipe; the Phase 2 reference (`SetDensity` per-voxel approach, tick-budget considerations).

## 6. Pitfalls / dead ends

None recorded yet. The `Luna.save` working copy has been edited multiple times during prototyping; use `Luna.save.bak` if the current file's behaviour looks off.

## 7. Open questions

These questions must be answered before Phase 2 implementation begins. Each is genuinely unknown; they are not conclusions.

- **Network sync.** Does `SetDensity` propagate changes to multiplayer clients automatically, or is a separate network notification needed? Trace the call chain in the decompiled `VoxelOctree.SetDensity()`.
- **Mesh rebuild.** Does `SetDensity` trigger chunk mesh regeneration internally, or must the mod call a separate dirty/rebuild method?
- **Room access.** How to get the list of `Room` objects and their `Grids` at runtime? Find the room manager singleton (likely on `WorldManager` or similar).
- **Tick budget.** How many `SetDensity` calls per frame before causing visible stutter? Needs empirical testing. Start conservative (100/tick) and tune.
- **Addon lifecycle.** Does Stationeers.Addons provide `Update()`, coroutine, or timer hooks for per-frame work?
- **Effects API.** Can the mod trigger screen shake, particle effects, and sound through public game APIs?
