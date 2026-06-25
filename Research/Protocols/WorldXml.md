---
title: world.xml
type: Protocols
created_in: 0.2.6228.27061
verified_in: 0.2.6228.27061
verified_at: 2026-04-20
sources:
  - Plans/SaveFixPrototype/plan.md:160-178
  - Plans/SaveFixPrototype/plan.md:217-220
related:
  - ./SaveFileStructure.md
  - ./AtmosphereSaveData.md
  - ./TerrainChunkChecksums.md
tags: [save-format, save-edit]
---

# world.xml

`world.xml` is the primary save payload: every Thing, atmosphere, network, and player lives here. This page documents individual subsections as they are investigated.

## Rooms (grid coordinates)
<!-- verified: 0.2.6228.27061 @ 2026-04-20 -->

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

## DifficultySetting
<!-- verified: 0.2.6228.27061 @ 2026-04-20 -->

`<DifficultySetting Id="Normal" />` near the top of world.xml (a direct child of root `<WorldData>`, serialized via `XmlSaveLoad.WorldData.DifficultySetting`, a `SerializedId` whose `Id` is an `[XmlAttribute]`). Values include `Easy`, `Normal`, `Stationeer`, and `Creative`. The game mode (Survival vs Creative) is NOT a separate field; it is derived from this difficulty on load. Setting `Id="Creative"` enables creative mode and persists across reloads. See [../GameSystems/CreativeModeAndDifficulty.md](../GameSystems/CreativeModeAndDifficulty.md) for the full mechanism, the stock difficulty presets, and the `Creative` preset's field values.

## Celestial (sun position / world time)
<!-- verified: 0.2.6228.27061 @ 2026-06-25 -->

The `<Celestial>` element is a direct child of root `<WorldData>` and holds the orbital/sun clock. It serializes `OrbitSimulationSaveData` (see [../GameClasses/OrbitalSimulation.md](../GameClasses/OrbitalSimulation.md) for the full mechanism). Two attribute-valued `DoubleReference` children, both in seconds:

```xml
<Celestial>
  <AccumulatedTime Value="407548.122450768" />
  <SimulationTime Value="122597.43671865921" />
</Celestial>
```

- `SimulationTime/@Value` is authoritative. On load, `OrbitSimulationSaveData.Deserialize` calls `OrbitalSimulation.SetSimulationTime(SimulationTime.Value)`, which rebuilds the sun direction `WorldSunVector` from this clock. Editing it moves the sun. Setting it does NOT freeze the sun (the sim resumes ticking on load); a freeze needs `TimeScale = 0` at runtime, e.g. the `orbital timescale 0` console command.
- `AccumulatedTime/@Value` is total real time; used as the fallback time source and to restore `TotalRealTimeSeconds`.
- If `<Celestial>` is absent, the loader falls back to `SetRealTime(DaysPast * siderealDaySeconds)`.

Sibling clock fields (also direct children of `<WorldData>`, mirrored in `world_meta.xml`):

- `<DaysPast>` (uint): in-world day counter; the `<Celestial>`-absent fallback time source.
- `<DateTime>` (.NET `DateTime.Ticks`, 100ns units): calendar/HUD clock display only. NOT read for sun position. The time-of-day component decodes to the wall-clock time shown in-game.

There is no explicit sun-angle or day-length element in world.xml; the sun is derived at runtime from the `<Celestial>` clock and the world setting's day length.

## Verification history
<!-- verified: 0.2.6228.27061 @ 2026-06-25 -->

- 2026-04-20: page created from the Research migration. Sources: F0236 (Rooms grid coordinate scale + cell size), F0250 (DifficultySetting enum location).
- 2026-06-25: added the Celestial section (sun position / world time fields) from a decompile read of `OrbitSimulationSaveData` / `WorldData` (game version 0.2.6228.27061) and confirmation against a real Luna save's world.xml. Documents `SimulationTime`, `AccumulatedTime`, `DaysPast`, `DateTime`.
- 2026-06-25: expanded the DifficultySetting section to note it is a `SerializedId` child of `<WorldData>`, that the live `difficultySettings.xml` ships a `Creative` value, and that the game mode (Survival/Creative) is derived from this element rather than stored separately. Cross-linked the new `../GameSystems/CreativeModeAndDifficulty.md`. Verified against a real Luna save's world.xml (`<DifficultySetting Id="Normal" />`) and the `XmlSaveLoad.WorldData` decompile.

## Open questions

None at creation.
