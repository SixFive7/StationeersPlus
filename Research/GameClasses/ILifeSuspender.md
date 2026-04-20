---
title: ILifeSuspender
type: GameClasses
created_in: 0.2.6228.27061
verified_in: 0.2.6228.27061
verified_at: 2026-04-20
sources:
  - Plans/LLM/RESEARCH.md:216-225
  - $(StationeersPath)\rocketstation_Data\Managed\Assembly-CSharp.dll :: Assets.Scripts.Objects.Electrical.ILifeSuspender
related:
  - ./Entity.md
  - ../GameSystems/StunStateMachine.md
tags: [entity, damage]
---

# ILifeSuspender

Vanilla game interface at `Assets.Scripts.Objects.Electrical.ILifeSuspender`. Marks devices that suspend a player's life tick (sleeper, cryo tube, bed). Drives `Entity.IsSleeping`.

## Interface + implementors
<!-- verified: 0.2.6228.27061 @ 2026-04-20 -->

Source: F0076.

`ILifeSuspender` interface (`Assets.Scripts.Objects.Electrical`): single property `bool IsSuspendingLife { get; }`

Implementors:
- `Sleeper`: `IsSuspendingLife => Powered;`
- `CryoTube`: `IsSuspendingLife => Powered;`
- `Bed`: `IsSuspendingLife => true;` (always active, no power needed)

`Entity.IsSleeping` is true when state is `Unconscious` AND the entity is inside a powered `ILifeSuspender`. This is the "good" unconscious (halved metabolic rates, no respawn prompt).

## Verification history
<!-- verified: 0.2.6228.27061 @ 2026-04-20 -->

- 2026-04-20: page created from the Research migration; verbatim content lifted from F0076. No conflicts.

## Open questions

None at creation.
