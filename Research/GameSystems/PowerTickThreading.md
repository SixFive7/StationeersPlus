---
title: PowerTickThreading
type: GameSystems
created_in: 0.2.6228.27061
verified_in: 0.2.6228.27061
verified_at: 2026-04-20
sources:
  - Mods/PowerTransmitterPlus/RESEARCH.md:43-49
  - Mods/PowerTransmitterPlus/RESEARCH.md:596-605
  - Mods/PowerTransmitterPlus/PowerTransmitterPlus/MainThreadDispatcher.cs:7-15
  - Mods/PowerTransmitterPlus/PowerTransmitterPlus/VisualiserPatches.cs:7-11
related:
  - ../GameClasses/PowerTransmitter.md
  - ../GameClasses/WirelessPower.md
  - ../Patterns/MainThreadDispatcher.md
tags: [power, threading]
---

# PowerTickThreading

The game's power-tick simulation runs on a UniTask ThreadPool worker. Any Harmony patch on `PowerTick`-adjacent methods (`UsePower`, `GetUsedPower`, `ReceivePower`, `GetGeneratedPower`, `VisualizerIntensity` setter) inherits that thread. Unity API calls from those threads hard-crash the player; a `MainThreadDispatcher` is required to bridge writes back to the main thread.

## ThreadPool worker crash pattern
<!-- verified: 0.2.6228.27061 @ 2026-04-20 -->

`PowerTick.ApplyState()` runs on a UniTask **ThreadPool worker** thread. Methods called from there (`UsePower`, `GetUsedPower`, `ReceivePower`, `GetGeneratedPower`, `VisualizerIntensity` setter) all execute on a background thread. **Any Unity API call from those threads, `new GameObject`, `Shader.Find`, `Transform.position`, `LineRenderer.SetPosition`, `Material.SetXxx`, hard-crashes the native Unity player.**

`MainThreadDispatcher` is a `MonoBehaviour` on a `DontDestroyOnLoad` GameObject. It maintains a `ConcurrentQueue<Action>` drained in `Update()`. Every Harmony postfix that touches Unity API enqueues onto this dispatcher. Closure runs on main thread one frame later. ~1 frame latency, fully safe.

Field reads/writes (managed memory, no Unity P/Invoke) ARE safe from background threads. That's why the `_powerProvided` reflection in `DistanceCostPatches` works without the dispatcher.

## Representative crash stack
<!-- verified: 0.2.6228.27061 @ 2026-04-20 -->

```
Shader.Find → BeamManager.SharedMaterial → BeamLine.ctor → ...
  ← VisualizerIntensitySetterPatch.Postfix
  ← PowerTransmitter.ReceivePower
  ← PowerTick.ConsumePower / ApplyState
  ← CableNetwork.OnPowerTick
  ← Cysharp.Threading.Tasks.SwitchToThreadPoolAwaitable
```

## VisualizerIntensity as single source of truth
<!-- verified: 0.2.6228.27061 @ 2026-04-20 -->

From `VisualiserPatches.cs`:

```
Single source of truth: the VisualizerIntensity setter on WirelessPower.
Vanilla's Activate/SetMaterialPropertiesForIntensity both flow through this
value, so observing it gives us correct on/off AND the current alpha /
power-level. Fires from a ThreadPool worker during PowerTick; BeamManager
routes everything to the main thread.
```

## MainThreadDispatcher class-header rationale
<!-- verified: 0.2.6228.27061 @ 2026-04-20 -->

From `MainThreadDispatcher.cs`:

```
// Stationeers drives power-tick code (PowerTick.ApplyState -> ReceivePower ->
// VisualizerIntensity setter) on a ThreadPool worker via UniTask's
// SwitchToThreadPoolAwaitable. Our Harmony postfixes inherit that thread,
// so any call to a Unity API (new GameObject, Shader.Find, Transform.position,
// LineRenderer.SetPosition) hard-crashes the native Unity player.
//
// This dispatcher parks a queue on a DontDestroyOnLoad GameObject, drained
// in Update() on the main thread. Patches enqueue closures from any thread,
// the closure body runs safely on the main thread one frame later.
```

## Verification history
<!-- verified: 0.2.6228.27061 @ 2026-04-20 -->

- 2026-04-20: page created from the Research migration; verbatim content lifted from F0032 (primary), F0048, F0308, and F0364.

## Open questions

None at creation.
