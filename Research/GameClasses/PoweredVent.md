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
  - rocketstation_Data/Managed/Assembly-CSharp.dll :: Assets.Scripts.Objects.Pipes.PoweredVent (CanLogicRead, CanLogicWrite, GetLogicValue, SetLogicValue, ExternalPressure, InternalPressure, ResetVent)
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

## PressureExternal: logic-writable on the Powered Vent (CanLogicWrite override)
<!-- verified: 0.2.6228.27061 @ 2026-05-31 -->

`LogicType.PressureExternal` maps to the vent's `ExternalPressure` field. The Large Powered Vent (`PoweredVent` / `PoweredVentMultiGrid` / `PoweredVentSingleGrid`) **overrides `CanLogicWrite` / `CanLogicRead` to expose `PressureExternal` directly**. This is easy to miss and a common review error: the base `Logicable.CanLogicWrite` writable set is only `{Color, Activate, Open, Mode, Lock, On}` and does NOT include `PressureExternal`, so a chain-walk that stops at the base wrongly concludes the write is rejected. The `PoweredVent` override adds it (verbatim, decompile lines 364303-364349):

```csharp
public override bool CanLogicRead(LogicType logicType)
{
    switch (logicType)
    {
    case LogicType.Setting:
    case LogicType.Maximum:
    case LogicType.Ratio:
        return false;
    case LogicType.PressureExternal:
        return true;        // <-- readable
    default:
        return base.CanLogicRead(logicType);
    }
}

public override bool CanLogicWrite(LogicType logicType)
{
    switch (logicType)
    {
    case LogicType.Setting:
    case LogicType.Maximum:
    case LogicType.Ratio:
        return false;
    case LogicType.PressureExternal:
        return true;        // <-- writable
    default:
        return base.CanLogicWrite(logicType);
    }
}

public override double GetLogicValue(LogicType logicType)
{
    if (logicType == LogicType.PressureExternal)
        return ExternalPressure.ToDouble();
    return base.GetLogicValue(logicType);
}

public override void SetLogicValue(LogicType logicType, double value)
{
    base.SetLogicValue(logicType, value);
    if (logicType == LogicType.PressureExternal)
        ExternalPressure = new PressurekPa(value);
}
```

So `sb`/`sbn LARGE_POWERED_VENT ... PressureExternal <value>` **does take effect** on a Large Powered Vent; it is NOT silently rejected. The `ExternalPressure` setter clamps to `[Chemistry.Pressure.Minimum, Chemistry.Pressure.Maximum] = [0, 1000000]` kPa, so a write of `100000` is in range (not clamped). The field default is `_externalPressure = Chemistry.OneAtmosphere` (~101.325 kPa).

`ExternalPressure` is the pressure target the vent pumps toward, passed straight into the pump calls as the limit: `pressureToMove = Min(pressureToMove, ExternalPressure - worldPressure)` for Outward (`PumpGasToWorld`) and `Min(pressureToMove, worldPressure - ExternalPressure)` for Inward (`PumpGasToPipe`). A vent stops doing useful work once the relevant side reaches `ExternalPressure`.

Setting `PressureExternal` to a very large number (IC10 scripts commonly write `100000`, i.e. 100000 kPa) effectively removes the cap so the vent keeps pumping until the source side is exhausted (vacuum on the Inward source, or the pipe is drained on the Outward source). This is the "unlimit the vent" idiom. There is no separate enable flag for unlimited mode; a large `PressureExternal` is the mechanism.

### Mode changes reset PressureExternal (ResetVent): set PressureExternal AFTER Mode
<!-- verified: 0.2.6228.27061 @ 2026-05-31 -->

Changing the vent's `Mode` calls `ResetVent()` synchronously (the `Mode` logic write routes through the interaction system to `OnInteractableUpdated`, which calls `ResetVent` when `interactable.Action == Mode`). `ResetVent` **overwrites `ExternalPressure`** based on the new direction (decompile lines 364237-364252):

```csharp
public void ResetVent()
{
    if (GameManager.RunSimulation)
    {
        if (Mode == 0)   // Outward
        {
            ExternalPressure = Chemistry.OneAtmosphere;   // ~101.325 kPa
            InternalPressure = PressurekPa.Zero;
        }
        else             // Inward
        {
            ExternalPressure = PressurekPa.Zero;          // 0 -> evacuates to vacuum
            InternalPressure = DefaultMaxPressure;        // 50662.5 kPa
        }
    }
}
```

Note: on `PoweredVent`, `InternalPressure`'s setter is empty (`get => Chemistry.Pressure.Maximum; set { }`, lines 364188-364197), so the `InternalPressure = ...` assignments in `ResetVent` are no-ops; only `ExternalPressure` actually changes.

Consequences for IC10:

- A script that wants a custom `PressureExternal` MUST write it AFTER the last `Mode` write to that vent, or the `Mode` write's `ResetVent` clobbers it back to the per-direction default. This is the real reason behind the common "set `Mode`, then re-set `PressureExternal`" idiom (often mislabeled a "yield bug"). No `yield` is required between the two writes: `ResetVent` fires synchronously during the `Mode` write, before any tick boundary, so `PressureExternal` written on the next line in the same tick survives.
- An Inward vent left at its post-`ResetVent` default (`ExternalPressure = 0`) evacuates its room all the way to vacuum with no explicit `PressureExternal` write. That is why pump-to-vacuum routines only need to raise `PressureExternal` on the Outward (fill) vent.

## IC10 usage notes
<!-- verified: 0.2.6228.27061 @ 2026-05-31 -->

To pump a chamber down to vacuum and store its gas in the pipe network: set the chamber's vent to `Mode 1` (Inward), set its `PressureExternal` high to unlimit, turn `On 1`, and poll the chamber's Gas Sensor `Pressure` until it reaches 0.

To pressurize a chamber from the pipe network: set the chamber's vent to `Mode 0` (Outward), set `PressureExternal` to the target pressure (or high to dump all pipe gas in), turn `On 1`.

A two-vent "pump A's gas into B" move is: vent in room A = Inward (A empties to pipe), vent in room B = Outward (pipe fills B). The room set to Inward is the one that ends up at vacuum; the room set to Outward is the one that ends up pressurized.

## Verification history
<!-- verified: 0.2.6228.27061 @ 2026-05-31 -->

- 2026-05-31: page created from a read of Assembly-CSharp decompile (.work/decomp/0.2.6228.27061) while reviewing an IC10 airlock script. `VentDirection` enum, `PoweredVentMultiGrid.ExchangeWithWorld`, `PoweredVentSingleGrid.ExchangeWithWorld`, and `ActiveVent.OnAtmosphericTick` all read directly. Inward -> PumpGasToPipe (room empties), Outward -> PumpGasToWorld (room fills) confirmed across all three. Additive page; no prior content contradicted.
- 2026-05-31: added "PressureExternal: logic-writable on the Powered Vent (CanLogicWrite override)" and "Mode changes reset PressureExternal (ResetVent)" sections from a direct read of `PoweredVent.CanLogicRead`/`CanLogicWrite`/`GetLogicValue`/`SetLogicValue`/`ExternalPressure`/`InternalPressure`/`ResetVent` (decompile lines 364171-364349). Records that `PoweredVent` overrides `CanLogicWrite` to return `true` for `PressureExternal` (so the IC10 write is NOT silently rejected on a Large Powered Vent, despite the base `Logicable` writable set excluding it), and that a `Mode` change synchronously resets `ExternalPressure` via `ResetVent` (Outward->1 atm, Inward->0), so `PressureExternal` must be written after the last `Mode` write. Resolved a reviewer conflict over whether the "unlimit the vent" write takes effect on the Large Powered Vent: it does. Additive; no prior claim on this page contradicted.

## Open questions
<!-- verified: 0.2.6228.27061 @ 2026-05-31 -->

- None.
