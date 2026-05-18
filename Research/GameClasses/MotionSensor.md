---
title: MotionSensor
type: GameClasses
created_in: 0.2.6228.27061
verified_in: 0.2.6228.27061
verified_at: 2026-05-18
sources:
  - $(StationeersPath)\rocketstation_Data\Managed\Assembly-CSharp.dll :: Assets.Scripts.Objects.MotionSensor
related:
  - ./Sensor.md
  - ./ProximitySensor.md
  - ./Grid3.md
tags: [logic, network]
---

# MotionSensor

Sensor subclass that detects `DynamicThing` entries on a single `WorldGrid` cell (the sensor's own cell). Driven by grid-watch events, not a distance scan. Despite sharing the `Sensor` / `IDoorControl` base with `ProximitySensor`, the two have effectively opposite behaviour: MotionSensor watches one grid cell exactly, ProximitySensor watches a free-space sphere with no occlusion test.

## Class shape
<!-- verified: 0.2.6228.27061 @ 2026-05-18 -->

```csharp
public class MotionSensor : Sensor, IDoorControl
{
    public List<DynamicThing> TriggeredDynamicThings = new List<DynamicThing>();

    public override bool IsTriggered => TriggeredDynamicThings.Count > 0;
    ...
}
```

`TriggeredDynamicThings` holds the currently-detected dynamic things. `IsTriggered` is a non-empty check.

## Grid watch registration
<!-- verified: 0.2.6228.27061 @ 2026-05-18 -->

```csharp
public override void OnRegistered(Cell cell)
{
    base.OnRegistered(cell);
    base.GridController.SetWatchGrid(base.WorldGrid, this);
}

public override void OnDeregistered()
{
    base.OnDeregistered();
    base.GridController.RemoveWatchGrid(base.WorldGrid, this);
}
```

The sensor subscribes to grid events on **its own `WorldGrid` cell** at registration time and unsubscribes at deregistration. No radius parameter, no settable range. Detection scope is exactly one grid cell.

## Detection: GridEvent driven
<!-- verified: 0.2.6228.27061 @ 2026-05-18 -->

```csharp
public override void OnGridEvent(GridEvent gridEvent)
{
    base.OnGridEvent(gridEvent);
    if (!GameManager.RunSimulation) return;
    switch (gridEvent.Type)
    {
    case GridEvent.GridEventType.Enter:
        if (TriggeredDynamicThings.Contains(gridEvent.DynamicThing)) return;
        TriggeredDynamicThings.Add(gridEvent.DynamicThing);
        break;
    case GridEvent.GridEventType.Leave:
        TriggeredDynamicThings.Remove(gridEvent.DynamicThing);
        break;
    }
    ActivateSensor();
}
```

Triggers strictly on `GridEvent.Enter` / `Leave` for the watched cell. No scan, no distance check. Whatever the GridController dispatches as entering or leaving this cell is what the sensor sees.

```csharp
public override void OnThreadUpdate()
{
    base.OnThreadUpdate();
    if (!GameManager.RunSimulation) return;
    if (Activate == 1)
    {
        int count = TriggeredDynamicThings.Count;
        while (count-- > 0)
        {
            DynamicThing dynamicThing = TriggeredDynamicThings[count];
            if (!dynamicThing)
                TriggeredDynamicThings.RemoveAt(count);
            else if (dynamicThing.WorldGrid != base.WorldGrid)
                TriggeredDynamicThings.RemoveAt(count);
        }
    }
    if (Activate == 1 != IsTriggered)
        OnServer.Interact(base.InteractActivate, IsTriggered ? 1 : 0);
}
```

Per-tick reconciliation when `Activate == 1`: scrubs destroyed things and anything whose `WorldGrid` no longer matches the sensor's cell. Acts as a defensive cleanup against missed `Leave` events. `Activate` is then toggled to match `IsTriggered`.

Detected type is `DynamicThing` (not narrowed to `Human`). Any dynamic-thing entry triggers the sensor: players, livestock, rovers, suits, dropped items, etc., subject to whatever subset the GridController emits Enter / Leave events for.

## LogicType surface
<!-- verified: 0.2.6228.27061 @ 2026-05-18 -->

```csharp
public override bool CanLogicRead(LogicType logicType)
{
    if (logicType == LogicType.Quantity) return true;
    return base.CanLogicRead(logicType);
}

public override double GetLogicValue(LogicType logicType)
{
    if (logicType == LogicType.Quantity) return TriggeredDynamicThings.Count;
    return base.GetLogicValue(logicType);
}
```

- `Quantity` is readable, returns the count of detected things on the watched cell.
- No writable `LogicType` overridden here (no `Setting` knob; range is fixed).
- Boolean trigger surfaces on `Activate` (`On` / `Open` etc.) via the inherited `Device` / `Sensor` chain.

## Motherboard hookup
<!-- verified: 0.2.6228.27061 @ 2026-05-18 -->

```csharp
public override void SetMotherboards(bool isTriggered)
{
    foreach (Motherboard linkedMotherboard in LinkedMotherboards)
    {
        if (linkedMotherboard is Circuitboard circuitboard && circuitboard.ParentComputer.AsDevice().Powered)
        {
            circuitboard.RemoteToggle(isTriggered);
        }
    }
}
```

Identical shape to `ProximitySensor.SetMotherboards`: powered-down computers are skipped.

## Behavioural consequences
<!-- verified: 0.2.6228.27061 @ 2026-05-18 -->

- Effective detection range is one `WorldGrid` cell (approximately 1m on each side in vanilla, matching the construction grid). A human one cell away does not register.
- Walls and floors are not directly tested, but they constrain where dynamic things can be on the grid; a person inside a sealed adjacent room is on a different cell and is not seen.
- Walking past the cell triggers Enter and Leave events in sequence; the sensor flips on then off via `ActivateSensor()` and the inherited `WaitUntilNotTriggered` UniTask.
- Cleanup pass in `OnThreadUpdate` guards against stuck triggers if a `Leave` was missed (object destroyed in-place, despawned, etc.).

## Contrast with ProximitySensor
<!-- verified: 0.2.6228.27061 @ 2026-05-18 -->

| Aspect | MotionSensor | ProximitySensor |
|---|---|---|
| Detection model | GridController Enter / Leave events | Per-tick `Vector3.SqrMagnitude` scan |
| Range | One `WorldGrid` cell, fixed | Sphere radius `Setting` metres, 0-250 |
| Target type | Any `DynamicThing` | `Human` only |
| Authorisation filter | None on the entry | `IsAuthorized(allHuman)` per match |
| Walls / floors | Implicit via grid placement | **Ignored** (true 3D euclidean) |
| `Quantity` reads | `TriggeredDynamicThings.Count` | `Activate` (count of authorised humans in sphere) |
| `Setting` writable via logic | No | Yes |

## Verification history

- 2026-05-18: Page created from Assembly-CSharp.decompiled.cs line 386206. Grid-watch registration, GridEvent-driven detection path, OnThreadUpdate reconciliation, LogicType surface, and behavioural contrast with ProximitySensor captured verbatim. No prior page existed.

## Open questions

- The set of `DynamicThing` subclasses that actually generate `GridEvent.Enter` / `Leave` for a watched grid cell depends on `GridController.SetWatchGrid` and the broader Grid / WorldGrid system. Not characterised on this page. See `Research/GameClasses/Grid3.md` (and related grid pages) for the dispatch side.
