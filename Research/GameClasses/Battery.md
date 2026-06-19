---
title: Battery (station-mounted)
type: GameClasses
created_in: 0.2.6228.27061
verified_in: 0.2.6228.27061
verified_at: 2026-06-19
sources:
  - rocketstation_Data/Managed/Assembly-CSharp.dll :: Assets.Scripts.Objects.Electrical.Battery
related:
  - ./HelmetBattery.md
tags: [power, prefab]
---

# Battery (station-mounted)

Vanilla `Assets.Scripts.Objects.Electrical.Battery`. Base class for the wall / floor station batteries (and what third-party mods like MorePowerMod's `StationBatteryNuclear` inherit from). Drives the on-prefab "charge bar" display: a stack of segment renderers whose count and material are recomputed from the current charge ratio every power tick.

Distinct from `Assets.Scripts.Objects.Items.BatteryCell` (the handheld cell, see `HelmetBattery.md`). The two classes share the same charge-state ladder verbatim but `Battery` adds the segment-bar display and a flashing-critical coroutine that `BatteryCell` does not have.

## Charge state ladder
<!-- verified: 0.2.6228.27061 @ 2026-04-28 -->

`OnPowerTick()` recomputes `_batteryState` from `num = _powerStored / PowerMaximum` (verbatim):

```csharp
public override void OnPowerTick()
{
    base.OnPowerTick();
    if (!_destroyed)
    {
        float num = _powerStored / PowerMaximum;
        _batteryState = BatteryCellState.Empty;
        if (num >= 0.999f)        { _batteryState = BatteryCellState.Full;     }
        else if (num >= 0.75f)    { _batteryState = BatteryCellState.High;     }
        else if (num >= 0.5f)     { _batteryState = BatteryCellState.Medium;   }
        else if (num >= 0.25f)    { _batteryState = BatteryCellState.Low;      }
        else if (num >= 0.1f)     { _batteryState = BatteryCellState.VeryLow;  }
        else if (num > 0f)        { _batteryState = BatteryCellState.Critical; }
        // num == 0 keeps the initial Empty assignment
        ...
    }
}
```

Resulting threshold table (identical to `BatteryCell.UpdateBatteryState`; see `HelmetBattery.md` for the enum and the `IsEmpty / IsCritical / IsLow / IsCharged` companion predicates):

| Charge ratio (`PowerStored / PowerMaximum`) | `BatteryCellState` | `Mode` |
|---|---|---|
| `num == 0`              | `Empty`    | 0 |
| `0 < num < 0.1`         | `Critical` | 1 |
| `0.1  <= num < 0.25`    | `VeryLow`  | 2 |
| `0.25 <= num < 0.5`     | `Low`      | 3 |
| `0.5  <= num < 0.75`    | `Medium`   | 4 |
| `0.75 <= num < 0.999`   | `High`     | 5 |
| `num >= 0.999`          | `Full`     | 6 |

`Powered => _batteryState != BatteryCellState.Empty` (any non-zero charge powers the device, including the `Critical` band).

## Display: Mode -> renderer count + material
<!-- verified: 0.2.6228.27061 @ 2026-04-28 -->

The on-prefab segment bar is driven by `RefreshAnimState` -> `SetRenderersState(int numberToEnable, Material material)`. Both the count of segments lit AND the material applied to them come from `Mode`:

```csharp
protected override void RefreshAnimState(bool skipAnimation = false)
{
    base.RefreshAnimState(skipAnimation);
    if (!OnOff)
    {
        SetRenderersState(0, displayModeMaterials[Mode]);
        return;
    }
    switch (Mode)
    {
    case 0: SetRenderersState(0, displayModeMaterials[Mode]); break; // Empty
    case 1:
        if (_flashingTask.Status != UniTaskStatus.Pending)
            _flashingTask = FlashingDisplay();                       // Critical (blink)
        break;
    case 2: SetRenderersState(1, displayModeMaterials[Mode]); break; // VeryLow
    case 3: SetRenderersState(2, displayModeMaterials[Mode]); break; // Low
    case 4: SetRenderersState(3, displayModeMaterials[Mode]); break; // Medium
    case 5: SetRenderersState(4, displayModeMaterials[Mode]); break; // High
    case 6: SetRenderersState(5, displayModeMaterials[Mode]); break; // Full
    }
}
```

`SetRenderersState` activates the first `numberToEnable` entries of `displayRenderers[]` and assigns `material` to each:

```csharp
private void SetRenderersState(int numberToEnable, Material material)
{
    if (displayRenderers == null) return;
    for (int num = displayRenderers.Length - 1; num >= 0; num--)
    {
        MeshRenderer meshRenderer = displayRenderers[num];
        if ((bool)meshRenderer)
        {
            bool flag = base.IsStructureCompleted && num < numberToEnable;
            meshRenderer.gameObject.SetActive(flag);
            if (flag) meshRenderer.material = material;
        }
    }
}
```

Mapping (segment count and material slot per `Mode`):

| `Mode` | State    | Segments lit | Material slot          |
|---|---|---|---|
| 0      | Empty    | 0            | `displayModeMaterials[0]` (none lit) |
| 1      | Critical | 1 (blinking) | toggles `displayModeMaterials[1]` and `[0]` every 250ms |
| 2      | VeryLow  | 1            | `displayModeMaterials[2]` |
| 3      | Low      | 2            | `displayModeMaterials[3]` |
| 4      | Medium   | 3            | `displayModeMaterials[4]` |
| 5      | High     | 4            | `displayModeMaterials[5]` |
| 6      | Full     | 5            | `displayModeMaterials[6]` |

The actual color of each `displayModeMaterials[i]` is set in the prefab (Unity inspector / `sharedassets`), not in code. Visually, vanilla station batteries show the bar as red at low Modes, orange at Medium, and green at High / Full; the exact RGB lives in the prefab material array, not here.

`OnOff == false` short-circuits to "all segments off" regardless of `Mode`. The `IsStructureCompleted` guard inside `SetRenderersState` keeps the bar dark while the prefab is mid-build.

## Critical-mode flashing coroutine
<!-- verified: 0.2.6228.27061 @ 2026-04-28 -->

`Mode == 1` (Critical, charge in `(0, 0.1)`) does not call `SetRenderersState` directly; it kicks off the `FlashingDisplay` UniTask which toggles the single segment between the Critical material and the Empty material every 250 ms:

```csharp
private async UniTask FlashingDisplay()
{
    CancellationToken cancelToken = this.GetCancellationTokenOnDestroy();
    while (Mode == 1 && OnOff)
    {
        SetRenderersState(1, displayModeMaterials[Mode]);
        await UniTask.Delay(250, ignoreTimeScale: false, PlayerLoopTiming.Update, cancelToken);
        if (cancelToken.IsCancellationRequested || Mode != 1 || !OnOff) break;
        SetRenderersState(1, displayModeMaterials[0]);
        await UniTask.Delay(250, ignoreTimeScale: false, PlayerLoopTiming.Update, cancelToken);
        if (cancelToken.IsCancellationRequested) break;
    }
}
```

The loop self-exits when `Mode` leaves Critical or when the device is switched off. The `_flashingTask.Status != UniTaskStatus.Pending` guard in `RefreshAnimState` prevents stacking multiple flashers when `RefreshAnimState` is called repeatedly while still in the Critical band.

Net effect: a battery dropping into the `(0, 10%)` band flashes red at 2 Hz; reaching 10% snaps to a steady single segment in the `VeryLow` material; and the bar fills out segment by segment up to 5 segments at `Full`.

## Class hierarchy: ElectricalInputOutput (two cable networks)
<!-- verified: 0.2.6228.27061 @ 2026-05-17 -->

Decompile line 370616: `public class Battery : ElectricalInputOutput, IRocketInternals, IRocketComponent, IChargable, IReferencable, IEvaluable, IRocketMassContributor`.

Battery inherits `ElectricalInputOutput`, the same base as `Transformer` and `AreaPowerControl`. This means Battery has both `InputNetwork` and `OutputNetwork` cable network references at runtime. The `ChargeEfficiencyControl` patch in PowerGridPlus's `StationaryBatteryPatches.cs` exercises this via `cableNetwork == __instance.InputNetwork`. Implication: a station Battery acts as a power bridge with separate cable ports for the input (charging) and output (discharging) sides. Logic-passthrough patches that work for `Transformer` (bridging device lists across the InputNetwork / OutputNetwork pair) can be applied identically to `Battery`.

## PowerMaximum default and vanilla prefab variants
<!-- verified: 0.2.6228.27061 @ 2026-05-26 -->

`Battery` declares one storage-capacity field (decompile line 370629):

```csharp
[Header("Battery")]
public float PowerMaximum = 3600000f;
```

The literal `3600000f` is the C# default. Prefabs override this in Unity asset data (the `Battery` MonoBehaviour serialised on the prefab GameObject), so a single `Battery` class drives every station battery variant at runtime with its own `PowerMaximum`.

Vanilla prefab variants in Stationeers 0.2.6228.27061 (values from `rocketstation_Data/StreamingAssets/Language/en.resx`; the Stationpedia text labels these "watts" but the underlying field is energy in Joules):

| Prefab | `PowerMaximum` (J) | Approx. Wh | Stationpedia key |
|---|---|---|---|
| `StructureBattery` (Station Battery) | 3,600,000 | 1,000 (1 kWh) | `Thing_StructureBattery_Description` |
| `StructureBatteryLarge` (Station Battery Large) | 9,000,001 | ~2,500 (~2.5 kWh) | `Thing_StructureBatteryLarge_Description` |

No vanilla `StructureBatteryNuclear` exists. The only "nuclear battery" in base game is `ItemBatteryCellNuclear`, a handheld `BatteryCell` (see `HelmetBattery.md`), not a station-mounted `Battery` prefab. Third-party mods (e.g., MorePowerMod) introduce a `StationBatteryNuclear : Battery` subclass with its own `PowerMaximum` override; that is mod content, not vanilla.

The `9000001` value is the literal in the en.resx string ("Able to store up to 9000001 watts of power") — the off-by-one (vs a clean 9,000,000) is the actual prefab value, not a typo in this page.

## Power-tick method bodies (vanilla rate behaviour)
<!-- verified: 0.2.6228.27061 @ 2026-05-26 -->

`Battery` overrides all four `Device` power-tick methods. Verbatim from the decompile (lines 371098-371138):

```csharp
public override void UsePower(CableNetwork cableNetwork, float powerUsed)
{
    if (Error != 1 && OnOff && cableNetwork == OutputNetwork && IsOperable)
    {
        PowerStored = Mathf.Clamp(PowerStored - powerUsed, 0f, PowerMaximum);
    }
}

public override void ReceivePower(CableNetwork cableNetwork, float powerAdded)
{
    if (Error != 1 && OnOff && cableNetwork == InputNetwork && IsOperable)
    {
        PowerStored = Mathf.Clamp(powerAdded + PowerStored, 0f, PowerMaximum);
    }
}

public override float GetUsedPower([NotNull] CableNetwork cableNetwork)
{
    if (InputNetwork == null || Error == 1 || cableNetwork != InputNetwork || !IsOperable)
        return 0f;
    if (!OnOff)
        return 0f;
    return Mathf.Clamp(PowerMaximum - PowerStored, 0f, PowerMaximum);
}

public override float GetGeneratedPower(CableNetwork cableNetwork)
{
    if (OutputNetwork == null || Error == 1 || cableNetwork != OutputNetwork || !IsOperable)
        return 0f;
    if (!OnOff)
        return 0f;
    return Mathf.Max(PowerStored, 0f);
}
```

Each method is side-keyed (`InputNetwork` for charge, `OutputNetwork` for discharge), matching the same pattern documented for `AreaPowerControl` and `Transformer` (see `AreaPowerControl.md` "Three power-tick methods, two sides").

Vanilla rate behaviour, derived from the bodies above:

- **`GetUsedPower(InputNetwork)`** returns `Clamp(PowerMaximum - PowerStored, 0, PowerMaximum)`. The full remaining headroom in Joules. No per-tick cap.
- **`GetGeneratedPower(OutputNetwork)`** returns `Max(PowerStored, 0)`. The full stored energy in Joules. No per-tick cap.
- **`ReceivePower(InputNetwork, powerAdded)`** adds `powerAdded` directly to `PowerStored`, clamped to `[0, PowerMaximum]`. No lossy charging.
- **`UsePower(OutputNetwork, powerUsed)`** subtracts `powerUsed` directly from `PowerStored`, clamped to `[0, PowerMaximum]`.

Net consequence: a vanilla station Battery is rate-unlimited from the Battery side. The actual charge / discharge wattage observed in-game is capped only by:
- Upstream `PotentialLoad` (network supply) on charge.
- Downstream `CurrentLoad` (network demand) on discharge.
- Cable tier carrying capacity of the connected network.

A fully-charged 3,600,000 J Station Battery can in principle empty itself in a single power tick (0.5 s), supplying ~7.2 MW peak, if downstream demand and the cable network can absorb it. Conversely, an empty battery on a 7.2 MW supply will charge to full in one tick.

This unbounded-per-tick behaviour is exactly the corner that `PowerGridPlus.Patches.StationaryBatteryPatches` exists to clamp: it Postfix-patches both `GetUsedPower` and `GetGeneratedPower` to cap the returned values at `PowerMaximum * Settings.MaxBatteryChargeRate.Value` and `PowerMaximum * Settings.MaxBatteryDischargeRate.Value` respectively. Defaults are 0.002 (charge) and 0.007 (discharge) of `PowerMaximum` per tick (Settings.cs lines 106, 110).

Worked numbers under PowerGridPlus defaults (per-tick Joules; tick interval is 500 ms per [PowerTickThreading.md](../GameSystems/PowerTickThreading.md)):

| Prefab | Charge cap (J/tick = displayed W) | Discharge cap (J/tick = displayed W) |
|---|---|---|
| `StructureBattery` (3,600,000 J) | 7,200 | 25,200 |
| `StructureBatteryLarge` (9,000,001 J) | 18,000 | 63,000 |

These numbers are how the game labels them in Stationpedia / device tooltips (the field value is shown as "W"). The actual wall-clock wattage is the field value divided by the 0.5 s tick interval, i.e. exactly double these numbers (so a 7,200 J/tick charge cap is physically 14,400 W of energy flow over real time). The doubling does NOT appear in the in-game UI and does NOT change which devices a Battery can run on a given tick; reason in the game's units when comparing against a player's observed numbers. See [PowerTickThreading.md](../GameSystems/PowerTickThreading.md) "Tick interval and the watts-vs-joules-per-tick labelling convention" for the convention.

The `BatteryChargeEfficiency` patch on `ReceivePower` (lines 31-43 of `StationaryBatteryPatches.cs`) is a separate concern: it multiplies stored energy by `Settings.BatteryChargeEfficiency.Value` (default 1.0 = lossless) before clamping, with a 500 W trickle-charge bypass.

## Subclassing notes for mods
<!-- verified: 0.2.6228.27061 @ 2026-04-28 -->

A mod that subclasses `Battery` (e.g., `StationBatteryNuclear : Battery` in MorePowerMod) inherits the full state ladder, segment-bar logic, and flashing coroutine for free. Two requirements for the inherited display to actually animate:

- The mod's prefab MUST populate `displayRenderers[]` and `displayModeMaterials[]` (length 7, indices 0..6) on the `Battery` component. Without these, `SetRenderersState` returns early or assigns null materials and the bar stays dark.
- Patching `RefreshAnimState` away (e.g. swapping the entire renderer to a single material on `PoweredChanged`) replaces the segment-count behavior with whatever the override does. MorePowerMod's `StationBatteryNuclear.PoweredChanged` writes a single solid material to `gameObject.GetComponent<MeshRenderer>().materials` regardless of `Powered`, but does NOT touch the `displayRenderers[]` segment bar, so the inherited 5-segment color ladder still drives the visible charge indicator.

## Rocket-internal variant (same class, prefab-distinguished)
<!-- verified: 0.2.6228.27061 @ 2026-06-19 -->

`Battery` implements `IRocketInternals, IRocketComponent, IRocketMassContributor` (class header L370616) and carries the same two `[SerializeField]` rocket fields as `Transformer` (decompile L370633-L370637):

```csharp
[SerializeField] private RocketInternalCellType _rocketInternalCellType;
[SerializeField] private bool _strictlyInternal;
```

"Rocket Battery Medium" and the station wall battery are the SAME `Battery` class; the rocket variant is a separate prefab (`_strictlyInternal = true`, a non-`None` `_rocketInternalCellType`, its own `OpenEnds`). No `RocketBattery` subclass exists. A Harmony patch on `typeof(Battery)` catches both. `IRocketMassContributor.MassContribution` adds the stored charge to rocket mass (`Transformer` does not implement this interface).

The `Battery`-class prefabs split into TWO families by display name + build kit, despite generic prefab codenames. From `english.xml` (`StreamingAssets/Language`) and a Luna rocket save (`PrefabName` of ref 525400):

| Prefab (codename) | Display name | Build kit | Family |
|---|---|---|---|
| `StructureBattery` | Station Battery | Kit (Battery) | station |
| `StructureBatteryLarge` | Station Battery (Large) | Kit (Battery) | station |
| `StructureBatteryMedium` | Battery (Medium) | Kit (Rocket Battery) | **rocket** |
| `StructureBatterySmall` | Auxiliary Rocket Battery | Kit (Rocket Battery) | **rocket** |

So the "medium rocket battery" IS `StructureBatteryMedium` (the codename is misleadingly generic; its small sibling is literally "Auxiliary Rocket Battery"). There is no prefab literally named `*RocketBattery*` -- the rocket batteries ARE `StructureBatteryMedium` / `StructureBatterySmall`, distinct from the station wall batteries `StructureBattery` / `StructureBatteryLarge`. (Plus the two cell-chargers, and the modded `StationBatteryNuclear`.) Measured `OpenEnds` (ScenarioRunner `connector-dump`):

| Prefab | OpenEnds |
|---|---|
| `StructureBattery`, `StructureBatteryLarge` (station) | 3: `Data/None`, `Power/Input`, `Power/Output` (dedicated pure-Data third port) |
| `StructureBatteryMedium`, `StructureBatterySmall` (rocket) | 3: `Power/Input`, `Power/Output`, `PowerAndData/None` (third port is `PowerAndData`: it carries power as well as data, so it accepts a heavy power cable) |

So the station batteries carry a pure `Data` third port; the rocket batteries carry a `PowerAndData` third port. No vanilla battery folds data onto its `Power/Input` or `Power/Output` connector. Which cable coil (normal vs heavy) physically snaps onto a connector is a separate matter of the connector's prefab grid-cell / collider geometry, not its `NetworkType` (cable type lives on `Cable`, see [Cable](./Cable.md); `SmallGrid.IsConnected` gates on `NetworkType` + grid coincidence only).

## Verification history

- 2026-06-19: corrected the battery-family identification (restamped the section). The 2026-06-18 entry below wrongly said "no rocket-battery prefab exists." Per `english.xml` (`StructureBatteryMedium` = "Battery (Medium)", `StructureBatterySmall` = "Auxiliary Rocket Battery", `StructureBattery` = "Station Battery", `StructureBatteryLarge` = "Station Battery (Large)", `ItemKitRocketBattery` = "Kit (Rocket Battery)", lines 1972 / 5460 / 7423 / 7523 / 8184) and a Luna rocket save (ref 525400 = `StructureBatteryMedium` at a rocket position), `StructureBatteryMedium` + `StructureBatterySmall` are the ROCKET battery line and `StructureBattery` + `StructureBatteryLarge` are the STATION line; the prefab codenames are misleadingly generic. Connector data unchanged (rocket batteries: `PowerAndData` third port; station batteries: pure `Data` third port). Confirmed via a runtime probe on the rocket save (ref 525400 = `StructureBatteryMedium`) plus the direct `english.xml` reads.
- 2026-06-18: added "Rocket-internal variant" section (the `_rocketInternalCellType` / `_strictlyInternal` serialized fields at L370633-L370637; rocket and station batteries are one class differing by prefab). Connector layouts confirmed the same day by a live `Prefab.AllPrefabs` dump (ScenarioRunner `connector-dump`, 0.2.6228.27061): Station + Large batteries have a dedicated pure-`Data` third port, Medium + Small have a `PowerAndData` third port, none fold data onto a `Power/Input`/`Power/Output` connector. (The "no rocket-battery prefab" claim in this entry was corrected 2026-06-19, above.) Additive; no conflict with existing content.
- 2026-05-28: corrected the rate-cap table in "Power-tick method bodies" to use the in-game labelling convention (J/tick is shown as "W" in Stationpedia / tooltips) instead of doubling to wall-clock Watts. Earlier numbers (14,400 W charge / 50,400 W discharge for the Station Battery) were physically correct but inconsistent with what the player sees in-game; updated to 7,200 / 25,200 with a note that the doubling is real-time-physics only. Authoritative convention documented in [PowerTickThreading.md](../GameSystems/PowerTickThreading.md). No factual change to the underlying patches.
- 2026-05-26: added two sections: "PowerMaximum default and vanilla prefab variants" (the C# default 3,600,000f at line 370629, the StructureBattery / StructureBatteryLarge prefab values 3,600,000 / 9,000,001 J from en.resx, and the absence of any vanilla `StructureBatteryNuclear`), and "Power-tick method bodies (vanilla rate behaviour)" (verbatim bodies of `UsePower` / `ReceivePower` / `GetUsedPower` / `GetGeneratedPower` at lines 371098-371138, the conclusion that vanilla Battery offers full headroom / stored energy each tick with no per-tick rate cap, and the worked PowerGridPlus rate-cap numbers in Joules-per-tick and Watts). Additive content; does not contradict prior sections.
- 2026-05-17: added "Class hierarchy: ElectricalInputOutput (two cable networks)" section, sourced from decompile line 370616. Documents that `Battery : ElectricalInputOutput, ...` mirrors `Transformer` and `AreaPowerControl` in having both `InputNetwork` and `OutputNetwork` cable references. Implication: PowerGridPlus's logic-passthrough patches (which currently only handle Transformer and APC) can apply identically to Battery to bridge data device lists across the input / output network pair.
- 2026-04-28: page created. Verbatim findings from `ilspycmd -t Assets.Scripts.Objects.Electrical.Battery` against `E:/Steam/steamapps/common/Stationeers/rocketstation_Data/Managed/Assembly-CSharp.dll` at game version 0.2.6228.27061. Confirms the threshold ladder is identical to `BatteryCell.UpdateBatteryState` documented on `HelmetBattery.md`; the new content here is the segment-bar display logic (`SetRenderersState` segment counts, `displayModeMaterials` per-Mode lookup, and the `FlashingDisplay` 2 Hz Critical-mode coroutine). Triggered by a question about MorePowerMod's `StationBatteryNuclear` inheriting from `Battery`: when does the prefab's color change as charge drops? Answer is the threshold ladder above; vanilla material colors (red / orange / green) are set in the prefab `displayModeMaterials[]` array, not in code.

## Open questions

None at creation.
