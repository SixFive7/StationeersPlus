---
title: StunStateMachine
type: GameSystems
created_in: 0.2.6228.27061
verified_in: 0.2.6228.27061
verified_at: 2026-04-20
sources:
  - Plans/LLM/RESEARCH.md:187-198
  - Plans/LLM/RESEARCH.md:200-214
  - Plans/LLM/RESEARCH.md:227-234
  - Plans/LLM/RESEARCH.md:243-254
  - Plans/LLM/RESEARCH.md:273-284
  - Plans/LLM/RESEARCH.md:182-185
  - Plans/LLM/RESEARCH.md:236-240
  - Plans/LLM/RESEARCH.md:256-260
  - Plans/LLM/RESEARCH.md:322-333
related:
  - ./DamageState.md
  - ./RespawnFlow.md
  - ../GameClasses/Human.md
  - ../GameClasses/Entity.md
  - ../GameClasses/ILifeSuspender.md
  - ../Workflows/KnockPlayerUnconscious.md
tags: [entity, damage]
---

# StunStateMachine

Consciousness in Stationeers is not a standalone stat. It is driven entirely by the stun damage channel on the Brain organ's `DamageState`. This page covers the transitions, the proxy chain from `Entity.DamageState.Stun` down to `OrganBrain.DamageState._stunDamage`, the sleeper mechanism, the full inventory of stun-damage sources, the natural-decay rule, the sleep-metabolism modifiers, and the public-member visibility reference used to drive stun programmatically.

## Consciousness source of truth
<!-- verified: 0.2.6228.27061 @ 2026-04-20 -->

Consciousness in Stationeers is not a standalone stat. It is driven entirely by the **stun damage channel** on the **Brain organ's DamageState**. There is no separate "consciousness" float anywhere.

## State transitions (Alive / Unconscious)
<!-- verified: 0.2.6228.27061 @ 2026-04-20 -->

State transitions live in `Human.OnLifeTick()`:

- **Alive -> Unconscious**: when `OrganBrain.DamageState.Stun >= 100`, or `>= 90` if inside a powered `ILifeSuspender`
- **Unconscious -> Alive**: when `OrganBrain.DamageState.Stun < 50`
- Hysteresis band 50-99: player stays in whichever state they were already in

## Proxy chain (where stun actually lives)
<!-- verified: 0.2.6228.27061 @ 2026-04-20 -->

The indirection chain:

1. `Entity.DamageState` is an `EntityDamageState`
2. `EntityDamageState.Stun` is a **proxy** that reads from `ParentEntity.OrganBrain.DamageState.Stun`
3. `EntityDamageState.Damage()` with `DamageUpdateType.Stun` **forwards** to `ParentEntity.OrganBrain.DamageState.Damage()`
4. `Brain.DamageState` is an `OrganicDamageState` containing `_stunDamage` (`ThingDamageValue`)
5. `ThingDamageValue` stores a clamped float `Value` in range [0, MaxDamage]. MaxDamage defaults to 200

The real stun value is at: `human.OrganBrain.DamageState.Stun`

Both paths work for writing:
- Direct: `human.OrganBrain.DamageState.Damage(ChangeDamageType.Set, 100f, DamageUpdateType.Stun)`
- Via entity (auto-forwards): `human.DamageState.Damage(ChangeDamageType.Set, 100f, DamageUpdateType.Stun)`

## Sleeper mechanism
<!-- verified: 0.2.6228.27061 @ 2026-04-20 -->

The sleeper itself does not set entity state. The mechanism:

1. `SpawnPointAtmospherics.OnAtmosphericTick()`: when powered and player is inside, increments stun by 10 per atmospheric tick (or snaps to 100 if close enough)
2. Once stun reaches 90, `Human.OnLifeTick()` transitions to `EntityState.Unconscious` (threshold is 90 inside an `ILifeSuspender`, not 100)
3. `Brain.OnLifeTick()`: when `IsSleeping` is true, stun does NOT naturally decay. The `!flag3` (where flag3 = IsSleeping) check prevents the decrement
4. Player stays asleep indefinitely until removed from the sleeper

## Human.OnExitInventory forces Alive
<!-- verified: 0.2.6228.27061 @ 2026-04-20 -->

`Human.OnExitInventory()`: exiting an `ILifeSuspender` forces `EntityState.Alive` immediately, bypassing the stun < 50 check. Natural wake-up: stun decays at 3 per life tick when not sleeping. Once it drops below 50, `Human.OnLifeTick()` transitions to Alive.

## Natural stun decay
<!-- verified: 0.2.6228.27061 @ 2026-04-20 -->

`Brain.OnLifeTick()`, non-robot path: when stun > 0, state is Alive or Unconscious, and NOT sleeping, stun decrements by 3 per life tick. Halved by offline metabolism scaling for disconnected players. When `IsSleeping` is true, decay is blocked entirely.

## All sources of stun damage
<!-- verified: 0.2.6228.27061 @ 2026-04-20 -->

| Source | Location | Mechanism | Amount |
|---|---|---|---|
| Sleeper / CryoTube | `SpawnPointAtmospherics.OnAtmosphericTick` | Increment per atmo tick when powered | +10/tick |
| Oxygen deprivation | `Brain.OnLifeTick` | When `Oxygenation <= 0` | +3/tick (scaled by offline metabolism) |
| Nitrous oxide (N2O) | `Entity.OnLifeTick` | `PartialPressureNitrousOxide / 5` (or `/16` with suit) | Variable, applied when > 1 |
| Robot no battery | `Brain.OnLifeTick` | Per life tick when robot battery empty | +3 (scaled) |
| Collision/impact | `Human.OnCollisionEnter` | Through suit `BruteDamagePassthroughAsStun` (default 4x multiplier) | Variable |
| Explosion | `Explosion.Process` | Direct stun increment | = explosion force |
| Stun pill | `StunPill.OnUseSecondary` | Direct set | +1000 (instant KO) |
| CryoTube revive | `CryoTube.ReviveOccupant` | Sets stun to MaxDamage on revive | Set to 200 |
| Entity death | `Human.OnEntityDeath` | Stun set to max | Set to MaxDamage |

## IsSleeping metabolism effects
<!-- verified: 0.2.6228.27061 @ 2026-04-20 -->

When `IsSleeping` is true:
- Brain damage rate halved (0.5x multiplier)
- Dehydration damage rate: 0.033 instead of 0.1
- Nutrition damage rate: 0.033 instead of 0.1
- Hydration loss halved
- Nutrition loss halved
- Stun does NOT decay (keeps player asleep)
- No respawn prompt shown

When inside a powered `ILifeSuspender` (separate check from `IsSleeping`):
- Nutrition/dehydration/mood/hygiene ticks skipped entirely

## Key member visibility reference
<!-- verified: 0.2.6228.27061 @ 2026-04-20 -->

Member visibility table: `Entity.State` (public get/set), `Entity.OrganBrain` (public field), `Entity.IsSleeping` (public property), `Human.OnLifeTick()` (public override), `Brain.OnLifeTick()` (public override), `EntityDamageState.Damage()` (public override), `OnServer.SetEntityState()` (public static). Everything needed is public.

## Verification history
<!-- verified: 0.2.6228.27061 @ 2026-04-20 -->

- 2026-04-20: page created from the Research migration; verbatim content lifted from F0074, F0075, F0077, F0078, F0080, F0095h, F0095i, F0095j, F0095k.

## Open questions

None at creation.
