---
title: ConnectionRole
type: GameClasses
created_in: 0.2.6228.27061
verified_in: 0.2.6228.27061
verified_at: 2026-06-19
sources:
  - rocketstation_Data/Managed/Assembly-CSharp.dll :: ConnectionRole enum (line 293230)
related:
  - Connection.md
  - NetworkType.md
tags: [network, power, logic]
---

# ConnectionRole

An enum that specifies the direction or purpose of flow through a connector.

## Enum Values
<!-- verified: 0.2.6228.27061 @ 2026-06-19 -->

From Assembly-CSharp.dll, lines 293230-293238:

```csharp
public enum ConnectionRole
{
	None,
	Input,
	Input2,
	Output,
	Output2,
	Waste
}
```

**Value reference:**

| Member | Value | Semantics |
|--------|-------|-----------|
| None | 0 | No directional role (generic connector) |
| Input | 1 | Primary input flow |
| Input2 | 2 | Secondary input flow |
| Output | 3 | Primary output flow |
| Output2 | 4 | Secondary output flow |
| Waste | 5 | Waste/discharge output |

## Semantics
<!-- verified: 0.2.6228.27061 @ 2026-06-19 -->

**None**: Generic connector with no enforced direction. Used for symmetric operations (e.g., a pipe cross-connector, or a balanced atmospheric pipe).

**Input / Input2**: Ingress points for gas, liquid, items, power, or data. Devices may have two input connectors (e.g., a pipe join receiving from two branches).

**Output / Output2**: Egress points. Devices may have two output connectors (e.g., a splitter or dual-export device).

**Waste**: Designated waste/byproduct outlet. Semantically distinct from Output for UI and gameplay clarity.

## Usage in Connection
<!-- verified: 0.2.6228.27061 @ 2026-06-19 -->

Every Connection stores its role:

```csharp
public class Connection
{
	public ConnectionRole ConnectionRole;
	// ...
}
```

The role is paired with the network type (`ConnectionType`). For example:
- A data-input connector: `ConnectionType = NetworkType.Data, ConnectionRole = ConnectionRole.Input`
- A power-output connector: `ConnectionType = NetworkType.Power, ConnectionRole = ConnectionRole.Output`
- A combined power-and-data input: `ConnectionType = NetworkType.PowerAndData, ConnectionRole = ConnectionRole.Input`

## Display and UI
<!-- verified: 0.2.6228.27061 @ 2026-06-19 -->

The Connection class uses both NetworkType and ConnectionRole for display names:

```csharp
public string ToStationpediaName()
{
	if (ConnectionRole != ConnectionRole.None)
	{
		StringBuilder stringBuilder = new StringBuilder();
		stringBuilder.Append(ConnectionType.GetName()).Append(" ");
		stringBuilder.Append(ConnectionRole.GetName());
		return stringBuilder.ToString();
	}
	if (ConnectionRole == ConnectionRole.None)
	{
		return GameStrings.ConnectionGeneric.DisplayString;
	}
	return EnumCollections.NetworkType.GetName(ConnectionType);
}
```

Examples:
- "Data Input" (Data + Input)
- "Power Output" (Power + Output)
- "Power And Data Input" (PowerAndData + Input)
- Generic connection name if ConnectionRole is None

## Verification history

- 2026-06-19: Initial documentation from Assembly-CSharp.dll decompilation (v0.2.6228.27061). All enum values and role semantics verified.

## Open questions

None at this time.
