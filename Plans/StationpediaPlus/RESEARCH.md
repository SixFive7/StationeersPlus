# Stationpedia Plus (Plans): Research Reference

Stationpedia Plus is an in-progress shared code library that every SixFive7 Stationeers mod uses to integrate with the in-game Stationpedia (the wiki system). It ships embedded into each consuming mod via ILRepack, not as a separately distributed BepInEx plugin. The first consumer is PowerTransmitterPlus; SprayPaintPlus, EquipmentPlus, and future SixFive7 mods follow the same recipe. First-time readers: mod-specific architecture and the four-kind page taxonomy live in Section 1; design decisions (including the ILRepack / structural-enforcement rationale) are Section 2; game internals the library depends on (Stationpedia core classes, UniversalPage flow, Stationpedia Ascended internals, ILRepack per-mod-copy pattern) live on the central pages pointed to from Section 5. Implementation strategy, task breakdown, test matrix, and file inventory live in the sibling `PLAN.md` and are out of scope here.

## Status

In-progress. Not yet graduated to `Mods/`. No Workshop handle. Scope at prototype stage:

- Four public helpers: `CategoryBuilder`, `ReferencePage`, `LogicTypePageBuilder`, `SpaBridge`.
- One runtime MonoBehaviour: `SixFive7LinkHandler` (click-only link routing).
- Internal plumbing: `SearchFilterPatch`, `SpaSearchFilter`, `TextElementFactory`, `HarmonyIdHelper`.
- Ships via ILRepack into each consuming mod's DLL; no standalone Workshop artifact.
- First consumer will be PowerTransmitterPlus (migration replaces the existing 66-line `StationpediaPatches.cs` stub).

## 1. Architecture

Mod identity:

| Field | Value |
|---|---|
| Display Name | Stationpedia Plus |
| Code Name | StationpediaPlus |
| Plugin GUID | (none: library only, no `[BepInPlugin]`) |
| Workshop ID | (none: not distributed separately) |
| Dependencies | StationeersLaunchPad (via consuming mod), Stationpedia Ascended optional (soft-detected) |

The library is a collection of static helper classes plus one `MonoBehaviour`. It has no `[BepInPlugin]` of its own; each consuming mod's own plugin is the BepInEx entry point. After ILRepack merges the library IL into the consuming mod's assembly, the consumer calls the library's helpers from its own `OnAllModsLoaded` callback.

### 1.1. Page taxonomy (four kinds)

Every piece of Stationpedia content maps to exactly one kind. The library's helpers are aligned to these kinds:

- **Kind A, New-thing pages.** Vanilla auto-generates a page per registered prefab, key `"Thing" + prefab.PrefabName`. Mod responsibility: author language XML, implement `CanLogicRead` / `Write`. Library responsibility: none.
- **Kind B, LogicType pages.** Vanilla auto-generates one page per value in `EnumCollections.LogicTypes.Values`, key `"LogicType" + enumName`. Mod calls `LogicTypePageBuilder.Register(spec)`. Library enriches the body with Summary / Formula / Related sections and auto-invokes `SpaBridge.TryEnrichLogicTooltips` for every device in `spec.RelatedDeviceKeys`.
- **Kind C, Extension sections.** For mods that modify vanilla prefabs (not add new ones). Vanilla page stays intact; the library injects one collapsible `StationpediaCategory` at the bottom. Mod calls `CategoryBuilder.Register(modName, deviceKeys, contentBuilder)`. Library postfixes `UniversalPage.ChangeDisplay` with `[HarmonyAfter("com.stationpediaascended.mod")]`, idempotently destroys and recreates a GameObject named `<ModName>Details` at sibling index 21, default collapsed.
- **Kind D, Shared-reference pages.** Content referenced from multiple pages. Keyed `<FullModName>_<Topic>` (e.g. `PowerTransmitterPlus_AutoAim`). Hidden from search and home-page listings; reachable only via `{LINK:...}`. Mod calls `ReferencePage.Register(topic, titleRaw, bodyMarkup)`. Library adds the key to an internal `HiddenKeys` HashSet and installs `SearchFilterPatch` on first call.

Full content placement matrix and per-helper lifecycle details live in `PLAN.md` §5 and §8.

### 1.2. Three primary Harmony hooks

Installed by the helpers lazily, one per hook, idempotent on repeat calls:

| Hook | Installed by | Purpose |
|---|---|---|
| `Stationpedia.PopulateLogicVariables` (Postfix) | `LogicTypePageBuilder` | Enrich per-LogicType page bodies |
| `UniversalPage.ChangeDisplay` (Postfix, `[HarmonyAfter(spa)]`) | `CategoryBuilder` | Inject collapsible extension sections on extended device pages |
| `StationpediaPage.IsRegexMatch(string, string)` (Postfix) | `ReferencePage` via internal `SearchFilterPatch` | Filter hidden pages out of search results |

Secondary hooks (installed on demand by `SearchFilterPatch` / `SpaSearchFilter`):

- `Stationpedia.PopulateGuideLoreContents` (Prefix) — filter hidden pages out of Guides / Lore home-page tabs.
- `Stationpedia.PopulateLists` (Postfix) — filter hidden pages out of the home-page category-listing dictionary.
- Stationpedia Ascended `ShouldHideFromSearch` (Postfix, soft-dependent) — keep hidden pages out of SPA's delayed re-injection.

### 1.3. Threading model

All library code runs on Unity's main thread. `Prefab.OnPrefabsLoaded` fires synchronously inside the game's main-thread loading sequence before `Stationpedia.Regenerate`, so every library entry point called from a consumer's `OnAllModsLoaded` is already main-thread. The library ships no dispatcher of its own. Consuming mods that have their own off-main-thread work (e.g. PowerTransmitterPlus's `PowerTick` ThreadPool writes) use their own `MainThreadDispatcher` for that work; the library does not depend on it.

### 1.4. Runtime support MonoBehaviour: SixFive7LinkHandler

A minimal `IPointerClickHandler` attached to every dynamically created TMP text element inside extension sections. Click-only. Routes `<link=...>` clicks to `Stationpedia.Instance.SetPage(linkID)`. The library ships its own handler rather than reusing vanilla `HelpLinkHandler` because the vanilla handler's `LateUpdate` references `WorldManager.IsGamePaused` (scene-state coupling); see Section 2 for rationale and Section 5 for the central page documenting the vanilla `LateUpdate` body.

### 1.5. Project layout

```
StationpediaPlus/
  StationpediaPlus.csproj
  src/
    CategoryBuilder.cs
    ReferencePage.cs
    LogicTypePageBuilder.cs
    SpaBridge.cs
    SixFive7LinkHandler.cs
    Internal/
      SearchFilterPatch.cs
      SpaSearchFilter.cs
      TextElementFactory.cs
      HarmonyIdHelper.cs
```

Namespaces: `StationpediaPlus` for public types, `StationpediaPlus.Internal` for plumbing. See `PLAN.md` §8.3 for the authoritative file inventory and §8.4 for the helper signatures.

## 2. Design decisions

### 2.1. Applied

- **Shipped via ILRepack, not as a separate Workshop subscription.** Players must not install any artifact beyond the consuming mod. Each mod ILRepacks the library IL into its final DLL at build time; every mod ends up with its own private copy of the library's types. Static state is per-mod; Harmony patches are per-mod. See cross-link below and the central `Research/Patterns/ILRepackPerModCopy.md` for the generalized pattern and tooling notes.

- **Structural enforcement of Stationpedia Ascended tooltip integration, not convention.** User explicitly rejected CLAUDE.md conventions / code-review discipline for Stationpedia Ascended tooltip integration. "CLAUDE.md rules are not deterministic (humans forget; reviews miss). Compiled code is deterministic." Chosen enforcement: `LogicTypePageBuilder.Register(spec)` invokes `SpaBridge.TryEnrichLogicTooltips` automatically for every device in `spec.RelatedDeviceKeys`. Mod authors cannot accidentally skip Stationpedia Ascended tooltip enrichment. `SpaBridge` remains public for edge-case direct use, but the common path does not require a second call. This decision is the mod-local half of F0219aa; the generalizable nugget (ILRepack pushes enforcement into the shared library rather than into conventions) is captured on the central page at `../../Research/Patterns/ILRepackPerModCopy.md` under "Structural enforcement over convention."

- **Per-mod-copy coordination happens at the game level, not via shared state.** Multiple SixFive7 mods in the same install each carry their own embedded library copy with independent static state and independent Harmony patches. The library must not try to build a shared registry across copies. Visible coordination (multi-mod sibling ordering on a device page, distinct `<ModName>Details` GameObject names, additive search filtering) is routed through game-level mechanisms: distinct names, sibling-index conventions, `CreatedCategories` cleanup lists, and multiple additive Harmony postfixes.

- **Custom `SixFive7LinkHandler` ships instead of reusing vanilla `HelpLinkHandler`.** Vanilla would give hover-color feedback for free, but its `LateUpdate` references `WorldManager.IsGamePaused`, risking NullReferenceException when Stationpedia opens from the main menu before world init. Click-only shipping trades hover color for reduced failure surface. Compensated by the mandatory click-phrasing rule on all `{LINK:...}` labels (see `PLAN.md` §11.3).

- **Four-kind page taxonomy (not three).** LogicType gets its own slot distinct from Extended-device because LogicType pages have their own generator (`PopulateLogicVariables`), own key prefix (`LogicType<Name>`), own rendering template, and are both navigation targets AND rows inside Extension sections. Surfacing the distinction to mod authors is worth the extra kind.

- **Reference page keys use the full mod name, not an abbreviation.** `PowerTransmitterPlus_AutoAim`, not `PTP_AutoAim`. Abbreviations risk collision with unrelated third-party mods by different authors (e.g. `PTP_` could conflict with "Power Tools Plus", "Precision Tracker Plus"). Full mod name is unambiguous across the Stationeers modding ecosystem. Aligns with the repo-wide no-abbreviations rule in the root `CLAUDE.md`.

- **Harmony instance IDs derived from the consuming mod's plugin GUID.** The library does not hardcode Harmony IDs. `HarmonyIdHelper.ForMod(helperName)` returns `<ConsumerPluginGuid>.stationpediaplus.<helperName>`. Two consuming mods' copies of the same helper never clash on identical Harmony IDs.

- **All patch bodies defensively wrapped.** Every Harmony patch body runs inside a try/catch, logs via `Debug.LogWarning` on exception, never rethrows, null-guards all public data access, and uses Unity fake-null patterns (`if (obj == null || !obj) return;`). A failure in one mod's embedded library copy must not crash another mod's embedded copy.

- **Imperative Harmony patching with `Prepare()` gating, not attribute-only.** Matches Stationpedia Ascended's own documented style and survives the same game-drift failure modes: missing target methods cause a silent no-op rather than a plugin load failure.

### 2.2. Rejected or deferred

- **Bundle the library DLL alongside each mod's plugin DLL (Decision 17 option A).** Rejected as fragile. The .NET assembly resolver would load one copy at runtime; version drift across mods would surface as hard-to-debug runtime mismatches.

- **Shared `BepInEx/plugins/StationpediaPlus/` location requiring a separate artifact (Decision 17 option B).** Rejected per user constraint: no separate install for players.

- **LogicInsert fallback when the four-part custom LogicType patch set fails (Decision 11).** Rejected. Root-cause the injection chain instead; do not paper over it with a fallback that makes divergence invisible.

- **ILRepack tooling choice between `ILRepack.Lib.MSBuild.Task` NuGet and standalone `ilrepack.exe` (Decision 17 / O1).** Deferred to the PowerTransmitterPlus integration commit; no existing SixFive7 mod uses ILRepack yet, so this choice sets the pattern for the monorepo.

- **HarmonyIdHelper mod-GUID discovery strategy (O2).** Deferred to implementation time. Two viable paths: discover from `Chainloader.PluginInfos` by caller-assembly lookup, or require each consuming mod to register its GUID once via `SetOwningModGuid`.

## 3. Harmony patches catalog

At prototype stage the library installs patches only when a consuming mod calls a helper's `Register` for the first time. All patches are imperative, gated, and idempotent. Until a consumer ships, there is no observable patching behaviour. The PLAN.md §8.7 through §8.11a documents the exact patch bodies and installation code; this section is deliberately short to avoid duplicating that material.

### 3.1. Planned installations (one per helper)

| Helper / source | Target | Type | Effect |
|---|---|---|---|
| `LogicTypePageBuilder` | `Stationpedia.PopulateLogicVariables` | Postfix | Builds and registers enriched LogicType page bodies; invokes `SpaBridge` per device. |
| `CategoryBuilder` | `UniversalPage.ChangeDisplay` | Postfix (after Stationpedia Ascended) | Injects `<ModName>Details` collapsible category at sibling index 21. |
| `SearchFilterPatch` (via `ReferencePage`) | `StationpediaPage.IsRegexMatch(string, string)` | Postfix | Forces `__result = false` for keys in `HiddenKeys`. |
| `SearchFilterPatch` (via `ReferencePage`) | `Stationpedia.PopulateGuideLoreContents` | Prefix | Rewrites the local `SPDAKeys` list, filtering out hidden keys. |
| `SearchFilterPatch` (via `ReferencePage`) | `Stationpedia.PopulateLists` | Postfix | Removes inserts whose `PageLink` is in `HiddenKeys` from the home-page category-listing dictionary. |
| `SpaSearchFilter` (internal, soft-dependent) | Stationpedia Ascended `ShouldHideFromSearch` | Postfix | ORs in our `HiddenKeys` HashSet; silent no-op when Stationpedia Ascended is absent. |
| `SpaBridge` | (none) | reflection only | Reflects into Stationpedia Ascended `DeviceDatabase` to enrich tooltips; no Harmony patches. |

## 4. Multiplayer and sync

Not applicable. The library touches only local Stationpedia state (page registration, UI GameObject construction, search filters). It has no network messages and no server / client split. Consuming mods that add multiplayer features (e.g. PowerTransmitterPlus's distance-cost sync, EquipmentPlus's `SetActiveSensorMessage`) handle their own networking independently; the library's Stationpedia integration runs identically on host and client.

## 5. Relevant central pages

### 5.1. GameClasses

- [../../Research/GameClasses/HelpLinkHandler.md](../../Research/GameClasses/HelpLinkHandler.md) - `LateUpdate` body and `WorldManager.IsGamePaused` coupling explain why we ship `SixFive7LinkHandler` instead of reusing vanilla.
- [../../Research/GameClasses/Stationpedia.md](../../Research/GameClasses/Stationpedia.md) - Singleton controller and `Register` replace semantics every helper relies on when publishing pages.
- [../../Research/GameClasses/StationpediaPage.md](../../Research/GameClasses/StationpediaPage.md) - `ParsePage` semantics and `IsRegexMatch` body are the patch lever `SearchFilterPatch` targets.
- [../../Research/GameClasses/UniversalPage.md](../../Research/GameClasses/UniversalPage.md) - `ChangeDisplay` flow and `CreatedCategories` cleanup list drive `CategoryBuilder`'s postfix.

### 5.2. GameSystems

- [../../Research/GameSystems/Localization.md](../../Research/GameSystems/Localization.md) - Vanilla transmitter / receiver body text that our Extension sections augment without replacing.
- [../../Research/GameSystems/ModLoadSequence.md](../../Research/GameSystems/ModLoadSequence.md) - `OnPrefabsLoaded` / `OnAllModsLoaded` main-thread timing relative to `Stationpedia.Regenerate` is why every helper runs safely from consumer callbacks.
- [../../Research/GameSystems/StationpediaAscendedInternals.md](../../Research/GameSystems/StationpediaAscendedInternals.md) - Stationpedia Ascended patch list, tooltip coroutine, resolution chain, and distinct-name destroy convention define our coordination surface.
- [../../Research/GameSystems/StationpediaMarkup.md](../../Research/GameSystems/StationpediaMarkup.md) - Token table for `{HEADER}`, `{LINK}`, `{LOGICTYPE}`, `{THING}` consumed by every helper that builds markup.
- [../../Research/GameSystems/StationpediaPageRendering.md](../../Research/GameSystems/StationpediaPageRendering.md) - `Regenerate` lifecycle and `PopulateLogicVariables` / `PopulateThingPages` timings determine when we can safely register.
- [../../Research/GameSystems/StationpediaSearch.md](../../Research/GameSystems/StationpediaSearch.md) - Search trigger chain and why `IsRegexMatch` is the correct patch lever.
- [../../Research/GameSystems/ThirdPartyModIdentities.md](../../Research/GameSystems/ThirdPartyModIdentities.md) - Stationpedia Ascended plugin GUID, Harmony ID, and Workshop ID used by `SpaBridge.IsInstalled` and `[HarmonyAfter(...)]` attributes.

### 5.3. Patterns

- [../../Research/Patterns/BestEffortIntegration.md](../../Research/Patterns/BestEffortIntegration.md) - Imperative patching, `Prepare()` gating, and silent failure on missing targets match the style every helper uses for soft Stationpedia Ascended coordination.
- [../../Research/Patterns/ILRepackPerModCopy.md](../../Research/Patterns/ILRepackPerModCopy.md) - Per-mod-copy state, per-mod-copy patches, Harmony-ID derivation, and the structural-enforcement corollary are the binding constraints that shaped the entire public API.

### 5.4. Protocols

- [../../Research/Protocols/SPADeviceDatabase.md](../../Research/Protocols/SPADeviceDatabase.md) - Stationpedia Ascended `DeviceDatabase` / `DeviceDescriptions` / `LogicDescription` schema is what `SpaBridge.TryEnrichLogicTooltips` writes into.

## 6. Open questions

- **ILRepack tooling (Open item O1).** `ILRepack.Lib.MSBuild.Task` NuGet package vs standalone `ilrepack.exe` binary in `tools/ilrepack/`. Decision deferred to the PowerTransmitterPlus integration commit because no existing SixFive7 mod uses ILRepack yet and the first consumer sets the pattern.
- **HarmonyIdHelper mod-GUID discovery (Open item O2).** Two viable paths: discover the consuming mod's plugin GUID from `Chainloader.PluginInfos` by matching on the calling method's declaring assembly, or require each consuming mod to register its GUID once via a `StationpediaPlus.SetOwningModGuid(guid)` init call. Both work with ILRepack; choice deferred to implementation of `HarmonyIdHelper`.
- **Native LogicType row rendering (Open item O3).** If runtime verification test T-Native-Rows fails (custom LogicType rows do not appear natively on extended device pages), investigate `LogicableInitializePatch` timing vs `EnumCollections.LogicTypes.Values` freeze point, whether `CanLogicRead` / `Write` postfixes fire during `AddLogicTypeInfo` iteration, and whether custom values are present in `EnumCollections.LogicTypes.Values` at the moment `Regenerate` fires. The fix is a root-cause correction, not a LogicInsert fallback.
