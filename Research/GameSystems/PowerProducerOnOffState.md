---
title: Power producer HasOnOffState and On-writeability
type: GameSystems
created_in: 0.2.6228.27061
verified_in: 0.2.6228.27061
verified_at: 2026-06-14
sources:
  - .work/decomp/0.2.6228.27061/Assembly-CSharp.decompiled.cs :: Thing.CacheStates 302678-302695 (HasOnOffState @ 302683), Thing.OnOff getter/setter 299160-299191, InteractOnOff property 299331, _interactableOnOff cache 301242/302700
  - .work/decomp/0.2.6228.27061/Assembly-CSharp.decompiled.cs :: Logicable.CanLogicWrite base (LogicType.On => HasOnOffState) 280696-280708 and 350314-350320; CanLogicRead On => HasOnOffState 280654-280655 and 350251-350252
  - .work/decomp/0.2.6228.27061/Assembly-CSharp.decompiled.cs :: SolarPanel 399762 (CanLogicWrite 400161-400168), WindTurbineGenerator 138706 (GetGeneratedPower 138898-138907), LargeWindTurbineGenerator 138218, TurbineGenerator 403819 (GetGeneratedPower 403973-403980), RadioscopicThermalGenerator 395566 (GetGeneratedPower 395580-395583), PowerConnector 386798, PowerGeneratorPipe 375414 (GetGeneratedPower 375517-375528, InteractOnOff calls 375659/375670), PowerGeneratorSlot 400441 (GetGeneratedPower 400512-400523), StirlingEngine 402334
  - .work/decomp/0.2.6228.27061/Assembly-CSharp.decompiled.cs :: OnOffAnimationComponent 33895-33897 (AssignedAction => InteractableType.OnOff), SwitchOnOff DevicePart 138394+
  - Mods/PowerGridPlus/PowerGridPlus/ProducerClassifier.cs :: IsFlashableProducer 55-60, hover-only comment 16-19/52-54
related:
  - ./PowerProducerLogicReadability.md
  - ./LogicType.md
  - ../GameClasses/Interactable.md
  - ../GameClasses/Device.md
tags: [power, logic, prefab]
---

# Power producer HasOnOffState and On-writeability

Whether each power-PRODUCER class reports `Thing.OnOff == true` depends entirely on one prefab fact: does the prefab carry an `Interactable` whose `Action == InteractableType.OnOff`. `Thing.CacheStates()` sets `HasOnOffState` from exactly that test, and `Thing.OnOff` can only return true when `HasOnOffState` is true. This page records, per producer, what the decompiled code reveals (directly or by proxy) about that flag. Companion to `PowerProducerLogicReadability.md`, which covers the orthogonal logic-READ surface.

The `Interactables` list is populated from the SERIALIZED prefab (an `OnOffAnimationComponent` / `SwitchOnOff` DevicePart on the prefab contributes the `OnOff` interactable at registration; the C# class body does not add it). So the decompile cannot DIRECTLY confirm presence/absence of the interactable for any class. The reliable indirect proxies are: (1) does code reference `base.InteractOnOff` (the cached `Action == OnOff` interactable) on `this`; (2) does `GetGeneratedPower` gate on `OnOff`; (3) does the class force `LogicType.On` writeable independent of `HasOnOffState`.

## Mechanism: the flag, the getter, the On-writeability gate
<!-- verified: 0.2.6228.27061 @ 2026-06-14 -->

`Thing.CacheStates()` (302678), line 302683:

```csharp
HasOnOffState = Interactables.Exists((Interactable i) => i.Action == InteractableType.OnOff);
```

`Thing.OnOff` getter (299160) returns true only via `HasOnOffState`:

```csharp
public virtual bool OnOff
{
    get
    {
        if (HasOnOffState && !HasBaseAnimator)
        {
            return InteractOnOff.State == 1;
        }
        if (ThreadedManager.IsThread || _frameOnOffUpdated == Time.frameCount)
        {
            return _onOff;
        }
        _onOff = (bool)BaseAnimator && HasOnOffState && BaseAnimator.GetInteger(Interactable.OnOffState) == 1;
        _frameOnOffUpdated = Time.frameCount;
        return _onOff;
    }
```

Both branches require `HasOnOffState`. With no `OnOff` interactable, `_onOff` is `false && ... == false` permanently, and the early branch is skipped. `InteractOnOff` (299331) is `=> _interactableOnOff`, which `CacheStates`/`OnPrefabLoad` populates ONLY from `Interactables.Find(i => i.Action == InteractableType.OnOff)` (301242, 302700); it is null when no such interactable exists.

`Logicable.CanLogicWrite` base (two emitted copies) ties `LogicType.On` writeability to the same flag. Lines 280705 and 350318:

```csharp
public virtual bool CanLogicWrite(LogicType logicType)
{
    return logicType switch
    {
        LogicType.Color => HasColorState,
        LogicType.Activate => HasActivateState,
        LogicType.Open => HasOpenState,
        LogicType.Mode => HasModeState,
        LogicType.Lock => HasLockState,
        LogicType.On => HasOnOffState,
        _ => false,
    };
}
```

(The older read-gate switches at 280654-280655 and 350251-350252 do the same: `case LogicType.On: return HasOnOffState;`.) Consequence: a producer that does NOT override `CanLogicWrite` to force `LogicType.On` true has `On`-writeability == `HasOnOffState`. None of the producers below force `On`; their `CanLogicWrite` overrides (where present) special-case only orientation/power-tracking types and fall through to base for `On`.

`InteractableType.OnOff` is a distinct enum value, separate from `Activate`, `Mode`, and `Button1-5` (see `Interactable.md`). A prefab whose only control is `Activate` (ignite) or `Mode` therefore has `HasOnOffState == false` even though it is "interactable". The component that contributes the `OnOff` axis is `OnOffAnimationComponent` (33895), `public override InteractableType AssignedAction => InteractableType.OnOff;`.

## Per-class verdict
<!-- verified: 0.2.6228.27061 @ 2026-06-14 -->

| Producer class | Decl | Strongest code proxy | HasOnOffState verdict | Confidence |
|---|---|---|---|---|
| SolarPanel | 399762 | `GetGeneratedPower` returns `GenerationRate` with no `OnOff` gate; `CanLogicWrite` (400161) forces only `logicType-20 <= Power`, falls through to base for `On`; no `OnOff`/`InteractOnOff` reference in body | FALSE | medium-high |
| WindTurbineGenerator | 138706 | `GetGeneratedPower` (138898) returns `CalculateGenerationRate()`, no `OnOff` gate; no `OnOff`/`InteractOnOff` reference; no `CanLogicWrite` override | FALSE | medium |
| LargeWindTurbineGenerator | 138218 | trivial subclass of WindTurbineGenerator; inherits the un-gated `GetGeneratedPower`; no own `OnOff` reference | FALSE | medium |
| TurbineGenerator (small wall) | 403819 | `GetGeneratedPower` (403973) returns `_generatedPower` (pressure-differential only), no `OnOff` gate; no `OnOff`/`InteractOnOff` reference; only `CanLogicRead` override | FALSE | medium |
| RadioscopicThermalGenerator (RTG) | 395566 | `GetGeneratedPower` (395580) returns `PowerGenerated` unconditionally; no `OnOff`/`InteractOnOff`/`CanLogicWrite` in body | FALSE | medium-high |
| PowerConnector | 386798 | pure `DynamicGenerator` dock; `GetGeneratedPower` forwards to the docked generator; no `OnOff`/`InteractOnOff`/`CanLogicWrite`; uses `IsOpen` (Open axis), not OnOff | FALSE | medium-high |
| PowerGeneratorPipe / GasFuelGenerator | 375414 | `GetGeneratedPower` (375523) `if (!OnOff) return 0f;` AND body calls `OnServer.Interact(base.InteractOnOff, 0)` (375659, 375670) | TRUE | high |
| PowerGeneratorSlot / SolidFuelGenerator | 400441 | `GetGeneratedPower` (400518) `if (PoweredTicks <= 0 || !OnOff) return 0f;` | TRUE | medium-high |
| StirlingEngine | 402334 | power is zeroed when `!OnOff` in `OnAtmosphericTick`; `GetGeneratedPower` returns `EnergyAsPower` (set to zero under `!OnOff`) | TRUE | medium |

Verbatim, the un-gated producers (thesis "no OnOff button" holds):

`RadioscopicThermalGenerator.GetGeneratedPower` (395580-395583), the cleanest case:

```csharp
public override float GetGeneratedPower(CableNetwork cableNetwork)
{
    return PowerGenerated;
}
```

`TurbineGenerator.GetGeneratedPower` (403973-403980):

```csharp
public override float GetGeneratedPower(CableNetwork cableNetwork)
{
    if (cableNetwork != base.PowerCableNetwork)
    {
        return 0f;
    }
    return _generatedPower;
}
```

`SolarPanel.CanLogicWrite` (400161-400168) does NOT force `LogicType.On`; only the orientation/power-tracking band (`logicType - 20 <= LogicType.Power`) is writeable, everything else (incl. `On`) defers to the base flag-gated switch:

```csharp
public override bool CanLogicWrite(LogicType logicType)
{
    if (logicType - 20 <= LogicType.Power)
    {
        return true;
    }
    return base.CanLogicWrite(logicType);
}
```

Verbatim, the gated fuel producers (thesis "no OnOff button" FAILS):

`PowerGeneratorPipe.GetGeneratedPower` (375517-375528):

```csharp
public override float GetGeneratedPower(CableNetwork cableNetwork)
{
    if (cableNetwork != base.PowerCableNetwork)
    {
        return 0f;
    }
    if (!OnOff)
    {
        return 0f;
    }
    return _energyAsPower;
}
```

and `PowerGeneratorPipe` actively drives that interactable off (375659):

```csharp
OnServer.Interact(base.InteractOnOff, 0);
```

`PowerGeneratorSlot.GetGeneratedPower` (400512-400523):

```csharp
public override float GetGeneratedPower(CableNetwork cableNetwork)
{
    if (cableNetwork != base.PowerCableNetwork)
    {
        return 0f;
    }
    if (PoweredTicks <= 0 || !OnOff)
    {
        return 0f;
    }
    return PowerGenerated;
}
```

## Why the fuel-generator verdict is TRUE (gas proven, solid/stirling strongly inferred)
<!-- verified: 0.2.6228.27061 @ 2026-06-14 -->

The three fuel producers are not the same KIND of TRUE, and the distinction matters because the decompile can prove an `OnOff` interactable PRESENT (via a `base.InteractOnOff` dereference that would crash if it were absent) but can never prove one ABSENT.

- `PowerGeneratorPipe` / GasFuelGenerator is **decompile-PROVEN**. `base.InteractOnOff` resolves to `_interactableOnOff`, which is null unless `Interactables` contains an `Action == OnOff` entry. The class calls `OnServer.Interact(base.InteractOnOff, 0)` to force itself off when the atmosphere is invalid or it overheats (375659/375670), on a normal runtime path inside `OnAtmosphericTick`. A null `_interactableOnOff` would NullReferenceException there every time; the shipped game does not crash, so the gas-generator prefab carries the `OnOff` interactable. Its `if (!OnOff) return 0f;` power gate (375523) is consistent with that.
- `PowerGeneratorSlot` / SolidFuelGenerator and `StirlingEngine` are **strongly INFERRED, not proven**. Their bodies never dereference `base.InteractOnOff` (their many `OnServer.Interact(...)` calls hit Mode / Error / Import / Powered, never OnOff), so the null-deref argument does not apply to them. Their TRUE rests on the power gate `if (... || !OnOff) return 0f;` (solid, 400518) and the `!OnOff` power-zeroing in `OnAtmosphericTick` (stirling, ~402718): a power gate on a flag that could never be true would make the device permanently dead, which contradicts both being functional fuel generators. That is a sound inference about designer intent, but it is not a decompile proof; an InspectorPlus read of `HasOnOffState` would settle them (see Open questions).

All three are the set the mod labels `IsFlashableProducer` (`ProducerClassifier.cs` 55-60), and the label is consistent with the code proxies: these are the producers with an `OnOff` interactable. The remaining six are the hover-only set.

## Mod classification cross-check (ProducerClassifier.cs)
<!-- verified: 0.2.6228.27061 @ 2026-06-14 -->

`Mods/PowerGridPlus/PowerGridPlus/ProducerClassifier.cs` splits producers exactly along the `HasOnOffState` line, though it phrases it as "flash button":

- `IsFlashableProducer` (55-60) = `PowerGeneratorPipe || PowerGeneratorSlot || StirlingEngine`. Comment (52): "A producer with an InteractableType.OnOff button (can host a red flash)." This matches the TRUE rows above.
- Comment (53-54, 16-19): SolarPanel, WindTurbineGenerator, LargeWindTurbineGenerator, RTG (and 35-36: TurbineGenerator, PowerConnector) are "hover-only". This matches the FALSE rows.

The mod's "flashable => has OnOff" inference is therefore supported by the code proxies, not contradicted by them. The one nuance the mod glosses: its FALSE-side claim rests on code evidence (no gate, no `InteractOnOff`), which is strong but not a direct read of the serialized prefab; see Open questions.

## Open questions
<!-- verified: 0.2.6228.27061 @ 2026-06-14 -->

- The FALSE verdicts (Solar, both winds, small turbine, RTG, PowerConnector) are CODE-INFERRED (absence of an `OnOff` gate / `InteractOnOff` reference, plus `CanLogicWrite` not forcing `On`). They are not a direct read of the serialized prefab's `Interactables` list. No human-readable per-prefab interactable export ships with the game (checked `rocketstation_Data/StreamingAssets/Data/**`: only `WorldObjectives.xml` carries any `Interactable` Action data and it does not cover the power producers; the authoritative list is in binary Unity asset bundles). Direct confirmation needs an in-game InspectorPlus dump of each producer's `HasOnOffState` / `Interactables[i].Action`.
- Two of the three TRUE rows are inferred, not proven. Only `PowerGeneratorPipe` / GasFuelGenerator is decompile-proven (the `base.InteractOnOff` null-deref). `PowerGeneratorSlot` / SolidFuelGenerator and `StirlingEngine` never dereference `base.InteractOnOff` in their own bodies; their TRUE rests on the `!OnOff` power gate / power-zeroing plus the mod's `IsFlashableProducer` membership, which is a sound designer-intent inference but not a direct read of the prefab. `StirlingEngine` is the weakest (its `GetGeneratedPower` returns `EnergyAsPower` and the zeroing happens one level removed, in `OnAtmosphericTick`). A single InspectorPlus read of `HasOnOffState` on a placed SolidFuelGenerator and StirlingEngine would upgrade both from inferred to confirmed.

## Verification history

- 2026-06-15: adversarial re-verification. Two independent advocates were tasked with opposite theses ("producers have OnOff" vs "producers are buttonless"); both CONVERGED on the table above (the pro-OnOff advocate could only win the three fuel generators; the buttonless advocate conceded exactly those three). An independent judge then clean-room re-read the citations and confirmed the verdicts, adding the key epistemic point now reflected here: the decompile can prove an `OnOff` interactable PRESENT (a `base.InteractOnOff` deref crashes if absent) but never ABSENT, so only GasFuelGenerator is decompile-proven TRUE while SolidFuelGenerator and StirlingEngine are strong inferences. The main agent independently re-read the decisive lines (`Device.CanLogicWrite` On=>HasOnOffState 350318, `SetLogicValue` On->Interact(InteractOnOff) 350354, `_interactableOnOff = Interactables.Find(Action==OnOff)` 302700, PowerGeneratorPipe `OnServer.Interact(base.InteractOnOff,0)` 375659/375670) and tightened the "Why TRUE" section and Open questions to separate proven from inferred. Driving context: a PowerGridPlus claim had wrongly lumped the fuel generators in with the buttonless producers; this pass corrected that.
- 2026-06-14: page created from `.work/decomp/0.2.6228.27061/Assembly-CSharp.decompiled.cs`. Verbatim: `CacheStates` HasOnOffState (302683), `Thing.OnOff` getter (299160-299175), `Logicable.CanLogicWrite` base (280696-280708), `InteractOnOff` (299331) and `_interactableOnOff` population (302700). Verbatim per-producer `GetGeneratedPower`: RTG (395580), TurbineGenerator (403973), PowerGeneratorPipe (375517 incl. `if(!OnOff)`), PowerGeneratorSlot (400512); SolarPanel `CanLogicWrite` (400161). Confirmed PowerGeneratorPipe calls `OnServer.Interact(base.InteractOnOff, 0)` (375659/375670). Confirmed no `OnOff`/`InteractOnOff` reference in SolarPanel/Wind/LargeWind/TurbineGenerator/RTG/PowerConnector bodies. Cross-checked against `ProducerClassifier.cs` (IsFlashableProducer 55-60). Checked StreamingAssets for a per-prefab interactable export: none readable. Established while adjudicating the "do power producers have an OnOff interactable" dispute.
