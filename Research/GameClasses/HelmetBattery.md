---
title: Helmet Battery
type: GameClasses
created_in: 0.2.6228.27061
verified_in: 0.2.6228.27061
verified_at: 2026-04-28
sources:
  - rocketstation_Data/Managed/Assembly-CSharp.dll :: Assets.Scripts.Objects.Items.Helmet
  - rocketstation_Data/Managed/Assembly-CSharp.dll :: Assets.Scripts.Objects.Items.HelmetBase
  - rocketstation_Data/Managed/Assembly-CSharp.dll :: Assets.Scripts.Objects.Items.GasMask
  - rocketstation_Data/Managed/Assembly-CSharp.dll :: Assets.Scripts.Objects.Items.HarmSuitHelmet
  - rocketstation_Data/Managed/Assembly-CSharp.dll :: Assets.Scripts.Objects.Items.BatteryCell
related:
  - ../GameSystems/ScrollInputHandling.md
tags: [equipment, slots]
---

# Helmet Battery

Two helmet families expose the powering battery via different paths. A mod that needs to gate "is this helmet's light powered?" must dispatch by type.

## Standard helmets: Helmet.Battery
<!-- verified: 0.2.6228.27061 @ 2026-04-27 -->

`Assets.Scripts.Objects.Items.Helmet` is the base class for normal EVA / atmosphere helmets.

```csharp
public class Helmet : HelmetBase, IBatteryPowered, IPowered, IDensePoolable, IReferencable, IEvaluable, IWearableLight
{
    public Slot BatterySlot => Slots[0];
    public BatteryCell Battery => BatterySlot.Occupant as BatteryCell;
    // ...
}
```

`Helmet.Battery` returns the slot-0 occupant cast as `BatteryCell`, or null if the slot is empty or holds something that is not a `BatteryCell`. Vanilla state-update path uses `Battery == null || Battery.IsEmpty` as the "no power" predicate (Helmet line 129):

```csharp
if (Powered && (!OnOff || Battery == null || Battery.IsEmpty))
{
    // turn the light off
}
```

## Hardsuit helmets: GasMask.ParentBattery
<!-- verified: 0.2.6228.27061 @ 2026-04-27 -->

`Assets.Scripts.Objects.Items.GasMask` and its subclasses (e.g. `HarmSuitHelmet : GasMask`) draw power from the worn suit's battery, not from a slot on the mask itself.

```csharp
public class GasMask : AtmosphericItem, ILogicable, IReferencable, IEvaluable, IWearableLight, ...
{
    public BatteryCell ParentBattery
    {
        get
        {
            if (ParentHuman != null && ParentHuman.Suit != null)
                return ParentHuman.Suit.Battery;
            return null;
        }
    }
    // ...
}
```

The "no power" predicate is the same shape (GasMask line 297, 302):

```csharp
if (!Powered && (object)ParentBattery != null && !ParentBattery.IsEmpty) { /* power on */ }
if (Powered && ((object)ParentBattery == null || ParentBattery.IsEmpty))   { /* power off */ }
```

`HarmSuitHelmet : GasMask` inherits this API unchanged.

## BatteryCell.IsEmpty
<!-- verified: 0.2.6228.27061 @ 2026-04-27 -->

`Assets.Scripts.Objects.Items.BatteryCell` exposes the canonical "no charge" predicate:

```csharp
public bool IsEmpty => Mode == 0;
```

`Mode` here is the `BatteryCellState` enum entry corresponding to the empty state. Vanilla power consumers use `IsEmpty` rather than `PowerStored <= 0f` so that the threshold matches the state-machine view of empty (which has hysteresis).

## BatteryCellState enum and threshold ladder
<!-- verified: 0.2.6228.27061 @ 2026-04-28 -->

The `Mode` integer that drives `IsEmpty / IsCritical / IsLow / IsCharged` indexes into the `BatteryCellState` enum. Numeric values are non-sequential: alphabetical order (`Low = 3`, `VeryLow = 2`) rather than severity order, so a mod that compares `Mode` directly without going through the named enum is error-prone.

```csharp
public enum BatteryCellState
{
    Empty    = 0,
    Critical = 1,
    VeryLow  = 2,
    Low      = 3,
    Medium   = 4,
    High     = 5,
    Full     = 6,
}
```

Companion predicates on `BatteryCell` (verbatim):

```csharp
public bool IsEmpty    => Mode == 0;
public bool IsCritical => Mode <= 1;
public bool IsLow      => Mode <= 3;
public bool IsCharged  => Mode == 6;
```

`IsLow == true` covers `Empty / Critical / VeryLow / Low`. That this happens to be a meaningful "low charge" group, rather than an arbitrary numeric range, is a side effect of the alphabetical numeric ordering, not an explicit check; flipping `VeryLow` and `Low` in the enum would silently break the predicate.

`Mode` is recomputed each `OnPowerTick` by `UpdateBatteryState()` from the current charge ratio (`num = PowerStored / PowerMaximum`):

| Charge ratio | State |
|---|---|
| `num == 0` | `Empty` |
| `0 < num < 0.1` | `Critical` |
| `0.1 <= num < 0.25` | `VeryLow` |
| `0.25 <= num < 0.5` | `Low` |
| `0.5 <= num < 0.75` | `Medium` |
| `0.75 <= num < 0.999` | `High` |
| `num >= 0.999` | `Full` |

A mod that needs "is the cell drained" should prefer `IsEmpty` (or, defensively, `IsCritical` to include the near-empty animated band). Comparing `PowerStored` against a hardcoded threshold misses the hysteresis these state buckets impose and can flicker against vanilla's animation cycle.

## Polymorphic gate pattern for mods
<!-- verified: 0.2.6228.27061 @ 2026-04-27 -->

A mod that wants to ask "would this helmet be powered if I turn it on?" must dispatch by helmet type because the battery path differs:

```csharp
internal static bool HelmetHasPower(DynamicThing helmet)
{
    if (helmet is Helmet h)
    {
        var battery = h.Battery;
        return battery != null && !battery.IsEmpty;
    }
    if (helmet is GasMask gm)
    {
        var battery = gm.ParentBattery;
        return battery != null && !battery.IsEmpty;
    }
    return true; // unknown helmet type: permissive default
}
```

`Powered` (the `IPowered.Powered` boolean) is NOT a reliable substitute when the helmet is currently OFF. `Powered` is set to true only when the light is actively drawing; an OFF helmet with a charged battery reports `Powered == false`. The direct battery check answers "is there charge available," not "is the helmet currently consuming."

## Verification history

- 2026-04-27: page created. Verbatim findings from `ilspycmd` decompile of `E:/Steam/steamapps/common/Stationeers/rocketstation_Data/Managed/Assembly-CSharp.dll` at game version 0.2.6228.27061. Triggered by EquipmentPlus item A (helmet power gate before scroll-toggle of the headlamp); the mod needed to know whether to surface a "no power" message before flipping `OnOff = true`. Confirms two distinct battery-access paths (`Helmet.Battery` from own slot, `GasMask.ParentBattery` from `ParentHuman.Suit.Battery`) and that `BatteryCell.IsEmpty` is the canonical empty-charge predicate.
- 2026-04-28: added "BatteryCellState enum and threshold ladder" section. Verbatim enum values (with the alphabetical-numbering trap), the three companion predicates beyond `IsEmpty` (`IsCritical / IsLow / IsCharged`), and the seven-bucket charge-ratio threshold table from `UpdateBatteryState()`. Sources: same `Assembly-CSharp.dll` at v0.2.6228.27061, decompile of `BatteryCell.cs` and `BatteryCellState.cs`. Original section stamps still match the version.

## Open questions

None at creation.
