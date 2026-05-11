---
title: Light Sources (built-in light emitters)
type: GameSystems
created_in: 0.2.6228.27061
verified_in: 0.2.6228.27061
verified_at: 2026-05-11
sources:
  - .work/decomp/0.2.6228.27061/Assembly-CSharp.decompiled.cs :: Assets.Scripts.Objects.ILight
  - .work/decomp/0.2.6228.27061/Assembly-CSharp.decompiled.cs :: Assets.Scripts.Objects.ThingLight
  - .work/decomp/0.2.6228.27061/Assembly-CSharp.decompiled.cs :: Assets.Scripts.Objects.WallLight / WallLightBattery / FlashingLight / GrowLight
  - .work/decomp/0.2.6228.27061/Assembly-CSharp.decompiled.cs :: Assets.Scripts.Objects.Items.StackableLight / ChemLight / RoadFlare
  - .work/decomp/0.2.6228.27061/Assembly-CSharp.decompiled.cs :: Assets.Scripts.Objects.Items.PowerTool / PortableLight / Flashlight / Headlamp / Helmet / GasMask
  - .work/decomp/0.2.6228.27061/Assembly-CSharp.decompiled.cs :: Assets.Scripts.LightChanger / LightManager / OcclusionManager
related:
  - ./RenderingPipelineAndGlow.md
  - ./PowerTickThreading.md
  - ./Occlusion.md
  - ../GameClasses/ColorSwatch.md
  - ../GameClasses/SolarPanel.md
tags: [power, unity, prefab]
---

# Light Sources (built-in light emitters)

This page catalogs every code-recognised light-emitting Thing in vanilla Stationeers (v0.2.6228.27061): how each one is powered, how it is enabled/disabled, and what the decompile does and does not tell you about its light *spread* (range, spot angle, point vs spot, intensity, color). Per-prefab `UnityEngine.Light` component values (the actual range/angle/intensity/color of each shipped prefab variant) live in `sharedassets0.assets`, not in the C# assembly; the limits of the decompile are spelled out in the last section.

For the related emissive-material / bloom glow path (ChemLight and RoadFlare also set `SetCustomColor(emissive: true)` on their *mesh* renderers, separate from their `Light` component) see `./RenderingPipelineAndGlow.md` and `../GameClasses/ColorSwatch.md`. For the sunlight-occlusion side (`ILightActivated`, `LightManager.HasSunlight`, the daylight sensor) see `../GameClasses/SolarPanel.md`.

## A. The `ILight` interface and `Thing.AllILights` / `AllIWearableLights`

<!-- verified: 0.2.6228.27061 @ 2026-05-11 -->

```csharp
public interface ILight
{
    Thing GetAsThing { get; }
    Light Light { get; }            // a single primary UnityEngine.Light
}
```

`ILight` is implemented only by `WallLight` (and its subclasses `WallLightBattery`, `FlashingLight`, `GrowLight`). `WallLight.Light` returns the serialized `private Light light` field; `WallLight.Awake()` adds `this` to the static `Thing.AllILights` list, `OnDestroy()` removes it.

A parallel marker interface, `IWearableLight`, covers worn/handheld lights:

```csharp
public interface IWearableLight : IReferencable, IEvaluable
{
    Thing GetAsThing { get; }
    bool OnOff { get; }
    long netId { get; }
}
```

Implemented by `Flashlight`, `Headlamp`, `Helmet`, and `GasMask`. Each adds itself to `Thing.AllIWearableLights` in `Awake()` (skipped when `IsCursor`) and removes itself in `OnDestroy()`.

Both lists are static and cleared on prefab reload (`Thing.AllILights.Clear()` / `AllIWearableLights.Clear()`). They exist so the storm-effect shader (`StormEffectMaterialController.UpdateLights`, only runs during a weather event) and other systems can iterate every active light cheaply. `UpdateLights` reads, per `ThingLight`, the light's world position, `transform.forward` (only for `LightType.Spot`), `color * intensity`, and `range`, sorts by camera distance, and uploads the nearest 8 to global shader vectors; it skips any Thing whose `OnOff` or `Powered` is false, or that is more than 20 m (`400f` squared) from the camera.

## B. The `ThingLight` wrapper

<!-- verified: 0.2.6228.27061 @ 2026-05-11 -->

`ThingLight` is the per-`Light`-component wrapper the game builds for every `UnityEngine.Light` found under a prefab. It is *not* a `MonoBehaviour`; it is a plain class held in `Thing.Lights` (`public List<ThingLight> Lights`).

```csharp
public class ThingLight
{
    public Thing Thing;
    public Light Light;
    public VolumetricLight VolumetricLight;   // optional component on the light GO
    public EasyFlares Flare;                   // optional component on the light GO
    public float Range;                        // captured from light.range at construction
    public LayerMask LayerMask;                // captured from light.cullingMask
    public static LayerMask BlankMask;         // shared "render to nothing" mask

    private bool IsRendered => !Thing.IsOccluded;

    public ThingLight(Light light, Thing thing)
    {
        Thing = thing;
        Light = light;
        VolumetricLight = light.GetComponent<VolumetricLight>();
        Flare = light.GetComponent<EasyFlares>();
        Range = light.range;
        LayerMask = light.cullingMask;
    }

    public void SetVisible(bool lightVisible, bool effectsVisible) { _isTurnedOn = lightVisible; _showEffects = effectsVisible; Refresh(); }

    public void Refresh()
    {
        if (Light != null) Light.cullingMask = (IsRendered ? LayerMask : BlankMask);
        if (VolumetricLight != null) VolumetricLight.enabled = ShowEffects;
        if (Flare != null) Flare.enabled = ShowEffects;
    }
}
// ShowEffects => IsRendered && _isTurnedOn && _showEffects && Light.enabled
```

How `Thing.Lights` is populated (in `Prefab.RegisterExisting`, run once per prefab at load):

```csharp
prefab.Lights.Clear();
Light[] componentsInChildren = prefab.GetComponentsInChildren<Light>(includeInactive: true);
foreach (Light obj in componentsInChildren)
{
    ThingLight thingLight = new ThingLight(obj, prefab);
    obj.shadowBias = 0.1f;                       // shadowBias forced to 0.1 on every light
    if (!obj.CompareTag("EffectLight"))          // tag "EffectLight" -> not added to Lights
    {
        prefab.Lights.Add(thingLight);
        thingLight.LayerMask = thingLight.Light.cullingMask;
    }
}
```

So: a prefab can carry any number of `UnityEngine.Light` children. The ones tagged `EffectLight` are excluded from `Lights` (and from all the toggle/occlusion logic below). `shadowBias` is normalised to `0.1` on all of them. Everything else (range, type, spot angle, intensity, color, render mode, culling mask) is whatever the prefab author set in the Unity editor; the decompile only ever *reads* `light.range` / `light.color` / `light.intensity` / `light.type` / `light.cullingMask` / `light.shadows` / `light.enabled` and writes `light.enabled`, `light.color`, `light.cullingMask`, `light.shadows`, and (for RoadFlare and a parachute case) `light.range` / `light.intensity`.

`Refresh()` is the central "should this light render" gate: it swaps the light's `cullingMask` between the prefab's mask and `BlankMask` based on `Thing.IsOccluded` (frustum/distance culling), and toggles the optional `VolumetricLight` and `EasyFlares` (light-shaft and lens-flare effect components) by `ShowEffects`.

There is also a separate light-anim helper, `LightChanger : MonoBehaviour`, used by tools that drive a single non-`ThingLight` lamp through named states:

```csharp
public class LightChanger : MonoBehaviour
{
    public class LightAnimState { public string name; public int Id; public Color color = Color.white; public bool Enabled = true;
        public void ApplyTo(Light l) { l.enabled = Enabled; l.color = color; } }
    [SerializeField] protected LightAnimState[] states;
    public Light Light;
    public void ChangeState(int id) { if (Light != null && TryGetState(id, out var s)) s.ApplyTo(Light); }
}
```

`MaterialChanger` (sibling class) does the same for `MeshRenderer` materials and is what `Flashlight._materialChanger`, `Headlamp._lightMaterialChanger`, `Suit`, etc. use to swap the glowing-bulb material when their light turns on/off. `MaterialAnimState.ApplyTo(Light l)` exists too (`l.enabled = true; l.color = materials[0].color;`), so a `MaterialChanger` state can also recolour a light from a material.

## C. Power model summary

<!-- verified: 0.2.6228.27061 @ 2026-05-11 -->

There are three power models:

1. **Cable-grid watts** — `Device`-derived. `Device.UsedPower` is a serialized field (`[Tooltip("How much power (in Watts) does the device used while turned on.")] public float UsedPower = 10f;`), defaulting to 10 W but overridden per prefab in the Unity editor. `Device.GetUsedPower(CableNetwork)` returns `0f` if `!OnOff || !IsStructureCompleted`, else `UsedPower`. `WallLight`, `FlashingLight`, and `GrowLight` use this base behaviour unchanged: their actual wattage is prefab data, not in the C# assembly.

2. **Battery (BatteryCell) drain per power tick** — `IBatteryPowered`. `WallLightBattery` (battery-backed wall light), and the wearables `Flashlight`/`Headlamp`/`Helmet` all drain a `BatteryCell` each `OnPowerTick`. `GasMask` drains the parent suit's battery. Exact figures below.

3. **None** — `StackableLight`/`ChemLight`/`RoadFlare` are `Stackable` items, not `Device`s; they consume no power. They have a finite *lifetime* instead (`LIFETIME = 180f` seconds, plus 0..5 s random) and self-destruct when it runs out.

`PowerTool` (the base for `Flashlight`, `Headlamp`, `MiningDrill`, `Defibrillator`, `PortableLight`, ...) defines the two battery-drain knobs:

```csharp
public class PowerTool : Tool, IBatteryPowered, ...
{
    [Header("Power Tool")]
    public float UsedPowerPassive;            // drained every power tick while OnOff && Powered
    public float UsedPowerActive;             // additionally drained every power tick while Activate == 1
    protected static readonly int BASEPOWERUSAGE = 1000;   // drained per OnUseItem call
    public virtual int BasePowerUsage => BASEPOWERUSAGE;

    public override void OnPowerTick()
    {
        base.OnPowerTick();
        if (OnOff && Powered && Battery != null)
        {
            Battery.PowerStored -= UsedPowerPassive;
            if (Activate == 1) Battery.PowerStored -= UsedPowerActive;
        }
    }
}
```

Both `UsedPowerPassive` and `UsedPowerActive` are `[SerializeField]`-style public fields set per prefab, so the actual numbers for the flashlight / mining headlamp are prefab data. (`Flashlight` overrides `OnPowerTick` to map its two Modes onto these fields, see section H.)

`BatteryCell.PowerStored` is in joules; one "power tick" is ~0.5 s of game time (see `./PowerTickThreading.md`), so a drain of `N` per tick is roughly `2*N` W.

## D. The light-source table

<!-- verified: 0.2.6228.27061 @ 2026-05-11 -->

| Display name (approx) | Prefab key (data, not in C#) | C# class | Category | Power model | Power figure (where in code) | Light type / spread (decompile says) |
|---|---|---|---|---|---|---|
| Wall Light / Ceiling Light Long / Ceiling Light Round / Angled Light / Sign lights, etc. | `StructureWallLight`, `StructureLightLong`, `StructureLightRound`, ... (prefab keys, not literals in the assembly) | `WallLight : SmallDevice, IAirlockDevice, ISmartRotatable, ILight` | Structure (player-built, wall-mounted) | Cable-grid watts | `Device.UsedPower` serialized field, default `10f`, **overridden per prefab** (figure NOT in code) | Single `light` + optional `terrainLight` + optional `lodFlare` (`LensFlare`); type/range/spotAngle/intensity/color all **prefab data**. The shared C# treats them generically. |
| Wall Light w/ Battery | `StructureWallLightBattery` (prefab key) | `WallLightBattery : WallLight, IBatteryPowered` | Structure | Cable watts when on a powered grid, else BatteryCell drain | `BatteryChargeRate = 200f` (W, hardcoded; rate to recharge its internal cell from the grid). On-tick drain when not grid-powered: `occupant.PowerStored -= UsedPower` (the inherited Device `UsedPower`, prefab data). | Same `Light` set as `WallLight`. |
| Emergency / Flashing Light (rotating beacon) | `StructureFlashingLight` (prefab key) | `FlashingLight : WallLight` | Structure | Cable watts (`Device.UsedPower`, prefab data) | not in code | Iterates **all** `Lights` (not just `light`); spins a `rotator` Transform at `720f` deg/s while on, powered, not occluded. Beam(s) type/range/angle = prefab data. |
| Grow Light | `StructureGrowLight` (prefab key) | `GrowLight : WallLight, ILightActivated, IDensePoolable` | Structure | Cable watts (`Device.UsedPower`, prefab data) | not in code | `NeverCastShadows => true`. Has a `BoxCollider LocalBoxCollider` trigger volume; planters/trays whose collider overlaps register into `IGrower.LinkedGrowLights`. A grower is "lit by grow light" when any linked `GrowLight` has `OnOff && Powered`. The `Light` component(s) themselves are prefab data; the gameplay growth effect is the trigger-volume membership, not the `Light`. |
| Portable Light (deployable lamp / "PortableLight") | `ItemPortableLight` / similar (prefab key) | `PortableLight : PowerTool` | Handheld / placed deployable item | BatteryCell drain via `PowerTool.OnPowerTick` | `UsedPowerPassive` / `UsedPowerActive` serialized on the prefab (figures NOT in code) | Iterates `Lights`, recolours each to `CustomColor.Light` on paint, hides them from the local player's own camera layers when held. Type/range = prefab data. |
| Flashlight (hand torch) | `ItemFlashlight` (prefab key) | `Flashlight : PowerTool, IWearableLight` | Handheld item | BatteryCell drain, two modes | `OnPowerTick`: `Battery.PowerStored -= (IsHighPower ? UsedPowerActive : (IsLowPower ? UsedPowerPassive : 0f))`. So Mode 0 ("Low Power") drains `UsedPowerPassive`/tick, Mode 1 ("High Power") drains `UsedPowerActive`/tick. Both fields are prefab data. | Exactly **two** `Light`s, `Lights[0]` = low-power beam, `Lights[1]` = high-power beam. Only one is visible at a time. Beams are re-parented either to the player's `HeadBone` (when held in a hand slot) or to the torch transform; local-player camera layers stripped from the beam mask so it doesn't blind the holder. Both beams' type/range/spotAngle/intensity = prefab data. |
| Mining Helmet / "Headlamp" (hard-hat lamp worn in helmet slot) | `ItemMiningHelmet` / similar (prefab key) | `Headlamp : PowerTool, IWearable, IWearableLight` | Wearable (helmet slot) | BatteryCell drain via `PowerTool.OnPowerTick` (`UsedPowerPassive` + `UsedPowerActive` if `Activate==1`) | serialized on prefab (figures NOT in code) | A single `GameObject _light` toggled `SetActive(OnOff && Powered)`, plus a `_lightMaterialChanger`. Auto-turns-off when placed into a `Structure`. Casts shadows only within `OcclusionManager.HelmetLightShadowDistanceSq()` (= `(LightShadowDistance/2)^2`, default `(30/2)^2 = 225`). Beam = prefab data. |
| Space Helmet / Hardsuit Helmet light | `ItemSpaceHelmet`, `ItemHardSuitHelmet` (prefab keys) | `Helmet : HelmetBase, IBatteryPowered, IWearableLight` | Wearable (helmet slot) | BatteryCell in the helmet's own battery slot | `OnPowerTick`: `Battery.PowerStored -= 5f;` per tick (**hardcoded `5f`**). `CheckPowerState()` turns the light off when the cell empties. | The `Light` component(s) are prefab data. Shadow distance = `HelmetLightShadowDistanceSq()` like `Headlamp`. `HelmetBase` provides the "Light On/Off" contextual interaction name. |
| Gas Mask light | `ItemGasMask` (prefab key) | `GasMask : AtmosphericItem, ..., IWearableLight` | Wearable (helmet slot) | Parent suit's BatteryCell | `private static float _lightPowerUsage = 50f;` (hardcoded). `OnAtmosphericTick`: `ParentBattery.PowerStored -= _lightPowerUsage;` when `OnOff && Powered` (drained per atmospheric tick, not power tick; `ParentBattery` = the worn `Suit.Battery`). | A single `GameObject _spotlight` toggled `SetActive(OnOff && Powered)`. Beam = prefab data. |
| Chem Light (glowstick) | `ItemGlowstick` / `ItemChemLight` (prefab key) | `ChemLight : StackableLight` | Handheld / dropped world item | None (finite lifetime) | `LIFETIME = 180f` s + `Random.Range(0,5)`; no power. | One `FlareLight` (`public Light FlareLight` on `StackableLight`). On activation: `FlareLight.gameObject.SetActive(true)`, `FlareLight.color = CustomColor.Light`, then a per-frame loop that just keeps the light positioned above the item (`+ up * UpOffset + up * 0.1f`) and decrements `ActualLifetime`. **No intensity flicker**, no atmosphere ignition. Also fires `SetCustomColor(emissive: true)` on the mesh (bloom glow, see `RenderingPipelineAndGlow.md`). `FlareLight` range/type/intensity = prefab data. |
| Road Flare | `ItemRoadFlare` (prefab key, hardcoded as `"ItemRoadFlare"` in one Create call) | `RoadFlare : StackableLight, IProjectile` | Handheld / thrown / dropped world item | None (finite lifetime) | `LIFETIME = 180f` s + random; `ENERGY_RELEASED_PER_TICK = 1000f`; `FuseTimeMs = 1200`. | One `FlareLight` + a `ParticleSystem LightParticles`. **Intensity flickers**: each ~0.1-0.5 s, `FlareLight.intensity = Random.Range(num, num+0.8f)` where `num = Mathf.Lerp(0.5f, 3f, ActualLifetime/180f)` (brighter when fresh, dims to ~0.5 as it burns out). `FlareLight.range` is set in code: `10f` normally (`SetNormalPhysicsAndLighting`), `25f` when its parachute is deployed (`SetParachutePhysicsAndLighting`). `OnAtmosphericTick`: if the surrounding atmosphere is above Armstrong's limit and the flare is lit, `burningAtmosphere.Sparked = true` and `GasMixture.AddEnergy(new MoleEnergy(1000.0))` — road flares ignite flammable atmospheres, chem lights do not. Also `SetCustomColor(emissive: true)` on the mesh. |
| Mining Drill (heavy / pneumatic) | `ItemMiningDrill...`, `ItemMiningDrillHeavyPneumatic` (prefab keys) | `MiningDrill : PowerTool, IMiningTool` | Handheld tool | `PowerTool` battery drain (`UsedPowerPassive`/`UsedPowerActive`) + `BasePowerUsage = 1000` per `OnUseItem` | serialized on prefab | **No light handling in C#.** `MiningDrill` does not touch `Lights`, has no `ToggleLightsOn`/`UpdateBeamStates`. If a drill prefab carries a head-lamp `Light`, it is purely a static prefab `Light` component with no on/off code path — not a code-recognised light source. |

Notes on the structure-light variants: the player-buildable ceiling/wall/sign/angled lights are **all prefabs on the `WallLight` class** (or `FlashingLight` for the rotating beacon, `GrowLight` for the hydroponics lamp, `WallLightBattery` for the battery-backed one). There is no per-shape C# class. The decompiled assembly contains **no string literals** like `"StructureWallLight"` / `"StructureLightLong"` / `"StructureLightRound"` — those prefab keys live in the world prefab list (`WorldManager.SourcePrefabs`) and the per-prefab data (mesh, `Light` components, `Device.UsedPower`, `SmartRotate` config) is in `sharedassets0.assets` / the prefab assets. So the *spread* differences between, say, a long ceiling light and a round one (point vs spot, range, spot angle, intensity, color, render mode) are **not recoverable from the C# decompile** — only from dumping the asset bundle.

## E. `WallLight`: toggling, occlusion, airlock, smart-rotate, custom colour

<!-- verified: 0.2.6228.27061 @ 2026-05-11 -->

`WallLight : SmallDevice, IAirlockDevice, ISmartRotatable, ILight`. Serialized fields: `private Light light;`, `private Light terrainLight;`, `protected LensFlare lodFlare;`. Constant `WALL_LIGHT_RENDER_DISTANCE = 40f`.

**On/off.** There is no brightness/dim control — a `WallLight` is binary. `InteractWith` on `InteractableType.OnOff` flips the device's `OnOff` state (`OnServer.Interact(interactable, state != 1 ? 1 : 0)`), refusing if `IsLocked`. `OnInteractableUpdated` for `OnOff` or `Powered` calls `ToggleLightsOn(OnOff && Powered)` and plays a switch sound:

```csharp
protected virtual void ToggleLightsOn(bool on)
{
    if (!HasBaseAnimator)
    {
        if (GetOpenEndsPermutation() != null) light.enabled = on;
        if (terrainLight) terrainLight.enabled = on;
        if (lodFlare) lodFlare.gameObject.SetActive(on);
        SetCustomColor(on);
    }
}
```

So a wall light with a base Animator drives its light through the animator instead; otherwise `light.enabled` (and `terrainLight.enabled`, and the LOD `lodFlare`) are toggled directly. `light` is only enabled when `GetOpenEndsPermutation()` is non-null (a smart-rotate consideration). The audio side (`UpdateAudio`): a `WallLightHum` pooled sound plays only when `!IsOccluded && OnOff && Powered && light`.

**Custom colour.** `SetCustomColor(int index, bool emissive=false)` chains `base.SetCustomColor`, then if `GameManager.IsValidColor(index)` calls `SetLightsCustomColor()` (sets `light.Light.color = CustomColor.Light` for every `ThingLight` in `Lights`) and then `SetCustomColor(OnOff && Powered)`. So painting a wall light recolours its actual `UnityEngine.Light`s to the swatch's `Light` colour (`ColorSwatch.Light`, a per-swatch colour distinct from `ColorSwatch.Color`; see `../GameClasses/ColorSwatch.md`). `OnAnimationStop` re-asserts the colour from `PaintableMaterial` if the current `CustomColor` isn't emissive, then `SetCustomColor(OnOff && Powered)`.

**Occlusion / shadows.** `SetOcclusion()` (called by `OcclusionManager`) decides whether the light should cast shadows: shadows on only when `Position.DistanceSquared(InventoryManager.WorldPosition) < OcclusionManager.LightShadowDistanceSq` (= `LightShadowDistance^2`, default `900` i.e. 30 m), and only when `ShadowQualitySetting != Disable` and `!NeverCastShadows`. The actual change is deferred a frame (`ShadowChange` UniTask) and maps the quality setting to `LightShadows.None` / `.Hard` / `.Soft` for every `ThingLight.Light` in `Lights`; `terrainLight.shadows` is forced to `None` on Medium/Low `ThingShadowMode`. `GetRenderMaxDistanceSquared() = (40 * RenderDistanceMultiplier)^2`; `GetShadowMaxDistanceSquared() = (40 * ThingShadowDistanceMultiplier)^2`. `IsOccluded` (a `Thing`-level property) is what `ThingLight.Refresh()` reads to swap the light's cullingMask to `BlankMask` when off-screen / too far.

**Airlock integration.** `WallLight` implements `IAirlockDevice` (so an airlock controller can include it in its managed device set and toggle it as part of cycling), but the airlock-specific behaviour is on the airlock side; `WallLight` itself just exposes the `IAirlockDevice` surface plus the standard `OnOff`/`Powered`/`Lock` device interactions.

**Smart-rotate.** `WallLight` implements `ISmartRotatable` with `ConnectionType ConnectionType = SmartRotate.ConnectionType.Exhaustive` and an `OpenEndsPermutation` int[6]; `GetConnectionType`/`SetConnectionType`/`GetOpenEndsPermutation`/`SetOpenEndsPermutation` are the contract. As noted above, `light.enabled` is only set when `GetOpenEndsPermutation()` is non-null.

`OnFinishedLoad` re-applies `SetCustomColor(OnOff && Powered)` and schedules an initial `ShadowChange`.

## F. `WallLightBattery`: battery backup

<!-- verified: 0.2.6228.27061 @ 2026-05-11 -->

`WallLightBattery : WallLight, IBatteryPowered`. Adds a battery slot (`BatterySlot => Slots[0]`, `Battery => BatterySlot.Get<BatteryCell>()`) and a hardcoded `public float BatteryChargeRate = 200f;` ("How many watts are used to charge the battery?").

- `GetUsedPower(cableNetwork)`: `(OnOff ? UsedPower : 0f)` + (if a cell is present and not charged) `Mathf.Min(BatteryChargeRate, occupant.PowerDelta)` — i.e. it draws its running wattage off the grid plus up to 200 W to top up its cell.
- `ReceivePower`: if the grid delivered less than `UsedPower`, it makes up the shortfall from the cell (`occupant.PowerStored -= min(stored, shortfall)`); if it got more than `UsedPower`, the surplus recharges the cell (`Recharge(powerAdded - UsedPower)`); a `_lastPoweredByCableOnTick` timestamp records grid power.
- `OnPowerTick`: when running and `!WasPoweredByCableLastTick`, drains the cell by `UsedPower` (`occupant.PowerStored -= UsedPower`). So off-grid, the light runs purely off its cell at `UsedPower` joules/tick.
- `CheckPowerState`: forces the device's `Powered` flag on/off depending on whether it `HasPower()` (grid this tick, or a non-empty cell). Re-checked on every interaction and on child-slot changes.

The `UsedPower` figure (inherited `Device.UsedPower`, default 10 W) is prefab data.

## G. `FlashingLight` and `GrowLight`

<!-- verified: 0.2.6228.27061 @ 2026-05-11 -->

**`FlashingLight : WallLight`** — the rotating emergency beacon. Serialized `Transform rotator`, constant `DEGREES_ROTATION_PER_SECOND = 720f`. Overrides `ToggleLightsOn` to enable/disable **every** `ThingLight.Light` in `Lights` (plus `lodFlare`), then `SetCustomColor(on)`. `UpdateEachFrame` spins `rotator` at 720 deg/s about local forward while `!IsOccluded && OnOff && Powered`. Power model is plain `WallLight`/`Device` (grid watts, prefab figure).

**`GrowLight : WallLight, ILightActivated, IDensePoolable`** — the hydroponics lamp.
- `NeverCastShadows => true` (its light never casts shadows regardless of settings).
- `Awake` caches a `BulbColorSwatch` from a serialized `Material BulbMaterial` (`GameManager.GetColorSwatch(BulbMaterial)`).
- `OnFinishedLoad` does `Physics.OverlapBox` over `LocalBoxCollider` and registers any overlapping `IGrower` (planter / hydroponics tray) into `_plantersAffected` and into that grower's `IGrower.LinkedGrowLights`. A `GrowLightEvent : MonoBehaviour` on the trigger collider forwards `OnTriggerEnter`/`OnTriggerExit` to `HandleEnterTrigger`/`HandleExitTrigger` for live add/remove. `OnDestroy` removes itself from every affected grower's list.
- Gameplay: a grower's `IsLitByGrowLight` is true iff any entry in `LinkedGrowLights` has `OnOff && Powered`. This is the actual "grow light powers photosynthesis" link; it does not depend on the `UnityEngine.Light` at all. (Compare `SolarPanel` / `LightManager.HasSunlight` for natural light; see `../GameClasses/SolarPanel.md`.)
- `SetLightsCustomColor()` is overridden to do **nothing** (a grow light keeps its fixed colour). Painting a grow light only swaps the named "Bulb" sub-material between the swatch's `Normal` and `Emissive` material via `SetBulbMaterial(OnOff && Powered)` — i.e. the bulb looks lit (emissive material) when on. Renderers tagged `NotPaintable` or not named `"Bulb"` are skipped.
- `ILightActivated` here is the marker `LightManager` uses to recheck sunlight occlusion for the thing each threaded pass — for a grow light it just means it participates in the occlusion bookkeeping, not that its own light is sun-driven.

## H. `Flashlight`, `Headlamp`, `Helmet`, `GasMask`: the wearable / handheld lights

<!-- verified: 0.2.6228.27061 @ 2026-05-11 -->

**`Flashlight : PowerTool, IWearableLight`** — the hand torch with Low/High power modes (`ModeStrings = { FlashLightModeLowPower, FlashLightModeHighPower }`).
- Power: `OnPowerTick` overrides `PowerTool`'s: `Battery.PowerStored -= (IsHighPower ? UsedPowerActive : (IsLowPower ? UsedPowerPassive : 0f))` when `OnOff && Powered`. So Low mode burns `UsedPowerPassive`/tick, High burns `UsedPowerActive`/tick (both prefab fields).
- Has **two** `ThingLight`s. `UpdateBeamStates()` (called on render start, mode/onoff/powered change, finished load, inventory move, world move): computes `flag = IsLowPower && LightIsActive`, `flag2 = IsHighPower && LightIsActive`, then `Lights[0].SetVisible(flag, flag && !IsHeldByLocalPlayer)` and `Lights[1].SetVisible(flag2, flag2 && !IsHeldByLocalPlayer)`. The inactive beam's `cullingMask` is forced to `ThingLight.BlankMask`. `LightIsActive` = `(ParentSlot == null || IsInHandSlot) && OnOff && IsOperable`.
- Beams are re-parented: held in a hand slot -> parented to `RootParentHuman.HeadBone`, localPos `(0.25, 0.25, ±0.25)` (sign by which hand), localRot `Euler(-45, -90, 0)`. Not in a hand -> parented to the torch transform, localPos `(-0.25, -0.023, -0.005)`, localRot `Euler(0, -90, 0)`. The `_materialChanger` swaps the torch body to its `OnPowered` or `Off` material.
- Beam range / spotAngle / intensity / color: prefab data.

**`Headlamp : PowerTool, IWearable, IWearableLight`** — the hard-hat head lamp (worn in the helmet slot).
- Power: plain `PowerTool.OnPowerTick` (`UsedPowerPassive` per tick, `+ UsedPowerActive` if `Activate == 1`); figures are prefab data.
- A single serialized `GameObject _light`; `OnInteractableUpdated` for `Powered`/`OnOff` does `_light.SetActive(OnOff && Powered)` and `_lightMaterialChanger.ChangeState(flag ? On : Off)`.
- `OnEnterInventory`: if turned on and dropped into a `Structure`, auto-turns-off (`OnServer.Interact(InteractOnOff, 0)`).
- Shadows: `SetOcclusion` toggles shadow casting within `OcclusionManager.HelmetLightShadowDistanceSq()` = `(LightShadowDistance/2)^2` (default `(30/2)^2 = 225`), deferred a frame via `ScheduleShadowChange`. `GetRenderMaxDistanceSquared` = `(100 * RenderDistanceMultiplier)^2` when in the helmet slot.
- Beam: prefab data.

**`Helmet : HelmetBase, IBatteryPowered, IPowered, IWearableLight`** — space / hardsuit helmet light, powered from a `BatteryCell` in the helmet's own battery slot (`BatterySlot => Slots[0]`).
- Power: `OnPowerTick`: `Battery.PowerStored -= 5f;` per tick (**hardcoded `5f`**), then `CheckPowerState()`. So a helmet light burns ~10 W. `CheckPowerState` flips `Powered` off when the cell is null/empty, on again when a non-empty cell is reinserted.
- Shadows: same `HelmetLightShadowDistanceSq()` logic and `ScheduleShadowChange` as `Headlamp`.
- `HelmetBase` supplies the `"Light On/Off"` contextual interaction name; `HelmetBase` also has the `OnEnterInventory` "turn off if dropped into a Structure" guard.
- `WaitEffects()` re-`Refresh()`es every `ThingLight` a frame after an animation stop (so the volumetric / flare effects re-sync).
- Beam(s): prefab data.

**`GasMask : AtmosphericItem, ..., IWearableLight`** — gas mask with an optional spotlight, powered from the worn suit's battery.
- Power: `private static float _lightPowerUsage = 50f;` (hardcoded). `OnAtmosphericTick`: when `OnOff && Powered && ParentBattery != null`, `ParentBattery.PowerStored -= _lightPowerUsage`. `ParentBattery` is `ParentHuman.Suit.Battery` only when the mask is in the helmet slot of a human wearing a suit; otherwise null (so the light needs a powered suit). `OnAtmosphericTick` also flips `Powered` based on whether the suit battery is present/non-empty.
- A single serialized `GameObject _spotlight`; `OnInteractableUpdated` for `OnOff`/`Powered` does `_spotlight.SetActive(OnOff && Powered)`.
- Beam: prefab data.

**`PortableLight : PowerTool`** — a deployable / handheld lamp (no Mode split). Plain `PowerTool` battery drain. Iterates `Lights`, recolours each to `CustomColor.Light` on paint. `HandleInventoryChange` strips the local player's own camera layers (`Layers.Player` / `Layers.PlayerInvisible`) from each beam's cullingMask when held by the local player so it doesn't self-blind; restores `ThingLight.LayerMask` otherwise. `OnAnimationStart`/`OnAnimationStop` track an `_isExtended` flag and re-apply the colour. Beam: prefab data.

## I. `StackableLight` / `ChemLight` / `RoadFlare`: lifetime-based emitters

<!-- verified: 0.2.6228.27061 @ 2026-05-11 -->

`StackableLight : Stackable` is the shared base for the consumable light sticks/flares.

```csharp
public class StackableLight : Stackable
{
    [Header("Road Flare")] public Light FlareLight;     // the single light component
    public float UpOffset;
    protected const float UP_SCALE = 0.1f;
    private new const float RENDER_DISTANCE = 100f;     // (100 * RenderDistanceMultiplier)^2 when not in a slot
    public const float LIFETIME = 180f;                 // seconds
    public float ActualLifetime { get; set; }

    public override void Awake() { base.Awake(); _actualLifetime = 180f + Random.Range(0, 5); }
}
```

- No power: these are `Stackable` items, not `Device`s. They burn for `LIFETIME` (180 s) + 0..5 s random, decrementing `ActualLifetime` by `Time.deltaTime` each frame in their operation coroutine, then `OnServer.Destroy(this)`.
- Activation: `OnUseSecondary` ("Light Flare" action, 0.5 s) lights one; if `Quantity > 1` it splits one off as a new `StackableLight`, drops it into a hand slot if free, decrements the stack, and `WaitThenBurn()` -> `Thing.Interact(InteractOnOff, 1)`. Stacking is blocked once `OnOff` (a lit flare can't be merged back).
- Custom colour: `StackableLight.SetCustomColor(index, emissive)` recolours every `ThingLight.Light` in `Lights` **and** `FlareLight` to `CustomColor.Light`, then `SetCustomColor(OnOff && Powered)`.
- Camera-layer handling: `HandleInventoryChange` strips `Layers.Player`/`PlayerInvisible` from `FlareLight.cullingMask` when held by the local player, restores them otherwise (so a held flare doesn't blind the holder's first-person view).
- Save data: `RoadflareSaveData` carries `Lifetime`.

**`ChemLight : StackableLight`** (the glowstick):
- On activate (`OnInteractableUpdated`, `InteractableType.OnOff`, `OnOff` true): `SetCustomColor(emissive: true)` (mesh bloom glow), then `ChemlightOperation()`.
- `ChemlightOperation`: `FlareLight.gameObject.SetActive(true)`, `FlareLight.color = CustomColor.Light` (if `CustomColor != null`), registers with `AtmosphericsManager` (but has **no** `OnAtmosphericTick` override that adds energy — so it does **not** ignite atmospheres), then a per-frame loop that just keeps `FlareLight.transform.position` pinned above the item (`ThingTransformPosition + up*UpOffset + up*0.1f`) and decrements `ActualLifetime`. **No intensity flicker.** When `ActualLifetime <= 0`, `OnServer.Destroy(this)`.
- `DeserializeSave` restarts `ChemlightOperation()` if `OnOff` after a load.
- `FlareLight` range / type / intensity: prefab data. The bright "glow" is the bloom on the emissive mesh material, not the `Light`.

**`RoadFlare : StackableLight, IProjectile`** (the road flare; can also be fired/thrown):
- Constants: `FuseTimeMs = 1200`, `ActivateLaunched = 2`, `ActivateParachuteDeployed = 3`, `ENERGY_RELEASED_PER_TICK = 1000f`.
- Has a `ParticleSystem LightParticles` (recoloured to `CustomColor.Light` on paint), plus `activateObjects[]` and a `parachute` GameObject.
- On activate (`OnInteractableUpdated`, `OnOff` true): `SetCustomColor(emissive: true)`, then `RoadFlareOperation()`.
- `RoadFlareOperation`: enables `activateObjects`, sets `FlareLight.color` and `LightParticles` start colour from `CustomColor`, registers with `AtmosphericsManager`, then a per-frame loop:
  - `SetLightVisibility(!IsOccluded)`.
  - **Intensity flicker**: every `Random.Range(0.1f, 0.5f)` s, `FlareLight.intensity = Random.Range(num, num + 0.8f)` where `num = Mathf.Lerp(0.5f, 3f, ActualLifetime / 180f)`. So a fresh flare flickers around 3.0-3.8 intensity, a near-dead one around 0.5-1.3.
  - Pins `FlareLight.transform.position` above the item like ChemLight.
  - Decrements `ActualLifetime`; when it hits 0, deregisters and `OnServer.Destroy(this)`.
- `FlareLight.range` IS set in code: `SetNormalPhysicsAndLighting()` -> `FlareLight.range = 10f`; `SetParachutePhysicsAndLighting()` -> `FlareLight.range = 25f` (and `RigidBody.drag = 10`, `mass = 0.1`, parachute active). The parachute path is the air-burst state after the flare is fired upward and the 1.2 s fuse expires.
- `OnAtmosphericTick`: gets the burning atmosphere (parent slot's internal atmosphere, else the world atmosphere at its grid, cloning the global atmosphere above Armstrong's limit); if that atmosphere `IsAboveArmstrong()` and the flare is `OnOff`, sets `burningAtmosphere.Sparked = true` and `burningAtmosphere.GasMixture.AddEnergy(new MoleEnergy(1000.0))`. **Road flares ignite flammable atmospheres** (1000 energy/tick + spark); chem lights don't.
- Projectile: `OnProjectileLaunched` rotates 90°, sets `Activate = 2`, starts `BurnFuse()` (1.2 s, then `Activate = 3` air-burst + `InteractOnOff 1`). `OnCollisionEnter` while parachute deployed resets `Activate` to 1. `OnFireStart` (caught fire) lights it via `WaitThenBurn()`.

## J. `LightManager` and `IlluminationManager` — what they do (and don't) for *lights*

<!-- verified: 0.2.6228.27061 @ 2026-05-11 -->

`LightManager : ThreadedManager` is the **sunlight-occlusion** manager, not the artificial-light manager. Despite the name it deals with whether grid cells / devices / structures / atmospheres "have sunlight":

- Constants: `GridSurfaceArea = 24f`, `LightCastDistance = 800f`, `MAX_LIGHT_ACTIVATED = 16384`, `DEFINITELY_HAS_SUNLIGHT_HEIGHT = 1000f`.
- `AllLightActivated` is a `ConcurrentDensePool<ILightActivated>`. `ILightActivated : IDensePoolable { bool IsBeingDestroyed { get; } }` is implemented by things whose sunlight state must be tracked: `Device`s (`GrowLight`, `HydroponicsAutomated`, ...), `HydroponicTray`, `DynamicThing`s. `Register`/`Deregister` add/remove.
- `ThreadedWork()` (off-main-thread, `Settings.NonFrameCriticalThreadPriority`): sets `LightRay = OrbitalSimulation.WorldSunVector * 800`, calls `CheckLightOcclusion(cell)` for every `Cell.AllCells`, then runs `AllLightActivatedAction` over `AllLightActivated`, which calls `CheckLightOcclusion(...)` per type and fires `OnHasSunlight()` / `OnLostSunlight()` (or, for a `HydroponicTray`, `Plant.HasLight = ...` + `OnHasSunlight/OnLostSunlight`).
- `HasSunlight(cell)`: `true` if `cell.Position.y >= 1000`; `false` if `OrbitalSimulation.IsEclipse`; otherwise a raycast along the sun vector.
- `AirSolarIrradiance => 0.05f * OrbitalSimulation.SolarIrradiance * (weather solar ratio)`. `SolarHeatingCurve(atmosTempK) = 1f / (1f + (atmosTempK/450f)^3.2f)`.

`LightManager` has **nothing** to do with `UnityEngine.Light` components, ranges, or spot angles. The only "artificial light" bookkeeping in code is `Thing.AllILights` / `AllIWearableLights` (section A) plus the per-Thing `ThingLight.Refresh()` cullingMask gate driven by `Thing.IsOccluded` (handled by `OcclusionManager`, not `LightManager`).

There is **no `IlluminationManager` class** in the v0.2.6228.27061 assembly. The "ambient illumination" / "is this place dark" gameplay concept is `LightManager.HasSunlight` (natural) plus the grow-light trigger-volume membership (`IGrower.IsLitByGrowLight`); the worn/handheld light beams are pure rendering with no gameplay illumination meter. The storm-shader light upload in `StormEffectMaterialController.UpdateLights` is the only place that aggregates artificial `Light` ranges/colours, and it only runs during a weather event.

`LightChanger` / `MaterialChanger` (section B) are the only "light *controller*" classes — small per-GameObject state-machine helpers, not managers.

## K. Limits of the decompile — what needs the asset bundle

<!-- verified: 0.2.6228.27061 @ 2026-05-11 -->

The C# assembly tells you the *behaviour* (on/off logic, occlusion, power model, lifetime, recolour rules, which `Light`s are toggled together) but almost nothing about the *spread* of any individual light. Specifically, the following are **prefab data in `sharedassets0.assets` (and the prefab assets), not in `Assembly-CSharp`**, and would require AssetStudio / AssetRipper to dump:

- For every structure-light prefab variant (each ceiling/wall/sign/angled light, the flashing beacon, the grow light, the battery wall light): the `UnityEngine.Light` component(s) on the prefab — `type` (Spot vs Point vs Directional), `range`, `spotAngle`, `innerSpotAngle`, `intensity`, `color`, `colorTemperature`, `renderMode`, `cullingMask`, `lightShadowCasterMode`, plus any `VolumetricLight` / `EasyFlares` / `LensFlare` / `LightChanger` sibling components and their settings.
- The wattage of each light: `Device.UsedPower` per prefab (the base default is 10 W; the real per-light value is overridden in the editor).
- `PowerTool.UsedPowerPassive` / `UsedPowerActive` for `Flashlight`, `Headlamp`, `PortableLight`, `MiningDrill` — the actual battery drain numbers.
- Whether a given drill prefab carries a head-lamp `Light` at all (the C# never touches it).
- The `UpOffset` and `FlareLight` settings of `ItemRoadFlare` / `ItemGlowstick` (range is 10 m for the road flare from code, but its `type`/`intensity`/`color`-default/`renderMode` are prefab data; the glowstick's range is entirely prefab data).
- The exact prefab key strings (`StructureWallLight`, `StructureLightLong`, `ItemFlashlight`, `ItemGlowstick`, `ItemRoadFlare` (this one is referenced as a literal once), `ItemMiningDrillHeavyPneumatic`, ...) — only `"ItemRoadFlare"` appears as a literal in the assembly; the rest are data-driven via `WorldManager.SourcePrefabs` and `Prefab.PrefabName` (set in the editor).

The hard, code-confirmed numbers are: `WallLightBattery.BatteryChargeRate = 200f` W; `Helmet` light drain `5f`/tick (~10 W); `GasMask` light drain `_lightPowerUsage = 50f`/atmospheric-tick; `StackableLight.LIFETIME = 180f` s (+0..5 s random); `RoadFlare.FlareLight.range` = `10f` normal / `25f` parachute, intensity `Random.Range(num, num+0.8f)` with `num = Lerp(0.5f, 3f, ActualLifetime/180f)`, `ENERGY_RELEASED_PER_TICK = 1000f`, `FuseTimeMs = 1200`; `FlashingLight` rotator `720f` deg/s; `Device.UsedPower` default `10f`; `PowerTool.BASEPOWERUSAGE = 1000`; `WallLight` render distance `40f`, shadow distance via `OcclusionManager.LightShadowDistanceSq` (default `900` = 30 m²); helmet/headlamp shadow distance via `HelmetLightShadowDistanceSq()` = `(LightShadowDistance/2)^2` (default `225`).

## Verification history

- 2026-05-11: page created from `.work/decomp/0.2.6228.27061/Assembly-CSharp.decompiled.cs`. All sections verified against the decompile at game version 0.2.6228.27061. Per-prefab `UnityEngine.Light` component values were not inspected (asset-bundle data); flagged in section K and the table.

## Open questions

- Per-prefab light spread for every structure-light variant (long/round/wall/angled/sign ceiling lights, flashing beacon, grow light): `type` (Spot vs Point), `range`, `spotAngle`, `intensity`, default `color`, `renderMode`. Requires dumping `sharedassets0.assets` / the prefab assets with AssetStudio / AssetRipper. Not recoverable from the C# decompile.
- The actual wattage of each shipped structure light (`Device.UsedPower` per prefab) and the battery-drain numbers for `Flashlight` (Low/High = `UsedPowerPassive`/`UsedPowerActive`), `Headlamp`, `PortableLight`. Prefab data.
- Whether the heavy pneumatic mining drill (`ItemMiningDrillHeavyPneumatic`) actually has a head-lamp `Light` child. The `MiningDrill` C# class never touches `Lights`, so even if present it has no on/off code path; confirm by inspecting the prefab.
- Does `GasMask` have a real `ThingLight` (in `Lights`) or only the `_spotlight` GameObject? It implements `IWearableLight` and is iterated by `StormEffectMaterialController.UpdateLights` via `thing.Lights`, so it presumably carries at least one non-`EffectLight`-tagged `Light` under `_spotlight`; confirm in the prefab.
