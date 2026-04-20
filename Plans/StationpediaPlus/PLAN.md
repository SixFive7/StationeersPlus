# StationpediaPlus Implementation Handoff

Complete briefing for the implementing agent. This document aggregates all
research, decompilation findings, design decisions, and content authored
across the planning phase.

Phase 5 migration note (2026-04-20): sections that held decompiled game
internals, verified SPA internals, line-number indexes, and ecosystem
research have been drained in-place to the central `Research/` knowledge
base. The original section headings are preserved so cross-references
still resolve; each drained section now carries a 1-3 sentence summary
plus a "Full content lifted to:" pointer list. Implementation-strategy
content (design decisions, API shapes, work plan, testing matrix, file
inventory, decision log, ILRepack constraints, authoring rules) remains
in place and unaltered. Original content preserved in git history at the
pre-lift commit.

Read front-to-back on first encounter, then use the Table of Contents as a
reference.

---

## Table of Contents

1. Orientation
2. Goals and constraints
3. Current state of PowerTransmitterPlus
4. Architecture overview
5. Page taxonomy (four kinds)
6. Verified game internals
7. Verified Stationpedia Ascended (SPA) internals
8. Shared library design (StationpediaPlus)
9. ILRepack constraints (binding)
10. Visual and naming conventions
11. Authoring rules (binding on all committed content)
12. Per-mod integration recipe
13. PowerTransmitterPlus concrete implementation plan
14. Testing matrix
15. Implementation phase plan
16. File and path inventory
17. Open items
18. Appendices
19. Decision log (all 18 decisions + research items)
20. Cheat sheet
21. Historical and ecosystem context

---

## 1. Orientation

### What we are building

A shared code library (`StationpediaPlus`) that every SixFive7 Stationeers mod
uses to integrate with the in-game Stationpedia (the wiki system). The
library is ILRepacked into each mod at build time so players never need to
install any separate artifact.

The first consumer is **PowerTransmitterPlus**. After that lands,
SprayPaintPlus, EquipmentPlus, and future SixFive7 mods follow the same
recipe.

### Why

- Each mod that adds or extends game content should document its changes
  inside the Stationpedia, visible where players already look.
- Doing this consistently across a growing mod collection requires shared
  code to avoid drift and duplication.
- The library also handles compatibility with the third-party "Stationpedia
  Ascended" (SPA) mod, which many players install.

### What this document replaces

All earlier planning artifacts in this directory (a.txt, b.txt, a1.txt,
b1.txt, a2.txt, b2.txt, a3.md, b3.md, a4.md, b4.md, a5.md, b5.md, c6.md) are
subsumed by this document. If this document conflicts with any of them, this
document wins.

---

## 2. Goals and constraints

### Hard goals

- **Compatible with Stationpedia Ascended (SPA) when installed.** SPA is the
  popular community Stationpedia-enhancement mod; Workshop ID 3634225688,
  Plugin GUID `com.florpydorp.stationpediaascended`, Harmony ID
  `com.stationpediaascended.mod`. Current version 0.8.6.
- **Never depends on SPA.** No `[BepInDependency]`, no compile-time assembly
  reference, no crash when SPA is absent. All SPA integration is runtime
  reflection that fails silently on any error.
- **Beautiful.** Native visual idiom, collapsible sections rather than walls
  of text, cross-linking between pages, match the game's own styling.
- **Stable.** UI-layer fragility isolated to one shared codebase. A fix in
  one place benefits every mod that rebuilds.
- **Generalizable.** Same recipe applies to every mod. Documented in the
  top-level CLAUDE.md.
- **Self-contained distribution.** Players do not install any separate
  artifact; each mod ships a single DLL.

### Hard constraints

- No abbreviations anywhere in user-visible strings.
- No reliance on CLAUDE.md rules for correctness where code can enforce the
  rule (enforcement is structural, not conventional).
- The shared library is embedded into each mod via ILRepack, so static state
  is per-mod-copy, and patches install per-mod independently.
- Every Harmony patch body is try/catch wrapped with defensive Unity
  fake-null guards; a failure in one mod's embedded copy must never break
  another mod's embedded copy.

### Soft guidance

- Code-first enforcement over documentation-first enforcement.
- Game-native markup tokens over bespoke formatting.
- Click-text convention compensates for missing vanilla hover feedback.
- Reference pages house shared content; device pages house device-specific
  content.

---

## 3. Current state of PowerTransmitterPlus

### Mod identity

- Source root: `c:\Source\SixFive7\StationeersPlus\PowerTransmitterPlus\PowerTransmitterPlus\`
- Plugin GUID: `net.powertransmitterplus`
- Harmony ID: `net.powertransmitterplus`
- Version: 1.1.0
- BepInEx plugin via LaunchPadBooster (`[BepInDependency("stationeers.launchpad", HardDependency)]`)
- Plugin class: `PowerTransmitterPlusPlugin : BaseUnityPlugin`

### What the mod does

Adds distance-cost microwave power transmission behavior to the vanilla
Microwave Power Transmitter and Microwave Power Receiver devices, plus six
custom LogicTypes for IC10 and tablet access, plus an auto-aim feature.

### Six custom LogicTypes (reserved band 6571-6599)

Registered in `LogicTypeRegistry.cs`:

| Value | Name | Access | Purpose |
|---|---|---|---|
| 6571 | `MicrowaveSourceDraw` | Read | Watts pulled from source network, including distance overhead |
| 6572 | `MicrowaveDestinationDraw` | Read | Watts delivered to receiver's downstream network |
| 6573 | `MicrowaveTransmissionLoss` | Read | Source draw minus delivered; zero at zero distance |
| 6574 | `MicrowaveEfficiency` | Read | Ratio delivered / source, 0..1 |
| 6575 | `MicrowaveAutoAimTarget` | Read/Write | Writable. Target Thing's ReferenceId to aim the dish. 0 disables. |
| 6576 | `MicrowaveLinkedPartner` | Read | Linked partner dish's ReferenceId, or 0 |

### Cost formula (server-authoritative)

```
source_draw      = delivered * (1 + k * distance_km)
transmission_loss = delivered * (multiplier - 1)
efficiency       = 1 / multiplier    (when transmitting, else 0)

where:
  k = DistanceCostFactor (default 5.0; synced from host in multiplayer)
  distance_km = straight-line distance between linked transmitter and receiver
  multiplier = (1 + k * distance_km)
```

Example values at k=5, delivering 200W:

| Distance | Source draw | Loss | Efficiency |
|---|---|---|---|
| 0 m | 200 W | 0 W | 100% |
| 100 m | 300 W | 100 W | 67% |
| 500 m | 700 W | 500 W | 29% |
| 1 km | 1.2 kW | 1 kW | 17% |
| 5 km | 5.2 kW | 5 kW | 3.8% |
| 10 km | 10.2 kW | 10 kW | 2.0% |

### Auto-aim mechanism

Writing a target Thing's `ReferenceId` to `MicrowaveAutoAimTarget` makes the
dish slew via its built-in servo to aim its pivot at the target's pivot.
Pivot-to-pivot geometry is rotation-invariant, which avoids self-referential
error when the dish moves mid-aim.

Key mechanics:
- Write 0 to disable auto-aim.
- Writing an invalid or unresolved id is a no-op.
- Manually adjusting `Horizontal` or `Vertical` (tablet, IC10, dish UI) cancels auto-aim.
- Reading the LogicType returns the current target id, or 0 when disabled.
- The base-game line-of-sight raycast decides when the wireless link actually forms.
- Per-dish cache via `ConditionalWeakTable<WirelessPower, StrongBox<long>>`
  for automatic GC-tied cleanup.

### Prefab device keys

From vanilla `english.xml`:

- `StructurePowerTransmitter`, Microwave Power Transmitter (the dish)
- `StructurePowerTransmitterReceiver`, Microwave Power Receiver
- `StructurePowerTransmitterOmni`, vanilla omni variant, NOT modified by this mod

Stationpedia page keys:

- `ThingStructurePowerTransmitter`
- `ThingStructurePowerTransmitterReceiver`
- `ThingStructurePowerTransmitterOmni` (DO NOT TOUCH)

### Existing Harmony patches (to keep)

- `LogicableInitializePatch`, extends `EnumCollections.LogicTypes.Values` to include 6571-6576
- `LogicReadoutPatches`, `CanLogicRead` / `GetLogicValue` postfixes branched by `__instance is PowerTransmitter / PowerReceiver`
- `AutoAimPatches`, `SetLogicValue` prefix, `CanLogicWrite` postfix, `RotatableBehaviour` setter postfixes to reset auto-aim on manual H/V write
- `EnumNamePatches`, postfixes on `Enum.GetName`, `EnumCollection<LogicType, ushort>.GetName` / `GetNameFromValue`
- `Ic10ConstantsPatcher`, reflection injection into `ProgrammableChip.AllConstants` for IC10 name resolution
- Distance-cost patches (four-patch dance):
  - `GeneratedPowerNoDistanceDeratePatch`, prefix on `PowerTransmitter.GetGeneratedPower`
  - `UsePowerInflateDebtPatch`, postfix on `PowerTransmitter.UsePower`
  - `GetUsedPowerLiftCapPatch`, postfix on `PowerTransmitter.GetUsedPower`
  - `ReceivePowerVisualizerFixPatch`, postfix on `PowerTransmitter.ReceivePower`

### Current Stationpedia stub (to be DELETED during migration)

`StationpediaPatches.cs` is a 66-line stub that registers one bare page per
custom LogicType via `Stationpedia.Register(page, false)`. No body content
beyond the one-line description. No extension sections. No reference pages.

Pattern origin: the stub follows the pattern established by
**Stationeers Logic Extended by ThunderDuck** (Workshop ID `3625190467`).
Stationeers Logic Extended has no public extensibility API, so every mod adding custom LogicTypes
reimplements the registration pattern from scratch. The stub uses the same
defensive reflection style (`AccessTools.TypeByName` with namespace fallbacks) and
the `TargetMethod()` + `Prepare()` Harmony pattern for graceful degradation
on game-version drift.

Stub will be deleted in the migration commit; shared library takes over.

### LogicType value bands

The reserved-band table for LogicType values: vanilla 0-349, Stationeers Logic Extended
1000-1830, PowerTransmitterPlus 6571-6599. Future SixFive7 mods adding
LogicTypes should reserve their own bands clear of these three.

Full content lifted to:
- [LogicType](../../Research/GameSystems/LogicType.md) - Reserved-band table, three-arrays extension mechanism, and EnumCollections injection path for custom LogicType values.

Lifted during Phase 5 migration on 2026-04-20. Original content preserved in git history at `<pre-lift-sha>:Plans/StationpediaPlus/PLAN.md:224-236`.

### Three LogicType arrays that must be extended

Stationeers stores LogicType lists in three separate places (`Logicable.LogicTypes`,
`EnumCollections.LogicTypes`, `ScreenDropdownBase.LogicTypes`); a mod adding
custom LogicTypes extends all three via `LogicableInitializePatch` for full
UI coverage. `LogicTypeNamesRedirects` is rebuilt as a best-effort index.
`Enum.GetName` and `EnumCollection<LogicType, ushort>.GetName`/`GetNameFromValue`
postfixes make reflection-based name lookups discover our custom values.

Full content lifted to:
- [LogicType](../../Research/GameSystems/LogicType.md) - Three-array extension mechanism with injection steps, plus the name-patch companion set.

Lifted during Phase 5 migration on 2026-04-20. Original content preserved in git history at `<pre-lift-sha>:Plans/StationpediaPlus/PLAN.md:238-255`.

### Threading / main-thread safety

`Prefab.OnPrefabsLoaded` fires on the Unity main thread, synchronously inside
the game's main-thread loading sequence (game.cs:59080-59090, before
`Stationpedia.Regenerate` at line 59090). `OnAllModsLoaded` is therefore
main-thread; all Unity API calls from within it are safe without dispatching.
PowerTransmitterPlus has a `MainThreadDispatcher` singleton MonoBehaviour for
distance-cost multiplayer sync from ThreadPool contexts, but StationpediaPlus
needs nothing of the sort: all its work runs on main thread.

Full content lifted to:
- [ModLoadSequence](../../Research/GameSystems/ModLoadSequence.md) - `OnPrefabsLoaded` / `OnAllModsLoaded` main-thread timing and ordering relative to `Stationpedia.Regenerate`.
- [MainThreadDispatcher](../../Research/Patterns/MainThreadDispatcher.md) - Per-mod dispatcher pattern (used elsewhere in PowerTransmitterPlus, not needed by StationpediaPlus).

Lifted during Phase 5 migration on 2026-04-20. Original content preserved in git history at `<pre-lift-sha>:Plans/StationpediaPlus/PLAN.md:257-270`.

### Plugin initialization

`PowerTransmitterPlusPlugin.Awake` binds configs and subscribes to
`Prefab.OnPrefabsLoaded`. The registered `OnAllModsLoaded` callback fires
after all mods' Awake phases complete (during the loading screen, before
`Stationpedia.Regenerate`). In that callback the plugin currently does:

1. `harmony.PatchAll()`
2. `Ic10ConstantsPatcher.Apply()`

After migration it will additionally call into StationpediaPlus's four
helpers (see §12 and §13).

---

## 4. Architecture overview

### Three primary Harmony hooks (installed by helpers)

| Hook | Helper | Purpose |
|---|---|---|
| `Stationpedia.PopulateLogicVariables` (Postfix) | `LogicTypePageBuilder` | Enrich per-LogicType page bodies |
| `UniversalPage.ChangeDisplay` (Postfix, HarmonyAfter SPA) | `CategoryBuilder` | Inject collapsible extension sections on extended device pages |
| `StationpediaPage.IsRegexMatch(string, string)` (Postfix) | `ReferencePage` (via internal `SearchFilterPatch`) | Filter hidden pages out of search results |

Optional secondary hook:

| Hook | Helper | Purpose |
|---|---|---|
| `Stationpedia.PopulateGuideLoreContents` (Prefix) | `ReferencePage` (via internal `SearchFilterPatch`) | Filter hidden pages out of Guides/Lore home-page tabs |

### One non-Harmony op

- ILRepack-embedded helper library; each mod gets its own private IL copy.
- Static state is per-mod-copy; no runtime sharing across mods.

### Four public helpers (mod-facing API)

1. `CategoryBuilder`, injects collapsible sections on vanilla device pages.
2. `ReferencePage`, registers hidden reference pages linkable but not
   searchable.
3. `LogicTypePageBuilder`, enriches LogicType page bodies; also invokes
   `SpaBridge` automatically for every registered spec.
4. `SpaBridge`, soft-detects SPA and reflects into its `DeviceDatabase` for
   tooltip enrichment. Usually called automatically by `LogicTypePageBuilder`;
   public for edge-case direct use.

### One runtime support MonoBehaviour

- `SixFive7LinkHandler`, minimal `IPointerClickHandler` attached to every
  dynamically created TMP text element inside extension sections.
  Routes `<link=...>` clicks to `Stationpedia.Instance.SetPage(linkID)`.

### Three internal plumbing files

- `SearchFilterPatch`, installs the search-filter Harmony postfixes.
- `TextElementFactory`, builds dynamic TMP elements with donor styling and
  attaches `SixFive7LinkHandler`.
- `HarmonyIdHelper`, derives per-mod Harmony instance IDs from the
  consuming mod's GUID.

---

## 5. Page taxonomy (four kinds)

Every piece of Stationpedia content maps to exactly one kind.

### Kind A, New-thing pages

Vanilla game auto-generates a page for any registered prefab. Key is
`"Thing" + prefab.PrefabName`. Body comes from the prefab's
`<RecordThing>/<Description>` in the mod's language XML. Logic rows appear
via `CanLogicRead`/`Write` iteration in vanilla `AddLogicTypeInfo`.

Mod responsibility: author language XML; implement `CanLogicRead/Write` on
the new device class. Zero Stationpedia-specific code.

Library responsibility: none.

### Kind B, LogicType pages

Vanilla auto-generates a page per enum value in
`EnumCollections.LogicTypes.Values`. Key is `"LogicType" + enumName`. Body
uses a minimal template (`LogicTypePageTemplate`) producing a one-line
description.

Mod responsibility: call `LogicTypePageBuilder.Register(spec)` per custom
LogicType with a fully-filled `CustomLogicTypeSpec`.

Library responsibility: via `LogicTypePageBuilder` postfix on
`PopulateLogicVariables`:
- Build enriched body with Summary, Formula, and Related sections.
- Set Text, call ParsePage, Register with fallback=false.
- Additionally invoke `SpaBridge.TryEnrichLogicTooltips` for every device key
  in `spec.RelatedDeviceKeys`.

### Kind C, Extension sections

For mods that modify vanilla prefabs (not add new ones). The vanilla page
stays intact; we inject one collapsible `StationpediaCategory` at the bottom.

Mod responsibility: call `CategoryBuilder.Register(modName, deviceKeys, contentBuilder)`
from `OnAllModsLoaded`.

Library responsibility: via `CategoryBuilder` postfix on
`UniversalPage.ChangeDisplay` (with `[HarmonyAfter(spa)]`):
- Gate on `page.Key in deviceKeys`.
- Idempotent destroy-then-create named GameObject `<ModName>Details`.
- Instantiate `Stationpedia.Instance.CategoryPrefab` under `page.Content`.
- Title: `<color=#FF7A18>{ModName} Details</color>` (non-bold, SPA-matching).
- Populate with `TextElementFactory`-produced TMP rows.
- Sibling index 21 (below SPA's 20).
- Default collapsed.
- `page.CreatedCategories.Add(category)` for vanilla cleanup on next navigation.

### Kind D, Shared-reference pages

Content referenced from more than one page. Promoted to a dedicated page
keyed `<FullModName>_<Topic>` (e.g. `PowerTransmitterPlus_AutoAim`). Hidden
from search and home-page listings; reachable only by clicking a `{LINK:...}`
from another page.

Mod responsibility: call `ReferencePage.Register(topic, titleRaw, bodyMarkup)`
per reference page.

Library responsibility: via `ReferencePage`:
- Build `StationpediaPage`, set Text, ParsePage, Register with fallback=false.
- Add the key to an internal `HiddenKeys` HashSet.
- Ensure `SearchFilterPatch` is installed (idempotent on first call).

### Content placement matrix

| Content scope | Destination |
|---|---|
| Specific to one device the mod modifies | Collapsible extension section on that device page (Kind C) |
| Shared across multiple pages | Reference page (Kind D) plus short link from each referrer |
| Specific to a new custom LogicType | Enriched LogicType page (Kind B); link to ref page for deep material |
| Specific to a newly-added device | Auto-generated Thing page (Kind A); content via language XML |
| Mod settings, config, install help | Out of scope for Stationpedia; stays in README / About.xml |

---

## 6. Verified game internals

Full decompiled inventory of the vanilla Stationpedia subsystem, drained
during Phase 5 migration. Covered: the core class roster (`Stationpedia`,
`StationpediaPage`, `UniversalPage`, `SPDALogic`, `StationLogicInsert`,
`StationpediaCategory`, `HelpLinkHandler`, `SPDAEntryType`), the Register /
Regenerate / ChangeDisplay / PopulateThingPages / PopulateLogicVariables
lifecycles, ParsePage semantics, the DoSearch async path plus the
`IsRegexMatch` 255-char cutoff and patching rationale, the markup-token
replacement table, HelpLinkHandler click routing, the 19 UniversalPage
category fields with their Populate methods, and sundry nuances (sibling
index clamping, Register leaks on filter change, the `_pageHistory`
back-stack, `{LIST_OF_*}` / `_worldHashes` token expansion, legacy
`ThingTemplate` dead code, and the shared vanilla transmitter/receiver
body text that our mod supersedes).

Full content lifted to:
- [Stationpedia](../../Research/GameClasses/Stationpedia.md) - Singleton controller, `Register` replace semantics, `SetPage`, `OnPageChanged` event, row-prefab inventory on `Stationpedia.Instance`, GuidesPages/LorePages re-register leak, back-stack navigation.
- [StationpediaPage](../../Research/GameClasses/StationpediaPage.md) - Data model fields, three constructors, `ParsePage` body and Description one-shot guard, `IsRegexMatch(string, string)` body and 255-char cutoff, `{LIST_OF_*}` / `_worldHashes` token expansion.
- [UniversalPage](../../Research/GameClasses/UniversalPage.md) - `ChangeDisplay` step-by-step flow, `CreatedCategories` cleanup list, 19-category field inventory with Populate methods and source fields, sibling-index clamp behaviour.
- [HelpLinkHandler](../../Research/GameClasses/HelpLinkHandler.md) - `OnPointerClick` body, `LateUpdate` `WorldManager.IsGamePaused` coupling, rationale for shipping `SixFive7LinkHandler` instead.
- [StationpediaPageRendering](../../Research/GameSystems/StationpediaPageRendering.md) - `Regenerate` 15-step lifecycle, `PopulateThingPages` + `AddLogicTypeInfo` loop, `PopulateLogicVariables` template, `ChangeDisplay` Populate-call ordering.
- [StationpediaSearch](../../Research/GameSystems/StationpediaSearch.md) - Search trigger chain (`onValueChanged` through `DoSearch`), async UniTask patching rationale, why `IsRegexMatch` is the right patch lever.
- [StationpediaMarkup](../../Research/GameSystems/StationpediaMarkup.md) - Full `Localization.ParseHelpText` token table, `[` / `]` rewrite rule, TMP rich-text passthrough.
- [Localization](../../Research/GameSystems/Localization.md) - Vanilla transmitter/receiver body text (shared prefab oversight), legacy `ThingTemplate` placeholder dead code.

Lifted during Phase 5 migration on 2026-04-20. Original content preserved in git history at `<pre-lift-sha>:Plans/StationpediaPlus/PLAN.md:414-1030`.

---

## 7. Verified Stationpedia Ascended (SPA) internals

Full decompiled inventory of SPA's integration surface, drained during Phase
5 migration. Covered: SPA identity constants (plugin GUID, Harmony ID,
Workshop ID, version), the complete SPA Harmony patch list plus the
corresponding "SPA does NOT patch" exclusions that define our coordination
surface, `ChangeDisplay_Postfix` six-step flow, `DeviceDatabase` schema
(`DeviceDescriptions` + `LogicDescription` + related types), the shipped
coverage for our three transmitter prefabs (all with empty
`operationalDetails`), the 100ms-poll + 2-frame-delay tooltip attachment
coroutine, tooltip resolution chain (device -> generic -> AdditionalData ->
placeholder), `GenericDescriptionsData` fallback, the distinct-child-name
convention that keeps our `<ModName>Details` GameObjects safe from SPA's
destroy list, the `SPDALogic`-only row-iteration filter, SPA's
`SearchPatches` caches and `ShouldHideFromSearch` policy, and SPA's own
`TocLinkHandler` / `CategoryHeaderHandler` click handlers (documented for
reference; we ship our own handler).

Full content lifted to:
- [StationpediaAscendedInternals](../../Research/GameSystems/StationpediaAscendedInternals.md) - Full SPA Harmony patch list, `ChangeDisplay_Postfix` flow, tooltip poll + attach coroutine, resolution chain, AdditionalData / GenericDescriptions fallbacks, distinct-name destroy convention, `SPDALogic` row filter, `SearchPatches` caches and `ShouldHideFromSearch`, UI-side click handlers, SPA `LoadDescriptions` synchronous readiness.
- [SPADeviceDatabase](../../Research/Protocols/SPADeviceDatabase.md) - `DeviceDatabase` schema (`DeviceDescriptions`, `LogicDescription`, related types), shipped coverage of our transmitter prefabs with empty `operationalDetails`, descriptions.json loading order and shipped metrics.
- [ThirdPartyModIdentities](../../Research/GameSystems/ThirdPartyModIdentities.md) - SPA plugin GUID, Harmony IDs, Workshop ID, version strings.

Lifted during Phase 5 migration on 2026-04-20. Original content preserved in git history at `<pre-lift-sha>:Plans/StationpediaPlus/PLAN.md:1034-1316`.

---

## 8. Shared library design (StationpediaPlus)

### 8.1 Project location

```
c:\Source\SixFive7\StationeersPlus\StationpediaPlus\
```

Built as `StationpediaPlus.dll`. ILRepacked into each consuming mod at build
time (§9). No runtime artifact distributed separately.

### 8.2 Namespace

`StationpediaPlus`, all public types live at this root.

`StationpediaPlus.Internal`, internal plumbing.

### 8.3 File layout (Decision 14A)

```
StationpediaPlus/
  StationpediaPlus.csproj
  Directory.Build.props.template
  Directory.Build.props                  (gitignored, per-developer)
  .gitignore
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

`SpaSearchFilter.cs` is new per decision 19 (see §19 and §8.11). It installs a
soft-dependent Harmony patch on Stationpedia Ascended's `ShouldHideFromSearch`
method so reference pages stay hidden from SPA's search-result re-injection.
Soft-detected via `Chainloader.PluginInfos.ContainsKey`; silent no-op when
SPA is absent.

### 8.4 Helper public APIs (signatures only)

`CategoryBuilder`:
```csharp
public static void Register(
    string modName,                    // e.g. "Power Transmitter Plus"
    IEnumerable<string> deviceKeys,    // e.g. ["ThingStructurePowerTransmitter", ...]
    Func<string, string> contentBuilder); // (pageKey) -> markup body
```

`ReferencePage`:
```csharp
public static void Register(string topic, string titleRaw, string bodyMarkup);
public static string KeyFor(string topic); // returns "<FullModName>_<topic>"
```

`LogicTypePageBuilder`:
```csharp
public static void Register(CustomLogicTypeSpec spec);
```

Where `CustomLogicTypeSpec` is:

```csharp
public class CustomLogicTypeSpec
{
    // Required:
    public string Name;                       // e.g. "MicrowaveSourceDraw"
    public ushort Value;                      // e.g. 6571
    public string DataType;                   // "Float", "Integer", "Boolean", "ReferenceId", "String"
    public string Range;                      // "0-1", "0+", "0 or id", etc.
    public string TooltipDescription;         // plain text, no markup
    public string PageSummary;                // short paragraph; markup OK
    public string[] RelatedDeviceKeys;        // drives SpaBridge enrichment AND page cross-links

    // Optional:
    public string FormulaOrBehavior;          // markup OK
    public string[] RelatedLogicTypeNames;
    public string[] RelatedReferenceKeys;
}
```

`SpaBridge`:
```csharp
public static bool IsInstalled();
public static bool TryEnrichLogicTooltips(
    string deviceKey,
    IReadOnlyList<(string name, string dataType, string range, string description)> entries);
```

Most mods never call `SpaBridge` directly; `LogicTypePageBuilder.Register`
invokes it automatically for every device in `spec.RelatedDeviceKeys`.

### 8.5 SixFive7LinkHandler

MonoBehaviour attached to dynamic TMP text elements. Click-only behavior:

```csharp
[RequireComponent(typeof(TextMeshProUGUI))]
public class SixFive7LinkHandler : MonoBehaviour, IPointerClickHandler
{
    public TextMeshProUGUI Text;

    public void OnPointerClick(PointerEventData eventData)
    {
        // On click:
        //  1. Find intersecting link via TMP_TextUtilities
        //  2. Extract linkID from linkInfo
        //  3. Call Stationpedia.Instance.SetPage(linkID) if not null
        //  4. Swallow all exceptions
    }
}
```

Attached by `TextElementFactory.Create(...)` to every dynamically-created TMP
text element. Uses `eventData.pressEventCamera` for TMP utility calls (no
setup required).

### 8.6 Internal helpers

`TextElementFactory.Create(RectTransform parent, TextMeshProUGUI donor, string parsedMarkup)`
,  creates a new GameObject named `DetailText` with:
- `TextMeshProUGUI` donor-styled (font, size, color, line spacing, margin)
- `ContentSizeFitter` (horizontal unconstrained, vertical preferred)
- `RectTransform` anchored top-stretch
- `SixFive7LinkHandler` with `Text = tmp`

`HarmonyIdHelper.ForMod(string helperName)`, returns `<ConsumingModPluginGuid>.stationpediaplus.<helperName>`. The consuming mod's GUID is discovered via `Chainloader.PluginInfos` or via a per-mod registration (TBD at implementation time; both approaches work with ILRepack).

`SearchFilterPatch.EnsureInstalled()`, idempotent install of the two search
Harmony postfixes (details in §6.12 and below).

### 8.7 CategoryBuilder implementation notes

Harmony installation (lazy on first `Register`):

```csharp
var orig = AccessTools.Method(typeof(UniversalPage), "ChangeDisplay");
var post = AccessTools.Method(typeof(CategoryBuilder),
    nameof(ChangeDisplay_Postfix));
harmony.Patch(orig, postfix: new HarmonyMethod(post)
{
    after = new[] { "com.stationpediaascended.mod" }
});
```

Postfix body:

```
For each registered entry:
  If page.Key matches one of the entry's DeviceKeys:
    Try:
      Build markup via entry.contentBuilder(pageKey)
      If markup is null or whitespace: skip this page
      Find pre-existing GameObject child of page.Content named
        "<ModName>Details", destroy it via DestroyImmediate (idempotency)
      Null-guard: Stationpedia.Instance, CategoryPrefab, up.PageDescription
      Instantiate CategoryPrefab under up.Content
      Name the GameObject "<ModName>Details"
      Set cat.Title.text = "<color=#FF7A18>{ModName} Details</color>"
      ParseHelpText the markup
      Call TextElementFactory.Create(cat.Contents, up.PageDescription, parsedMarkup)
      SetSiblingIndex(21)
      Collapsed by default: cat.Contents.gameObject.SetActive(false),
        swap cat.CollapseImage.sprite to cat.NotVisibleImage
      LayoutRebuilder.ForceRebuildLayoutImmediate(Stationpedia.Instance.ContentRectTransform)
      page.CreatedCategories.Add(cat)
    Catch:
      LogDebug; never rethrow
```

### 8.8 ReferencePage implementation notes

```
On Register(topic, titleRaw, bodyMarkup):
  key = "<FullModName>_" + topic
  ensure SearchFilterPatch is installed
  page = new StationpediaPage(key, titleRaw)
  page.Text = bodyMarkup ?? string.Empty
  page.DisplayFilter = SPDAEntryType.Undefined
  page.ParsePage()
  Stationpedia.Register(page, false)
  Internal.HiddenKeys.Add(key)
```

`FullModName` is the consuming mod's display name (no spaces or special
characters). For PowerTransmitterPlus it's `"PowerTransmitterPlus"`. For
Spray Paint Plus it's `"SprayPaintPlus"`. Discovered via
`Chainloader.PluginInfos` or per-mod registration (implementer chooses).

### 8.9 LogicTypePageBuilder implementation notes

Validates `CustomLogicTypeSpec` at Register time. All "required" fields must
be non-null / non-empty; throw (or log and skip) otherwise.

Installs Harmony postfix on `Stationpedia.PopulateLogicVariables` (lazy on
first `Register`). Postfix body:

```
For each registered spec:
  Try:
    key = "LogicType" + spec.Name
    Build body markup:
      {HEADER:Summary}
      {LIST}{spec.PageSummary}{/LIST}
      (if spec.FormulaOrBehavior)
        {HEADER:Formula}
        {LIST}{spec.FormulaOrBehavior}{/LIST}
      (if any related)
        {HEADER:Related}
        {LIST}
        For each related LogicType name: {LOGICTYPE:name}
        For each related device key: {LINK:key;Click here for the ... page}
        For each related reference key: {LINK:key;Click here for the ... reference}
        {/LIST}
    page = new StationpediaPage(key, "LogicType." + spec.Name, body)
    page.CustomSpriteToUse = Stationpedia.Instance.VariableImage
    page.ParsePage()
    Stationpedia.Register(page, false)
  Catch:
    LogDebug; continue

After registering all LogicType pages:
  Group specs by RelatedDeviceKey.
  For each device key:
    Build entries list: (spec.Name, spec.DataType, spec.Range, spec.TooltipDescription)
      for every spec whose RelatedDeviceKeys contains this key.
    SpaBridge.TryEnrichLogicTooltips(deviceKey, entries)
```

Note on auto-generated link labels: the "Click here for the ... page" and
"Click here for the ... reference" labels in the Related section must
comply with the click-phrasing rule (§11.3). The helper composes the label
from a lookup table or from the key stripped of its prefix. Example:
`{LINK:ThingStructurePowerTransmitter;Click here for the Microwave Power Transmitter page}`.
If the human-readable target name isn't available at code-gen time, fall back
to `{LINK:key;Open this page}` (still compliant with §11.3).

SpaBridge invocation is automatic; mods never need to call it directly for
the common path.

### 8.10 SpaBridge implementation notes

Public helper. Reflects into SPA's `DeviceDatabase` on each call. Every step
is null-guarded and try/catch wrapped; any failure returns false silently.

```
On TryEnrichLogicTooltips(deviceKey, entries):
  If !IsInstalled(): return false
  Try:
    Resolve SPA assembly via Chainloader.PluginInfos[spaGuid].Instance.GetType().Assembly
    Resolve StationpediaAscendedMod type
    Resolve DeviceDescriptions type
    Resolve LogicDescription type
    Get DeviceDatabase static property; cast to IDictionary
    If device key not present: return false
    Get the device entry's logicDescriptions field; initialize if null
    For each (name, dataType, range, description):
      Create new LogicDescription via Activator
      Set dataType, range, description fields via reflection
      logicDescriptions[name] = newEntry
    Return true
  Catch:
    Return false
```

Concrete reflection skeleton (reference; final implementation refines
error-handling and edge cases):

```csharp
const string SpaGuid = "com.florpydorp.stationpediaascended";

public static bool IsInstalled()
    => BepInEx.Bootstrap.Chainloader.PluginInfos != null
       && BepInEx.Bootstrap.Chainloader.PluginInfos.ContainsKey(SpaGuid);

public static bool TryEnrichLogicTooltips(
    string deviceKey,
    IReadOnlyList<(string name, string dataType, string range, string description)> entries)
{
    if (!IsInstalled() || entries == null || entries.Count == 0) return false;
    try
    {
        var info = BepInEx.Bootstrap.Chainloader.PluginInfos[SpaGuid];
        var asm = info.Instance.GetType().Assembly;

        var modType = asm.GetType("StationpediaAscended.StationpediaAscendedMod");
        var descType = asm.GetType("StationpediaAscended.Data.DeviceDescriptions");
        var ldType = asm.GetType("StationpediaAscended.Data.LogicDescription");
        if (modType == null || descType == null || ldType == null) return false;

        var dbProp = modType.GetProperty("DeviceDatabase",
            System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
        var db = dbProp?.GetValue(null) as System.Collections.IDictionary;
        if (db == null || !db.Contains(deviceKey)) return false;

        var entry = db[deviceKey];
        var ldField = descType.GetField("logicDescriptions",
            System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
        var ld = ldField?.GetValue(entry) as System.Collections.IDictionary;
        if (ld == null)
        {
            var dictType = typeof(System.Collections.Generic.Dictionary<,>).MakeGenericType(typeof(string), ldType);
            ld = (System.Collections.IDictionary)System.Activator.CreateInstance(dictType);
            ldField.SetValue(entry, ld);
        }

        var fDt = ldType.GetField("dataType",
            System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
        var fRg = ldType.GetField("range",
            System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
        var fDesc = ldType.GetField("description",
            System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);

        foreach (var e in entries)
        {
            var obj = System.Activator.CreateInstance(ldType);
            fDt?.SetValue(obj, e.dataType);
            fRg?.SetValue(obj, e.range);
            fDesc?.SetValue(obj, e.description);
            ld[e.name] = obj;
        }
        return true;
    }
    catch { return false; }
}
```

Key reflection details confirmed during research:
- `StationpediaAscendedMod.DeviceDatabase` is a public static property with
  a private setter; the dictionary itself is mutable through the getter.
- `DeviceDescriptions.logicDescriptions` is a public instance field (not
  property); type `Dictionary<string, LogicDescription>`.
- `LogicDescription` has three public instance fields: `dataType`, `range`,
  `description` (all lowercase-first; plain strings; no setters needed).
- Cache invalidation: SPA's `SPDABaseTooltip.ClearCache()` exists as a
  public method for manual invalidation if needed. Not required during
  normal flow (tooltips are rebuilt on each page navigation anyway), but
  available if a mod mutates DeviceDatabase while a tooltip GameObject is
  currently displayed.

### 8.11 SearchFilterPatch implementation notes

Idempotent `EnsureInstalled()`. Installs:

Primary: postfix on `StationpediaPage.IsRegexMatch(string, string)`, two-arg overload only.

```csharp
static void Postfix(StationpediaPage __instance, ref bool __result)
{
    if (__result && __instance != null && __instance.Key != null
        && Internal.HiddenKeys.Contains(__instance.Key))
    {
        __result = false;
    }
}
```

Secondary: prefix on `Stationpedia.PopulateGuideLoreContents`:

```csharp
static void Prefix(ref List<string> SPDAKeys)
{
    if (SPDAKeys == null) return;
    SPDAKeys = SPDAKeys.Where(k => k != null && !Internal.HiddenKeys.Contains(k)).ToList();
}
```

Uses `ref List<string>` on the parameter so the mutation replaces only the
local; caller's static `GuidesPages`/`LorePages` lists remain untouched.
Covers the home-page Guides and Lore sidebar shortcuts (which call
`PopulateGuideLoreContents(GuidesPages, ...)` and
`PopulateGuideLoreContents(LorePages, ...)` respectively).

Tertiary: postfix on `Stationpedia.PopulateLists` (private; target via
string name):

```csharp
static void Postfix()
{
    var dict = Stationpedia.DataHandler._listDictionary;
    if (dict == null) return;
    foreach (var outer in dict.Values)
    {
        if (outer == null) continue;
        foreach (var inner in outer.Values)
        {
            if (inner == null) continue;
            inner.RemoveAll(insert =>
                insert != null && insert.PageLink != null
                && Internal.HiddenKeys.Contains(insert.PageLink));
        }
    }
}
```

Runs after vanilla `PopulateLists` populates
`SPDADataHandler._listDictionary` (the nested
`Dictionary<string, Dictionary<string, List<StationCategoryInsert>>>` that
drives home-page category-listing pages like "Devices", "Logic_Units",
etc.). Each `StationCategoryInsert.PageLink` is a `"Thing" + PrefabName`
key; our reference pages are not in these listings under normal operation
(they are not Thing-backed), but the filter is a correctness backstop in
case a future code path routes a reference key through a category listing.

No `[HarmonyAfter]` / `[HarmonyBefore]` needed on any of the three targets
,  none is patched by SPA.

### 8.11a SpaSearchFilter (internal, soft-dependent; decision 19 ii)

The three patches in §8.11 handle vanilla search surfaces. When Stationpedia
Ascended is installed, SPA runs a delayed coroutine (`ReorganizeSearchResults`)
that re-injects search matches from its own `_pageTitleIndex` built by
`BuildPageIndexes` from `Stationpedia.StationpediaPages`. SPA's own
`ShouldHideFromSearch` filter knows only about burnt / ruptured / wreckage
variants; it does not know our `HiddenKeys`. Consequence: without further
action, our hidden reference pages reappear in SPA-augmented search results
0.3 to 0.8 seconds after vanilla search runs.

`SpaSearchFilter` closes this gap via a soft-dependent Harmony Postfix on
SPA's `ShouldHideFromSearch`. Installed lazily on first `ReferencePage.Register`
call when SPA is present; silent no-op when SPA is absent.

Resolution:

1. Detect SPA via
   `BepInEx.Bootstrap.Chainloader.PluginInfos.ContainsKey("com.florpydorp.stationpediaascended")`.
2. Resolve SPA's `ShouldHideFromSearch` method via reflection against
   `StationpediaAscended.Patches.SearchPatches` (confirm exact name at
   implementation time by reading `SearchPatches.cs` around line 360 per
   `Research/GameSystems/StationpediaAscendedInternals.md`).
3. Install a Harmony Postfix with instance ID
   `<ModGuid>.stationpediaplus.spasearchfilter` that sets `__result = true`
   when `page.Key` is in the shared `HiddenKeys` HashSet.
4. Every reflection or patch failure caught and swallowed; on failure we fall
   back to "pages visible in SPA search" without crashing.

Activation is structural (parallel to decision 16's `SpaBridge`):
`ReferencePage.Register` calls `SpaSearchFilter.EnsureInstalled()` automatically.
Mod authors do not invoke `SpaSearchFilter` directly.

Fallback method-name candidates if `ShouldHideFromSearch` is refactored in a
future SPA release: `BuildPageIndexes` (Postfix that trims our keys from the
index) or `ReorganizeSearchResults` (Postfix that filters the result list).

### 8.12 Harmony instance IDs (per-mod, per-helper)

Each helper uses its own Harmony instance ID, derived from the consuming
mod's plugin GUID:

| Helper | ID template |
|---|---|
| `CategoryBuilder` | `<ModGuid>.stationpediaplus.categorybuilder` |
| `ReferencePage` + `SearchFilterPatch` | `<ModGuid>.stationpediaplus.referencepage` |
| `LogicTypePageBuilder` | `<ModGuid>.stationpediaplus.logictypepagebuilder` |
| `SpaSearchFilter` | `<ModGuid>.stationpediaplus.spasearchfilter` |
| `SpaBridge` | No Harmony patches (reflection only) |

For PowerTransmitterPlus: `net.powertransmitterplus.stationpediaplus.*`.

---

## 9. ILRepack constraints (binding)

The shared codebase is merged into each consuming mod's DLL at build time.
Nothing is distributed separately to players.

### 9.1 Static state is per-mod

After ILRepack, each mod's final assembly contains its own private copy of
every type in the library. Static fields are per-copy. Two SixFive7 mods in
the same player install have completely separate library state in memory.

### 9.2 Harmony patches are per-mod

Each mod's embedded copy independently installs its own Harmony patches.
Multiple postfixes on the same game method coexist fine in Harmony, but
each mod's copy only knows about its own registrations.

### 9.3 Harmony instance IDs derived from mod GUID

The library must NOT hardcode instance IDs. `HarmonyIdHelper` derives them
from the consuming mod's plugin GUID at runtime.

### 9.4 Idempotent patch installation

Each helper's Harmony installation is guarded: first `Register` call installs
the patch; subsequent calls append to the static list. Multiple `Register`
calls from the same mod do not double-patch.

### 9.5 Defensive patch bodies

Every Harmony patch body:
- Wrapped in try/catch.
- Logs via `Debug.LogWarning` on exception; never rethrows.
- Unity fake-null guards: `if (obj == null || !obj) return;`
- Null-guards all public data access before dereferencing.

A failure in one mod's embedded library copy must not crash another mod's
embedded copy.

### 9.6 No cross-mod coordination at runtime

Each mod operates as if it were the only SixFive7 mod installed. Visible
coordination (multi-mod sibling ordering, distinct GameObject names) happens
via game-level mechanisms (distinct names, sibling indices, CreatedCategories),
not via shared state.

### 9.7 Shared codebase is version-neutral

IL is frozen at each mod's build time. Upgrading the shared library means
rebuilding every consumer. No runtime version skew possible.

### 9.8 Documentation constraint

The shared codebase's top-of-file comment explicitly states it is designed
for ILRepack and lists these constraints. This prevents future maintainers
from accidentally introducing cross-mod state.

### 9.9 ILRepack tooling (NOT YET CHOSEN)

Open item. Two options:
- `ILRepack.Lib.MSBuild.Task` NuGet package (integrates via PackageReference).
- Standalone `ilrepack.exe` binary in `tools/ilrepack/` (no NuGet dependency).

Defer decision until integrating into PowerTransmitterPlus. No existing
SixFive7 mod uses ILRepack yet.

---

## 10. Visual and naming conventions

### 10.1 Colors

- Extension section title: `#FF7A18` (orange), matches SPA's `OperationalDetailsCategory`.
- LINK token default blue: `#0080FFFF` (game-controlled).
- LOGICTYPE token default orange: `orange` (game-controlled).

### 10.2 Page keys

| Kind | Format | Example |
|---|---|---|
| Thing (vanilla) | `Thing<PrefabName>` | `ThingStructurePowerTransmitter` |
| LogicType (vanilla) | `LogicType<EnumName>` | `LogicTypeMicrowaveSourceDraw` |
| Reference (ours) | `<FullModName>_<Topic>` | `PowerTransmitterPlus_MicrowavePowerTransmissionModel` |

Full mod name is used for reference keys (no abbreviation) to prevent
collision with unrelated third-party mods that might use similar abbreviations.

### 10.3 Page titles

| Kind | Format | Example |
|---|---|---|
| LogicType page | `LogicType.<EnumName>` | `LogicType.MicrowaveSourceDraw` |
| Reference page | plain natural-language title | `Microwave Power Transmission Model` |

No abbreviations in any title. Mod ownership, if needed, surfaces via a
footer section on the reference page body rather than in the title.

### 10.4 GameObject names

- Extension section container: `<ModName>Details` (e.g. `PowerTransmitterPlusDetails`).
- Dynamic text element: `DetailText`.
- Never reuse `OperationalDetailsCategory` (SPA-reserved).

### 10.5 Sibling indices

| Source | Sibling index |
|---|---|
| SPA `OperationalDetailsCategory` | 20 |
| StationeersPlus `<ModName>Details` | 21 |

Unity preserves insertion order among equal-index siblings. If multiple
SixFive7 mods target the same device, they all use 21 and appear in
registration order.

### 10.6 Section title format

```
<color=#FF7A18>{ModName} Details</color>
```

Non-bold, " Details" suffix, matching SPA's visual rhythm. Bolding our
section would create dissonance with vanilla category titles (also non-bold).

### 10.7 Default collapse state

All extension sections collapsed by default: `cat.Contents.gameObject.SetActive(false)`,
and `cat.CollapseImage.sprite = cat.NotVisibleImage`. Matches SPA.

---

## 11. Authoring rules (binding on all committed content)

### 11.1 Style rules (from top-level CLAUDE.md)

- No em dashes or en dashes. Use commas, colons, semicolons, parentheses, periods.
- No ellipsis character. Use three periods.
- No curly quotes. Straight quotes only.
- No mention of AI, agents, automation, LLM, Claude, ChatGPT, "generated by",
  or similar in any committed file.
- Keep sentences short. Prefer lists over prose walls.
- Terse, matter-of-fact tone.

### 11.2 No abbreviations anywhere

Applies to all user-visible strings:
- Extension section titles (`Power Transmitter Plus Details`, not `PTP Details`)
- Reference page titles (`Microwave Power Transmission Model`, not `[PTP] Microwave Power Transmission Model`)
- LogicType page body text
- Cross-link display labels
- CLAUDE.md integration text

Mod display names always appear in full form.

### 11.3 Explicit click phrasing on `{LINK:...}` labels

Because `SixFive7LinkHandler` does not provide hover-color feedback, every
`{LINK:...}` display label must include explicit clickable-action phrasing.

GOOD examples:
- `{LINK:PowerTransmitterPlus_MicrowavePowerTransmissionModel;Click here for the full power loss curve}`
- `{LINK:PowerTransmitterPlus_AutoAim;Open the auto-aim walkthrough}`
- `{LINK:ThingStructurePowerTransmitter;Go to the Microwave Power Transmitter page}`

BAD examples (insufficient signal):
- `{LINK:PowerTransmitterPlus_MicrowavePowerTransmissionModel;Distance Cost Model}`
- `{LINK:ThingStructurePowerTransmitter;Microwave Power Transmitter}`

`{LOGICTYPE:Name}` and `{THING:PrefabName}` are exempt because the game
parser wraps them in colored link tags which signal linkability visually.

### 11.4 No literal `[` or `]` in authored markup

`StationpediaPage.ParsePage` rewrites `[` -> `<` and `]` -> `>`. This is
reserved as an XML-escape hatch. Never author literal square brackets.

### 11.5 Markup token usage

- `{HEADER:Title}`, sub-headings within any section body.
- `{LIST}...{/LIST}`, bulleted lists (indentation).
- `{LINK:Key;Label}`, cross-page link. Label must include click phrasing (§11.3).
- `{LOGICTYPE:Name}`, LogicType cross-link (orange, auto-formatted).
- `{THING:PrefabName}`, Thing page cross-link (green, auto-formatted).
- `{POS:N}`, column alignment in table-like layouts.
- Unity rich text (`<b>`, `<i>`, `<color=#RRGGBB>`, `<size=N%>`) for inline emphasis.

### 11.6 Pre-parse before assigning to Description in postfixes

Any string written to `PageDescription.text` in a postfix context does NOT
go through `ParsePage` automatically. Pre-parse with
`Localization.ParseHelpText(...)` before assignment. Applies to Description
mutations; does NOT apply to our helpers' authored markup (those go through
ParsePage via Text + ParsePage, Decision 12B).

### 11.7 RESEARCH.md reading rule (inherited from CLAUDE.md)

The top-level CLAUDE.md at `c:\Source\SixFive7\StationeersPlus\CLAUDE.md`
enforces: before doing any work on a mod (code changes, debugging, feature
additions, refactors, content edits, build changes), read that mod's
`RESEARCH.md` in full if it exists. It documents architecture, patch
formulas, decompiled game internals, multiplayer protocol details, known
pitfalls, and the rationale behind past design decisions.

For StationpediaPlus work specifically:
- Before touching any mod's Stationpedia integration, read the mod's
  `RESEARCH.md`.
- After landing significant changes that affect architecture, patch
  catalogs, or game-internals understanding, update the relevant mod's
  `RESEARCH.md` and/or create a new StationpediaPlus-specific RESEARCH.md
  at `c:\Source\SixFive7\StationeersPlus\StationpediaPlus\RESEARCH.md`.
- This applies to sub-agents too. When delegating a mod task, instruct the
  sub-agent to read the mod's RESEARCH.md first.

### 11.8 Validate new lessons independently

Also from CLAUDE.md: any non-obvious finding surfaced during reverse-engineering
or deep investigation must be validated by a second, independent sub-agent
before being written into RESEARCH.md. The second agent receives the raw
question and sources with no exposure to the first agent's conclusions and
must reach the same conclusion independently. Speculation does not go into
RESEARCH.md; only verified, sourced findings.

### 11.9 Consuming-mod content sync rules (from CLAUDE.md)

These rules apply to each consuming mod (PowerTransmitterPlus,
SprayPaintPlus, EquipmentPlus, future mods) when StationpediaPlus
integration ships. They do NOT apply to StationpediaPlus itself (library
only; §18.8).

**README and About.xml sync.** User-facing content lives in three mirrored
places, each with its own markup:

- `README.md` is the source of truth (Markdown, rendered on GitHub).
- `About.xml` `<Description>` is the Steam Workshop mirror (BBCode:
  `[h1]`, `[b]`, `[list][*]`, `[url=...]`). Compressed from README.
- `About.xml` `<InGameDescription>` is the in-game mod-settings panel
  (Unity rich text: `<b>`, `<size>`, `<color>`). Tighter feature-list style.

When adding Stationpedia integration to a mod, decide whether to mention
the feature in README / About (typically yes, under a "Stationpedia
integration" section naming what pages the mod contributes and where
players find them). Mirror the mention in all three places.

**Version / ChangeLog bump.** When `PluginVersion` in the plugin's main
class changes, bump both:
- `About.xml` `<Version>` to the same value.
- Prepend a top-of-file entry to `CHANGELOG.md` with the matching
  version and date.

The StationpediaPlus migration is a meaningful user-visible change, so the
PowerTransmitterPlus migration commit bumps its PluginVersion (and
About.xml, and CHANGELOG.md).

**Reporting Issues section.** Every mod's README and About.xml
`<Description>` must include a "Reporting Issues" section directing
users to the mod's GitHub issues page (Steam Workshop comment
notifications are unreliable). `<InGameDescription>` does not need this
section.

**Preview image rules.** Every mod's preview art must be exact 16:9. Three
files per mod: `Preview.source.png` at repo root (archival), `About/Preview.png`
at 1280x720 (Steam listing), `About/thumb.png` at 640x360 (in-game mod
browser). Not applicable to StationpediaPlus (library, no Workshop listing).

---

## 12. Per-mod integration recipe

Every mod that uses StationpediaPlus follows this four-step ladder. Each
step is optional based on the mod's feature set.

### Step 1, Adding a new prefab (Kind A page)

Mod responsibility:
- Author `<RecordThing>` entry in the mod's language XML with display name and description.
- Implement `CanLogicRead`/`CanLogicWrite` on the device class.
- Ensure custom LogicTypes (if any) are in `EnumCollections.LogicTypes.Values`
  via `LogicableInitializePatch`.

Library responsibility: none. Vanilla `PopulateThingPages` handles it.

### Step 2, Extending a vanilla device (Kind C page)

Mod responsibility:

(a) Ensure custom LogicType rows appear natively by extending the enum
collection and postfixing `CanLogicRead`/`Write`. (Existing PowerTransmitterPlus pattern.)

(b) Call from `OnAllModsLoaded`:

```csharp
CategoryBuilder.Register(
    modName: "Power Transmitter Plus",
    deviceKeys: new[] {
        "ThingStructurePowerTransmitter",
        "ThingStructurePowerTransmitterReceiver",
    },
    contentBuilder: pageKey => pageKey switch {
        "ThingStructurePowerTransmitter" => TransmitterSectionMarkup,
        "ThingStructurePowerTransmitterReceiver" => ReceiverSectionMarkup,
        _ => null,
    });
```

### Step 3, Adding custom LogicTypes (Kind B page; auto-triggers SpaBridge)

Mod responsibility: call for each LogicType from `OnAllModsLoaded`:

```csharp
LogicTypePageBuilder.Register(new CustomLogicTypeSpec {
    Name = "MicrowaveSourceDraw",
    Value = 6571,
    DataType = "Float",
    Range = "0+",
    TooltipDescription = "Watts the transmitter pulls from source...",
    PageSummary = "Read-only. Watts the Microwave Power Transmitter...",
    FormulaOrBehavior = "source_draw = delivered * (1 + k * distance_km)",
    RelatedLogicTypeNames = new[] { "MicrowaveDestinationDraw", /* ... */ },
    RelatedDeviceKeys = new[] { "ThingStructurePowerTransmitter" },
    RelatedReferenceKeys = new[] { "PowerTransmitterPlus_MicrowavePowerTransmissionModel" },
});
```

Library automatically invokes `SpaBridge.TryEnrichLogicTooltips` for every
device in `RelatedDeviceKeys` (grouped per device). Mod never calls SpaBridge
directly in the common path.

### Step 4, Shared content (Kind D page)

Mod responsibility: call for each reference page from `OnAllModsLoaded`:

```csharp
ReferencePage.Register(
    topic: "MicrowavePowerTransmissionModel",
    titleRaw: "Microwave Power Transmission Model",
    bodyMarkup: transmissionModelMarkup);
```

Library registers the page and adds the key to its hidden-key set; filter is
installed lazily.

From extension sections or LogicType pages, reference the page via:

```
{LINK:PowerTransmitterPlus_MicrowavePowerTransmissionModel;Click here for the full model with worked examples}
```

### Recipe compatibility checks

- Always call helpers from `OnAllModsLoaded` (post `Harmony.PatchAll`).
- Harmony patch installation is lazy; first Register call per helper installs the patch.
- Register can be called multiple times; each call appends (idempotent replacement at game level via `Register(page, false)`).

---

## 13. PowerTransmitterPlus concrete implementation plan

### 13.1 Registry content

Mod adds custom LogicTypes (6), extension sections (2), and reference pages (2).

**CustomLogicTypeSpecs:** 6 entries, one per custom LogicType. See §13.6 for
full spec data.

**Extension sections:** 2 entries, one per extended device.
- `ThingStructurePowerTransmitter`, section markup in §13.2.
- `ThingStructurePowerTransmitterReceiver`, section markup in §13.3.

**Reference pages:** 2 entries.
- `PowerTransmitterPlus_MicrowavePowerTransmissionModel` (§13.4).
- `PowerTransmitterPlus_AutoAim` (§13.5).

### 13.2 Transmitter section markup

```
{HEADER:Distance cost model}
{LIST}This mod replaces the vanilla distance derate with an explicit per-watt overhead. Delivered power is unchanged; the transmitter's source draw grows with link distance.{/LIST}
<b>source_draw = delivered * (1 + k * distance_km)</b>
{LIST}where k is the server-authoritative DistanceCostFactor (default 5.0).{/LIST}
{LINK:PowerTransmitterPlus_MicrowavePowerTransmissionModel;Click here for the full model with worked examples}

{HEADER:Auto-aim}
{LIST}Write a target Thing's ReferenceId to {LOGICTYPE:MicrowaveAutoAimTarget} to slew the dish onto it. The dish uses its built-in servo; the base-game line-of-sight raycast decides when the link forms.{/LIST}
{LIST}Write 0 to disable auto-aim.{/LIST}
{LIST}Manually adjusting Horizontal or Vertical cancels auto-aim.{/LIST}
{LINK:PowerTransmitterPlus_AutoAim;Open the full auto-aim walkthrough}

{HEADER:Custom logic variables on this device}
{LIST}{LOGICTYPE:MicrowaveSourceDraw} {POS:260}Watts pulled from source, including distance overhead.{/LIST}
{LIST}{LOGICTYPE:MicrowaveDestinationDraw} {POS:260}Watts delivered to the receiver.{/LIST}
{LIST}{LOGICTYPE:MicrowaveTransmissionLoss} {POS:260}Overhead (source minus destination).{/LIST}
{LIST}{LOGICTYPE:MicrowaveEfficiency} {POS:260}Ratio of delivered to source.{/LIST}
{LIST}{LOGICTYPE:MicrowaveAutoAimTarget} {POS:260}Writable auto-aim target ReferenceId.{/LIST}
{LIST}{LOGICTYPE:MicrowaveLinkedPartner} {POS:260}Linked receiver ReferenceId, or 0 when unlinked.{/LIST}
```

### 13.3 Receiver section markup

```
{HEADER:Distance cost model}
{LIST}The paired transmitter now pays a per-kilometer overhead for power delivered to this receiver. The receiver still outputs the same wattage it always did.{/LIST}
<b>source_draw = delivered * (1 + k * distance_km)</b>
{LIST}where k is the server-authoritative DistanceCostFactor (default 5.0).{/LIST}
{LINK:PowerTransmitterPlus_MicrowavePowerTransmissionModel;Click here for the full model with worked examples}

{HEADER:Custom logic variables on this device}
{LIST}{LOGICTYPE:MicrowaveDestinationDraw} {POS:260}Watts delivered to this receiver's network.{/LIST}
{LIST}{LOGICTYPE:MicrowaveTransmissionLoss} {POS:260}Overhead paid by the linked transmitter.{/LIST}
{LIST}{LOGICTYPE:MicrowaveEfficiency} {POS:260}Ratio of delivered to source (read from either side).{/LIST}
{LIST}{LOGICTYPE:MicrowaveLinkedPartner} {POS:260}Linked transmitter ReferenceId, or 0 when unlinked.{/LIST}

{HEADER:Not available on receivers}
{LIST}{LOGICTYPE:MicrowaveSourceDraw} reads on the transmitter side and is not meaningful on a receiver.{/LIST}
{LIST}{LOGICTYPE:MicrowaveAutoAimTarget} is transmitter-only; receivers don't aim.{/LIST}
```

### 13.4 Reference page: `PowerTransmitterPlus_MicrowavePowerTransmissionModel`

Title: `Microwave Power Transmission Model`

Body markup:

```
{HEADER:Cost model}
{LIST}This mod replaces the vanilla distance derate on microwave power transmission with an explicit per-watt overhead that scales linearly with link distance in kilometers.{/LIST}

<b>source_draw = delivered * (1 + k * distance_km)</b>

{LIST}delivered = watts actually landing on the receiver.{/LIST}
{LIST}k = DistanceCostFactor, default 5.0. Server-authoritative; clients see the host's value.{/LIST}
{LIST}distance_km = straight-line distance between linked transmitter and receiver, in kilometers.{/LIST}

{HEADER:Derived quantities}
<b>transmission_loss = delivered * (multiplier - 1)</b>
<b>efficiency = 1 / multiplier</b>  when transmitting, 0 otherwise.
{LIST}where multiplier = (1 + k * distance_km).{/LIST}

{HEADER:Worked example at k=5, delivering 200W}
{LIST}Distance {POS:180}Source Draw {POS:320}Loss {POS:430}Efficiency{/LIST}
{LIST}0 m {POS:180}200 W {POS:320}0 W {POS:430}100%{/LIST}
{LIST}100 m {POS:180}300 W {POS:320}100 W {POS:430}67%{/LIST}
{LIST}500 m {POS:180}700 W {POS:320}500 W {POS:430}29%{/LIST}
{LIST}1 km {POS:180}1.2 kW {POS:320}1 kW {POS:430}17%{/LIST}
{LIST}5 km {POS:180}5.2 kW {POS:320}5 kW {POS:430}3.8%{/LIST}
{LIST}10 km {POS:180}10.2 kW {POS:320}10 kW {POS:430}2.0%{/LIST}

{HEADER:Related logic variables}
{LIST}{LOGICTYPE:MicrowaveSourceDraw}{/LIST}
{LIST}{LOGICTYPE:MicrowaveDestinationDraw}{/LIST}
{LIST}{LOGICTYPE:MicrowaveTransmissionLoss}{/LIST}
{LIST}{LOGICTYPE:MicrowaveEfficiency}{/LIST}

{HEADER:Device pages}
{LIST}{LINK:ThingStructurePowerTransmitter;Go to the Microwave Power Transmitter page}{/LIST}
{LIST}{LINK:ThingStructurePowerTransmitterReceiver;Go to the Microwave Power Receiver page}{/LIST}
```

### 13.5 Reference page: `PowerTransmitterPlus_AutoAim`

Title: `Auto-Aim`

Body markup:

```
{HEADER:Overview}
{LIST}Write a Thing's ReferenceId to {LOGICTYPE:MicrowaveAutoAimTarget} to aim the Microwave Power Transmitter's dish at that Thing. The dish slews via its built-in servo; the base-game line-of-sight raycast decides when the link actually forms.{/LIST}

{HEADER:Controls}
{LIST}Write the ReferenceId of the target Thing to enable.{/LIST}
{LIST}Write 0 to disable auto-aim.{/LIST}
{LIST}Writing an invalid or unresolved id is a no-op.{/LIST}
{LIST}Manually adjusting Horizontal or Vertical via tablet, IC10, or dish UI cancels auto-aim.{/LIST}
{LIST}Reading {LOGICTYPE:MicrowaveAutoAimTarget} returns the current target id, or 0 when disabled.{/LIST}

{HEADER:Geometry}
{LIST}Pivot-to-pivot: the dish aims its pivot at the target's pivot, not at any child transform. Rotation-invariant; suppresses self-referential error when the dish moves.{/LIST}

{HEADER:Device pages}
{LIST}{LINK:ThingStructurePowerTransmitter;Go to the Microwave Power Transmitter page}{/LIST}
```

### 13.6 Six CustomLogicTypeSpecs

`MicrowaveSourceDraw`:
- Name: "MicrowaveSourceDraw"
- Value: 6571
- DataType: "Float"
- Range: "0+"
- TooltipDescription: "Watts the transmitter pulls from its source network. Equals delivered * (1 + k * distance_km). Server-authoritative."
- PageSummary: "Read-only. Watts the Microwave Power Transmitter is pulling from its source cable network, including the per-kilometer distance overhead."
- FormulaOrBehavior: "source_draw = delivered * (1 + k * distance_km)"
- RelatedLogicTypeNames: [ "MicrowaveDestinationDraw", "MicrowaveTransmissionLoss", "MicrowaveEfficiency" ]
- RelatedDeviceKeys: [ "ThingStructurePowerTransmitter" ]
- RelatedReferenceKeys: [ "PowerTransmitterPlus_MicrowavePowerTransmissionModel" ]

`MicrowaveDestinationDraw`:
- Name: "MicrowaveDestinationDraw"
- Value: 6572
- DataType: "Float"
- Range: "0+"
- TooltipDescription: "Watts delivered to the receiver's downstream network. Mirrors PowerActual."
- PageSummary: "Read-only. Watts being delivered to the receiver's downstream cable network. Equal to the wireless link's actual throughput; mirrors PowerActual but named for clarity in the microwave context."
- FormulaOrBehavior: null
- RelatedLogicTypeNames: [ "MicrowaveSourceDraw", "MicrowaveEfficiency" ]
- RelatedDeviceKeys: [ "ThingStructurePowerTransmitter", "ThingStructurePowerTransmitterReceiver" ]
- RelatedReferenceKeys: [ "PowerTransmitterPlus_MicrowavePowerTransmissionModel" ]

`MicrowaveTransmissionLoss`:
- Name: "MicrowaveTransmissionLoss"
- Value: 6573
- DataType: "Float"
- Range: "0+"
- TooltipDescription: "Overhead watts lost to distance. Equal to source minus destination."
- PageSummary: "Read-only. Watts lost to distance overhead. Equal to MicrowaveSourceDraw minus MicrowaveDestinationDraw. Zero at zero distance or when k = 0."
- FormulaOrBehavior: "transmission_loss = delivered * (k * distance_km)"
- RelatedLogicTypeNames: [ "MicrowaveSourceDraw", "MicrowaveDestinationDraw" ]
- RelatedDeviceKeys: [ "ThingStructurePowerTransmitter", "ThingStructurePowerTransmitterReceiver" ]
- RelatedReferenceKeys: [ "PowerTransmitterPlus_MicrowavePowerTransmissionModel" ]

`MicrowaveEfficiency`:
- Name: "MicrowaveEfficiency"
- Value: 6574
- DataType: "Float"
- Range: "0-1"
- TooltipDescription: "Ratio delivered / source. 1.0 at zero distance; approaches 0 as distance grows."
- PageSummary: "Read-only. Ratio of delivered power to source draw, 0..1. 1.0 at zero distance or when k = 0; approaches 0 as distance grows."
- FormulaOrBehavior: "efficiency = 1 / (1 + k * distance_km)"
- RelatedLogicTypeNames: [ "MicrowaveSourceDraw", "MicrowaveDestinationDraw", "MicrowaveTransmissionLoss" ]
- RelatedDeviceKeys: [ "ThingStructurePowerTransmitter", "ThingStructurePowerTransmitterReceiver" ]
- RelatedReferenceKeys: [ "PowerTransmitterPlus_MicrowavePowerTransmissionModel" ]

`MicrowaveAutoAimTarget`:
- Name: "MicrowaveAutoAimTarget"
- Value: 6575
- DataType: "ReferenceId"
- Range: "0 or id"
- TooltipDescription: "Writable. Set to a Thing's ReferenceId to aim the dish at it. 0 disables auto-aim. Manual Horizontal or Vertical input cancels."
- PageSummary: "Writable. Set to a Thing's ReferenceId to aim the dish at that Thing; the dish slews via its built-in servo."
- FormulaOrBehavior: null
- RelatedLogicTypeNames: [ "MicrowaveLinkedPartner" ]
- RelatedDeviceKeys: [ "ThingStructurePowerTransmitter" ]
- RelatedReferenceKeys: [ "PowerTransmitterPlus_AutoAim" ]

`MicrowaveLinkedPartner`:
- Name: "MicrowaveLinkedPartner"
- Value: 6576
- DataType: "ReferenceId"
- Range: "0 or id"
- TooltipDescription: "Read-only. The linked partner dish's ReferenceId. 0 when unlinked."
- PageSummary: "Read-only. Returns the ReferenceId of the currently linked partner dish: on a transmitter this is the linked receiver; on a receiver this is the linked transmitter. Returns 0 when unlinked."
- FormulaOrBehavior: null
- RelatedLogicTypeNames: [ "MicrowaveAutoAimTarget" ]
- RelatedDeviceKeys: [ "ThingStructurePowerTransmitter", "ThingStructurePowerTransmitterReceiver" ]
- RelatedReferenceKeys: [ "PowerTransmitterPlus_AutoAim" ]

Note on `TooltipDescription`: SPA's tooltip UI renders plain text only and
does NOT parse vanilla markup tokens (`{LINK:...}`, `{LOGICTYPE:...}`, etc.).
Keep `TooltipDescription` as plain prose. The `PageSummary` field, by
contrast, feeds the enriched page body (§8.9) and is parsed by
`Localization.ParseHelpText`; markup tokens are allowed and rendered.

### 13.6a Fully authored LogicType page bodies (reference)

The `LogicTypePageBuilder.Register(spec)` implementation composes the final
page `Text` from the spec fields using the template in §8.9. This
subsection shows what the final composed body looks like for each of the
six specs above, so a reader can verify the output without hand-walking
the algorithm.

These strings are produced by the library automatically; they are NOT
authored constants in `PtpStationpediaContent.cs`. They appear here for
cross-reference with §14 test plans and §13.3/§13.4 hand-authored markup.

`LogicTypeMicrowaveSourceDraw`:
```
{HEADER:Summary}
{LIST}Read-only. Watts the Microwave Power Transmitter is pulling from its source cable network, including the per-kilometer distance overhead.{/LIST}

{HEADER:Formula}
{LIST}source_draw = delivered * (1 + k * distance_km){/LIST}

{HEADER:Related}
{LIST}
{LOGICTYPE:MicrowaveDestinationDraw}
{LOGICTYPE:MicrowaveTransmissionLoss}
{LOGICTYPE:MicrowaveEfficiency}
{LINK:ThingStructurePowerTransmitter;Open the Microwave Power Transmitter page}
{LINK:PowerTransmitterPlus_MicrowavePowerTransmissionModel;Open the full cost model reference}
{/LIST}
```

`LogicTypeMicrowaveDestinationDraw`:
```
{HEADER:Summary}
{LIST}Read-only. Watts being delivered to the receiver's downstream cable network. Equal to the wireless link's actual throughput; mirrors PowerActual but is named for clarity in the microwave context.{/LIST}

{HEADER:Related}
{LIST}
{LOGICTYPE:MicrowaveSourceDraw}
{LOGICTYPE:MicrowaveEfficiency}
{LINK:ThingStructurePowerTransmitter;Open the Microwave Power Transmitter page}
{LINK:ThingStructurePowerTransmitterReceiver;Open the Microwave Power Receiver page}
{LINK:PowerTransmitterPlus_MicrowavePowerTransmissionModel;Open the full cost model reference}
{/LIST}
```

`LogicTypeMicrowaveTransmissionLoss`:
```
{HEADER:Summary}
{LIST}Read-only. Watts lost to distance overhead. Equal to MicrowaveSourceDraw minus MicrowaveDestinationDraw. Zero at zero distance or when k = 0.{/LIST}

{HEADER:Formula}
{LIST}transmission_loss = delivered * (k * distance_km){/LIST}

{HEADER:Related}
{LIST}
{LOGICTYPE:MicrowaveSourceDraw}
{LOGICTYPE:MicrowaveDestinationDraw}
{LOGICTYPE:MicrowaveEfficiency}
{LINK:ThingStructurePowerTransmitter;Open the Microwave Power Transmitter page}
{LINK:ThingStructurePowerTransmitterReceiver;Open the Microwave Power Receiver page}
{LINK:PowerTransmitterPlus_MicrowavePowerTransmissionModel;Open the full cost model reference}
{/LIST}
```

`LogicTypeMicrowaveEfficiency`:
```
{HEADER:Summary}
{LIST}Read-only. Ratio of delivered power to source draw, 0..1. 1.0 at zero distance or when k = 0; approaches 0 as distance grows.{/LIST}

{HEADER:Formula}
{LIST}efficiency = 1 / (1 + k * distance_km){/LIST}

{HEADER:Related}
{LIST}
{LOGICTYPE:MicrowaveSourceDraw}
{LOGICTYPE:MicrowaveDestinationDraw}
{LOGICTYPE:MicrowaveTransmissionLoss}
{LINK:ThingStructurePowerTransmitter;Open the Microwave Power Transmitter page}
{LINK:ThingStructurePowerTransmitterReceiver;Open the Microwave Power Receiver page}
{LINK:PowerTransmitterPlus_MicrowavePowerTransmissionModel;Open the full cost model reference}
{/LIST}
```

`LogicTypeMicrowaveAutoAimTarget`:
```
{HEADER:Summary}
{LIST}Writable. Set to a Thing's ReferenceId to aim the dish at that Thing; the dish slews via its built-in servo.{/LIST}

{HEADER:Related}
{LIST}
{LOGICTYPE:MicrowaveLinkedPartner}
{LINK:ThingStructurePowerTransmitter;Open the Microwave Power Transmitter page}
{LINK:PowerTransmitterPlus_AutoAim;Open the auto-aim walkthrough reference}
{/LIST}
```

`LogicTypeMicrowaveLinkedPartner`:
```
{HEADER:Summary}
{LIST}Read-only. Returns the ReferenceId of the currently linked partner dish: on a transmitter this is the linked receiver; on a receiver this is the linked transmitter. Returns 0 when unlinked.{/LIST}

{HEADER:Related}
{LIST}
{LOGICTYPE:MicrowaveAutoAimTarget}
{LINK:ThingStructurePowerTransmitter;Open the Microwave Power Transmitter page}
{LINK:ThingStructurePowerTransmitterReceiver;Open the Microwave Power Receiver page}
{LINK:PowerTransmitterPlus_AutoAim;Open the auto-aim walkthrough reference}
{/LIST}
```

Note on click-phrasing: every `{LINK:...}` display label follows the §11.3
rule ("Open the ... page"). The `LogicTypePageBuilder` implementation must
generate these labels deterministically from the target key; if the
human-readable target name is not available at code-gen time, it falls
back to "Open this page" which still complies.

### 13.7 Migration steps

1. **Cross-mod stub scan.** Before ILRepack integration, scan
   PowerTransmitterPlus (and every other SixFive7 mod that will later use
   StationpediaPlus) for:
   - `StationpediaPatches.cs` or similar files with placeholder registration
   - Harmony postfixes on `Stationpedia.PopulateLogicVariables`,
     `Stationpedia.PopulateThingPages`, `UniversalPage.ChangeDisplay` in
     mod-owned code
   - Direct `Stationpedia.Register(...)` calls in mod-owned code
   - Manual `StationpediaPage` or `StationLogicInsert` construction in
     mod-owned code
   - Reflection into `Stationpedia.*` types in mod-owned code
   Delete or migrate each finding to the appropriate helper.

2. **Build StationpediaPlus library.** Implement per §8 and §9.

3. **Integrate into PowerTransmitterPlus.**
   - Reference `StationpediaPlus.dll` from the mod's .csproj.
   - Add ILRepack task to merge at build time.
   - Delete `StationpediaPatches.cs`.
   - Add `PtpStationpediaContent.cs` with the markup constants and specs.
   - In `Plugin.OnAllModsLoaded`, after `harmony.PatchAll()` and
     `Ic10ConstantsPatcher.Apply()`, call in order:
     ```
     ReferencePage.Register(...) x2  // MicrowavePowerTransmissionModel, AutoAim
     LogicTypePageBuilder.Register(...) x6  // six specs
     CategoryBuilder.Register(...)  // with both device keys and dispatching builder
     ```
   - (SpaBridge is auto-invoked by LogicTypePageBuilder; no explicit call.)

4. **Single migration commit.** One atomic commit that:
   - Deletes old stub.
   - Adds new content file.
   - Updates Plugin.cs.
   - Updates .csproj with StationpediaPlus reference and ILRepack config.
   Atomic revert remains available if post-ship issues surface.

---

## 14. Testing matrix

Run every test twice: once with SPA installed, once without SPA. Tests T11
and T12 apply only to mods using SpaBridge (i.e. adding custom LogicTypes).

### T-Native-Rows (release-blocking; from Decision 11A)

Open each extended device page (transmitter and receiver). Every registered
custom LogicType appears as a row in the vanilla Logic Variables category
with correct access string (Read / Write / Read/Write). Orange clickable
LogicName column. If any row is missing, it is a release-blocker bug;
investigate timing of `EnumCollections.LogicTypes.Values` extension vs
`CanLogicRead/Write` postfixes; do NOT add a fallback.

### T1, Native rows with content

Click each custom LogicType row. Navigates to the enriched LogicType page
(Summary / Formula / Related sections populated).

### T2, Extension section renders

Scroll to bottom of transmitter page. One collapsible category
`<color=#FF7A18>Power Transmitter Plus Details</color>`, collapsed by
default. Click to expand. Three subsections render correctly: Distance cost
model, Auto-aim, Custom logic variables. Repeat on receiver (three
different subsections).

### T3, Link clicks work inside extension sections

Inside expanded transmitter section, click "Click here for the full model
with worked examples". Navigates to
`PowerTransmitterPlus_MicrowavePowerTransmissionModel` page. Reference
page renders with full body.

### T4, Reference page cross-links resolve

On the reference page, click "Go to the Microwave Power Transmitter page".
Navigates back to the transmitter page. Our extension section is still
present on re-render (CreatedCategories + CategoryBuilder postfix pattern).

### T5, Reference page hidden from search

Open search bar. Type `transmission model`. No result for
`PowerTransmitterPlus_MicrowavePowerTransmissionModel`. Type the exact
title: still no match. Type `auto-aim`: no match for the auto-aim reference
page. Vanilla transmitter page may match if description contains the term.

### T5a, Reference page absent from SPA-reorganized search (SPA only; decision 19)

With SPA installed. Open search bar; type `transmission model`. Wait 1 full
second for SPA's `ReorganizeSearchResults` coroutine to fire (0.3s on submit,
0.8s on value-changed; 1s is a safe ceiling). Reference page
`PowerTransmitterPlus_MicrowavePowerTransmissionModel` does NOT appear in the
re-injected results either. Repeat for `auto-aim` against
`PowerTransmitterPlus_AutoAim`. Confirms `SpaSearchFilter` is active and its
reflection path resolved correctly.

If T5a fails (reference page appears after the delay), `SpaSearchFilter`
failed to install. Most likely causes: (a) SPA's method name differs from
`ShouldHideFromSearch`, read `SearchPatches.cs` and try fallbacks
(`BuildPageIndexes` Postfix, `ReorganizeSearchResults` Postfix); (b) reflection
exception swallowed, check BepInEx log for the LogDebug entry from the
soft-detect path.

### T6, Reference page absent from home-page listings

Open home page. Browse every category listing and the Guides/Lore tabs.
Reference pages are absent from every listing.

### T7, SPA installed: visual coexistence

Open transmitter page. SPA's Operational Details category (empty;
operationalDetails: [] in JSON) at sibling 20. Our Power Transmitter Plus
Details at sibling 21. No collision, distinct GameObject names.

### T8, SPA installed: custom tooltip rendering

Hover a `MicrowaveSourceDraw` row. Tooltip shows our
TooltipDescription text ("Watts the transmitter pulls from its source
network..."), not SPA's placeholder ("No detailed description available
yet."). Same for all six custom rows.

Hover a vanilla Power row. SPA's standard tooltip shows. Unaffected by our mod.

### T9, SPA uninstalled

All T1-T6 still pass. No Harmony patch failures in BepInEx log. Hover does
nothing on any row (vanilla has no tooltip system).

### T10, Language change mid-session

Change game language. Revisit transmitter page. Extension section still
present. Reference pages still hidden. No duplicate categories. No errors.

### T11, SpaBridge reflection verification (SpaBridge-using mods only)

With SPA installed, use a runtime debug command or log statement to inspect
SPA's `DeviceDatabase["ThingStructurePowerTransmitter"].logicDescriptions`.
Confirm all six of our `LogicDescription` entries are present with correct
`dataType`, `range`, `description`. Same for
`ThingStructurePowerTransmitterReceiver`.

### T12, SpaBridge silent-degradation verification

Uninstall SPA. Boot the game. Expected:
- No BepInEx exceptions related to StationpediaPlus or SpaBridge.
- LogDebug entry confirming "SPA not installed; SpaBridge skipped".
- All other behavior identical to T9.

### T13, Idempotency

Navigate to transmitter, away, back, 10 times in succession. Exactly one
extension section instance present each time. No duplicates, no orphans.

---

## 15. Implementation phase plan

Implementer should work these phases in order. Each phase produces a
testable artifact.

### Phase 0, Pre-implementation verification (5 minutes, no code)

Before spending any implementation time on StationpediaPlus, verify that
PowerTransmitterPlus's existing patches already cause our six custom
LogicType rows to appear natively on the Microwave Power Transmitter and
Receiver Stationpedia pages.

Steps:
1. Build the current PowerTransmitterPlus (no changes).
2. Deploy to game install per the mod's existing deployment workflow.
3. Launch game; open Stationpedia; navigate to Microwave Power Transmitter.
4. Scroll to the Logic Variables category.
5. Confirm all six custom types present as orange-link rows:
   `MicrowaveSourceDraw`, `MicrowaveDestinationDraw`,
   `MicrowaveTransmissionLoss`, `MicrowaveEfficiency`,
   `MicrowaveAutoAimTarget`, `MicrowaveLinkedPartner`.
6. Repeat on Microwave Power Receiver (expect four: no `MicrowaveSourceDraw`,
   no `MicrowaveAutoAimTarget`, both transmitter-only).

Rationale: Decision 11 is firm on "no LogicInsert fallback"; if native
rendering fails, the fix is a root-cause correction in the existing
patches, not a fallback workaround. Catching this at Phase 0 saves
rework later. If Phase 0 passes, proceed confidently through Phase 1-4
knowing the rendering baseline is already correct. If Phase 0 fails,
investigate timing of `LogicableInitializePatch` vs
`EnumCollections.LogicTypes.Values` construction and `CanLogicRead`/`Write`
postfix firing before continuing.

### Phase 1, StationpediaPlus project skeleton

Create `c:\Source\SixFive7\StationeersPlus\StationpediaPlus\`:
- `.gitignore` excluding `Directory.Build.props` and `bin/`, `obj/`.
- `Directory.Build.props.template` with placeholder path.
- `StationpediaPlus.csproj` per §16.2.
- `src/`, `src/Internal/` empty folders.

Build produces empty DLL; confirms references resolve.

### Phase 2, SixFive7LinkHandler

Smallest component; no dependencies. Implement and build.

### Phase 3, Internal helpers

- `HarmonyIdHelper.cs`
- `TextElementFactory.cs` (depends on SixFive7LinkHandler)
- `SearchFilterPatch.cs` (depends on HarmonyIdHelper)
- `SpaSearchFilter.cs` (depends on HarmonyIdHelper; soft-dependent on SPA)

### Phase 4, Public helpers

Implement in order of independence:
- `SpaBridge.cs` (no helper dependencies)
- `ReferencePage.cs` (depends on SearchFilterPatch and SpaSearchFilter)
- `CategoryBuilder.cs` (depends on TextElementFactory, HarmonyIdHelper)
- `LogicTypePageBuilder.cs` (depends on SpaBridge, HarmonyIdHelper)

### Phase 5, Stub scan and cleanup

Run the cross-mod stub scan (§13.7 step 1). Document findings.

### Phase 6, PowerTransmitterPlus migration

- Add StationpediaPlus.dll reference.
- Configure ILRepack (choose tooling option).
- Delete `StationpediaPatches.cs`.
- Add `PtpStationpediaContent.cs` per §13.
- Update `Plugin.cs` per §13.7 step 3.

### Phase 7, Runtime verification

Run the 13-test matrix (§14). All must pass. T-Native-Rows is
release-blocking; if it fails, root-cause before shipping.

### Phase 8, CLAUDE.md integration

Add the Stationpedia integration recipe to top-level CLAUDE.md at
`c:\Source\SixFive7\StationeersPlus\CLAUDE.md`. See §11 and §12 for content.
Draft text:

```markdown
## Content: Stationpedia integration

Every mod that adds or modifies in-game content integrates with the
Stationpedia through the shared StationpediaPlus library (ILRepacked into
each mod, no separate distribution). Follow this four-step ladder.

1. Adding a new prefab? Author its description in the mod's language XML.
   The game generates the Stationpedia page automatically. Implement
   CanLogicRead / CanLogicWrite on the device class.

2. Modifying a vanilla prefab? Postfix CanLogicRead / CanLogicWrite for any
   custom LogicTypes and extend EnumCollections.LogicTypes.Values via
   LogicableInitializePatch. Call StationpediaPlus.CategoryBuilder.Register
   from OnAllModsLoaded with the mod's display name, the affected device
   keys, and a content builder.

3. Adding custom LogicTypes? Call StationpediaPlus.LogicTypePageBuilder.Register
   for each custom LogicType with a fully filled CustomLogicTypeSpec
   (Name, Value, DataType, Range, TooltipDescription, PageSummary, plus
   RelatedDeviceKeys). SpaBridge enrichment is invoked automatically for
   every device in RelatedDeviceKeys; no explicit SpaBridge call needed.

4. Shared content referenced from multiple pages? Promote to a reference
   page via StationpediaPlus.ReferencePage.Register(topic, titleRaw,
   bodyMarkup). Key is <FullModName>_<Topic>. Linkable but filtered from
   search and home-page listings. Link from referrers via
   {LINK:<Key>;Click here for ...}.

Key conventions:
- Thing pages: Thing<PrefabName> (game-controlled).
- LogicType pages: LogicType<Name> (game-controlled; body enriched by us).
- Reference pages: <FullModName>_<Topic> (mod-controlled).
- Extension section GameObject: <ModName>Details.
- Section title color: #FF7A18 (uniform across all StationeersPlus mods).
- Sibling index 21 (SPA uses 20).

Authoring rules:
- No abbreviations in any user-visible string.
- Every {LINK:Key;Display} label must contain explicit click phrasing (e.g.
  "Click here for ...", "Go to the ... page", "Open the ... walkthrough")
  because the custom click handler does not provide vanilla hover feedback.
- {LOGICTYPE:Name} and {THING:PrefabName} are exempt; game-parsed colors
  signal linkability.
- No literal [ or ] in markup; ParsePage rewrites them.

Do NOT:
- Hard-depend on Stationpedia Ascended. All integration is soft-detected.
- Name a custom category GameObject OperationalDetailsCategory.
- Directly call Stationpedia.Register or construct StationpediaPage in mod
  code. Use the helpers.
- Directly call SpaBridge in the common path; LogicTypePageBuilder invokes
  it automatically.
```

### Phase 9, Document this pattern for future mods

Once Phase 7 passes, snapshot the successful PowerTransmitterPlus integration
as the canonical example for SprayPaintPlus, EquipmentPlus, and future mods.

---

## 16. File and path inventory

### 16.1 New files to create

```
c:\Source\SixFive7\StationeersPlus\StationpediaPlus\
  StationpediaPlus.csproj
  Directory.Build.props.template
  Directory.Build.props                           (gitignored)
  .gitignore
  src\
    CategoryBuilder.cs
    ReferencePage.cs
    LogicTypePageBuilder.cs
    SpaBridge.cs
    SixFive7LinkHandler.cs
    Internal\
      SearchFilterPatch.cs
      SpaSearchFilter.cs
      TextElementFactory.cs
      HarmonyIdHelper.cs
```

### 16.2 StationpediaPlus.csproj template

Copy from `EquipmentPlus.csproj` (closest existing template: UI +
TextMeshPro + EventSystems). Key differences:
- `RootNamespace` = `StationpediaPlus`
- `AssemblyName` = `StationpediaPlus`
- Generate new `ProjectGuid`
- Remove `LaunchPadBooster` reference (not needed for UI library).
- `OutputType` = Library.
- No `Content` items (no About.xml, no preview images; library, not plugin).

Required references:

| Reference | HintPath | Private |
|---|---|---|
| `0Harmony` | `$(StationeersPath)\BepInEx\core\0Harmony.dll` | False |
| `Assembly-CSharp` | `$(StationeersPath)\rocketstation_Data\Managed\Assembly-CSharp.dll` | False |
| `BepInEx` | `$(StationeersPath)\BepInEx\core\BepInEx.dll` | False |
| `UnityEngine` | `$(StationeersPath)\rocketstation_Data\Managed\UnityEngine.dll` | False |
| `UnityEngine.CoreModule` | `$(StationeersPath)\rocketstation_Data\Managed\UnityEngine.CoreModule.dll` | False |
| `UnityEngine.UI` | `$(StationeersPath)\rocketstation_Data\Managed\UnityEngine.UI.dll` | False |
| `UnityEngine.EventSystems` | `$(StationeersPath)\rocketstation_Data\Managed\UnityEngine.EventSystems.dll` | False |
| `Unity.TextMeshPro` | `$(StationeersPath)\rocketstation_Data\Managed\Unity.TextMeshPro.dll` | False |

Plus framework refs: `System`, `System.Core`, `Microsoft.CSharp`.

Required MSBuild validation target (standard across SixFive7 mods):

```xml
<Target Name="EnsureStationeersPath" BeforeTargets="ResolveAssemblyReferences">
  <Error Condition="'$(StationeersPath)' == ''" Text="StationeersPath is not set. Copy Directory.Build.props.template to Directory.Build.props and edit it to point at your Stationeers install." />
  <Error Condition="'$(StationeersPath)' != '' AND !Exists('$(StationeersPath)\rocketstation_Data\Managed\Assembly-CSharp.dll')" Text="StationeersPath '$(StationeersPath)' does not contain rocketstation_Data\Managed\Assembly-CSharp.dll. Check the path in Directory.Build.props." />
</Target>
```

Compile include pattern:

```xml
<ItemGroup>
  <Compile Include="src\CategoryBuilder.cs" />
  <Compile Include="src\ReferencePage.cs" />
  <Compile Include="src\LogicTypePageBuilder.cs" />
  <Compile Include="src\SpaBridge.cs" />
  <Compile Include="src\SixFive7LinkHandler.cs" />
  <Compile Include="src\Internal\SearchFilterPatch.cs" />
  <Compile Include="src\Internal\SpaSearchFilter.cs" />
  <Compile Include="src\Internal\TextElementFactory.cs" />
  <Compile Include="src\Internal\HarmonyIdHelper.cs" />
  <Compile Include="Properties\AssemblyInfo.cs" />
</ItemGroup>
```

### 16.3 Directory.Build.props.template content

```xml
<!--
  LOCAL BUILD CONFIGURATION

  Copy this file to "Directory.Build.props" (same folder) and edit
  StationeersPath to point at your Stationeers install root. The copy is
  gitignored so each developer keeps their own path.
-->
<Project>
  <PropertyGroup>
    <StationeersPath>C:\Program Files (x86)\Steam\steamapps\common\Stationeers</StationeersPath>
  </PropertyGroup>
</Project>
```

### 16.4 Files to modify in PowerTransmitterPlus

```
c:\Source\SixFive7\StationeersPlus\PowerTransmitterPlus\PowerTransmitterPlus\
  Plugin.cs                   MODIFY: add StationpediaPlus calls in OnAllModsLoaded
  PowerTransmitterPlus.csproj MODIFY: reference StationpediaPlus.dll; add ILRepack
  PtpStationpediaContent.cs   NEW: markup constants, specs
  LogicTypeRegistry.cs        RETAIN: values still referenced by CustomLogicTypeSpec
```

### 16.5 Files to delete

```
c:\Source\SixFive7\StationeersPlus\PowerTransmitterPlus\PowerTransmitterPlus\
  StationpediaPatches.cs      DELETE: superseded by StationpediaPlus
```

### 16.6 Template mod to clone for .csproj

`c:\Source\SixFive7\StationeersPlus\EquipmentPlus\EquipmentPlus\EquipmentPlus.csproj`
,  closest to StationpediaPlus's shape (UI + TextMeshPro; missing only EventSystems).

### 16.7 Game decompile path

Developer-local path to the decompiled `Assembly-CSharp` source tree,
documented in `DEV.md` (gitignored) as `<STATIONEERS_DECOMPILE>`. All
line-number anchors into that decompile have been drained to central
Research pages where each page pins the relevant symbols, method names,
and control-flow without relying on absolute line numbers that drift with
game updates.

Full content lifted to:
- [StationpediaPageRendering](../../Research/GameSystems/StationpediaPageRendering.md) - Canonical method bodies and lifecycle ordering for Stationpedia vanilla code; consult instead of raw line anchors.
- [StationpediaSearch](../../Research/GameSystems/StationpediaSearch.md) - Search path methods (`DoSearch`, `IsRegexMatch`, `PopulateGuideLoreContents`).
- [LogicType](../../Research/GameSystems/LogicType.md) - `EnumCollections.LogicTypes` declaration and injection points.

Lifted during Phase 5 migration on 2026-04-20 (drop; game.cs line-number index not migrated to central). Original content preserved in git history at `<pre-lift-sha>:Plans/StationpediaPlus/PLAN.md:2950-3032` (§18.1 line-reference table below has the same treatment).

### 16.8 SPA decompile path

Developer-local path to the SPA decompile tree, documented in `DEV.md`
(gitignored) as `<SPA_DECOMPILE>`. Reference symbols and files are
cataloged on the central SPA pages rather than via absolute filesystem
paths.

Full content lifted to:
- [StationpediaAscendedInternals](../../Research/GameSystems/StationpediaAscendedInternals.md) - File walkthrough by SPA component (`StationpediaAscendedMod.cs`, `HarmonyPatches.cs`, `SearchPatches.cs`, Data/* types, UI/* handlers).
- [SPADeviceDatabase](../../Research/Protocols/SPADeviceDatabase.md) - `descriptions.json` schema reference.

Lifted during Phase 5 migration on 2026-04-20. Original content preserved in git history at `<pre-lift-sha>:Plans/StationpediaPlus/PLAN.md:2885-2895`.

---

## 17. Open items

### O1, ILRepack tooling choice (DEFERRED)

Two options:
- `ILRepack.Lib.MSBuild.Task` NuGet package
- Standalone `ilrepack.exe` binary

Decide at Phase 6 when integrating into PowerTransmitterPlus. No existing
SixFive7 mod uses ILRepack, so this choice sets the pattern.

### O2, Mod GUID discovery for HarmonyIdHelper

At runtime, `HarmonyIdHelper.ForMod(helperName)` needs to know the consuming
mod's plugin GUID. Options:
- Discover from `Chainloader.PluginInfos` by finding the plugin whose assembly contains the calling method.
- Each consuming mod registers its GUID once in a `StationpediaPlus.SetOwningModGuid(guid)` init call.

Decide at Phase 4 when implementing HarmonyIdHelper.

### O3, Runtime verification items flagged by T-Native-Rows

If T-Native-Rows fails, investigate:
- `LogicableInitializePatch` timing vs `EnumCollections.LogicTypes.Values` freeze point.
- `CanLogicRead`/`Write` postfix firing during `AddLogicTypeInfo` iteration.
- Whether our custom LogicType values are present in `EnumCollections.LogicTypes.Values` at the moment `Regenerate` fires.

The fix is a root-cause correction, NOT addition of a LogicInsert fallback
(Decision 11A is firm on this).

### O4, SPA-cache re-injection (RESOLVED by decision 19 ii)

**Status: resolved.** SPA's `ReorganizeSearchResults` coroutine re-injects
search results from `_pageTitleIndex` (built by `BuildPageIndexes` from
`Stationpedia.StationpediaPages`). SPA's own `ShouldHideFromSearch` filter
hides only burnt / ruptured / wreckage variants and knows nothing about our
`HiddenKeys`. Without intervention, hidden reference pages appear in
SPA-augmented search results 0.3 to 0.8 seconds after vanilla search runs.

Resolution (decision 19 ii): `SpaSearchFilter` (new internal helper at
`src/Internal/SpaSearchFilter.cs`) installs a soft-dependent Harmony Postfix
on SPA's `ShouldHideFromSearch` that ORs in our `HiddenKeys` HashSet.
Activated automatically by `ReferencePage.Register` via
`SpaSearchFilter.EnsureInstalled()`. Silent no-op when SPA is absent or its
method has been renamed. Runtime verification via T5a.

See §8.11a and §19 Decision 19 for full details.

---

## 18. Appendices

### 18.1 Game.cs line reference table

The flat game.cs line-number index that once sat here was a developer-local
mapping of ~70 line anchors into the decompile. It has been dropped per
Phase 2/3 rule M1 (line numbers drift with every game update and pinned
absolute paths are developer-specific). The durable facts those anchors
pointed at are captured on the central Stationpedia pages by symbol name
and method body rather than by line number.

Full content lifted to:
- [Stationpedia](../../Research/GameClasses/Stationpedia.md) - Class body, `Register`, `SetPage`, `Regenerate`, `OnPageChanged` event, `Initialize`, `UpdateLinkedPages`.
- [StationpediaPage](../../Research/GameClasses/StationpediaPage.md) - Class body, constructors, `ParsePage`, `IsRegexMatch`, `Parsed` getter, 255-char cutoff.
- [UniversalPage](../../Research/GameClasses/UniversalPage.md) - Class body, `ChangeDisplay`, `PopulateLogicInserts`, 19-category field declarations, `CreatedCategories`.
- [StationpediaPageRendering](../../Research/GameSystems/StationpediaPageRendering.md) - `Regenerate`, `PopulateThingPages`, `PopulateLogicVariables`, `AddLogicTypeInfo`, ChangeDisplay Populate* call range.
- [StationpediaSearch](../../Research/GameSystems/StationpediaSearch.md) - `DoSearch`, search trigger chain, `PopulateGuideLoreContents`, `ClearPreviousSearch`.
- [StationpediaMarkup](../../Research/GameSystems/StationpediaMarkup.md) - `Localization.ParseHelpText`, `ReplaceHelpHeadings`, `ReplaceLogicTypes`.
- [HelpLinkHandler](../../Research/GameClasses/HelpLinkHandler.md) - Class body, `OnPointerClick`, `HelpReference.IsRegexMatch` disambiguation.
- [LogicType](../../Research/GameSystems/LogicType.md) - `EnumCollections.LogicTypes`, `EnumCollection<T1,T2>` ctor, `Logicable.Initialize`, `Logicable.CanLogicRead`/`CanLogicWrite` switches.
- [PowerTransmitter](../../Research/GameClasses/PowerTransmitter.md) - `PowerTransmitter` / `PowerReceiver` class bodies.

Lifted during Phase 5 migration on 2026-04-20 (drop; flat line index not migrated). Original content preserved in git history at `<pre-lift-sha>:Plans/StationpediaPlus/PLAN.md:2950-3032`.

### 18.1a /spda_dumpkeys console command

SPA registers a BepInEx console command `/spda_dumpkeys` that iterates
`Stationpedia.StationpediaPages` and prints every page's Key, Title, and
DisplayFilter to the BepInEx console. Useful during implementation
debugging to confirm our reference pages are registered, hidden keys are
in `StationpediaPages`, and there are no key collisions. Run interactively;
available only when SPA is installed.

Full content lifted to:
- [SPADumpKeys](../../Research/Workflows/SPADumpKeys.md) - Command invocation, output format, diagnostic checklist for confirming reference-page registration.

Lifted during Phase 5 migration on 2026-04-20. Original content preserved in git history at `<pre-lift-sha>:Plans/StationpediaPlus/PLAN.md:3034-3050`.

### 18.2 SPA reference

SPA identity constants, decompile file walkthrough (per-class key methods,
`ApplyHarmonyPatches` entry site, `ChangeDisplay_Postfix` line, tooltip
attach site), and shipped `descriptions.json` entry anchors for the three
microwave transmitter prefabs.

Full content lifted to:
- [ThirdPartyModIdentities](../../Research/GameSystems/ThirdPartyModIdentities.md) - SPA plugin GUID, Harmony IDs, Workshop ID, version.
- [StationpediaAscendedInternals](../../Research/GameSystems/StationpediaAscendedInternals.md) - File walkthrough by component, key method anchors by name.
- [SPADeviceDatabase](../../Research/Protocols/SPADeviceDatabase.md) - `descriptions.json` coverage for transmitter prefabs, loading order, shipped metrics.

Lifted during Phase 5 migration on 2026-04-20. Original content preserved in git history at `<pre-lift-sha>:Plans/StationpediaPlus/PLAN.md:3052-3102`.

### 18.3 Language files

```
<StationeersInstall>\rocketstation_Data\StreamingAssets\Language\
  english.xml           - RecordThing entries (device names + descriptions)
                        - Microwave Power Transmitter: line 6769
                        - Microwave Power Receiver: line 6773
  english_help.xml      - HelpPage templates (LogicTypePageTemplate etc.)
                        - Not directly modified by our mod
```

### 18.4 CLAUDE.md reference

```
c:\Source\SixFive7\StationeersPlus\CLAUDE.md
```

Key conventions enforced (all apply to StationpediaPlus):
- `$(StationeersPath)` externalization (no hardcoded paths).
- `Directory.Build.props.template` committed; `Directory.Build.props` gitignored.
- `BeforeTargets="ResolveAssemblyReferences"` validation target in every .csproj.
- Apache 2.0 license for every mod (StationpediaPlus is a library, same license).
- `LICENSE` file (full, unmodified Apache 2.0 text) at every repo root.
- `NOTICE` file naming the copyright holder (SixFive7) and the mod.
- Style rules (no em dashes, no ellipsis char, no curly quotes, no AI mentions).
- Preview image dimensions (not applicable to StationpediaPlus, library, no Preview.png).
- Mandatory RESEARCH.md reading before any mod work (§11.7).
- Validate new lessons via independent second sub-agent (§11.8).
- Reporting Issues section in every mod's README and About.xml pointing to GitHub issues.

`DEV.md` (one level up at `c:\Source\SixFive7\StationeersPlus\DEV.md`, not in
any mod's repo, never committed) contains machine-specific paths (game
install, deploy target, MSBuild, log paths, InspectorPlus request/snapshot
directories). Shared across every mod in the directory.

### 18.5 Directory structure under StationeersPlus

Sibling mods to StationpediaPlus at `c:\Source\SixFive7\StationeersPlus\`:

```
StationeersPlus/
  CLAUDE.md                 (project-level conventions; committed to every mod's repo)
  DEV.md                    (machine-specific; gitignored; one level above mods)
  PowerTransmitterPlus/     (first consumer of StationpediaPlus)
  SprayPaintPlus/           (future consumer)
  EquipmentPlus/            (future consumer)
  InspectorPlus/            (local-only BepInEx plugin for runtime state dumps; see §18.6)
  LLM/                      (LLM integration experiment)
  StationpediaPlus/         (NEW, this library)
```

Each mod directory is an independent git repository with its own
`Directory.Build.props.template`, `.gitignore`, and `Directory.Build.props`.

### 18.6 InspectorPlus (live runtime state dumping)

`InspectorPlus` is a local-only BepInEx plugin (NOT a Workshop mod) that
dumps live game state to JSON on demand. Useful during T-Native-Rows
debugging (§17 O3) if we need to verify `EnumCollections.LogicTypes.Values`
contains our custom values, or inspect `page.LogicInsert` contents at
runtime.

Two triggers:
- Drop a request JSON into `<GameInstall>\BepInEx\inspector\requests\`
  specifying types and fields to inspect. The plugin writes a snapshot to
  `<GameInstall>\BepInEx\inspector\snapshots\snapshot_<timestamp>.json` and
  deletes the request. This is the programmatic path.
- The user can press F8 in-game to dump every MonoBehaviour to the snapshots
  folder.

Full schema and operational details in `DEV.md` under
"InspectorPlus: live runtime state snapshots." Not required for
StationpediaPlus implementation but available for diagnostic work.

### 18.7 Workshop and external references

- SPA Workshop: https://steamcommunity.com/sharedfiles/filedetails/?id=3634225688
- SPA GitHub: https://github.com/FlorpyDorpinator/StationpediaAscended
- Stationeers Logic Extended Workshop: https://steamcommunity.com/sharedfiles/filedetails/?id=3625190467

### 18.8 Preview / About.xml for StationpediaPlus

**Not applicable.** StationpediaPlus is a shared code library consumed via
ILRepack; it is never published as a standalone Workshop item and has no
`About.xml`, `Preview.png`, `thumb.png`, or README beyond a minimal
developer-facing one. CLAUDE.md's preview-image rules do not apply to this
project. Each CONSUMING mod still has its own About.xml, preview art, and
README per CLAUDE.md rules.

---

## 19. Decision log

All 18 design decisions resolved during the planning phase, plus 2 research
tasks (R1, R2). For each decision: the choice, the motivation, and any
bound implications.

### Decision 1, API shape
**A, Three imperative helpers** (`CategoryBuilder.Register`,
`ReferencePage.Register`, `LogicTypePageBuilder.Register`, plus `SpaBridge`
called automatically). Mods call each helper as needed, à la carte.

Motivation: modular, piecemeal adoption, clear per-helper boundaries, simple
documentation per helper.

### Decision 2, Page taxonomy
**B, Four kinds** (New-thing, LogicType, Extension, Reference).

Motivation: LogicType pages have their own game-side generator, key prefix,
and rendering path; folding them into Extended-device hides that distinction.
Explicit categories reduce doc traversal cost for new mod authors.

### Decision 3, LogicType page enrichment ownership
**B, Shared `LogicTypePageBuilder` helper.** Brings helper count to four.

Motivation: composition is nontrivial (header sections, related links, parse
timing, sprite assignment); centralizing enforces cross-mod visual
consistency. Under ILRepack each mod gets its own embedded postfix, but
authoring-time code is shared.

### Decision 4, Reference page key prefix
**B, `<FullModName>_<Topic>`** (NOT abbreviation).

Motivation: abbreviations risk collision with unrelated third-party mods by
different authors. Full mod name is unambiguous across the entire Stationeers
modding ecosystem.

### Decision 5, Reference page title format
**A, Plain natural-language title.** No bracketed abbreviation.

Motivation: no abbreviations anywhere in user-visible documentation. This
rule extends globally to all user-visible strings.

### Decision 6, Hide-from-search filter identification
**B, Explicit `HiddenKeys` HashSet.**

Motivation: decouples "has mod prefix" from "is hidden from search" for
future flexibility (mod-prefixed pages that should remain searchable,
per-page visibility toggles via config, etc.).

### Decision 7, Extension section GameObject name
**A, `<ModName>Details`** (e.g. `PowerTransmitterPlusDetails`).

Motivation: GameObject names are dev-facing only; short and unambiguous in
Unity hierarchy. Matches SPA's `OperationalDetailsCategory` idiom for visual
consistency.

### Decision 8, Section title format
**A, `<color=#FF7A18>{ModName} Details</color>`** (non-bold, " Details" suffix).

Motivation: matches SPA's visual rhythm. Vanilla category titles are
non-bold; bolding only our section creates dissonance. " Details" suffix
signals supplementary content.

### Decision 9, Link-click handler
**A, Ship custom `SixFive7LinkHandler`.**

Motivation: avoids vanilla `HelpLinkHandler`'s `WorldManager.IsGamePaused`
LateUpdate dependency (risk of NullReferenceException on pre-world-init pedia
open). Minimal click-only scope; reduced failure surface.

**Compensating authoring rule**: every `{LINK:Key;Display}` label must
include explicit click phrasing (§11.3).

### Decision 10, Harmony instance IDs
**A, One instance per helper per mod.**

Motivation: preserves three-helper boundary to runtime diagnostics. Mod
conflict reports naming a Harmony ID immediately identify the responsible
helper. ILRepack constraint: IDs derived from consuming mod's GUID, not
hardcoded to `com.sixfive7.*`.

### Decision 11, LogicInsert fallback
**A, No fallback.** Mandatory T-Native-Rows release-blocking test verifies.

Motivation: loud failure beats silent compensation for a bug class that
should be root-caused. Simpler codebase.

### Decision 12, LogicType page body assignment pattern
**B, Text + ParsePage** (idiomatic game pattern).

Motivation: matches vanilla `PopulateLogicVariables` exactly. ParsePage is
the game's authoritative parser including niche features (`{LIST_OF_...}`,
`PageCustomCategories`, `_worldHashes`) that direct assignment skips.

### Decision 13, PowerTransmitterPlus reference page count
**B, Two reference pages** (`MicrowavePowerTransmissionModel` + `AutoAim`).

Motivation: auto-aim is referenced from three locations (transmitter
extension section, MicrowaveAutoAimTarget page, MicrowaveLinkedPartner
page); meets the "cited from multiple pages -> promote to reference"
taxonomy rule. Sets precedent for future mods.

### Decision 14, Shared library file layout
**A, `src/` + `src/Internal/` subdir.**

Motivation: public-vs-internal split visible in file tree. `Internal/` is a
recognized .NET convention. Scales as the library grows.

### Decision 15, Testing matrix scope
**B, 12 tests plus T-Native-Rows (13 for PowerTransmitterPlus).**

Motivation: SpaBridge is pure reflection into another mod's internals;
explicit runtime verification tests catch regressions at test time rather
than user-report time.

### Decision 16, SpaBridge usage policy
**B, Structural enforcement via the shared codebase** (NOT convention-based).

`LogicTypePageBuilder.Register(spec)` internally invokes
`SpaBridge.TryEnrichLogicTooltips` for every device in `spec.RelatedDeviceKeys`.
SpaBridge entry tuple is derived from spec fields. Mods cannot accidentally
skip SPA enrichment; the library does it for them.

Motivation: CLAUDE.md conventions are non-deterministic (humans forget,
reviews miss). Compiled code is deterministic.

### Decision 17, DLL distribution model
**C, ILRepack into each mod from a shared codebase.**

Motivation: players must not subscribe to or install any separate artifact.
Each mod ships a single self-contained DLL. Imposes the seven architectural
constraints listed in §9.

### Decision 18, Stub deprecation timing
**A, One-commit migration.** Plus cross-mod stub-scan TODO.

Motivation: Register replace semantics mean the old stub provides no
runtime fallback value; running both in parallel adds risk, not safety.
Clean commit history; atomic revert available. Shared codebase is
test-covered (13 tests) so shipping with confidence is warranted.

Additional action: before migrating each mod, scan for dead stub code
(§13.7 step 1) and remove.

### Decision 19, SPA search-index re-injection handling
**ii, Soft-dependent SPA patch** (parallel to SpaBridge enforcement pattern).

Surfaced by R1 findings. SPA's `ReorganizeSearchResults` re-injects search
results from its own `_pageTitleIndex` built by `BuildPageIndexes` from
`Stationpedia.StationpediaPages`. SPA's own `ShouldHideFromSearch` knows
only about burnt / ruptured / wreckage variants; it has no awareness of our
`HiddenKeys`. Consequence: patching `StationpediaPage.IsRegexMatch` alone
is insufficient when SPA is installed, reference pages reappear in SPA's
re-injected results after 0.3-0.8 seconds.

Resolution: new internal helper `SpaSearchFilter` (`src/Internal/SpaSearchFilter.cs`)
installs a soft-dependent Harmony Postfix on SPA's `ShouldHideFromSearch` that
ORs in our `HiddenKeys`. Invoked automatically by `ReferencePage.Register`
(structural enforcement parallel to decision 16's `SpaBridge` invocation from
`LogicTypePageBuilder.Register`). Soft-detected via
`Chainloader.PluginInfos.ContainsKey("com.florpydorp.stationpediaascended")`;
silent no-op when SPA absent. All reflection and patch-installation failures
swallowed; falls back to option i (pages visible in SPA search) on failure
rather than crashing.

Target method: `StationpediaAscended.Patches.SearchPatches.ShouldHideFromSearch`.
Confirm exact signature at implementation time. Fallback patch targets if
renamed: `BuildPageIndexes` Postfix (filter `_pageTitleIndex`) or
`ReorganizeSearchResults` Postfix (filter result list). See
`Research/GameSystems/StationpediaAscendedInternals.md` for the method
catalog.

Harmony instance ID (decision 10 pattern): `<ModGuid>.stationpediaplus.spasearchfilter`.

Motivation: reference pages must be structurally hidden for ALL users, not
just those without SPA. The CLAUDE.md rule "reference pages are hidden from
search" cannot be quietly broken for 40%+ of the player base who run SPA.
Soft-detect keeps the zero-SPA-dependency invariant intact; ILRepack-friendly.

Testing: T5a added to the matrix (§14), open Stationpedia with SPA installed,
search for a reference-page title, wait 1 second for SPA's coroutine, confirm
the page does not appear in either vanilla or SPA-reorganized results.

### Research Task R1, Search method identification (RESOLVED)

Primary vanilla target: `StationpediaPage.IsRegexMatch(string, string)`.
One caller (`DoSearch`). SPA does NOT patch it. Prefix that sets
`__result = false` when `__instance.Key` is in HiddenKeys.

Secondary vanilla target: `Stationpedia.PopulateGuideLoreContents(List<string>, bool)`.
Prefix that filters `SPDAKeys` via a LINQ `Where`. Covers the Guides/Lore
home-page shortcuts.

Tertiary vanilla target: `Stationpedia.PopulateLists()`. Postfix that
walks `DataHandler._listDictionary` and removes entries whose `PageLink`
is in HiddenKeys. Covers home-page category-listing pages.

SPA-side target (soft-dependent, decision 19 ii):
`StationpediaAscended.Patches.SearchPatches.ShouldHideFromSearch`. Postfix
that ORs in HiddenKeys. Handles SPA's `ReorganizeSearchResults` coroutine
re-injection.

DO NOT patch: `ClearPreviousSearch`, `SetPage`, `Register`, `Regenerate`,
`SortPages`, `Render`, `GetPage`, `OpenPageByKey`, `OpenAt`.

### Research Task R2, HintPath inventory (RESOLVED)

Eight game/BepInEx DLLs plus three framework refs. All with `<Private>False</Private>`.
Full list in §16.2. Closest existing template: `EquipmentPlus.csproj` (UI +
TextMeshPro + EventSystems).

ILRepack tooling choice deferred to Phase 6 integration (Decision O1).

---

## 20. Cheat sheet

### Page keys
- Thing: `Thing<PrefabName>`
- LogicType: `LogicType<EnumName>`
- Reference: `<FullModName>_<Topic>`

### Category GameObject
- Name: `<ModName>Details`
- Sibling index: 21 (SPA uses 20)

### Color
- Section title: `#FF7A18` (orange, matching SPA)

### Harmony targets and kinds
- `Stationpedia.PopulateLogicVariables`, Postfix (LogicTypePageBuilder)
- `UniversalPage.ChangeDisplay`, Postfix, `[HarmonyAfter(spa)]` (CategoryBuilder)
- `StationpediaPage.IsRegexMatch(string, string)`, Prefix (SearchFilterPatch, primary)
- `Stationpedia.PopulateGuideLoreContents`, Prefix (SearchFilterPatch, secondary)
- `Stationpedia.PopulateLists`, Postfix (SearchFilterPatch, tertiary; filters `_listDictionary`)
- `StationpediaAscended.Patches.SearchPatches.ShouldHideFromSearch`, Postfix, soft-dependent (SpaSearchFilter; decision 19 ii)

### Mandatory markup rules
- No bare identifiers, always `{LOGICTYPE:X}` or `{LINK:K;D}`.
- No literal `[` or `]`, ParsePage rewrites.
- `{LINK:...}` labels include click phrasing ("Click here for ...", "Open the ...", "Go to the ...").
- `{LOGICTYPE:N}` / `{THING:P}` are exempt from click phrasing.

### Register modes
- `fallback:false` = REPLACE (default; use for pages we own).
- `fallback:true` = INSERT ONLY IF MISSING (use for coexistence).

### Description rendering path
- `ChangeDisplay` reads `page.Description` only; does not call `ParsePage`.
- For postfix mutations: pre-parse via `Localization.ParseHelpText`, assign to Description directly.
- For fresh pages: set Text, call `ParsePage()`, then Register.

### Never
- `[BepInDependency]` on SPA.
- GameObject name `OperationalDetailsCategory`.
- Direct `Stationpedia.Register(...)` in mod code (use helpers).
- Direct `SpaBridge` call in common path (LogicTypePageBuilder does it).
- Direct `SpaSearchFilter` call from mod code (ReferencePage does it).
- Hardcoded Harmony instance ID `com.sixfive7.*` (must derive from mod GUID).

### Always
- `try/catch` every patch body with LogDebug; never rethrow.
- Unity fake-null guard: `if (obj == null || !obj) return;`
- `[HarmonyAfter("com.stationpediaascended.mod")]` on ChangeDisplay patch.
- `page.CreatedCategories.Add(cat)` for injected categories.
- Full mod name (no abbreviations) in keys and titles.

### SPA constants
- Plugin GUID: `com.florpydorp.stationpediaascended`
- Harmony ID: `com.stationpediaascended.mod`

### PowerTransmitterPlus constants
- Plugin GUID: `net.powertransmitterplus`
- Reference page keys: `PowerTransmitterPlus_MicrowavePowerTransmissionModel`, `PowerTransmitterPlus_AutoAim`
- Extended device keys: `ThingStructurePowerTransmitter`, `ThingStructurePowerTransmitterReceiver`

### Stationeers Logic Extended reference (pattern origin)
- Workshop ID: `3625190467`
- Author: ThunderDuck
- Reserved LogicType band: 1000-1830 (avoid collision)

---

## 21. Historical and ecosystem context

This section records context that surfaced during the planning thread. The
mod-specific design rationale (why the library is ILRepacked, the per-mod
copy pattern, the four-kind taxonomy evolution, why reference keys use full
mod name) stays here as implementation background. Generalizable research
content (why custom LogicTypes need patches at all, the ILRepack-per-mod
central pattern write-up, SPA's specific manual-patching style, tooltip
coroutine timing, descriptions.json metrics, AutoAim cache shape, distance
cost patch quartet reference) has been drained to central Research pages.

### 21.1 Why custom LogicTypes don't appear on vanilla pages without patches

Vanilla Stationpedia pages for stock devices have a frozen `LogicInsert`
list: `AddLogicTypeInfo` iterates `EnumCollections.LogicTypes.Values`, a
collection built from `Enum.GetValues(typeof(LogicType))` at
`EnumCollection<LogicType,ushort>` construction time. Runtime-cast
`ushort -> LogicType` values are NOT in the compiled enum, so
`Enum.GetValues` never returns them. The four-part patch set
(`LogicableInitializePatch`, `EnumNamePatches`, `CanLogicRead`/`Write`
postfixes, `ProgrammableChip.AllConstants` IC10 injection) makes
`AddLogicTypeInfo` discover and emit native rows organically, which is
why Decision 11's "no LogicInsert fallback" stance is safe.

Full content lifted to:
- [LogicType](../../Research/GameSystems/LogicType.md) - `EnumCollections.LogicTypes` construction from `Enum.GetValues`, the four-part injection recipe, and why custom values need all four to render natively.
- [CustomLogicValueInjection](../../Research/Patterns/CustomLogicValueInjection.md) - Pattern write-up for injecting custom values into compiled `enum` collections.

Lifted during Phase 5 migration on 2026-04-20. Original content preserved in git history at `<pre-lift-sha>:Plans/StationpediaPlus/PLAN.md:3483-3506`.

### 21.2 Why the helper library is ILRepacked and not distributed separately

User explicitly rejected the "separate Workshop subscription" model:
players must not need to install any artifact beyond the mod itself. The
three considered options at Decision 17:

- A: Bundle `StationpediaPlus.dll` alongside each mod's plugin DLL. .NET
  assembly resolver would load one copy at runtime; if versions drift
  across mods, conflict. Rejected as fragile.
- B: Shared `BepInEx/plugins/StationpediaPlus/` location requiring players
  to install a separate artifact. Rejected per user constraint.
- C: ILRepack the library IL into each mod's final DLL at build time. Each
  mod ships self-contained; players see nothing new. Selected.

Side effect: each mod ends up with its own private copy of the library's
types (internal to the repacked assembly). Static state is per-mod; any
helper must operate as if it were the only mod in memory. This drives the
seven ILRepack constraints in §9.

The generalized pattern (tooling choice, per-mod static state, Harmony ID
derivation, defensive patch bodies) lives on a central page because future
mods that want to share a library across the monorepo will replay the same
tradeoffs.

Full content lifted to:
- [ILRepackPerModCopy](../../Research/Patterns/ILRepackPerModCopy.md) - Canonical write-up of the per-mod-copy pattern, ILRepack tooling options, and the seven ILRepack constraints that flow from it.

Lifted during Phase 5 migration on 2026-04-20. Original content preserved in git history at `<pre-lift-sha>:Plans/StationpediaPlus/PLAN.md:3507-3525`.

### 21.3 Why SpaBridge enforcement is structural, not conventional

User explicitly rejected relying on CLAUDE.md conventions or code-review
discipline for SPA tooltip integration. Compiled code is deterministic;
reviews are not. The chosen enforcement:
`LogicTypePageBuilder.Register(spec)` invokes
`SpaBridge.TryEnrichLogicTooltips` automatically for every device in
`spec.RelatedDeviceKeys`. Mod authors cannot accidentally skip SPA tooltip
enrichment because the registration call does it for them. `SpaBridge`
remains public for edge-case direct use. This rationale is mod-specific
(`SpaBridge` is a StationpediaPlus concern); the generalizable nugget is
that ILRepack pushes enforcement into the shared library rather than into
conventions, captured centrally.

Full content lifted to:
- [ILRepackPerModCopy](../../Research/Patterns/ILRepackPerModCopy.md) - "Structural vs conventional enforcement" subsection.
- Mod-local detail stays here as the `LogicTypePageBuilder` / `SpaBridge` wiring rationale.

Lifted during Phase 5 migration on 2026-04-20. Original content preserved in git history at `<pre-lift-sha>:Plans/StationpediaPlus/PLAN.md:3526-3537`.

### 21.4 Why the custom link handler ships instead of reusing vanilla

Vanilla `HelpLinkHandler` would give hover-color feedback for free, but its
`LateUpdate` references `WorldManager.IsGamePaused`, tying our UI to scene
state and risking `NullReferenceException` when Stationpedia opens from
the main menu before world init. `SixFive7LinkHandler` avoids LateUpdate
entirely (click-only), trading hover color for reduced failure surface.
Compensated by the mandatory click-phrasing rule (§11.3).

Full content lifted to:
- [HelpLinkHandler](../../Research/GameClasses/HelpLinkHandler.md) - `LateUpdate` body and `WorldManager.IsGamePaused` coupling (the reason we ship our own).
- [BestEffortIntegration](../../Research/Patterns/BestEffortIntegration.md) - General pattern of shipping a minimal replacement to avoid coupling to optional scene state.

Lifted during Phase 5 migration on 2026-04-20. Original content preserved in git history at `<pre-lift-sha>:Plans/StationpediaPlus/PLAN.md:3538-3547`.

### 21.5 The per-mod-copy pattern vs shared Harmony state

Under ILRepack, each mod's embedded library copy installs its OWN Harmony
patches on the same game methods. Harmony handles multiple postfixes on
one target by running them in sequence. Each mod's copy only knows about
its own registrations (static state is per-copy).

This means:
- No shared registry of all mods' specs. Each mod contributes only its own.
- Multiple mods extending the same device page each get their own
  `<ModName>Details` GameObject.
- Multiple mods hiding reference pages each maintain their own `HiddenKeys`
  HashSet, but the filter runs via multiple postfixes that each remove
  their own keys, additive effect.
- Harmony instance IDs are derived from the consuming mod's plugin GUID
  (e.g. `net.powertransmitterplus.stationpediaplus.categorybuilder`) so
  two mods' patches don't clash on identical IDs.

### 21.6 Evolution of the four-kind page taxonomy

Early drafts proposed three-kind and four-kind taxonomies. Three-kind
(New-device, Extended-device, Shared-reference) folded LogicType handling
into Extended-device. Four-kind (New-thing, LogicType, Extension,
Reference) split LogicType out.

Four-kind won (Decision 2) because LogicType pages have their own game-side
generator (`PopulateLogicVariables`), own key prefix (`LogicType<Name>`),
own rendering path via the `LogicTypePageTemplate`, and are both navigation
targets AND rows inside Extension sections. The distinction is worth
surfacing to mod authors rather than hiding it.

### 21.7 Why reference page keys use full mod name, not abbreviation

Decision 4 initially considered `Ref_<Topic>` (universal prefix) and
`<ModAbbr>_<Topic>` (per-mod abbreviation). User refined to require FULL
mod name, not abbreviation, because abbreviations risk collision with
unrelated third-party mods by different authors (e.g. `PTP_` could conflict
with "Power Tools Plus", "Precision Tracker Plus", etc.). Full mod name
is unambiguous across the entire Stationeers modding ecosystem.

This rule, no abbreviations in user-visible strings, extends globally to
all authored content (Decision 5 note).

### 21.8 Current StationpediaPatches.cs stub (what decision 18 replaces)

The existing PowerTransmitterPlus stub at
`c:\Source\SixFive7\StationeersPlus\PowerTransmitterPlus\PowerTransmitterPlus\StationpediaPatches.cs`
is ~66 lines and currently drives the mod's six LogicType-page
registrations. It uses defensive reflection (`AccessTools.TypeByName` with
namespace fallbacks), the `TargetMethod()` + `Prepare()` pattern for
graceful degradation, and iterates `LogicTypeRegistry.All` to create bare
`StationpediaPage` instances via `Activator.CreateInstance` then calls
`Register(page, false)` via reflection. The pattern lineage is Stationeers
Logic Extended by ThunderDuck. Net runtime effect today: six LogicType
pages with bare titles and one-line descriptions, no page bodies, no
cross-links, no device-page content, no reference pages, no SPA tooltip
enrichment. Decision 18 A replaces the entire file in one migration
commit; `LogicTypeRegistry` stays.

Full content lifted to:
- [PowerTransmitterPlus RESEARCH.md, Harmony Patches Catalog](../../Mods/PowerTransmitterPlus/RESEARCH.md#harmony-patches-catalog) - The mod-local record of the stub's structure, lineage, and the replacement plan.

Lifted during Phase 5 migration on 2026-04-20. Original content preserved in git history at `<pre-lift-sha>:Plans/StationpediaPlus/PLAN.md:3591-3625`.

### 21.9 SPA's "manual patching" self-comment

SPA's `ApplyHarmonyPatches` carries a source comment noting imperative
`_harmony.Patch(...)` calls are "more reliable than attribute-based
patching for game assemblies." Implication for StationpediaPlus: our own
helpers follow the imperative pattern, resolving targets via
`AccessTools.Method` with `Prepare()`-equivalent gates and silent failure
on missing targets, matching SPA's style and surviving the same game-drift
failure modes.

Full content lifted to:
- [BestEffortIntegration](../../Research/Patterns/BestEffortIntegration.md) - Imperative-patching pattern, `Prepare()` gating, silent failure on missing targets.

Lifted during Phase 5 migration on 2026-04-20. Original content preserved in git history at `<pre-lift-sha>:Plans/StationpediaPlus/PLAN.md:3627-3644`.

### 21.10 SPA tooltip coroutine timing

SPA's `MonitorStationpediaCoroutine` polls `Stationpedia.CurrentPageKey`
every 100ms. On a page-change detection it schedules `AddTooltipsAfterDelay`
via a 2-frame delay so vanilla `UniversalPage.ChangeDisplay` can finish
populating `LogicContents.Contents`. Our custom LogicType rows (which
render as `SPDALogic` prefab instances via vanilla's own
`PopulateLogicInserts`) get decorated automatically; SpaBridge supplies the
tooltip text.

Full content lifted to:
- [StationpediaAscendedInternals](../../Research/GameSystems/StationpediaAscendedInternals.md) - Tooltip coroutine (poll interval, 2-frame delay, `AddTooltipsToCategory` flow).

Lifted during Phase 5 migration on 2026-04-20. Original content preserved in git history at `<pre-lift-sha>:Plans/StationpediaPlus/PLAN.md:3646-3670`.

### 21.11 SPA descriptions.json shipped metrics

Approximately 1.2 MB, shipped as both an embedded resource and a loose file
next to SPA's DLL. Content breakdown: 499 devices, 5 guides, 2 mechanics,
250 `genericDescriptions`. Four-step loading order (embedded resource first,
then three filesystem locations, first hit wins). Already has entries for
all three microwave transmitter prefabs with empty `operationalDetails`;
none of our six custom LogicType names appear. SpaBridge injects entries
into the existing `logicDescriptions` dicts at runtime without touching
the shipped JSON.

Full content lifted to:
- [SPADeviceDatabase](../../Research/Protocols/SPADeviceDatabase.md) - Shipped metrics section (size, entry counts, loading order, transmitter-prefab coverage).

Lifted during Phase 5 migration on 2026-04-20. Original content preserved in git history at `<pre-lift-sha>:Plans/StationpediaPlus/PLAN.md:3672-3705`.

### 21.12 Per-dish AutoAim cache via ConditionalWeakTable

PowerTransmitterPlus's `AutoAimPatches` stores per-dish target state in a
`ConditionalWeakTable<WirelessPower, StrongBox<long>>`. GC-tied cleanup:
when a `WirelessPower` instance is collected, its cache entry is
automatically reclaimed. `MicrowaveAutoAimTarget` writes set entries;
manual Horizontal/Vertical adjustments via `RotatableBehaviour` setter
postfixes clear them (with a re-entry flag to suppress clearing during
auto-aim's own writes). Not directly relevant to Stationpedia integration
but documented in the reference content (`PowerTransmitterPlus_AutoAim`
page and the transmitter extension section).

Full content lifted to:
- [ConditionalWeakTableCache](../../Research/Patterns/ConditionalWeakTableCache.md) - GC-tied cache pattern, per-instance key, `StrongBox<T>` value shape, re-entry guards.
- [PowerTransmitter](../../Research/GameClasses/PowerTransmitter.md) - Mod-specific AutoAim cache implementation on `WirelessPower`.

Lifted during Phase 5 migration on 2026-04-20. Original content preserved in git history at `<pre-lift-sha>:Plans/StationpediaPlus/PLAN.md:3707-3721`.

### 21.13 Distance cost patch quartet (reference)

PowerTransmitterPlus's cost model is implemented as four interdependent
Harmony patches on `PowerTransmitter`
(`GeneratedPowerNoDistanceDeratePatch`, `UsePowerInflateDebtPatch`,
`GetUsedPowerLiftCapPatch`, `ReceivePowerVisualizerFixPatch`). Disabling
any one breaks the model observably. Not in scope for the StationpediaPlus
implementation but documented in the reference content
(`PowerTransmitterPlus_MicrowavePowerTransmissionModel` reference page
and the transmitter extension section).

Full content lifted to:
- [PowerTransmitter](../../Research/GameClasses/PowerTransmitter.md) - Distance-cost quartet table with per-patch target, kind, and effect; interdependence argument.

Lifted during Phase 5 migration on 2026-04-20. Original content preserved in git history at `<pre-lift-sha>:Plans/StationpediaPlus/PLAN.md:3723-3736`.

---

End of PLAN.md (Phase 5 drained draft).
