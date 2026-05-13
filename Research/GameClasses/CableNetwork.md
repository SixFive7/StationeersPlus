---
title: CableNetwork
type: GameClasses
created_in: 0.2.6228.27061
verified_in: 0.2.6228.27061
verified_at: 2026-05-13
sources:
  - $(StationeersPath)\rocketstation_Data\Managed\Assembly-CSharp.dll :: Assets.Scripts.Objects.Electrical.CableNetwork
related:
  - ./PowerTransmitter.md
  - ./Cable.md
  - ./Device.md
tags: [power, logic, network]
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

## Data device list: separate from power device list, walked from the same Cable graph
<!-- verified: 0.2.6228.27061 @ 2026-05-13 -->

`CableNetwork` holds two device lists, not one:

- `_powerDeviceList` (exposed as `PowerDeviceList`): devices that have at least one `PowerCable` whose `CableNetwork == this`.
- `_dataDeviceList` (exposed as `DataDeviceList`): devices that have at least one `DataCable` whose `CableNetwork == this`, plus any devices pulled in via `HandleDataNetTransmissionDevice`.

The base `DeviceList` is the superset; the two typed lists are filtered views rebuilt by `RefreshPowerAndDataDeviceLists()` (Assembly-CSharp.dll decompile lines 253589-253628):

```csharp
protected virtual void RefreshPowerAndDataDeviceLists()
{
    if (DataDeviceListDirty)  { _dataDeviceList.Clear();  }
    if (PowerDeviceListDirty) { _powerDeviceList.Clear(); }
    for (int num = DeviceList.Count - 1; num >= 0; num--)
    {
        Device device = DeviceList[num];
        if (DataDeviceListDirty)
        {
            HandleDataNetTransmissionDevice(device);
            foreach (Cable dataCable in device.DataCables)
            {
                if (dataCable.CableNetwork == this) { _dataDeviceList.Add(device); break; }
            }
        }
        if (PowerDeviceListDirty)
        {
            foreach (Cable powerCable in device.PowerCables)
            {
                if (powerCable.CableNetwork == this) { _powerDeviceList.Add(device); break; }
            }
        }
    }
    PowerDeviceListDirty = false;
    DataDeviceListDirty = false;
}
```

Consequences:

- A device on the network can be in `_powerDeviceList` but not `_dataDeviceList` (its cables carry power only into this network) or vice versa (a device pulled in by `HandleDataNetTransmissionDevice` from a different network, or one wired with data-only cabling). The two lists are independent filters over `DeviceList`.
- The two lists are walked independently. The power tick uses `_powerDeviceList`; IC10 / `Logicable` traversals use `_dataDeviceList`.
- Dirtying is split. `DirtyPowerAndDataDeviceLists()` dirties both; `DirtyDataDeviceList()` dirties only the data side.

This is the architectural hook that lets a single physical cable carry both signals while letting a device participate in only one.

## HandleDataNetTransmissionDevice: data devices reach across cable networks
<!-- verified: 0.2.6228.27061 @ 2026-05-13 -->

Logic devices can talk to other devices on a different `CableNetwork` without a shared cable run between them. The mechanism is a paired transmitter / receiver, bound by player configuration in the device UI (canonical example: station-side rocket data link configured to talk to rocket-side data link). Each side sits on its own `CableNetwork`. The link is bridged during the data-list refresh (Assembly-CSharp.dll decompile lines 253630-253655):

```csharp
private void HandleDataNetTransmissionDevice(Device device)
{
    if (device is IReceiveDataNetworkDevices receiver
        && receiver.DataConnectionActive()
        && receiver.ConnectedDataNetTransmitter?.DataCableNetwork != null
        && receiver.ConnectedDataNetTransmitter.DataCableNetwork != this)
    {
        List<Device> remote = receiver.ConnectedDataNetTransmitter.DataCableNetwork.DeviceList;
        for (int i = remote.Count - 1; i >= 0; i--)
        {
            if (!_dataDeviceList.Contains(remote[i]))
                _dataDeviceList.Add(remote[i]);
        }
    }
    if (!(device is ITransmitDataNetworkDevices transmitter) || !transmitter.DataConnectionActive())
        return;
    for (int i = transmitter.ConnectedDataNetReceivers.Count - 1; i >= 0; i--)
    {
        IReceiveDataNetworkDevices r = transmitter.ConnectedDataNetReceivers[i];
        if (r?.DataCableNetwork != null && r.DataCableNetwork != this)
            r.DataCableNetwork.DirtyDataDeviceList();
    }
}
```

Two paths:

1. **Receiver pull.** When the local network refreshes its data list and one of the local devices is an `IReceiveDataNetworkDevices` with an active connection to a transmitter on a different `CableNetwork`, the local `_dataDeviceList` is augmented with every entry from the transmitter's `DeviceList`. Logic on this side can then read those remote devices.
2. **Transmitter push request.** When the local device is an `ITransmitDataNetworkDevices` with active receivers on other networks, each remote network's data list is marked dirty so it will pull on its next refresh. The actual copy still happens on the receiver side.

The relay is intentionally one-way per pair: devices flow from transmitter to receiver, not back. The interfaces enforce direction (Assembly-CSharp.dll decompile lines 364740-364751 and 365149-365158):

```csharp
public interface ITransmitDataNetworkDevices : ILogicable, IReferencable, IEvaluable
{
    List<IReceiveDataNetworkDevices> ConnectedDataNetReceivers { get; set; }
    CableNetwork DataCableNetwork { get; }
    bool DataConnectionActive();
    void RemoveReceiver(IReceiveDataNetworkDevices receiver);
    void AddReceiver(IReceiveDataNetworkDevices receiver);
}

public interface IReceiveDataNetworkDevices : ILogicable, IReferencable, IEvaluable
{
    ITransmitDataNetworkDevices ConnectedDataNetTransmitter { get; set; }
    CableNetwork DataCableNetwork { get; }
    bool DataConnectionActive();
    bool GetIsOperable();
}
```

Vanilla implementers (decompile lines 364757, 365159): `RocketDataDownLink` is the transmitter, `RocketDataUpLink` is the receiver. The two are bound by configured device IDs, and the resulting relay makes the rocket-side device list readable from a station-side IC10 chip.

Note that the remote devices are added by reference. They still own their original `DataCables` pointing into their home network. Adding them to a foreign `_dataDeviceList` only means a logic walker iterating that list will find them; their internal state is unchanged. Logic reads and writes go through the standard `ILogicable` methods on the device, which do not care which network the iteration started from.

A mod that wants a device to appear logic-transparent (logic flows through it even though it splits the power network) has two implementation choices:

1. Implement both `ITransmitDataNetworkDevices` and `IReceiveDataNetworkDevices` on the bridging device and configure the two sides as a bound pair (input side as receiver pointing at output side as transmitter, and the inverse), so the standard relay merges each side into the other's data list.
2. Patch `RefreshPowerAndDataDeviceLists` to, for a chosen device class, pull the other side's `DeviceList` into the local `_dataDeviceList` directly. This skips the interface dance for devices that are always bound to themselves (a transformer's input is always paired with its own output for data purposes).

## Verification history
<!-- verified: 0.2.6228.27061 @ 2026-05-13 -->

- 2026-05-02: page created. Sourced from a long-distance auto-aim test on the Lunar save: seven TX-RX pairs at 163-222 m all linked successfully (verified via InspectorPlus DishProbe), but only one RX showed `Powered=True` and `PowerProvided > 0`. Reading `CableNetwork.ConsumePower` in Assembly-CSharp.dll (decompile lines 254579-254654) confirmed the single-supplier-first iteration, identifying the observed asymmetry as expected vanilla behaviour for parallel receivers on a shared destination network.
- 2026-05-13: added "Data device list" and "HandleDataNetTransmissionDevice" sections, sourced from `Assembly-CSharp.dll` decompile lines 253589-253655 (refresh + relay) and 364740-365158 (interfaces and rocket-link implementers). Added `logic` and `network` tags. No conflict with the existing power-side content. Findings produced while researching whether transformers and APCs can be made logic-transparent for Power Grid Plus.

## Open questions

None at creation.
