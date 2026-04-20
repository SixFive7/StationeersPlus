---
title: PooledSpan enumeration with trailing-null filter
type: Patterns
created_in: 0.2.6228.27061
verified_in: 0.2.6228.27061
verified_at: 2026-04-20
sources:
  - Plans/LLM/RESEARCH.md:381-390 (F0085, primary)
  - Plans/RepairPrototype/plan.md:383-390 (F0229e)
related: []
tags: []
---

# PooledSpan enumeration with trailing-null filter

Stationeers exposes world collections through `PooledSpan<T>` rentals backed by `ArrayPool`. The rented array can be larger than the actual count, leaving trailing nulls the enumeration must filter. Skipping the null check crashes the first time the pool hands out an oversized buffer.

## Problem
<!-- verified: 0.2.6228.27061 @ 2026-04-20 -->

F0085 (Plans/LLM/RESEARCH.md:381-390, primary):

```csharp
using PooledSpan<Thing> span = OcclusionManager.AllThings.AsPooledSpan();
foreach (Thing thing in span.Collection)
{
    if (thing == null) continue;
    // ...
}
```

> `PooledSpan` rents from `ArrayPool`, so the collection may contain trailing nulls. Always null-check.

The `foreach` exposes the backing array, not a logically-sized wrapper. Indices beyond the populated range hold nulls (or stale references from a previous rental). Iterating without the null check crashes on the first trailing slot.

## Solution / recipe
<!-- verified: 0.2.6228.27061 @ 2026-04-20 -->

Standard enumeration pattern:

```csharp
using PooledSpan<Thing> span = OcclusionManager.AllThings.AsPooledSpan();
foreach (Thing thing in span.Collection)
{
    if (thing == null) continue;
    // real work
}
```

The `using` disposes the span (returns the buffer to the pool) deterministically. Do not keep references to `span.Collection` beyond the `using` block; the pool reuses the array.

### Alternative collections

F0229e (Plans/RepairPrototype/plan.md:383-390) enumerates the static collections this pattern applies to:

> Iterating Things: `Thing.AllThings` static list of ALL things in the world; `Structure.AllStructures` all structures; `Device.AllDevices` all devices; `Thing.TryFind(referenceId, out var thing)` find by ID.

`Thing.AllThings` is a different shape (static list, not `PooledSpan`) but the null-check discipline still applies on any Stationeers collection until the exact shape is confirmed. `Device.AllDevices` specifically can contain duplicates (see `./StationeersModdingGotchas.md`).

### Common anti-pattern

```csharp
// WRONG: buffer may contain trailing nulls from ArrayPool
foreach (var thing in span.Collection)
{
    thing.DoWork();  // NullReferenceException on first trailing slot
}
```

The `using` is critical: without it the buffer leaks back to the pool never, which causes allocation growth over time.

## Cited verifications
<!-- verified: 0.2.6228.27061 @ 2026-04-20 -->

- F0085: primary code sample and trailing-null rule.
- F0229e: alternative iteration entry points (Thing / Structure / Device / TryFind) documented in the RepairPrototype plan.

## Verification history
<!-- verified: 0.2.6228.27061 @ 2026-04-20 -->

- 2026-04-20: page created from the Research migration; F0085 primary, F0229e additional.

## Open questions

None at creation.
