---
title: Structure
type: GameClasses
created_in: 0.2.6228.27061
verified_in: 0.2.6403.27689
verified_at: 2026-07-15
sources:
  - Mods/SprayPaintPlus/RESEARCH.md:177-179
  - Mods/SprayPaintPlus/SprayPaintPlus/NetworkPainterPatch.cs:320-328
  - $(StationeersPath)\rocketstation_Data\Managed\Assembly-CSharp.dll :: Assets.Scripts.Objects.Structure
  - $(StationeersPath)\rocketstation_Data\Managed\Assembly-CSharp.dll :: Assets.Scripts.Objects.Ladder / LadderEnd / LadderPlatform / SmallGrid
  - .work/decomp/0.2.6403.27689/Assembly-CSharp.decompiled.cs :: lines 314994-315262 (Structure.AttackWith deconstruct branch), 315366-315369 (CanDeconstruct), 315390-315491 (StructureDestroyed), 316015-316234 (ToolBasic / ToolUse / SpawnItem), 39630-39654 / 39866-39876 / 39914-39926 (OnServer.AttackWith / Create / Destroy), 277409-277420 (AttackWithMessage.Process), 315307-315315 (UpgradeStructureServer)
related:
  - ./Thing.md
  - ./Wall.md
  - ./OnServer.md
  - ./Cable.md
  - ../GameSystems/DamageState.md
  - ../GameSystems/Explosions.md
  - ../GameSystems/StructureRegistration.md
tags: [prefab, damage]
---

# Structure

Vanilla game class for player-built, fixed-position game objects. Subclass of `Thing`. Covers walls, frames, pipes, cables, and devices.

## NotImplementedException on batched structures
<!-- verified: 0.2.6228.27061 @ 2026-04-20 -->

Source: F0029e.

Some structures use `structureRenderMode != Standard` and share a combined mesh. `SetCustomColor` throws `NotImplementedException` on these. `PaintSafe` catches the exception per-item so one unpaintable structure does not abort the rest of the network.

### PaintSafe catch comment (F0322)
<!-- verified: 0.2.6228.27061 @ 2026-04-20 -->

Source comment from `NetworkPainterPatch.cs:320-328`:

```
/// <summary>
/// Individual SetCustomColor calls can throw. Most notably,
/// Structure.SetCustomColor throws NotImplementedException on any
/// structure whose structureRenderMode != Standard (batched-render
/// structures share a combined mesh and can't be recolored per
/// instance). A destroyed-mid-paint item can also trip a null deref.
/// Without the catch, one unpaintable or stale item would abort
/// painting the rest of the network.
/// </summary>
```

## IsBroken property
<!-- verified: 0.2.6228.27061 @ 2026-04-21 -->

Source: `$(StationeersPath)\rocketstation_Data\Managed\Assembly-CSharp.dll :: Assets.Scripts.Objects.Structure` and `Assets.Scripts.Objects.Thing`.

`Structure` has a public `IsBroken` property, overriding the base on `Thing`.

Base (`Thing.IsBroken`):

```csharp
public virtual bool IsBroken
{
    get
    {
        if (DamageState != null)
            return DamageState.Total >= DamageState.MaxDamage;
        return false;
    }
}
```

Override (`Structure.IsBroken`):

```csharp
public override bool IsBroken
{
    get
    {
        if (!base.IsBroken)
            return CurrentBuildStateIndex < 0;
        return true;
    }
}
```

A `Structure` is `IsBroken` when either:

- Its `DamageState.Total >= DamageState.MaxDamage` (fully damage-destroyed), OR
- Its `CurrentBuildStateIndex < 0` (deconstructed past the first build stage, which is how the game models a wreckage / half-torn-down state).

Read-only property; no setter. Use it verbatim as `thing.IsBroken` to detect "is this structure currently wreckage / destroyed." For detecting structures that have broken build states in their prefab definition (not the runtime state), use `Structure.HasBrokenBuildStates` (getter tests `BrokenBuildStates?.Count > 0`).

## Build-state model and the destruction path
<!-- verified: 0.2.6228.27061 @ 2026-06-19 -->

`Structure : Thing`. Subclass tree of interest: `Structure -> LargeStructure -> Wall` / `Frame` (and `Geyser`, `StructureFuselage`, `LaunchMount`); `Structure -> SmallGrid` (attached devices / small wall-mounted machines), `Cladding`, `Stairs`, `RoverFrame`. The player-facing "structure frame" is `Frame : LargeStructure`, a trivial subclass; there is no class literally named `StructureFrame`. (`MultiConstructor` is the *kit item* you build frames from, not the structure.)

Ladders sit on the `SmallGrid` branch, not `LargeStructure`: `Ladder : SmallGrid, ISmartRotatable` and `LadderEnd : Ladder`. The contrast is `LadderPlatform : Floor : Wall : LargeStructure` -- a ladder *platform* is a `LargeStructure`, a plain ladder is not. Code that branches on `is LargeStructure` (a grid-walk / flood-fill, for example) therefore catches ladder platforms but skips ladders and ladder ends. (`Stairs : Structure` directly, also not `LargeStructure`.)

Construction is genuinely stage-by-stage. A `Structure` carries `List<BuildState> BuildStates`, `List<BrokenBuildState> BrokenBuildStates`, `int CurrentBuildStateIndex` (setter clamps, fires `OnBuildState`, sets `NetworkUpdateFlags |= 64`), `BuildState CurrentBuildState`, `bool IsStructureCompleted`, `bool HasBrokenMesh => BrokenBuildStates?.Count > 0`, plus grid registration (`LocalGrid`, `BlockingGrids`). `Structure.AttackWith` with the matching tool moves `CurrentBuildStateIndex` up (construct) or down (deconstruct) one step; deconstructing build state 0 is what removes the object (via `BuildStates[0].Tool.Deconstruct(eventInstance)` then `OnServer.Destroy`). A `Structure` can also be removed in one shot by `Thing.Delete` / `OnServer.Destroy` (the engine path), bypassing the build-stage walk and the tool/ingot requirements.

### When DamageState maxes out
<!-- verified: 0.2.6228.27061 @ 2026-04-29 -->

When `DamageState.Total >= MaxDamage`, `ThingDamageState.OnDamageUpdated()` schedules `ThingDamageState.Destroy()`:

```csharp
private async UniTask Destroy()
{
    await UniTask.SwitchToMainThread();
    await UniTask.DelayFrame(1);
    Structure asStructure = Parent.AsStructure;
    if ((bool)asStructure)
    {
        if (!_isDestroyed && asStructure.IsBroken && asStructure.HasBrokenMesh)
        {
            asStructure.UpdateBuildStateAndVisualizer(asStructure.GetBrokenState(), _particlesOnDestroy);
            asStructure.OnStructureBroken();
            HealAll();                       // reset HP so the wreck persists
            _isDestroyed = true;
            return;                          // NOT removed -- left as a wreck/damaged shell
        }
        EffectManager.CreateDeconstructionEffect(asStructure, _particlesOnDestroy);
        if ((bool)Parent) Parent.OnDamageDestroyed();
    }
    if ((bool)Parent) Parent.OnDamageDestroyed();
}
```

**Pitfall**: a structure that has a broken-mesh build state (most walls and frames do; check `HasBrokenMesh` / `HasBrokenBuildStates`) does NOT despawn when damage maxes out -- it converts to its wreck visual and stays alive in an `IsBroken` state with HP reset. A mod that wants *guaranteed* removal should call `OnServer.Destroy(thing)` or `Thing.Delete(...)` directly rather than cranking damage. See `./Thing.md` and `./OnServer.md`.

### OnDamageDestroyed and StructureDestroyed
<!-- verified: 0.2.6228.27061 @ 2026-04-29 -->

`Structure.OnDamageDestroyed()` is the "fully demolish, drop wreckage, despawn" path -- it skips the broken-mesh conversion:

```csharp
public override void OnDamageDestroyed()
{
    base.OnDamageDestroyed();                       // Thing.OnDamageDestroyed: IsBurning = false
    if (GameManager.RunSimulation && !base.Indestructable)
    {
        ConstructionEventInstance eventInstance = new ConstructionEventInstance { Parent = this, Position = ..., Rotation = ..., SteamId = OwnerClientId, OtherHandSlot = null };
        StructureDestroyed(eventInstance, destroyedFromDamage: true);
        if (this is IWreckage wreckage) wreckage.SpawnWreckage();
        OnServer.Destroy(this);
    }
}
```

`StructureDestroyed(eventInstance, destroyedFromDamage)` unregisters from the grid / atmospheres, handles `WorldParticleEffect` grid bookkeeping; if `destroyedFromDamage && BrokenBuildStates.Count > 0` it marks the broken build state, otherwise it runs `BuildStates[0].Tool.Deconstruct(eventInstance)` and moves slot occupants to the world; then for each `AttachedDevices` entry it deconstructs through all build states and `OnServer.Destroy`s it. `IWreckage.SpawnWreckage()` is what drops the broken-frame / wreckage debris item; not every structure implements `IWreckage` (`Frame` and walls generally do). `OnServer.Destroy(this)` is the actual removal. `Indestructable` is checked here (the damage path); `Thing.Delete` / `OnServer.Destroy` themselves do not check it. All server-authoritative; destruction replicates via the normal `DestroyEvent` / construction-event networking.

### Tool deconstruct branch of AttackWith
<!-- verified: 0.2.6403.27689 @ 2026-07-14 -->

Entry: `Structure.AttackWith(Attack attack, bool doAction)` (starts at 0.2.6403.27689 decompile line 314994). The deconstruct branch for the current build state (315205-315262), verbatim:

```csharp
else if (CurrentBuildStateIndex >= 0 && CurrentBuildStateIndex < BuildStates.Count)
{
    BuildState buildState2 = BuildStates[CurrentBuildStateIndex];
    DelayedActionInstance delayedActionInstance3 = new DelayedActionInstance
    {
        Duration = buildState2.Tool.ExitTime / value,
        ActionMessage = ActionStrings.Deconstruct
    };
    Tool tool3 = sourceItem as Tool;
    if ((bool)buildState2.Tool.ToolExit && buildState2.Tool.IsToolExit(item))
    {
        BuildState buildState3 = BuildStates[CurrentBuildStateIndex];
        if ((bool)tool3 && !tool3.IsOperable)
        {
            return InoperableToolResult(delayedActionInstance3, tool3, doAction);
        }
        CanConstructInfo canConstructInfo = CanDeconstruct();
        if (tool3 != null && !canConstructInfo.CanConstruct)
        {
            return delayedActionInstance3.Fail(canConstructInfo.ErrorMessage);
        }
        if (!doAction)
        {
            return delayedActionInstance3;
        }
        if (CurrentBuildStateIndex > 0 && GameManager.RunSimulation)
        {
            ConstructionEventInstance eventInstance3 = new ConstructionEventInstance
            {
                Parent = this,
                Position = attack.Position,
                Rotation = ThingTransform.rotation,
                SteamId = base.OwnerClientId,
                OtherHandSlot = attack.OtherHand
            };
            buildState3.Tool.Deconstruct(eventInstance3);
        }
        if ((bool)tool3 && !tool3.OnUseItem(buildState2.Tool.ExitQuantity, this))
        {
            return null;
        }
        CurrentBuildStateIndex--;
        if (CurrentBuildStateIndex < 0)
        {
            ConstructionEventInstance eventInstance4 = new ConstructionEventInstance
            {
                Parent = this,
                Position = attack.Position,
                Rotation = ThingTransform.rotation,
                SteamId = base.OwnerClientId,
                OtherHandSlot = attack.OtherHand
            };
            StructureDestroyed(eventInstance4);
            OnServer.Destroy(this);
            return delayedActionInstance3;
        }
        UpdateStateVisualizer();
    }
}
```

`CanDeconstruct()` gates the branch. Base `Structure.CanDeconstruct` (315366-315369) returns `CanConstructInfo.ValidPlacement`; `Cable.CanDeconstruct` (392393-392405) refuses while `AttachedDevices` is non-empty. A structure with a single build state (index 0), such as a cable, goes straight to `StructureDestroyed(eventInstance4); OnServer.Destroy(this);`.

The kit-refund portion of `StructureDestroyed` (excerpt of 315390-315491, key lines; the refund does NOT run when `destroyedFromDamage`):

```csharp
else if ((bool)BuildStates[0].Tool.ToolEntry && !destroyedFromDamage)
{
    BuildStates[0].Tool.Deconstruct(eventInstance);
    if (Slots.Count > 0)
    {
        foreach (Slot slot in Slots)
        {
            if ((bool)slot.Occupant && GameManager.RunSimulation)
            {
                OnServer.MoveToWorld(slot.Occupant);
            }
        }
    }
}
```

Related: `Structure.UpgradeStructureServer` (315307-315315) is the destroy-then-`SpawnConstruct` pattern used by build-state upgrades; the same shape a refuse-and-replace mod would use if it ever swapped a piece instead of refunding.

### Which kit item: ToolBasic / ToolUse serialized prefab data
<!-- verified: 0.2.6403.27689 @ 2026-07-14 -->

The kit identity is serialized prefab data: `BuildState.Tool` is a `ToolUse : ToolBasic` and the refunded item is its `ToolEntry` (plus optional `ToolEntry2`), quantity `EntryQuantity` (`EntryQuantity2`). `ToolBasic` fields (316015-316027):

```csharp
public class ToolBasic
{
    [Header("Construction")]
    public Item ToolEntry;

    public Item ToolEntry2;

    [Range(0f, 60f)]
    public float EntryTime = 2f;

    public int EntryQuantity;

    public int EntryQuantity2;
```

`ToolUse` adds the deconstruction side (316130-316143):

```csharp
[Serializable]
public class ToolUse : ToolBasic
{
    [Tooltip("If true the entry tool will always be shown to player, regardless of if they have a tool or not")]
    public ToolUseType ToolUseType;

    [Header("Deconstruction")]
    public Item ToolExit;

    [Range(0f, 60f)]
    public float ExitTime = 2f;

    public int ExitQuantity;
```

For a cable, `BuildStates[0].Tool.ToolEntry` is the cable coil item (`ItemCableCoil` / `ItemCableCoilHeavy` / `ItemCableCoilSuperHeavy`, per the electronics.xml recipe names on [Cable](./Cable.md)) and `EntryQuantity` is 1 for the 1-cell straight (per the resources.assets extraction table on [MultiMergeConstructor](./MultiMergeConstructor.md); long variants 3/4/7-8). A mod refund should read `__instance.BuildStates[0].Tool.ToolEntry` and `EntryQuantity` from the live instance rather than hardcoding prefab names; that automatically matches variant cost (junctions cost 2-3).

### ToolUse.SpawnItem: the canonical drop-spawn pattern
<!-- verified: 0.2.6403.27689 @ 2026-07-14 -->

`ToolUse.Deconstruct` + `SpawnItem` (316169-316234), verbatim; this is BOTH the "which item" and the "server-side spawn a drop" pattern (try to stack into the other hand first, else `OnServer.Create<Item>` at position + `SetQuantity`, with paint-color carry-over for construction kits):

```csharp
private void SpawnItem(ConstructionEventInstance eventInstance, Item tool)
{
    if (tool == null)
    {
        return;
    }
    int num = ((tool == ToolEntry) ? EntryQuantity : EntryQuantity2);
    if (tool is Tool || num == 0)
    {
        return;
    }
    Stackable stackable = tool as Stackable;
    Slot slot = null;
    if (eventInstance.OtherHandSlot != null)
    {
        if (!eventInstance.OtherHandSlot.Occupant)
        {
            slot = eventInstance.OtherHandSlot;
        }
        else if ((bool)stackable)
        {
            Stackable stackable2 = eventInstance.OtherHandSlot.Occupant as Stackable;
            if ((bool)stackable2 && stackable2.CanStack(stackable))
            {
                int num2 = Mathf.Min(stackable2.MaxQuantity - stackable2.Quantity, num);
                if (num2 > 0)
                {
                    stackable2.AddQuantity(num2);
                    num -= num2;
                }
            }
        }
    }
    if (num <= 0)
    {
        return;
    }
    Item item = OnServer.Create<Item>(tool, eventInstance.Position, eventInstance.Rotation);
    if ((bool)item)
    {
        if (slot != null)
        {
            OnServer.MoveToSlot(item, slot);
        }
        Stackable stackable3 = item as Stackable;
        if ((bool)stackable3)
        {
            stackable3.SetQuantity(num);
        }
        Consumable consumable = item as Consumable;
        if ((bool)consumable)
        {
            consumable.Quantity = num;
        }
        if (item is IConstructionKit && (eventInstance.Parent is DraggableThing || eventInstance.Parent is Structure { CurrentBuildStateIndex: <=0 }) && (object)eventInstance.Parent.PaintableMaterial != null && eventInstance.Parent.CustomColor.Index != item.CustomColor.Index)
        {
            OnServer.SetCustomColor(item, eventInstance.Parent.CustomColor.Index);
        }
    }
}

public void Deconstruct(ConstructionEventInstance eventInstance)
{
    SpawnItem(eventInstance, ToolEntry);
    SpawnItem(eventInstance, ToolEntry2);
}
```

`OnServer.Create<T>` (39866-39876) wraps `Thing.Create` (registration chain on [StructureRegistration](../GameSystems/StructureRegistration.md)):

```csharp
public static T Create<T>(Thing prefab, Vector3 position, Quaternion rotation) where T : Thing
{
    T val = Thing.Create<T>(prefab, position, rotation, 0L);
    if ((object)val != null)
    {
        T result = val;
        _ = val;
        return result;
    }
    throw new NullReferenceException();
}
```

`OnServer.Destroy` (39914-39926), verbatim. Note the SOFT non-simulation guard: called on a client it logs an error but still destroys the local GameObject:

```csharp
public static void Destroy(Thing thing)
{
    if (!GameManager.RunSimulation && GameManager.GameState == GameState.Running)
    {
        string text = (thing ? thing.DisplayName : "unknown");
        ConsoleWindow.PrintError("OnServer.Destroy called on client for " + text);
    }
    if ((bool)thing && (bool)thing.GameObject)
    {
        thing.GameObject.DestroyGameObject();
        thing.BeingDestroyed = true;
    }
}
```

### Construction completion: the tool stroke has no network side effects
<!-- verified: 0.2.6403.27689 @ 2026-07-15 -->

The construct branch of `Structure.AttackWith` completes a build state with exactly two statements (0.2.6403.27689 decompile lines 315202-315203): `CurrentBuildStateIndex++;` then `UpdateStateVisualizer();`. No cable-network, grid, or device-list operation runs on completion; the structure was already registered on the grid and in its network's `DeviceList` since placement (see [StructureRegistration](../GameSystems/StructureRegistration.md)). What changes on completion is only what reads the index afterwards.

`IsStructureCompleted` (313965-313978) is `CurrentBuildStateIndex == BuildStates.Count - 1`, with one carve-out: a structure whose current build state has `CanManufacture` set also reports completed at that middle state.

The base `Device.GetUsedPower(CableNetwork)` (371510-371521) returns `-1` when the queried network is not one of the device's own power networks, and `0` unless `OnOff && IsStructureCompleted`. Consequence: an under-construction device sits on the network with zero demand, and its demand appears the moment the completing tool stroke lands, with no other side effect.

Programmatic spawns land INCOMPLETE for multi-state structures: `Thing.Create` / `OnServer.Create` instances arrive at `CurrentBuildStateIndex = 0` (live-observed on `StructureConsole`, 2 build states, spawned via `OnServer.Create<Structure>` on the dedicated server at 0.2.6403.27689: 24 electricity ticks on a live network with row demand 0, then demand appeared on the tick after `CurrentBuildStateIndex` was advanced to final through the real completion write). A single-state structure (a cable) spawns complete because state 0 IS its final state.

Live verification 2026-07-15 via a ScenarioRunner per-tick trace on the dedicated server (scenario pgp-fresh-device-trace: RTG feeding a small transformer feeding consumers; logged per electricity tick: snapshot demand rows, transformer grants, net verdicts, device `IsStructureCompleted` / `OnOff` / `Powered`): a spawn-complete device's demand and its funding both appear on the same first tick that snapshots it, and a build-state completion likewise lands demand and funding on the same tick. The registration and completion boundaries are tick-coherent in vanilla.

### Multiplayer flow: the destructive interaction runs host-side
<!-- verified: 0.2.6403.27689 @ 2026-07-14 -->

`OnServer.AttackWith` (39630-39654) executes `thing.AttackWith(attack, runSimulation)` locally with `doAction = GameManager.RunSimulation`; on a remote client that is a preview-only call, and the client forwards an `AttackWithMessage` to the server. `AttackWithMessage.Process` (277409-277420) reconstructs the `Attack` and calls `thing2.AttackWith(attack)` with the default `doAction = true`. So the kit spawn and destroy above only ever execute where `RunSimulation` is true. `AttackWithMessage` is MessageFactory index 63 (191267; see [GameMessageFactory](../Protocols/GameMessageFactory.md)).

```csharp
public static void AttackWith(Thing attackParent, byte activeHandSlotId, byte offHandSlotId, long targetId, Vector3 attackPosition, float completedRatio, bool isDestroy, bool isCopy)
{
    bool runSimulation = GameManager.RunSimulation;
    Slot activeHand = attackParent.Slots[activeHandSlotId];
    Thing thing = Thing.Find(targetId);
    if ((bool)thing)
    {
        Slot otherHand = attackParent.Slots[offHandSlotId];
        Attack attack = new Attack(activeHand, otherHand, attackPosition, thing, completedRatio, null, isDestroy, isCopy);
        if (thing.AttackWith(attack, runSimulation) != null && Assets.Scripts.Networking.NetworkManager.IsClient)
        {
            NetworkClient.SendToServer(new AttackWithMessage { ... });   // fields at 39641-39651
        }
    }
}
```

What syncs automatically once a mod acts host-side: item spawns replicate via `Thing.NewToSend` (added inside `Thing.Create`; clients construct in `Thing.DeserializeNew` 322270); destruction replicates via `Thing.DestroyToSend` (`Thing.OnDestroy` 320984-320999 adds `DestroyEvent.Create(this)` when `IsServer && HasClients`); cable-network splits replicate via `RebuildCableNetworkEvent` (271214-271217); quantity on a spawned stack rides the normal delta state (the `Stackable.Quantity` setter sets `NetworkUpdateFlags |= 1024`).

Therefore a mod's refuse-and-drop needs exactly: run on `GameManager.RunSimulation` only; `OnServer.Create<Item>(BuildStates[0].Tool.ToolEntry, position, rotation)`; `SetQuantity(EntryQuantity)`; `OnServer.Destroy(structure)`. Nothing manual to send. Mimic `ToolUse.SpawnItem`'s order (spawn the refund, then destroy) and prefer `Item.SetQuantity` (as used at 316216) over the raw `Quantity` setter.

## Verification history

- 2026-04-20: page created from the Research migration; verbatim content lifted from F0029e, F0322. No conflicts.
- 2026-04-21: added "IsBroken property" section from direct decompile of `Assets.Scripts.Objects.Structure` and `Assets.Scripts.Objects.Thing`. Additive only; no existing content changed. Game version 0.2.6228.27061.
- 2026-04-29: added "Build-state model and the destruction path" section (with "When DamageState maxes out" and "OnDamageDestroyed and StructureDestroyed" subsections) from a research pass on the explosion / structure-destruction system. Additive; no existing content changed. Sources: `Assets.Scripts.Objects.Structure` (`BuildStates`, `BrokenBuildStates`, `CurrentBuildStateIndex`, `HasBrokenMesh`, `AttackWith`, `OnDamageDestroyed`, `StructureDestroyed`, `OnDestroy`), `ThingDamageState.OnDamageUpdated` / `Destroy`, `IWreckage` (all in `Assembly-CSharp`, game version 0.2.6228.27061).
- 2026-06-19: extended the subclass tree with the ladder family (`Ladder : SmallGrid, ISmartRotatable`, `LadderEnd : Ladder`, `LadderPlatform : Floor : Wall : LargeStructure`) and a `Stairs : Structure` note. Additive; no existing content changed. Sources: `Assets.Scripts.Objects.Ladder` / `LadderEnd` / `LadderPlatform`, `SmallGrid`, `LargeStructure`, `Wall`, `Floor`, `Stairs` (all in `Assembly-CSharp`, game version 0.2.6228.27061).
- 2026-07-14: added four subsections to "Build-state model and the destruction path" from the mixed-tier cable network guard research pass against the 0.2.6403.27689 decompile: the tool-deconstruct branch of `Structure.AttackWith` verbatim (315205-315262, with the `CanDeconstruct` gate at 315366-315369 and `Cable.CanDeconstruct`'s `AttachedDevices` refusal at 392393-392405, and the `StructureDestroyed` kit-refund excerpt gated on `!destroyedFromDamage`), the `ToolBasic` / `ToolUse` field blocks (316015-316027 / 316130-316143) with the cable-coil kit-identity note (read `BuildStates[0].Tool.ToolEntry` x `EntryQuantity` from the live instance), `ToolUse.Deconstruct` + `SpawnItem` verbatim (316169-316234) as the canonical drop-spawn pattern plus `OnServer.Create<T>` (39866-39876) and `OnServer.Destroy` (39914-39926) with its soft non-simulation guard (logs an error on a client, still destroys locally), and the multiplayer flow (`OnServer.AttackWith` 39630-39654 runs with `doAction = RunSimulation`; `AttackWithMessage.Process` 277409-277420 re-runs host-side with `doAction = true`; `AttackWithMessage` is MessageFactory index 63 at 191267; spawns replicate via `NewToSend`, destroys via `DestroyToSend` per `Thing.OnDestroy` 320984-320999, stack quantity via `NetworkUpdateFlags |= 1024`). Also recorded `Structure.UpgradeStructureServer` (315307-315315) as the destroy-then-`SpawnConstruct` upgrade pattern. Additive; the existing prose summary of the deconstruct path ("deconstructing build state 0 removes the object via `BuildStates[0].Tool.Deconstruct` then `OnServer.Destroy`") is refined, not contradicted, by the exact branch mechanics.
- 2026-07-15: added the "Construction completion: the tool stroke has no network side effects" subsection from the fresh-device power investigation against the 0.2.6403.27689 decompile plus a live dedicated-server trace: the construct branch's completion write is `CurrentBuildStateIndex++` + `UpdateStateVisualizer()` only (315202-315203); `IsStructureCompleted` is last-index with the `CanManufacture` carve-out (313965-313978); base `Device.GetUsedPower` returns -1 for a foreign network and gates demand on `OnOff && IsStructureCompleted` (371510-371521); `Thing.Create` / `OnServer.Create` spawns of multi-state structures arrive at `CurrentBuildStateIndex = 0` (live-observed on StructureConsole). Live verification via a ScenarioRunner per-tick trace (scenario pgp-fresh-device-trace on the dedicated server): demand and funding land on the same electricity tick at both the spawn-complete and the build-completion boundaries. Additive; no existing claim contradicted.

## Open questions

None.
