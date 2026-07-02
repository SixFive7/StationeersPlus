---
title: PowerTransmitter
type: GameClasses
created_in: 0.2.6228.27061
verified_in: 0.2.6403.27689
verified_at: 2026-07-02
sources:
  - Mods/PowerTransmitterPlus/RESEARCH.md:251-271
  - Mods/PowerTransmitterPlus/RESEARCH.md:273-333
  - Mods/PowerTransmitterPlus/RESEARCH.md:335-366
  - Mods/PowerTransmitterPlus/RESEARCH.md:368-377
  - Mods/PowerTransmitterPlus/RESEARCH.md:379-396
  - Mods/PowerTransmitterPlus/RESEARCH.md:211-239
  - $(StationeersPath)\rocketstation_Data\Managed\Assembly-CSharp.dll :: Assets.Scripts.Objects.Electrical.PowerTransmitter
  - .work/decomp/0.2.6403.27689/Assembly-CSharp.decompiled.cs :: lines 408269-408581 (PowerTransmitter), 408065-408262 (PowerReceiver), 408582-408702 (PowerTransmitterOmni), 272453-272550 (WirelessNetwork), 394786-394812 (Electrical), 426779 (WirelessPower)
related:
  - ./PowerReceiver.md
  - ./WirelessPower.md
  - ./PowerTransmitterOmni.md
  - ./RotatableBehaviour.md
  - ../GameSystems/PowerTickThreading.md
  - ../Patterns/HarmonyInheritedMethods.md
  - ../Patterns/MainThreadDispatcher.md
tags: [power, logic, transforms]
---

# PowerTransmitter

Vanilla game class at `Assets.Scripts.Objects.Electrical.PowerTransmitter`. Transmitter half of the dish-to-dish wireless power pair. Paired at runtime with a `PowerReceiver` via raycast and drives a private `_powerProvided` debt accumulator across two cable-network ticks.

## Class hierarchy
<!-- verified: 0.2.6403.27689 @ 2026-07-02 -->

Source: F0035; re-verified against the 0.2.6403.27689 decompile (class declarations at lines 394786 `Electrical`, 394813 `ElectricalInputOutput`, 426779 `WirelessPower`, 408269 `PowerTransmitter`, 408065 `PowerReceiver`, 408582 `PowerTransmitterOmni`).

```
MonoBehaviour
  Thing
    ...
      Device
        Electrical                ← sibling branch of ElectricalInputOutput; carries only SmartRotate
                                     members (line 394786)
          PowerTransmitterOmni    ← single-network omnidirectional charger (line 408582); NOT a
                                     WirelessPower subclass, no Horizontal/Vertical/RayTransform/
                                     DishTransform servo machinery; see PowerTransmitterOmni.md
        ElectricalInputOutput     ← public CableNetwork InputNetwork; public CableNetwork OutputNetwork; (394813)
          WirelessPower            ← public Transform RayTransform; AxleTransform; DishTransform;
                                     double Horizontal { get; set; }; double Vertical { get; set; };
                                     protected PowerTransmitterVisualiser PowerTransmitterVisualiser; (426779)
            PowerTransmitter       ← public PowerReceiver LinkedReceiver; private PowerReceiver _linkedReceiver;
                                     private float _linkedReceiverDistance; private float _powerProvided;
                                     public static float MaxPowerTransmission = 5000f;
                                     private static readonly float _MaxTransmitterDistance = 500f;
                                     public AnimationCurve PowerLossOverDistance; (408269)
            PowerReceiver          ← public PowerTransmitter LinkedPowerTransmitter;
                                     private PowerTransmitter _linkedPowerTransmitter;
                                     public Transform DishTarget; private float _powerProvided (408065)
```

`PowerTransmitterVisualiser` lives in **global namespace** (no `Assets.Scripts...` prefix). `Thing.EMISSION_COLOR = Shader.PropertyToID("_EmissionColor")`.

## Constants
<!-- verified: 0.2.6403.27689 @ 2026-07-02 -->

Source: F0038. Values read from vanilla fields on `PowerTransmitter` and related electrical classes. Re-verified against the 0.2.6403.27689 decompile: `MaxPowerTransmission = 5000f` (line 408275), `_MaxTransmitterDistance = 500f` (408273), `PowerLossOverDistance` keyframes `(0,0)/(1,1)/(2,1)` (408277), APC `BatteryChargeRate = 1000f` (390586), `BatteryCell.PowerMaximum = 36000f` (340493), `Cable.MaxVoltage = 5000f` C# default (392341).

| Constant | Class | Type | Value | Notes |
|---|---|---|---|---|
| `MaxPowerTransmission` | `PowerTransmitter` | `public static float` | `5000f` | Mutable at runtime |
| `_MaxTransmitterDistance` | `PowerTransmitter` | `private static readonly float` | `500f` | Only used as loss-curve denominator |
| `PowerLossOverDistance` | `PowerTransmitter` | `AnimationCurve` | `(0,0)→(1,1)→(2,1)` linear | `loss = distance × 10 W` capped at 5000 W |
| `BatteryChargeRate` | `AreaPowerControl` | `[SerializeField] float` | `1000f` | APC's max battery-slot charge rate |
| `PowerMaximum` | `BatteryCell` | `public float` | `36000f` | Joule capacity, NOT a rate cap |
| `MaxVoltage` | `Cable` | `float` | `5000f` | Rupture threshold in watts (despite the name) |

## Method semantics
<!-- verified: 0.2.6403.27689 @ 2026-07-02 -->

Source: F0036. Primary source for method bodies and the `TryContactReceiver` raycast condition. All bodies re-verified verbatim against the 0.2.6403.27689 decompile: `GetGeneratedPower` 408472-408484, `UsePower` 408424-408430, `GetUsedPower` 408446-408469, `ReceivePower` 408432-408444, `TryContactReceiver` 408492-408508 (raycast + dual anti-parallel 7 deg checks + `GameManager.RunSimulation && GameState == Running` host gate).

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

Unlinked-TX retry: `PowerTransmitter.OnPowerTick` (decompile 408396-408413) increments `_tryTargetCount` while `LinkedReceiver == null && OnOff`; once the count passes `ReTargetWait = 10` (private static readonly int, line 408285) it resets the counter and retries the link. New at 0.2.6403.27689: the retry goes through `WaitTryContactReceiverMainThread()` (408486-408490), an `async UniTask` that does `await UniTask.SwitchToMainThread()` before calling `TryContactReceiver()`, so the `Physics.Raycast` never runs on the power-tick ThreadPool worker.

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

## TX/RX wireless link topology (network bridging)
<!-- verified: 0.2.6403.27689 @ 2026-07-02 -->

`PowerTransmitter` (decompile line 408269) and `PowerReceiver` (line 408065) inherit from `WirelessPower`. The wireless pair is bridged through a shared `WirelessNetwork` instance:

- `WirelessPower : ElectricalInputOutput, IRotatable` (decompile line 426779). Both `PowerTransmitter` and `PowerReceiver` inherit two `CableNetwork`-typed slots from `ElectricalInputOutput`: `InputNetwork` and `OutputNetwork`. Directional usage in vanilla:
  - On a `PowerTransmitter`: `InputNetwork` is the physical cable network the dish is wired to (the power source); `OutputNetwork` is the `WirelessNetwork` (the dish's broadcast).
  - On a `PowerReceiver`: `InputNetwork` is the `WirelessNetwork` (the dish's reception, assigned from `LinkedPowerTransmitter.OutputNetwork`); `OutputNetwork` is the physical cable network the dish is wired to (the power destination).
- `PowerTransmitter.OutputNetwork` is a `WirelessNetwork` (`WirelessOutputNetwork => OutputNetwork as WirelessNetwork`). The TX's `LinkedReceiver` setter (line 408293) calls `WirelessOutputNetwork.RemoveDevice(_linkedReceiver)` (408305) then `WirelessOutputNetwork.AddDevice(value)` -- the RX is registered as a device on the TX's wireless network.
- `PowerReceiver.LinkedPowerTransmitter` setter (line 408081) executes `InputNetwork = value.OutputNetwork;` -- the RX's `InputNetwork` is assigned the TX's `OutputNetwork` reference, sharing the same WirelessNetwork instance.
- The fields backing the link are `PowerTransmitter._linkedReceiver` (private `PowerReceiver`) and `PowerReceiver._linkedPowerTransmitter` (private `PowerTransmitter`).
- For wire-side serialization, `PowerTransmitter.SerializeOnJoin` (line 408352) writes `OutputNetwork.ReferenceId` then `LinkedReceiver?.ReferenceId ?? 0`; `DeserializeOnJoin` (line 408359) reads them in the same order and rehydrates the wireless network reference via `Referencable.Find<WirelessNetwork>`. The runtime link is rebuilt via `_savedRecieverId` (note vanilla typo `Reciever`).
- `PowerTransmitter.OnDestroy` (line 408387) and `PowerReceiver.OnDestroy` (line 408108) both null-out the partner side.

### WirelessNetwork internals
<!-- verified: 0.2.6403.27689 @ 2026-07-02 -->

`WirelessNetwork : CableNetwork` (decompile lines 272453-272550) is the network type both dishes bridge through. Facts a mod touching the wireless side needs:

- `GetWirelessNetwork(WirelessPower parentTransmitter)` (272472-272484) first scans `CableNetwork.AllCableNetworks` for an existing `WirelessNetwork` whose `DeviceList` contains the caller; if none is found it creates one ONLY when `GameManager.RunSimulation` is true (host / single-player) and returns `null` on a client. Wireless network creation is host-only.
- `RefreshPowerAndDataDeviceLists()` override (272486-272492) clears both typed lists, then copies the WHOLE `DeviceList` into `_powerDeviceList` and leaves `_dataDeviceList` permanently empty. Every device on a wireless network is a power device; no data traversal ever crosses it.
- `OnPowerTick()` (272506-272512) runs the base `CableNetwork.OnPowerTick` only when `IsNetworkValid()` is true, and `IsNetworkValid()` (272514-272517) is `DeviceList.Count > 0` (a plain `CableNetwork` keys validity on `CableList.Count` instead; a wireless network has no cables).

Cable-side networks are separate from the wireless network: the TX's cable port is on its own physical `CableNetwork` (the cable network ID seen as `OutputNetworkReferenceId` in `PowerTransmitterSaveData` is the cable-side network). Same for the RX. So a TX-RX pair touches four networks total: two wireless (one shared instance bridging both sides) and two physical cable networks (one wired to the TX side, one wired to the RX side). To bridge cable-side logic across a TX-RX link you traverse: caller cable network -> caller's TX or RX -> partner via `_linkedReceiver` / `_linkedPowerTransmitter` -> partner's cable network.

## Link establishment is host-authoritative; only the TX side replicates
<!-- verified: 0.2.6403.27689 @ 2026-07-02 -->

`PowerTransmitter.TryContactReceiver` (the raycast that sets `LinkedReceiver` and mirrors `LinkedReceiver.LinkedPowerTransmitter = this`) runs only on the host: its body is wrapped in `if (GameManager.RunSimulation && GameManager.GameState == GameState.Running)` (decompile 408492-408508). Clients never establish the link locally; they receive it by replication. The two halves are asymmetric:

- TX side replicates. The `PowerTransmitter.LinkedReceiver` setter (line 408293) sets `NetworkUpdateFlags |= NetworkUpdateType.Thing.WirelessPower.Receiver` (line 408318) on the server. `PowerTransmitter.BuildUpdate` (line 408334) writes `LinkedReceiver?.ReferenceId ?? 0` under that flag; `ProcessUpdate` (line 408343) reads it back as `LinkedReceiver = Thing.Find<PowerReceiver>(reader.ReadInt64())`. `SerializeOnJoin` (408352) carries it too. So a client's `tx.LinkedReceiver` stays current.
- RX back-reference does NOT replicate. `PowerReceiver.LinkedPowerTransmitter`'s setter (line 408081) sets no `NetworkUpdateFlags` and is assigned only host-side, inside `TryContactReceiver`. There is no `BuildUpdate` / `ProcessUpdate` field carrying it. So on a client `rx.LinkedPowerTransmitter` stays null even while `tx.LinkedReceiver` points at that RX.

Consequence for cross-link cable-side logic computed per-peer: a traversal that starts from the TX side (`tx.LinkedReceiver`) resolves on a client, but one that starts from the RX side (`rx.LinkedPowerTransmitter`) does not. A logic-passthrough merge is therefore symmetric on the host (both sides set in `TryContactReceiver`) but one-directional on a client (TX-cable side sees across the link; RX-cable side does not), unless the mod mirrors the back-reference on the client itself.

## Mod extensions
<!-- verified: 0.2.6228.27061 @ 2026-04-26 -->

- PowerTransmitterPlus extends this class; see [../../Mods/PowerTransmitterPlus/RESEARCH.md](../../Mods/PowerTransmitterPlus/RESEARCH.md) for distance-cost patches, AutoAim, and related extensions.

## Verification history
<!-- verified: 0.2.6403.27689 @ 2026-07-02 -->

- 2026-07-02: conflict resolution on the class-hierarchy diagram (game version 0.2.6403.27689). Contradicted claim: the diagram placed `PowerTransmitterOmni` under the `WirelessPower` subtree (as a third sibling of `PowerTransmitter` / `PowerReceiver`), implying it inherits the servo machinery. Fresh validator verdict (binding): `public class PowerTransmitterOmni : Electrical` at decompile line 408582; `Electrical` (394786) and `ElectricalInputOutput` (394813) are SIBLINGS under `Device`; the Omni does NOT inherit `WirelessPower` and has no `Horizontal` / `Vertical` / `RayTransform` / `DishTransform` members. Resulting change: moved `PowerTransmitterOmni` out of the `WirelessPower` subtree into a sibling `Electrical` branch with a pointer to the new [PowerTransmitterOmni](./PowerTransmitterOmni.md) page.
- 2026-07-02: re-verification pass against the 0.2.6403.27689 decompile after the game update from 0.2.6228.27061. Confirmed unchanged: `PowerTransmitter : WirelessPower` (408269), `MaxPowerTransmission = 5000f` (408275), `_MaxTransmitterDistance = 500f` (408273), `GetGeneratedPower` = `Min(5000, InputNetwork.PotentialLoad) - PowerLossOverDistance.Evaluate(clamp01(dist/500)) * 5000` (408472-408484), `GetUsedPower` = `Min(5000, _powerProvided)` (408446-408469), `UsePower` / `ReceivePower` ledger (408424-408444), `TryContactReceiver` raycast + dual anti-parallel 7 deg checks + host gate (408492-408508), `ReTargetWait = 10` (408285). New at this version: the unlinked-TX retry in `OnPowerTick` (408396-408413) marshals to the main thread via `WaitTryContactReceiverMainThread` (408486-408490) before the raycast; documented in Method semantics. Added the "WirelessNetwork internals" subsection (`WirelessNetwork : CableNetwork` 272453-272550; host-only `GetWirelessNetwork` creation 272472-272484; `RefreshPowerAndDataDeviceLists` override makes the power list the whole `DeviceList` and the data list permanently empty 272486-272492; `OnPowerTick` gated on `IsNetworkValid()` = `DeviceList.Count > 0` 272506-272517). Updated the TX/RX topology and host-authoritative sections' line refs from the 0.2.6228 decompile to the 0.2.6403 one (setter 408293, flag 408318, BuildUpdate 408334, ProcessUpdate 408343, SerializeOnJoin 408352, OnDestroy 408387 / 408108).
- 2026-05-28: added "Link establishment is host-authoritative; only the TX side replicates". Confirmed `TryContactReceiver` is gated on `GameManager.RunSimulation` (host-only) in both vanilla and PowerTransmitterPlus (PTP prefix-replaces the body but keeps the gate); the `PowerTransmitter.LinkedReceiver` setter (387089) flags `NetworkUpdateType.Thing.WirelessPower.Receiver` (387114) and `BuildUpdate` / `ProcessUpdate` (387130-387146) carry it by ReferenceId; `PowerReceiver.LinkedPowerTransmitter` setter (386871) sets no flag and is assigned only host-side, so the back-reference does not replicate. Driving work: PowerGridPlus dish-link passthrough refresh, where this asymmetry makes the client merge one-directional. Additive; no prior claim contradicted, no fresh validator.
- 2026-05-17: added "TX/RX wireless link topology (network bridging)" section, sourced from decompile lines 387065-387162 (PowerTransmitter), 386861-386911 (PowerReceiver), and 405441 (`WirelessPower : ElectricalInputOutput`). Documents the shared `WirelessNetwork` between linked TX and RX (`RX.InputNetwork = TX.OutputNetwork` assignment in the `LinkedPowerTransmitter` setter), the private fields backing the link (`_linkedReceiver`, `_linkedPowerTransmitter`, `_savedRecieverId` vanilla typo), the wire format used by `SerializeOnJoin` / `DeserializeOnJoin`, the topology fact that a TX/RX pair touches four networks total (one shared wireless + two separate cable networks on each end), and the directional `InputNetwork`/`OutputNetwork` assignment on each side (TX: `InputNetwork`=cable, `OutputNetwork`=wireless; RX: `InputNetwork`=wireless, `OutputNetwork`=cable). Driving question: how would a logic-passthrough mod bridge cable-side logic across a TX-RX wireless link. Answer: in a `RefreshPowerAndDataDeviceLists` postfix, when iterating a Cable network's DeviceList and encountering a TX, the "other side" cable network is `tx.LinkedReceiver?.OutputNetwork`; encountering an RX, the other side is `rx.LinkedPowerTransmitter?.InputNetwork`.
- 2026-04-20: page created from the Research migration; verbatim content lifted from F0035, F0036, F0037, F0038, F0039, F0042, F0044, F0050, F0052, F0053, F0219z, F0300, F0301. No conflicts.
- 2026-04-20: removed PowerTransmitterPlus-specific subsections per Phase 6 Pass B editorial decision (GameClasses pages are strictly vanilla). Added mod-extensions pointer.
- 2026-04-25: refined the "Placement" line in "Transforms and geometry" to clarify the floor-only restriction comes from the prefab's serialized `AllowedRotations` value (the gate is in `InventoryManager.UpdatePlacement`, not hardcoded to `PowerTransmitter`), and added caveats covering `OnRegistered` H/V reset, TX-RX link raycast frame-invariance, and vertical clamp behavior under non-upright placement. Source: deep decompile pass producing `Research/GameClasses/AllowedRotations.md`, `Research/GameSystems/PlacementOrientation.md`, `Research/GameClasses/WirelessPower.md`. No existing factual claim was contradicted; the new wording disambiguates the previously implicit assumption.
- 2026-04-26: added "Auto-aim accuracy under non-floor mounts" section after empirical testing of wall TX + ceiling RX showed pivot-to-pivot aim leaves a 2.51 deg residual at 42 m (1.84 m off-axis at the receiver), enough to miss the narrow `Physics.Raycast` against `DishTarget`. Documents the contraction-factor analysis (`k ~ |root-to-RayTransform offset| / link_distance`) and the iterative `RayTransform` -> `DishTarget` solve template that PowerTransmitterPlus now uses. Source: empirical InspectorPlus snapshots of `RayTransform.position` / `DishTarget.position` / forwards / rights vectors during the wall+ceiling test, plus direct reads of `PowerTransmitter.TryContactReceiver` and `WirelessPower.Vertical` / `Horizontal` setters. Additive: previously the page documented only the floor-only frame-invariance argument; this section covers what changes when one or both endpoints are mounted on non-floor surfaces.
- 2026-04-26: noted in mod-extensions context that the vanilla `TryContactReceiver` right-axis antiparallel check (condition 5) is geometrically unsatisfiable for non-floor pairs because H/V control aim direction, not roll around the forward axis. Empirically on wall TX + ceiling RX: forwards angle 178.92 deg (within 7 deg of 180), rights angle 56.01 deg (124 deg outside tolerance). For floor-only pairs the rights are GEOMETRICALLY FORCED antiparallel once forwards are antiparallel (both axles spin around world up), making the check a redundant tautology. Documented in the mod's `LinkPatch.cs` rationale; no change to the central page's vanilla description, since vanilla is unchanged.

## Open questions

None at creation.
