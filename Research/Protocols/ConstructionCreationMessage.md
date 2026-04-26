---
title: ConstructionCreationMessage
type: Protocols
created_in: 0.2.6228.27061
verified_in: 0.2.6228.27061
verified_at: 2026-04-25
sources:
  - $(StationeersPath)\rocketstation_Data\Managed\Assembly-CSharp.dll :: Assets.Scripts.Networking.ConstructionCreationMessage
  - $(StationeersPath)\rocketstation_Data\Managed\Assembly-CSharp.dll :: Assets.Scripts.Networking.CreateStructureInstance
related:
  - ../GameClasses/Constructor.md
  - ../GameClasses/Structure.md
  - ../GameSystems/PlacementOrientation.md
tags: [network, prefab]
---

# ConstructionCreationMessage

Vanilla `ProcessedMessage<ConstructionCreationMessage>` at `Assets.Scripts.Networking.ConstructionCreationMessage`. The client-to-server message that requests construction of a `Structure` when the client is not the simulation owner. Carries the placement Quaternion losslessly via two `WriteQuaternion` slots.

## Wire format
<!-- verified: 0.2.6228.27061 @ 2026-04-25 -->

```csharp
public class ConstructionCreationMessage : ProcessedMessage<ConstructionCreationMessage>
{
    public int PrefabHash { get; set; }
    public Vector3 WorldPosition { get; set; }
    public Quaternion WorldRotation { get; set; }
    public Quaternion LocalRotation { get; set; }
    public ulong OwnerClientId { get; set; }
    public Grid3 LocalGrid { get; set; }
    public int CustomColorIndex { get; set; }
}
```

Serialize / Deserialize in order:

```csharp
public override void Serialize(RocketBinaryWriter writer)
{
    writer.WriteInt32(PrefabHash);
    writer.WriteVector3(WorldPosition);
    writer.WriteQuaternion(WorldRotation);
    writer.WriteQuaternion(LocalRotation);
    writer.WriteUInt64(OwnerClientId);
    writer.WriteGrid3(LocalGrid);
    writer.WriteInt32(CustomColorIndex);
}

public override void Deserialize(RocketBinaryReader reader)
{
    PrefabHash = reader.ReadInt32();
    WorldPosition = reader.ReadVector3();
    WorldRotation = reader.ReadQuaternion();
    LocalRotation = reader.ReadQuaternion();
    OwnerClientId = reader.ReadUInt64();
    LocalGrid = reader.ReadGrid3();
    CustomColorIndex = reader.ReadInt32();
}
```

Both rotations use the full 16-byte float-quaternion encoding (no half-precision, no axis-angle compaction). Any Quaternion the client computes - including non-cardinal, non-floor placements - survives the round-trip exactly.

## Server handler
<!-- verified: 0.2.6228.27061 @ 2026-04-25 -->

```csharp
public override void Process(long hostId)
{
    base.Process(hostId);
    OnServer.Create<Structure>(PrefabHash, WorldPosition, WorldRotation).SetStructureData(LocalRotation, OwnerClientId, LocalGrid, CustomColorIndex);
}
```

Mirrors the in-process simulation path in `Constructor.SpawnConstruct`: the server resolves the prefab by hash, instantiates with the world Quaternion, and writes the `LocalRotation` Quaternion into the new structure's `Direction` field via `SetStructureData`. No server-side validation of rotation; whatever the client sent lands on the spawned Structure.

## CreateStructureInstance (companion)
<!-- verified: 0.2.6228.27061 @ 2026-04-25 -->

`Assets.Scripts.Networking.CreateStructureInstance` is the in-process placement-intent struct used by `Constructor.SpawnConstruct` and as the source of the `ConstructionCreationMessage` payload.

```csharp
public class CreateStructureInstance
{
    public Grid3 LocalGrid;
    public Quaternion LocalRotation;
    public Structure Prefab;
    public GridController GridController;
    public int CustomColor = -1;
    public ulong OwnerClientId;

    public Vector3 WorldPosition => GridController.LocalToWorld(LocalGrid);
    public Quaternion WorldRotation => LocalRotation;

    public CreateStructureInstance(Structure prefabToCreate, Structure oldPrefab) { /* ... */ }
    public CreateStructureInstance(Structure prefabToCreate, Grid3 localGrid, Quaternion worldRotation, ulong ownerClientId, int colorIndex = -1) { /* ... */ }
}
```

Note: the public-facing `WorldRotation` getter returns `LocalRotation` directly. The two fields are aliased; there is no separate "local-relative-to-something-else" rotation in this struct. The caller passes a single Quaternion; both `WorldRotation` and `LocalRotation` channels of the message carry the same value.

## Verification history
<!-- verified: 0.2.6228.27061 @ 2026-04-25 -->

- 2026-04-25: page created from a six-agent decompile pass on the placement pipeline. Source content lifted verbatim from `/tmp/decompile_check/Assets/Scripts/Networking/ConstructionCreationMessage.cs` and `CreateStructureInstance.cs`. No conflicts.

## Open questions

None.
