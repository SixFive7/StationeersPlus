---
title: Structure placement validation (CanConstruct / CanReplace)
type: GameSystems
created_in: 0.2.6228.27061
verified_in: 0.2.6228.27061
verified_at: 2026-05-12
sources:
  - $(StationeersPath)\rocketstation_Data\Managed\Assembly-CSharp.dll :: Assets.Scripts.Inventory.InventoryManager (placement preview), Assets.Scripts.Objects.Structure / Cable / Device (CanConstruct), Assets.Scripts.Objects.Constructor (SpawnConstruct), CanConstructInfo
  - .work/decomp/0.2.6228.27061/Assembly-CSharp.decompiled.cs :: lines ~270692-270760 (placement preview loop), 276780-276796 (CanConstructInfo), 276940-276953 (Constructor.SpawnConstruct), 371598-371645 (Cable.CanConstruct / CanReplace)
  - Plans/PowerGridPlus/PLAN.md (phase 3 research)
related:
  - ../GameClasses/Cable.md
  - ../GameClasses/Constructor.md
  - ../GameClasses/MultiMergeConstructor.md
  - ../GameClasses/AllowedRotations.md
tags: [prefab]
---

# Structure placement validation (CanConstruct / CanReplace)

How the game decides whether a structure may be placed at the cursor, and where a mod can hook to reject a placement with a player-visible message.

## The placement-preview loop calls CanConstruct() every frame
<!-- verified: 0.2.6228.27061 @ 2026-05-12 -->

In `InventoryManager`'s placement-preview path (the per-frame loop that updates the build cursor), after positioning the cursor selection box:

```csharp
CanConstructInfo canConstructInfo = ConstructionCursor.CanConstruct();
if (!canConstructInfo.CanConstruct)
{
    if (!string.IsNullOrEmpty(tooltip.State))
        tooltip.State += "\n";
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
    ... begin placement (UsePrimaryComplete / WaitUntilDone) ...
    return;
}
UnityEngine.Color color = (flag2 ? UnityEngine.Color.green : UnityEngine.Color.red);
```

So a placement is allowed only when `CanConstruct()` returns `CanConstruct == true` AND (for `IGridMergeable` cursors, e.g. cables/pipes) `CanReplace(...)` returns `CanConstruct == true`. A false result writes `ErrorMessage` (red) into the cursor tooltip and tints the ghost red. This is the clean hook point for build-time rejection.

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

## The gate is client-side preview only, not server-authoritative
<!-- verified: 0.2.6228.27061 @ 2026-05-12 -->

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

(Authoring mode bypasses item cost but, per [Constructor](../GameClasses/Constructor.md), not the placement gates -- and the placement loop above explicitly skips the `IGridMergeable.CanReplace` branch only when `IsAuthoringMode`.)

## Verification history

- 2026-05-12: page created. Sourced from a phase 3 research dive (planned mod "Power Grid Plus") into `.work/decomp/0.2.6228.27061/Assembly-CSharp.decompiled.cs` lines ~270692-270760, 276780-276796, 276940-276953, 371598-371645; verbatim excerpts of the placement-preview `CanConstruct`/`CanReplace` block, `CanConstructInfo`, `Constructor.SpawnConstruct`, and `Cable.CanConstruct`/`CanReplace`. Consistent with [Constructor](../GameClasses/Constructor.md) ("authoring mode bypasses item-cost only, NOT placement gates") and [AllowedRotations](../GameClasses/AllowedRotations.md) (the same loop auto-corrects the cursor face).

## Open questions

None at creation.
