---
title: Structure placement validation (CanConstruct / CanReplace)
type: GameSystems
created_in: 0.2.6228.27061
verified_in: 0.2.6403.27689
verified_at: 2026-07-14
sources:
  - $(StationeersPath)\rocketstation_Data\Managed\Assembly-CSharp.dll :: Assets.Scripts.Inventory.InventoryManager (placement preview), Assets.Scripts.Objects.Structure / Cable / Device (CanConstruct), Assets.Scripts.Objects.Constructor (SpawnConstruct), CanConstructInfo
  - .work/decomp/0.2.6228.27061/Assembly-CSharp.decompiled.cs :: lines ~270692-270760 (placement preview loop), 276780-276796 (CanConstructInfo), 276940-276953 (Constructor.SpawnConstruct), 371598-371645 (Cable.CanConstruct / CanReplace)
  - .work/decomp/0.2.6403.27689/Assembly-CSharp.decompiled.cs :: lines 288665-288726 (placement preview loop), 312645-312728 (SmallGrid._IsCollision / CanConstruct), 314919-314941 (Structure.CanConstruct), 392588-392694 (Cable._IsCollision / CanConstruct / CanReplace / WillMergeWhenPlaced)
  - Plans/PowerGridPlus/PLAN.md (phase 3 research)
related:
  - ../GameClasses/Cable.md
  - ../GameClasses/Constructor.md
  - ../GameClasses/MultiMergeConstructor.md
  - ../GameClasses/AllowedRotations.md
  - ../GameClasses/SmallCell.md
  - ./StructureRegistration.md
tags: [prefab]
---

# Structure placement validation (CanConstruct / CanReplace)

How the game decides whether a structure may be placed at the cursor, and where a mod can hook to reject a placement with a player-visible message.

## The placement-preview loop calls CanConstruct() every frame
<!-- verified: 0.2.6403.27689 @ 2026-07-14 -->

In `InventoryManager`'s placement-preview path (the per-frame loop that updates the build cursor), after positioning the cursor selection box. Verbatim head at 0.2.6403.27689 (288665-288726); both arms AND into `flag2`, which gates the primary click and `UsePrimaryComplete()`:

```csharp
CanConstructInfo canConstructInfo = ConstructionCursor.CanConstruct();
if (!canConstructInfo.CanConstruct)
{
    if (!string.IsNullOrEmpty(tooltip.State))
    {
        tooltip.State += "\n";
    }
    ref string state = ref tooltip.State;
    state = state + "<color=red>" + canConstructInfo.ErrorMessage + "</color>";
}
flag2 = flag2 && canConstructInfo.CanConstruct;
if (!IsAuthoringMode && ConstructionCursor is IGridMergeable gridMergeable)
{
    Assets.Scripts.Objects.Item inactiveHandItem = Parent.Slots[InactiveHand.SlotId].Occupant as Assets.Scripts.Objects.Item;
    CanConstructInfo canConstructInfo2 = gridMergeable.CanReplace(multiConstructor, inactiveHandItem);
    if (flag2 && !canConstructInfo2.CanConstruct)
    {
        ref string state2 = ref tooltip.State;
        state2 = state2 + "<color=red>" + canConstructInfo2.ErrorMessage + "</color>";
    }
    flag2 &= canConstructInfo2.CanConstruct;
}
...
if (KeyManager.GetMouseDown("Primary") && flag2)
{
    _usePrimaryPosition = _cursorPosition;
    _usePrimaryRotation = ConstructionCursor.ThingTransform.rotation;
    if (ConstructionCursor.BuildPlacementTime > 0f)
    {
        ...
        ActionCoroutine = StartCoroutine(WaitUntilDone(UsePrimaryComplete, ConstructionCursor.BuildPlacementTime / num, ConstructionCursor));
    }
    else
    {
        UsePrimaryComplete();
    }
    return;
}
```

So a placement is allowed only when `CanConstruct()` returns `CanConstruct == true` AND (for `IGridMergeable` cursors, e.g. cables/pipes) `CanReplace(...)` returns `CanConstruct == true`. A false result writes `ErrorMessage` (red) into the cursor tooltip and tints the ghost red. This is the clean hook point for build-time rejection.

(The `<color=red>` markup here renders correctly because the cursor tooltip is a TextMeshPro surface; the F3 console is ImGui and does NOT render such tags, see [ChatBroadcast](./ChatBroadcast.md), "Rendering constraints".)

## CanConstructInfo
<!-- verified: 0.2.6228.27061 @ 2026-05-12 -->

```csharp
public static readonly CanConstructInfo ValidPlacement = new CanConstructInfo(canConstruct: true, string.Empty);

public bool CanConstruct { get; }
public string ErrorMessage { get; }

public CanConstructInfo(bool canConstruct, string errorMessage)
{
    CanConstruct = canConstruct;
    ErrorMessage = errorMessage;
}

public static CanConstructInfo InvalidPlacement(string error)
{
    return new CanConstructInfo(canConstruct: false, error);
}
```

`Structure.CanConstruct()` base walks the structure's grid cells (`CanConstructCell`). Subclasses override and call `base.CanConstruct()`: `Cable.CanConstruct()` (umbilical check), `Device.CanConstruct()` (adjacent-device check), the rocket-tower / launch-pad / fuselage families, etc. A mod can postfix `Cable.CanConstruct` / `Device.CanConstruct` and, if `__result.CanConstruct`, inspect `__instance.OpenEnds` / `Connected()` / `ConnectedCables(...)` (the cursor has a valid `GridController` and `OpenEnds` at preview time) and replace `__result` with `CanConstructInfo.InvalidPlacement("...")`.

## Which method blocks a merge depends on the family: pipes via CanReplace, cables via _IsCollision
<!-- verified: 0.2.6228.27061 @ 2026-05-31 -->

A merge onto an existing small-grid piece is gated for the two `IGridMergeable` families (`Piping`, `Cable`), but the gate lives in a different method for each. This matters for any mod that re-validates a planned placement before completing it: checking only one of `CanConstruct()` / `CanReplace()` catches one family and misses the other.

**Pipes (`Piping`): blocked by `CanReplace`, not by `_IsCollision`.** `Piping._IsCollision` treats a same-type, same-content pipe in the cell as NOT a collision (it is a merge candidate), so `CanConstruct()` returns valid for a pipe-over-pipe merge:

```csharp
protected override bool _IsCollision(SmallGrid smallGrid)
{
    if (smallGrid is Piping piping)
    {
        if (piping.PipeType == PipeType)
        {
            return piping.PipeContentType != base.PipeContentType;
        }
        return true;
    }
    return base._IsCollision(smallGrid);
}
```

The merge-onto-a-long-variant rejection is in `Piping.CanReplace`, via the `DontAllowMergingWithWrench` flag (`public bool DontAllowMergingWithWrench;` on `Piping`, the long Straight 3 / 5 / 10 variants set it):

```csharp
if (DontAllowMergingWithWrench || smallCell.Pipe is Piping { DontAllowMergingWithWrench: not false })
{
    return CanConstructInfo.InvalidPlacement(GameStrings.CannotMergeIMergeable.AsString(ToTooltip()));
}
```

So a pipe merge onto a long variant has `CanConstruct().CanConstruct == true` and `CanReplace(...).CanConstruct == false`.

**Cables (`Cable`): blocked by `_IsCollision` (via `CanConstruct`), NOT by `CanReplace`.** `Cable.CanReplace` does not reject a merge onto a long cable. For a same-`CableType` cell with the merge tool in the off hand it returns `ValidPlacement`:

```csharp
if (multiMergeConstructor.ToolExit.PrefabHash == inactiveHandItem.PrefabHash || (inactiveHandItem.ReplacementOf != null && multiMergeConstructor.ToolExit.PrefabHash == inactiveHandItem.ReplacementOf.PrefabHash))
{
    if (WillMergeWhenPlaced())
    {
        Cable.OnMerge?.Invoke(this);
    }
    if (smallCell.Cable.CableType != CableType)
    {
        return CanConstructInfo.InvalidPlacement(GameStrings.CannotMergeIMergeableOfDifferentType.AsString(smallCell.Cable.DisplayName));
    }
    return CanConstructInfo.ValidPlacement;
}
```

The cable rejection is in `Cable._IsCollision` (reached by `CanConstruct()`), via the `BlockMergeWithOtherCables` flag (`public bool BlockMergeWithOtherCables;` on `Cable`):

```csharp
protected override bool _IsCollision(SmallGrid smallGrid)
{
    if (!(smallGrid is Cable cable))
    {
        return base._IsCollision(smallGrid);
    }
    if (cable.CableType != CableType)
    {
        return true;
    }
    if (!cable.BlockMergeWithOtherCables)
    {
        return BlockMergeWithOtherCables;
    }
    return true;
}
```

So a cable merge that `_IsCollision` rejects has `CanConstruct().CanConstruct == false`, while `CanReplace(...).CanConstruct == true`. (`BlockMergeWithOtherCables` is also read by `Cable.WillMergeWhenPlaced`, a separate method that fires the `Cable.OnMerge` event; it is not part of `CanReplace`'s return path.)

**Consequence.** The interactive placement loop checks both (`flag2 = CanConstruct && (mergeable ? CanReplace : true)`), so it blocks both families. Code that re-runs only one check does not: `CanReplace` alone blocks pipes but passes cables; `CanConstruct` alone blocks cables (when `_IsCollision` rejects) but passes pipes. To reproduce the interactive gate, run both. Whether a same-type cable merge is actually rejected by `_IsCollision` further depends on the runtime `BlockMergeWithOtherCables` value on the cable prefabs, a serialized field that is not visible in the decompiled C#.

## SmallGrid.CanConstruct: the cursor-time occupancy chain, and the same-tier stacking gap
<!-- verified: 0.2.6403.27689 @ 2026-07-14 -->

`SmallGrid.CanConstruct` (0.2.6403.27689 decompile 312669-312728), verbatim. This is the occupancy half of the cursor gate for cables/pipes/chutes: it walks the ghost's cells and consults `_IsCollision` per occupant slot:

```csharp
public override CanConstructInfo CanConstruct()
{
    Grid3[] array = (Grid3[])GridBounds.GetLocalSmallGrid(base.ThingTransformPosition, base.ThingTransformRotation);
    for (int i = 0; i < array.Length; i++)
    {
        Grid3 localGrid = array[i];
        SmallCell smallCell = base.GridController.GetSmallCell(localGrid);
        if (this is IRocketInternals { StrictlyInternal: not false } && !(smallCell?.Owner is RocketNetwork))
        {
            return CanConstructInfo.InvalidPlacement(GameStrings.CannotPlaceOutsideRocket);
        }
        if (smallCell?.Owner == null && this is IRocketInternals)
        {
            Grid3 localGrid2 = new Grid3(localGrid.x, (float)localGrid.y + SmallGridSize * 10f, localGrid.z);
            Grid3 localGrid3 = new Grid3(localGrid.x, (float)localGrid.y - SmallGridSize * 10f, localGrid.z);
            SmallCell smallCell2 = base.GridController.GetSmallCell(localGrid2);
            SmallCell smallCell3 = base.GridController.GetSmallCell(localGrid3);
            if (smallCell2?.Owner != null || smallCell3?.Owner != null)
            {
                return CanConstructInfo.InvalidPlacement(GameStrings.PlacementConnectingNeedsFuselage.DisplayString);
            }
        }
        if (smallCell != null)
        {
            if ((bool)smallCell.Cable && _IsCollision(smallCell.Cable))
            {
                return CanConstructInfo.InvalidPlacement(GameStrings.PlacementBlockedBySmallGrid.AsString(smallCell.Cable.DisplayName));
            }
            if ((bool)smallCell.Device && _IsCollision(smallCell.Device))
            {
                return CanConstructInfo.InvalidPlacement(GameStrings.PlacementBlockedBySmallGrid.AsString(smallCell.Device.DisplayName));
            }
            if ((bool)smallCell.Pipe && _IsCollision(smallCell.Pipe))
            {
                return CanConstructInfo.InvalidPlacement(GameStrings.PlacementBlockedBySmallGrid.AsString(smallCell.Pipe.DisplayName));
            }
            if ((bool)smallCell.Chute && _IsCollision(smallCell.Chute))
            {
                return CanConstructInfo.InvalidPlacement(GameStrings.PlacementBlockedBySmallGrid.AsString(smallCell.Chute.DisplayName));
            }
            if (smallCell.Rail != null && _IsCollision((SmallGrid)smallCell.Rail))
            {
                return CanConstructInfo.InvalidPlacement(GameStrings.PlacementBlockedBySmallGrid.AsString(smallCell.Rail.DisplayName));
            }
            if ((bool)smallCell.Other && _IsCollision(smallCell.Other))
            {
                return CanConstructInfo.InvalidPlacement(GameStrings.PlacementBlockedBySmallGrid.AsString(smallCell.Other.DisplayName));
            }
            if (smallCell.Owner != null && smallCell.Owner.IsCollision(this, smallCell.SmallGrid))
            {
                return CanConstructInfo.InvalidPlacement(GameStrings.PlacementConnectingNeedsFuselage.DisplayString);
            }
        }
    }
    if (!DualRegister)
    {
        return CanConstructInfo.ValidPlacement;
    }
    return base.CanConstruct();
}
```

(Cables are not `DualRegister`, so `Structure.CanConstruct` (314919-314941, large-grid `Cell` occupancy via `CanConstructCell`) never runs for them.)

The `SmallGrid._IsCollision` base + helper (312645-312667):

```csharp
public virtual bool IsPipeEndCollision(SmallGrid smallGrid)
{
    foreach (Connection openEnd in OpenEnds)
    {
        foreach (Connection openEnd2 in smallGrid.OpenEnds)
        {
            if ((openEnd.Transform.position - openEnd2.Transform.position).sqrMagnitude < 0.010000001f)
            {
                return true;
            }
        }
    }
    return false;
}

protected virtual bool _IsCollision(SmallGrid smallGrid)
{
    if ((smallGrid.SmallCollisionType & SmallCollisionType) != SmallGridBlock.None)
    {
        return true;
    }
    return IsPipeEndCollision(smallGrid);
}
```

0.2.6403.27689 line refs for the cable-family overrides (full verbatim bodies on [Cable](../GameClasses/Cable.md)): `Cable._IsCollision` 392588-392603, `Cable.CanConstruct` 392625-392632 (umbilical check then base), `Cable.CanReplace` 392634-392672, `Cable.WillMergeWhenPlaced` 392674-392694.

**The same-tier stacking gap.** For a cable ghost over an existing SAME-tier cable (neither `BlockMergeWithOtherCables` flag set), `Cable._IsCollision` returns false, so `SmallGrid.CanConstruct` PASSES. The thing that still blocks the click is the second arm of the cursor gate, `IGridMergeable.CanReplace`, which demands the merge tool. For a DIFFERENT-tier cable, `_IsCollision` returns true and `CanConstruct` fails with `PlacementBlockedBySmallGrid`. Consequence: a programmatic caller that honors `CanConstruct` but skips `CanReplace` can still stack same-tier cables into one cell; the registration path itself has NO occupancy check of any kind (`_IsCollision` call-site census and the register chain on [StructureRegistration](./StructureRegistration.md)), and the resulting stacked-pair misbehavior is on [SmallCell](../GameClasses/SmallCell.md). Code that re-validates a planned placement must run BOTH arms to reproduce the interactive gate (consistent with the per-family section above).

## The gate is client-side preview only, not server-authoritative
<!-- verified: 0.2.6403.27689 @ 2026-07-14 -->

```csharp
public static Structure SpawnConstruct(CreateStructureInstance instance)   // on Constructor
{
    if (GameManager.RunSimulation)
    {
        Structure structure = Thing.Create<Structure>(instance.Prefab, instance.WorldPosition, instance.WorldRotation, 0L);
        structure.SetStructureData(instance.LocalRotation, instance.OwnerClientId, instance.LocalGrid, instance.CustomColor);
        return structure;
    }
    if (Assets.Scripts.Networking.NetworkManager.IsClient)
        new ConstructionCreationMessage(instance).SendToServer();
    return null;
}
```

`SpawnConstruct` (server side, `GameManager.RunSimulation`) does NOT re-run `CanConstruct`; it just `Thing.Create`s the structure. So a `CanConstruct`/`CanReplace` postfix stops a vanilla/modded client from triggering placement and gives a clear message, but it is not anti-cheat against a hand-crafted `ConstructionCreationMessage`. That is fine for a balance / lore restriction; for a hard rule, add a server-side check (e.g. in an `OnRegistered` postfix that severs / errors the structure) as well.

Re-confirmed at 0.2.6403.27689: the whole `Thing.Create` -> `Register` -> `AddSmallGridStructure` chain contains no `CanConstruct`, no `_IsCollision`, no occupancy test of any kind (call-site census on [StructureRegistration](./StructureRegistration.md); registration-time interception options on [Cable](../GameClasses/Cable.md), "Registration-time guard").

(Authoring mode bypasses item cost but, per [Constructor](../GameClasses/Constructor.md), not the placement gates -- and the placement loop above explicitly skips the `IGridMergeable.CanReplace` branch only when `IsAuthoringMode`.)

## Verification history

- 2026-05-12: page created. Sourced from a phase 3 research dive (planned mod "Power Grid Plus") into `.work/decomp/0.2.6228.27061/Assembly-CSharp.decompiled.cs` lines ~270692-270760, 276780-276796, 276940-276953, 371598-371645; verbatim excerpts of the placement-preview `CanConstruct`/`CanReplace` block, `CanConstructInfo`, `Constructor.SpawnConstruct`, and `Cable.CanConstruct`/`CanReplace`. Consistent with [Constructor](../GameClasses/Constructor.md) ("authoring mode bypasses item-cost only, NOT placement gates") and [AllowedRotations](../GameClasses/AllowedRotations.md) (the same loop auto-corrects the cursor face).
- 2026-05-31: added "Which method blocks a merge depends on the family" section. Sourced from `.work/decomp/0.2.6228.27061/Assembly-CSharp.decompiled.cs` `Piping._IsCollision` (363943-363954), `Piping.CanReplace` (363956-, the `DontAllowMergingWithWrench` branch at 363974-363977), `Cable._IsCollision` (371561-371576), `Cable.CanReplace` (371607-371645), and the field declarations `Piping.DontAllowMergingWithWrench` (363936) / `Cable.BlockMergeWithOtherCables` (371306). Finding: the merge-block is in `CanReplace` for pipes but in `_IsCollision` (via `CanConstruct`) for cables, so a mod re-checking only one method catches one family. Surfaced while validating a ZoopMod upstream fix for the long-variant merge crash (see [MultiMergeConstructor](../GameClasses/MultiMergeConstructor.md)).
- 2026-07-14: refreshed against the 0.2.6403.27689 decompile during the mixed-tier cable network guard research pass. Restamped "The placement-preview loop" with the 0.2.6403 verbatim head (288665-288726, now including the primary-click gate that runs `UsePrimaryComplete` / `WaitUntilDone`) and noted the tooltip's `<color=red>` renders because the cursor tooltip is TextMeshPro, unlike the ImGui console. Added the "SmallGrid.CanConstruct: the cursor-time occupancy chain, and the same-tier stacking gap" section: `SmallGrid.CanConstruct` verbatim (312669-312728), `SmallGrid._IsCollision` + `IsPipeEndCollision` verbatim (312645-312667), the non-DualRegister note (`Structure.CanConstruct` 314919-314941 never runs for cables), refreshed 0.2.6403 line refs for the cable-family methods (`_IsCollision` 392588-392603, `CanConstruct` 392625-392632, `CanReplace` 392634-392672, `WillMergeWhenPlaced` 392674-392694), and the same-tier stacking gap: same-tier cable-over-cable passes `CanConstruct` (the click is blocked only by the `CanReplace` merge-tool arm in `InventoryManager`), so a programmatic caller honoring `CanConstruct` but skipping `CanReplace` can still stack same-tier cables. Restamped "The gate is client-side preview only" with the 0.2.6403 re-confirmation that the Create -> Register chain performs no occupancy re-validation (census on [StructureRegistration](./StructureRegistration.md)). The per-family merge-block section keeps its 0.2.6228.27061 stamp (the pipe half was not re-read this pass; the cable half's re-verified refs live in the new section). No prior claim contradicted: the new same-tier note covers the no-merge-tool case, which is disjoint from the 2026-05-31 finding that `CanReplace` passes cables WITH the tool.

## Open questions

None at creation.
