---
title: PowerTick
type: GameClasses
created_in: 0.2.6228.27061
verified_in: 0.2.6403.27689
verified_at: 2026-07-02
sources:
  - $(StationeersPath)\rocketstation_Data\Managed\Assembly-CSharp.dll :: Assets.Scripts.Networks.PowerTick
  - .work/decomp/0.2.6228.27061/Assembly-CSharp.decompiled.cs :: lines 254512-254760 (PowerTick), 254484-254511 (PowerProvider), 253668-253681 (CableNetwork.OnPowerTick), 254610-254613 (CheckForRecursiveProviders), 254656 (CacheState), 350696-350724 (Device base query/settle methods)
  - .work/decomp/0.2.6403.27689/Assembly-CSharp.decompiled.cs :: lines 271698-271947 (PowerTick), 271676-271697 (PowerProvider), 270827-270840 (CableNetwork.OnPowerTick), 270617 (CableNetwork.PowerTick field), 230535-230542 (Pick extension), 371501-371534 (Device base query/settle methods)
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

Per-`CableNetwork` power-tick worker. `Assets.Scripts.Networks.PowerTick` (class at decompile line 271698 in the 0.2.6403.27689 decompile). Each `CableNetwork` holds exactly one `PowerTick` instance for its whole lifetime: `public readonly PowerTick PowerTick = new PowerTick();` (line 270617; initialized once at field level, `PowerTick` has no constructor). `CableNetwork.OnPowerTick()` calls `PowerTick.Initialise -> CalculateState -> ApplyState` once per tick. This is the class Re-Volt subclasses as `RevoltTick : PowerTick` and the class whose `Initialise` / `CalculateState` / `ApplyState` Re-Volt prefix-patches.

> Reconciliation note: [CableNetwork](./CableNetwork.md) currently describes `ConsumePower` / `CalculateState` and the "single-supplier-first" provider iteration as `CableNetwork` members. Those methods live on `PowerTick`, held in `CableNetwork.PowerTick`. The code bodies on the `CableNetwork` page are correct (that region IS `PowerTick`); only the class attribution is imprecise there. Re-Volt's `RevoltTick : PowerTick` corroborates ownership. A future pass should fix the wording on the `CableNetwork` page to point here.

## Fields
<!-- verified: 0.2.6403.27689 @ 2026-07-02 -->

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

Field declarations at 0.2.6403.27689: class 271698, `BreakableCables` 271708, `BreakableFuses` 271710, `_powerAvailable` 271718. `_powerAvailable` is a DEAD field: it is declared and never referenced anywhere else in the whole decompile (single grep hit at 271718). See "BreakableFuses / BreakableCables are append-only accumulators" below for the lifetime semantics of the two Breakable lists.

## CableNetwork.OnPowerTick drives it
<!-- verified: 0.2.6403.27689 @ 2026-07-02 -->

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

### Load mirrors are written at the END of the tick: cross-network advertise is one-tick lagged, and a dead island cannot bootstrap
<!-- verified: 0.2.6403.27689 @ 2026-07-02 -->

The body above (decompile 270827-270840) writes `RequiredLoad` / `CurrentLoad` / `PotentialLoad` / `ShortfallLoad` only AFTER `ApplyState` has finished, i.e. at the very end of the network's own tick. Those four floats are the ONLY values other networks' devices can read about this network: `PowerReceiver.GetGeneratedPower` returns `WirelessInputNetwork.PotentialLoad`, `ElectricalInputOutput.PotentialLoad` / `CurrentLoad` / `AvailablePower` forward to `InputNetwork.PotentialLoad` / `OutputNetwork.CurrentLoad`, `Transformer.GetGeneratedPower` clamps against `InputNetwork.PotentialLoad`, `AreaPowerControl.AvailablePower` adds `InputNetwork.PotentialLoad` to its cell. So cross-network advertisement is one-tick lagged by construction:

- If network A ticks BEFORE network B this game tick (pool-slot order, see [ElectricityManager](./ElectricityManager.md)), B's devices read A's value from THIS game tick.
- If A ticks AFTER B, B's devices read A's value from the PREVIOUS game tick.
- Either way a device can never see a mid-tick value, and a bridge chain propagates the advertisement at one network-hop per tick at worst.

**Dead-island corollary.** A network (or island of bridged networks) whose every generator currently reports 0 potential can never bootstrap itself under vanilla relaxation: its `PotentialLoad` mirror is 0, every bridge reading that mirror advertises 0 onward, `CacheState` computes `_isPowerMet == false` with `_powerRatio == 1` but zero `Providers`, and the `ApplyState` device loop un-powers every device that demands anything (or nothing; see the AllowSetPower section below). There is no term in `Initialise` / `CalculateState` / `ApplyState` that can raise `Potential` above the sum of the devices' own `GetGeneratedPower` returns, so 0 stays 0 until something outside the relaxation changes device state: stored energy (a battery / APC cell), a generator whose output does not depend on being powered (solar panels once re-aimed, wind), or a bridge from a live network. The concrete solar case (panels parked off-sun powering their own tracker logic) is worked through on [SolarPanel](./SolarPanel.md), "Efficiency recompute cadence, and the solar-only island bootstrap corollary".

## State calculation and the break checks
<!-- verified: 0.2.6403.27689 @ 2026-07-02 -->

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

Vanilla cable burn is **instantaneous** and **per-cable**: any cable whose `MaxVoltage < _actual` (where `_actual = min(Potential, Required)`) qualifies against this tick's `_actual`, but the qualification is only the ADD criterion; the list `Pick()` draws from is cumulative across ticks (see the accumulator subsection below). `BreakableCables.Pick()` picks one entry at random and `Break()`s it (after a fuse, if `BreakableFuses` is non-empty, blows first). Re-Volt replaces this whole class (`RevoltTick : PowerTick`) with a probabilistic, sliding-window model. A mod that wants e.g. heavy cables to never burn must intercept `GetBreakableCables` (private; `AccessTools.Method(typeof(PowerTick), "GetBreakableCables")`) or `BreakSingleCable`, or guard `Cable.Break()` -- and must account for Re-Volt having swapped the instance if both are installed.

`CalculateState` calls a private `CheckForRecursiveProviders()` (declared at decompile line 271799, called at line 271796) -- this is the "force-burn cables when the grid loops through multiple transformers/batteries" check that walks `_networkTraversalRecord`. `CacheState()` is private at line 271842. Both are private instance methods on `PowerTick` (`CalculateState` itself is PUBLIC, line 271765, as are `Initialise` and `ApplyState`); a subclass-replacement tick that wants the vanilla behaviour back can reach the private ones via Harmony reverse patches (`[HarmonyReversePatch, HarmonyPatch("CacheState")] static void CacheState(PowerTick _)`), which is what Re-Volt and Power Grid Plus do for `CacheState` (always) and `CheckForRecursiveProviders` (only when the recursive-network-limits option is on).

### BreakableFuses / BreakableCables are append-only accumulators for the PowerTick instance lifetime
<!-- verified: 0.2.6403.27689 @ 2026-07-02 -->

Fresh-validator verdict (2026-07-02, binding): the two Breakable lists are never cleared. One `readonly PowerTick` lives per `CableNetwork` for the network's whole life (`public readonly PowerTick PowerTick = new PowerTick();`, CableNetwork line 270617), and the complete reference census over the 0.2.6403.27689 decompile is:

- Declarations: `BreakableCables` 271708, `BreakableFuses` 271710. Initialized once at field level; the class has no constructor.
- Adds: 271809 (fuse) and 271813 (cable) in `CheckForRecursiveProviders` (a recursive-provider hit pre-adds ONE fuse from the looping device's `PowerCableNetwork.FuseList`, or that device's own `PowerCable`, before `ApplyState` even runs its threshold scans); 271864 in `GetBreakableFuses`; 271876 in `GetBreakableCables`. Neither Get method clears the list first.
- Reads: `Pick()` at 271883 / 271894 (null-guarded at 271884 / 271895), `Count` gates in `ApplyState` at 271907 / 271912.
- `Pick<T>(this List<T>)` (230535-230542) returns a random element WITHOUT removing it.
- `Initialise` (271742-271763) clears only `Devices` / `Fuses` / `Cables` and zeroes only `Potential` / `Required` / `Consumed`.
- There is NO `.Clear()`, `.Remove()`, or reassignment of either Breakable list anywhere.

Consequences: entries accumulate across ticks for the life of the PowerTick instance. A cable that qualified as breakable during one overload tick stays in the pick pool on every later tick, and duplicate entries from repeated qualifying ticks skew the random pick toward chronically-overloaded elements. Once either list is non-empty, `ApplyState`'s `Count > 0` gate fires EVERY tick and breaks one random entry per tick until the entries die.

Caveat that shortens the stale window in practice: an actual cable `Break()` destroys the cable, and `Cable.OnDestroy` re-partitions the survivors into NEW `CableNetwork`s (see [Cable](./Cable.md), "Network split on destruction"), each with a fresh `PowerTick` and therefore fresh empty Breakable lists. So on the cable-burn path the stale entries usually die with the old network object. Fuse breaks (the fuse blows; the network keeps its cables and its PowerTick) and non-splitting ticks accumulate without that reset. Stale entries reference destroyed objects; `Pick()` can return such an entry, in which case `BreakSingleCable` / `BreakSingleFuse`'s null guard skips the break for that tick (Unity fake-null makes a destroyed `Cable` compare equal to null) but the entry itself is never removed.

## The query/settle split: GetGeneratedPower/GetUsedPower are pure reads; ReceivePower/UsePower are the mutations
<!-- verified: 0.2.6403.27689 @ 2026-07-02 -->

The four `Device` power methods divide cleanly into two reads and two writes, and the tick uses them in two distinct phases:

- **Queries (called by `CalculateState` AND again at the top of the `ApplyState` device loop):** `device.GetGeneratedPower(network)` and `device.GetUsedPower(network)`. These must be side-effect-free with respect to the power ledger. `CalculateState` calls each once per device to build the running `Potential` / `Required` sums and the `Providers[]` array; `ApplyState` re-reads `GetUsedPower` per device to know how much to actually pull. A device returns `-1f` from either when the queried network is not its own `PowerCable.CableNetwork` (the sentinel that means "I am not on this network"; see [Device](./Device.md)). On a bridge device the query reads a debt/store field (`Transformer`/`PowerTransmitter`/`PowerReceiver`/`AreaPowerControl` read `_powerProvided`; `Battery` reads `PowerStored`), so the value already encodes prior-tick state -- the query stays a read, but the field it reads was written by last tick's settle. (A few processing machines read `_powerUsedDuringTick`, which is set during their own work loop and reset in `ReceivePower`; still a read at query time. See [Device](./Device.md), "Two per-device draw-state fields".)
- **Settles (called only inside `ApplyState`):** `device.ReceivePower(network, amount)` is the CONSUMER settle (the device just received `amount` watts), called from `ConsumePower`. `device.UsePower(network, amount)` is the PRODUCER settle (the device just supplied `amount` watts), called from `PowerProvider.ApplyPower`. These are where the mutation happens: a battery moves `PowerStored`, a transformer/transmitter moves `_powerProvided`, a processing machine resets `_powerUsedDuringTick`. The base `Device` makes both no-ops (decompile lines 371523-371529 at 0.2.6403.27689); only devices that store or bridge power override them.

So one network's `ApplyState` runs both settles: consumers via `ConsumePower -> ReceivePower`, then producers via the final `Providers[].ApplyPower -> UsePower` loop. The mutation a settle performs on a BRIDGE device (`_powerProvided`) is what the OTHER network's query reads on a later tick -- and which network is "later" is pool-slot order, not topology (see [ElectricityManager](./ElectricityManager.md)). This read-now / write-now / read-elsewhere-next-tick structure is the mechanism behind every cross-network one-tick lag in the vanilla model.

## CacheState: strict power-met, brownout ratio, and `_actual`
<!-- verified: 0.2.6403.27689 @ 2026-07-02 -->

`CacheState` (decompile lines 271842-271855, excerpted verbatim above) derives three values from the summed `Potential` and `Required`:

- `_isPowerMet = (Potential - Required) > 0f` is **strict**: potential must EXCEED required, not merely equal it (`_netPower` 271844, `_isPowerMet` 271845). A network with `Potential == Required` exactly reports `_isPowerMet == false` and runs in brownout. This matters because `_isPowerMet` is the primary gate in the `ApplyState` device loop for marking a device `Powered`: a device on a network that exactly meets its load is not flagged powered unless it is itself a provider (`device.IsPowerProvider && _powerRatio > 0f`). A mod computing "is this network fully supplied" must use `>` to match vanilla, not `>=`.
- `_powerRatio = Clamp(_isPowerMet ? 1f : Potential / Required, 0f, 1f)` when both `Potential > 0f` and `Required > 0f`, else `_powerRatio = 1f` (guarded assignment, lines 271846-271853). This is the **brownout factor**. In `ApplyState` every device's draw is scaled by it: `usedPower *= _powerRatio` (line 271928) before `ConsumePower` pulls. So under-supply does not black out the network; it linearly de-rates every consumer's draw by the supply/demand ratio. A network supplying 60% of its demand runs every device at 60% draw. Version note: at 0.2.6228.27061 the assignment was the bare `Clamp(...)` expression with no both-positive guard, so `Potential == 0` with `Required > 0` produced ratio 0 and `0 / 0` produced NaN; the 0.2.6403.27689 guard yields 1f in both corners instead. The `Potential == 0 && Required > 0` flip from 0 to 1 is masked downstream because such a network has no `Providers`, so `ConsumePower` delivers nothing regardless; the guard's practical effect is killing the 0/0 NaN.
- `_actual = Min(Potential, Required)` (line 271854) is the figure the burn checks compare cable/fuse ratings against (`cable.MaxVoltage < _actual`, `fuse.PowerBreak < _actual`). It is the power that will actually flow this tick (you cannot draw more than is generated, nor more than is demanded).

`CacheState` is called at the top of `ApplyState` and AGAIN inside `BreakSingleFuse` / `BreakSingleCable` after they set `Required = the broken element's rating` (lines 271881-271901), so the post-break `_powerRatio` / `_actual` reflect the reduced network capacity before the device loop runs.

## ConsumePower and PowerProvider.ApplyPower: the two settle loops
<!-- verified: 0.2.6403.27689 @ 2026-07-02 -->

`ApplyState`'s device loop (lines 271916-271940) drives the CONSUMER settle, and the trailing provider loop (lines 271941-271945) drives the PRODUCER settle. `ApplyState` spans 271903-271946.

**Consumer settle** -- `ConsumePower(device, network, powerRequired)` (lines 271820-271840) walks `Providers[]` from the end toward 0, pulling `Min(powerRequired, provider.Energy)` from each until demand is met (the single-supplier-first behaviour documented on [CableNetwork](./CableNetwork.md)). For each pull it does `provider.Energy -= num2`, `Consumed += num2`, and `device.ReceivePower(network, num2)`. So the consumer's `ReceivePower` is called once per provider drained, with the slice that provider supplied. The provider's `Energy` is decremented here (this is what records how much each provider gave).

**Producer settle** -- after the device loop, the trailing loop calls `Providers[num]?.ApplyPower()` backward over the whole array. `PowerProvider.ApplyPower` (decompile lines 271690-271696, called once per provider and gated on `EnergyUsed > 0f`) is:

```csharp
public void ApplyPower()
{
    if (!(Device == null) && !(EnergyUsed <= 0f))
    {
        Device.UsePower(CableNetwork, EnergyUsed);
    }
}
```

where `PowerProvider.EnergyUsed => _originalEnergy - Energy` (line 271680) and `_originalEnergy` was captured at construction: the `PowerProvider` constructor calls `device.GetGeneratedPower(cableNetwork)` a second time (after `CalculateState`'s own query) and stores it into both `Energy` and `_originalEnergy` (lines 271686-271687). So `EnergyUsed` is exactly how much of that provider's offered generation was actually drained by the consumer settle above. `ApplyPower` then calls the producer's `UsePower(network, EnergyUsed)` ONCE with that total, which is where a battery decrements `PowerStored`, a transformer increments `_powerProvided` on its output side, a transmitter increments `_powerProvided` on its wireless side, and so on.

The ordering is load-bearing: consumers are settled FIRST (draining `provider.Energy` and calling `ReceivePower`), then each provider's net contribution is settled via a single `UsePower`. A producer therefore learns its total delivery for the tick only after all consumers on the network have pulled. This is why a bridge device's input-side draw (reported next tick from `_powerProvided`) lags its output-side delivery: the `UsePower` that grows `_powerProvided` runs at the END of the output network's tick, and the input network may already have been ticked earlier this same tick (pool-slot order again).

## ApplyState un-powers zero-demand and unfed devices; AllowSetPower picks the network allowed to do it
<!-- verified: 0.2.6403.27689 @ 2026-07-02 -->

Re-reading the `ApplyState` device loop (decompile 271916-271940, excerpted verbatim in "State calculation and the break checks" above), the powered/unpowered decision per device per network-tick is:

- `usedPower < 0f` (the off-network `-1f` sentinel): `continue`. The device's power state is untouched by this network's tick.
- `usedPower > 0f && ConsumePower(...) && (_isPowerMet || (device.IsPowerProvider && _powerRatio > 0f))`: power ON (`SetPowerFromThread(network, true)` when not already `Powered`).
- **Everything else falls into `else if (device.AllowSetPower(CableNetwork) && device.Powered) SetPowerFromThread(network, false)`.** That "everything else" includes a device demanding exactly **0 W**: `usedPower == 0` fails the `> 0f` test, so a zero-demand device on an otherwise healthy network is switched OFF every tick, as is a device whose demand could not be met or that lost the `_isPowerMet` gate.

`Device.AllowSetPower(CableNetwork)` is the ONLY gate standing between that else-branch and the un-power. Base implementation: `PowerCableNetwork == cableNetwork` (Device, decompile 371531-371534; see [Device](./Device.md)), i.e. only the device's own power network may flip it. The bridge classes override it to nominate ONE of their two networks as the authority (whole-decompile override census at 0.2.6403.27689):

| Class | Override (line) | Network allowed to un-power |
|---|---|---|
| `Transformer` | 424748-424755 | `InputNetwork` |
| `AreaPowerControl` | 390991-390998 | `InputNetwork` |
| `PowerTransmitter` | 408415-408422 | `InputNetwork` (its cable side) |
| `PowerReceiver` | 408170-408177 | `WirelessInputNetwork` (the wireless side, NOT its output cable) |

All four nominate the input side: a bridge's `Powered` state is owned by the network that FEEDS it. The output network's tick can still power a bridge ON (the if-branch has no `AllowSetPower` check; a bridge drawing on its output side as a provider passes `device.IsPowerProvider && _powerRatio > 0f`), but only the input-side tick may switch it OFF. Two practical consequences:

- A bridge whose input-side `GetUsedPower` reports 0 (a `Transformer` / `PowerTransmitter` with `_powerProvided == 0` because nothing drew downstream last tick) is un-powered by its input network's tick every tick until downstream demand appears. The vanilla "transmitter pair shows unpowered under zero load" symptom is this branch, not a fault.
- `SetPowerFromThread` (Device, 371648-371652) hops to the main thread and calls `SetPower` -> `OnServer.Interact(InteractPowered, ...)` gated on an actual state change and `GameManager.RunSimulation` (371640-371646), so the flip lands one main-thread frame after the worker-thread decision.

Un-powering also happens OUTSIDE the tick on wiring/state events via the `CheckPower` family (`ElectricalInputOutput.CheckPower` and overrides); see [ElectricalInputOutput](./ElectricalInputOutput.md), "CheckPower: event-driven un-power outside the tick".

## CalculateState accumulates into Required/Potential; only Initialise resets them
<!-- verified: 0.2.6403.27689 @ 2026-07-02 -->

`CalculateState` (public, lines 271765-271797) ADDS to the running sums (`Required += usedPower` at decompile line 271780, `Potential += generatedPower` at 271785; providers whose device reports `IsPowerInputOutput` are additionally collected into `InputOutputDevices`, lines 271788-271791); it never zeroes them itself. The reset lives in `Initialise` (`Potential = 0f; Required = 0f; Consumed = 0f;`, lines 271760-271762; full body 271742-271763). The two are therefore a mandatory pair: every `CalculateState` must be preceded by `Initialise`, or the sums double-count.

This matters for any mod that ticks a network more than once per game tick. PowerGridPlus's atomic tick splits the work into an OBSERVE pass and an ENFORCE pass, and each pass calls `Initialise` then `CalculateState`, so the second pass starts from a clean zero instead of doubling the first. `ApplyState` is different: it has real side effects (it drains `Providers[].Energy` and calls `Device.ReceivePower` via `ConsumePower`, lines 271820-271840 -- which mutates a transformer's `_powerProvided` ledger -- sets `Device.Powered`, and may `Break()` a fuse or cable), so it is run only once per game tick, in the enforce pass. Re-running `ApplyState` a second time would double-drain providers and re-trigger the break checks.

## Verification history

- 2026-07-09: re-confirmed the `ApplyState` device power-apply gate (271903-271946) and base `Device.AllowSetPower` (371531-371534) verbatim against the 0.2.6403.27689 decompile; no content change (the "ApplyState un-powers zero-demand and unfed devices" and "CacheState: strict power-met" sections already capture the else-branch, the strict `_isPowerMet` gate, and the base `PowerCableNetwork == cableNetwork` semantics). Occasion: diagnosing a PowerGridPlus fabricator print-start reboot. A print spikes a fabricator's `GetUsedPower` for one tick; because PowerGridPlus pins each net's `Potential` to the allocator's exact grant with no headroom, that one-tick spike makes `Potential < Required`, `_isPowerMet` false, and the else-branch (271936-271938) un-powers the fabricator, whose main-thread `OnPoweredChanged` cancels the in-progress print. Vanilla's advertised supply headroom normally masks this. The mod-side fix mirrors PowerGridPlus's existing segmenter `AllowSetPower` veto (`PoweredPresentationPatches`) for plain consumers behind a one-tick grace; that is mod-local and belongs in the PowerGridPlus docs, not here.
- 2026-07-07: re-confirmed the `CacheState` ratio computation (271842-271855, zero-Potential guard assigns the literal `1f`) and the `ApplyState` ratio consumption (`usedPower *= _powerRatio` at 271928 feeding `ConsumePower`, plus the power-on/off ladder) byte-identical against the 0.2.6403.27689 decompile. Occasion: deriving the PowerGridPlus partial-power sentinel's scope (which networks a sub-1 ratio can actually deprive) and its injection positive control (zeroing a sole supplier's advertise trips the zero-Potential guard and reads whole-dark ratio 1, so the injector must understate fractionally). No content change.
- 2026-07-02 (later): added two sections at game version 0.2.6403.27689, additive (no prior claim contradicted). (a) "Load mirrors are written at the END of the tick": `OnPowerTick` (270827-270840) assigns `RequiredLoad` / `CurrentLoad` / `PotentialLoad` / `ShortfallLoad` only after `ApplyState` returns, so cross-network advertisement (every `*.PotentialLoad` read by bridge devices) is one-tick lagged, with the dead-island no-bootstrap corollary (nothing in `Initialise` / `CalculateState` / `ApplyState` can raise `Potential` above the devices' own `GetGeneratedPower` sum; recovery needs stored energy, aim/state change, or a live bridge; solar case cross-linked on [SolarPanel](./SolarPanel.md)). (b) "ApplyState un-powers zero-demand and unfed devices; AllowSetPower picks the network allowed to do it": the `else if (device.AllowSetPower(CableNetwork) && device.Powered)` branch (271916-271940) catches `usedPower == 0` as well as unmet demand; base `Device.AllowSetPower` = `PowerCableNetwork == cableNetwork` (371531-371534); override census: Transformer 424748-424755, AreaPowerControl 390991-390998, PowerTransmitter 408415-408422 (all `InputNetwork`), PowerReceiver 408170-408177 (`WirelessInputNetwork`); `SetPowerFromThread` main-thread hop then `OnServer.Interact` (371648-371652, 371640-371646). Cross-linked the event-driven `CheckPower` family on [ElectricalInputOutput](./ElectricalInputOutput.md). Driving work: Powered-semantics stage of the power rearchitecture session.
- 2026-07-02: conflict resolution on the burn-candidate wording (game version 0.2.6403.27689). Contradicted claim: "any cable whose `MaxVoltage < _actual` ... is a burn candidate this tick", implying the pick pool is rebuilt per tick. Fresh validator verdict (binding): `BreakableFuses` / `BreakableCables` are append-only accumulators for the PowerTick instance lifetime. Complete census at 0.2.6403.27689: declarations 271708 / 271710 (field-initialized once; the class has no constructor; one `readonly PowerTick` per CableNetwork, decl 270617); Adds at 271809 / 271813 (`CheckForRecursiveProviders`), 271864 (`GetBreakableFuses`, no clear first), 271876 (`GetBreakableCables`, no clear first); `Pick` reads 271883 / 271894 with null guards 271884 / 271895; `Count` gates 271907 / 271912; `Pick<T>` (230535-230542) returns a random element WITHOUT removing; `Initialise` (271742-271763) clears only `Devices` / `Fuses` / `Cables` and zeroes only `Potential` / `Required` / `Consumed`; no `.Clear()` / `.Remove()` / reassignment of either list exists. Resulting change: rewrote the sentence (per-tick criterion, cumulative pick pool) and added the "append-only accumulators" subsection, including the caveat that a cable `Break()` usually splits the network into fresh `CableNetwork`s with fresh PowerTicks (shortening stale entries' lives on that path) while fuse breaks and non-splitting ticks accumulate.
- 2026-07-02: re-verification pass against the 0.2.6403.27689 decompile after the game update from 0.2.6228.27061. Confirmed unchanged with new line refs: `CableNetwork.OnPowerTick` sequence + load mirrors (270827-270840), `Initialise` verbatim (271742-271763), `CalculateState` (271765-271797; noted it is PUBLIC; `IsPowerInputOutput` -> `InputOutputDevices` 271788-271791; `PowerProvider` ctor calls `GetGeneratedPower` a second time capturing `_originalEnergy` 271686-271687), `CheckForRecursiveProviders` (271799-271818), `ApplyState` (271903-271946), `ConsumePower` single-supplier-first (271820-271840), `PowerProvider.ApplyPower` once with `EnergyUsed > 0` gate (271690-271696), base `Device` query/settle methods (371501-371534). CHANGED at 0.2.6403.27689: `CacheState` (271842-271855) now guards the `_powerRatio` assignment with `if (Potential > 0f && Required > 0f) ... else _powerRatio = 1f`; at 0.2.6228.27061 the game code was the bare unguarded expression (so `Potential == 0 && Required > 0` gave 0 and `0/0` gave NaN). Note: this page's `CacheState` excerpt already showed the guarded form before this pass (it was ahead of the 0.2.6228 game code it was stamped against); at 0.2.6403.27689 the excerpt is now verbatim-correct, so only the semantics note, the version note, and the stamps changed. Recorded `_powerAvailable` as a dead field (declared 271718, zero other references in the decompile).
- 2026-05-12: page created. Sourced from a voltage-tier research dive (planned mod "Power Grid Plus") into `.work/decomp/0.2.6228.27061/Assembly-CSharp.decompiled.cs` lines 254512-254760 and 253668-253681; verbatim excerpts of the `PowerTick` field block, `CableNetwork.OnPowerTick`, `CacheState`, `GetBreakableFuses`, `GetBreakableCables`, `BreakSingleFuse`, `BreakSingleCable`, `ApplyState`. Reconciles the class-attribution imprecision on [CableNetwork](./CableNetwork.md) (those `ConsumePower`/`CalculateState` bodies are `PowerTick` members). Re-Volt mod source (`RevoltTick : PowerTick`, reverse-patches `PowerTick.Initialise`/`CalculateState`/`ApplyState`) independently corroborates ownership.
- 2026-06-10: re-read lines 254512-254761 in full against the same 0.2.6228.27061 decompile during the PowerGridPlus single-architecture rework (the mod now runs this vanilla class unmodified in its atomic Phases 1 and 3). All sections still match verbatim; no content change.
- 2026-05-12: while building Power Grid Plus, confirmed `PowerTick.CheckForRecursiveProviders()` is a private instance method (decompile line 254613, called from `CalculateState` at 254610) and `CacheState()` is private at line 254656; added a note that both are reachable via Harmony reverse patches (Power Grid Plus, like Re-Volt, reverse-patches `CacheState` unconditionally and `CheckForRecursiveProviders` when its recursive-network-limits option is on). Also: `PowerTick` and `PowerProvider` are in namespace `Assets.Scripts.Networks`; the `Pick<T>(this List<T>)` / `Pick<T>(this List<T>, System.Random)` extension used by `BreakSingleCable`/`BreakSingleFuse` lives in `Assets.Scripts.Util` (decompile ~line 214049).

- 2026-06-17: added the "CalculateState accumulates; Initialise resets" pairing note. Re-confirmed against the 0.2.6228.27061 decompile: `Initialise` zeroes `Potential`/`Required`/`Consumed` (lines 254574-254576), `CalculateState` uses `+=` (254594, 254599), and `ApplyState`/`ConsumePower` drain `Providers[].Energy` and call `Device.ReceivePower` (254634-254654). Surfaced while explaining PowerGridPlus's two-pass atomic tick (Initialise+CalculateState in Phase 1 OBSERVE and Phase 3 ENFORCE; ApplyState only in Phase 3).
- 2026-06-29: completed the `ApplyState` excerpt (was previously truncated with `...`; now shows the full device loop with `usedPower *= _powerRatio`, the `SetPowerFromThread` powered/unpowered branch, and the trailing `Providers[num]?.ApplyPower()` loop, decompile lines 254717-254760) and added three sections during the vanilla power-model curation pass: "The query/settle split" (GetGeneratedPower/GetUsedPower are pure reads returning the `-1f` off-network sentinel from base Device at lines 350696-350716; ReceivePower is the consumer settle and UsePower the producer settle, both no-ops on base Device at 350718-350724), "CacheState: strict power-met, brownout ratio, and `_actual`" (`_isPowerMet = (Potential - Required) > 0f` is STRICT not `>=`, `_powerRatio` is the brownout de-rate applied as `usedPower *= _powerRatio`, `_actual = Min(Potential, Required)` feeds the burn checks; lines 254656-254669 + 254742), and "ConsumePower and PowerProvider.ApplyPower: the two settle loops" (verbatim `PowerProvider.ApplyPower` at 254504-254510 with `EnergyUsed => _originalEnergy - Energy` at 254494 and `_originalEnergy = device.GetGeneratedPower(...)` captured at construction 254500-254501; consumer settle drains `provider.Energy` and calls `ReceivePower` per drained provider, producer settle calls `UsePower` once with the net `EnergyUsed`). Additive; no prior claim contradicted (the existing `CacheState` and `ConsumePower` verbatim bodies already on the page are unchanged, the new sections explain their semantics). Cross-links the new [ElectricityManager](./ElectricityManager.md) (pool-slot cross-network order) and [ElectricalInputOutput](./ElectricalInputOutput.md) (the bridge base whose `_powerProvided` the settle mutates) pages. Restamped `verified_at` to 2026-06-29.

## Open questions

- The `CableNetwork.md` page should have its prose updated to attribute `ConsumePower` / `CalculateState` / the provider iteration to `PowerTick`; deferred to a future reconciliation pass (the behavioral content there is correct).
