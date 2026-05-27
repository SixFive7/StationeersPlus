---
title: LogicUnitBase
type: GameClasses
created_in: 0.2.6228.27061
verified_in: 0.2.6228.27061
verified_at: 2026-05-27
sources:
  - $(StationeersPath)\rocketstation_Data\Managed\Assembly-CSharp.dll :: Assets.Scripts.Objects.Electrical.LogicUnitBase
  - .work/decomp/0.2.6228.27061/Assembly-CSharp.decompiled.cs:384220-384440
  - .work/decomp/0.2.6228.27061/KeypadMod.decompiled.cs:54-447
related:
  - ./CableNetwork.md
  - ./Device.md
  - ./LogicTransmitter.md
  - ../GameSystems/LogicType.md
tags: [logic, power, network]
---

# LogicUnitBase

Vanilla base class for the family of small "logic unit" devices: logic gates, math, memory, compare, batch reader/writer, and (via the Keypad workshop mod) the third-party keypad. Class declaration:

```csharp
public class LogicUnitBase : SmallDevice, IPrefabHash, ISetable, ILogicable, IReferencable, IEvaluable, ISmartRotatable
```

Two cable connectors. Two separate `CableNetwork` references. The device does NOT bridge the two networks.

## Two CableNetwork connectors, kept separate
<!-- verified: 0.2.6228.27061 @ 2026-05-27 -->

`LogicUnitBase` holds exactly two `CableNetwork` references, one per physical connector on the prefab:

```csharp
private CableNetwork _inputNetwork1;
private List<ILogicable> _inputNetwork1DevicesSorted;

private CableNetwork _outputNetwork1;
private List<ILogicable> _outputNetwork1DevicesSorted;

public int Input1Index;
public int OutputIndex = 1;
```

(`Assembly-CSharp.decompiled.cs:384234-384246`)

The two integer `Input1Index = 0` and `OutputIndex = 1` are the **socket indices on the prefab**: the device exposes two `CableConnect` ports on its mesh, the first one is the input port, the second one is the output port. The base `Device` resolution machinery walks the connectors at those indices, finds the `CableNetwork` each is plugged into, and assigns to `InputNetwork1` / `OutputNetwork1` setters:

```csharp
public CableNetwork InputNetwork1
{
    get { return _inputNetwork1; }
    set
    {
        if (_inputNetwork1 != value)
        {
            _inputNetwork1 = value;
            LogicNetworkChange();
        }
    }
}

public CableNetwork OutputNetwork1
{
    get { return _outputNetwork1; }
    set
    {
        if (_outputNetwork1 != value)
        {
            _outputNetwork1 = value;
            LogicNetworkChange();
        }
    }
}
```

(`Assembly-CSharp.decompiled.cs:384254-384296`)

The setters keep the two networks as independent references. There is no field that merges them, no method that joins their `Devices` lists, and no propagation of power, signal, or device membership from one network to the other through the device.

This is the classic "logic gate" topology in Stationeers: read input(s) from the input network, write the result to the output network. The two networks remain electrically and logically separate; what crosses between them is whatever this single device chooses to publish on its own `LogicType` slots.

## What a connection carries: depends on the cable in the network, not on the connector
<!-- verified: 0.2.6228.27061 @ 2026-05-27 -->

Both connectors plug into a `CableNetwork`. A `CableNetwork` is the network the cables form, not a "power network" or "data network" by type. Whether the network carries power, data, or both is a property of the cables (regular cable carries power + data; heavy cable carries power only; data cable carries data only). The device just sees whatever the connected network exposes.

Practical consequences:

- The Keypad (and any other `LogicUnitBase` device) draws power from whichever side has a cable carrying power. `((Thing)this).Powered` reflects whether the device received power this tick from any of its networks, not which connector that power came through.
- If only one side has power cables, the other side can still carry data without power.
- The device itself does NOT propagate power across the two networks. Plugging power into the input connector does not energise the output network through the keypad.

## Setting is the only persistent logic value
<!-- verified: 0.2.6228.27061 @ 2026-05-27 -->

`LogicUnitBase.Setting` is the single double-precision value the device stores and serialises:

```csharp
[ByteArraySync]
public virtual double Setting
{
    get { return _setting; }
    set
    {
        double setting = _setting;
        _setting = value;
        if (!RocketMath.Approximately(setting, _setting))
        {
            OnSettingChanged();
        }
        LogicChanged();
        this.OnLogicNetworkChanged?.Invoke();
    }
}
```

(`Assembly-CSharp.decompiled.cs:384310-384328`)

`Setting` is serialised under `LogicBaseSaveData.Setting` and synchronised across the network via `NetworkUpdateFlags |= 256` and `ProcessUpdate`. Subclasses (vanilla `LogicMemory`, `LogicMath`, third-party `Keypad`, etc.) treat `Setting` as their stored value; their tick logic decides when to recompute it from `InputNetwork1DevicesSorted` and when to publish it to `OutputNetwork1DevicesSorted`.

## Application: third-party Keypad
<!-- verified: 0.2.6228.27061 @ 2026-05-27 -->

The Keypad workshop mod (Steam Workshop 3478434324) defines `Keypad : LogicUnitBase` (`KeypadMod.decompiled.cs:54`). Its C# code adds no extra cable connectors and overrides nothing in the connection or network code paths. The two connectors visible in-game are the inherited `Input1Index = 0` / `OutputIndex = 1` sockets from `LogicUnitBase`.

In use:

- The keypad needs power to interact (`if (!((Thing)this).Powered) return val.Fail(GameStrings.DeviceNoPower);` at line 230). Power can come from a regular or data-carrying cable on either connector, whichever the player wires.
- Player-entered digits write to `((LogicUnitBase)this).Setting = num` (line 280) and pulse `(LogicType)3` (Activate) high for 550 ms, then low for 200 ms (`PulseMode` async, lines 108-131).
- The keypad exposes `Setting` (LogicType 12) as both readable and writable; vanilla `LogicType` slots delegate through `Device` to the same input/output network plumbing.

The two networks remain separate exactly as the base class defines them: the keypad is a one-device-wide logic gate that emits its pulse to whatever logic devices share its `OutputNetwork1`, while its `InputNetwork1` is available for other devices to read its `Setting` via `(d r LogicReader.x = keypad.Setting)`.

## Verification history

- 2026-05-27: initial writeup against game version 0.2.6228.27061. Sources: decompile of `Assembly-CSharp.dll` (LogicUnitBase class definition at line 384220-384440) and decompile of `KeypadMod.dll` (Keypad subclass at line 54-447). Triggered by user question "the keypad from this mod has two connections; do they bridge power and logic?" Answer: no, the two connectors are an input network and an output network, each tracking a separate `CableNetwork`; the device does not bridge power or data across them.

## Open questions

None.
