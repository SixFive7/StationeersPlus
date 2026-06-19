---
title: LogicMirror
type: GameClasses
created_in: 0.2.6228.27061
verified_in: 0.2.6228.27061
verified_at: 2026-06-19
sources:
  - $(StationeersPath)\rocketstation_Data\Managed\Assembly-CSharp.dll :: Assets.Scripts.Objects.Electrical.LogicMirror
  - .work/decomp/0.2.6228.27061/Assembly-CSharp.decompiled.cs:380860-381082
  - .work/decomp/0.2.6228.27061/Assembly-CSharp.decompiled.cs:359676-359760 (Logicable.GetNextReadable / RecalculateSortedDevicesList)
  - .work/decomp/0.2.6228.27061/Assembly-CSharp.decompiled.cs:379542-379548 (LogicInputBase)
related:
  - ./LogicUnitBase.md
  - ./Device.md
  - ./CableNetwork.md
  - ../GameSystems/LogicType.md
  - ../Protocols/LogicValueWriteMessages.md
tags: [logic, ic10, network, save-load]
---

# LogicMirror

Vanilla `Assets.Scripts.Objects.Electrical.LogicMirror`, the "Logic Mirror" device. It is a transparent proxy (a pointer / alias) for one other logic device on its own data network. Once a target is set, reading any `LogicType` from the mirror returns the target's value, and writing any `LogicType` to the mirror forwards to the target. The mirror carries its own stable `ReferenceId` and its own physical presence on the data network, so automation (an IC10 chip, a Batch reader, another Logic Reader) can address the mirror once and the player can re-target which physical device the mirror stands for without rewiring or reprogramming.

Inheritance: `LogicMirror : LogicInputBase : LogicUnitBase`. `LogicInputBase` is a one-method abstract shim that only sets the Stationpedia category:

```csharp
public abstract class LogicInputBase : LogicUnitBase
{
    public override string GetStationpediaCategory()
    {
        return Localization.GetInterface(StationpediaCategoryStrings.LogicInputCategory);
    }
}
```

(`Assembly-CSharp.decompiled.cs:379542-379548`)

All of the data-port, power, `Setting`, and two-connector machinery comes from [LogicUnitBase](./LogicUnitBase.md) (the input network is `InputNetwork1`, socket `Input1Index = 0`; the sorted device list is `InputNetwork1DevicesSorted`). This page only covers what `LogicMirror` adds on top: the `CurrentDevice` target and the delegation.

## Full class (verbatim)
<!-- verified: 0.2.6228.27061 @ 2026-06-19 -->

```csharp
[XmlInclude(typeof(LogicBaseSaveData))]
public class LogicMirrorSaveData : StructureSaveData
{
    [XmlElement]
    public long CurrentDeviceId;
}
public class LogicMirror : LogicInputBase
{
    private long _currentDeviceIdOnJoin;

    private Device _currentDevice;

    private long _savedId;

    public Device CurrentDevice
    {
        get
        {
            return _currentDevice;
        }
        set
        {
            if (_currentDevice?.ReferenceId != value?.ReferenceId)
            {
                _currentDevice = value;
                if (Assets.Scripts.Networking.NetworkManager.IsServer)
                {
                    base.NetworkUpdateFlags |= 1024;
                }
            }
        }
    }

    protected override bool IsOperable => CheckOperable();

    public override void BuildUpdate(RocketBinaryWriter writer, ushort networkUpdateType)
    {
        base.BuildUpdate(writer, networkUpdateType);
        if (Thing.IsNetworkUpdateRequired(1024u, networkUpdateType))
        {
            writer.WriteInt64(CurrentDevice?.ReferenceId ?? 0);
        }
    }

    public override void ProcessUpdate(RocketBinaryReader reader, ushort networkUpdateType)
    {
        base.ProcessUpdate(reader, networkUpdateType);
        if (Thing.IsNetworkUpdateRequired(1024u, networkUpdateType))
        {
            CurrentDevice = Thing.Find<Device>(reader.ReadInt64());
        }
    }

    public override void SerializeOnJoin(RocketBinaryWriter writer)
    {
        base.SerializeOnJoin(writer);
        writer.WriteInt64(CurrentDevice?.ReferenceId ?? 0);
    }

    public override void DeserializeOnJoin(RocketBinaryReader reader)
    {
        base.DeserializeOnJoin(reader);
        _savedId = reader.ReadInt64();
    }

    public override ThingSaveData SerializeSave()
    {
        ThingSaveData savedData = new LogicMirrorSaveData();
        InitialiseSaveData(ref savedData);
        return savedData;
    }

    public override void DeserializeSave(ThingSaveData savedData)
    {
        base.DeserializeSave(savedData);
        if (savedData is LogicMirrorSaveData logicMirrorSaveData)
        {
            _savedId = logicMirrorSaveData.CurrentDeviceId;
        }
    }

    protected override void InitialiseSaveData(ref ThingSaveData savedData)
    {
        base.InitialiseSaveData(ref savedData);
        if (savedData is LogicMirrorSaveData logicMirrorSaveData && (bool)CurrentDevice)
        {
            logicMirrorSaveData.CurrentDeviceId = CurrentDevice.ReferenceId;
        }
    }

    public override void OnFinishedLoad()
    {
        base.OnFinishedLoad();
        CurrentDevice = Thing.Find<Device>(_savedId);
    }

    public override bool CanLogicRead(LogicType logicType)
    {
        if (!CurrentDevice)
        {
            return false;
        }
        return CurrentDevice.CanLogicRead(logicType);
    }

    public override double GetLogicValue(LogicType logicType)
    {
        if (!CurrentDevice || !Powered)
        {
            return 0.0;
        }
        return CurrentDevice.GetLogicValue(logicType);
    }

    public override bool CanLogicWrite(LogicType logicType)
    {
        if (!CurrentDevice)
        {
            return false;
        }
        return CurrentDevice.CanLogicWrite(logicType);
    }

    public override void SetLogicValue(LogicType logicType, double value)
    {
        if ((bool)CurrentDevice && Powered)
        {
            CurrentDevice.SetLogicValue(logicType, value);
        }
    }

    public override string GetContextualName(Interactable interactable)
    {
        if (interactable.Action == InteractableType.Button1)
        {
            if (!CurrentDevice)
            {
                return InterfaceStrings.LogicNoDevice;
            }
            return CurrentDevice.DisplayName;
        }
        return base.GetContextualName(interactable);
    }

    public override PassiveTooltip GetPassiveTooltip(Collider hitCollider)
    {
        PassiveTooltip result = new PassiveTooltip(true);
        foreach (Connection openEnd in OpenEnds)
        {
            if (hitCollider == openEnd.Collider && hitCollider != null)
            {
                result.Title = DisplayName;
                return result;
            }
        }
        result.Title = DisplayName;
        result.State = (CurrentDevice ? $"Mirroring <color=green>{CurrentDevice.DisplayName}</color>" : "No Device Set");
        return result;
    }

    private bool CheckOperable()
    {
        if (CurrentDevice == null || base.InputNetwork1 == null || !base.InputNetwork1.DataDeviceList.Contains(CurrentDevice))
        {
            if (Error == 0)
            {
                OnServer.Interact(base.InteractError, 1);
            }
            return false;
        }
        if (Error == 1)
        {
            OnServer.Interact(base.InteractError, 0);
        }
        return true;
    }

    public override DelayedActionInstance InteractWith(Interactable interactable, Interaction interaction, bool doAction = true)
    {
        if (interactable == null)
        {
            return null;
        }
        if (interactable.Action == InteractableType.Button1)
        {
            DelayedActionInstance delayedActionInstance = new DelayedActionInstance
            {
                Duration = 0f,
                ActionMessage = interactable.ContextualName
            };
            delayedActionInstance.SwitchTitleForTooltip = true;
            if (!(interaction.SourceSlot.Occupant is Screwdriver))
            {
                return delayedActionInstance.Fail(GameStrings.RequiresScrewdriver);
            }
            if (interactable.Action == InteractableType.Button1)
            {
                Device nextReadable = Logicable.GetNextReadable(this, CurrentDevice, base.InputNetwork1DevicesSorted, interaction.AltKey);
                if (!nextReadable)
                {
                    return delayedActionInstance.Fail(GameStrings.LogicNoReadableDevices);
                }
                delayedActionInstance.AppendStateMessage(GameStrings.GlobalChangeSettingTo, nextReadable.ToTooltip());
                if (!KeyManager.GetButton(KeyMap.QuantityModifier))
                {
                    delayedActionInstance.ExtendedMessage = InterfaceStrings.HoldForPreviousObject;
                }
                if (!doAction)
                {
                    return delayedActionInstance.Succeed();
                }
                ScrewSound();
                if (GameManager.RunSimulation)
                {
                    CurrentDevice = nextReadable;
                    CheckOperable();
                }
                return delayedActionInstance.Succeed();
            }
        }
        return base.InteractWith(interactable, interaction, doAction);
    }
}
```

(`Assembly-CSharp.decompiled.cs:380860-381082`)

## CurrentDevice: the mirrored target
<!-- verified: 0.2.6228.27061 @ 2026-06-19 -->

The single state `LogicMirror` adds over `LogicUnitBase` is `CurrentDevice` (a `Device` reference), held in the field `_currentDevice`. The setter compares by `ReferenceId` (not object identity) and, on a real change on the server, raises the custom sync bit `NetworkUpdateFlags |= 1024`:

```csharp
public Device CurrentDevice
{
    get { return _currentDevice; }
    set
    {
        if (_currentDevice?.ReferenceId != value?.ReferenceId)
        {
            _currentDevice = value;
            if (Assets.Scripts.Networking.NetworkManager.IsServer)
            {
                base.NetworkUpdateFlags |= 1024;
            }
        }
    }
}
```

`1024` (bit 10) is the mirror's own update flag, distinct from `LogicUnitBase`'s `Setting` flag `256` (bit 8). On the wire the flag carries `CurrentDevice.ReferenceId` as an `Int64` (0 when no device is set). `LogicMirror` does NOT use the inherited `Setting` for anything; the target is the `CurrentDevice` reference, not a `double`.

The two unused-looking fields `_currentDeviceIdOnJoin` and `_savedId` are deserialization staging slots (see Persistence below). `_currentDeviceIdOnJoin` is declared but not read in this version; `_savedId` is the one actually consumed.

## Read / write delegation (the proxy behaviour)
<!-- verified: 0.2.6228.27061 @ 2026-06-19 -->

All four logic methods forward to `CurrentDevice`. This is what makes the mirror transparent: to any reader or writer it looks exactly like the target device for every `LogicType` the target supports.

- `CanLogicRead(t)` returns `CurrentDevice.CanLogicRead(t)`, or `false` if no target. NOT power-gated.
- `CanLogicWrite(t)` returns `CurrentDevice.CanLogicWrite(t)`, or `false` if no target. NOT power-gated.
- `GetLogicValue(t)` returns `CurrentDevice.GetLogicValue(t)`, but returns `0.0` when there is no target OR the mirror is not `Powered`.
- `SetLogicValue(t, v)` calls `CurrentDevice.SetLogicValue(t, v)`, but only when there is a target AND the mirror is `Powered`; otherwise it silently no-ops.

The asymmetry is deliberate: the capability flags (`CanLogicRead` / `CanLogicWrite`) report the target's surface regardless of power, so UI surfaces still list the right rows on an unpowered mirror, while the actual value read/write requires power. `Powered` here is the mirror's own power state (inherited from `Device`), not the target's.

The mirror imposes no `LogicType` filtering of its own. Whatever the target declares readable/writable is exactly what the mirror declares readable/writable, including mod-registered `LogicType` values (the target's overrides are what answer).

## Target selection: screwdriver cycling over the data network
<!-- verified: 0.2.6228.27061 @ 2026-06-19 -->

In vanilla the only in-world way to set the target is the screwdriver on `InteractableType.Button1`. The interaction:

1. Fails with `GameStrings.RequiresScrewdriver` unless the player's active hand holds a `Screwdriver`.
2. Calls `Logicable.GetNextReadable(this, CurrentDevice, base.InputNetwork1DevicesSorted, interaction.AltKey)` to pick the next device. `interaction.AltKey` reverses direction (the tooltip shows "Hold for previous object").
3. Fails with `GameStrings.LogicNoReadableDevices` if nothing readable is found.
4. On commit (`GameManager.RunSimulation`), sets `CurrentDevice = nextReadable` and calls `CheckOperable()`.

`InputNetwork1DevicesSorted` is the candidate pool: every logicable device on the cable network plugged into the mirror's input socket, sorted by `DisplayName`. From [LogicUnitBase](./LogicUnitBase.md):

```csharp
public static List<ILogicable> RecalculateSortedDevicesList(CableNetwork cableNetwork)
{
    if (cableNetwork == null) return null;
    List<ILogicable> list = new List<ILogicable>();
    list.AddRange(cableNetwork.DataDeviceList);
    list.Sort((ILogicable a, ILogicable b) => a.DisplayName.CompareTo(b.DisplayName));
    return list;
}
```

`GetNextReadable<T>` is a thin wrapper over the shared `_GetNext` cursor used by every device-selecting logic unit (Batch reader/writer, Logic Reader, etc.). It walks the sorted list from the current index in the chosen direction, skipping nulls, non-`T`, the device doing the selecting (`this`), and anything that is not `IsLogicReadable()`:

```csharp
public static T GetNextReadable<T>(ILogicable thisLogicable, T currentDevice, List<ILogicable> deviceList, bool isForward, bool allowNull = false) where T : ILogicable
{
    return _GetNext(thisLogicable, IOCheck.Readable, currentDevice, deviceList, isForward, allowNull);
}
```

(`Assembly-CSharp.decompiled.cs:359676-359760`)

Consequence: the mirror can only target a device that is readable AND on the same data network as the mirror's input connector. There is no cross-network or by-id selection in the vanilla in-world flow. The `CurrentDevice` setter itself is public, so external code (a mod) could assign any `Device`, but `CheckOperable` will then flag an error for any target not on the input network.

## Operable / error state
<!-- verified: 0.2.6228.27061 @ 2026-06-19 -->

`IsOperable` is `CheckOperable()`. The mirror is operable only when it has a target AND that target is currently a member of the input network's data device list:

```csharp
private bool CheckOperable()
{
    if (CurrentDevice == null || base.InputNetwork1 == null || !base.InputNetwork1.DataDeviceList.Contains(CurrentDevice))
    {
        if (Error == 0) OnServer.Interact(base.InteractError, 1);
        return false;
    }
    if (Error == 1) OnServer.Interact(base.InteractError, 0);
    return true;
}
```

So if the target is deleted, unpowered off the network, or the mirror is re-wired to a different network, the mirror raises its `Error` interactable (and the error LED). `CheckOperable` is called after each screwdriver re-target and via the `IsOperable` getter.

## Persistence and networking
<!-- verified: 0.2.6228.27061 @ 2026-06-19 -->

The target is stored and synced as a bare `ReferenceId` (`long`), resolved to a live `Device` lazily, because the target Thing may not exist yet at the moment the mirror deserializes.

- **Save**: `SerializeSave` emits a `LogicMirrorSaveData` whose `CurrentDeviceId` is `CurrentDevice.ReferenceId` (written only when a target exists, in `InitialiseSaveData`). `DeserializeSave` stashes that id into `_savedId` WITHOUT resolving it.
- **Load resolution**: `OnFinishedLoad` runs after all Things exist and resolves `CurrentDevice = Thing.Find<Device>(_savedId)`. This deferral is the standard "store id at deserialize, resolve in OnFinishedLoad" pattern; resolving inside `DeserializeSave` would race the target's own load.
- **Late join**: `SerializeOnJoin` writes the live `CurrentDevice.ReferenceId`; `DeserializeOnJoin` stores it in `_savedId` (again deferred, resolved later). 
- **Per-tick sync**: the custom flag `1024` carries the `ReferenceId` in `BuildUpdate`; `ProcessUpdate` resolves it immediately with `CurrentDevice = Thing.Find<Device>(reader.ReadInt64())` (a no-target write sends/reads 0, which resolves to null).

`LogicMirrorSaveData` is decorated `[XmlInclude(typeof(LogicBaseSaveData))]` and extends `StructureSaveData`; it adds exactly one element, `CurrentDeviceId`.

## Tooltip and contextual name
<!-- verified: 0.2.6228.27061 @ 2026-06-19 -->

- The passive tooltip shows `Mirroring <color=green>{target.DisplayName}</color>` when a target is set, else `No Device Set` (unless the cursor is over a connector open-end, in which case it shows just the device title).
- The Button1 contextual name is the target's `DisplayName`, or `InterfaceStrings.LogicNoDevice` when none is set, so the screwdriver prompt reads back the current target.

## Relationship to the SetLogicFromClient write path
<!-- verified: 0.2.6228.27061 @ 2026-06-19 -->

`LogicMirror` is `ISetable` (inherited from `LogicUnitBase`), so a client "Set" write addressed to the mirror's own ReferenceId passes the `is ISetable` gate in `SetLogicFromClient.Process` and reaches `LogicMirror.SetLogicValue`, which then forwards to the target (power permitting). See [../Protocols/LogicValueWriteMessages.md](../Protocols/LogicValueWriteMessages.md). Notably, the not-yet-resolved retry branch in that same `Process` body uses the literal debug context string `"LogicMirror"`:

```csharp
if (!(Thing.Find<Thing>(LogicId) is ISetable setable))
{
    List<long> instanceId = new List<long> { LogicId };
    WaitUntilFound(hostId, Process, Process, instanceId, 10f, "LogicMirror");
}
```

The label suggests this 10-second resolve-and-retry path was built with the mirror's deferred-id resolution in mind (a write may arrive before the target id resolves). The string is only a diagnostic context tag; the retry fires for any not-yet-found `ISetable` target, not exclusively mirrors.

## Verification history

- 2026-06-19: initial writeup against game version 0.2.6228.27061. Sources: decompile of `Assembly-CSharp.dll` (`LogicMirror` / `LogicMirrorSaveData` at lines 380860-381082, `LogicInputBase` at 379542-379548, `Logicable.GetNextReadable` / `RecalculateSortedDevicesList` at 359676-359760). Triggered by user question "what is the logic mirror device and how does it work." Full class captured verbatim; delegation, screwdriver selection, operable check, and id-deferred persistence/networking documented. Additive page; no existing verified content contradicted, so no fresh validator. Cross-links the `WaitUntilFound("LogicMirror")` observation already recorded on `../Protocols/LogicValueWriteMessages.md`.

## Open questions

- Whether any non-screwdriver in-game flow sets `CurrentDevice` (e.g. a Labeller "Set" popup, as `LogicDial` supports for `Setting`). The `InteractWith` override only handles `Button1` + Screwdriver and otherwise falls through to base; no Labeller branch is present, so screwdriver appears to be the only vanilla in-world setter, but this was not confirmed against the prefab's `Interactable` list.
