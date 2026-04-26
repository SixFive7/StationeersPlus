---
title: WirelessPower
type: GameClasses
created_in: 0.2.6228.27061
verified_in: 0.2.6228.27061
verified_at: 2026-04-25
sources:
  - $(StationeersPath)\rocketstation_Data\Managed\Assembly-CSharp.dll :: Assets.Scripts.Objects.Electrical.WirelessPower
  - $(StationeersPath)\rocketstation_Data\Managed\Assembly-CSharp.dll :: Assets.Scripts.Objects.Electrical.PowerTransmitter
  - $(StationeersPath)\rocketstation_Data\Managed\Assembly-CSharp.dll :: Assets.Scripts.Objects.Electrical.PowerReceiver
  - $(StationeersPath)\rocketstation_Data\Managed\Assembly-CSharp.dll :: Assets.Scripts.RotatableBehaviour
related:
  - ./PowerTransmitter.md
  - ./RotatableBehaviour.md
  - ../GameSystems/PlacementOrientation.md
  - ../GameSystems/PowerTickThreading.md
tags: [power, transforms, network]
---

# WirelessPower

Vanilla abstract base class at `Assets.Scripts.Objects.Electrical.WirelessPower : ElectricalInputOutput`. The shared servo / dish-aim machinery used by `PowerTransmitter`, `PowerReceiver`, and `PowerTransmitterOmni`. Holds `Horizontal` / `Vertical` properties (servo state in normalized [0..1] units), the dish transform tree, and the network-sync hook that ships H/V to clients.

## Servo math
<!-- verified: 0.2.6228.27061 @ 2026-04-25 -->

Source: `Assets/Scripts/Objects/Electrical/WirelessPower.cs:66-100`.

```csharp
public double Vertical
{
    get { return _vertical; }
    set
    {
        if (_vertical != value)
        {
            _vertical = value;
            if ((bool)DishTransform)
            {
                DishTransform.localRotation = Quaternion.Euler(Mathf.Lerp(90f, -90f, (float)_vertical), 0f, 0f);
            }
            DishForward = DishTransform.up;
            RayPosition = RayTransform.position;
            IsDirty = true;
        }
    }
}

public double Horizontal
{
    get { return _horizontal; }
    set
    {
        if (_horizontal != value)
        {
            _horizontal = value;
            if ((bool)AxleTransform)
            {
                AxleTransform.localRotation = Quaternion.Euler(0f, (float)(_horizontal * MaximumHorizontal), 0f);
            }
            AxleForward = AxleTransform.up;
            RayPosition = RayTransform.position;
            IsDirty = true;
        }
    }
}

public double MaximumVertical => 180.0;
public double MaximumHorizontal => 360.0;
```

Both setters write `localRotation` (parent-relative). The math is therefore frame-invariant: if the dish's root transform is rotated (sideways on a wall, upside-down on a ceiling), the same `Vertical` and `Horizontal` ratios still produce the dish-local Euler rotations the geometry expects. The sphere of reachable directions rotates with the parent.

`MaximumHorizontal = 360.0` and `MaximumVertical = 180.0` are degree-space constants; `_vertical` and `_horizontal` are normalized [0..1] ratios. `Lerp(90f, -90f, V)` maps V=0 -> +90 deg pitch (nadir of the dish-local frame) and V=1 -> -90 deg pitch (zenith of the dish-local frame).

## Vertical clamping in input paths
<!-- verified: 0.2.6228.27061 @ 2026-04-25 -->

Source: `WirelessPower.cs:273-293, 389-395`.

The setter itself does not clamp, but every public consumer of `Vertical` does:

- Logic-write (`SetLogicValue` for `LogicType.Vertical`): clamps `value < 0.0` to 0 and `value > MaximumVertical` to `MaximumVertical`, then divides by `MaximumVertical` to produce the normalized ratio.
- Interaction button 3 (decrement vertical): `if (num < 0.0) num = 0.0;`.
- Interaction button 4 (increment vertical): `if (num > 1.0) num = 1.0;`.

The dish servo's reachable space is the full hemisphere from nadir (V=0) to zenith (V=1) in the dish-local frame. Mods that need finer-grained control (e.g. clamping to 7 deg above horizon for visual realism, or removing the clamp to allow wrap-around) can patch `WirelessPower.SetLogicValue` or the interaction handlers.

## Network sync (H/V via flag 256)
<!-- verified: 0.2.6228.27061 @ 2026-04-25 -->

Source: `WirelessPower.cs:120-168`.

```csharp
public override void BuildUpdate(RocketBinaryWriter writer, ushort networkUpdateType)
{
    base.BuildUpdate(writer, networkUpdateType);
    if (Thing.IsNetworkUpdateRequired(256u, networkUpdateType))
    {
        writer.WriteFloatHalf((float)(RotatableBehaviour?.TargetVertical ?? 0.0));
        writer.WriteFloatHalf((float)(RotatableBehaviour?.TargetHorizontal ?? 0.0));
    }
    if (Thing.IsNetworkUpdateRequired(512u, networkUpdateType))
    {
        writer.WriteFloatHalf(VisualizerIntensity);
    }
}

public override void ProcessUpdate(RocketBinaryReader reader, ushort networkUpdateType)
{
    base.ProcessUpdate(reader, networkUpdateType);
    if (Thing.IsNetworkUpdateRequired(256u, networkUpdateType))
    {
        float num = reader.ReadFloatHalf();
        float num2 = reader.ReadFloatHalf();
        if (RotatableBehaviour != null)
        {
            RotatableBehaviour.TargetVertical = num;
            RotatableBehaviour.TargetHorizontal = num2;
        }
    }
    if (Thing.IsNetworkUpdateRequired(512u, networkUpdateType))
    {
        VisualizerIntensity = reader.ReadFloatHalf();
    }
}

public override void SerializeOnJoin(RocketBinaryWriter writer)
{
    base.SerializeOnJoin(writer);
    writer.WriteDouble(RotatableBehaviour?.TargetVertical ?? 0.0);
    writer.WriteDouble(RotatableBehaviour?.TargetHorizontal ?? 0.0);
}

public override void DeserializeOnJoin(RocketBinaryReader reader)
{
    base.DeserializeOnJoin(reader);
    double targetVertical = reader.ReadDouble();
    double targetHorizontal = reader.ReadDouble();
    if (RotatableBehaviour != null)
    {
        RotatableBehaviour.TargetVertical = targetVertical;
        RotatableBehaviour.TargetHorizontal = targetHorizontal;
    }
}
```

Two flags:

- `256` (0x100): per-tick delta carrying half-precision `TargetVertical` / `TargetHorizontal` (4 bytes total). Triggered on the host-side setters of `RotatableBehaviour.TargetHorizontal` / `TargetVertical`.
- `512` (0x200): per-tick delta carrying half-precision `VisualizerIntensity` (2 bytes).

Late-join (`SerializeOnJoin` / `DeserializeOnJoin`) ships full-precision `double` values for both targets so a joining client picks up an exact slew destination.

## Save data
<!-- verified: 0.2.6228.27061 @ 2026-04-25 -->

```csharp
public class WirelessPowerSaveData : StructureSaveData
{
    [XmlElement] public double Horizontal;
    [XmlElement] public double Vertical;
    [XmlElement] public double TargetHorizontal;
    [XmlElement] public double TargetVertical;
}
```

`PowerTransmitterSaveData : WirelessPowerSaveData` adds `OutputNetworkReferenceId`. `PowerReceiverSaveData : WirelessPowerSaveData` adds nothing. Both H/V live values and slew targets persist across save/load.

## OnRegistered: H/V reset to upright
<!-- verified: 0.2.6228.27061 @ 2026-04-25 -->

Source: `PowerTransmitter.cs:308-327`, `PowerReceiver.cs:229-236`.

```csharp
// PowerTransmitter
public override void OnRegistered(Cell cell)
{
    if (GameManager.GameState != GameState.Loading && GameManager.RunSimulation)
    {
        OutputNetwork = WirelessNetwork.GetWirelessNetwork(this);
        OutputNetwork.AddDevice(this);
    }
    base.OnRegistered(cell);
    if (GameManager.GameState == GameState.Running)
    {
        base.Horizontal = 0.0;
        base.Vertical = 1.0;
        base.RotatableBehaviour.TargetHorizontal = base.Horizontal;
        base.RotatableBehaviour.TargetVertical = base.Vertical;
    }
    if (GameManager.GameState != GameState.Loading)
    {
        TryContactReceiver();
    }
}
```

When a fresh dish is placed (`GameState.Running`), H is reset to 0 and V is reset to 1 (zenith). At save-load (`GameState.Loading`) this branch is skipped so the saved H/V values persist.

V=1 corresponds to "the dish points along its dish-local +Y axis" (zenith of the dish-local frame). For a floor-mounted dish that aligns with world up. For a ceiling-mounted dish, V=1 would point world-down. Mod authors enabling non-floor placement should consider either (a) leaving V=1 alone (the dish points away from the mount surface, intuitive) or (b) re-deriving the post-place default H/V from the prefab's mount surface.

## Verification history
<!-- verified: 0.2.6228.27061 @ 2026-04-25 -->

- 2026-04-25: page created during a deep decompile of the placement-orientation system. Verbatim source extracts from `WirelessPower.cs`, `PowerTransmitter.cs`, `PowerReceiver.cs`, `WirelessPowerSaveData.cs`. No conflicts with `PowerTransmitter.md`; this page documents the BASE class.

## Open questions

None.
