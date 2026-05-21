---
title: LogicValueWriteMessages
type: Protocols
created_in: 0.2.6228.27061
verified_in: 0.2.6228.27061
verified_at: 2026-05-21
sources:
  - $(StationeersPath)\rocketstation_Data\Managed\Assembly-CSharp.dll :: Assets.Scripts.Networking.SetLogicValueMessage / RocketMotherboard.LogicValueChanged
related:
  - ./GameMessageFactory.md
  - ../GameSystems/NetworkRoles.md
  - ../Patterns/ServerAuthoritativeSimulation.md
  - ../Patterns/HarmonyLogicableInheritedMethodTrap.md
tags: [logic, network, harmony]
---

# Writing a logic value to a device over the wire

How a logic-value write (IC10 `s d0 X v`, tablet field edit, UI on/off toggle) reaches a device's `SetLogicValue` on the authoritative side, and what that means for a Harmony patch on `SetLogicValue`. Sourced while debugging why a PowerTransmitterPlus prefix on `WirelessPower.SetLogicValue` fired for the post-load re-solve but appeared not to fire for a remote client's tablet edit.

## SetLogicValueMessage: client-to-server write
<!-- verified: 0.2.6228.27061 @ 2026-05-18 -->

`SetLogicValueMessage` (decompile line 261398) is registered in the message factory as id 104 (line 179647). Wire shape and server-side apply (verbatim):

```csharp
public class SetLogicValueMessage : ProcessedMessage<SetLogicValueMessage>
{
    public long DeviceReferenceId;
    public LogicType LogicType;
    public double LogicValue;

    public override void Process(long hostId)
    {
        base.Process(hostId);
        Thing.Find<ILogicable>(DeviceReferenceId).SetLogicValue(LogicType, LogicValue);
    }

    public override void Deserialize(RocketBinaryReader reader)
    {
        Network.ReadPackedId(reader, out DeviceReferenceId);
        Network.ReadLogicValue(reader, out LogicType, out LogicValue);
    }

    public override void Serialize(RocketBinaryWriter writer)
    {
        Network.WritePackedId(writer, DeviceReferenceId);
        Network.WriteLogicValue(writer, LogicType, LogicValue);
    }
}
```

The `Process` body runs on the server. It resolves the device by ReferenceId as `ILogicable` and calls `SetLogicValue(LogicType, double)` through the interface. The actual method that runs is the concrete type's `SetLogicValue` override, dispatched virtually.

## The send side and the host bypass
<!-- verified: 0.2.6228.27061 @ 2026-05-18 -->

The canonical send pattern, from `RocketMotherboard.LogicValueChanged` (decompile line 318416):

```csharp
public void LogicValueChanged(float newValue, LogicType logicType, long deviceReferenceId)
{
    if (Assets.Scripts.Networking.NetworkManager.IsClient)
    {
        NetworkClient.SendToServer(new SetLogicValueMessage
        {
            DeviceReferenceId = deviceReferenceId,
            LogicType = logicType,
            LogicValue = newValue
        });
    }
    else
    {
        Thing.Find<Device>(deviceReferenceId).SetLogicValue(logicType, newValue);
    }
}
```

Two consequences:

- **Remote client**: `IsClient` is true, so the write is packed into a `SetLogicValueMessage` and sent to the server. The client never calls `SetLogicValue` locally; only the server does (inside `Process`). A Harmony patch on `SetLogicValue` therefore fires on the server for a client-originated write, NOT on the client.
- **Host / single-player**: `IsClient` is false, so `SetLogicValue` is called directly in-process. The patch fires locally.

Note the input is typed `float newValue` here, widened to the message's `double LogicValue`. A logic value carrying a 64-bit `Thing.ReferenceId` (which can exceed 2^24) loses precision through a `float`-typed UI path; whether a given UI control is `float`- or `double`-typed determines whether large-id writes (e.g. a target ReferenceId) survive. `RocketMotherboard.LogicValueChanged` is `float`-typed; other write entry points may differ and were not all enumerated this pass (see Open Questions).

## The RocketMotherboard logic-config panel: float-typed commit (a separate path from the handheld tablet)
<!-- verified: 0.2.6228.27061 @ 2026-05-21 -->

The in-game logic-config tablet (the `LogicControlPanel` on `RocketMotherboard`, shown when a logicable is selected) builds its per-LogicType rows in `RocketUIBuilder.BuildModel` (decompile around line 154914). The row list is assembled CLIENT-SIDE from the local logicable instance:

```csharp
for (int num = EnumCollections.LogicTypes.Length - 1; num >= 0; num--)
{
    LogicType logicType = EnumCollections.LogicTypes.Values[num];
    if (logicable.CanLogicRead(logicType))
    {
        ...
        model.LogicControlModel.LogicValues.Add(new LogicValueModel
        {
            DeviceReferenceId = logicable.ReferenceId,
            DisplayName = logicType.GetName(),
            Pinned = pinned2,
            LogicType = logicType,
            CanLogicWrite = logicable.CanLogicWrite(logicType),
            Value = motherboard.SelectedDeviceLogicValues[(int)logicType]
        });
    }
}
```

Two consequences for a mod-registered writable LogicType:

- A row appears only when `CanLogicRead(logicType)` returns true (the gate is read, not write). The field is then made editable when `CanLogicWrite(logicType)` returns true.
- Both `CanLogicRead` and `CanLogicWrite` are called on the CLIENT's own copy of the logicable. A Harmony postfix that forces these true (as PowerTransmitterPlus does on `WirelessPower.CanLogicRead` / `CanLogicWrite`) runs on every peer, so the row shows and the input is interactable on a remote client with no server round-trip. The writable-list computation is NOT server-driven.

`LogicValueModel.Value` is typed `double` (line 155246) and `CanLogicWrite` is `bool` (line 155248). The editable field's interactability is set in `LogicValueDisplay.Initialize` (line 152748): `_valueInputField.interactable = model.CanLogicWrite;`.

The commit, however, goes through a `float`. `LogicValueDisplay.OnValueChanged` (line 152762, verbatim):

```csharp
private void OnValueChanged(string value)
{
    if (float.TryParse(value, out var result))
    {
        _motherboard.LogicValueChanged(result, _logicType, LogicValueModel.DeviceReferenceId);
    }
}
```

`result` is a `float`. It flows into `RocketMotherboard.LogicValueChanged(float newValue, ...)` (line 318416), which packs it into `SetLogicValueMessage.LogicValue` (a `double`) on a client or calls `SetLogicValue(logicType, newValue)` directly on the host. The widening `float -> double` happens AFTER the value is already a `float`, so the precision was lost at `float.TryParse`, not recovered.

This `float` typing is lossy for any LogicType whose value is a `Thing.ReferenceId` above 2^24 (~16.7M): a `float` mantissa holds only ~24 bits, so a large typed-in id is quantized to the nearest representable `float`, the server casts it back (`(long)value`), and `Thing.Find(quantizedId)` may resolve nothing.

**Scope (corrected 2026-05-21):** this lossy path is ONLY the `RocketMotherboard` `LogicControlPanel`, a wall-mounted console screen. It is NOT the path a handheld tablet uses, and it does NOT explain the observed "dish auto-aim write via the AdvancedTablet reads back 0 on a dedicated server" symptom. The handheld tablet commits through `double.TryParse` and sends `SetLogicFromClient` (a `double` field, full precision -- see the next section), so there is no quantization on that path. And even if a value were quantized, the server would still CALL `SetLogicValue` (with a wrong target), firing a `SetLogicValue` patch -- whereas the dish symptom was ZERO `SetLogicValue` calls. The dish symptom is the `ISetable` gate documented in the next section, not float loss. The float path is a real, separate defect that only bites a large ReferenceId typed into the RocketMotherboard panel.

The handheld `ConfigCartridge` (line 323182) is read-only IN VANILLA: `ReadLogicText` (line 323217) only appends `GetLogicValue` results to a display string and never writes. EquipmentPlus's AdvancedTablet adds click-to-write by patching `ConfigCartridge.OnScreenUpdate`, and that write goes through the `SetLogicFromClient` path below (not the RocketMotherboard float path above).

## SetLogicFromClient (id 37): the handheld / settable write, gated on ISetable
<!-- verified: 0.2.6228.27061 @ 2026-05-21 -->

The in-world "Set" popup and EquipmentPlus's AdvancedTablet click-to-write both use `SetLogicFromClient` (message factory id 37, decompile line 258660), NOT `SetLogicValueMessage`. Its value field is a `double` (no float quantization), but its server-side apply is gated on the target being `ISetable`. Verbatim:

```csharp
public class SetLogicFromClient : ProcessedMessage<SetLogicFromClient>
{
    public long LogicId;
    public LogicType LogicType;
    public double Value;

    public override void Process(long hostId)
    {
        if (GameManager.RunSimulation)
        {
            if (!(Thing.Find<Thing>(LogicId) is ISetable setable))
            {
                List<long> instanceId = new List<long> { LogicId };
                WaitUntilFound(hostId, Process, Process, instanceId, 10f, "LogicMirror");
            }
            else if (setable.CanLogicWrite(LogicType))
            {
                setable.SetLogicValue(LogicType, Value);
            }
        }
    }

    public override void Deserialize(RocketBinaryReader reader)
    {
        Network.ReadPackedId(reader, out LogicId);
        LogicType = (LogicType)reader.ReadUInt16();
        Value = reader.ReadDouble();
    }
    // Serialize mirrors: WritePackedId, WriteUInt16((ushort)LogicType), WriteDouble(Value)
}
```

The gate: `Process` calls `SetLogicValue` ONLY when `Thing.Find<Thing>(LogicId) is ISetable setable` succeeds AND `setable.CanLogicWrite(LogicType)` is true. If the target is NOT `ISetable`, the write is diverted to `WaitUntilFound` (a 10-second mirror-resolution retry intended for not-yet-resolved `LogicMirror` targets) and `SetLogicValue` is NEVER called. There is no fallback to a plain `ILogicable` write.

`ISetable` (interface, line 329860, extends `ILogicable, IReferencable, IEvaluable`) is implemented by only a specific subset of devices. Confirmed implementers in v0.2.6228.27061 (decompile grep): `Waypoint` (114787), `AdvancedFurnace` (344409), `SettableAtmosDevice` (366393), `LogicUnitBase` (384220), `Stacker` (401794), `Transformer` (403300), `VendingMachineRefrigerated` (404776), `AdvancedSuit` (405979), `SuitBase` (407792). The WirelessPower family is NOT among them: `WirelessPower : ElectricalInputOutput, IRotatable` (405441), `PowerTransmitter : WirelessPower` (387065), `PowerReceiver : WirelessPower, ITransmitable, ILogicable, IReferencable, IEvaluable` (386861) -- none declares `ISetable`, and the shared base `ElectricalInputOutput` does not either (its sibling `Transformer : ElectricalInputOutput, ISetable` opts in explicitly, which it would not need to do if the base already implemented it). `Device` itself (349588) declares `ILogicable, ISlotWriteable, ...` but NOT `ISetable`.

**Consequence (root cause of the dish-tablet symptom).** A logic-value write to a non-`ISetable` device -- a dish, and any other `ILogicable`-but-not-`ISetable` `Device` -- sent via `SetLogicFromClient` from a remote client is silently dropped server-side at the `is ISetable` cast. This is exactly why a remote-client AdvancedTablet write of `MicrowaveAutoAimTarget` to a dish reads back 0 and fires zero `SetLogicValue` patches on the server, while:

- the same write on the host works -- the host path calls `Device.SetLogicValue(...)` directly through `ILogicable` (EquipmentPlus `ConfigCartridgePatches.WriteLogicValue`, `IsServer` branch), never touching the `ISetable` gate; and
- an IC10 `s d0 MicrowaveAutoAimTarget` works -- it reaches `SetLogicValue` server-side directly (verified by control test 2026-05-21).

Contrast `SetLogicValueMessage.Process` (above): it resolves `Thing.Find<ILogicable>` and has NO `ISetable` gate, so it would apply to a dish. The two client write messages differ critically in the target-interface requirement: `SetLogicValueMessage` needs only `ILogicable`; `SetLogicFromClient` needs `ISetable`. A mod that wants its non-`ISetable` device writable from a remote client via a handheld tablet must NOT route through `SetLogicFromClient`.

## Patching SetLogicValue: target the declaring type
<!-- verified: 0.2.6228.27061 @ 2026-05-18 -->

`SetLogicValue(LogicType, double)` is declared on `Device` (decompile line 350323) and overridden by many subclasses. For a Harmony attribute patch to attach, the named type must DECLARE the method, not merely inherit it (see `../Patterns/HarmonyLogicableInheritedMethodTrap.md`). Verified for the WirelessPower family:

- `WirelessPower : ElectricalInputOutput, IRotatable` (line 405441) DECLARES its own `SetLogicValue` override at line 405799 (it routes `Horizontal` / `Vertical` / `HorizontalRatio` to its `RotatableBehaviour` then calls `base.SetLogicValue`). So `[HarmonyPatch(typeof(WirelessPower), nameof(WirelessPower.SetLogicValue))]` attaches cleanly.
- `PowerTransmitter : WirelessPower` (line 387065) and `PowerReceiver : WirelessPower, ...` (line 386861) do NOT override `SetLogicValue`. A write to a PowerTransmitter or PowerReceiver dispatches to `WirelessPower.SetLogicValue`, so a patch on `WirelessPower.SetLogicValue` covers both.

Because the server-side `SetLogicValueMessage.Process` dispatches through `ILogicable.SetLogicValue`, a patch on the declaring type (`WirelessPower` here) fires for the virtual call regardless of whether the runtime instance is a PowerTransmitter or PowerReceiver. A patch on a non-declaring subclass (`PowerTransmitter`) would throw `Undefined target method` at PatchAll and bail the whole batch.

## Verification history

- 2026-05-18: page created while debugging PowerTransmitterPlus auto-aim on a dedicated server. Confirmed `SetLogicValueMessage.Process` (line 261398) calls `Thing.Find<ILogicable>(...).SetLogicValue(...)` server-side; `RocketMotherboard.LogicValueChanged` (line 318416) shows the IsClient-send / host-direct-call split; `WirelessPower` declares `SetLogicValue` at line 405799 while `PowerTransmitter` / `PowerReceiver` inherit it. Message factory id 104 at line 179647.
- 2026-05-21: added section "The motherboard tablet UI: float-typed commit, client-side writable list" while tracing why a `MicrowaveAutoAimTarget` (ReferenceId-valued) write via the in-game tablet reads back 0 on a dedicated server. Resolved both prior Open Questions. New facts (all from the v0.2.6228.27061 decompile): the tablet writable-row list is built client-side in `RocketUIBuilder.BuildModel` (line 154914) by calling `CanLogicRead` / `CanLogicWrite` on the local logicable; rows gate on `CanLogicRead`, editability on `CanLogicWrite`. `LogicValueDisplay.OnValueChanged` (line 152762) commits via `float.TryParse` and passes a `float` to `LogicValueChanged`, so any ReferenceId-valued LogicType is quantized to ~24 bits BEFORE the `float -> double` widening, on whichever peer types the value (host or client). The handheld `ConfigCartridge` (line 323182) is read-only display (`ReadLogicText`, line 323217) and is not a write entry point. Additive plus open-question resolution; no existing claim contradicted, so no fresh validator. The pre-existing "loses precision through a float-typed UI path" note in the send-side section is now corroborated with the concrete control.
- 2026-05-21 (correction): the prior 2026-05-21 entry over-attributed the dish-tablet symptom to the RocketMotherboard float path. A control test (server-side `Device.SetLogicValue` on a dish works; IC10 path works) plus reading EquipmentPlus's `ConfigCartridgePatches.WriteLogicValue` (sends `SetLogicFromClient` with a `double`, parsed via `double.TryParse`) and the `SetLogicFromClient.Process` body (decompile 258660) established that the handheld/AdvancedTablet write uses `SetLogicFromClient` (id 37), NOT the RocketMotherboard `LogicValueDisplay` float path. The actual root cause of the dish symptom is the `is ISetable` gate in `SetLogicFromClient.Process`: a dish (`WirelessPower`, not `ISetable`) fails the cast and the write is diverted to `WaitUntilFound`, so `SetLogicValue` is never called. Rescoped the float section to the RocketMotherboard panel and added the "SetLogicFromClient (id 37)" section with the `ISetable` gate, the implementer list, and the WirelessPower-is-not-ISetable confirmation. The float path remains documented as a real but separate defect; this correction does not delete it, only narrows its scope.

## Open questions

- The `SetLogicFromClient` path is now confirmed as a loss-free (`double`) write path: it carries the value as a raw `double` with no per-type quantization, so a large ReferenceId survives the wire. The remaining lossy writer is the `RocketMotherboard` `LogicValueDisplay` / `LogicValueChanged` pair (both `float`-typed); a large ReferenceId typed into the RocketMotherboard wall panel specifically would still quantize. Whether any other UI control commits through `float` was not exhaustively enumerated.
- IC10 `s d0 X v`: not traced at the instruction level, but the control test (2026-05-21) confirmed a server-side `Device.SetLogicValue` call applies correctly, and IC10 registers are `double`, so the IC10 store path is loss-free in practice. The exact `ProgrammableChip` store-instruction typing was not confirmed from the decompile.
