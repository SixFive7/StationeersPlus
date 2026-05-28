---
title: PowerTickThreading
type: GameSystems
created_in: 0.2.6228.27061
verified_in: 0.2.6228.27061
verified_at: 2026-05-28
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

## Tick interval and the watts-vs-joules-per-tick labelling convention
<!-- verified: 0.2.6228.27061 @ 2026-05-28 -->

The power tick fires once per `GameTick`. From the decompile:

```csharp
// line 188884
private static readonly int DefaultTickSpeedMs = 500;

// line 189061
public static int GameTickSpeedMs => DefaultTickSpeedMs;

// line 189063
public static float GameTickSpeedSeconds => (float)GameTickSpeedMs / 1000f;
```

`GameTick` is the only loop that drives `ElectricityManager.ElectricityTick()` (decompile line 189484), and `ElectricityTick` iterates every `CableNetwork.OnPowerTick()` which in turn runs `PowerTick.Initialise` / `CalculateState` / `ApplyState`. So the power-tick interval is exactly `GameTickSpeedMs = 500 ms = 0.5 s`, i.e. 2 Hz.

The 500 ms interval has a consequence for units that is worth stating once, because the game itself is inconsistent about it:

- **`Battery.PowerStored`, `BatteryCell.PowerStored`, and `Battery.PowerMaximum` are in Joules.** Stationpedia text labels these as "watts" ("Able to store up to 3600000 watts of power" for `StructureBattery`), but the underlying field is energy. A 3,600,000 unit Station Battery holds 3,600,000 J = 1 kWh. The Stationpedia label is wrong; the numeric value is right as Joules.
- **`AreaPowerControl.BatteryChargeRate = 1000f` is a per-tick Joules cap.** Its tooltip labels it "How many watts are used to charge the battery?" but the code applies it as a one-tick budget: `Mathf.Min(BatteryChargeRate, Battery.PowerDelta)` is added directly to `Battery.PowerStored` (decompile lines 369975, 369995). The field is therefore J/tick, not W.
- **`GetUsedPower(network)` and `GetGeneratedPower(network)` return values that the network sums into `Required` / `Potential` and that flow through `ConsumePower` / `ApplyPower` straight into `ReceivePower(net, num)` / `UsePower(net, num)`, where they are again added directly to `PowerStored` or to `_powerProvided`.** These are also J/tick all the way through. A heater whose `UsedPower` is 500 contributes 500 J/tick to its network's `Required` and consumes 500 J/tick of stored battery energy.
- **Stationpedia / device tooltips show the raw field value labelled as W.** A device with `UsedPower = 500` displays as "500 W" in the player UI even though the per-tick cost is 500 J and the actual wall-clock power draw is 500 / 0.5 = 1000 W.

Net consequence: the entire in-game economy treats "watts" and "joules per tick" as the same number throughout. Player observations like "the APC only charges when downstream load is below 1 kW" track the in-game label (1000 = `BatteryChargeRate`) rather than the strict-physics wattage (which would be 2 kW). When reasoning about absolute energy flow over wall-clock seconds, multiply field values by 2 to get true Watts; when reasoning about in-game balance / what the player sees, use the field value directly.

This page is the canonical place for that convention. Other pages that quote numbers (`Battery.md`, `AreaPowerControl.md`, settings docs) should cite the field value as-is and link here rather than doing the doubling inline, to avoid two parallel number systems.

## Verification history
<!-- verified: 0.2.6228.27061 @ 2026-05-28 -->

- 2026-05-28: added "Tick interval and the watts-vs-joules-per-tick labelling convention" section. Documents `DefaultTickSpeedMs = 500` (decompile line 188884), the call-chain `GameTick -> ElectricityManager.ElectricityTick -> CableNetwork.OnPowerTick -> PowerTick.Initialise/CalculateState/ApplyState` (line 189484), and the in-game convention that "watts" labels and "joules per tick" values are the same number numerically even though they differ by a factor of 2 in real units. This corrects a sloppy "J/tick x 2 = W" formula previously used in `Battery.md`'s rate-cap table; the in-game-displayed wattage is the field value as-is, not doubled.
- 2026-04-20: page created from the Research migration; verbatim content lifted from F0032 (primary), F0048, F0308, and F0364.

## Open questions

None at creation.
