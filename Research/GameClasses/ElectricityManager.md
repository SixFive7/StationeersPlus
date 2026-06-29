---
title: ElectricityManager
type: GameClasses
created_in: 0.2.6228.27061
verified_in: 0.2.6228.27061
verified_at: 2026-06-29
sources:
  - $(StationeersPath)\rocketstation_Data\Managed\Assembly-CSharp.dll :: Assets.Scripts.Networks.ElectricityManager
  - .work/decomp/0.2.6228.27061/Assembly-CSharp.decompiled.cs :: lines 254818-254964 (ElectricityManager), 254905 (ElectricityTick), 254830-254840 (AllPoweredThings pool + action delegates), 253430 (AllCableNetworks pool), 212399-212412 (DensePool.ForEach), 371855-371886 (CircuitHolders.Execute)
related:
  - ./CableNetwork.md
  - ./PowerTick.md
  - ./Device.md
  - ../GameSystems/PowerTickThreading.md
  - ../GameSystems/IC10ExecutionTick.md
tags: [power, threading, network]
---

# ElectricityManager

Vanilla power-simulation driver. `Assets.Scripts.Networks.ElectricityManager : ThreadedManager` (decompile line 254818). The single entry point for one whole power tick: `ElectricityManager.ElectricityTick()` (line 254905) runs every game tick (the 500 ms / 2 Hz `GameManager` simulation tick, the same cadence that drives IC10; see [IC10ExecutionTick](../GameSystems/IC10ExecutionTick.md)) and does three things in fixed order: tick every `CableNetwork`, fire every `IPowered.OnPowerTick`, then execute every IC10 circuit.

This page covers the top-level orchestration and the cross-network propagation-order consequence. The per-network arithmetic (supply/demand summation, brownout, fuse/cable burn, provider settle) lives on [PowerTick](./PowerTick.md); the per-network book-keeping and load mirrors live on [CableNetwork](./CableNetwork.md).

## ElectricityTick: three phases in fixed order
<!-- verified: 0.2.6228.27061 @ 2026-06-29 -->

`ElectricityTick` is `static`, gated on `GameManager.RunSimulation` (host / single-player only; clients do not run it), and wraps the whole body in a try/catch that logs to the console rather than letting a power-tick exception kill the frame. Verbatim (decompile lines 254905-254931):

```csharp
public static void ElectricityTick()
{
    if (!GameManager.RunSimulation)
    {
        return;
    }
    try
    {
        CableNetwork.AllCableNetworks.ForEach(CableNetworkTickAction);
        AllPoweredThings.ForEach(IPoweredThingsAction);
        CircuitHolders.Execute();
    }
    catch (System.Exception ex)
    {
        string text = "Exception: " + ex.Message + "\n";
        string stackTrace = ex.StackTrace;
        for (int i = 0; i < stackTrace.Length; i++)
        {
            text += stackTrace[i];
        }
        if (GameManager.GameState != GameState.None)
        {
            UnityEngine.Debug.LogError(text);
            ConsoleWindow.PrintError("Electronics THread Exception.</b> " + ex.Message + "</color>");
        }
    }
}
```

The two action delegates are plain null-guarded forwarders (lines 254832-254840):

```csharp
private static readonly Action<IPowered> IPoweredThingsAction = delegate(IPowered ipowered)
{
    ipowered?.OnPowerTick();
};

private static readonly Action<CableNetwork> CableNetworkTickAction = delegate(CableNetwork cableNetwork)
{
    cableNetwork?.OnPowerTick();
};
```

The three phases:

1. **`CableNetwork.AllCableNetworks.ForEach(CableNetworkTickAction)`** -> `CableNetwork.OnPowerTick()` on every network. This is where the actual power arithmetic happens: each network runs `PowerTick.Initialise -> CalculateState -> ApplyState` and then copies the result into its four display load fields. See [PowerTick](./PowerTick.md) and [CableNetwork](./CableNetwork.md). After this phase, every device's `Powered` flag and every provider's energy has been settled for the tick.
2. **`AllPoweredThings.ForEach(IPoweredThingsAction)`** -> `IPowered.OnPowerTick()` on every registered powered thing. This is the per-device post-power hook (batteries recompute their charge-state ladder and segment bar here, the [Battery](./Battery.md) `OnPowerTick` runs in this phase, lights update, machines react to their now-known `Powered` state, etc.). It runs AFTER all networks have settled, so a device's `OnPowerTick` sees this tick's final power result, not a mid-settle value.
3. **`CircuitHolders.Execute()`** -> every IC10 chip runs up to 128 instructions. `CircuitHolders` is a separate static class (decompile line 371855); its `Execute()` (line 371882) is `AllCircuitHolders.ForEach(iCircuitHolder?.Execute())`. So IC10 logic executes LAST in the power tick, reading the power state that phases 1 and 2 already finalised. See [IC10ExecutionTick](../GameSystems/IC10ExecutionTick.md) for the chip-level execution model.

`SerialiseDeltaState` / `DeserializeDeltaState` (lines 254938-254963) ship each network's `CurrentLoad` / `PotentialLoad` / `RequiredLoad` to clients per tick; clients do not run `ElectricityTick` themselves (the `RunSimulation` gate), they receive these three load floats and display them. This is consistent with [ServerAuthoritativeSimulation](../Patterns/ServerAuthoritativeSimulation.md): the power simulation is host-only, clients are display mirrors.

## The pools: DensePool, slot-order iteration, no sort, no double-buffer
<!-- verified: 0.2.6228.27061 @ 2026-06-29 -->

Both phase-1 and phase-2 collections are `DensePool`-family containers iterated by `ForEach`:

- `CableNetwork.AllCableNetworks` is a `ConcurrentDensePool<CableNetwork>("AllCableNetworks", 4096)` (decompile line 253430).
- `ElectricityManager.AllPoweredThings` is a `DensePool<IPowered>("AllPoweredThings", 8192)` (line 254830, `MAX_POWERED_ITEMS = 8192`).
- `CircuitHolders.AllCircuitHolders` is a `DensePool<ICircuitHolder>("AllCircuitHolders", 4096)` (line 371859).

`DensePool<T>.ForEach` (decompile lines 212399-212412) walks the pool's `_activeList` in slot order with a plain `for` loop, no copy, no sort, no double-buffer:

```csharp
public void ForEach(Action<T> action)
{
    if (action == null)
    {
        throw new ArgumentNullException("action");
    }
    T[] entries = _entries;
    int[] activeList = _activeList;
    int activeCount = _activeCount;
    for (int i = 0; i < activeCount; i++)
    {
        action(entries[activeList[i]]);
    }
}
```

`_activeList[i]` is the slot index of the i-th active entry. Slots are assigned on `Add` from a free-list LIFO (`_freeList[_freeCount - 1]`, line 212365) and compacted on `RemoveAt` by swapping the last active entry into the freed position (lines 212379-212391). So the iteration order is **pool-slot order**: the order in which the surviving entries were added, perturbed by the swap-with-last compaction whenever an entry is removed. It is NOT topological, NOT by `ReferenceId`, NOT distance-sorted, NOT stable across a removal.

### Why this is the key architectural fact

Phase 1 ticks each `CableNetwork` in isolation, but power crosses between networks through bridge devices ([ElectricalInputOutput](./ElectricalInputOutput.md): transformers, batteries, APCs, and the wireless transmitter/receiver pair). A bridge device reports its INPUT-side draw from a debt accumulator (`_powerProvided`) that was filled when its OUTPUT side was ticked (see [Device](./Device.md), "Two per-device draw-state fields", and [PowerTick](./PowerTick.md)). Whether the upstream network sees this tick's downstream demand or last tick's depends entirely on **which of the two networks `AllCableNetworks.ForEach` visits first this tick** -- and that is pool-slot order, an artifact of construction/destruction history, not of grid topology.

Consequences:

- A multi-hop power chain (source network -> transformer -> intermediate network -> transformer -> load network) does NOT settle in one tick. Each bridge hop the propagation has to "swim against" the iteration order adds one tick of lag. The microwave transmitter/receiver path is a two-hop debt chain whose source-payment lags the load by roughly two ticks for the same reason; see [PowerTransmitter](./PowerTransmitter.md) / [PowerReceiver](./PowerReceiver.md).
- The lag is order-DEPENDENT, so destroying and replacing a cable, transformer, or any pooled network member can change the slot order and therefore change how many ticks a given chain takes to settle, without any change to the wiring the player sees. This is the structural reason power readings on a transformer cascade can "shimmer" for a tick or two after a topology edit.
- A mod that needs deterministic same-tick cross-network settlement cannot get it by patching one network's tick; it has to re-architect the pass (compute all supply/demand first, then settle), which is exactly what PowerGridPlus's atomic two-pass tick does (OBSERVE all networks, then ENFORCE all networks; see [PowerTick](./PowerTick.md), "CalculateState accumulates; Initialise resets").
- `ConcurrentDensePool` (used for `AllCableNetworks`) takes a lock on `Add` / `RemoveAt` (the `lock` block at decompile line ~212360), so registration is thread-safe, but `ForEach` itself snapshots `_entries` / `_activeList` / `_activeCount` into locals and iterates without a lock -- consistent with the tick running on the worker thread while structural edits are gated to the main thread.

## Registration and lifecycle
<!-- verified: 0.2.6228.27061 @ 2026-06-29 -->

`AllPoweredThings` is populated by `ElectricityManager.Register(IPowered)` / `Deregister(IPowered)` (lines 254889-254903), both no-ops when `GameManager.GameState == GameState.None`:

```csharp
public static void Register(IPowered item)
{
    if (GameManager.GameState != GameState.None)
    {
        AllPoweredThings.Add(item);
    }
}
```

`IPowered` (interface, declares only `void OnPowerTick()`) is implemented by every `Device` and by the non-Device powered things; the device registers itself during its registration chain. `ClearAll()` (line 254933) empties the pool on world teardown.

`ElectricityManager : ThreadedManager` also owns the solar pass: `SolarProcessing` (an `async UniTaskVoid` started in `StartManager`, line 254876) walks `SolarRadiators.AllSolarRadiators.ForEachAsync(PlayerLoopTiming.FixedUpdate, ..., CalculateSolarEfficiencyAction)` on the `FixedUpdate` `PlayerLoopTiming`, separate from `ElectricityTick`. So solar EFFICIENCY (the per-radiator sun reading, `SolarPanel.GenerationEfficiency`) is computed on the FixedUpdate player-loop, while the power the panel contributes to its network is pulled inside phase 1 via `SolarPanel.GetGeneratedPower`. See [SolarPanel](./SolarPanel.md). The per-frame throughput and the headless-load ramp consequence are detailed in "The solar efficiency pass: one radiator per FixedUpdate, and the load-time ramp" below.

The class is a singleton (`public static ElectricityManager Instance;`, assigned in `ManagerAwake`, line 254855). Being a `ThreadedManager`, the tick body runs on the UniTask ThreadPool worker; see [PowerTickThreading](../GameSystems/PowerTickThreading.md) for why any Unity-API call from a power-tick-adjacent patch needs a `MainThreadDispatcher`.

## The solar efficiency pass: one radiator per FixedUpdate, and the load-time ramp
<!-- verified: 0.2.6228.27061 @ 2026-06-29 -->

`SolarPanel.GenerationRate` (the watts a panel contributes to its `CableNetwork`, returned through `GetGeneratedPower`, decompile line 400139) multiplies two terms that update on DIFFERENT clocks:

```csharp
// SolarPanel.GenerationRate (line 399911)
_generated = PowerGenerated() * GenerationEfficiency * (1f - DamageState.TotalRatio);
```

- `PowerGenerated()` (line 399948) reads `OrbitalSimulation.SolarIrradiance * _panelArea * weatherFactor` through the log soft-cap. `SolarIrradiance` is an **instantaneous** function of body-to-sun distance, `SolarConstant / distanceAu^2` (`OrbitalSimulation.CalculateSolarIrradiance`, line 56827), set on world load in `OrbitalSimulation.Load` (line 57089) and again every frame in `HandleUpdate` (line 56723). It is NOT smoothed, lerped, or time-averaged. At a fixed orbital distance it is a constant. So `PowerGenerated()` is at its steady value from the first tick.
- `GenerationEfficiency` is a plain `public float` field on `SolarPanel` (line 399791), C#-default **0** until first written. It is written ONLY by `SolarPanel.CalculateSolarEfficiency()` (line 400354), and the only caller is the `ElectricityManager.SolarProcessing` FixedUpdate pass via `CalculateSolarEfficiencyAction` (line 254826). Until that pass touches a given panel, the panel multiplies its full `PowerGenerated()` by `0` and contributes `0` W.

Therefore a freshly-loaded panel reads `Gen = 0` and steps up to its steady value the moment the solar pass first computes its efficiency. The ramp lives entirely in `GenerationEfficiency`, not in the irradiance or the orbital state.

`SolarProcessing` (line 254864) and the throughput of `DensePool.ForEachAsync` (line 212674) determine HOW FAST efficiency settles:

```csharp
// ElectricityManager.SolarProcessing (line 254864)
private static async UniTaskVoid SolarProcessing(CancellationToken cancellationToken)
{
    while (!cancellationToken.IsCancellationRequested)
    {
        if (!WorldManager.IsGamePaused && GameManager.GameState == GameState.Running)
        {
            await SolarRadiators.AllSolarRadiators.ForEachAsync(PlayerLoopTiming.FixedUpdate, cancellationToken, CalculateSolarEfficiencyAction);
        }
        await UniTask.NextFrame(PlayerLoopTiming.FixedUpdate, cancellationToken);
    }
}

// DensePool<T>.ForEachAsync (line 212674)
public async UniTask ForEachAsync(PlayerLoopTiming timing, CancellationToken cancellationToken, Func<T, bool> action)
{
    ...
    for (int i = 0; i < count; i++)
    {
        if (action(entries[activeList[i]]))
        {
            await UniTask.NextFrame(timing, cancellationToken);   // yields AFTER each radiator whose action returned true
        }
    }
}
```

`ForEachAsync` is NOT a one-frame sweep of the whole pool. It `await UniTask.NextFrame(FixedUpdate)` after EVERY radiator whose action returned `true`, and `SolarPanel.CalculateSolarEfficiency()` always returns `true` (lines 400359, 400392). So the pass advances **one radiator per FixedUpdate frame**: a pool of N solar radiators takes N FixedUpdate frames to complete one full efficiency sweep, then the outer `while` yields one more frame and starts the next sweep. With a single panel in the pool, its efficiency is set on the first FixedUpdate the pass runs; with many panels, a given panel's first non-zero efficiency is delayed by its slot position in `AllSolarRadiators`. (Note: `RadiatorRotatable.CalculateSolarEfficiency` returns `false` when its 60-frame cooldown blocks it, line ~303 of `RadiatorRotatable`; a `false` return does NOT yield, so cooled-down radiators are skipped without consuming a frame.)

Critical consequence for headless / `-batchmode -nographics` dedicated servers: this pass rides the Unity `FixedUpdate` player-loop, which is exactly the loop the dedicated-server docs flag as not firing reliably after world load (see [SimulationTickDriverHooks](../GameSystems/SimulationTickDriverHooks.md): "`MonoBehaviour.Update` does not reliably fire after world load"; the `FixedUpdate` continuation timing is in the same player-loop family). `ElectricityTick` itself runs off `GameManager.GameTick` (an `async UniTask` self-scheduling loop, gated on `GameManager.RunSimulation = !NetworkManager.IsClient`, line 188999) and ticks normally headless, so the panel's power is PULLED every sim tick, but the `GenerationEfficiency` it multiplies by is only refreshed when the FixedUpdate player-loop advances. If FixedUpdate fires slowly or sporadically headless (e.g. only when the player-loop is pumped), `GenerationEfficiency` rises slowly, producing the observed 0 -> full ramp over tens of sim ticks that a normal client (with FixedUpdate firing every physics step) would not show, or would show only for a frame or two. The eclipse term that also gates efficiency to 0 (`OrbitalSimulation.IsEclipse => EclipseRatio > 0`, line 56392) is irrelevant headless because `EclipseRatio` is only updated in `SetSunState`, which early-returns under `GameManager.IsBatchMode` (line 56735).

Inputs to `CalculateSolarEfficiency` that could themselves ramp, and their status on a headless load: `OrbitalSimulation.WorldSunVector` is settled at load by `OrbitalSimulation.Load` -> `SetAllBodies` (line 57107), not ramped; `PanelCells.forward` (panel orientation) only changes while `RotatableBehaviour.DoMoveTask` is slewing (line 201998), and that loop runs on `UniTask.NextFrame()` default (Update) timing and only when current orientation differs from target, so a panel loaded already at its target does not slew; the five `IsRaycastObscured` structural raycasts and the `VoxelTerrain.OctreeRaycast` terrain check (lines 400383, 400410) depend on colliders/terrain being present, which is a candidate second-order ramp if collision is still streaming in early post-load, but the dominant and sufficient explanation is the 0-initialised `GenerationEfficiency` waiting on the FixedUpdate pass.

## Verification history

- 2026-06-29: added section "The solar efficiency pass: one radiator per FixedUpdate, and the load-time ramp" and refined the Registration-and-lifecycle description of `SolarProcessing`. New finding while investigating why solar generation ramps 0 -> full over tens of sim ticks immediately after a headless world load. Key facts sourced from `.work/decomp/0.2.6228.27061/Assembly-CSharp.decompiled.cs`: `SolarPanel.GenerationRate` = `PowerGenerated() * GenerationEfficiency * (1 - damage)` (line 399911); `GenerationEfficiency` is a `public float` default-0 field (line 399791) written only by `CalculateSolarEfficiency()` (line 400354); `PowerGenerated()` reads instantaneous `OrbitalSimulation.SolarIrradiance` (line 399948), which is `SolarConstant / distanceAu^2` with no smoothing (`CalculateSolarIrradiance` line 56827, set at load line 57089 and per-frame line 56723); `SolarProcessing` (line 254864) drives `ForEachAsync(PlayerLoopTiming.FixedUpdate, ...)`; `DensePool.ForEachAsync` (line 212674) `await UniTask.NextFrame` after EACH radiator whose action returns `true`, so it processes one radiator per FixedUpdate frame, not a whole-pool sweep per frame. Refines the prior "once per FixedUpdate frame" phrasing (which described how often the outer sweep restarts, not its per-frame throughput); no factual claim on the page was reversed, this is an additive clarification of throughput. Also captured: `WorldSunVector` settled at load via `SetAllBodies` (line 57107); `EclipseRatio` not updated headless because `SetSunState` early-returns under `IsBatchMode` (line 56735); `RotatableBehaviour.DoMoveTask` slews on Update timing only when off-target (line 201998). Cross-links [SimulationTickDriverHooks](../GameSystems/SimulationTickDriverHooks.md) for the headless FixedUpdate-does-not-fire-reliably fact.
- 2026-06-29: page created. Sourced from `.work/decomp/0.2.6228.27061/Assembly-CSharp.decompiled.cs`: `ElectricityManager` class (line 254818), `ElectricityTick` verbatim (254905-254931) with its three-phase `AllCableNetworks.ForEach(CableNetworkTickAction)` -> `AllPoweredThings.ForEach(IPoweredThingsAction)` -> `CircuitHolders.Execute()` order, the two action delegates (254832-254840), the pool declarations (`AllCableNetworks` ConcurrentDensePool 253430, `AllPoweredThings` DensePool 254830, `AllCircuitHolders` DensePool 371859), `DensePool.ForEach` slot-order walk (212399-212412) with the `Add` free-list LIFO (212365) and `RemoveAt` swap-with-last compaction (212379-212391), `CircuitHolders.Execute` (371882), `Register`/`Deregister`/`ClearAll` (254889-254936), and the `SolarProcessing` FixedUpdate loop (254864-254881). Establishes the KEY architectural fact that cross-network power propagation order is pool-slot order (construction/destruction history), not topological, so multi-hop bridge chains settle over multiple ticks in an order-dependent way. Cross-links the existing [PowerTick](./PowerTick.md), [CableNetwork](./CableNetwork.md), [ElectricalInputOutput](./ElectricalInputOutput.md), and [Device](./Device.md) pages, which carry the per-network and per-device detail. Additive (new page); no existing verified content contradicted.

## Open questions

- The exact call site that invokes `ElectricityManager.ElectricityTick()` from the `GameManager` simulation tick was not re-traced in this pass; [IC10ExecutionTick](../GameSystems/IC10ExecutionTick.md) documents the `GameManager -> ElectricityManager.ElectricityTick -> CircuitHolders.Execute` chain and the 500 ms cadence. Confirm the caller (likely `ThreadedManager.SimulationTick` / a `GameManager` driver) if precise timing relative to the atmospheric tick is needed.
