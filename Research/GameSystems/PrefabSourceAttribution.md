---
title: Prefab source attribution (which mod added a prefab)
type: GameSystems
created_in: 0.2.6228.27061
verified_in: 0.2.6228.27061
verified_at: 2026-06-21
sources:
  - rocketstation_Data/Managed/Assembly-CSharp.dll :: Assets.Scripts.Objects.Prefab (decompile .work/decomp/0.2.6228.27061/Assembly-CSharp.decompiled.cs:303810-303912)
  - rocketstation_Data/Managed/Assembly-CSharp.dll :: WorldManager.LoadGameDataAsync / LoadDataFiles (decompile lines 58982-59156)
  - rocketstation_Data/Managed/Assembly-CSharp.dll :: ThingImport ThingImporter.RegisterThing (decompile lines 116835-116844)
  - BepInEx/plugins/StationeersLaunchPad/LaunchPadBooster.dll :: LaunchPadBooster.Mod / PrefabPatch (decompile .work/decomp/0.2.6228.27061/LaunchPadBooster.decompiled.cs:99-242)
  - BepInEx/plugins/StationeersLaunchPad/StationeersLaunchPad.dll :: StationeersLaunchPad.DebugPackage (decompile .work/decomp/0.2.6228.27061/StationeersLaunchPad.decompiled.cs:763-815)
related:
  - ./RecipeDataLoading.md
  - ../Workflows/StationeersLaunchPadModLoading.md
  - ../Patterns/PrefabCloning.md
  - ./ModLoadSequence.md
tags: [prefab, launchpad]
---

# Prefab source attribution (which mod added a prefab)

How to determine, at runtime inside a BepInEx plugin, which mod contributed each `Thing` in `Prefab.AllPrefabs`. The short answer: the game keeps NO per-prefab provenance, the prefab-registration path flattens every source into one list before identity is observable, so single-pass per-mod attribution is only possible by reading the per-loader source registries that still hold mod identity at registration time, or by hooking those registration points to record `(prefab, mod)` as they fire.

## No provenance field on Thing
<!-- verified: 0.2.6228.27061 @ 2026-06-21 -->

The base `Thing` class carries only the prefab's own identity, not its origin. The only identity-like fields are:

```csharp
[Header("Thing")]
[ReadOnly]
public string PrefabName;

[ReadOnly]
public int PrefabHash;
```

There is no `SourceMod`, `ModId`, `ModName`, `AssetBundle`, or `SourcePath` field on `Thing` (grep of the `Thing` class region returned none). `PrefabHash` is `Animator.StringToHash(PrefabName)` (enforced in `RegisterExisting`, see below). So a prefab cannot be attributed by reading any field on the prefab itself.

There is a `Thing.IsCustomThing` bool (set `true` at `ThingImporter` line 116830 for `CustomThingData`-defined things), but it only distinguishes XML-blueprint-defined things from everything else; it does not name the mod.

Mod prefab names are NOT conventionally prefixed. Some mods prefix (`ReVolt...`) but most reuse vanilla-style names (`StructureX`, `ItemY`), and a mod can deliberately replace a vanilla prefab by reusing its exact name/hash. Name-prefix attribution is unreliable and must not be used as the primary mechanism.

## The two registries and the clone step
<!-- verified: 0.2.6228.27061 @ 2026-06-21 -->

`Prefab` holds two registries (Assembly-CSharp.decompiled.cs:303810-303812):

```csharp
public static readonly Dictionary<int, Thing> _allPrefabs = new Dictionary<int, Thing>();
public static readonly List<Thing> AllPrefabs = new List<Thing>();
```

`Prefab.AllPrefabs` (the list the caller wants to attribute) is populated only inside `Prefab.LoadAll()` (line 303822), which iterates `WorldManager.Instance.SourcePrefabs` and clones each into the `~Prefabs` holder via `Register` -> `RegisterExisting`:

```csharp
foreach (Thing sourcePrefab in WorldManager.Instance.SourcePrefabs)   // line 303851
{
    if (!(sourcePrefab == null))
    {
        try { Register(sourcePrefab); ... }
        ...
    }
}
```

```csharp
private static void Register(Thing sourcePrefab)                       // line 303895
{
    Thing thing = UnityEngine.Object.Instantiate(sourcePrefab, PrefabsGameObject.transform);
    thing.name = sourcePrefab.name;
    thing.ThingTransformPosition = Vector3.zero;
    thing.ThingTransform.rotation = Quaternion.identity;
    RegisterExisting(thing);
}

public static void RegisterExisting(Thing prefab)                      // line 303904
{
    if (prefab.PrefabHash != Animator.StringToHash(prefab.PrefabName))
        UnityEngine.Debug.LogError(prefab.PrefabName + " Has incorrect prefab hash!");
    _allPrefabs.Add(prefab.PrefabHash, prefab);
    prefab.CacheStates();
    AllPrefabs.Add(prefab);
    ...
}
```

Critical consequence for attribution: by the time a `Thing` is in `AllPrefabs`, it is a fresh `Instantiate` clone of the source prefab, registered from one flat `SourcePrefabs` list. Neither `Register`, `RegisterExisting`, nor the `LoadAll` loop has any `ModInfo` / `ModData` / mod identity in scope. The mod that contributed the prefab is not knowable from inside this code path. The `AllPrefabs` entry maps back to a `SourcePrefabs` entry only by `PrefabHash` / name identity, not by a stored link.

`WorldManager.SourcePrefabs` itself is a plain flat list with no per-entry origin (line 58763):

```csharp
public List<Thing> SourcePrefabs = new List<Thing>();
```

## How SourcePrefabs gets populated, and where mod identity still exists
<!-- verified: 0.2.6228.27061 @ 2026-06-21 -->

`SourcePrefabs` is filled from three distinct sources before/around `Prefab.LoadAll`. Mod identity is present only in the per-loader registries each source maintains, NOT in `SourcePrefabs`.

1. **Vanilla base prefabs.** The ~3000 shipped prefabs are loaded into `SourcePrefabs` by the engine's startup Resources-load path (before any mod runs). These are "vanilla" by definition.

2. **LaunchPadBooster asset-bundle prefabs.** A mod calls `LaunchPadBooster.Mod.AddPrefabs(...)`, which appends to that `Mod` instance's own `Prefabs` list (LaunchPadBooster.decompiled.cs:161-173):

   ```csharp
   public void AddPrefabs(IEnumerable<GameObject> prefabs)
   {
       List<Thing> list = (from prefab in prefabs
           select prefab.GetComponent<Thing>() into thing
           where (Object)(object)thing != (Object)null
           select thing).ToList();
       if (list.Count != 0)
       {
           (Networking as ModNetworking).HasPrefabs = true;
           PrefabPatch.Initialize();
           Prefabs.AddRange(list);
       }
   }
   ```

   These per-mod lists are flushed into `SourcePrefabs` by a Harmony **prefix on `Prefab.LoadAll`** (LaunchPadBooster.decompiled.cs:216-241):

   ```csharp
   [HarmonyPrefix]
   private static void PatchPrefabs()
   {
       foreach (Mod allMod in Mod.AllMods)
       {
           foreach (Thing prefab in allMod.Prefabs)
           {
               if (!WorldManager.Instance.SourcePrefabs.Contains(prefab))
               {
                   WorldManager.Instance.SourcePrefabs.Add(prefab);
                   Debug.Log((object)("Add prefab to WorldManager: " + ((Object)prefab).name));
               }
               ...
           }
       }
       ...
   }
   ```

   The mod identity for every prefab added this way is preserved permanently in `LaunchPadBooster.Mod.AllMods` (LaunchPadBooster.decompiled.cs:103-111):

   ```csharp
   public static readonly List<Mod> AllMods = new List<Mod>();
   internal static readonly Dictionary<int, Mod> ModsByHash = new Dictionary<int, Mod>();
   public readonly ModID ID;            // ModID { string Name; string Version; }
   internal readonly List<Thing> Prefabs = new List<Thing>();
   ```

   So for any LaunchPadBooster-registered prefab, `Mod.AllMods.First(m => m.Prefabs.Contains(prefab-or-its-source)).ID.Name` is the source mod name. The same instances are added to `SourcePrefabs`, but `AllPrefabs` holds clones, so the join back to `Mod.Prefabs` must be by `PrefabHash` / name, not reference.

3. **CustomThingData (GameData XML blueprint) prefabs.** GameData XML `<CustomThingData>` entries (seeds/plants today) are turned into prefabs by `ThingImporter` during `LoadDataFiles` (after `LoadAll`, see ordering below). `ThingImporter.RegisterThing` registers them directly into BOTH registries (Assembly-CSharp.decompiled.cs:116835-116844):

   ```csharp
   private void RegisterThing(Thing thing)
   {
       Prefab.RegisterExisting(thing);
       WorldManager.Instance.SourcePrefabs.Add(thing);
       InventoryManager.DynamicThingPrefabs.Add(thing.PrefabName);
       ...
   }
   ```

   Note this is the only in-engine `SourcePrefabs.Add` call (line 116838); everything else is engine-internal Resources loading or the Booster prefix. The owning mod is in scope at the GameData loop level (the `LoadDataFiles` per-mod loop, see below), not inside `RegisterThing`.

There is no `Dictionary<Thing, ModInfo>`, `Dictionary<prefabHash, ModData>`, or any prefab-keyed mod-valued map anywhere in Assembly-CSharp, StationeersLaunchPad, or LaunchPadBooster. Attribution data lives only in the per-loader source registries above.

## The load ordering that matters
<!-- verified: 0.2.6228.27061 @ 2026-06-21 -->

`WorldManager.LoadGameDataAsync` (line 58982) runs prefab loading first, then GameData XML:

```csharp
public async UniTask LoadGameDataAsync()
{
    ...
    await Prefab.LoadAll();        // line 58987  -> AllPrefabs populated; Booster prefix flushes its prefabs
    ...
    BeforeLoadDataFiles();         // line 59035
    LoadDataFiles();               // line 59036  -> per-mod GameData XML (recipes + CustomThingData seeds)
    AfterLoadDataFiles();          // line 59037
    ...
    OnGameDataLoaded?.Invoke();    // line 59106
}
```

`LoadDataFiles` (line 59145) is the one place the game iterates mods with the `ModData` in scope:

```csharp
private static void LoadDataFiles()
{
    LoadDataFilesAtPath(Path.Combine(Application.streamingAssetsPath, "Worlds"));
    LoadDataFilesAtPath(Path.Combine(Application.streamingAssetsPath, "Data"));
    foreach (ModData mod in WorkshopMenu.ModsConfig.Mods)
    {
        if (mod.Enabled && !(mod is CoreModData))
            LoadDataFilesAtPath(Path.Combine(mod.DirectoryPath, "GameData"));
    }
}
```

This loop knows `mod` (a `ModData` with `DirectoryPath`). Inside, `LoadXmlFileData` re-derives a `ModAbout` per file via `ModAbout.Load(xmlFile)` and threads it into recipe/difficulty/visualiser registration (see RecipeDataLoading.md). But `CustomThingData` prefab creation happens in the FIRST pass (`LoadXmlFileDataFirstPass` -> `LoadCustomThingData` -> `RegisterPrefab`), and that first-pass path does NOT receive the `ModAbout` (only the main pass does). So even for XML-defined prefabs, the engine does not record the owning mod on the prefab; it is only inferable from which mod's `GameData/` folder the XML file sat in (the `mod` loop variable / the file path).

Implication: there is no single hook with every prefab AND its mod in scope simultaneously. `Prefab.LoadAll` sees all asset-bundle prefabs but no mod identity. `LoadDataFiles` sees mod identity but only for XML-defined content, and only after `LoadAll`.

## What the StationeersLaunchPad DebugPackage already dumps
<!-- verified: 0.2.6228.27061 @ 2026-06-21 -->

StationeersLaunchPad's `DebugPackage.Export` (StationeersLaunchPad.decompiled.cs:767-815) already writes both prefab lists to a zip, with no attribution:

```csharp
archive.TryWriteEntry("sourceprefabs.txt", WritePrefabs(() => WorldManager.Instance?.SourcePrefabs));
archive.TryWriteEntry("prefabs.txt", WritePrefabs(() => Prefab.AllPrefabs));
```

These are flat name dumps. They confirm there is no built-in per-mod grouping; the DebugPackage authors had the same flat lists available and produced no attribution either. (`prefabs.txt` = `AllPrefabs`, the registered clones; `sourceprefabs.txt` = `SourcePrefabs`, the pre-clone sources. The two differ only by the clone step and any prefab that failed to register.)

## Attribution strategies (verdict)
<!-- verified: 0.2.6228.27061 @ 2026-06-21 -->

**Single-pass runtime per-mod attribution IS feasible** for the dominant case (asset-bundle prefabs registered through LaunchPadBooster), without any extra server starts, by reading `LaunchPadBooster.Mod.AllMods` after `OnPrefabsLoaded`. It is NOT feasible from a field on the prefab, and NOT complete for every possible registration route in a single read (a mod that bypasses LaunchPadBooster and pokes `SourcePrefabs` / `RegisterExisting` directly leaves no registry trace). Ranked options:

**(Primary) Read the per-loader source registries, no hook on the registration internals.** After `Prefab.OnPrefabsLoaded` (or any time after load), build the attribution map by joining `Prefab.AllPrefabs` to the source registries by `PrefabHash`:

- For each `m` in `LaunchPadBooster.Mod.AllMods`, every `t` in `m.Prefabs` attributes hash `t.PrefabHash` (== `Animator.StringToHash(t.PrefabName)`) to `m.ID.Name`. Reach it by reflection against `LaunchPadBooster.dll` type `LaunchPadBooster.Mod` (static `AllMods`), or reference the assembly directly since this repo already depends on LaunchPadBooster.
- For StationeersMods-style mods that do not use LaunchPadBooster, consult StationeersLaunchPad `ModLoader.LoadedMods` and each `LoadedMod.Prefabs` (`List<GameObject>`); each GameObject's `Thing` component (via `GetComponent<Thing>()`) gives `PrefabHash` -> attributes to that `LoadedMod` (see StationeersLaunchPadModLoading.md). Map `LoadedMod` -> name via `LoadedMod.Info` (a `ModInfo`): read `Info.Name` (forwards to `About.Name`). `LoadedMod` has NO `Def` / `Name` / `About` member of its own (runtime-confirmed field set: `Info`, `Assemblies`, `Prefabs`, `Exports`, `ContentHandler`, `Entrypoints`, `ConfigFiles`, `Logger`, status bools), so reading `LoadedMod.Def`/`Name`/`About` directly returns nothing and collapses every SLP mod into one unnamed bucket.
- Any `AllPrefabs` hash not claimed by either registry = "vanilla" (or a direct-injection mod that left no trace; treat as a residual bucket and log its count).

This is one pass, no game restarts, and names the specific mod for every prefab that came through the two supported loaders. It is the recommended approach.

**(Fallback c) Harmony hook to record `(prefab, mod)` at registration time.** If the residual "unattributed" bucket is non-empty and must be named, hook the registration points and capture identity as it flows:

- Prefix `LaunchPadBooster.PrefabPatch.PatchPrefabs` is unnecessary (its data is already in `Mod.AllMods`); instead, to catch direct injections, wrap `WorldManager.SourcePrefabs.Add` is not patchable (List.Add). The patchable seam is `Prefab.RegisterExisting(Thing prefab)` (Assembly-CSharp, line 303904) and/or `Prefab.LoadAll`. There is no "current mod" parameter, so the hook must derive it from the call stack: `StationeersLaunchPad.Metadata.ModLoader.TryGetExecutingMod(out LoadedMod mod)` (StationeersLaunchPad.decompiled.cs ~16342) walks `new StackTrace(...)` against the `AssemblyToMod` dictionary and returns the mod whose assembly is on the stack. This works only when the registering call is made from mod code still on the stack (true for direct `RegisterExisting` calls from a mod's `OnLoaded`; NOT true for the deferred Booster prefix, which runs from StationeersLaunchPad's own `Prefab.LoadAll` frame, hence why Booster prefabs must come from `Mod.AllMods` instead).
- Method signature to hook: `public static void Prefab.RegisterExisting(Thing prefab)`. In the patch, resolve the mod via `TryGetExecutingMod`, store `prefab.PrefabHash -> mod.Name` in a static dictionary, read it back when dumping.

**(Fallback a) Two-pass diff (no-mods vs all-mods).** Dump `AllPrefabs` names with zero mods, then with all mods; the set difference is "modded". Cheap (two starts) but does NOT say WHICH mod. Use only to validate the vanilla baseline set for the residual bucket.

**(Fallback b) Incremental cumulative dumps.** Load mods one at a time (or cumulative load-order prefixes), dump after each; new hashes attribute to the mod just added. Names the mod, but costs N server starts for N mods and is sensitive to load-order-dependent prefab replacement. Only worth it if both the registry read and the stack-trace hook fail to cover a particular mod.

Recommended: **Primary (registry read) + Fallback c only for the residual bucket.** This gives single-pass, per-mod attribution with no restarts for every prefab that came through LaunchPadBooster or the StationeersLaunchPad asset loader, and a stack-trace-based catch for direct injectors, with "vanilla" as the well-defined remainder.

## Verification history

- 2026-06-21: corrected the StationeersLaunchPad attribution name source from `LoadedMod.Def.About.Name` to `LoadedMod.Info.Name` (`LoadedMod` carries its `ModInfo` in an `Info` field and has no `Def`/`Name`/`About` member of its own). Verified empirically: a full-mod-set runtime dump (ScenarioRunner `device-port-dump` on the dedicated server, 61 active mods, game 0.2.6228.27061) initially collapsed every StationeersLaunchPad-loaded mod into one `(slp-unnamed)` bucket using the old recipe; switching to `LoadedMod.Info.Name` named all of them. All 39 mod-introduced power/data device prefabs attributed to named mods (LaunchPadBooster `Mod.AllMods` + StationeersLaunchPad `LoadedMods[].Info.Name`), with an empty direct-injection residual for this mod set.
- 2026-06-21: page created from a fresh read of Assembly-CSharp, StationeersLaunchPad, and LaunchPadBooster decompiles at game version 0.2.6228.27061. Established: no provenance field on `Thing`; `AllPrefabs` populated by cloning a flat `SourcePrefabs` with no mod identity in scope (`Prefab.Register` / `RegisterExisting`, lines 303895-303912); `Prefab.LoadAll` (line 58987) runs before `LoadDataFiles` (line 59036); mod identity persists only in `LaunchPadBooster.Mod.AllMods` (line 103) and StationeersLaunchPad `ModLoader.LoadedMods`; the only in-engine `SourcePrefabs.Add` is `ThingImporter.RegisterThing` (line 116838) for `CustomThingData`. No prefab-keyed mod-valued dictionary exists anywhere. DebugPackage already dumps both flat lists with no attribution (lines 792-793).

## Open questions

- Exact engine call site that loads the ~3000 vanilla base prefabs into `SourcePrefabs` (the pre-mod Resources-load path) was not pinned to a line in this pass; it is upstream of `Prefab.LoadAll` and not needed for attribution (vanilla is the residual bucket), but a future pass could cite it for completeness.
- Whether any shipped StationeersMods-only mod (no LaunchPadBooster dependency) registers prefabs by a route that leaves neither a `Mod.AllMods` nor a `LoadedMod.Prefabs` trace (e.g. directly calling `Prefab.RegisterExisting` from a non-mod-stack context). If such a mod exists, only the stack-trace hook or the incremental-dump fallback can attribute it. Confirm empirically against the actual mod set when the dump runs. (2026-06-21: confirmed empirically for the current 61-mod active set: the registry read named every mod-introduced power/data device, with no untraceable residual. The three `[StationeersMods]`-loader mods present, FilterCleanerMod / VeryImportantButtonMod / BowlingMod, all traced via `ModLoader.LoadedMods`.)
