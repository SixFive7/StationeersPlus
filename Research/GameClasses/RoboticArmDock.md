---
title: RoboticArmDock and dock subclasses (Larre dock IC10 / slot surface)
type: GameClasses
created_in: 0.2.6228.27061
verified_in: 0.2.6228.27061
verified_at: 2026-04-25
sources:
  - E:\Steam\steamapps\common\Stationeers\rocketstation_Data\Managed\Assembly-CSharp.dll :: Objects.RoboticArm.RoboticArmDock
  - E:\Steam\steamapps\common\Stationeers\rocketstation_Data\Managed\Assembly-CSharp.dll :: Objects.RoboticArm.RoboticArmDockCargo
  - E:\Steam\steamapps\common\Stationeers\rocketstation_Data\Managed\Assembly-CSharp.dll :: Objects.RoboticArm.RoboticArm
  - E:\Steam\steamapps\common\Stationeers\rocketstation_Data\Managed\Assembly-CSharp.dll :: Assets.Scripts.Objects.Motherboards.LogicSlotType
related:
  - RoboticArmRail.md
tags: [logic, ic10, slots, prefab]
---

## Overview
<!-- verified: 0.2.6228.27061 @ 2026-04-25 -->

`RoboticArmDock` is the rail-graph dock that holds a `RoboticArm` and slides it along the rail to a target junction. Subclasses (`RoboticArmDockAtmos`, `RoboticArmDockCargo`, `RoboticArmDockCollector`, `RoboticArmDockHydroponics`) extend the dock with a slot/proxy surface specific to their domain. The class hierarchy and rail-graph plumbing are documented in [RoboticArmRail.md](RoboticArmRail.md); this page covers the IC10 logic surface and slot model needed to read dock state from a programmable chip.

## Arm hand slot model
<!-- verified: 0.2.6228.27061 @ 2026-04-25 -->

Every `RoboticArmDock` exposes the arm's hand as `Slots[0]`. The dock declares it as a private alias:

```csharp
// RoboticArmDock.cs
private Slot ArmSlot => Slots[0];
```

`RoboticArmDockCargo` re-aliases the same slot under a different name, so both names refer to the same `Slots[0]` reference:

```csharp
// RoboticArmDockCargo.cs
private Slot HandSlot => Slots[0];
```

There is exactly one slot owned by the dock itself (slot index 0). All "is the arm holding anything" checks read from that slot.

`RoboticArmDockCargo` additionally implements `IProxySlot` and reserves index 255 as the proxy slot id:

```csharp
// RoboticArmDockCargo.cs
public const int PROXY_SLOT_ID = 255;
```

`GetSlot(255)` returns the slot of the device the arm is currently aimed at, indexed by `CurrentSlotIndex`:

```csharp
public override Slot GetSlot(int slotIndex)
{
    Slot slot;
    if (slotIndex == 255)
    {
        ILogicable targetLogicable = TargetLogicable;
        if (targetLogicable != null && targetLogicable.HasAnySlots)
        {
            slot = TargetLogicable.GetSlot(CurrentSlotIndex);
            if (!CanAccessSlot(slot))
                return null;
            return slot;
        }
    }
    slot = base.GetSlot(slotIndex);
    if (!CanAccessSlot(slot))
        return null;
    return slot;
}
```

So slot index 0 is the arm's own hand, and slot index 255 is the proxied slot on the target device under the arm. IC10 `ls`/`lbns` reads against slot 0 read the hand; reads against slot 255 read whatever device-slot the cargo dock is currently pointing at.

## CanLogicRead / CanLogicWrite (RoboticArmDock)
<!-- verified: 0.2.6228.27061 @ 2026-04-25 -->

Direct quote from `RoboticArmDock.CanLogicRead` and `CanLogicWrite`:

```csharp
public override bool CanLogicRead(LogicType logicType)
{
    return logicType switch
    {
        LogicType.Setting => true,
        LogicType.Idle => true,
        LogicType.Extended => true,
        LogicType.PositionX => true,
        _ => base.CanLogicRead(logicType),
    };
}

public override bool CanLogicWrite(LogicType logicType)
{
    if (logicType == LogicType.Setting)
        return true;
    return base.CanLogicWrite(logicType);
}
```

Read/write semantics from `GetLogicValue` / `SetLogicValue`:

| LogicType | Read | Write | Meaning |
|---|---|---|---|
| `Setting` | yes | yes | `TargetJunctionIndex` (which junction the arm is heading to / parked at) |
| `PositionX` | yes | no | `CurrentJunctionIndex` (which junction the arm is currently sitting at, or -1 while moving / between junctions) |
| `Idle` | yes | no | 1 if `IsIdle()` (not moving, not bypassing, `ArmState.Up`), else 0 |
| `Extended` | yes | no | 1 if `ArmState == ArmState.Down`, else 0 |
| `Activate` | yes (inherited) | yes | Triggers the down/up extend cycle when set to 1 (gated by `IsOperable && !IsMoving`) |
| `Open` | yes (inherited) | yes | Calls `TrySetOpenState(value)` to dock/undock at the current bypass |
| `On` / `Power` / `Error` etc. | inherited from `Device` | varies | standard device logic |

`IsIdle()` definition:

```csharp
public bool IsIdle()
{
    if (!IsMoving && !DoingBypassMove)
        return ArmState == ArmState.Up;
    return false;
}
```

`Activate` write gate (verbatim from `RoboticArmDock.SetLogicValue`):

```csharp
case LogicType.Activate:
    if (IsOperable && !IsMoving)
    {
        OnServer.Interact(base.InteractActivate, (int)value);
    }
    break;
```

`Open` write goes through `TrySetOpenState`, which moves the arm onto / off the bypass spur. Writing `Open = 1` to a dock that is currently on the main rail (and whose underlying rail node is an `IRoboticArmBypass`) makes the arm step onto the bypass so other arms can pass; writing `Open = 0` brings it back. Verbatim:

```csharp
public bool TrySetOpenState(int openState)
{
    if (!GameManager.RunSimulation) { return true; }
    if (openState == 1 == IsOpen) { return false; }
    if (!ArmIsStationary(out var railNodeIndex)) { return false; }
    if (base.RoboticArmNetwork == null || base.RoboticArmNetwork.RailNodeList.Count == 0 || railNodeIndex >= base.RoboticArmNetwork.RailNodeList.Count) { return false; }
    IRoboticArmRail obj = base.RoboticArmNetwork?.RailNodeList[railNodeIndex].Rail;
    bool flag = obj is RoboticArmDock roboticArmDock && !roboticArmDock.BypassPointValid;
    IRoboticArmBypass roboticArmBypass = obj as IRoboticArmBypass;
    if (roboticArmBypass == null || flag) { return false; }
    if (IsOpen && roboticArmBypass.CanClose && CanBypass(roboticArmBypass))
    {
        HandleBypassAction(roboticArmBypass, 0);
        return true;
    }
    if (!IsOpen && roboticArmBypass.CanOpen)
    {
        HandleBypassAction(null, 1);
        return true;
    }
    return false;
}
```

## CanLogicRead / CanLogicWrite (RoboticArmDockCargo additions)
<!-- verified: 0.2.6228.27061 @ 2026-04-25 -->

Cargo-dock-specific overrides on top of the base `RoboticArmDock` set:

```csharp
public override bool CanLogicRead(LogicType logicType)
{
    return logicType switch
    {
        LogicType.TargetSlotIndex => true,
        LogicType.TargetPrefabHash => true,
        _ => base.CanLogicRead(logicType),
    };
}

public override bool CanLogicWrite(LogicType logicType)
{
    if (logicType == LogicType.TargetSlotIndex)
        return true;
    return base.CanLogicWrite(logicType);
}

public override double GetLogicValue(LogicType logicType)
{
    return logicType switch
    {
        LogicType.TargetSlotIndex => CurrentSlotIndex,
        LogicType.TargetPrefabHash => ((double?)TargetLogicable?.GetPrefabHash()) ?? 0.0,
        _ => base.GetLogicValue(logicType),
    };
}
```

Cargo-dock additions:

| LogicType | Read | Write | Meaning |
|---|---|---|---|
| `TargetSlotIndex` | yes | yes (clamped 0-50) | Which slot index on the device under the arm the cargo dock is targeting |
| `TargetPrefabHash` | yes | no | Prefab hash of the device the arm is currently pointing at (`TargetLogicable`); 0 if no target |

`MAX_SLOT_INDEX` is `50` (`SetLogicValue` clamps writes to `[0, 50]`). `CurrentSlotIndex` is the local "which slot of the target device" knob and persists across saves.

## "Is the arm holding anything?" (the IC10 read)
<!-- verified: 0.2.6228.27061 @ 2026-04-25 -->

The arm's hand is `Slots[0]` on the dock. To read whether it is occupied, IC10 reads slot 0 of the dock with the `Occupied` slot logic type:

```
ls r0 d0 0 Occupied   # r0 = 1 if hand has something, 0 if empty
```

Other useful slot-0 reads, from `LogicSlotType`:

| LogicSlotType | Meaning when read against `Slots[0]` |
|---|---|
| `Occupied` | 1 if hand contains anything, 0 if empty |
| `OccupantHash` | prefab hash of the held thing (0 if empty) |
| `Quantity` | stack count of the held thing (0 if empty) |
| `MaxQuantity` | stack cap of the held thing |
| `PrefabHash` | same as `OccupantHash` for occupied slots |
| `ReferenceId` | thing-network reference id of the held thing |

`LogicSlotType` is a flat byte enum (verified verbatim against the decompile, all 33 entries):

```
None, Occupied, OccupantHash, Quantity, Damage, Efficiency, Health, Growth,
Pressure, Temperature, Charge, ChargeRatio, Class, PressureWaste, PressureAir,
MaxQuantity, Mature, PrefabHash, Seeding, LineNumber, Volume, Open, On, Lock,
SortingClass, FilterType, ReferenceId, HarvestedHash, Mode, MaturityRatio,
SeedingRatio, FreeSlots, TotalSlots
```

Every `RoboticArmDock` subclass has a single slot at index 0 (the arm's hand). Reading any other slot index against the bare dock is invalid except on `RoboticArmDockCargo`, which adds the proxy index 255 (the slot on the device under the arm). For the proxy:

```
ls r0 d0 255 Occupied      # 1 if the targeted device-slot is occupied
ls r0 d0 255 OccupantHash  # prefab hash of what's in the targeted device-slot
```

`GetSlot(255)` returns null when `TargetLogicable` is missing, has no slots, or `CanAccessSlot` rejects the slot (plant-class slots, locked slots, hidden-occupant slots). When `GetSlot` returns null, IC10 slot reads will return 0 / NaN per the standard slot-read failure path.

## Cargo dock action cycle and slot-access filter
<!-- verified: 0.2.6228.27061 @ 2026-04-25 -->

The cargo dock has no internal cycle of its own; the descend/ascend motion comes from the parent `RoboticArmDock`'s state machine. The cargo subclass only contributes the slot-transfer that fires when descent finishes:

```csharp
// RoboticArmDockCargo.cs
protected override void AnimateDownFinished()
{
    if (GameManager.RunSimulation)
    {
        DoContextualAction();
    }
}

private void DoContextualAction()
{
    SmallCell armInteractionCell = GetArmInteractionCell();
    if (armInteractionCell?.Device == null || armInteractionCell.Device.Slots.Count <= CurrentSlotIndex)
    {
        WaitThenSetActivate().Forget();
        return;
    }
    Slot slot = armInteractionCell.Device.Slots[CurrentSlotIndex];
    if (!CanAccessSlot(slot))
    {
        WaitThenSetActivate().Forget();
        return;
    }
    if (HandSlot.Contains<DynamicThing>(out var occupant))
    {
        DoHandOccupied(slot, occupant);
    }
    else
    {
        DoHandEmpty(slot);
    }
    WaitThenSetActivate().Forget();
}

private void DoHandOccupied(Slot slot, DynamicThing inHand)
{
    if (slot.IsAllowedType(inHand))
    {
        if (slot.IsEmpty()) { OnServer.MoveToSlot(inHand, slot); }
        else if (slot.IsSwappable) { OnServer.SwapSlots(slot.Parent.ReferenceId, base.ReferenceId, slot.SlotIndex, HandSlot.SlotIndex); }
    }
}

private void DoHandEmpty(Slot slot)
{
    if (!slot.IsEmpty()) { OnServer.MoveToSlot(slot.Get(), HandSlot); }
}
```

Hand-occupied + target-empty places into the target. Hand-empty + target-occupied grabs from the target. Hand-occupied + target-occupied swaps if the target slot allows the held type and is swappable. Anything else does nothing.

`CanAccessSlot` is the proxy / cycle filter:

```csharp
private bool CanAccessSlot(Slot slot)
{
    if (slot == null) { return false; }
    if (slot.Type == Slot.Class.Plant) { return false; }
    if (slot.IsInteractable && !slot.IsLocked)
    {
        return !slot.HidesOccupant;
    }
    return false;
}
```

Plant-class slots, locked slots, non-interactable slots, and slots whose occupant is hidden are all rejected. The same filter gates `GetSlot(255)` reads — IC10 reads against slot 255 return null when the proxy slot fails this check.

`SetTargetSmallGrid` keeps `TargetLogicable` (the source of `TargetPrefabHash` and the proxy slot) in sync with the cell under the arm head, but clears it while the arm is sitting on a bypass spur:

```csharp
protected override void SetTargetSmallGrid()
{
    if (base.CurrentBypass != null)
    {
        TargetLogicable = null;
    }
    else
    {
        TargetLogicable = GetArmInteractionCell()?.Device;
    }
}
```

Manual dial controls — front-panel buttons step `CurrentSlotIndex` by one. `Button4` is decrement, `Button5` is increment:

```csharp
public override DelayedActionInstance InteractWith(Interactable interactable, Interaction interaction, bool doAction = true)
{
    return interactable.Action switch
    {
        InteractableType.Button4 => HandleDecrementIndex(interactable, doAction),
        InteractableType.Button5 => HandleIncrementIndex(interactable, doAction),
        _ => base.InteractWith(interactable, interaction, doAction),
    };
}

private DelayedActionInstance ChangeSlotIndex(Interactable interactable, bool doAction, int increment)
{
    ...
    if (!OnOff)        { return delayedActionInstance.Fail(GameStrings.DeviceNotOn); }
    if (!Powered)      { return delayedActionInstance.Fail(GameStrings.DeviceNoPower); }
    if (Activate == 1) { return delayedActionInstance.Fail(GameStrings.RoboticArmBusy); }
    if (doAction && GameManager.RunSimulation)
    {
        CurrentSlotIndex = Mathf.Clamp(CurrentSlotIndex + increment, 0, 50);
    }
    ...
}
```

`GetNextSlotId` defines the slot-cycle order used by IC10 enumerators (`d0:0` -> `d0:255` wrap):

```csharp
public override int GetNextSlotId(int slotIndex, bool isForward)
{
    if (Slots == null) { return -1; }
    if (Slots.Count == 0) { return 0; }
    int num = slotIndex;
    if (num == 255)
    {
        if (!isForward) { return Slots.Count - 1; }
        return 0;
    }
    num += (isForward ? 1 : (-1));
    if (num < 0 || num >= Slots.Count)
    {
        num = 255;
    }
    return num;
}
```

Slot 255 is the wrap-around terminator, not a member of `Slots`. Reading or writing slots 1..254 against the cargo dock is invalid (the dock only owns `Slots[0]`).

## Practical IC10 patterns
<!-- verified: 0.2.6228.27061 @ 2026-04-25 -->

Branch on hand-empty:

```
alias dock d0
ls r0 dock 0 Occupied
beqz r0 handEmpty
# ... hand has something
j done
handEmpty:
# ... hand is empty
done:
```

Compare the held item to a known prefab:

```
ls r0 d0 0 OccupantHash
breq r0 -1252983604 handHasIron   # example: ItemIronIngot
```

Wait until the arm is back up and idle before reading the hand:

```
loop:
l r0 d0 Idle
beqz r0 loop                      # spin until Idle == 1
ls r1 d0 0 Occupied               # now safe to read hand
```

Drive the cargo dock's targeting knob and read what it is now pointing at:

```
s d0 Setting 3                    # go to junction 3
s d0 TargetSlotIndex 5            # point at slot 5 of the device under the arm
l r0 d0 TargetPrefabHash          # what device is under us (0 if nothing)
```

## Verification history

- 2026-04-25: page creation. Class hierarchy cross-referenced against `RoboticArmRail.md`. `CanLogicRead` / `CanLogicWrite` / `GetLogicValue` / `SetLogicValue` quoted verbatim from `RoboticArmDock.cs` and `RoboticArmDockCargo.cs`. `Slots[0]` aliasing (`ArmSlot` on dock, `HandSlot` on cargo dock) and the `IProxySlot` / `PROXY_SLOT_ID = 255` model verified by direct read. `LogicSlotType` enum (all 33 entries) quoted verbatim from `Assets.Scripts.Objects.Motherboards.LogicSlotType`. `MAX_SLOT_INDEX = 50` from `RoboticArmDockCargo`.
- 2026-04-25: added cargo-dock action cycle section (`AnimateDownFinished`, `DoContextualAction`, `DoHandOccupied`, `DoHandEmpty`), `CanAccessSlot` filter rules, `SetTargetSmallGrid` clear-on-bypass behavior, `Button4` / `Button5` slot-index controls, and `GetNextSlotId` cycle order. Quoted verbatim Activate write gating (`IsOperable && !IsMoving`) and the full `TrySetOpenState` body. All sourced from `Assembly-CSharp.dll` :: `Objects.RoboticArm.RoboticArmDockCargo` and `Objects.RoboticArm.RoboticArmDock`. Additive content; no prior claims contradicted.

## Open questions

- The other dock subclasses (`RoboticArmDockAtmos`, `RoboticArmDockCollector`, `RoboticArmDockHydroponics`) likely add their own LogicType overrides (atmos pressure / temperature, hydroponics growth, collector volume) but were not opened in this pass. A follow-up read of those four files is needed to round out the per-subclass LogicType matrix.
- The IC10 behavior of `ls` against slot 255 on a non-cargo dock is presumed to return 0 via the standard "slot doesn't exist" path, but was not directly verified by chip-side test.
