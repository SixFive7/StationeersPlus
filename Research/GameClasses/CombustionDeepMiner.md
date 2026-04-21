---
title: CombustionDeepMiner
type: GameClasses
created_in: 0.2.6228.27061
verified_in: 0.2.6228.27061
verified_at: 2026-04-20
sources:
  - rocketstation_Data/Managed/Assembly-CSharp.dll :: Assets.Scripts.Objects.Pipes.CombustionDeepMiner
  - rocketstation_Data/Managed/Assembly-CSharp.dll :: Assets.Scripts.Objects.Pipes.DeepMiner
  - rocketstation_Data/Managed/Assembly-CSharp.dll :: Assets.Scripts.Objects.Pipes.InternalCombustion
  - rocketstation_Data/Managed/Assembly-CSharp.dll :: Assets.Scripts.Atmospherics.GasMixture.Combust
  - rocketstation_Data/Managed/Assembly-CSharp.dll :: Assets.Scripts.Atmospherics.Combustion
  - rocketstation_Data/Managed/Assembly-CSharp.dll :: Assets.Scripts.Atmospherics.Mole.Enthalpy
related:
  - ../GameSystems/LogicType.md
tags: [logic, ic10]
---

# CombustionDeepMiner

Vanilla game class behind the in-game "Combustion Deep Miner." An internal-combustion drill: two pipe inputs (fuel + oxidizer arrive already mixed in the pipe network), two player-facing "levers" (`Throttle` and `CombustionLimiter`), an internal 2 L combustion chamber with ignition threshold and stress-based failure.

## Class hierarchy
<!-- verified: 0.2.6228.27061 @ 2026-04-20 -->

```csharp
// Assembly-CSharp.dll :: Assets.Scripts.Objects.Pipes.CombustionDeepMiner
public class CombustionDeepMiner : DeepMiner, IInternalCombustion, IReferencable, IEvaluable
```

Chain: `CombustionDeepMiner` -> `DeepMiner` -> `DeviceInputOutputImportExport` -> (Device / Machine). Internal chamber is inherited from `DeepMiner.InitInternalAtmosphere()` at 2.0 L volume. The combustion state machine is delegated to an `InternalCombustion _internalCombustion` field rather than lived in the deep-miner class itself.

## The two levers
<!-- verified: 0.2.6228.27061 @ 2026-04-20 -->

Both levers live on `InternalCombustion`, clamp 0..100, snap to multiples of 10, and expose themselves through `LogicType.Throttle` / `LogicType.CombustionLimiter`. The deep miner does not expose `LogicType.Setting` (blocked by `CanLogicRead` / `CanLogicWrite`).

### Throttle (fuel intake valve)

Controls how much gas the chamber pulls from the input pipe each atmospheric tick. Linear.

```csharp
// InternalCombustion.Throttle
private float _throttle;
public float Throttle {
    get { return _throttle; }
    set {
        value = Mathf.Clamp(value, 0f, 100f);
        value = Mathf.Round(value / 10f) * 10f;
        float throttle = Throttle;
        _throttle = value;
        AnimateThrottle(throttle).Forget();
        if (NetworkManager.IsServer) Parent.NetworkUpdateFlags |= 4096;
    }
}
```

Read in the intake path:

```csharp
// InternalCombustion.HandleGasInput
public void HandleGasInput(PipeNetwork inputNetwork)
{
    Thing getAsThing = Parent.GetAsThing;
    if (getAsThing.OnOff && getAsThing.Powered && getAsThing.Error <= 0)
    {
        MoleQuantity transferMoles = MaxMolarInput * (Throttle / 100f);
        GasMixture gasMixture = inputNetwork.Atmosphere.Remove(transferMoles, AtmosphereHelper.MatterState.All);
        Parent.InternalAtmosphere.Add(gasMixture);
    }
}
```

`MaxMolarInput = 0.009999999776482582` moles per tick. At `Throttle=100` the chamber pulls 0.01 mol/tick; at `Throttle=50`, 0.005 mol/tick; at 0, nothing.

### CombustionLimiter (burn fraction)

Controls what fraction of the gas already inside the chamber is burned this tick. Quadratic.

```csharp
// InternalCombustion.CombustionLimiter
private float _combustionLimiter;
public float CombustionLimiter {
    get { return _combustionLimiter; }
    set {
        value = Mathf.Clamp(value, 0f, 100f);
        value = Mathf.Round(value / 10f) * 10f;
        float combustionLimiter = CombustionLimiter;
        _combustionLimiter = value;
        AnimateCombustionLimiter(combustionLimiter).Forget();
        if (NetworkManager.IsServer) Parent.NetworkUpdateFlags |= 8192;
    }
}
```

Read in the burn path:

```csharp
// InternalCombustion.ManualCombust
public void ManualCombust()
{
    _normalCombustionEnergyCache = Parent.InternalAtmosphere.CombustionEnergy;
    Parent.InternalAtmosphere.TryCombust(
        Mathf.Clamp(Mathf.Pow(CombustionLimiter / 10f, 2f) * 0.0075f, 0.001f, 1f),
        force: true
    );
}
```

Burn-fraction table (exact values of `Clamp(Pow(CL/10, 2) * 0.0075, 0.001, 1.0)`):

| CombustionLimiter | Burn fraction |
|---|---|
| 0 | 0.001 (floor) |
| 10 | 0.0075 |
| 20 | 0.0300 |
| 30 | 0.0675 |
| 40 | 0.1200 |
| 50 | 0.1875 |
| 60 | 0.2700 |
| 70 | 0.3675 |
| 80 | 0.4800 |
| 90 | 0.6075 |
| 100 | 0.7500 (not clamped; 0.75 < 1.0) |

Peak burn is 75% of the chamber per tick at Limiter=100; the 1.0 clamp is never reached.

### Interaction between the two

Throttle and Limiter are independent fields; nothing in code ties one to the other. They interact only through the shared internal atmosphere:

1. `OnAtmosphericTick` order: `ManualCombust()` -> `SpeedTick()` -> `HandleGasOutput()` -> `HandleGasInput()`.
2. `ManualCombust` burns a fraction of what is already in the chamber before new fuel enters.
3. `HandleGasOutput` vents anything above 4000 kPa (`_targetPressure`) into the output pipe (10% of overpressure per tick).
4. `HandleGasInput` then admits up to `0.01 * Throttle/100` moles of new mixed gas.

Consequences:

- **Throttle high + Limiter low**: chamber fills faster than it burns, pressure climbs past 4000 kPa, vent loop dumps unburned fuel into the output pipe (wasted).
- **Throttle low + Limiter high**: chamber is burned down to near-empty before new fuel arrives; RPM accel fails the `CombustionEnergy + _normalCombustionEnergyCache >= 30` check and stress accumulates.
- **Balanced**: the ideal is `burned moles per tick` roughly equal to `admitted moles per tick`, with pressure sitting at or just below 4000 kPa and temperature in the RPM-accel band.

## Tick pipeline
<!-- verified: 0.2.6228.27061 @ 2026-04-20 -->

```csharp
// CombustionDeepMiner.OnAtmosphericTick
public override void OnAtmosphericTick()
{
    base.OnAtmosphericTick();
    if (_internalCombustion == null) return;
    lock (_rpmStressLock)
    {
        if (!IsOperable) { _internalCombustion.HandleShutDown(); return; }
        if (!base.IsStructureCompleted) { _internalCombustion.Rpm = 0f; return; }

        _internalCombustion.ManualCombust();
        _internalCombustion.SpeedTick();

        if (!OnOff || !Powered || Error == 1) _internalCombustion.HandleShutDown();

        _internalCombustion.HandleGasOutput(base.PressurePerTick, InputNetwork, OutputNetwork);
        _internalCombustion.HandleGasInput(InputNetwork);

        if (OnOff && Powered) base.InternalAtmosphere.Sparked = true;
    }
}
```

Atmospheric tick runs at 20 Hz (0.05 s), which is the cadence both levers operate on. Lever setters only animate the visual rotation; the control value itself updates instantly.

## SpeedTick: RPM, friction, stress
<!-- verified: 0.2.6228.27061 @ 2026-04-20 -->

```csharp
// InternalCombustion.SpeedTick
public void SpeedTick()
{
    _targetPressure = RunningTargetPressure;
    float num = 0f;
    MoleEnergy moleEnergy = new MoleEnergy(30.0);
    float num2 = Rpm;

    if (Parent.InternalAtmosphere.Temperature >= new TemperatureKelvin(573.15)
        && Parent.InternalAtmosphere.CombustionEnergy + _normalCombustionEnergyCache >= moleEnergy)
    {
        num = Mathf.Lerp(0f, 12f, Mathf.Clamp01(Parent.InternalAtmosphere.Temperature.ToFloat() / 5000f));
        MoleEnergy energy = moleEnergy * num;
        Parent.InternalAtmosphere.GasMixture.RemoveEnergy(energy);
        num2 += num;
        DidCombustionLastTick = true;
    }
    else { DidCombustionLastTick = false; }

    float num3 = 0.99f;               // 1% friction / tick
    float num4 = num2 * num3;
    float num5 = num2 - num4;
    if (Parent.GetAsThing.OnOff && Parent.GetAsThing.Powered) Rpm = num4;

    float num6 = Mathf.Abs(num - num5);
    float num7 = Mathf.Lerp(0.8f, 2f, Mathf.Clamp01((Rpm - 300f) / 1200f));
    bool flag = num6 > 1f / num7;

    if (!_gainedStress && (!Parent.GetAsThing.OnOff || !Parent.GetAsThing.Powered)) flag = false;
    if (flag)  { Stress = Mathf.Clamp(Stress + (num6 - 1f) * num7, 0f, 100f); _gainedStress = true; }
    else       { Stress = Mathf.Clamp(Stress - 0.5f, 0f, 100f);              _gainedStress = false; }

    if (Stress >= 100f)
    {
        Rpm -= 40f;
        if (Parent.GetAsThing.Error == 0) OnServer.Interact(Parent.GetAsThing.InteractError, 1);
    }
}
```

Per-tick rules:

- **Ignition floor**: internal temperature must reach `573.15 K` (300 C) before combustion contributes to RPM.
- **Energy floor**: `CombustionEnergy + _normalCombustionEnergyCache >= 30 MJ` (`MoleEnergy(30.0)`). Below this, no RPM gain.
- **RPM accel coefficient**: `num = Lerp(0, 12, T / 5000)`. Temperature buys more RPM per tick, maxing at 12 RPM/tick near 5000 K.
- **Friction**: `Rpm *= 0.99` every tick. Steady-state RPM is where `num == Rpm - Rpm*0.99 == 0.01 * Rpm`, so `Rpm_steady = 100 * num`. At `num = 6`, steady-state is 600 RPM.
- **Stress threshold**: `|num - 0.01*Rpm| > 1 / Lerp(0.8, 2, (Rpm - 300) / 1200)`. The hysteresis tightens as RPM rises: at 300 RPM tolerance is `1/0.8 = 1.25`, at 1500 RPM tolerance is `1/2 = 0.5`.
- **Stress growth**: when tripped, `Stress += (mismatch - 1) * hysteresisFactor`.
- **Stress decay**: `-0.5 per tick` when not tripped.
- **Stress failure**: at 100, `Rpm -= 40` and the device's `Error` flips to 1, which also knocks `IsOperable` false and pipes through `HandleShutDown`.

## Gas output and venting
<!-- verified: 0.2.6228.27061 @ 2026-04-20 -->

```csharp
// InternalCombustion.HandleGasOutput
public void HandleGasOutput(PressurekPa pressurePerTick, PipeNetwork inputNetwork, PipeNetwork outputNetwork)
{
    PressurekPa pressureGassesAndLiquids = Parent.InternalAtmosphere.PressureGassesAndLiquids;
    if (!(pressureGassesAndLiquids <= _targetPressure))
    {
        PressurekPa pressure = pressureGassesAndLiquids - _targetPressure;
        MoleQuantity transferMoles = RocketMath.Min(
            IdealGas.Quantity(pressurePerTick, Chemistry.PipeVolume, inputNetwork.Atmosphere.Temperature),
            IdealGas.Quantity(pressure, Parent.InternalAtmosphere.Volume, Parent.InternalAtmosphere.Temperature) * 0.1
        );
        GasMixture gasMixture = Parent.InternalAtmosphere.Remove(transferMoles, AtmosphereHelper.MatterState.All);
        outputNetwork.Atmosphere.Add(gasMixture);
    }
}
```

`_targetPressure` resolves to `RunningTargetPressure` while running, defined verbatim as:

```csharp
// InternalCombustion class field
private static readonly PressurekPa RunningTargetPressure = new PressurekPa(4000.0);
```

`PressurekPa` is a struct wrapper around a raw `double _value` (1.0 in `PressurekPa` space = 1 kPa; no scaling factor), so the target is 4000 kPa exactly. No sibling constants (`StartingTargetPressure`, `IdleTargetPressure`, etc.) exist; this single value governs every running-state venting decision.

During shutdown, the target slides down proportional to RPM:

```csharp
// InternalCombustion.HandleShutDown
_targetPressure = RocketMath.Lerp(PressurekPa.Zero, RunningTargetPressure, Mathf.Clamp01(Rpm / 1000f));
```

Above the target, up to 10% of the overpressure vents per tick (bounded by pipe transfer capacity). No damage or efficiency penalty for venting; it is pure loss of unburned mixture into the output pipe.

## Logic variables
<!-- verified: 0.2.6228.27061 @ 2026-04-20 -->

```csharp
// CombustionDeepMiner.CanLogicRead / CanLogicWrite / GetLogicValue / SetLogicValue
public override bool CanLogicRead(LogicType logicType)
{
    switch (logicType) {
        case LogicType.CombustionLimiter: return true;
        case LogicType.Throttle: return true;
        case LogicType.Rpm: return true;
        case LogicType.Stress: return true;
        case LogicType.Setting:
        case LogicType.Maximum:
        case LogicType.Ratio: return false;
        default: return base.CanLogicRead(logicType);
    }
}

public override bool CanLogicWrite(LogicType logicType) => logicType switch {
    LogicType.CombustionLimiter => true,
    LogicType.Throttle => true,
    LogicType.Setting => false,
    _ => base.CanLogicWrite(logicType),
};

public override double GetLogicValue(LogicType logicType) => logicType switch {
    LogicType.CombustionLimiter => _internalCombustion.CombustionLimiter,
    LogicType.Throttle => _internalCombustion.Throttle,
    LogicType.Rpm => _internalCombustion.Rpm,
    LogicType.Stress => _internalCombustion.Stress,
    _ => base.GetLogicValue(logicType),
};
```

| LogicType | Read | Write | Notes |
|---|---|---|---|
| `Throttle` | yes | yes | Clamped 0..100, snaps to mult of 10 |
| `CombustionLimiter` | yes | yes | Clamped 0..100, snaps to mult of 10 |
| `Rpm` | yes | no | Current rotations/min |
| `Stress` | yes | no | 0..100 |
| `Setting`, `Maximum`, `Ratio` | no | no | Explicitly blocked |
| `Power`, `On`, `Error`, `Temperature`, `Pressure` | inherited | inherited (On is write) | From base `Device` / `Logicable` |

## Fuel chemistry: Combust method
<!-- verified: 0.2.6228.27061 @ 2026-04-20 -->

The deep miner routes through `GasMixture.TryCombust` -> `GasMixture.Combust` -> fuel-specific `CombustionResult.RunCombustion`. The combustion table uses `CombustionResult` records and fuel enthalpies.

```csharp
// Assets.Scripts.Atmospherics.Combustion (reaction stoichiometry + output gases)
public static readonly CombustionResult ResultHydrogenOxygen = new CombustionResult(
    fuelCount: 2.0, oxidiserCount: 1.0,
    outputs: new[] { new CombustionValue(Chemistry.GasType.Steam, 3.0) });

public static readonly CombustionResult ResultHydrogenOzone = new CombustionResult(
    fuelCount: 3.0, oxidiserCount: 1.0,
    outputs: new[] { new CombustionValue(Chemistry.GasType.Steam, 4.0) });

public static readonly CombustionResult ResultMethaneOxygen = new CombustionResult(
    fuelCount: 2.0, oxidiserCount: 1.0,
    outputs: new[] {
        new CombustionValue(Chemistry.GasType.Pollutant, 3.0),
        new CombustionValue(Chemistry.GasType.CarbonDioxide, 6.0) });

public static readonly CombustionResult ResultMethaneOzone = new CombustionResult(
    fuelCount: 3.0, oxidiserCount: 2.0,
    outputs: new[] {
        new CombustionValue(Chemistry.GasType.Pollutant, 3.0),
        new CombustionValue(Chemistry.GasType.CarbonDioxide, 6.0),
        new CombustionValue(Chemistry.GasType.Steam, 1.0) });
```

```csharp
// Assets.Scripts.Atmospherics.Mole.Enthalpy (fuel side)
public static double Enthalpy(Chemistry.GasType gasType) => gasType switch {
    Chemistry.GasType.Methane         => 286000.0,
    Chemistry.GasType.LiquidMethane   => 286000.0,
    Chemistry.GasType.Hydrogen        => 306000.0,
    Chemistry.GasType.LiquidHydrogen  => 306000.0,
    Chemistry.GasType.Hydrazine       => 306000.0,
    Chemistry.GasType.LiquidHydrazine => 306000.0,
    Chemistry.GasType.LiquidAlcohol   => 566000.0,
    _ => 0.0,
};

// Oxidiser enthalpy multiplier (applied to fuel enthalpy, not an oxidiser base energy)
public double EnthalpyMultiplier => Type switch {
    Chemistry.GasType.Oxygen              => 1.0,
    Chemistry.GasType.LiquidOxygen        => 1.0,
    Chemistry.GasType.NitrousOxide        => 2.0,
    Chemistry.GasType.LiquidNitrousOxide  => 2.0,
    Chemistry.GasType.Ozone               => 2.0,
    Chemistry.GasType.LiquidOzone         => 2.0,
    _ => 1.0,
};
```

Energy release per full reaction (stoichiometric):

| Reaction | Fuel moles | Oxidiser moles | Energy (J) | Energy / mol fuel | Energy / mol oxidiser |
|---|---|---|---|---|---|
| H2 + O2 -> steam | 2 | 1 | 612,000 | 306,000 | 612,000 |
| H2 + O3 -> steam | 3 | 1 | 918,000 | 306,000 | 918,000 |
| CH4 + O2 | 2 | 1 | 572,000 | 286,000 | 572,000 |
| CH4 + O3 | 3 | 2 | 858,000 | 286,000 | 429,000 |

### Is H2 + O3 better than CH4 + O2?

Yes, in the deep miner specifically. Three reasons, all code-backed:

1. **Higher energy per mole of fuel.** Hydrogen enthalpy is 306 kJ/mol vs methane 286 kJ/mol. Since the chamber's fuel intake budget is capped by moles (`MaxMolarInput = 0.01 mol/tick`), every mole of hydrogen admitted releases more energy than every mole of methane.
2. **Higher energy per mole of oxidiser.** Ozone's 2.0x `EnthalpyMultiplier`, combined with the 3:1 fuel:oxidiser stoichiometry, pushes energy per O3 mole to 918 kJ (vs 612 kJ per O2 mole for H2+O2). Ozone starvation is easier to avoid because each ozone mole releases more energy.
3. **Clean exhaust.** H2+O3 and H2+O2 both emit only `Steam`. CH4 burns produce `Pollutant` and `CarbonDioxide` in large mole counts, which (because the chamber is 2 L and vents at 4000 kPa) crowd the chamber and vent into the output pipe. Steam is the only vanilla output of an H2 burn, and the mole count (3 or 4) is only slightly above the fuel count (2 or 3), so the chamber fills less quickly.

Code does not model unburned-fuel penalties, rich/lean mixture penalties, incomplete combustion, or NOx. The only "efficiency" terms are (a) total combustion energy released per tick (determines RPM accel), (b) stress (determines whether RPM gets debited), and (c) venting above 4000 kPa (determines whether admitted fuel is wasted).

Caveat: the pipe network delivers whatever the player puts on the input side. The deep miner does not force a stoichiometry; a 3:1 H2:O3 input mix is the player's responsibility, built upstream with a mixer / furnace / chemistry output. Off-ratio mixes just leave leftover H2 or O3 in the chamber, which vents unburned at 4000 kPa.

## Constants summary
<!-- verified: 0.2.6228.27061 @ 2026-04-20 -->

| Name | Value | Location |
|---|---|---|
| Throttle clamp | [0, 100] step 10 | `InternalCombustion.Throttle` setter |
| CombustionLimiter clamp | [0, 100] step 10 | `InternalCombustion.CombustionLimiter` setter |
| Max molar input | 0.009999999776482582 mol/tick | `InternalCombustion.MaxMolarInput` |
| Burn fraction formula | `Clamp(Pow(CL/10, 2) * 0.0075, 0.001, 1)` | `InternalCombustion.ManualCombust` |
| Chamber volume | 2.0 L | `DeepMiner.InitInternalAtmosphere` |
| Ignition temperature | 573.15 K (300 C) | `InternalCombustion.SpeedTick` |
| Energy threshold | 30 MJ (`MoleEnergy(30.0)`) | `InternalCombustion.SpeedTick` |
| RPM accel coefficient | `Lerp(0, 12, T / 5000 K)` | `InternalCombustion.SpeedTick` |
| Friction per tick | 0.99 (1% drag) | `InternalCombustion.SpeedTick` |
| Stress hysteresis factor | `Lerp(0.8, 2, (Rpm - 300) / 1200)` | `InternalCombustion.SpeedTick` |
| Stress failure | Stress >= 100 -> -40 RPM + Error=1 | `InternalCombustion.SpeedTick` |
| Stress decay | -0.5 / tick when not tripped | `InternalCombustion.SpeedTick` |
| Target pressure | 4000 kPa | `InternalCombustion.RunningTargetPressure` |
| Vent rate | up to 10% of overpressure / tick | `InternalCombustion.HandleGasOutput` |
| Atmospheric tick rate | 20 Hz (0.05 s) | Game-wide atmosphere sim |
| H2 enthalpy | 306,000 J/mol | `Mole.Enthalpy` |
| CH4 enthalpy | 286,000 J/mol | `Mole.Enthalpy` |
| O3 enthalpy multiplier | 2.0 | `Mole.EnthalpyMultiplier` |
| H2 + O2 ratio | 2:1 | `Combustion.ResultHydrogenOxygen` |
| H2 + O3 ratio | 3:1 | `Combustion.ResultHydrogenOzone` |

## Verification history

- 2026-04-20: page created. Decompile pass against `E:\Steam\steamapps\common\Stationeers\rocketstation_Data\Managed\Assembly-CSharp.dll` via ILSpy. All section stamps set to 0.2.6228.27061.
- 2026-04-20: re-verified `RunningTargetPressure` definition. Added verbatim `private static readonly PressurekPa RunningTargetPressure = new PressurekPa(4000.0)` quote, `HandleShutDown` Lerp quote, and `PressurekPa` unit convention note to the "Gas output and venting" section. No sibling target-pressure constants exist. Section stamp unchanged (same version, same day).
- 2026-04-20: re-verified the step-of-10 rounding claim on both lever setters. Setter bodies unchanged from the page's existing quotes; order is `Clamp([0, 100])` then `Round(value / 10f) * 10f`. `CombustionDeepMiner.SetLogicValue` is a pass-through cast from double to float with no intermediate rounding. No additional Float16 quantization on the network-serialization path for `Throttle` / `CombustionLimiter` (both only set `NetworkUpdateFlags` bits, 4096 and 8192 respectively). Unity's `Mathf.Round` uses banker's rounding per Unity docs, but the governor never writes half-values so the behavior at .5 is not exercised.

## Open questions

- Drill speed / ore yield formula: the decompile points at `DeepMiner.ProgressProcessing` and `RpmNormalised(Rpm) = Rpm / 200f` with a `0.125 * RpmNormalised * deltaTime` voxel-depth step, but `DeepMiner` was not fully captured in this pass. A follow-up should verify the exact yield-per-tick formula and whether ore type / substrate hardness modifies it.
- Liquid fuels in a combustion chamber: `Mole.Enthalpy` returns the same energy for `Hydrogen` and `LiquidHydrogen`, but the `Combust` method subtracts a latent-heat term (`IdealGas.Energy(burned_fuel_moles, fuel.LatentHeat)`) when the fuel matter-state is liquid. Net effect on deep-miner RPM accel was not measured in this pass.
