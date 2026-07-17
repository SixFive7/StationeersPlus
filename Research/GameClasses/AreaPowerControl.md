---
title: AreaPowerControl
type: GameClasses
created_in: 0.2.6228.27061
verified_in: 0.2.6403.27689
verified_at: 2026-07-18
sources:
  - rocketstation_Data/Managed/Assembly-CSharp.dll :: Assets.Scripts.Objects.Electrical.AreaPowerControl
  - .work/decomp/0.2.6403.27689/Assembly-CSharp.decompiled.cs :: lines 390555-391057 (AreaPowerControl declaration through GetGeneratedPower), 424757-424805 (Transformer power methods)
  - .work/decomp/0.2.6403.27689/Assembly-CSharp.decompiled.cs :: lines 390753-390773 (CanLogicRead/GetLogicValue overrides), 371028-371164 (Device logic virtuals), 394813 (ElectricalInputOutput declaration)
  - .work/decomp/0.2.6403.27689/Assembly-CSharp.decompiled.cs :: lines 390547-390554 (PowerMode), 390616-390630 (IsOperable), 370422-370432 / 394861-394871 (Device / ElectricalInputOutput IsOperable), 390843-390879 (crowbar AttackWith), 390919-390930 (CheckConnections), 391059-391091 (SetContentsVisibility)
  - Mods/PowerGridPlus/PowerGridPlus/Patches/AreaPowerControlPatches.cs
  - .work/revolt-source/Assets/Scripts/Patches/AreaPowerControllerPatches.cs
related:
  - ./Battery.md
  - ./CableNetwork.md
  - ./Transformer.md
  - ./Device.md
  - ./Structure.md
  - ../Patterns/HarmonyLogicableInheritedMethodTrap.md
tags: [power, prefab, logic]
---

# AreaPowerControl

Vanilla `Assets.Scripts.Objects.Electrical.AreaPowerControl` (the APC). Bridges two cable networks (`InputNetwork` from `ElectricalInputOutput`, `OutputNetwork` from the same base) and holds an optional `BatteryCell` in slot 0. Reports a load on the input side, supplies power to the output side, and uses its own battery as a buffer.

The class extends `ElectricalInputOutput` (not `Logicable` directly), so its two cable ports sit in two different `CableNetwork.DeviceList`s -- the APC itself appears in BOTH networks' device lists.

## Three power-tick methods, two sides
<!-- verified: 0.2.6403.27689 @ 2026-07-02 -->

`AreaPowerControl` overrides three of the four `Device` power-tick methods, and each one is side-keyed: it returns / acts only when called for either the input cable network or the output cable network. There is no behaviour on a third unrelated network.

| Method | Side | Purpose |
|---|---|---|
| `GetUsedPower(CableNetwork)` | `InputNetwork` only | Reports the APC's load on the upstream side (quiescent draw + downstream throughput + battery charge demand). |
| `ReceivePower(CableNetwork, float powerAdded)` | `InputNetwork` only | Settles upstream supply against `_powerProvided`; if surplus and battery not full, charges the battery up to `BatteryChargeRate`. |
| `GetGeneratedPower(CableNetwork)` | `OutputNetwork` only | Reports the APC's potential supply downstream (`AvailablePower` = upstream `PotentialLoad` + `Battery.PowerStored`). |
| `UsePower(CableNetwork, float powerUsed)` | `OutputNetwork` only | Drains the battery to cover the downstream consumption; bumps `_powerProvided` by `powerUsed` for the next tick. |

The four methods inherited from `Device` are `GetUsedPower`, `GetGeneratedPower`, `ReceivePower`, `UsePower`. APC overrides all four.

Verbatim from the 0.2.6403.27689 decompile (class declaration 390555; `UsePower` 391000-391012, `ReceivePower` 391014-391026, `GetUsedPower` 391028-391044, `GetGeneratedPower` 391046-391057; the slot `Battery` property is a `BatteryCell`, `BatterySlot.Get<BatteryCell>()` at 390594):

```csharp
public override void UsePower(CableNetwork cableNetwork, float powerUsed)
{
    if (cableNetwork == OutputNetwork)
    {
        if (_powerProvided > 0f && (bool)Battery && !Battery.IsEmpty)
        {
            float num = Mathf.Min(Battery.PowerStored, _powerProvided);
            Battery.PowerStored -= num;
            _powerProvided -= num;
        }
        _powerProvided += powerUsed;
    }
}

public override void ReceivePower(CableNetwork cableNetwork, float powerAdded)
{
    if ((InputNetwork == null || cableNetwork == InputNetwork) && InputNetwork != null)
    {
        _powerProvided -= powerAdded;
        if (_powerProvided < 0f && (bool)Battery && !Battery.IsCharged)
        {
            float num = Mathf.Min(Battery.PowerDelta, BatteryChargeRate, powerAdded);
            Battery.PowerStored += num;
            _powerProvided += num;
        }
    }
}

public override float GetUsedPower([NotNull] CableNetwork cableNetwork)
{
    if (InputNetwork == null || cableNetwork != InputNetwork)
    {
        return 0f;
    }
    float num = 0f;
    if (OnOff && OutputNetwork != null)
    {
        num = Mathf.Max(_powerProvided, UsedPower);
    }
    if ((bool)Battery && !Battery.IsCharged)
    {
        num += Mathf.Min(BatteryChargeRate, Battery.PowerDelta);
    }
    return num;
}

public override float GetGeneratedPower(CableNetwork cableNetwork)
{
    if (OutputNetwork == null || Error == 1 || cableNetwork != OutputNetwork)
    {
        return 0f;
    }
    if (!OnOff)
    {
        return 0f;
    }
    return AvailablePower;
}
```

Key invariants in these bodies:

- `_powerProvided` is the running ledger between input-side received power and output-side consumed power. It is incremented on `UsePower` (output settled this tick) and decremented on `ReceivePower` (input supplied this tick). Across a balanced tick the ledger trends to zero.
- The battery participates on BOTH sides: it absorbs upstream surplus inside `ReceivePower` (when `_powerProvided < 0`, i.e., upstream over-supplied), and discharges into downstream demand inside `UsePower` (when `_powerProvided > 0`, i.e., last tick the output side asked for more than the input side ultimately delivered).
- The "drain only when `_powerProvided > 0`" gate inside `UsePower` is the vanilla deferred-settlement model: the battery covers the previous tick's shortfall before this tick's `powerUsed` is added to the next ledger.

### Gate shape: the cell-charge draw is not gated on OnOff, Error, or OutputNetwork
<!-- verified: 0.2.6403.27689 @ 2026-07-07 -->

Re-read from the verbatim bodies above (`GetUsedPower` 391028-391044, `GetGeneratedPower` 391046-391057): the two `GetUsedPower` terms carry different gates, and neither term carries an `Error` gate.

| Term | Gate |
|---|---|
| Quiescent + ledger: `Max(_powerProvided, UsedPower)` | `OnOff && OutputNetwork != null` |
| Cell charge: `Min(BatteryChargeRate, Battery.PowerDelta)` | `(bool)Battery && !Battery.IsCharged` only |
| `GetGeneratedPower` (downstream supply advertisement) | `OutputNetwork != null && Error != 1 && OnOff` |

Consequence: an APC that is switched OFF, errored (`Error == 1`), or has no output cable still adds its cell-charge demand to the INPUT network every tick while a non-charged cell sits in the slot, while `GetGeneratedPower` (which IS Error- and OnOff-gated) advertises nothing downstream. A vanilla APC therefore charge-draws in states a player reads as "off" or "faulted"; during the PowerGridPlus partial-power forensics this input-side draw from apparently idle APCs is expected vanilla behavior, not a mod-introduced load. The same asymmetry exists on `WallLightBattery.GetUsedPower` (`(OnOff ? UsedPower : 0f)` plus a charge term gated only on cell-present-and-not-charged, decompile 327979-327992; see [LightSources](../GameSystems/LightSources.md), "F. WallLightBattery: battery backup").

## PowerMode enum: the values behind the Mode state
<!-- verified: 0.2.6403.27689 @ 2026-07-18 -->

`PowerMode` is a top-level enum declared immediately before the class (decompile 390547-390554), verbatim:

```csharp
public enum PowerMode
{
    Idle = 0,
    Charged = 4,
    Charging = 3,
    Discharging = 2,
    Discharged = 1
}
```

In numeric order: `Idle` 0, `Discharged` 1, `Discharging` 2, `Charging` 3, `Charged` 4. The APC's mode strings come from this enum (`public override string[] ModeStrings => EnumCollections.PowerModes.Names;`, 390632), and an APC's saved `Mode` state stores these numeric values.

## IsOperable: a valid output network bypasses completion, broken, and short-circuit gating
<!-- verified: 0.2.6403.27689 @ 2026-07-18 -->

Three overrides stack on the APC's operability. Verbatim at 0.2.6403.27689:

`Device.IsOperable` (370422-370432), the base gate every powered device starts from:

```csharp
protected virtual bool IsOperable
{
    get
    {
        if (base.IsStructureCompleted)
        {
            return !IsBroken;
        }
        return false;
    }
}
```

`ElectricalInputOutput.IsOperable` (394861-394871) adds the short-circuit check:

```csharp
protected override bool IsOperable
{
    get
    {
        if (OutputNetwork != null && InputNetwork == OutputNetwork)
        {
            return false;
        }
        return base.IsOperable;
    }
}
```

`AreaPowerControl.IsOperable` (390616-390630) adds the output-network fallback:

```csharp
protected override bool IsOperable
{
    get
    {
        if (!base.IsOperable || InputNetwork == null || !InputNetwork.IsNetworkValid())
        {
            if (OutputNetwork != null)
            {
                return OutputNetwork.IsNetworkValid();
            }
            return false;
        }
        return true;
    }
}
```

The fallback branch fires whenever `base.IsOperable` is false OR the input network is missing / invalid, and it consults ONLY `OutputNetwork.IsNetworkValid()`. So an APC that is under construction (`IsStructureCompleted` false), broken (`IsBroken`), or even short-circuited (`InputNetwork == OutputNetwork`, which forces the `ElectricalInputOutput` layer to false and thereby routes into the fallback) still reports operable as long as a valid output network exists.

`CheckConnections` (390919-390930) is the only place that raises or clears the APC's `Error` flag, and it keys entirely off `IsOperable`:

```csharp
protected override void CheckConnections()
{
    base.CheckConnections();
    if (GameManager.RunSimulation && !IsOperable && Error == 0)
    {
        OnServer.Interact(base.InteractError, 1);
    }
    else if (GameManager.RunSimulation && IsOperable && Error == 1)
    {
        OnServer.Interact(base.InteractError, 0);
    }
}
```

With the fallback holding `IsOperable` true, `Error` stays 0. `GetGeneratedPower` (391046-391057, quoted above) gates only on `OutputNetwork != null`, `Error != 1`, and `OnOff`; none of those ever see the build state. `GetUsedPower` (391028-391044, quoted above) REPLACES the base `Device.GetUsedPower` outright, never calling it, so the base's completion gate is dropped too. The base for contrast (371510-371521), verbatim:

```csharp
public virtual float GetUsedPower(CableNetwork cableNetwork)
{
    if (PowerCable == null || PowerCable.CableNetwork != cableNetwork)
    {
        return -1f;
    }
    if (!OnOff || !base.IsStructureCompleted)
    {
        return 0f;
    }
    return UsedPower;
}
```

The APC override carries no completion term anywhere, and its cell-charge draw term is additionally not gated on `OnOff` (see "Gate shape" above).

Net effect: an APC generates output power at ANY `CurrentBuildStateIndex` as long as an output cable exists (with `OnOff` on for the generation side). The completion gating the base `Device` power path applies is dropped wholesale by this class; completion gating is per-class, not an engine-level invariant (cross-reference: [Structure](./Structure.md), "Network and port registration is never completion-gated").

## Crowbar AttackWith: toggles the Open interactable only, never build state
<!-- verified: 0.2.6403.27689 @ 2026-07-18 -->

The APC's `AttackWith` override (390843-390879), verbatim:

```csharp
public override DelayedActionInstance AttackWith(Attack attack, bool doAction = true)
{
    Crowbar crowbar = attack.SourceItem as Crowbar;
    if ((bool)crowbar)
    {
        if (!base.AllowInteraction)
        {
            return null;
        }
        DelayedActionInstance delayedActionInstance = new DelayedActionInstance
        {
            Duration = 1f,
            ActionMessage = (IsOpen ? ActionStrings.Close : ActionStrings.Open)
        };
        if (IsLocked)
        {
            delayedActionInstance.IsDisabled = true;
            delayedActionInstance.AppendStateMessage(GameStrings.ApcUnableToMoveLocked);
            return delayedActionInstance;
        }
        if (!crowbar.IsOperable)
        {
            delayedActionInstance.IsDisabled = true;
            delayedActionInstance.AppendStateMessage(GameStrings.ApcUnableToMoveTool);
            return delayedActionInstance;
        }
        if (!doAction)
        {
            return delayedActionInstance;
        }
        if (GameManager.RunSimulation)
        {
            OnServer.Interact(base.InteractOpen, (!IsOpen) ? 1 : 0);
        }
    }
    return base.AttackWith(attack, doAction);
}
```

The only server-side mutation in the crowbar branch is `OnServer.Interact(base.InteractOpen, (!IsOpen) ? 1 : 0);` (390875): it flips the `Open` interactable state and nothing else. Build-state changes (`CurrentBuildStateIndex--`) live solely in `Structure.AttackWith`'s tool-exit branch (315205-315262, reached through the `base.AttackWith(attack, doAction)` tail call when the held tool matches the current build state's `ToolExit`; see [Structure](./Structure.md), "Tool deconstruct branch of AttackWith"). Crowbarring an APC open is orthogonal to deconstruction.

`IsOpen` is read NOWHERE in the power methods: the four power-tick bodies quoted above (391000-391057) contain no `IsOpen` reference (whole-class census over the class body, 390555-391092). While open, the only functional change is collider / visibility plumbing: `OnAnimationStart` / `OnAnimationStop` (391059-391075) call `SetContentsVisibility` (391077-391091), which enables or disables the colliders of the battery-slot, `Slot1`, and `OnOff` interactables and toggles the cell's render visibility. Verbatim (391059-391091):

```csharp
public override void OnAnimationStart()
{
    base.OnAnimationStart();
    if (IsOpen)
    {
        SetContentsVisibility(isVisible: true);
    }
}

public override void OnAnimationStop()
{
    base.OnAnimationStop();
    if (!IsOpen)
    {
        SetContentsVisibility(isVisible: false);
    }
}

public void SetContentsVisibility(bool isVisible)
{
    InteractableType action = BatterySlot.Action;
    foreach (Interactable interactable in Interactables)
    {
        if ((interactable.Action == action || interactable.Action == InteractableType.Slot1 || interactable.Action == InteractableType.OnOff) && (bool)interactable.Collider)
        {
            interactable.Collider.enabled = isVisible;
        }
    }
    if ((bool)Battery)
    {
        Battery.SetVisibility(isVisible);
    }
}
```

An open APC keeps generating, charging, and discharging exactly like a closed one; opening only exposes the cell and the on / off switch to interaction.

## Logic-method override surface: CanLogicRead and GetLogicValue only
<!-- verified: 0.2.6403.27689 @ 2026-07-13 -->

Of the four `LogicType` accessors that `Device` declares as virtuals (`CanLogicRead` 371028, `CanLogicWrite` 371104, `SetLogicValue` 371122, `GetLogicValue` 371164), `AreaPowerControl` overrides exactly two. Verbatim from the class body (390753-390773):

```csharp
public override bool CanLogicRead(LogicType logicType)
{
    if (logicType == LogicType.Charge || logicType - 23 <= LogicType.Mode)
    {
        return true;
    }
    return base.CanLogicRead(logicType);
}

public override double GetLogicValue(LogicType logicType)
{
    return logicType switch
    {
        LogicType.Charge => AvailablePower, 
        LogicType.Maximum => Battery ? Battery.PowerMaximum : 0f, 
        LogicType.Ratio => Battery ? (Battery.PowerStored / Battery.PowerMaximum) : 0f, 
        LogicType.PowerPotential => base.PotentialLoad, 
        LogicType.PowerActual => base.CurrentLoad, 
        _ => base.GetLogicValue(logicType), 
    };
}
```

(The `logicType - 23 <= LogicType.Mode` guard is the decompiler's rendering of a small enum range check, not source-shaped code.)

Both overrides fall through to the base for every unhandled `LogicType`: `return base.CanLogicRead(logicType);` at 390759 and the `_ => base.GetLogicValue(logicType)` default arm at 390771. `CanLogicWrite` and `SetLogicValue` are NOT overridden anywhere in the class body (390555-391146; a grep for the four accessor names inside that range returns only the two methods above), and the intermediate base `ElectricalInputOutput` (394813-395114) overrides none of the four either, so on an APC both write-side accessors resolve directly to the `Device` virtuals (371104, 371122).

Mod context, two sides of the same fact:

- A Harmony patch on the base `Device` logic methods reaches the APC for mod-registered LogicTypes: the write-side pair dispatches straight to `Device.CanLogicWrite` / `Device.SetLogicValue` (no APC override exists to shadow the patch), and the read-side pair funnels every unhandled type into `base.*` through the fall-through lines above.
- A direct `[HarmonyPatch(typeof(AreaPowerControl), "CanLogicWrite")]` attribute cannot resolve, because that method does not exist on `AreaPowerControl` (it is declared on `Device`); the failed resolve once killed every patch in the mod that tried it (PowerGridPlus, 2026-06-22). Target the declaring type instead. Same trap as [HarmonyLogicableInheritedMethodTrap](../Patterns/HarmonyLogicableInheritedMethodTrap.md) documents for `Battery` / `PowerTransmitter` / `PowerReceiver`.

## How the vanilla `PowerTick` drives these methods
<!-- verified: 0.2.6403.27689 @ 2026-07-02 -->

The `PowerTick` body in `Assets.Scripts.Networks.PowerTick` calls these methods in a fixed order across `Initialise` -> `CalculateState` -> `ApplyState` (decompile lines 271742-271946 at 0.2.6403.27689; see [PowerTick](./PowerTick.md)):

1. **`Initialise(CableNetwork)`** (or per-tick rebuild): iterates `Devices` (each device that holds this `CableNetwork` reference, including the APC if its `InputNetwork == this` OR `OutputNetwork == this`). For each device:
   - `float usedPower = device.GetUsedPower(CableNetwork);` is folded into `Required`.
   - `float generatedPower = device.GetGeneratedPower(CableNetwork);` is folded into `Potential` and creates a `PowerProvider` capturing `Energy = generatedPower`.

   On the InputNetwork's tick, the APC contributes `Required` via `GetUsedPower` and contributes 0 to `Potential` (its `GetGeneratedPower` returns 0 unless the tick's network is OutputNetwork). On the OutputNetwork's tick, the inverse: the APC is a `PowerProvider` with `Energy = AvailablePower`.

2. **`CalculateState()`** (public at 0.2.6403.27689, line 271765) sums `Required` / `Potential`; the private `CacheState()` (271842) then computes `_powerRatio` (guarded: `Clamp(_isPowerMet ? 1 : Potential / Required, 0, 1)` when both sums are positive, else `1f`) and `_actual = Min(Potential, Required)`.

3. **`ApplyState()`**: iterates `Devices`, ratio-scales each device's `GetUsedPower(CableNetwork)`, and calls `ConsumePower(device, CableNetwork, scaledUsedPower)`. `ConsumePower` walks the `Providers` array and per-provider:
   - Reduces the provider's `Energy` by the share consumed (`num2 = Min(powerRequired, provider.Energy)`).
   - Calls `device.ReceivePower(cableNetwork, num2)` -- so on the InputNetwork side, an APC consuming power FROM an upstream provider has `ReceivePower(InputNetwork, num2)` called on it for `num2 = its scaled share`.

   After all devices have consumed, `ApplyState` iterates `Providers` and calls `provider.ApplyPower()`. That helper computes `EnergyUsed = _originalEnergy - Energy` (i.e., how much of this provider's potential was drawn off this tick) and calls `Device.UsePower(CableNetwork, EnergyUsed)`. On the OutputNetwork side, the APC's `EnergyUsed` is exactly the share of its `AvailablePower` that downstream loads pulled this tick; `UsePower(OutputNetwork, EnergyUsed)` then drains the battery to cover that share.

So the call chain on each side is:

- **InputNetwork tick**: `Initialise` -> `APC.GetUsedPower(InputNetwork)` adds to Required. `ApplyState` -> `ConsumePower` -> `APC.ReceivePower(InputNetwork, num2)` settles `_powerProvided` and charges the battery if there is surplus.
- **OutputNetwork tick**: `Initialise` -> `APC.GetGeneratedPower(OutputNetwork)` adds to Potential (and creates the PowerProvider). `ApplyState` -> downstream `device.ReceivePower(OutputNetwork, num2)` decrements the APC PowerProvider's `Energy`. Then `Providers[i].ApplyPower()` -> `APC.UsePower(OutputNetwork, EnergyUsed)` -- battery drains here.

Conclusion: **`UsePower` is the only method that decrements `Battery.PowerStored` on the output side. Battery drain into a downstream network goes through `UsePower`, not `GetUsedPower`. `GetUsedPower` is a query that returns a value; mutating the battery from inside `GetUsedPower` would double-debit (because `GetUsedPower` is called twice per tick on the InputNetwork inside `Initialise` and `ApplyState`).**

## BatteryChargeRate and net throughput
<!-- verified: 0.2.6403.27689 @ 2026-07-02 -->

The APC declares one tunable wattage field:

```csharp
[Header("Area Power Control")]
[Tooltip("How many watts are used to charge the battery?")]
public float BatteryChargeRate = 1000f;
```

(decompile line 390586)

This is the only numeric cap inside `AreaPowerControl`. It is a watts (per-second) limit, and it applies only to charging the APC's INTERNAL `BatteryCell` from upstream surplus. Both call sites verify the gate:

- `ReceivePower` clamps the per-tick charge delta to `Min(Battery.PowerDelta, BatteryChargeRate, powerAdded)` (line 391021; `Battery.PowerDelta` here is the `BatteryCell` positive-headroom form, see [Battery](./Battery.md) "PowerDelta is SIGN-REVERSED").
- `GetUsedPower` adds `Min(BatteryChargeRate, Battery.PowerDelta)` to the input-side reported load when the battery is not full (line 391041).

What `BatteryChargeRate` does NOT cap:

- Network-to-network throughput. The APC's input-side `GetUsedPower` returns `Mathf.Max(_powerProvided, UsedPower)` for the downstream consumption portion (line 391037), with no upper bound. Whatever the output-side devices demand this tick, the APC requests that much from the input network plus the (capped) charge demand.
- Output-side discharge. `GetGeneratedPower(OutputNetwork)` returns the full `AvailablePower` = `InputNetwork.PotentialLoad + Battery.PowerStored` uncapped (`GetGeneratedPower` 391046-391057; `AvailablePower` getter 390652-390663). The battery can dump its entire `PowerStored` in a single tick if downstream demand and the cable-network limits permit.

Net effect: the APC is effectively transparent for network-to-network power transfer. The 1000 W field name "BatteryChargeRate" is literal: it gates only the internal cell charging, not the bridge throughput.

PowerGridPlus does NOT modify `BatteryChargeRate` and does NOT introduce a net-throughput cap on the APC. Its only `AreaPowerControl` patch is `UsePowerPatch` (battery-drain settlement, see next section). The `BatteryChargeRate` field stays at its vanilla 1000 W under the mod.

### Vanilla has no discharge-rate counterpart; the PowerGridPlus cap is mod-introduced
<!-- verified: 0.2.6403.27689 @ 2026-07-18 -->

Re-stating the discharge side of the section above as an explicit census: vanilla `AreaPowerControl` contains NO discharge-rate limit anywhere.

- `BatteryChargeRate = 1000f` (390584-390586) gates CHARGING only. Both consumers are charge-side: the `Mathf.Min(Battery.PowerDelta, BatteryChargeRate, powerAdded)` clamp inside `ReceivePower` (391021-391022) and the `Mathf.Min(BatteryChargeRate, Battery.PowerDelta)` charge-demand term inside `GetUsedPower` (391041).
- `GetGeneratedPower` advertises the whole remaining store: `AvailablePower` adds the full `Battery.PowerStored` on top of upstream `PotentialLoad` (getter 390652-390663), with no rate term.
- `UsePower` (391000-391012) drains `Mathf.Min(Battery.PowerStored, _powerProvided)`: bounded only by stored energy and the consumed-output ledger, never by a rate field.

PowerGridPlus's "APC Battery Discharge Rate" setting (`Settings.ApcBatteryDischargeRate`, consumed in `Mods/PowerGridPlus/PowerGridPlus/PowerAllocator.cs` when computing the APC cell's per-tick effective discharge) is therefore a mod-introduced cap with no vanilla counterpart, unlike the charge side where the mod's `ApcBatteryChargeRate` config re-tunes a role vanilla already had.

## Re-Volt 1.4.0 patch attribution: probable bug
<!-- verified: 0.2.6228.27061 @ 2026-05-22 -->

Re-Volt 1.4.0's `Assets/Scripts/Patches/AreaPowerControllerPatches.cs` (verbatim, MIT (c) 2025 Sukasa) declares THREE prefix patches on `AreaPowerControl`:

```csharp
[HarmonyPrefix]
[HarmonyPatch(nameof(AreaPowerControl.ReceivePower))]
public static bool ReceivePowerPatch(CableNetwork cableNetwork, float powerAdded, ...)

[HarmonyPrefix]
[HarmonyPatch(nameof(AreaPowerControl.GetUsedPower))]     // <-- declares GetUsedPower target
public static bool UsePowerPatch(CableNetwork cableNetwork, float powerUsed, ...)  // <-- but takes powerUsed

[HarmonyPrefix]
[HarmonyPatch(nameof(AreaPowerControl.GetUsedPower))]
public static bool GetUsedPowerPatch(CableNetwork cableNetwork, ..., ref float __result, ...)
```

The second patch (`UsePowerPatch`) is attributed to `GetUsedPower` but takes a `powerUsed` parameter. Inspecting the four `AreaPowerControl` overrides above, only `UsePower(CableNetwork, float powerUsed)` has a `powerUsed` parameter; `GetUsedPower(CableNetwork)` does not. The patch body inside `UsePowerPatch` is structurally the battery-drain logic that belongs on `UsePower`:

```csharp
// Re-Volt's UsePowerPatch body (declared on GetUsedPower):
if (cableNetwork != __instance.OutputNetwork) return false;
if (__instance.Battery && !__instance.Battery.IsEmpty)
{
    float num = Mathf.Min(__instance.Battery.PowerStored, powerUsed);
    __instance.Battery.PowerStored -= num;
    powerUsed -= num;
}
____powerProvided += powerUsed;
return false;
```

This decrements `Battery.PowerStored` -- the output-side drain. The `OutputNetwork` gate matches `UsePower`'s `OutputNetwork` gate (vanilla `UsePower` is the only method gated on `cableNetwork == OutputNetwork`). The body must target `UsePower` to function.

HarmonyX's behaviour at `PatchAll` time when a prefix declares a parameter (`powerUsed`) that the resolved target method (`GetUsedPower`) does not have: the prefix is rejected. Both patches on `GetUsedPower` are declared in the same class; HarmonyX iterates them and the rejected one bails out without disabling the rest. Net effect at runtime for Re-Volt:

- `ReceivePower` is patched correctly (input-side charge).
- `GetUsedPower` is patched correctly by `GetUsedPowerPatch` (input-side query).
- `UsePower` is NOT patched -- vanilla body runs, including the vanilla `_powerProvided > 0` drain gate.

So the battery still drains in Re-Volt, via vanilla `UsePower`. The "bug" is silent: the misattributed patch never runs, but vanilla covers the same behaviour minus Re-Volt's intended refinement (dropping the `_powerProvided > 0` gate so the battery drains every tick that demands power, not only when last tick's settlement carried a positive debit).

## Power Grid Plus deviation: retarget at `UsePower`
<!-- verified: 0.2.6228.27061 @ 2026-05-22 -->

`Mods/PowerGridPlus/PowerGridPlus/Patches/AreaPowerControlPatches.cs` reattributes the misnamed prefix at the method whose signature actually matches:

```csharp
[HarmonyPrefix, HarmonyPatch(nameof(AreaPowerControl.UsePower))]   // <-- corrected to UsePower
public static bool UsePowerPatch(CableNetwork cableNetwork, float powerUsed, AreaPowerControl __instance, ref float ____powerProvided)
{
    if (!Settings.EnableAreaPowerControlFix.Value) return true;
    if (cableNetwork != __instance.OutputNetwork) return false;

    if ((bool)__instance.Battery && !__instance.Battery.IsEmpty)
    {
        float num = Mathf.Min(__instance.Battery.PowerStored, powerUsed);
        __instance.Battery.PowerStored -= num;
        powerUsed -= num;
    }

    ____powerProvided += powerUsed;
    return false;
}
```

Difference vs vanilla `UsePower`:

- Vanilla: drains battery only when `_powerProvided > 0` (deferred settlement: cover last tick's debit before logging this tick's `powerUsed`).
- PGP: drains battery whenever `Battery && !Battery.IsEmpty` (immediate settlement: cover this tick's `powerUsed` directly, then log the remainder to `_powerProvided`).

Both correctly drain `Battery.PowerStored`. PGP's variant matches Re-Volt's intended behaviour (which Re-Volt itself fails to achieve because of the patch-attribution mistake).

If reverting PGP's `UsePowerPatch` to Re-Volt 1:1 (declaring it on `GetUsedPower` again), the patch would silently no-op as it does in Re-Volt, and PGP would fall back to vanilla `UsePower`. That would still drain the battery -- just with the `_powerProvided > 0` gate restored.

## PGP post-b3baffb (2026-05-28): cable-headroom and cable-tier output cap
<!-- verified: 0.2.6228.27061 @ 2026-05-28 -->

After commit `b3baffb` (2026-05-28), `AreaPowerControlPatches.cs` no longer carries a `UsePower` prefix. Vanilla `UsePower` runs unmodified, retaining the `_powerProvided > 0` drain gate that drains the cell only on a real shortfall. PGP's APC patches today, gated on `Settings.EnableAreaPowerControlFix.Value` (default ON):

- **`ReceivePowerPatch` (prefix)**: closes the upstream leak. Subtracts `UsedPower` first, settles `_powerProvided`, then charges the cell using `ComputeChargeCap(__instance)` instead of the vanilla `BatteryChargeRate` field.
- **`GetUsedPowerPatch` (prefix)**: input-side demand = `UsedPower + _powerProvided + Min(ComputeChargeCap, Battery.PowerDelta)`.
- **`TrackPassthroughPatch` (postfix, new)**: stashes last tick's `powerUsed` per APC into a `ConcurrentDictionary<long, float>` keyed by `ReferenceId`. Provides the pass-through value used by `ComputeChargeCap`.
- **`GetGeneratedPowerPatch` (postfix, new)**: clamps APC output supply at `outputCable.MaxVoltage`. A single APC cannot supply more than its output cable physically carries.

`ComputeChargeCap` formula (verbatim, `AreaPowerControlPatches.cs`):

```csharp
float configCap = Settings.ApcBatteryChargeRate.Value;        // default 1000 W
if (configCap <= 0f) return 0f;
var inputCable = apc.InputConnection?.GetCable();
if (inputCable == null) return configCap;
float maxVoltage = inputCable.MaxVoltage;
if (maxVoltage <= 0f) return 0f;
float passthrough = 0f;
_lastPassthrough.TryGetValue(apc.ReferenceId, out passthrough);
float cableSpare = maxVoltage - passthrough;
if (cableSpare <= 0f) return 0f;
return Mathf.Min(configCap, cableSpare);
```

Steady-state behaviour under surplus upstream (downstream demand `X`, configCap `R = 1000`):

1. `GetGeneratedPower(Output)` returns `min(AvailablePower, outputCable.MaxVoltage)`.
2. Vanilla `UsePower(Output, X)`: drains the cell ONLY when `_powerProvided > 0` (carried-over shortfall debit). Under surplus, `_powerProvided` is settled to `<= 0` by `ReceivePower`, the drain branch never fires, then `_powerProvided += X`.
3. `GetUsedPower(Input)` requests `UsedPower + _powerProvided + min(R, MaxVoltage - passthrough)` from upstream.
4. `ReceivePower(Input)`: settles `_powerProvided` and charges cell up to `ComputeChargeCap`.

The pre-b3baffb `R - X` ceiling no longer applies. Under surplus upstream the cell accumulates `ComputeChargeCap` per tick regardless of downstream draw, until the cell fills or upstream supply falls short. When upstream falls short, vanilla `UsePower` drains the cell for the shortfall -- buffer semantics, the APC's intended role.

Per-device, NOT per-network. Other APCs / batteries on the same input cable are NOT subtracted from `cableSpare`. The cable can still overload from combined load of multiple devices; that's handled by PGP's existing cable burn-on-overload mechanism, not this cap. The cap only ensures a single device cannot blow its own cable by adding charge demand on top of its own pass-through.

`EnableAreaPowerControlFix = false` reverts `ReceivePower` / `GetUsedPower` / `GetGeneratedPower` to vanilla (no leak fix, no cable caps). Since vanilla `UsePower` runs in both branches now, the toggle no longer affects the `UsePower` path.

Verified headlessly 2026-05-28 via the `pgp-rate-cap-probe` ScenarioRunner scenario against `APC-Luna.save`: 8/8 wired APCs pass both `ComputeChargeCap` and `GetGeneratedPower` cap checks across normal (5 kW) and heavy (100 kW) cable tiers.

## HISTORICAL: pre-b3baffb net-charge ceiling (PGP versions before 2026-05-28)
<!-- verified: 0.2.6228.27061 @ 2026-05-28 -->

**Status: FIXED in commit `b3baffb` (2026-05-28).** This section preserved for context: the analysis explains why the ceiling existed and what motivated the rewrite. The PGP code described here is no longer in the tree; see the section above for current behaviour. Three earlier-PGP patches had this non-obvious steady-state consequence: when the APC's downstream load was at or above `BatteryChargeRate`, the internal cell's stored charge never accumulated, even with unlimited input-side supply. Below that load it filled normally. The crossover was exactly `BatteryChargeRate` (1000), independent of `UsedPower`.

The three PGP patches in play (verbatim, `AreaPowerControlPatches.cs`):

```csharp
// GetUsedPowerPatch -- input-side request
usedPower += UsedPower;
if (OutputNetwork != null) usedPower += ____powerProvided;
if (Battery && !Battery.IsCharged) usedPower += Mathf.Min(BatteryChargeRate, Battery.PowerDelta);

// UsePowerPatch -- output-side drain, EVERY tick the battery is non-empty
if (Battery && !Battery.IsEmpty) {
    float num = Mathf.Min(Battery.PowerStored, powerUsed);
    Battery.PowerStored -= num;
    powerUsed -= num;
}
____powerProvided += powerUsed;

// ReceivePowerPatch -- input-side settle + charge
powerAdded -= UsedPower;
if (powerAdded <= 0f) return false;
____powerProvided -= powerAdded;
if (____powerProvided >= 0f || !Battery || Battery.IsCharged) return false;
float num = Mathf.Min(Battery.PowerDelta, BatteryChargeRate, powerAdded);
Battery.PowerStored += num;
____powerProvided += num;
```

Steady-state trace (battery non-empty, surplus input, continuous downstream demand `X` per tick, `UsedPower` = `u`, `BatteryChargeRate` = `R` = 1000):

1. `UsePower(Output, X)`: battery non-empty, so `drain = Min(Battery.PowerStored, X)`. With charge in hand, `drain = X`, `PowerStored -= X`, leftover `powerUsed = 0`, so `_powerProvided` unchanged.
2. `GetUsedPower(Input)` returns `u + _powerProvided + Min(R, PowerDelta) = u + _powerProvided + R`.
3. `ReceivePower(Input, supplied)` with `supplied = u + _powerProvided + R`: subtract `u` -> `_powerProvided + R`; `_powerProvided -= (_powerProvided + R)` -> `-R`; `< 0`, so `num = Min(PowerDelta, R, _powerProvided+R) = R`; `PowerStored += R`; `_powerProvided += R` -> `0`.

Net `PowerStored` change per tick = `-X` (step 1) `+ R` (step 3) = **`R - X`**. The `u` term appears once with `+` in `GetUsedPower` and once with `-` in `ReceivePower` and cancels, so the crossover does not depend on `UsedPower`:

- `X < R`: net positive, cell fills.
- `X = R`: net zero.
- `X > R`: net negative; the cell drains until it hits the near-empty regime, where `UsePower` can only drain what little is stored and the cell stays pinned near 0 (charging `R` per tick, drained `R`+ per tick). Visually the cell sits at Empty / Critical and looks like it "won't charge". With `R = 1000` against a multi-hundred-kJ `ItemBatteryCellNuclear`, one tick's `R` is a negligible fraction of `PowerMaximum`, so the cell never visibly leaves the bottom `BatteryCellState` band.

This is the threshold a player observes as "the APC only charges when load is below ~1 kW": the in-game load figure crossing `BatteryChargeRate`'s literal `1000` value is the crossover, matching the `R - X` net derived above.

Vanilla does NOT exhibit this ceiling. Vanilla `UsePower` drains the cell only when `_powerProvided > 0` at the *start* of the call (a carried-over debit from a tick where input fell short). Under surplus input the input-side `ReceivePower` settles `_powerProvided` to exactly 0 (or negative) every tick, so the vanilla drain branch never fires and the cell accumulates `BatteryChargeRate` per tick regardless of downstream load (until input can no longer cover demand + charge, or the cell fills). The ceiling is therefore an artifact of PGP's "drain every tick" change interacting with the unchanged `BatteryChargeRate` charge cap, NOT a vanilla behaviour and NOT a new throughput cap (network-to-network transfer is still uncapped, per the "BatteryChargeRate and net throughput" section above; only the cell's net *accumulation* is bounded).

Setting `EnableAreaPowerControlFix` to false restored vanilla `UsePower` (cell fills regardless of load, at the cost of vanilla's idle slow-leak that the fix exists to stop). `BatteryChargeRate` was per-prefab serialized data (C# default 1000f, line 369540), so the crossover could not be retuned through PowerGridPlus config without an additional field patch. (Both points obsolete post-b3baffb: vanilla `UsePower` always runs now regardless of the toggle, and the new `Settings.ApcBatteryChargeRate` config retunes the cap.)

## Pattern presence in other ledger-based classes
<!-- verified: 0.2.6403.27689 @ 2026-07-02 -->

The `_powerProvided` ledger field that drives the net-charge ceiling above is not unique to `AreaPowerControl`. Four vanilla classes carry a `_powerProvided` field (`grep "_powerProvided"` over the 0.2.6403.27689 decompile returns these declarations):

| Class | Decompile line | Has internal energy storage? | Patched by PowerGridPlus? |
|---|---|---|---|
| `AreaPowerControl` | 390592 | Yes (`BatteryCell` in slot 0) | yes -- `AreaPowerControlPatches.cs` |
| `Transformer` | 424621 | No | yes -- `TransformerExploitPatches.cs` |
| `PowerReceiver` | 408071 | No | no |
| `PowerTransmitter` | 408287 | No | no |

Only `AreaPowerControl` couples `_powerProvided` to an energy store. The other three use the ledger purely to gate request math: vanilla `Transformer.GetUsedPower` returns `Min((float)Setting + UsedPower, _powerProvided)` (line 424791) and vanilla `PowerReceiver.GetUsedPower` returns `Min(PowerTransmitter.MaxPowerTransmission + UsedPower, _powerProvided)` (line 408229); both use `_powerProvided` as an upper bound on what to request from upstream, not as a debit to drain from a buffer. With no storage to mis-drain, the APC's net-charge asymmetry has no analogue in those classes.

Verbatim vanilla `Transformer` power-tick methods (`UsePower` 424757-424763, `ReceivePower` 424765-424771, `GetUsedPower` 424773-424792, `GetGeneratedPower` 424794-424805):

```csharp
public override void UsePower(CableNetwork cableNetwork, float powerUsed)
{
    if (Error != 1 && OnOff && cableNetwork == OutputNetwork)
    {
        _powerProvided += powerUsed;
    }
}

public override void ReceivePower(CableNetwork cableNetwork, float powerAdded)
{
    if ((InputNetwork == null || cableNetwork == InputNetwork) && OnOff && InputNetwork != null)
    {
        _powerProvided -= powerAdded;
    }
}

public override float GetUsedPower([NotNull] CableNetwork cableNetwork)
{
    if (InputNetwork == null || OutputNetwork == null || cableNetwork != InputNetwork) return 0f;
    if (Error == 1) return !OnOff ? 0f : UsedPower;
    if (!OnOff) return 0f;
    return Mathf.Min((float)Setting + UsedPower, _powerProvided);
}

public override float GetGeneratedPower(CableNetwork cableNetwork)
{
    if (OutputNetwork == null || Error == 1 || cableNetwork != OutputNetwork) return 0f;
    if (!OnOff || InputNetwork == null) return 0f;
    return Mathf.Min((float)Setting, InputNetwork.PotentialLoad);
}
```

Vanilla `Transformer.UsePower` has no battery-drain branch and no `_powerProvided > 0` gate -- it just increments the ledger. The asymmetry that produces the APC bug literally cannot exist here.

PGP's `TransformerExploitPatches.cs` accordingly does NOT patch `UsePower`. It replaces `GetGeneratedPower` (subtracts `_powerProvided` from upstream `PotentialLoad` so a transformer simultaneously offering and being drawn from does not double-count its share), `GetUsedPower` (adds `UsedPower` as an explicit additive term so the transformer charges its own quiescent draw to upstream), and `ReceivePower` (subtracts `UsedPower` from incoming power before settling the ledger). None of these touch any energy store. Verbatim from `Mods/PowerGridPlus/PowerGridPlus/Patches/TransformerExploitPatches.cs`:

```csharp
// GetGeneratedPowerPatch
__result = Mathf.Min((float)__instance.Setting, __instance.InputNetwork.PotentialLoad - ____powerProvided);

// GetUsedPowerPatch
__result = Mathf.Min((float)__instance.Setting, ____powerProvided) + __instance.UsedPower;

// ReceivePowerPatch
powerAdded -= __instance.UsedPower;
if (powerAdded < 0.0f) return false;
____powerProvided -= powerAdded;
```

`PowerReceiver` (wireless RX, lines 408184-408229) and `PowerTransmitter` (wireless TX, lines 408424-408469) follow the same shape as Transformer: ledger-only, no storage. PowerGridPlus does not patch either of them; they retain full vanilla behaviour and are out of scope of the APC charge-ceiling bug.

Station-mounted `Battery` (documented in [Battery.md](./Battery.md)) is the other class with internal energy storage, but it does NOT carry a `_powerProvided` field. Its vanilla `UsePower` / `ReceivePower` write directly into `PowerStored` without a ledger (decompile lines 392144-392158). PGP's `StationaryBatteryPatches.cs` adds per-tick caps on `GetUsedPower` (charge headroom) and `GetGeneratedPower` (discharge availability) and rewrites `ReceivePower` to apply `BatteryChargeEfficiency`, but does NOT touch `UsePower`. There is no `_powerProvided > 0` gate to remove and no equivalent immediate-vs-deferred settlement issue.

The station `Battery` does carry an intentional asymmetry of its own under PowerGridPlus defaults (`MaxBatteryChargeRate = 0.002`, `MaxBatteryDischargeRate = 0.007` -- discharge cap is 3.5x the charge cap), so a Battery whose continuous downstream draw equals its discharge cap will slowly net-drain rather than hold equilibrium. That is a design choice (batteries are a reserve, not a generator) and is exposed as a tunable in the BepInEx config; it is not a bug surface comparable to the APC ceiling, where the asymmetry is fixed at the literal `BatteryChargeRate = 1000f` field and the player has no config to retune it.

Net conclusion: the net-charge ceiling is localised to `AreaPowerControl`. The proximate cause -- PGP's `UsePower` patch removing vanilla's `_powerProvided > 0` drain gate -- does not appear in any other PGP-patched class. The other `_powerProvided` users either have no `UsePower` patch (Transformer, by Re-Volt design) or are unpatched entirely (PowerReceiver, PowerTransmitter), and the only other storage-bearing electrical class (`Battery`) uses a different mechanism without a ledger.

### The ledger is not serialized: no save field, no join field (0.2.6403 census)
<!-- verified: 0.2.6403.27689 @ 2026-07-06 -->

None of the four `_powerProvided` carriers persists the ledger at 0.2.6403.27689. The save-data records: `TransformerSaveData` (424593-424597) holds only `[XmlElement] public float OutputSetting;`; `WirelessPowerSaveData` (426765-426778) holds only the four dish-rotation doubles; `PowerTransmitterSaveData` (408264-408268) adds only `long OutputNetworkReferenceId`; `PowerReceiverSaveData` (408062-408064) adds nothing. `AreaPowerControl` itself defines NO save-data record and NO serialization member of any kind: its class body (390555-391146; the next type declaration is `AtmosphericSeat` at 391147) contains no `SerializeSave` / `InitialiseSaveData` / `DeserializeSave` / `SerializeOnJoin` / `DeserializeOnJoin` / `BuildUpdate` / `ProcessUpdate` override, so the APC saves purely through its inherited structure save path.

The whole-decompile `_powerProvided` reference census (declarations 390592 / 408071 / 408287 / 424621 plus only the `UsePower` / `ReceivePower` / `GetUsedPower` sites quoted on this page) also proves no join-stream or delta-sync member touches the field. In-game, the only paths that write it are the two dispatch sites inside `PowerTick.ApplyState` on the power worker: `PowerProvider.ApplyPower` (271690-271696) -> `UsePower`, and `PowerTick.ConsumePower` (271820-271840) -> `ReceivePower`.

Consequence: the ledger is runtime accumulation within a session. On save load and on client join every `_powerProvided` restarts at the C# default 0; the "residual debt persists for the life of the object" rule on [Device](./Device.md) ("Two per-device draw-state fields") is bounded by the session, and a mod auditing or reconstructing the ledger never has to consider saved state. Scope: verified at 0.2.6403.27689; older game versions unverified.

## Verification history
<!-- verified: 0.2.6403.27689 @ 2026-07-06 -->

- 2026-07-18: added three sections and one subsection from the APC operability / open-state research pass; the findings were produced and independently adversarially verified against the 0.2.6403.27689 decompile this session. "PowerMode enum" (390547-390554 verbatim; Idle 0, Discharged 1, Discharging 2, Charging 3, Charged 4). "IsOperable: a valid output network bypasses completion, broken, and short-circuit gating" (APC override 390616-390630 over the Device 370422-370432 and ElectricalInputOutput 394861-394871 layers; CheckConnections 390919-390930 keys Error entirely off IsOperable, so the fallback keeps Error at 0; GetUsedPower replaces the base outright, dropping Device's `OnOff && IsStructureCompleted` gate at 371510-371521; net effect, output generation at any CurrentBuildStateIndex while a valid output network exists). "Crowbar AttackWith" (390843-390879; sole mutation `OnServer.Interact(base.InteractOpen, ...)` at 390875; IsOpen unread in the power methods per a whole-class census; SetContentsVisibility 391059-391091 toggles interactable colliders and cell visibility only). "Vanilla has no discharge-rate counterpart" subsection (BatteryChargeRate's two consumers are both charge-side; GetGeneratedPower / UsePower carry no rate term; PowerGridPlus's `Settings.ApcBatteryDischargeRate` is a mod-introduced cap). All additive; no existing claim changed. Incidental line-ref note: the class body actually closes at 391092 and the `AsciiString` struct occupies 391093-391146 before `AtmosphericSeat` at 391147; earlier entries citing "class body 390555-391146" span that extra struct, which contains none of the members those censuses looked for, so their conclusions stand unchanged.
- 2026-07-13: added "Logic-method override surface: CanLogicRead and GetLogicValue only" section (game version 0.2.6403.27689). Direct grep census over the class body (390555-391146): `CanLogicRead` (390753-390760) and `GetLogicValue` (390762-390773) are the only two of the four `Device` logic accessors the APC overrides, quoted verbatim; both fall through to `base.*` for unhandled types (390759 and the default arm at 390771); no `CanLogicWrite` / `SetLogicValue` override exists in the class or in `ElectricalInputOutput` (394813-395114, grep returns nothing), so the write side resolves to the `Device` virtuals (`CanLogicWrite` 371104, `SetLogicValue` 371122; declarations `CanLogicRead` 371028, `GetLogicValue` 371164). Recorded the two Harmony consequences: base-`Device` patches reach the APC for mod-registered LogicTypes, and a `typeof(AreaPowerControl)` attribute on the write-side names cannot resolve (the PowerGridPlus 2026-06-22 all-patches-dead incident). Additive; no prior content contradicted.
- 2026-07-07: added "Gate shape" subsection under the four-methods section (game version 0.2.6403.27689). Re-read `GetUsedPower` (391028-391044) and `GetGeneratedPower` (391046-391057): the quiescent + ledger term is gated on `OnOff && OutputNetwork != null`, the cell-charge term on `(bool)Battery && !Battery.IsCharged` ONLY (no OnOff, no Error, no OutputNetwork), and `GetGeneratedPower` on `OutputNetwork != null && Error != 1 && OnOff`. Note: the incoming claim from the PowerGridPlus partial-power forensics said the charge term was "gated on OnOff only"; the decompile shows it is not OnOff-gated either, so the documented consequence is wider (OFF, errored, and output-less APCs all still charge-draw). Cross-referenced the same asymmetry on `WallLightBattery.GetUsedPower` (327979-327992). Additive; the verbatim bodies already on this page were confirmed unchanged.
- 2026-07-06: added "The ledger is not serialized" subsection under the pattern-presence section (game version 0.2.6403.27689). Evidence read directly from the decompile: the four SaveData record bodies (`TransformerSaveData` 424593-424597, `WirelessPowerSaveData` 426765-426778, `PowerTransmitterSaveData` 408264-408268, `PowerReceiverSaveData` 408062-408064), the absence of any serialization member in the `AreaPowerControl` class body (390555-391146, next type at 391147), the whole-decompile `_powerProvided` reference census, and the `.UsePower(` / `.ReceivePower(` caller census isolating `PowerProvider.ApplyPower` (271690-271696) and `PowerTick.ConsumePower` (271820-271840) as the only dispatch sites reaching the four ledger classes. Checked first for existing verified content claiming the ledger persists into saves (fresh-validator trigger): none found on this or any other central page, so the addition is additive; no validator spawned.
- 2026-07-02: re-verification pass against the 0.2.6403.27689 decompile after the game update from 0.2.6228.27061. Confirmed unchanged with new line refs: class declaration (390555), `BatteryChargeRate = 1000f` (390586), the slot `Battery` property is a `BatteryCell` (390594), `UsePower` drain-gated on `_powerProvided > 0` (391000-391012), `ReceivePower` charges the cell only when `_powerProvided < 0` (391014-391026), `GetUsedPower` = `Max(_powerProvided, UsedPower)` + `Min(BatteryChargeRate, Battery.PowerDelta)` (391028-391044), `GetGeneratedPower` = `AvailablePower` = `InputNetwork.PotentialLoad + Battery.PowerStored` uncapped (391046-391057, `AvailablePower` 390652-390663). Also refreshed the Transformer method citations carried by this page (`UsePower` / `ReceivePower` 424757-424771, `GetUsedPower` 424773-424792, `GetGeneratedPower` = `Min(Setting, InputNetwork.PotentialLoad)` 424794-424805), the four `_powerProvided` declaration lines in the pattern-presence table (390592 / 424621 / 408071 / 408287), the RX/TX method ranges (408184-408229 / 408424-408469), and the station-Battery `UsePower` / `ReceivePower` range (392144-392158). Corrected the PowerTick-driver section's attribution of the ratio math: `CalculateState` is public at 0.2.6403 and the ratio lives in the private `CacheState` with the both-positive guard (see [PowerTick](./PowerTick.md)). PGP-behaviour sections (post-b3baffb, historical ceiling, Re-Volt attribution) were not re-tested this pass and keep their stamps.
- 2026-05-28 (later): added "PGP post-b3baffb (2026-05-28): cable-headroom and cable-tier output cap" section reflecting commit `b3baffb` and reframed the prior "Net-charge ceiling" section as historical (the `UsePowerPatch` that produced the ceiling is removed; vanilla `UsePower` now runs unmodified). The earlier analysis is preserved verbatim under a HISTORICAL heading because it explains why the bug existed and what the rewrite addressed. Verified headlessly via `pgp-rate-cap-probe` ScenarioRunner scenario: 8/8 wired APCs on `APC-Luna.save` pass `ComputeChargeCap` (per-device, cable-bounded) and `GetGeneratedPower` (output cable MaxVoltage cap) checks across normal and heavy cable tiers.
- 2026-05-28: added "Pattern presence in other ledger-based classes" section. Sweeps the four vanilla classes that carry a `_powerProvided` field (decompile lines 369546, 403323, 386867, 387083) and confirms that the APC's net-charge ceiling bug pattern is structurally impossible in `Transformer`, `PowerReceiver`, `PowerTransmitter`, and the station-mounted `Battery`: the other three `_powerProvided` users have no internal energy storage to mis-drain (vanilla `Transformer.UsePower` only increments the ledger; lines 403459-403465), and the only other storage-bearing electrical class (`Battery`) has no `_powerProvided` ledger and is not patched on `UsePower` by PowerGridPlus. Documents PGP's TransformerExploitPatches verbatim and confirms it does not patch `UsePower`. Notes the station `Battery`'s intentional charge/discharge asymmetry under PGP defaults (`0.002` / `0.007`) is a separate, configurable design choice and not the same bug shape. Additive content; does not contradict prior sections.
- 2026-05-28: added "Net-charge ceiling under PowerGridPlus" section. Documents the steady-state consequence of PGP's three-patch APC rewrite (`AreaPowerControlPatches.cs`, gated on `EnableAreaPowerControlFix`): the internal cell's net charge per tick is `BatteryChargeRate - downstreamDemand`, so the cell stops accumulating once continuous downstream load reaches `BatteryChargeRate` (1000). Derived the crossover is independent of `UsedPower` (it cancels between `GetUsedPower`'s `+u` and `ReceivePower`'s `-u`). Confirmed vanilla does NOT show the ceiling (vanilla `UsePower` drain gate `_powerProvided > 0` never fires under surplus input, so the cell fills regardless of load). Corroborated by a developer's in-game observation on the APC-Luna save: "the APC only charges when load is below ~1 kW." Additive content; refines but does not contradict the "BatteryChargeRate and net throughput" and "Power Grid Plus deviation" sections. Full InspectorPlus before/after capture on a dedicated server still pending.
- 2026-05-26: added "BatteryChargeRate and net throughput" section. Documents the literal field value (1000f, line 369540), its two call sites in `ReceivePower` and `GetUsedPower`, and the explicit conclusion that this cap applies only to internal-cell charging and NOT to network-to-network throughput (`GetGeneratedPower` returns full `AvailablePower` uncapped; `_powerProvided` term in `GetUsedPower` is unbounded). Additive content; does not contradict prior sections.
- 2026-05-22: page created during PowerGridPlus pre-release verification task 6 (APC patch correctness). Source: Re-Volt 1.4.0 `AreaPowerControllerPatches.cs` (MIT, (c) 2025 Sukasa) cross-checked against Stationeers 0.2.6228.27061 `AreaPowerControl` decompile (lines 369509-370046) and `PowerTick` decompile (lines 254512-254765). Verdict: PGP's retargeting of `UsePowerPatch` from `GetUsedPower` to `UsePower` is structurally correct (the patch body only matches `UsePower`'s signature, and `UsePower` is the only method that decrements `Battery.PowerStored` in the output-side call chain). Re-Volt's original attribution is a silent no-op at HarmonyX `PatchAll` time. Dynamic verification on a dedicated-server test save pending.

## Open questions
<!-- verified: 0.2.6228.27061 @ 2026-05-22 -->

- Does HarmonyX's parameter-mismatch handling differ across HarmonyX versions, such that Re-Volt's misattributed `UsePowerPatch` could apply as a prefix on `GetUsedPower` (with `powerUsed` bound to 0)? The static-analysis verdict above assumes the patch is rejected at `PatchAll` time. If instead the patch applies with a default-bound `powerUsed`, the `____powerProvided += powerUsed` becomes `+= 0` (no-op) and `Min(Battery.PowerStored, 0) = 0` (no-op) -- behaviourally still silent. Either failure mode produces the same observable end state, so the verdict does not change.
