---
title: PowerTick
type: GameClasses
created_in: 0.2.6228.27061
verified_in: 0.2.6228.27061
verified_at: 2026-06-29
sources:
  - $(StationeersPath)\rocketstation_Data\Managed\Assembly-CSharp.dll :: Assets.Scripts.Networks.PowerTick
  - .work/decomp/0.2.6228.27061/Assembly-CSharp.decompiled.cs :: lines 254512-254760 (PowerTick), 254484-254511 (PowerProvider), 253668-253681 (CableNetwork.OnPowerTick), 254610-254613 (CheckForRecursiveProviders), 254656 (CacheState), 350696-350724 (Device base query/settle methods)
  - Mods/PowerGridPlus/RESEARCH.md (voltage-tier research); .work/revolt-source/Assets/Scripts/RevoltTick.cs (RevoltTick : PowerTick); Mods/PowerGridPlus (PowerGridTick : PowerTick, reverse-patches CacheState + CheckForRecursiveProviders)
related:
  - ./CableNetwork.md
  - ./Cable.md
  - ./ElectricityManager.md
  - ./ElectricalInputOutput.md
  - ./Device.md
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
        {
            if (!device.Powered)
                device.SetPowerFromThread(CableNetwork, hasPower: true).Forget();
        }
        else if (device.AllowSetPower(CableNetwork) && device.Powered)
        {
            device.SetPowerFromThread(CableNetwork, hasPower: false).Forget();
        }
    }
    int num = Providers.Length;
    while (num-- > 0)
    {
        Providers[num]?.ApplyPower();
    }
}
```

Vanilla cable burn is **instantaneous** and **per-cable**: any cable whose `MaxVoltage < _actual` (where `_actual = min(Potential, Required)`) is a burn candidate this tick; `BreakableCables.Pick()` picks one at random and `Break()`s it (after a fuse, if any fuse's `PowerBreak < _actual`, blows first). Re-Volt replaces this whole class (`RevoltTick : PowerTick`) with a probabilistic, sliding-window model. A mod that wants e.g. heavy cables to never burn must intercept `GetBreakableCables` (private; `AccessTools.Method(typeof(PowerTick), "GetBreakableCables")`) or `BreakSingleCable`, or guard `Cable.Break()` -- and must account for Re-Volt having swapped the instance if both are installed.

`CalculateState` calls a private `CheckForRecursiveProviders()` (declared at decompile line 254613, called at line 254610) -- this is the "force-burn cables when the grid loops through multiple transformers/batteries" check that walks `_networkTraversalRecord`. `CacheState()` is private at line 254656. Both are private instance methods on `PowerTick`; a subclass-replacement tick that wants the vanilla behaviour back can reach them via Harmony reverse patches (`[HarmonyReversePatch, HarmonyPatch("CacheState")] static void CacheState(PowerTick _)`), which is what Re-Volt and Power Grid Plus do for `CacheState` (always) and `CheckForRecursiveProviders` (only when the recursive-network-limits option is on).

## The query/settle split: GetGeneratedPower/GetUsedPower are pure reads; ReceivePower/UsePower are the mutations
<!-- verified: 0.2.6228.27061 @ 2026-06-29 -->

The four `Device` power methods divide cleanly into two reads and two writes, and the tick uses them in two distinct phases:

- **Queries (called by `CalculateState` AND again at the top of the `ApplyState` device loop):** `device.GetGeneratedPower(network)` and `device.GetUsedPower(network)`. These must be side-effect-free with respect to the power ledger. `CalculateState` calls each once per device to build the running `Potential` / `Required` sums and the `Providers[]` array; `ApplyState` re-reads `GetUsedPower` per device to know how much to actually pull. A device returns `-1f` from either when the queried network is not its own `PowerCable.CableNetwork` (the sentinel that means "I am not on this network"; see [Device](./Device.md)). On a bridge device the query reads a debt/store field (`Transformer`/`PowerTransmitter`/`PowerReceiver`/`AreaPowerControl` read `_powerProvided`; `Battery` reads `PowerStored`), so the value already encodes prior-tick state -- the query stays a read, but the field it reads was written by last tick's settle. (A few processing machines read `_powerUsedDuringTick`, which is set during their own work loop and reset in `ReceivePower`; still a read at query time. See [Device](./Device.md), "Two per-device draw-state fields".)
- **Settles (called only inside `ApplyState`):** `device.ReceivePower(network, amount)` is the CONSUMER settle (the device just received `amount` watts), called from `ConsumePower`. `device.UsePower(network, amount)` is the PRODUCER settle (the device just supplied `amount` watts), called from `PowerProvider.ApplyPower`. These are where the mutation happens: a battery moves `PowerStored`, a transformer/transmitter moves `_powerProvided`, a processing machine resets `_powerUsedDuringTick`. The base `Device` makes both no-ops (decompile lines 350718-350724); only devices that store or bridge power override them.

So one network's `ApplyState` runs both settles: consumers via `ConsumePower -> ReceivePower`, then producers via the final `Providers[].ApplyPower -> UsePower` loop. The mutation a settle performs on a BRIDGE device (`_powerProvided`) is what the OTHER network's query reads on a later tick -- and which network is "later" is pool-slot order, not topology (see [ElectricityManager](./ElectricityManager.md)). This read-now / write-now / read-elsewhere-next-tick structure is the mechanism behind every cross-network one-tick lag in the vanilla model.

## CacheState: strict power-met, brownout ratio, and `_actual`
<!-- verified: 0.2.6228.27061 @ 2026-06-29 -->

`CacheState` (decompile lines 254656-254669, excerpted verbatim above) derives three values from the summed `Potential` and `Required`:

- `_isPowerMet = (Potential - Required) > 0f` is **strict**: potential must EXCEED required, not merely equal it. A network with `Potential == Required` exactly reports `_isPowerMet == false` and runs in brownout. This matters because `_isPowerMet` is the primary gate in the `ApplyState` device loop for marking a device `Powered`: a device on a network that exactly meets its load is not flagged powered unless it is itself a provider (`device.IsPowerProvider && _powerRatio > 0f`). A mod computing "is this network fully supplied" must use `>` to match vanilla, not `>=`.
- `_powerRatio = Clamp(_isPowerMet ? 1f : Potential / Required, 0f, 1f)` when both `Potential > 0` and `Required > 0`, else `1f`. This is the **brownout factor**. In `ApplyState` every device's draw is scaled by it: `usedPower *= _powerRatio` (line 254742) before `ConsumePower` pulls. So under-supply does not black out the network; it linearly de-rates every consumer's draw by the supply/demand ratio. A network supplying 60% of its demand runs every device at 60% draw. (Note the empty-network case `_powerRatio = 1f`: a network with no generation AND no load has ratio 1, harmless because the device loop's `usedPower > 0f` guard skips it.)
- `_actual = Min(Potential, Required)` is the figure the burn checks compare cable/fuse ratings against (`cable.MaxVoltage < _actual`, `fuse.PowerBreak < _actual`). It is the power that will actually flow this tick (you cannot draw more than is generated, nor more than is demanded).

`CacheState` is called at the top of `ApplyState` and AGAIN inside `BreakSingleFuse` / `BreakSingleCable` after they set `Required = the broken element's rating` (lines 254700-254712), so the post-break `_powerRatio` / `_actual` reflect the reduced network capacity before the device loop runs.

## ConsumePower and PowerProvider.ApplyPower: the two settle loops
<!-- verified: 0.2.6228.27061 @ 2026-06-29 -->

`ApplyState`'s device loop (lines 254730-254754) drives the CONSUMER settle, and the trailing provider loop (lines 254755-254759) drives the PRODUCER settle.

**Consumer settle** -- `ConsumePower(device, network, powerRequired)` (lines 254634-254654) walks `Providers[]` from the end toward 0, pulling `Min(powerRequired, provider.Energy)` from each until demand is met (the single-supplier-first behaviour documented on [CableNetwork](./CableNetwork.md)). For each pull it does `provider.Energy -= num2`, `Consumed += num2`, and `device.ReceivePower(network, num2)`. So the consumer's `ReceivePower` is called once per provider drained, with the slice that provider supplied. The provider's `Energy` is decremented here (this is what records how much each provider gave).

**Producer settle** -- after the device loop, the trailing loop calls `Providers[num]?.ApplyPower()` backward over the whole array. `PowerProvider.ApplyPower` (decompile lines 254504-254510) is:

```csharp
public void ApplyPower()
{
    if (!(Device == null) && !(EnergyUsed <= 0f))
    {
        Device.UsePower(CableNetwork, EnergyUsed);
    }
}
```

where `PowerProvider.EnergyUsed => _originalEnergy - Energy` (line 254494) and `_originalEnergy` was captured at construction as `device.GetGeneratedPower(cableNetwork)` (lines 254500-254501). So `EnergyUsed` is exactly how much of that provider's offered generation was actually drained by the consumer settle above. `ApplyPower` then calls the producer's `UsePower(network, EnergyUsed)` ONCE with that total, which is where a battery decrements `PowerStored`, a transformer increments `_powerProvided` on its output side, a transmitter increments `_powerProvided` on its wireless side, and so on.

The ordering is load-bearing: consumers are settled FIRST (draining `provider.Energy` and calling `ReceivePower`), then each provider's net contribution is settled via a single `UsePower`. A producer therefore learns its total delivery for the tick only after all consumers on the network have pulled. This is why a bridge device's input-side draw (reported next tick from `_powerProvided`) lags its output-side delivery: the `UsePower` that grows `_powerProvided` runs at the END of the output network's tick, and the input network may already have been ticked earlier this same tick (pool-slot order again).

## CalculateState accumulates into Required/Potential; only Initialise resets them
<!-- verified: 0.2.6228.27061 @ 2026-06-17 -->

`CalculateState` ADDS to the running sums (`Required += usedPower` at decompile line 254594, `Potential += generatedPower` at 254599); it never zeroes them itself. The reset lives in `Initialise` (`Potential = 0f; Required = 0f; Consumed = 0f;`, lines 254574-254576). The two are therefore a mandatory pair: every `CalculateState` must be preceded by `Initialise`, or the sums double-count.

This matters for any mod that ticks a network more than once per game tick. PowerGridPlus's atomic tick splits the work into an OBSERVE pass and an ENFORCE pass, and each pass calls `Initialise` then `CalculateState`, so the second pass starts from a clean zero instead of doubling the first. `ApplyState` is different: it has real side effects (it drains `Providers[].Energy` and calls `Device.ReceivePower` via `ConsumePower`, lines 254634-254654 -- which mutates a transformer's `_powerProvided` ledger -- sets `Device.Powered`, and may `Break()` a fuse or cable), so it is run only once per game tick, in the enforce pass. Re-running `ApplyState` a second time would double-drain providers and re-trigger the break checks.

## Verification history

- 2026-05-12: page created. Sourced from a voltage-tier research dive (planned mod "Power Grid Plus") into `.work/decomp/0.2.6228.27061/Assembly-CSharp.decompiled.cs` lines 254512-254760 and 253668-253681; verbatim excerpts of the `PowerTick` field block, `CableNetwork.OnPowerTick`, `CacheState`, `GetBreakableFuses`, `GetBreakableCables`, `BreakSingleFuse`, `BreakSingleCable`, `ApplyState`. Reconciles the class-attribution imprecision on [CableNetwork](./CableNetwork.md) (those `ConsumePower`/`CalculateState` bodies are `PowerTick` members). Re-Volt mod source (`RevoltTick : PowerTick`, reverse-patches `PowerTick.Initialise`/`CalculateState`/`ApplyState`) independently corroborates ownership.
- 2026-06-10: re-read lines 254512-254761 in full against the same 0.2.6228.27061 decompile during the PowerGridPlus single-architecture rework (the mod now runs this vanilla class unmodified in its atomic Phases 1 and 3). All sections still match verbatim; no content change.
- 2026-05-12: while building Power Grid Plus, confirmed `PowerTick.CheckForRecursiveProviders()` is a private instance method (decompile line 254613, called from `CalculateState` at 254610) and `CacheState()` is private at line 254656; added a note that both are reachable via Harmony reverse patches (Power Grid Plus, like Re-Volt, reverse-patches `CacheState` unconditionally and `CheckForRecursiveProviders` when its recursive-network-limits option is on). Also: `PowerTick` and `PowerProvider` are in namespace `Assets.Scripts.Networks`; the `Pick<T>(this List<T>)` / `Pick<T>(this List<T>, System.Random)` extension used by `BreakSingleCable`/`BreakSingleFuse` lives in `Assets.Scripts.Util` (decompile ~line 214049).

- 2026-06-17: added the "CalculateState accumulates; Initialise resets" pairing note. Re-confirmed against the 0.2.6228.27061 decompile: `Initialise` zeroes `Potential`/`Required`/`Consumed` (lines 254574-254576), `CalculateState` uses `+=` (254594, 254599), and `ApplyState`/`ConsumePower` drain `Providers[].Energy` and call `Device.ReceivePower` (254634-254654). Surfaced while explaining PowerGridPlus's two-pass atomic tick (Initialise+CalculateState in Phase 1 OBSERVE and Phase 3 ENFORCE; ApplyState only in Phase 3).
- 2026-06-29: completed the `ApplyState` excerpt (was previously truncated with `...`; now shows the full device loop with `usedPower *= _powerRatio`, the `SetPowerFromThread` powered/unpowered branch, and the trailing `Providers[num]?.ApplyPower()` loop, decompile lines 254717-254760) and added three sections during the vanilla power-model curation pass: "The query/settle split" (GetGeneratedPower/GetUsedPower are pure reads returning the `-1f` off-network sentinel from base Device at lines 350696-350716; ReceivePower is the consumer settle and UsePower the producer settle, both no-ops on base Device at 350718-350724), "CacheState: strict power-met, brownout ratio, and `_actual`" (`_isPowerMet = (Potential - Required) > 0f` is STRICT not `>=`, `_powerRatio` is the brownout de-rate applied as `usedPower *= _powerRatio`, `_actual = Min(Potential, Required)` feeds the burn checks; lines 254656-254669 + 254742), and "ConsumePower and PowerProvider.ApplyPower: the two settle loops" (verbatim `PowerProvider.ApplyPower` at 254504-254510 with `EnergyUsed => _originalEnergy - Energy` at 254494 and `_originalEnergy = device.GetGeneratedPower(...)` captured at construction 254500-254501; consumer settle drains `provider.Energy` and calls `ReceivePower` per drained provider, producer settle calls `UsePower` once with the net `EnergyUsed`). Additive; no prior claim contradicted (the existing `CacheState` and `ConsumePower` verbatim bodies already on the page are unchanged, the new sections explain their semantics). Cross-links the new [ElectricityManager](./ElectricityManager.md) (pool-slot cross-network order) and [ElectricalInputOutput](./ElectricalInputOutput.md) (the bridge base whose `_powerProvided` the settle mutates) pages. Restamped `verified_at` to 2026-06-29.

## Open questions

- The `CableNetwork.md` page should have its prose updated to attribute `ConsumePower` / `CalculateState` / the provider iteration to `PowerTick`; deferred to a future reconciliation pass (the behavioral content there is correct).
