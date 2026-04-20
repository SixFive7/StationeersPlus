---
title: Periodic processing options
type: Patterns
created_in: 0.2.6228.27061
verified_in: 0.2.6228.27061
verified_at: 2026-04-20
sources:
  - Plans/RepairPrototype/plan.md:432-450 (F0229g)
related: []
tags: []
---

# Periodic processing options

When a mod needs to do work every N seconds (auto-repair ticks, background scans, status polling), three mechanisms are available. Pick by coupling and frequency: patch-based for per-thing loops, plugin Update() for low-frequency global work, coroutine for simple fixed-interval polls.

## Options
<!-- verified: 0.2.6228.27061 @ 2026-04-20 -->

F0229g (Plans/RepairPrototype/plan.md:432-450):

Options:
1. Patch `DynamicThing.Update()`; runs per frame per thing (PerishableItems uses this).
2. Timer in plugin's `Update()`:
    ```csharp
    timer += Time.deltaTime;
    if (timer >= 5.0f) { timer = 0f; DoWork(); }
    ```
3. Coroutine with `yield return new WaitForSeconds(5f)`.

### Trade-offs

- **Option 1 (patch `DynamicThing.Update`)**: Per-frame, per-thing. High overhead. Only appropriate when the work is genuinely per-thing and cheap, and when hooking a more specific method does not suffice. PerishableItems uses this to decay food items.
- **Option 2 (plugin Update + timer)**: One timer, one `DoWork()` call every N seconds. Global work; cheap. Requires `Time.deltaTime` which is Unity main-thread-only.
- **Option 3 (coroutine + `WaitForSeconds`)**: Simplest to write. Owned by a `MonoBehaviour`. Coroutines pause on scene unload; if the work must survive main-menu exits, park the coroutine on a `DontDestroyOnLoad` GameObject.

### When none of the three fit

If the work must run on a background thread (not Unity main thread), neither Option 2 nor Option 3 are correct; they run main-thread. Dispatch from a background worker via `./MainThreadDispatcher.md` for the portions that touch Unity API, and do the background math on the worker directly.

## Verification history
<!-- verified: 0.2.6228.27061 @ 2026-04-20 -->

- 2026-04-20: page created from the Research migration; single source (F0229g).

## Open questions

None at creation.
