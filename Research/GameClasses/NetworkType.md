---
title: NetworkType
type: GameClasses
created_in: 0.2.6228.27061
verified_in: 0.2.6228.27061
verified_at: 2026-06-19
sources:
  - rocketstation_Data/Managed/Assembly-CSharp.dll :: NetworkType enum (line 293197)
related:
  - Connection.md
  - ConnectionRole.md
tags: [network, power, logic]
---

# NetworkType

A [Flags]-attributed enum that defines the types of networks a connector can carry.

## Enum Values
<!-- verified: 0.2.6228.27061 @ 2026-06-19 -->

From Assembly-CSharp.dll, lines 293196-293211:

```csharp
[Flags]
public enum NetworkType
{
	None = 0,
	Pipe = 1,
	Power = 2,
	Data = 4,
	Chute = 8,
	Elevator = 0x10,
	PipeLiquid = 0x20,
	LandingPad = 0x40,
	LaunchPad = 0x80,
	RoboticArmRail = 0x100,
	PowerAndData = 6,
	All = int.MaxValue
}
```

**Value reference:**

| Member | Value | Hex | Notes |
|--------|-------|-----|-------|
| None | 0 | 0x0 | No network |
| Pipe | 1 | 0x1 | Gas pipe network |
| Power | 2 | 0x2 | Electrical power network |
| Data | 4 | 0x4 | Data/logic network (IC10, sensors, etc.) |
| Chute | 8 | 0x8 | Item transport chute network |
| Elevator | 16 | 0x10 | Vertical elevator network |
| PipeLiquid | 32 | 0x20 | Liquid pipe network |
| LandingPad | 64 | 0x40 | Landing pad (atmos exchange) network |
| LaunchPad | 128 | 0x80 | Launch pad network |
| RoboticArmRail | 256 | 0x100 | Robotic arm rail network |
| PowerAndData | 6 | 0x6 | Composite: Power (2) \| Data (4) |
| All | 2147483647 | int.MaxValue | Matches all networks |

## Key Semantics
<!-- verified: 0.2.6228.27061 @ 2026-06-19 -->

**[Flags] attribute**: NetworkType is a flags enum. A single connector can carry multiple network types simultaneously via bitwise OR. For example, `PowerAndData = 6 = Power | Data`.

**Data networks**: Connectors with `ConnectionType == NetworkType.Data` OR `ConnectionType == NetworkType.PowerAndData` are data connectors and can carry IC10 signals and sensor data.

**Power networks**: Connectors with `ConnectionType` containing the `Power` bit (2) carry electrical power.

**Pipe networks**: Connectors with `ConnectionType` containing the `Pipe` bit (1) carry gases; those with `PipeLiquid` (0x20) carry liquids.

**Composite values**: `PowerAndData` is the only composite constant defined. It allows a single connector to be used for both power and data simultaneously.

## Usage in Connection
<!-- verified: 0.2.6228.27061 @ 2026-06-19 -->

Every Connection stores its network type(s) in the `ConnectionType` field:

```csharp
public class Connection
{
	public NetworkType ConnectionType = NetworkType.Pipe;
	// ...
}
```

Default is `Pipe`. To check if a connector carries data:

```csharp
if ((connection.ConnectionType & NetworkType.Data) != NetworkType.None)
{
	// Connector carries data
}
```

Or use the explicit two-value check (from Device.DataConnection):
```csharp
if (connection.ConnectionType == NetworkType.Data || connection.ConnectionType == NetworkType.PowerAndData)
{
	// Connector is a data connector
}
```

## Verification history

- 2026-06-19: Initial documentation from Assembly-CSharp.dll decompilation (v0.2.6228.27061). All enum values and flags semantics verified.

## Open questions

None at this time.
