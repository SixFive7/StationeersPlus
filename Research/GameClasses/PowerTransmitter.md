---
title: PowerTransmitter
type: GameClasses
created_in: 0.2.6228.27061
verified_in: 0.2.6228.27061
verified_at: 2026-04-20
sources:
  - Mods/PowerTransmitterPlus/RESEARCH.md:251-271
  - Mods/PowerTransmitterPlus/RESEARCH.md:273-333
  - Mods/PowerTransmitterPlus/RESEARCH.md:335-366
  - Mods/PowerTransmitterPlus/RESEARCH.md:368-377
  - Mods/PowerTransmitterPlus/RESEARCH.md:379-396
  - Mods/PowerTransmitterPlus/RESEARCH.md:211-239
  - $(StationeersPath)\rocketstation_Data\Managed\Assembly-CSharp.dll :: Assets.Scripts.Objects.Electrical.PowerTransmitter
related:
  - ./PowerReceiver.md
  - ./WirelessPower.md
  - ./RotatableBehaviour.md
  - ../GameSystems/PowerTickThreading.md
  - ../Patterns/HarmonyInheritedMethods.md
  - ../Patterns/MainThreadDispatcher.md
tags: [power, logic, transforms]
---

# PowerTransmitter

Vanilla game class at `Assets.Scripts.Objects.Electrical.PowerTransmitter`. Transmitter half of the dish-to-dish wireless power pair. Paired at runtime with a `PowerReceiver` via raycast and drives a private `_powerProvided` debt accumulator across two cable-network ticks.

## Class hierarchy
<!-- verified: 0.2.6228.27061 @ 2026-04-20 -->

Source: F0035.

```
MonoBehaviour
  Thing
    ...
      Device
        ElectricalInputOutput     ← public CableNetwork InputNetwork; public CableNetwork OutputNetwork;
          WirelessPower            ← public Transform RayTransform; AxleTransform; DishTransform;
                                     double Horizontal { get; set; }; double Vertical { get; set; };
                                     protected PowerTransmitterVisualiser PowerTransmitterVisualiser;
            PowerTransmitter       ← public PowerReceiver LinkedReceiver; private PowerReceiver _linkedReceiver;
                                     private float _linkedReceiverDistance; private float _powerProvided;
                                     public static float MaxPowerTransmission = 5000f;
                                     private static readonly float _MaxTransmitterDistance = 500f;
                                     public AnimationCurve PowerLossOverDistance;
            PowerReceiver          ← public PowerTransmitter LinkedPowerTransmitter;
                                     private PowerTransmitter _linkedPowerTransmitter;
                                     public Transform DishTarget; private float _powerProvided
            PowerTransmitterOmni   ← unrelated, omnidirectional charger
```

`PowerTransmitterVisualiser` lives in **global namespace** (no `Assets.Scripts...` prefix). `Thing.EMISSION_COLOR = Shader.PropertyToID("_EmissionColor")`.

## Constants
<!-- verified: 0.2.6228.27061 @ 2026-04-20 -->

Source: F0038. Values read from vanilla fields on `PowerTransmitter` and related electrical classes.

| Constant | Class | Type | Value | Notes |
|---|---|---|---|---|
| `MaxPowerTransmission` | `PowerTransmitter` | `public static float` | `5000f` | Mutable at runtime |
| `_MaxTransmitterDistance` | `PowerTransmitter` | `private static readonly float` | `500f` | Only used as loss-curve denominator |
| `PowerLossOverDistance` | `PowerTransmitter` | `AnimationCurve` | `(0,0)→(1,1)→(2,1)` linear | `loss = distance × 10 W` capped at 5000 W |
| `BatteryChargeRate` | `AreaPowerControl` | `[SerializeField] float` | `1000f` | APC's max battery-slot charge rate |
| `PowerMaximum` | `BatteryCell` | `public float` | `36000f` | Joule capacity, NOT a rate cap |
| `MaxVoltage` | `Cable` | `float` | `5000f` | Rupture threshold in watts (despite the name) |

## Method semantics
<!-- verified: 0.2.6228.27061 @ 2026-04-20 -->

Source: F0036. Primary source for method bodies and the `TryContactReceiver` raycast condition.

`PowerTransmitter.GetGeneratedPower`:
```csharp
public override float GetGeneratedPower(CableNetwork cableNetwork)
{
    if (OutputNetwork == null || Error == 1 || cableNetwork != OutputNetwork) return 0f;
    float num = PowerLossOverDistance.Evaluate(
        Mathf.Clamp01(_linkedReceiverDistance / _MaxTransmitterDistance)) * MaxPowerTransmission;
    if (!OnOff || InputNetwork == null) return 0f;
    return Mathf.Min(MaxPowerTransmission, InputNetwork.PotentialLoad) - num;
}
```

`PowerTransmitter.UsePower`:
```csharp
public override void UsePower(CableNetwork cableNetwork, float powerUsed)
{
    if (Error != 1 && OnOff && cableNetwork == WirelessOutputNetwork)
        _powerProvided += powerUsed;
}
```

`PowerTransmitter.GetUsedPower`:
```csharp
public override float GetUsedPower([NotNull] CableNetwork cableNetwork)
{
    if (InputNetwork == null) base.VisualizerIntensity = 0f;
    if (InputNetwork == null || cableNetwork != InputNetwork) return 0f;
    if (Error == 1) {
        base.VisualizerIntensity = 0f;
        if (!OnOff) return 0f;
        return UsedPower;
    }
    if (!OnOff) return 0f;
    return Mathf.Min(MaxPowerTransmission, _powerProvided);
}
```

`PowerTransmitter.ReceivePower`:
```csharp
public override void ReceivePower(CableNetwork cableNetwork, float powerAdded)
{
    if (InputNetwork == null || cableNetwork == InputNetwork) {
        if (!OnOff || InputNetwork == null) { base.VisualizerIntensity = 0f; return; }
        base.VisualizerIntensity = RocketMath.MapToScale(0f, MaxPowerTransmission, 0f, 1f, powerAdded);
        _powerProvided -= powerAdded;
    }
}
```

`TryContactReceiver` core condition (from `PowerTransmitter.cs`):
```csharp
Physics.Raycast(RayTransform.position, RayTransform.TransformDirection(Vector3.forward), out hit, float.PositiveInfinity)
    && Thing._colliderLookup.TryGetValue(hit.collider, out value) && value is PowerReceiver rx
    && hit.transform == rx.DishTarget
    && RocketMath.Approximately(Vector3.Angle(RayTransform.forward, rx.RayTransform.forward), 180f, 7f)
    && RocketMath.Approximately(Vector3.Angle(RayTransform.right,   rx.RayTransform.right),   180f, 7f)
```

Link requires: raycast hit lands on the receiver's `DishTarget` collider AND both dishes' forward axes anti-parallel AND both right axes anti-parallel, within 7° on each.

## Transforms and geometry
<!-- verified: 0.2.6228.27061 @ 2026-04-20 -->

Sources: F0037, F0053.

Both `StructurePowerTransmitter` and `StructurePowerTransmitterReceiver` share the same rig:

```
root GameObject                     pos (0, 1, 0)          rot identity
  inner StructureXxx                pos (0, 0, 0)          rot identity
    Rotation     (= AxleTransform)  pos (0, 0.27, 0)       rot identity → runtime Euler(0, H·360°, 0)
      Arm                           pos (0, 0.19, 0)       rot identity
        Head     (= DishTransform)  pos (0, 0.65, 0)       rot identity → runtime Euler(Lerp(90°, -90°, V), 0, 0)
          Line     (= RayTransform)  pos (0, 0.34, 0.03)   rot identity   ← ray origin, moves with H/V
          DishTarget (RX only)       pos (0, 0.33, 0.54)   rot identity   ← link raycast target, moves with H/V
          Transmitter (TX, dish mesh) pos (0, 0.34, 0.76)  rot identity
```

`RayTransform`, `DishTarget`, and `Transmitter` are all children of `Head` and **their world positions change as the dish rotates**. The only positions invariant under H/V rotation are the root GameObject's `transform.position` and the `Rotation` / `Arm` nodes up to `Head`.

Dish reachable space: full sphere via `V ∈ [0, 1]` (nadir → horizon → zenith) and `H ∈ [0, 1)` (full azimuth).

- V=0 (Euler X = +90°): `RayTransform.forward` = `(0, -1, 0)` in root-local = **straight down**
- V=0.5 (Euler X = 0°): `(0, 0, 1)` = horizon forward
- V=1 (Euler X = -90°): `(0, 1, 0)` = **straight up** (the vanilla rest state)

Inverse formula (world direction → (H, V)):
```
d_local = dish.transform.InverseTransformDirection((targetPos - dishPos).normalized)
V       = 0.5 + asin(d_local.y) / π
H       = (atan2(d_local.x, d_local.z) / (2π) + 1) mod 1
```
At the poles (`|d_local.y| ≈ 1`), `H` is undefined. Keep the current value.

Placement: floor-only, four cardinal rotations. `root.up = world.up` always. The math is frame-agnostic via `InverseTransformDirection`, so any future placement orientation continues to work without changes.

### Head-child drift trap (F0053)
<!-- verified: 0.2.6228.27061 @ 2026-04-20 -->

`Line` (RayTransform), `DishTarget`, and `Transmitter` are all children of `Head`, which rotates via `DishTransform.localRotation = Euler(Lerp(90°, -90°, V), 0, 0)`. Their world positions therefore change as the dish rotates. Any aim algorithm that treats them as fixed produces:

- Self-referential error when used as RAY ORIGIN: aim computed from current RayTransform position goes stale once the dish rotates and the RayTransform moves. Observed as ~0.3° drift that prevented link-raycast hits.
- Pose-lock-in when used as RAY TARGET: aiming at the other dish's RayTransform / DishTarget locks onto that dish's CURRENT pose. When the target later rotates to a correct aim, your aim is pointing at empty space.

**Rule**: use `dish.transform.position` (the placement-anchored root) as both origin and target for aim computation. Invariant under all dish rotation.

## Prefab extraction
<!-- verified: 0.2.6228.27061 @ 2026-04-20 -->

Source: F0039. Extracted via UnityPy from `rocketstation_Data/sharedassets0.assets`:

**`PowerTransmitterVisualiser` MonoBehaviour serialized values**:
- `InnerColor` = `(1, 1, 1, 1)` white
- `EmissionColor` = `(0, 0.4915, 10, 10)` HDR cyan-blue (`[ColorUsage(false, true)]`); alpha animated 0..1 by Activate/Deactivate.

**`LineRenderer` on child GO `Line` under `.../Rotation/Arm/Head/Line`**:
- `widthMultiplier` = `0.1`
- `alignment` = local
- `colorGradient` baked to semi-transparent red; **never touched at runtime**

**Material `Custom_PowerTransmission` on the LineRenderer**:
- Shader: name **stripped from build**; `Shader.Find("Custom_PowerTransmission")` will NOT find it.
- `_EmissionColor` = `(5.992, 0.188, 0, 1)` HDR orange-red baked. **Overwritten at runtime by DOColor with the MonoBehaviour's cyan-blue EmissionColor**. The baked orange-red is a vestige.

Net result: the visible in-game beam color comes from the MonoBehaviour field, not the baked material.

## Pitfalls
<!-- verified: 0.2.6228.27061 @ 2026-04-20 -->

Sources: F0052, F0053.

### DishForward is a lie for aim (F0052)

`WirelessPower.Vertical` and `Horizontal` setters both update `DishForward = DishTransform.up`, which reads naturally when inferring aim direction. **It is wrong for the raycast.** The base-game link raycast uses `RayTransform.forward`, NOT `DishTransform.up`. These two vectors are ORTHOGONAL in the local Head frame (forward is local +Z, up is local +Y). Using `DishForward` restricts aim to the upper hemisphere only, contradicting observed in-game behavior.

**Rule**: the dish's true aim direction is the raycast's direction vector. Check the actual raycast call site before deriving aim math.

### Inherited Logic methods

`CanLogicRead` / `GetLogicValue` / `SetLogicValue` / `CanLogicWrite` are declared on `WirelessPower`, NOT on `PowerTransmitter`. `[HarmonyPatch(typeof(PowerTransmitter), ...)]` fails via `AccessTools.DeclaredMethod`. Target the declaring class (`WirelessPower`) instead. See `../Patterns/HarmonyInheritedMethods.md`.

### PowerTick threading

The `VisualizerIntensity` setter on `WirelessPower` fires from a ThreadPool worker during `PowerTick`. Any Unity API from that call path hard-crashes the player. Route work to the main thread via `MainThreadDispatcher`. See `../GameSystems/PowerTickThreading.md`.

## Mod extensions
<!-- verified: 0.2.6228.27061 @ 2026-04-20 -->

- PowerTransmitterPlus extends this class; see [../../Mods/PowerTransmitterPlus/RESEARCH.md](../../Mods/PowerTransmitterPlus/RESEARCH.md) for distance-cost patches, AutoAim, and related extensions.

## Verification history
<!-- verified: 0.2.6228.27061 @ 2026-04-20 -->

- 2026-04-20: page created from the Research migration; verbatim content lifted from F0035, F0036, F0037, F0038, F0039, F0042, F0044, F0050, F0052, F0053, F0219z, F0300, F0301. No conflicts.
- 2026-04-20: removed PowerTransmitterPlus-specific subsections per Phase 6 Pass B editorial decision (GameClasses pages are strictly vanilla). Added mod-extensions pointer.

## Open questions

None at creation.
