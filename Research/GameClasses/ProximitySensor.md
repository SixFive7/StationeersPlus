---
title: ProximitySensor
type: GameClasses
created_in: 0.2.6228.27061
verified_in: 0.2.6228.27061
verified_at: 2026-05-18
sources:
  - $(StationeersPath)\rocketstation_Data\Managed\Assembly-CSharp.dll :: Assets.Scripts.Objects.ProximitySensor
related:
  - ./Sensor.md
  - ./MotionSensor.md
  - ./Human.md
tags: [logic, network, entity]
---

# ProximitySensor

Sensor subclass that detects authorised humans inside a sphere centred on the sensor. Detection is pure euclidean squared distance against `Human.AllHumans`. **No line-of-sight, no raycast, no grid / room / atmosphere consideration. Walls, floors, terrain, and planetary surface are all transparent to it.** Differs sharply from `MotionSensor` (single-cell grid event source) despite both being `Sensor` subclasses with `IDoorControl`.

## Class shape and fields
<!-- verified: 0.2.6228.27061 @ 2026-05-18 -->

```csharp
public class ProximitySensor : Sensor, IDoorControl
{
    private int _setting = 2;
    private static int MaxSetting = 250;

    [SerializeField]
    private Knob knob;

    public override bool IsTriggered => Activate > 0;

    public int Setting
    {
        get => _setting;
        set
        {
            value = Mathf.Clamp(value, 0, MaxSetting);
            _setting = value;
            knob.SetKnob(Setting, MaxSetting).Forget();
            if (Assets.Scripts.Networking.NetworkManager.IsServer)
                base.NetworkUpdateFlags |= 256;
        }
    }
    ...
}
```

- `Setting` is the detection radius in metres. Clamped to `[0, 250]`. Default `2`.
- `IsTriggered` is true whenever the count of detected humans is non-zero (`Activate > 0`).
- The serialised knob field drives the in-world UI; turning Button1 / Button2 adjusts `Setting` by `1` (or `10` with Alt).
- Network delta flag `256` carries `Setting` (single byte) to clients via `BuildUpdate` / `ProcessUpdate` and on join via `SerializeOnJoin` / `DeserializeOnJoin`. Maximum representable byte is 255, so the on-wire cap matches the `MaxSetting = 250` clamp.

## Detection: squared-distance check, no occlusion
<!-- verified: 0.2.6228.27061 @ 2026-05-18 -->

```csharp
public override void OnThreadUpdate()
{
    base.OnThreadUpdate();
    if (!GameManager.RunSimulation) return;
    int num = 0;
    foreach (Human allHuman in Human.AllHumans)
    {
        if (Vector3.SqrMagnitude(allHuman.Position - base.Position) < (float)(Setting * Setting) && IsAuthorized(allHuman))
        {
            num++;
        }
    }
    if (Activate != num)
        OnServer.Interact(base.InteractActivate, num);
}
```

Key facts:

- Iterates `Human.AllHumans` every `OnThreadUpdate` tick (server-only via `GameManager.RunSimulation`).
- Uses `Vector3.SqrMagnitude` against `Setting * Setting` (squared comparison avoids sqrt). Detection radius is exactly `Setting` metres in 3D world space.
- **No `Physics.Raycast`, no `WorldGrid` adjacency, no `Cell` containment, no `AtmosphericsController.SampleGlobalAtmosphere`, no room / structure / wall check anywhere in the detection path.** The sensor sees every authorised human within radius regardless of intervening geometry.
- `IsAuthorized(allHuman)` filters by the sensor's authorisation list (inherited authorisation surface), but does not affect line of sight.
- `Activate` becomes the **count of detected humans**, not a boolean. Multiple authorised humans inside the sphere produce `Activate = N`.

## LogicType surface
<!-- verified: 0.2.6228.27061 @ 2026-05-18 -->

```csharp
public override bool CanLogicRead(LogicType logicType) => logicType switch
{
    LogicType.Setting => true,
    LogicType.Quantity => true,
    _ => base.CanLogicRead(logicType),
};

public override bool CanLogicWrite(LogicType logicType) => logicType switch
{
    LogicType.Setting => true,
    LogicType.Activate => false,
    _ => base.CanLogicWrite(logicType),
};

public override void SetLogicValue(LogicType logicType, double value)
{
    if (logicType == LogicType.Setting) Setting = (int)value;
    base.SetLogicValue(logicType, value);
}

public override double GetLogicValue(LogicType logicType) => logicType switch
{
    LogicType.Setting => Setting,
    LogicType.Quantity => Activate,
    _ => base.GetLogicValue(logicType),
};
```

- `Setting` is readable and writable (sphere radius in metres).
- `Quantity` is readable, returns `Activate` (count of authorised humans in range).
- `Activate` is explicitly NOT writable. Boolean trigger state surfaces on the inherited `On` / `Open` paths depending on what the inherited `Device` / `Sensor` chain exposes; nothing new is added here.
- Setting `Setting` via logic clamps through the property setter `[0, 250]`.

## Save format
<!-- verified: 0.2.6228.27061 @ 2026-05-18 -->

```csharp
public class ProximitySensorSaveData : StructureSaveData
{
    public int Setting;
}
```

Only `Setting` persists. `Activate` is recomputed each tick from live human positions.

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

Same shape as `Sensor.SetMotherboards` plus a `circuitboard.ParentComputer.AsDevice().Powered` gate: a powered-down computer's circuitboards are skipped.

## Interaction (knob)
<!-- verified: 0.2.6228.27061 @ 2026-05-18 -->

```csharp
public override DelayedActionInstance InteractWith(Interactable interactable, Interaction interaction, bool doAction = true)
{
    ...
    switch (interactable.Action)
    {
    case InteractableType.Button1:
        delayedActionInstance.ActionMessage = GameStrings.GlobalDecrease.AsString();
        delayedActionInstance.AppendStateMessage(GameStrings.GlobalRadius, StringManager.Get(Setting));
        if (!doAction) return delayedActionInstance.Succeed();
        knob.PlayKnobSound(Setting, increase: false, MaxSetting);
        Setting = Mathf.Max(Setting - ((!interaction.AltKey) ? 1 : 10), 0);
        knob.SetKnob(Setting, MaxSetting).Forget();
        return DelayedActionInstance.Success(interactable.ContextualName);
    case InteractableType.Button2:
        ... // mirror of Button1, +1 or +10
    }
}
```

Button1 decreases radius, Button2 increases. Alt-modifier multiplies step from 1 to 10. Contextual name resolves to `InterfaceStrings.Radius`.

## Behavioural consequences
<!-- verified: 0.2.6228.27061 @ 2026-05-18 -->

- Walls, doors, steel sheets, and full base interiors do not block detection. A ProximitySensor in a bunker triggers on a player walking past outside, provided they are within `Setting` metres of the sensor in straight-line 3D distance.
- Floors are equally transparent: a sensor on the top floor of a multi-level base will trigger on humans below or above within radius.
- Terrain and planetary surface are not consulted. Underground caverns within radius register.
- Z-axis distance counts the same as XZ; the sphere is true 3D.
- Only `Human` instances count. Non-human `DynamicThing` (livestock, rovers, suits without an occupant) do not trigger this sensor. (For dynamic-thing detection on a grid cell, see `MotionSensor`.)
- Authorisation filtering excludes non-authorised humans from the count entirely. With `Quantity` wired into logic, this effectively gives an authorised-headcount-in-radius read.

## Verification history

- 2026-05-18: Page created from Assembly-CSharp.decompiled.cs line 394549. Detection mechanism, LogicType surface, save format, knob interaction, and motherboard gate captured verbatim. Explicit determination: no LOS / occlusion / grid check in the detection path. No prior page existed.

## Open questions

None.
