---
title: AreaPowerControl
type: GameClasses
created_in: 0.2.6228.27061
verified_in: 0.2.6228.27061
verified_at: 2026-05-28
sources:
  - rocketstation_Data/Managed/Assembly-CSharp.dll :: Assets.Scripts.Objects.Electrical.AreaPowerControl
  - Mods/PowerGridPlus/PowerGridPlus/Patches/AreaPowerControlPatches.cs
  - .work/revolt-source/Assets/Scripts/Patches/AreaPowerControllerPatches.cs
related:
  - ./Battery.md
  - ./CableNetwork.md
  - ./Transformer.md
tags: [power, prefab]
---

# AreaPowerControl

Vanilla `Assets.Scripts.Objects.Electrical.AreaPowerControl` (the APC). Bridges two cable networks (`InputNetwork` from `ElectricalInputOutput`, `OutputNetwork` from the same base) and holds an optional `BatteryCell` in slot 0. Reports a load on the input side, supplies power to the output side, and uses its own battery as a buffer.

The class extends `ElectricalInputOutput` (not `Logicable` directly), so its two cable ports sit in two different `CableNetwork.DeviceList`s -- the APC itself appears in BOTH networks' device lists.

## Three power-tick methods, two sides
<!-- verified: 0.2.6228.27061 @ 2026-05-22 -->

`AreaPowerControl` overrides three of the four `Device` power-tick methods, and each one is side-keyed: it returns / acts only when called for either the input cable network or the output cable network. There is no behaviour on a third unrelated network.

| Method | Side | Purpose |
|---|---|---|
| `GetUsedPower(CableNetwork)` | `InputNetwork` only | Reports the APC's load on the upstream side (quiescent draw + downstream throughput + battery charge demand). |
| `ReceivePower(CableNetwork, float powerAdded)` | `InputNetwork` only | Settles upstream supply against `_powerProvided`; if surplus and battery not full, charges the battery up to `BatteryChargeRate`. |
| `GetGeneratedPower(CableNetwork)` | `OutputNetwork` only | Reports the APC's potential supply downstream (`AvailablePower` = upstream `PotentialLoad` + `Battery.PowerStored`). |
| `UsePower(CableNetwork, float powerUsed)` | `OutputNetwork` only | Drains the battery to cover the downstream consumption; bumps `_powerProvided` by `powerUsed` for the next tick. |

The four methods inherited from `Device` are `GetUsedPower`, `GetGeneratedPower`, `ReceivePower`, `UsePower`. APC overrides all four.

Verbatim from the decompile (line ranges 369954-370011):

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

## How the vanilla `PowerTick` drives these methods
<!-- verified: 0.2.6228.27061 @ 2026-05-22 -->

The `PowerTick` body in `Assets.Scripts.Networks.PowerTick` calls these methods in a fixed order across `Initialise` -> `CalculateState` -> `ApplyState` (decompile excerpts from PowerTick around line 254580-254755):

1. **`Initialise(CableNetwork)`** (or per-tick rebuild): iterates `Devices` (each device that holds this `CableNetwork` reference, including the APC if its `InputNetwork == this` OR `OutputNetwork == this`). For each device:
   - `float usedPower = device.GetUsedPower(CableNetwork);` is folded into `Required`.
   - `float generatedPower = device.GetGeneratedPower(CableNetwork);` is folded into `Potential` and creates a `PowerProvider` capturing `Energy = generatedPower`.

   On the InputNetwork's tick, the APC contributes `Required` via `GetUsedPower` and contributes 0 to `Potential` (its `GetGeneratedPower` returns 0 unless the tick's network is OutputNetwork). On the OutputNetwork's tick, the inverse: the APC is a `PowerProvider` with `Energy = AvailablePower`.

2. **`CalculateState()`** (private): computes `_powerRatio = isPowerMet ? 1 : Potential / Required` and `_actual = Min(Potential, Required)`.

3. **`ApplyState()`**: iterates `Devices`, ratio-scales each device's `GetUsedPower(CableNetwork)`, and calls `ConsumePower(device, CableNetwork, scaledUsedPower)`. `ConsumePower` walks the `Providers` array and per-provider:
   - Reduces the provider's `Energy` by the share consumed (`num2 = Min(powerRequired, provider.Energy)`).
   - Calls `device.ReceivePower(cableNetwork, num2)` -- so on the InputNetwork side, an APC consuming power FROM an upstream provider has `ReceivePower(InputNetwork, num2)` called on it for `num2 = its scaled share`.

   After all devices have consumed, `ApplyState` iterates `Providers` and calls `provider.ApplyPower()`. That helper computes `EnergyUsed = _originalEnergy - Energy` (i.e., how much of this provider's potential was drawn off this tick) and calls `Device.UsePower(CableNetwork, EnergyUsed)`. On the OutputNetwork side, the APC's `EnergyUsed` is exactly the share of its `AvailablePower` that downstream loads pulled this tick; `UsePower(OutputNetwork, EnergyUsed)` then drains the battery to cover that share.

So the call chain on each side is:

- **InputNetwork tick**: `Initialise` -> `APC.GetUsedPower(InputNetwork)` adds to Required. `ApplyState` -> `ConsumePower` -> `APC.ReceivePower(InputNetwork, num2)` settles `_powerProvided` and charges the battery if there is surplus.
- **OutputNetwork tick**: `Initialise` -> `APC.GetGeneratedPower(OutputNetwork)` adds to Potential (and creates the PowerProvider). `ApplyState` -> downstream `device.ReceivePower(OutputNetwork, num2)` decrements the APC PowerProvider's `Energy`. Then `Providers[i].ApplyPower()` -> `APC.UsePower(OutputNetwork, EnergyUsed)` -- battery drains here.

Conclusion: **`UsePower` is the only method that decrements `Battery.PowerStored` on the output side. Battery drain into a downstream network goes through `UsePower`, not `GetUsedPower`. `GetUsedPower` is a query that returns a value; mutating the battery from inside `GetUsedPower` would double-debit (because `GetUsedPower` is called twice per tick on the InputNetwork inside `Initialise` and `ApplyState`).**

## BatteryChargeRate and net throughput
<!-- verified: 0.2.6228.27061 @ 2026-05-26 -->

The APC declares one tunable wattage field:

```csharp
[Header("Area Power Control")]
[Tooltip("How many watts are used to charge the battery?")]
public float BatteryChargeRate = 1000f;
```

(decompile line 369540)

This is the only numeric cap inside `AreaPowerControl`. It is a watts (per-second) limit, and it applies only to charging the APC's INTERNAL `BatteryCell` from upstream surplus. Both call sites verify the gate:

- `ReceivePower` clamps the per-tick charge delta to `Min(Battery.PowerDelta, BatteryChargeRate, powerAdded)` (line 369975).
- `GetUsedPower` adds `Min(BatteryChargeRate, Battery.PowerDelta)` to the input-side reported load when the battery is not full (line 369995).

What `BatteryChargeRate` does NOT cap:

- Network-to-network throughput. The APC's input-side `GetUsedPower` returns `Mathf.Max(_powerProvided, UsedPower)` for the downstream consumption portion (line 369991), with no upper bound. Whatever the output-side devices demand this tick, the APC requests that much from the input network plus the (capped) charge demand.
- Output-side discharge. `GetGeneratedPower(OutputNetwork)` returns the full `AvailablePower` = `InputNetwork.PotentialLoad + Battery.PowerStored` (line 369606-369617), with no rate cap. The battery can dump its entire `PowerStored` in a single tick if downstream demand and the cable-network limits permit.

Net effect: the APC is effectively transparent for network-to-network power transfer. The 1000 W field name "BatteryChargeRate" is literal: it gates only the internal cell charging, not the bridge throughput.

PowerGridPlus does NOT modify `BatteryChargeRate` and does NOT introduce a net-throughput cap on the APC. Its only `AreaPowerControl` patch is `UsePowerPatch` (battery-drain settlement, see next section). The `BatteryChargeRate` field stays at its vanilla 1000 W under the mod.

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
<!-- verified: 0.2.6228.27061 @ 2026-05-28 -->

The `_powerProvided` ledger field that drives the net-charge ceiling above is not unique to `AreaPowerControl`. Four vanilla classes carry a `_powerProvided` field (`grep "_powerProvided"` over the decompile returns these line ranges):

| Class | Decompile line | Has internal energy storage? | Patched by PowerGridPlus? |
|---|---|---|---|
| `AreaPowerControl` | 369546 | Yes (`BatteryCell` in slot 0) | yes -- `AreaPowerControlPatches.cs` |
| `Transformer` | 403323 | No | yes -- `TransformerExploitPatches.cs` |
| `PowerReceiver` | 386867 | No | no |
| `PowerTransmitter` | 387083 | No | no |

Only `AreaPowerControl` couples `_powerProvided` to an energy store. The other three use the ledger purely to gate request math: vanilla `Transformer.GetUsedPower` returns `Min((float)Setting + UsedPower, _powerProvided)` (line 403493) and vanilla `PowerReceiver.GetUsedPower` returns `Min(PowerTransmitter.MaxPowerTransmission + UsedPower, _powerProvided)` (line 387025); both use `_powerProvided` as an upper bound on what to request from upstream, not as a debit to drain from a buffer. With no storage to mis-drain, the APC's net-charge asymmetry has no analogue in those classes.

Verbatim vanilla `Transformer` power-tick methods (lines 403459-403507):

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

`PowerReceiver` (wireless RX, lines 386980-387025) and `PowerTransmitter` (wireless TX, lines 387220-387280) follow the same shape as Transformer: ledger-only, no storage. PowerGridPlus does not patch either of them; they retain full vanilla behaviour and are out of scope of the APC charge-ceiling bug.

Station-mounted `Battery` (documented in [Battery.md](./Battery.md)) is the other class with internal energy storage, but it does NOT carry a `_powerProvided` field. Its vanilla `UsePower` / `ReceivePower` write directly into `PowerStored` without a ledger (decompile lines 371098-371138). PGP's `StationaryBatteryPatches.cs` adds per-tick caps on `GetUsedPower` (charge headroom) and `GetGeneratedPower` (discharge availability) and rewrites `ReceivePower` to apply `BatteryChargeEfficiency`, but does NOT touch `UsePower`. There is no `_powerProvided > 0` gate to remove and no equivalent immediate-vs-deferred settlement issue.

The station `Battery` does carry an intentional asymmetry of its own under PowerGridPlus defaults (`MaxBatteryChargeRate = 0.002`, `MaxBatteryDischargeRate = 0.007` -- discharge cap is 3.5x the charge cap), so a Battery whose continuous downstream draw equals its discharge cap will slowly net-drain rather than hold equilibrium. That is a design choice (batteries are a reserve, not a generator) and is exposed as a tunable in the BepInEx config; it is not a bug surface comparable to the APC ceiling, where the asymmetry is fixed at the literal `BatteryChargeRate = 1000f` field and the player has no config to retune it.

Net conclusion: the net-charge ceiling is localised to `AreaPowerControl`. The proximate cause -- PGP's `UsePower` patch removing vanilla's `_powerProvided > 0` drain gate -- does not appear in any other PGP-patched class. The other `_powerProvided` users either have no `UsePower` patch (Transformer, by Re-Volt design) or are unpatched entirely (PowerReceiver, PowerTransmitter), and the only other storage-bearing electrical class (`Battery`) uses a different mechanism without a ledger.

## Verification history
<!-- verified: 0.2.6228.27061 @ 2026-05-28 -->

- 2026-05-28 (later): added "PGP post-b3baffb (2026-05-28): cable-headroom and cable-tier output cap" section reflecting commit `b3baffb` and reframed the prior "Net-charge ceiling" section as historical (the `UsePowerPatch` that produced the ceiling is removed; vanilla `UsePower` now runs unmodified). The earlier analysis is preserved verbatim under a HISTORICAL heading because it explains why the bug existed and what the rewrite addressed. Verified headlessly via `pgp-rate-cap-probe` ScenarioRunner scenario: 8/8 wired APCs on `APC-Luna.save` pass `ComputeChargeCap` (per-device, cable-bounded) and `GetGeneratedPower` (output cable MaxVoltage cap) checks across normal and heavy cable tiers.
- 2026-05-28: added "Pattern presence in other ledger-based classes" section. Sweeps the four vanilla classes that carry a `_powerProvided` field (decompile lines 369546, 403323, 386867, 387083) and confirms that the APC's net-charge ceiling bug pattern is structurally impossible in `Transformer`, `PowerReceiver`, `PowerTransmitter`, and the station-mounted `Battery`: the other three `_powerProvided` users have no internal energy storage to mis-drain (vanilla `Transformer.UsePower` only increments the ledger; lines 403459-403465), and the only other storage-bearing electrical class (`Battery`) has no `_powerProvided` ledger and is not patched on `UsePower` by PowerGridPlus. Documents PGP's TransformerExploitPatches verbatim and confirms it does not patch `UsePower`. Notes the station `Battery`'s intentional charge/discharge asymmetry under PGP defaults (`0.002` / `0.007`) is a separate, configurable design choice and not the same bug shape. Additive content; does not contradict prior sections.
- 2026-05-28: added "Net-charge ceiling under PowerGridPlus" section. Documents the steady-state consequence of PGP's three-patch APC rewrite (`AreaPowerControlPatches.cs`, gated on `EnableAreaPowerControlFix`): the internal cell's net charge per tick is `BatteryChargeRate - downstreamDemand`, so the cell stops accumulating once continuous downstream load reaches `BatteryChargeRate` (1000). Derived the crossover is independent of `UsedPower` (it cancels between `GetUsedPower`'s `+u` and `ReceivePower`'s `-u`). Confirmed vanilla does NOT show the ceiling (vanilla `UsePower` drain gate `_powerProvided > 0` never fires under surplus input, so the cell fills regardless of load). Corroborated by a developer's in-game observation on the APC-Luna save: "the APC only charges when load is below ~1 kW." Additive content; refines but does not contradict the "BatteryChargeRate and net throughput" and "Power Grid Plus deviation" sections. Full InspectorPlus before/after capture on a dedicated server still pending.
- 2026-05-26: added "BatteryChargeRate and net throughput" section. Documents the literal field value (1000f, line 369540), its two call sites in `ReceivePower` and `GetUsedPower`, and the explicit conclusion that this cap applies only to internal-cell charging and NOT to network-to-network throughput (`GetGeneratedPower` returns full `AvailablePower` uncapped; `_powerProvided` term in `GetUsedPower` is unbounded). Additive content; does not contradict prior sections.
- 2026-05-22: page created during PowerGridPlus pre-release verification task 6 (APC patch correctness). Source: Re-Volt 1.4.0 `AreaPowerControllerPatches.cs` (MIT, (c) 2025 Sukasa) cross-checked against Stationeers 0.2.6228.27061 `AreaPowerControl` decompile (lines 369509-370046) and `PowerTick` decompile (lines 254512-254765). Verdict: PGP's retargeting of `UsePowerPatch` from `GetUsedPower` to `UsePower` is structurally correct (the patch body only matches `UsePower`'s signature, and `UsePower` is the only method that decrements `Battery.PowerStored` in the output-side call chain). Re-Volt's original attribution is a silent no-op at HarmonyX `PatchAll` time. Dynamic verification on a dedicated-server test save pending.

## Open questions
<!-- verified: 0.2.6228.27061 @ 2026-05-22 -->

- Does HarmonyX's parameter-mismatch handling differ across HarmonyX versions, such that Re-Volt's misattributed `UsePowerPatch` could apply as a prefix on `GetUsedPower` (with `powerUsed` bound to 0)? The static-analysis verdict above assumes the patch is rejected at `PatchAll` time. If instead the patch applies with a default-bound `powerUsed`, the `____powerProvided += powerUsed` becomes `+= 0` (no-op) and `Min(Battery.PowerStored, 0) = 0` (no-op) -- behaviourally still silent. Either failure mode produces the same observable end state, so the verdict does not change.
