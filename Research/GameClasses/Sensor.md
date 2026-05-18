---
title: Sensor
type: GameClasses
created_in: 0.2.6228.27061
verified_in: 0.2.6228.27061
verified_at: 2026-05-18
sources:
  - $(StationeersPath)\rocketstation_Data\Managed\Assembly-CSharp.dll :: Assets.Scripts.Objects.Sensor
related:
  - ./ProximitySensor.md
  - ./MotionSensor.md
  - ./Device.md
tags: [logic, network]
---

# Sensor

Abstract-ish base for the small sensor family (ProximitySensor, MotionSensor, OccupancySensor, and others). Wires sensors into the motherboard / circuitboard automation system through `Activate`, `IsTriggered`, and a held UniTask that flips circuitboards on and off.

## Class shape
<!-- verified: 0.2.6228.27061 @ 2026-05-18 -->

```csharp
public class Sensor : SmallDevice, ISmartRotatable
{
    [Header("ISmartRotation")]
    public SmartRotate.ConnectionType ConnectionType = SmartRotate.ConnectionType.Exhaustive;
    public int[] OpenEndsPermutation = new int[6] { 0, 1, 2, 3, 4, 5 };

    public List<Motherboard> LinkedMotherboards = new List<Motherboard>();

    private UniTask _notTriggeredTask;
    private CancellationTokenSource _taskCancel;

    public virtual bool IsTriggered => false;
    ...
}
```

Inherits from `SmallDevice` (a `Device` variant). Implements `ISmartRotatable` so it participates in the smart-rotate cable connector logic via `ConnectionType.Exhaustive` and a six-element permutation of open ends.

## Trigger lifecycle
<!-- verified: 0.2.6228.27061 @ 2026-05-18 -->

`IsTriggered` is the per-subclass condition (override returns `true` while a target is detected). `Activate` is the inherited `Device` interactable value; subclass `OnThreadUpdate` keeps the two in sync via `OnServer.Interact(InteractActivate, ...)`. The base class itself runs no detection.

`WaitUntilNotTriggered()` is an `async UniTask`:

```csharp
public async UniTask WaitUntilNotTriggered()
{
    if (GameManager.GameState == GameState.None) return;
    if (GameManager.RunSimulation)
        OnServer.Interact(this, InteractableType.Activate, 1);
    SetMotherboards(isTriggered: true);
    while (IsTriggered)
        await UniTask.Delay(100, ignoreTimeScale: false, PlayerLoopTiming.Update, _taskCancel.Token);
    if (GameManager.GameState != GameState.None && !_taskCancel.IsCancellationRequested)
    {
        if (GameManager.RunSimulation)
            OnServer.Interact(this, InteractableType.Activate, 0);
        SetMotherboards(isTriggered: false);
    }
}
```

`ActivateSensor()` starts that task if `Activate == 0` and no task is pending. The task polls `IsTriggered` every 100 ms; when it goes false, the sensor clears `Activate` and flips every linked motherboard back off. `ResetSensor()` cancels the task at destruction or on game-state change.

Polling cadence is 100 ms (`UniTask.Delay(100)`) on the Update loop, distinct from `OnThreadUpdate()` which subclasses use for their own per-tick detection scan.

## Motherboard linkage
<!-- verified: 0.2.6228.27061 @ 2026-05-18 -->

```csharp
public virtual void SetMotherboards(bool isTriggered)
{
    foreach (Motherboard linkedMotherboard in LinkedMotherboards)
    {
        if (linkedMotherboard is Circuitboard circuitboard)
        {
            circuitboard.RemoteToggle(isTriggered);
        }
    }
}

public override void OnLinkWithBoard(Motherboard motherboard)
{
    base.OnLinkWithBoard(motherboard);
    LinkedMotherboards.Add(motherboard);
}

public override void OnUnlinkWithBoard(Motherboard motherboard)
{
    base.OnUnlinkWithBoard(motherboard);
    LinkedMotherboards.Remove(motherboard);
}
```

`SetMotherboards` ignores non-`Circuitboard` motherboards. `ProximitySensor` and `MotionSensor` both override this to additionally require `circuitboard.ParentComputer.AsDevice().Powered` before issuing `RemoteToggle`.

## ISmartRotatable
<!-- verified: 0.2.6228.27061 @ 2026-05-18 -->

```csharp
public SmartRotate.ConnectionType GetConnectionType() => ConnectionType;
public void SetOpenEndsPermutation(int[] permutation) => OpenEndsPermutation = (int[])permutation.Clone();
public void SetConnectionType(SmartRotate.ConnectionType connectionType) => ConnectionType = connectionType;
public int[] GetOpenEndsPermutation() => (int[])OpenEndsPermutation.Clone();
```

Default `ConnectionType = SmartRotate.ConnectionType.Exhaustive`, default permutation `{0, 1, 2, 3, 4, 5}`.

## Verification history

- 2026-05-18: Page created from Assembly-CSharp.decompiled.cs line 397847. Initial class shape, trigger lifecycle, motherboard linkage, ISmartRotatable surface captured. No prior page existed.

## Open questions

None.
