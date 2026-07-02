---
title: RotatableBehaviour
type: GameClasses
created_in: 0.2.6228.27061
verified_in: 0.2.6403.27689
verified_at: 2026-07-02
sources:
  - Mods/PowerTransmitterPlus/RESEARCH.md:735-736
  - $(StationeersPath)\rocketstation_Data\Managed\Assembly-CSharp.dll :: RotatableBehaviour
  - .work/decomp/0.2.6228.27061/Assembly-CSharp.decompiled.cs :: lines 201818-202089 (RotatableBehaviour full class), 405441-405899 (WirelessPower IRotatable members)
  - .work/decomp/0.2.6403.27689/Assembly-CSharp.decompiled.cs :: lines 217254-217525 (RotatableBehaviour full class), 426869-426973 (WirelessPower RotationTolerance + UpdateAnimator + Awake), 421087-421683 (SolarPanel IRotatable members)
related:
  - ./PowerTransmitter.md
  - ./WirelessPower.md
  - ./SolarPanel.md
tags: [transforms, network, threading]
---

# RotatableBehaviour

Vanilla game class providing the servo behavior that drives dish rotation on `WirelessPower` subclasses. Holds `TargetHorizontal` / `TargetVertical` setters and an existing networked delta-state that clients slew to.

## Servo delta-state
<!-- verified: 0.2.6403.27689 @ 2026-07-02 -->

Source: F0058; re-verified against the 0.2.6403.27689 decompile (setter flag writes at lines 217283-217289 / 217307-217313).

`WirelessPower.SetLogicValue` is server-authoritative in vanilla. Our prefix runs on the server. The ensuing writes to `RotatableBehaviour.TargetHorizontal` / `TargetVertical` set `NetworkUpdateFlags |= 256`, which the existing delta-state serialization ships to clients. `WirelessPower.ProcessUpdate` reads the flag and writes those targets on the client; the client's local servo then slews the dish. No new `INetworkMessage`.

## Servo state and slew
<!-- verified: 0.2.6403.27689 @ 2026-07-02 -->

Source: `.work/decomp/0.2.6403.27689/Assembly-CSharp.decompiled.cs` lines 217254-217525 (full class).

`RotatableBehaviour` is a plain `[Serializable]` managed class (line 217254; NOT a MonoBehaviour) constructed by the parent's `Awake` (`new RotatableBehaviour(this)`, constructor at 217400-217403). It carries:

- `private IRotatable _parentRotatable;` (line 217256) - reference to the owning `WirelessPower`, `SolarPanel`, `RadiatorRotatable`, or other `IRotatable`.
- `public double TargetVertical` (217270) and `public double TargetHorizontal` (217294) setters that discard NaN (`if (double.IsNaN(value)) return;`), call `SyncTargetVertical` / `SyncTargetHorizontal` (which store `_targetVertical` / `_targetHorizontal`, call `MoveToTarget()`, and fire `_parentRotatable.UpdateAnimator().Forget()`, lines 217413-217425), and then, only `if (NetworkManager.IsServer)`, set `NetworkUpdateFlags |= 256` on `_parentRotatable.GetAsThing` (217283-217289 / 217307-217313).
- `MovementSpeedHorizontal => (float)(180.0 / _parentRotatable.MaximumHorizontal) * _parentRotatable.MovementSpeedHorizontal * Time.deltaTime` (line 217344) and the same shape for vertical (217346). The slew rate is computed in [0..1]-ratio-per-frame space, so it is independent of how the parent's `MaximumHorizontal` / `MaximumVertical` map to degrees.
- `DoMoveTask` (217434-217517) animates `_parentRotatable.Horizontal` / `Vertical` toward `_targetHorizontal` / `_targetVertical` each frame, with modulo wrap on the horizontal channel (so `Horizontal` from 0.95 to 0.05 traverses 0.10 worth of rotation in the short direction).

The slew updates the parent's `Horizontal` and `Vertical` properties; those setters are responsible for writing the actual transform `localRotation`. The slew loop never references `Vector3.up`, `Vector3.forward`, or `transform.parent.up`, so the math is parent-frame-invariant. A `RotatableBehaviour` attached to a `WirelessPower` whose root is rotated (sideways on a wall, upside-down on a ceiling) continues to slew correctly because the actual rotation writes happen on `AxleTransform.localRotation` and `DishTransform.localRotation` in the parent-relative frame.

## Current vs target angle, slew-complete detection, slew speed
<!-- verified: 0.2.6403.27689 @ 2026-07-02 -->

Source: `.work/decomp/0.2.6403.27689/Assembly-CSharp.decompiled.cs` lines 217254-217525.

The TARGET angle and the CURRENT (animated) angle are stored in two different places:

- TARGET: `RotatableBehaviour._targetHorizontal` / `_targetVertical` (private doubles), exposed via the public `TargetHorizontal` / `TargetVertical` getters/setters (lines 217294, 217270). These are the slew DESTINATION and are what gets network-synced (the setter sets `NetworkUpdateFlags |= 256` on the server).
- CURRENT: `_parentRotatable.Horizontal` / `Vertical` (the `IRotatable`'s own properties, i.e. `WirelessPower.Horizontal` / `Vertical`). These are the LIVE animated values the model actually points at. `RotatableBehaviour` does not store a copy of the current angle; it reads and writes the parent's properties directly. So to read the live aim, read `wirelessPower.Horizontal` / `wirelessPower.Vertical` (normalized [0..1] ratios), NOT `RotatableBehaviour.TargetHorizontal` / `TargetVertical`.

Slew-complete detection (per-channel), lines 217320-217342:

```csharp
public bool IsHorizontal
{
    get
    {
        if (_parentRotatable != null)
        {
            return Math.Abs(_parentRotatable.Horizontal - TargetHorizontal) < (double)_parentRotatable.RotationTolerance;
        }
        return true;
    }
}

public bool IsVertical
{
    get
    {
        if (_parentRotatable != null)
        {
            return Math.Abs(_parentRotatable.Vertical - TargetVertical) < (double)_parentRotatable.RotationTolerance;
        }
        return true;
    }
}
```

"Fully aimed" is `IsHorizontal && IsVertical` (the `DoMoveTask` loop breaks on exactly that, line 217509). The epsilon is the parent's `RotationTolerance`. For `WirelessPower` that is `1E-07f` (see constants below), an extremely tight tolerance, so a dish reads as "still slewing" until its current ratio is within 1e-7 of target.

`IsMoving => _currentTask.Status == UniTaskStatus.Pending` (line 217348) is a coarser "is the slew task still running" flag. The task runs until both channels satisfy `IsHorizontal && IsVertical` (or the parent breaks / can't rotate).

`WirelessPower` slew constants (the `IRotatable` members `RotatableBehaviour` reads through `_parentRotatable`), re-verified at 0.2.6403.27689 (`RotationTolerance => 1E-07f` at line 426869):

| Member | Value on `WirelessPower` | Notes |
|---|---|---|
| `RotationTolerance` | `1E-07f` | slew-complete epsilon in ratio space |
| `MaximumHorizontal` | `360.0` | degrees |
| `MaximumVertical` | `180.0` | degrees |
| `MovementSpeedHorizontal` | `0.05f` (virtual) | ratio/sec base, before scaling |
| `MovementSpeedVertical` | `0.05f` (virtual) | ratio/sec base, before scaling |
| `CanRotate()` | `true` | dish always allowed to rotate |

The effective per-frame step is `MovementSpeedHorizontal => (float)(180.0 / _parentRotatable.MaximumHorizontal) * _parentRotatable.MovementSpeedHorizontal * Time.deltaTime` (line 217344; vertical twin at 217346), i.e. for a transmitter `(180/360) * 0.05 * dt = 0.025 * dt` ratio/frame horizontal, and `(180/180) * 0.05 * dt = 0.05 * dt` ratio/frame vertical. Different `IRotatable` implementers use different constants (at 0.2.6403.27689: `SolarPanel.RotationTolerance => 0.001f` at 421162 with `MovementSpeedHorizontal` / `MovementSpeedVertical => 0.05f` at 421170-421172 and `MaximumVertical = 165` / `MaximumHorizontal = 360`, so a solar panel steps `(180/360)*0.05*dt = 0.025*dt` ratio/frame horizontal and `(180/165)*0.05*dt ~ 0.0545*dt` ratio/frame vertical); the WirelessPower values above are the ones that apply to the dish pair.

Note these are NOT distinct fields exposing the current angle separately as "animated angle" objects: the current angle is just the parent's `Horizontal` / `Vertical` double, and target is `RotatableBehaviour.Target*`. Both are readable independently, which is what lets a mod evaluate aim from the LIVE orientation (`wirelessPower.Horizontal` / `Vertical`) rather than the destination.

## Slew runs on the main thread, only while GameState.Running, on every peer
<!-- verified: 0.2.6403.27689 @ 2026-07-02 -->

Source: `.work/decomp/0.2.6403.27689/Assembly-CSharp.decompiled.cs` lines 217434-217517 (`DoMoveTask`), 426953-426964 (`WirelessPower.UpdateAnimator`).

`DoMoveTask` is an `async UniTask` that:

1. Switches to the main thread if started from a worker (`if (ThreadedManager.IsThread) await UniTask.SwitchToMainThread();`, lines 217436-217439, the method's FIRST statement). So all transform writes happen on the Unity main thread regardless of where `MoveToTarget` was called from.
2. Bails immediately if the parent is null / a cursor / broken / `!CanRotate()` (line 217440).
3. **Snaps instantly to target without animating when `GameManager.GameState != GameState.Running`** (lines 217445-217450): sets `_parentRotatable.Horizontal = TargetHorizontal; Vertical = TargetVertical;` and returns. This is the load-time / non-running path: a dish restored from save jumps straight to its saved target rather than visibly slewing.
4. While `GameState.Running`, loops `await UniTask.NextFrame()` and steps `Horizontal` / `Vertical` toward target by the movement speed each frame, with modulo-wrap on the horizontal channel (short-direction rotation), until `IsHorizontal && IsVertical`. The loop's break condition (217509-217515) is `(IsHorizontal && IsVertical) || Parent.IsBroken || !_parentRotatable.CanRotate()`, and on break it calls `_parentRotatable.RunAfterAnimation()` (a virtual completion hook: no-op on `WirelessPower` and `SolarPanel`; `PowerReceiver` overrides it at 408179-408182 to `RequestRetarget()`).

Trigger: `TargetHorizontal` / `TargetVertical` setters call `SyncTargetVertical` / `SyncTargetHorizontal` -> `MoveToTarget()` (lines 217405-217425), which starts `DoMoveTask` only if one is not already pending. So setting a target (re)launches the slew.

Server vs client / dedicated-server behavior:

- The slew animation (`DoMoveTask` stepping the current angle each frame) is **driven locally on every peer that has the object in its scene**, because it is keyed on `GameState.Running` and `Time.deltaTime`, NOT gated by `NetworkManager.IsServer` / `IsClient`. There is no `IsServer` / `IsClient` guard around the stepping loop. The transform genuinely rotates on whichever peer is running the task.
- What IS server-gated is only the SYNC of the target: `TargetHorizontal` / `TargetVertical` setters set `NetworkUpdateFlags |= 256` solely `if (NetworkManager.IsServer)` (lines 217283-217289, 217307-217313). Clients receive the new target via `WirelessPower.ProcessUpdate` (flag 256) and their own local `RotatableBehaviour` then slews to it. Late-join ships full-precision target doubles via `SerializeOnJoin` / `DeserializeOnJoin`.
- On a **dedicated (headless) server**: `GameManager.GameState` is `Running` and the simulation ticks, so the server's `RotatableBehaviour` DOES run `DoMoveTask` and the server-side transform rotates. `Time.deltaTime` is valid in headless/batch mode. There is no rendering, but the transform math (and therefore `RayTransform.position` / `DishTarget.position`, which the link raycast in `PowerTransmitter.TryContactReceiver` consumes) is live server-side. This is why the vanilla link raycast can succeed on a dedicated server: the dish's current orientation is actually simulated there, not merely animated on clients. (`DoMoveTask` does guard `Parent.IsBroken` and audio on `InventoryManager.ParentHuman` proximity, which is null on a headless server, so the moving-sound branch is skipped, but the rotation stepping is not.)

`WirelessPower.UpdateAnimator()` (the `IRotatable.UpdateAnimator` impl, lines 426953-426964) likewise switches to the main thread and only pushes `TargetHorizontal` / `TargetVertical` into the BaseAnimator float parameters when a `BaseAnimator` exists; it does not itself move the dish. `SolarPanel.UpdateAnimator()` is an empty `async UniTaskVoid` (421320-421322).

## TargetHorizontal / TargetVertical are the worker-thread-safe aim writers; the slew is gated on IsBroken / CanRotate, never on power state
<!-- verified: 0.2.6403.27689 @ 2026-07-02 -->

Source: `.work/decomp/0.2.6403.27689/Assembly-CSharp.decompiled.cs` lines 217270-217316 (setters), 217405-217425 (`MoveToTarget` / `SyncTarget*`), 217434-217517 (`DoMoveTask`), 426953-426964 (`WirelessPower.UpdateAnimator`), 421320-421322 (`SolarPanel.UpdateAnimator`), 421136-421160 (`SolarPanel.Vertical` / `Horizontal` setters).

For code that needs to aim an `IRotatable` from the power tick (or any UniTask ThreadPool worker), `RotatableBehaviour.TargetHorizontal` / `TargetVertical` are the ONLY safe write surface. Everything the setter path executes synchronously on the caller thread is managed:

- `_targetVertical` / `_targetHorizontal` double stores plus the `double.IsNaN` guard (217278-217282, 217302-217306);
- the `NetworkUpdateFlags |= 256` bitor on `_parentRotatable.GetAsThing` (a managed field write, server-only);
- `MoveToTarget()` reading `_currentTask.Status` and kicking `DoMoveTask`, whose FIRST statement is the conditional `await UniTask.SwitchToMainThread()` (217436-217439), so no Unity API is touched before the hop;
- `_parentRotatable.UpdateAnimator().Forget()`, where `WirelessPower`'s implementation itself begins with the same `ThreadedManager.IsThread` main-thread switch (426955-426958) and `SolarPanel`'s is empty (421320-421322).

(The `MovementSpeedHorizontal` / `MovementSpeedVertical` getters read `Time.deltaTime`, a main-thread-only Unity API, but they are only evaluated inside `DoMoveTask`'s loop after the main-thread switch.)

By contrast, the parents' CURRENT-angle properties are main-thread-only: `WirelessPower.Horizontal` / `Vertical` setters write `AxleTransform` / `DishTransform.localRotation`, and `SolarPanel.Vertical` / `Horizontal` setters (421136-421160) call `SetArmPitch` / `SetArmYaw`, which write `PitchPivot` / `YawPivot.localRotation` on every `SolarPanelArm` (see [SolarPanel](./SolarPanel.md)). Writing those from a worker thread crashes the player.

Gating: the slew task bails or breaks only on `Parent == null`, `Parent.IsCursor`, `Parent.IsBroken`, or `!_parentRotatable.CanRotate()` (217440, 217509). There is NO `OnOff`, `Powered`, or `Error` check anywhere in `RotatableBehaviour`, and `SolarPanel.CanRotate() => !IsBroken` (421225-421228) adds none. An unpowered, switched-off solar panel or dish still slews to any target written to it. Aim writes are free; only the WRITERS of targets (IC10 chips via `SetLogicValue`, `SolarControl` motherboards, wrench interactions) need power to run. This is what makes a solar-only island unrecoverable when its panels park off-sun: the servo would happily move, but the logic that would command it is dead (see [SolarPanel](./SolarPanel.md), "Solar-only island bootstrap corollary", and [PowerTick](./PowerTick.md)).

## Verification history
<!-- verified: 0.2.6403.27689 @ 2026-07-02 -->

- 2026-07-02: re-verification and extension pass against the 0.2.6403.27689 decompile after the game update from 0.2.6228.27061 (full class now at lines 217254-217525). Confirmed unchanged with new line refs: target/current split (setters 217270/217294, `IsHorizontal`/`IsVertical` 217320-217342, `IsMoving` 217348), slew-speed formula `(180/Maximum*) * MovementSpeed* * Time.deltaTime` (217344-217346), `DoMoveTask` main-thread switch + non-Running snap + per-frame stepping (217434-217517), server-only flag-256 sync (217283-217289 / 217307-217313), `WirelessPower.RotationTolerance => 1E-07f` (426869), `WirelessPower.UpdateAnimator` main-thread switch (426953-426964). New at this version: the target setters gained a `double.IsNaN` early-return, the flag write goes through the new `IRotatable.GetAsThing` member, the loop's break calls the new `IRotatable.RunAfterAnimation()` completion hook (no-op on WirelessPower/SolarPanel; `PowerReceiver` overrides it to `RequestRetarget()` at 408179-408182), and a client-side `OnClientStart` snap helper exists (217427-217432). Added the "TargetHorizontal / TargetVertical are the worker-thread-safe aim writers" section: the setter path is managed-only up to `DoMoveTask`'s first-statement `SwitchToMainThread`, `SolarPanel.UpdateAnimator` is empty (421320-421322), the parents' Horizontal/Vertical setters are Transform writes (main-thread-only), and the slew gates on `IsBroken` / `CanRotate()` only, never on OnOff/Powered/Error. Updated the implementer-constants comparison to the 0.2.6403 SolarPanel values (RotationTolerance 0.001 at 421162, speeds 0.05 at 421170-421172). Driving work: solar-panel auto-aim from the power tick (PowerGridPlus / PowerTransmitterPlus rearchitecture session).
- 2026-04-20: page created from the Research migration; verbatim content lifted from F0058. No conflicts.
- 2026-04-25: added "Servo state and slew" section. Additive only; no existing content changed. Source: direct decompile of `RotatableBehaviour.cs` during the placement-orientation deep-research pass. Game version 0.2.6228.27061.
- 2026-05-22: added "Current vs target angle, slew-complete detection, slew speed" and "Slew runs on the main thread, only while GameState.Running, on every peer" sections. Additive only; no existing content changed. Driving question: a beam-rendering mod (PowerTransmitterPlus) needs to read the LIVE dish orientation vs the slew target, the exact slew tolerance/speed, how slew-complete is detected, and whether rotation is simulated on a dedicated server. Findings sourced from `.work/decomp/0.2.6228.27061/Assembly-CSharp.decompiled.cs`: `RotatableBehaviour.IsHorizontal`/`IsVertical` (lines 201884-201906), `IsMoving` (201912), `MovementSpeedHorizontal`/`Vertical` derived getters (201908-201910), `DoMoveTask` main-thread switch + `GameState.Running` snap + per-frame stepping (201998-202081), `TargetHorizontal`/`TargetVertical` server-only `NetworkUpdateFlags |= 256` (201847/201871); and `WirelessPower.cs`: `RotationTolerance => 1E-07f` (405531), `MovementSpeedHorizontal`/`Vertical => 0.05f` (405537/405539), `CanRotate() => true` (405554), `UpdateAnimator` (405615). Cross-referenced WirelessPower.md (servo math, flag 256 sync) and PowerTransmitter.md (Head-child drift trap, TryContactReceiver consuming RayTransform.position).

## Open questions

None at creation.
