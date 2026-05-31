---
title: Door / Airlock CanAirPass (atmosphere gating) and the ForceFieldDoor mod override
type: GameClasses
created_in: 0.2.6228.27061
verified_in: 0.2.6228.27061
verified_at: 2026-05-31
sources:
  - rocketstation_Data/Managed/Assembly-CSharp.dll :: Assets.Scripts.Objects.Door (CanAirPass, IsOpen)
  - rocketstation_Data/Managed/Assembly-CSharp.dll :: Assets.Scripts.Objects.Structure (CanAirPass, NeverAirPass)
  - rocketstation_Data/Managed/Assembly-CSharp.dll :: Assets.Scripts.Objects.LogicBase (LogicType.Open -> Device.IsOpen comparison)
  - third-party mod ForceFieldDoorMod.dll :: ForceFieldDoorMod.ForceFieldDoor (CanAirPass, OnAtmosphericTick, PoweredChanged, OnRegistered) [.work/decomp/0.2.6228.27061/ForceFieldDoorMod.decompiled.cs]
related:
  - ./PoweredVent.md
  - ../GameSystems/LogicType.md
tags: [logic, atmospherics]
---

# Door / Airlock atmosphere gating, and the ForceFieldDoor mod

Whether a door-type Structure lets the room atmosphere on one side mix with the other is decided by the `CanAirPass` property, evaluated by the atmospherics/room system. This page records the exact gating for the vanilla `Door` / `Airlock` family and for the third-party `ForceFieldDoor` mod, because an IC10 airlock controller that drives a door with the wrong LogicType (e.g. `On` instead of `Open`) will not actually open or seal the barrier it thinks it is controlling.

## Vanilla Structure.CanAirPass and NeverAirPass
<!-- verified: 0.2.6228.27061 @ 2026-05-31 -->

`Structure` (the base of `Door`) ties air passage to the current build state:

```csharp
public virtual bool CanAirPass
{
    get
    {
        if (CurrentBuildStateIndex >= 0)
            return !CurrentBuildState.BlockAir;
        return true;
    }
}

protected bool NeverAirPass
{
    get
    {
        if (CurrentBuildStateIndex >= 0)
            return CurrentBuildState.AlwaysBlockAir;
        return false;
    }
}
```

A fully built solid wall has a build state with `BlockAir == true`, so `CanAirPass == false` (air is held). `AlwaysBlockAir` is the stronger flag a Door uses for the "even when open this never passes air" case (not used by normal doors).

## Door.CanAirPass: gated on IsOpen
<!-- verified: 0.2.6228.27061 @ 2026-05-31 -->

`Door` overrides `CanAirPass` so that an OPEN door passes air and a CLOSED door defers to the base (build-state) value:

```csharp
public override bool CanAirPass
{
    get
    {
        if (!IsOpen || base.NeverAirPass)
            return base.CanAirPass;   // closed door (or AlwaysBlockAir): seals per build state
        return true;                  // open door: air passes
    }
}
```

So for a Blast Door (and any normal Door/Airlock): **air is held only when the door is CLOSED** (`IsOpen == false`). The thing that opens/closes the door over logic is `LogicType.Open`, not `LogicType.On`. `On` (`OnOff`) is the power switch and does not move the door leaf.

`IsOpen` itself (lines 299228-299259) reflects the door's animator open-state integer (`Interactable.OpenState == 1`); writing `LogicType.Open` flips that state. Confirmed by the logic comparison path: `case LogicType.Open: IsTrue = RelativeTruth(Device.IsOpen == ((int)Value == 1));` (vs `case LogicType.Power: ... Device.OnOff ...`). `Open` and `On`/`Power` are distinct logic values backed by distinct fields.

A Blast Door is documented community-side as a powered Portal with no pressure-differential limit (it can hold any pressure delta when closed). The atmosphere gate is still `IsOpen`: a closed blast door seals, an open one passes air, regardless of `OnOff`.

## ForceFieldDoor (third-party mod) CanAirPass
<!-- verified: 0.2.6228.27061 @ 2026-05-31 -->

`ForceFieldDoor` is from the third-party `ForceFieldDoorMod` (prefab hash `696254815`, code name not vanilla). It derives from `Airlock` and overrides `CanAirPass`:

```csharp
public override bool CanAirPass
{
    get
    {
        if (_open || !((Thing)this).Powered)
            return true;   // air passes when OPEN or when UNPOWERED
        return false;      // air held only when CLOSED *and* POWERED
    }
}
```

`_open` tracks the door's open state, set from `IsOpen`:

```csharp
public void PoweredChanged()
{
    if (!((Thing)this).Powered)
        _open = true;
    _open = ((Thing)this).IsOpen;   // note: the unconditional reassignment makes the Powered branch above dead
}
```

Consequences:

- A force field door **does** hold atmosphere, but only while it is BOTH closed (`_open == false`, i.e. `IsOpen == false`) AND powered. Lose power and it passes air (`CanAirPass` returns true). This is unlike a blast door, which seals on `IsOpen == false` regardless of power.
- The thing that "raises/lowers the field" for atmosphere purposes is therefore still the **Open** state (driving `IsOpen`/`_open`), plus power. An IC10 script that toggles only `LogicType.On` (OnOff) is toggling power, which is one of the two conditions, but it is not the same as opening/closing the leaf. Driving the field via `On` is fragile: `On 0` cuts power, which forces `CanAirPass = true` (field down, air passes) and also stops the `OnAtmosphericTick` power model; `On 1` restores power but leaves the actual barrier state dependent on `IsOpen`. Whether `On` alone produces a clean "field up / field down" depends on how the mod couples OnOff to IsOpen, which is NOT established here (see Open questions).
- It is indestructible: `InitializeDamageState` sets `ThingHealth = float.MaxValue` and an `IndestructableDamageState`.

## ForceFieldDoor power model (OnAtmosphericTick)
<!-- verified: 0.2.6228.27061 @ 2026-05-31 -->

Power draw scales with the pressure differential the field is holding:

```csharp
public override void OnAtmosphericTick()
{
    ((Device)this).OnAtmosphericTick();
    if (_open)
    {
        ((Device)this).UsedPower = 10f;   // open: 10 W idle
        return;
    }
    float x = 0f;
    if (GridController.CanContainAtmos(_facingGrid) && GridController.CanContainAtmos(_rearGrid))
    {
        Atmosphere val = AtmosphericsController.SampleGlobalAtmosphere(_facingGrid);
        Atmosphere obj = AtmosphericsController.SampleGlobalAtmosphere(_rearGrid);
        float num  = val.PressureGassesAndLiquidsInPa / 1000f;
        float num2 = obj.PressureGassesAndLiquidsInPa / 1000f;
        x = MathF.Abs(num - num2);        // |facing kPa - rear kPa|
    }
    x = MathF.Floor(x);
    if (FLUCTUATES && x >= 5f) { /* adds up to +5 random jitter */ }
    float x2 = POWERUSAGE_BASE + x * POWERUSAGE_RATE;   // 100 + delta*10
    x2 = MathF.Max(x2, POWERUSAGE_BASE);                // floor 100 W
    x2 = MathF.Min(x2, POWERUSAGE_MAX);                 // cap 100000 W
    UsedPower = x2;
}
```

Field constants: `POWERUSAGE_BASE = 100`, `POWERUSAGE_MAX = 100000`, `POWERUSAGE_RATE = 10`, `FLUCTUATES = true`. `_facingGrid`/`_rearGrid` are the two world grids 0.1 units in front of / behind the door, set in `OnRegistered`. So a closed, powered force field separating vacuum from 1 atm (~101 kPa delta) draws on the order of `100 + 101*10 ≈ 1110 W` plus jitter; the harder the pressure delta, the more power, capped at 100 kW.

This confirms the design intent: the force field is meant to hold a pressure differential with blast doors open, at a power cost proportional to the delta. It is a real atmosphere barrier (while closed and powered), not a cosmetic effect.

## How `On` and `Open` control the force field (resolved)
<!-- verified: 0.2.6228.27061 @ 2026-05-31 -->

`CanAirPass = _open || !Powered` (verbatim above). There is **no `OnOff`/`On` term**. The field blocks air only when the leaf is CLOSED (`_open == false`) AND the door is electrically `Powered`.

Two facts decide how to control it over logic:

- `_open` tracks `IsOpen`, and `IsOpen` is driven by `LogicType.Open`. `ForceFieldDoor.OnInteractableUpdated` calls `PoweredChanged()`, which does `_open = IsOpen` (the leading `if (!Powered) _open = true;` is dead, immediately overwritten). So every interaction re-syncs `_open` to the current leaf state.
- `Powered` (`Powered => PoweredValue >= 1`, decompile line 299193) is the ELECTRICAL-network powered state. `PoweredValue` (299195-299226) reads the `Powered` interactable / animator `PoweredState`, which the power system sets from the cable network (e.g. `OnServer.Interact(InteractableType.Powered, network.PoweredValue)`, line 188443). It is **independent of `LogicType.On`/OnOff** — confirmed by the pervasive `OnOff && Powered` checks across the codebase (e.g. `PoweredVent.OnAtmosphericTick`: `if (!OnOff || !Powered)`), which are only non-redundant because `(OnOff == false, Powered == true)` is a real, reachable state.

Therefore **`LogicType.On`/OnOff does NOT change `CanAirPass`**. Writing `On 0` / `On 1` to a force field door does not raise or lower the field. The seal is controlled only by:

- `LogicType.Open`: `Open 1` -> `IsOpen`/`_open` true -> field DOWN (air passes); `Open 0` -> leaf closed -> field UP **if the door is powered**.
- cable power: unpowered -> `!Powered` -> field DOWN regardless of the leaf (the intended fail-open on power loss).

Trap for an `On`-based controller: `_open` initializes to `true` (field down). The FIRST `On` write of either value triggers `PoweredChanged -> _open = IsOpen`, flipping `_open` from its initial `true` to the closed-leaf `false`, which on a powered door SEALS the field. So an `On 0` written meaning "field off" can actually seal a powered, closed force-field door (the opposite of the intent), and subsequent `On` writes then do nothing to the seal. The correct logic control is `Open` (plus ensuring the door has cable power whenever the field must hold).

## Verification history
<!-- append-only -->

- 2026-05-31: Page created while adversarially reviewing an IC10 airlock+forcefield script that drives the force field door with `LogicType.On`. Read `Structure.CanAirPass`/`NeverAirPass`, `Door.CanAirPass`/`IsOpen`, and the `LogicType.Open` vs `LogicType.Power` comparison path from Assembly-CSharp v0.2.6228.27061. Read `ForceFieldDoorMod.ForceFieldDoor` (CanAirPass / OnAtmosphericTick / PoweredChanged / OnRegistered) from the third-party mod decompile at .work/decomp/0.2.6228.27061/ForceFieldDoorMod.decompiled.cs. Additive page; no prior Door/Airlock page existed, nothing contradicted.
- 2026-05-31: Resolved both Open Questions after a full read of `ForceFieldDoorMod.decompiled.cs`. `OnInteractableUpdated` overrides to call base `Door.OnInteractableUpdated` then `PoweredChanged()`, and `PoweredChanged` sets `_open = IsOpen` unconditionally (the `if (!Powered) _open = true;` first line is dead). So an `On`/OnOff logic write re-syncs `_open` to `IsOpen`: with the leaf closed (`IsOpen == false`), `CanAirPass` reduces to `!Powered`, so `On` alone gates atmosphere (works, but fragile: `_open` defaults `true` until the first interaction, and an opened leaf cannot be re-sealed with `On` alone). The `!Powered` term in `CanAirPass` independently carries the unpowered case. Updated "IC10 implication" section accordingly; no contradiction of prior content (the page previously flagged these as untraced).
- 2026-05-31: CORRECTION of the entry immediately above. That entry wrongly concluded `On` controls the seal while the leaf stays closed (`On 1` seals, `On 0` drops via `!Powered`); it conflated `On 0` with `!Powered`. Verified via `Device.PoweredValue` (decompile 299193-299226): `Powered` reads the `Powered` interactable/animator state set by the cable network (`OnServer.Interact(InteractableType.Powered, network.PoweredValue)`, line 188443) and is INDEPENDENT of `LogicType.On`/OnOff (the pervasive, non-redundant `OnOff && Powered` checks confirm `(OnOff == false, Powered == true)` is reachable). `On`/OnOff is absent from `CanAirPass = _open || !Powered`, so `On` writes do NOT raise/lower the field; only `Open` (via `IsOpen`/`_open`) and cable power do. Section rewritten to "How `On` and `Open` control the force field". The earlier entry is retained per the append-only VH convention but is superseded by this one.

## Open questions
<!-- verified: 0.2.6228.27061 @ 2026-05-31 -->

- None. Both prior questions (the `On`-vs-`Open`/`IsOpen` coupling, and whether the `!Powered` term carries the unpowered case given the dead `if (!Powered) _open = true;` line) were resolved 2026-05-31; see "How `On` and `Open` control the force field". Net: `On`/OnOff does not affect the seal at all (it is absent from `CanAirPass`, and `Powered` is OnOff-independent); the field is controlled by `Open` (leaf) and cable power.
