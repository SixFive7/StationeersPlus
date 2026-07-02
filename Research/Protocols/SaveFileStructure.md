---
title: Save file ZIP structure
type: Protocols
created_in: 0.2.6228.27061
verified_in: 0.2.6403.27689
verified_at: 2026-07-02
sources:
  - Plans/RepairPrototype/plan.md:58-66
  - .work/decomp/0.2.6403.27689/Assembly-CSharp.decompiled.cs :: SaveLoadConstants (lines 265202-265210), SaveHelper.Save (264972-265043)
  - DedicatedServer/data/saves/Luna/Luna.save (entry listing, 2026-07-02)
related:
  - ./TerrainDat.md
  - ./TerrainChunkChecksums.md
  - ./WorldXml.md
  - ./AtmosphereSaveData.md
  - ../GameSystems/SaveZipExtension.md
tags: [save-format, save-edit]
---

# Save file ZIP structure

Top-level file layout of a Stationeers save ZIP. The archive is a standard ZIP; each entry is plaintext XML, binary voxel data, or a PNG. Sizes in the example column are from an observed mid-sized save and are indicative, not exact.

## File table
<!-- verified: 0.2.6403.27689 @ 2026-07-02 -->

| File | Size (example) | Content |
|---|---|---|
| `world_meta.xml` | ~700 B | Save metadata: world name, version, stats |
| `world.xml` | ~2.6 MB | ALL game data: things, atmospheres, networks, players |
| `terrain.dat` | ~184 KB | Terrain/voxel data (binary) |
| `preview.png` | ~125 KB | Save thumbnail |
| `screenshot.png` | ~139 KB | Screenshot |

The five names are code constants (`SaveLoadConstants`, 0.2.6403.27689 decompile lines 265202-265210): `TerrainFileName = "terrain.dat"`, `MetaFileName = "world_meta.xml"`, `WorldFileName = "world.xml"`, `PreviewFileName = "preview.png"`, `ScreenshotFileName = "screenshot.png"`. The writer (`SaveHelper.Save`, line 264972) emits them in the order `world_meta.xml`, `world.xml`, `terrain.dat`, `preview.png`, `screenshot.png`, and skips the two PNGs entirely in batch mode (`if (!GameManager.IsBatchMode)`, line 265020), so a save written BY a dedicated server has only the first three members. A save that carries the PNGs was last written by a client.

## Mod sidecar entries
<!-- verified: 0.2.6403.27689 @ 2026-07-02 -->

A save ZIP can carry additional entries beyond the five standard members: mods append their own files at save time. Observed entry listing of a real modded save (`DedicatedServer/data/saves/Luna/Luna.save`, 2026-07-02, sizes in bytes):

```
       698  world_meta.xml
  61514025  world.xml
    456908  terrain.dat
    149788  preview.png
    177904  screenshot.png
       779  equipmentplus-active-slots.xml
       551  equipmentplus-beam.xml
      3667  pwrxmplus-autoaim.xml
      4421  pwrgridplus-passthrough.xml
       486  pwrgridplus-priority.xml
```

The last five are per-mod sidecars written by this repository's mods: `equipmentplus-active-slots.xml` and `equipmentplus-beam.xml` by EquipmentPlus (`ActiveSlotSideCar.SideCarEntryName`, `HelmetBeamSideCar.SideCarEntryName`), `pwrxmplus-autoaim.xml` by PowerTransmitterPlus (`AutoAimSideCar.SideCarEntryName`), and `pwrgridplus-passthrough.xml` / `pwrgridplus-priority.xml` by PowerGridPlus (`PassthroughSideCar.SideCarEntryName`, `PrioritySideCar.SideCarEntryName`). The mechanism, and why it works, is documented in [../GameSystems/SaveZipExtension.md](../GameSystems/SaveZipExtension.md): the game's LOAD path looks up known entry names and silently ignores everything else, while the game's SAVE path rebuilds the ZIP from scratch with only the known members, so a mod must re-append its sidecar on every save (Harmony patch on the private `SaveHelper.Save` worker).

Consequences for offline save tooling:

- Any tool that repacks a save ZIP MUST copy through unknown entries verbatim (name, order, bytes), or it silently strips mod data. A repack that hardcodes the five standard members destroys the sidecars without any error; the mods then see missing state on next load. This bit in practice on 2026-07-02: `tools/save-edit/stationeers_save.py` `repack()` writes only the five standard members, so the save-surgery session used a one-off script that copies every entry through and edits only world.xml, precisely to keep the five sidecars alive.
- The sidecar names are chosen by each mod; there is no registry and no namespace rule beyond collision avoidance. Treat "entries I do not recognize" as load-bearing mod data, never as junk.

## Verification history
<!-- verified: 0.2.6228.27061 @ 2026-04-20 -->

- 2026-04-20: page created from the Research migration. Source: F0220 (save file ZIP structure).
- 2026-07-02: re-confirmed the file table against the 0.2.6403.27689 decompile (`SaveLoadConstants` names, `SaveHelper.Save` write order, batch-mode PNG skip) and added the "Mod sidecar entries" section from the observed Luna.save entry listing during the same day's save-surgery session. Mechanism cross-referenced to `../GameSystems/SaveZipExtension.md` rather than duplicated.

## Open questions

None at creation.
