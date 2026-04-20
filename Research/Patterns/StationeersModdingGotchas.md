---
title: Stationeers modding gotchas (catch-all)
type: Patterns
created_in: 0.2.6228.27061
verified_in: 0.2.6228.27061
verified_at: 2026-04-20
sources:
  - Plans/RepairPrototype/plan.md:844-850 (F0229)
related:
  - ../GameSystems/PowerTickThreading.md
  - ./MainThreadDispatcher.md
  - ./PooledSpanEnumeration.md
tags: []
---

# Stationeers modding gotchas (catch-all)

Small list of recurring pitfalls that don't have their own page. Add new entries as they surface; promote to a dedicated page once an entry has three or more distinct expressions.

## Gotchas
<!-- verified: 0.2.6228.27061 @ 2026-04-20 -->

F0229 (Plans/RepairPrototype/plan.md:844-850):

- `Device.AllDevices` can contain duplicates (use HashSet to dedup).
- `AtmosphericsManager.AllAtmospheres` can contain nulls (must filter).
- Power calculations can produce `NaN` (guard against this).
- Power tick runs on background thread; can't use `UnityEngine.Random`.
- Atmosphere may be null until a player logs in on dedicated servers.

### Context per entry

- **Device.AllDevices duplicates.** Iterating to collect unique devices requires dedup. A `HashSet<Device>` over the enumeration is the simplest fix.
- **AtmosphericsManager.AllAtmospheres nulls.** The collection can hold null slots; filter with a null check before dereference.
- **NaN in power calculations.** When distance-cost or power-draw math produces NaN (divide by zero, sqrt of negative), subsequent comparisons silently behave unexpectedly (NaN compares false to everything). Guard with `!double.IsNaN(value)`.
- **PowerTick is on a background thread.** `UnityEngine.Random` is main-thread-only. Use `System.Random` (thread-safe with the right constructor) or dispatch to the main thread via `./MainThreadDispatcher.md`. See also `../GameSystems/PowerTickThreading.md`.
- **Dedicated-server atmosphere timing.** `Atmosphere` may be null until the first player logs in; guard early-startup code.

## Verification history
<!-- verified: 0.2.6228.27061 @ 2026-04-20 -->

- 2026-04-20: page created from the Research migration; single source (F0229) compiled from the RepairPrototype plan.

## Open questions

None at creation.
