---
title: RotatableBehaviour
type: GameClasses
created_in: 0.2.6228.27061
verified_in: 0.2.6228.27061
verified_at: 2026-04-20
sources:
  - Mods/PowerTransmitterPlus/RESEARCH.md:735-736
  - $(StationeersPath)\rocketstation_Data\Managed\Assembly-CSharp.dll :: RotatableBehaviour
related:
  - ./PowerTransmitter.md
  - ./WirelessPower.md
tags: [transforms, network]
---

# RotatableBehaviour

Vanilla game class providing the servo behavior that drives dish rotation on `WirelessPower` subclasses. Holds `TargetHorizontal` / `TargetVertical` setters and an existing networked delta-state that clients slew to.

## Servo delta-state
<!-- verified: 0.2.6228.27061 @ 2026-04-20 -->

Source: F0058.

`WirelessPower.SetLogicValue` is server-authoritative in vanilla. Our prefix runs on the server. The ensuing writes to `RotatableBehaviour.TargetHorizontal` / `TargetVertical` set `NetworkUpdateFlags |= 256`, which the existing delta-state serialization ships to clients. `WirelessPower.ProcessUpdate` reads the flag and writes those targets on the client; the client's local servo then slews the dish. No new `INetworkMessage`.

## Servo state and slew
<!-- verified: 0.2.6228.27061 @ 2026-04-25 -->

Source: `Assets/Scripts/RotatableBehaviour.cs` (full file).

`RotatableBehaviour` carries:

- `private IRotatable _parentRotatable;` (line 15) - reference to the owning `WirelessPower` (or other `IRotatable`).
- `public double TargetHorizontal` and `public double TargetVertical` setters that write `_targetHorizontal` / `_targetVertical`, call `SyncTargetHorizontal` / `SyncTargetVertical`, and set `NetworkUpdateFlags |= 256`.
- `MovementSpeedHorizontal = (180.0 / MaximumHorizontal) * ParentSpeed * deltaTime` and similarly for vertical. The slew rate is computed in [0..1]-ratio-per-second space, so it is independent of how the parent's `MaximumHorizontal` / `MaximumVertical` map to degrees.
- `DoMoveTask` (`RotatableBehaviour.cs:96-186`) animates `_parentRotatable.Horizontal` / `Vertical` toward `_targetHorizontal` / `_targetVertical` each tick, with modulo wrap on the horizontal channel (so `Horizontal` from 0.95 to 0.05 traverses 0.10 worth of rotation in the short direction).

The slew updates the parent's `Horizontal` and `Vertical` properties; those setters are responsible for writing the actual transform `localRotation`. The slew loop never references `Vector3.up`, `Vector3.forward`, or `transform.parent.up`, so the math is parent-frame-invariant. A `RotatableBehaviour` attached to a `WirelessPower` whose root is rotated (sideways on a wall, upside-down on a ceiling) continues to slew correctly because the actual rotation writes happen on `AxleTransform.localRotation` and `DishTransform.localRotation` in the parent-relative frame.

## Verification history
<!-- verified: 0.2.6228.27061 @ 2026-04-25 -->

- 2026-04-20: page created from the Research migration; verbatim content lifted from F0058. No conflicts.
- 2026-04-25: added "Servo state and slew" section. Additive only; no existing content changed. Source: direct decompile of `RotatableBehaviour.cs` during the placement-orientation deep-research pass. Game version 0.2.6228.27061.

## Open questions

None at creation.
