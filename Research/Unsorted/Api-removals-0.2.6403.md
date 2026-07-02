---
title: API removals at 0.2.6403 that break mods
type: Unsorted
created_in: 0.2.6403.27689
verified_in: 0.2.6403.27689
verified_at: 2026-07-02
sources:
  - .work/decomp/0.2.6403.27689/Assembly-CSharp.decompiled.cs :: whole-file greps for LoadThing / ConnectedDevices / ShowTransformArrow / UnitTest_ConstructionValidate (zero hits each); Wireframe class (line 255549), SmallGrid class (line 312025), XmlSaveLoad.Load overloads (lines 268424, 268463)
  - DedicatedServer/data/server.log and DedicatedServer/install/BepInEx/LogOutput.log, 2026-07-02 dedicated-server boot at game 0.2.6403.27689
related:
  - ../Patterns/SaveLoadOrdering.md
  - ../Patterns/CursorAdjacencyLookup.md
  - ../GameClasses/CableNetwork.md
  - ../GameClasses/InventoryManager.md
tags: [harmony]
---

# API removals at 0.2.6403 that break mods

The 0.2.6403 game update removed or renamed several `Assembly-CSharp` members that third-party mods reference. Each produces a distinct failure signature at load or at first JIT of the touching method. Every absence below is verified by a whole-decompile grep of `.work/decomp/0.2.6403.27689/Assembly-CSharp.decompiled.cs` (zero hits for the member name); the parent classes all still exist, so these are member removals, not class removals.

## Summary
<!-- verified: 0.2.6403.27689 @ 2026-07-02 -->

| Member (pre-0.2.6403) | Fate at 0.2.6403.27689 | Failure signature in a mod built against the old API | Details |
|---|---|---|---|
| `XmlSaveLoad.LoadThing(ThingSaveData, bool)` | Renamed to `Load(ThingSaveData, bool)`; new generic `Load<T>` overload added | `MissingMethodException` on a compiled call; `AccessTools.Method` returns null | [SaveLoadOrdering](../Patterns/SaveLoadOrdering.md), section "XmlSaveLoad.LoadThing renamed to Load at 0.2.6403" |
| `SmallGrid.ConnectedCables()` / `ConnectedCables(NetworkType)` / `ConnectedDevices()` | Removed; replaced by the allocation-free `FillConnected` Span-filler family on `SmallGrid` (class survives at line 312025) | `MissingMethodException` / null `AccessTools` lookups | [CursorAdjacencyLookup](../Patterns/CursorAdjacencyLookup.md), section "FillConnected replaced ConnectedCables/ConnectedDevices at 0.2.6403 (API migration)"; also noted on [CableNetwork](../GameClasses/CableNetwork.md) |
| `Wireframe.ShowTransformArrow` (public `bool` field on `Assets.Scripts.UI.Wireframe`) | Removed | `MissingFieldException` at JIT of any method reading or writing the field | This page, below |
| `InventoryManager.UnitTest_ConstructionValidate` (on `Assets.Scripts.Inventory.InventoryManager`) | Removed | HarmonyX warning `AccessTools.DeclaredMethod: Could not find method ...`; an attribute-targeted patch would throw at `PatchAll` | This page, below |

The first two rows are already curated in depth on their linked pages; this page holds the two removals with no better-fitting home plus the shared provenance. "Previously present" status: the two linked pages quote the pre-0.2.6403 members verbatim from the 0.2.6228.27061 decompile ([SaveLoadOrdering](../Patterns/SaveLoadOrdering.md) and [UnregisteredSaveDataBehavior](../GameSystems/UnregisteredSaveDataBehavior.md) for `LoadThing`; [CursorAdjacencyLookup](../Patterns/CursorAdjacencyLookup.md) for the `Connected*` family). For `ShowTransformArrow` and `UnitTest_ConstructionValidate` no page quoted them before removal and the 0.2.6228 decompile cache has been deleted per the one-version rule; their prior existence is evidenced by the workshop mods below, which compiled against them and ran on 0.2.6228-era game builds.

## Wireframe.ShowTransformArrow (bool field) removed
<!-- verified: 0.2.6403.27689 @ 2026-07-02 -->

`public class Wireframe : MonoBehaviour` survives at decompile line 255549. Its field list at 0.2.6403.27689 is: `WireframeEdges`, `BlueprintBounds`, `BlueprintTransform`, `BlueprintMeshFilter`, `BlueprintRenderer`, `LineMaterial`, `OrientationArrowOffset`, private `OrientationArrowStyle` (default `ArrowStyle.NarrowCross`), static `ColorBlueAlpha`, plus private redraw/grid/position/rotation trackers (lines 255551-255581). No `ShowTransformArrow` member exists anywhere in the decompile.

Because the removed member was a FIELD, the failure is `MissingFieldException`, thrown when the runtime JITs a mod method whose IL still references it. Two workshop mods hit it during the 2026-07-02 dedicated-server boot, both from blueprint/wireframe setup helpers:

```
Error setting up prefab StructureDataNetworkConnector
MissingFieldException: Field not found: bool Assets.Scripts.UI.Wireframe.ShowTransformArrow Due to: Could not find field in class
  at semoro.DataNetworkConnector.PrefabBlueprintUtil.Blueprintify (Assets.Scripts.Objects.Thing thing) [0x0008c] ...
  at LaunchPadBooster.PrefabSetup`1[T].LaunchPadBooster.IPrefabSetup.Run (System.Collections.Generic.IEnumerable`1[T] things) [0x00061] ...
```

```
MissingFieldException: Field not found: bool Assets.Scripts.UI.Wireframe.ShowTransformArrow Due to: Could not find field in class
  at ridorana.IC10Inspector.patches.PrefabPatch.Blueprintify (Assets.Scripts.Objects.Thing thing) [0x0000e] ...
  at ridorana.IC10Inspector.patches.PrefabPatch.Prefix () [0x0003e] ...
```

Both stacks unwind into `Assets.Scripts.Objects.Prefab:DMD<Assets.Scripts.Objects.Prefab::LoadAll>()`: the first via LaunchPadBooster's `PrefabSetup.Run` (which caught it per-prefab and logged `Error setting up prefab ...`), the second via a raw Harmony prefix on `Prefab.LoadAll`. On the dedicated server these load-time failures feed the StationeersLaunchPad exit path documented in [StationeersLaunchPadDedicatedServer](../Workflows/StationeersLaunchPadDedicatedServer.md), section "Load-failure and self-update exits (StationeersLaunchPad 0.4.0)".

## InventoryManager.UnitTest_ConstructionValidate removed
<!-- verified: 0.2.6403.27689 @ 2026-07-02 -->

Zero decompile hits for `UnitTest_ConstructionValidate`. The `UnitTest_*` family is otherwise still present at 0.2.6403.27689: `CameraController.UnitTest_Rotation` (line 197541), `CameraController.UnitTest_SetRotation` (line 197555), and `UnitTest_SetPos` (line 211952), with call sites at lines 32773 and 32850-32851. The `InventoryManager` class itself survives (the exception below names it as `Assets.Scripts.Inventory.InventoryManager`).

Observed signature in `BepInEx/LogOutput.log` (2026-07-02 boot, line 21, immediately after `Chainloader startup complete`):

```
[Warning:  HarmonyX] AccessTools.DeclaredMethod: Could not find method for type Assets.Scripts.Inventory.InventoryManager and name UnitTest_ConstructionValidate and parameters
```

This is the soft failure mode: some mod resolves the method via `AccessTools.DeclaredMethod`, gets null plus this HarmonyX warning, and its patch silently does not apply. A `[HarmonyPatch(typeof(InventoryManager), "UnitTest_ConstructionValidate")]` attribute target would instead throw at `PatchAll` time. The warning line does not name the owning mod (see Open questions).

## Verification history

- 2026-07-02: page created during the dedicated-server boot investigation at game 0.2.6403.27689. All four absences verified by whole-file grep of `.work/decomp/0.2.6403.27689/Assembly-CSharp.decompiled.cs`; surviving-class evidence and remaining-member inventories read directly from the same decompile; failure signatures quoted verbatim from `DedicatedServer/data/server.log` (lines 2350-2357, 2409-2415) and `DedicatedServer/install/BepInEx/LogOutput.log` (line 21) of the same day's boot. ModularConsoleMod breaking on the `LoadThing` removal is recorded from the boot-investigation session notes, not from these log files (its failure predates the current log rotation).

## Open questions

- Which installed mod triggered the `UnitTest_ConstructionValidate` AccessTools warning. The HarmonyX warning line does not identify the caller; finding it requires correlating the chainloader plugin order around LogOutput.log line 21 or grepping the installed plugin DLLs for the string.
