---
title: BlueprintModPlacement
type: Patterns
created_in: 0.2.6403.27689
verified_in: 0.2.6403.27689
verified_at: 2026-07-14
sources:
  - Steam Workshop item 3672138641 (BlueprintMod v1.6.3), BlueprintMod.dll
  - .work/decomp/0.2.6403.27689/BlueprintMod.decompiled.cs
  - .work/decomp/0.2.6403.27689/Assembly-CSharp.decompiled.cs
related:
  - ../GameClasses/Cable.md
tags: [prefab, harmony]
---

# BlueprintModPlacement

BlueprintMod ("Copy, paste, save and load groups of structures as blueprints", Steam Workshop item 3672138641, v1.6.3, author JXSN (JacksonTheMaster)) is a third-party StationeersLaunchPad plugin, shipped as the single assembly BlueprintMod.dll, that prints saved groups of structures into a running world. This page documents its placement internals because that placement path performs no occupancy checks at all: on 2026-07-13 it printed normal-tier power cables into an existing super-heavy cable mainline in a player world, merging mixed cable tiers in a state vanilla construction can never produce, and bypassing both the vanilla cursor-time checks and a `Cable.CanConstruct` Harmony postfix guard. The facts below feed the mixed-tier prevention design for PowerGridPlus.

Mod identity and analysis basis:

- Exactly one assembly, `BlueprintMod.dll` (183,296 bytes, file date 2026-03-30), at the Workshop item root. The rest of the item is asset bundles (`StandaloneWindows/blueprintmod.assets`, `.scenes`), `GameData/*.xml` recipe additions, and `About/`.
- Version 1.6.3 per About.xml and the `BlueprintMod.ModVersion` constant (line 4699).
- StationeersLaunchPad plugin: About.xml tag `StationeersLaunchPad`, plus an `About/stationeersmods` marker file. Entry point is the StationeersMods-style `BlueprintMod.OnLoaded(List<GameObject> prefabs, ConfigFile config)` MonoBehaviour method (line 4721). Uses LaunchPadBooster (`new Mod("BlueprintMod", "1.6.3")`, line 4707; `MOD.SetVersionCheck` requires major.minor match, lines 4763-4768).
- Decompile analyzed: `.work/decomp/0.2.6403.27689/BlueprintMod.decompiled.cs` (9,852 lines, ilspycmd 10.0.0.8330). All line numbers on this page refer to that file unless marked `[Assembly-CSharp]`, which refers to `.work/decomp/0.2.6403.27689/Assembly-CSharp.decompiled.cs` (game version 0.2.6403.27689).
- Decompile artifact note: ilspycmd 10.0.0.8330 appended its own update notice as the last two lines of the output file (lines 9851-9852: "You are not using the latest version of the tool, please update."). Code content ends at line 9850. Harmless but worth knowing when the file tail looks odd.
- Player-facing surface: chat console commands (`pos1`, `pos2`, `bpcopy`, `bppaste`, `bpsave`, `bpload`, `bplist`, `bpdelete`, `bpundo`, `bpguidelines`, `copyguidelines`, `bpworkshop`, `bpworkshoplist`, `bpworkshopload`, `bppreview`; registered lines 4812-4826) plus two craftable motherboards with custom UI: MotherboardDBCU (copy unit) and MotherboardDBPU (paste unit). Both UI paths funnel into the same BlueprintCommands code.

## Placement call chain
<!-- verified: 0.2.6403.27689 @ 2026-07-14 -->

Call chain, host/singleplayer:

```
bppaste  -> BlueprintCommands.Paste(args)                        line 3274
         -> GameManager.RunSimulation check                      line 3308
         -> BlueprintCommands.StartStaggeredPaste(...)           line 3320
         -> coroutine RunLocalStaggeredPaste(...)                line 3437 (started line 3373)
         -> ExecutePasteStaggered(...)                           line 3466
         -> CreateSingleEntry(entry, worldPos, worldRot)         line 3557
         -> OnServer.Create<Thing>(entry.PrefabName, worldPos, worldRot)   line 3562
```

The MotherboardDBPU UI path converges on the same functions: `DBPUMotherboardUI.OnPaste` (line 8315) calls `BlueprintCommands.StartStaggeredPaste` when `GameManager.RunSimulation` (line 8373) or `BlueprintNetwork.SendPasteRequest` otherwise (line 8383).

The single placement call, verbatim (lines 3557-3567):

```csharp
private static Thing CreateSingleEntry(BlueprintEntry entry, Vector3 worldPos, Quaternion worldRot)
{
    ...
    Thing val = OnServer.Create<Thing>(entry.PrefabName, worldPos, worldRot);
    if ((Object)(object)val == (Object)null)
    {
        Debug.LogWarning((object)$"[BlueprintMod] Failed to create '{entry.PrefabName}' at {worldPos}");
        return null;
    }
```

After creation it applies cosmetic and logic state: `OnServer.SetCustomColor` (line 3570), `Structure.UpdateBuildStateAndVisualizer(entry.BuildState, 0)` (line 3577), `OnServer.SetCustomName` (line 3584), `ProgrammableChip.SetSourceCode` + `SendUpdate` (lines 3598-3599), `Thing.OnOff` (line 3611), `Thing.Mode` (line 3622), `ILogicable.SetLogicValue(LogicType 12 and 72)` (lines 3638, 3642), and `RestoreSlotContents` (line 3653) which spawns slot items via `OnServer.Create<DynamicThing>(slot.PrefabName, val)` (line 4104).

So the answer to "which API": `OnServer.Create<Thing>(prefabName, position, rotation)`, the raw server-side spawn API. It is NOT `Structure/Constructor.SpawnConstruct`, NOT `OnServer.Construct`, NOT `UseMultiConstructor`, and not a bare Unity Instantiate; the game API does the Instantiate plus registration internally.

### Game-side continuation

`[Assembly-CSharp]`:

```
OnServer.Create<T>(string, Vector3, Quaternion)      line 39856
  -> OnServer.Create<T>(Thing prefab, Vector3, Quaternion)  line 39866
     -> Thing.Create<T>(prefab, position, rotation, 0L)     line 318983
```

`Thing.Create<T>` verbatim core `[Assembly-CSharp]` (lines 318993-319031):

```csharp
Thing thing = Prefab.Find(prefab.PrefabHash);
Thing thing2 = UnityEngine.Object.Instantiate(thing, worldPosition, worldRotation);
...
if (referenceId == 0L)
{
    flag = Referencable.RegisterNew(thing2);          // line 319004
    ...
    if (Assets.Scripts.Networking.NetworkManager.IsServer && NetworkBase.Clients.Count > 0)
    {
        ... NewToSend.Add(thing2);                    // line 319019, spawn message to clients
    }
}
...
if (flag)
{
    OcclusionManager.Register(thing2);                // line 319030
}
```

`Referencable.RegisterNew` triggers `Structure.OnAssignedReference` `[Assembly-CSharp]` (lines 314182-314207), which snaps the transform to the grid and registers with the grid controller:

```csharp
public override void OnAssignedReference()
{
    base.OnAssignedReference();
    Direction = ThingTransform.rotation;
    RegisteredLocalGrid = new Grid3(base.ThingTransformPosition);    // line 314186
    base.ThingTransformPosition = RegisteredLocalGrid.ToVector3();
    ...
    GridController.World.Register(this);                             // line 314204
}
```

`GridController.Register(Structure)` `[Assembly-CSharp]` (line 206469) routes SmallGrid derivatives (cables, pipes, chutes) to `AddSmallGridStructure` (line 206870), which does get-or-create per cell and then `smallCell.Add(smallGridObject)` (lines 206880 and 206885) with no occupancy validation, then fires `OnRegistered(null)` (line 206899) and neighbor `OnGridPlaced` callbacks (lines 206914-206944).

### Coroutine staggering and placement order

Entries are sorted bottom-up by world Y before placement (line 3476), with LibConstruct board structures reordered to follow their host (lines 3477-3504). Placement is batched across frames on a fixed time budget, one entry per step: `ComputePasteDuration(int structureCount) => Mathf.Clamp(structureCount * 0.15f, 2f, 30f)` (lines 3315-3318); `delay = duration / (count - 1)` (line 3506); after each entry `yield return new WaitForSeconds(delay)` (lines 3549-3552). Small blueprints stretch to the 2 s minimum; large ones cap at 30 s total, so a 1000-entry paste places roughly one structure every 30 ms for 30 seconds.

### Paste placement math

Paste anchor is the player position snapped to the 2 m large grid with +1/+2/+1 offsets (`SnapPlayerToLargeGrid`, lines 3971-3979); per-entry world position is the stored relative position rotated by (playerYAngle - CopyYAngle + extraRotY) (lines 3956-3966); the rotation argument is quantized to 90 degree steps (lines 3297-3302). BuildState is replayed via `UpdateBuildStateAndVisualizer(entry.BuildState, 0)` (line 3577), so partially built structures paste partially built.

### What bpcopy captures (what an entry replays)

Copy (`bpcopy`, line 3046) iterates `Referencable.Referencables` (line 3144) filtered by a Pos1/Pos2 bounding box inflated by 1.01 m (lines 3103-3110). It captures PrefabName, relative position, rotation, paint color index, `Structure.CurrentBuildStateIndex` (line 3172), CustomName, IC10 source code, OnOff, Mode, logic Setting (LogicType 12) and OutputTemperatureSetting (LogicType 72) via `CanLogicRead`/`GetLogicValue` (lines 3224-3231), and recursive slot contents except for storage lockers and silos (skip list `StoragePrefabNames`, line 2634, checked line 3237). Entities are excluded (line 3148); slotted DynamicThings are excluded at top level (lines 3152-3156).

### LibConstruct board structures

`LibConstructCompat` (line 4384) detects the LibConstructReloaded placement-board API by probing `typeof(PlacementBoard)` (line 4404). On paste, board-mounted structures are spawned with the same `OnServer.Create` call and then attached to their host board with `PlacementBoard.Register(structure)` (line 4528). The incident world also ran LibConstructReloaded (Steam Workshop item 3751750326).

## Occupancy: zero checks in the paste path
<!-- verified: 0.2.6403.27689 @ 2026-07-14 -->

The mod never calls `CanConstruct`, `CanConstructInfo`, `_IsCollision`, `CanReplace`, or `WillMergeWhenPlaced`. A full-text grep of the decompile finds zero references to any of them. The only occupancy-adjacent code in the whole assembly is the visual collision preview in `BlueprintPreview.RepositionPreviews` (line 1364), which tints the hologram red (verbatim, line 1409):

```csharp
bool flag = collisionPreviewEnabled && GridController.World != null && (Object)(object)GridController.World.GetStructure(list[i].Item1) != (Object)null;
```

and its own config text admits placement is not gated by it (line 4246):

```csharp
_collisionPreview = cfg.Bind<bool>("Preview", "CollisionPreviewEnabled", true, "Tint previews red when the blueprint structure overlaps with an existing structure in the world. Pasting will still be allowed regardless of preview colour.");
```

Note the preview check uses `GridController.World.GetStructure` (large-grid structure lookup); it does not even query the small grid, so a cable-on-cable overlap would not tint red in the preview either.

Placement happens without any check at line 3562 (`OnServer.Create<Thing>`), shown in the placement call chain section above.

Mechanically, how a normal cable lands in a cell already holding a super-heavy cable: the small-grid cell registry stores at most ONE cable reference per cell, and registration overwrites it unconditionally. `[Assembly-CSharp]` `SmallCell` fields (lines 292987-293003):

```csharp
public class SmallCell
{
    public Grid3 SmallGrid;
    public Chute Chute;
    public Pipe Pipe;
    public Device Device;
    public Cable Cable;          // line 292997, single slot
    ...
```

`SmallCell.Add` verbatim `[Assembly-CSharp]` (lines 293100-293107):

```csharp
public void Add(SmallGrid smallGridObjectGrid)
{
    Cable cable = smallGridObjectGrid as Cable;
    if ((bool)cable)
    {
        Cable = cable;       // line 293105, unconditional overwrite
        return;
    }
```

The occupancy denial that would normally prevent this ("grid blocked by structure", `CanConstructInfo.InvalidPlacement(GameStrings.GridBlockedByStructure...)`, `[Assembly-CSharp]` line 161150) lives exclusively in the cursor-time CanConstruct path driven by `Constructor`/`ConstructionCursor` when a player uses a cable coil. `Thing.Create` and the `GridController.Register` chain never execute it. That is also why a `Cable.CanConstruct` Harmony postfix never fires for BlueprintMod pastes: no construction cursor, no Constructor, no CanConstruct call at all.

Result in the incident world: the super-heavy cable Thing keeps existing (it is never deregistered), keeps its own CableNetwork membership, and keeps rendering; the mod-spawned normal cable is instantiated at the same coordinates, registered, and steals the `SmallCell.Cable` pointer. Cell lookups (`SmallCell.Get<T>`, power network tracing, deconstruct targeting) now resolve to the normal cable, while the super-heavy cable becomes an orphan that is still visible and still referenced by its network. Deregistration is reference-compared (`if (smallGridObject == smallCell.Cable)`, `[Assembly-CSharp]` line 206979), so removing the newer cable later nulls the cell without restoring the older one.

Config surface (BepInEx, `BlueprintSettings.Init`, lines 4237-4270): `Preview/PastePreviewEnabled` (true), `Preview/CollisionPreviewEnabled` (true, visual only), `Preview/MaxPreviews` (0 = unlimited), `Preview/MaxWireframeRenderThreshold` (100), `General/MaxUndoLevels` (20). Nothing gates placement legality.

## Failure handling in the print loop
<!-- verified: 0.2.6403.27689 @ 2026-07-14 -->

One attempt per entry, no retry, no backoff, no abort. Failures increment a counter and the loop continues. Verbatim from `ExecutePasteStaggered` (lines 3508-3554):

```csharp
for (int s = 0; s < sorted.Count; s++)
{
    if (op.Cancelled)
    {
        yield break;
    }
    int num7 = sorted[s];
    BlueprintEntry blueprintEntry2 = blueprint.Entries[num7];
    var (worldPos, worldRot, text) = positions[num7];
    if (blueprintEntry2.BuildState < 0)
    {
        Compat.ConsoleWindowPrint("[BlueprintMod] Skipped loose item: " + blueprintEntry2.PrefabName);
        op.Skipped++;
        continue;
    }
    try
    {
        Thing val = CreateSingleEntry(blueprintEntry2, worldPos, worldRot);
        if ((Object)(object)val != (Object)null)
        {
            op.PastedThings.Add(val);
            op.Created++;
            ...
        }
        else
        {
            op.Failed++;
        }
    }
    catch (Exception ex)
    {
        Debug.LogError((object)("[BlueprintMod] Error pasting '" + text + "': " + ex.Message));
        op.Failed++;
    }
    if (s < sorted.Count - 1 && delay > 0f)
    {
        yield return (object)new WaitForSeconds(delay);
    }
}
op.Complete = true;
```

End-of-paste reporting (lines 3452-3462): prints `"Pasted {op.Created} structures."` plus `" ({op.Skipped} loose Items skipped...)"` and `" ({op.Failed} failed)"` to the console window and fires the `OnPasteComplete` UI event. The server-side variant reports counts back to the requesting client over the chat protocol: `SendChatBroadcast(string.Format(..., "{0}OK{1}{2}{3}{4:F2}", "BP", op.Created, op.Failed, op.Skipped, pasteCost), humanId)` (line 798), rendered client-side as `"Pasted N structures. ... (M failed)"` (lines 873-892).

Failure surfaces:

- If `OnServer.Create` returns null, `CreateSingleEntry` logs a warning and returns null (lines 3563-3567), counted as `op.Failed++` (line 3541). In practice `OnServer.Create<T>` never returns null; it throws `NullReferenceException` when `Thing.Create` produced nothing (`[Assembly-CSharp]` lines 39866-39876), and `Thing.Create` itself throws on a missing prefab (`[Assembly-CSharp]` lines 318985-318992). Either way the per-entry try/catch (lines 3523-3548) converts it to `op.Failed++` and moves on.
- `bpundo` while a paste is running cancels it: `CancelActivePaste` (line 3393) stops the coroutine and destroys everything placed so far via `OnServer.Destroy(pastedThing)` (line 3415). Completed pastes are undone the same way (`Undo`, line 3784, destroy loop lines 3804-3818, tolerant of already-destroyed things via `!item.IsBeingDestroyed` and try/catch).

Duplicate-paste guard fingerprint is coarse: `string.Format("{0}@{1:F1},{2:F1},{3:F1}r{4:F0}", count, pastePos.x, pastePos.y, pastePos.z, rotY)` (line 3347). It encodes only entry count, position, and rotation, so pasting a different blueprint with the same entry count at the same spot is also blocked until undo. It also means re-running an identical paste (for example after an external guard refused some pieces) is blocked until `bpundo`, which conveniently suppresses guard-refusal retry spam even at the player level.

## Materials: DeanamicMatter cost economy
<!-- verified: 0.2.6403.27689 @ 2026-07-14 -->

Printing costs a mod-specific consumable, DeanamicMatter, unless the world is creative:

- Creative check: `DeanamicMatterCost.IsCreativeMode()` (line 9379) reads `DifficultySetting.Current.Creative`, falling back to `WorldManager.IsCreative()`. Creative pastes are free.
- Cost model: `CalculateCost` (line 9396) sums per-prefab gram prices over buildable entries only (`BuildState >= 0`). Prices come from an `ExactCosts` dictionary (line 9009, about 290 prefabs), then a `PrefixCosts` prefix table (line 9302; `("StructureCableSuperHeavy", 0.2f)` line 9304, `("StructureCable", 0.1f)` line 9305), then a `ContainsCosts` substring table (line 9363), default 0.5 g (line 9437).
- Deduction is up-front for the WHOLE blueprint, before any placement: host path lines 3352-3366 (`TryDeductCost`, abort with "Not enough DeanamicMatter" if short); server-on-behalf-of-client path lines 692-709. `TryDeductCost` (line 9489) drains grams from `DeanamicMatter` consumable stacks found recursively in the player's slots (`FindDeanamicMatter`, line 9440).
- Failed placements are NOT refunded per piece. `op.Failed` has no interaction with cost anywhere; the full blueprint price stays spent even if every entry fails. Refunds happen only for: cancel mid-paste (lines 3427-3434), full undo (host lines 3820-3828, server lines 836-848), or a server paste that ends cancelled (lines 775-782). `TryRefundCost` (line 9519) refills existing stacks and spawns overflow `ItemDeanamicMatter` items above the player via `OnServer.Create<Thing>("ItemDeanamicMatter", ...)` (line 9560).
- DeanamicMatter is itself craftable: `GameData/autolathe.xml` adds an Autolathe recipe (20 Steel, 5 Silicon, 3 Gold, 2 Solder per unit), `AdvancedFurnaceRecipePatch` (line 9695, postfix on `WorldManager.LoadDataFiles`) registers three Advanced Furnace alloy recipes at runtime, and `GameData/recycling.xml` plus `Recycler.AddRecycleRecipe` (line 9780) make it recyclable.

So: materials are a flat gram economy, not kits; charged before placement; per-piece failures lose the grams for those pieces; only cancel/undo refunds. Cost table trivia: a super-heavy cable costs 0.2 g and a normal cable 0.1 g of DeanamicMatter (lines 9304-9305), so the 2026-07-13 incident overwrite was also the cheaper piece stomping the more expensive one.

## Timing and multiplayer protocol
<!-- verified: 0.2.6403.27689 @ 2026-07-14 -->

- Unity main thread only. The paste is a coroutine started with `((MonoBehaviour)BlueprintMod.Instance).StartCoroutine(RunLocalStaggeredPaste(...))` (line 3373) or `RunServerStaggeredPaste` (line 744). No Task/Thread usage in the placement path. The frame-staggering budget math is in the placement call chain section above.
- Host-only execution, gated on `GameManager.RunSimulation`: command path line 3308, chunk/header/undo handlers lines 567, 608, 804. A client without simulation authority sends the blueprint to the server over the vanilla chat channel: `BlueprintNetwork.SendPasteRequest` (line 457) gzips the XML, base64s it, splits it into 700-char chunks, and transmits them as `ChatMessage` traffic prefixed `BP` (lines 468-478). The server reassembles (`HandleChunk` line 565, `HandleHeader` line 606), then `TryExecutePaste` (line 648) runs the same staggered coroutine server-side (line 744).
- Clients cannot paste at all if the server lacks the mod: a BPPING/BPPONG handshake (up to 3 pings, 10 s apart, lines 426-455) sets `ServerHasMod`, and `SendPasteRequest` refuses without it (lines 462-465).
- Chat interception is a Harmony prefix on `ChatMessage.Process` (attribute line 353, method lines 356-373): any chat text starting with `"BP"` is consumed by the mod (`return false`) and routed to `BlueprintNetwork.HandleProtocol`. Consumed lines are never displayed (lines 361-366); unknown `BP...` text logs `"[BlueprintMod] Unknown protocol"` (line 552). Chunk buffers are keyed by `humanId:sessionId` and expire after 60 s (`CleanStaleBuffers`, line 1108, swept every 90 s from `Update`, lines 4848-4852).
- No permission model: any client with the mod installed can paste on any server that has the mod, subject only to DeanamicMatter cost. Undo is per-client (server tracks `_clientPastedThings` by humanId, line 414); one client cannot undo another client's paste.
- Save load / multiplayer join: nothing is pasted at load or join. At data-file load, the `WorldManager.LoadDataFiles` postfix registers furnace recipes (line 9694). On joining as a client, up to three BPPING chat messages are emitted from `BlueprintMod.Update -> BlueprintNetwork.ClientUpdate` (lines 4847, 426-455). Session state (clipboard, undo stack, paste history) resets when the local player disappears (world exit, lines 4841-4845). The undo stack holds live `Thing` references and is not persisted; a save/load cycle forgets undo history.

Blueprint storage and transport: XML via `XmlSerializer` (`BlueprintSerializer`, lines 5212-5227), one file per blueprint at `Application.persistentDataPath/Blueprints/<name>.blueprint` (lines 4769, 4145-4147), plus a `<name>.previews/` folder of JPEG renders (lines 3682-3688). Schema: `BlueprintData` root `Blueprint` (line 4870) with Name, Created, GameVersion, ModVersion, Author, BoundsSize, PlayerLocalOffset, CopyYAngle, WorkshopFileHandle, ContainsPlacementBoardStructures, and `Entries` of `BlueprintEntry` (line 4935). Network transfer reuses the same XML, gzip plus base64 (lines 1081-1106). Blueprints can also be published to and subscribed from the Steam Workshop as their own items (`BlueprintWorkshop`, line 1746, including raw `SteamAPI_ISteamUGC_*` P/Invokes, lines 2151-2176).

The v1.6.3 changelog (About.xml) confirms known rough edges: paste animation stops early on large blueprints for multiplayer clients, and locker/silo slot contents were newly excluded from copies "to avoid cheating a little bit atleast".

## Harmony patch inventory
<!-- verified: 0.2.6403.27689 @ 2026-07-14 -->

Harmony id `com.blueprintmod.patch`, `PatchAll` at lines 4776-4778. Complete patch list:

- `ChatMessage.Process` prefix (line 353): the `BP` chat-protocol interception described in the multiplayer section.
- Seven `KeyManager` prefixes (`GetForwardAxis`, `GetRightAxis`, `GetAscend`, `GetDescend`, `GetButton`, `GetButtonDown`, `GetButtonUp`, lines 4599-4695) that suppress game input only while a mod UI text field is focused (`BlueprintInputGuard.IsTyping`, line 4562).
- `WorldManager.LoadDataFiles` postfix (line 9694): registers the Advanced Furnace DeanamicMatter recipes at data-file load.
- `Stationpedia.PopulateLists` postfix (line 9806): adds its five prefabs to a Stationpedia category.

It patches NOTHING on Cable, Structure, Constructor, InventoryManager, or any placement or occupancy method, so a PowerGridPlus guard on the registration path will not collide with any BlueprintMod patch.

## Guard-relevant facts
<!-- verified: 0.2.6403.27689 @ 2026-07-14 -->

- Every BlueprintMod placement flows through `Thing.Create<T>(prefab, pos, rot, referenceId: 0L)` and then `GridController.Register(Structure)`. The save-load and client-join paths use `referenceId != 0` (`Referencable.RegisterAs`, `[Assembly-CSharp]` line 319026) while fresh spawns use `RegisterNew` (referenceId 0). A registration-time guard can use that distinction to avoid interfering with load and join.
- Vanilla legit construction also ends in `Thing.Create`/`GridController.Register` after `CanConstruct` passes, so a registration-time occupancy guard only fires in states vanilla checks would have prevented (for cables: incoming Cable into a SmallCell whose `Cable` field is already occupied). Refusing there cannot break normal building.
- A refused placement (the create call throws, returns null, or the guard destroys the thing immediately) costs BlueprintMod exactly one failed entry. There is no retry loop and no spam; the player sees a single aggregate "(N failed)" plus one warning or error log line per refused piece, and the grams already deducted for the refused pieces are not refunded (see the materials section). The coarse duplicate-paste fingerprint additionally blocks re-running the identical paste until `bpundo`, suppressing retry spam at the player level too.
- Undo interaction with a guard: `op.PastedThings` only receives non-null results (lines 3526-3528), and the undo destroy loops tolerate missing or destroyed things (lines 3804-3818; line 3806 checks `IsBeingDestroyed`, try/catch around `OnServer.Destroy`). If a guard destroys a pasted cable after the fact, a later `bpundo` does not crash.
- The undo stack holds live `Thing` references in memory only; it is not persisted, so it is gone after a save/load cycle. Any cleanup that relies on `bpundo` must happen in the same session as the paste.

## Verification history
<!-- verified: 0.2.6403.27689 @ 2026-07-14 -->

- 2026-07-14: page created from a fresh ilspycmd 10.0.0.8330 decompile of the Workshop copy of BlueprintMod v1.6.3 (Steam Workshop item 3672138641, BlueprintMod.dll), cross-read against the game decompile at 0.2.6403.27689. Driving work: mixed-tier cable placement prevention design for PowerGridPlus after the 2026-07-13 incident where a paste merged normal-tier cables into a super-heavy mainline.

## Open questions

None currently.
