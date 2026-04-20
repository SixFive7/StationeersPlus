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

`<DifficultySetting Id="Normal" />` near the top of world.xml. Values: `Easy`, `Normal`, `Hard`, `Stationeer`.

## Verification history
<!-- verified: 0.2.6228.27061 @ 2026-04-20 -->

- 2026-04-20: page created from the Research migration. Sources: F0236 (Rooms grid coordinate scale + cell size), F0250 (DifficultySetting enum location).

## Open questions

None at creation.
