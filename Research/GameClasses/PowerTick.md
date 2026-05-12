---
title: PowerTick
type: GameClasses
created_in: 0.2.6228.27061
verified_in: 0.2.6228.27061
verified_at: 2026-05-12
sources:
  - $(StationeersPath)\rocketstation_Data\Managed\Assembly-CSharp.dll :: Assets.Scripts.Networks.PowerTick
  - .work/decomp/0.2.6228.27061/Assembly-CSharp.decompiled.cs :: lines 254512-254760 (PowerTick), 253668-253681 (CableNetwork.OnPowerTick), 254610-254613 (CheckForRecursiveProviders), 254656 (CacheState)
  - Mods/PowerGridPlus/RESEARCH.md (voltage-tier research); .work/revolt-source/Assets/Scripts/RevoltTick.cs (RevoltTick : PowerTick); Mods/PowerGridPlus (PowerGridTick : PowerTick, reverse-patches CacheState + CheckForRecursiveProviders)
related:
  - ./CableNetwork.md
  - ./Cable.md
  - ../GameSystems/PowerTickThreading.md
tags: [power, threading]
---

# PowerTick

Per-`CableNetwork` power-tick worker. `Assets.Scripts.Networks.PowerTick`. Each `CableNetwork` holds one `PowerTick` instance in its `PowerTick` field; `CableNetwork.OnPowerTick()` calls `PowerTick.Initialise -> CalculateState -> ApplyState` once per tick. This is the class Re-Volt subclasses as `RevoltTick : PowerTick` and the class whose `Initialise` / `CalculateState` / `ApplyState` Re-Volt prefix-patches.

> Reconciliation note: [CableNetwork](./CableNetwork.md) currently describes `ConsumePower` / `CalculateState` and the "single-supplier-first" provider iteration as `CableNetwork` members. In version 0.2.6228.27061 those methods live on `PowerTick` (declared at decompile line 254512), held in `CableNetwork.PowerTick`. The line numbers and code bodies on the `CableNetwork` page are correct (that region IS `PowerTick`); only the class attribution is imprecise there. Re-Volt's `RevoltTick : PowerTick` corroborates ownership. A future pass should fix the wording on the `CableNetwork` page to point here.

## Fields
<!-- verified: 0.2.6228.27061 @ 2026-05-12 -->

```csharp
public class PowerTick
{
    public CableNetwork CableNetwork;

    public List<Device> Devices = new List<Device>();
    public List<CableFuse> Fuses = new List<CableFuse>();
    public List<Cable> Cables = new List<Cable>();
    public List<Cable> BreakableCables = new List<Cable>();
    public List<CableFuse> BreakableFuses = new List<CableFuse>();

    public float Potential;
    public float Required;
    public float Consumed;

    private float _powerAvailable;
    private Device _currentDevice;
    private CableFuse _currentFuse;
    private readonly List<PowerProvider> _providers = new List<PowerProvider>();
    private readonly List<PowerProvider> _inputOutputDevices = new List<PowerProvider>();
    private List<long> _networkTraversalRecord = new List<long>(Device.MaxProviderRecursionIterations);
    private float _netPower;
    private bool _isPowerMet;
    private float _powerRatio;
    private float _actual;

    public PowerProvider[] Providers { get; private set; }
    public PowerProvider[] InputOutputDevices { get; private set; }
    ...
}
```

## CableNetwork.OnPowerTick drives it
<!-- verified: 0.2.6228.27061 @ 2026-05-12 -->

```csharp
public virtual void OnPowerTick()   // on CableNetwork
{
    if (DeviceList.Count != 0)
    {
        PowerTick.Initialise(this);
        PowerTick.CalculateState();
        PowerTick.ApplyState();
        DuringTickLoad = 0f;
        RequiredLoad = PowerTick.Required;
        CurrentLoad = PowerTick.Consumed;
        PotentialLoad = PowerTick.Potential;
        ShortfallLoad = ((PowerTick.Required > PowerTick.Potential) ? (PowerTick.Required - PowerTick.Potential) : 0f);
    }
}
```

Runs on the UniTask ThreadPool worker (see [PowerTickThreading](../GameSystems/PowerTickThreading.md)): managed-memory reads (`cable.MaxVoltage`, `cable.CableType`, list iteration) are safe off-thread; Unity-API calls crash. `Cable.Break()` is reached from here and self-marshals to the main thread.

## State calculation and the break checks
<!-- verified: 0.2.6228.27061 @ 2026-05-12 -->

`Initialise` clears and refills `Devices` / `Fuses` / `Cables` from the network's `PowerDeviceList` / `FuseList` (under `lock`) and the cable list. `CalculateState` builds the `Providers` array (reverse-walks `Devices`, summing `Required` from `GetUsedPower` and `Potential` from `GetGeneratedPower`; provider iteration order documented on [CableNetwork](./CableNetwork.md)). Then `ApplyState`:

```csharp
private void CacheState()
{
    _netPower = Potential - Required;
    _isPowerMet = _netPower > 0f;
    if (Potential > 0f && Required > 0f)
        _powerRatio = Mathf.Clamp(_isPowerMet ? 1f : (Potential / Required), 0f, 1f);
    else
        _powerRatio = 1f;
    _actual = Mathf.Min(Potential, Required);
}

private void GetBreakableFuses()
{
    for (int i = 0; i < Fuses.Count; i++)
    {
        CableFuse cableFuse = Fuses[i];
        if (!(cableFuse == null) && cableFuse.PowerBreak < _actual)
            BreakableFuses.Add(cableFuse);
    }
}

private void GetBreakableCables()
{
    for (int i = 0; i < Cables.Count; i++)
    {
        Cable cable = Cables[i];
        if (!(cable == null) && cable.MaxVoltage < _actual)
            BreakableCables.Add(cable);
    }
}

private void BreakSingleFuse()
{
    CableFuse cableFuse = BreakableFuses.Pick();
    if (cableFuse != null)
    {
        Required = cableFuse.PowerBreak;
        CacheState();
        cableFuse.Break();
    }
}

private void BreakSingleCable()
{
    Cable cable = BreakableCables.Pick();
    if (cable != null)
    {
        Required = cable.MaxVoltage;
        CacheState();
        cable.Break();
    }
}

public void ApplyState()
{
    CacheState();
    GetBreakableFuses();
    if (BreakableFuses.Count > 0)
        BreakSingleFuse();
    GetBreakableCables();
    if (BreakableCables.Count > 0)
        BreakSingleCable();
    for (int i = 0; i < Devices.Count; i++)
    {
        Device device = Devices[i];
        if (device == null) continue;
        float usedPower = device.GetUsedPower(CableNetwork);
        if (usedPower < 0f) continue;
        usedPower *= _powerRatio;
        if (usedPower > 0f && ConsumePower(device, CableNetwork, usedPower) && (_isPowerMet || (device.IsPowerProvider && _powerRatio > 0f)))
        { ... device gets power ... }
        ...
    }
}
```

Vanilla cable burn is **instantaneous** and **per-cable**: any cable whose `MaxVoltage < _actual` (where `_actual = min(Potential, Required)`) is a burn candidate this tick; `BreakableCables.Pick()` picks one at random and `Break()`s it (after a fuse, if any fuse's `PowerBreak < _actual`, blows first). Re-Volt replaces this whole class (`RevoltTick : PowerTick`) with a probabilistic, sliding-window model. A mod that wants e.g. heavy cables to never burn must intercept `GetBreakableCables` (private; `AccessTools.Method(typeof(PowerTick), "GetBreakableCables")`) or `BreakSingleCable`, or guard `Cable.Break()` -- and must account for Re-Volt having swapped the instance if both are installed.

`CalculateState` calls a private `CheckForRecursiveProviders()` (declared at decompile line 254613, called at line 254610) -- this is the "force-burn cables when the grid loops through multiple transformers/batteries" check that walks `_networkTraversalRecord`. `CacheState()` is private at line 254656. Both are private instance methods on `PowerTick`; a subclass-replacement tick that wants the vanilla behaviour back can reach them via Harmony reverse patches (`[HarmonyReversePatch, HarmonyPatch("CacheState")] static void CacheState(PowerTick _)`), which is what Re-Volt and Power Grid Plus do for `CacheState` (always) and `CheckForRecursiveProviders` (only when the recursive-network-limits option is on).

## Verification history

- 2026-05-12: page created. Sourced from a voltage-tier research dive (planned mod "Power Grid Plus") into `.work/decomp/0.2.6228.27061/Assembly-CSharp.decompiled.cs` lines 254512-254760 and 253668-253681; verbatim excerpts of the `PowerTick` field block, `CableNetwork.OnPowerTick`, `CacheState`, `GetBreakableFuses`, `GetBreakableCables`, `BreakSingleFuse`, `BreakSingleCable`, `ApplyState`. Reconciles the class-attribution imprecision on [CableNetwork](./CableNetwork.md) (those `ConsumePower`/`CalculateState` bodies are `PowerTick` members). Re-Volt mod source (`RevoltTick : PowerTick`, reverse-patches `PowerTick.Initialise`/`CalculateState`/`ApplyState`) independently corroborates ownership.
- 2026-05-12: while building Power Grid Plus, confirmed `PowerTick.CheckForRecursiveProviders()` is a private instance method (decompile line 254613, called from `CalculateState` at 254610) and `CacheState()` is private at line 254656; added a note that both are reachable via Harmony reverse patches (Power Grid Plus, like Re-Volt, reverse-patches `CacheState` unconditionally and `CheckForRecursiveProviders` when its recursive-network-limits option is on). Also: `PowerTick` and `PowerProvider` are in namespace `Assets.Scripts.Networks`; the `Pick<T>(this List<T>)` / `Pick<T>(this List<T>, System.Random)` extension used by `BreakSingleCable`/`BreakSingleFuse` lives in `Assets.Scripts.Util` (decompile ~line 214049).

## Open questions

- The `CableNetwork.md` page should have its prose updated to attribute `ConsumePower` / `CalculateState` / the provider iteration to `PowerTick`; deferred to a future reconciliation pass (the behavioral content there is correct).
