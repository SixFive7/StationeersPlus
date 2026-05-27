---
title: Motherboard device-list refresh
type: GameClasses
created_in: 0.2.6228.27061
verified_in: 0.2.6228.27061
verified_at: 2026-05-28
sources:
  - .work/decomp/0.2.6228.27061/Assembly-CSharp.decompiled.cs:315578-315744 (LogicMotherboard)
  - .work/decomp/0.2.6228.27061/Assembly-CSharp.decompiled.cs:317577-317765 (ProgrammableChipMotherboard)
  - .work/decomp/0.2.6228.27061/Assembly-CSharp.decompiled.cs:330588-331074 (Motherboard base)
  - .work/decomp/0.2.6228.27061/Assembly-CSharp.decompiled.cs:372732-372884 (Computer host forwarding)
  - .work/decomp/0.2.6228.27061/Assembly-CSharp.decompiled.cs:253519-253661 (CableNetwork.DataDeviceList / DirtyPowerAndDataDeviceLists)
related:
  - ./CableNetwork.md
  - ../GameSystems/ScreenDropdownBase.md
  - ../GameSystems/IC10DeviceAddressing.md
tags: [ui, logic, network]
---

# Motherboard device-list refresh

A Computer (Console, Big Screen, Wall-Mounted Screen) hosts one `Motherboard` cartridge. Several motherboard types show a dropdown / list of the logic-addressable devices reachable on the host's data network: `LogicMotherboard` (the visual if/then editor) lists them in its condition/action device dropdowns; `ProgrammableChipMotherboard` (the IC Editor) lists them in its device `ButtonDropdown` and chip list. That list is a cached snapshot rebuilt only on specific events. It does NOT observe the underlying `CableNetwork` data-device-list dirty flags, so a change that only invalidates the network's cached device list (without firing a device connect/disconnect or rename event) leaves the motherboard's dropdown stale until the next rebuild trigger.

## Device-list source chain
<!-- verified: 0.2.6228.27061 @ 2026-05-28 -->

Both motherboard types source their list from the host computer's data cable network. `LogicMotherboard.RebuildDeviceList` (`Assembly-CSharp.decompiled.cs:315637`):

```csharp
private void RebuildDeviceList()
{
    DeviceOptionList.Clear();
    if (ParentComputer == null || !ParentComputer.DataCable || ParentComputer.DataCable.CableNetwork == null)
    {
        DisplayedDevices.Clear();
    }
    else
    {
        DisplayedDevices = new List<Device>(ParentComputer.DataCable.CableNetwork.DataDeviceList);
        for (int i = 0; i < DisplayedDevices.Count; i++)
        {
            DeviceOptionList.Add(new Dropdown.OptionData(DisplayedDevices[i].DisplayName));
        }
    }
    foreach (LogicState logicState in LogicStates)
    {
        logicState.OnDeviceListUpdated();
    }
}
```

`ProgrammableChipMotherboard` reads the same network via `Computer.DeviceList()` (`Assembly-CSharp.decompiled.cs:372732`):

```csharp
public List<ILogicable> DeviceList()
{
    if ((object)base.DataCable == null) FindDataCable();
    if (!(base.DataCable != null) || base.DataCable.CableNetwork == null)
        return new List<ILogicable>();
    return new List<ILogicable>(base.DataCable.CableNetwork.DataDeviceList);
}
```

The terminal source for both is `CableNetwork.DataDeviceList` (`Assembly-CSharp.decompiled.cs:253519`), which is itself lazily rebuilt and carries the wrong-dirty-flag quirk documented in [CableNetwork.md](./CableNetwork.md) ("Field shape and accessor quirk"): the getter refreshes on `PowerDeviceListDirty`, not `DataDeviceListDirty`. The motherboard copies the list into its own `DisplayedDevices` / `DeviceOptionList` (or the IC Editor's chip list), so even once `DataDeviceList` is correct, the motherboard's copy is only as fresh as its last rebuild call.

## Rebuild triggers
<!-- verified: 0.2.6228.27061 @ 2026-05-28 -->

`LogicMotherboard` rebuilds in exactly two places (`Assembly-CSharp.decompiled.cs:315724` and `315733`):

```csharp
public override void OnDeviceListChanged()
{
    base.OnDeviceListChanged();
    if (GameManager.GameState == GameState.Running)
        RebuildDeviceList();
}

public override void OnInsertedToComputer(IComputer computer)
{
    base.OnInsertedToComputer(computer);
    if (GameManager.GameState == GameState.Running)
    {
        if (CurrentLogicState != null && CurrentLogicState.Enabled)
            CurrentLogicState.IsTriggered = false;
        RebuildDeviceList();
    }
}
```

`ProgrammableChipMotherboard` mirrors this with a `_DevicesChanged` flag and an async `HandleDeviceListChange()` (`Assembly-CSharp.decompiled.cs:317743`, `317753`): `OnDeviceListChanged()` and `OnInsertedToComputer()` both set `_DevicesChanged = true` and call `HandleDeviceListChange().Forget()`. On a client, `ProcessUpdate` also calls `HandleDeviceListChange()` when it receives the serialized `_DevicesChanged` flag (network update flag `256`, `Assembly-CSharp.decompiled.cs:317673`). The server only sets that flag from `SendUpdate()`, which is reached from `OnInsertedToComputer` / `OnRemovedFromComputer` (gated on `GameManager.RunSimulation`) - i.e. slot changes, not network topology changes.

So for both motherboard types the rebuild fires on: motherboard insertion/removal (`OnInsertedToComputer`), and `OnDeviceListChanged()`. There is no polling and no per-frame refresh of the device list (`LogicMotherboard.OnThreadUpdate` / `UpdateEachFrame` re-assess conditions, but never call `RebuildDeviceList`).

## Computer host forwards OnDeviceListChanged
<!-- verified: 0.2.6228.27061 @ 2026-05-28 -->

`OnDeviceListChanged()` on the motherboard is driven by the host `Computer`, which forwards to `CurrentMotherboard.OnDeviceListChanged()` from these events (`Assembly-CSharp.decompiled.cs:372779-372884`):

- `OnRenamed` (line 372782) - the computer itself was renamed.
- an async `await UniTask.NextFrame()` handler (line 372819).
- `OnNetworkedDeviceNameChanged` (line 372846) - a device on the network was renamed.
- two `FindDataCable()` paths (lines 372856, 372866) - the host (re)discovered its data cable.
- `OnDeviceConnectToNetwork(device)` (line 372875) - a device joined the data network.
- `OnDeviceDisconnectFromNetwork(device)` (line 372884) - a device left the data network.

The base `Motherboard.OnDeviceListChanged()` is virtual and empty (`Assembly-CSharp.decompiled.cs:331066`); `Motherboard.OnSlaveStateChange()` (line 330866) also calls it. The common thread: every trigger is a physical topology change (device added/removed from the cable network), an identity change (rename), a cable rediscovery, or a slot/slave action. A change that alters only which devices are *logically reachable* without adding/removing a device from the cable network, and without a rename or cable rediscovery, fires none of these.

## The refresh gap: dirty-only invalidation does not refresh the dropdown
<!-- verified: 0.2.6228.27061 @ 2026-05-28 -->

`CableNetwork.DirtyPowerAndDataDeviceLists()` (`Assembly-CSharp.decompiled.cs:253657`) sets only the two dirty flags:

```csharp
public void DirtyPowerAndDataDeviceLists()
{
    PowerDeviceListDirty = true;
    DataDeviceListDirty = true;
}
```

It does not call `OnDeviceListChanged()` on any device, does not call `OnDeviceConnectToNetwork` / `OnDeviceDisconnectFromNetwork`, and the motherboard does not subscribe to the dirty flags or to any network-changed event. Neither `LogicMotherboard` nor `ProgrammableChipMotherboard` overrides `OnDeviceConnectToNetwork`. Consequently, code that mutates the *contents* of `DataDeviceList` (for example, a Harmony postfix on `CableNetwork.RefreshPowerAndDataDeviceLists` that appends extra devices, gated by a runtime mode flag) and then calls `DirtyPowerAndDataDeviceLists()` will make `DataDeviceList` itself correct on the next read, but will NOT cause the host computer to re-fire `OnDeviceListChanged()`. The motherboard's `DisplayedDevices` / chip-list copy stays stale.

Reinserting the motherboard ("replug") is the reliable manual refresh because `Computer` calls `CurrentMotherboard.OnInsertedToComputer(this)` on insertion, which calls `RebuildDeviceList()` / `HandleDeviceListChange()` directly, re-reading `DataDeviceList` from scratch.

To force a refresh without a replug, a caller must invoke a path that reaches `Motherboard.OnDeviceListChanged()` on the host computer's `CurrentMotherboard` - for example calling the host's `OnDeviceConnectToNetwork(device)` forwarder, or calling `CurrentMotherboard.OnDeviceListChanged()` directly on each affected computer after the device set changes.

## Vanilla consumer-notification cascade: RefreshNetworkDevice
<!-- verified: 0.2.6228.27061 @ 2026-05-28 -->

The path that tells consumers "this network's membership changed" is `CableNetwork.RefreshNetworkDevice` (`Assembly-CSharp.decompiled.cs:253861`):

```csharp
public void RefreshNetworkDevice(Device device)
{
    device.OnAddCableNetwork(this);
    foreach (Device device2 in DeviceList)
    {
        if (!(device2 == device))
            device2.OnDeviceConnectToNetwork(device);
    }
}
```

It fires `OnDeviceConnectToNetwork(device)` on every other device on the network: the cascade a real device-add rides. It is SEPARATE from the list rebuild. `CableNetwork.RefreshPowerAndDataDeviceLists` (`:253589`) and `HandleDataNetTransmissionDevice` (`:253630`) mutate the cached `_dataDeviceList` silently and fire no per-device callback (see [CableNetwork.md](./CableNetwork.md)). `AddDevice` (`:253852`) / `RemoveDevice` (`:253873`) additionally raise the static `CableNetwork.OnNetworkChanged` action (a coarse "some network changed" signal, distinct from the per-device cascade).

Consequence: a passthrough merge that only dirties and rebuilds the data list changes WHAT is in `DataDeviceList` without firing the cascade, so no consumer is notified. To refresh consumers generically, the cascade (`OnDeviceConnectToNetwork`, e.g. by invoking `RefreshNetworkDevice` once per affected network) must be triggered explicitly.

## Consumer signals: which devices react to what
<!-- verified: 0.2.6228.27061 @ 2026-05-28 -->

Consumers split into two groups by the notification they react to:

- React to `OnDeviceListChanged()`: motherboard cartridges. They do not watch the network directly; their host `Computer` forwards `OnDeviceConnectToNetwork` / `OnDeviceDisconnectFromNetwork` (and rename / cable-find events) to `CurrentMotherboard.OnDeviceListChanged()` (see "Computer host forwards OnDeviceListChanged"). Overriders: LogicMotherboard (`:315724`), ProgrammableChipMotherboard (`:317743`), RocketMotherboard (`:318694`), SolarControlMotherboard (`:318985`), SorterMotherboard (`:319238`), CommsMotherboard (`:312212`), ManufacturingMotherboard (`:313663`/`:317090`), and the Circuitboard base (`:322844`) with its airlock / air / camera / gas / graph / hash display subclasses (`:309950, 310519, 311181, 311818, 312682, 313019, 313215`). Base declaration `Circuitboard.OnDeviceListChanged()` (`:331066`) = `ParentComputer?.CheckStatus()`, virtual, overridable by any modded motherboard.
- React to `OnDeviceConnectToNetwork(Device)` / `OnDeviceDisconnectFromNetwork(Device)` DIRECTLY (no Computer host): the IC housing `ProgrammableChip` (`:164259`/`:164271`) nulls its `_dataNetworkDevicesSorted` cache via `OnNetworkChange()`, and `LogicBase` sensors (`:384515`/`:384527`) null `_inputNetwork1DevicesSorted` / `_outputNetwork1DevicesSorted` the same way.

Implication for a generic refresh: firing `OnDeviceListChanged()` on host Computers refreshes motherboards but MISSES the IC housings and sensors that have no Computer host. Firing the full cascade (`OnDeviceConnectToNetwork` on each device, e.g. via `RefreshNetworkDevice`) refreshes BOTH groups, plus any third-party device that follows the vanilla pattern of overriding these methods. The cascade is the generalizable trigger; `OnDeviceListChanged`-on-Computers is not.

These notifications fire per-peer. `RefreshPowerAndDataDeviceLists` has no server gate and runs on clients too (each peer computes its own device lists locally from replicated device / cable state; `CableNetwork` membership is not itself replicated). So a refresh trigger that must reach client UI has to run on the client, and the list it recomputes is only correct if the inputs to the merge are present on the client.

## Verification history

- 2026-05-28: added "Vanilla consumer-notification cascade: RefreshNetworkDevice" and "Consumer signals: which devices react to what". Verified `RefreshNetworkDevice` (`:253861`) fires `OnDeviceConnectToNetwork` per device; confirmed the silent rebuild (`RefreshPowerAndDataDeviceLists` `:253589`, `HandleDataNetTransmissionDevice` `:253630`) fires no cascade; read the IC-housing (`ProgrammableChip` `:164259`) and `LogicBase` sensor (`:384515`) connect/disconnect overrides directly (both null sorted caches via `OnNetworkChange`). Consumer overrider enumeration cross-checked against the OnDeviceListChanged grep across the decompile. Recorded per-peer execution of the refresh path for the multiplayer dimension.
- 2026-05-28: page created. Traced the motherboard device-list source chain and rebuild triggers at game 0.2.6228.27061 from `Assembly-CSharp.decompiled.cs`. Directly read `LogicMotherboard.RebuildDeviceList` (315637-315656), `LogicMotherboard.OnDeviceListChanged` (315724-315731), `LogicMotherboard.OnInsertedToComputer` (315733-315744), and enumerated every `CurrentMotherboard.OnDeviceListChanged()` call site in the `Computer` host (372779-372884) plus the base `Motherboard.OnDeviceListChanged` (331066) and `OnSlaveStateChange` (330866). `ProgrammableChipMotherboard` triggers (`_DevicesChanged`, `HandleDeviceListChange`, `OnInsertedToComputer`, `ProcessUpdate` flag 256) read at 317673-317765. `CableNetwork.DataDeviceList` getter and `DirtyPowerAndDataDeviceLists` cross-referenced with [CableNetwork.md](./CableNetwork.md) and `Mods/PowerGridPlus/TODO.md:23` (decompile 253519-253529, 253657-253661).

## Open questions

- The async `await UniTask.NextFrame()` handler at 372816-372819 that forwards `OnDeviceListChanged()` was not traced to its enclosing method name; it is one more host-side refresh path but its trigger condition is unconfirmed.
