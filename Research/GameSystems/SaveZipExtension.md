---
title: Side-car file persistence in save ZIPs
type: GameSystems
created_in: 0.2.6228.27061
verified_in: 0.2.6228.27061
verified_at: 2026-04-21
sources:
  - rocketstation_Data/Managed/Assembly-CSharp.dll :: Assets.Scripts.Serialization.XmlSaveLoad
  - Plans/SaveFixPrototype/terrain_reset.py
related:
  - ../Protocols/SaveFileStructure.md
  - ../GameSystems/SaveDataRegistration.md
  - ../GameSystems/UnregisteredSaveDataBehavior.md
tags: [save-load, save-format, launchpad]
---

# Side-car file persistence in save ZIPs

Feasibility study for mods to write auxiliary files into the Stationeers save ZIP to persist optional mod state without custom ThingSaveData subclasses.

## Core finding: read-safe, write-unsafe
<!-- verified: 0.2.6228.27061 @ 2026-04-21 -->

- **Load:** Unknown ZIP entries are **preserved silently** (reader only looks up known filenames by name).
- **Save:** Unknown ZIP entries are **stripped** (SaveWorld rebuilds the ZIP from scratch, known entries only).
- **Implication:** Side-car persistence requires mod to write the file on every save, not just once.

## ZIP read path (LoadWorld)
<!-- verified: 0.2.6228.27061 @ 2026-04-21 -->

Uses ZipArchive.GetEntry(name) for known filenames only. Unknown entries remain untouched in the archive.

## ZIP write path (SaveWorld)
<!-- verified: 0.2.6228.27061 @ 2026-04-21 -->


Rebuilds archive from scratch:
1. New ZipArchive created in write mode
2. Known entries serialized (world.xml, world_meta.xml, terrain.dat, preview.png, screenshot.png)
3. Unknown entries NOT copied forward
4. ZipArchive disposed, sealing and closing the file

Single seal point at stream close. Side-car file must be injected before that moment.

## LaunchPadBooster extension hooks
<!-- verified: 0.2.6228.27061 @ 2026-04-21 -->

**No public API exists** for ZIP-entry injection. `Mod.AddSaveDataType<T>()` registers only ThingSaveData subclasses in XmlSaveLoad.ExtraTypes.

**Required workaround:** Harmony Prefix or Postfix on XmlSaveLoad.SaveWorld() to intercept the ZipArchive and inject the side-car file before disposal.

## Post-load hook for re-application
<!-- verified: 0.2.6228.27061 @ 2026-04-21 -->

**Recommended:** Thing.OnFinishedLoad() per-Thing Postfix
- Fires after DeserializeSave and child placement complete
- Safe for SetCustomColor(index, emissive: true)
- Read side-car file once during LoadWorld postfix into a cache, look up per-Thing

**Alternative:** XmlSaveLoad.LoadWorld() postfix for bulk re-apply to all Things

## State capture at save time
<!-- verified: 0.2.6228.27061 @ 2026-04-21 -->

At SaveWorld() call:
- GlowPaintHelpers.GlowingThingIds dictionary is fully populated
- Main thread only
- Enumeration and XML serialization can happen synchronously before ZIP seal

## Multiplayer and save operations
<!-- verified: 0.2.6228.27061 @ 2026-04-21 -->

- **Multiplayer:** Host writes saves only; clients cannot inject side-car entries
- **Save-copy:** Game's own backup/copy operations preserve unknown ZIP entries (file copy, not rebuild)
- **Auto-save vs manual:** Same SaveWorld() code path

## Precedent: terrain_reset.py
<!-- verified: 0.2.6228.27061 @ 2026-04-21 -->

Plans/SaveFixPrototype/terrain_reset.py reads/modifies terrain.dat in the save ZIP, confirming that save files are standard ZIP archives and unknown entries persist through offline editing. No shipped mod currently writes side-car files.

## Verdict
<!-- verified: 0.2.6228.27061 @ 2026-04-21 -->


**Side-car approach is viable for optional cosmetic state (glow colors)** with these constraints:
1. Mod must inject side-car file on every save (Harmony patch required)
2. Mod must read and re-apply state on every load (OnFinishedLoad postfix)
3. Without mod, side-car file is harmlessly ignored; glow state is simply lost (desired behavior for optional features)
4. Enables removal without save breakage, unlike custom ThingSaveData which breaks loads when mod is absent

Not a replacement for critical save-breaking state (use ThingSaveData + registration for that). For glow-paint, this approach is superior to current GlowThingSaveData method because removal becomes non-fatal.

## Verification history

- 2026-04-21: page created from decompilation of XmlSaveLoad.SaveWorld and LoadWorld in Assembly-CSharp.dll v0.2.6228.27061, combined with terrain_reset.py precedent analysis.

## Open questions

- Exact Harmony pattern to capture active ZipArchive without IL patching
- Stream wrapping feasibility before ZipArchive creation
- Should removal trigger user warning about abandoned side-car data?

