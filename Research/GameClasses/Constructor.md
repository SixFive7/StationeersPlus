---
title: Constructor
type: GameClasses
created_in: 0.2.6228.27061
verified_in: 0.2.6228.27061
verified_at: 2026-04-25
sources:
  - $(StationeersPath)\rocketstation_Data\Managed\Assembly-CSharp.dll :: Assets.Scripts.Objects.Constructor
related:
  - ./MultiConstructor.md
  - ./Structure.md
  - ../GameSystems/PlacementOrientation.md
  - ../Protocols/ConstructionCreationMessage.md
tags: [prefab, network]
---

# Constructor

Vanilla `Constructor : Stackable, IConstructionKit` at `Assets.Scripts.Objects.Constructor`. The held-item kit form for placing a single `Structure` prefab. Used by every "single-item kit" (kit-cable, kit-wall-frame, kit-airlock, ...). The companion `MultiConstructor` covers multi-prefab kits (rail kit with 8 rail variants, etc.).

## Class layout
<!-- verified: 0.2.6228.27061 @ 2026-04-25 -->

```csharp
public class Constructor : Stackable, IConstructionKit
{
    [Header("Constructor")]
    public Structure BuildStructure;
    public int QuantityUsed = 1;

    public override void OnUsePrimary(Vector3 targetLocation, Quaternion targetRotation, ulong steamId, bool authoringMode)
    public virtual void Construct(Grid3 localPosition, Quaternion targetRotation, bool authoringMode, ulong steamId)
    public static Structure SpawnConstruct(CreateStructureInstance instance)
    public List<Thing> GetConstructedPrefabs()
}
```

`BuildStructure` is the prefab the kit places. `QuantityUsed` is how many of the stack the kit consumes per placed structure (default 1).

## OnUsePrimary -> Construct
<!-- verified: 0.2.6228.27061 @ 2026-04-25 -->

```csharp
public override void OnUsePrimary(Vector3 targetLocation, Quaternion targetRotation, ulong steamId, bool authoringMode)
{
    base.OnUsePrimary(targetLocation, targetRotation, steamId, authoringMode);
    Construct(targetLocation.ToGridPosition(), targetRotation, authoringMode, steamId);
}

public virtual void Construct(Grid3 localPosition, Quaternion targetRotation, bool authoringMode, ulong steamId)
{
    if (authoringMode || OnUseItem(QuantityUsed, null))
    {
        CreateStructureInstance createStructureInstance = new CreateStructureInstance(BuildStructure, localPosition, targetRotation, steamId);
        if (PaintableMaterial != null && CustomColor.Normal != null)
        {
            createStructureInstance.CustomColor = CustomColor.Index;
        }
        SpawnConstruct(createStructureInstance);
    }
}
```

The Quaternion parameter `targetRotation` flows through unchanged. `authoringMode == true` bypasses the `OnUseItem(...)` quantity-consumption check but does NOT bypass any other gate; it is purely a creative-mode item-cost waiver.

## SpawnConstruct: simulation vs client routing
<!-- verified: 0.2.6228.27061 @ 2026-04-25 -->

```csharp
public static Structure SpawnConstruct(CreateStructureInstance instance)
{
    if (GameManager.RunSimulation)
    {
        Structure structure = Thing.Create<Structure>(instance.Prefab, instance.WorldPosition, instance.WorldRotation, 0L);
        structure.SetStructureData(instance.LocalRotation, instance.OwnerClientId, instance.LocalGrid, instance.CustomColor);
        return structure;
    }
    if (NetworkManager.IsClient)
    {
        new ConstructionCreationMessage(instance).SendToServer();
    }
    return null;
}
```

Two execution branches based on simulation role:

- `GameManager.RunSimulation == true` (single-player or multiplayer host): create the structure directly via `Thing.Create<Structure>(prefab, worldPos, worldRot)` and call `SetStructureData(localRot, ...)`. The full Quaternion lands on the new GameObject's transform AND is mirrored into `Structure.Direction` (see `GameSystems/PlacementOrientation.md`).
- `RunSimulation == false && NetworkManager.IsClient == true` (multiplayer remote client): wrap the placement intent in a `ConstructionCreationMessage` and send to server. The server runs the same `Thing.Create + SetStructureData` path inside the message's `Process(hostId)` handler.

Either branch preserves the Quaternion losslessly; the wire format uses full `WriteQuaternion` (16 bytes) per `Protocols/ConstructionCreationMessage.md`.

## Authoring mode
<!-- verified: 0.2.6228.27061 @ 2026-04-25 -->

`authoringMode` is enabled when the player's active hand holds an `AuthoringTool` (creative-mode wrench). Source: `InventoryManager.cs:422` `public static bool IsAuthoringMode => Instance.ActiveHand.Slot.Occupant is AuthoringTool;`. The flag travels into `Construct(... authoringMode ...)` and only affects the `OnUseItem` quantity-consumption call. It does NOT bypass `AllowedRotations`, `CanConstruct`, or any other gate. A player in authoring mode still cannot place a `AllowedRotations.Floor` device on a wall via vanilla input; the cursor's `UpdatePlacement` snaps the orientation to floor before `OnUsePrimary` fires.

## Verification history
<!-- verified: 0.2.6228.27061 @ 2026-04-25 -->

- 2026-04-25: page created from a six-agent decompile pass on the placement pipeline. Source content lifted verbatim from `/tmp/decompile_check/Assets/Scripts/Objects/Constructor.cs`. No conflicts.

## Open questions

None.
