---
title: Device Port Geometry (power connection cells)
type: GameSystems
created_in: 0.2.6403.27689
verified_in: 0.2.6403.27689
verified_at: 2026-07-17
sources:
  - .work/decomp/0.2.6403.27689/Assembly-CSharp.decompiled.cs :: lines 312730-312745 (connection cell math), 312896-312941 (FillConnected)
  - DedicatedServer/dev-plugins/ScenarioRunner/ScenarioRunner/Dispatcher.FreshDeviceTrace.cs :: FdProbeOne / FdProbeSpawnMainThread (the probe instrument)
  - Live probe capture on the dedicated server, fresh Lunar world, 2026-07-17 (scenario pgp-fresh-device-trace, PROBE log lines quoted verbatim below)
related:
  - ../GameClasses/Structure.md
  - ../GameClasses/Device.md
  - ../GameClasses/Cable.md
tags: [power, prefab]
---

# Device Port Geometry (power connection cells)

Which small-grid cells a device's power ports occupy and face, measured live per prefab. This is the data needed to place cables programmatically so they actually connect (fixture authoring, save editing, spawn tooling); hand-positioning cables without it fails silently because the connection law is cell-exact.

## The connection law
<!-- verified: 0.2.6403.27689 @ 2026-07-17 -->

From the decompile (connection cell math at lines 312730-312745, `FillConnected` at 312896-312941 of `.work/decomp/0.2.6403.27689/Assembly-CSharp.decompiled.cs`):

A device port connects to the cable occupying the port's `LocalGrid` cell only when that cable has an open end whose `LocalGrid` equals the port's `FacingGrid`. The relation is mutual: the cable end must sit in the cell the port occupies, AND point back at the cell the port faces. For every single-cell device probed below, the port's `FacingGrid` is the device's own body cell (facing offset `(0,0,0)` from `ThingTransformPosition` at identity rotation), so in practice the adjacent cable end must aim at the device body.

Connections register at structure COMPLETION, not at kit placement: a multi-build-state structure at `CurrentBuildStateIndex` 0 has not run its port registration, sits on no cable network, and is invisible to the power grid until the final build state lands (see Structure.md, "Construction completion" and the build-state completion boundary section). This is why a placed-but-unfinished device shows no power behavior of any kind.

## How the data was captured
<!-- verified: 0.2.6403.27689 @ 2026-07-17 -->

The ScenarioRunner scenario `pgp-fresh-device-trace` spawns each prefab at identity rotation on a fresh dedicated-server world and reads, for every entry of `SmallGrid.OpenEnds` whose `ConnectionType` includes `NetworkType.Power`:

- `ConnectionRole` (None / Input / Output),
- `ConnectionType` (Power / PowerAndData),
- `LocalGrid` minus `ThingTransformPosition` (the port cell offset, printed as `local=`),
- `FacingGrid` minus `ThingTransformPosition` (the faced cell offset, printed as `facing=`),
- `Structure.BuildStates.Count` (printed as `buildStates=`).

Reproducible: set `Scenario = pgp-fresh-device-trace` in the ScenarioRunner config, start a fresh world, and grep the log for `FDT PROBE`. Offsets are in world meters on the small grid (0.5 m half-cell steps); a rotated placement rotates the offsets with the body.

## Measured port geometry (verbatim capture, 2026-07-17)
<!-- verified: 0.2.6403.27689 @ 2026-07-17 -->

```
FDT PROBE StructureRTG body=(440,400,400) buildStates=1 ports=[None:Power local=(0,0.5,0) facing=(0,0,0)]
FDT PROBE StructureTransformerSmall body=(440,400,404) buildStates=1 ports=[Input:PowerAndData local=(-0.5,0,0) facing=(0,0,0) | Output:Power local=(0.5,0,0) facing=(0,0,0)]
FDT PROBE StructureWallLight body=(440,400,408) buildStates=1 ports=[None:PowerAndData local=(0,-0.5,0) facing=(0,0,0)]
FDT PROBE StructureGrowLight body=(440,400,412) buildStates=1 ports=[None:Power local=(0,-0.5,0) facing=(0,0,0)]
FDT PROBE StructureConsole body=(440,400,416) buildStates=2 ports=[None:PowerAndData local=(0,-0.5,0) facing=(0,0,0)]
FDT PROBE StructureWallCooler body=(440,400,420) buildStates=1 ports=[None:PowerAndData local=(0,0.5,0) facing=(0,0,0)]
FDT PROBE StructureWallHeater body=(440,400,424) buildStates=1 ports=[None:PowerAndData local=(0,0.5,0) facing=(0,0,0)]
FDT PROBE StructureCableCorner body=(444,400,428) buildStates=1 ports=[None:PowerAndData local=(-0.5,0,0) facing=(0,0,0) | None:PowerAndData local=(0,0,0.5) facing=(0,0,0)]
FDT PROBE StructureCableCornerH body=(444,400,432) buildStates=1 ports=[None:PowerAndData local=(0,0,-0.5) facing=(0,0,0) | None:PowerAndData local=(-0.5,0,0) facing=(0,0,0)]
FDT PROBE StructureCableJunction body=(444,400,436) buildStates=1 ports=[None:PowerAndData local=(0,0,-0.5) facing=(0,0,0) | None:PowerAndData local=(0,0,0.5) facing=(0,0,0) | None:PowerAndData local=(-0.5,0,0) facing=(0,0,0)]
FDT PROBE StructureCableJunctionH body=(444,400,440) buildStates=1 ports=[None:PowerAndData local=(0.5,0,0) facing=(0,0,0) | None:PowerAndData local=(-0.5,0,0) facing=(0,0,0) | None:PowerAndData local=(0,0,-0.5) facing=(0,0,0)]
FDT PROBE StructureCableJunction4 body=(444,400,444) buildStates=1 ports=[None:PowerAndData local=(0,0,-0.5) facing=(0,0,0) | None:PowerAndData local=(0,0,0.5) facing=(0,0,0) | None:PowerAndData local=(-0.5,0,0) facing=(0,0,0) | None:PowerAndData local=(0.5,0,0) facing=(0,0,0)]
FDT PROBE StructureCableJunctionH4 body=(444,400,448) buildStates=1 ports=[None:PowerAndData local=(0,0,-0.5) facing=(0,0,0) | None:PowerAndData local=(0,0,0.5) facing=(0,0,0) | None:PowerAndData local=(-0.5,0,0) facing=(0,0,0) | None:PowerAndData local=(0.5,0,0) facing=(0,0,0)]
FDT PROBE StructureCableStraight body=(444,400,452) buildStates=1 ports=[None:PowerAndData local=(0,0,-0.5) facing=(0,0,0) | None:PowerAndData local=(0,0,0.5) facing=(0,0,0)]
FDT PROBE StructureCableStraightH body=(444,400,456) buildStates=1 ports=[None:PowerAndData local=(0,0,-0.5) facing=(0,0,0) | None:PowerAndData local=(0,0,0.5) facing=(0,0,0)]
```

Normalized (offsets from the registered body position, identity rotation):

| Prefab | Build states | Ports (role : type @ local offset) |
|---|---|---|
| StructureRTG | 1 | None : Power @ (0, +0.5, 0) |
| StructureTransformerSmall | 1 | Input : PowerAndData @ (-0.5, 0, 0); Output : Power @ (+0.5, 0, 0) |
| StructureWallLight | 1 | None : PowerAndData @ (0, -0.5, 0) |
| StructureGrowLight | 1 | None : Power @ (0, -0.5, 0) |
| StructureConsole | 2 | None : PowerAndData @ (0, -0.5, 0) |
| StructureWallCooler | 1 | None : PowerAndData @ (0, +0.5, 0) |
| StructureWallHeater | 1 | None : PowerAndData @ (0, +0.5, 0) |
| StructureCableCorner | 1 | 2x None : PowerAndData @ (-0.5, 0, 0) and (0, 0, +0.5) |
| StructureCableCornerH | 1 | 2x None : PowerAndData @ (0, 0, -0.5) and (-0.5, 0, 0) |
| StructureCableJunction | 1 | 3x None : PowerAndData @ (0, 0, -0.5), (0, 0, +0.5), (-0.5, 0, 0) |
| StructureCableJunctionH | 1 | 3x None : PowerAndData @ (+0.5, 0, 0), (-0.5, 0, 0), (0, 0, -0.5) |
| StructureCableJunction4 | 1 | 4x None : PowerAndData @ all four horizontal half-cells |
| StructureCableJunctionH4 | 1 | 4x None : PowerAndData @ all four horizontal half-cells |
| StructureCableStraight | 1 | 2x None : PowerAndData @ (0, 0, -0.5) and (0, 0, +0.5) |
| StructureCableStraightH | 1 | 2x None : PowerAndData @ (0, 0, -0.5) and (0, 0, +0.5) |

## Notable facts
<!-- verified: 0.2.6403.27689 @ 2026-07-17 -->

- **The small transformer's two ports differ in type**: the Input port is `PowerAndData`, the Output port is `Power` only. A data-carrying read reaches the transformer only from its input side wiring.
- **The RTG's single port points up** (`(0, +0.5, 0)` at identity rotation) and is `Power` only, consistent with the RTG exposing no logic surface.
- **Wall-mount devices put their port half a cell toward the mount face**: WallLight / GrowLight / Console at `(0, -0.5, 0)`, WallCooler / WallHeater at `(0, +0.5, 0)`. GrowLight's port is `Power` only; the other wall devices carry `PowerAndData`.
- **StructureConsole is the only probed prefab with 2 build states**; everything else in this set completes at state 0. A 2-state device spawned or placed at state 0 is grid-invisible until completed (the completion boundary, Structure.md).
- **All facing offsets read `(0,0,0)`** for single-cell devices: the faced cell is the body cell, so a connecting cable end must occupy the port's half-cell and aim at the device body.
- Cable pieces expose role `None` on every end; only the transformer distinguishes Input / Output roles in this set.

## Verification history

- 2026-07-17: page created from the live probe capture on the dedicated server (fresh Lunar world, game 0.2.6403.27689) plus the decompile-cited connection law; all 16 probe lines quoted verbatim.

## Open questions

- Multi-cell prefabs (cable straights 3/5/10, large devices) and non-identity mount rotations were not probed; their per-cell port lists and facing cells are uncaptured.
- Port geometry for the APC, batteries, and the large transformer family is uncaptured (the probe set covered the fresh-device-trace chain only).
