---
title: Reset Terrain Offline (terrain_reset.py)
type: Workflows
created_in: 0.2.6228.27061
verified_in: 0.2.6228.27061
verified_at: 2026-04-20
sources:
  - Plans/SaveFixPrototype/plan.md:228-236
  - Plans/SaveFixPrototype/plan.md:246-251
related:
  - ../Protocols/TerrainDat.md
  - ../Protocols/SaveZipLayout.md
  - ResetTerrainLive.md
tags: [terrain, save-edit, save-format, python]
---

# Reset Terrain Offline (terrain_reset.py)

Run the `terrain_reset.py` script against a closed `.save` archive to collapse every modified voxel outside a computed "keep" bounding box back to base terrain. Reach for this recipe when a save has accumulated so many minor terrain deltas that performance or file size has degraded, but the player wants to preserve the built-up area around their base.

## When to use
<!-- verified: 0.2.6228.27061 @ 2026-04-20 -->

- The `.save` archive is closed (game not running).
- The player wants to reset terrain everywhere except inside the rooms they have built, plus a small wall buffer.
- A wider or narrower wall buffer is desired than the default 3-voxel margin.

For a live multiplayer reset during a running session, see [ResetTerrainLive.md](ResetTerrainLive.md) instead.

## Prerequisites
<!-- verified: 0.2.6228.27061 @ 2026-04-20 -->

- A closed `.save` archive (the game must not be running against the save).
- Python available on the command line.
- Enough disk space to hold the archive plus the automatic `.save.bak` backup.

## What it does
<!-- verified: 0.2.6228.27061 @ 2026-04-20 -->

1. Opens the .save ZIP, reads world.xml and terrain.dat.
2. Parses all `<Room>` grid cells from world.xml, converts to world coordinates.
3. Expands each 2x2x2 cell by a configurable margin (default 3 voxels) for wall structures. Produces a bounding box.
4. Walks the terrain.dat DFS octree. For each node, tests whether its spatial volume overlaps the keep bounding box.
   - **No overlap:** if it's a branch, skips all children in the input stream and writes a single unmodified leaf (`01 00 00`), collapsing the entire subtree. If it's a modified leaf, replaces it with an unmodified leaf.
   - **Overlap:** writes the node through unchanged. If it's a branch, recurses into all 8 children.
5. Filters vein records: keeps only those whose VeinWorldPosition falls inside the keep bounding box.
6. Zeros all 64 TerrainChunkChecksums.
7. Repacks the ZIP. Creates a .save.bak backup if one doesn't exist.

## Usage
<!-- verified: 0.2.6228.27061 @ 2026-04-20 -->

```
python terrain_reset.py path/to/save.save              # apply
python terrain_reset.py path/to/save.save --dry-run     # analyze only
python terrain_reset.py path/to/save.save --margin 5    # wider wall buffer
```

## Verification
<!-- verified: 0.2.6228.27061 @ 2026-04-20 -->

- Run with `--dry-run` first to see the planned modifications: how many modified voxels would be reset, how many branches would be collapsed, how many veins would be stripped.
- The first real run creates a `.save.bak` backup automatically. Keep it until the reloaded save is verified in-game.
- Load the modified save in Stationeers; confirm that interior of the keep region is intact, that exterior terrain has reset to base, and that the file size has shrunk substantially.

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

## Design decisions and known limitations
<!-- verified: 0.2.6228.27061 @ 2026-04-20 -->

- **Bounding box not per-voxel:** overlap test uses the rectangular bounding box of the keep set, not the per-voxel set. This over-preserves slightly at corners of L-shaped rooms. The per-voxel set is computed but not used for leaf-level filtering. A future improvement could check individual leaves against the set.
- **Conservative leaf handling:** a modified leaf that partially overlaps the keep zone is kept entirely. Splitting would require base-terrain values we don't have in the save file.
- **No branch re-collapse:** after rewriting, a branch might have all-identical children that could be collapsed back to a leaf. This optimization is skipped; the game handles it fine.
- **Unmodified leaf bytes:** replacement leaf is `01 00 00` (IS_LEAF, density=0, nodeType=None). The values are irrelevant because IS_MODIFIED is not set, so the game reads from base terrain instead.

## Pitfalls
<!-- verified: 0.2.6228.27061 @ 2026-04-20 -->

- Run only against a closed save. The script does not coordinate with a running game session.
- The 3-voxel default margin is designed for typical wall thickness. Underground bases excavated from solid rock may need a wider margin to avoid slicing walls off from the kept region.
- The script zeros all 64 TerrainChunkChecksums. This is required because rewriting the octree invalidates the old checksums; loading a save with inconsistent checksums can reject terrain data.

## Verification history
<!-- verified: 0.2.6228.27061 @ 2026-04-20 -->

- 2026-04-20: page created from the Research migration; verbatim content lifted from F0240 (algorithm) and F0241 (design decisions) in `Plans/SaveFixPrototype/plan.md`.

## Open questions

None at creation.
