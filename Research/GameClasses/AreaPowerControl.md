---
title: AreaPowerControl
type: GameClasses
created_in: 0.2.6228.27061
verified_in: 0.2.6228.27061
verified_at: 2026-05-22
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

## Verification history
<!-- verified: 0.2.6228.27061 @ 2026-05-22 -->

- 2026-05-22: page created during PowerGridPlus pre-release verification task 6 (APC patch correctness). Source: Re-Volt 1.4.0 `AreaPowerControllerPatches.cs` (MIT, (c) 2025 Sukasa) cross-checked against Stationeers 0.2.6228.27061 `AreaPowerControl` decompile (lines 369509-370046) and `PowerTick` decompile (lines 254512-254765). Verdict: PGP's retargeting of `UsePowerPatch` from `GetUsedPower` to `UsePower` is structurally correct (the patch body only matches `UsePower`'s signature, and `UsePower` is the only method that decrements `Battery.PowerStored` in the output-side call chain). Re-Volt's original attribution is a silent no-op at HarmonyX `PatchAll` time. Dynamic verification on a dedicated-server test save pending.

## Open questions
<!-- verified: 0.2.6228.27061 @ 2026-05-22 -->

- Does HarmonyX's parameter-mismatch handling differ across HarmonyX versions, such that Re-Volt's misattributed `UsePowerPatch` could apply as a prefix on `GetUsedPower` (with `powerUsed` bound to 0)? The static-analysis verdict above assumes the patch is rejected at `PatchAll` time. If instead the patch applies with a default-bound `powerUsed`, the `____powerProvided += powerUsed` becomes `+= 0` (no-op) and `Min(Battery.PowerStored, 0) = 0` (no-op) -- behaviourally still silent. Either failure mode produces the same observable end state, so the verdict does not change.
