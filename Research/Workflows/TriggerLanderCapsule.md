---
title: Trigger Lander Capsule
type: Workflows
created_in: 0.2.6228.27061
verified_in: 0.2.6228.27061
verified_at: 2026-04-20
sources:
  - Plans/LLM/RESEARCH.md:471-491
related:
  - ../GameClasses/LanderCapsule.md
  - ../GameClasses/Human.md
  - TimeSkipWorldManipulation.md
tags: [worldgen, entity, timeskip]
---

# Trigger Lander Capsule

Spawn a `LanderCapsule` under a living player, move the player into it, and trigger the descent animation without going through the death / respawn flow. Reach for this recipe when a mod wants a cinematic "the player just arrived" effect, or a time-skip cover window that locks the player's camera inside a capsule while world state mutates.

## When to use
<!-- verified: 0.2.6228.27061 @ 2026-04-20 -->

- A mod wants to drop a capsule on a living player for a time-skip cover window.
- A mod wants to re-create the "just arrived" cinematic without the expensive full respawn that rebuilds organs and empties inventory.
- A scripted event wants to time a world mutation behind a ~13.5 second capsule sequence.

The `LanderCapsule` is completely independent of the death / respawn system. The respawn flow uses it through XML spawn data config, but the capsule itself has no awareness of whether the player is alive, dead, or respawning.

## Prerequisites
<!-- verified: 0.2.6228.27061 @ 2026-04-20 -->

- Server-side code path (`OnServer.Create` / `OnServer.MoveToSlot` / `OnServer.Interact`).
- A live `Human` reference with a valid `ThingTransform`.
- `Prefab.Find<LanderCapsule>()` resolves to the capsule prefab (vanilla registers it at load).

## Steps
<!-- verified: 0.2.6228.27061 @ 2026-04-20 -->

```csharp
// Create capsule at player's position
var pos = human.ThingTransform.position;
var rot = human.ThingTransform.rotation;
var capsule = OnServer.Create<LanderCapsule>(Prefab.Find<LanderCapsule>(), pos, rot);

// Move player into the seat (Slots[1])
OnServer.MoveToSlot(human, capsule.Slots[1]);

// Trigger descent (capsule teleports 100m up and drops back)
OnServer.Interact(capsule.InteractMode, 1);
```

Three calls. The capsule handles everything else.

## Using as time-skip cover
<!-- verified: 0.2.6228.27061 @ 2026-04-20 -->

Players are locked inside the capsule for ~13.5 seconds (descent + door open + unlock). During this window:

- Players can look around (`FreeLook = true`) but cannot exit or interact.
- World changes can be made: repair items, advance sun, drain hunger, spawn debris.
- The 13-second window can be extended by patching `WaitThenOpen()` to delay longer than 3 seconds, or by combining with stun (knock them to ~80 stun inside the capsule for a groggy descent, wake-up takes additional seconds).

## Verification
<!-- verified: 0.2.6228.27061 @ 2026-04-20 -->

- Snapshot the target `Human` before and after the three calls: the `ParentSlot` should reference `capsule.Slots[1]` after the second call.
- Observe the descent animation in-game; the capsule teleports 100m up and drops back down.
- Confirm no new `Human` was created: the full respawn flow (`Human.CreateCharacter()`) resets all stats, creates new organs, empties inventory (old items go into a `CardboardBox` on the ground), and the old body becomes a `DynamicBodyBag`. None of this happens when you just create a `LanderCapsule` and move a living player into it.

## Pitfalls
<!-- verified: 0.2.6228.27061 @ 2026-04-20 -->

- Client-side paths will silently no-op or desync; guard on `GameManager.IsServer`.
- The 13.5 second window is a soft figure from observed behaviour (descent + door open + unlock). It is not a precise API-exposed constant; plan any world-mutation work to fit within that window rather than assume it is exact.
- Moving a player into the capsule's `Slots[1]` locks their interaction input. If a mod wants the player to stay inside longer, patch `WaitThenOpen()` rather than repeatedly re-moving them into the slot.

## Verification history
<!-- verified: 0.2.6228.27061 @ 2026-04-20 -->

- 2026-04-20: page created from the Research migration; verbatim content lifted from F0090 and F0095p (`Plans/LLM/RESEARCH.md:471-491`).

## Open questions

None at creation.
