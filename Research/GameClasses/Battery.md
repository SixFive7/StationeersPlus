---
title: Battery (station-mounted)
type: GameClasses
created_in: 0.2.6228.27061
verified_in: 0.2.6228.27061
verified_at: 2026-04-28
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

## Subclassing notes for mods
<!-- verified: 0.2.6228.27061 @ 2026-04-28 -->

A mod that subclasses `Battery` (e.g., `StationBatteryNuclear : Battery` in MorePowerMod) inherits the full state ladder, segment-bar logic, and flashing coroutine for free. Two requirements for the inherited display to actually animate:

- The mod's prefab MUST populate `displayRenderers[]` and `displayModeMaterials[]` (length 7, indices 0..6) on the `Battery` component. Without these, `SetRenderersState` returns early or assigns null materials and the bar stays dark.
- Patching `RefreshAnimState` away (e.g. swapping the entire renderer to a single material on `PoweredChanged`) replaces the segment-count behavior with whatever the override does. MorePowerMod's `StationBatteryNuclear.PoweredChanged` writes a single solid material to `gameObject.GetComponent<MeshRenderer>().materials` regardless of `Powered`, but does NOT touch the `displayRenderers[]` segment bar, so the inherited 5-segment color ladder still drives the visible charge indicator.

## Verification history

- 2026-04-28: page created. Verbatim findings from `ilspycmd -t Assets.Scripts.Objects.Electrical.Battery` against `E:/Steam/steamapps/common/Stationeers/rocketstation_Data/Managed/Assembly-CSharp.dll` at game version 0.2.6228.27061. Confirms the threshold ladder is identical to `BatteryCell.UpdateBatteryState` documented on `HelmetBattery.md`; the new content here is the segment-bar display logic (`SetRenderersState` segment counts, `displayModeMaterials` per-Mode lookup, and the `FlashingDisplay` 2 Hz Critical-mode coroutine). Triggered by a question about MorePowerMod's `StationBatteryNuclear` inheriting from `Battery`: when does the prefab's color change as charge drops? Answer is the threshold ladder above; vanilla material colors (red / orange / green) are set in the prefab `displayModeMaterials[]` array, not in code.

## Open questions

None at creation.
