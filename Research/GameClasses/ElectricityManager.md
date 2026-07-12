---
title: ElectricityManager
type: GameClasses
created_in: 0.2.6228.27061
verified_in: 0.2.6403.27689
verified_at: 2026-07-13
sources:
  - $(StationeersPath)\rocketstation_Data\Managed\Assembly-CSharp.dll :: Assets.Scripts.Networks.ElectricityManager
  - .work/decomp/0.2.6228.27061/Assembly-CSharp.decompiled.cs :: lines 254818-254964 (ElectricityManager), 254905 (ElectricityTick), 254830-254840 (AllPoweredThings pool + action delegates), 253430 (AllCableNetworks pool), 212399-212412 (DensePool.ForEach), 371855-371886 (CircuitHolders.Execute)
  - .work/decomp/0.2.6403.27689/Assembly-CSharp.decompiled.cs :: lines 272091-272117 (ElectricityTick), 272016 (AllPoweredThings), 270588 (AllCableNetworks), 228801-228835 (DensePool.RemoveAt/ForEach), 229121-229137 (ForEachAsync), 229139-229223 (ConcurrentDensePool), 204387-204466 (GameManager.GameTick invocation), 272050-272067 (SolarProcessing)
  - .work/decomp/0.2.6403.27689/Assembly-CSharp.decompiled.cs :: lines 228696-229137 (full DensePool member census incl. Add 228779-228798, Snapshot 228838-228853, ForEach<TInt> 228889-228968, bool ForEach 228970-228980), 203945 (GameManager.RunSimulation), 204448-204472 (caller-side RunSimulation gate)
related:
  - ./CableNetwork.md
  - ./PowerTick.md
  - ./Device.md
  - ../GameSystems/PowerTickThreading.md
  - ../GameSystems/IC10ExecutionTick.md
tags: [power, threading, network]
---

# ElectricityManager

Vanilla power-simulation driver. `Assets.Scripts.Networks.ElectricityManager : ThreadedManager`. The single entry point for one whole power tick: `ElectricityManager.ElectricityTick()` (decompile line 272091 at 0.2.6403.27689) runs every game tick (the 500 ms / 2 Hz `GameManager` simulation tick, the same cadence that drives IC10; see [IC10ExecutionTick](../GameSystems/IC10ExecutionTick.md)) and does three things in fixed order: tick every `CableNetwork`, fire every `IPowered.OnPowerTick`, then execute every IC10 circuit.

This page covers the top-level orchestration and the cross-network propagation-order consequence. The per-network arithmetic (supply/demand summation, brownout, fuse/cable burn, provider settle) lives on [PowerTick](./PowerTick.md); the per-network book-keeping and load mirrors live on [CableNetwork](./CableNetwork.md).

## ElectricityTick: three phases in fixed order
<!-- verified: 0.2.6403.27689 @ 2026-07-13 -->

`ElectricityTick` is `static`, gated on `GameManager.RunSimulation` (host / single-player only; clients do not run it), and wraps the whole body in a try/catch that logs to the console rather than letting a power-tick exception kill the frame. The caller is `GameManager.GameTick` (an `async UniTask` self-scheduling loop, decompile line 204387): the loop does `await UniTask.SwitchToThreadPool()` (204418) and then, inside the `RunSimulation` branch, calls `ElectricityManager.ElectricityTick()` (204466) after the atmospherics jobs and before `LogicStack.LogicStackTick`. So the whole tick body runs on a ThreadPool worker, not the Unity main thread. Verbatim (decompile lines 272091-272117):

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

The two action delegates are plain null-guarded forwarders (lines 272018-272026):

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
3. **`CircuitHolders.Execute()`** -> every IC10 chip runs up to 128 instructions. `CircuitHolders` is a separate static class (0.2.6228 decompile line 371855, `Execute()` at 371882; not re-located this pass): `AllCircuitHolders.ForEach(iCircuitHolder?.Execute())`. So IC10 logic executes LAST in the power tick, reading the power state that phases 1 and 2 already finalised. See [IC10ExecutionTick](../GameSystems/IC10ExecutionTick.md) for the chip-level execution model.

`SerialiseDeltaState` (line 272124) / `DeserializeDeltaState` (via the per-network writer delegate `CableNetworkWriteAction` at 272028-272039, which ships `CurrentLoad` / `PotentialLoad` / `RequiredLoad`) send each network's three load floats to clients per tick; clients do not run `ElectricityTick` themselves (the `RunSimulation` gate), they receive these floats and display them. This is consistent with [ServerAuthoritativeSimulation](../Patterns/ServerAuthoritativeSimulation.md): the power simulation is host-only, clients are display mirrors.

The client-side gate is DOUBLE, and it makes client-side Harmony patches on this method inert. `public static bool RunSimulation => !Assets.Scripts.Networking.NetworkManager.IsClient;` (verbatim, GameManager, 203945). On a multiplayer client the `GameTick` loop never reaches the call at all (204466 sits inside `GameTick`'s `if (RunSimulation)` block, 204448-204472), and even if something else invoked `ElectricityTick`, the body's own `if (!GameManager.RunSimulation) return;` (272093-272096) exits before any work. Consequence for mods: a prefix or postfix on `ElectricityTick` never executes on a multiplayer client through the vanilla call path (the caller-side gate stops the invocation before any patch on the method could run), so a client-side power-tick replacement patched here is dead code on clients. The load values clients display arrive exclusively through `DeserializeDeltaState` (272132-272149); client-visible mod behavior has to hook something clients actually run.

## The pools: DensePool, slot-order iteration, no sort, no double-buffer
<!-- verified: 0.2.6403.27689 @ 2026-07-02 -->

Both phase-1 and phase-2 collections are `DensePool`-family containers iterated by `ForEach`:

- `CableNetwork.AllCableNetworks` is a `ConcurrentDensePool<CableNetwork>("AllCableNetworks", 4096)` (decompile line 270588).
- `ElectricityManager.AllPoweredThings` is a `DensePool<IPowered>("AllPoweredThings", 8192)` (line 272016, `MAX_POWERED_ITEMS = 8192` at 272014).
- `CircuitHolders.AllCircuitHolders` is a `DensePool<ICircuitHolder>("AllCircuitHolders", 4096)` (0.2.6228 line 371859; not re-located this pass).

`DensePool<T>.ForEach` (decompile lines 228822-228835) walks the pool's `_activeList` in slot order with a plain `for` loop, no copy, no sort, no double-buffer:

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

`_activeList[i]` is the slot index of the i-th active entry. Slots are assigned on `Add` from a free-list LIFO (the `Add` body ends with `_activeList[_activeCount++] = num;` at 228795) and compacted on `RemoveAt` by swapping the last active entry into the freed position (lines 228801-228813). So the iteration order is **pool-slot order**: the order in which the surviving entries were added, perturbed by the swap-with-last compaction whenever an entry is removed. It is NOT topological, NOT by `ReferenceId`, NOT distance-sorted, NOT stable across a removal.

### Why this is the key architectural fact

Phase 1 ticks each `CableNetwork` in isolation, but power crosses between networks through bridge devices ([ElectricalInputOutput](./ElectricalInputOutput.md): transformers, batteries, APCs, and the wireless transmitter/receiver pair). A bridge device reports its INPUT-side draw from a debt accumulator (`_powerProvided`) that was filled when its OUTPUT side was ticked (see [Device](./Device.md), "Two per-device draw-state fields", and [PowerTick](./PowerTick.md)). Whether the upstream network sees this tick's downstream demand or last tick's depends entirely on **which of the two networks `AllCableNetworks.ForEach` visits first this tick** -- and that is pool-slot order, an artifact of construction/destruction history, not of grid topology.

Consequences:

- A multi-hop power chain (source network -> transformer -> intermediate network -> transformer -> load network) does NOT settle in one tick. Each bridge hop the propagation has to "swim against" the iteration order adds one tick of lag. The microwave transmitter/receiver path is a two-hop debt chain whose source-payment lags the load by roughly two ticks for the same reason; see [PowerTransmitter](./PowerTransmitter.md) / [PowerReceiver](./PowerReceiver.md).
- The lag is order-DEPENDENT, so destroying and replacing a cable, transformer, or any pooled network member can change the slot order and therefore change how many ticks a given chain takes to settle, without any change to the wiring the player sees. This is the structural reason power readings on a transformer cascade can "shimmer" for a tick or two after a topology edit.
- A mod that needs deterministic same-tick cross-network settlement cannot get it by patching one network's tick; it has to re-architect the pass (compute all supply/demand first, then settle), which is exactly what PowerGridPlus's atomic two-pass tick does (OBSERVE all networks, then ENFORCE all networks; see [PowerTick](./PowerTick.md), "CalculateState accumulates; Initialise resets").
- `ConcurrentDensePool` (used for `AllCableNetworks`) takes a `lock (this)` on every override: `Add` (229147-229153), `Remove` (229156-229162), `RemoveAt` (229165-229171), and, at 0.2.6403.27689, `ForEach` as well (229216-229222 wraps `base.ForEach` in the same lock). So phase 1's `AllCableNetworks.ForEach` holds the pool lock for the duration of the walk, serializing it against concurrent registration; this supersedes the earlier claim that `ForEach` iterates without a lock (true only of the base `DensePool.ForEach`, which is what phase 2's `AllPoweredThings` uses).

### Complete lock-surface census: which pool methods lock, and what unlocked iteration can observe
<!-- verified: 0.2.6403.27689 @ 2026-07-13 -->

Method-by-method census of `DensePool<T>` (class 228696) vs `ConcurrentDensePool<T> : DensePool<T>` (229139), read verbatim at 0.2.6403.27689.

Base `DensePool<T>`:

- `Add(T)` (228779-228798) DOES `lock (this)` even in the base class, the only base method that locks: it pops a slot from the free list, calls `item.OnAddToPool(this, slot)` (a `false` return aborts the add), then publishes `_entries[num] = item; _slotToActiveIndex[num] = _activeCount; _activeList[_activeCount++] = num;`. So even on a plain pool, concurrent `Add`s are serialized against each other, but against nothing else.
- `RemoveAt(int)` (228801-228813) does NOT lock: swap-with-last compaction ends with `_slotToActiveIndex[slot] = -1; _entries[slot] = null; _freeList[_freeCount++] = slot;`. `Remove(T)` (228816-228819) is `item?.OnRemoveFromPool(this)`, also unlocked.
- `ForEach(Action<T>)` (228822-228835) does not lock. It copies the ARRAY REFERENCES and `_activeCount` to locals and walks `action(entries[activeList[i]])`; the arrays are live, so this is not a snapshot.
- Also unlocked in the base: the bool-early-exit `ForEach(Func<T, bool>)` (228970-228980, which reads the fields directly on every iteration rather than capturing locals), the serializer `ForEach<TInt>(RocketBinaryWriter, Func<RocketBinaryWriter, T, bool>, ref TInt)` (228889-228968), `Snapshot(Span<T>)` (228838-228853), `Active()` (228856-228859, returning the `ActiveEnumerable` ref struct over the live arrays, 228708-228739), `RemoveWhere` (228861-228877), `Populate` (228879-228887), `Clear` (228982-228994), `Cleanup` (228996-229006), `FindUsing` (229008-229023), `Pick` (229026-229035), `ToList` (229093-229105), `Find` (229107-229119), `ForEachAsync` (229121-229137).

`ConcurrentDensePool<T>` (229139-229223) overrides NINE members, each wrapping the base call in `lock (this)`: `Add` (229147), `Remove` (229156), `RemoveAt` (229165), `Clear` (229173), `Active` (229182), `Cleanup` (229191), `RemoveWhere` (229199), `Populate` (229207), `ForEach(Action<T>)` (229216). Every other member is INHERITED UNLOCKED, notably:

- `ForEach(Func<T, bool>)`, the `ForEach<TInt>` serializer overload, `Snapshot`, `FindUsing`, `Pick`, `ToList`, `Find`, and `ForEachAsync` stay unlocked even on the concurrent pool. `ElectricityManager.SerialiseDeltaState` (272124-272130) iterates `AllCableNetworks` through exactly that unlocked `ForEach<TInt>` overload (after a LOCKED `Cleanup`), so the per-tick multiplayer load-sync walk is NOT serialized against concurrent `Add` / `Remove`, even though phase 1's plain `ForEach` is. `WirelessNetwork.GetWirelessNetwork` goes through the unlocked `FindUsing` (272474) the same way.
- `ConcurrentDensePool.Active()` locks only the CREATION of the enumerable; the returned ref struct captures the live arrays and its enumeration runs after the lock is released.

What an unlocked walk can observe when `Add` / `RemoveAt` land concurrently (mechanics follow from the bodies above): `RemoveAt` nulls the slot and swaps the last active entry into the removed `_activeList` position, so an in-flight walk can read `null` (both `ElectricityTick` delegates null-guard for exactly this: `cableNetwork?.OnPowerTick()` / `ipowered?.OnPowerTick()`, 272018-272026), can visit the swapped entry twice, or can skip it; a concurrent `Add` publishes past a locally captured `activeCount`, so the walk misses new entries until the next pass. No field in either class is `volatile` and there are no memory barriers. Since `AllPoweredThings` is a plain `DensePool` (272016), phase 2 of `ElectricityTick` runs with all of these hazards while `Register` / `Deregister` (272075-272089) fire from main-thread device creation and destruction; `AllCableNetworks` (ConcurrentDensePool, 270588) is protected only for `ForEach(Action<T>)` and the mutation methods.

## Registration and lifecycle
<!-- verified: 0.2.6403.27689 @ 2026-07-02 -->

`AllPoweredThings` is populated by `ElectricityManager.Register(IPowered)` / `Deregister(IPowered)` (lines 272075-272089), both no-ops when `GameManager.GameState == GameState.None`:

```csharp
public static void Register(IPowered item)
{
    if (GameManager.GameState != GameState.None)
    {
        AllPoweredThings.Add(item);
    }
}
```

`IPowered` (interface, declares only `void OnPowerTick()`) is implemented by every `Device` and by the non-Device powered things; the device registers itself during its registration chain. `ClearAll()` (line 272119) empties the pool on world teardown.

`ElectricityManager : ThreadedManager` also owns the solar pass: `SolarProcessing` (an `async UniTaskVoid`, lines 272050-272060, started in `StartManager` at 272062-272067) walks `SolarRadiators.AllSolarRadiators.ForEachAsync(PlayerLoopTiming.FixedUpdate, ..., CalculateSolarEfficiencyAction)` on the `FixedUpdate` `PlayerLoopTiming`, separate from `ElectricityTick`. So solar EFFICIENCY (the per-radiator sun reading, `SolarPanel.GenerationEfficiency`) is computed on the FixedUpdate player-loop, while the power the panel contributes to its network is pulled inside phase 1 via `SolarPanel.GetGeneratedPower`. See [SolarPanel](./SolarPanel.md). The per-frame throughput and the headless-load ramp consequence are detailed in "The solar efficiency pass: one radiator per FixedUpdate, and the load-time ramp" below.

The class is a singleton (`public static ElectricityManager Instance;`, assigned in `ManagerAwake`, lines 272041-272048). Being a `ThreadedManager`, the tick body runs on the UniTask ThreadPool worker (see the `GameManager.GameTick` invocation above); see [PowerTickThreading](../GameSystems/PowerTickThreading.md) for why any Unity-API call from a power-tick-adjacent patch needs a `MainThreadDispatcher`.

## The solar efficiency pass: one radiator per FixedUpdate, and the load-time ramp
<!-- verified: 0.2.6403.27689 @ 2026-07-02 -->

`SolarPanel.GenerationRate` (the watts a panel contributes to its `CableNetwork`, returned through `GetGeneratedPower` at 0.2.6403.27689 line 421445) multiplies two terms that update on DIFFERENT clocks:

```csharp
// SolarPanel.GenerationRate (line 421216)
_generated = PowerGenerated() * GenerationEfficiency * (1f - DamageState.TotalRatio);
```

- `PowerGenerated()` (line 421245) reads `OrbitalSimulation.SolarIrradiance * _panelArea * weatherFactor` through the log soft-cap (at 0.2.6403.27689 the weather factor is the altitude-aware `WeatherManager.GetSolarRatioAt(Position.y)`; see [SolarPanel](./SolarPanel.md)). `SolarIrradiance` is an **instantaneous** function of body-to-sun distance, `SolarConstant / distanceAu^2` (`OrbitalSimulation.CalculateSolarIrradiance`, 0.2.6228 line 56827; the OrbitalSimulation line refs in this section are 0.2.6228 refs pending a re-locate), set on world load in `OrbitalSimulation.Load` (line 57089) and again every frame in `HandleUpdate` (line 56723). It is NOT smoothed, lerped, or time-averaged. At a fixed orbital distance it is a constant. So `PowerGenerated()` is at its steady value from the first tick.
- `GenerationEfficiency` is a `[ReadOnly] public float` field on `SolarPanel` (line 421101), C#-default **0** until first written. It is written ONLY by `SolarPanel.CalculateSolarEfficiency()` (421660-421670; at 0.2.6403.27689 that method averages the per-arm `SolarPanelArm.CalculateSolarEfficiency` results, see [SolarPanel](./SolarPanel.md)), and the only caller is the `ElectricityManager.SolarProcessing` FixedUpdate pass via `CalculateSolarEfficiencyAction` (line 272012, which also skips null / `IsBeingDestroyed` radiators). Until that pass touches a given panel, the panel multiplies its full `PowerGenerated()` by `0` and contributes `0` W.

Therefore a freshly-loaded panel reads `Gen = 0` and steps up to its steady value the moment the solar pass first computes its efficiency. The ramp lives entirely in `GenerationEfficiency`, not in the irradiance or the orbital state.

`SolarProcessing` (line 272050 at 0.2.6403.27689) and the throughput of `DensePool.ForEachAsync` (line 229121 at 0.2.6403.27689) determine HOW FAST efficiency settles:

```csharp
// ElectricityManager.SolarProcessing (line 272050)
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

// DensePool<T>.ForEachAsync (line 229121)
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

`ForEachAsync` is NOT a one-frame sweep of the whole pool. It `await UniTask.NextFrame(FixedUpdate)` after EVERY radiator whose action returned `true`, and `SolarPanel.CalculateSolarEfficiency()` always returns `true` (0.2.6403.27689 line 421669). So the pass advances **one radiator per FixedUpdate frame**: a pool of N solar radiators takes N FixedUpdate frames to complete one full efficiency sweep, then the outer `while` yields one more frame and starts the next sweep. With a single panel in the pool, its efficiency is set on the first FixedUpdate the pass runs; with many panels, a given panel's first non-zero efficiency is delayed by its slot position in `AllSolarRadiators`. (Note: `RadiatorRotatable.CalculateSolarEfficiency` returns `false` when its 60-frame cooldown blocks it, line ~303 of `RadiatorRotatable` at 0.2.6228; a `false` return does NOT yield, so cooled-down radiators are skipped without consuming a frame.)

Critical consequence for headless / `-batchmode -nographics` dedicated servers: this pass rides the Unity `FixedUpdate` player-loop, which is exactly the loop the dedicated-server docs flag as not firing reliably after world load (see [SimulationTickDriverHooks](../GameSystems/SimulationTickDriverHooks.md): "`MonoBehaviour.Update` does not reliably fire after world load"; the `FixedUpdate` continuation timing is in the same player-loop family). `ElectricityTick` itself runs off `GameManager.GameTick` (an `async UniTask` self-scheduling loop, gated on `GameManager.RunSimulation = !NetworkManager.IsClient`, line 188999) and ticks normally headless, so the panel's power is PULLED every sim tick, but the `GenerationEfficiency` it multiplies by is only refreshed when the FixedUpdate player-loop advances. If FixedUpdate fires slowly or sporadically headless (e.g. only when the player-loop is pumped), `GenerationEfficiency` rises slowly, producing the observed 0 -> full ramp over tens of sim ticks that a normal client (with FixedUpdate firing every physics step) would not show, or would show only for a frame or two. The eclipse term that also gates efficiency to 0 (`OrbitalSimulation.IsEclipse => EclipseRatio > 0`, line 56392) is irrelevant headless because `EclipseRatio` is only updated in `SetSunState`, which early-returns under `GameManager.IsBatchMode` (line 56735).

Inputs to `CalculateSolarEfficiency` that could themselves ramp, and their status on a headless load: `OrbitalSimulation.WorldSunVector` is settled at load by `OrbitalSimulation.Load` -> `SetAllBodies` (0.2.6228 line 57107), not ramped; the per-arm `Cells.forward` orientation (at 0.2.6403.27689 the panel normal lives on each `SolarPanelArm`, driven through `YawPivot` / `PitchPivot`, see [SolarPanel](./SolarPanel.md)) only changes while `RotatableBehaviour.DoMoveTask` is slewing (0.2.6403 line 217434), and that loop runs on `UniTask.NextFrame()` default (Update) timing and only when current orientation differs from target, so a panel loaded already at its target does not slew; the five per-arm obscurance raycasts and the `VoxelTerrain.OctreeRaycast` terrain check (0.2.6403 lines 186970-187001) depend on colliders/terrain being present, which is a candidate second-order ramp if collision is still streaming in early post-load, but the dominant and sufficient explanation is the 0-initialised `GenerationEfficiency` waiting on the FixedUpdate pass.

## Verification history

- 2026-07-13: fresh-validation pass at 0.2.6403.27689 (decompile-claim audit), two additions. (a) The "client-side gate is DOUBLE" paragraph in the ElectricityTick section: `RunSimulation => !NetworkManager.IsClient` quoted verbatim (203945), the caller-side `if (RunSimulation)` gate around the 204466 call (204448-204472) re-read, and the modding consequence made explicit (a Harmony patch on `ElectricityTick` never runs on a multiplayer client via the vanilla path because the caller gate stops the invocation itself; restamped that section). (b) The "Complete lock-surface census" subsection under the pools section: every `DensePool` member classified locked/unlocked verbatim (base `Add` locks `this` at 228781, the only base lock; `RemoveAt` / `Remove` / both extra `ForEach` overloads / `Snapshot` / `Active` / the rest unlocked), the nine `ConcurrentDensePool` overrides enumerated (Add/Remove/RemoveAt/Clear/Active/Cleanup/RemoveWhere/Populate/ForEach(Action<T>), 229146-229222), the inherited-unlocked surface called out (notably `SerialiseDeltaState` iterating `AllCableNetworks` through the unlocked `ForEach<TInt>` at 272128, and `Active()` locking only enumerable creation), and the concurrent-iteration failure modes (null reads, double-visit, skip, missed adds). Additive; extends rather than contradicts the 2026-07-02 `ConcurrentDensePool.ForEach` supersession note ("every override locks" remains true, the override list is now complete).
- 2026-07-02 (later): restamped "The solar efficiency pass" to 0.2.6403.27689 after the SolarPanel restructure pass. Updated the SolarPanel-internals refs to the new decompile (`GetGeneratedPower` 421445, `GenerationRate` line 421216, `PowerGenerated` 421245 with the altitude-aware weather factor, `GenerationEfficiency` field 421101, `CalculateSolarEfficiency` 421660-421670 now averaging per-arm `SolarPanelArm` results with its always-`true` return at 421669, `CalculateSolarEfficiencyAction` 272012 with the added `IsBeingDestroyed` guard, per-arm raycasts 186970-187001, `DoMoveTask` 217434). The ramp analysis and one-radiator-per-FixedUpdate throughput are unchanged. OrbitalSimulation refs (56827 / 57089 / 56723 / 56392 / 56735 / 57107) and the RadiatorRotatable cooldown ref remain 0.2.6228 refs pending a re-locate, marked inline.
- 2026-07-02: re-verification pass against the 0.2.6403.27689 decompile after the game update from 0.2.6228.27061. Confirmed unchanged with new line refs: `ElectricityTick` three phases + `RunSimulation` gate + try/catch (272091-272117), the two action delegates (272018-272026), `AllPoweredThings` DensePool 8192 (272016), `AllCableNetworks` ConcurrentDensePool 4096 (270588), `DensePool.ForEach` pool-slot walk (228822-228835), `RemoveAt` swap-with-last (228801-228813), `Register` / `Deregister` (272075-272089), `ClearAll` (272119), `SolarProcessing` one-radiator-per-FixedUpdate (272050-272067) with `ForEachAsync` (229121-229137). NEW / superseding: (a) resolved the open question about the tick's caller: `GameManager.GameTick` (204387) does `await UniTask.SwitchToThreadPool()` (204418) and calls `ElectricityManager.ElectricityTick()` at 204466 inside the `RunSimulation` branch, after the atmospherics jobs and before `LogicStack.LogicStackTick`; documented in the ElectricityTick section and removed from Open Questions. (b) `ConcurrentDensePool.ForEach` at 0.2.6403.27689 wraps the walk in `lock (this)` (229216-229222), superseding the prior claim that `ForEach` iterates without a lock (that remains true only for the base `DensePool.ForEach` used by `AllPoweredThings`). The SolarPanel-internals line refs inside the solar section were not re-located this pass (that section keeps its 0.2.6228.27061 stamp; only the verified `SolarProcessing` / `ForEachAsync` refs were updated inline), and the `CircuitHolders` decl/Execute refs are marked as 0.2.6228 refs pending a re-locate.
- 2026-06-29: added section "The solar efficiency pass: one radiator per FixedUpdate, and the load-time ramp" and refined the Registration-and-lifecycle description of `SolarProcessing`. New finding while investigating why solar generation ramps 0 -> full over tens of sim ticks immediately after a headless world load. Key facts sourced from `.work/decomp/0.2.6228.27061/Assembly-CSharp.decompiled.cs`: `SolarPanel.GenerationRate` = `PowerGenerated() * GenerationEfficiency * (1 - damage)` (line 399911); `GenerationEfficiency` is a `public float` default-0 field (line 399791) written only by `CalculateSolarEfficiency()` (line 400354); `PowerGenerated()` reads instantaneous `OrbitalSimulation.SolarIrradiance` (line 399948), which is `SolarConstant / distanceAu^2` with no smoothing (`CalculateSolarIrradiance` line 56827, set at load line 57089 and per-frame line 56723); `SolarProcessing` (line 254864) drives `ForEachAsync(PlayerLoopTiming.FixedUpdate, ...)`; `DensePool.ForEachAsync` (line 212674) `await UniTask.NextFrame` after EACH radiator whose action returns `true`, so it processes one radiator per FixedUpdate frame, not a whole-pool sweep per frame. Refines the prior "once per FixedUpdate frame" phrasing (which described how often the outer sweep restarts, not its per-frame throughput); no factual claim on the page was reversed, this is an additive clarification of throughput. Also captured: `WorldSunVector` settled at load via `SetAllBodies` (line 57107); `EclipseRatio` not updated headless because `SetSunState` early-returns under `IsBatchMode` (line 56735); `RotatableBehaviour.DoMoveTask` slews on Update timing only when off-target (line 201998). Cross-links [SimulationTickDriverHooks](../GameSystems/SimulationTickDriverHooks.md) for the headless FixedUpdate-does-not-fire-reliably fact.
- 2026-06-29: page created. Sourced from `.work/decomp/0.2.6228.27061/Assembly-CSharp.decompiled.cs`: `ElectricityManager` class (line 254818), `ElectricityTick` verbatim (254905-254931) with its three-phase `AllCableNetworks.ForEach(CableNetworkTickAction)` -> `AllPoweredThings.ForEach(IPoweredThingsAction)` -> `CircuitHolders.Execute()` order, the two action delegates (254832-254840), the pool declarations (`AllCableNetworks` ConcurrentDensePool 253430, `AllPoweredThings` DensePool 254830, `AllCircuitHolders` DensePool 371859), `DensePool.ForEach` slot-order walk (212399-212412) with the `Add` free-list LIFO (212365) and `RemoveAt` swap-with-last compaction (212379-212391), `CircuitHolders.Execute` (371882), `Register`/`Deregister`/`ClearAll` (254889-254936), and the `SolarProcessing` FixedUpdate loop (254864-254881). Establishes the KEY architectural fact that cross-network power propagation order is pool-slot order (construction/destruction history), not topological, so multi-hop bridge chains settle over multiple ticks in an order-dependent way. Cross-links the existing [PowerTick](./PowerTick.md), [CableNetwork](./CableNetwork.md), [ElectricalInputOutput](./ElectricalInputOutput.md), and [Device](./Device.md) pages, which carry the per-network and per-device detail. Additive (new page); no existing verified content contradicted.

## Open questions

None. (The 2026-06-29 question about the exact `ElectricityTick` call site was resolved 2026-07-02: `GameManager.GameTick` at decompile 204387 / 204418 / 204466; see the ElectricityTick section.)
