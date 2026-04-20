---
title: Save file ZIP structure
type: Protocols
created_in: 0.2.6228.27061
verified_in: 0.2.6228.27061
verified_at: 2026-04-20
sources:
  - Plans/RepairPrototype/plan.md:58-66
related:
  - ./TerrainDat.md
  - ./TerrainChunkChecksums.md
  - ./WorldXml.md
  - ./AtmosphereSaveData.md
tags: [save-format, save-edit]
---

# Save file ZIP structure

Top-level file layout of a Stationeers save ZIP. The archive is a standard ZIP; each entry is plaintext XML, binary voxel data, or a PNG. Sizes in the example column are from an observed mid-sized save and are indicative, not exact.

## File table
<!-- verified: 0.2.6228.27061 @ 2026-04-20 -->

| File | Size (example) | Content |
|---|---|---|
| `world_meta.xml` | ~700 B | Save metadata: world name, version, stats |
| `world.xml` | ~2.6 MB | ALL game data: things, atmospheres, networks, players |
| `terrain.dat` | ~184 KB | Terrain/voxel data (binary) |
| `preview.png` | ~125 KB | Save thumbnail |
| `screenshot.png` | ~139 KB | Screenshot |

## Verification history
<!-- verified: 0.2.6228.27061 @ 2026-04-20 -->

- 2026-04-20: page created from the Research migration. Source: F0220 (save file ZIP structure).

## Open questions

None at creation.
