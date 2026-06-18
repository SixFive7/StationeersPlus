---
title: Connection
type: GameClasses
created_in: 0.2.6228.27061
verified_in: 0.2.6228.27061
verified_at: 2026-06-18
sources:
  - $(StationeersPath)\rocketstation_Data\Managed\Assembly-CSharp.dll :: Assets.Scripts.Objects.Pipes.Connection
related:
  - ./CableNetwork.md
  - ./Cable.md
  - ./Grid3.md
tags: [power, network]
---

# Connection

A `Connection` is one OpenEnd of a `SmallGrid` (cable, pipe, device, chute, rail). The list of `OpenEnds` on each `SmallGrid` is what `ConnectedCables` / `ConnectedDevices` / `Connected` iterate over to discover adjacency.

## Fully qualified type and shape
<!-- verified: 0.2.6228.27061 @ 2026-05-15 -->

`Assets.Scripts.Objects.Pipes.Connection` (decompile line 293240). It is a plain `[Serializable]` POCO, NOT a `MonoBehaviour` and NOT a `UnityEngine.Object`. So it has **no** `name` property; the `Connection` object has no Unity identity of its own. Patches that want a human-readable identifier should use `connection.Transform.gameObject.name` (which is the name of the child GameObject the Connection was attached to in the prefab, e.g. `OpenEnd1`, `Input`, `Output`).

Verbatim shape (line 293239 onward):

```csharp
[Serializable]
public class Connection
{
    public NetworkType ConnectionType = NetworkType.Pipe;
    public Transform Transform;
    public Collider Collider;
    public ConnectionRole ConnectionRole;
    [ReadOnly] public Renderer HelperRenderer;
    public SmallGrid Parent;
    public Grid3 LocalGrid;
    public Grid3 FacingGrid;
    private bool _isInitialized;
    private bool _isValid;
    public Vector3 TransformUp { get; set; }
    public bool IsValid { get { ... } }
    ...
}
```

Key fields for adjacency lookups:

- `ConnectionType` is a `NetworkType` enum bitmask (Pipe / Power / Data / Chute / Rail). Used by `SmallGrid.IsConnected(Connection)` to gate cross-network mismatches.
- `Transform` is the Unity transform of the GameObject the Connection was prefab-bound to. `Transform.position` is what `SmallGrid.ConnectedCables()` and friends pass to `GridController.WorldToLocalGrid` to find the adjacent `SmallCell`.
- `LocalGrid` and `FacingGrid` are precomputed `Grid3` coordinates: `LocalGrid` is the cell the Connection sits in (the one the parent SmallGrid's body occupies on that side), `FacingGrid` is the cell the Connection points AT (the cell beyond the body). `IsConnected` asserts that the other side's `LocalGrid` equals this side's `FacingGrid`.
- `Parent` is the `SmallGrid` (Cable, Device, Pipe, etc.) that owns this Connection.
- `ConnectionRole` is the directional/role tag (Input, Output, Output2, Waste, ...).

## Data-port discovery and the PowerAndData connector
<!-- verified: 0.2.6228.27061 @ 2026-06-18 -->

`NetworkType` is `[Flags]` (decompile L293197): `None=0, Pipe=1, Power=2, Data=4, Chute=8, Elevator=0x10, PipeLiquid=0x20, LandingPad=0x40, LaunchPad=0x80, RoboticArmRail=0x100, PowerAndData=6 (= Power | Data), All=int.MaxValue`. A device's "data port" is not a dedicated field; it is discovered from the `OpenEnds` list:

- `Device.DataConnection` (decompile L349649) returns `OpenEnds.Find(c => c.ConnectionType == NetworkType.Data || c.ConnectionType == NetworkType.PowerAndData)` -- the first connector carrying data, whether a Data-only connector or a combined PowerAndData connector.
- `SmallGrid.HasDataConnection` (a `[ReadOnly]` bool, L293498) is set in `SmallGrid.Awake` (L293563) by scanning `OpenEnds` for those same two types.
- `SmallGrid.GetConnection(NetworkType type, ConnectionRole role, out Connection)` (L293576) fetches one connector by type + role.

Consequences for "how many connectors does a device have":

- Data riding on a power connector = an `OpenEnds` entry typed `PowerAndData` (=6); `DataConnection` returns that same physical connector. Typical 2-connector power device (power-in + power-out, one of them PowerAndData).
- A dedicated data port = a separate `OpenEnds` entry typed `Data` (=4) alongside the `Power` connectors. This is the 3-connector shape on rocket-internal device prefabs (see [Transformer](./Transformer.md), [Battery](./Battery.md), [RocketPowerUmbilical](./RocketPowerUmbilical.md)).

The number of `OpenEnds` entries and each entry's `ConnectionType` / `ConnectionRole` are serialized prefab asset data (the only `ConnectionType =` assignment in the decompile is the field default `= NetworkType.Pipe`). The decompile gives the model; the per-prefab layout requires a live read (InspectorPlus `OpenEnds` dump, or a `Prefab.AllPrefabs` ScenarioRunner dump).

## Verification history

- 2026-06-18: added "Data-port discovery and the PowerAndData connector" section (NetworkType.PowerAndData=6 at L293197, Device.DataConnection L349649, SmallGrid.HasDataConnection L293498 set in Awake L293563, GetConnection L293576). Additive, sourced from a PowerGridPlus rocket-device investigation; no conflict with existing content.
- 2026-05-15: page created. Sourced from decompile line 293240 (class declaration) and the `Connection` field listing immediately after. Created while writing a Bug 2 diagnostic Harmony patch that needed to log per-OpenEnd identity; the patch initially used `connection.name` which fails to compile because `Connection` is a serializable POCO without a Unity name.

## Open questions

None at creation.
