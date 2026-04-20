---
title: Heal All Damaged Things
type: Workflows
created_in: 0.2.6228.27061
verified_in: 0.2.6228.27061
verified_at: 2026-04-20
sources:
  - Plans/LLM/RESEARCH.md:394-411
related:
  - ../GameSystems/DamageState.md
  - ../GameClasses/Thing.md
tags: [damage, save-edit]
---

# Heal All Damaged Things

Iterate every `Thing` currently alive in the scene and call `DamageState.HealAll()` on each damaged, repairable instance. Reach for this recipe when a mod needs a bulk repair pass, a time-skip cover operation, or a "service technician" event that resets wear / decay / burn / brute damage across the world.

## When to use
<!-- verified: 0.2.6228.27061 @ 2026-04-20 -->

- A mod wants to clear accumulated minor damage across every structure and item in a single pass.
- A time-skip cover effect wants to make the world look "cleaned up" after the skip resolves.
- A console command or admin tool wants a one-shot repair-the-world action.

## Prerequisites
<!-- verified: 0.2.6228.27061 @ 2026-04-20 -->

- Server-side code path. `HealAll()` internally calls `Damage()` which checks `GameManager.RunSimulation`.
- A stable moment in the main loop (iterate on the main thread; reflection-based iteration of world state off the main thread is unsafe).

## Steps
<!-- verified: 0.2.6228.27061 @ 2026-04-20 -->

```csharp
using PooledSpan<Thing> span = OcclusionManager.AllThings.AsPooledSpan();
foreach (Thing thing in span.Collection)
{
    if (thing == null) continue;
    if (thing.DamageState.Indestructable) continue;
    if (thing.IsBroken) continue;              // skip destroyed
    if (thing.DamageState.Total <= 0f) continue; // skip undamaged
    thing.DamageState.HealAll();
}
```

`PooledSpan` rents from `ArrayPool`, so the collection may contain trailing nulls. Always null-check.

## Verification
<!-- verified: 0.2.6228.27061 @ 2026-04-20 -->

- Snapshot a representative cross-section of Things before and after: pick a few damaged `Structure` instances, a few damaged `Item` instances, and any known-broken entries.
- Confirm `DamageState.Total` is zero on each previously damaged Thing after the pass.
- Confirm `Indestructable` Things and `IsBroken` Things retained their prior values (the loop skips them).

## Caveats
<!-- verified: 0.2.6228.27061 @ 2026-04-20 -->

- Server-side only. `HealAll()` internally calls `Damage()` which checks `GameManager.RunSimulation`.
- Destroyed items (`IsBroken`): Total >= MaxDamage. Healing resets the numbers but doesn't rebuild structures in broken mesh state. Skip them.
- Decay extra flag: `Item.IsDecayed` is a separate bool. `HealAll()` zeros decay damage but does NOT reset `IsDecayed`. Set `((Item)thing).IsDecayed = false` separately to un-decay items.
- Structures with broken meshes: `CurrentBuildStateIndex < 0`. Need rebuilding, not healing. `Structure.IsBroken` catches these.

## Verification history
<!-- verified: 0.2.6228.27061 @ 2026-04-20 -->

- 2026-04-20: page created from the Research migration; verbatim content lifted from F0086 and F0095l (`Plans/LLM/RESEARCH.md:394-411`).

## Open questions

None at creation.
