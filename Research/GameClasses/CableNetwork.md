---
title: CableNetwork
type: GameClasses
created_in: 0.2.6228.27061
verified_in: 0.2.6228.27061
verified_at: 2026-05-02
sources:
  - $(StationeersPath)\rocketstation_Data\Managed\Assembly-CSharp.dll :: Assets.Scripts.Objects.Electrical.CableNetwork
related:
  - ./PowerTransmitter.md
tags: [power]
---

# CableNetwork

Vanilla cable network. Each connected island of power cables is one `CableNetwork` instance, holding the list of `Devices` attached to it, the `Providers` array of devices that generate power into this network this tick, the `Potential` (total generation), `Required` (total consumption), and the per-tick book-keeping that drives `ConsumePower`.

`CableNetwork` lives in `Assets.Scripts.Objects.Electrical`. The decompile excerpts below are verbatim.

## Provider iteration is single-supplier-first, not load-balanced
<!-- verified: 0.2.6228.27061 @ 2026-05-02 -->

`ConsumePower(Device, CableNetwork, float)` walks the `Providers` array from the END toward index 0 and pulls from each provider until the requested power demand reaches zero. As soon as one provider can satisfy the remaining demand, the walk returns and the rest are untouched.

```csharp
private bool ConsumePower(Device device, CableNetwork cableNetwork, float powerRequired)
{
    int num = Providers.Length;
    while (num-- > 0)
    {
        PowerProvider powerProvider = Providers[num];
        if (powerProvider != null && !(powerProvider.Energy <= 0f))
        {
            float num2 = Mathf.Min(powerRequired, powerProvider.Energy);
            powerProvider.Energy -= num2;
            powerRequired -= num2;
            Consumed += num2;
            device.ReceivePower(cableNetwork, num2);
            if (powerRequired <= 0f)
            {
                return true;
            }
        }
    }
    return powerRequired <= 0f;
}
```

Two consequences fall out of this:

- **Under-loaded networks**: when the network's total `Required` is less than any one provider's `Energy`, only ONE provider per consumer call provides power. Every other provider sees `PowerProvided == 0` for that tick, even though their internal generators are running and capable. The "wasted" generation is observable as low `_powerProvidedToCableNetwork` on the dormant providers and is not a bug.
- **Overloaded networks**: when the demand exceeds one provider's `Energy`, the walk continues to the next provider and pulls the residual. Multiple providers contribute proportionally when at least one has been drained to zero, but always in the order specified by the array.

## Provider array order
<!-- verified: 0.2.6228.27061 @ 2026-05-02 -->

`Providers` is built by `CalculateState()` from the network's `Devices` list. The list is walked backwards while collecting providers:

```csharp
public void CalculateState()
{
    _providers.Clear();
    _inputOutputDevices.Clear();
    int count = Devices.Count;
    while (count-- > 0)
    {
        _currentDevice = Devices[count];
        if (_currentDevice == null) continue;
        float usedPower = _currentDevice.GetUsedPower(CableNetwork);
        if (usedPower > 0f) Required += usedPower;
        float generatedPower = _currentDevice.GetGeneratedPower(CableNetwork);
        if (generatedPower > 0f)
        {
            Potential += generatedPower;
            PowerProvider item = new PowerProvider(_currentDevice, CableNetwork);
            _providers.Add(item);
            if (_currentDevice.IsPowerInputOutput) _inputOutputDevices.Add(item);
        }
    }
    Providers = _providers.ToArray();
    InputOutputDevices = _inputOutputDevices.ToArray();
    CheckForRecursiveProviders();
}
```

The reverse walk over `Devices` followed by `_providers.Add(item)` produces a `Providers` array whose order is the REVERSE of the `Devices` list. `ConsumePower` then walks `Providers` backwards (`num--`), which double-reverses to give iteration order = original `Devices` order.

Net effect: the device that appears earliest in `Devices` (first added to the network at construction time, typically the lowest `ReferenceId` of the providers on a given network) is the FIRST one asked to supply each `ConsumePower` call. Tie-breaking among providers is therefore device-insertion-order, not load-balancing, distance, or heuristic.

## Implication for wireless power: only one PowerReceiver supplies a shared network
<!-- verified: 0.2.6228.27061 @ 2026-05-02 -->

A `PowerReceiver` is an `IsPowerInputOutput` device: it appears in both the source-side `Devices` list (as a consumer) and the destination-side `Devices` list (as a provider via the wireless link). When multiple `PowerReceiver` dishes are wired into the SAME destination `CableNetwork` (for example, seven receiver dishes mounted in a row and all connected to one battery bank), the wireless link from each transmitter delivers power into a shared `Providers` array on that one network.

Because `ConsumePower` is single-supplier-first, the network's `Required` is satisfied by the first provider in iteration order whose `Energy` is non-zero. The other receivers stay at `PowerProvided == 0` for the tick. The matching transmitters (which only pull source power when their receiver is providing into its destination network) likewise stay at `PowerProvided == 0` and `Powered == false`.

Under low or zero destination demand, only one of N parallel TX+RX pairs appears active. This looks like a bug ("six of seven links broken") but is the documented vanilla behaviour: the transmitters and the wireless link are fine; the destination network simply has no work for the other six providers to do.

To distribute load across several wireless links a player must split the destination side into separate cable networks (one per link) or push the consumption past one receiver's `Energy` capacity so the iteration falls through to the next provider.

## Verification history
<!-- verified: 0.2.6228.27061 @ 2026-05-02 -->

- 2026-05-02: page created. Sourced from a long-distance auto-aim test on the Lunar save: seven TX-RX pairs at 163-222 m all linked successfully (verified via InspectorPlus DishProbe), but only one RX showed `Powered=True` and `PowerProvided > 0`. Reading `CableNetwork.ConsumePower` in Assembly-CSharp.dll (decompile lines 254579-254654) confirmed the single-supplier-first iteration, identifying the observed asymmetry as expected vanilla behaviour for parallel receivers on a shared destination network.

## Open questions

None at creation.
