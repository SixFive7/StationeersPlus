---
title: Prefab cloning (Mirrored Devices template)
type: Patterns
created_in: 0.2.6228.27061
verified_in: 0.2.6228.27061
verified_at: 2026-04-20
sources:
  - Plans/RepairPrototype/plan.md:479-484 (F0225)
  - Plans/RepairPrototype/plan.md:487-495 (F0226)
  - Plans/RepairPrototype/plan.md:521-545 (F0227)
  - Plans/RepairPrototype/plan.md:612-620 (F0228)
  - Plans/RepairPrototype/plan.md:452-459 (F0229h)
  - Plans/RepairPrototype/plan.md:497-514 (F0229n)
  - Plans/RepairPrototype/plan.md:547-560 (F0229o)
  - Plans/RepairPrototype/plan.md:583-610 (F0229p)
related:
  - ../GameSystems/Localization.md
tags: [prefab, harmony, save-load]
---

# Prefab cloning (Mirrored Devices template)

Full recipe for cloning a vanilla prefab, registering it with the game's prefab system, wiring multi-constructor kits, and preserving multiplayer/save-load compatibility. Lifted verbatim from RepairPrototype's reverse-engineering of the Mirrored Devices mod.

## Problem
<!-- verified: 0.2.6228.27061 @ 2026-04-20 -->

A mod that wants to add a new device whose behavior matches an existing one does NOT need to hand-build the prefab. Clone the vanilla prefab, override identity (name, hash), register with `WorldManager.SourcePrefabs`, and the game treats the clone as any other device. Multiplayer save/load "just works" because the clone shares all original components.

The recipe has three Harmony entry points, each serving a distinct phase of the prefab lifecycle, plus helpers for constructor conversion, localization, and crafting recipes.

## Solution / recipe
<!-- verified: 0.2.6228.27061 @ 2026-04-20 -->

### Three Harmony patches

F0225 (Plans/RepairPrototype/plan.md:479-484):

1. **`Prefab.LoadAll` Prefix** (line 499): Runs before game loads prefabs. This is where cloning happens.
2. **`Localization.LanguageFolder.LoadAll` Prefix** (line 828): Registers display names.
3. **`InventoryManager.SetupConstructionCursors` Postfix** (line 455): Fixes placement cursor arrows.

### HiddenParent setup

F0226 (Plans/RepairPrototype/plan.md:487-495):

```csharp
// Static field (lives for entire game session)
private static readonly GameObject HiddenParent = new GameObject("~HiddenGameObject");

// In Prefab.LoadAll prefix:
UnityEngine.Object.DontDestroyOnLoad(HiddenParent.gameObject);
HiddenParent.SetActive(value: false);
```

The hidden parent prevents clones from being visible. `DontDestroyOnLoad` survives scene loads.

### CreateMirroredThing core

F0227 (Plans/RepairPrototype/plan.md:521-545):

```csharp
// (1) Clone the entire GameObject hierarchy
GameObject mirroredGameObject = GameObject.Instantiate(source, HiddenParent.transform);
mirroredGameObject.name = mirrorDef.mirrorName;

// (2) Override identity
Thing mirroredThing = mirroredGameObject.GetComponent<Thing>();
mirroredThing.PrefabName = mirrorDef.mirrorName;
mirroredThing.PrefabHash = mirrorDef.mirrorHash;

// (3) [Mirror-specific: flip transform -- NOT NEEDED for repair mod]
FlipTransform(mirroredGameObject.transform);

// (4) Clone and process blueprint (placement ghost)
if (mirroredThing.Blueprint != null)
{
    mirroredThing.Blueprint = GameObject.Instantiate(mirroredThing.Blueprint, HiddenParent.transform);
    FlipTransform(mirroredThing.Blueprint.transform);
    Wireframe blueprintWireframe = mirroredThing.Blueprint.GetComponent<Wireframe>();
    FlipWireframe(blueprintWireframe);
}

// (5) REGISTER with the game
WorldManager.Instance.SourcePrefabs.Add(mirroredThing);
```

### FindMirrorInfos + ConvertConstructorToMultiConstructor

F0229n (Plans/RepairPrototype/plan.md:497-514):

> `FindMirrorInfos()` scans `WorldManager.Instance.SourcePrefabs`: if prefab is `MultiConstructor` with matching `Constructable`, store constructor reference; if plain `Constructor`, queue for upgrade; if matches by name, store as `deviceToMirror`. Walks all prefabs' `BuildStates` to find `Tool.ToolEntry`/`ToolEntry2` references pointing at old Constructor. `ConvertConstructorToMultiConstructor` (line 526): `var mctor = ctor.gameObject.AddComponent<MultiConstructor>(); mctor.Constructables = new List<Structure>() { buildStructure }; CopySharedFields((Stackable)ctor, (Stackable)mctor); WorldManager.Instance.SourcePrefabs[prefabIndex] = mctor; UnityEngine.Object.DestroyImmediate(ctor);` `CopySharedFields` uses reflection to copy every field from Stackable up through Item/DynamicThing/Thing, with special handling for self-references and IList<Interactable> with Parent refs pointing to source.

### AddToConstructor + MirrorDefinition class

F0229o (Plans/RepairPrototype/plan.md:547-560):

> `AddToConstructor`: `int insertIndex = mirrorDef.constructor.Constructables.FindIndex(p => p.name == mirrorDef.deviceName); mirrorDef.constructor.Constructables.Insert(insertIndex + 1, mirroredDevice as Structure);` Inserts clone right after original in kit's constructable list. `MirrorDefinition` class: deviceName, mirrorName (= deviceName + "Mirrored"), mirrorHash (= `Animator.StringToHash(mirrorName)`), mirrorDescription, connectionsToFlip, deviceToMirror, constructor, postfix delegate. Per-device postfix delegate runs device-specific tweaks.

### Localization + free crafting recipes

F0229p (Plans/RepairPrototype/plan.md:583-610):

> Localization registration via `[HarmonyPatch(typeof(Localization.LanguageFolder), nameof(Localization.LanguageFolder.LoadAll))]` Prefix: finds original name in `__instance.LanguagePages[0].Things`, then adds `new Localization.RecordThing { Key = mirrorDef.mirrorName, Value = originalName + " (Mirrored)", ThingDescription = mirrorDef.mirrorDescription }`. No XML needed. Crafting recipes FREE: `GameObject.Instantiate()` copies entire `BuildStates` chain. Clone inherits exact same recipe as original. No XML, no recipe code needed.

### Save-load stability

F0229h (Plans/RepairPrototype/plan.md:452-459):

> Stationeers saves use `PrefabHash` (integer) to identify things. `Animator.StringToHash(name)` is deterministic - same name always produces same hash. Cloned prefabs with stable names survive save/load automatically. If mod removed, things with unknown PrefabHash fail to load (silently disappear or cause errors). No custom save data types needed if clone doesn't add new state fields.

## Key takeaways
<!-- verified: 0.2.6228.27061 @ 2026-04-20 -->

F0228 (Plans/RepairPrototype/plan.md:612-620):

1. The actual cloning is ~30 lines. Everything else is bookkeeping.
2. `Prefab.LoadAll` prefix is the universal hook for prefab manipulation.
3. `Animator.StringToHash(name)` is the hash function; deterministic, stable across saves.
4. Hidden parent with `DontDestroyOnLoad` + inactive is essential.
5. The Constructor/MultiConstructor distinction is the trickiest bookkeeping.
6. Multiplayer and save/load "just work" because the clone shares all original components.
7. This breaks the moment you add custom state or behavior; then all clients need the mod.

## Cited verifications
<!-- verified: 0.2.6228.27061 @ 2026-04-20 -->

Eight findings, all from RepairPrototype's plan, fit together as one template:

| Finding | Subject |
|---|---|
| F0225 | Three Harmony patches (Prefab.LoadAll, Localization.LoadAll, SetupConstructionCursors) |
| F0226 | HiddenParent + DontDestroyOnLoad setup |
| F0227 | CreateMirroredThing core (Instantiate + rename + register) |
| F0228 | Key-takeaways summary |
| F0229h | PrefabHash stability via Animator.StringToHash |
| F0229n | FindMirrorInfos + ConvertConstructorToMultiConstructor + CopySharedFields |
| F0229o | AddToConstructor + MirrorDefinition |
| F0229p | Localization registration + free crafting recipes |

## Verification history
<!-- verified: 0.2.6228.27061 @ 2026-04-20 -->

- 2026-04-20: page created from the Research migration; eight findings combined verbatim into one coherent template.

## Open questions

None at creation.
