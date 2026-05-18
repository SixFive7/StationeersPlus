---
title: Connection
type: GameClasses
created_in: 0.2.6228.27061
verified_in: 0.2.6228.27061
verified_at: 2026-05-15
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

## Verification history

- 2026-05-15: page created. Sourced from decompile line 293240 (class declaration) and the `Connection` field listing immediately after. Created while writing a Bug 2 diagnostic Harmony patch that needed to log per-OpenEnd identity; the patch initially used `connection.name` which fails to compile because `Connection` is a serializable POCO without a Unity name.

## Open questions

None at creation.
