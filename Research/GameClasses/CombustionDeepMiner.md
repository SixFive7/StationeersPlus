---
title: CombustionDeepMiner
type: GameClasses
created_in: 0.2.6228.27061
verified_in: 0.2.6228.27061
verified_at: 2026-04-24
sources:
  - rocketstation_Data/Managed/Assembly-CSharp.dll :: Assets.Scripts.Objects.Pipes.CombustionDeepMiner
  - rocketstation_Data/Managed/Assembly-CSharp.dll :: Assets.Scripts.Objects.Pipes.DeepMiner
  - rocketstation_Data/Managed/Assembly-CSharp.dll :: InternalCombustion (default namespace)
  - rocketstation_Data/Managed/Assembly-CSharp.dll :: Assets.Scripts.Objects.Pipes.IInternalCombustion
  - rocketstation_Data/Managed/Assembly-CSharp.dll :: Assets.Scripts.Objects.Pipes.DeviceInputOutput
  - rocketstation_Data/Managed/Assembly-CSharp.dll :: Assets.Scripts.Objects.Pipes.DeviceAtmospherics
  - rocketstation_Data/Managed/Assembly-CSharp.dll :: Assets.Scripts.Objects.Pipes.Device
  - rocketstation_Data/Managed/Assembly-CSharp.dll :: Assets.Scripts.Atmospherics.GasMixture.Combust
  - rocketstation_Data/Managed/Assembly-CSharp.dll :: Assets.Scripts.Atmospherics.Combustion
  - rocketstation_Data/Managed/Assembly-CSharp.dll :: Assets.Scripts.Atmospherics.CombustionResult
  - rocketstation_Data/Managed/Assembly-CSharp.dll :: Assets.Scripts.Atmospherics.Mole.Enthalpy
  - rocketstation_Data/Managed/Assembly-CSharp.dll :: Assets.Scripts.Atmospherics.MoleEnergy
related:
  - ../GameSystems/LogicType.md
tags: [logic, ic10]
---

# CombustionDeepMiner

Vanilla game class behind the in-game "Combustion Deep Miner." An internal-combustion drill: two pipe inputs (fuel + oxidizer arrive already mixed in the pipe network), two player-facing "levers" (`Throttle` and `CombustionLimiter`), an internal 2 L combustion chamber with ignition threshold and stress-based failure.

## Class hierarchy
<!-- verified: 0.2.6228.27061 @ 2026-04-21 -->

```csharp
// Assembly-CSharp.dll :: Assets.Scripts.Objects.Pipes.CombustionDeepMiner
public class CombustionDeepMiner : DeepMiner, IInternalCombustion, IReferencable, IEvaluable

// Assembly-CSharp.dll :: Assets.Scripts.Objects.Pipes.DeepMiner
public class DeepMiner : DeviceInputOutputImportExportCircuit, IGenerateMinables

// Assembly-CSharp.dll :: Assets.Scripts.Objects.Pipes.DeviceInputOutputImportExportCircuit
public class DeviceInputOutputImportExportCircuit : DeviceInputOutputImportExport, ICircuitHolder, IDensePoolable

// Assembly-CSharp.dll :: Assets.Scripts.Objects.Pipes.DeviceInputOutputImportExport
public class DeviceInputOutputImportExport : DeviceInputOutputImport

// Assembly-CSharp.dll :: Assets.Scripts.Objects.Pipes.DeviceInputOutput
public class DeviceInputOutput : DeviceAtmospherics

// Assembly-CSharp.dll :: Assets.Scripts.Objects.Pipes.DeviceAtmospherics
public class DeviceAtmospherics : Device, ISmartRotatable

// Assembly-CSharp.dll :: Assets.Scripts.Objects.Pipes.Device
public class Device : SmallGrid, ILogicable, IReferencable, IEvaluable, IConnected,
                      ISlotWriteable, IWreckage, IPowered, IDensePoolable
```

Full chain: `CombustionDeepMiner` -> `DeepMiner` -> `DeviceInputOutputImportExportCircuit` -> `DeviceInputOutputImportExport` -> `DeviceInputOutputImport` -> `DeviceInputOutput` -> `DeviceAtmospherics` -> `Device` -> `SmallGrid` -> ... -> `Thing`.

Who owns what:

- **`CombustionDeepMiner`** owns the combustion-state surface (`Throttle`, `CombustionLimiter`, `Rpm`, `Stress` logic entries and setter routing), audio (`_motorAudio`, `_stressAudio`, `_maxStressAudio`, `_gear2Audio`, `_gear4Audio`), the `_rpmStressLock`, the stress-dial UI, and the `OnAtmosphericTick` override that drives the `InternalCombustion` state machine.
- **`DeepMiner`** owns the drill-down animation (`DRILL_DOWN_SPEED = 0.125f`), voxel removal, ore spawning (`DirtyOre` prefab, `_spawnOrePrefab`), `CanMine()` / `ThingInTheWay`, `Processing` byte (0..100), `OreSpawnTime`, `_deepMinables` reagent selection from `WorldSetting.Current.Data.DeepMinablesData`, `Rpm` (virtual, returns 200 on base; overridden by `CombustionDeepMiner` to return `_internalCombustion.Rpm`), `RpmNormalised(float rpm) => rpm / 200f`, `ProgressProcessing`, and the gear/drillshaft audio + wobble animation.
- **`DeviceInputOutputImportExportCircuit`** owns the embedded `ProgrammableChip` slot circuit and import/export chute behavior.
- **`DeviceInputOutput`** owns `InputNetwork` / `InputNetwork2` / `OutputNetwork` / `OutputNetwork2` `PipeNetwork` fields and the enormous `CanLogicRead` case list for `PressureInput` / `TemperatureInput` / `RatioXxxInput` / ... / `PressureOutput` / ... variants (these are pipe-network readings, distinct from the chamber's internal readings).
- **`DeviceAtmospherics`** owns `[FormerlySerializedAs("PressurePerTick")] protected float pressurePerTick = 101.325f` and the `PressurekPa PressurePerTick => new PressurekPa(pressurePerTick)` property used by `InternalCombustion.HandleGasOutput` to cap vent transfer moles per tick. The serialized default is 101.325 kPa (1 atm) per atmospheric tick, which governs how fast overpressure can drain into the output pipe.
- **`Device`** owns `OnOff`, `Powered`, `Error`, the `InternalAtmosphere` field, and the generic `CanLogicRead` / `GetLogicValue` routing for `LogicType.Pressure` / `LogicType.Temperature` / `LogicType.TotalMoles` / `LogicType.RatioXxx` (gated on `HasReadableAtmosphere`). This is why `LogicType.Pressure` and `LogicType.Temperature` work on `CombustionDeepMiner` (which sets `HasReadableAtmosphere => true`) and return the chamber's values, not the input pipe's.

Internal chamber comes from `CombustionDeepMiner.InitInternalAtmosphere()` (the class overrides `DeepMiner`'s `InitInternalAtmosphere` with its own 2.0 L `VolumeLitres Volume` static field, not inherited via `DeepMiner.InitInternalAtmosphere`):

```csharp
// Assembly-CSharp.dll :: Assets.Scripts.Objects.Pipes.CombustionDeepMiner
private static readonly VolumeLitres Volume = new VolumeLitres(2.0);
...
public override void InitInternalAtmosphere()
{
    if (base.InternalAtmosphere == null)
    {
        base.InternalAtmosphere = new Atmosphere(this, Volume, 0L);
    }
}
```

The combustion state machine is NOT a direct method set on `CombustionDeepMiner`. It lives on a separately decompiled class `InternalCombustion` in the default (empty) namespace, referenced via `private InternalCombustion _internalCombustion`. `CombustionDeepMiner` constructs it in `Awake`:

```csharp
// Assembly-CSharp.dll :: Assets.Scripts.Objects.Pipes.CombustionDeepMiner.Awake
public override void Awake()
{
    base.Awake();
    if (!IsCursor)
    {
        _internalCombustion = new InternalCombustion(this);
    }
    ...
}
```

`InternalCombustion` takes `IInternalCombustion parent` in its constructor (`CombustionDeepMiner` implements that interface), so the helper reads `Parent.InternalAtmosphere`, `Parent.GetAsThing`, `Parent.ThrottleLever`, `Parent.CombustionLever` via the interface.

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
<!-- verified: 0.2.6228.27061 @ 2026-04-21 -->

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

`HandleGasInput` is additionally gated on `Error <= 0`:

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

Consequence: when `IsOperable` flips false (stress hit 100 and set `Error = 1`, or a `ThingInTheWay`, or an input/output not valid, or the chip errored), the tick goes directly to `HandleShutDown` and returns early - no combustion, no input, no output for that tick. When the block clears (e.g. stress decays below 100 and `AssessError` / `IsOperable` flip back), the full tick chain resumes next atmospheric tick. Writes to `Throttle` / `CombustionLimiter` always take effect instantly (no multi-tick ramp on the control values themselves), but the RPM response lags because of the 0.99 friction coefficient: RPM halves over `ln(0.5) / ln(0.99) ~ 69 ticks ~ 3.4 s` in the absence of combustion energy.

See "Error states and shutdown" below for the verbatim `IsOperable` auto-toggle, every `Error = 1` trigger, and the `HandleShutDown` body including the `!_gainedStress` gate that distinguishes stress-trip decay from non-stress-Error decay.

## SpeedTick: RPM, friction, stress
<!-- verified: 0.2.6228.27061 @ 2026-04-24 -->

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
- **Energy floor**: `CombustionEnergy + _normalCombustionEnergyCache >= 30 J` (`MoleEnergy(30.0)`, stored verbatim with no unit multiplier; `CombustionEnergy` is in SI joules, accumulated as `Mole.Enthalpy() * EnthalpyMultiplier * Quantity.ToDouble()` where `Enthalpy` is in J/mol). Below this, no RPM gain. A single tick of burn that consumes even ~0.0001 mol of H2 fuel produces ~30 J, so any fueled chamber above the 573.15 K temperature gate clears this floor trivially; it only closes when the chamber is essentially empty of fuel.
- **RPM accel coefficient**: `num = Lerp(0, 12, T / 5000)`. Temperature buys more RPM per tick, maxing at 12 RPM/tick at 5000 K. `Mathf.Clamp01` is applied to the lerp fraction `T/5000`, **not to T itself**. Chamber temperature can rise freely above 5000 K (excess fuel enthalpy accumulates as heat and leaves via venting); num just saturates at 12. Any chamber temperature above 5000 K is thermodynamically wasted fuel as far as RPM is concerned.
- **Friction**: each tick, `Rpm_new = 0.99 * (Rpm_old + num)`. At steady state `Rpm_ss = 0.99 * (Rpm_ss + num)`, so `Rpm_ss = 99 * num`. At `num = 6`, steady-state is 594 RPM. At `num = 12` (max, T >= 5000 K), steady-state is 1188 RPM. **Absolute RPM ceiling is 1188**, approached asymptotically; no code path pushes Rpm above this (the `Rpm -= 40` stress-trip and `Rpm -= 15` shutdown branches only subtract; `_internalCombustion.Rpm = 0f` on structure-incomplete is a zeroing; `Rpm = num4` in SpeedTick is the only additive path and is bounded by num <= 12). An earlier version of this page stated `Rpm_steady = 100 * num` (ceiling 1200); that was a sign-error in the algebraic derivation and is corrected here.
- **Stress threshold**: the code computes `num5 = num2 - num4 = 0.01 * (Rpm + num)`, then `num6 = |num - num5| = |0.99*num - 0.01*Rpm|`. So the mismatch is `|0.99*num - 0.01*Rpm|`, and the stress trigger is `|0.99*num - 0.01*Rpm| > 1 / Lerp(0.8, 2, (Rpm - 300) / 1200)`. The hysteresis tightens as RPM rises: at 300 RPM tolerance is `1/0.8 = 1.25`, at 1500 RPM tolerance is `1/2 = 0.5`. (An earlier version of this page wrote the mismatch as `|num - 0.01*Rpm|`; the 0.01*num correction is small in magnitude but matters at high num.)
- **Stress growth**: when tripped, `Stress += (mismatch - 1) * hysteresisFactor`.
- **Stress decay**: `-0.5 per tick` when not tripped.
- **Stress failure**: at 100, `Rpm -= 40` every tick (only the first sets `Error = 1`, gated on `Error == 0`). The stress branch does NOT route through `IsOperable`; on the next atmospheric tick `IsOperable` auto-clears `Error` back to 0 if the physical `flag2` conditions are good, and SpeedTick runs again. If `Stress` is still 100, the trip re-fires, subtracting another 40 RPM. This oscillates until the mismatch drops below threshold. See "Error states and shutdown" below for the full Error/shutdown machinery.

## Error states and shutdown
<!-- verified: 0.2.6228.27061 @ 2026-04-24 -->

`Error = 1` on a running `CombustionDeepMiner` comes from two independent paths: the stress-100 branch in `SpeedTick` (documented above) and the `IsOperable` auto-set, which fires on non-stress physical conditions every atmospheric tick.

### IsOperable auto-toggle

```csharp
// Assembly-CSharp.dll :: Assets.Scripts.Objects.Pipes.CombustionDeepMiner.IsOperable
protected override bool IsOperable
{
    get
    {
        bool flag = (bool)base.ProgrammableChip &&
                   (CodeErrorState != 0 || base.ProgrammableChip.CompilationError);
        bool flag2 = base.IsInputValid && base.IsOutputValid && !flag &&
                    CanMine() && base.IsStructureCompleted;
        if (Error == 0 && !flag2)
            OnServer.Interact(base.InteractError, 1);
        else if (Error == 1 && flag2)
            OnServer.Interact(base.InteractError, 0);
        return flag2;
    }
}
```

Evaluated at the top of every `OnAtmosphericTick` call (20 Hz). Auto-toggles Error:

- `Error = 0` and `!flag2` -> sets `Error = 1`.
- `Error = 1` and `flag2` -> sets `Error = 0`.
- Otherwise no Error write.

Returns `flag2` (the physical-condition bit), independent of whether `Error` was just toggled. `OnAtmosphericTick` then uses that return: `if (!IsOperable) { HandleShutDown(); return; }`. So a physical failure short-circuits SpeedTick entirely for that tick.

### IsInputValid / IsOutputValid: structural, no pressure minimum

```csharp
// Assembly-CSharp.dll :: Assets.Scripts.Objects.Pipes.DeviceInputOutput
public bool IsInputValid
{
    get
    {
        if (InputNetwork != null && InputNetwork.IsNetworkValid())
            return !InputNetwork.IsAwaitingEvent;
        return false;
    }
}

public bool IsOutputValid
{
    get
    {
        if (OutputNetwork != null && OutputNetwork.IsNetworkValid())
            return !OutputNetwork.IsAwaitingEvent;
        return false;
    }
}

public bool IsInput2Valid
{
    get
    {
        if (InputNetwork2 != null && InputNetwork2.IsNetworkValid())
            return !InputNetwork2.IsAwaitingEvent;
        return false;
    }
}
```

No override on `DeviceInputOutputImport`, `DeviceInputOutputImportExport`, `DeviceInputOutputImportExportCircuit`, `DeepMiner`, or `CombustionDeepMiner` — the base implementation is what the `IsOperable` gate sees.

Three structural checks, in order:

1. `InputNetwork != null` — the pipe is physically connected (connection exists). A disconnected pipe sets `InputNetwork` to null in `CheckConnections()` / `OnRemovePipeNetwork()`.
2. `InputNetwork.IsNetworkValid()` — the network has at least one structure (`StructureList.Count > 0`).
3. `!InputNetwork.IsAwaitingEvent` — the network is not in the middle of a deferred rebuild event this frame.

**No minimum pressure.** No minimum mole count. No gas-type requirement. No matter-state (gas/liquid) requirement. No temperature requirement. An empty-but-connected pipe returns `IsInputValid = true`; downstream combustion simply sees zero moles at `HandleGasInput` and admits nothing.

`IsOutputValid` is symmetric on `OutputNetwork`. The `HandleGasOutput` vent path is gated by its own pressure comparison against `_targetPressure`, but that runs only if `IsOutputValid` already passed; no maximum-pressure check blocks `IsOutputValid` itself.

Quirk on the secondary input: `CombustionDeepMiner` does expose a second pipe connection (`InputNetwork2`) and the matching `IsInput2Valid` property, but `IsOperable` gates only on `IsInputValid` and `IsOutputValid` - not `IsInput2Valid`. A broken secondary input therefore does not trigger the Error auto-set via this path. `InternalCombustion.HandleGasInput` only consumes from the primary `InputNetwork`, so on vanilla behaviour the second input is not load-bearing for the combustion cycle.

Disconnected vs. empty-but-connected are different code paths: the first fails at the null check and returns false; the second passes all three checks and returns true.

### Every path that sets Error = 1 on a running miner

| Source | Condition | Requires Stress = 100? | Clearing mechanism |
|---|---|---|---|
| `IsOperable` | `!base.IsInputValid` | no | Auto-cleared by `IsOperable` next tick when pipe recovers |
| `IsOperable` | `!base.IsOutputValid` | no | Auto-cleared next tick when pipe recovers |
| `IsOperable` | Chip `CodeErrorState != 0` or `CompilationError` | no | Auto-cleared next tick when chip state recovers |
| `IsOperable` | `!CanMine()` (DeepMiner-side gates: obstruction, missing `_deepMinables`, export chute, etc.) | no | Auto-cleared next tick when condition recovers |
| `IsOperable` | `!base.IsStructureCompleted` | no | Auto-cleared next tick when structure completes |
| `SpeedTick` | `Stress >= 100` (`if (Parent.Error == 0) Interact(InteractError, 1)`) | yes | Auto-cleared by `IsOperable` next tick if `flag2` is true; but SpeedTick re-fires the trip the same tick while Stress stays >= 100 |

Every non-stress path is checked at the `IsOperable` gate and cuts the tick short before `SpeedTick` runs. The stress path is inside `SpeedTick` itself and therefore only evaluates when `IsOperable` already returned true for that tick.

### HandleShutDown

```csharp
// InternalCombustion.HandleShutDown
public void HandleShutDown()
{
    if (!_gainedStress)
    {
        Rpm -= 15f;
        _targetPressure = RocketMath.Lerp(PressurekPa.Zero, RunningTargetPressure,
                                          Mathf.Clamp01(Rpm / 1000f));
    }
}
```

The entire method. It only touches RPM / target-pressure when `_gainedStress` is false. Called from two sites in `OnAtmosphericTick`:

1. **Pre-tick short-circuit**: `if (!IsOperable) { HandleShutDown(); return; }`. Fires on any non-stress Error. `SpeedTick` does not run this tick, so `_gainedStress` keeps its previous value (typically false on a miner that wasn't actively stressing), and RPM decays at -15/tick = 300 RPM/sec.
2. **Mid-tick branch**: `if (!OnOff || !Powered || Error == 1) HandleShutDown();` runs after `SpeedTick`. On a stress-100 trip, `SpeedTick` has just set `_gainedStress = true` (the stress accumulation branch sets it), so `HandleShutDown` is a no-op. On an `!OnOff` / `!Powered` case, `SpeedTick` did run this tick and `_gainedStress` depends on whether mismatch exceeded threshold.

### Two distinct failure-mode decay rates

- **Non-stress Error (`IsOperable` fails)**: -15 RPM/tick = 300 RPM/sec. Stress value is whatever it was when the Error fired; it does not update because `SpeedTick` is skipped. Chamber loses fuel intake (`HandleGasInput` is gated on `Error <= 0`) but existing chamber gas continues to be vented by `HandleGasOutput` with a target pressure that slides toward 0 as RPM falls.
- **Stress-100 trip**: -40 RPM/tick = 800 RPM/sec while `Stress >= 100`. Stress stays pinned at 100 until the chamber cools or empties enough that mismatch drops below `1 / num7`, then decays at -0.5/tick via normal SpeedTick. Error toggles 1 -> 0 -> 1 each tick (SpeedTick sets, next-tick IsOperable clears), which means `HandleGasInput` is blocked on the set-Error half of each tick and admitted on the cleared half.

The two rates and their dial signatures differ:

- A stress trip pins `Stress = 100` for multiple seconds during decay. The [dial animator](Research/GameClasses/CombustionDeepMiner.md#stress-dial-and-audio) lerps toward live stress at rate 2/sec (time constant 0.5 s), so sustained stress=100 produces a visibly pegged needle within about 1.5 seconds.
- A non-stress Error leaves stress at its pre-failure value. The needle does not rise.

Useful for diagnosing in-game: if the miner drops RPM fast while the stress dial never leaves the low band, the trigger is one of the five `IsOperable` paths, not a stress trip.

### Stress dial and audio

```csharp
// Dial needle rendering (60 Hz)
_lastAngle = Mathf.Lerp(_lastAngle, targetAngle, deltaTime * 2f);
```

Needle lerps toward the live stress value at rate 2/second, time constant 0.5 s. A single-tick spike (0.05 s) moves the needle only a few percent; sustained stress reaches ~95 % of full deflection in ~1.5 s. Audio thresholds read live stress directly (not the lerped needle): warning fires `Stress > 40`, max-stress sting fires `Stress > 95` and stops at `Stress < 90` (hysteresis band 90-95).

## Startup transient: why a hot chamber explodes on any throttle
<!-- verified: 0.2.6228.27061 @ 2026-04-24 -->

This section is derived analysis from the `SpeedTick` formulas above. Every number here follows from the verbatim code quoted in "SpeedTick: RPM, friction, stress"; no extra game data.

**Key observation**: `num` (RPM accel per tick) is a function of **temperature only**, not of throttle or limiter:

```
num = Lerp(0, 12, Clamp01(T / 5000))
```

Throttle and Limiter control fuel mass flow and burn fraction, both of which drive temperature indirectly. Once the two gates are open (`T >= 573.15 K` and `CombustionEnergy + _normalCombustionEnergyCache >= 30 J`), num is set by whatever T currently is, and the same amount of RPM accel is applied regardless of how much fuel was actually burned this tick.

Stress accumulates on the mismatch between accel input (`num`) and friction loss (`num5 = 0.01 * Rpm`):

```
mismatch   = |num - 0.01*Rpm|
num7       = Lerp(0.8, 2, Clamp01((Rpm - 300) / 1200))   # 0.8 at Rpm <= 300, 2.0 at Rpm >= 1500
threshold  = 1 / num7
if mismatch > threshold: Stress += (mismatch - 1) * num7
else:                    Stress -= 0.5
```

At Rpm = 0, num7 = 0.8 and threshold = 1.25. So unless num <= 1.25 at Rpm = 0, stress accumulates on the first tick.

### Temperature bands vs. cold-start tolerance

Cold-start means Rpm = 0 with combustion firing. num-at-ignition vs. stress outcome:

| Chamber T | num | mismatch at Rpm=0 | Stress/tick | Stress/sec (20 Hz) | Ticks to 100 |
|---|---|---|---|---|---|
| 573.15 K (ignition floor) | 1.375 | 1.375 | 0.30 | 6.0 | ~333 |
| 800 K | 1.92 | 1.92 | 0.74 | 14.7 | ~136 |
| 1200 K | 2.88 | 2.88 | 1.50 | 30.1 | ~67 |
| 2000 K | 4.80 | 4.80 | 3.04 | 60.8 | ~33 |
| 3000 K | 7.20 | 7.20 | 4.96 | 99.2 | ~20 |
| 4000 K | 9.60 | 9.60 | 6.88 | 137.6 | ~15 |
| 5000 K (cap) | 12.00 | 12.00 | 8.80 | 176.0 | ~12 |

The "Stress/sec" column makes the failure mode visible: anywhere above ~700 K the stress curve outruns RPM ramp entirely, because RPM catches up to num only at rate `(num - 0.01*Rpm)` per tick. The RPM transient is first-order with time constant ~5 s (friction 1%), but stress hits 100 in ~0.6 s at 5000 K. By the time RPM has risen enough for the mismatch to drop under threshold, stress has long since tripped `Error = 1`.

### Why `CombustionLimiter` cannot stop this

`CombustionLimiter` scales burn fraction, not whether combustion fires. The SpeedTick gate is a pair of Boolean checks on the chamber state, not a magnitude check on this tick's burn:

- `T >= 573.15 K` depends on chamber history, not on this tick's burn rate.
- `CombustionEnergy + _normalCombustionEnergyCache >= 30 J` is a pre-burn-plus-post-burn sum of combustion energy available in the chamber. Even at `CombustionLimiter = 0` (burn fraction 0.001 floor), only 0.1% of chamber fuel is consumed per tick, so a hot chamber with any fuel load clears the 30-J floor trivially (30 J is roughly 0.0001 mol of H2 combusted).

Consequence: once the chamber is hot and has fuel, num fires at the full T-determined rate every tick, and Limiter has zero effect on num.

### Why `Throttle = 10` cannot stop this either

`Throttle = 10` admits 0.001 mol/tick of fresh mixture. This is a tiny addition. It changes neither num (temperature-driven) nor the ignition-floor state (chamber already hot, already has fuel). The only thing Throttle changes during a hot-chamber start is how fast new fuel refills the chamber to replace what combustion consumes; at 0.001 mol/tick of intake vs. 0.0075 fraction/tick of burn, the chamber fuel load actually drains, but slowly.

### Why `Throttle = 0` for one tick does not reset anything

Setting `Throttle = 0` halts HandleGasInput. The chamber's existing fuel keeps burning at `CombustionLimiter`'s fraction. For the gates to close (combustion to stop firing), `CombustionEnergy + _normalCombustionEnergyCache` must fall below 30 J, which in practice means the chamber is essentially empty of fuel (30 J is ~0.0001 mol of H2). Time to drain the chamber of fuel to that point depends on burn fraction:

- At `CombustionLimiter = 0` (burn fraction 0.001): half-life of fuel ~693 ticks = 34.7 s.
- At `CombustionLimiter = 10` (burn fraction 0.0075): half-life ~92 ticks = 4.6 s.
- At `CombustionLimiter = 100` (burn fraction 0.75): half-life ~0.5 ticks = 25 ms (effectively one tick).

So the fastest way to stop combustion on a hot chamber is counter-intuitive: **raise** CombustionLimiter to 100 (burns all fuel in one tick, 30-J gate closes next tick) rather than lower it. Note that this also dumps all the fuel's energy as heat in a single tick, which spikes temperature even higher, which spikes num even higher for that one tick. Net: one catastrophic tick followed by cold-gate closure.

The user's manual pulse strategy (Throttle=10, then Throttle=0 above 30% stress, repeat) works because:

1. Throttle=10 admits enough fuel to keep combustion firing.
2. RPM ramps during the "on" phase. Stress accumulates faster than RPM rises, so stress hits the 30% abort before RPM stabilises.
3. Throttle=0 halts new fuel. Existing fuel burns at CL=10 rate, draining the chamber on a ~4.6 s half-life.
4. During this drain, RPM is gaining until fuel drops below 30 MoleEnergy, then num flips to 0 and RPM decays by 1% per tick.
5. Stress decays at 0.5/tick throughout the "off" phase (no fresh mismatch).
6. Each cycle nets positive RPM gain if "on" adds more RPM than "off" loses, and net-zero stress if the decay phase is long enough.

It is a hand-tuned ratchet. It works only because the chamber manages to stay hot enough across cycles to keep num high, while RPM eventually crosses into the tolerant band (Rpm >= 300 where num7 increases, threshold widens only slightly from 1.25 to 0.5 by 1500 RPM).

### The only safe cold-start

A cold start (ambient chamber, T around 300 K well below the 573.15 K ignition floor) avoids the transient entirely. num = 0 until T crosses ignition, and temperature rises slowly driven by fuel enthalpy over the chamber's heat capacity. As T crosses 573.15 K, num starts at ~1.37, mismatch at Rpm=0 is 1.37, stress gain is 0.30/tick = 6.0/sec. Rpm gains (num - 0) per tick initially, so mismatch falls as Rpm rises. For the mismatch-cross formula `mismatch = num - 0.01*Rpm`:

- At num = 1.37 and `mismatch = 1.25` (the threshold at Rpm <= 300), Rpm must be 12. This happens in ~9 ticks at num=1.37 per tick into 0.99 friction.

So roughly half a second after ignition, stress stops accumulating, and whatever stress was accrued (max ~3) decays away. Temperature continues to rise as fuel burns, num rises with it, and RPM trails num smoothly because mismatch stays close to threshold on the whole ramp.

Regime summary:

- **Cold chamber (T < 573.15 K)**: safe to open throttle and run up. Stress never spikes because num rises alongside Rpm.
- **Warm chamber (573.15 K < T < ~700 K)**: marginal. Stress accumulates at single-digit ticks. Ramp is recoverable but close.
- **Hot chamber (T > ~700 K)**: stress outruns Rpm. No fixed (Throttle, CombustionLimiter) pair stabilises the transient; only pulsing, manual recovery, or a forced cool-down works.

Practical operation rules for a governor that must start on an already-hot chamber:

1. **Detect the hot state before engaging.** Read `Temperature` via LogicType.Temperature (chamber-scoped per `HasReadableAtmosphere => true`). If T >= 573.15 K, skip straight to cool-down; do not write Throttle > 0.
2. **Cool-down plan**: set OnOff=0, Throttle=0, CombustionLimiter=0. HandleGasInput is gated on OnOff and Error; this freezes fuel admission. With OnOff=0, SpeedTick's `Rpm = num4` update is also gated off, so Rpm holds rather than decaying through friction (a small plus). Wait for T to drop via natural chamber heat loss and venting. Temperature decay rate depends on the chamber's radiation and wall conduction parameters, which are not documented in this page; measure empirically.
3. **Ramp from cold**: once T < 573.15 K, set Throttle and CombustionLimiter from their minimum non-zero values. Raise slowly, observing Stress. The transient is safe as long as T remains below ~700 K during the first few hundred ticks.
4. **Do not pulse on hot chamber**: pulsing works manually because a human can see the stress dial and abort within ~500 ms. An IC10 script runs at ~1 line/tick and cannot out-manoeuvre a 176 stress/sec spike once T = 5000 K. Pulse cycles are valid only below T ~= 2000 K where the pulse-off phase fits inside the stress-decay budget.

**Higher-energy fuels make this worse only via temperature**: H2+O3 vs CH4+O2 differ by a 1.5x energy ratio per mole of fuel and a 1.5x oxidiser multiplier. Higher energy per mole means the chamber heats up faster per mol burned, which pushes T toward 5000 K faster. The num ceiling is the same (12 at T >= 5000 K), but the time to reach that ceiling from a cold start is shorter with higher-energy fuel. The cold-start ramp is still safe; the hot-chamber failure mode is identical. The practical difference is smaller time windows: less time to react between ignition and running away.

## Drill depth and ore yield
<!-- verified: 0.2.6228.27061 @ 2026-04-21 -->

Drilling depth and ore production scale linearly with RPM via `DeepMiner.RpmNormalised(rpm) = rpm / 200f`. The base class constant `STANDARD_RPM = 200f` is the reference point: RPM 200 is "1.0x speed" for both the drill-down animation and the per-tick ore-spawn progress.

Drill-head descent (per-frame, off the main update):

```csharp
// Assembly-CSharp.dll :: Assets.Scripts.Objects.Pipes.DeepMiner.DrillDownAnimation
private void DrillDownAnimation()
{
    ...
    _isReachedBedRock = vector.y <= 0f;
    vector += Vector3.down * (Time.deltaTime * 0.125f * RpmNormalised(Rpm));
    _drillBit.position = vector;
    ...
}

// Assembly-CSharp.dll :: Assets.Scripts.Objects.Pipes.DeepMiner
private const float DRILL_DOWN_SPEED = 0.125f;

public static float RpmNormalised(float rpm) => rpm / 200f;
```

Descent rate: `0.125 m/s * (Rpm / 200f)` = 0.000625 m/s per RPM. At RPM 200, that's 0.125 m/s (7.5 m/min). At RPM 600 (a typical steady-state on a combustion deep miner), 0.375 m/s. The drill stops descending once `_drillBit.position.y <= 0f` (bedrock reached).

Descent only runs in `UpdateEachFrame` when `!_isReachedBedRock && OnOff && Powered && Error == 0`:

```csharp
// Assembly-CSharp.dll :: Assets.Scripts.Objects.Pipes.DeepMiner.UpdateEachFrame
public override void UpdateEachFrame()
{
    base.UpdateEachFrame();
    if (!_isReachedBedRock && OnOff && Powered && Error == 0)
    {
        DrillDownAnimation();
    }
    ...
}
```

Ore-spawn progress (server export tick, runs only after bedrock is reached):

```csharp
// Assembly-CSharp.dll :: Assets.Scripts.Objects.Pipes.DeepMiner.OnServerExportTick
protected override void OnServerExportTick(float deltaTime)
{
    if (!OnOff || !Powered || !_isReachedBedRock || Error != 0 || !base.IsStructureCompleted)
    {
        return;
    }
    ProgressProcessing(deltaTime);
    if (IsSpawnTime() && !base.IsExportChuteBlocked)
    {
        ResetSpawnTime();
        SpawnOre();
        if (CanBeginExport)
        {
            OnServer.Interact(base.InteractExport, 1);
        }
    }
}

// Assembly-CSharp.dll :: Assets.Scripts.Objects.Pipes.DeepMiner.ProgressProcessing
protected void ProgressProcessing(float deltaTime)
{
    if (OnOff && Powered)
    {
        _currentProgress += deltaTime * RpmNormalised(Rpm);
        Processing = (byte)(Mathf.Clamp01(_currentProgress / OreSpawnTime) * 100f);
    }
}

// Assembly-CSharp.dll :: Assets.Scripts.Objects.Pipes.DeepMiner (ore-spawn timing)
private bool IsSpawnTime() => _currentProgress > OreSpawnTime;

private void ResetSpawnTime()
{
    _currentProgress = 0f;
    OreSpawnTime = _deepMinables.GetTimeToMine();
}

private void SpawnOre()
{
    DirtyOre dirtyOre = Thing.Create<DirtyOre>(_spawnOrePrefab, ExportSlot.Location);
    _deepMinables.SetValues(dirtyOre);
    dirtyOre.ParentSlot = null;
    OnServer.MoveToSlot(dirtyOre, ExportSlot);
}
```

Ore yield:

- `_currentProgress` accumulates in real-time seconds scaled by `RpmNormalised(Rpm) = Rpm / 200f`. At RPM 200 the progress bar fills at one unit per real second; at RPM 600 it fills 3x as fast.
- `OreSpawnTime` is pulled from the selected `DeepMinablesGenerationData.GetTimeToMine()` once per completed ore, set on `OnRegistered` and reset after each spawn.
- When `_currentProgress > OreSpawnTime`, an ore is spawned as a `DirtyOre` prefab (`"ItemDirtyOre"`), populated via `_deepMinables.SetValues(dirtyOre)` (which determines the ore reagent based on the world region), and moved into `ExportSlot`.
- No temperature, stress, or limiter gating on ore production: it depends only on `Rpm` going above zero, `_isReachedBedRock`, `OnOff`, `Powered`, `Error == 0`, and the export chute being unblocked.

Practical consequence for an IC10 script: RPM is the only thing that matters for mining throughput. Keeping RPM high (targeting the ~600 RPM steady-state at temperature ~5000 K, or higher if limiter/throttle keep stress contained) directly multiplies ore output. Temperature and limiter matter only to the extent they feed the RPM loop in `InternalCombustion.SpeedTick`.

## Gas output and venting
<!-- verified: 0.2.6228.27061 @ 2026-04-21 -->

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

`pressurePerTick` comes from the inherited `DeviceAtmospherics.PressurePerTick` property:

```csharp
// Assembly-CSharp.dll :: Assets.Scripts.Objects.Pipes.DeviceAtmospherics
[FormerlySerializedAs("PressurePerTick")]
[SerializeField]
protected float pressurePerTick = 101.325f;

public PressurekPa PressurePerTick => new PressurekPa(pressurePerTick);
```

The serialized default is `101.325 kPa` per atmospheric tick (1 atm/tick). This caps the first argument of the `RocketMath.Min` in `HandleGasOutput`: the vent can transfer at most the moles corresponding to 101.325 kPa in a `Chemistry.PipeVolume` at the input pipe's temperature, OR 10% of the chamber's overpressure, whichever is smaller. At 4000 kPa chamber / 5000 K and pipe at ~300 K / 10 L, the 10% overpressure term is the usual binder; the `pressurePerTick` term only starts dominating at severe overpressures or when the input pipe is very cold.

Note the quirk: `pressurePerTick` is looked up against the **input network's** `Atmosphere.Temperature` and against `Chemistry.PipeVolume`, not the chamber. That is a game-wide `DeviceAtmospherics` convention: the "how many moles can a device move per tick" budget is always computed as though the transfer were happening in a reference pipe volume at the input pipe's temperature, regardless of the actual source or sink.

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
<!-- verified: 0.2.6228.27061 @ 2026-04-24 -->

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

| LogicType | Read | Write | Source | Notes |
|---|---|---|---|---|
| `Throttle` | yes | yes | `CombustionDeepMiner` | Clamped 0..100, snaps to mult of 10. Writes via `_internalCombustion.Throttle = (float)value`. |
| `CombustionLimiter` | yes | yes | `CombustionDeepMiner` | Clamped 0..100, snaps to mult of 10. Writes via `_internalCombustion.CombustionLimiter = (float)value`. |
| `Rpm` | yes | no | `CombustionDeepMiner` | Current rotations/min. Read-only. |
| `Stress` | yes | no | `CombustionDeepMiner` | 0..100. Read-only. Synchronised on network-update flag 32768. |
| `Setting`, `Maximum`, `Ratio` | no | no | `CombustionDeepMiner` (blocked) | Explicitly blocked in `CanLogicRead` / `CanLogicWrite`. |
| `Temperature` | yes | no | `Device.GetLogicValue` via `HasReadableAtmosphere` | Returns `InternalAtmosphere.Temperature.ToDouble()` (chamber, not input pipe). Kelvin. |
| `Pressure` | yes | no | `Device.GetLogicValue` via `HasReadableAtmosphere` | Returns `InternalAtmosphere.PressureGassesAndLiquids.ToDouble()` (chamber). kPa. |
| `TotalMoles` | yes | no | `Device.GetLogicValue` via `HasReadableAtmosphere` | Returns `InternalAtmosphere.TotalMoles.ToDouble()`. |
| `RatioXxx` (Oxygen, Hydrogen, Steam, Pollutant, CarbonDioxide, etc.) | yes | no | `Device.GetLogicValue` via `HasReadableAtmosphere` | Chamber ratios. |
| `RatioXxxInput`, `PressureInput`, `TemperatureInput`, `TotalMolesInput` | yes | no | `DeviceInputOutput` | Input pipe network readings. Similar `...Input2`, `...Output`, `...Output2` also exposed. |
| `CombustionInput` | yes | no | `DeviceInputOutput` | Returns `InputNetwork.Atmosphere.Inflamed ? 1 : 0`. **Boolean flame-present flag on the input pipe**, not fuel energy / potency / enthalpy. 0 for cold unburned mix, 1 only while the input pipe's gas is actively burning (rare in a fuel-feed pipe). Also available as `CombustionInput2`, `CombustionOutput`, `CombustionOutput2`. |
| `On` | yes | yes | `Device` via `HasOnOffState` | Writable; routes to `OnServer.Interact(base.InteractOnOff, state)`. |
| `Power`, `Error`, `RequiredPower`, `Activate`, `Open`, `Mode`, `Lock` | inherited | inherited (some writable) | `Device` / `Logicable` | Standard device plumbing. |

Concretely for the script's read set:

```csharp
// Assembly-CSharp.dll :: Assets.Scripts.Objects.Pipes.Device.GetLogicValue (relevant cases)
case LogicType.On:          return OnOff ? 1 : 0;
case LogicType.TotalMoles:  return base.InternalAtmosphere?.TotalMoles.ToDouble() ?? 0.0;
case LogicType.Pressure:    return base.InternalAtmosphere?.PressureGassesAndLiquids.ToDouble() ?? 0.0;
case LogicType.Temperature: return base.InternalAtmosphere?.Temperature.ToDouble() ?? 0.0;
```

```csharp
// Assembly-CSharp.dll :: Assets.Scripts.Objects.Pipes.Device.CanLogicRead (relevant cases)
case LogicType.On:          return HasOnOffState;
case LogicType.Pressure:
case LogicType.Temperature:
case LogicType.RatioOxygen:
... (all ratio / moles / pressure / temperature cases):
                            return HasReadableAtmosphere;
```

`CombustionDeepMiner.HasReadableAtmosphere => true` (verbatim override), so every ratio-style read above resolves to the chamber's internal atmosphere rather than returning zero or falling through to the base `false`.

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
<!-- verified: 0.2.6228.27061 @ 2026-04-24 -->

| Name | Value | Location |
|---|---|---|
| Throttle clamp | [0, 100] step 10 | `InternalCombustion.Throttle` setter |
| CombustionLimiter clamp | [0, 100] step 10 | `InternalCombustion.CombustionLimiter` setter |
| Max molar input | 0.009999999776482582 mol/tick | `InternalCombustion.MaxMolarInput` |
| Burn fraction formula | `Clamp(Pow(CL/10, 2) * 0.0075, 0.001, 1)` | `InternalCombustion.ManualCombust` |
| Chamber volume | 2.0 L | `CombustionDeepMiner.InitInternalAtmosphere` |
| Ignition temperature | 573.15 K (300 C) | `InternalCombustion.SpeedTick` |
| Energy threshold | 30 J (`MoleEnergy(30.0)` stored verbatim; `MoleEnergy` wraps a raw `double _value` with no unit multiplier, and `CombustionEnergy` accumulates J via `Mole.Enthalpy() * EnthalpyMultiplier * Quantity.ToDouble()`) | `InternalCombustion.SpeedTick` |
| RPM accel coefficient | `Lerp(0, 12, T / 5000 K)` | `InternalCombustion.SpeedTick` |
| Friction per tick | 0.99 (1% drag) | `InternalCombustion.SpeedTick` |
| Stress hysteresis factor | `Lerp(0.8, 2, (Rpm - 300) / 1200)` | `InternalCombustion.SpeedTick` |
| Stress failure | Stress >= 100 -> -40 RPM + Error=1 | `InternalCombustion.SpeedTick` |
| Stress decay | -0.5 / tick when not tripped | `InternalCombustion.SpeedTick` |
| Shutdown RPM decay | -15 per tick while `!_gainedStress` | `InternalCombustion.HandleShutDown` |
| Running target pressure | 4000 kPa | `InternalCombustion.RunningTargetPressure` |
| Vent rate (chamber side) | up to 10% of overpressure / tick | `InternalCombustion.HandleGasOutput` |
| Vent rate (pipe side) | `IdealGas.Quantity(101.325 kPa, PipeVolume, InputTemp)` | `DeviceAtmospherics.pressurePerTick` default |
| STANDARD_RPM | 200f | `DeepMiner.STANDARD_RPM` |
| DRILL_DOWN_SPEED | 0.125 m/s at RPM 200 | `DeepMiner.DRILL_DOWN_SPEED` |
| RpmNormalised | `rpm / 200f` | `DeepMiner.RpmNormalised` |
| Stress audio thresholds | warn at 40, loud band 50..95, max-stress at 95 | `CombustionDeepMiner.HandleStressAnimSound` |
| Dial wobble thresholds | `STRESS_WOBBLE_THRESHOLD=40`, `WOBBLE_RANGE=60` | `CombustionDeepMiner` consts |
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
- 2026-04-24: added "Atmosphere / GasMixture access for chamber composition" subsection. Verbatim verified: `Atmosphere.CombustionEnergy` is a public *field* (`public MoleEnergy CombustionEnergy = MoleEnergy.Zero;`) not a property; per-gas ratios are read via the method `GasMixture.GetGasTypeRatio(Chemistry.GasType)` returning `float`, with no per-gas Ratio properties on either `Atmosphere` or `GasMixture`; `InternalCombustion._normalCombustionEnergyCache` is a private MoleEnergy field on the global-namespace type, resolvable via `Type.GetType("InternalCombustion, Assembly-CSharp")` and snapshots `atm.CombustionEnergy` once per tick *before* TryCombust runs. Resolves bugs in DeepMinerLogger v0.1.0 where ratio reads and CombustionEnergy reads were returning 0 because they searched for properties that did not exist. Additive content; no contradiction with existing claims.
- 2026-04-24: full-class decompile pass of `CombustionDeepMiner` and `InternalCombustion`. Added a new "Additional miner-level findings (full-class decompile 2026-04-24)" section capturing findings not previously documented: `CanMine()` verbatim (two conditions: `ThingInTheWay == null` AND `_deepMinables != null`, nothing else), `CombustionDeepMiner.AssessError` override (mirrors `IsOperable` auto-toggle on a second path, gated on `GameManager.RunSimulation`), `CombustionDeepMinerSaveData` four-field persistence (Throttle, CombustionLimiter, Rpm, Stress), network update flag 16384 for Rpm (completing the flag set), InteractWith Button4-7 mappings (Throttle/CombustionLimiter +/- 10), motor audio silencing at RPM < 2, `Awake` as the only init override, confirmation that no damage/wear state exists on the miner, and confirmation that `InputNetwork2` is structurally present but never consumed by `HandleGasInput` or checked by `IsOperable`/`AssessError`. Flagged an ordering issue in the page: "Error = 1 trigger conditions", "HandleShutDown implementation", "Stress dial display (real-time behavior)", and "Stress audio thresholds (refined)" were appended after "Verification history" by an earlier session and should be moved above VH in a future cleanup pass, consolidating overlapping material with the "Error states and shutdown" section. No contradictions with existing verified content. New open questions: where `_deepMinables` is assigned (likely `DeepMiner.OnRegistered` / `IGenerateMinables`), where `ThingInTheWay` is set (likely `DeepMiner.CheckThingInWay()` called from `DrillDownAnimation`), where `InteractError` bit is defined on base `Device`.
- 2026-04-24: expanded "Error states and shutdown" with a dedicated "IsInputValid / IsOutputValid: structural, no pressure minimum" subsection. Verbatim decompiled `IsInputValid`, `IsOutputValid`, `IsInput2Valid` from `DeviceInputOutput`; confirmed no override anywhere in the chain (`DeviceInputOutputImport`, `DeviceInputOutputImportExport`, `DeviceInputOutputImportExportCircuit`, `DeepMiner`, `CombustionDeepMiner`). Documented the three structural checks (network not null, `IsNetworkValid` = StructureList count > 0, not `IsAwaitingEvent`), and explicitly noted what the property does NOT check: no minimum pressure, no mole count, no gas-type, no matter-state, no temperature. Flagged the `IsInput2Valid` quirk: secondary input has its own validity property, but `CombustionDeepMiner.IsOperable` gates only on primary `IsInputValid`/`IsOutputValid`, not `IsInput2Valid`; `HandleGasInput` also consumes only from the primary. Additive content under already-stamped "Error states and shutdown" section; no contradiction. Resolves the open question "does low input pipe pressure trigger Error=1" - it does not.
- 2026-04-24: added "Error states and shutdown" section with verbatim `CombustionDeepMiner.IsOperable` override, verbatim `InternalCombustion.HandleShutDown`, and the complete list of six `Error = 1` triggers (five via `IsOperable` auto-toggle, one via `SpeedTick` stress branch). Previous page coverage was limited to the stress-trip case and a single "Shutdown RPM decay" row in the constants table; the `IsOperable` auto-toggle and the non-stress Error paths were undocumented, and the claim that stress=100 "knocks IsOperable false" in the "Per-tick rules" bullet list was incorrect (stress sets Error directly in SpeedTick; IsOperable is a separate gate over physical conditions and auto-clears Error next tick unless a physical condition also holds). Corrected the bullet, added cross-reference, added "Stress dial and audio" subsection documenting the 2/sec lerp on the needle (explains why brief stress spikes are invisible on the dial while sustained stress=100 is visible within ~1.5 s). Stress audio thresholds updated from `>=` to `>` conditions with the 90-95 hysteresis band. Additive section; no contradiction with existing verified content beyond the stress/IsOperable clarification. Added to sources: `CombustionDeepMiner.IsOperable`, `DeviceInputOutput.IsInputValid`, `DeviceInputOutput.IsOutputValid`, `DeepMiner.CanMine`, `InternalCombustion.HandleShutDown`. Section stamps: "SpeedTick: RPM, friction, stress" and "Error states and shutdown" at 0.2.6228.27061 @ 2026-04-24.
- 2026-04-24: conflict on "SpeedTick energy gate units". Previous claim (across "SpeedTick: RPM, friction, stress", "Startup transient", and "Constants summary"): `MoleEnergy(30.0)` = 30 MJ (30,000,000 J). New finding: `MoleEnergy(30.0)` = 30 J. Fresh validator verdict: B is correct, gate is 30 J. Verbatim evidence: `public MoleEnergy(double value) { _value = value; }` stores verbatim with no multiplier; `combustionEnergy = new MoleEnergy(mole.Enthalpy() * mole2.EnthalpyMultiplier * mole.Quantity.ToDouble())` in `CombustionResult.RunCombustion` where `Mole.Enthalpy(Hydrogen) => 306000.0` (J/mol), so `CombustionEnergy` is in SI joules. Result: corrected "30 MJ" / "30 MoleEnergy" to "30 J" in all six locations, added explanatory parentheticals on the `MoleEnergy` unit convention, added `CombustionResult` and `MoleEnergy` to sources. Practical consequence: the gate is trivially clearable (0.0001 mol of H2 combustion clears it), so it effectively only closes when the chamber is empty of fuel. This weakens the "Throttle=0 shuts combustion in N seconds" argument in "Startup transient" only quantitatively (the gate does eventually close but via chamber draining, not via the gate being tight). Sections restamped: SpeedTick, Startup transient, Constants summary.
- 2026-04-24: resolved `CombustionInput` semantics in "Logic variables". Decompiled `Assets.Scripts.Objects.Pipes.DeviceInputOutput.GetLogicValue` (and sibling `DeviceInput.GetLogicValue`) against `E:\Steam\steamapps\common\Stationeers\rocketstation_Data\Managed\Assembly-CSharp.dll` via ilspycmd. Both implementations read `CombustionInput` as `InputNetwork.Atmosphere.Inflamed ? 1 : 0`, a boolean flame-present flag. This is not a fuel potency / enthalpy / combustion-energy reading; it is true only while the input pipe's gas mixture is actively burning (set inside `Atmosphere.TryCombust`, cleared by `Atmosphere.ResetCombustionData`). Corrected the table entry previously grouped with the quantitative input reads. Section stamp on "Logic variables" bumped to 2026-04-24.
- 2026-04-22: clarified the num Lerp clamping in "SpeedTick: RPM, friction, stress". `Mathf.Clamp01` is on the lerp fraction `T/5000`, not on T itself; chamber temperature can rise arbitrarily above 5000 K while num saturates at 12. No code change; the verbatim `SpeedTick` quote already contains this, the sentence summarising it was ambiguous ("maxing at 12 RPM/tick near 5000 K" read as if T were also capped). Reinforces the "Temperature above 5000 K is wasted fuel" intuition used by the control algorithm. Section stamp unchanged (same day).
- 2026-04-22: corrected steady-state RPM formula and mismatch formula in "SpeedTick: RPM, friction, stress". Previous text said `Rpm_steady = 100 * num` and `mismatch = |num - 0.01*Rpm|`. Re-derivation from the verbatim `SpeedTick` code (`num2 = Rpm + num`, `num4 = num2 * 0.99`, `Rpm = num4`, `num5 = num2 - num4`, `num6 = |num - num5|`) gives `Rpm_ss = 99 * num` and `mismatch = |0.99*num - 0.01*Rpm|`. Max RPM at num=12 is 1188, not 1200. No decompile; sign-error in the algebra on the existing code quote. Section stamp on "SpeedTick: RPM, friction, stress" bumped to 2026-04-22. Downstream sections ("Startup transient" tables and values) use the slightly-off formulas; differences are ~1% in stress-per-tick values and unchanged qualitative conclusions, so those sections are not restamped pending a fuller pass.
- 2026-04-21: added "Startup transient: why a hot chamber explodes on any throttle" section. Derived analysis from existing `SpeedTick` quotes in "SpeedTick: RPM, friction, stress"; no new decompile. Documents the temperature-only dependence of `num`, the stress/sec table as a function of chamber T, why `CombustionLimiter` and `Throttle = 10` cannot stop a hot-chamber runaway, why `Throttle = 0` takes multiple seconds to close the combustion gates, the mechanics that make the user's manual pulse strategy work, the safe cold-start regime, and governor implications for IC10 scripts that must start on a hot chamber.
- 2026-04-21: re-decompiled `CombustionDeepMiner`, `DeepMiner`, `InternalCombustion`, `DeviceInputOutput`, `DeviceAtmospherics`, `Pipes.Device`, `IInternalCombustion` against the same DLL. Extended the "Class hierarchy" section with the full chain (`CombustionDeepMiner` -> `DeepMiner` -> `DeviceInputOutputImportExportCircuit` -> `DeviceInputOutputImportExport` -> `DeviceInputOutputImport` -> `DeviceInputOutput` -> `DeviceAtmospherics` -> `Device`) and the per-class ownership of state. Corrected the `InitInternalAtmosphere` source: it is a `CombustionDeepMiner` override with its own `private static readonly VolumeLitres Volume = new VolumeLitres(2.0)` field, not inherited from `DeepMiner`. Added "Drill depth and ore yield" section with verbatim `DeepMiner.DrillDownAnimation`, `ProgressProcessing`, `IsSpawnTime`, `ResetSpawnTime`, `SpawnOre`, `RpmNormalised` quotes; resolves the prior open question. Extended "Gas output and venting" with the `DeviceAtmospherics.pressurePerTick = 101.325f` default and the pipe-side vent-rate cap. Extended "Tick pipeline" with `HandleGasInput` gating on `Error <= 0` and the RPM half-life calculation for lag after a limiter/throttle change. Extended "Logic variables" with explicit rows for `Temperature`, `Pressure`, `TotalMoles`, `RatioXxx`, `On`, and a verbatim excerpt of `Device.CanLogicRead` / `Device.GetLogicValue` showing that `HasReadableAtmosphere => true` on `CombustionDeepMiner` routes those reads to the chamber's internal atmosphere. Added `STANDARD_RPM`, `DRILL_DOWN_SPEED`, `RpmNormalised`, shutdown RPM decay, pipe-side vent rate, stress-audio thresholds, and dial-wobble constants to the "Constants summary" table.

## Open questions

- Liquid fuels in a combustion chamber: `Mole.Enthalpy` returns the same energy for `Hydrogen` and `LiquidHydrogen`, but the `Combust` method subtracts a latent-heat term (`IdealGas.Energy(burned_fuel_moles, fuel.LatentHeat)`) when the fuel matter-state is liquid. Net effect on deep-miner RPM accel was not measured in this pass.
## Error = 1 trigger conditions
<!-- verified: 0.2.6228.27061 @ 2026-04-24 -->

Every path that sets `Error = 1` on `CombustionDeepMiner`:

**1. Stress reaches 100 (SpeedTick)** — requires stress to hit 100:
```csharp
// InternalCombustion.SpeedTick
if (Stress >= 100f)
{
    Rpm -= 40f;
    if (Parent.GetAsThing.Error == 0)
    {
        OnServer.Interact(Parent.GetAsThing.InteractError, 1);
    }
}
```

**2. IsOperable property returns false** — does NOT require stress to hit 100. Triggered when any of these conditions are true:
```csharp
// CombustionDeepMiner.IsOperable (property getter)
bool flag = (bool)base.ProgrammableChip && (CodeErrorState != 0 || base.ProgrammableChip.CompilationError);
bool flag2 = base.IsInputValid && base.IsOutputValid && !flag && CanMine() && base.IsStructureCompleted;
if (Error == 0 && !flag2)
{
    OnServer.Interact(base.InteractError, 1);
}
```

This sets `Error = 1` when ANY of these are true:
- `!base.IsInputValid`: input pipe disconnected or broken
- `!base.IsOutputValid`: output pipe disconnected or broken
- `CodeErrorState != 0`: ProgrammableChip has a runtime error
- `base.ProgrammableChip.CompilationError`: ProgrammableChip failed to compile
- `!CanMine()`: either `ThingInTheWay != null` OR `_deepMinables == null`
- `!base.IsStructureCompleted`: structure blueprint not complete

**3. OnAtmosphericTick immediately calls HandleShutDown when Error=1** — this is a consequence, not a trigger:
```csharp
// CombustionDeepMiner.OnAtmosphericTick (line 226 in decompile)
if (!OnOff || !Powered || Error == 1)
{
    _internalCombustion.HandleShutDown();
}
```

**4. AssessError (called from DeepMiner base class)** — duplicate of IsOperable logic:
```csharp
// CombustionDeepMiner.AssessError override
public override void AssessError()
{
    if (GameManager.RunSimulation)
    {
        bool flag = (bool)base.ProgrammableChip && (CodeErrorState != 0 || base.ProgrammableChip.CompilationError);
        bool flag2 = base.IsInputValid && base.IsOutputValid && !flag && CanMine() && base.IsStructureCompleted;
        if (Error == 0 && !flag2)
        {
            OnServer.Interact(base.InteractError, 1);
        }
        else if (Error == 1 && flag2)
        {
            OnServer.Interact(base.InteractError, 0);
        }
    }
}
```

## HandleShutDown implementation
<!-- verified: 0.2.6228.27061 @ 2026-04-24 -->

```csharp
// InternalCombustion.HandleShutDown
public void HandleShutDown()
{
    if (!_gainedStress)
    {
        Rpm -= 15f;
        _targetPressure = RocketMath.Lerp(PressurekPa.Zero, RunningTargetPressure, Mathf.Clamp01(Rpm / 1000f));
    }
}
```

**Behavior when shutdown is active:**
- If `_gainedStress` is false: decays RPM by 15 per tick, and `_targetPressure` slides from 4000 kPa down to 0 as RPM falls toward 0.
- Stress continues to decay at 0.5/tick in SpeedTick (handled before the shutdown branch).
- Stress does NOT freeze; it decays automatically when the mismatch condition clears.
- Chamber continues to vent via HandleGasOutput (line 228 in OnAtmosphericTick, which runs AFTER the HandleShutDown call).
- Chamber does NOT admit new gas: HandleGasInput (line 229) is gated on `Error <= 0`, so when `Error = 1`, no fuel enters.

**Consequence: when a miner crashes due to any non-stress Error = 1, the chamber fuel load drains at whatever burn-fraction rate CombustionLimiter specifies (minimum 0.1%, maximum 75%), while RPM decays by 15/tick (4.3x faster than the running friction-only decay of 1%/tick) and venting runs freely.**

## Stress dial display (real-time behavior)
<!-- verified: 0.2.6228.27061 @ 2026-04-24 -->

The stress dial is smoothed with Lerp, NOT driven by live instantaneous stress value:

```csharp
// Dial.UpdatePosition (called from CombustionDeepMiner.UpdateEachFrame every frame)
public void UpdatePosition(float target, float deltaTime)
{
    float t = Mathf.Clamp01(target / maxValue);
    float b = Mathf.Lerp(needleMinimum, needleMaximum, t);
    _lastAngle = Mathf.Lerp(_lastAngle, b, deltaTime * lerpSpeed);
    _needleTransform.localRotation = Quaternion.AngleAxis(_lastAngle, axisOfRotation);
}
```

- `lerpSpeed = 2f` (hardcoded constant in Dial.cs).
- Each frame: `_lastAngle` moves toward the target angle at rate `Mathf.Lerp(..., deltaTime * 2.0f)`.
- At 60 FPS (deltaTime ≈ 0.0167 s): dial moves `0.0167 * 2 = 0.0334 = 3.34%` of remaining distance per frame.
- **Time to reach 63% of target:** ~1 frame at 60 FPS.
- **Time to reach 95% of target:** ~89 frames ≈ 1.48 seconds.

**Consequence: A stress spike from 0 to 100 that occurs in a single 0.05 s atmospheric tick (while the dial updates at 60 FPS in the render loop) would:**
- Update the dial once per frame during the spike.
- On the frame when stress = 100: dial angle = Lerp(previous, 100_degrees, 0.0167 * 2) ≈ 3.3% of the way to full scale.
- Next frame, stress is already decaying (0.5/tick in SpeedTick).
- Dial would show a barely-visible upward twitch, then slowly creep downward.

**The player's observation that "stress dial stays low the whole time... never visibly climbs" is consistent with stress spiking above 100 and immediately crashing the miner, leaving the dial showing only the low mid-decay remainder before shutdown fully kicks in.**

## Stress audio thresholds (refined)
<!-- verified: 0.2.6228.27061 @ 2026-04-24 -->

```csharp
// CombustionDeepMiner.HandleStressAnimSound
if (_internalCombustion.Stress > 40f)
{
    float t = Mathf.Clamp01((_internalCombustion.Stress - 50f) / 50f);
    num = Mathf.Lerp(0.3f, 1f, t);
    // ... trigger / update stress audio
}
else if (_stressAudio.IsPlaying)
{
    _stressAudio.Stop();
}

if (_internalCombustion.Stress < 90f && _maxStressAudio.IsPlaying)
{
    _maxStressAudio.Stop();
}
else if (_internalCombustion.Stress > 95f && !_maxStressAudio.IsPlaying)
{
    _maxStressAudio.Trigger();
}
```

- **Warning audio** (steady hum): triggers when `Stress > 40f`, volume ramps from 0.3 to 1.0 as stress goes from 50 to 100.
- **Max-stress audio** (loud alarm): triggers when `Stress > 95f`, stops when `Stress < 90f` (hysteresis band).

The page's summary of "warn at 40, loud band 50..95, max-stress at 95" is slightly loose; the precise conditions are `Stress > 40` for warn-trigger and `Stress > 95` for max-stress.

## Additional miner-level findings (full-class decompile 2026-04-24)
<!-- verified: 0.2.6228.27061 @ 2026-04-24 -->

This section captures findings from a whole-class decompile pass of `CombustionDeepMiner` and `InternalCombustion` that are not covered by the sections above. Note: the ordering of this page is currently inconsistent because earlier sessions appended content after `## Verification history`; a future pass should move "Error = 1 trigger conditions", "HandleShutDown implementation", "Stress dial display (real-time behavior)", "Stress audio thresholds (refined)", and this section up into their logical positions before Verification History, and consolidate overlapping material with the earlier "Error states and shutdown" section.

### CanMine verbatim

```csharp
// Assembly-CSharp.dll :: Assets.Scripts.Objects.Pipes.DeepMiner.CanMine
public bool CanMine()
{
    if (!ThingInTheWay)
    {
        return _deepMinables != null;
    }
    return false;
}
```

Two conditions only: no physical obstruction (`ThingInTheWay == null`) AND an ore-generation table is set (`_deepMinables != null`). No export-chute check, no bedrock check, no silo-fill check, no power check (power is gated separately via `Powered` in `OnAtmosphericTick`'s second shutdown branch). A mining miner that stops because `!CanMine()` went false is either obstructed or lost its minable table; those are the only two levers.

`_deepMinables` is assigned once per world region from `WorldSetting.Current.Data.DeepMinablesData` on registration; the exact assignment site was not re-verified in this pass. `ThingInTheWay` is checked in `DeepMiner.CheckThingInWay()` (also not re-verified) and referenced from `DrillDownAnimation`. Both remain as open questions for a targeted decompile.

### AssessError is a second path that mirrors IsOperable

```csharp
// Assembly-CSharp.dll :: Assets.Scripts.Objects.Pipes.CombustionDeepMiner.AssessError
public override void AssessError()
{
    if (GameManager.RunSimulation)
    {
        bool flag = (bool)base.ProgrammableChip && (CodeErrorState != 0 || base.ProgrammableChip.CompilationError);
        bool flag2 = base.IsInputValid && base.IsOutputValid && !flag && CanMine() && base.IsStructureCompleted;
        if (Error == 0 && !flag2)
            OnServer.Interact(base.InteractError, 1);
        else if (Error == 1 && flag2)
            OnServer.Interact(base.InteractError, 0);
    }
}
```

`AssessError` is a base-class-driven hook that duplicates the `IsOperable` auto-toggle logic verbatim, gated on `GameManager.RunSimulation`. This means Error auto-set/clear fires from two paths per tick (once from `IsOperable` in `OnAtmosphericTick`, once from `AssessError` in whatever base tick calls it). Net behaviour is idempotent because both sides apply the same branch conditions; the redundancy matters only as noise in the `Interact` history.

### CombustionDeepMinerSaveData persists four fields

```csharp
public class CombustionDeepMinerSaveData : DeepMinerSaveData
{
    public float Throttle;
    public float CombustionLimiter;
    public float Rpm;
    public float Stress;
}
```

`InitialiseSaveData` writes these four; `DeserializeSave` reads them back in the same order. `SerializeOnJoin` / `DeserializeOnJoin` transmit the same four on network join, same order. No chamber-atmosphere persistence sits on `CombustionDeepMiner` specifically; whatever `DeviceAtmospherics` saves for `InternalAtmosphere` applies through the base path.

Saved -> loaded invariants: Throttle, CombustionLimiter, Rpm, Stress all survive save/load and rejoin. A reloaded miner resumes at its persisted RPM and stress; it does not cold-start.

### Network update flags: RPM uses 16384

Completing the flag set previously documented (4096 Throttle, 8192 CombustionLimiter, 32768 Stress):

| Bit | Field | Setter trigger |
|---|---|---|
| 4096 | Throttle | `Throttle` setter |
| 8192 | CombustionLimiter | `CombustionLimiter` setter |
| 16384 | Rpm | `Rpm` setter on the server |
| 32768 | Stress | `Stress` setter on the server |

No other flags. No custom RPCs. `ProcessUpdate` applies the four fields directly to `_internalCombustion` on the client side; no interpolation, no prediction. The dial's visual lerp (rate 2/sec) is the only smoothing anywhere in the stack.

### Player interaction buttons route through the same setters as logic

```csharp
// CombustionDeepMiner.InteractWith (button4-7 only)
case Button4: _internalCombustion.Throttle            = Mathf.Clamp(_internalCombustion.Throttle            - 10f, 0f, 100f); break;
case Button5: _internalCombustion.Throttle            = Mathf.Clamp(_internalCombustion.Throttle            + 10f, 0f, 100f); break;
case Button6: _internalCombustion.CombustionLimiter   = Mathf.Clamp(_internalCombustion.CombustionLimiter   - 10f, 0f, 100f); break;
case Button7: _internalCombustion.CombustionLimiter   = Mathf.Clamp(_internalCombustion.CombustionLimiter   + 10f, 0f, 100f); break;
```

Matters: manual button interactions and IC10 `sb ... Throttle` writes hit the same property setter. Lever animation, step-of-10 rounding, and network-flag emission fire identically regardless of path.

### Motor sound gated on RPM >= 2

```csharp
// CombustionDeepMiner sound update
if (IsOccluded || Rpm < 2f || !base.IsStructureCompleted || !OnOff || !Powered)
{
    _motorAudio.UpdatePlayState(shouldPlay: false);
}
```

The motor audio silences at RPM < 2. Low relevance for gameplay but useful when debugging - "the miner is silent" does not imply "the miner is off"; it can also mean RPM decayed to < 2 via friction.

### Awake is the only init override; `_internalCombustion` is constructed here

```csharp
public override void Awake()
{
    base.Awake();
    if (!IsCursor)
    {
        _internalCombustion = new InternalCombustion(this);
    }
    dial?.Init();
    _gear1Audio = GetAudioEvent(Animator.StringToHash("GearTwo"));
    _gear3Audio = GetAudioEvent(Animator.StringToHash("GearFour"));
    _stressAudio = GetAudioEvent(Animator.StringToHash("Stress"));
    _maxStressAudio = GetAudioEvent(Animator.StringToHash("MaxStress"));
    _motorAudio = GetAudioEvent(Animator.StringToHash("MotorRunning"));
}
```

No `OnFinishedLoad`, `OnRegistered`, or `Start` override on `CombustionDeepMiner`. `_internalCombustion` must exist before any atmospheric tick runs; the `IsCursor` guard prevents the cursor preview prefab from constructing one.

### No damage, wear, or decay on the miner itself

No `OnDamage`, `TakeDamage`, `DamageState` override, or wear-state field on `CombustionDeepMiner` or `DeepMiner`. The only degradation mechanism is RPM loss (friction, stress trip, shutdown). Save/load persistence carries Throttle, CombustionLimiter, Rpm, Stress only; no implicit "miner got damaged after 10 hours of runtime" state. A miner that behaves differently today than yesterday is not doing so because of accumulated wear.

### Atmosphere / GasMixture access for chamber composition

The fields and methods needed to read the chamber's per-gas composition and combustion-energy potential, verified verbatim:

```csharp
// Assets.Scripts.Atmospherics.Atmosphere
public MoleEnergy CombustionEnergy = MoleEnergy.Zero;   // PUBLIC FIELD, not a property
```

`Atmosphere.CombustionEnergy` is a public *field*, initialized to `MoleEnergy.Zero`. This is the value the SpeedTick gate compares against the 30 J floor (`Parent.InternalAtmosphere.CombustionEnergy + _normalCombustionEnergyCache >= moleEnergy`). Reading it requires `Type.GetField`, not `Type.GetProperty`.

```csharp
// Assets.Scripts.Atmospherics.GasMixture
public float GetGasTypeRatio(Chemistry.GasType gasType)
{
    MoleQuantity getTotalMolesGassesAndLiquids = GetTotalMolesGassesAndLiquids;
    if (getTotalMolesGassesAndLiquids.Equals(MoleQuantity.Zero))
        return 0f;
    return gasType switch
    {
        Chemistry.GasType.Oxygen => (Oxygen.Quantity / getTotalMolesGassesAndLiquids).ToFloat(),
        Chemistry.GasType.Nitrogen => (Nitrogen.Quantity / getTotalMolesGassesAndLiquids).ToFloat(),
        Chemistry.GasType.Hydrogen => (Hydrogen.Quantity / getTotalMolesGassesAndLiquids).ToFloat(),
        Chemistry.GasType.Steam => (Steam.Quantity / getTotalMolesGassesAndLiquids).ToFloat(),
        Chemistry.GasType.CarbonDioxide => (CarbonDioxide.Quantity / getTotalMolesGassesAndLiquids).ToFloat(),
        Chemistry.GasType.NitrousOxide => (NitrousOxide.Quantity / getTotalMolesGassesAndLiquids).ToFloat(),
        Chemistry.GasType.Pollutant => (Pollutant.Quantity / getTotalMolesGassesAndLiquids).ToFloat(),
        Chemistry.GasType.Ozone => (Ozone.Quantity / getTotalMolesGassesAndLiquids).ToFloat(),
        // ...
    };
}
```

Per-gas ratios are read via the `GasMixture.GetGasTypeRatio(Chemistry.GasType)` method on the chamber's `GasMixture` (accessed as `atm.GasMixture`). Returns `float` in [0, 1]. Returns 0 when the chamber has no gas. There are no per-gas Ratio properties on either `Atmosphere` or `GasMixture` (no `Atmosphere.RatioOxygen`, no `GasMixture.RatioOxygen`); earlier prose that named them as if they were properties was incorrect.

```csharp
// InternalCombustion (default namespace, in Assembly-CSharp)
private MoleEnergy _normalCombustionEnergyCache = MoleEnergy.Zero;
```

The cache is a private MoleEnergy field on the global-namespace `InternalCombustion` type. To resolve via reflection from a mod assembly: `Type.GetType("InternalCombustion, Assembly-CSharp")`, then `GetField` with `BindingFlags.Instance | BindingFlags.NonPublic`. `Harmony.AccessTools.TypeByName("InternalCombustion")` also works since it scans loaded assemblies.

Set exactly once per `OnAtmosphericTick`, inside `ManualCombust`, *before* `TryCombust` runs:

```csharp
public void ManualCombust()
{
    _normalCombustionEnergyCache = Parent.InternalAtmosphere.CombustionEnergy;
    Parent.InternalAtmosphere.TryCombust(...);
}
```

So the cache holds the *pre-burn* `CombustionEnergy` snapshot. The SpeedTick gate then sums `CombustionEnergy` (which `TryCombust` may have updated to the just-released energy) plus the cache (pre-burn potential), so the gate is effectively "did the chamber have meaningful fuel before this tick OR did this tick's burn release meaningful energy."

### InputNetwork2 is structurally present but functionally dead for this device

`DeviceInputOutput` defines `InputNetwork2` and the matching `IsInput2Valid` property. `CombustionDeepMiner` never reads either: `OnAtmosphericTick` passes only `InputNetwork` (not `InputNetwork2`) to `HandleGasInput`, and `HandleGasInput` consumes only its single parameter. The `IsOperable` / `AssessError` gates check `IsInputValid` (primary only), not `IsInput2Valid`. Consistent with the in-game observation that the miner has a single input pipe connection: the second input exists on the inheritance chain but is inert for combustion logic. Any mod that adds a second-input behaviour to this device would need to wire it explicitly.

