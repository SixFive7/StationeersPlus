---
title: Heat emission to atmosphere
type: GameSystems
created_in: 0.2.6228.27061
verified_in: 0.2.6228.27061
verified_at: 2026-05-22
sources:
  - $(StationeersPath)\rocketstation_Data\Managed\Assembly-CSharp.dll :: Assets.Scripts.Atmospherics.GasMixture
  - $(StationeersPath)\rocketstation_Data\Managed\Assembly-CSharp.dll :: Assets.Scripts.Objects.Device
  - $(StationeersPath)\rocketstation_Data\Managed\Assembly-CSharp.dll :: Assets.Scripts.Atmospherics.AtmosphericsController
  - $(StationeersPath)\rocketstation_Data\Managed\Assembly-CSharp.dll :: Assets.Scripts.Atmospherics.AtmosphericsManager
  - $(StationeersPath)\rocketstation_Data\Managed\Assembly-CSharp.dll :: Assets.Scripts.Atmospherics.AtmosphericEventInstance
  - $(StationeersPath)\rocketstation_Data\Managed\Assembly-CSharp.dll :: Assets.Scripts.GameManager
  - .work/decomp/0.2.6228.27061/Assembly-CSharp.decompiled.cs :: lines 184227-184329 (AtmosphericEventInstance.Create*), 184573-184593 (event apply), 184601-184960 (AtmosphericsController), 189436-189517 (game-tick atmospheric phase ordering), 200541-200563 (PlanetaryAtmosphereSimulation.AddEnergy / RemoveEnergy), 202278 (ThreadedManager.TickSpeedSeconds), 284513-284525 (Lungs TakeBreath), 302441-302505 (Thing.OnAtmosphericTick virtual + OnFireConsume), 306061-306072 (Grenade), 338811-338823 (BurningFurnace-style), 349717 / 350985-350997 (Device base EnergyToHeatRatio + OnAtmosphericTick), 358929-358946 (GasMixture.AddEnergy/RemoveEnergy struct-form, 421702-421719 reference-form), 363815-363870 (PipeHeater), 369588 (AreaPowerControl override), 370720 (Battery override), 371140-371160 (Battery base atmospheric tick), 403351 (Transformer EnergyToHeatRatio), 417811-417908 (AtmosphericsManager Threaded driver), 418214-418248 (ThingAtmosphereTick / AtmosphericsNetworksTick dispatchers), 418542-418548 (CloneGlobalAtmosphereThreadSafe)
related:
  - ../GameClasses/Atmosphere.md
  - ../GameClasses/CableNetwork.md
  - ../GameClasses/Cable.md
  - ../GameClasses/Device.md
  - ../GameClasses/CombustionDeepMiner.md
  - ../GameSystems/PowerTickThreading.md
  - ../GameSystems/DevicePowerDraw.md
tags: [power, threading, network]
---

# Heat emission to atmosphere

How vanilla devices and game systems deposit thermal energy (joules) into atmospheres, what API to call, where to call it from, and the threading and multiplayer-authority contract a mod must respect to do the same without desync.

## Canonical API: GasMixture.AddEnergy(MoleEnergy)
<!-- verified: 0.2.6228.27061 @ 2026-05-22 -->

`GasMixture` carries the stored internal energy. There is no `Atmosphere.AddHeat` or `Atmosphere.AddEnergyToContents` wrapper; the canonical method is on the gas mixture itself. Both the struct-form and the class-form are present in the assembly (the codebase contains two `AddEnergy` definitions, one inside a class with `private MoleEnergy _energy;` and one operating on `TotalEnergy`); both share the same null/NaN/zero guard.

Verbatim, from `Assembly-CSharp.dll` decompile line 358929:

```csharp
public void AddEnergy(MoleEnergy energy)
{
    if (!energy.IsDenormalOrZero() && !energy.IsNaN())
    {
        _energy += energy;
    }
}

public MoleEnergy RemoveEnergy(MoleEnergy energy)
{
    if (energy <= MoleEnergy.Zero || energy.IsNaN())
    {
        return MoleEnergy.Zero;
    }
    MoleEnergy moleEnergy = RocketMath.Min(Energy, energy);
    _energy -= moleEnergy;
    return moleEnergy;
}
```

And the parallel definition (line 421702) checks emptiness in addition:

```csharp
public void AddEnergy(MoleEnergy energy)
{
    if (!(GetTotalMolesGassesAndLiquids <= Chemistry.MINIMUM_VALID_TOTAL_MOLES)
        && !energy.IsDenormalOrZero()
        && !energy.IsNaN())
    {
        TotalEnergy += energy;
    }
}
```

`MoleEnergy` is the joules wrapper (`new MoleEnergy(double joules)`). Adding to an empty atmosphere is a no-op in the second form; the first form has no such guard. **Temperature is derived from energy + heat capacity** via `IdealGas.Energy(HeatCapacity, T)`, so depositing energy implicitly raises temperature unless there is no gas to hold it.

`TransferEnergyTo(ref GasMixture target, MoleEnergy signedEnergyToTransfer)` (line 358893) and `(HeatSink target, MoleEnergy)` (line 421749) are the conduction primitives; they call `RemoveEnergy` on the source and `AddEnergy` on the destination, clamping by both sides' available capacity. Heat exchangers (`HeatExchangerBase`, `WallCooler`), pipe heaters, and the rocket thermal plant use `TransferEnergyTo`.

Typical caller form (one of dozens across the assembly):

```csharp
atmosphere.GasMixture.AddEnergy(new MoleEnergy(usedPower * EnergyToHeatRatio));
```

## Global / planetary atmosphere energy

For deposits to the unenclosed world atmosphere outside any sealed room, there is a separate static API at `PlanetaryAtmosphereSimulation`:

```csharp
public static void AddEnergy(MoleEnergy energy)
{
    if (!IsGlobalInteraction || energy <= MoleEnergy.Zero) return;
    lock (GlobalInteraction)
    {
        ExternalInputEnergyOffset += energy;
    }
}
```

(decompile line 200541). This is the path for "the whole planet absorbed N joules" rather than per-cell deposits. Power dissipation should NOT use this path: it is for global thermal accounting (sun, planet-wide events). Use the per-cell `GasMixture.AddEnergy` instead.

## Resolving "which atmosphere am I in"
<!-- verified: 0.2.6228.27061 @ 2026-05-22 -->

Three resolution paths exist, gated by call context:

1. **Per-cell local (server, main / atmospherics thread)**: `base.AtmosphericsController.SampleGlobalAtmosphere(WorldGrid)` (read-only, returns the local atmosphere if one exists, falls back to a read-only global wrapper) or `base.AtmosphericsController.CloneGlobalAtmosphere(WorldGrid, 0L)` (writable, **server-only**: throws if called on a client, see line 184887 `throw new System.Exception("Clone Global Atmosphere called on Client")`).

2. **Per-cell local (thread-safe form)**: `AtmosphericsManager.CloneGlobalAtmosphereThreadSafe(WorldGrid)` (line 418542) takes the `AllWorldAtmospheresLookUp` lock before delegating to `CloneGlobalAtmosphere`. Use this from atmospheric worker threads or anywhere the lock invariant matters.

3. **Through a device**: a `Device` has `WorldAtmosphere` (cached, refreshed from `CloneGlobalAtmosphere`), `InternalAtmosphere` (its own sealed volume if any, e.g. canister, pipe-mounted internal), and `NetworkAtmosphere` (the connected pipe network's atmosphere via `DevicePipeMounted` -> `NetworkAtmosphere`). A pipe-mounted heater deposits into `NetworkAtmosphere`; a device that heats the room around it deposits into `WorldAtmosphere`.

The cell map is `private static readonly Dictionary<WorldGrid, Atmosphere> AllWorldAtmospheresLookUp` inside `AtmosphericsManager` (line 417826), and `Find(WorldGrid)` is a locked `TryGetValue`. See `../GameClasses/Atmosphere.md` for the full Room <-> Atmosphere binding.

`Atmosphere.IsAboveArmstrong()` (the boiling-pressure-of-water threshold) is the conventional gate before depositing heat into the world atmosphere: in near-vacuum there is nothing to hold the energy, so most devices skip the deposit (`if (atmosphere.IsAboveArmstrong())`).

## Device.OnAtmosphericTick: the canonical convection-from-electrical-load pattern
<!-- verified: 0.2.6228.27061 @ 2026-05-22 -->

The single most-replicated pattern in the codebase: a `Device`'s `EnergyToHeatRatio` (default 0.2) multiplied by `UsedPower` is added to the world atmosphere each atmospheric tick when the device is on, powered, and in a non-vacuum cell. This lives on `Device` itself (line 350985), so every `Device` subclass inherits it.

Verbatim from `Device.cs` (`Assembly-CSharp.dll` decompile line 349717 and 350985-350997):

```csharp
protected virtual float EnergyToHeatRatio => 0.2f;
...
public override void OnAtmosphericTick()
{
    base.OnAtmosphericTick();
    if (OnOff && Powered && (object)PowerCable != null && EnergyToHeatRatio > 0f
        && base.AtmosphericsController.SampleGlobalAtmosphere(base.WorldGrid).IsAboveArmstrong())
    {
        Atmosphere atmosphere = base.AtmosphericsController.CloneGlobalAtmosphere(base.WorldGrid, 0L);
        float usedPower = GetUsedPower(PowerCable.CableNetwork);
        if (usedPower > 0f)
        {
            atmosphere.GasMixture.AddEnergy(new MoleEnergy(usedPower * EnergyToHeatRatio));
        }
    }
}
```

Notes on the formula:

- `UsedPower` is the device's per-tick power demand in watts. The `MoleEnergy` constructor takes joules. Since one `AtmosphericTick` corresponds to `AtmosphericsManager.Instance.TickSpeedSeconds` of game time (typically 0.5 s on default settings, see `ThreadedManager.TickSpeedSeconds => (float)TickSpeed / 1000f` at line 202278), this is dimensionally watts * unitless ratio passed as joules without a per-tick multiplier. **The game treats `UsedPower * EnergyToHeatRatio` as a joules-per-tick quantity, not strictly watts**; the ratio absorbs the tick-time conversion. A mod adding heat from `CableNetwork.Required` (watts) similarly does not need to multiply by `TickSpeedSeconds` to match vanilla intensity.
- `EnergyToHeatRatio = 0.2f` is the base default. Subclass overrides observed:
  - `Battery` and `AreaPowerControl` (`ElectricalInputOutput`): `0f` (storage devices do not radiate heat from throughput).
  - `Transformer`: `0.05f` (5% of throughput becomes heat).
  - All other `Device` subclasses inherit `0.2f` unless explicitly overridden.
- The deposit goes into the **world atmosphere of the device's own cell** (`WorldGrid`), not the room atmosphere directly. The room's gas mixture is composed of its grids' atmospheres; depositing into one cell raises that cell's temperature and conduction propagates it. There is no "deposit to the room's GasMixture" API.

## Other deposit shapes observed in vanilla
<!-- verified: 0.2.6228.27061 @ 2026-05-22 -->

Beyond the `Device.OnAtmosphericTick` pattern, the assembly contains these distinct deposit shapes:

**Constant-rate world deposit (NetworkAtmosphere)**: `PipeHeater` (line 363815-363842):

```csharp
public float HeatTransferJoulesPerTick = 1000f;

public override void OnAtmosphericTick()
{
    base.OnAtmosphericTick();
    if (OnOff && Powered && base.NetworkAtmosphere != null && base.NetworkAtmosphere.IsValid()
        && base.NetworkAtmosphere.IsAboveArmstrong()
        && base.NetworkAtmosphere.Temperature < WallHeater.MAXTemperature)
    {
        base.NetworkAtmosphere.GasMixture.AddEnergy(new MoleEnergy(HeatTransferJoulesPerTick));
        _powerUsedDuringTick = HeatTransferJoulesPerTick;
    }
}
```

Note the temperature ceiling (`< WallHeater.MAXTemperature`) and the deposit into `NetworkAtmosphere` (the pipe network's gas), not `WorldAtmosphere`.

**Per-power deposit with PowerEfficiency**: `BurningFurnace`-style at line 279717:

```csharp
base.WorldAtmosphere.GasMixture.AddEnergy(new MoleEnergy(UsedPower * PowerEfficiency));
```

The ratio is a named `PowerEfficiency` field instead of the inherited `EnergyToHeatRatio`.

**Constant lit/burning marker**: `Grenade` / similar burning items at line 306071:

```csharp
base.WorldAtmosphere.GasMixture.AddEnergy(new MoleEnergy(100.0));
```

**Combustion `OnFireConsume`**: the universal fire-consumption path (`Thing.OnFireConsume`, line 302453) locks the atmosphere, removes oxygen, adds CO2, and adds `heatEnergyReleased` as energy:

```csharp
lock (atmosphere)
{
    if (atmosphere.GasMixture.Oxygen.Quantity > FireConsumeMoleAmount)
    {
        atmosphere.GasMixture.Oxygen.Remove(FireConsumeMoleAmount);
        atmosphere.GasMixture.CarbonDioxide.Add(FireConsumeMoleAmount, MoleEnergy.Zero);
        atmosphere.GasMixture.AddEnergy(new MoleEnergy(heatEnergyReleased));
        return true;
    }
    ...
}
```

The `lock (atmosphere)` is the per-atmosphere lock used for cross-thread mutation; see "Threading" below.

**Battery base atmospheric tick (heat-from-power-discharge in cold)**: `Battery.OnAtmosphericTick` at line 371140-371160 (despite the `EnergyToHeatRatio => 0f` override, the base battery does dump heat into its world cell when discharging, but ONLY when above Armstrong AND temperature below freezing point, scaling with one-atmosphere-clamped ratio). The deposit is the cold-loss energy (`PowerStored -= num; atmosphere.GasMixture.AddEnergy(new MoleEnergy(num));`).

**Lungs / Entity body heat**: `Lungs.TakeBreath` (line 284520-284526) and `Entity.EnergyReleasedPerTick = 100f` (line 338712). Human body heat is `EnergyReleasedPerTick` added to the **lung atmosphere** (the entity's internal pocket), not to the world / room. The room only receives this via exhalation conduction back through `BreathingAtmosphere`. So `HumanPlayer`'s "warms the room" effect is indirect.

**Pipe heat conduction along network** (the closest analog to "compute across N segments"): `AtmosphereHelper.GetConvectionHeat(a, b, area)` returns the joules transferable per call; the caller (e.g. `HeatExchangerBase` line 358781, `WallHeatExchanger` line 366744) multiplies by `AtmosphericsManager.Instance.TickSpeedSeconds` and feeds it into `TransferEnergyTo`. There is **no aggregate "loop over all pipe segments, deposit to each"** primitive; each device computes its own per-tick exchange against its `InputNetwork.Atmosphere` / `OutputNetwork2.Atmosphere`. A "cable network heat dissipation" feature has no direct vanilla precedent: the closest is the pipe-heater-per-network pattern, which is still device-driven, not network-driven.

## Device table
<!-- verified: 0.2.6228.27061 @ 2026-05-22 -->

| Device | Formula (joules per atmospheric tick) | Target atmosphere | Resolution | Override on EnergyToHeatRatio | Notes |
|---|---|---|---|---|---|
| Generic `Device` (default base) | `UsedPower * 0.2f` | `WorldAtmosphere` of device cell | `CloneGlobalAtmosphere(WorldGrid, 0L)` | inherits 0.2 | Gated on `OnOff && Powered && PowerCable != null && IsAboveArmstrong` |
| `Transformer` | `UsedPower * 0.05f` | `WorldAtmosphere` | same | overrides to 0.05 | Inefficiency-as-heat |
| `Battery` | `0f` from base; cold-discharge path adds `num` joules (50 in cold, 10 normal) on top in its own override | `WorldAtmosphere` | `CloneGlobalAtmosphere` | overrides to 0 (skips base path) | Cold-only path lives in `Battery.OnAtmosphericTick` itself |
| `AreaPowerControl` | `0f` | n/a | n/a | overrides to 0 | Storage / passthrough |
| `PipeHeater` | `1000f` constant when on+powered | `NetworkAtmosphere` (pipe) | inherited from `DevicePipeMounted.NetworkAtmosphere` | inherits 0.2 (but pipe path uses constant) | Temperature ceiling = `WallHeater.MAXTemperature` |
| `Furnace` / burning-style | `EnergyReleasedPerTick` constant | `WorldAtmosphere` | `CloneGlobalAtmosphere` | varies | Sets `Sparked = true` |
| `Grenade` (when armed) | `100.0` constant | `WorldAtmosphere` | `CloneGlobalAtmosphere` | n/a | Sets `Sparked = true` |
| `Lungs` (Human breath) | `Entity.EnergyReleasedPerTick = 100f` | **`LungAtmosphere`** (internal) | direct ref | n/a | Room sees this only through exhalation conduction |
| `CombustionDeepMiner` | inherits combustion path (`OnFireConsume` -> `heatEnergyReleased` -> world cell) | `WorldAtmosphere` | combustion atmosphere | n/a | See `../GameClasses/CombustionDeepMiner.md` |
| `HeatExchangerBase` | `convectionHeat * TickSpeedSeconds` between `InputNetwork.Atmosphere` and `InputNetwork2.Atmosphere` | both networks (transfer) | `TransferEnergyTo` | n/a | Conduction primitive; not generation |

## Tick driver, threading, and multiplayer authority
<!-- verified: 0.2.6228.27061 @ 2026-05-22 -->

**Where `OnAtmosphericTick` runs**. The per-tick dispatcher is `AtmosphericsManager.ThingAtmosphereTick()` (line 418214):

```csharp
public static void ThingAtmosphereTick()
{
    AtmosphericThings.ForEach(ThingAtmosphereTickAction);
}
```

`ThingAtmosphereTickAction` (line 417874) invokes each thing's `OnAtmosphericTick`. The call site is in the central game tick loop at decompile line 189476:

```csharp
await UniTask.SwitchToThreadPool();     // line 189436
...
if (RunSimulation)
{
    AtmosphericsManager.BeforeAtmosphericsTick();
    AtmosphericsController.World.DoAtmosphereMixJobs();
    AtmosphericsController.World.RunInternalReactionsJobs();
    SubmergedHandler.Tick();
    AtmosphericsManager.ThingAtmosphereTick();   // <-- THIS CALL
    AtmosphericsManager.LifeTicksTick();
    AtmosphericsManager.AtmosphericsNetworksTick();
    ...
}
```

So:

- **`OnAtmosphericTick` runs on a ThreadPool worker, not the Unity main thread.** A `Device` patch that touches Unity APIs (transforms, materials, GameObject lifecycle) directly from inside an `OnAtmosphericTick` override will fail or race; marshal via `UnityMainThreadDispatcher.Instance().Enqueue(...)` if needed.
- **`OnAtmosphericTick` runs only when `RunSimulation` is true**, which means **host only in multiplayer** (single-player counts as host). Clients do not invoke `OnAtmosphericTick`. The atmosphere state is then replicated to clients via `AtmosphericsManager.SerialiseDeltaStateAction` (line 418011) on the per-tick delta packet.
- **`CloneGlobalAtmosphere` throws on client**: `if (Assets.Scripts.Networking.NetworkManager.IsClient) throw new System.Exception("Clone Global Atmosphere called on Client")` (line 184887). A heat-emission patch that runs on both sides would crash on the client.

**Thread-safety of `GasMixture.AddEnergy`**. The method is not internally locked. Concurrent access is governed by two patterns:

1. The atmosphere is per-cell; the worker dispatcher routes a given `Atmosphere` to one worker per tick (`AtmosphericsWorker.Assign(atmosphere)`). Within one tick, only one worker mutates a given atmosphere's GasMixture.
2. For cross-thread main-thread events (e.g. construction, ignition, mod-driven sparks), the `AtmosphericEventInstance.CreateAddEnergy` / `CreateRemoveEnergy` / `CreateAdd` / `CreateRemove` queue (line 184227-184329) defers the mutation to the main-thread `AtmosphericEventInstance.HandleNetworkChangedEvents()` pump in `AtmosphericsController.HandleMainThreadEvents()` (line 184948). This is the "I want to deposit energy but I am on the wrong thread or in the wrong phase" escape hatch:

```csharp
public static void CreateAddEnergy(Atmosphere atmosphere, MoleEnergy energy, bool spark)
{
    if (GameManager.RunSimulation)
    {
        AddEvent(new AtmosphericEventInstance(AtmosphericEventAction.AddEnergy, atmosphere, energy, spark));
    }
}
```

The event-apply (line 184573) reuses `GasMixture.AddEnergy` and additionally marks the atmosphere `Sparked` if requested. Use the event form when:
- You are on the main thread and need to defer to the atmospherics phase.
- You hold an `Atmosphere` reference but cannot guarantee phase ordering.
- You want sparks (auto-ignition).

Use direct `atmosphere.GasMixture.AddEnergy(new MoleEnergy(x))` when:
- You are inside an `OnAtmosphericTick` override (on the worker thread, in the right phase, your cell's atmosphere is already assigned to your worker).
- You do not need a spark.

**Save / load**. `GasMixture` is serialised as part of the per-atmosphere save record (`AtmosphericsManager.SerialiseOnJoin` at line 417994, `Atmosphere.Write` / `Atmosphere.Read` family). The internal energy `_energy` / `TotalEnergy` is therefore persisted across save / load and replicated to clients on join. Heat deposited via `AddEnergy` is durable; it does not need a separate save hook.

## Patterns for adding heat from outside an `OnAtmosphericTick`
<!-- verified: 0.2.6228.27061 @ 2026-05-22 -->

A "per-CableNetwork dissipation" feature does not have a vanilla per-tick callback on `CableNetwork`. The available hooks, ranked by fit:

1. **Iterate `CableNetwork` from inside an `OnAtmosphericTick` postfix on a sentinel device**. Bad: introduces ordering coupling on which device is the sentinel; the device might not be on the network being dissipated.

2. **Iterate `CableNetwork.AllCableNetworks` from a periodic main-thread tick**. Cable resistance / dissipation is conceptually per-network, not per-device; pull the aggregate from `CableNetwork.Consumed` (or `Required`) and the cable list, compute per-segment heat in joules, then deposit. Three sub-options for where to deposit:

   a. **One deposit per cable** (most physical): for each `Cable c in network.CableList`, resolve `c.GridController.AtmosphericsController.CloneGlobalAtmosphere(c.WorldGrid, 0L)` and `AddEnergy(new MoleEnergy(perCableJoules))`. Cost: O(N) `CloneGlobalAtmosphere` calls per network per tick. Each call is a dictionary lookup plus an optional `new Atmosphere(...)` allocation on first access.

   b. **Aggregate by atmosphere cell** (cheaper): pre-group cables by `WorldGrid` (or by `Atmosphere` reference), call `AddEnergy` once per unique cell with the summed joules.

   c. **Deposit only to one canonical cell** (cheapest, least physical): e.g. the lowest-id cable's cell, or the cell of the highest-load consumer. Easy but breaks "long cable run heats up along its length".

3. **Hook into the existing per-cable `OnAtmosphericTick`**. Cable is `SmallGrid`, not `Device`; it does NOT have an `OnAtmosphericTick` override and is not in `AtmosphericThings`, so this is not directly available. Adding cables to `AtmosphericsManager.AtmosphericThings` is one option but requires the cable to implement / register itself like a `Device`.

4. **Replace / shadow `PowerTick`**. `Re-Volt` already does this (`RevoltTick : PowerTick`) — see `../GameClasses/Cable.md`. A heat patch could piggyback on the same `PowerTick.Run` postfix and walk each network's `CableList` to deposit per-cable. Runs on the power tick thread; same threading caveats as `OnAtmosphericTick`. See `../GameSystems/PowerTickThreading.md` for the power tick threading contract.

5. **Use `AtmosphericEventInstance.CreateAddEnergy`** to defer the deposit to the main-thread atmospheric event pump from anywhere (including a power-tick callback). Trades direct mutation for queue cost; safest cross-phase.

**Recommendation for a per-`CableNetwork` heat dissipation feature**: pattern 4 (postfix on `PowerTick.Run` or whatever consumed-power calculation already exists in this mod's `Power Grid Plus`) combined with pattern 2b (aggregate-per-cell deposit via `AddEnergy` on the worker thread, since `PowerTick` already runs there). Aggregating by cell minimizes `CloneGlobalAtmosphere` calls. If the dispatch is from outside an atmospheric / power tick phase, use `AtmosphericEventInstance.CreateAddEnergy` instead.

**Multiplayer**: gate every deposit on `RunSimulation` (`GameManager.RunSimulation`) so it runs only on the host. Client atmospheres receive the energy via the standard delta-state replication. Never call `CloneGlobalAtmosphere` from a client; use `SampleGlobalAtmosphere` (read-only) or skip the path entirely on `NetworkManager.IsClient`.

## Performance shape
<!-- verified: 0.2.6228.27061 @ 2026-05-22 -->

`Device.OnAtmosphericTick` runs **per registered atmospheric thing per atmospheric tick**, on the ThreadPool. With N devices and one atmospheric tick per 0.5 s (default), the per-device cost is one `SampleGlobalAtmosphere` (dictionary lookup) + one `CloneGlobalAtmosphere` (dictionary lookup + occasional allocation) + one `AddEnergy` (a denormal / NaN check + a field add). A single network with K cables, billed per-cable in a heat-dissipation feature, multiplies that K-fold.

`AllAtmospheres` is a `ConcurrentDensePool` (line 417824) sized for 65535. `AtmosphericThings` is a `DensePool` sized for 32768. Iteration is a straight foreach over a contiguous backing buffer; the per-iteration cost is small but multiplied by tick rate.

A cable count in the thousands per network is realistic in late-game. Aggregating by `WorldGrid` (pattern 2b above) collapses K cable-deposits into M cell-deposits where M is bounded by the network's cell footprint. For a one-dimensional long cable run this still O(K), but cables sharing a cell (cable junctions, dense panels) collapse meaningfully.

`AtmosphericsManager.TickSpeed` is configurable; do not assume 500 ms. Use `AtmosphericsManager.Instance.TickSpeedSeconds` when a feature's heat rate is expressed in watts. Vanilla `EnergyToHeatRatio` does not multiply by `TickSpeedSeconds`; if it did, a higher tick rate would also raise the per-tick deposit. The implicit contract is "the ratio is calibrated for the default tick rate".

## Verification history

- 2026-05-22: page created from a heat-emission API survey for a planned per-`CableNetwork` cable-dissipation feature in `Mods/PowerGridPlus`. Sourced verbatim from `.work/decomp/0.2.6228.27061/Assembly-CSharp.decompiled.cs` lines listed in `sources:`. New page; no conflicts with existing central content (cross-checked against `Research/GameClasses/Atmosphere.md`, `Research/GameSystems/DevicePowerDraw.md`, `Research/GameSystems/PowerTickThreading.md`, `Research/GameClasses/CombustionDeepMiner.md`, `Research/GameClasses/PipeIgniter.md`).

## Open questions

- Confirm exact dimensionality of `EnergyToHeatRatio * UsedPower`. The argument to `new MoleEnergy(...)` is joules per docs, but the deposit happens once per atmospheric tick (~0.5 s default). The decompiled formula does not multiply by `AtmosphericsManager.Instance.TickSpeedSeconds`, so the "ratio" effectively encodes a per-tick fraction rather than a thermodynamic efficiency. A future verification pass could measure observed temperature rise on a known load and confirm the implicit tick-scale.
- Whether `Cable` instances can be added to `AtmosphericThings` to gain a per-cable `OnAtmosphericTick` without subclassing `Device`. The `AtmosphericsManager.Register` path (called from `Device.OnRegistered` line 351004) is the entry point; whether it tolerates a non-Device `Thing` registration safely is not yet verified.
