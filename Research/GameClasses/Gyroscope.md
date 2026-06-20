---
title: Gyroscope (StructureGyroscope) -- empty stub, not buildable
type: GameClasses
created_in: 0.2.6228.27061
verified_in: 0.2.6228.27061
verified_at: 2026-06-20
sources:
  - .work/decomp/0.2.6228.27061/Assembly-CSharp.decompiled.cs :: Assets.Scripts.Objects.Electrical.Gyroscope (lines 376045-376047)
  - .work/decomp/0.2.6228.27061/Assembly-CSharp.decompiled.cs :: Assets.Scripts.Objects.Electrical.Electrical (lines 373728-373754)
  - rocketstation_DedicatedServer_Data/StreamingAssets/Language/english.xml :: RecordThing StructureGyroscope (lines 2306-2310)
related:
  - Device.md
  - Structure.md
tags: [prefab, dead-end]
---

# Gyroscope (StructureGyroscope): empty stub, not buildable

`Gyroscope` is the C# type behind the vanilla prefab `StructureGyroscope`. It is a leftover / placeholder prefab with no implementation: the class body is completely empty, there is no construction path (no kit, no `BuildStates` code reference, no Stationpedia data), and it carries only a lore flavor-text Stationpedia entry. A player cannot build it or place it through any normal or creative game flow. Skip it for anything that operates on real player-facing structures (for example, deciding what is worth making spray-paintable).

## Class definition (empty)
<!-- verified: 0.2.6228.27061 @ 2026-06-20 -->

The entire class body is empty. It declares no fields, no properties, no methods, and overrides nothing:

```csharp
public class Gyroscope : Electrical
{
}
```

(`.work/decomp/0.2.6228.27061/Assembly-CSharp.decompiled.cs`, lines 376045-376047.)

Because the body is empty, all behavior is inherited from `Electrical`. There is no per-tick logic, no attitude/rotation code, no rocket interaction, no custom logic types, nothing gyroscope-specific anywhere in the assembly. The class name is the only thing that ties it to the "gyroscope" concept; functionally it is a bare powered device shell.

## Base class: Electrical
<!-- verified: 0.2.6228.27061 @ 2026-06-20 -->

`Electrical` is itself a thin base, a smart-rotatable powered `Device`:

```csharp
public class Electrical : Device, ISmartRotatable, IPowered, IDensePoolable, IReferencable, IEvaluable
{
    [Header("ISmartRotation")]
    public SmartRotate.ConnectionType ConnectionType = SmartRotate.ConnectionType.Exhaustive;

    public int[] OpenEndsPermutation = new int[6] { 0, 1, 2, 3, 4, 5 };

    // GetConnectionType / SetOpenEndsPermutation / SetConnectionType / GetOpenEndsPermutation
}
```

(`.work/decomp/0.2.6228.27061/Assembly-CSharp.decompiled.cs`, lines 373728-373754.) `Electrical` adds only smart-rotation connection plumbing on top of `Device`; it is the generic "a powered device that snaps to a cable network" base, not anything rocket- or attitude-specific. So whatever `Gyroscope` would have been, the inherited surface gives it nothing beyond a generic powered device. (`Electrical` is distinct from `ElectricalInputOutput` at line 373755, which is the pass-through power-bridge base; `Gyroscope` derives from plain `Electrical`.)

## Not buildable: no kit, no BuildStates code reference, no Stationpedia data
<!-- verified: 0.2.6228.27061 @ 2026-06-20 -->

Across the entire decompiled `Assembly-CSharp`, the strings `Gyroscope` and `StructureGyroscope` appear in exactly one place: the class definition itself (line 376045). There is:

- No construction-kit reference. No `ItemKit*` or `Constructor` / `MultiConstructor` lists `Gyroscope` among its constructed prefabs. The Stationpedia build-info pipeline derives "Constructed By Kits" from each construction kit's `GetConstructedPrefabs()` (`AddBuildStates` / kit loop around lines 231419-231432); nothing feeds `StructureGyroscope` into that list. A leftover `ItemKitGyroscope` ("Kit (Gyroscope)") entry exists in `english.xml` (line 1466), but `ItemKitGyroscope` is itself absent from `Assembly-CSharp` and from every `StreamingAssets/Data` fabricator recipe (it appears only as a bare name in `paints.xml`'s prefab-name registry list), so it is a dangling localization string, not a working construction kit.
- No code reference to `Gyroscope.BuildStates`. The `BuildStates` machinery (`structure.BuildStates`, `UpdateBuildStateAndVisualizer`, the Stationpedia `AddBuildStates`) is generic and never names this prefab. Whether the prefab asset itself carries `BuildStates` cannot be confirmed from the decompile alone (build states are authored on the Unity prefab), but with no kit pointing at it, even a populated `BuildStates` list would be unreachable in normal play.
- No Stationpedia record. The community Stationpedia dump (Emilgardis Stationeers-Wiki-Page-Helper `third_party/Stationpedia.json`, version 0.2.5906.26015) has no `StructureGyroscope` entry; therefore no `ConstructedByKits` for it. (The WebFetch of that file truncates the large JSON, so this is corroborating, not primary, evidence; the decompile is the primary evidence.)

In Stationeers, a structure is player-buildable only if a construction kit's `GetConstructedPrefabs()` includes it (kit places the first build state, the multitool advances states). With no kit referencing `StructureGyroscope`, there is no build path. It would only be reachable via a developer / debug raw-prefab spawn, not the normal build menu and not the standard creative menu DynamicThing flow.

## In-game name and description (lore flavor only)
<!-- verified: 0.2.6228.27061 @ 2026-06-20 -->

From `english.xml` (lines 2306-2310):

```xml
<RecordThing>
  <Key>StructureGyroscope</Key>
  <Value>Gyroscope</Value>
  <Description>An element of an ancient class of spaceship, the gyroscope allowed these vehicles to rotate in at least one of the universe's most popular directions.</Description>
</RecordThing>
```

- Display name: **Gyroscope**.
- Description: pure lore/joke flavor text ("an ancient class of spaceship... rotate in at least one of the universe's most popular directions"). It gives no build instructions, no kit name, no usage, and no "work in progress" note. The string in `english.xml` is the only localized content; the other language files match only on the unrelated substring "gyroscope" inside other words, not on a `StructureGyroscope` key (confirmed: english.xml is the only language file with a `StructureGyroscope` record).

## Bottom line
<!-- verified: 0.2.6228.27061 @ 2026-06-20 -->

`StructureGyroscope` is a legacy / placeholder prefab, not a real player-facing structure. The class is an empty `Electrical` shell with zero implementation, no construction kit builds it, and it has only flavor-text localization. A player cannot place it or see it in normal play. Treat it as inaccessible and skip it for any feature that targets buildable structures (spray-paint support, etc.).

## Verification history
<!-- verified: 0.2.6228.27061 @ 2026-06-20 -->

- 2026-06-20: page created. `Gyroscope : Electrical` confirmed as an empty class body (decompile lines 376045-376047); `Electrical` base read in full (lines 373728-373754); english.xml `StructureGyroscope` record read (lines 2306-2310); no kit / BuildStates / Stationpedia reference found anywhere in `Assembly-CSharp`. Conclusion: not buildable, not accessible. Additive page, no prior content contradicted.
- 2026-06-20: independently cross-verified during a SprayPaintPlus paintability follow-up (decompile `Gyroscope` hit only at line 376045; `StructureGyroscope` unpaintable in the scan). Added the dangling `ItemKitGyroscope` note to "Not buildable" (english.xml line 1466; the kit string is unreferenced in `Assembly-CSharp` and has no fabricator recipe, only a bare entry in `paints.xml`). Conclusion unchanged.

## Open questions
<!-- verified: 0.2.6228.27061 @ 2026-06-20 -->

- Whether the Unity prefab asset for `StructureGyroscope` carries any authored `BuildStates` or a mesh is not determinable from the decompiled C# alone. It is moot for buildability (no kit references it), but a future check against the prefab asset or an in-game raw spawn could confirm whether a model even exists.
