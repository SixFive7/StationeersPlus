---
title: Device power draw (which devices exceed the 5 kW normal-cable rating)
type: GameSystems
created_in: 0.2.6228.27061
verified_in: 0.2.6228.27061
verified_at: 2026-05-12
sources:
  - $(StationeersPath)\rocketstation_Data\Managed\Assembly-CSharp.dll :: Assets.Scripts.Objects.Device, AdvancedFurnace, ArcFurnace, CarbonSequester, Transformer
  - .work/decomp/0.2.6228.27061/Assembly-CSharp.decompiled.cs :: lines 349604 (Device.UsedPower), 344409+ (AdvancedFurnace), 344837+ (ArcFurnace), 345293+ (CarbonSequester), 403311 (Transformer.OutputMaximum)
  - Plans/PowerGridPlus/PLAN.md (NEW-3 / voltage-tier research)
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

## Verification history

- 2026-05-12: page created. From a voltage-tier design check (planned mod "Power Grid Plus") against `.work/decomp/0.2.6228.27061/Assembly-CSharp.decompiled.cs`: `Device.UsedPower` default (line 349604), `AdvancedFurnace.GetUsedPower` (verbatim, ~line 344688), `ArcFurnace.GetUsedPower`/`_powerUsedDuringTick` (~line 344877+), `CarbonSequester.POWER_PER_UNIT_CARBON = 45000f` (line ~345293), `Transformer.OutputMaximum` default (line 403311). Per-device wattages confirmed to be prefab serialized data, not in the decompile.
- 2026-05-12: added "Vanilla generator outputs" section. Sourced from the same decompile: `SolarPanel.MaxPowerGenerated = 500f` (line 399768) + `Research/GameClasses/SolarPanel.md` formula; `WindTurbineGenerator` base getters `MAXPowerOutput => 500f` / `MaxPowerOutputStorm => 1000f` / `WeatherUtilisationMultiplier => 3f` / `NoiseIntensity => 10f` (~lines 138758-138764) and `CalculateGenerationRate` (~138817-138865); `LargeWindTurbineGenerator` overrides `MAXPowerOutput => 1000f` / `MaxPowerOutputStorm => 20000f` / `WeatherUtilisationMultiplier => 20f` / `NoiseIntensity => 25f` (lines 138218-138242); `TurbineGenerator.MaxOutputPower = 90f` + `_generatedPower = num * MaxOutputPower` (~line 403849, ~403959); `StirlingEngine.maxPower = 6000f` (~line 402367); `PowerGeneratorSlot.PowerGenerated = 20000f` + `SolidFuelGenerator : PowerGeneratorSlot` (lines 400449, 400538); `GasFuelGenerator : PowerGeneratorPipe` (lines 375700, 375414); `RadioscopicThermalGenerator.PowerGenerated = 50000f` + `GetGeneratedPower` returns it flat (line 395568+).

## Open questions

- Exhaustive list of vanilla devices and their `UsedPower` (prefab data) -- needs InspectorPlus or a prefab/asset extract.
- The small / medium / large transformer prefabs' `OutputMaximum` values (prefab data).
- The Gas Fuel Generator's exact peak output (it is a heat-to-power converter in `PowerGeneratorPipe`, not a single constant; read the full `PowerGeneratorPipe` body or measure via InspectorPlus).
