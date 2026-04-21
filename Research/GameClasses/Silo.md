---
title: Silo
type: GameClasses
created_in: 0.2.6228.27061
verified_in: 0.2.6228.27061
verified_at: 2026-04-20
sources:
  - rocketstation_Data/Managed/Assembly-CSharp.dll :: Assets.Scripts.Objects.Chutes.Silo
related:
  - ./CombustionDeepMiner.md
tags: [logic, ic10]
---

# Silo

Vanilla game class at `Assets.Scripts.Objects.Chutes.Silo`. Bulk item storage that sits on a chute network and accepts stacked items (ore, ingots, etc.). Minimal logic surface: exposes its current item count and nothing else.

## Class hierarchy
<!-- verified: 0.2.6228.27061 @ 2026-04-20 -->

```csharp
// Assembly-CSharp.dll :: Assets.Scripts.Objects.Chutes.Silo
public class Silo : DeviceImportExport { ... }
```

Chain: `Silo` -> `DeviceImportExport` -> `DeviceImport` -> `Device`.

## Logic variables
<!-- verified: 0.2.6228.27061 @ 2026-04-20 -->

```csharp
// Silo.CanLogicRead
public override bool CanLogicRead(LogicType logicType)
{
    if (logicType == LogicType.Quantity)
    {
        return true;
    }
    return base.CanLogicRead(logicType);
}

// Silo.GetLogicValue
public override double GetLogicValue(LogicType logicType)
{
    if (logicType == LogicType.Quantity)
    {
        return SiloThingQuantity;
    }
    return base.GetLogicValue(logicType);
}
```

| LogicType | Read | Write | Value |
|---|---|---|---|
| `Quantity` | yes | no | current stored item count, 0..600 |
| `Ratio` | no | no | NOT exposed, do not use |
| `Maximum` | no | no | NOT exposed, capacity is implicit |
| `Setting` | no | no | NOT exposed |
| (inherited) | | | `On`, `Power`, `Error` from base |

`LogicType.Ratio` is a common fill-indicator on other Stationeers devices (sorters, furnaces, tanks), but the `Silo` class deliberately does not implement it. Any IC10 script using `lb <silo> Ratio Average` will fail to read. Scripts needing a fill percentage must compute it themselves from `Quantity` and the fixed capacity.

## Capacity
<!-- verified: 0.2.6228.27061 @ 2026-04-20 -->

`MaxItems = 600` is hardcoded in the class. A full silo reports `Quantity = 600`; an empty silo reports `Quantity = 0`. There is no logic-readable way to query the max; it must be encoded as a constant in consuming scripts (or derived from stationpedia at design time).

## Typical IC10 pattern
<!-- verified: 0.2.6228.27061 @ 2026-04-20 -->

Absolute-threshold gating is simpler than computing a ratio:

```
define Silo HASH("StructureSilo")
define SILO_FULL 600                 # MaxItems from the decompile
define SILO_EMPTY 0

lb r1 Silo Quantity Average          # read current item count
sge r2 r1 SILO_FULL                  # 1 if silo is at capacity
sle r3 r1 SILO_EMPTY                 # 1 if silo is drained
```

If a ratio is genuinely needed (for display, etc.), `div r1 r1 600` after the `lb` produces a float 0.0..1.0, with no division-by-zero concern because 600 is a constant.

## Verification history

- 2026-04-20: page created. Decompile pass against `Assets.Scripts.Objects.Chutes.Silo`; only readable LogicType is `Quantity`, capacity is hardcoded at `MaxItems = 600`.

## Open questions

- Whether `StructureSilo` is the correct prefab name for `HASH()`. The decompile shows the class name is `Silo`; Stationeers convention prefixes `Structure` on the prefab name for built structures, but this was not verified against a `PrefabName` string in the decompile. Confirmable via configuration tablet against a placed silo.
- Community references to "SDB silo" (Silo Dense Bulk) may denote a modded variant rather than vanilla `Silo`. A modded silo would have its own prefab hash and its own LogicType surface; the vanilla analysis above may not apply.
