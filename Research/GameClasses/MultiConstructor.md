---
title: MultiConstructor
type: GameClasses
created_in: 0.2.6228.27061
verified_in: 0.2.6228.27061
verified_at: 2026-04-25
sources:
  - $(StationeersPath)\rocketstation_Data\Managed\Assembly-CSharp.dll :: Assets.Scripts.Objects.MultiConstructor
related:
  - ./Constructor.md
  - ./ItemKitLinearRail.md
  - ../GameSystems/PlacementOrientation.md
tags: [prefab, network]
---

# MultiConstructor

Vanilla `MultiConstructor : Stackable, IConstructionKit, IConstructionStarter, IReferencable, IEvaluable` at `Assets.Scripts.Objects.MultiConstructor`. The held-item kit form for placing one of N prefab variants (rail-kit with 8 pieces, etc.). Sibling to `Constructor` (single-prefab kit).

## Class layout
<!-- verified: 0.2.6228.27061 @ 2026-04-25 -->

```csharp
public class MultiConstructor : Stackable, IConstructionKit, IConstructionStarter, IReferencable, IEvaluable
{
    [Header("Multi-Constructable")]
    public List<Structure> Constructables = new List<Structure>();

    [NonSerialized]
    public int LastSelectedIndex;

    public override void OnPrefabLoad()                          // strips null entries from Constructables, clamps LastSelectedIndex
    public virtual void Construct(Grid3, Quaternion, int optionIndex, Item offhandItem, bool authoringMode, ulong, [int quantity])
    public override string GetStationpediaCategory() => Localization.GetInterface(StationpediaCategoryStrings.KitCategory);
    public bool CanBuild(int index)                              // checks Quantity >= Constructables[index].BuildStates[0].Tool.EntryQuantity
    public List<Thing> GetConstructedPrefabs()
}
```

`Constructables` is a serialized list of every prefab the kit can place (e.g. all 8 rail variants for the rail-kit). `LastSelectedIndex` persists which variant the player last picked, so swapping back to the kit reopens on the same option.

## Construct
<!-- verified: 0.2.6228.27061 @ 2026-04-25 -->

Two overloads. The arity-7 form looks up the EntryQuantity for the chosen variant and forwards to the arity-8 form.

```csharp
public virtual void Construct(Grid3 localPosition, Quaternion targetRotation, int optionIndex, Item offhandItem, bool authoringMode, ulong steamId)
{
    int entryQuantity = Constructables[optionIndex].BuildStates[0].Tool.EntryQuantity;
    Construct(localPosition, targetRotation, optionIndex, offhandItem, authoringMode, steamId, entryQuantity);
}

public virtual void Construct(Grid3 localPosition, Quaternion targetRotation, int optionIndex, Item offhandItem, bool authoringMode, ulong steamId, int quantity)
{
    if (authoringMode || OnUseItem(quantity, null))
    {
        CreateStructureInstance createStructureInstance = new CreateStructureInstance(Constructables[optionIndex], localPosition, targetRotation, steamId);
        if (PaintableMaterial != null && CustomColor.Normal != null)
        {
            createStructureInstance.CustomColor = CustomColor.Index;
        }
        if (GameManager.RunSimulation)
        {
            Constructor.SpawnConstruct(createStructureInstance);
        }
    }
}
```

Identical Quaternion handling to `Constructor.Construct`: the incoming `targetRotation` is stored in `CreateStructureInstance.LocalRotation` and forwarded to `Constructor.SpawnConstruct`. Note the asymmetry: `MultiConstructor.Construct` only calls `SpawnConstruct` when `GameManager.RunSimulation == true`, omitting the client-side `new ConstructionCreationMessage(...).SendToServer()` branch. Multi-kit placement on a remote client therefore relies on the calling code (e.g. `InventoryManager.PlacementMode`) to dispatch the network message via `OnServer.UseMultiConstructor(...)` rather than going through this method.

## Differences from Constructor
<!-- verified: 0.2.6228.27061 @ 2026-04-25 -->

| Aspect | Constructor | MultiConstructor |
|---|---|---|
| Built prefab | One: `BuildStructure` | One of N: `Constructables[optionIndex]` |
| Per-prefab quantity | `QuantityUsed` (single int field) | `Constructables[i].BuildStates[0].Tool.EntryQuantity` (per-variant) |
| `OnUsePrimary` override | Yes; calls `Construct(targetLocation.ToGridPosition(), targetRotation, ...)` | None; placement dispatched at the inventory layer |
| Client network branch in own code | Yes: `new ConstructionCreationMessage(...).SendToServer()` | No; relies on caller |
| `Constructables` mutation hook | n/a | `OnPrefabLoad` strips nulls, clamps `LastSelectedIndex` |

For mod patterns that need to insert a new variant into an existing rail-kit / wall-frame-kit, see `Patterns/PrefabCloning.md` "AddToConstructor" (the recipe that calls `mirrorDef.constructor.Constructables.Insert(insertIndex + 1, mirroredDevice as Structure)`).

## Verification history
<!-- verified: 0.2.6228.27061 @ 2026-04-25 -->

- 2026-04-25: page created from a six-agent decompile pass on the placement pipeline. Source content lifted verbatim from `/tmp/decompile_check/Assets/Scripts/Objects/MultiConstructor.cs`. No conflicts.

## Open questions

None.
