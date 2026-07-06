---
title: PowerReceiver
type: GameClasses
created_in: 0.2.6228.27061
verified_in: 0.2.6403.27689
verified_at: 2026-07-06
sources:
  - $(StationeersPath)\rocketstation_Data\Managed\Assembly-CSharp.dll :: Assets.Scripts.Objects.Electrical.PowerReceiver
  - .work/decomp/0.2.6228.27061/Assembly-CSharp.decompiled.cs :: lines 386861-387058 (PowerReceiver), 387065+ (PowerTransmitter)
  - .work/decomp/0.2.6403.27689/Assembly-CSharp.decompiled.cs :: lines 408065-408262 (PowerReceiver), 408269-408581 (PowerTransmitter), 394993-395006 (ElectricalInputOutput.OnRemoveCableNetwork), 271011-271027 (CableNetwork.RemoveDevice)
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
<!-- verified: 0.2.6403.27689 @ 2026-07-02 -->

Line refs at 0.2.6403.27689: class 408065, `_powerProvided` 408071, `LinkedPowerTransmitter` setter 408081-408097 (assigns `InputNetwork = value.OutputNetwork` only when `value` is non-null), `OnDestroy` 408108-408115, `CheckConnections` 408257-408261 (touches `OutputNetwork` only).

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
<!-- verified: 0.2.6403.27689 @ 2026-07-02 -->

Re-verified verbatim at 0.2.6403.27689: `UsePower` / `ReceivePower` ledger 408184-408204, `GetUsedPower` 408206-408229, `GetGeneratedPower` 408232-408243.

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

### `_powerProvided` is runtime-only: not saved, not synced
<!-- verified: 0.2.6403.27689 @ 2026-07-06 -->

The debt accumulator (declaration 408071) has no persistence path at 0.2.6403.27689. `PowerReceiverSaveData : WirelessPowerSaveData` (408062-408064) has an EMPTY body; the base record (426765-426778) carries only the four dish-rotation doubles, so nothing at the receiver level saves the ledger, and the whole-decompile census shows the field touched exclusively by `UsePower` (408188), `ReceivePower` (408202), and `GetUsedPower` (408229); no `SerializeOnJoin` / `BuildUpdate` / `ProcessUpdate` member reads it. It restarts at 0 on save load and on client join: a loaded or late-joining receiver reports zero wireless-side draw until its output cable network pulls power again. Cross-class census and consequences on [Device](./Device.md), "Two per-device draw-state fields".

## The deliverable path
<!-- verified: 0.2.6228.27061 @ 2026-06-14 -->

End to end, vanilla:

```
PT input cable --(PT.GetUsedPower)--> PT --(PT.GetGeneratedPower(wireless))--> WirelessNetwork.PotentialLoad
   --(PR.GetUsedPower(wireless))--> PR --(PR.GetGeneratedPower(cable) = WirelessInputNetwork.PotentialLoad)--> RX output cable
```

Delivered == drawn in vanilla: the transmitter's `GetGeneratedPower` already bakes in the distance derate (`PowerLossOverDistance`), and the receiver passes that derated potential straight to its cable network. Neither side multiplies or divides; the loss is entirely inside the transmitter's `GetGeneratedPower` number.

## Unlink behavior: the stale-InputNetwork window NARROWED at 0.2.6403
<!-- verified: 0.2.6403.27689 @ 2026-07-02 -->

At 0.2.6228 an unlinked receiver kept its last wireless `InputNetwork` reference indefinitely (nothing nulled it). At 0.2.6403.27689 the normal unlink path clears it:

- `ElectricalInputOutput.OnRemoveCableNetwork(oldNetwork)` (394993-395006) now nulls `InputNetwork` when `oldNetwork == InputNetwork` (and `OutputNetwork` symmetrically), then re-runs `CheckConnections` / `CheckPower`.
- `CableNetwork.RemoveDevice(Device)` invokes `device.OnRemoveCableNetwork(this)` (line 271024).
- The TX-side `LinkedReceiver` setter removes the old RX from the `WirelessNetwork` (`WirelessOutputNetwork.RemoveDevice(_linkedReceiver)`, PowerTransmitter line 408305).

So a setter-driven unlink or retarget (`TryContactReceiver` clearing / replacing `LinkedReceiver`, an OnOff toggle retarget, etc.) DOES clear `rx.InputNetwork` now: setter -> `RemoveDevice` -> `OnRemoveCableNetwork` -> `InputNetwork = null`. Paths that bypass `CableNetwork.RemoveDevice` still leave the reference stale; the known one is `PowerTransmitter.OnDestroy` (408387-408394), which only does `LinkedReceiver.LinkedPowerTransmitter = null` and never removes the RX from the wireless network before the TX (and with it the network's only ticking anchor) goes away. See Open Questions for the runtime-unverified hazard on that path.

## PowerTransmitterPlus interaction
<!-- verified: 0.2.6228.27061 @ 2026-06-14 -->

`PowerReceiver`'s own methods are NOT patched by PowerTransmitterPlus; the distance model lives entirely on the transmitter side. PowerTransmitterPlus (a) drops the 5000 W delivery ceiling on `PowerTransmitter.GetGeneratedPower` (so the wireless `PotentialLoad`, and hence `PR.GetGeneratedPower`, can exceed 5000 W) and (b) inflates the TRANSMITTER's source-side draw to `delivered * (1 + k * distance_km)`. The receiver still reports `WirelessInputNetwork.PotentialLoad` as its generation and its debt-capped draw as its use; it is a faithful pass-through. See [PowerTransmitter](./PowerTransmitter.md) and [PowerGridPlus cross-mod compatibility](../Unsorted/PowerGridPlusCrossModCompat.md) for the transmitter-side model and the PowerGridPlus allocator's handling of the inflated source draw.

## Verification history

- 2026-07-06: added the "`_powerProvided` is runtime-only" subsection (game version 0.2.6403.27689). Evidence: `PowerReceiverSaveData` empty body (408062-408064), `WirelessPowerSaveData` body (426765-426778), whole-decompile `_powerProvided` census (RX sites: declaration 408071, 408188 / 408202 / 408229 only; no serialization member). Additive; no prior claim on this page addressed save behavior of the ledger, no fresh validator.
- 2026-07-02: re-verification pass against the 0.2.6403.27689 decompile after the game update from 0.2.6228.27061. Confirmed unchanged with new line refs: `GetGeneratedPower(OutputNetwork)` = `WirelessInputNetwork.PotentialLoad` verbatim (408232-408243), `GetUsedPower` = `Min(MaxPowerTransmission + UsedPower, _powerProvided)` (408206-408229), `UsePower` / `ReceivePower` ledger (408184-408204), `LinkedPowerTransmitter` setter assigning `InputNetwork = value.OutputNetwork` only when non-null (408081-408097), `CheckConnections` touching `OutputNetwork` only (408257-408261). CHANGED at 0.2.6403.27689 (supersession, new section "Unlink behavior"): `ElectricalInputOutput.OnRemoveCableNetwork` now nulls `InputNetwork` when `oldNetwork == InputNetwork` (394993-395006), `CableNetwork.RemoveDevice` invokes it (271024), and the TX `LinkedReceiver` setter removes the old RX from the WirelessNetwork (408305), so a setter-driven unlink or retarget clears `rx.InputNetwork`; previously nothing cleared it and a formerly-linked RX kept advertising the dead wireless network's `PotentialLoad`. Bypass paths (`PowerTransmitter.OnDestroy` doing only `LinkedReceiver.LinkedPowerTransmitter = null`, 408387-408394) still leave the reference stale; that residual hazard moved to Open Questions as runtime-unverified.
- 2026-06-14: page created. Sourced from `.work/decomp/0.2.6228.27061/Assembly-CSharp.decompiled.cs` lines 386861-387058; verbatim `UsePower` / `ReceivePower` / `GetUsedPower` / `GetGeneratedPower` / `LinkedPowerTransmitter` setter / `CheckConnections` bodies. Confirms `PowerReceiver.GetGeneratedPower(OutputNetwork)` returns `WirelessInputNetwork.PotentialLoad` and `PowerReceiver.GetUsedPower(InputNetwork)` returns `Mathf.Min(PowerTransmitter.MaxPowerTransmission + UsedPower, _powerProvided)`. Established while correcting the PowerGridPlus x PowerTransmitterPlus loss-model interaction (POWER.md §6.3 / §8.4.2).

## Open questions

- `PowerTransmitter.MaxPowerTransmission` (5000) survives only as the `GetUsedPower` cap and the visualizer reference once PowerTransmitterPlus removes it as the delivery ceiling. Whether any other vanilla consumer still relies on the 5000 W receiver-draw cap is unverified; PowerGridPlus and PowerTransmitterPlus do not.
- Bypass-path stale-InputNetwork hazard (runtime-unverified at 0.2.6403.27689): when a linked TX is destroyed, `PowerTransmitter.OnDestroy` (408387-408394) only nulls `LinkedPowerTransmitter` and does not remove the RX from the WirelessNetwork, so `rx.InputNetwork` should keep pointing at the dead wireless network; a still-ON formerly-linked RX would then keep advertising that network's last `PotentialLoad` through `GetGeneratedPower` until relink or destruction. Confirm with InspectorPlus (request: types=[PowerReceiver], fields=[_linkedPowerTransmitter, InputNetwork, OutputNetwork]) after destroying a linked transmitter.
