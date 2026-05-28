---
title: Device power draw (which devices exceed the 5 kW normal-cable rating)
type: GameSystems
created_in: 0.2.6228.27061
verified_in: 0.2.6228.27061
verified_at: 2026-05-27
sources:
  - $(StationeersPath)\rocketstation_Data\Managed\Assembly-CSharp.dll :: Assets.Scripts.Objects.Device, AdvancedFurnace, ArcFurnace, CarbonSequester, Transformer
  - .work/decomp/0.2.6228.27061/Assembly-CSharp.decompiled.cs :: lines 349604 (Device.UsedPower), 350696 (Device.GetGeneratedPower virtual), 350705 (Device.GetUsedPower virtual), 344409+ (AdvancedFurnace), 344837+ (ArcFurnace), 345293+ (CarbonSequester), 403311 (Transformer.OutputMaximum), 138702 (IPowerGenerator interface)
  - Plans/PowerGridPlus/PLAN.md (NEW-3 / voltage-tier research)
  - ScenarioRunner power-prefab-dump (DedicatedServer/dev-plugins/ScenarioRunner)
related:
  - ../GameClasses/Cable.md
  - ../GameClasses/PowerTick.md
  - ../GameClasses/Transformer.md
tags: [power]
---

# Device power draw (which devices exceed the 5 kW normal-cable rating)

The vanilla normal/"Cable" tier ruptures at `Cable.MaxVoltage` (prefab data; the in-game normal cable is rated 5 kW). This page records what is and isn't known about per-device power draw and which devices are known to exceed 5 kW, for the "all devices on the normal network" design question in Power Grid Plus.

## Per-device draw is prefab data; only a few defaults / formulas live in code
<!-- verified: 0.2.6228.27061 @ 2026-05-12 -->

`Device.UsedPower` is `public float UsedPower = 10f;` (decompile line 349604) -- the C# default (the small "quiescent" idle draw most devices have). The actual per-prefab `UsedPower` value is serialized in the asset bundle, not in the decompile, so an exhaustive "list every device's wattage" cannot be extracted from `Assembly-CSharp` alone (use InspectorPlus on a populated save: `types=[Device], fields=[DisplayName, UsedPower, Powered, OnOff]`, then read live `GetUsedPower(network)` per device). What *is* in code is a handful of `GetUsedPower` overrides that multiply or accumulate on top of `UsedPower`, plus a couple of large constants. Those overrides are what prove the >5 kW devices exist.

## Known devices that draw far more than 5 kW
<!-- verified: 0.2.6228.27061 @ 2026-05-12 -->

- **`CarbonSequester`** (`: DeviceInputOutputImportExportElectrical`, decompile ~line 345293): `public const float POWER_PER_UNIT_CARBON = 45000f;`. The atmospheric carbon scrubber pulls **45 kW per unit of carbon processed** -- nine times the 5 kW normal-cable rating, by design. (`ArcFurnace` also has its own `POWER_PER_UNIT_CARBON = 45000f` near line 345296.)
- **`AdvancedFurnace`** (`: FurnaceBase`, decompile line 344409). `GetUsedPower`:
  ```csharp
  public override float GetUsedPower(CableNetwork cableNetwork)
  {
      if (base.PowerCable == null || base.PowerCable.CableNetwork != cableNetwork)
          return 0f;
      float num = (OnOff ? UsedPower : 0f);
      num += (float)Setting / MaxSetting * UsedPower;
      num += (float)Setting2 / MaxSetting2 * UsedPower;
      return num;
  }
  ```
  At full settings it draws up to **3 x `UsedPower`** (idle + one full "Setting" + one full "Setting2"). `UsedPower` for the Advanced Furnace prefab is well above the ~10-50 W of ordinary devices (it is the classic "needs heavy cable" machine); the multiplier alone guarantees it exceeds 5 kW at high settings.
- **`ArcFurnace`** (`: DeviceImportExport, IResourceConsumer`, decompile line 344837). `GetUsedPower` returns `UsedPower + _powerUsedDuringTick`, where `_powerUsedDuringTick += _currentRecipe.Energy` each smelting tick (and `ReceivePower` resets it to 0). Recipe energy is large; the Arc Furnace is a heavy-draw device whose draw spikes per smelt tick.
- Other high-draw machines (the `Furnace`, `Centrifuge`, `Recycler`, `IceCrusher`, `HydraulicPipeBender`, electric `DeepMiner`, `PressurantLiquidEngine`, etc.) draw their per-prefab `UsedPower` while active; several exceed 5 kW. Exact numbers need InspectorPlus / a prefab extract.

Implication: a design that puts **every** device on a single 5 kW network is not viable for endgame machines without either (a) exempting high-draw devices (a power-threshold or whitelist carve-out), or (b) accepting that those machines need a higher-rated cable -- a 45 kW Carbon Sequester physically must be fed by a >= 45 kW (heavy) cable; no transformer between it and a 5 kW cable changes that, because the *cable feeding the device* still carries the device's full draw. A transformer steps voltage between *networks*, not the load a single cable segment carries.

`Transformer.OutputMaximum = 10000f` is the C# default (decompile line 403311); the per-prefab small/medium/large transformer values are serialized, not in the decompile.

## Vanilla generator outputs (which generators feed more than 5 kW into a network)
<!-- verified: 0.2.6228.27061 @ 2026-05-12 -->

A `normal` cable ruptures on the network's *total* throughput vs the weakest cable's `MaxVoltage` (5 kW), so a generator (or a generator field) feeding more than 5 kW into a `normal` network burns it. The peak outputs below are mostly C# constants (unlike per-device `UsedPower`, which is prefab data), so they *are* extractable. "Event peak" = output during a weather event (solar storm for solar, high-wind storm for wind turbines).

| Generator | Class | Normal peak | Event peak | Source (decompile) |
|---|---|---|---|---|
| Solar Panel (all 8 fixed prefabs: basic/dual/flat/angled x standard/heavy) | `SolarPanel` | ~500 W | ~800 W + log headroom (soft-cap, `efficiencyScalar` 1.4 -> 1.6 during a weather event in `PowerGenerated()`) | `MaxPowerGenerated = 500f` (line 399768); formula in `Research/GameClasses/SolarPanel.md` |
| Portable Solar (handheld) | `PortableSolar` | ~100 W | -- (charges its own cell, not a network) | `SolarPowerMaximum = 100f` |
| Wind Turbine (small) | `WindTurbineGenerator` | ~500 W (`MAXPowerOutput`) | ~1 kW (`MaxPowerOutputStorm`); `WeatherUtilisationMultiplier = 3`, `NoiseIntensity = 10` | line 138758-138764 (`virtual` getters); `CalculateGenerationRate` clamps to `[min, max]` where `max = storm ? MaxPowerOutputStorm : MAXPowerOutput` (~line 138852) |
| **Large Wind Turbine** | `LargeWindTurbineGenerator : WindTurbineGenerator` | ~1 kW (`MAXPowerOutput`) | **~20 kW** (`MaxPowerOutputStorm = 20000f`); `WeatherUtilisationMultiplier = 20`, `NoiseIntensity = 25` | lines 138218-138242 |
| Turbine Generator (gas-pipe spinner) | `TurbineGenerator` | **~90 W** (`_generatedPower = (pressureDiff/MaxProcessing) * MaxOutputPower`) | -- | `MaxOutputPower = 90f`, `MaxProcessing = OneAtmosphere/2` (lines ~403849, ~403959) |
| Stirling Engine | `StirlingEngine : DeviceInputOutput` | **up to ~6 kW** (`maxPower`) | -- | `private float maxPower = 6000f;` (~line 402367); `MaxPower => new MoleEnergy(maxPower)` |
| Solid Fuel Generator | `SolidFuelGenerator : PowerGeneratorSlot` | **~20 kW** when burning (`GetGeneratedPower` returns `PowerGenerated` if `PoweredTicks > 0 && OnOff`) | -- | `PowerGenerated = 20000f` on `PowerGeneratorSlot` (line 400449) |
| Gas Fuel Generator | `GasFuelGenerator : PowerGeneratorPipe` | **>5 kW; scales with combustion energy** (the `GasFuelGenerator` class body is empty; power logic is in `PowerGeneratorPipe : DeviceInputOutput, IThermal`, ~line 375414 -- a heat-to-power converter, can reach tens of kW with a hot H2/O2 mix; exact cap not a single constant) | -- | `class GasFuelGenerator` (line 375700) -> `PowerGeneratorPipe` (line 375414) |
| RTG (Creative RTG; the various RTG-recipe mods make it craftable) | `RadioscopicThermalGenerator : Electrical` | **50 kW constant** (`GetGeneratedPower` returns `PowerGenerated` flat) | -- | `PowerGenerated = 50000f` (line 395568) |
| Wireless Power Receiver | `PowerReceiver : WirelessPower` | not a generator; relays whatever the paired transmitter sends, up to the link's capacity (can be large) | -- | line 386861 |

So the generators that **must sit on `heavy` cable or higher** under a "lowest tier rated for peak output" rule: **RTG** (50 kW), **Solid Fuel Generator** (~20 kW), **Gas Fuel Generator** (tens of kW), **Stirling Engine** (~6 kW, just over), and **Large Wind Turbine** (only ~1 kW normally, but ~20 kW during a high-wind event -- its *peak* is what matters). Solar panels, the small Wind Turbine, and the Turbine Generator stay on `normal`. Note the *field-total* trap: many small `normal`-rated generators on one `normal` network can sum past 5 kW and burn it even though no single unit does.

## Generator detection: two parallel mechanisms in vanilla code
<!-- verified: 0.2.6228.27061 @ 2026-05-27 -->

The game uses two independent mechanisms to mark a device as a power generator. Both must be checked to enumerate generators exhaustively; many generators use only one of the two.

**Mechanism 1: the `IPowerGenerator` interface.** Declared at decompile line 138702 in `namespace Objects`:

```csharp
public interface IPowerGenerator : IReferencable, IEvaluable
{
    float GetMaxPowerGenerated();
}
```

Implementers in vanilla (game version 0.2.6228.27061):

- `WindTurbineGenerator : Device, ..., IPowerGenerator` (decompile line 138706)
- `LargeWindTurbineGenerator : WindTurbineGenerator` (line 138218 — inherits the interface)
- `SolarPanel : Electrical, IPowerGenerator` (~line 399768; see Research/GameClasses/SolarPanel.md)

**Mechanism 2: the `Device.GetGeneratedPower(CableNetwork)` virtual.** Declared at decompile line 350696:

```csharp
public virtual float GetGeneratedPower(CableNetwork cableNetwork)
```

This is the parallel of `GetUsedPower` (line 350705) on the same class. Subclasses override it to compute per-tick power output. Vanilla classes that override it but do NOT implement `IPowerGenerator`:

- `Battery` (line 371127) — discharges generate power into the OutputNetwork side.
- `Transformer` (line 403496) — output side is "generated" relative to the upstream network.
- `WirelessPower.PowerReceiver` (line 386810) / `PowerTransmitter` (line 387028) / `PowerTransmitterOmni` (line 387268) — relays.
- `RocketPowerUmbilicalFemale` (line 148191) / `RocketPowerUmbilicalMale` (line 148761) — rocket -> ground bridge.
- `RadioscopicThermalGenerator : Electrical, IRocketInternals, IRocketComponent` (line 395566; override at 395580). The 50 kW RTG.
- `PowerGeneratorSlot : DeviceImport, ...` (line 400441; override at 400512). Base class for `SolidFuelGenerator` (line 400538).
- `PowerGeneratorPipe : DeviceInputOutput, IThermal` (line 375414; override at 375517). Base class for `GasFuelGenerator` (line 375700).
- `TurbineGenerator : Device, ISmartRotatable` (line 403819; override at 403973). The gas-pipe spinner.
- `StirlingEngine : DeviceInputOutput, IThermal` (line 402334; override at 402686).
- Two more overrides at lines 370000 and 400139 — context-dependent (Battery has two and a similar shape repeats inside the same hierarchy).

**Why this matters for tier classification.** PowerGridPlus's voltage-tier check distinguishes generators (heavy-only) from consumers (normal+ if low draw, heavy+ if high draw). A type check that only walks `IPowerGenerator` implementers misses every generator that uses `GetGeneratedPower` instead — RTG, Solid Fuel, Gas Fuel, Turbine, Stirling. To catch all generators exhaustively, the classifier must walk both: `type.GetInterfaces().Any(i => i.Name == "IPowerGenerator")` OR `type.GetMethod("GetGeneratedPower", new[] { typeof(CableNetwork) }).DeclaringType != typeof(Device)`.

## Runtime enumeration via the prefab registry
<!-- verified: 0.2.6228.27061 @ 2026-05-27 -->

`Prefab.AllPrefabs` is a static `List<Thing>` populated at world-asset load (well before the first `ElectricityTick`). Every loaded prefab the game knows about lives there, including DLC-tagged prefabs (their `Thing.DLCType` carries the DLC enum value). The list is read-only after load and safe to iterate from any thread, including the simulation-tick worker.

`ScenarioRunner` exposes a `power-prefab-dump` scenario (`DedicatedServer/dev-plugins/ScenarioRunner/ScenarioRunner/Dispatcher.cs`) that walks `Prefab.AllPrefabs` once on the first scenario tick and emits one log line per power-relevant prefab with: PrefabName, PrefabHash, full type FullName, prefab-default UsedPower, OverridesGetUsedPower flag, OverridesGetGeneratedPower flag, ImplementsIPowerGenerator flag, IsBattery / IsTransformer / IsWireless / IsAPC type-check flags, and DLC tag. Output is parseable structured text under `[ScenarioRunner] power-prefab-dump | ...` lines in the BepInEx log, framed by `START` and `END emitted=<N> totalPrefabs=<M>` markers.

A vanilla `-New Lunar` dump on game 0.2.6228.27061 reported `emitted=410 totalPrefabs=2041` (332 vanilla, 78 mod-added in the test deployment). 1631 prefabs were filtered as non-power-relevant (Things, raw items, dynamic objects, etc.).

This is the cheapest way to get an authoritative current-game list of power devices without trusting the decompile's static `UsedPower` literals (which are usually `10f` defaults overridden by prefab-asset data).

## Verification history

- 2026-05-12: page created. From a voltage-tier design check (planned mod "Power Grid Plus") against `.work/decomp/0.2.6228.27061/Assembly-CSharp.decompiled.cs`: `Device.UsedPower` default (line 349604), `AdvancedFurnace.GetUsedPower` (verbatim, ~line 344688), `ArcFurnace.GetUsedPower`/`_powerUsedDuringTick` (~line 344877+), `CarbonSequester.POWER_PER_UNIT_CARBON = 45000f` (line ~345293), `Transformer.OutputMaximum` default (line 403311). Per-device wattages confirmed to be prefab serialized data, not in the decompile.
- 2026-05-27: added two new sections, "Generator detection: two parallel mechanisms in vanilla code" and "Runtime enumeration via the prefab registry". Sourced from the same decompile: `IPowerGenerator` interface at line 138702 (namespace `Objects`), `Device.GetGeneratedPower` virtual at line 350696, and grep of every `override.*GetGeneratedPower\(CableNetwork` in the decompile yielding 15 hits across Battery, Transformer, the wireless trio, the rocket umbilical pair, RTG, PowerGeneratorSlot/SolidFuelGenerator, PowerGeneratorPipe/GasFuelGenerator, TurbineGenerator, and StirlingEngine. Runtime methodology verified by `ScenarioRunner` `power-prefab-dump` scenario (`DedicatedServer/dev-plugins/ScenarioRunner/ScenarioRunner/Dispatcher.cs`) returning `emitted=410 totalPrefabs=2041` on a vanilla `-New Lunar` start; the 410-line dump now lives at `.work/2026-05-27-pgp-device-classification/source2-runtime-dump.raw.log` for that session and is the basis of PowerGridPlus's per-device tier classification table.
- 2026-05-12: added "Vanilla generator outputs" section. Sourced from the same decompile: `SolarPanel.MaxPowerGenerated = 500f` (line 399768) + `Research/GameClasses/SolarPanel.md` formula; `WindTurbineGenerator` base getters `MAXPowerOutput => 500f` / `MaxPowerOutputStorm => 1000f` / `WeatherUtilisationMultiplier => 3f` / `NoiseIntensity => 10f` (~lines 138758-138764) and `CalculateGenerationRate` (~138817-138865); `LargeWindTurbineGenerator` overrides `MAXPowerOutput => 1000f` / `MaxPowerOutputStorm => 20000f` / `WeatherUtilisationMultiplier => 20f` / `NoiseIntensity => 25f` (lines 138218-138242); `TurbineGenerator.MaxOutputPower = 90f` + `_generatedPower = num * MaxOutputPower` (~line 403849, ~403959); `StirlingEngine.maxPower = 6000f` (~line 402367); `PowerGeneratorSlot.PowerGenerated = 20000f` + `SolidFuelGenerator : PowerGeneratorSlot` (lines 400449, 400538); `GasFuelGenerator : PowerGeneratorPipe` (lines 375700, 375414); `RadioscopicThermalGenerator.PowerGenerated = 50000f` + `GetGeneratedPower` returns it flat (line 395568+).

## Open questions

- Exhaustive list of vanilla devices and their `UsedPower` (prefab data) -- needs InspectorPlus or a prefab/asset extract.
- The small / medium / large transformer prefabs' `OutputMaximum` values (prefab data).
- The Gas Fuel Generator's exact peak output (it is a heat-to-power converter in `PowerGeneratorPipe`, not a single constant; read the full `PowerGeneratorPipe` body or measure via InspectorPlus).
