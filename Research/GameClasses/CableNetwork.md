---
title: CableNetwork
type: GameClasses
created_in: 0.2.6228.27061
verified_in: 0.2.6228.27061
verified_at: 2026-06-20
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

`CableNetwork` lives in the `Assets.Scripts.Networks` namespace (decompile line 253403 opens the namespace; class declaration at line 253411). The sibling base `StructureNetwork` lives in a different namespace called just `Networks` (decompile line 175608 / 177045) -- those two namespaces share a folder convention but are distinct in the assembly. The decompile excerpts below are verbatim.

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

## Consumer: the Network Analyser reads the raw `DeviceList`, not `DataDeviceList`
<!-- verified: 0.2.6228.27061 @ 2026-06-20 -->

The `NetworkAnalyser` tablet cartridge (decompile line 331171, `public class NetworkAnalyser : Cartridge`) populates its screen from the scanned network's base `DeviceList` field, not from `DataDeviceList`. `GetScannedNetwork()` (line 331205) returns the `CableNetwork`; `OnPreScreenUpdate()` (line 331273) reads the device count and iterates the device list directly (lines 331287 and 331295):

```csharp
_devicesValueText = _scannedNetwork.DeviceList.Count.ToString();
...
foreach (Device device in _scannedNetwork.DeviceList)
{
    num++;
    _tempDeviceOutput += $"\n{StringManager.Get(num)}.{device.DisplayName}";
    // ...per-device OnOff / Powered / Open / Error state appended...
}
```

Consequences:

- The on-screen device list and "Total Devices" count reflect physical cable membership only. `DeviceList` is the superset rebuilt from each device's `PowerCables` / `DataCables` pointing into this network (see "Data device list" above); it is never augmented by the data-relay path. Devices merged into `_dataDeviceList` are NOT shown on the analyser: `HandleDataNetTransmissionDevice` (the rocket data-link relay) and any mod that augments the data list target `_dataDeviceList`, never the base `DeviceList`.
- A mod that makes remote devices logic-visible by augmenting the data list (a postfix on the `DataDeviceList` getter, or on `RefreshPowerAndDataDeviceLists` that appends to `_dataDeviceList`) changes what IC10 batch ops (`lb` / `sb`) and logic readers traverse, but does NOT change what the Network Analyser screen displays. Reflecting such a merge on the analyser requires separately patching `NetworkAnalyser.OnPreScreenUpdate`, for example a transpiler that redirects the two `DeviceList` field loads to a merged list. Observed in the wild: the third-party OmniLink mod pairs a `DataDeviceList`-getter postfix with exactly such a `NetworkAnalyser.OnPreScreenUpdate` transpiler for this reason.

## Refresh cadence: device lists dirty only on structural change, never per tick
<!-- verified: 0.2.6228.27061 @ 2026-05-21 -->

`RefreshPowerAndDataDeviceLists()` is invoked lazily from the two property getters (see "Field shape and accessor quirk" below), so it runs only when a dirty flag is set and the list is then read. Every call site that sets a dirty flag is a structural (membership / connectivity) event; none sits on a per-tick or per-frame path. Call sites in `Assembly-CSharp.dll` (decompile, version 0.2.6228.27061):

`DirtyPowerAndDataDeviceLists()`:

- `CableNetwork.AddDevice(Cable, Device)` line 253849 -- a device gains a cable into this network.
- `CableNetwork.AddDevice(Device)` line 253858 -- the virtual overload.
- `CableNetwork.RemoveDevice(Device)` line 253892 -- a device leaves the network.
- `CableNetwork.Remove(Cable)` line 253966 -- a cable is pulled from the network (after `RefreshNetwork()`).
- `CableNetwork.Merge(CableNetwork oldNetwork)` line 253995 -- `oldNetwork.DirtyPowerAndDataDeviceLists()` as two networks fuse.
- `Device.InitializeDataConnection()` lines 350798-350799 -- dirties both `DataCableNetwork` and `PowerCableNetwork` when a device (re)derives its cable connections.

`DirtyDataDeviceList()` (data side only):

- `HandleDataNetTransmissionDevice` line 253652 -- the transmitter-push half of the rocket data-link relay dirties each receiver network during a refresh (see the next section).
- The `ITransmit` / `IReceiveDataNetworkDevices` implementers (`RocketDataDownLink` / `RocketDataUpLink`) lines 364937, 365077, 365094, 365182, 365333 -- on data-connection add / remove / config change.

Consequences:

- The lists rebuild on construction, destruction, cable add / remove, network merge / split, device connect, and data-link configuration: all structural events. A device's `Powered` flip (set every tick by the power tick) does NOT dirty either list; membership is independent of power state. So in steady state the cached `_dataDeviceList` is reused and IC10 batch reads (`lb` / `sb`, which walk `DataDeviceList`) incur no rebuild.
- A Harmony postfix on `RefreshPowerAndDataDeviceLists` (for example a logic-passthrough merge) therefore fires at structural-change cadence, not per tick. Its cost is amortized over topology edits, not paid on the frame path.
- The vanilla call sites dirty only the network that directly changed (plus the explicit `HandleDataNetTransmissionDevice` receiver-push). A mod that makes one network's data list depend on a DIFFERENT network's membership (transitive logic passthrough across a chain of bridge devices) gets no automatic invalidation when the far network changes; it must propagate the dirty itself. Per the accessor quirk below, `DirtyDataDeviceList()` alone will not force a rebuild on the next `DataDeviceList` read (the getter checks `PowerDeviceListDirty`), so a reliable cross-network invalidation must call `DirtyPowerAndDataDeviceLists()`.

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

## Field shape and accessor quirk
<!-- verified: 0.2.6228.27061 @ 2026-05-13 -->

The backing lists are declared `protected readonly`, which is reachable from a Harmony patch via field-injection by name (`___dataDeviceList` / `___powerDeviceList`). `readonly` only fixes the list reference; `.Add` / `.Remove` are still valid mutations. Verbatim, from decompile lines 253460-253468:

```csharp
public readonly List<Device> DeviceList = new List<Device>();
protected readonly List<Device> _dataDeviceList = new List<Device>();
protected readonly List<Device> _powerDeviceList = new List<Device>();
protected bool PowerDeviceListDirty;
protected bool DataDeviceListDirty;
```

The public property accessors (decompile lines 253519-253541) refresh on read:

```csharp
public List<Device> DataDeviceList
{
    get
    {
        if (PowerDeviceListDirty)        // checks the POWER flag, not the data flag
            RefreshPowerAndDataDeviceLists();
        return _dataDeviceList;
    }
}

public List<Device> PowerDeviceList
{
    get
    {
        if (PowerDeviceListDirty || DataDeviceListDirty)
            RefreshPowerAndDataDeviceLists();
        return _powerDeviceList;
    }
}
```

Both accessors return the underlying list reference (no copy), so mutations through the property are persistent.

Note the asymmetry: `DataDeviceList.get` refreshes when `PowerDeviceListDirty` is true and ignores `DataDeviceListDirty`. `PowerDeviceList.get` checks both flags. This is almost certainly a copy-paste leftover; in practice `DirtyDataDeviceList()` (which sets only the data flag) followed by a `DataDeviceList` read will return stale data unless something else (cable add/remove, power dirty) has happened to also set the power flag. Code that needs a fresh data list after a `DirtyDataDeviceList()` call should call `RefreshPowerAndDataDeviceLists()` explicitly. See Open Questions for the unresolved "is this a bug or a deliberate optimization" question.

## Lifecycle: three constructors + pool registration + multiplayer sync
<!-- verified: 0.2.6228.27061 @ 2026-05-15 -->

`CableNetwork` is tracked in `ConcurrentDensePool<CableNetwork> AllCableNetworks = new ConcurrentDensePool<CableNetwork>("AllCableNetworks", 4096)` (line 253430). The pool is the canonical "every network in the world" list. Instances are NOT acquired/returned from the pool via Get/Release in the call sites; the `new CableNetwork(...)` expressions seen below allocate fresh objects which then register themselves with `AllCableNetworks` via `AssignReference`. So each `new CableNetwork(...)` does go through one of three constructors, and Harmony postfixes on the constructors fire reliably.

The three constructors (lines 253735-253764):

```csharp
public CableNetwork()
{
    AssignReference(this, 0L);           // 0L = "assign a fresh ReferenceId from the global counter"
    CableNetworkType = CableNetworkType.CableNetwork;
    if (CableNetwork.OnNetworkChanged != null)
        CableNetwork.OnNetworkChanged();
}

public CableNetwork(long cableNetworkId)
{
    AssignReference(this, cableNetworkId); // use the explicit id; do NOT increment the counter
    CableNetworkType = CableNetworkType.CableNetwork;
    if (CableNetwork.OnNetworkChanged != null)
        CableNetwork.OnNetworkChanged();
}

public CableNetwork(Cable cable)
{
    AssignReference(this, 0L);           // fresh ReferenceId
    CableNetworkType = CableNetworkType.CableNetwork;
    Add(cable);                          // attaches the seed cable
    if (CableNetwork.OnNetworkChanged != null)
        CableNetwork.OnNetworkChanged();
}
```

Server vs client constructor usage:

- **Server creates new networks** via `new CableNetwork()` or `new CableNetwork(cable)`. Both call `AssignReference(this, 0L)`, which generates a fresh ReferenceId from the global counter. Example call site: `Cable.OnRegistered` at line 254055 (`CableNetwork cableNetwork2 = new CableNetwork(cable);`) when a placed cable opens a new network island.
- **Client recreates networks** via `new CableNetwork(referenceId)` from `DeserializeNew(RocketBinaryReader)` (line 254162). The factory reads a `CableNetworkType` byte and a packed `ReferenceId` from the wire, then switches on the type to construct either `new CableNetwork(referenceId)` or `new WirelessNetwork(referenceId)`. This is the `SyncList<CableNetwork> NewToSend = new SyncList<CableNetwork>(DeserializeNew)` (line 253491) factory.
- **Both sides recreate from saved id** via the `(long)` constructor: in `Cable` (line 371386), `(Referencable.Find<CableNetwork>(cableNetworkId) ?? new CableNetwork(cableNetworkId)).Add(this)` -- if a cable carries a deserialised `cableNetworkId` and no `CableNetwork` with that id is found, one is constructed with the saved id rather than a fresh one. Used during save load and during the join-time `Add` chain.

`DeserializeNew` verbatim (line 254162):

```csharp
private static void DeserializeNew(RocketBinaryReader reader)
{
    CableNetworkType cableNetworkType = (CableNetworkType)reader.ReadByte();
    Network.ReadPackedId(reader, out var referenceId);
    switch (cableNetworkType)
    {
        case CableNetworkType.CableNetwork:
            new CableNetwork(referenceId);
            break;
        case CableNetworkType.WirelessNetwork:
            new WirelessNetwork(referenceId);
            break;
        case CableNetworkType.None:
            break;
    }
}
```

Note the discard pattern: `new CableNetwork(referenceId)` allocates and the result is thrown away. `AssignReference(this, referenceId)` inside the constructor adds the instance to `AllCableNetworks` keyed by `referenceId`, so subsequent `Referencable.Find<CableNetwork>(referenceId)` lookups return the instance. The constructor's only externally-visible job on the client side is "register this id".

`OnNetworkChanged` is a static `Action` (no parameters) invoked at the tail of every constructor. Subscribers can react to "a network was created or recreated"; the event does not pass which network.

Pool / id-counter implications for mods:

- A Harmony postfix on any of the three `CableNetwork` constructors fires for both server-created and client-recreated networks. Posting state into the instance (e.g. injecting a replacement `PowerTick`) is therefore consistent on both sides.
- The `(long)` constructor is the ONLY path on a remote client that creates a `CableNetwork`. Anything that ends up on the client side that should know about a network must be reachable from this constructor postfix.
- Cable burns destroy cables and can split networks on the server. Each split creates a new `CableNetwork()` via the default constructor (fresh id from the counter). The server's id counter advances; clients only learn about the new id when the server replicates the split via `NewToSend.Send` -> `DeserializeNew`. If a server-side action (mod patch, voltage rule, anything that calls `cable.Break()` server-only) creates an extra network between two replicated states, the server's id counter will be ahead of the client's expectation by one until the next sync delivers the new id.
- The counter itself is per-runtime state, not derived from any synchronised seed. Server and client do NOT share a counter; the client relies entirely on the server-supplied `referenceId` from `DeserializeNew`. An id "drift" between server and client of N means the server has created N more networks than the client has received messages for.

## Merge: `cableNetworks[0]` wins, ConnectedNetworks defines the order
<!-- verified: 0.2.6228.27061 @ 2026-06-13 -->

When a placed cable bridges two or more existing networks, the surviving network is determined entirely by **list order**. The static factory at line 253998:

```csharp
public static CableNetwork Merge(List<CableNetwork> cableNetworks)
{
    if (cableNetworks.Count <= 0) return null;
    if (cableNetworks.Count == 1) return cableNetworks[0];
    CableNetwork cableNetwork = cableNetworks[0];
    for (int i = 1; i < cableNetworks.Count; i++)
        cableNetwork.Merge(cableNetworks[i]);
    return cableNetwork;
}
```

`cableNetworks[0]` is the survivor; every other network in the list is consumed via the instance-level `Merge`:

```csharp
public void Merge(CableNetwork oldNetwork)
{
    if (oldNetwork == null || oldNetwork == this) return;
    for (int num = oldNetwork.CableList.Count - 1; num >= 0; num--)
    {
        Cable cable = oldNetwork.CableList[num];
        if ((object)cable != null) Add(cable);
    }
    oldNetwork.CableList.Clear();
    oldNetwork.RefreshNetwork();
    oldNetwork.DirtyPowerAndDataDeviceLists();
}
```

The instance `Merge` walks `oldNetwork.CableList` in reverse and reassigns each cable into `this` via `Add`. The old network's cable list is cleared, its `RefreshNetwork` runs to flush internal state, and both lists' dirty flags are set.

`ConnectedNetworks` is where the order is established (line 254110):

```csharp
public static List<CableNetwork> ConnectedNetworks(Cable cable)
{
    List<Cable> list = cable.ConnectedCables();
    List<CableNetwork> list2 = new List<CableNetwork>(list.Count);
    foreach (Cable item in list)
    {
        if (!list2.Contains(item.CableNetwork))
            list2.Add(item.CableNetwork);
    }
    return list2;
}
```

So the order of networks in the merge list is the order of unique `CableNetwork` references encountered while iterating `cable.ConnectedCables()`. Two cables that belong to the same network are deduplicated; the first cable from each unique network "claims" the slot.

**Multiplayer-relevance implication.** Both server and client run the merge independently on their own side. `Cable.OnRegistered` runs on both sides (cable placement replicates), but its merge call is server-only via the `GameManager.RunSimulation` gate at line 371479. The client's runtime merge happens in `Cable.DeserializeOnJoin` at line 371405:

```csharp
public override void DeserializeOnJoin(RocketBinaryReader reader)
{
    base.DeserializeOnJoin(reader);
    CableNetworkId = reader.ReadInt64();
    CableNetwork cableNetwork = Referencable.Find<CableNetwork>(CableNetworkId);
    CableNetwork cableNetwork2 = cableNetwork;
    if (GameManager.GameState != GameState.Joining)
    {
        cableNetwork2 = CableNetwork.Merge(CableNetwork.ConnectedNetworks(this)) ?? cableNetwork;
    }
    cableNetwork2.Add(this);
}
```

`DeserializeOnJoin` here is the per-cable wire deserialise. The `!Joining` gate means: during initial join, accept the server's network id verbatim; during normal runtime, re-derive the merge survivor locally via `ConnectedNetworks`. Both sides therefore run their OWN `Merge(ConnectedNetworks(cable))` on the placed cable. If `cable.ConnectedCables()` returns adjacent cables in a different order on server vs client at the moment of merge, the chosen survivors differ. The two pre-merge ids both still resolve to live `CableNetwork` instances on the side that did NOT consume them (`Merge` only destroys the non-survivors LOCALLY on each side), producing a stable "server sees id X, client sees id Y" desync that persists indefinitely until a future merge reconciles via a different cable registration.

**ConnectedCables iteration order (verified verbatim).** `Cable.ConnectedCables()` at decompile line 294267 (in `SmallGrid`):

```csharp
public List<Cable> ConnectedCables()
{
    FoundCables.Clear();
    foreach (Connection openEnd in OpenEnds)
    {
        Grid3 localGrid = base.GridController.WorldToLocalGrid(
            openEnd.Transform.position, SmallGridSize, SmallGridOffset);
        SmallCell smallCell = base.GridController.GetSmallCell(localGrid);
        if (smallCell != null && smallCell.Cable != null
            && smallCell.Cable != this
            && smallCell.Cable.IsConnected(openEnd))
        {
            FoundCables.Add(smallCell.Cable);
        }
    }
    return FoundCables;
}
```

Key facts:

- `FoundCables` is a **shared static reusable list** -- cleared at the top, populated and returned by value-share. Calling `ConnectedCables()` twice on different cables in rapid succession overwrites the previous result. Worker-thread access from outside the game thread would corrupt it.
- The result order is the `OpenEnds` iteration order: for each OpenEnd in declaration order, the cable at the cell adjacent to that OpenEnd is appended (if any, and if it agrees via `smallCell.Cable.IsConnected(openEnd)`).
- `OpenEnds` itself is the prefab-serialised list of connection points on the cable; identical on both sides for the same prefab orientation.

So under vanilla mechanics, two consecutive runs of `cable.ConnectedCables()` on the same cable from the same logical game-state point should produce identical orderings on both server and client. Any divergence implies one of:

- A different physical OpenEnds list (different cable prefab rotation/orientation on the two sides, which would also cause many other visible discrepancies -- unlikely without a much louder symptom).
- A different `smallCell.Cable` at the same grid position on the two sides (e.g. one side has already updated the cell to point at the new cable, the other hasn't).
- A different return value from `smallCell.Cable.IsConnected(openEnd)` for one of the neighbours (e.g. that neighbour's network/state hasn't been refreshed on one side).
- `FoundCables` being overwritten by a concurrent caller between the two sides' runs (only matters if a thread other than the game thread calls `ConnectedCables` -- the host's power tick runs on a worker but the client never runs the tick).

`ConnectedCables(NetworkType networkType)` at line 294319 is a separate overload that uses a fresh `new List<Cable>(4)` per call. Mods filtering by `NetworkType.Power` (e.g. our `VoltageTier.HasHigherTierNeighbour`) use this overload and cannot corrupt `FoundCables`. **Buffer-safe is not thread-safe, though:** this overload still reads `openEnd.Transform.position` at line 294326 (verbatim: `Grid3 localGrid = base.GridController.WorldToLocalGrid(openEnd.Transform.position, SmallGridSize, SmallGridOffset);`), exactly like the parameterless overload at 294272. A Unity `Transform` position getter is main-thread-only, so calling EITHER overload from the power-tick worker thread (`ThreadedManager.IsThread` true) is unsafe -- the game routes around this on the worker thread via the cached `Connection.LocalGrid` (`SmallGrid.Connected()` and `ElectricalInputOutput.OnSubmergeableTick` use `LocalGrid`; `Connection.Initialize()` early-returns the cached `_isInitialized` value without touching the Transform when `IsThread`). Power Grid Plus's `VoltageTier.HasHigherTierNeighbour` wraps its `ConnectedCables(NetworkType.Power)` call in a `try/catch` that returns `false` on throw precisely because it runs inside Phase 1.5a on that worker thread; the consequence is degraded boundary targeting (fall through to "any lowest-tier cable") rather than a crash. The same `Transform.position` coupling is why vanilla's split BFS `RebuildNetwork` (which calls `ConnectedCables()` per dequeued cable) cannot be invoked from the worker thread to make a cable burn's network split land in the same tick; see [Cable](./Cable.md), "Network split on destruction".

**Calls inside `Add()` that could re-enter the cable graph** (decompile line 253923):

```csharp
public void Add(Cable cable)
{
    ...
    foreach (Device item in cable.ConnectedDevices())   // returns static FoundDevices-equivalent
    {
        AddDevice(cable, item);
        item.FindDataCable();        // -> ConnectedCables(NetworkType.Data)  -- safe overload
    }
    cable.CableNetworkId = ReferenceId;
    if (CableNetwork.OnNetworkChanged != null) CableNetwork.OnNetworkChanged();
}
```

`Device.FindDataCable()` (decompile line 350763) uses the **fresh-list** `ConnectedCables(NetworkType.Data)` overload, so it does NOT touch the static `FoundCables`:

```csharp
public void FindDataCable()
{
    DataCables = ConnectedCables(NetworkType.Data);   // fresh new List<Cable>(4)
    ...
}
```

Same for `Device.FindPowerCable()` (line 350778, uses `ConnectedCables(NetworkType.Power)`) and `Device.InitializeDataConnection()` (line 350794, which calls both Find methods then dirties both networks). So the `Add()` walk done by `Merge` cannot reentrantly clobber the outer caller's `FoundCables` -- the only path from inside `Merge` that re-touches the cable graph goes through the safe overload.

`RebuildCableNetworkServer` at line 254050 (called from the destruction side of the lifecycle) is explicitly server-only by name. `RebuildNetwork` (private, line 254016) is the BFS used to re-fold cables into a target network after a split or a forced rebuild; both calls fire `OnNetworkChanged`.

## Split is authoritative; merge is not. Asymmetric multiplayer sync
<!-- verified: 0.2.6228.27061 @ 2026-05-15 -->

The split (destruction / fuse-break) and merge (placement) paths sync differently across the wire. This is the structural reason a cable-id desync can persist across many ticks.

**Split path (authoritative, ids synced)** -- `RebuildCableNetworkServer` (line 254050):

```csharp
public static void RebuildCableNetworkServer(Cable cable)
{
    if (GameManager.GameState != GameState.None)
    {
        CableNetwork cableNetwork = cable.CableNetwork;          // old (will be split)
        CableNetwork cableNetwork2 = new CableNetwork(cable);    // new (server-allocated id)
        if (Assets.Scripts.Networking.NetworkManager.IsServer && NetworkServer.HasClients())
        {
            RebuildCableNetworkEvent.NewEvents.Add(
                new RebuildCableNetworkEvent(
                    cable.ReferenceId,
                    cableNetwork2.ReferenceId,    // new id
                    cableNetwork.ReferenceId));   // old id
        }
        RebuildNetwork(cable, cableNetwork2, cableNetwork);
    }
}
```

The server allocates `new CableNetwork(cable)` (which increments the server-only ReferenceId counter) and emits `RebuildCableNetworkEvent` carrying both the new and old ids. The client receives this event and runs `RebuildCableNetworkClient(cable, newNetwork, oldNetwork)` (line 254064) which delegates to the same `RebuildNetwork` BFS using **the server's chosen ids**. Splits cannot desync.

**Merge path (each side decides independently, no event)** -- `Cable.OnRegistered` (line 371477) on the server runs:

```csharp
public override void OnRegistered(Cell cell)
{
    if (GameManager.GameState != GameState.Loading && GameManager.RunSimulation)
    {
        CableNetwork cableNetwork = CableNetwork.Merge(CableNetwork.ConnectedNetworks(this));
        if (cableNetwork != null) cableNetwork.Add(this);
        else                       new CableNetwork(this);
    }
    base.OnRegistered(cell);
}
```

and the client runs `Cable.DeserializeOnJoin` (line 371405), already excerpted above. No `RebuildCableNetworkEvent`-equivalent fires for merges. The server's chosen survivor is implicitly carried in the cable's `CableNetworkId` field on the wire, **but the client discards it** in `DeserializeOnJoin` whenever `GameManager.GameState != GameState.Joining` and instead recomputes `Merge(ConnectedNetworks(this))` locally. The local recomputation is what desyncs.

**`RebuildCableNetworkEvent` wire shape and client apply path** (decompile line 255178):

```csharp
public readonly struct RebuildCableNetworkEvent(long originNetworkedStructureReference, long newNetworkReference, long oldNetworkReference) : ISyncListable
{
    public readonly long OriginNetworkedStructureReference = ...;
    public readonly long NewNetworkReference = ...;
    public readonly long OldNetworkReference = ...;
    public static SyncList<RebuildCableNetworkEvent> NewEvents = new SyncList<RebuildCableNetworkEvent>(DeserializeEvent);

    public void Serialize(RocketBinaryWriter writer) {
        Network.WritePackedId(writer, OriginNetworkedStructureReference);
        Network.WritePackedId(writer, NewNetworkReference);
        Network.WritePackedId(writer, OldNetworkReference);
    }

    private static void DeserializeEvent(RocketBinaryReader reader) {
        Network.ReadPackedId(reader, out var referenceId);
        Network.ReadPackedId(reader, out var referenceId2);
        Network.ReadPackedId(reader, out var referenceId3);
        Cable cable = Referencable.Find<Cable>(referenceId);
        CableNetwork newNetwork = Referencable.Find<CableNetwork>(referenceId2);
        CableNetwork oldNetwork = Referencable.Find<CableNetwork>(referenceId3);
        CableNetwork.RebuildCableNetworkClient(cable, newNetwork, oldNetwork);
    }
}
```

The client `Referencable.Find<CableNetwork>(referenceId2)` lookup for the NEW network depends on that network having been previously delivered via `SyncList<CableNetwork> NewToSend`. If the SyncList ordering puts the rebuild event before the network creation, `newNetwork` is null and `RebuildCableNetworkClient` exits early with `ConsoleWindow.PrintError("Cable or Network null during rebuild")` (line 254070). Same hazard applies to `cable` (must arrive via thing-sync first) and `oldNetwork` (must still be alive). The serializer at line 197579 (`RebuildCableNetworkEvent.NewEvents.Serialize(writer)`) is part of the per-tick MessageFactory packet, so all three syncs (NewToSend, thing-sync, RebuildEvent) ride the same packet -- but their per-stream order is what determines whether the client's `Referencable.Find` succeeds when the event applies.

**Vanilla survivability vs mod-driven desync.** Under vanilla mechanics the iteration is expected to match: same OpenEnds, same SmallCells (network state is deterministic), same `IsConnected` results, same `FoundCables` (the static reusable list is only the game thread's caller on vanilla since the power tick runs server-side only and even the host does not run merges on a worker). So vanilla mostly "gets lucky" with deterministic ordering -- the structural bug is latent. Once a mod perturbs anything that touches the moment-of-merge state of an adjacent cable's network reference, or causes an additional construction event whose ordering differs between server and client, the latent bug surfaces and stays stuck: each side has already picked its winner and the loser network instance is still alive on the OTHER side, so no future tick alone reconciles them.

## Resolution: deterministic Merge sort makes the survivor independent of ConnectedCables order
<!-- verified: 0.2.6228.27061 @ 2026-05-18 -->

The structural fragility above is closed by a Harmony prefix that sorts the input list to `Merge(List<CableNetwork>)` by `ReferenceId` ascending before vanilla picks `list[0]`. The same fix applies to the parallel `StructureNetwork.Merge(List<StructureNetwork>, out StructureNetwork)` overload (decompile line 177412), which has the identical "list[0] wins" shape and is reached by `RocketNetwork`, `RoboticArmNetwork`, `LandingPadNetwork`, and (via the `StructureNetwork` base class) any other static-network type that has an `INetworkedStructure.DeserializeOnJoin` that re-runs a local merge. `WirelessNetwork` inherits from `CableNetwork` and so is covered by the `CableNetwork.Merge` patch.

ReferenceIds are server-allocated and identical on every peer for the same network instance (see "Lifecycle: three constructors" above). Sorting by them makes survivor selection a pure function of the network identifiers rather than `ConnectedCables` iteration order, so server and client converge on the same survivor regardless of whether `OpenEnds` ordering, `smallCell.Cable` state, or any other adjacency input differs between the two sides.

Both `Merge(List)` overloads only iterate the input list (they never mutate it), and every call site passes a fresh `ConnectedNetworks(thing)` allocation (`CableNetwork.ConnectedNetworks` line 254113, `StructureNetwork.ConnectedNetworks` line 177115 -- both allocate a new `List<>` per call), so in-place sort inside the prefix is safe. The instance `Merge(other)` is the recursive consumer and does not call back to the static `Merge(List)`, so a Harmony prefix on the static method does not re-enter.

The fix lives in Power Grid Plus as `Patches/MergeDeterminismPatches.cs`. It is independent of the upstream cause of any ordering divergence: even if some future mod or vanilla code path causes `ConnectedCables` to disagree across the wire, the merge id-level outcome stays in sync. The cosmetic rotation of the placed cable may still differ visually between sides if the upstream cause is in cable-rotation handling, but the `CableNetwork.ReferenceId` (and therefore power-tick / logic-tick membership) converges.

Survivor-selection rule change: vanilla picked whichever network's first cable was first in `ConnectedCables` iteration order, which was usually-but-not-always the larger/older network. The new rule picks the lowest `ReferenceId`. Older networks have lower ids, so in steady-state play the rule tends to preserve the same survivor vanilla would have picked under non-divergent conditions. In the smoking-gun reproduction the server picked 386495 (6 cables, higher id) and the client picked 386494 (269 cables, lower id); after the fix both sides pick 386494 -- the lower id, which was also the larger of the two pre-merge networks.

**`OnRegistered` order-of-operations.** Note that `base.OnRegistered(cell)` (which is the `SmallGrid` / `Thing` chain that writes `smallCell.Cable = this` for the new cable's own cell) runs AFTER the merge call. So at the moment of `ConnectedNetworks(this)`, the new cable is **not yet in its own SmallCell**. The `smallCell.Cable != this` filter in `ConnectedCables` is therefore vacuously satisfied on the server's first run. The client's `DeserializeOnJoin` path does not have this guarantee: by the time `cableNetwork2.Add(this)` runs at the end of `DeserializeOnJoin`, base classes have already executed `base.DeserializeOnJoin(reader)` which may have completed registration paths that the server has not yet performed at the equivalent point. Investigating which exact base method writes the SmallCell pointer, and whether it differs between the construction and join paths, is the next decomp step.

**`SmallGrid.IsConnected(Connection)` semantics** (line 294154, two overloads at 294154 and 294171):

```csharp
public virtual bool IsConnected(Connection otherEnd)
{
    Grid3 facingGrid = otherEnd.GetFacingGrid();
    foreach (Connection openEnd in OpenEnds)
    {
        if ((openEnd.ConnectionType & otherEnd.ConnectionType) != NetworkType.None)
        {
            Grid3 localGrid = openEnd.GetLocalGrid();
            if (facingGrid == localGrid) return true;
        }
    }
    return false;
}
```

A neighbour is considered "connected" if **any** of its OpenEnds shares a NetworkType bit with the caller's OpenEnd AND its local grid equals the caller's facing grid. The bitwise `&` means a Data-only connection and a Power-only connection do not pass the filter; a Power+Data cable connecting to a Power+Data port does. Both overloads short-circuit on the first match; only the second returns the actual `connectedEnd`. Since the result depends solely on the neighbour cable's `OpenEnds` and the caller's `otherEnd` -- both prefab-determined transform state -- this filter is deterministic across server and client as long as cable orientation matches.

## Verification history
<!-- verified: 0.2.6228.27061 @ 2026-05-15 -->

- 2026-05-02: page created. Sourced from a long-distance auto-aim test on the Lunar save: seven TX-RX pairs at 163-222 m all linked successfully (verified via InspectorPlus DishProbe), but only one RX showed `Powered=True` and `PowerProvided > 0`. Reading `CableNetwork.ConsumePower` in Assembly-CSharp.dll (decompile lines 254579-254654) confirmed the single-supplier-first iteration, identifying the observed asymmetry as expected vanilla behaviour for parallel receivers on a shared destination network.
- 2026-05-13: added "Data device list" and "HandleDataNetTransmissionDevice" sections, sourced from `Assembly-CSharp.dll` decompile lines 253589-253655 (refresh + relay) and 364740-365158 (interfaces and rocket-link implementers). Added `logic` and `network` tags. No conflict with the existing power-side content. Findings produced while researching whether transformers and APCs can be made logic-transparent for Power Grid Plus.
- 2026-05-13: added "Field shape and accessor quirk" section, sourced from `Assembly-CSharp.dll` decompile lines 253460-253541. Notes the `protected readonly` declarations (Harmony-reachable by name) and the `DataDeviceList.get` asymmetry that checks `PowerDeviceListDirty` instead of `DataDeviceListDirty`.
- 2026-05-15: added "Lifecycle: three constructors + pool registration + multiplayer sync" section, sourced from decompile lines 253430 (`ConcurrentDensePool<CableNetwork> AllCableNetworks`), 253491 (`SyncList<CableNetwork> NewToSend`), 253735-253764 (the three constructors), 254162 (`DeserializeNew` factory), 254055 (`Cable.OnRegistered` `new CableNetwork(cable)` call site), and 371386 (`Cable.Add` `(Referencable.Find<CableNetwork>(cableNetworkId) ?? new CableNetwork(cableNetworkId))`). Documents the server-only ReferenceId counter, the client's exclusive use of the `(long)` constructor via `DeserializeNew`, and the implication that mod-driven server-side network creation between replicated states advances the server's counter past the client's id horizon.
- 2026-05-15: added "Merge: `cableNetworks[0]` wins, ConnectedNetworks defines the order" section, sourced from decompile lines 253979 (instance `Merge`), 253998 (static `Merge`), 254110 (`ConnectedNetworks`), 254016 (private `RebuildNetwork`), 254050 (`RebuildCableNetworkServer`), 371413 (`Cable.OnRegistered` merge call site). Documents that merge survivor is list-index-zero, that list order comes from `cable.ConnectedCables()`, and the multiplayer implication that any perturbation of `ConnectedCables` ordering between server and client produces a stable id desync that persists across many ticks (both pre-merge ids stay live, one on each side).
- 2026-05-15: expanded the multiplayer-relevance section with the verbatim `Cable.DeserializeOnJoin` body (line 371405). Documents that `Cable.OnRegistered`'s merge is server-only (`GameManager.RunSimulation` gate) while the client's runtime merge happens in `DeserializeOnJoin` (gated by `GameManager.GameState != GameState.Joining` -- merge is skipped during the initial join handshake, runs during normal runtime updates). Both sides therefore run their own `Merge(ConnectedNetworks(cable))` independently. Implications: a desync that depends on `ConnectedCables` ordering between sides cannot be resolved by "the server is authoritative" because the client is making its own merge decision at runtime.
- 2026-05-15: added the verbatim `SmallGrid.ConnectedCables()` body (decompile line 294267) with notes on `FoundCables` being a shared static reusable list, OpenEnds-iteration ordering, and the four mechanisms by which two sides could produce different orderings (different OpenEnds, different `smallCell.Cable`, different `IsConnected` result, or `FoundCables` corruption by a concurrent caller). Notes that `ConnectedCables(NetworkType)` at line 294319 is a different overload using a fresh per-call list.
- 2026-05-15: added "Split is authoritative; merge is not. Asymmetric multiplayer sync" section, sourced from decompile lines 254050-254076 (`RebuildCableNetworkServer` and `RebuildCableNetworkClient`), 371477-371491 (`Cable.OnRegistered`), 371405-371416 (`Cable.DeserializeOnJoin`), 294154-294188 (both `SmallGrid.IsConnected(Connection)` overloads). Documents that `RebuildCableNetworkEvent` carries the server's chosen `newNetwork.ReferenceId` + `oldNetwork.ReferenceId` to the client (splits are id-authoritative), while merges have no equivalent event and the client always re-runs `Merge(ConnectedNetworks(this))` locally after `GameState != Joining`. This is the structural source of a stable cable-network-id desync after a merge: each side's loser network instance is still alive on the OTHER side, so the discrepancy persists indefinitely. Notes `Cable.OnRegistered`'s `base.OnRegistered(cell)`-after-merge ordering means the new cable is not yet in its own SmallCell at the moment of `ConnectedNetworks(this)` on the server, while the equivalent guarantee for the client's `DeserializeOnJoin` path is not yet established.
- 2026-05-15: added the verbatim `RebuildCableNetworkEvent` struct + `DeserializeEvent` body (decompile line 255178-255205) inside the split-vs-merge section. Documents the wire shape (three packed `long` ids: origin cable, new network, old network), the per-tick serializer at line 197579, and the per-stream ordering hazard: the client's `Referencable.Find<CableNetwork>(NewNetworkReference)` requires the matching `SyncList<CableNetwork> NewToSend` delivery to have already occurred, otherwise `RebuildCableNetworkClient` early-returns with a `Cable or Network null during rebuild` console error (line 254070).
- 2026-05-15: extended the ConnectedCables-iteration-order section with verification that the `Add()` walk's `cable.ConnectedDevices()` and `item.FindDataCable()` calls do NOT re-enter the static `FoundCables`. `Device.FindDataCable` (decompile line 350763), `Device.FindPowerCable` (line 350778), and `Device.InitializeDataConnection` (line 350794) all use the `ConnectedCables(NetworkType)` overload (line 294319) which allocates a fresh `new List<Cable>(4)` per call. So the only path from inside `Merge -> Add` that re-touches the cable graph is via the safe overload; the static `FoundCables` cannot be reentrantly clobbered by the merge's own internal walks.
- 2026-05-18: corrected the page-intro namespace claim. `CableNetwork` is in `Assets.Scripts.Networks` (decompile lines 253403 / 253411), NOT `Assets.Scripts.Objects.Electrical` as previously stated. Sibling base `StructureNetwork` is in a different top-level namespace called just `Networks` (decompile lines 175608 / 177045). Confirmed by a Power Grid Plus build that needed `using Networks;` separately from `using Assets.Scripts.Networks;` to resolve both types.
- 2026-05-18: added "Resolution: deterministic Merge sort" section. Confirmed via the full decompile that the bug was vanilla-structural, not Power Grid Plus -- the original 2026-05-15 "disappears when Power Grid Plus is disabled" observation was symptomatic of Power Grid Plus's tier-burn mechanic generating enough cable destroy/place churn to expose the latent ordering bug, not a Power Grid Plus patch directly mutating cable state on the client. Reviewed all 15 Power Grid Plus patches, plus the NetworkPuristPlus and SprayPaintPlus patches that touch cable / glow state: every state-mutating path is either caller-gated on `GameManager.RunSimulation` (host-only) or mode-gated on a thread-static set inside a `RunSimulation` branch. None mutate cable rotation, OpenEnds, or network state on a client instance. The remaining open question is upstream-cause-of-rotation-divergence, narrowed below. Sourced from decompile lines 253998-254014 (`CableNetwork.Merge(List)`), 177412-177430 (`StructureNetwork.Merge(List, out)`), 254113 (`CableNetwork.ConnectedNetworks`), 177115 (`StructureNetwork.ConnectedNetworks`), 150889 / 162600 (`INetworkedRocketPart` / `INetworkedRoboticArm` `DeserializeOnJoin` client-side local merge call sites).
- 2026-06-13: extended the ConnectedCables-iteration-order note in the **Merge** section to record that the `ConnectedCables(NetworkType)` overload (line 294319) ALSO reads `openEnd.Transform.position` (line 294326), so it is buffer-safe (fresh list) but NOT worker-thread-safe -- the same main-thread-only Transform coupling as the parameterless overload. Documents that the game routes around this on the worker thread via cached `Connection.LocalGrid`, that Power Grid Plus's `VoltageTier.HasHigherTierNeighbour` wraps its call in a `try/catch` for exactly this reason (degraded boundary targeting on the worker thread, not a crash), and that this coupling is why vanilla's `RebuildNetwork` split BFS cannot be invoked from the power-tick worker thread to land a split in-tick. Sourced from `.work/decomp/0.2.6228.27061/Assembly-CSharp.decompiled.cs` lines 294267-294335 (both `ConnectedCables` overloads verbatim). Produced while evaluating whether Power Grid Plus could make a cable burn's network split land in the same tick. Cross-links the new [Cable](./Cable.md) "Network split on destruction" section.
- 2026-05-21: added "Refresh cadence: device lists dirty only on structural change, never per tick" section. Enumerated all `DirtyPowerAndDataDeviceLists()` / `DirtyDataDeviceList()` call sites from the decompile (lines 253652, 253849, 253858, 253892, 253966, 253995, 350798-350799, 364937, 365077, 365094, 365182, 365333): every one is a membership / connectivity / data-link-config event, none per-tick. Additive (no existing claim contradicted). Documents that a postfix on `RefreshPowerAndDataDeviceLists` runs at topology-change cadence and that transitive cross-network logic passthrough must propagate its own dirty. Produced while designing multi-hop logic passthrough for Power Grid Plus.
- 2026-06-20: added "Consumer: the Network Analyser reads the raw `DeviceList`, not `DataDeviceList`" section, sourced from `Assembly-CSharp` decompile lines 331171 (`NetworkAnalyser : Cartridge`), 331205 (`GetScannedNetwork`), and 331273-331317 (`OnPreScreenUpdate` reading `_scannedNetwork.DeviceList.Count` at 331287 and iterating `_scannedNetwork.DeviceList` at 331295). Additive; no existing claim contradicted. Documents that the analyser screen shows physical `DeviceList` membership only, so devices merged into `_dataDeviceList` (the rocket data-link relay, or a mod's logic-passthrough) do not appear there unless `NetworkAnalyser.OnPreScreenUpdate` is also patched. Found while analyzing the third-party OmniLink mod, which pairs a `DataDeviceList`-getter postfix with a `NetworkAnalyser.OnPreScreenUpdate` transpiler for exactly this reason.

## Open questions

- Is the `DataDeviceList.get` accessor checking `PowerDeviceListDirty` instead of `DataDeviceListDirty` a vanilla bug or an intentional optimization tied to the fact that power dirties typically co-occur with data dirties? Recommend treating it as a bug and refreshing explicitly when only the data flag is set.
- What in the multiplayer cable-spawn pipeline causes the client's instantiated cable rotation to be 180 degrees off the server's, when the player's cursor preview matched the server's eventual rotation? Reproduced 2026-05-15: cable rid=386513 lands at world position (-1292.0, 206.0, -780.0) with PowerDataConnection1 east / PowerDataConnection2 west on the server and the mirrored arrangement on the client (verified via `BUG2 CLT Cable.OnRegistered.PRE` OpenEnd dumps). The deterministic Merge sort fix above removes the consequence at the network-id level (both sides converge on the same survivor), but the underlying rotation desync still produces a cosmetic visual difference. The state-mutation audit completed 2026-05-18 ruled out Power Grid Plus, NetworkPuristPlus, and SprayPaintPlus as the cause. Candidates remaining: (1) `Cable.SerializeOnJoin` / `Cable.DeserializeOnJoin` not preserving rotation faithfully when the join packet captures a cable that the server has already canonicalised but the per-tick bit-1 delta has not yet caught up to deliver -- the cable could arrive at the client at its pre-canonical rotation while the server's instance has already been re-rolled; (2) a third-party Workshop mod in the test session perturbing cable rotation on the client side; (3) latency-sensitive interaction between the bit-1 per-tick rotation delta and the SyncList delivery ordering of the new cable. Diagnostic next-step: log the server-side `Cable.SerializeOnJoin` rotation argument and the client-side `Cable.DeserializeOnJoin` rotation result for the same cable, see whether the rotation arrived correctly on the wire or whether it was already wrong at deserialisation time.
