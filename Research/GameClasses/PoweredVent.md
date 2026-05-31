---
title: PoweredVent / ActiveVent (vent direction semantics)
type: GameClasses
created_in: 0.2.6228.27061
verified_in: 0.2.6228.27061
verified_at: 2026-05-31
sources:
  - rocketstation_Data/Managed/Assembly-CSharp.dll :: Assets.Scripts.Objects.Pipes.VentDirection (enum)
  - rocketstation_Data/Managed/Assembly-CSharp.dll :: Assets.Scripts.Objects.Pipes.PoweredVentMultiGrid.ExchangeWithWorld
  - rocketstation_Data/Managed/Assembly-CSharp.dll :: Assets.Scripts.Objects.Pipes.PoweredVentSingleGrid.ExchangeWithWorld
  - rocketstation_Data/Managed/Assembly-CSharp.dll :: Assets.Scripts.Objects.Pipes.ActiveVent.OnAtmosphericTick
related:
tags: [logic, ic10]
---

# PoweredVent / ActiveVent: vent direction semantics

The active-vent family (`ActiveVent` = the small Active Vent, `PoweredVent` / `PoweredVentSingleGrid` / `PoweredVentMultiGrid` = the Large Powered Vent) moves gas between the room atmosphere the vent sits in and the pipe network it is connected to. The direction of flow is controlled by the `VentDirection` enum, settable over logic as `LogicType.Mode`. This page records which way each direction actually moves gas, because the naming is easy to invert and the error silently breaks any IC10 airlock or pump-down routine.

## VentDirection enum values
<!-- verified: 0.2.6228.27061 @ 2026-05-31 -->

```csharp
public enum VentDirection
{
    Inward = 1,
    Outward = 0
}
```

So in IC10, `Mode 0` = Outward, `Mode 1` = Inward.

## Which way each direction moves gas
<!-- verified: 0.2.6228.27061 @ 2026-05-31 -->

The flow is the OPPOSITE of the intuitive reading of the names. "Inward" and "Outward" are named relative to the PIPE, not relative to the room:

- **`Inward` (1)** calls `PumpGasToPipe(roomAtmosphere, pipeAtmosphere, ...)`. Gas moves FROM the room INTO the pipe network. The net effect is that the room the vent is in is **EVACUATED** (room loses pressure; pipe gains it). To pump a room down to vacuum, set the vent in that room to **Inward**.
- **`Outward` (0)** calls `PumpGasToWorld(roomAtmosphere, pipeAtmosphere, ...)`. Gas moves FROM the pipe network INTO the room/world. The net effect is that the room the vent is in is **FILLED** (room gains pressure; pipe loses it). To pressurize a room from the pipe, set the vent in that room to **Outward**.

`PoweredVentMultiGrid.ExchangeWithWorld` (Large Powered Vent), verbatim case heads:

```csharp
case VentDirection.Inward:
{
    PressurekPa pressurekPa = base.PressurePerTick - MultiGridAtmospherics.PumpGasToPipe(atmosphere, targetAtmosphere, base.PressurePerTick, base.ExternalPressure);
    ...
case VentDirection.Outward:
{
    PressurekPa pressurekPa = base.PressurePerTick - MultiGridAtmospherics.PumpGasToWorld(atmosphere, targetAtmosphere, totalTemperature, base.PressurePerTick, base.ExternalPressure);
    ...
```

where `atmosphere` is the cloned world/room atmosphere (`CloneGlobalAtmosphere(base.WorldGrid, 0L)`) and `targetAtmosphere => ConnectedPipeNetwork.Atmosphere`.

`PoweredVentSingleGrid.ExchangeWithWorld` and `ActiveVent.OnAtmosphericTick` use the identical pattern: `Inward` -> `PumpGasToPipe`, `Outward` -> `PumpGasToWorld`. The semantics are shared across the whole active-vent family.

## PressureExternal (the pressure cap)
<!-- verified: 0.2.6228.27061 @ 2026-05-31 -->

`LogicType.PressureExternal` maps to the vent's `ExternalPressure` field (`PoweredVent` exposes it as a readable/writable logic value; the property default seen on the related airlock-control class is `101f` kPa). It is the pressure target the vent pumps toward and is passed straight into the `PumpGasToPipe` / `PumpGasToWorld` / `InwardsPressureRequired` / `OutwardsPressureRequired` calls as the limit. A vent stops doing useful work once the relevant side reaches `ExternalPressure`.

Setting `PressureExternal` to a very large number (IC10 scripts commonly write `100000`, i.e. 100000 kPa) effectively removes the cap so the vent keeps pumping until the source side is exhausted (vacuum on the Inward source, or the pipe is drained on the Outward source). This is the "unlimit the vent" idiom. There is no separate enable flag for unlimited mode; a large `PressureExternal` is the mechanism.

## IC10 usage notes
<!-- verified: 0.2.6228.27061 @ 2026-05-31 -->

To pump a chamber down to vacuum and store its gas in the pipe network: set the chamber's vent to `Mode 1` (Inward), set its `PressureExternal` high to unlimit, turn `On 1`, and poll the chamber's Gas Sensor `Pressure` until it reaches 0.

To pressurize a chamber from the pipe network: set the chamber's vent to `Mode 0` (Outward), set `PressureExternal` to the target pressure (or high to dump all pipe gas in), turn `On 1`.

A two-vent "pump A's gas into B" move is: vent in room A = Inward (A empties to pipe), vent in room B = Outward (pipe fills B). The room set to Inward is the one that ends up at vacuum; the room set to Outward is the one that ends up pressurized.

## Verification history
<!-- verified: 0.2.6228.27061 @ 2026-05-31 -->

- 2026-05-31: page created from a read of Assembly-CSharp decompile (.work/decomp/0.2.6228.27061) while reviewing an IC10 airlock script. `VentDirection` enum, `PoweredVentMultiGrid.ExchangeWithWorld`, `PoweredVentSingleGrid.ExchangeWithWorld`, and `ActiveVent.OnAtmosphericTick` all read directly. Inward -> PumpGasToPipe (room empties), Outward -> PumpGasToWorld (room fills) confirmed across all three. Additive page; no prior content contradicted.

## Open questions
<!-- verified: 0.2.6228.27061 @ 2026-05-31 -->

- None.
