---
title: ItemKitLinearRail (multi-constructor kit for rail pieces)
type: GameClasses
created_in: 0.2.6228.27061
verified_in: 0.2.6228.27061
verified_at: 2026-04-20
sources:
  - E:\Steam\steamapps\common\Stationeers\rocketstation_Data\Managed\Assembly-CSharp.dll :: Assets.Scripts.Objects.MultiConstructor
  - E:\Steam\steamapps\common\Stationeers\rocketstation_Data\resources.assets :: GameObject ItemKitLinearRail
related:
  - RoboticArmRail.md
tags: [prefab]
---

## Overview
<!-- verified: 0.2.6228.27061 @ 2026-04-20 -->

`ItemKitLinearRail` is a `MultiConstructor` item: a single held stackable kit that offers multiple structure build options through the radial selector. Its eight constructables are the eight "linear rail" track pieces for the Larre robotic-arm rail system.

Prefab name: `ItemKitLinearRail` (confirmed via `resources.assets` GameObject name scan).

Prefab path_ids observed: 38053 and 41664 (one is the in-world item; the other is the mirrored / cursor preview variant).

## MultiConstructor base class
<!-- verified: 0.2.6228.27061 @ 2026-04-20 -->

`Assets.Scripts.Objects.MultiConstructor` is the base kit type. All "multi-build" held kits in Stationeers use this class:

```csharp
namespace Assets.Scripts.Objects;

public class MultiConstructor : Stackable, IConstructionKit, IConstructionStarter, IReferencable, IEvaluable
{
    [Header("Multi-Constructable")]
    public List<Structure> Constructables = new List<Structure>();

    [NonSerialized]
    public int LastSelectedIndex;

    public override void OnPrefabLoad()
    {
        base.OnPrefabLoad();
        for (int num = Constructables.Count - 1; num >= 0; num--)
            if (Constructables[num] == null)
                Constructables.RemoveAt(num);
        if (LastSelectedIndex >= Constructables.Count)
            LastSelectedIndex = Constructables.Count - 1;
    }

    public virtual void Construct(Grid3 localPosition, Quaternion targetRotation, int optionIndex,
        Item offhandItem, bool authoringMode, ulong steamId)
    {
        int entryQuantity = Constructables[optionIndex].BuildStates[0].Tool.EntryQuantity;
        Construct(localPosition, targetRotation, optionIndex, offhandItem, authoringMode, steamId, entryQuantity);
    }

    public virtual void Construct(Grid3 localPosition, Quaternion targetRotation, int optionIndex,
        Item offhandItem, bool authoringMode, ulong steamId, int quantity)
    {
        if (authoringMode || OnUseItem(quantity, null))
        {
            CreateStructureInstance createStructureInstance = new CreateStructureInstance(
                Constructables[optionIndex], localPosition, targetRotation, steamId);
            if (PaintableMaterial != null && CustomColor.Normal != null)
                createStructureInstance.CustomColor = CustomColor.Index;
            if (GameManager.RunSimulation)
                Constructor.SpawnConstruct(createStructureInstance);
        }
    }

    public List<Thing> GetConstructedPrefabs()
    {
        List<Thing> list = new List<Thing>(Constructables.Count);
        foreach (Structure constructable in Constructables)
            list.Add(constructable);
        return list;
    }
}
```

Key points:

- `Constructables` is a `List<Structure>` populated via Unity's `SerializeField` in the prefab (Inspector-assigned). It does NOT live in decompiled source; a prefab extraction is the only way to read it.
- `MultiConstructor` inherits from `Stackable`, not from `Item` directly. The kit is stackable in inventory.
- The kit's own `CustomColor` propagates to the spawned structure via `CreateStructureInstance.CustomColor`: if the player has painted the kit itself, every placed rail spawns pre-painted that colour.
- `MultiConstructor` is the same base class used by every kit with a radial option selector (e.g. pipe kits, wall kits, chute kits). It is not rail-specific.

## Constructables list (the 8 rail prefabs)
<!-- verified: 0.2.6228.27061 @ 2026-04-20 -->

The `Constructables` list is populated in the prefab, not in code. Direct typetree extraction via UnityPy failed silently on the MonoBehaviour attached to the `ItemKitLinearRail` GameObject (path_id 104509 referenced the rail's MonoBehaviour; the typetree read raised `Expected to read 64432 bytes, but only read 32 bytes`, which is a UnityPy compatibility issue with this particular serialized field layout).

The eight rail-only prefabs confirmed in `resources.assets` under the `StructureRoboticArm` naming convention are the only candidates that match "linear rail" semantics (dock / junction / bypass variants each have their own `ItemKit` prefabs — `ItemKitLarreDockAtmos`, `ItemKitLarreDockBypass`, `ItemKitLarreDockCargo`, `ItemKitLarreDockCollector`, `ItemKitLarreDockHydroponics`):

1. `StructureRoboticArmRailStraight`
2. `StructureRoboticArmRailStraightStop`
3. `StructureRoboticArmRailCorner`
4. `StructureRoboticArmRailCornerStop`
5. `StructureRoboticArmRailInnerCorner`
6. `StructureRoboticArmRailOuterCorner`
7. `StructureRoboticArmRailScrewLeft`
8. `StructureRoboticArmRailScrewRight`

Eight entries match the user's stated "8 rail part classes." The order is prefab-inspector-defined and was not extracted; consumers should iterate `Constructables` rather than assume a specific index.

Every one of these prefabs binds to either `Objects.RoboticArm.RoboticArmRailStraight` or `Objects.RoboticArm.RoboticArmRailInnerCorner` at the C# level; see [RoboticArmRail.md](RoboticArmRail.md) for the full class roster and connection model.

Note the older `StructureRobotArmRail*` (no "ic") prefabs also exist in `resources.assets`: `StructureRobotArmRailControlBox`, `StructureRobotArmRailCorkscrewL`, `StructureRobotArmRailCorkscrewR`, `StructureRobotArmRailCorner`, `StructureRobotArmRailCornerInner`, `StructureRobotArmRailCornerStop`, `StructureRobotArmRailCornerVertical`, `StructureRobotArmRailStraight`, `StructureRobotArmRailStraightStop`. These are a legacy generation retained in the asset bundle and are not the current `ItemKitLinearRail` constructables. Mods that want to cover both generations need to handle both prefab-name prefixes.

## Construct flow
<!-- verified: 0.2.6228.27061 @ 2026-04-20 -->

Player selects a rail piece via the radial on the kit; `LastSelectedIndex` persists the last pick on the kit instance. On placement, `MultiConstructor.Construct(..., optionIndex, ...)` consumes `Constructables[optionIndex].BuildStates[0].Tool.EntryQuantity` from the stack and spawns the structure through `Constructor.SpawnConstruct(createStructureInstance)`. The spawn runs server-authoritative (`GameManager.RunSimulation`).

## Verification history

- 2026-04-20: page creation. `MultiConstructor` source cited verbatim from decompile; `ItemKitLinearRail` prefab existence verified via `resources.assets` GameObject name scan; 8-entry rail-prefab list inferred from the prefab naming pattern and the absence of any other rail-only kit in the prefab list.

## Open questions

- Exact `Constructables` list order and presence of any non-obvious entry (vertical-corner / stop / corkscrew variants that could be hidden). Resolving this requires either a successful typetree extraction of the MonoBehaviour, a Mono.Cecil inspection of the `MultiConstructor`-derived asset block, or in-game observation of the radial menu.
