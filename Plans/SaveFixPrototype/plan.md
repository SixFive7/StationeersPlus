# SaveFixPrototype: Stationeers Terrain Reset Tool

## Read this first

You are picking up a project mid-stream. Here is what exists, what the goal is, and what to do next.

**What exists right now:**
- `terrain_reset.py` in this directory: a working Python CLI tool that selectively resets terrain in a Stationeers .save file while preserving sealed rooms. It has been tested against a real save and produces correct output.
- A full decompilation of the game's Assembly-CSharp.dll was done to a local scratch directory but that directory is ephemeral and may be gone. The game DLL lives at `$(StationeersPath)\rocketstation_Data\Managed\Assembly-CSharp.dll`. You can re-decompile with `ilspycmd` (installed globally via dotnet tools) using `ilspycmd <dll> -p -o <SCRATCH_DIR>`.

**What the user wants (the full arc):**

1. **Phase 1 (DONE):** Python prototype that edits .save files offline. This is `terrain_reset.py`.
2. **Phase 2 (NEXT):** Port the logic to a C# mod for Stationeers using the Stationeers.Addons framework. The mod runs inside a live multiplayer server. Instead of editing files, it calls `VoxelTerrain.Octree.SetDensity()` at runtime to revert voxels to their base-terrain values. The reset is spread over many ticks (a few hundred voxels per tick) to avoid freezing the server, and is wrapped in a lore-flavored in-game event (seismic reclamation, nanite reconstruction, time anomaly, or similar) with visual/audio effects. Players see the terrain gradually filling back in around them while their sealed rooms remain untouched.

**Build conventions:** read the repository root `CLAUDE.md` before writing any code. It defines MSBuild property rules, README/About.xml sync requirements, preview image dimensions, and a strict no-AI-tells writing style for user-facing text.

**Test save:** a local `Luna.save` (Lunar world, game version 0.2.6228.27061). A backup of the original sits alongside at `Luna.save.bak`. The save has been edited multiple times during prototyping and may not match the original.

Mod-local test metrics (tested-against result numbers from `Luna.save`) and the detailed test save inventory have moved into `Plans/SaveFixPrototype/RESEARCH.md`. See that file for verified numbers (modified-voxel counts, structure extents, vein counts) and the open questions that still need to be resolved before Phase 2 implementation starts.

---

## How the game saves terrain

Stationeers is a voxel-based space engineering game (by RocketWerkz, Unity/C#). Terrain is a 3D voxel grid stored as an octree. The game keeps two octrees in memory: a procedural readonly baseline and a mutable delta. Reads fall through from the delta to the baseline when a node is unmodified, which is the key property the reset tool exploits. A `.save` file is a ZIP archive with `world_meta.xml`, `world.xml`, `terrain.dat`, `preview.png`, and `screenshot.png`.

Full content lifted to:
- [../../Research/GameSystems/TerrainOctree.md](../../Research/GameSystems/TerrainOctree.md) - Dual-octree system, ReadOnlyOctree / VoxelOctree split, fallthrough read semantics.
- [../../Research/Protocols/SaveFileStructure.md](../../Research/Protocols/SaveFileStructure.md) - ZIP archive layout and the file roster inside a `.save`.

Lifted during Phase 5 migration on 2026-04-20. Original content preserved in git history at `<commit-sha>:Plans/SaveFixPrototype/plan.md:22-53`.

---

## terrain.dat binary format (fully reverse-engineered)

Determined by decompiling `VoxelOctree.SerializeDeltaTerrain()` / `DeserializeDeltaTerrain()` and confirmed against a real save. File starts with an INT32 LE `MaxDepth`, then DFS pre-order octree nodes (variable length), then vein / minable delta data. The file is not compressed internally; the outer ZIP provides compression. `MaxDepth` determines world size (`Size = 2^MaxDepth`); 10 = 1024^3 (1 base octree), 11 = 2048^3 (8), 12 = 4096^3 (64). Coordinate system shifts X and Z only by `Size/2` (Y offset is 0). Leaves are 3 bytes (flags, density, nodeType); branches are a 1-byte header followed by 8 recursive children with bit-encoded child indices. Vein block after the octree carries `INT32 veinCount` then per-vein records with `VeinWorldPosition`, `ClusterPosition`, `IdHash`, and a `minables` list.

Full content lifted to:
- [../../Research/Protocols/TerrainDat.md](../../Research/Protocols/TerrainDat.md) - terrain.dat top-level layout, node serialization (leaf 3 bytes, branch 1 + 8 children), vein data appended after octree nodes.
- [../../Research/GameSystems/TerrainOctree.md](../../Research/GameSystems/TerrainOctree.md) - MaxDepth / world size reference table and the octree coordinate system.

Lifted during Phase 5 migration on 2026-04-20. Original content preserved in git history at `<commit-sha>:Plans/SaveFixPrototype/plan.md:55-140`.

---

## TerrainChunkChecksums in world.xml

64 `<int>` values inside `<TerrainChunkChecksums>`, one per base-terrain octree. On load, the game compares each against the corresponding `ReadonlyVoxelOctree.CheckSum`; any mismatch triggers an all-or-nothing regenerate prompt. The checksum algorithm is a rolling XOR-multiply with prime 257 over DFS pre-order nodes. When rewriting `terrain.dat`, zero all checksums so the game rebuilds its base-terrain state cleanly on load.

Full content lifted to:
- [../../Research/Protocols/TerrainChunkChecksums.md](../../Research/Protocols/TerrainChunkChecksums.md) - Checksum format, per-chunk mapping, and the rolling XOR-multiply algorithm.

Lifted during Phase 5 migration on 2026-04-20. Original content preserved in git history at `<commit-sha>:Plans/SaveFixPrototype/plan.md:142-156`.

---

## Room data in world.xml

Rooms are sealed pressurized volumes. Each `<Room>` carries a `<RoomId>` and a `<Grids>` list of sealed cells. Grid coordinates are stored at 10x world scale (divide by 10 for world coords). Each cell is a 2x2x2 voxel block; cells are spaced 2 apart on each axis. Sealed tunnels connecting rooms are rooms themselves.

Full content lifted to:
- [../../Research/Protocols/WorldXml.md](../../Research/Protocols/WorldXml.md) - `<Rooms>` / `<Grids>` schema, 10x grid coordinate scale, and the 2x2x2 cell-size convention.

Lifted during Phase 5 migration on 2026-04-20. Original content preserved in git history at `<commit-sha>:Plans/SaveFixPrototype/plan.md:158-178`.

---

## Key game classes (from decompiled Assembly-CSharp.dll)

The voxel / terrain stack lives entirely in `Assembly-CSharp.dll` (no separate terrain DLL). The relevant types: `VoxelTerrain` (static facade with `Octree`, `ReadOnlyOctree`, `MaxDepth`, `Get()`, `WorldToOctreeSpace()`, `Serialize()`/`Deserialize()`), `VoxelOctree` (mutable tree with `SetDensity()` / `Get()` / `SerializeDeltaTerrain()` / `DeserializeDeltaTerrain()`), `Node` (per-node fields including `_density`, `_nodeType`, `_isModified`, `Children`, `PartnerNode`, `Depth`), `OctTreeCluster` (holds `ReadonlyVoxelOctree[]`; `GetOctreeCount(maxDepth) = 8^max(0, maxDepth-10)`), `ReadonlyVoxelOctree` (flat `NativeArray<byte>`), `PartnerNode` and `NodeInfo` readonly structs. `VoxelNodeType` is a `[Flags]` byte enum (None=0, Crust=1, Dirt=2, Macro=4, Bedrock=8). `NodeFlags` defines `IS_LEAF=0x01`, `IS_MODIFIED=0x02`, and `OFFSET_SIZE_*` (only used in the flat in-memory format, never in `terrain.dat`).

Full content lifted to:
- [../../Research/GameSystems/TerrainOctree.md](../../Research/GameSystems/TerrainOctree.md) - Voxel/terrain class inventory with `VoxelNodeType` and `NodeFlags` enums.

Lifted during Phase 5 migration on 2026-04-20. Original content preserved in git history at `<commit-sha>:Plans/SaveFixPrototype/plan.md:180-208`.

---

## Pipe network atmosphere editing (for reference)

Each pipe network's gas mix is an `<AtmosphereSaveData>` in `world.xml` keyed by `<NetworkReferenceId>`. All gas species are individual float XML elements. Set unwanted species to 0 to scrub contaminants. Energy scales with total moles; small changes relative to a dominant gas have negligible thermal impact.

Full content lifted to:
- [../../Research/Protocols/AtmosphereSaveData.md](../../Research/Protocols/AtmosphereSaveData.md) - Full gas species list and the energy-scaling note.

Lifted during Phase 5 migration on 2026-04-20. Original content preserved in git history at `<commit-sha>:Plans/SaveFixPrototype/plan.md:210-214`.

---

## Difficulty setting

`<DifficultySetting Id="Normal" />` near the top of `world.xml`. Valid values are `Easy`, `Normal`, `Hard`, `Stationeer`.

Full content lifted to:
- [../../Research/Protocols/WorldXml.md](../../Research/Protocols/WorldXml.md) - `<DifficultySetting>` element location and enum values.

Lifted during Phase 5 migration on 2026-04-20. Original content preserved in git history at `<commit-sha>:Plans/SaveFixPrototype/plan.md:216-220`.

---

## The prototype: terrain_reset.py

### What it does

The tool opens a `.save` ZIP, parses room grid cells out of `world.xml`, expands each 2x2x2 cell by a margin (default 3 voxels) for wall structures, and walks the `terrain.dat` DFS octree. For each node it tests overlap against the keep bounding box: no overlap collapses a branch to a single unmodified leaf or replaces a modified leaf with an unmodified one; overlap writes through and recurses. It then filters vein records to those inside the keep box, zeros all 64 `TerrainChunkChecksums`, and repacks the ZIP. A `.save.bak` backup is created if one doesn't exist.

### Usage

```
python terrain_reset.py path/to/save.save              # apply
python terrain_reset.py path/to/save.save --dry-run     # analyze only
python terrain_reset.py path/to/save.save --margin 5    # wider wall buffer
```

### Design decisions and known limitations

Bounding-box overlap (not per-voxel), conservative leaf handling (partial overlap keeps the whole leaf), no post-rewrite branch re-collapse, and a `01 00 00` unmodified replacement leaf (values irrelevant because `IS_MODIFIED` is clear).

Full content lifted to:
- [../../Research/Workflows/ResetTerrainOffline.md](../../Research/Workflows/ResetTerrainOffline.md) - `terrain_reset.py` algorithm, CLI usage, and design decisions / known limitations.

Mod-local test metrics from the tested-against `Luna.save` run moved to `Plans/SaveFixPrototype/RESEARCH.md` under Test observations.

Lifted during Phase 5 migration on 2026-04-20. Original content preserved in git history at `<commit-sha>:Plans/SaveFixPrototype/plan.md:222-266`.

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

For each voxel to reset, convert world position to octree space, read the base density via `VoxelTerrain.ReadOnlyOctree.Get(octreePos)`, and write it back with `VoxelTerrain.Octree.SetDensity(worldPos, baseDensity, ...)`. This overwrites the delta with the base value; on next save the node collapses. Process a few hundred voxels per tick; sort by distance from base center (furthest first) for a dramatic closing-in effect.

Full content lifted to:
- [../../Research/Workflows/ResetTerrainLive.md](../../Research/Workflows/ResetTerrainLive.md) - Live-MP runtime reset approach: `SetDensity` per-voxel recipe and tick-budget considerations.

Open questions about `SetDensity` network sync, mesh rebuild, room-manager access, tick budget, Stationeers.Addons lifecycle, and effects APIs moved to `Plans/SaveFixPrototype/RESEARCH.md` under Open questions. Lore-concept brainstorm (Seismic Reclamation / Nanite Reconstruction / Temporal Anomaly) is implementation strategy and stays below.

Lifted during Phase 5 migration on 2026-04-20. Original content preserved in git history at `<commit-sha>:Plans/SaveFixPrototype/plan.md:268-313`.

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

Moved to `Plans/SaveFixPrototype/RESEARCH.md` under Test observations. The `Luna.save` test-save inventory (world seed, MaxDepth, player IDs, room layout, structure extents, terrain-delta spans, excavation metrics, vein counts, pipe-network state, prior edit history, and the `Luna.save.bak` backup pointer) is evidence rather than durable research, so it lives in the mod-local RESEARCH file rather than a central page.

Full content lifted to:
- `Plans/SaveFixPrototype/RESEARCH.md` - Test observations section (mod-local, not a central page).

Lifted during Phase 5 migration on 2026-04-20. Original content preserved in git history at `<commit-sha>:Plans/SaveFixPrototype/plan.md:332-346`.
