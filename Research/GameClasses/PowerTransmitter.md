---
title: PowerTransmitter
type: GameClasses
created_in: 0.2.6228.27061
verified_in: 0.2.6228.27061
verified_at: 2026-04-26
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

Placement: floor-only in vanilla (the dish prefab's serialized `AllowedRotations` value gates the cursor; see `./AllowedRotations.md`), four cardinal rotations, `root.up = world.up` always. The aim math is frame-agnostic via `InverseTransformDirection`, so a non-floor placement (achieved by mutating `AllowedRotations` on the prefab or the construction cursor) continues to compute correct (H, V) without changes - the dish-local sphere of reachable directions rotates with the parent.

Caveats for non-floor placement:

- The `OnRegistered` reset to `(H=0, V=1)` (zenith of the dish-local frame) is a fresh-place default. For a ceiling-mounted dish, V=1 points world-down, which is the side facing away from the mount and therefore the natural "default" orientation. For a wall-mounted dish, V=1 points horizontally outward, also natural.
- The TX-RX link raycast checks `Vector3.Angle(RayTransform.forward, rx.RayTransform.forward) ~ 180` AND `Vector3.Angle(RayTransform.right, rx.RayTransform.right) ~ 180` within 7 deg. Both vectors are dish-local; the check works regardless of root orientation as long as the two dishes face each other along `RayTransform.forward`.
- The vertical-input clamps (`SetLogicValue` and the interaction increment/decrement handlers) keep V in [0..1] = [+90 deg, -90 deg] dish-local pitch. The reachable hemisphere is always the dish-local upper hemisphere; "world directions" reachable depends entirely on root orientation.

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

## Auto-aim accuracy under non-floor mounts
<!-- verified: 0.2.6228.27061 @ 2026-04-26 -->

A mod that auto-aims the dish at a target by setting `Horizontal` / `Vertical` from the world direction between two transforms inherits the offset between those transforms and the actual ray endpoints. Specifically, the link raycast in `TryContactReceiver` originates from `RayTransform.position` (a child of the rotating `Head`) and tests against `rx.DishTarget` (also a child of the receiver's rotating `Head`). Pivot-to-pivot aim (TX root -> RX root) is convenient because both pivots are rotation-invariant, but it leaves a residual angular error equal to the perpendicular component of the root-to-RayTransform offset against the aim direction.

For floor-mounted dishes the offset is mostly along world up, which is mostly perpendicular to the (mostly horizontal) aim direction; at link distances of several tens of meters that perpendicular component is small in angular terms (a fraction of a degree). For non-floor mounts (wall, ceiling, sideways) the offset maps to non-vertical world directions and the perpendicular component can dominate: empirically, a wall-mounted TX with offset magnitude 1.937 m aimed at an RX 42 m away showed a 2.51 deg residual aim error after one slew (1.84 m off-axis at the receiver), enough to make a narrow `Physics.Raycast` miss the small `DishTarget` collider entirely.

The fix is a fixed-point iteration that converges to a pose where the predicted `RayTransform.position` and the chosen `(Horizontal, Vertical)` are mutually consistent:

```
origin = current RayTransform.position
for i in 1..N:
    direction = (DishTarget - origin).normalized
    (H, V) = solve_dish_local(direction)
    origin = predict(H, V)        # apply candidate H / V to AxleTransform /
                                  # DishTransform localRotation, read
                                  # RayTransform.position, restore
    if step delta < 1 cm: break
set TargetHorizontal = H, TargetVertical = V
```

The contraction factor is approximately `k = |root-to-RayTransform offset| / link_distance`. For our 42 m wall + ceiling case `k = 1.94 / 42 = 0.046`, which converges to mm precision in 2-3 iterations. The contraction degrades at short range: at `D = |offset|` (~ 2 m) the iteration does not converge meaningfully, but a 1.9 m dish-to-dish placement is pathological. PowerTransmitterPlus caps at 10 iterations as a safety net and breaks early at the 1 cm tolerance, with the cache check on the public auto-aim entry preventing redundant solves on rewrites of the same target.

## Mod extensions
<!-- verified: 0.2.6228.27061 @ 2026-04-26 -->

- PowerTransmitterPlus extends this class; see [../../Mods/PowerTransmitterPlus/RESEARCH.md](../../Mods/PowerTransmitterPlus/RESEARCH.md) for distance-cost patches, AutoAim, and related extensions.

## Verification history
<!-- verified: 0.2.6228.27061 @ 2026-04-26 -->

- 2026-04-20: page created from the Research migration; verbatim content lifted from F0035, F0036, F0037, F0038, F0039, F0042, F0044, F0050, F0052, F0053, F0219z, F0300, F0301. No conflicts.
- 2026-04-20: removed PowerTransmitterPlus-specific subsections per Phase 6 Pass B editorial decision (GameClasses pages are strictly vanilla). Added mod-extensions pointer.
- 2026-04-25: refined the "Placement" line in "Transforms and geometry" to clarify the floor-only restriction comes from the prefab's serialized `AllowedRotations` value (the gate is in `InventoryManager.UpdatePlacement`, not hardcoded to `PowerTransmitter`), and added caveats covering `OnRegistered` H/V reset, TX-RX link raycast frame-invariance, and vertical clamp behavior under non-upright placement. Source: deep decompile pass producing `Research/GameClasses/AllowedRotations.md`, `Research/GameSystems/PlacementOrientation.md`, `Research/GameClasses/WirelessPower.md`. No existing factual claim was contradicted; the new wording disambiguates the previously implicit assumption.
- 2026-04-26: added "Auto-aim accuracy under non-floor mounts" section after empirical testing of wall TX + ceiling RX showed pivot-to-pivot aim leaves a 2.51 deg residual at 42 m (1.84 m off-axis at the receiver), enough to miss the narrow `Physics.Raycast` against `DishTarget`. Documents the contraction-factor analysis (`k ~ |root-to-RayTransform offset| / link_distance`) and the iterative `RayTransform` -> `DishTarget` solve template that PowerTransmitterPlus now uses. Source: empirical InspectorPlus snapshots of `RayTransform.position` / `DishTarget.position` / forwards / rights vectors during the wall+ceiling test, plus direct reads of `PowerTransmitter.TryContactReceiver` and `WirelessPower.Vertical` / `Horizontal` setters. Additive: previously the page documented only the floor-only frame-invariance argument; this section covers what changes when one or both endpoints are mounted on non-floor surfaces.
- 2026-04-26: noted in mod-extensions context that the vanilla `TryContactReceiver` right-axis antiparallel check (condition 5) is geometrically unsatisfiable for non-floor pairs because H/V control aim direction, not roll around the forward axis. Empirically on wall TX + ceiling RX: forwards angle 178.92 deg (within 7 deg of 180), rights angle 56.01 deg (124 deg outside tolerance). For floor-only pairs the rights are GEOMETRICALLY FORCED antiparallel once forwards are antiparallel (both axles spin around world up), making the check a redundant tautology. Documented in the mod's `LinkPatch.cs` rationale; no change to the central page's vanilla description, since vanilla is unchanged.

## Open questions

None at creation.
