---
title: Knock Player Unconscious
type: Workflows
created_in: 0.2.6228.27061
verified_in: 0.2.6228.27061
verified_at: 2026-04-20
sources:
  - Plans/LLM/RESEARCH.md:288-319
related:
  - ../GameSystems/DamageState.md
  - ../GameClasses/Human.md
  - ../GameClasses/Brain.md
tags: [entity, damage]
---

# Knock Player Unconscious

Transition a player to `EntityState.Unconscious` (and back) without using the sleeper. Reach for this recipe when a mod needs to induce unconsciousness directly from code, to cover the player during a time-skip, or to drive a custom sleep-like mechanic.

## When to use
<!-- verified: 0.2.6228.27061 @ 2026-04-20 -->

- A mod needs to force a player unconscious without the player being inside an `ILifeSuspender` (sleeper bed, cryo tube).
- A mod wants to wake a player back up after an induced unconscious window.
- A mod wants a gradual dimming effect matching the sleeper's behaviour rather than an instantaneous drop.

Consciousness in Stationeers is not a standalone stat. It is driven entirely by the stun damage channel on the Brain organ's DamageState. There is no separate "consciousness" float anywhere.

## Prerequisites
<!-- verified: 0.2.6228.27061 @ 2026-04-20 -->

- Server-side code path. Damage application is gated on `GameManager.RunSimulation`.
- A reference to the target `Human` entity.

Everything required is public: `Entity.State` (public get / set), `Entity.OrganBrain` (public field), `Entity.IsSleeping` (public property), `Human.OnLifeTick()` (public override), `Brain.OnLifeTick()` (public override), `EntityDamageState.Damage()` (public override), `OnServer.SetEntityState()` (public static).

## Steps
<!-- verified: 0.2.6228.27061 @ 2026-04-21 -->

Knock a player unconscious without a sleeper:

```csharp
// All public API, no reflection needed
human.DamageState.Damage(ChangeDamageType.Set, 100f, DamageUpdateType.Stun);
// OnLifeTick transitions to Unconscious next tick
```

Wake a player up:

```csharp
human.OrganBrain.DamageState.Damage(ChangeDamageType.Set, 0f, DamageUpdateType.Stun);
// OnLifeTick transitions to Alive next tick (0 < 50)
```

### Type details
<!-- verified: 0.2.6228.27061 @ 2026-04-21 -->

Source: `$(StationeersPath)\rocketstation_Data\Managed\Assembly-CSharp.dll`.

- `Assets.Scripts.Objects.ChangeDamageType` (enum): `Set, Increment, Decrement`.
- `Assets.Scripts.Objects.DamageUpdateType` (ushort, `[Flags]`): `None=0, Burn=2, Brute=4, Oxygen=8, Hydration=0x10, Radiation=0x20, Starvation=0x40, Toxic=0x80, Stun=0x100, Decay=0x200, All=ushort.MaxValue`.
- `human.DamageState` is inherited from `Thing.DamageState`. On a `Human` / `Entity` it is typed as `Assets.Scripts.Objects.EntityDamageState` (a subclass of `OrganicDamageState`), set up in `Entity.InitializeDamageState`.
- Method signature (`EntityDamageState.Damage`, override): `public override void Damage(ChangeDamageType change, float value, DamageUpdateType updateType)`.
- `EntityDamageState.Damage` auto-routes `updateType == DamageUpdateType.Stun` and `DamageUpdateType.Oxygen` to `ParentEntity.OrganBrain.DamageState.Damage(change, value, updateType)`. So `human.DamageState.Damage(Set, 100f, Stun)` and `human.OrganBrain.DamageState.Damage(Set, 100f, Stun)` both write the same Stun channel on the Brain organ's damage state. Either form is correct; the shorter form is preferred and is what vanilla `Human.ForceKnockout`-style code uses (see `Human.cs:3034`: `DamageState.Damage(ChangeDamageType.Set, DamageState.MaxDamage, DamageUpdateType.Stun);`).

Gradual consciousness loss (like the sleeper):

```csharp
human.DamageState.Damage(ChangeDamageType.Increment, 10f, DamageUpdateType.Stun);
// Repeat each tick. Player sees screen darken progressively.
// At 100 they go unconscious.
```

Keep a player unconscious without a sleeper (prevent stun decay):
Harmony postfix on `Brain.OnLifeTick()` to re-set stun after the natural decrement, or Harmony prefix to skip the decrement block entirely.

Simulate `IsSleeping` benefits without a sleeper:
Harmony postfix on `Entity.IsSleeping` getter to return true under custom conditions. This halves metabolic rates and blocks stun recovery, but does NOT skip the nutrition / dehydration ticks (that check is `RootParent is ILifeSuspender`, separate from `IsSleeping`).

Set state directly (fragile, not recommended):

```csharp
human.State = EntityState.Unconscious;
// Works but OnLifeTick immediately reverts to Alive if stun < 50
// Must also maintain stun >= 50 to keep the state
```

## Verification
<!-- verified: 0.2.6228.27061 @ 2026-04-20 -->

- Snapshot the target human's `State` and `OrganBrain.DamageState.Stun` before and after the call.
- `Human.OnLifeTick()` transitions on the next life tick once the stun threshold is crossed: at >= 50 stun the state becomes `Unconscious`; below 50 it reverts to `Alive`.

## Pitfalls
<!-- verified: 0.2.6228.27061 @ 2026-04-20 -->

- Stun recovery natural decay: `Brain.OnLifeTick()` decrements stun by 3 per life tick when stun > 0, state is Alive or Unconscious, and the entity is NOT sleeping. Halved by offline metabolism scaling for disconnected players. When `IsSleeping` is true, decay is blocked entirely. An induced unconscious state therefore wakes up on its own after enough ticks unless you maintain stun.
- `Human.OnExitInventory()` forces `EntityState.Alive` immediately when a player exits an `ILifeSuspender`, bypassing the stun < 50 check. If the target is inside a sleeper, moving them out wakes them regardless of stun.
- Setting `human.State = EntityState.Unconscious` directly is fragile: `OnLifeTick` immediately reverts to `Alive` if stun < 50, so the direct-set approach must be paired with a stun maintenance loop.
- The `IsSleeping`-postfix trick halves metabolic rates and blocks stun recovery, but does NOT skip nutrition / dehydration. Those checks depend on `RootParent is ILifeSuspender`, which is separate from `IsSleeping`.

## Verification history

- 2026-04-20: page created from the Research migration; verbatim content lifted from F0081 (`Plans/LLM/RESEARCH.md:288-319`).
- 2026-04-21: appended "Type details" sub-section with fully-qualified enum names, `EntityDamageState` typing, the `Damage` signature, and the auto-route-to-brain behaviour observed in `EntityDamageState.Damage`. Confirmed the existing claim ("calling `Damage(Set, v, Stun)` on `human.DamageState` hits the Brain organ's Stun channel") via direct read of `Assets.Scripts.Objects.EntityDamageState.Damage` which branches on `updateType == Stun || Oxygen` and forwards to `ParentEntity.OrganBrain.DamageState.Damage(change, value, updateType)`. Game version 0.2.6228.27061.

## Open questions

None.
