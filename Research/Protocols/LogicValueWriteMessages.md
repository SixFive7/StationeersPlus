---
title: LogicValueWriteMessages
type: Protocols
created_in: 0.2.6228.27061
verified_in: 0.2.6228.27061
verified_at: 2026-05-18
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

## Patching SetLogicValue: target the declaring type
<!-- verified: 0.2.6228.27061 @ 2026-05-18 -->

`SetLogicValue(LogicType, double)` is declared on `Device` (decompile line 350323) and overridden by many subclasses. For a Harmony attribute patch to attach, the named type must DECLARE the method, not merely inherit it (see `../Patterns/HarmonyLogicableInheritedMethodTrap.md`). Verified for the WirelessPower family:

- `WirelessPower : ElectricalInputOutput, IRotatable` (line 405441) DECLARES its own `SetLogicValue` override at line 405799 (it routes `Horizontal` / `Vertical` / `HorizontalRatio` to its `RotatableBehaviour` then calls `base.SetLogicValue`). So `[HarmonyPatch(typeof(WirelessPower), nameof(WirelessPower.SetLogicValue))]` attaches cleanly.
- `PowerTransmitter : WirelessPower` (line 387065) and `PowerReceiver : WirelessPower, ...` (line 386861) do NOT override `SetLogicValue`. A write to a PowerTransmitter or PowerReceiver dispatches to `WirelessPower.SetLogicValue`, so a patch on `WirelessPower.SetLogicValue` covers both.

Because the server-side `SetLogicValueMessage.Process` dispatches through `ILogicable.SetLogicValue`, a patch on the declaring type (`WirelessPower` here) fires for the virtual call regardless of whether the runtime instance is a PowerTransmitter or PowerReceiver. A patch on a non-declaring subclass (`PowerTransmitter`) would throw `Undefined target method` at PatchAll and bail the whole batch.

## Verification history

- 2026-05-18: page created while debugging PowerTransmitterPlus auto-aim on a dedicated server. Confirmed `SetLogicValueMessage.Process` (line 261398) calls `Thing.Find<ILogicable>(...).SetLogicValue(...)` server-side; `RocketMotherboard.LogicValueChanged` (line 318416) shows the IsClient-send / host-direct-call split; `WirelessPower` declares `SetLogicValue` at line 405799 while `PowerTransmitter` / `PowerReceiver` inherit it. Message factory id 104 at line 179647.

## Open questions

- The handheld Configuration Cartridge tablet's exact write path was not fully traced. `RocketMotherboard.LogicValueChanged` is one sender of `SetLogicValueMessage`, but whether the handheld `ConfigCartridge` (decompile line 323182) routes through the same message or a different one (e.g. a setting-write message) is unconfirmed. This matters because a write that does not flow through `SetLogicValue` would bypass any `SetLogicValue` patch entirely. Diagnostic next step: a prefix on `SetLogicValueMessage.Process` logging every inbound write, plus tracing `ConfigCartridge`'s commit handler.
- Which UI controls are `float`-typed (lossy for large ReferenceId logic values) vs `double`-typed was not enumerated. A target-ReferenceId write through a `float` path could quantize the id.
