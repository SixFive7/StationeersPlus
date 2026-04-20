---
title: ConditionalWeakTable per-instance cache
type: Patterns
created_in: 0.2.6228.27061
verified_in: 0.2.6228.27061
verified_at: 2026-04-20
sources:
  - Mods/PowerTransmitterPlus/RESEARCH.md:211-239 (F0044, primary)
  - Plans/StationpediaPlus/PLAN.md:3708-3721 (F0219y)
related:
  - ../GameClasses/PowerTransmitter.md
  - ../GameClasses/RotatableBehaviour.md
tags: [unity]
---

# ConditionalWeakTable per-instance cache

`ConditionalWeakTable<TKey, TValue>` stores a per-instance value keyed weakly on `TKey`. When the key is GC'd, the entry is removed automatically; no manual cleanup code. Ideal for "one piece of state per Thing instance" mod caches whose lifetime should match the underlying instance.

## Problem
<!-- verified: 0.2.6228.27061 @ 2026-04-20 -->

Mods that attach state to specific game objects ("the dish currently auto-aiming at target X") need somewhere to store it. Options:

- Patch the class to add a field: invasive, conflicts with other mods.
- `Dictionary<TKey, TValue>` keyed by `ReferenceId`: survives the Thing's destruction, leaks forever.
- `Dictionary<Thing, TValue>`: holds a strong reference to the Thing, preventing collection.
- `ConditionalWeakTable<Thing, TValue>`: weak-keyed, entry cleaned automatically on GC.

## Solution / recipe
<!-- verified: 0.2.6228.27061 @ 2026-04-20 -->

F0044 (Mods/PowerTransmitterPlus/RESEARCH.md:211-239, primary; auto-aim implementation):

> `AutoAimState` (static):
> - `ConditionalWeakTable<WirelessPower, StrongBox<long>> _target`: per-dish cache. Lifetime-tied to the dish instance, self-cleans on GC.
> - `[ThreadStatic] bool _suppressReset`: re-entry flag set during our own servo writes.
> - `HandleWrite(dish, newId)`:
>   - Cache hit -> early return.
>   - `newId == 0` -> cache 0, no aim change.
>   - `Thing.Find(newId) == null || target == dish` -> return WITHOUT updating cache (so a later rewrite of the same id re-attempts lookup).
>   - Otherwise: pivot-to-pivot geometry -> set `RotatableBehaviour.TargetHorizontal` / `TargetVertical` under suppression flag.

F0219y (Plans/StationpediaPlus/PLAN.md:3708-3721) summarizes the pattern:

> PowerTransmitterPlus's `AutoAimPatches` stores per-dish target state in a `ConditionalWeakTable<WirelessPower, StrongBox<long>>`. GC-tied cleanup: when a `WirelessPower` instance is collected, its cache entry is automatically reclaimed. No manual lifecycle management. Cache entries are set by `MicrowaveAutoAimTarget` writes and cleared by manual Horizontal/Vertical adjustments via postfixes on `RotatableBehaviour` setters (with a re-entry flag to suppress clearing during auto-aim's own writes).

### Value-type wrapping

`ConditionalWeakTable<TKey, TValue>` requires `TValue` to be a reference type. For primitive payloads (`long`, `int`), wrap in `StrongBox<T>`:

```csharp
private static readonly ConditionalWeakTable<WirelessPower, StrongBox<long>> _target =
    new ConditionalWeakTable<WirelessPower, StrongBox<long>>();

// Write:
_target.AddOrUpdate(dish, new StrongBox<long>(newId));

// Read:
if (_target.TryGetValue(dish, out var box)) { var id = box.Value; }
```

The `StrongBox<T>` is a managed object; the weak key still lets the dish be collected.

### Re-entry suppression

F0044 also documents the `[ThreadStatic] bool _suppressReset` companion: when the mod's own logic writes to `RotatableBehaviour.TargetHorizontal` / `TargetVertical`, a postfix on that setter clears the cache. Without a suppression flag, the mod's own write triggers cache-clear immediately after it was set. The pattern applies generally whenever the cache is maintained by patching the same setters the mod itself calls.

## Cited verifications
<!-- verified: 0.2.6228.27061 @ 2026-04-20 -->

- F0044: primary source with full implementation context (auto-aim in PowerTransmitterPlus).
- F0219y: Stationpedia-Plus's summary cite, independently confirming the pattern.

## Verification history
<!-- verified: 0.2.6228.27061 @ 2026-04-20 -->

- 2026-04-20: page created from the Research migration; F0044 primary, F0219y cited summary.

## Open questions

None at creation.
