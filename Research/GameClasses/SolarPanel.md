---
title: SolarPanel
type: GameClasses
created_in: 0.2.6228.27061
verified_in: 0.2.6228.27061
verified_at: 2026-04-26
sources:
  - rocketstation_Data/Managed/Assembly-CSharp.dll :: Assets.Scripts.Objects.Electrical.SolarPanel
  - rocketstation_Data/Managed/Assembly-CSharp.dll :: Assets.Scripts.Objects.Items.PortableSolar
  - rocketstation_Data/Managed/Assembly-CSharp.dll :: Assets.Scripts.Objects.Motherboards.SolarControl
  - rocketstation_Data/Managed/Assembly-CSharp.dll :: Assets.Scripts.Networks.SolarRadiators
  - rocketstation_Data/Managed/Assembly-CSharp.dll :: Assets.Scripts.Objects.RadiatorRotatable
  - rocketstation_Data/Managed/Assembly-CSharp.dll :: Assets.Scripts.Objects.LargeExtendableRadiator
  - rocketstation_Data/Managed/Assembly-CSharp.dll :: Assets.Scripts.Objects.Electrical.DaylightSensor
  - rocketstation_Data/StreamingAssets/Language/english.xml
related:
  - ../GameSystems/LogicType.md
  - ../GameClasses/RotatableBehaviour.md
tags: [power, logic, ic10, prefab]
---

# SolarPanel

Vanilla game class at `Assets.Scripts.Objects.Electrical.SolarPanel : Electrical, IRepairable, IRotatable, ISolarRadiator, IDensePoolable, IPowerGenerator, IReferencable, IEvaluable`. One C# class drives every fixed-mount power-generating solar panel prefab in the game; the per-prefab variants (basic, dual, flat, angled, heavy) differ in inspector-overridden `MaxPowerGenerated` and `PanelSize` values plus visuals, not in code path. The handheld `PortableSolar` is a separate class under `Assets.Scripts.Objects.Items` and follows different power math.

The `ISolarRadiator` interface covers a wider set of solar-tracking devices than just power panels. The `Networks.SolarRadiators` registry is the canonical pool of "things the game considers a solar radiator," and at v0.2.6228.27061 it has TWO implementing class trees (verified by reading `SolarRadiators.CheckSolarRadiatorWeatherDamageAction`, lines 17-33: only `SolarPanel` and `RadiatorRotatable` are type-checked):

1. `SolarPanel` (this page's primary subject) — power generation. Documented in detail below.
2. `RadiatorRotatable` and its subclass `LargeExtendableRadiator` — heat radiation, with the same orientation surface and raycast-obscurance model as `SolarPanel` but a different output (heat into pipe network, not watts into cable network). Documented in the "RadiatorRotatable (solar heat radiator)" section below.

A separate but related family is the solar SENSORS: `DaylightSensor : Sensor, IDoorControl, ILightActivated, IDensePoolable` reads sun angle and irradiance for IC10 logic but does not generate power or heat. Documented in the "DaylightSensor (solar sensor)" section below. `ILightActivated` is also implemented by `PortableSolar` (handheld) and by `Sensor` itself; the interface is the "is this thing currently in sunlight" predicate that `OrbitalSimulation` drives.

## Class hierarchy
<!-- verified: 0.2.6228.27061 @ 2026-04-25 -->

- `SolarPanel` (fixed-mount, motherboard-controllable, IC10-readable). Subclass of `Electrical`. Implements `IRotatable` (so it owns a `RotatableBehaviour`), `IPowerGenerator`, `ISolarRadiator`. Decompile: lines 20-678 of `SolarPanel.cs`.
- `PortableSolar` (handheld, charges its internal battery in sunlight). Subclass of `PowerTool` under `Assets.Scripts.Objects.Items`. Not part of the `SolarPanel` hierarchy. Decompile: 126 lines.
- `SolarControl` (the circuitboard that drives auto-tracking). Subclass of `Circuitboard` under `Assets.Scripts.Objects.Motherboards`. Pushes target horizontal/vertical to every linked `SolarPanel` via `OrientatePanel`. Decompile: 264 lines.

## Prefab variants (vanilla)
<!-- verified: 0.2.6228.27061 @ 2026-04-26 -->

Verified by reading `resources.assets` MonoBehaviours through a UnityPy `TypeTreeGenerator` (loading every Managed/*.dll except the duplicate `Sentry.System.Runtime.CompilerServices.Unsafe.dll`) so that the `SolarPanel` subclass schema is available. All eight `SolarPanel`-class prefabs found:

| Prefab key | Display name | `MaxPowerGenerated` | `PanelSize` | `_panelArea` | `WeatherDamageScale` | `PrefabHash` |
|---|---|---|---|---|---|---|
| `StructureSolarPanel` | Solar Panel | 500 W | (2, 2) | 4 m² | 1.0 | -2045627372 |
| `StructureSolarPanelReinforced` | Solar Panel (Heavy) | 500 W | (2, 2) | 4 m² | 0.0 | -934345724 |
| `StructureSolarPanelDual` | Solar Panel (Dual) | 500 W | (2, 2) | 4 m² | 1.0 | -539224550 |
| `StructureSolarPanelDualReinforced` | Solar Panel (Heavy Dual) | 500 W | (2, 2) | 4 m² | 0.0 | -1545574413 |
| `StructureSolarPanelFlat` | Solar Panel (Flat) | 500 W | (2, 2) | 4 m² | 1.0 | 1968102968 |
| `StructureSolarPanelFlatReinforced` | Solar Panel (Heavy Flat) | 500 W | (2, 2) | 4 m² | 0.0 | 1697196770 |
| `StructureSolarPanel45` | Solar Panel (Angled) | 500 W | (2, 2) | 4 m² | 1.0 | -1554349863 |
| `StructureSolarPanel45Reinforced` | Solar Panel (Heavy Angled) | 500 W | (2, 2) | 4 m² | 0.0 | 930865127 |

**Notable equality:** every fixed-mount `SolarPanel` prefab carries identical `MaxPowerGenerated = 500W` and `PanelSize = (2, 2)`. The class default `MaxPowerGenerated = 500f` matches; the class default `PanelSize = (1, 1)` is overridden on every prefab to `(2, 2)`. There is NO output difference between the standard, dual, flat, and 45-degree forms or between standard and heavy forms. Maximum power and panel area are constants across the family. The only field that differentiates standard from heavy is `WeatherDamageScale`: standard = `1.0` (full storm damage gate), heavy = `0.0` (storm-damage path skipped at the `WeatherDamageScale > 0` check in `SolarRadiators.DamageSolarRadiators`). Output efficiency at any given moment differs only via real-time orientation, occlusion, and accumulated damage state.

`StructureSolarPanelFused` appears in `english.xml` but no `SolarPanel`-class MonoBehaviour with that PrefabName exists in `resources.assets`. The english.xml entry is a leftover localization key, not a live prefab.

`PortableSolarPanel` (handheld, drives `PortableSolar`): `SolarPowerMaximum = 100f` (matches class default).

Construction kits, also from `english.xml`:

- `ItemKitSolarPanel` -> "Kit (Solar Panel)" (drives `StructureSolarPanel` / `StructureSolarPanelDual`).
- `ItemKitSolarPanelBasic` -> "Kit (Solar Panel Basic)" (drives `StructureSolarPanelFlat` / `StructureSolarPanel45`).
- `ItemKitSolarPanelReinforced` -> "Kit (Solar Panel Heavy)".
- `ItemKitSolarPanelBasicReinforced` -> "Kit (Solar Panel Basic Heavy)".

Heavy variants all carry the `<Description>` "This solar panel is resistant to storm damage." Verified mechanism: `WeatherDamageScale = 0.0` short-circuits the damage check in `SolarRadiators.DamageSolarRadiators` (the gate is `WeatherDamageScale <= 0f` -> skip).

Wreckage prefabs (`ItemWreckageSolarPanelBase`, `ItemWreckageSolarPanelFragment`, `ItemWreckageSolarPanelLarge`) exist in english.xml but are wreckage items, not functional panels.

## Power generation formula
<!-- verified: 0.2.6228.27061 @ 2026-04-25 -->

`SolarPanel.PowerGenerated()` (lines 206-215):

```csharp
public float PowerGenerated()
{
    float num = ((WeatherManager.CurrentWeatherEvent != null && WeatherManager.IsWeatherEventRunning) ? ((float)WeatherManager.CurrentWeatherEvent.SolarRatio) : 1f);
    float num2 = OrbitalSimulation.SolarIrradiance * _panelArea * num;
    float num3 = (WeatherManager.IsWeatherEventRunning ? 1.6f : 1.4f);
    float b = num2 / MaxPowerGenerated;
    b = Mathf.Max(num3, b);
    float num4 = Mathf.Log(b, num3);
    return num2 / num4;
}
```

Step-by-step:

1. `weatherFactor` = `WeatherManager.CurrentWeatherEvent.SolarRatio` if a weather event is running, else `1.0`.
2. `rawIrradiance` = `OrbitalSimulation.SolarIrradiance * _panelArea * weatherFactor`. `_panelArea = PanelSize.x * PanelSize.y`, computed once in `Awake` (line 273).
3. `efficiencyScalar` = `1.6` during a weather event (`EFFICIENCY_SCALAR_STORM`), `1.4` otherwise (`EFFICIENCY_SCALAR`). Constants at lines 28-30.
4. `b = max(efficiencyScalar, rawIrradiance / MaxPowerGenerated)`. Floors the division at `efficiencyScalar` so the log is always >= 1.
5. Output = `rawIrradiance / log_efficiencyScalar(b)`.

Effect: when `rawIrradiance <= MaxPowerGenerated * efficiencyScalar`, the divisor `log_s(b)` equals 1 and the panel returns `rawIrradiance` directly. When `rawIrradiance` exceeds that threshold the divisor grows logarithmically, soft-capping output above the rated max while still allowing some headroom.

`PowerGenerated()` is then multiplied by `GenerationEfficiency * (1 - DamageState.TotalRatio)` in `GenerationRate` (lines 169-184) to produce the final wattage:

```csharp
public float GenerationRate
{
    get
    {
        if (!IsOperable)
            return 0f;
        _generated = PowerGenerated() * GenerationEfficiency * (1f - DamageState.TotalRatio);
        if (!(_generated > MinimumToProvide))
            return 0f;
        return _generated;
    }
}
```

`MinimumToProvide = 0.1f` (line 69): below 0.1 W, the panel reports zero. `IsOperable` requires `!IsBroken && CurrentBuildStateIndex == BuildStates.Count - 1`.

## GenerationEfficiency formula
<!-- verified: 0.2.6228.27061 @ 2026-04-25 -->

`SolarPanel.CalculateSolarEfficiency()` (lines 612-651):

```csharp
public bool CalculateSolarEfficiency()
{
    if (Vector3.Dot(PanelCells.forward, OrbitalSimulation.WorldSunVector) <= 0f)
    {
        GenerationEfficiency = 0f;
        return true;
    }
    SolarVisibility = 1f;
    float num = 0.2f;
    if (IsRaycastObscured(_rayCenter))    SolarVisibility -= num;
    if (IsRaycastObscured(_rayLeftUp))    SolarVisibility -= num;
    if (IsRaycastObscured(_rayRightUp))   SolarVisibility -= num;
    if (IsRaycastObscured(_rayRightDown)) SolarVisibility -= num;
    if (IsRaycastObscured(_rayLeftDown))  SolarVisibility -= num;
    bool flag = VoxelTerrain.Instance.OctreeRaycast(PanelCells.position, OrbitalSimulation.WorldSunVector.normalized);
    if (OrbitalSimulation.IsEclipse || flag)
    {
        GenerationEfficiency = 0f;
    }
    else
    {
        GenerationEfficiency = Mathf.Clamp((1f - (PanelCells.forward - OrbitalSimulation.WorldSunVector).magnitude) * SolarVisibility, 0f, 1f);
    }
    return true;
}
```

Behavior:

- Sun on the wrong side of the panel (`dot <= 0`) -> `GenerationEfficiency = 0`.
- Five raycasts (center plus four corners offset by `RayOffset = (0.5, 0.5, 0.5)`, line 75) check structural obscurance. Each obscured raycast deducts `0.2` from `SolarVisibility`, so a fully-obscured panel reads `SolarVisibility = 0`.
- A separate `VoxelTerrain.OctreeRaycast` checks terrain occlusion.
- Eclipse or terrain-blocked -> `GenerationEfficiency = 0` regardless of orientation.
- Otherwise: `clamp((1 - |panelForward - sunVector|) * solarVisibility, 0, 1)`. Perfect alignment yields ~1; misaligned panels lose efficiency by vector distance.

`PanelCells` (line 36) is the inspector-assigned transform whose `forward` axis represents the panel's normal. Stored separately from `PanelRotation` (horizontal axis, line 39) and `PanelVertical` (vertical axis, line 42) so the math is independent of the rotation animation.

## Rotation surface
<!-- verified: 0.2.6228.27061 @ 2026-04-25 -->

Limits and tolerances (lines 123-133):

| Member | Value | Notes |
|---|---|---|
| `MaximumVertical` | `165.0` | degrees |
| `MinimumVertical` | `15.0` | degrees |
| `MaximumHorizontal` | `360.0` | degrees |
| `MovementSpeedHorizontal` | `0.05f` | virtual; subclasses can override |
| `MovementSpeedVertical` | `0.05f` | virtual; subclasses can override |
| `RotationTolerance` | `0.001f` | normalized-ratio tolerance for IC10 writes |
| `_horizontalIncrement` | `1f / 36f` | 10-degree wrench step (line 73) |

`Vertical` and `Horizontal` are `double` properties stored as `0..1` ratios internally and applied to the gizmo transforms via `Quaternion.Euler` in their setters (lines 91-121). Writing the property updates `PanelVertical.localRotation` (lerp `-75..+75`) or `PanelRotation.localRotation` (z-axis, `_horizontal * 360`). Initial values: `Horizontal = 0`, `Vertical = 0.5` set in `OnRegistered` (lines 658-659) and `Awake` (line 281).

The `RotatableBehaviour` is created in `Awake` (line 272) and slews `TargetHorizontal` / `TargetVertical` (also normalized ratios) toward the actual rotation each tick. Auto-tracking happens via `SolarControl.OrientatePanel(this)` writing the motherboard's target into the panel's `RotatableBehaviour` (lines 602-610).

Wrench interaction (lines 499-592) uses Button1-4 for vertical-up, horizontal-back, vertical-down, horizontal-forward respectively (large step 10 deg, with QuantityModifier held for 1 deg). Sound on each step: `Defines.Sounds.WrenchOneShot`.

## Logic-readable / writable properties
<!-- verified: 0.2.6228.27061 @ 2026-04-25 -->

`CanLogicRead` (lines 410-417) and `CanLogicWrite` (lines 419-426) accept the LogicTypes below; `GetLogicValue` / `SetLogicValue` (lines 428-497) implement the actual mapping.

Readable:

| LogicType | Returned value |
|---|---|
| `Horizontal` | `Horizontal * 360.0` (current horizontal angle in degrees) |
| `Vertical` | `lerp(15, 165, Vertical)` (current vertical angle in degrees) |
| `HorizontalRatio` | `Horizontal` (0-1 ratio) |
| `VerticalRatio` | `Vertical` (0-1 ratio) |
| `Charge` | `GenerationRate` (current watts) |
| `Maximum` | `PowerGenerated()` (pre-efficiency, pre-damage) |
| `Ratio` | `GenerationEfficiency` (the alignment-and-visibility coefficient, 0-1) |
| (other) | base `Electrical` returns |

The `CanLogicRead` predicate uses range tricks: `logicType - 20 <= LogicType.Power` covers `Horizontal` (20), `Vertical` (21), `Charge` (22), and `logicType - 23 <= LogicType.Power` covers `HorizontalRatio` (23), `VerticalRatio` (24), `Ratio` (25). Plus `LogicType.Charge` is checked explicitly. This is brittle: any LogicType registry shuffle breaks it.

Writable:

| LogicType | Effect |
|---|---|
| `Horizontal` | Wraps with `RocketMath.ModuloCorrect(value, 360)`, divides by 360, writes to `RotatableBehaviour.TargetHorizontal` if outside `RotationTolerance`. |
| `Vertical` | Clamps to `[15, 165]`, maps to `[0, 1]`, writes to `RotatableBehaviour.TargetVertical` if outside tolerance. |
| `HorizontalRatio` | Wraps with `ModuloCorrect(value, 1.0)`, writes to `TargetHorizontal`. |
| `VerticalRatio` | Clamps to `[0, 1]`, writes to `TargetVertical`. |

`SetLogicValue` calls `base.SetLogicValue` first (line 445) then runs the switch, so base writes are not skipped.

## SolarControl auto-tracking
<!-- verified: 0.2.6228.27061 @ 2026-04-25 -->

`SolarControl : Circuitboard` is the motherboard. Key facts:

- `MaxExtension = 1f`, `ExtensionIncrementLarge = 5`, `ExtensionIncrementSmall = 1` (lines 27-31). The +/- buttons on the screen step `TargetHorizontal` / `TargetVertical` by `referenceInt / 100f` per click (lines 209, 213, 217, 221), so a "large" press moves 0.05 (5 percent of full range) and a "small" press moves 0.01.
- `TargetHorizontal` / `TargetVertical` are `[ByteArraySync]` floats clamped `[0, 1]`. They are persisted in `SolarControlSaveData`.
- Setters call `UpdateConnectedSolars()` which iterates the `HashSet<SolarPanel> SolarPanels` and calls `OrientatePanel(this)` on each connected panel (lines 158-171). Each panel copies the motherboard's targets into its own `RotatableBehaviour.TargetHorizontal/Vertical` (`SolarPanel.OrientatePanel`, lines 602-610) and slews from there.
- `CanDeviceLink` (lines 117-124) accepts `SolarPanel` exactly OR any subclass. Mod subclasses of `SolarPanel` link automatically.
- The screen displays current connected count, smoothed total wattage (`SmoothDisplayRate = 2f`, line 33), and the current target percentages.
- `MotherboardCommand` switch handles `SolarControlCommands.IncreaseHorizontal = 1001` through `DecreaseVertical = 1004` (lines 16-21).

Note: `SolarControl` only writes to the panel's `TargetHorizontal/Vertical`. It does NOT compute the sun direction itself; the mainstream "solar tracker" IC10 patterns (read sun angle, write back to motherboard) drive this loop externally. The motherboard is a remote knob, not an autonomous tracker.

## Damage and repair
<!-- verified: 0.2.6228.27061 @ 2026-04-25 -->

- `RepairSpeedScale = 0.4f` (line 65). Static, applies to every solar panel.
- `Efficiency` (used for tooltip) is `round(GenerationEfficiency * (1 - DamageState.TotalRatio) * 100)` (line 135).
- `Health` (used for tooltip) is `round(100 - DamageState.TotalRatio * 100)` (line 137).
- `DamageColor` thresholds (lines 139-153): red above 0.75 ratio, yellow above 0.25, green below.
- `AttackWith(Attack)` (lines 373-395) accepts any source item implementing `ISolarRepairer` (duct tape, etc.). Repair duration is `solarRepairer.RepairQuantity(this) * solarRepairer.GetRepairSpeed() * RepairSpeedScale`.

## Multiplayer sync
<!-- verified: 0.2.6228.27061 @ 2026-04-25 -->

- `BuildUpdate` / `ProcessUpdate` use flag bit `256u` to ride the `Thing.NetworkUpdateFlags` delta stream. Payload is `(half TargetVertical, half TargetHorizontal)` (lines 217-240). Half-precision quantization applies (see `Patterns/Float16Quantization.md`).
- `SerializeOnJoin` / `DeserializeOnJoin` use full doubles for the same two fields (lines 242-259).
- `SolarPanelSaveData` stores `Horizontal`, `Vertical`, `TargetHorizontal`, `TargetVertical` (lines 285-332). No power-output fields persist; everything else is recomputed on load.

## PortableSolar (handheld)
<!-- verified: 0.2.6228.27061 @ 2026-04-25 -->

`PortableSolar : PowerTool, ILightActivated, IDensePoolable` is the handheld portable solar panel item (`PortableSolarPanel` prefab key). Different math, different lifecycle:

- `SolarPowerMaximum = 100f` (line 10). Class default; prefab override possible but the class hard-codes 100.
- `CenterOffset = (0, 0.5, 0)` (line 8): the panel face is half a meter above the item's origin.
- `PowerGenerated` (lines 22-32): zero unless `HasLight` (the `ILightActivated` predicate); otherwise `GenerationEfficiency * SolarPowerMaximum * OrbitalSimulation.EarthSolarRatio`. Note the `EarthSolarRatio` factor; the panel scales with body distance to sun, not local irradiance.
- `GenerationEfficiency` (lines 61-74, in `OnThreadUpdate`): `clamp(dot(WorldUpVector, WorldSunVector), 0, 1)`. The handheld panel tracks via its world-up vector, not via a fixed `PanelCells` transform. This means it reads the highest power when laid flat under the sun directly overhead, and zero when tilted past 90 degrees off vertical.
- `OnPowerTick` (lines 82-88): if there is light, the internal battery is not full, and `PowerGenerated > 0`, the battery's `PowerStored` is incremented by `PowerGenerated` directly. Single-frame, no rate scaling.
- Auto-opens (sets the `InteractOpen` interactable to 1) on `Start` if dropped to the world (`ParentSlot == null`), and auto-closes on enter-inventory (`OnEnterInventory`). Auto-reopens on exit-inventory.
- Tooltip uses `SolarVisibility` defined as `GenerationEfficiency * 100` when lit, 0 otherwise (lines 34-44). Note: this `SolarVisibility` is a percentage, NOT the same field as `SolarPanel.SolarVisibility` which is the 0-1 raycast-obscurance ratio.

PortableSolar has NO IC10 logic surface. It is not a `Logicable`. `SolarControl.CanDeviceLink` only accepts `SolarPanel`-derived devices (verified line 119), so a `PortableSolar` cannot be linked to a solar control circuit.

## Tracking the sun in IC10
<!-- verified: 0.2.6228.27061 @ 2026-04-25 -->

The canonical "solar tracker" pattern reads `Vertical` / `Horizontal` from the panel and writes back via `SolarControl`'s `Setting` channels. The motherboard's underlying targets are exposed via the `Circuitboard` base class; the `SolarControl` decompile shows no IC10 read/write override beyond what `Circuitboard` provides. Verifying which exact LogicTypes `SolarControl` accepts requires reading `Circuitboard` plus `Computer` plus the `IDeviceLink` chain; not done in this pass.

## SolarRadiators registry
<!-- verified: 0.2.6228.27061 @ 2026-04-25 -->

`Assets.Scripts.Networks.SolarRadiators` (decompile: 58 lines) is a static class holding the global pool of `ISolarRadiator`-implementing devices:

```csharp
public static class SolarRadiators
{
    private const int MAX_SOLAR_RADIATORS = 1024;
    public static readonly DensePool<ISolarRadiator> AllSolarRadiators = new DensePool<ISolarRadiator>("AllSolarRadiators", 1024);
    public const float SOLAR_PANEL_HEALTH_DAMAGE = 0.005f;
    // ...
}
```

Pool size cap is 1024 entries; exceeded entries silently drop on `Add`. `Register` and `Deregister` are called from each `ISolarRadiator`'s lifecycle (lifetime tied to `IDensePoolable` plumbing).

`DamageSolarRadiators()` runs once per weather tick when a weather event is active and the event has a non-zero `WeatherDamageMultiplier`. The per-radiator action (lines 17-33) explicitly type-tests for the two known implementers and applies different damage rules:

| Implementer | Damage condition | Damage formula |
|---|---|---|
| `SolarPanel` | `WeatherDamageScale > 0`, not broken, exposed to global atmosphere, 10 percent random roll per tick | `ThingHealth * WeatherDamageScale * 0.005 * WeatherDamageMultiplier` to Brute |
| `RadiatorRotatable` | `WeatherDamageScale > 0`, not broken, exposed to global atmosphere, 10 percent random roll per tick, AND `IsOpen == true` | same formula |

Note the asymmetry: `RadiatorRotatable` only takes weather damage when `IsOpen` (panels deployed). The "fold away to protect from storms" mechanic is enforced here. `SolarPanel` has no `IsOpen` concept; folding is not in its API. The registry's type-check list is the most authoritative count of solar-radiator categories in the game; if a third class implementing `ISolarRadiator` were added, it would still be enrolled in the pool (because the pool is keyed by interface, not concrete type) but `DamageSolarRadiators()` would not damage it because both arms of the `is` chain would fall through.

`SOLAR_PANEL_HEALTH_DAMAGE = 0.005f` is the per-tick coefficient referenced in both formulas (the literal `0.005f` in the action body matches the constant; both sites would need to update together if the value changed).

## RadiatorRotatable (solar heat radiator)
<!-- verified: 0.2.6228.27061 @ 2026-04-26 -->

`Assets.Scripts.Objects.RadiatorRotatable : Radiator, IRotatable, ISolarRadiator, IDensePoolable` (decompile: 511 lines). Heat-exchange radiator that orients toward the sun for solar HEATING (not power generation). Subclass of `Radiator : DeviceInputOutput, IThermal`; sibling classes (`PassiveRadiator`, `MediumRadiator`, `MediumRadiatorBase`, `MediumRadiatorConvection`, `PipeRadiator`, `PipePanelRadiator`) do NOT implement `ISolarRadiator` — they are convection-only.

Class hierarchy of solar-tracking radiators:

- `RadiatorRotatable` (abstract base for the rotating panel-style radiator).
- `LargeExtendableRadiator : RadiatorRotatable` (decompile: 150 lines). The only known concrete subclass at v0.2.6228.27061; corresponds to the prefab key `StructureLargeExtendableRadiator`.

### RadiatorRotatable rotation surface

| Member | Value | Notes |
|---|---|---|
| `MaximumVertical` | `180.0` | degrees (note: SolarPanel uses 165) |
| `MinimumVertical` | implicit `0` (no constant; clamp uses 0) | degrees |
| `MaximumHorizontal` | `360.0` | degrees |
| `MovementSpeedHorizontal` | `0.05f` | matches SolarPanel |
| `MovementSpeedVertical` | `0.05f` | matches SolarPanel |
| `RotationTolerance` | `0.001f` | matches SolarPanel |
| `_horizontalIncrement` | `10 / 360` | wrench step is 10 degrees, 1 degree with QuantityModifier |
| `_verticalIncrement` | `10 / 180` | wrench step is 10 degrees, 1 degree with QuantityModifier |
| `FrameUpdateCooldown` | `60` frames | `CalculateSolarEfficiency` short-circuits if called more than once per 60 frames |

Rotation is not done via the same `PanelRotation` / `PanelVertical` transform pair as `SolarPanel`. `RadiatorRotatable` exposes only `_panelRotation` (horizontal axis, line 18) — vertical is driven by an animation, not a transform-rotation property (see line 16 tooltip). Vertical changes drive the open/close fold animation. `LargeExtendableRadiator.Horizontal` setter (lines 74-88) writes `_panelRotation.localRotation = Quaternion.Euler(0, Horizontal * 360, 0)`.

### RadiatorRotatable solar-heating model

`CalculateSolarEfficiency()` (lines 303-336):

```csharp
public bool CalculateSolarEfficiency()
{
    if (_framePanelUpdated > Time.frameCount - FrameUpdateCooldown)
        return false;
    _framePanelUpdated = Time.frameCount;
    SolarVisibility = 1f;
    float num = 0.2f;
    if (CastForObsurance(_rayCenter))     SolarVisibility -= num;
    if (CastForObsurance(_rayLeftUp))     SolarVisibility -= num;
    if (CastForObsurance(_rayRightUp))    SolarVisibility -= num;
    if (CastForObsurance(_rayRightDown))  SolarVisibility -= num;
    if (CastForObsurance(_rayLeftDown))   SolarVisibility -= num;
    float a = SunAngleHeatCurve.Evaluate(Mathf.Clamp(1f - (_radiatorPanel.forward - OrbitalSimulation.WorldSunVector).magnitude, -1f, 1f));
    float b = SunAngleHeatCurve.Evaluate(Mathf.Clamp(1f - (_radiatorPanel.forward * -1f - OrbitalSimulation.WorldSunVector).magnitude, -1f, 1f));
    HeatingEfficiency = Mathf.Max(a, b) * SolarVisibility;
    return true;
}
```

Differences from `SolarPanel.CalculateSolarEfficiency`:

- 60-frame cooldown gate at the top (panel does no such throttling).
- No early-out for sun on the wrong side of the panel: a `RadiatorRotatable` is double-faced (`max(forward, -forward)` evaluation) so it heats from sun on either face.
- No eclipse / terrain occlusion early-out (panel has both).
- `HeatingEfficiency` is curved through a serialized `AnimationCurve SunAngleHeatCurve` (line 50), inspector-assigned per prefab. Solar panels use a linear `1 - |panelForward - sunVector|`. The curve gives prefab designers per-degree shaping.
- Output property is named `HeatingEfficiency`, not `GenerationEfficiency`.

### LargeExtendableRadiator heating output

`LargeExtendableRadiator` (lines 12-150) overrides three properties from the `Radiator` base class to compose the actual heat math:

```csharp
public override float ConvectionFactor       // pipe-to-atmos convection
{
    get
    {
        if (base.SourcePrefab == this) return 0.02f;   // prefab inspector value
        if (!IsOpen || IsBroken) return 0f;
        return 0.02f;
    }
}

public override float RadiationFactor        // heat radiated to space
{
    get
    {
        if (base.SourcePrefab == this) return 2f;
        if (!IsOpen || IsBroken) return 0f;
        return Mathf.Lerp(SolarRadiationModulator, 1f, base.HeatingEfficiency) / SolarRadiationModulator + 1f;
    }
}

public override float SolarHeatingFactor     // heat absorbed from sun
{
    get
    {
        if (!IsOpen || IsBroken) return 0f;
        return base.HeatingEfficiency * SolarHeatingScale;
    }
}

private float SolarRadiationModulator
{
    get
    {
        if (!IsHeatedByAtmosphere())
            return _solarRadiationRetardationDenominator;   // class default 24 (line 16); prefab override 4
        return 1f;
    }
}
```

Verified prefab override (UnityPy typetree dump of `resources.assets`): `StructureLargeExtendableRadiator` carries `_solarRadiationRetardationDenominator = 4.0f` (NOT the class default of 24f). The class-default value never reaches a placed prefab.

Behavior with the actual `4.0` denominator:

- Closed (`!IsOpen`) or broken: convection, radiation, and solar heating are all zero. The "fold away to protect from storms" UI corresponds to setting `IsOpen = false` via the open/close interactable.
- Open and unbroken with a hotter ambient atmosphere: full convection (0.02), full radiation (lerp top end), full solar heating.
- Open and unbroken in vacuum or in a colder atmosphere: full convection (still 0.02; convection is symmetric), and the radiation factor is divided down by `_solarRadiationRetardationDenominator = 4f` while pointed at the sun. Tooltip in source: "Numbers Greater than 1 reduce panel radiation effectiveness when in sunlight. Panel radiation is multiplied by 1 / value when in direct sunlight." (line 14). When pointed at the sun (`HeatingEfficiency = 1`), radiation is `lerp(4, 1, 1) / 4 + 1 = 1/4 + 1 = 1.25`. When pointed away (`HeatingEfficiency = 0`), radiation is `lerp(4, 1, 0) / 4 + 1 = 4/4 + 1 = 2.0`. So this radiator is a weaker heat-shedder when sun-pointed and a stronger heat-shedder when away from sun, by a 1.6x ratio.

`StructureLargeExtendableRadiator` also carries `WeatherDamageScale = 2.0f`. This is twice the standard `SolarPanel.WeatherDamageScale = 1.0`, so when caught open during a weather event (`IsOpen` gate in `SolarRadiators.DamageSolarRadiators`), the radiator takes Brute damage at twice the rate of an unfortified solar panel.

### RadiatorRotatable IC10 surface

| LogicType | Read | Write |
|---|---|---|
| `Horizontal` | `Horizontal * 360` | clamps to `[0, 360]`, writes `RotatableBehaviour.TargetHorizontal` |
| `Vertical` | `Vertical * 180` | clamps to `[0, 180]`, writes `RotatableBehaviour.TargetVertical` |
| `HorizontalRatio` | `Horizontal` (0-1) | clamps `[0, 1]`, writes target |
| `VerticalRatio` | `Vertical` (0-1) | clamps `[0, 1]`, writes target |

NOTE: `LargeExtendableRadiator` overrides `CanLogicRead` and `CanLogicWrite` to BLOCK `LogicType.Vertical` (lines 114-130). So the only IC10-controllable axis on the Large Extendable Radiator is horizontal. Vertical is implicitly `IsOpen`, exposed through a different channel (the `Open` button interactable, not a LogicType).

`HeatingEfficiency` and `SolarVisibility` are public properties on `RadiatorRotatable` (lines 64-66) but they are NOT exposed via `GetLogicValue`. There is no IC10 readable for "current heating ratio." Tooltip-only.

### RadiatorRotatable multiplayer sync

Identical to `SolarPanel`: flag bit `256u`, `(half TargetVertical, half TargetHorizontal)` per delta tick (lines 129-152). Save data type: `RadiatorRotatableSaveData` with `Horizontal`, `Vertical`, `TargetHorizontal`, `TargetVertical` doubles.

### RadiatorRotatable prefab variants

From `english.xml`, the only `RadiatorRotatable`-derived prefab at v0.2.6228.27061:

| Prefab key | Display name | Class | Description |
|---|---|---|---|
| `StructureLargeExtendableRadiator` | Large Extendable Radiator | `LargeExtendableRadiator` | "Optimized for radiating heat in vacuum and low pressure environments. If pointed at the sun it will heat its contents rapidly via solar heating. The panels can fold away to stop all heat radiation/solar heating and protect them from storms." |

Plus the kit (`ItemKitLargeExtendableRadiator` -> "Kit (Large Extendable Radiator)") and the wreckage (`ItemWreckageLargeExtendableRadiator` -> "Wreckage"). No "heavy" variant.

## DaylightSensor (solar sensor)
<!-- verified: 0.2.6228.27061 @ 2026-04-25 -->

`Assets.Scripts.Objects.Electrical.DaylightSensor : Sensor, IDoorControl, ILightActivated, IDensePoolable` (decompile: 187 lines). Reads the sun direction relative to the sensor's mounting orientation and exposes the result via IC10. Not a power generator, not a heat radiator, not enrolled in `SolarRadiators.AllSolarRadiators` (it implements `ILightActivated` but NOT `ISolarRadiator`).

Prefab: `StructureDaylightSensor` -> "Daylight Sensor". Sole instance.

### DaylightSensor mode and outputs

```csharp
public enum DaylightSensorMode
{
    Default,        // Mode 0: returns Vector3.Angle(Forward, sun) in degrees
    Horizontal,     // Mode 1: returns 57.29578 * azimuth (rad-to-deg)
    Vertical,       // Mode 2: returns 57.29578 * elevation
}
```

`OnThreadUpdate` (lines 99-114) runs each tick:

```csharp
Vector3 v = RocketMath.InverseTransformDirecton(OrbitalSimulation.WorldSunVector, Direction);
v = v.yxz();
RocketMath.CartesianToSpherical(out azimuth, out elevation, out radius, v);
_solarAngle = (DaylightSensorMode)Mode switch
{
    DaylightSensorMode.Horizontal => 57.29578f * azimuth,
    DaylightSensorMode.Vertical   => 57.29578f * elevation,
    _                              => Vector3.Angle(Forward, OrbitalSimulation.WorldSunVector),
};
RocketMath.CartesianToSphericalFixed(out azimuth, out elevation, out radius, v);
_horizontal = 57.29578f * azimuth;
_vertical   = 57.29578f * elevation;
```

`_horizontal` and `_vertical` always carry the (azimuth, elevation) regardless of `Mode`. `_solarAngle` follows `Mode`. So the sensor delivers all three readings simultaneously through different LogicTypes.

### DaylightSensor IC10 surface

| LogicType | Returned value |
|---|---|
| `SolarAngle` | `_solarAngle` (per `Mode`; default = total angle from forward) |
| `Horizontal` | `_horizontal` (azimuth in degrees) |
| `Vertical` | `_vertical` (elevation in degrees) |
| `SolarIrradiance` | `OrbitalSimulation.SolarIrradiance * weatherSolarRatio` if `HasLight`, else `0` |
| `Activate` | `HasLight ? 1 : 0` |
| `Mode` | mode index 0/1/2 (via `Sensor` base) |

`CanLogicRead` (lines 136-143): `logicType - 20 <= LogicType.Open || logicType == LogicType.SolarIrradiance`. The 20-30ish range covers `Horizontal` (20), `Vertical` (21), `Setting` (22 etc), through `Open` (30); plus `SolarIrradiance` separately.

### DaylightSensor as a solar tracker source

The Daylight Sensor is the canonical "what direction is the sun" signal source for IC10 solar trackers. Pattern: `Horizontal` on the sensor -> `Horizontal` on the panel motherboard (via `SolarControl`). The sensor's mounting orientation is part of the math (line 102 inverts the world sun vector through the sensor's `Direction`), so a sensor installed on a tilted surface produces tilted angles. The english.xml description hints at this: "the orientation of the sensor alters the reported solar angle, while Logic systems can be used to offset it."

The `RocketCelestialTracker` (in `Objects.Rockets.RocketCelestialTracker`) is a parallel device for use in rockets; it provides similar Horizontal/Vertical readings calibrated to the rocket's orientation. Used to align the `StructureGroundBasedTelescope`. Not decompiled in this pass; flagged in Open Questions.

## Source citations
<!-- verified: 0.2.6228.27061 @ 2026-04-25 -->

- `rocketstation_Data/Managed/Assembly-CSharp.dll :: Assets.Scripts.Objects.Electrical.SolarPanel` (decompiled 678 lines).
- `rocketstation_Data/Managed/Assembly-CSharp.dll :: Assets.Scripts.Objects.Items.PortableSolar` (decompiled 126 lines).
- `rocketstation_Data/Managed/Assembly-CSharp.dll :: Assets.Scripts.Objects.Motherboards.SolarControl` (decompiled 264 lines).
- `rocketstation_Data/StreamingAssets/Language/english.xml` lines 933, 1491, 2455-2457, 3110-3112, 3286-3287, 3850-3858, 4628-4629, 5226-5237, 5650-5677 (prefab keys, display names, descriptions).

## Open questions
<!-- verified: 0.2.6228.27061 @ 2026-04-26 -->

- `StructureSolarPanelFused` purpose. Listed in `english.xml` with display name "Solar Panel" but the UnityPy `resources.assets` typetree dump finds no `SolarPanel`-class MonoBehaviour with that PrefabName. Likely a stale localization key from a removed or renamed prefab.
- `SolarControl` IC10 surface. Whether the motherboard exposes its own `TargetHorizontal` / `TargetVertical` as readable / writable LogicTypes (vs. only via the linked panels) is determined upstream in `Circuitboard` / `Computer`. Not checked.
- `LargeExtendableRadiator.SolarHeatingScale` value. The `SolarHeatingFactor` getter multiplies `HeatingEfficiency * SolarHeatingScale` but `SolarHeatingScale` is a base-class field (`Radiator.SolarHeatingScale`), not in the `LargeExtendableRadiator` source. Not yet captured.
- `RadiatorRotatable.SunAngleHeatCurve` shape. The `AnimationCurve` is a serialized inspector field; key/value pairs not extracted in this pass.
- `RocketCelestialTracker` (`Objects.Rockets.RocketCelestialTracker`). Parallel device to `DaylightSensor` for in-rocket use; provides Horizontal/Vertical for telescope alignment. Not decompiled in this pass.

## Verification history
<!-- verified: 0.2.6228.27061 @ 2026-04-26 -->

- 2026-04-25: page created from decompile of `SolarPanel`, `PortableSolar`, `SolarControl` against game version 0.2.6228.27061. Prefab list cross-referenced against `english.xml`. Per-prefab `MaxPowerGenerated` overrides flagged in Open Questions; class default 500W documented.
- 2026-04-25: expanded scope to cover all `ISolarRadiator` implementers and adjacent solar devices. Added sections on the `Networks.SolarRadiators` registry (showing the `DamageSolarRadiators` type-test exposes exactly two implementer trees: `SolarPanel` and `RadiatorRotatable`), `RadiatorRotatable` and its sole subclass `LargeExtendableRadiator` (solar-heat radiator with `SunAngleHeatCurve`, double-faced sun reading, `SolarRadiationModulator`, 60-frame cooldown), and `DaylightSensor` (solar sensor; not in the `SolarRadiators` pool but reads sun via `OrbitalSimulation.WorldSunVector`). Frontmatter `sources:` extended.
- 2026-04-26: per-prefab field values extracted from `resources.assets` via UnityPy `TypeTreeGenerator` (loaded all `Managed/*.dll` excluding the duplicate `Sentry.System.Runtime.CompilerServices.Unsafe.dll`; built hierarchical `TypeTreeNode` from the generator's flat output by `m_Level`; called `obj.read_typetree(nodes=root)`). Findings: every fixed-mount `SolarPanel` prefab carries identical `MaxPowerGenerated = 500W` and `PanelSize = (2, 2)` -> `_panelArea = 4`; the four heavy variants set `WeatherDamageScale = 0.0` (gating mechanism for storm-damage immunity), the four standard variants set `1.0`. `StructureSolarPanelFused` has no live MonoBehaviour and is moved to Open Questions as a stale localization key. `LargeExtendableRadiator._solarRadiationRetardationDenominator` is `4.0` per prefab (class default 24 never reaches a placed instance), and its `WeatherDamageScale` is `2.0` (twice the standard panel rate). `PortableSolarPanel` confirms `SolarPowerMaximum = 100f` matches the class default. `MaxPowerGenerated` and `PanelSize` Open Questions resolved.
