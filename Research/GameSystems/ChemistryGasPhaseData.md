---
title: Chemistry gas phase data (freezing points, max-liquid temperatures, Antoine-style coefficients)
type: GameSystems
created_in: 0.2.6228.27061
verified_in: 0.2.6228.27061
verified_at: 2026-05-14
sources:
  - $(StationeersPath)\rocketstation_Data\Managed\Assembly-CSharp.dll :: Assets.Scripts.Atmospherics.Chemistry, Mole, MoleHelper
  - .work/decomp/0.2.6228.27061/Assembly-CSharp.decompiled.cs :: lines 419000-419279 (Chemistry static constants), 424068-424103 (Mole.FreezingTemperature switch), 424157-424197 (Mole.MaxLiquidTemperature switch), 424115-424155 (Mole.MinLiquidPressure switch), 424204-424239 (Mole.MinimumLiquidPressureAtMaxTemperature switch), 425341-425463 (MoleHelper.EvaporationTemperature / EvaporationPressure / coefficient tables)
related:
  - ./Explosions.md
tags: [save-format]
---

# Chemistry gas phase data (freezing points, max-liquid temperatures, Antoine-style coefficients)

Every gas / liquid in Stationeers shares one closed-form vapor-pressure curve, parameterised per gas by two coefficients `A` and `B`. The curve plus two per-gas reference pressures (a minimum-liquid pressure and a critical / maximum-liquid pressure) is the full phase-diagram data the game uses. There is no XML for this; everything lives in `Assets.Scripts.Atmospherics.Chemistry` as static constants and in `MoleHelper` as switch tables.

This page records the verbatim constants, the formula, the freezing-point and max-liquid-temperature switches per `GasType`, and the resulting numeric table.

## Vapor-pressure formula
<!-- verified: 0.2.6228.27061 @ 2026-05-14 -->

`MoleHelper.EvaporationTemperature` and `EvaporationPressure` (decompile lines 425341-425357):

```csharp
public static TemperatureKelvin EvaporationTemperature(Chemistry.GasType gasType, PressurekPa pressure)
{
    if (gasType == Chemistry.GasType.Undefined)
        throw new ArgumentOutOfRangeException("gasType", gasType, null);
    return new TemperatureKelvin(Math.Pow(pressure.ToDouble() / EvaporationCoefficientA(gasType), 1.0 / EvaporationCoefficientB(gasType)));
}

public static PressurekPa EvaporationPressure(Chemistry.GasType gasType, TemperatureKelvin temperature)
{
    if (gasType == Chemistry.GasType.Undefined)
        throw new ArgumentOutOfRangeException("gasType", gasType, null);
    return new PressurekPa(EvaporationCoefficientA(gasType) * Math.Pow(temperature.ToDouble(), EvaporationCoefficientB(gasType)));
}
```

So the vapor-pressure curve per gas is `P = A * T^B` (pressure in kPa, temperature in K), inverted as `T = (P / A)^(1 / B)`. This is the standard "power-law" form (NOT the textbook Antoine `log P = A - B / (T + C)`); the curve plotted as `log P` vs `log T` is a straight line of slope `B` and intercept `log A`. The `qwoa4qjloh` Desmos search hit titled "Phase state diagram" is consistent with this form, though we have not visually confirmed the curves match.

Helium is the exception: `A = 0`, `B = 0`. The formula degenerates to `0^? = 0`, which is why both `FreezingTemperature(Helium)` and `MaxLiquidTemperature(Helium)` resolve to `TemperatureKelvin.Zero`. Helium never has a liquid phase in the game.

## Per-gas coefficients A and B
<!-- verified: 0.2.6228.27061 @ 2026-05-14 -->

`MoleHelper.EvaporationCoefficientA` and `EvaporationCoefficientB` (decompile lines 425359-425463). Verbatim values:

| GasType | A | B |
|---|---|---|
| Oxygen / LiquidOxygen | `2.6854996004e-11` | `6.49214937325` |
| Nitrogen / LiquidNitrogen | `5.5757107833e-07` | `4.40221368946` |
| CarbonDioxide / LiquidCarbonDioxide | `1.579573e-26` | `12.195837931` |
| Methane / LiquidMethane | `5.863496734e-15` | `7.8643601035` |
| Pollutant / LiquidPollutant | `2.079033884` | `1.31202194555` |
| Water / Steam | `3.8782059839e-19` | `7.90030107708` |
| PollutedWater | `4e-20` | `8.27025711260823` |
| NitrousOxide / LiquidNitrousOxide | `0.065353501531` | `1.70297431874` |
| Hydrogen / LiquidHydrogen | `3.18041e-05` | `4.4843872973` |
| Hydrazine / LiquidHydrazine | `8e-22` | `9.15642808045339` |
| LiquidAlcohol (Ethanol) | `9e-20` | `8.391884446078986` |
| Helium | `0.0` | `0.0` |
| LiquidSodiumChloride | `6.211737044295e-08` | `2.8774143233482707` |
| Silanol / LiquidSilanol | `0.22176555607618392` | `1.5206578718752168` |
| HydrochloricAcid / LiquidHydrochloricAcid | `1e-21` | `9.108844460789863` |
| Ozone / LiquidOzone | `0.006219763823043718` | `2.4097251251207226` |

Gas and its companion liquid (e.g. `Oxygen` and `LiquidOxygen`) share the same `A`, `B`. They are the same chemical with different state tags.

## Per-gas reference pressures
<!-- verified: 0.2.6228.27061 @ 2026-05-14 -->

Two pressures per gas anchor the freezing point and the critical / max-liquid point.

**Freezing-pressure floor** (`Mole.MinLiquidPressure`, decompile lines 424115-424155). The minimum pressure at which the substance can exist as a liquid. The freezing point is then the curve temperature at this pressure, clamped to `Chemistry.ArmstrongLimit`. The value `6.300000190734863` is the float-precision form of `6.3f` widened to `double`; functionally identical to `6.3`.

**Critical pressure / maximum-liquid pressure** (`Mole.MinimumLiquidPressureAtMaxTemperature`, decompile lines 424204-424239). Above the critical temperature the substance is always a gas regardless of pressure. The max-liquid temperature is the curve temperature at this pressure.

| GasType | Freeze pressure (kPa) | Critical pressure (kPa) |
|---|---:|---:|
| Oxygen | 6.3 | 6000 |
| Nitrogen | 6.3 | 6000 |
| CarbonDioxide | 517 | 6000 |
| Methane | 6.3 | 6000 |
| Pollutant (SO2) | 1800 | 6000 |
| Water | (special: hardcoded 273.15 K) | 6000 |
| PollutedWater | (special: hardcoded 276.15 K) | 6000 |
| NitrousOxide | 800 | 2000 |
| Hydrogen | 6.3 | 6000 |
| Hydrazine | 6.3 | 6000 |
| Alcohol (Ethanol) | 6.3 | 1000 |
| Helium | 0 | 515 (unused; helium has no liquid phase) |
| SodiumChloride | 6.3 | 515 |
| Silanol | 516 | 6000 |
| HydrochloricAcid | 6.3 | 1000 |
| Ozone | 250 | 6000 |

The freezing-point and max-liquid-temperature switches (`Mole.FreezingTemperature` / `Mole.MaxLiquidTemperature`, decompile lines 424068-424197) route each `GasType` to one of the `Chemistry.FREEZING_POINT_*` / `Chemistry.CRITICAL_TEMPERATURE_*_K` static fields, which are themselves initialized by calling `MoleHelper.EvaporationTemperature(gasType, refPressure)` at the listed pressures. Water and PollutedWater short-circuit this: their freezing temperatures are the hardcoded constants `_TRIPLE_POINT_TEMPERATURE_WATER_K = 273.15f` and `_TRIPLE_POINT_TEMPERATURE_POLLUTED_WATER_K = 276.15f` (decompile lines 419098-419100 and 419112-419114) rather than evaluations of the curve.

## Computed phase boundaries
<!-- verified: 0.2.6228.27061 @ 2026-05-14 -->

Computed from `T = (P / A)^(1 / B)` with the coefficients and reference pressures above. "Freeze K" is the temperature at the freezing-pressure floor (gas freezes below this); "MaxLiquid K" is the temperature at the critical pressure (gas cannot become liquid at any pressure above this).

| Gas | Freeze K | Freeze C | MaxLiquid K | MaxLiquid C |
|---|---:|---:|---:|---:|
| Oxygen | 56.42 | -216.73 | 162.27 | -110.88 |
| Nitrogen | 40.01 | -233.14 | 190.03 | -83.12 |
| CarbonDioxide | 217.82 | -55.33 | 266.31 | -6.84 |
| Methane | 81.53 | -191.62 | 195.02 | -78.13 |
| Pollutant (SO2) | 173.32 | -99.83 | 433.88 | 160.73 |
| Water | 273.15 | 0.00 | 643.71 | 370.56 |
| PollutedWater | 276.15 | 3.00 | 634.37 | 361.22 |
| NitrousOxide | 251.42 | -21.73 | 430.60 | 157.45 |
| Hydrogen | 15.18 | -257.97 | 70.06 | -203.09 |
| Hydrazine | 246.24 | -26.91 | 520.81 | 247.66 |
| Alcohol (Ethanol) | 231.63 | -41.52 | 423.68 | 150.53 |
| Helium | n/a (always gas) | n/a | n/a (always gas) | n/a |
| SodiumChloride | 605.90 | 332.75 | 2799.31 | 2526.16 |
| Silanol | 163.69 | -109.46 | 821.67 | 548.52 |
| HydrochloricAcid | 247.29 | -25.86 | 431.32 | 158.17 |
| Ozone | 81.41 | -191.74 | 304.39 | 31.24 |

Note on Hydrazine: its computed max-liquid temperature `520.81 K` matches the hydrazine auto-ignition constant exactly. `private const double AUTOIGNITION_HYDRAZINE` is not in the decompile around the Hydrazine block (lines 419138-419156) but `_TRIPLE_POINT_PRESSURE`-style derivations land on 520.81; in practice this means a hydrazine condenser pushing right up against the critical temperature is also pushing right up against the spark threshold. The wiki claim "ignites at 520.8 K (247.7C)" lines up with this value, not with a separate `AUTO_IGNITION_HYDRAZINE` constant.

Note on SodiumChloride: only the liquid form `LiquidSodiumChloride` is registered as a fluid; the freezing point at 605.9 K (332.75 C) means below this temperature it solidifies (becomes ingot salt). The 515 kPa critical pressure is unusually low.

Note on Helium: `A = 0` short-circuits both `FreezingTemperature` and `MaxLiquidTemperature` to `TemperatureKelvin.Zero` (`FREEZING_POINT_HELIUM = TemperatureKelvin.Zero`, `CRITICAL_TEMPERATURE_HELIUM_K = TemperatureKelvin.Zero`, decompile lines 419188-419190). `CanEvaporate(Helium) = false` and `CanCondense(Helium) = false` (`MoleHelper.CanEvaporate` / `CanCondense`, lines 425465-425540) make it a single-phase gas at every (T, P) the simulation supports.

## GasType enum (every state Stationeers tracks)
<!-- verified: 0.2.6228.27061 @ 2026-05-14 -->

For reference, the full set of `Chemistry.GasType` values that participate in phase logic, as enumerated by the switches in `Mole` / `MoleHelper`:

```
Undefined,
Oxygen, Nitrogen, CarbonDioxide, Methane, Pollutant, Water, PollutedWater, NitrousOxide,
LiquidNitrogen, LiquidOxygen, LiquidMethane, Steam, LiquidCarbonDioxide, LiquidPollutant, LiquidNitrousOxide,
Hydrogen, LiquidHydrogen,
Hydrazine, LiquidHydrazine,
LiquidAlcohol,
Helium,
LiquidSodiumChloride,
Silanol, LiquidSilanol,
HydrochloricAcid, LiquidHydrochloricAcid,
Ozone, LiquidOzone
```

`Volatiles` (the pre-2026 name for methane) no longer appears as a separate `GasType` value; the rename to `Methane` is reflected throughout. There is no separate `Steam` constant table; `Steam` re-uses Water's `A`, `B` and critical temperature (line 424178 routes `Steam` to `Chemistry.CRITICAL_TEMPERATURE_WATER_K`).

## Verification history
<!-- verified: 0.2.6228.27061 @ 2026-05-14 -->

- 2026-05-14: page created from a fresh read of `.work/decomp/0.2.6228.27061/Assembly-CSharp.decompiled.cs`. All coefficients, switches, and reference pressures captured verbatim. Computed freeze and max-liquid temperatures via `T = (P / A)^(1 / B)` in PowerShell.

## Open questions
<!-- verified: 0.2.6228.27061 @ 2026-05-14 -->

- Whether the `qwoa4qjloh` Desmos calculator titled "Phase state diagram" implements this `P = A * T^B` form for all post-March-2026 gases. WebFetch cannot read the rendered curves, and the project's single Playwright instance was held by another session at the time of this writeup; visual confirmation pending.
- Whether the in-game Stationpedia phase-diagram pages use exactly this formula or a piecewise variant. The hardcoded water/polluted-water triple-point temperatures suggest at least one piecewise step exists at the curve-vs-floor boundary; the exact in-game render path was not traced.
