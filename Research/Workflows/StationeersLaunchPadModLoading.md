---
title: StationeersLaunchPad mod loading pipeline
type: Workflows
created_in: 0.2.6228.27061
verified_in: 0.2.6403.27689
verified_at: 2026-07-02
sources:
  - BepInEx/plugins/StationeersLaunchPad/StationeersLaunchPad.dll :: StationeersLaunchPad.Metadata.ModInfo / LoadedMod / ModLoader (decompile .work/decomp/0.2.6228.27061/StationeersLaunchPad.decompiled.cs:13287-16400)
  - BepInEx/plugins/StationeersLaunchPad/StationeersLaunchPad.dll :: PrefabEntrypoint / ModBehaviourEntrypoint (decompile lines 16892-16935)
  - BepInEx/plugins/StationeersLaunchPad/LaunchPadBooster.dll :: LaunchPadBooster.Mod / PrefabPatch (decompile .work/decomp/0.2.6228.27061/LaunchPadBooster.decompiled.cs:99-242)
  - StationeersLaunchPad 0.4.0 (dedicated-server shipped DLL) :: ModLoader / LoadedMod / StationeersLaunchPad.Entrypoints (decompile .work/decomp/0.2.6403.27689/StationeersLaunchPad.decompiled.cs:16791, 18552, 18645-18680, 19067-19082; cache folder keyed by game version, assembly version 0.4.0 per lines 74 / 3627 / 4123)
related:
  - ../GameSystems/PrefabSourceAttribution.md
  - ../GameSystems/ModLoadSequence.md
  - ../GameSystems/ModDeduplication.md
tags: [launchpad, prefab]
---

# StationeersLaunchPad mod loading pipeline

How StationeersLaunchPad turns an enabled mod into loaded assemblies, prefabs, and a fired entrypoint, and where (if anywhere) the identity of the mod that contributed a given prefab is retained. The companion page PrefabSourceAttribution.md uses this to answer "which mod added this prefab".

Line numbers below come from the 0.2.6228.27061 decompile. Several registration sites sit inside async state machines, so the cited lines are the compiler-generated `MoveNext` bodies, not hand-written source; the surrounding method names are the stable handles.

## ModInfo: the config-phase mod record
<!-- verified: 0.2.6228.27061 @ 2026-06-21 -->

`ModInfo` (StationeersLaunchPad.decompiled.cs:13287-13365) is the metadata-phase representation built from a mod's `About.xml`. Relevant members:

```csharp
public readonly ModDefinition Def;
public readonly List<string> Assemblies = new List<string>();
public readonly List<string> AssetBundles = new List<string>();
public bool Enabled;

public ModAboutEx About => Def.About;
public ModSourceType Source => Def.Type;
public string Name => About.Name;
public string DirectoryPath => Def.DirectoryPath;
public string DirectoryName => new DirectoryInfo(DirectoryPath).Name;
public ulong WorkshopHandle => Def.WorkshopHandle;
public string ModID => About.ModID ?? "";
```

`Assemblies` and `AssetBundles` are file-path lists (the `.dll` and `.assets` files discovered under the mod directory). `ModInfo` does NOT track which prefabs an asset bundle contains; that is resolved later, at runtime, into `LoadedMod.Prefabs`.

## LoadedMod: the runtime mod record
<!-- verified: 0.2.6228.27061 @ 2026-06-21 -->

`LoadedMod` (StationeersLaunchPad.decompiled.cs:14532-14712) is instantiated once per enabled mod and holds the runtime collections:

```csharp
public ModInfo Info;                                    // the config-phase record; carries the name
public List<Assembly> Assemblies = new List<Assembly>();
public List<GameObject> Prefabs = new List<GameObject>();
public List<ExportSettings> Exports = new List<ExportSettings>();
public ContentHandler ContentHandler;
```

The `ContentHandler` is created in the constructor with a read-only view of this mod's `Prefabs` list (line 14569):

```csharp
ContentHandler = new ContentHandler(mod, new List<IResource>().AsReadOnly(), Prefabs.AsReadOnly());
```

So once asset loading has populated `Prefabs`, the mod's own `OnLoaded(ContentHandler)` can enumerate exactly the prefabs that mod shipped. `ContentHandler` itself is defined in the main game assembly, not in StationeersLaunchPad.

`LoadedMod.Info` is this mod's `ModInfo` (the config-phase record above), and it is the StationeersLaunchPad-side attribution key. `LoadedMod` has NO `Def` / `Name` / `About` member of its own; to get the mod name from a `LoadedMod`, read `LoadedMod.Info.Name` (which forwards to `About.Name`). The full instance field set, confirmed at runtime via a reflection member dump (game 0.2.6228.27061, `StationeersLaunchPad.Loading.LoadedMod`), is: `_lock`, `Info` (`ModInfo`), `Logger`, `Assemblies`, `Prefabs`, `Exports`, `ContentHandler`, `Entrypoints`, `ConfigFiles`, and the `LoadedAssemblies` / `LoadedAssets` / `LoadedEntryPoints` / `LoadFinished` / `LoadFailed` / `_configDirty` status fields; no public properties. See PrefabSourceAttribution.md for the join-by-hash attribution recipe.

## ModLoader: the global registries
<!-- verified: 0.2.6228.27061 @ 2026-06-21 -->

`ModLoader` (StationeersLaunchPad.decompiled.cs:16000, 16328-16332) holds the static cross-mod registries:

```csharp
public static readonly List<LoadedMod> LoadedMods = new List<LoadedMod>();
private static readonly Dictionary<Assembly, LoadedMod> AssemblyToMod = new Dictionary<Assembly, LoadedMod>();
```

`LoadedMods` is the global "every mod that loaded" list (the StationeersLaunchPad-side analogue of `LaunchPadBooster.Mod.AllMods`). `AssemblyToMod` is populated during assembly loading via `ModLoader.RegisterAssembly(assembly, this)` and is what makes the stack-trace lookup below possible.

## The three loading phases
<!-- verified: 0.2.6228.27061 @ 2026-06-21 -->

StationeersLaunchPad loads mods in three async phases, each stopwatch-logged (the log strings "Assemblies loading...", "Asset loading...", "Loading entrypoints..." appear in the server/player log per StationeersLaunchPadDedicatedServer.md):

1. **Assemblies.** Each mod's `.dll` files are `Assembly.LoadFrom`-ed and `ModLoader.RegisterAssembly(assembly, mod)` records the assembly -> `LoadedMod` mapping in `AssemblyToMod`.

2. **Assets.** Each mod's asset bundles load and their `GameObject` contents are extracted. In `LoadAssetsSingle` (state machine around lines 14329-14460) the extracted list is appended to that mod's `Prefabs` under a lock (line 14439):

   ```csharp
   Monitor.Enter(<>s__6, ref <>s__7);
   <>4__this.Prefabs.AddRange(<prefabs>5__2);
   ```

   `<>4__this` is the `LoadedMod` instance, so the mod identity is in scope here. The bundle extraction is `bundle.LoadAllAssetsAsync<GameObject>()` (via `ModLoader.LoadBundleExportSettings` / `LoadAllBundleAssets`).

3. **Entrypoints.** Mods implementing `ModBehaviour` (StationeersMods) or BepInEx plugins are initialized. The `OnLoaded(ContentHandler)` callback fires at lines 16892-16935:

   ```csharp
   // PrefabEntrypoint.Initialize
   foreach (ModBehaviour modBehaviour in Behaviours)
   {
       modBehaviour.contentHandler = mod.ContentHandler;
       modBehaviour.OnLoaded(mod.ContentHandler);
   }

   // ModBehaviourEntrypoint.Initialize
   Instance.contentHandler = mod.ContentHandler;
   Instance.OnLoaded(mod.ContentHandler);
   ```

At the entrypoint phase, the mod's code runs and is responsible for actually registering its prefabs with the game (commonly by handing them to `LaunchPadBooster.Mod.AddPrefabs`, which defers the real registration to a `Prefab.LoadAll` prefix; see below and PrefabSourceAttribution.md).

## Where the live plugin instance lives: LoadedMod.Entrypoints, never Chainloader.PluginInfos
<!-- verified: 0.2.6403.27689 @ 2026-07-02 -->

Source: StationeersLaunchPad **0.4.0** (the dedicated server's shipped DLL; assembly version confirmed in-decompile at lines 74 `AssemblyFileVersion("0.4.0")`, 3627 `VERSION = "0.4.0"`, 4123 `[BepInPlugin("stationeers.launchpad", "StationeersLaunchPad", "0.4.0")]`), decompiled to `.work/decomp/0.2.6403.27689/StationeersLaunchPad.decompiled.cs` (cache folder keyed by GAME version per repo convention; the assembly is StationeersLaunchPad 0.4.0).

**A BepInEx-style plugin loaded THROUGH StationeersLaunchPad never appears in `BepInEx.Bootstrap.Chainloader.PluginInfos`.** A whole-decompile grep of StationeersLaunchPad 0.4.0 for `Chainloader` and `PluginInfos` returns zero hits: StationeersLaunchPad neither reads nor writes BepInEx's plugin registry. It instantiates a mod's `BaseUnityPlugin` type by raw `AddComponent`, bypassing the BepInEx chainloader entirely, so `Chainloader.PluginInfos` only ever contains plugins BepInEx itself loaded from `BepInEx/plugins`.

Where the live instance IS held, at 0.4.0 (entrypoint classes live in the `StationeersLaunchPad.Entrypoints` namespace):

```csharp
// StationeersLaunchPad.Loading.ModLoader (line 18552)
public static readonly List<LoadedMod> LoadedMods = new List<LoadedMod>();

// StationeersLaunchPad.Loading.LoadedMod (line 16791)
public List<ModEntrypoint> Entrypoints = new List<ModEntrypoint>();

// StationeersLaunchPad.Entrypoints (lines 19067-19082)
public abstract class ModEntrypoint
{
    public abstract string DebugName();
    public abstract void Instantiate(GameObject parent);
    public abstract void Initialize(LoadedMod mod);
    public abstract IEnumerable<ConfigFile> Configs();
}
public abstract class BehaviourEntrypoint<T>(Type type) : ModEntrypoint where T : MonoBehaviour
{
    public readonly Type Type = type;
    public T Instance;
}

// StationeersLaunchPad.Entrypoints.BepInExEntrypoint (lines 18647-18677)
public class BepInExEntrypoint : BehaviourEntrypoint<BaseUnityPlugin>
{
    public override void Instantiate(GameObject parent)
    {
        Instance = (BaseUnityPlugin)parent.AddComponent(Type);   // line 18663
    }
    public override void Initialize(LoadedMod mod) { }           // empty; BepInEx plugins get no OnLoaded
    public override IEnumerable<ConfigFile> Configs()
    {
        if (Instance.Config != null) yield return Instance.Config;
    }
}
```

`EntrypointSearch.FindBepInExEntrypoints` (line 18690 area) scans each mod assembly for `BaseUnityPlugin`-derived types to build these entries; `PrefabEntrypoint` (19083+) keeps the same `OnLoaded(ContentHandler)` initialize flow documented in "The three loading phases" above (the 0.4.0 restructure moved the entrypoint classes into their own namespace and introduced the `BehaviourEntrypoint<T>` base without changing that flow).

Lookup recipe for tooling: walk `StationeersLaunchPad.Loading.ModLoader.LoadedMods`, match the mod by `Info.Name` / `Info.ModID`, then scan its `Entrypoints` for a `BepInExEntrypoint` and read `.Instance` (typed `BaseUnityPlugin`; the concrete plugin type is `.Type`). Practical consequence: tooling that needs a live plugin instance (dev probes, diagnostics, cross-mod reflection) must fall back from `Chainloader.PluginInfos` to this registry whenever the target mod is loaded through StationeersLaunchPad's `data/mods` / Workshop path rather than sitting in `BepInEx/plugins`.

## No "current mod" static; identity sources
<!-- verified: 0.2.6228.27061 @ 2026-06-21 -->

There is NO static field such as `CurrentMod` / `LoadingMod` / `ActiveMod`. During loading, mod identity is available only as:

1. The `LoadedMod` loop variable inside the load-strategy loops (e.g. lines 14020-14022, iterating `ModLoader.LoadedMods`).
2. The `LoadedMod <>4__this` instance captured by each async state machine (e.g. `LoadAssetsSingle`, line 14337).
3. A runtime stack-trace lookup (lines ~16342-16365):

   ```csharp
   public static bool TryGetExecutingMod(out LoadedMod mod)
   {
       return TryGetStackTraceMod(new StackTrace(3), out mod);
   }
   ```

   `TryGetStackTraceMod` walks each frame's declaring assembly against `AssemblyToMod` and returns the first match. A Harmony patch on a registration method can call `ModLoader.TryGetExecutingMod` to learn which mod's code is currently on the stack, but only when the registering call is actually made from that mod's assembly (it fails for deferred registration that runs from a StationeersLaunchPad or game frame).

## LaunchPadBooster prefab path
<!-- verified: 0.2.6228.27061 @ 2026-06-21 -->

LaunchPadBooster is the common registration route. A mod constructs a `LaunchPadBooster.Mod` and calls `AddPrefabs`, which stores the prefabs on that `Mod` instance (LaunchPadBooster.decompiled.cs:161-173). The static `Mod.AllMods` (line 103) retains the per-mod prefab lists permanently:

```csharp
public static readonly List<Mod> AllMods = new List<Mod>();
internal readonly List<Thing> Prefabs = new List<Thing>();
public readonly ModID ID;   // struct ModID { string Name; string Version; }
```

A Harmony **prefix on `Prefab.LoadAll`** (`PrefabPatch.PatchPrefabs`, lines 216-241) flushes every `Mod.AllMods[].Prefabs` into `WorldManager.Instance.SourcePrefabs` just before the game clones the source list into `Prefab.AllPrefabs`. Because `Mod.AllMods` survives, this is the reliable place to read "which mod shipped this prefab" without any extra hook. See PrefabSourceAttribution.md for the join-by-hash recipe and the full ordering.

## Verification history

- 2026-07-02: added "Where the live plugin instance lives: LoadedMod.Entrypoints, never Chainloader.PluginInfos" from the StationeersLaunchPad 0.4.0 decompile (`.work/decomp/0.2.6403.27689/StationeersLaunchPad.decompiled.cs`; assembly version confirmed at lines 74 / 3627 / 4123). Facts: whole-decompile grep for `Chainloader` / `PluginInfos` returns zero hits, so StationeersLaunchPad-loaded plugins never register with BepInEx's chainloader; the live `BaseUnityPlugin` is held by `ModLoader.LoadedMods` (18552) -> `LoadedMod.Entrypoints` (`public List<ModEntrypoint>`, 16791) -> `BepInExEntrypoint : BehaviourEntrypoint<BaseUnityPlugin>` whose `Instantiate` does `Instance = (BaseUnityPlugin)parent.AddComponent(Type)` (18663); `BehaviourEntrypoint<T>` declares `public readonly Type Type` + `public T Instance` (19077-19082); `EntrypointSearch.FindBepInExEntrypoints` builds the entries (18690 area); `PrefabEntrypoint` (19083+) keeps the OnLoaded flow. Additive; the 0.2.6228-stamped sections were not re-read (the `LoadedMod` field list already included `Entrypoints` from the 2026-06-21 runtime dump, now pinned to a decompile line). Driving work: dedicated-server dev-probe tooling needing a live plugin instance for a mod loaded via the data/mods path.
- 2026-06-21: page created from a read of the StationeersLaunchPad and LaunchPadBooster decompiles at game version 0.2.6228.27061. Documents ModInfo / LoadedMod / ModLoader, the three load phases, the absence of a current-mod static (identity via loop variable, state-machine `this`, or `TryGetExecutingMod` stack walk), and the LaunchPadBooster `Mod.AllMods` + `PrefabPatch` route. Reframed and reformatted from an initial sub-agent draft to comply with Research conventions (repo-relative source citations, triple-backtick fences, valid related links).
- 2026-06-21: added `LoadedMod.Info` (the `ModInfo` carrying the mod name) to the LoadedMod section, plus the full runtime-confirmed instance field set, after a ScenarioRunner runtime reflection member dump (the `device-port-dump` LoadedMod diagnostic, game 0.2.6228.27061) showed `LoadedMod` exposes `Info:ModInfo` and has no `Def`/`Name`/`About` member of its own. This corrects the StationeersLaunchPad prefab-attribution name source: read `LoadedMod.Info.Name`, not `LoadedMod.Def.About.Name`. Additive correction; no prior stamped claim contradicted (the original LoadedMod member list was described as "the runtime collections", not exhaustive).

## Open questions

- Exact line of `ModLoader.RegisterAssembly` and the phase-log print sites were cited approximately (state-machine bodies). A future pass can pin them precisely if needed.
- `ContentHandler`'s game-side definition (it lives in Assembly-CSharp, not StationeersLaunchPad) and whether it exposes the owning mod beyond the prefab list was not chased here; not required for attribution since `Mod.AllMods` / `LoadedMods` already carry identity.
