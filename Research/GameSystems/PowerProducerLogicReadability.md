---
title: Power producer logic-read surfaces
type: GameSystems
created_in: 0.2.6228.27061
verified_in: 0.2.6228.27061
verified_at: 2026-06-14
sources:
  - .work/decomp/0.2.6228.27061/Assembly-CSharp.decompiled.cs :: WindTurbineGenerator 138706 (CanLogicRead 139030, GetLogicValue 139039), LargeWindTurbineGenerator 138218, TurbineGenerator 403819 (CanLogicRead 404002, GetLogicValue 404011), RadioscopicThermalGenerator 395566, GasFuelGenerator 375700-375703, PowerGeneratorPipe 375414 (CanLogicRead 375530), SolarPanel 399762 (CanLogicRead 400152), PowerGeneratorSlot 400441, SolidFuelGenerator 400538 (CanLogicRead 400652), StirlingEngine 402334 (CanLogicRead 402831), PowerConnector 386798
related:
  - ./LogicType.md
  - ../Patterns/LogicTypeRegistration.md
tags: [power, logic]
---

# Power producer logic-read surfaces

Which power-producer classes are logic-readable, i.e. declare a device-specific `CanLogicRead` / `GetLogicValue` override (a data surface an IC10 / Logic Reader can address), versus those that only inherit the universal base logic types. Relevant to any mod that adds a read-only `LogicType` to producers (PowerGridPlus's `CurrentMismatchFault` is the worked example): a Harmony postfix attaches to the method the runtime type actually dispatches to, so a class with no override resolves (via `AccessTools`) to a shared base method that fires for every device and must be instance-filtered, while a class with its own override is patched in isolation.

## Per-class override status
<!-- verified: 0.2.6228.27061 @ 2026-06-14 -->

| Producer class | Base | Own CanLogicRead / GetLogicValue override | Device-specific vanilla read |
|---|---|---|---|
| SolarPanel | Electrical | YES (CanLogicRead @ 400152) | tracking / solar logic (device-specific) |
| WindTurbineGenerator | Device | YES (@ 139030) | `PowerGeneration` (= `_generatedPower`) |
| LargeWindTurbineGenerator | WindTurbineGenerator | inherits Wind's | `PowerGeneration` |
| TurbineGenerator (small wall turbine) | Device | YES (@ 404002) | `PowerGeneration` (= `_generatedPower`) |
| PowerGeneratorPipe | DeviceInputOutput | YES (@ 375530) | generator logic |
| GasFuelGenerator | PowerGeneratorPipe | NO (trivial subclass) -> inherits Pipe's | generator logic (via Pipe) |
| SolidFuelGenerator | PowerGeneratorSlot | YES (@ 400652) | generator logic |
| StirlingEngine | DeviceInputOutput | YES (@ 402831) | generator logic |
| RadioscopicThermalGenerator (RTG) | Electrical, IRocketInternals | NO | none device-specific (universal base types only) |
| PowerConnector | Electrical | NO | none device-specific |

The wind / small-turbine surfaces are identical in shape (verbatim, `WindTurbineGenerator` decompile 139030-139046; `TurbineGenerator` 404002-404018 is the same with `PowerGeneration`):

```csharp
public override bool CanLogicRead(LogicType logicType)
{
    if (logicType == LogicType.PowerGeneration) return true;
    return base.CanLogicRead(logicType);
}
public override double GetLogicValue(LogicType logicType)
{
    if (logicType == LogicType.PowerGeneration) return _generatedPower;
    return base.GetLogicValue(logicType);
}
```

`GasFuelGenerator` is a one-line subclass (decompile 375700-375703) and does NOT override the logic methods:

```csharp
public class GasFuelGenerator : PowerGeneratorPipe
{
    public override WreckageSize WreckageSize => WreckageSize.Medium;
}
```

so a Harmony patch on `PowerGeneratorPipe.CanLogicRead` / `GetLogicValue` correctly fires for GasFuelGenerator (it inherits them); there is no inherited-method-trap break for gas in this version.

## Readability implications
<!-- verified: 0.2.6228.27061 @ 2026-06-14 -->

- Every producer that overrides with a device-specific readable type (Solar, both wind turbines, the small turbine, the three fuel generators) is logic-read-designed: it has a data surface an IC10 can address. Crucially, the producers with NO on/off button (Solar, Wind, small turbine) are still logic-readable, having no flash button does NOT imply having no logic surface; they expose `PowerGeneration` (or tracking) and fall through to base for everything else.
- `RadioscopicThermalGenerator` (RTG) does NOT override the logic methods and is a ROCKET-INTERNAL component (`IRocketInternals`; `InternalCellType => RocketInternalCellType.Devices`; `PowerGenerated = 50000f`). It is not a standalone base-building device with a normal data port, so it is effectively not IC10-readable for a device-specific value the way the others are.
- `PowerConnector` (dynamic-generator dock) likewise declares no device-specific logic override.
- Every overriding producer ends its `CanLogicRead` / `GetLogicValue` with `base.CanLogicRead/GetLogicValue(logicType)`, so an additive postfix that exposes a NEW readable LogicType composes correctly whether it lands on the class's own override or on the inherited base.

## Verification history

- 2026-06-14: page created from `.work/decomp/0.2.6228.27061/Assembly-CSharp.decompiled.cs`. Verbatim `WindTurbineGenerator.CanLogicRead/GetLogicValue` (139030/139039), `TurbineGenerator.CanLogicRead/GetLogicValue` (404002/404011), and the `GasFuelGenerator` body (375700-375703). Confirmed `RadioscopicThermalGenerator` (395566) and `PowerConnector` (386798) declare no `CanLogicRead` override (the nearest overrides at 396371 and 386694/393527 belong to neighbouring slot/other classes). Override presence for SolarPanel/PowerGeneratorPipe/SolidFuelGenerator/StirlingEngine confirmed by line position within each class range; bodies not transcribed. Established while widening PowerGridPlus's `VariableVoltageFault` LogicType exposure (deviation P10).

## Open questions

- The exact device-specific LogicType set for SolarPanel / PowerGeneratorPipe / SolidFuelGenerator / StirlingEngine is not transcribed here (only the override presence at the cited lines). Read those bodies if a precise per-class readable list is needed.
- Whether RTG can be addressed by logic through a rocket's internal data bus (as opposed to base-building data cable) is unconfirmed; treated here as not normally IC10-readable.
