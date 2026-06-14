---
title: PowerReceiver
type: GameClasses
created_in: 0.2.6228.27061
verified_in: 0.2.6228.27061
verified_at: 2026-06-14
sources:
  - $(StationeersPath)\rocketstation_Data\Managed\Assembly-CSharp.dll :: Assets.Scripts.Objects.Electrical.PowerReceiver
  - .work/decomp/0.2.6228.27061/Assembly-CSharp.decompiled.cs :: lines 386861-387058 (PowerReceiver), 387065+ (PowerTransmitter)
  - Mods/PowerGridPlus/PowerGridPlus/PowerAllocator.cs (PT/PR pair modelled as one synthetic contributor)
related:
  - ./PowerTransmitter.md
  - ./WirelessPower.md
  - ./CableNetwork.md
  - ../Unsorted/PowerGridPlusCrossModCompat.md
tags: [power, network]
---

# PowerReceiver

The receiver half of a microwave power link (`Assets.Scripts.Objects.Electrical.PowerReceiver`). Its partner is [PowerTransmitter](./PowerTransmitter.md); both subclass [WirelessPower](./WirelessPower.md). The transmitter beams power onto a shared `WirelessNetwork`; the receiver pulls it off that network and re-emits it onto its own output cable network. The receiver carries no rating of its own: what it delivers is whatever potential the linked transmitter put on the wireless network.

```csharp
public class PowerReceiver : WirelessPower, ITransmitable, ILogicable, IReferencable, IEvaluable
```

## Fields and pairing
<!-- verified: 0.2.6228.27061 @ 2026-06-14 -->

```csharp
public Transform DishTarget;
private PowerTransmitter _linkedPowerTransmitter;
private float _powerProvided;                     // debt accumulator: += on UsePower, -= on ReceivePower

public WirelessNetwork WirelessInputNetwork => InputNetwork as WirelessNetwork;

public PowerTransmitter LinkedPowerTransmitter
{
    get => _linkedPowerTransmitter;
    set
    {
        if (value != null)
            InputNetwork = value.OutputNetwork;          // RX input network IS the TX output (wireless) network
        if (GameManager.RunSimulation)
            OnServer.Interact(base.InteractMode, (value != null) ? 1 : 0);
        if (value == null)
            base.VisualizerIntensity = 0f;
        _linkedPowerTransmitter = value;
        CheckError();
    }
}
```

Topology consequences:

- Setting `LinkedPowerTransmitter` writes `InputNetwork = value.OutputNetwork`, so the receiver's `InputNetwork` is the SAME `WirelessNetwork` object the transmitter outputs to. `WirelessInputNetwork` is that network typed as `WirelessNetwork` (the [PowerTransmitter](./PowerTransmitter.md) page documents `PT.OutputNetwork === PR.InputNetwork`).
- The receiver's `OutputNetwork` is its own cable network, set in `CheckConnections` from the output connection's cable:

```csharp
protected override void CheckConnections()
{
    Cable cable = OutputConnection.GetCable();
    OutputNetwork = (cable ? cable.CableNetwork : null);
}
```

- `OnDestroy` clears the partner's back-reference (`LinkedPowerTransmitter.LinkedReceiver = null`), so destroying either dish breaks the pair cleanly.
- Pairing is by cross-reference only (`PR._linkedPowerTransmitter` and `PT.LinkedReceiver`); there is no central registry. Enumerating pairs requires walking both instance lists (see PowerGridPlus's bipartite wireless-edge gating, POWER.md §6.5).

## Power-flow methods (verbatim)
<!-- verified: 0.2.6228.27061 @ 2026-06-14 -->

```csharp
public override void UsePower(CableNetwork cableNetwork, float powerUsed)
{
    if (Error != 1 && OnOff && cableNetwork == OutputNetwork)
        _powerProvided += powerUsed;
}

public override void ReceivePower(CableNetwork cableNetwork, float powerAdded)
{
    if (WirelessInputNetwork == null || cableNetwork == WirelessInputNetwork)
    {
        if (!OnOff || WirelessInputNetwork == null)
        {
            base.VisualizerIntensity = 0f;
            return;
        }
        base.VisualizerIntensity = RocketMath.MapToScale(0f, PowerTransmitter.MaxPowerTransmission, 0f, 1f, powerAdded);
        _powerProvided -= powerAdded;
    }
}

public override float GetUsedPower([NotNull] CableNetwork cableNetwork)
{
    if (InputNetwork == null)
        base.VisualizerIntensity = 0f;
    if (InputNetwork == null || cableNetwork != InputNetwork)
        return 0f;
    if (Error == 1)
    {
        base.VisualizerIntensity = 0f;
        if (!OnOff) return 0f;
        return UsedPower;
    }
    if (!OnOff) return 0f;
    return Mathf.Min(PowerTransmitter.MaxPowerTransmission + UsedPower, _powerProvided);
}

public override float GetGeneratedPower(CableNetwork cableNetwork)
{
    if (OutputNetwork == null || Error == 1 || cableNetwork != OutputNetwork)
        return 0f;
    if (!OnOff || WirelessInputNetwork == null)
        return 0f;
    return WirelessInputNetwork.PotentialLoad;
}
```

Reading them as a pair:

- **`GetGeneratedPower(OutputNetwork)` = `WirelessInputNetwork.PotentialLoad`.** The receiver puts onto its cable network exactly the potential standing on the wireless network. That wireless potential is the linked transmitter's `GetGeneratedPower(wireless)` (the deliverable cap), summed into `CableNetwork.PotentialLoad` by the previous tick's `OnPowerTick` (see [CableNetwork](./CableNetwork.md) / [PowerTick](./PowerTick.md)). So the receiver is the grid-facing generator of the link, and its output equals the transmitter's deliverable. It carries no rating of its own.
- **`GetUsedPower(InputNetwork)` = `Mathf.Min(PowerTransmitter.MaxPowerTransmission + UsedPower, _powerProvided)`.** The receiver's draw on the wireless network is its accumulated debt, capped at `MaxPowerTransmission + UsedPower` (= 5000 + the dish's own quiescent draw). `_powerProvided` rises in `UsePower` (when the grid pulls power from the receiver's output cable) and falls in `ReceivePower` (when the receiver is fed on the wireless side), so it tracks net outstanding throughput.
- `MaxPowerTransmission` is the static 5000 W constant on `PowerTransmitter`; it appears here on the receiver as the `GetUsedPower` cap and the visualizer's full-brightness reference.

## The deliverable path
<!-- verified: 0.2.6228.27061 @ 2026-06-14 -->

End to end, vanilla:

```
PT input cable --(PT.GetUsedPower)--> PT --(PT.GetGeneratedPower(wireless))--> WirelessNetwork.PotentialLoad
   --(PR.GetUsedPower(wireless))--> PR --(PR.GetGeneratedPower(cable) = WirelessInputNetwork.PotentialLoad)--> RX output cable
```

Delivered == drawn in vanilla: the transmitter's `GetGeneratedPower` already bakes in the distance derate (`PowerLossOverDistance`), and the receiver passes that derated potential straight to its cable network. Neither side multiplies or divides; the loss is entirely inside the transmitter's `GetGeneratedPower` number.

## PowerTransmitterPlus interaction
<!-- verified: 0.2.6228.27061 @ 2026-06-14 -->

`PowerReceiver`'s own methods are NOT patched by PowerTransmitterPlus; the distance model lives entirely on the transmitter side. PowerTransmitterPlus (a) drops the 5000 W delivery ceiling on `PowerTransmitter.GetGeneratedPower` (so the wireless `PotentialLoad`, and hence `PR.GetGeneratedPower`, can exceed 5000 W) and (b) inflates the TRANSMITTER's source-side draw to `delivered * (1 + k * distance_km)`. The receiver still reports `WirelessInputNetwork.PotentialLoad` as its generation and its debt-capped draw as its use; it is a faithful pass-through. See [PowerTransmitter](./PowerTransmitter.md) and [PowerGridPlus cross-mod compatibility](../Unsorted/PowerGridPlusCrossModCompat.md) for the transmitter-side model and the PowerGridPlus allocator's handling of the inflated source draw.

## Verification history

- 2026-06-14: page created. Sourced from `.work/decomp/0.2.6228.27061/Assembly-CSharp.decompiled.cs` lines 386861-387058; verbatim `UsePower` / `ReceivePower` / `GetUsedPower` / `GetGeneratedPower` / `LinkedPowerTransmitter` setter / `CheckConnections` bodies. Confirms `PowerReceiver.GetGeneratedPower(OutputNetwork)` returns `WirelessInputNetwork.PotentialLoad` and `PowerReceiver.GetUsedPower(InputNetwork)` returns `Mathf.Min(PowerTransmitter.MaxPowerTransmission + UsedPower, _powerProvided)`. Established while correcting the PowerGridPlus x PowerTransmitterPlus loss-model interaction (POWER.md §6.3 / §8.4.2).

## Open questions

- `PowerTransmitter.MaxPowerTransmission` (5000) survives only as the `GetUsedPower` cap and the visualizer reference once PowerTransmitterPlus removes it as the delivery ceiling. Whether any other vanilla consumer still relies on the 5000 W receiver-draw cap is unverified; PowerGridPlus and PowerTransmitterPlus do not.
