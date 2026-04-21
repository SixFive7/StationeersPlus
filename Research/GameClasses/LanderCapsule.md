---
title: LanderCapsule
type: GameClasses
created_in: 0.2.6228.27061
verified_in: 0.2.6228.27061
verified_at: 2026-04-20
sources:
  - Plans/LLM/RESEARCH.md:420-428
  - Plans/LLM/RESEARCH.md:438-446
  - Plans/LLM/RESEARCH.md:450-457
  - Plans/LLM/RESEARCH.md:415-418
  - Plans/LLM/RESEARCH.md:430-437
  - Plans/LLM/RESEARCH.md:459-465
  - $(StationeersPath)\rocketstation_Data\Managed\Assembly-CSharp.dll :: Assets.Scripts.Objects.LanderCapsule
related:
  - ../Workflows/TriggerLanderCapsule.md
tags: [worldgen, entity]
---

# LanderCapsule

Vanilla game class at `Assets.Scripts.Objects.LanderCapsule`. The drop pod that delivers players from orbit. Inherits from `DynamicThing`. Entirely independent of the death/respawn system.

## Independence from death/respawn
<!-- verified: 0.2.6228.27061 @ 2026-04-20 -->

Source: F0095m.

The `LanderCapsule` (`Assets.Scripts.Objects`, inherits `DynamicThing`) is the drop pod that delivers players from orbit. It is completely independent of the death/respawn system. The respawn flow uses it through XML spawn data config, but the capsule itself has no awareness of whether the player is alive, dead, or respawning.

## Key constants
<!-- verified: 0.2.6228.27061 @ 2026-04-20 -->

Source: F0087.

```
KinematicStartHeight = 100m     (teleport distance above ground)
DURATION = 10s                  (total descent time)
ENGINE_START_TIME = 3s          (engines fire at this point)
DESCENT_SHAKE = 0.03            (camera shake during freefall)
ENGINES_SHAKE = 0.2             (camera shake when engines fire)
DOOR_EJECT_FORCE = 9f           (force on door when blown off)
```

## LanderMode enum
<!-- verified: 0.2.6228.27061 @ 2026-04-20 -->

Source: F0095n.

`LanderMode` enum: `AtRest = 0` (on ground, idle), `Descending = 1` (in-flight descent), `Venting = 2` (door ejected, gas particles).

## Descent sequence
<!-- verified: 0.2.6228.27061 @ 2026-04-20 -->

Source: F0088.

Triggered by setting `InteractableType.Mode` to 1 (`Descending`):

1. `BeginDescent()`: sets `isKinematic = true`, saves ground position, teleports capsule 100m straight up, locks the door (`InteractLock = 1`), starts `ControlledDescent()` coroutine
2. `ControlledDescent()`: lerps position from start (high) to target (ground) over 10 seconds using `EaseOutQuad(t) = 1 - (1-t)^2` (fast start, decelerating end). At t > 3s: enables Activate (fires engines, starts `EntryEffects`). Sets `ControlledDescentLerp` each frame (0 to 1) which drives thruster intensity. Camera shake increases from 0.03 to 0.2.
3. `TerminateDescent()`: disables kinematic (physics resume), disables entry effects, clears camera shake, sets mode to AtRest
4. `WaitThenOpen()`: waits 3 seconds, opens capsule (`InteractOpen = 1`), waits 500ms, unlocks (`InteractLock = 0`)
5. Door ejection: door slot occupant gets `MoveToWorld` with forward force of 9. Mode changes to Venting, gas particles emit for 3 seconds

## Player experience timeline
<!-- verified: 0.2.6228.27061 @ 2026-04-20 -->

Source: F0089.

| Time | Event |
|---|---|
| 0s | Pulled into capsule seat, camera switches to interior view, teleported 100m up |
| 0-3s | Rapid freefall descent, gentle camera shake (0.03) |
| 3-10s | Engines fire, thruster effects visible, shake intensifies (0.2), descent decelerates |
| 10s | Ground contact, physics settle |
| 13s | Door blows off, gas venting particles |
| 13.5s | Lock released, player can exit |

## EntryEffects and SpaceSuitRespawn visuals
<!-- verified: 0.2.6228.27061 @ 2026-04-20 -->

Source: F0095o.

`EntryEffects` component on the capsule controls re-entry fire/thruster visuals. `EnableEffects()` / `DisableEffects()` toggle linked GameObjects. `SetIntensity(lerpFactor)` scales thruster transforms using `EaseOutQuart(lerp) * 0.7` with random jitter. `SpaceSuitRespawn` visual materialization effect: `SpawnEffectTime = 5s` fade-in, `PauseTime = 1s`, `HideEffectTime = 3s` fade-out. Animates a `_cutoff` shader property.

## Slots
<!-- verified: 0.2.6228.27061 @ 2026-04-21 -->

Source: `$(StationeersPath)\rocketstation_Data\Managed\Assembly-CSharp.dll :: Assets.Scripts.Objects.LanderCapsule`.

`LanderCapsule.Slots` is inherited from `Thing`:

```csharp
// Thing
public List<Slot> Slots;
```

`LanderCapsule` defines two fixed slot accessors:

```csharp
private Slot DoorSlot => Slots[0];
private Slot SeatSlot => Slots[1];
```

Index 0 is the door slot (the breakable door Thing occupies this). Index 1 is the seat for the human occupant. Both accessors are private, so external callers must reach into `capsule.Slots[0]` / `capsule.Slots[1]` directly.

## InteractMode
<!-- verified: 0.2.6228.27061 @ 2026-04-21 -->

Source: `$(StationeersPath)\rocketstation_Data\Managed\Assembly-CSharp.dll :: Assets.Scripts.Objects.Thing` and `LanderCapsule`.

`InteractMode` is inherited from `Thing`:

```csharp
// Thing
public Interactable InteractMode => _interactableMode;
```

It is a property returning an `Interactable` instance whose `Action == InteractableType.Mode`. `Interactable.State` is an `int` indexed over `LanderMode` (byte-backed enum): `0 = AtRest`, `1 = Descending`, `2 = Venting`.

Descent is triggered by `LanderCapsule.OnInteractableStateChanged`:

```csharp
if (GameManager.RunSimulation
    && interactable.Action == InteractableType.Mode
    && newState != oldState
    && newState == 1)
{
    BeginDescent().Forget();
}
```

Setting state 1 via `OnServer.Interact(capsule.InteractMode, 1)` is what spawns the descent sequence documented in the "Descent sequence" section above.

## Verification history

- 2026-04-20: page created from the Research migration; verbatim content lifted from F0087, F0088, F0089, F0095m, F0095n, F0095o. No conflicts.
- 2026-04-21: added "Slots" and "InteractMode" sections from direct decompile. Additive only; no existing content changed. Verified against `Assets.Scripts.Objects.LanderCapsule` in game version 0.2.6228.27061.

## Open questions

None.
