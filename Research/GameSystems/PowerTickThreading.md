---
title: PowerTickThreading
type: GameSystems
created_in: 0.2.6228.27061
verified_in: 0.2.6403.27689
verified_at: 2026-07-13
sources:
  - Mods/PowerTransmitterPlus/RESEARCH.md:43-49
  - Mods/PowerTransmitterPlus/RESEARCH.md:596-605
  - Mods/PowerTransmitterPlus/PowerTransmitterPlus/MainThreadDispatcher.cs:7-15
  - Mods/PowerTransmitterPlus/PowerTransmitterPlus/VisualiserPatches.cs:7-11
  - .work/decomp/0.2.6403.27689/Assembly-CSharp.decompiled.cs:146848-147049, 205218, 396574-396677, 408014-408472, 416899-416911, 421445-421910, 423735-424015, 425248-425271 (generator census)
  - .work/decomp/0.2.6403.27689/Assembly-CSharp.decompiled.cs:419486 (SimpleFabricatorBase class), 420195-420220 (OnBeginSmelt / GetUsedPower / ReceivePower), ~420309 (WaitThenMake per-frame accumulation) (consumer demand census)
related:
  - ../GameClasses/PowerTransmitter.md
  - ../GameClasses/WirelessPower.md
  - ../GameClasses/WindTurbineGenerator.md
  - DevicePowerDraw.md
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

## Generator output stability census (mid-electricity-tick mutability)
<!-- verified: 0.2.6403.27689 @ 2026-07-08 -->

Because the electricity tick runs on a ThreadPool worker while the main thread keeps rendering frames, a generator's `GetGeneratedPower` is only tick-stable if the value it returns cannot change between two calls inside one electricity tick. The atmosphere tick and the electricity tick run sequentially inside the same GameTick chain (they never interleave), so a field written only in `OnAtmosphericTick` is stable for the whole electricity tick that follows it. A value recomputed per call from main-thread-mutated state is not. Census of every producer class (decompile 0.2.6403.27689):

| Class | Output source | Mutation site | Tick-stable? |
|---|---|---|---|
| `WindTurbineGenerator` (146848) + `LargeWindTurbineGenerator` (146360, no override) | `GetGeneratedPower` (147040) recomputes `CalculateGenerationRate()` per call | `WindStrength` static rewritten every frame by `WindTurbineGenerator.UpdateWind()`, called as the last line of `GameManager.Update` (205218); plus main-thread `WeatherManager` state and world atmosphere pressure | NO: recomputed per call from per-frame state |
| `SolarPanel` | per-call recompute from sun / orientation state | main-thread sun and orientation updates | NO: same class of hazard (established earlier; the mod's solar latch predates this census) |
| `PowerGeneratorPipe` (gas fuel generator) | returns field `_energyAsPower` (`GetGeneratedPower` 396574) | written only in `OnAtmosphericTick` (396677) | YES |
| `PowerGeneratorSlot` (solid fuel generator) | constant `PowerGenerated = 20000f`, gated on `PoweredTicks` (`GetGeneratedPower` 421775) | `PoweredTicks` decremented in `OnAtmosphericTick` (421868-421910) | YES |
| `StirlingEngine` | returns `EnergyAsPower` (`GetGeneratedPower` 423984) | written in `OnAtmosphericTick` (423735/424015) | YES |
| `TurbineGenerator` (small wall turbine) | returns `_generatedPower` (`GetGeneratedPower` 425271) | written in `OnAtmosphericTick` (425248) | YES |
| `RTG` | constant `PowerGenerated = 50000f` (416899; `GetGeneratedPower` 416911) | field initializer only | YES |

Consequence for PowerGridPlus: exactly two producer families need a tick-scoped first-read latch (solar, both wind turbine classes via the single base-class patch); every other generator is stable by construction and patching it would add cost for nothing. The wind half of this census shipped as `WindTurbineOutputLatchPatches.cs` in the 2026-07-08 auditor round; details and verbatim code in `../GameClasses/WindTurbineGenerator.md`.

## Consumer demand stability census (mid-electricity-tick mutability)
<!-- verified: 0.2.6403.27689 @ 2026-07-09 -->

The producer census above has a demand-side twin. A consumer's `GetUsedPower` is only tick-stable if the value it returns cannot change between two calls inside one electricity tick. Most consumers return a fixed `UsedPower` (or `OnOff ? UsedPower : 0`) and are stable by construction. The exception is the fabricator family, whose draw accumulates per rendered frame on the main thread while a production job runs.

`SimpleFabricatorBase` (decompile 0.2.6403.27689, `Assets.Scripts.Objects.Electrical`, class at line 419486) is the base of the Autolathe and every other fabricator; the Autolathe inherits this method without overriding it. Its draw method (line 420203):

```csharp
public override float GetUsedPower(CableNetwork cableNetwork)
{
    if ((object)base.PowerCable == null || base.PowerCable.CableNetwork != cableNetwork)
        return -1f;
    if (!OnOff)
        return _powerUsedDuringTick;
    return UsedPower + _powerUsedDuringTick;
}
```

`_powerUsedDuringTick` is a per-frame accumulator: it is added to each FixedUpdate frame from inside the production coroutine `WaitThenMake` (~line 420309) and reset to `0f` in `ReceivePower` (lines 420216-420220, called at the ENFORCE phase of the tick). `OnBeginSmelt` (line 420195) starts `WaitThenMake` when `GameManager.RunSimulation && OnOff && Powered && !ExportSlot.Occupant && IsStructureCompleted` and no job is already pending; the print path is `Activate=1 -> OnBeginSmelt -> WaitThenMake`. So while a print runs the return value steps upward once per frame: it is NOT tick-stable, and OBSERVE, the allocator's GATHER, and ENFORCE's re-read inside one electricity tick can each see a different number.

Note the foreign-network sentinel. Unlike a producer's `GetGeneratedPower` (which returns `0` on a network mismatch), the fabricator returns `-1f`. Anything that latches or caches this read must gate on the same predicate vanilla uses to return the real draw (`PowerCable != null && PowerCable.CableNetwork == cableNetwork`), so it never caches the `-1f` path and then serves it to a real-network read within the tick.

The COMPLETE census (2026-07-09 whole-decompile enumeration: every `override float GetUsedPower(CableNetwork)`, all 34, classified by mutation site and thread; segmenters are cache-governed by the mod and out of scope here). Correction to the first version of this section: `Fabricator` (class 396068) is a SIBLING of `SimpleFabricatorBase` (419486) under `FabricatorBase`, with its own independent override; the earlier "Autolathe + every fabricator" reading of SimpleFabricatorBase's coverage was wrong.

Mid-tick-mutable (a MAIN-thread writer can land between two solve reads):

| Class (decl) | Override | Mutation site -> thread |
|---|---|---|
| `SimpleFabricatorBase` (419486; Autolathe, AutomatedOven, ElectronicsPrinter, OrganicsPrinter, SecurityPrinter, ToolManufactory inherit) | 420203 | `_powerUsedDuringTick +=` per frame in `WaitThenMake` (~420309, `UniTask.NextFrame`) -> MAIN; reset in `ReceivePower` (420219) |
| `Fabricator` (396068, sibling) | 396283 | `+= CurrentJob.Recipe.Energy * 0.01f` in `OnServerExportTick` (396302-396364) -> MAIN (100 ms server tick) |
| `ArcFurnace` (365208) | 365548 | `+= _currentRecipe.Energy` in `WaitThenSmelt` (365576-365621, `PlayerLoopTiming.Update`) -> MAIN |
| `IceCrusher` (380012) | 380296 | `+= EnergyPerSmelt` in `OnServerImportTick` (380247-380277) -> MAIN (its `+= EnergyForHeating` at 380203 is OnAtmosphericTick -> stable) |
| `Fermenter` (181427, Objects.Electrical) | 181583 | `= 200f` / `= 0f` in `OnServerTick` (181660-181680) -> MAIN |
| `Bench` (325406) | 325494 | the `Appliances` list (add/remove 325519/325538), each appliance's OnOff, the tablet-dock occupant battery -> MAIN (player-driven) |
| `SuitStorage` (327095) | 327442 | slot occupants + occupant battery `PowerDelta` -> MAIN (swaps) |
| `WallLightBattery` (327936) | 327980 | cell swaps -> MAIN (drain / charge are device-tick / elec-tick, stable) |
| `BatteryCellCharger` (392218) | 392271 | the `Batteries` list (392293-392308) + cell swaps -> MAIN |
| `AdvancedFurnace` (364780) | 365058 | `Setting` / `Setting2` wheels + Labeller -> MAIN |
| `VolumePump` (176245, Objects.Pipes) | 176382 | `Setting` via InteractWith (176360-176375) -> MAIN |
| `TurboVolumePump` (176149, own override) | 176232 | same, plus `BasePowerDraw` term |
| `SatelliteDish` (417919) | 418401 | `Setting` + the trading contact state (`InterrogatingContact` / `ScannedContactData`) -> MAIN |
| `RocketMiner` (389271) | 389454 | `_miningHead` swap (OnChildEnter/ExitInventory ~389395-389425) -> MAIN |

Producer-side twin: `PowerConnector.GetGeneratedPower` (408014) forwards a DOCKED `DynamicGenerator.PowerGenerated` to ANY asking network with no network guard; the dock reference (408023-408048) and the generator's OnOff / Powered gate inside `PowerGenerated` (297398-297408) are MAIN-mutable, while the underlying `_powerGenerated` is OnAtmosphericTick-written (297589/297628, stable). `DynamicComposter` (296224) is a `DraggableThing` SIBLING of `DynamicGenerator` (297342), so `newChild as DynamicGenerator` (408026) is null for it and its threaded accumulator is unreachable by the solve.

Tick-STABLE by construction, no latch warranted: `AdvancedComposter` 179738, `FridgePowered` 182033, `FiltrationMachineBase` 377868 (+ `FiltrationMachine`, `LiquidFiltrationMachine`), `IndustrialFiltration` 382268, `PipeHeater` 384822, `AirConditioner` 390078, `SpawnPointAtmospherics` 422723, `VendingMachineRefrigerated` 426480, `WallCooler` 426608, `WallHeater` 426725 (all OnAtmosphericTick-written; the atmosphere tick runs sequentially BEFORE the electricity tick on the same worker, 204418/204458/204466); `DroidSleeper` 181250 and `PowerTransmitterOmni` 408690 (`OnPowerTick`-written, and the device tick runs AFTER the electricity solve, so the value is constant across the solve's reads); `LiquidDrain` 186099 and the base `Device` 371510 (constants). `ElevatorShaftNetwork.GetUsedPower` (395878-395890) has ZERO callers in the whole decompile: dead code.

Consequence for PowerGridPlus: every class in the mid-tick-mutable table carries a tick-scoped first-read latch (`ConsumerDemandLatchPatches` for `SimpleFabricatorBase`, `ConsumerDemandLatchesExtended` for the other thirteen, `PowerConnectorOutputLatchPatches` for the producer gap), keyed by `ReferenceId`, engaged only on the device's own-network path while `GameManager.RunSimulation` is true, so OBSERVE, GATHER, and ENFORCE see one number per device per tick. The latches are demand-coherence hygiene: the device-reboot symptom itself (a demand spike letting vanilla ApplyState un-power the device for one tick) is closed structurally by the mod's net-liveness Powered ownership (Powered is decided per network, never per device demand; POWER.md section 0 decisions 19-21), so a torn read on an unlatched third-party class degrades to a one-tick allocation wobble, never a depower.

## _powerUsedDuringTick synchronization census (no volatile, no lock, no Interlocked)
<!-- verified: 0.2.6403.27689 @ 2026-07-12 -->

The accumulator behind the consumer census above has NO synchronization of any kind. Whole-decompile enumeration (0.2.6403.27689):

**Declarations.** The field is declared 17 times independently, once per class; it does not live on a shared base (`DroidSleeper` derives directly from `Device` and declares its own copy, so `Device` carries no such field). Every declaration is a plain instance `float`: never `volatile`, never `static`, never `double`. 16 are `private`; the one `protected` exception is `FiltrationMachineBase` (377803), inherited by `FiltrationMachine` (377798, empty subclass) and `LiquidFiltrationMachine` (175793, writes it at 175803/175807). There is no `PowerUsedDuringTick` property wrapper anywhere (zero matches). Declaring classes with declaration lines: AdvancedComposter 179582, DroidSleeper 181202, Fermenter 181435, FridgePowered 181976, DynamicComposter 296259, ArcFurnace 365233, FiltrationMachineBase 377803, IceCrusher 380029, IndustrialFiltration 382135, PipeHeater 384784, AirConditioner 389863, Fabricator 396079, SimpleFabricatorBase 419518, SpawnPointAtmospherics 422355, VendingMachineRefrigerated 426120, WallCooler 426552, WallHeater 426653.

**Synchronization primitives.** `Interlocked` appears ZERO times in the entire decompiled assembly; the game has never used an atomic primitive. None of the 16 `ReceivePower` overrides, 16+ `GetUsedPower` overrides, or any accrual site (`OnServerTick` family, `WaitThenMake` / `WaitThenSmelt` coroutines, `OnAtmosphericTick` bodies, `OnPowerTick`) contains a `lock`. The only lock in the power-tick machinery at all is `PowerTick.Initialise` (271748-271758), which locks `CableNetwork.PowerDeviceList` / `FuseList` / `CableList` while snapshotting the lists; it guards no device field.

**Writer-thread classification.** Three writer families:

- POWER WORKER (sequenced with the solve, same thread): the reset `_powerUsedDuringTick = 0f;` in all 16 `ReceivePower` overrides, executed synchronously from `PowerTick.ApplyState -> ConsumePower -> device.ReceivePower` (call sites 271820-271832, driven from 271903-271929). Proof this chain is off the main thread: the same `ApplyState` cannot write Powered directly and marshals it via `device.SetPowerFromThread(...)` (271933) whose body is `await UniTask.SwitchToMainThread(); SetPower(...)` (371648-371651). `DroidSleeper` additionally accrues inside `OnPowerTick` (181230/181238/181246), also on the power worker, after the solve.
- ATMOSPHERE PHASE (sequenced before the solve): the eleven `OnAtmosphericTick` writers listed as tick-stable in the consumer census (queue-drained at 34739). `GameTick` completes `ThingAtmosphereTick` before `ElectricityTick` (204458/204466), so these writes happen-before every solve read regardless of which thread drains the atmospherics queue.
- MAIN THREAD (genuinely concurrent with the solve): `Fermenter.OnServerTick` (181666/181674), `IceCrusher.OnServerImportTick` (380277), `Fabricator.OnServerExportTick` (396364), `ArcFurnace.WaitThenSmelt` (365606, resumes on `PlayerLoopTiming.Update`), `SimpleFabricatorBase.WaitThenMake` (420309, `UniTask.NextFrame`). These five are exactly the accumulator-pattern rows of the mid-tick-mutable table above.

**Reset variants a mod-owned write-back must catalog.** The standard body (14 of 16 overrides) is `base.ReceivePower(cableNetwork, powerAdded); _powerUsedDuringTick = 0f;`. Two variants:

```csharp
// SpawnPointAtmospherics (422742-422746): folds the accumulator into the delivery before zeroing.
public override void ReceivePower(CableNetwork cableNetwork, float powerAdded)
{
    base.ReceivePower(cableNetwork, powerAdded + _powerUsedDuringTick);
    _powerUsedDuringTick = 0f;
}
```

`DynamicComposter` (296224) never resets: it has no `ReceivePower` override; its accumulator is copied into `_powerGenerated` (296395) and is unreachable by the solve (the `as DynamicGenerator` sibling dead-end in the consumer census).

**How vanilla survives without synchronization.** Three layers. (1) CLR atomicity: ECMA-335 guarantees aligned loads/stores of 32-bit-or-smaller types are atomic, so a plain `float` read can never observe a torn bit pattern; corruption in the garbage-value sense is structurally impossible (this guarantee would NOT hold if the field were a `double`). (2) Phase sequencing: the reset and the atmosphere-phase accruals are ordered against the solve by the tick structure itself, so most writers are never actually concurrent with it. (3) Tolerance: the five main-thread writers race the worker's reads and its `= 0f` reset with non-atomic read-modify-writes; an interleaving can lose an increment (work done but wiped unbilled: free power) or resurrect a pre-reset value (billed again next tick: double bill), each bounded by one frame or one 100 ms slice of work, and the absence of barriers permits slightly stale reads. Vanilla never audits energy and ratio-scales forgivingly, so these leaks are invisible in the base game. Note the direction of the hazard: the blind reset destroys even increments the resetter never observed, which is why a subtract-what-you-billed write-back (drain-and-bill) is strictly safer than the vanilla reset it replaces, and a main-thread-marshaled debit (vanilla's own `SetPowerFromThread` idiom) eliminates the writer race entirely by making every mutation execute on one thread.

## PowerDeviceList lazy rebuild semantics (in-place, dirty-flag, unlocked)
<!-- verified: 0.2.6403.27689 @ 2026-07-13 -->

The `CableNetwork.PowerDeviceList` / `DataDeviceList` getters lazily rebuild on read, with ASYMMETRIC
dirty tests: the `PowerDeviceList` getter runs `if (PowerDeviceListDirty || DataDeviceListDirty)
RefreshPowerAndDataDeviceLists();` (270690-270700), while the `DataDeviceList` getter tests
`PowerDeviceListDirty` ALONE (270678-270688; verbatim bodies in the lock-census subsection below).
The refresh (270746-270787) is IN PLACE on the SAME list instances (`_powerDeviceList
.Clear()` then re-`Add`), never a reference swap, iterating `DeviceList` backwards and testing power
membership as `device.PowerCables[i].CableNetwork == this` (270772-270782; data membership the same
over `DataCables`). It takes NO lock and runs on whichever thread happens to touch a getter while a
dirty flag is set. Consequences:

- Two threads calling a getter concurrently while dirty both run the refresh over the same lists
  (duplicate entries / torn state). A reader that does `lock (net.PowerDeviceList) { copy }` is NOT
  protected either: the `lock` expression evaluates the getter FIRST (potentially running the
  unlocked refresh), and a later refresh triggered from another thread mutates the very instance
  the reader holds locked, because the refresh itself never locks.
- The race-free way to read power membership off-thread is to bypass the getter entirely: snapshot
  `DeviceList` under `lock (net.DeviceList)` (vanilla writers hold that lock) and apply the same
  `PowerCables` predicate yourself. This is what PowerGridPlus's per-tick GridSnapshot does.
- A derived network class overrides `RefreshPowerAndDataDeviceLists` (272486), so a Harmony patch
  on the base virtual would not cover every network type; the bypass above is override-proof.

### Device-list lock census: who locks which list (writers, the tick reader, the rebuild)
<!-- verified: 0.2.6403.27689 @ 2026-07-13 -->

Complete census, at 0.2.6403.27689, of every vanilla `lock` naming the `CableNetwork` list objects, of the
readers, and of the dirty-flag writers, produced by a fresh-validation pass on the rebuild semantics above.
The getters, verbatim (270678-270700):

```csharp
public List<Device> DataDeviceList
{
    get
    {
        if (PowerDeviceListDirty)
        {
            RefreshPowerAndDataDeviceLists();
        }
        return _dataDeviceList;
    }
}

public List<Device> PowerDeviceList
{
    get
    {
        if (PowerDeviceListDirty || DataDeviceListDirty)
        {
            RefreshPowerAndDataDeviceLists();
        }
        return _powerDeviceList;
    }
}
```

Note the asymmetry (fresh-validated 2026-07-13, see Verification history): only the `PowerDeviceList`
getter tests the OR of both flags; the `DataDeviceList` getter tests `PowerDeviceListDirty` alone, so a
network with `DataDeviceListDirty` set and `PowerDeviceListDirty` clear serves the stale `_dataDeviceList`
until something reads `PowerDeviceList` or dirties the power flag. The dirty test at 270694 is the ONLY
`PowerDeviceListDirty || DataDeviceListDirty` expression in the entire assembly. (`WirelessNetwork`'s
override, 272486-272492, copies ALL of `DeviceList` into `_powerDeviceList` and clears only
`PowerDeviceListDirty`, never `DataDeviceListDirty`.) The same asymmetry, with the explicit-refresh
workaround, is documented on [CableNetwork](../GameClasses/CableNetwork.md) ("Field shape and accessor
quirk").

Lock census:

- `CableNetwork.AddDevice(Cable, Device)` wraps `DeviceList.Add(device)` in `lock (DeviceList)`
  (270972-270975); `CableNetwork.RemoveDevice(Device)` wraps `DeviceList.Remove(device)` in
  `lock (DeviceList)` (271014-271017); `WirelessNetwork.AddDevice(Device)` has the same shape
  (272525-272531). These are main-thread topology events, outside the tick machinery.
- `CableNetwork.Add(Cable)` / `Remove(Cable)` wrap `CableList.Add` / `Remove` in `lock (CableList)`
  (271064-271067, 271096-271099), and the null-compaction sweep locks per removal (271255-271258); but
  `Merge(CableNetwork)` clears the losing network's list UNLOCKED (`oldNetwork.CableList.Clear()`, 271124).
- `PowerTick.Initialise` locks all three list objects while snapshotting: `lock (CableNetwork.PowerDeviceList)`
  / `lock (CableNetwork.FuseList)` / `lock (CableNetwork.CableList)` (271748-271758), on the power worker.
  This is the ONLY vanilla `lock` on `PowerDeviceList` anywhere.
- `FuseList` writers never lock: `CableFuse` self-registers with plain `FuseList.Add(this)` /
  `FuseList.Remove(this)` (392811, 392823). The `lock (FuseList)` in `Initialise` therefore excludes nothing
  that actually mutates that list.

Reader census: `PowerDeviceList` has exactly ONE vanilla reader in the whole decompile, `PowerTick.Initialise`
(the `lock` expression at 271748 plus the `AddRange` argument at 271750), so the power-list rebuild normally
runs on the power worker itself, triggered by evaluating that `lock` expression. `DataDeviceList` is read from
dozens of sites (the logic-device family plus main-thread UI such as the computer `DisplayedDevices` at
334968 / 342003), none of which lock anything; any of them can run the shared rebuild concurrently with the
power worker when the power flag is dirty. Dirty-flag writers: `DirtyPowerAndDataDeviceLists` (270816) called
from `AddDevice` 270991, virtual `AddDevice(Device)` 270996, `RemoveDevice` 271026, `Remove(Cable)` 271101,
`Merge` 271126, and `Device.InitializeDataConnection` 371613-371614; `DirtyDataDeviceList` (270822) from
`HandleDataNetTransmissionDevice` 270811.

Operative conclusions:

- `lock (net.PowerDeviceList)` in mod code buys mutual exclusion against exactly one thing: the snapshot
  window inside `PowerTick.Initialise`. It buys nothing against the rebuild (which locks nothing, and which
  the `lock` expression itself may run BEFORE the lock is acquired) and nothing against the `DeviceList`
  writers (a different lock object).
- The rebuild iterates `DeviceList` backwards (270756) WITHOUT taking the `DeviceList` lock the writers hold,
  so even the vanilla-triggered rebuild races a concurrent main-thread `AddDevice` / `RemoveDevice`.

## Consumer demand formula nuances: Fabricator vs SimpleFabricatorBase, and Fermenter's gates
<!-- verified: 0.2.6403.27689 @ 2026-07-12 -->

Exact `GetUsedPower` bodies for the accumulator classes whose formulas differ in their OFF branch
(needed verbatim by anything reconstructing demand from the accumulator, e.g. PowerGridPlus's
DemandModel):

- `SimpleFabricatorBase` (420203): foreign net -> `-1f`; `!OnOff` -> `_powerUsedDuringTick` (the
  residual accumulator still bills while switched off); else `UsedPower + _powerUsedDuringTick`.
  `ArcFurnace` (365548) and `IceCrusher` (380296) share this shape.
- `Fabricator` (396283-396294): foreign net -> `-1f`; `!OnOff` -> `0f` (the accumulator is NOT
  billed while off, unlike its SimpleFabricatorBase sibling); else `UsedPower +
  _powerUsedDuringTick`.
- `Fermenter` (181583-181594): `!OnOff || foreign || PowerCableNetwork == null ||
  !IsStructureCompleted` -> `0f`; `!IsOperable` -> `UsedPower` only; else `UsedPower +
  _powerUsedDuringTick`.

## Verification history
<!-- verified: 0.2.6403.27689 @ 2026-07-09 -->

- 2026-07-13 (later): fresh-validator RESOLUTION of the recorded getter dirty-test conflict (protocol per Research/WORKFLOW.md Rule 3). Verdict: the verbatim census quote is correct; the 2026-07-12 parent-section paraphrase was wrong. Ground truth at 0.2.6403.27689: the `DataDeviceList` getter (270678-270688) tests `if (PowerDeviceListDirty)` alone (the test at 270682); the `PowerDeviceList` getter (270690-270700) tests `if (PowerDeviceListDirty || DataDeviceListDirty)` (270694). A whole-assembly search for both flag names confirms 270694 is the ONLY OR-test anywhere; every other occurrence is the declarations (270625/270627), the per-flag tests inside the refresh body (270748/270752/270759/270772), the resets (270785/270786), the dirty setters (270818/270819/270824), and the `WirelessNetwork` override reset (272491). The "both getters test the OR" wording therefore described nothing real elsewhere in the class: a paraphrase error. Independently corroborated by ../GameClasses/CableNetwork.md "Field shape and accessor quirk" (asymmetry first recorded 2026-05-13 from the 0.2.6228.27061 decompile lines 253460-253541, re-verified 2026-07-02 at 0.2.6403.27689 lines 270678-270700). Result: parent-section paraphrase corrected in place and restamped 2026-07-13; the RECORDED CONFLICT note in the census subsection replaced with a plain asymmetry note (`WirelessNetwork` override range normalized to 272486-272492, the full method 272486 declaration through 272492 closing brace); the corresponding Open-questions bullet removed. Swept every other Research page mentioning either flag (CableNetwork.md, Motherboard.md): both already state the asymmetry correctly; no other page carried the wrong paraphrase.
- 2026-07-13: fresh-validation pass on the PowerDeviceList rebuild claim (0.2.6403.27689) added the "Device-list lock census" subsection: verbatim getter bodies (270678-270700), the complete lock census (`AddDevice` / `RemoveDevice` / `WirelessNetwork.AddDevice` lock `DeviceList` at 270972 / 271014 / 272525; `Add(Cable)` / `Remove(Cable)` / null-sweep lock `CableList` at 271064 / 271096 / 271255 while `Merge` clears unlocked at 271124; `PowerTick.Initialise` 271748-271758 is the only `PowerDeviceList` lock; `CableFuse` writes `FuseList` unlocked at 392811 / 392823), the single-reader census for `PowerDeviceList` (271748 / 271750 only) vs the many unlocked `DataDeviceList` readers, and the dirty-flag writer census (270991 / 270996 / 271026 / 271101 / 271126 / 371613-371614). CONFLICT RECORDED, not resolved: the 2026-07-12 section paraphrases both getters as testing `PowerDeviceListDirty || DataDeviceListDirty`, but the verbatim `DataDeviceList` getter tests `PowerDeviceListDirty` alone (the OR-form belongs to `PowerDeviceList` only). Both claims stand on the page pending the fresh-validator conflict protocol; also logged under Open questions. The parent section's operative conclusions (unlocked in-place rebuild; `lock (net.PowerDeviceList)` cannot exclude it) are CONFIRMED by this pass.
- 2026-07-12 (later): added "PowerDeviceList lazy rebuild semantics" (getter dirty-check 270690-270699, in-place unlocked refresh body 270746-270787 with the PowerCables membership predicate, the derived override at 272486, and the lock(DeviceList)+predicate bypass) and "Consumer demand formula nuances" (Fabricator 396283-396294 bills NO accumulator while off, unlike SimpleFabricatorBase/ArcFurnace/IceCrusher which bill the residual accumulator; Fermenter's completion + IsOperable gates 181583-181594). Read directly from the 0.2.6403.27689 decompile during the PowerGridPlus B + D1 data-plane implementation. No prior content contradicted; the consumer census rows are refined, not changed.
- 2026-07-12: added the "_powerUsedDuringTick synchronization census" section from a whole-decompile enumeration pass (0.2.6403.27689) driven by the PowerGridPlus data-plane redesign discussion. All 17 field declarations read verbatim (plain instance float, never volatile / static / double; 16 private, `FiltrationMachineBase` 377803 protected; no shared base field, proven by `DroidSleeper` declaring its own directly under `Device`; no property wrapper). Every write site classified by thread: the power-worker reset in all 16 `ReceivePower` overrides via `ApplyState -> ConsumePower` (271820-271832), with the off-main-thread proof at 271933 / 371648-371651 (`SetPowerFromThread` marshals via `SwitchToMainThread`); eleven atmosphere-phase writers (sequenced before the solve, 204458/204466); five genuinely concurrent main-thread writers matching the mid-tick-mutable accumulator rows (181666/181674, 380277, 396364, 365606, 420309). `Interlocked` confirmed at ZERO occurrences assembly-wide; no `lock` in any relevant method (the only power-machinery lock is `PowerTick.Initialise` 271748-271758 snapshotting the lists). The `SpawnPointAtmospherics` fold-then-zero variant (422742-422746) and the `DynamicComposter` never-reset variant (296395) recorded verbatim. No prior content contradicted: the consumer census's mid-tick-mutable vs tick-stable split is confirmed and extended with the declaration and reset-thread facts.
- 2026-07-09 (later): COMPLETED the consumer census from a whole-decompile enumeration (all 34 `GetUsedPower(CableNetwork)` overrides + all 15 `GetGeneratedPower` overrides classified by mutation site and thread; produced by three independent census agents for the PowerGridPlus Powered-ownership redesign and cross-checked against the mod build). CONFLICT RESOLUTION against this page's morning version: `SimpleFabricatorBase` does NOT cover "every fabricator"; `Fabricator` (class decl 396068, `public class Fabricator : FabricatorBase`, read directly this pass) is a SIBLING with its own `GetUsedPower` override (396283) and a main-thread `OnServerExportTick` accumulator (396364). Replaced the three-row table with the complete fourteen-row mid-tick-mutable set plus the PowerConnector forwarding producer, the tick-stable list with the atmos-before-electricity and device-tick-after-solve discriminators (204418/204458/204466), the `DynamicComposter` sibling dead-end (296224 vs 297342, `as DynamicGenerator` null at 408026), and the `ElevatorShaftNetwork.GetUsedPower` dead-code finding (395878-395890, zero callers). Consequence paragraph updated to the shipped latch set + the net-liveness ownership model.
- 2026-07-09: added the "Consumer demand stability census" section, the demand-side twin of the producer census. Read directly from `.work/decomp/0.2.6403.27689/Assembly-CSharp.decompiled.cs`: `SimpleFabricatorBase` class (line 419486), `OnBeginSmelt` (420195), `GetUsedPower` (420203-420214, foreign-network sentinel `-1f`, else `OnOff ? UsedPower + _powerUsedDuringTick : _powerUsedDuringTick`), `ReceivePower` reset of `_powerUsedDuringTick` (420216-420220); the per-frame `WaitThenMake` accumulation (~420309) carried from the same session's earlier read. Establishes the fabricator family as the consumer-side analogue of the solar/wind producer read-coherence hazard. No prior content contradicted; earlier sections keep their stamps.
- 2026-07-08: added the "Generator output stability census" section from the PowerGridPlus auditor-round decompile pass (0.2.6403.27689). Wind chain, pipe generator, and the LargeWindTurbineGenerator no-override claim re-read directly; solid fuel / Stirling / turbine / RTG write sites verified by the round's implementing agent at the cited lines in the same decompile file. Earlier sections untouched (their 0.2.6228.27061 stamps stand).
- 2026-05-28: added "Tick interval and the watts-vs-joules-per-tick labelling convention" section. Documents `DefaultTickSpeedMs = 500` (decompile line 188884), the call-chain `GameTick -> ElectricityManager.ElectricityTick -> CableNetwork.OnPowerTick -> PowerTick.Initialise/CalculateState/ApplyState` (line 189484), and the in-game convention that "watts" labels and "joules per tick" values are the same number numerically even though they differ by a factor of 2 in real units. This corrects a sloppy "J/tick x 2 = W" formula previously used in `Battery.md`'s rate-cap table; the in-game-displayed wattage is the field value as-is, not doubled.
- 2026-04-20: page created from the Research migration; verbatim content lifted from F0032 (primary), F0048, F0308, and F0364.

## Open questions

None.
