---
title: PowerTransmitterOmni
type: GameClasses
created_in: 0.2.6403.27689
verified_in: 0.2.6403.27689
verified_at: 2026-07-02
sources:
  - .work/decomp/0.2.6403.27689/Assembly-CSharp.decompiled.cs :: lines 408582-408702 (PowerTransmitterOmni), 394786-394812 (Electrical), 356760-356800 (WirelessBattery)
related:
  - ./PowerTransmitter.md
  - ./WirelessPower.md
tags: [power]
---

# PowerTransmitterOmni

Vanilla omnidirectional wireless charger at `Assets.Scripts.Objects.Electrical.PowerTransmitterOmni` (decompile lines 408582-408702). Trickle-charges nearby battery cells (`WirelessBattery`) over the air within a radius. Despite the name it shares nothing with the dish pair: it is NOT a `WirelessPower` subclass, has no servo, no link raycast, no `WirelessNetwork`, and no `_powerProvided` ledger.

## Class hierarchy: Electrical, not WirelessPower
<!-- verified: 0.2.6403.27689 @ 2026-07-02 -->

```csharp
public class PowerTransmitterOmni : Electrical    // line 408582
```

`Electrical : Device, ISmartRotatable, IPowered, IDensePoolable, IReferencable, IEvaluable` (lines 394786-394812) carries ONLY SmartRotate members (`ConnectionType`, `OpenEndsPermutation` and their four accessors); it adds no power state. `Electrical` and `ElectricalInputOutput` (394813) are sibling branches under `Device`, so the Omni is a SINGLE-network device:

- It uses the base `Device.PowerCable` / `PowerCableNetwork`; there is no `InputNetwork` / `OutputNetwork` pair.
- It is not a bridge: it never implements `GetGeneratedPower` (base `Device` returns 0 on its own network), so it appears on its cable network purely as a consumer.
- It has no `_powerProvided` debt accumulator and none of the `WirelessPower` servo machinery (`Horizontal` / `Vertical` / `RayTransform` / `DishTransform`).

## Serialized tuning fields
<!-- verified: 0.2.6403.27689 @ 2026-07-02 -->

```csharp
[Header("PowerTransmitterOmni")]
[Tooltip("The Maximum Distance that the device can charge a battery")]
[SerializeField]
private float _maxDistance = 15f;                 // line 408587

[Tooltip("How much power (in Watts) can it supply to one battery (Note: 25-75% of this will be lost depending on transmission distance).")]
[SerializeField]
private float _batteryChargeRate = 300f;          // line 408591

[Tooltip("The Maximum power the unit will draw to charge all nearby batteries.")]
[SerializeField]
private float _maximumPowerUsage = 2000f;         // line 408595
```

The values above are C# defaults; the fields are `[SerializeField]`, so prefabs can override them in asset data. `MaxDistance => _maxDistance` is the public read (408605).

## Per-tick flow: OnPowerTick computes demand, ReceivePower delivers
<!-- verified: 0.2.6403.27689 @ 2026-07-02 -->

Phase-2 `OnPowerTick` (lines 408607-408612, invoked from `ElectricityManager` after all networks have settled, see [ElectricityManager](./ElectricityManager.md)):

```csharp
public override void OnPowerTick()
{
    base.OnPowerTick();
    LocateBatteries();
    CalculateUsedPower();
}
```

`CalculateUsedPower` (lines 408674-408688) sums the demand: own quiescent `UsedPower` plus, for every linked non-charged cell, `Min(cell.PowerDelta, _batteryChargeRate)`, capped at `_maximumPowerUsage`:

```csharp
private void CalculateUsedPower()
{
    _batteriesUsingPower.Clear();
    _batteriesUsingPower.AddRange(_batteriesInRange);
    float num = 0f;
    num += UsedPower;
    foreach (WirelessBattery item in _batteriesUsingPower)
    {
        if (!item.IsCharged && item.LinkedOmni != null && item.LinkedOmni == this)
        {
            num += Mathf.Min(item.PowerDelta, _batteryChargeRate);
        }
    }
    _powerRequired = Mathf.Min(num, _maximumPowerUsage);
}
```

`GetUsedPower` (lines 408690-408701) reports that cached figure with the standard `-1f` off-network sentinel:

```csharp
public override float GetUsedPower([NotNull] CableNetwork cableNetwork)
{
    if (base.PowerCable == null || base.PowerCable.CableNetwork != cableNetwork)
    {
        return -1f;
    }
    if (!OnOff)
    {
        return 0f;
    }
    return _powerRequired;
}
```

Ordering consequence: `_powerRequired` is written in phase 2 (`OnPowerTick`) but read by the next phase-1 `CalculateState` on the cable network, so the Omni's grid draw is one tick stale by construction (it bills this tick for last tick's battery census).

Delivery happens inside `ReceivePower` (lines 408641-408672), called during the cable network's consumer settle:

```csharp
public override void ReceivePower(CableNetwork cableNetwork, float powerAdded)
{
    if (powerAdded < UsedPower)
    {
        return;
    }
    powerAdded -= UsedPower;
    _batteriesToCharge.Clear();
    _batteriesToCharge.AddRange(_batteriesInRange);
    float num = 0.1f;
    while (_batteriesToCharge.Count > 0 && powerAdded > num)
    {
        float num2 = powerAdded / (float)_batteriesToCharge.Count;
        for (int num3 = _batteriesToCharge.Count - 1; num3 >= 0; num3--)
        {
            if (_batteriesToCharge[num3].LinkedOmni == null || _batteriesToCharge[num3].LinkedOmni != this)
            {
                _batteriesToCharge.RemoveAt(num3);
            }
            else
            {
                float num4 = _batteriesToCharge[num3].AddPowerWireless(num2);
                powerAdded -= num2 - num4;
                if (_batteriesToCharge[num3] == null || _batteriesToCharge[num3].IsCharged || _batteriesToCharge[num3].CalculateUniqueRatioIdentifier() >= 0.999f)
                {
                    _batteriesToCharge.RemoveAt(num3);
                }
            }
        }
    }
    base.ReceivePower(cableNetwork, powerAdded);
}
```

Semantics: the Omni pays its own `UsedPower` first (returns without charging when the delivered slice is below it), then repeatedly splits the remaining budget EQUALLY across the still-eligible linked cells (`num2 = powerAdded / count` per pass) while the budget stays above 0.1 W. Cells that fill up, or that another Omni has claimed, drop out between passes. `AddPowerWireless` returns the overflow the cell could not absorb, which flows back into the budget (`powerAdded -= num2 - num4`).

## Distance loss lives in WirelessBattery.AddPowerWireless
<!-- verified: 0.2.6403.27689 @ 2026-07-02 -->

The per-battery real distance derate is NOT in the Omni; it is in `WirelessBattery.AddPowerWireless` (lines 356781-356785):

```csharp
public float AddPowerWireless(float availablePower)
{
    availablePower *= efficiencyOverDistanceMultiplier.Evaluate(Mathf.Clamp01(RangeToOmni / LinkedOmni.MaxDistance));
    return AddPowerSafe(availablePower);
}
```

with curve keys `(0, 0.75) -> (1, 0.25)` (line 356768): 75% efficiency at zero range falling to 25% at max range, then a clamp-add into the cell via `BatteryCell.AddPowerSafe`. `WirelessBattery : BatteryCell` (356760) carries `LinkedOmni` (356762), `RangeToOmni` (356764), and the static `AllWirelessBatteries` list (356766) the Omni scans.

## Target discovery and takeover: LocateBatteries
<!-- verified: 0.2.6403.27689 @ 2026-07-02 -->

`LocateBatteries` (lines 408614-408639) rebuilds `_batteriesInRange` each tick from `WirelessBattery.AllWirelessBatteries`:

- Distance is `Vector3.Distance(base.Position, battery.RootParent.Position)`. Beyond `_maxDistance` the Omni releases a cell it owned (`LinkedOmni = null`, `RangeToOmni = float.MaxValue`).
- Eligibility: the cell must be loose (`!ParentAsDevice`) or slotted inside an `IBatteryPowered` device (`ParentAsDevice is IBatteryPowered`). `WirelessBattery.OnEnterInventory` clears `LinkedOmni` when the cell enters a non-`IBatteryPowered` device (356793-356799).
- Takeover rule: an in-range eligible cell links to THIS Omni when it has no current Omni, its current Omni is this one, its current Omni is off / unpowered / errored, or this Omni is strictly closer than the recorded `RangeToOmni`. Net effect: each cell belongs to the nearest working Omni, re-evaluated every tick.
- `LocateBatteries` clears the in-range list and returns immediately when `OnOff` is false (already-linked cells keep their stale link until another Omni or the range check releases them).

## Verification history

- 2026-07-02: page created at game version 0.2.6403.27689 during the 0.2.6228 -> 0.2.6403 migration pass, as the landing page for the fresh-validator verdict that `PowerTransmitterOmni : Electrical` (decompile 408582) is NOT a `WirelessPower` subclass (the [PowerTransmitter](./PowerTransmitter.md) hierarchy diagram and the [WirelessPower](./WirelessPower.md) intro previously implied it was). All excerpts verbatim from `.work/decomp/0.2.6403.27689/Assembly-CSharp.decompiled.cs`: class body 408582-408702 (fields 408587/408591/408595, OnPowerTick 408607-408612, LocateBatteries 408614-408639, ReceivePower 408641-408672, CalculateUsedPower 408674-408688, GetUsedPower 408690-408701), `Electrical` 394786-394812, `WirelessBattery` 356760-356800 (curve 356768, AddPowerWireless 356781-356785).

## Open questions

- Prefab-serialized overrides of `_maxDistance` / `_batteryChargeRate` / `_maximumPowerUsage` (the C# defaults 15 / 300 / 2000 may differ on the shipped prefab). Verify via InspectorPlus or a prefab dump.
- Whether `CalculateUniqueRatioIdentifier() >= 0.999f` in the `ReceivePower` drop-out check differs in practice from `IsCharged` (Mode == 6); the redundant-looking pair suggests a rounding guard.
