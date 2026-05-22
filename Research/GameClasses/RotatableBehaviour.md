---
title: RotatableBehaviour
type: GameClasses
created_in: 0.2.6228.27061
verified_in: 0.2.6228.27061
verified_at: 2026-05-22
sources:
  - Mods/PowerTransmitterPlus/RESEARCH.md:735-736
  - $(StationeersPath)\rocketstation_Data\Managed\Assembly-CSharp.dll :: RotatableBehaviour
  - .work/decomp/0.2.6228.27061/Assembly-CSharp.decompiled.cs :: lines 201818-202089 (RotatableBehaviour full class), 405441-405899 (WirelessPower IRotatable members)
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

## Current vs target angle, slew-complete detection, slew speed
<!-- verified: 0.2.6228.27061 @ 2026-05-22 -->

Source: `Assets/Scripts/RotatableBehaviour.cs` lines 201818-202089.

The TARGET angle and the CURRENT (animated) angle are stored in two different places:

- TARGET: `RotatableBehaviour._targetHorizontal` / `_targetVertical` (private doubles), exposed via the public `TargetHorizontal` / `TargetVertical` getters/setters (lines 201834, 201858). These are the slew DESTINATION and are what gets network-synced (the setter sets `NetworkUpdateFlags |= 256` on the server).
- CURRENT: `_parentRotatable.Horizontal` / `Vertical` (the `IRotatable`'s own properties, i.e. `WirelessPower.Horizontal` / `Vertical`). These are the LIVE animated values the model actually points at. `RotatableBehaviour` does not store a copy of the current angle; it reads and writes the parent's properties directly. So to read the live aim, read `wirelessPower.Horizontal` / `wirelessPower.Vertical` (normalized [0..1] ratios), NOT `RotatableBehaviour.TargetHorizontal` / `TargetVertical`.

Slew-complete detection (per-channel), lines 201884-201906:

```csharp
public bool IsHorizontal =>
    _parentRotatable == null
    || Math.Abs(_parentRotatable.Horizontal - TargetHorizontal) < (double)_parentRotatable.RotationTolerance;

public bool IsVertical =>
    _parentRotatable == null
    || Math.Abs(_parentRotatable.Vertical - TargetVertical) < (double)_parentRotatable.RotationTolerance;
```

"Fully aimed" is `IsHorizontal && IsVertical` (the `DoMoveTask` loop breaks on exactly that, line 202073). The epsilon is the parent's `RotationTolerance`. For `WirelessPower` that is `1E-07f` (see constants below), an extremely tight tolerance, so a dish reads as "still slewing" until its current ratio is within 1e-7 of target.

`IsMoving => _currentTask.Status == UniTaskStatus.Pending` (line 201912) is a coarser "is the slew task still running" flag. The task runs until both channels satisfy `IsHorizontal && IsVertical` (or the parent breaks / can't rotate).

`WirelessPower` slew constants (the `IRotatable` members `RotatableBehaviour` reads through `_parentRotatable`), from `WirelessPower.cs` lines 405531-405557:

| Member | Value on `WirelessPower` | Notes |
|---|---|---|
| `RotationTolerance` | `1E-07f` | slew-complete epsilon in ratio space |
| `MaximumHorizontal` | `360.0` | degrees |
| `MaximumVertical` | `180.0` | degrees |
| `MovementSpeedHorizontal` | `0.05f` (virtual) | ratio/sec base, before scaling |
| `MovementSpeedVertical` | `0.05f` (virtual) | ratio/sec base, before scaling |
| `CanRotate()` | `true` | dish always allowed to rotate |

The effective per-frame step is `MovementSpeedHorizontal => (180.0 / MaximumHorizontal) * _parentRotatable.MovementSpeedHorizontal * Time.deltaTime` (line 201908), i.e. for a transmitter `(180/360) * 0.05 * dt = 0.025 * dt` ratio/frame horizontal, and `(180/180) * 0.05 * dt = 0.05 * dt` ratio/frame vertical. Different `IRotatable` implementers use different constants (SolarPanel/RadiatorRotatable `RotationTolerance => 0.0001f`, `MovementSpeed => 0.05f` at lines 192661/175499; DaylightSensor at 396713 uses `0.0001f` / `0.01f` / `0.005f`); the WirelessPower values above are the ones that apply to the dish pair.

Note these are NOT distinct fields exposing the current angle separately as "animated angle" objects: the current angle is just the parent's `Horizontal` / `Vertical` double, and target is `RotatableBehaviour.Target*`. Both are readable independently, which is what lets a mod evaluate aim from the LIVE orientation (`wirelessPower.Horizontal` / `Vertical`) rather than the destination.

## Slew runs on the main thread, only while GameState.Running, on every peer
<!-- verified: 0.2.6228.27061 @ 2026-05-22 -->

Source: `RotatableBehaviour.cs` lines 201998-202081 (`DoMoveTask`), 405615-405626 (`WirelessPower.UpdateAnimator`).

`DoMoveTask` is an `async UniTask` that:

1. Switches to the main thread if started from a worker (`if (ThreadedManager.IsThread) await UniTask.SwitchToMainThread();`, line 202000). So all transform writes happen on the Unity main thread regardless of where `MoveToTarget` was called from.
2. Bails immediately if the parent is null / a cursor / broken / `!CanRotate()` (line 202004).
3. **Snaps instantly to target without animating when `GameManager.GameState != GameState.Running`** (lines 202009-202014): sets `_parentRotatable.Horizontal = TargetHorizontal; Vertical = TargetVertical;` and returns. This is the load-time / non-running path: a dish restored from save jumps straight to its saved target rather than visibly slewing.
4. While `GameState.Running`, loops `await UniTask.NextFrame()` and steps `Horizontal` / `Vertical` toward target by the movement speed each frame, with modulo-wrap on the horizontal channel (short-direction rotation), until `IsHorizontal && IsVertical`.

Trigger: `TargetHorizontal` / `TargetVertical` setters call `SyncTargetHorizontal` / `SyncTargetVertical` -> `MoveToTarget()` (line 201969), which starts `DoMoveTask` only if one is not already pending. So setting a target (re)launches the slew.

Server vs client / dedicated-server behavior:

- The slew animation (`DoMoveTask` stepping the current angle each frame) is **driven locally on every peer that has the object in its scene**, because it is keyed on `GameState.Running` and `Time.deltaTime`, NOT gated by `NetworkManager.IsServer` / `IsClient`. There is no `IsServer` / `IsClient` guard around the stepping loop. The transform genuinely rotates on whichever peer is running the task.
- What IS server-gated is only the SYNC of the target: `TargetHorizontal` / `TargetVertical` setters set `NetworkUpdateFlags |= 256` solely `if (NetworkManager.IsServer)` (lines 201847, 201871). Clients receive the new target via `WirelessPower.ProcessUpdate` (flag 256) and their own local `RotatableBehaviour` then slews to it. Late-join ships full-precision target doubles via `SerializeOnJoin` / `DeserializeOnJoin`.
- On a **dedicated (headless) server**: `GameManager.GameState` is `Running` and the simulation ticks, so the server's `RotatableBehaviour` DOES run `DoMoveTask` and the server-side transform rotates. `Time.deltaTime` is valid in headless/batch mode. There is no rendering, but the transform math (and therefore `RayTransform.position` / `DishTarget.position`, which the link raycast in `PowerTransmitter.TryContactReceiver` consumes) is live server-side. This is why the vanilla link raycast can succeed on a dedicated server: the dish's current orientation is actually simulated there, not merely animated on clients. (`DoMoveTask` does guard `Parent.IsBroken` and audio on `InventoryManager.ParentHuman` proximity, which is null on a headless server, so the moving-sound branch is skipped, but the rotation stepping is not.)

`WirelessPower.UpdateAnimator()` (the `IRotatable.UpdateAnimator` impl, line 405615) likewise switches to the main thread and only pushes `TargetHorizontal` / `TargetVertical` into the BaseAnimator float parameters when a `BaseAnimator` exists; it does not itself move the dish.

## Verification history
<!-- verified: 0.2.6228.27061 @ 2026-05-22 -->

- 2026-04-20: page created from the Research migration; verbatim content lifted from F0058. No conflicts.
- 2026-04-25: added "Servo state and slew" section. Additive only; no existing content changed. Source: direct decompile of `RotatableBehaviour.cs` during the placement-orientation deep-research pass. Game version 0.2.6228.27061.
- 2026-05-22: added "Current vs target angle, slew-complete detection, slew speed" and "Slew runs on the main thread, only while GameState.Running, on every peer" sections. Additive only; no existing content changed. Driving question: a beam-rendering mod (PowerTransmitterPlus) needs to read the LIVE dish orientation vs the slew target, the exact slew tolerance/speed, how slew-complete is detected, and whether rotation is simulated on a dedicated server. Findings sourced from `.work/decomp/0.2.6228.27061/Assembly-CSharp.decompiled.cs`: `RotatableBehaviour.IsHorizontal`/`IsVertical` (lines 201884-201906), `IsMoving` (201912), `MovementSpeedHorizontal`/`Vertical` derived getters (201908-201910), `DoMoveTask` main-thread switch + `GameState.Running` snap + per-frame stepping (201998-202081), `TargetHorizontal`/`TargetVertical` server-only `NetworkUpdateFlags |= 256` (201847/201871); and `WirelessPower.cs`: `RotationTolerance => 1E-07f` (405531), `MovementSpeedHorizontal`/`Vertical => 0.05f` (405537/405539), `CanRotate() => true` (405554), `UpdateAnimator` (405615). Cross-referenced WirelessPower.md (servo math, flag 256 sync) and PowerTransmitter.md (Head-child drift trap, TryContactReceiver consuming RayTransform.position).

## Open questions

None at creation.
