---
title: Stackable
type: GameClasses
created_in: 0.2.6228.27061
verified_in: 0.2.6228.27061
verified_at: 2026-05-31
sources:
  - $(StationeersPath)\rocketstation_Data\Managed\Assembly-CSharp.dll :: Assets.Scripts.Objects.Items.Stackable
  - DedicatedServer ScenarioRunner probe `merge-long-variant-num4` (live runtime read on game 0.2.6228.27061, 2026-05-31): stock ItemKitPipe StackedGeneCollections != null; num4 = -2 on 5 merge-capable kits
  - $(StationeersPath)\rocketstation_Data\Managed\Assembly-CSharp.dll :: Assets.Scripts.Objects.MultiConstructor
  - $(StationeersPath)\rocketstation_Data\Managed\Assembly-CSharp.dll :: Assets.Scripts.Objects.MultiMergeConstructor
  - $(StationeersPath)\rocketstation_Data\Managed\Assembly-CSharp.dll :: Assets.Scripts.Objects.OnServer (UseMultiConstructor)
  - $(StationeersPath)\rocketstation_Data\Managed\Assembly-CSharp.dll :: Assets.Scripts.Networking.UseStackableMessage
related:
  - ./MultiConstructor.md
  - ./MultiMergeConstructor.md
  - ./Constructor.md
tags: [prefab, slots]
---

# Stackable

Vanilla `Stackable : Item, IQuantity, ITradable, IEvaluable, IReferencable, IMergeable` at `Assets.Scripts.Objects.Items.Stackable` (decompile line 335033 in game version 0.2.6228.27061). The base class for every quantity-bearing held item: ores, sheets (`SteelSheet`, `GlassSheet`, `PlasticSheet`), `Plant`, the construction kits (`Constructor`, `MultiConstructor`, `MultiMergeConstructor`, `DynamicThingConstructor`), and more. Holds the integer `Quantity` and the optional gene-stack list `StackedGeneCollections`.

This page exists because `Stackable.OnUseItem` is the crash site for an `ArgumentOutOfRangeException ("count")` thrown from `List<GeneCollection>.RemoveRange` when a construction kit consumes a **negative** quantity. The negative quantity is produced by `MultiMergeConstructor.Construct`'s "merge cost delta" arithmetic. See "OnUseItem and the negative-quantity RemoveRange crash" below.

## Class shape and Quantity

<!-- verified: 0.2.6228.27061 @ 2026-05-30 -->

```csharp
public class Stackable : Item, IQuantity, ITradable, IEvaluable, IReferencable, IMergeable
{
    public List<GeneCollection> StackedGeneCollections;   // non-null EMPTY list on every stackable (Unity serializes a public List<T> field as empty, not null); only Plant fills it

    [Header("Stackable")]
    [SerializeField]
    [FormerlySerializedAs("Quantity")]
    private int quantity = 1;

    public int MaxQuantity = 10;

    public int Quantity
    {
        get => quantity;
        set
        {
            if (value == quantity) return;
            quantity = value;
            if (base.ParentSlot != null)
            {
                if (!GameManager.IsBatchMode && base.ParentSlot.Display != null)
                    base.ParentSlot.RefreshQuantity();
                ItemMerged();
            }
            if (GameManager.RunSimulation && Quantity <= 0)
                DestroyItemAtZero();
            if (NetworkManager.IsServer)
                base.NetworkUpdateFlags |= 1024;
        }
    }
}
```

The C# code only allocates it (`new List<GeneCollection>(MaxQuantity)`) on the genetics-bearing subclass `Plant` (`Assets.Scripts.Objects.Items.Plant`, decompile line 337701 and via split/merge `range` assignment at 338438/338470/338495/338526). **But the field is a public serialized `List<T>` on a MonoBehaviour, and Unity deserializes such a field as a non-null EMPTY list, never null.** So every stackable prefab/instance (ores, sheets, and the construction kits) carries `StackedGeneCollections != null` with `Count == 0`; only `Plant` ever puts entries in it. **Live-verified on the dedicated server (game 0.2.6228.27061): a stock `ItemKitPipe` instance reports `StackedGeneCollections != null`** (ScenarioRunner `merge-long-variant-num4` probe). This is decisive for the crash below: the `RemoveRange` line runs on a plain kit (the `!= null` guard passes), so a negative `quantity` throws -- no genetics or mod carry-over needed. (An earlier draft of this page inferred `null` from the decompiled C# alone, where only `Plant` allocates it; the live runtime read corrected that.)

`Quantity`'s setter auto-destroys the stack at `Quantity <= 0` (`DestroyItemAtZero`) when `RunSimulation`. A negative quantity passed to `OnUseItem` *increases* `Quantity` (see below), so it does not trip this guard.

## OnUseItem and the negative-quantity RemoveRange crash

<!-- verified: 0.2.6228.27061 @ 2026-05-30 -->

`Stackable.OnUseItem(float quantity, Thing onUseThing)` is the override invoked by the construction kits (`Constructor`, `MultiConstructor`, `MultiMergeConstructor` do NOT override `OnUseItem`, so `this.OnUseItem(...)` inside their `Construct` resolves here). Verbatim at decompile line 335433-335453:

```csharp
public override bool OnUseItem(float quantity, Thing onUseThing)
{
    if (!base.IsInstantiated)
    {
        return true;
    }
    quantity = Mathf.Min(quantity, Quantity);
    Quantity -= (int)quantity;
    if (GameManager.RunSimulation && StackedGeneCollections != null && StackedGeneCollections.Count - (int)quantity >= 0)
    {
        StackedGeneCollections.RemoveRange(StackedGeneCollections.Count - (int)quantity, (int)quantity);
    }
    if (Assets.Scripts.Networking.NetworkManager.IsClient)
    {
        UseStackableMessage useStackableMessage = new UseStackableMessage();
        useStackableMessage.QuantityUsed = (int)quantity;
        useStackableMessage.ReferenceId = base.ReferenceId;
        useStackableMessage.SendToServer();
    }
    return base.OnUseItem(quantity, onUseThing);
}
```

The `RemoveRange(index, count)` call passes:

- `index = StackedGeneCollections.Count - (int)quantity`
- `count = (int)quantity`

`List<T>.RemoveRange(int index, int count)` throws `ArgumentOutOfRangeException` with parameter name `"count"` and message "Non-negative number required." precisely when `count < 0`. So the crash fires when `(int)quantity < 0`.

The guard on the `if` is the only defense, and it is **incomplete**:

```csharp
StackedGeneCollections.Count - (int)quantity >= 0
```

This guards the *index* (`Count - quantity`) against being negative, but does NOT independently guard `count` (= `quantity`). When `quantity` is negative:

- `Mathf.Min(quantity, Quantity)` keeps it negative (e.g. `Min(-3, 50) == -3`).
- `Quantity -= (int)quantity` becomes `Quantity -= (-3)`, i.e. `Quantity` *increases* by 3 (the kit gains items, a second visible symptom).
- The guard `Count - (-3) = Count + 3 >= 0` is **always true**.
- `RemoveRange(Count + 3, -3)` is reached with `count == -3 < 0` -> `ArgumentOutOfRangeException ("count")`.

IL confirmation (disassembled from `Assembly-CSharp.dll`, method `Assets.Scripts.Objects.Items.Stackable::OnUseItem`, game version 0.2.6228.27061) shows the guard is exactly `(Count - quantity) < 0 -> skip`, with the `count` argument loaded as `ldarg.1; conv.i4` (i.e. `(int)quantity`) and no separate non-negativity check:

```
IL_003d: callvirt ...List`1<GeneCollection>::get_Count()
IL_0042: ldarg.1            // quantity
IL_0043: conv.i4
IL_0044: sub                // Count - (int)quantity
IL_0045: ldc.i4.0
IL_0046: blt.s IL_0063      // if (Count - (int)quantity) < 0 skip the RemoveRange block
...
IL_0054: callvirt ...get_Count()
IL_0059: ldarg.1
IL_005a: conv.i4
IL_005b: sub                // index = Count - (int)quantity
IL_005c: ldarg.1
IL_005d: conv.i4            // count = (int)quantity   <-- not guarded against < 0
IL_005e: callvirt ...List`1<GeneCollection>::RemoveRange(int32, int32)
```

This is a genuine base-game logic gap, not a decompiler artifact: the decompiled C# and the raw IL agree.

Two preconditions for the crash via this method:

1. `quantity < 0` reaching `OnUseItem`. The producer is `MultiMergeConstructor.Construct` (next section).
2. `GameManager.RunSimulation == true` (host / single-player, or the server processing the multi-construct). The other half, `StackedGeneCollections != null`, holds for ANY stackable -- Unity serializes the field as a non-null empty list (live-verified on a stock `ItemKitPipe`, 0.2.6228.27061). With `Count == 0` and `quantity == -2`, the guard `0 - (-2) = 2 >= 0` passes and `RemoveRange(2, -2)` throws on the negative `count`. The crash fires on a plain kit on the host; no mod is needed to populate the list.

## MultiMergeConstructor.Construct: where the negative quantity comes from

<!-- verified: 0.2.6228.27061 @ 2026-05-30 -->

`OnServer.UseMultiConstructor` (decompile line 40078) is the host-side entry for placing/merging a multi-kit piece. It forwards to `MultiConstructor.Construct` (arity-7), which `MultiMergeConstructor` overrides:

```csharp
public static void UseMultiConstructor(Thing thing, int activeHandSlotId, int inactiveHandSlotId,
    Vector3 targetLocation, Quaternion targetRotation, int optionIndex, bool authoringMode,
    ulong steamId, ICreativeSpawnable spawnPrefab)
{
    MultiConstructor multiConstructor = thing.Slots[activeHandSlotId].Get<MultiConstructor>();
    Item offhandItem = thing.Slots[inactiveHandSlotId].Get<Item>();
    if (!multiConstructor)
    {
        multiConstructor = Prefab.Find<MultiConstructor>(spawnPrefab.SpawnId);
        if ((object)multiConstructor == null) return;
    }
    multiConstructor.Construct(targetLocation.ToGridPosition(), targetRotation, optionIndex, offhandItem, authoringMode, steamId);
}
```

The merge path of `MultiMergeConstructor.Construct` (decompile line 288338-288458) ends with this cost-delta computation and the recursive `base.Construct` call (decompile line 288454-288457):

```csharp
OnServer.Destroy(thing);   // the existing piece being absorbed
int entryQuantity = Constructables[index].BuildStates[0].Tool.EntryQuantity;          // cost of the EXISTING piece's matching variant
int num4 = Constructables[num2].BuildStates[0].Tool.EntryQuantity - entryQuantity;    // cost of the RESULT variant - cost of existing
base.Construct(localPosition, quaternion2, num2, null, authoringMode, steamId, num4); // num4 passed as `quantity`
```

where:

- `num2` is the first `Constructables` index whose `IGridMergeable.GetConnectionType()` equals the resolved combined `connectionType` (the variant that results from merging the new ghost's open ends with the existing piece's open ends; decompile line 288423-288433).
- `index` is the first `Constructables` index whose `GetConnectionType()` equals `gridMergeable2.GetConnectionType()`, i.e. the existing piece's own connection type (decompile line 288445-288453).

So the quantity charged is:

```
quantity = num4 = Constructables[num2].BuildStates[0].Tool.EntryQuantity
                - Constructables[index].BuildStates[0].Tool.EntryQuantity
         = EntryQuantity(result variant) - EntryQuantity(existing variant)
```

This is `base.Construct(..., quantity)` = `MultiConstructor.Construct` arity-8 (decompile line 288278-288292):

```csharp
public virtual void Construct(..., int quantity)
{
    if (authoringMode || OnUseItem(quantity, null))   // -> Stackable.OnUseItem(quantity, null)
    {
        ...
        if (GameManager.RunSimulation)
            Constructor.SpawnConstruct(createStructureInstance);
    }
}
```

`num4` is an `int` and `Tool.EntryQuantity` is the per-build-state item cost. **`num4` goes negative whenever the merge result is a cheaper variant than the existing piece being absorbed** (`EntryQuantity(result) < EntryQuantity(existing)`). The negative `num4` flows straight into `Stackable.OnUseItem(quantity, null)` and triggers the `RemoveRange` crash described above (when `StackedGeneCollections != null` and `RunSimulation`).

When `num4 >= 0` (the normal upgrade direction, where the longer/combined run costs at least as much as the absorbed piece), `OnUseItem` behaves correctly and charges the delta.

Note the `authoringMode || OnUseItem(...)` short-circuit: in authoring (creative) mode `OnUseItem` is never called, so the crash cannot occur in authoring mode regardless of sign.

## Is this base-game-reachable or mod-specific?

<!-- verified: 0.2.6228.27061 @ 2026-05-30 -->

The arithmetic that produces the negative quantity is entirely in stock `MultiMergeConstructor.Construct`. Any caller of `OnServer.UseMultiConstructor` / `MultiConstructor.Construct` (or `MultiMergeConstructor.Construct`) that drives a merge whose resolved result variant is cheaper than the absorbed existing piece will pass a negative `num4`. The bug does not require a mod per se; it requires the specific combination:

- A `MultiMergeConstructor` kit (pipe / Super-Heavy cable / fuselage form).
- A merge that resolves to a `connectionType` whose matching `Constructables` entry has a *lower* `BuildStates[0].Tool.EntryQuantity` than the existing piece's matching entry.
- On the host / single-player (`RunSimulation`). The `StackedGeneCollections != null` guard is always satisfied (Unity empty-list default, live-verified on a stock kit), so it never gates the crash.

A batch/drag builder (ZoopMod-style) is the realistic trigger: in normal interactive play the placement cursor's `CanReplace` / `_IsCollision` validation blocks merging onto a long (`StraightAsymmetric`) variant, but ZoopMod reflectively calls `InventoryManager.UsePrimaryComplete`, which performs no such validation, so the merge onto a long variant executes and `num4 = EntryQuantity(result) - EntryQuantity(long) < 0` reaches `OnUseItem`. The "non-negative count" guard gap means the game has no defense once `num4 < 0` reaches `OnUseItem`. The `StackedGeneCollections != null` guard is NOT a barrier: Unity gives every stackable a non-null empty list (live-verified on a stock `ItemKitPipe`, 0.2.6228.27061), so the negative `num4` reaches `RemoveRange` and throws on the host -- no mod is needed to populate the list. See `MultiMergeConstructor.md` "Confirmed bypass: ZoopMod via InventoryManager.UsePrimaryComplete" for the full placement-path analysis.

## Related RemoveRange sites (NOT the crash site)

<!-- verified: 0.2.6228.27061 @ 2026-05-30 -->

Two other `StackedGeneCollections.RemoveRange` calls exist; neither is in the reported crash stack (`Stackable.OnUseItem`), but both share the same shape and the second has the same missing-`count`-guard pattern:

- `UseStackableMessage.Process(long hostId)` (decompile line 259214-259223): the server-side handler for the client's `UseStackableMessage` (sent from `Stackable.OnUseItem` when `NetworkManager.IsClient`). It does `QuantityUsed = Mathf.Min(QuantityUsed, stackable.Quantity); if (stackable.StackedGeneCollections != null) stackable.StackedGeneCollections.RemoveRange(stackable.StackedGeneCollections.Count - QuantityUsed, QuantityUsed); stackable.Quantity -= QuantityUsed;`. Here there is NO `>= 0` guard at all, only the null check, so a negative `QuantityUsed` arriving over the wire would throw the same `ArgumentOutOfRangeException ("count")` on the server. (A negative-quantity `OnUseItem` on a client both crashes locally AND sends a negative `QuantityUsed` to the server.)
- `Plant.RemoveQuantity(int quantityToRemove)` (decompile line 338383-338395): `int num = base.RemoveQuantity(quantityToRemove); if (num > 0) StackedGeneCollections.RemoveRange(StackedGeneCollections.Count - num, num);`. Guarded by `num > 0`, so it cannot pass a negative `count`. Not the crash site.

## Verification history

<!-- verified: 0.2.6228.27061 @ 2026-05-30 -->

- 2026-05-30: page created while diagnosing an `ArgumentOutOfRangeException ("count")` from `List.RemoveRange` in `Stackable.OnUseItem` on the multi-construct code path (`OnServer.UseMultiConstructor` -> `MultiMergeConstructor.Construct` -> `MultiConstructor.Construct` -> `Stackable.OnUseItem`). All bodies lifted verbatim from `.work/decomp/0.2.6228.27061/Assembly-CSharp.decompiled.cs` (`Stackable` class header 335033, `Stackable.OnUseItem` 335433-335453, `OnServer.UseMultiConstructor` 40078-40091, `MultiConstructor.Construct` arity-8 288278-288292, `MultiMergeConstructor.Construct` merge tail 288338-288458, `UseStackableMessage.Process` 259214-259223, `Plant.RemoveQuantity` 338383-338395). The `OnUseItem` guard gap (guards `index` via `Count - quantity >= 0` but not `count = quantity < 0`) confirmed against raw IL (`ilspycmd -t Assets.Scripts.Objects.Stackable -il`, method body IL_0042-IL_005e). Negative-quantity producer identified as `num4 = EntryQuantity(result) - EntryQuantity(existing)` in `MultiMergeConstructor.Construct`. Additive page; no existing page contradicted. Existing `MultiConstructor.md` and `MultiMergeConstructor.md` cover the `Construct` bodies up to the `OnUseItem(quantity, null)` call but not the `OnUseItem` body itself, so this page fills that gap and cross-links.
- 2026-05-31: **live-verified on the dedicated server (game 0.2.6228.27061)** via a `ScenarioRunner` probe (`merge-long-variant-num4`), and a **fresh-validator correction**. The page previously stated a stock kit's `StackedGeneCollections` is `null` (inferred from the decompiled C#, where only `Plant` calls `new List<>`), treated the non-null requirement as a likely mod interaction, and said the negative `num4` would silently inflate `Quantity` rather than throw on this build. The runtime read overturns that: a public serialized `List<T>` field on a MonoBehaviour is deserialized by Unity as a non-null EMPTY list (never null), so a stock `ItemKitPipe` reports `StackedGeneCollections != null`. Therefore the negative-`num4` `RemoveRange(2, -2)` **throws on a plain kit on the host with no mod needed** -- the crash is not mod- or version-gated on this build. Also confirmed live: `num4 = EntryQuantity(StructurePipeStraight = 1) - EntryQuantity(StructurePipeStraight3 = 3) = -2` (and -3 / -7 for the 5 / 10 variants) on all 5 merge-capable kits (pipe, liquid pipe, insulated pipe, insulated-liquid pipe, super-heavy cable); chute kits carry the long entries but their base is not `IGridMergeable`, so the merge branch is never reached. And confirmed `NetworkPuristPlus`, when active, strips the `StraightAsymmetric` entries from those kits (`ItemKitPipe` 11 -> 8 options), removing the merge-onto-long precondition (it is protective, not causal). Corrected: the class-shape comment, the `StackedGeneCollections` paragraph, both crash preconditions, the base-game-reachable section, and the open questions (now resolved below).

## Resolved (live-verified on the dedicated server, game 0.2.6228.27061, 2026-05-31)

ScenarioRunner probe `merge-long-variant-num4` reproduced the game's own `index`/`num2` resolution over the live kit `Constructables`, reading each entry's runtime `GetConnectionType()` + `BuildStates[0].Tool.EntryQuantity`:

- **A stock kit DOES have a non-null `StackedGeneCollections`** -- a non-null empty list (Unity serializes the public `List<GeneCollection>` field as empty, never null). A stock `ItemKitPipe` reported `StackedGeneCollections != null`. So the `RemoveRange` line runs and the negative `quantity` throws on a plain kit; no mod or genetics carry-over is required. (The prior "open question" assumed `null` from the decompiled C#; the runtime read overturned it. The earlier "silent `Quantity` inflation instead of a throw" reading was wrong for this build.)
- **The merge that yields the negative delta** is "merge a single straight onto an existing long (`StraightAsymmetric`) variant." Live kit costs: `Constructables[0]` (the 1-cell `Straight` base) `EntryQuantity = 1`; the first `StraightAsymmetric` entry (`StructurePipeStraight3`) `EntryQuantity = 3`. A collinear merge resolves `num2` -> idx 0 (cost 1) and `index` -> idx 8 (cost 3), so `num4 = 1 - 3 = -2` (and -3 / -7 for the 5 / 10 variants). Confirmed on 5 merge-capable kits (pipe, liquid pipe, insulated pipe, insulated-liquid pipe, super-heavy cable). Chute kits carry the long entries but their base is not `IGridMergeable`, so the merge branch is never reached (placed as-is). See `MultiMergeConstructor.md` "Confirmed bypass" and "Build costs ...".
