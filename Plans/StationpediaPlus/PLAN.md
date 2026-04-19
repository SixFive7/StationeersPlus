# StationpediaPlus Implementation Handoff

Complete briefing for the implementing agent. This document aggregates all
research, decompilation findings, design decisions, and content authored
across the planning phase. It is intentionally overcomplete; nothing from the
planning thread is omitted.

Read front-to-back on first encounter, then use the Table of Contents as a
reference. Every section is self-contained.

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

- `StructurePowerTransmitter` — Microwave Power Transmitter (the dish)
- `StructurePowerTransmitterReceiver` — Microwave Power Receiver
- `StructurePowerTransmitterOmni` — vanilla omni variant, NOT modified by this mod

Stationpedia page keys:

- `ThingStructurePowerTransmitter`
- `ThingStructurePowerTransmitterReceiver`
- `ThingStructurePowerTransmitterOmni` (DO NOT TOUCH)

### Existing Harmony patches (to keep)

- `LogicableInitializePatch` — extends `EnumCollections.LogicTypes.Values` to include 6571-6576
- `LogicReadoutPatches` — `CanLogicRead` / `GetLogicValue` postfixes branched by `__instance is PowerTransmitter / PowerReceiver`
- `AutoAimPatches` — `SetLogicValue` prefix, `CanLogicWrite` postfix, `RotatableBehaviour` setter postfixes to reset auto-aim on manual H/V write
- `EnumNamePatches` — postfixes on `Enum.GetName`, `EnumCollection<LogicType, ushort>.GetName` / `GetNameFromValue`
- `Ic10ConstantsPatcher` — reflection injection into `ProgrammableChip.AllConstants` for IC10 name resolution
- Distance-cost patches (four-patch dance):
  - `GeneratedPowerNoDistanceDeratePatch` — prefix on `PowerTransmitter.GetGeneratedPower`
  - `UsePowerInflateDebtPatch` — postfix on `PowerTransmitter.UsePower`
  - `GetUsedPowerLiftCapPatch` — postfix on `PowerTransmitter.GetUsedPower`
  - `ReceivePowerVisualizerFixPatch` — postfix on `PowerTransmitter.ReceivePower`

### Current Stationpedia stub (to be DELETED during migration)

`StationpediaPatches.cs` is a 66-line stub that registers one bare page per
custom LogicType via `Stationpedia.Register(page, false)`. No body content
beyond the one-line description. No extension sections. No reference pages.

Pattern origin: the stub follows the pattern established by
**Stationeers Logic Extended (SLE) by ThunderDuck** (Workshop ID `3625190467`).
SLE has no public extensibility API, so every mod adding custom LogicTypes
reimplements the registration pattern from scratch. The stub uses SLE-style
defensive reflection (`AccessTools.TypeByName` with namespace fallbacks) and
the `TargetMethod()` + `Prepare()` Harmony pattern for graceful degradation
on game-version drift.

Stub will be deleted in the migration commit; shared library takes over.

### LogicType value bands

Values in `LogicType` are `ushort`; any 0-65535 value is legal at runtime
once injected into `EnumCollections.LogicTypes`. Reserved bands:

| Band | Owner | Notes |
|---|---|---|
| 0-349 | Vanilla game | Compiled-in `LogicType` enum members |
| 1000-1830 | Stationeers Logic Extended (ThunderDuck) | Reserved by SLE; avoid |
| 6571-6599 | **PowerTransmitterPlus** | Our mod's reserved band |

Future SixFive7 mods adding LogicTypes should reserve their own bands
clear of vanilla, SLE, and PowerTransmitterPlus.

### Three LogicType arrays that must be extended

Stationeers stores LogicType lists in three separate places. A mod adding
custom LogicTypes must extend all three for full UI coverage:

| Array | Used by | PTP extension mechanism |
|---|---|---|
| `Logicable.LogicTypes` / `Logicable.LogicTypeNames` | `NextLogicType` cycling, tablet display | `LogicableInitializePatch` postfix |
| `Assets.Scripts.EnumCollections.LogicTypes` | `ConfigCartridge` (configuration tablet UI) | Same patch; extends Values, ValuesAsInts, Names, PaddedNames, `<Length>k__BackingField` |
| `Assets.Scripts.UI.Motherboard.ScreenDropdownBase.LogicTypes` | IC housing on-screen dropdowns | Same patch (best-effort) |

`LogicTypeNamesRedirects` is a binary-search index rebuilt by the same
patch (best-effort; tolerates absence).

Additionally the mod must patch `Enum.GetName(Type, object)` and
`EnumCollection<LogicType, ushort>.GetName` / `GetNameFromValue` postfixes
so reflection-based name lookups find our custom values — done by
`EnumNamePatches`.

### Threading / main-thread safety

`Prefab.OnPrefabsLoaded` fires on the Unity main thread (the callback runs
synchronously inside the game's main-thread loading sequence around
game.cs:59080-59090, before `Stationpedia.Regenerate` at line 59090).
`OnAllModsLoaded` is therefore main-thread; all Unity API calls from within
it are safe without dispatching.

PowerTransmitterPlus has a `MainThreadDispatcher` singleton MonoBehaviour
for enqueuing actions from ThreadPool-run PowerTick contexts to the main
thread (used by the distance-cost multiplayer sync, not by Stationpedia
integration). The StationpediaPlus library does not need this; all its work
happens on main thread during `OnAllModsLoaded` and during the
main-thread-driven `Regenerate` / `ChangeDisplay` paths.

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

1. `CategoryBuilder` — injects collapsible sections on vanilla device pages.
2. `ReferencePage` — registers hidden reference pages linkable but not
   searchable.
3. `LogicTypePageBuilder` — enriches LogicType page bodies; also invokes
   `SpaBridge` automatically for every registered spec.
4. `SpaBridge` — soft-detects SPA and reflects into its `DeviceDatabase` for
   tooltip enrichment. Usually called automatically by `LogicTypePageBuilder`;
   public for edge-case direct use.

### One runtime support MonoBehaviour

- `SixFive7LinkHandler` — minimal `IPointerClickHandler` attached to every
  dynamically created TMP text element inside extension sections.
  Routes `<link=...>` clicks to `Stationpedia.Instance.SetPage(linkID)`.

### Three internal plumbing files

- `SearchFilterPatch` — installs the search-filter Harmony postfixes.
- `TextElementFactory` — builds dynamic TMP elements with donor styling and
  attaches `SixFive7LinkHandler`.
- `HarmonyIdHelper` — derives per-mod Harmony instance IDs from the
  consuming mod's GUID.

---

## 5. Page taxonomy (four kinds)

Every piece of Stationpedia content maps to exactly one kind.

### Kind A — New-thing pages

Vanilla game auto-generates a page for any registered prefab. Key is
`"Thing" + prefab.PrefabName`. Body comes from the prefab's
`<RecordThing>/<Description>` in the mod's language XML. Logic rows appear
via `CanLogicRead`/`Write` iteration in vanilla `AddLogicTypeInfo`.

Mod responsibility: author language XML; implement `CanLogicRead/Write` on
the new device class. Zero Stationpedia-specific code.

Library responsibility: none.

### Kind B — LogicType pages

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

### Kind C — Extension sections

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

### Kind D — Shared-reference pages

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

All references to `c:\Source\tmp\game.cs` (~12.7 MB decompile). Line numbers
match that file exactly. All types live in `namespace Assets.Scripts.UI`
unless noted otherwise.

### 6.1 Stationpedia core classes

| Class | Line | Purpose |
|---|---|---|
| `Stationpedia : ResizableWindow, IModal` | 230120 | Singleton controller of the pedia window |
| `StationpediaPage` | 233507 | Data model for one page |
| `StationpediaCategory : UserInterfaceBase` | 233199 | UI for one collapsible category |
| `UniversalPage : UserInterfaceBase` | 233792 | Whole-page renderer |
| `SPDALogic : UserInterfaceBase` | 233092 | One logic-row prefab (two TMP fields) |
| `StationLogicInsert` | 233362 | Data model for one logic row |
| `HelpLinkHandler : UserInterfaceBase, IPointerClickHandler, ...` | 221638 | Vanilla TMP link-click handler (uses `WorldManager.IsGamePaused` in LateUpdate) |
| `SPDAEntryType` (enum) | 233007-233017 | Members: Undefined, Guides, Lore, Maximum |

### 6.2 StationpediaPage field inventory

Constructors (game.cs:233666-233681):

```csharp
public StationpediaPage() { }
public StationpediaPage(string key, string title, string text)
    { Key = key; Title = title; Text = text; }
public StationpediaPage(string key, string title)
    { Key = key; Title = title; }
```

The `(key, title, text)` overload sets `Text`, NOT `Description`.

Key fields relevant to our implementation:

- `string Key` — unique ID, goes into `_linkIdLookup`
- `string Title` — display name, used in nav and search results
- `string Text` — raw markup source, consumed by `ParsePage`
- `string Description` — parsed body; this is what `ChangeDisplay` renders
- `int SortPriority` — ascending sort key
- `bool ImportantPage` — visual flag (not used by us)
- `SPDAEntryType DisplayFilter` — default Undefined; Guides/Lore add to category shortcuts
- `Sprite CustomSpriteToUse` — optional page thumbnail
- `List<StationLogicInsert> LogicInsert` — public mutable, eagerly initialized
- `List<StationLogicInsert> LogicSlotInsert`, `ModeInsert`, `ConnectionInsert` — same
- `List<StationCategory> CreatedCategories` on `UniversalPage` (not `page`) — game-owned cleanup list

### 6.3 StationLogicInsert full definition

```csharp
public class StationLogicInsert
{
    public string LogicName;
    public string LogicAccessTypes;
}
```

Two string fields. No description slot, no type info. Vanilla formats
`LogicName` via `{LOGICTYPE:Name}` expansion which produces
`<link=LogicTypeName><color=orange>Name</color></link>` (clickable to the
LogicType page).

### 6.4 SPDALogic prefab

```csharp
public class SPDALogic : UserInterfaceBase
{
    public TextMeshProUGUI InfoValue;
    public TextMeshProUGUI InfoReadWrite;
}
```

Prefab on `Stationpedia.Instance.LogicInsertPrefab`.

### 6.5 StationpediaCategory

```csharp
public class StationpediaCategory : UserInterfaceBase
{
    public RectTransform Contents;
    public RectTransform SecondContents;
    public UnityEngine.UI.Image CollapseImage;
    public TextMeshProUGUI Title;
    public Sprite VisibleImage;
    public Sprite NotVisibleImage;
    public void ToggleContentVisibility();
    public int GetChildCount();
    public void ClearChildInserts();
}
```

Prefab on `Stationpedia.Instance.CategoryPrefab`. No `AddRow` method;
populate by instantiating row prefabs under `category.Contents`.

### 6.6 UniversalPage.ChangeDisplay flow

Method at game.cs:234485. Body order:

1. Destroy every GameObject in `CreatedCategories`, clear the list.
2. Set `PageHeaderImage.sprite`.
3. Call `ResetUniversalPageInserts()` — clears all built-in categories' children.
4. `CheckAndSetTextElement(PageDescription, page.Description)` — plain
   assignment; no re-parse.
5. Call every other `CheckAndSetTextElement` (thermal, power, chemistry, etc.).
6. Call each `Populate*Inserts` method synchronously (game.cs:234554-234574):
   PopulateLifeRequirements, PopulateCustomCategories, PopulateSlotInserts,
   PopulateStructureVersion, PopulateLogicInserts, PopulateLogicInstructions,
   PopulateLogicSlotInserts, PopulateModeInserts, PopulateConnectionInserts,
   PopulateOreInserts, PopulateGasInserts, PopulateFermentationInserts,
   PopulateConstructedThings, PopulateUsedResources, PopulateUsedIn,
   PopulateCombustionInfo, PopulateProducedThings, PopulateKitInserts,
   PopulateHowToBuildInserts, PopulateBuildStatesInserts, PopulatePhaseDiagram.

A `ChangeDisplay` Postfix runs AFTER step 6. Mutating `page.LogicInsert` at
that point does not retroactively render rows; vanilla already iterated.
Injecting a new `StationpediaCategory` under `page.Content` does work (we do
this in `CategoryBuilder`).

### 6.7 Register semantics

`Stationpedia.Register(StationpediaPage page, bool fallback = false)` at
game.cs:230948-230969:

```csharp
public static void Register(StationpediaPage page, bool fallback = false)
{
    _linkIdLookup.TryGetValue(page.Key, out var value);
    if (!fallback || value == null)
    {
        if (value != null)
        {
            _linkIdLookup.Remove(value.Key);
            StationpediaPages.Remove(value);
        }
        if (page.DisplayFilter == SPDAEntryType.Guides) GuidesPages.Add(page.Key);
        else if (page.DisplayFilter == SPDAEntryType.Lore) LorePages.Add(page.Key);
        StationpediaPages.Add(page);
        _linkIdLookup.Add(page.Key, page);
    }
}
```

- `fallback:false` (default): always replaces existing entry.
- `fallback:true`: inserts only if key is missing.

Both `StationpediaPages` (public list) and `_linkIdLookup` (private dict)
are kept consistent on replace. The page object reference is shared; mutation
via one is visible via the other.

### 6.8 Regenerate lifecycle

`Stationpedia.Regenerate()` at game.cs:231012-231040. Body:

1. `Instance.PopulateLists()`
2. `foreach (StationpediaPage p in StationpediaPages) p.ParsePage()`
3. `Instance.PopulateThingPages()` — builds Thing pages
4. `Instance.PopulateLogicVariables()` — builds LogicType pages
5. `Instance.PopulateLogicSlotVariables()`
6. `Instance.PopulateReagents()`
7. `Instance.PopulateGenes()`
8. `Instance.PopulateTrading()`
9. `Instance.PopulateGases()`
10. `Instance.PopulateFactionLorePages()`
11. `Instance.UpdateLinkedPages()`
12. `Instance.SetPage(CurrentPageKey)`
13. `Instance.SortPages()`
14. `GC.Collect()`
15. First-call only: subscribes `Regenerate` to `Localization.OnLanguageChanged`.

Call sites (exactly two):

- `GameManager.LoadGameDataAsync` at game.cs:59090, AFTER `await Prefab.LoadAll()` completes.
- `Localization.OnLanguageChanged` event, every language change.

Mod Harmony patches installed in `OnAllModsLoaded` are active before the
first `Regenerate` runs.

`Regenerate` does NOT clear `StationpediaPages` at the top. Register's
replace semantics handle deduplication on re-runs.

### 6.9 Thing page generation

`Stationpedia.PopulateThingPages` at game.cs:231964. Body iterates
`Prefab.AllPrefabs` and for each creates:

```csharp
StationpediaPage page3 = new StationpediaPage(
    $"Thing{allPrefab.PrefabName}", allPrefab.DisplayName);
```

At game.cs:232041:

```csharp
page3.Description = Localization.ParseHelpText(
    Localization.GetThingDescription(allPrefab.PrefabName));
```

`GetThingDescription` reads the `<RecordThing>/<Description>` entry from the
language XML keyed on `Animator.StringToHash(prefabName)`.

Logic rows come from `AddLogicTypeInfo(Thing prefab, ref StationpediaPage page)`
at game.cs:231184:

```csharp
LogicType[] values = EnumCollections.LogicTypes.Values;
for (int i = 0; i < values.Length; i++)
{
    LogicType logicType = values[i];
    bool flag  = logicable.CanLogicRead(logicType);
    bool flag2 = logicable.CanLogicWrite(logicType);
    if (!flag2 && !flag) continue;
    // ...
    stationLogicInsert.LogicName = Localization.ParseHelpText(
        "{LOGICTYPE:" + logicType.ToString() + "}");
    page.LogicInsert.Add(stationLogicInsert);
}
```

Because `LogicableInitializePatch` extends `EnumCollections.LogicTypes.Values`
to include our custom values before `Regenerate` fires, and because our
`CanLogicRead`/`Write` postfixes return true for those values, vanilla
naturally adds our custom rows. No LogicInsert fallback is needed (Decision 11A).

### 6.10 LogicType page generation

`Stationpedia.PopulateLogicVariables` at game.cs:232142-232193:

```csharp
private void PopulateLogicVariables()
{
    StationpediaPage page = GetPage("LogicTypePageTemplate");
    if (page == null) return;
    LogicType[] values = EnumCollections.LogicTypes.Values;
    for (int i = 0; i < values.Length; i++)
    {
        LogicType logicType = values[i];
        if (LogicBase.IsDeprecated(logicType)) continue;
        try
        {
            string logicDescription = LogicBase.GetLogicDescription(logicType);
            string text = string.Format(page.Parsed, logicDescription);
            string text2 = EnumCollections.LogicTypes.GetName(logicType);
            string title = "LogicSlotType." + text2;
            StationpediaPage stationpediaPage = new StationpediaPage(
                "LogicType" + text2, title, text);
            // ... SoundAlert sub-loop ...
            stationpediaPage.Title = "LogicType." + EnumCollections.LogicTypes.GetName(logicType);
            stationpediaPage.Description = text;
            stationpediaPage.CustomSpriteToUse = VariableImage;
            stationpediaPage.ParsePage();
            Register(stationpediaPage);
        }
        catch (System.Exception ex2) { ... }
    }
}
```

Template body source: `LogicTypePageTemplate.Text` is just `{0}` from
`english_help.xml`. `string.Format({0}, logicDescription)` resolves to the
vanilla one-liner description.

Our `LogicTypePageBuilder` postfix runs AFTER this, replacing each of our
custom LogicType pages with an enriched version via `Register(page, false)`.

### 6.11 ParsePage semantics

`StationpediaPage.ParsePage()` at game.cs:233683-233707:

```csharp
public void ParsePage()
{
    _parsed = Localization.ParseHelpText(Text);
    _parsed = _parsed.Replace('[', '<');
    _parsed = _parsed.Replace(']', '>');
    _parsed = _parsed.Replace("\t", string.Empty);
    _parsed = _parsed.TrimStart();
    foreach (string listOfAllListOfObject in Stationpedia.DataHandler.ListOfAllListOfObjects)
    {
        string text = "{LIST_OF_" + listOfAllListOfObject.ToUpper() + "}";
        if (Text.Contains(text))
            PageCustomCategories.Add(listOfAllListOfObject);
        if (Text.Contains(_worldHashes))
            _parsed = _parsed.Replace(_worldHashes, Localization.ParseHelpText(NewWorldMenu.WorldHashes));
        _parsed = _parsed.Replace(text, string.Empty);
    }
    if (string.IsNullOrEmpty(Description))
        Description = _parsed;
}
```

Critical guard at last line: `Description` is populated from `_parsed` only
if it was empty. Mutating `Text` after `ParsePage` has run does not update
`Description` unless `Description` is cleared or assigned directly.

`_parsed` is only set by `ParsePage`. The `Parsed` property (line 233664) is
a plain backing-field getter, no lazy parse.

### 6.12 Search mechanism

`Stationpedia.DoSearch(string hash, string pattern, CancellationTokenSource)`
at game.cs:230584 is the ONLY path that iterates `StationpediaPages` for
display (search results). Driven by `SearchField.onValueChanged` →
`SearchBehaviour` → `StartSearchCountdown` → `ClearAndStartSearch` →
`ForceSearch` → `DoSearch`.

`DoSearch` calls `StationpediaPage.IsRegexMatch(hash, pattern)` per page at
game.cs:230602. This two-argument overload has exactly ONE caller in the
entire game assembly (DoSearch). It returns bool indicating a match.

`StationpediaPage.IsRegexMatch(string text, string pattern)` body at
game.cs:233709:

```csharp
public bool IsRegexMatch(string text, string pattern)
{
    if (text == PrefabHashString) return true;
    if (!Match(pattern, Title) && !Match(pattern, Key))
        return Match(pattern, Description);
    return true;
}
```

Truncates at 255 chars for regex matching.

Separate `HelpReference.IsRegexMatch(string pattern)` at game.cs:221825 is
unrelated (IC10 help panel). Do NOT patch the one-arg overload.

Why we patch `IsRegexMatch` and not `DoSearch` directly: `DoSearch` is
`private async UniTask`, meaning Harmony patches the state-machine kick-off
method — the patch receives the `UniTask` return before iteration has run.
A Postfix on `DoSearch` can't filter results because the results haven't
been materialized at postfix time. `IsRegexMatch`, by contrast, is a
regular synchronous method called from inside the async state machine's
`MoveNext`, and Harmony-patched synchronous methods called from inside an
async body ARE redirected correctly. This is why `IsRegexMatch` is the
right lever. Full body of `DoSearch` (game.cs:230584-230614):

```csharp
private async UniTask DoSearch(string hash, string pattern, CancellationTokenSource cancelToken)
{
    if (string.IsNullOrEmpty(pattern) && string.IsNullOrEmpty(hash))
    {
        ClearPreviousSearch();
        NoResultsFromSearchText.SetActive(value: true);
        return;
    }
    NoResultsFromSearchText.SetActive(value: false);
    await UniTask.SwitchToThreadPool();
    int count = 0;
    int i = StationpediaPages.Count - 1;
    while (i >= 0 && count < searchResultsPerPage)
    {
        if (cancelToken.IsCancellationRequested) return;
        if (!string.IsNullOrEmpty(StationpediaPages[i].Title)
            && !StationpediaPages[i].Title.Equals("Search")
            && StationpediaPages[i].IsRegexMatch(hash, pattern))
        {
            StationpediaPage page = _linkIdLookup[StationpediaPages[i].Key];
            SPDAListItem insert = _SPDASearchInserts[count];
            MakePage(page, insert).Forget();
            await UniTask.Delay(20, DelayType.UnscaledDeltaTime);
            count++;
        }
        i--;
    }
    await UniTask.SwitchToMainThread();
    NoResultsFromSearchText.SetActive(count == 0);
}
```

Iterates `StationpediaPages` in reverse, up to `searchResultsPerPage` (100)
results. Note it mutates pre-pooled `_SPDASearchInserts` in-flight rather
than returning a list; patch targets that need to filter the final list
would need to trace into `MakePage` or patch `_SPDASearchInserts`
population, both hairier than patching `IsRegexMatch`.

### 6.13 Guides and Lore home-page lists

`Stationpedia.PopulateGuideLoreContents(List<string> SPDAKeys, bool important)`
at game.cs:230861. Iterates the provided list (either `GuidesPages` or
`LorePages` populated by Register) and instantiates `ListSearchPrefab`
children into `LoreGuideContents`.

Callers:
- `SetPageGuides()` passes `GuidesPages`.
- `SetPageLore()` passes `LorePages`.

Both callers are reached from home-page shortcuts via `SetPage("Guides")` /
`SetPage("Lore")`.

### 6.14 Markup tokens (Localization.ParseHelpText)

game.cs:194901-194918. Full replacement table:

| Input | Output |
|---|---|
| `{THING:Name}` | `<link=ThingName><color=green>DisplayName</color></link>` |
| `{GAS:Type}` | `<link=GasType><color=#44AD83>Name</color></link>` |
| `{REAGENT:X}` | `<link=ReagentX><color=#B566FF>X</color></link>` |
| `{SLOT:X}` | `<link=SlotX><color=orange>Name</color></link>` |
| `{COLOR<name>:Text}` | `<color=<name>>Text</color>` |
| `{HEADER:Title}` | `<size=120%><b>Title</b></size>` |
| `{POS:N}` | `<pos=N>` |
| `{LINK:Key;Display}` | `<link=Key><color=#0080FFFF>Display</color></link>` |
| `{LOGICTYPE:Name}` | `<link=LogicTypeName><color=orange>Name</color></link>` |
| `{LOGICSLOTTYPE:Name}` | `<link=LogicSlotTypeName><color=orange>Name</color></link>` |
| `{KEY:name}` | `<color=#FBB03B>keyname</color>` |
| `{LIST}` / `{/LIST}` | `<indent=10>` / `</indent>` |
| `{LIST:N}` | `<indent=N>` |
| `{INPUT:action}` | key-binding with color |

Rules:

- **No auto-linking.** Bare identifiers render as plain text. Use
  `{LOGICTYPE:X}` or `{LINK:K;D}` explicitly.
- **No literal `[` or `]`.** `ParsePage` rewrites them to `<` / `>`.
- TMP rich text tags (`<b>`, `<i>`, `<color>`, `<size>`, `<link>`, `<indent>`,
  `<pos>`, `<sup>`, `<sub>`, `<sprite>`) pass through unchanged.

### 6.15 Link-click handling

`HelpLinkHandler.OnPointerClick` at game.cs:221692-221717:

```csharp
public void OnPointerClick(PointerEventData eventData)
{
    int num = TMP_TextUtilities.FindIntersectingLink(Parent, Input.mousePosition, _pCamera);
    if (num == -1) return;
    TMP_LinkInfo linkInfo = Parent.textInfo.linkInfo[num];
    string linkID = linkInfo.GetLinkID();
    if (linkID == "Clipboard") { GameManager.Clipboard = linkInfo.GetLinkText(); }
    else if (ForceOpen) Stationpedia.OpenAt(linkID);
    else Stationpedia.Instance.SetPage(linkID);
}
```

The class has `[RequireComponent(typeof(TextMeshProUGUI))]` and a public
`Parent` field. Its LateUpdate references `WorldManager.IsGamePaused` for
hover-color vertex updates, which ties it to game-scene state.

Our own `SixFive7LinkHandler` replicates the click-only behavior (line
221692 onwards) without the LateUpdate dependency. See §8.

### 6.16 Sibling index 21 sanity

`Transform.SetSiblingIndex(N)` clamps to `childCount - 1` if N exceeds
available siblings. With SPA loaded, our index-21 section sits below SPA's
index-20 OperationalDetailsCategory. Without SPA, our section sits last.
Both cases render correctly.

### 6.17 Stationpedia.OnPageChanged event (non-patch hook)

`Stationpedia.OnPageChanged` at game.cs:230434 is a `public static event Event`
fired from `SetPage` (line 230781) whenever navigation changes. Stable,
public, no reflection needed. Available as an alternative to Harmony
patching for mods that want to react to page navigation without intercepting
the render. Not used by StationpediaPlus's current design; documented here
in case a future feature needs it.

### 6.18 GuidesPages / LorePages leak on re-register

`Register` appends to `GuidesPages` or `LorePages` based on `page.DisplayFilter`
(lines 230958-230965) but NEVER removes stale entries. If a mod re-registers
a page with a different DisplayFilter (e.g. changing from Guides to
Undefined), the old entry leaks.

Not relevant to StationpediaPlus because:
- Reference pages use `SPDAEntryType.Undefined` (no Guides/Lore insertion).
- LogicType pages are re-registered every Regenerate with the same filter.

Documented in case a future helper wants to use Guides/Lore filters
legitimately.

### 6.19 Search trigger chain

Search is driven by input events wired in `Stationpedia.Awake` at game.cs:230436:

```csharp
SearchField.onSubmit.AddListener(delegate {
    StartSearchNow();
    KeyManager.RemoveInputState("Stationpedia");
});
SearchField.onValueChanged.AddListener(SearchBehaviour);
```

Chain: `onValueChanged` → `SearchBehaviour(text)` → if non-empty,
`SetPage("Search")` + `StartSearchCountdown` (400ms debounce) →
`WaitStartSearch` → `ClearAndStartSearch(text)` → `ClearPreviousSearch` +
`ForceSearch` → builds regex pattern → `DoSearch(hash, pattern, cancelToken)`
→ iterates `StationpediaPages` in reverse, calls `IsRegexMatch`, `MakePage`
for each hit.

Enter-key / submit triggers the same chain via `StartSearchNow`
(zero-delay variant).

### 6.20 DoSearch is async UniTask (why we don't postfix it directly)

`DoSearch` is an `async UniTask` (game.cs:230584) that mutates a pooled
`_SPDASearchInserts` list of UI widgets in-flight. It has no return list and
no `ref` parameter that a Harmony postfix could filter.

This is why our filter targets `StationpediaPage.IsRegexMatch` instead:
- IsRegexMatch returns `bool`; Postfix with `ref bool __result` works cleanly.
- IsRegexMatch has exactly one caller in the game (DoSearch itself).
- Returning false short-circuits the MakePage call in DoSearch's loop.

### 6.21 IsRegexMatch 255-char cutoff

`StationpediaPage.IsRegexMatch` (game.cs:233728) truncates at 255 chars when
performing the regex match. Pages with Description longer than 255 chars
will still match on Title and Key, but regex-body search is skipped.

Irrelevant for our Ref pages (filtered out of search regardless) and
acceptable for LogicType pages (descriptions are short by design).

### 6.22 ThingTemplate placeholder dead code

`english_help.xml` defines a `ThingTemplate` entry with `{0}..{3}`
placeholders, but current `PopulateThingPages` (game.cs:231964) does NOT
apply `string.Format` with this template. It sets `page.Description`
directly from the language XML's `<RecordThing>/<Description>`. The
placeholders are legacy.

Mod authors should not attempt to template against `ThingTemplate`; it is
a no-op.

### 6.23a UniversalPage category inventory

The full set of `StationpediaCategory` fields on `UniversalPage`, each with
the `Populate*Inserts` method that fills it and the `StationpediaPage` field
that method reads (from game.cs lines 233895-233932 and the Populate methods
at 234554-234574):

| UniversalPage field | Populate method | Source on page |
|---|---|---|
| `SlotContents` | `PopulateSlotInserts` | `page.SlotInserts` |
| `CostToPrintContents` | `PopulateHowToBuildInserts` | `page.HowToBuild` |
| `BuildStateContents` | `PopulateBuildStatesInserts` | `page.BuildStates` |
| `StructureVersionContents` | `PopulateStructureVersion` | `page.StructVersionInsert` |
| `LogicContents` | `PopulateLogicInserts` | `page.LogicInsert` |
| `LogicInstructions` | `PopulateLogicInstructions` | `page.LogicInstructions` (+ `page.HasMemory`) |
| `LogicSlotContents` | `PopulateLogicSlotInserts` | `page.LogicSlotInsert` |
| `ModeContents` | `PopulateModeInserts` | `page.ModeInsert` |
| `ConnectionContents` | `PopulateConnectionInserts` | `page.ConnectionInsert` |
| `LifeRequirements` | `PopulateLifeRequirements` | `page.LifeRequirements` |
| `FoundInOreContents` | `PopulateOreInserts` | `page.FoundInOre` |
| `FoundInGasContents` | `PopulateGasInserts` | `page.FoundInGas` |
| `FoundInFermentationContents` | `PopulateFermentationInserts` | `page.FoundInFermentation` |
| `ConstructedThingsContents` | `PopulateKitInserts` | `page.ConstructedByKits` (naming inverted in source) |
| `ProducedThingsContents` | `PopulateProducedThings` | `page.ProducedThingsInserts` |
| `ConstructedByKitsContents` | `PopulateConstructedThings` | `page.ConstructedThings` (also inverted) |
| `ResourcesUsed` | `PopulateUsedResources` | `page.ResourcesUsed` |
| `UsedIn` | `PopulateUsedIn` | `page.UsedIn` |
| `CombustionInfo` | `PopulateCombustionInfo` | `page.CombustionInserts` |

Note: the `ConstructedThingsContents` ↔ `ConstructedByKits` and
`ConstructedByKitsContents` ↔ `ConstructedThings` pairs are genuinely
inverted in the vanilla source (likely a historical naming mixup). If
future work needs to inject into either kit-related category, match the
mapping above precisely.

For StationpediaPlus's first consumer we only mutate `page.LogicInsert`
indirectly (via enum extension + CanLogicRead/Write postfixes; Decision 11A).
Other categories are untouched. The inventory above exists for future mods
that may need to inject slots, recipes, version history, etc.

### 6.23b Page navigation back-stack (`_pageHistory`)

`Stationpedia` maintains a private `_pageHistory` list for the back-button
navigation state. Updated by `SetPage` when a forward navigation occurs;
consumed by the home-screen back button. Not exposed publicly; untouched by
our helpers. Our `SixFive7LinkHandler` routes clicks to `SetPage`, so
cross-page navigation from inside our extension sections and reference
pages participates normally in the back stack.

### 6.23c ParsePage token expansion details

`StationpediaPage.ParsePage` additionally handles:

- `{LIST_OF_<CATEGORY>}` tokens: for each entry in
  `Stationpedia.DataHandler.ListOfAllListOfObjects`, a `{LIST_OF_<UPPER>}`
  token in Text adds the category to `PageCustomCategories` and is
  subsequently stripped from the parsed output. The game's
  `PopulateCustomCategories` step at ChangeDisplay-time renders the
  corresponding list per category name.
- `_worldHashes` placeholder: a magic token (private constant) that, when
  found in Text, is replaced with the parsed version of
  `NewWorldMenu.WorldHashes` (used by the Home page and world-setup pages
  to dynamically insert the list of world hashes). Not relevant to our
  content; listed for completeness.

Our StationpediaPlus helpers never emit `{LIST_OF_...}` or `_worldHashes`
tokens. Reference-page and LogicType-page markup uses only the standard
markup from §6.14.

### 6.23 Vanilla transmitter/receiver body text

`english.xml` (at `<StationeersInstall>\rocketstation_Data\StreamingAssets\Language\`)
contains identical body text for both the Microwave Power Transmitter and
the Microwave Power Receiver (vanilla oversight; both prefabs share the
transmitter description):

> "The `{LINK:Norsec;Norsec}` Wireless Power Transmitter is an uni-directional,
> A-to-B, far field microwave electrical transmission system. The rotatable
> base transmitter delivers a narrow, non-lethal microwave beam to a
> dedicated base receiver. The transmitter must be aligned to the base
> station in order to transmit any power. The brightness of the
> transmitter's collimator arc provides an indication of transmission
> intensity. Note that there is an attrition over longer ranges, so the
> unit requires more power over greater distances to deliver the same
> output."

The last sentence ("attrition over longer ranges, so the unit requires more
power over greater distances") is the vanilla description of the behavior
our mod REPLACES with the explicit distance-cost formula. StationpediaPlus
leaves this description untouched; our extension section explains how the
mod supersedes the vanilla behavior.

---

## 7. Verified Stationpedia Ascended (SPA) internals

### 7.1 Identity

- Plugin GUID: `com.florpydorp.stationpediaascended`
- Harmony ID: `com.stationpediaascended.mod`
- Secondary Harmony ID (script engine): `com.stationpediaascended.mod.scriptengine`
- Workshop ID: `3634225688`
- Version: `0.8.6` per `[BepInPlugin]` (About.xml shows stale 0.8.5)
- Decompile root: `C:\Users\jori\AppData\Local\Temp\decompile_spa\`

Use these strings:
- `Chainloader.PluginInfos.ContainsKey("com.florpydorp.stationpediaascended")`
- `[HarmonyAfter("com.stationpediaascended.mod")]`

### 7.2 SPA's Harmony patches (full list)

All imperative `_harmony.Patch(...)`, default `Priority.Normal`, no
priority attributes anywhere in SPA's codebase.

| Target | Kind |
|---|---|
| `UniversalPage.ChangeDisplay` | Postfix |
| `UniversalPage.PopulateLogicSlotInserts` | Postfix (cosmetic slot compaction) |
| `Stationpedia.OnDrag` | Prefix |
| `Stationpedia.OnBeginDrag` | Prefix |
| `Stationpedia.ClearPreviousSearch` | Postfix (UI state only, not filtering) |
| `Stationpedia.SetPage` | Prefix (custom key navigation) |
| `Stationpedia.SetPageGuides` | Postfix |
| `Stationpedia.SetPageLore` | Prefix + Postfix |
| `KeyManager.SetupKeyBindings` | Postfix |
| `KeyManager.GetButtonDown` | Prefix |

**SPA does NOT patch:**
- `Stationpedia.PopulateLogicVariables`
- `Stationpedia.PopulateThingPages`
- `Stationpedia.Register`
- `StationpediaPage.IsRegexMatch` (our primary search-filter target)
- `Stationpedia.DoSearch`
- `Stationpedia.PopulateGuideLoreContents`
- `page.LogicInsert`, `PopulateLogicInserts`

So our three primary hooks and one optional secondary hook all land on
SPA-untouched methods except `UniversalPage.ChangeDisplay`, where we
coordinate via `[HarmonyAfter]`.

### 7.3 SPA's ChangeDisplay postfix behavior

At `HarmonyPatches.cs:111`. Flow:

1. Null-guards.
2. Destroys prior SPA-owned children by name: `GuideSectionsContent`,
   `SurvivalManualContent`, `OperationalDetailsCategory`.
3. Dispatches special pages (SurvivalManual, custom JSON guides, etc.).
4. `DeviceDatabase.TryGetValue(pageKey, out descriptionEntry)`; early return
   on miss.
5. Applies `pageDescription` / `Prepend` / `Append` if set.
6. Creates `OperationalDetailsCategory` at sibling index 20 if entry has
   non-empty `operationalDetails: [...]`.

### 7.4 SPA's DeviceDatabase

Public static dictionary on `StationpediaAscended.StationpediaAscendedMod`:

```csharp
public static Dictionary<string, DeviceDescriptions> DeviceDatabase { get; }
```

Populated synchronously by `LoadDescriptions` inside SPA's Awake. By the time
any BepInEx `OnAllModsLoaded` callback fires, the database is ready.

SPA entry schema:

```csharp
public class DeviceDescriptions
{
    public string deviceKey;
    public string displayName;
    public string pageDescription;
    public string pageDescriptionPrepend;
    public string pageDescriptionAppend;
    public string pageImage;
    public Dictionary<string, LogicDescription> logicDescriptions;
    public Dictionary<string, ModeDescription> modeDescriptions;
    public Dictionary<string, SlotDescription> slotDescriptions;
    public Dictionary<string, VersionDescription> versionDescriptions;
    public Dictionary<string, MemoryDescription> memoryDescriptions;
    public List<OperationalDetail> operationalDetails;
    public string operationalDetailsTitleColor;
    public string operationalDetailsBackgroundColor;
    public bool generateToc;
    public string tocTitle;
    public bool tocFlat;
}

public class LogicDescription
{
    public string dataType;     // "Boolean", "Float", "Integer", "ReferenceId", "String"
    public string range;        // "0-1", "0+", "any", "0 or id", etc.
    public string description;  // plain text only; no markup tokens
}
```

All lowercase-first field names. Plain fields, no properties, no constructors.

### 7.5 SPA's existing coverage of our devices

From shipped `descriptions.json`:

- `ThingStructurePowerTransmitter` (line 24385) — empty `operationalDetails: []`; `logicDescriptions` covers vanilla LogicTypes only.
- `ThingStructurePowerTransmitterReceiver` (line 24524) — same.
- `ThingStructurePowerTransmitterOmni` (line 24482) — same.

None of our six custom LogicType names appear in SPA's JSON. Without
`SpaBridge` enrichment, SPA users hovering our custom rows see "No detailed
description available yet." — which we fix automatically via
`LogicTypePageBuilder` invoking `SpaBridge`.

### 7.6 SPA tooltip flow

A 100ms polling coroutine watches `Stationpedia.CurrentPageKey`. On page
change, iterates every `SPDALogic` component under `LogicContents.Contents`
and attaches `SPDALogicTooltip`. Resolution chain:

1. `DeviceDatabase[deviceKey].logicDescriptions[cleanName]`
2. `GenericDescriptions.logic[cleanName]`
3. `GenericDescriptionsData.AdditionalData[cleanName]`
4. Placeholder: "No detailed description available yet."

`CleanName` strips `<[^>]+>` tags and trims. No case normalization. So our
`LogicName` column stripping leaves the bare enum name as the lookup key.

Our `TextElementFactory` produces raw TMP elements (not `SPDALogic`), so
SPA's coroutine ignores our custom content in extension sections. That's the
intended behavior.

### 7.7 SPA search caches

SPA has `SearchPatches._pageTitleIndex`, `_pageWordIndex`,
`_hideFromSearchCache`, `_lastSearchText`, `_lastResultCount`. Its
`FindMissingMatches` / `InjectMissingResults` could theoretically re-inject
a hidden page if its title matches a search query. Unlikely for explicitly
hidden keys (mod-prefix + topic doesn't match natural search terms), but
flagged for testing.

### 7.8 SPA AdditionalData tooltip fallback

Tooltip resolution chain (step 3) is `GenericDescriptionsData.AdditionalData[cleanName]`
— a `[JsonExtensionData]` catch-all dictionary that Newtonsoft.Json uses for
unknown JSON properties deserialized into `GenericDescriptionsData`. SPA
uses it as a dynamic bucket for community-authored descriptions not
captured by the typed schema.

Not relevant to our SpaBridge flow (we write to the typed `logicDescriptions`
dict, which is tier 1), but documented for completeness.

### 7.9 SPA GenericDescriptions property

`StationpediaAscendedMod.GenericDescriptions` is a public static
`GenericDescriptionsData` property populated from `descriptions.json`'s
`genericDescriptions` entry. Used as fallback tier 2 in tooltip resolution.
We do NOT write to it; SpaBridge targets per-device `logicDescriptions`
only.

### 7.10 SPA's ChangeDisplay destroys prior SPA children by name

SPA's `ChangeDisplay_Postfix` destroys any pre-existing children of
`UniversalPage.Content` named `OperationalDetailsCategory`,
`GuideSectionsContent`, or `SurvivalManualContent` before re-creating its
own (HarmonyPatches.cs:111 flow step 2).

Our `<ModName>Details` GameObjects are NOT in that destroy list, so SPA's
postfix leaves them alone. This is why the distinct-name convention is
essential: if we named ours `OperationalDetailsCategory`, SPA would destroy
it on every navigation.

### 7.11 SPA row-iteration prefab filter

SPA's `AddTooltipsToCategory` specifically filters for `SPDALogic` component
instances under `LogicContents.Contents`. Our `TextElementFactory.Create`
produces raw `TextMeshProUGUI` GameObjects (not `SPDALogic`), so SPA's
coroutine skips them. SPA only decorates vanilla-populated rows, which
includes our custom LogicType rows (because those ARE `SPDALogic` instances
produced by the game's own `PopulateLogicInserts`).

Net effect: SPA tooltips appear on our native custom rows (after SpaBridge
enrichment), and SPA ignores our extension section's text elements
(intended behavior).

### 7.12a SPA SearchPatches caches and internals

SPA's `SearchPatches` class (at `StationpediaAscended.Patches\SearchPatches.cs`)
owns the following static state:

- `_pageTitleIndex` / `_pageWordIndex` — inverted indexes from words/titles
  to `StationpediaPage` references. Built once by `BuildPageIndexes` on
  first need, rebuilt on explicit invalidation.
- `_hideFromSearchCache` — memoized bool per page computed by
  `ShouldHideFromSearch`.
- `_lastSearchText`, `_lastResultCount` — used to skip redundant
  reorganizations when the visible result count hasn't changed.

Key methods:

- `BuildPageIndexes` — scans `Stationpedia.StationpediaPages`, skips pages
  failing `ShouldHideFromSearch`, populates the two indexes.
- `ShouldHideFromSearch(StationpediaPage page)` — SPA's own hide policy.
  Based on name patterns like `Ruptured`, `Burnt`, `Wreckage`. Returns
  `true` (hide) for pages matching those patterns. This is SPA's opinion;
  our mod's hidden keys are NOT in this set.
- `ReorganizeSearchResults` — runs after vanilla `DoSearch` populates its
  pool; adds SPA category headers to the search UI, may inject missing
  matches from its own indexes via `FindMissingMatches` /
  `InjectMissingResults`.
- `ClearPreviousSearch_Postfix` — patches vanilla `Stationpedia.ClearPreviousSearch`
  to take care of SPA-owned category headers and to lazily register
  listeners on `SearchField` / build indexes.

Risk surface for our hidden pages: if SPA's title/word indexes contain our
`PowerTransmitterPlus_*` keys AND SPA's `FindMissingMatches` decides a
query matches them, SPA could inject them as "missing results" even after
our `IsRegexMatch` postfix returned false. Mitigated in practice because
SPA builds its indexes via `BuildPageIndexes` which skips pages failing
`ShouldHideFromSearch` — our pages DO pass `ShouldHideFromSearch` (SPA
doesn't know about our mod), so they ARE in SPA's indexes. Whether SPA
re-injects them depends on whether the typed query's regex happens to
match our page titles or keys. For `PowerTransmitterPlus_...` keys that's
unlikely (players rarely type the full prefix), but flagged as open item
O4 (§17) for runtime test verification.

Escape hatch if observed: soft-reflective postfix on SPA's
`ShouldHideFromSearch` to return true when the page's key is in our
`HiddenKeys` set. Implement only if runtime test T5 shows re-injection.

### 7.12b SPA UI-side click handlers

SPA ships two of its own `IPointerClickHandler` MonoBehaviours in
`StationpediaAscended.UI\`:

- `TocLinkHandler` — attached to dynamic TMP elements SPA creates inside
  its operational-detail sections. Handles `toc_*` link clicks (its own
  Table-of-Contents anchor system) AND falls through to
  `Stationpedia.Instance.SetPage(linkID, true)` for standard links.
  Includes hover-color feedback via vertex colors.
- `CategoryHeaderHandler` — attached to SPA's search-result category
  headers for click-to-expand behavior on the Search page.

We do NOT reuse `TocLinkHandler` even though our use case is similar
because:
- Reuse would require reflection attach of an SPA type, which creates a
  soft dependency on SPA's assembly (against our architecture, §2).
- SPA's handler understands `toc_*` links we don't emit (harmless but
  unnecessary code path).

Our `SixFive7LinkHandler` (§8.5) is the click-only equivalent without SPA
dependency. It lacks hover-color feedback, compensated by the mandatory
click-phrasing authoring rule (§11.3).

### 7.12 SPA prefab inventory on Stationpedia.Instance

Row prefabs visible as public fields on `Stationpedia.Instance`
(game.cs:230122-230150):

```csharp
public SPDAListItem ListInsertPrefab;
public SPDAListItem ListSearchPrefab;
public SPDACombustionItem CombustionItemPrefab;
public StationpediaCategory CategoryPrefab;          // what we clone
public SPDASlot SlotInsertPrefab;
public SPDAManufacturer ManufactureInsertPrefab;
public SPDAVersion MachineTierInsertPrefab;
public SPDALogic LogicInsertPrefab;                  // logic row prefab
public SPDAGeneric GenericPrefab;
public SPDAFoundIn FoundInInsertPrefab;
public SPDAFoundIn FermentationInsertPrefab;
public SPDAGeneric InfoBoxPrefab;
public SPDALifeRequirement LifeRequirementPrefab;
public SPDAHomePageCategory HomePageButtonPrefab;
```

Our helper uses `CategoryPrefab` (for cloning collapsible sections). The
`LogicInsertPrefab` would be used if we ever needed to inject SPDALogic
rows manually (Decision 11A says we don't).

---

## 8. Shared library design (StationpediaPlus)

### 8.1 Project location

```
c:\Source\SixFive7\StationeersPlus\StationpediaPlus\
```

Built as `StationpediaPlus.dll`. ILRepacked into each consuming mod at build
time (§9). No runtime artifact distributed separately.

### 8.2 Namespace

`StationpediaPlus` — all public types live at this root.

`StationpediaPlus.Internal` — internal plumbing.

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
    Func<string, string> contentBuilder); // (pageKey) → markup body
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
— creates a new GameObject named `DetailText` with:
- `TextMeshProUGUI` donor-styled (font, size, color, line spacing, margin)
- `ContentSizeFitter` (horizontal unconstrained, vertical preferred)
- `RectTransform` anchored top-stretch
- `SixFive7LinkHandler` with `Text = tmp`

`HarmonyIdHelper.ForMod(string helperName)` — returns `<ConsumingModPluginGuid>.stationpediaplus.<helperName>`. The consuming mod's GUID is discovered via `Chainloader.PluginInfos` or via a per-mod registration (TBD at implementation time; both approaches work with ILRepack).

`SearchFilterPatch.EnsureInstalled()` — idempotent install of the two search
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
        "<ModName>Details" — destroy it via DestroyImmediate (idempotency)
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

Primary: postfix on `StationpediaPage.IsRegexMatch(string, string)` — two-arg overload only.

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
— none is patched by SPA.

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
   implementation time by reading
   `C:\Users\jori\AppData\Local\Temp\decompile_spa\StationpediaAscended.Patches\SearchPatches.cs`
   around line 360 per §18.2).
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

- Extension section title: `#FF7A18` (orange) — matches SPA's `OperationalDetailsCategory`.
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

`StationpediaPage.ParsePage` rewrites `[` → `<` and `]` → `>`. This is
reserved as an XML-escape hatch. Never author literal square brackets.

### 11.5 Markup token usage

- `{HEADER:Title}` — sub-headings within any section body.
- `{LIST}...{/LIST}` — bulleted lists (indentation).
- `{LINK:Key;Label}` — cross-page link. Label must include click phrasing (§11.3).
- `{LOGICTYPE:Name}` — LogicType cross-link (orange, auto-formatted).
- `{THING:PrefabName}` — Thing page cross-link (green, auto-formatted).
- `{POS:N}` — column alignment in table-like layouts.
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

### Step 1 — Adding a new prefab (Kind A page)

Mod responsibility:
- Author `<RecordThing>` entry in the mod's language XML with display name and description.
- Implement `CanLogicRead`/`CanLogicWrite` on the device class.
- Ensure custom LogicTypes (if any) are in `EnumCollections.LogicTypes.Values`
  via `LogicableInitializePatch`.

Library responsibility: none. Vanilla `PopulateThingPages` handles it.

### Step 2 — Extending a vanilla device (Kind C page)

Mod responsibility:

(a) Ensure custom LogicType rows appear natively by extending the enum
collection and postfixing `CanLogicRead`/`Write`. (Existing PTP pattern.)

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

### Step 3 — Adding custom LogicTypes (Kind B page; auto-triggers SpaBridge)

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

### Step 4 — Shared content (Kind D page)

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
- `ThingStructurePowerTransmitter` — section markup in §13.2.
- `ThingStructurePowerTransmitterReceiver` — section markup in §13.3.

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

### T1 — Native rows with content

Click each custom LogicType row. Navigates to the enriched LogicType page
(Summary / Formula / Related sections populated).

### T2 — Extension section renders

Scroll to bottom of transmitter page. One collapsible category
`<color=#FF7A18>Power Transmitter Plus Details</color>`, collapsed by
default. Click to expand. Three subsections render correctly: Distance cost
model, Auto-aim, Custom logic variables. Repeat on receiver (three
different subsections).

### T3 — Link clicks work inside extension sections

Inside expanded transmitter section, click "Click here for the full model
with worked examples". Navigates to
`PowerTransmitterPlus_MicrowavePowerTransmissionModel` page. Reference
page renders with full body.

### T4 — Reference page cross-links resolve

On the reference page, click "Go to the Microwave Power Transmitter page".
Navigates back to the transmitter page. Our extension section is still
present on re-render (CreatedCategories + CategoryBuilder postfix pattern).

### T5 — Reference page hidden from search

Open search bar. Type `transmission model`. No result for
`PowerTransmitterPlus_MicrowavePowerTransmissionModel`. Type the exact
title: still no match. Type `auto-aim`: no match for the auto-aim reference
page. Vanilla transmitter page may match if description contains the term.

### T5a — Reference page absent from SPA-reorganized search (SPA only; decision 19)

With SPA installed. Open search bar; type `transmission model`. Wait 1 full
second for SPA's `ReorganizeSearchResults` coroutine to fire (0.3s on submit,
0.8s on value-changed; 1s is a safe ceiling). Reference page
`PowerTransmitterPlus_MicrowavePowerTransmissionModel` does NOT appear in the
re-injected results either. Repeat for `auto-aim` against
`PowerTransmitterPlus_AutoAim`. Confirms `SpaSearchFilter` is active and its
reflection path resolved correctly.

If T5a fails (reference page appears after the delay), `SpaSearchFilter`
failed to install. Most likely causes: (a) SPA's method name differs from
`ShouldHideFromSearch` — read `SearchPatches.cs` and try fallbacks
(`BuildPageIndexes` Postfix, `ReorganizeSearchResults` Postfix); (b) reflection
exception swallowed — check BepInEx log for the LogDebug entry from the
soft-detect path.

### T6 — Reference page absent from home-page listings

Open home page. Browse every category listing and the Guides/Lore tabs.
Reference pages are absent from every listing.

### T7 — SPA installed: visual coexistence

Open transmitter page. SPA's Operational Details category (empty;
operationalDetails: [] in JSON) at sibling 20. Our Power Transmitter Plus
Details at sibling 21. No collision, distinct GameObject names.

### T8 — SPA installed: custom tooltip rendering

Hover a `MicrowaveSourceDraw` row. Tooltip shows our
TooltipDescription text ("Watts the transmitter pulls from its source
network..."), not SPA's placeholder ("No detailed description available
yet."). Same for all six custom rows.

Hover a vanilla Power row. SPA's standard tooltip shows. Unaffected by our mod.

### T9 — SPA uninstalled

All T1-T6 still pass. No Harmony patch failures in BepInEx log. Hover does
nothing on any row (vanilla has no tooltip system).

### T10 — Language change mid-session

Change game language. Revisit transmitter page. Extension section still
present. Reference pages still hidden. No duplicate categories. No errors.

### T11 — SpaBridge reflection verification (SpaBridge-using mods only)

With SPA installed, use a runtime debug command or log statement to inspect
SPA's `DeviceDatabase["ThingStructurePowerTransmitter"].logicDescriptions`.
Confirm all six of our `LogicDescription` entries are present with correct
`dataType`, `range`, `description`. Same for
`ThingStructurePowerTransmitterReceiver`.

### T12 — SpaBridge silent-degradation verification

Uninstall SPA. Boot the game. Expected:
- No BepInEx exceptions related to StationpediaPlus or SpaBridge.
- LogDebug entry confirming "SPA not installed; SpaBridge skipped".
- All other behavior identical to T9.

### T13 — Idempotency

Navigate to transmitter, away, back, 10 times in succession. Exactly one
extension section instance present each time. No duplicates, no orphans.

---

## 15. Implementation phase plan

Implementer should work these phases in order. Each phase produces a
testable artifact.

### Phase 0 — Pre-implementation verification (5 minutes, no code)

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

### Phase 1 — StationpediaPlus project skeleton

Create `c:\Source\SixFive7\StationeersPlus\StationpediaPlus\`:
- `.gitignore` excluding `Directory.Build.props` and `bin/`, `obj/`.
- `Directory.Build.props.template` with placeholder path.
- `StationpediaPlus.csproj` per §16.2.
- `src/`, `src/Internal/` empty folders.

Build produces empty DLL; confirms references resolve.

### Phase 2 — SixFive7LinkHandler

Smallest component; no dependencies. Implement and build.

### Phase 3 — Internal helpers

- `HarmonyIdHelper.cs`
- `TextElementFactory.cs` (depends on SixFive7LinkHandler)
- `SearchFilterPatch.cs` (depends on HarmonyIdHelper)
- `SpaSearchFilter.cs` (depends on HarmonyIdHelper; soft-dependent on SPA)

### Phase 4 — Public helpers

Implement in order of independence:
- `SpaBridge.cs` (no helper dependencies)
- `ReferencePage.cs` (depends on SearchFilterPatch and SpaSearchFilter)
- `CategoryBuilder.cs` (depends on TextElementFactory, HarmonyIdHelper)
- `LogicTypePageBuilder.cs` (depends on SpaBridge, HarmonyIdHelper)

### Phase 5 — Stub scan and cleanup

Run the cross-mod stub scan (§13.7 step 1). Document findings.

### Phase 6 — PowerTransmitterPlus migration

- Add StationpediaPlus.dll reference.
- Configure ILRepack (choose tooling option).
- Delete `StationpediaPatches.cs`.
- Add `PtpStationpediaContent.cs` per §13.
- Update `Plugin.cs` per §13.7 step 3.

### Phase 7 — Runtime verification

Run the 13-test matrix (§14). All must pass. T-Native-Rows is
release-blocking; if it fails, root-cause before shipping.

### Phase 8 — CLAUDE.md integration

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

### Phase 9 — Document this pattern for future mods

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
— closest to StationpediaPlus's shape (UI + TextMeshPro; missing only EventSystems).

### 16.7 Game decompile path

`c:\Source\tmp\game.cs` (~12.7 MB, all classes in `namespace Assets.Scripts.UI`
unless noted).

### 16.8 SPA decompile path

`C:\Users\jori\AppData\Local\Temp\decompile_spa\`

Key files:
- `StationpediaAscended\StationpediaAscendedMod.cs` — plugin entry, Awake, DeviceDatabase declaration
- `StationpediaAscended.Patches\HarmonyPatches.cs` — all Harmony patches
- `StationpediaAscended.Patches\SearchPatches.cs` — search reorg coroutine
- `StationpediaAscended.Data\*` — LogicDescription, DeviceDescriptions, etc.
- `StationpediaAscended.UI\TocLinkHandler.cs` — SPA's own click handler (for reference; we do not use it)

---

## 17. Open items

### O1 — ILRepack tooling choice (DEFERRED)

Two options:
- `ILRepack.Lib.MSBuild.Task` NuGet package
- Standalone `ilrepack.exe` binary

Decide at Phase 6 when integrating into PowerTransmitterPlus. No existing
SixFive7 mod uses ILRepack, so this choice sets the pattern.

### O2 — Mod GUID discovery for HarmonyIdHelper

At runtime, `HarmonyIdHelper.ForMod(helperName)` needs to know the consuming
mod's plugin GUID. Options:
- Discover from `Chainloader.PluginInfos` by finding the plugin whose assembly contains the calling method.
- Each consuming mod registers its GUID once in a `StationpediaPlus.SetOwningModGuid(guid)` init call.

Decide at Phase 4 when implementing HarmonyIdHelper.

### O3 — Runtime verification items flagged by T-Native-Rows

If T-Native-Rows fails, investigate:
- `LogicableInitializePatch` timing vs `EnumCollections.LogicTypes.Values` freeze point.
- `CanLogicRead`/`Write` postfix firing during `AddLogicTypeInfo` iteration.
- Whether our custom LogicType values are present in `EnumCollections.LogicTypes.Values` at the moment `Regenerate` fires.

The fix is a root-cause correction, NOT addition of a LogicInsert fallback
(Decision 11A is firm on this).

### O4 — SPA-cache re-injection (RESOLVED by decision 19 ii)

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

```
59090    GameManager.LoadGameDataAsync - Regenerate call site
188542   EnumCollections.LogicTypes declaration
188670   EnumCollection<T1,T2> ctor
194838   ReplaceHelpHeadings (HEADER token)
194874   ReplaceLogicTypes (LOGICTYPE token)
194901   Localization.ParseHelpText body
221638   HelpLinkHandler class
221692   HelpLinkHandler.OnPointerClick
221825   HelpReference.IsRegexMatch (UNRELATED; do not confuse)
230120   Stationpedia class
230129   Stationpedia.CategoryPrefab field
230137   Stationpedia.LogicInsertPrefab field
230339   Stationpedia.CurrentPageKey
230436   Stationpedia.Awake
230584   Stationpedia.DoSearch (primary search method)
230602   DoSearch call to IsRegexMatch
230619   Stationpedia.ClearAndStartSearch
230659   Stationpedia.ClearPreviousSearch (UI teardown, NOT filter)
230760   Stationpedia.SetPage
230861   Stationpedia.PopulateGuideLoreContents (secondary filter target)
230948   Stationpedia.Register body
231012   Stationpedia.Regenerate body
231037   OnLanguageChanged subscription
231042   Stationpedia.SortPages
231184   AddLogicTypeInfo
231198   AddLogicTypeInfo iterates EnumCollections.LogicTypes.Values
231208   AddLogicTypeInfo sets LogicName via ParseHelpText{LOGICTYPE:...}
231226   AddLogicTypeInfo .LogicInsert.Add
231964   Stationpedia.PopulateThingPages
231994   Thing page key construction
232041   page.Description assignment for Thing pages
232142   Stationpedia.PopulateLogicVariables
232163   LogicType page construction
233007   SPDAEntryType enum
233092   SPDALogic class
233199   StationpediaCategory class
233362   StationLogicInsert class (2 fields)
233507   StationpediaPage class
233632   StationpediaPage.LogicInsert field declaration
233666   StationpediaPage constructors
233683   StationpediaPage.ParsePage body
233709   StationpediaPage.IsRegexMatch (primary filter target; two-arg overload)
233728   IsRegexMatch 255-char cutoff
233792   UniversalPage class
233798   UniversalPage.PageDescription
233800   UniversalPage.Content
233970   UniversalPage.CreatedCategories
234152   UniversalPage.PopulateLogicInserts body
234485   UniversalPage.ChangeDisplay body
234508   CheckAndSetTextElement(PageDescription, page.Description)
234554   Populate* calls begin
234574   Populate* calls end
234188   PopulateLogicInstructions (uses SPDAGeneric prefab)
230434   Stationpedia.OnPageChanged event declaration
230494   Stationpedia.ClearAll
230675   Stationpedia.Initialize
230781   SetPage CurrentPageKey update line
231053-4 GuidesPages/LorePages sort
231057   Stationpedia.UpdateLinkedPages
231068   UpdateLinkedPages ImportantPage bump (HomePageOverride)
231083   UpdateLinkedPages ImportantPage bump (lore faction)
232115   PopulateThingPages ParsePage call
232183   Description assignment in PopulateLogicVariables
232185   ParsePage call in PopulateLogicVariables
232434   Stationpedia.PopulateLists
232465   SPDADataHandler.GenerateList overloads begin
233195-247 StationpediaCategory class body (full)
233236-246 StationpediaCategory.ClearChildInserts body
233515   StationpediaPage.SortPriority default
233517   StationpediaPage.ImportantPage field
233664   StationpediaPage.Parsed getter (backing-field only; no lazy parse)
233706   ParsePage Description one-shot guard line
233895-932 UniversalPage category field declarations block
280634   Logicable.CanLogicRead base switch
280696   Logicable.CanLogicWrite base switch
359638   static Logicable.LogicTypes array
359656   Logicable.Initialize
386861   PowerReceiver class
387065   PowerTransmitter class
```

### 18.1a /spda_dumpkeys console command

SPA registers a BepInEx console command `/spda_dumpkeys`
(`StationpediaAscendedMod.cs:1652-1684`) that iterates
`Stationpedia.StationpediaPages` and prints every page's Key, Title, and
DisplayFilter to the console. Useful during implementation/debugging to
confirm:

- Our reference pages are registered in `_linkIdLookup`.
- Our hidden keys appear in `StationpediaPages` (they should; the filter is
  at search-display time, not at registration).
- No key collisions between our mod and any other mod's registered keys.

Not invoked automatically; run from the BepInEx console during
interactive testing. Available only when SPA is installed. For non-SPA
debugging, write an equivalent one-shot diagnostic in the implementing
mod temporarily.

### 18.2 SPA reference

```
Plugin GUID:        com.florpydorp.stationpediaascended
Harmony ID:         com.stationpediaascended.mod
Workshop ID:        3634225688
Version:            0.8.6 (runtime); 0.8.5 (stale About.xml)

Decompile root:
  C:\Users\jori\AppData\Local\Temp\decompile_spa\

Key files:
  StationpediaAscended\StationpediaAscendedMod.cs
    :30      [BepInPlugin]
    :75      PluginGuid const
    :81      HarmonyId const
    :338     Awake
    :375     Initialize
    :1185    LoadDescriptions
    :1257    ApplyHarmonyPatches
    :1285    new Harmony("com.stationpediaascended.mod")
    :1301    ChangeDisplay patch registration
    :1652    /spda_dumpkeys command
    :2116    tooltip attach site
    :2210    AddTooltipsToCategory
    :2262    Tooltip iteration
    :2429    GetLogicDescription
    :2569    CleanLogicTypeName

  StationpediaAscended.Patches\HarmonyPatches.cs
    :55      PopulateLogicSlotInserts_Postfix
    :111     ChangeDisplay_Postfix
    :361     HandlePageDescriptionModifications
    :1418    CreateOperationalDetailsCategory
    :2324    CreateTextElement

  StationpediaAscended.Patches\SearchPatches.cs
    :124     ClearPreviousSearch_Postfix
    :237     ReorganizeSearchResults
    :360     ShouldHideFromSearch
    :410     InjectMissingResults

  StationpediaAscended.Data\
    DescriptionsRoot.cs, DeviceDescriptions.cs, LogicDescription.cs,
    OperationalDetail.cs, GenericDescriptionsData.cs

  StationpediaAscended.descriptions.json
    :24385   ThingStructurePowerTransmitter entry (empty operationalDetails)
    :24482   ThingStructurePowerTransmitterOmni entry
    :24524   ThingStructurePowerTransmitterReceiver entry
```

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
- Preview image dimensions (not applicable to StationpediaPlus — library, no Preview.png).
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
  StationpediaPlus/         (NEW — this library)
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
- SLE (Stationeers Logic Extended) Workshop: https://steamcommunity.com/sharedfiles/filedetails/?id=3625190467

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

### Decision 1 — API shape
**A — Three imperative helpers** (`CategoryBuilder.Register`,
`ReferencePage.Register`, `LogicTypePageBuilder.Register`, plus `SpaBridge`
called automatically). Mods call each helper as needed, à la carte.

Motivation: modular, piecemeal adoption, clear per-helper boundaries, simple
documentation per helper.

### Decision 2 — Page taxonomy
**B — Four kinds** (New-thing, LogicType, Extension, Reference).

Motivation: LogicType pages have their own game-side generator, key prefix,
and rendering path; folding them into Extended-device hides that distinction.
Explicit categories reduce doc traversal cost for new mod authors.

### Decision 3 — LogicType page enrichment ownership
**B — Shared `LogicTypePageBuilder` helper.** Brings helper count to four.

Motivation: composition is nontrivial (header sections, related links, parse
timing, sprite assignment); centralizing enforces cross-mod visual
consistency. Under ILRepack each mod gets its own embedded postfix, but
authoring-time code is shared.

### Decision 4 — Reference page key prefix
**B — `<FullModName>_<Topic>`** (NOT abbreviation).

Motivation: abbreviations risk collision with unrelated third-party mods by
different authors. Full mod name is unambiguous across the entire Stationeers
modding ecosystem.

### Decision 5 — Reference page title format
**A — Plain natural-language title.** No bracketed abbreviation.

Motivation: no abbreviations anywhere in user-visible documentation. This
rule extends globally to all user-visible strings.

### Decision 6 — Hide-from-search filter identification
**B — Explicit `HiddenKeys` HashSet.**

Motivation: decouples "has mod prefix" from "is hidden from search" for
future flexibility (mod-prefixed pages that should remain searchable,
per-page visibility toggles via config, etc.).

### Decision 7 — Extension section GameObject name
**A — `<ModName>Details`** (e.g. `PowerTransmitterPlusDetails`).

Motivation: GameObject names are dev-facing only; short and unambiguous in
Unity hierarchy. Matches SPA's `OperationalDetailsCategory` idiom for visual
consistency.

### Decision 8 — Section title format
**A — `<color=#FF7A18>{ModName} Details</color>`** (non-bold, " Details" suffix).

Motivation: matches SPA's visual rhythm. Vanilla category titles are
non-bold; bolding only our section creates dissonance. " Details" suffix
signals supplementary content.

### Decision 9 — Link-click handler
**A — Ship custom `SixFive7LinkHandler`.**

Motivation: avoids vanilla `HelpLinkHandler`'s `WorldManager.IsGamePaused`
LateUpdate dependency (risk of NullReferenceException on pre-world-init pedia
open). Minimal click-only scope; reduced failure surface.

**Compensating authoring rule**: every `{LINK:Key;Display}` label must
include explicit click phrasing (§11.3).

### Decision 10 — Harmony instance IDs
**A — One instance per helper per mod.**

Motivation: preserves three-helper boundary to runtime diagnostics. Mod
conflict reports naming a Harmony ID immediately identify the responsible
helper. ILRepack constraint: IDs derived from consuming mod's GUID, not
hardcoded to `com.sixfive7.*`.

### Decision 11 — LogicInsert fallback
**A — No fallback.** Mandatory T-Native-Rows release-blocking test verifies.

Motivation: loud failure beats silent compensation for a bug class that
should be root-caused. Simpler codebase.

### Decision 12 — LogicType page body assignment pattern
**B — Text + ParsePage** (idiomatic game pattern).

Motivation: matches vanilla `PopulateLogicVariables` exactly. ParsePage is
the game's authoritative parser including niche features (`{LIST_OF_...}`,
`PageCustomCategories`, `_worldHashes`) that direct assignment skips.

### Decision 13 — PowerTransmitterPlus reference page count
**B — Two reference pages** (`MicrowavePowerTransmissionModel` + `AutoAim`).

Motivation: auto-aim is referenced from three locations (transmitter
extension section, MicrowaveAutoAimTarget page, MicrowaveLinkedPartner
page); meets the "cited from multiple pages → promote to reference"
taxonomy rule. Sets precedent for future mods.

### Decision 14 — Shared library file layout
**A — `src/` + `src/Internal/` subdir.**

Motivation: public-vs-internal split visible in file tree. `Internal/` is a
recognized .NET convention. Scales as the library grows.

### Decision 15 — Testing matrix scope
**B — 12 tests plus T-Native-Rows (13 for PTP).**

Motivation: SpaBridge is pure reflection into another mod's internals;
explicit runtime verification tests catch regressions at test time rather
than user-report time.

### Decision 16 — SpaBridge usage policy
**B — Structural enforcement via the shared codebase** (NOT convention-based).

`LogicTypePageBuilder.Register(spec)` internally invokes
`SpaBridge.TryEnrichLogicTooltips` for every device in `spec.RelatedDeviceKeys`.
SpaBridge entry tuple is derived from spec fields. Mods cannot accidentally
skip SPA enrichment; the library does it for them.

Motivation: CLAUDE.md conventions are non-deterministic (humans forget,
reviews miss). Compiled code is deterministic.

### Decision 17 — DLL distribution model
**C — ILRepack into each mod from a shared codebase.**

Motivation: players must not subscribe to or install any separate artifact.
Each mod ships a single self-contained DLL. Imposes the seven architectural
constraints listed in §9.

### Decision 18 — Stub deprecation timing
**A — One-commit migration.** Plus cross-mod stub-scan TODO.

Motivation: Register replace semantics mean the old stub provides no
runtime fallback value; running both in parallel adds risk, not safety.
Clean commit history; atomic revert available. Shared codebase is
test-covered (13 tests) so shipping with confidence is warranted.

Additional action: before migrating each mod, scan for dead stub code
(§13.7 step 1) and remove.

### Decision 19 — SPA search-index re-injection handling
**ii — Soft-dependent SPA patch** (parallel to SpaBridge enforcement pattern).

Surfaced by R1 findings. SPA's `ReorganizeSearchResults` re-injects search
results from its own `_pageTitleIndex` built by `BuildPageIndexes` from
`Stationpedia.StationpediaPages`. SPA's own `ShouldHideFromSearch` knows
only about burnt / ruptured / wreckage variants; it has no awareness of our
`HiddenKeys`. Consequence: patching `StationpediaPage.IsRegexMatch` alone
is insufficient when SPA is installed — reference pages reappear in SPA's
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

Target method: `StationpediaAscended.Patches.SearchPatches.ShouldHideFromSearch`
at SPA SearchPatches.cs:360 per §18.2. Confirm exact signature at
implementation time. Fallback patch targets if renamed: `BuildPageIndexes`
Postfix (filter `_pageTitleIndex`) or `ReorganizeSearchResults` Postfix
(filter result list).

Harmony instance ID (decision 10 pattern): `<ModGuid>.stationpediaplus.spasearchfilter`.

Motivation: reference pages must be structurally hidden for ALL users, not
just those without SPA. The CLAUDE.md rule "reference pages are hidden from
search" cannot be quietly broken for 40%+ of the player base who run SPA.
Soft-detect keeps the zero-SPA-dependency invariant intact; ILRepack-friendly.

Testing: T5a added to the matrix (§14) — open Stationpedia with SPA installed,
search for a reference-page title, wait 1 second for SPA's coroutine, confirm
the page does not appear in either vanilla or SPA-reorganized results.

### Research Task R1 — Search method identification (RESOLVED)

Primary vanilla target: `StationpediaPage.IsRegexMatch(string, string)` at
game.cs:233709. One caller (`DoSearch`). SPA does NOT patch it. Prefix that
sets `__result = false` when `__instance.Key` is in HiddenKeys.

Secondary vanilla target: `Stationpedia.PopulateGuideLoreContents(List<string>, bool)`
at game.cs:230861. Prefix that filters `SPDAKeys` via a LINQ `Where`. Covers
the Guides/Lore home-page shortcuts.

Tertiary vanilla target: `Stationpedia.PopulateLists()` at game.cs:232434.
Postfix that walks `DataHandler._listDictionary` and removes entries whose
`PageLink` is in HiddenKeys. Covers home-page category-listing pages.

SPA-side target (soft-dependent, decision 19 ii):
`StationpediaAscended.Patches.SearchPatches.ShouldHideFromSearch` at SPA
SearchPatches.cs:360. Postfix that ORs in HiddenKeys. Handles SPA's
`ReorganizeSearchResults` coroutine re-injection.

DO NOT patch: `ClearPreviousSearch`, `SetPage`, `Register`, `Regenerate`,
`SortPages`, `Render`, `GetPage`, `OpenPageByKey`, `OpenAt`.

### Research Task R2 — HintPath inventory (RESOLVED)

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
- `Stationpedia.PopulateLogicVariables` — Postfix (LogicTypePageBuilder)
- `UniversalPage.ChangeDisplay` — Postfix, `[HarmonyAfter(spa)]` (CategoryBuilder)
- `StationpediaPage.IsRegexMatch(string, string)` — Prefix (SearchFilterPatch, primary)
- `Stationpedia.PopulateGuideLoreContents` — Prefix (SearchFilterPatch, secondary)
- `Stationpedia.PopulateLists` — Postfix (SearchFilterPatch, tertiary; filters `_listDictionary`)
- `StationpediaAscended.Patches.SearchPatches.ShouldHideFromSearch` — Postfix, soft-dependent (SpaSearchFilter; decision 19 ii)

### Mandatory markup rules
- No bare identifiers — always `{LOGICTYPE:X}` or `{LINK:K;D}`.
- No literal `[` or `]` — ParsePage rewrites.
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

### SLE reference (pattern origin)
- Workshop ID: `3625190467`
- Author: ThunderDuck
- Reserved LogicType band: 1000-1830 (avoid collision)

---

## 21. Historical and ecosystem context

This section records context that surfaced during the planning thread but
does not directly drive implementation. Included for the implementing
agent's situational awareness and for archival completeness.

### 21.1 Why custom LogicTypes don't appear on vanilla pages without patches

Early investigation established that vanilla Stationpedia pages for stock
devices (like the Microwave Power Transmitter) have a frozen `LogicInsert`
list built once at game startup. Even though `Logicable.LogicTypes` can be
extended at runtime, the page's `LogicInsert` is populated inside
`AddLogicTypeInfo` which iterates `EnumCollections.LogicTypes.Values` — a
collection constructed from `Enum.GetValues(typeof(LogicType))` at
`EnumCollection<LogicType,ushort>` construction time. Our runtime-cast
`ushort → LogicType` values are NOT in the compiled enum, so
`Enum.GetValues` doesn't return them.

This is why we (1) extend `EnumCollections.LogicTypes` via
`LogicableInitializePatch`, (2) patch `Enum.GetName` and
`EnumCollection<LogicType, ushort>.GetName` / `GetNameFromValue` so
reflection-based name lookups find our values, (3) postfix
`CanLogicRead`/`CanLogicWrite` on `PowerTransmitter`/`PowerReceiver` to
return true for our types, and (4) inject into `ProgrammableChip.AllConstants`
for IC10 name resolution.

Given all four of those, `AddLogicTypeInfo` naturally discovers and emits
native rows for our custom LogicTypes. This is why the LogicInsert fallback
(Decision 11) is NOT needed under normal operation.

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

### 21.3 Why SpaBridge enforcement is structural, not conventional

User explicitly rejected relying on CLAUDE.md conventions or code-review
discipline for SPA tooltip integration. "CLAUDE.md rules are not
deterministic (humans forget; reviews miss). Compiled code is deterministic."

The chosen enforcement: `LogicTypePageBuilder.Register(spec)` invokes
`SpaBridge.TryEnrichLogicTooltips` automatically for every device in
`spec.RelatedDeviceKeys`. Mod authors who register a LogicType page cannot
accidentally skip SPA tooltip enrichment because the registration call
does it for them. `SpaBridge` remains public for edge-case direct use.

### 21.4 Why the custom link handler ships instead of reusing vanilla

Vanilla `HelpLinkHandler` would give us hover-color feedback for free, but
its `LateUpdate` references `WorldManager.IsGamePaused`, tying our UI to
scene state. Risk: opening Stationpedia from the main menu (before world
init) could throw NullReferenceException. The custom `SixFive7LinkHandler`
avoids LateUpdate entirely (click-only scope), trading hover-color for
reduced failure surface. Compensated by the mandatory click-phrasing rule
on `{LINK:...}` display labels (§11.3).

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
  their own keys — additive effect.
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

This rule — no abbreviations in user-visible strings — extends globally to
all authored content (Decision 5 note).

### 21.8 Current StationpediaPatches.cs stub (what decision 18 replaces)

The existing PowerTransmitterPlus stub at
`c:\Source\SixFive7\StationeersPlus\PowerTransmitterPlus\PowerTransmitterPlus\StationpediaPatches.cs`
is ~66 lines. Structure:

- `internal static class StationpediaPatches` with method
  `RegisterCustomLogicTypePages()` that:
  - Uses `AccessTools.TypeByName("Assets.Scripts.UI.Stationpedia")` with
    namespace fallback to bare `"Stationpedia"` for game-version resilience.
  - Same fallback pattern for `StationpediaPage`.
  - Uses reflection to find the static `Register(StationpediaPage, bool)` method.
  - Iterates `LogicTypeRegistry.All` and for each custom LogicType creates
    a `StationpediaPage` via
    `Activator.CreateInstance(pageType, "LogicType" + t.Name, t.Name, t.Description)`
    then calls `register.Invoke(null, new object[] { page, false })`.
  - Wraps everything in try/catch and logs via `LogDebug` on failure.
- `public static class StationpediaPopulateLogicVariablesPatch` Harmony
  patch with:
  - `TargetMethod()` resolving via `AccessTools.TypeByName(...)` + fallback
  - `Prepare()` gating on the target method existing
  - `Postfix()` calling `StationpediaPatches.RegisterCustomLogicTypePages()`

Source comment: `// Pattern lifted from Stationeers Logic Extended (ThunderDuck).`

Net runtime effect today: six LogicType pages registered (one per custom
LogicType), each with bare Title and one-line Description (from
`LogicTypeRegistry.CustomLogicType.Description`). No page body beyond the
description. No cross-links. No device-page content. No reference pages. No
SPA tooltip enrichment.

Decision 18 A replaces this entire file in one commit with calls to the
StationpediaPlus helpers from `Plugin.cs`. The LogicType values table and
the underlying `LogicTypeRegistry` class stay; only `StationpediaPatches.cs`
is deleted.

### 21.9 SPA's "manual patching" self-comment

SPA's `StationpediaAscendedMod.ApplyHarmonyPatches` carries a source comment:

> Manual patching - more reliable than attribute-based patching for game assemblies.

All SPA patches use imperative `_harmony.Patch(original, prefix: ..., postfix: ...)`
rather than `[HarmonyPatch]` attributes. Rationale is not elaborated further
in SPA's source, but consistent with "reflecting against a moving target"
concerns: imperative `AccessTools.Method(...)` with fallback name candidates
is friendlier to game-version drift than attributed patches that throw on
missing targets.

Implication for StationpediaPlus: our own Harmony installs follow the
imperative pattern (see §11 helper spec). Each helper resolves its target
via `AccessTools.Method` with a `Prepare()`-equivalent gate and fails
silently if the target method has been renamed/removed in a future game
update. Matches SPA's style and survives the same failure modes.

### 21.10 SPA tooltip coroutine timing

`MonitorStationpediaCoroutine` (SPA `StationpediaAscendedMod.cs`) polls
`Stationpedia.CurrentPageKey` at a 100ms interval. On a page-change
detection, it schedules `AddTooltipsAfterDelay` via a 2-frame delay
(Unity coroutine `yield return null` twice) to let vanilla
`UniversalPage.ChangeDisplay` finish populating `LogicContents.Contents`
before tooltip attachment iterates the rendered children.

Tooltip flow per detected page change:
1. Wait 2 frames.
2. Call `AddTooltipsToCategory(universalPageRef.LogicContents, pageKey, "Logic")`.
3. Iterate transforms under `LogicContents.Contents`; for each, get its
   `SPDALogic` component; if non-null and no existing `SPDALogicTooltip`,
   attach a new `SPDALogicTooltip` MonoBehaviour seeded with `pageKey`,
   `component.InfoValue.text` (the rendered row name; tag-stripped by
   `CleanLogicTypeName`), and `"Logic"` as the category name.

Implication: our custom LogicType rows (which render as `SPDALogic` prefab
instances inside the vanilla Logic Variables category) are automatically
decorated by SPA's coroutine regardless of whether SpaBridge ran. SpaBridge
supplies the tooltip TEXT content via `DeviceDatabase` enrichment; without
SpaBridge, SPA's tooltip shows the "No detailed description available yet."
placeholder. Decision 16 structural enforcement ensures SpaBridge runs
every time.

### 21.11 SPA descriptions.json shipped metrics

Size: approximately 1.2 MB (shipped as both an embedded resource
`StationpediaAscended.descriptions.json` inside SPA's DLL and as a loose
file next to the DLL at
`E:\Steam\steamapps\workshop\content\544550\3634225688\descriptions.json`).

Content breakdown:

- `devices`: 499 entries. One per modded or vanilla Thing that SPA documents.
- `guides`: 5 entries (Survival, Power, Airlock, AC, plus one general).
- `mechanics`: 2 entries (game-mechanic explainer pages).
- `genericDescriptions`: 250 entries (fallback LogicType / slot / version /
  memory descriptions used when a device-specific entry is missing).

Loading order (first hit wins):
1. Embedded resource `StationpediaAscended.descriptions.json` inside the DLL.
2. `<dll dir>/descriptions.json` (next to SPA's DLL).
3. `BepInEx/scripts/descriptions.json`.
4. `<My Games>/Stationeers/mods/StationpediaAscended/descriptions.json`.

Deserialized via Newtonsoft.Json into `DescriptionsRoot` which contains
`List<DeviceDescriptions> devices` etc. `GenericDescriptionsData` uses
`[JsonExtensionData]` so additional unknown keys are preserved in
`AdditionalData`.

Implication: SPA's shipped file already has entries for all three microwave
transmitter variants (`ThingStructurePowerTransmitter` at :24385,
`ThingStructurePowerTransmitterOmni` at :24482,
`ThingStructurePowerTransmitterReceiver` at :24524) with `logicDescriptions`
for every vanilla LogicType but empty `operationalDetails: []`. None of our
six custom LogicType names appear. SpaBridge's reflection into
`DeviceDatabase` adds entries to the existing `logicDescriptions` dicts on
these devices at runtime; it does not modify the shipped JSON file itself.

### 21.12 Per-dish AutoAim cache via ConditionalWeakTable

PowerTransmitterPlus's `AutoAimPatches` stores per-dish target state in a
`ConditionalWeakTable<WirelessPower, StrongBox<long>>`. GC-tied cleanup:
when a `WirelessPower` instance is collected, its cache entry is
automatically reclaimed. No manual lifecycle management. Cache entries are
set by `MicrowaveAutoAimTarget` writes (see §3.4) and cleared by manual
Horizontal/Vertical adjustments via postfixes on `RotatableBehaviour`
setters (with a re-entry flag to suppress clearing during auto-aim's own
writes).

This detail is not directly relevant to Stationpedia integration but
explains why `MicrowaveAutoAimTarget` reads return the current target id
without needing to re-resolve it every frame: the cached long sits in the
weak-table entry and a GetLogicValue postfix consults it.

### 21.13 Distance cost patch quartet (reference)

PowerTransmitterPlus's cost model is implemented as four interdependent
Harmony patches on `PowerTransmitter`:

| Patch | Target | Kind | Effect |
|---|---|---|---|
| GeneratedPowerNoDistanceDeratePatch | PowerTransmitter.GetGeneratedPower | Prefix (return false) | Drop vanilla distance-based derate; return Min(5000, PotentialLoad) uncapped |
| UsePowerInflateDebtPatch | PowerTransmitter.UsePower | Postfix | If multiplier > 1, add powerUsed * (multiplier - 1) to _powerProvided (debt accounting) |
| GetUsedPowerLiftCapPatch | PowerTransmitter.GetUsedPower | Postfix | If debt > returned value, set result to full debt (remove MaxPowerTransmission cap) |
| ReceivePowerVisualizerFixPatch | PowerTransmitter.ReceivePower | Postfix | Override VisualizerIntensity = (powerAdded / multiplier) / MaxPowerTransmission |

Disabling any one breaks the model observably. Not in scope for the
StationpediaPlus implementation but documented in the reference content
(PowerTransmitterPlus_MicrowavePowerTransmissionModel reference page and
the transmitter extension section).

---

End of 7.md.
