---
title: Save-load ordering: defer restore to OnFinishedLoad
type: Patterns
created_in: 0.2.6228.27061
verified_in: 0.2.6403.27689
verified_at: 2026-07-18
sources:
  - Plans/EquipmentPlus/RESEARCH.md:250-251 (F0122, primary)
  - Plans/EquipmentPlus/EquipmentPlus/ActiveSlotPersistence.cs:11-28 (F0333)
  - Plans/EquipmentPlus/EquipmentPlus/AdvancedTabletPatches.cs:103-108 (F0373)
  - Plans/EquipmentPlus/EquipmentPlus/ActiveSlotPersistence.cs:95-101 (F0374)
  - .work/decomp/0.2.6403.27689/Assembly-CSharp.decompiled.cs :: XmlSaveLoad class (line 267663), Load<T> (line 268424), Load (line 268463)
  - .work/decomp/0.2.6403.27689/Assembly-CSharp.decompiled.cs :: LoadWorld (line 268507), caller (line 264682), GameManager.GameTick pause gate (lines 204387-204418)
  - .work/decomp/0.2.6403.27689/Assembly-CSharp.decompiled.cs :: NetworkClient.ProcessJoinData (line 213188, tail 213235-213239), GameManager.StartGame (204575-204581), UpdateThingsOnGameStart (204629-204632) + UpdateThingsOnGameStartAction (203847-203869), Device.InitAllDevices (371889-371892)
related:
  - ../GameSystems/SaveDataRegistration.md
  - ./HarmonyInheritedMethods.md
tags: [save-load, harmony]
---

# Save-load ordering: defer restore to OnFinishedLoad

When a parent Thing's saved state references child Things that have not yet been placed into its slots, `DeserializeSave` cannot restore that state immediately. The child-placement pass fires `OnChildEnterInventory` per child, which can clobber values set earlier. The pattern: stash the saved value in a dictionary during `DeserializeSave`, then apply it from `OnFinishedLoad`, which runs after every child has been placed.

## Problem
<!-- verified: 0.2.6228.27061 @ 2026-04-20 -->

F0122 (Plans/EquipmentPlus/RESEARCH.md:250-251, primary):

> Vanilla restores children (chips/cartridges) one at a time AFTER the parent's `DeserializeSave`. Each child's `MoveToSlot` fires `OnChildEnterInventory`, which for lenses unconditionally sets `Sensor = child`. The last chip to enter wins, overwriting any value set during deserialization. Active-slot restoration must wait for `OnFinishedLoad`, which fires after all children are placed.

F0333 (code comment, `ActiveSlotPersistence.cs:11-28`) extends to `AdvancedTablet`:

```text
    /// <summary>
    /// Vanilla's load sequence places every child of a Thing (sensor chips
    /// inside lenses, cartridges inside a tablet) one at a time AFTER the
    /// parent's DeserializeSave returns. Each MoveToSlot fires the parent's
    /// OnChildEnterInventory, which for SensorLenses unconditionally sets
    /// <c>Sensor = child</c>. As a result, the last chip to re-enter wins
    /// and any value we set from saved data is immediately overwritten.
    ///
    /// AdvancedTablet has an analogous problem: <c>Mode</c> isn't persisted
    /// at all, so it resets to 0 (first cartridge) on every load.
    ///
    /// This class runs the saved active-slot restore from <c>OnFinishedLoad</c>
    /// -- a Thing-lifecycle hook that fires after child slots are populated.
    /// Save-time capture is done by <c>SensorLensesSerializeSavePatch</c> and
    /// <c>AdvancedTabletSerializeSavePatch</c>; at deserialize time the
    /// saved value is stashed in a dictionary here; <c>OnFinishedLoad</c>
    /// reads the dictionary and applies.
    /// </summary>
```

## Solution / recipe
<!-- verified: 0.2.6228.27061 @ 2026-04-20 -->

Three-step pattern:

1. **Save-time capture.** `Postfix SerializeSave` to write the reference-id (or index, or whatever identifies the active child) into the saved blob.
2. **Deserialize stash.** `Postfix DeserializeSave` to read the saved value and store it in a dictionary keyed by the parent's `ReferenceId`. Do NOT attempt to apply it here; the children are not in their slots yet.
3. **OnFinishedLoad apply.** Hook `Thing.OnFinishedLoad` (by whichever Harmony mechanism suits; inherited methods need `TargetMethod()` per `./HarmonyInheritedMethods.md`). Look up the dictionary entry, find the matching child by reference-id, and set the active-slot field.

F0373 (Plans/EquipmentPlus/EquipmentPlus/AdvancedTabletPatches.cs:103-108) is the specific AdvancedTablet case:

> Mode cannot be restored yet -- CartridgeSlots is rebuilt by our RefreshSlots later, and vanilla hasn't placed cartridges into the slots yet either. Stash the saved reference id and apply it in OnFinishedLoad.

F0374 (Plans/EquipmentPlus/EquipmentPlus/ActiveSlotPersistence.cs:95-101) documents the apply step:

> Find the CartridgeSlots index whose occupant matches the saved cartridge reference id, and set Mode to that index. `Thing.set_Mode` propagates via `Interactable.set_State`; we also call the private `GetCartridge()` to force the display to reflect the new active cartridge immediately.

Skeleton:

```csharp
// Stash during deserialize:
[HarmonyPostfix]
static void DeserializePostfix(Thing __instance, /* saved data */ data)
{
    _savedActiveIds[__instance.ReferenceId] = data.ActiveChildRefId;
}

// Apply during OnFinishedLoad:
[HarmonyPostfix]
static void OnFinishedLoadPostfix(Thing __instance)
{
    if (!_savedActiveIds.TryGetValue(__instance.ReferenceId, out var savedId)) return;
    _savedActiveIds.Remove(__instance.ReferenceId);

    // Find child by reference id among populated slots, then set active-slot field.
}
```

## Cited verifications
<!-- verified: 0.2.6228.27061 @ 2026-04-20 -->

- F0122: primary rule (OnChildEnterInventory clobbers; wait for OnFinishedLoad).
- F0333: generalized to both SensorLenses and AdvancedTablet.
- F0373: AdvancedTablet-specific case where `Mode` is not persisted and also depends on rebuilt dynamic slots.
- F0374: apply-side implementation including `Thing.set_Mode` via `Interactable.set_State` and the `GetCartridge()` refresh.

## OnRegistered fires before DeserializeSave
<!-- verified: 0.2.6228.27061 @ 2026-06-10 -->

The per-Thing load order starts earlier than the DeserializeSave/OnFinishedLoad pair above. `XmlSaveLoad.LoadThing` (decompile line 251312) is:

```csharp
Thing thing2 = Thing.Create<Thing>(thing, worldPosition, worldRotation, thingData.ReferenceId);
...
thing2.DeserializeSave(thingData);
thing2.ValidateOnLoad(CurrentSaveRevision);
```

`Thing.Create` registers the thing into the world synchronously; for structures the registration path is `GridController.AddGridStructure` (line 191515), whose last act is `structure.OnRegistered(cell)` (line 191563). So the complete per-Thing order on world load is:

1. `Thing.Create` -> grid registration -> `OnRegistered(cell)` (prefab-default field values).
2. `DeserializeSave(thingData)` (saved values applied).
3. Child placement (`OnChildEnterInventory` per child).
4. `OnFinishedLoad` (everything in place).

Implication: a Harmony postfix on `OnRegistered` that initialises a field to a non-vanilla default (e.g. PowerGridPlus initialising `Transformer.Setting = OutputMaximum`) is automatically overwritten by the saved value for loaded things, while sticking for fresh constructions (which never run DeserializeSave). This gives "default for new builds, preserve saved state" semantics with no load-vs-build discrimination logic.

## XmlSaveLoad.LoadThing renamed to Load at 0.2.6403
<!-- verified: 0.2.6403.27689 @ 2026-07-02 -->

At game version 0.2.6403.27689, `XmlSaveLoad.LoadThing(ThingSaveData, bool)` no longer exists: a whole-decompile grep for `LoadThing` returns zero hits. The per-Thing loader is now a pair of overloads on the same class (`XmlSaveLoad : ManagerBase`, decompile line 267663):

```csharp
public static T Load<T>(ThingSaveData thingData, bool generatesTerrain = true) where T : Thing   // line 268424, uses Prefab.Find<T> + Thing.Create<T>
public static Thing Load(ThingSaveData thingData, bool generatesTerrain = true)                   // line 268463
```

The non-generic body (lines 268463-268500) keeps the semantics the previous section relies on, in the same order:

```csharp
	public static Thing Load(ThingSaveData thingData, bool generatesTerrain = true)
	{
		Thing thing = Prefab.Find(thingData.PrefabName);
		if (thing == null)
		{
			UnityEngine.Debug.LogWarning("Can't spawn " + thingData.PrefabName);
			return null;
		}
		if (!thingData.IsValidData())
		{
			return null;
		}
		Vector3 worldPosition = thingData.WorldPosition;
		Quaternion worldRotation = thingData.WorldRotation;
		if (thingData is StructureSaveData structureSaveData)
		{
			worldPosition = structureSaveData.RegisteredWorldPosition;
			worldRotation = structureSaveData.RegisteredWorldRotation;
			RocketRecordData rocketRecord = structureSaveData.RocketRecord;
			if (rocketRecord != null && rocketRecord.RocketNetworkId != 0L)
			{
				EngineFuselage engineFuselage = Referencable.Find<RocketNetwork>(rocketRecord.RocketNetworkId)?.Anchor;
				if ((object)engineFuselage != null)
				{
					worldPosition = engineFuselage.ThingTransformPosition + rocketRecord.Offset;
				}
			}
		}
		Thing thing2 = Thing.Create<Thing>(thing, worldPosition, worldRotation, thingData.ReferenceId);
		if (!thing2)
		{
			return null;
		}
		thing2.generateTerrain = generatesTerrain;
		thing2.DeserializeSave(thingData);
		thing2.ValidateOnLoad(CurrentSaveRevision);
		return thing2;
	}
```

`Thing.Create` -> `DeserializeSave` -> `ValidateOnLoad` is unchanged, so the ordering conclusion in "OnRegistered fires before DeserializeSave" above (verified at 0.2.6228.27061 through the old `LoadThing` name) carries over; only the method name and the added generic overload differ. The `GridController.AddGridStructure` registration leg of that section was not re-read at 0.2.6403.

Mod impact: any mod that calls or reflection-resolves `XmlSaveLoad.LoadThing` by name breaks at 0.2.6403 (`MissingMethodException` on a compiled call, null from `AccessTools.Method`). The 2026-07-02 dedicated-server boot investigation records ModularConsoleMod as a casualty of exactly this removal. Retarget to `Load(ThingSaveData, bool)`; when resolving by reflection, pass explicit parameter types since the name now also has a generic overload. See `../Unsorted/Api-removals-0.2.6403.md` for the sibling API removals in the same game update.

## XmlSaveLoad.LoadWorld is async: a Harmony postfix fires at load START, not load completion
<!-- verified: 0.2.6403.27689 @ 2026-07-13 -->

`XmlSaveLoad.LoadWorld` (same class, decompile line 268507 at 0.2.6403.27689) is an async method. Verbatim head:

```csharp
public static async UniTask LoadWorld(bool loadWithoutChars = false)                                // 268507
{
    WorldManager.SetGamePause(pauseGame: true);                                                     // 268509: first synchronous statement
    await ImGuiLoadingScreen.SetState(GameStrings.LoadingScreenInitializing.DisplayString);         // 268510: first await
    FadePanel.ToBlack(0.5f);
    await UniTask.Delay(500, DelayType.UnscaledDeltaTime);
    Stopwatch overallLoadTime = new Stopwatch();
    overallLoadTime.Start();
    await UniTask.DelayFrame(2);
    GameManager.GameState = GameState.Loading;                                                      // 268516: only after three awaits
    ...
}
```

The C# compiler rewrites an `async` method into a stub that constructs the state machine, runs it to the first suspending `await`, and returns the `UniTask`. Harmony patches that stub. A `[HarmonyPostfix]` on `LoadWorld` therefore runs when the stub returns, i.e. as soon as the method suspends at its first await (268510): at postfix time the pause request from 268509 has been issued, `GameState` is not yet `Loading` (268516), nothing has been deserialized, and the actual load continues asynchronously long after the postfix returned. The postfix observes load START, never load completion. The single vanilla caller awaits the task (`await XmlSaveLoad.LoadWorld();`, 264682).

Two consequences for mods:

- To run code at load completion, do not postfix `LoadWorld` directly. Hook something the tail of the load actually reaches (per-Thing `OnFinishedLoad`, or the `GameState` transition to `Running`), or take `__result` (the `UniTask`) in the postfix and continue after awaiting it.
- A postfix that assumes "no simulation tick is running because the game just paused" is also wrong on arrival: `GameManager.GameTick` (204387) checks `WorldManager.IsGamePaused` only at the TOP of each loop iteration (204394) and then moves the tick body onto a ThreadPool worker (`await UniTask.SwitchToThreadPool()`, 204418). A tick that passed the pause check before `SetGamePause(true)` landed runs to completion, including `ElectricityManager.ElectricityTick()` (204466), concurrently with the first stretch of `LoadWorld` and with anything a load-start postfix does. See [PowerTickThreading](../GameSystems/PowerTickThreading.md).

## OnFinishedLoad also fires on multiplayer client join, not only on save load
<!-- verified: 0.2.6403.27689 @ 2026-07-18 -->

The `OnFinishedLoad` hook this page's pattern relies on is NOT exclusive to the save-load path. Both game-entry paths funnel into the same per-Thing sweep, `GameManager.UpdateThingsOnGameStart` (204629-204632):

```csharp
public static void UpdateThingsOnGameStart()
{
    OcclusionManager.AllThings.ForEach(UpdateThingsOnGameStartAction);
}
```

whose action (203847-203869) invokes `thing.OnFinishedLoad()` per thing behind a per-thing try / catch, verbatim:

```csharp
private static readonly Action<Thing> UpdateThingsOnGameStartAction = delegate(Thing thing)
{
    if ((object)thing == null)
    {
        return;
    }
    try
    {
        thing.OnFinishedLoad();
        foreach (Interactable interactable in thing.Interactables)
        {
            if (interactable.JoinInProgressSync && (bool)interactable.Animator)
            {
                interactable.SetState();
                thing.OnFinishedInteractionSync(interactable);
            }
        }
    }
    catch (System.Exception arg)
    {
        ConsoleWindow.PrintError($"OnFinishedLoad failed for {thing.DisplayName} #{thing.ReferenceId}: {arg}");
    }
};
```

Call sites (whole-decompile census at 0.2.6403.27689: exactly two):

- Host / single-player start after load: `GameManager.StartGame` (204575) sets `GameState = GameState.Running;` (204577) and calls `UpdateThingsOnGameStart();` (204580).
- Multiplayer client join: `NetworkClient.ProcessJoinData` (`NetworkClient : NetworkBase` at 212856; the method at 213188, awaited from the join flow at 213084) ends the join with (213235-213239), verbatim:

```csharp
GameManager.GameState = GameState.Running;
GameManager.UpdateThingsOnGameStart();
Device.InitAllDevices();
Singleton<DiscordClient>.Instance.UpdateActivityInGame();
Rocket.OnFinishedLoad();
```

(`Device.InitAllDevices` at 371889-371892 is `AllDevices.ForEach(InitializeDeviceAction);`, the same device network-registration sweep the load path runs at 268725; the joining client re-registers device network membership right after the OnFinishedLoad sweep.) On both paths the sweep runs after `GameState` was set to `Running`, so a `GameManager.GameState` check inside an `OnFinishedLoad` hook sees `Running`, never `Loading` or `Joining`.

Consequence for this page's pattern (stale-cache hazard): a mod restore keyed on `Thing.OnFinishedLoad` ALSO fires on every multiplayer join, for every replicated thing, with whatever state the mod's process-level caches hold at that moment. A stash dictionary populated by an earlier world in the same game process (a single-player session before joining a server, or a previous join) is still there when the join-time sweep runs, and colliding `ReferenceId`s apply stale entries to the new world's things. A load-keyed restore must clear its stashes on world teardown (or key them per world session) and must tolerate things for which no stash entry exists.

## Verification history
<!-- verified: 0.2.6228.27061 @ 2026-04-20 -->

- 2026-04-20: page created from the Research migration; F0122 primary, three additional sources corroborating across two device classes.
- 2026-06-10: added the "OnRegistered fires before DeserializeSave" section from a direct read of `XmlSaveLoad.LoadThing` (line 251312) and `GridController.AddGridStructure` (line 191515-191563) in the 0.2.6228.27061 decompile, during the PowerGridPlus Setting-init design.
- 2026-07-02: added the "XmlSaveLoad.LoadThing renamed to Load at 0.2.6403" section (`Load` plus a new generic `Load<T>` overload). Verified against the 0.2.6403.27689 decompile: zero grep hits for `LoadThing`, replacement overloads quoted verbatim from lines 268424 and 268463. The 0.2.6228-stamped ordering sections above were not restamped; the new section records which part of their evidence (the Create -> DeserializeSave -> ValidateOnLoad order) is re-confirmed by the new body and which part (`GridController.AddGridStructure`) was not re-read.
- 2026-07-13: added the "XmlSaveLoad.LoadWorld is async" section (game version 0.2.6403.27689) from a fresh-validation pass on a decompile-claim audit. Verbatim method head read at 268507-268516 (`public static async UniTask LoadWorld(bool loadWithoutChars = false)`; `SetGamePause(true)` is the first synchronous statement, `GameState = Loading` lands only after three awaits); sole caller `await XmlSaveLoad.LoadWorld();` at 264682; the `GameTick` top-of-loop pause gate (204394) and `SwitchToThreadPool` handoff (204418) read the same pass. Establishes that a Harmony postfix on `LoadWorld` fires at load start (the async stub returns at the first suspending await) with a simulation tick possibly still in flight. Additive; no existing content contradicted.
- 2026-07-18: added the "OnFinishedLoad also fires on multiplayer client join" section (game version 0.2.6403.27689); the finding was produced and independently adversarially verified against the decompile this session. Whole-decompile census: `UpdateThingsOnGameStart` (204629-204632, `OcclusionManager.AllThings.ForEach(UpdateThingsOnGameStartAction)`) has exactly two callers, `GameManager.StartGame` (204580, after `GameState = Running` at 204577) and the tail of `NetworkClient.ProcessJoinData` (213236, after `GameState = Running` at 213235 and followed by `Device.InitAllDevices()` at 213237); the action delegate (203847-203869) calls `thing.OnFinishedLoad()` (203855) behind a per-thing try / catch. Records the stale-cache hazard for OnFinishedLoad-keyed restores on join. Additive; no existing content contradicted.

## Open questions

None at creation.
