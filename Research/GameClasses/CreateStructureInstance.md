---
title: CreateStructureInstance
type: GameClasses
created_in: 0.2.6228.27061
verified_in: 0.2.6228.27061
verified_at: 2026-04-25
sources:
  - $(StationeersPath)\rocketstation_Data\Managed\Assembly-CSharp.dll :: Assets.Scripts.Networking.CreateStructureInstance
related:
  - ./Constructor.md
  - ./MultiConstructor.md
  - ../Protocols/ConstructionCreationMessage.md
  - ../GameSystems/PlacementOrientation.md
tags: [prefab, network]
---

# CreateStructureInstance

Vanilla `Assets.Scripts.Networking.CreateStructureInstance`. Plain managed class used as the in-process placement-intent payload for `Constructor.SpawnConstruct` and as the constructor source for `ConstructionCreationMessage`. Carries the prefab reference, target grid cell, target Quaternion, owner ID, and optional custom color index.

## Class layout
<!-- verified: 0.2.6228.27061 @ 2026-04-25 -->

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

    public CreateStructureInstance(Structure prefabToCreate, Structure oldPrefab)
    {
        Prefab = prefabToCreate;
        LocalGrid = oldPrefab.GridController.WorldToLocal(oldPrefab.ThingTransformPosition);
        LocalRotation = oldPrefab.ThingTransformLocalRotation;
        GridController = oldPrefab.GridController;
        OwnerClientId = oldPrefab.OwnerClientId;
        CustomColor = ((oldPrefab.CustomColor != null) ? oldPrefab.CustomColor.Index : 0);
    }

    public CreateStructureInstance(Structure prefabToCreate, Grid3 localGrid, Quaternion worldRotation, ulong ownerClientId, int colorIndex = -1)
    {
        Prefab = prefabToCreate;
        GridController = GridController.World;
        LocalGrid = localGrid;
        LocalRotation = worldRotation;
        OwnerClientId = ownerClientId;
        CustomColor = colorIndex;
    }
}
```

## Notes
<!-- verified: 0.2.6228.27061 @ 2026-04-25 -->

- `WorldRotation` is a getter aliased to `LocalRotation` - they are NOT separate channels in this struct. A Quaternion passed to either constructor lands in `LocalRotation` and surfaces unchanged on the `WorldRotation` getter. The `ConstructionCreationMessage` later writes both fields as the same value.
- `WorldPosition` is computed from the grid by `GridController.LocalToWorld(LocalGrid)` at access time, not stored. Mutating `LocalGrid` after construction shifts the world position.
- The "old prefab" constructor is used by routines that respawn / replace an existing structure (e.g. swapping a placed prefab for a variant), reading the existing object's transform rather than fresh placement input.

## Verification history
<!-- verified: 0.2.6228.27061 @ 2026-04-25 -->

- 2026-04-25: page created during the placement-pipeline decompile pass.

## Open questions

None.
