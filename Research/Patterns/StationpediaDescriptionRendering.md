---
title: StationpediaDescriptionRendering
type: Patterns
created_in: 0.2.6228.27061
verified_in: 0.2.6228.27061
verified_at: 2026-05-28
sources:
  - rocketstation_Data/Managed/Assembly-CSharp.dll :: Assets.Scripts.Localization.ParseHelpText
  - rocketstation_Data/Managed/Assembly-CSharp.dll :: Assets.Scripts.UI.Stationpedia.PopulateThingPages
  - rocketstation_Data/StreamingAssets/Language/en.resx (vanilla description corpus)
  - .work/decomp/0.2.6228.27061/Assembly-CSharp.decompiled.cs
related:
  - ../GameClasses/Stationpedia.md
  - ../GameSystems/StationpediaPageRendering.md
tags: [stationpedia, ui, unity]
---

# Stationpedia description rendering: text-to-TMP pipeline

How a vanilla device description in `en.resx` becomes pixels in the Stationpedia UI. Useful for any mod that wants to extend a description via a Harmony postfix on `Localization.GetThingDescription`: tells you what conventions to follow, what tokens you must NOT emit, and where text gets cached.

## The pipeline
<!-- verified: 0.2.6228.27061 @ 2026-05-28 -->

```
Localization.GetThingDescription(prefabName)         // reads en.resx Thing_<Name>_Description key
  ->  Localization.ParseHelpText(text)               // expands {TOKEN:...} macros, preserves \n
       ->  StationpediaPage.Description = parsed     // cached ONCE at Stationpedia.PopulateThingPages
            ->  TextMeshProUGUI.text = page.Description   // render
```

Postfixes on `GetThingDescription` see the raw text from `en.resx` before `ParseHelpText` runs, so they can append free-form text and have it flow through the rest of the pipeline naturally. The catch: macro tokens emitted by the postfix WILL be expanded by `ParseHelpText`. See "What tokens to avoid emitting" below.

## Vanilla newline convention: literal `\n`, no markup
<!-- verified: 0.2.6228.27061 @ 2026-05-28 -->

Sampled descriptions in `rocketstation_Data/StreamingAssets/Language/en.resx`:

- `Thing_StructureBattery_Description` (line 2816): four paragraphs separated by raw LF. Uses `{HEADER:POWER OUTPUT}`, `{LINK:...}`, `{THING:...}`.
- `Thing_StructureAreaPowerControl_Description` (line 2756): three paragraphs, raw LF, no headers.
- `Thing_StructureBatteryLarge_Description` (line 6576): four paragraphs, raw LF, `{HEADER:POWER OUTPUT}`.
- `Thing_StructureFurnace_Description` (line 3270): three paragraphs, raw LF, `{GAS:Oxygen}`, `{LINK:IngotPage;ingots}`.
- `Thing_StructureTransformer_Description` (line 3804): two paragraphs, raw LF.
- `Thing_StructureSolarPanel_Description` (line 3534): single paragraph.

Pattern: paragraph breaks use a single literal `\n` (LF in the XML body). No `<br>` tag, no `[hr]`, no BBCode. Rich-text styling is delegated entirely to `{HEADER:...}` and `{LINK:...}` token expansion -- the raw text body contains no `<b>`/`<i>`/`<color>` tags.

## `Localization.ParseHelpText`: token catalogue
<!-- verified: 0.2.6228.27061 @ 2026-05-28 -->

Verbatim (decompile line 194901):

```csharp
public static string ParseHelpText(string helpText) {
    ReplaceThings(ref helpText);          // {THING:X}      -> <link=ThingX><color=...>...</color></link>
    ReplaceGases(ref helpText);           // {GAS:X}        -> styled link
    ReplaceReagents(ref helpText);        // {REAGENT:X}    -> styled link
    ReplaceSlots(ref helpText);
    ReplaceColors(ref helpText);
    ReplaceHelpHeadings(ref helpText);    // {HEADER:X}     -> <size=120%><b>X</b></size>
    ReplaceHelpMargin(ref helpText);      // {POS:n}        -> <pos=n>
    ReplaceHelpLinks(ref helpText);       // {LINK:id;name} -> <link=id><color=#0080FFFF>name</color></link>
    ReplaceHelpList(ref helpText);        // {LIST:n}       -> <indent=n>
    ReplaceLogicTypes(ref helpText);      // {LOGICTYPE:X}
    ReplaceLogicSlotTypes(ref helpText);
    ReplaceInput(ref helpText);
    ReplaceHelpMacro(ref helpText, "{LIST}", "<indent=10>");
    ReplaceHelpMacro(ref helpText, "{/LIST}", "</indent>");
    return helpText;
}
```

`\n` is never touched, escaped, or stripped. The function only expands tokens of the form `{TOKEN:value}` and the two literal-match macros `{LIST}` / `{/LIST}`. The resulting string is fed directly into TextMeshPro, which interprets the embedded `<link=...>`, `<color=...>`, `<size=...>`, `<b>` tags from token expansion.

## What tokens to avoid emitting from a postfix
<!-- verified: 0.2.6228.27061 @ 2026-05-28 -->

A mod that postfixes `GetThingDescription` must NOT emit any of these literal strings unless the mod actually wants them expanded:

- `{THING:<prefabName>}` -- expanded to a TMP `<link>` pointing at the prefab's Stationpedia page. If the prefab name doesn't exist, the result is an `<N:English:...>` sentinel.
- `{LINK:id;displayName}` -- expanded to a generic TMP `<link>` with blue text.
- `{HEADER:title}` -- expanded to a bold, 120%-size heading.
- `{GAS:gasName}`, `{REAGENT:name}`, `{SLOT:type}`, `{COLOR:name}`, `{LOGICTYPE:name}`, `{LOGICSLOTTYPE:name}` -- styled links.
- `{POS:n}` -- TMP `<pos>` tag.
- `{LIST:n}`, `{LIST}`, `{/LIST}` -- indentation.
- `{INPUT:...}` -- key-binding lookup.

Plain text with `\n`, `\n\n` paragraph breaks, and ASCII punctuation passes through unchanged. If a postfix wants TMP styling, embed the raw TMP tag directly (`<b>...</b>`, `<color=#XXX>...</color>`) -- ParseHelpText doesn't touch them, TMP renders them at the end.

## TextMeshPro newline behaviour (`PageDescription` component)
<!-- verified: 0.2.6228.27061 @ 2026-05-28 -->

`PageDescription` is declared `public TextMeshProUGUI PageDescription;` at decompile line 233798. Grep for `paragraphSpacing` / `lineSpacing` in the decompile returns zero hits, meaning the Stationpedia uses the TMP component's prefab defaults. In a default TMP component:

- `\n` -> one hard line break.
- `\n\n` -> one blank line (two hard breaks).
- `paragraphSpacing = 0` (default) -> visual paragraph gap equals one line height.

Vanilla descriptions get their paragraph effect from `\n` alone (single line break, visually one blank line gap because each line carries normal line height). A postfix using `\n\n` produces a slightly wider visual gap before its appended content, which is acceptable for "this is an additive section" framing.

## `StationpediaPage.Description` cache: once at game load
<!-- verified: 0.2.6228.27061 @ 2026-05-28 -->

`Stationpedia.PopulateThingPages()` (decompile line 231964) iterates `Prefab.AllPrefabs` once and stores the parsed text:

```csharp
page3.Description = Localization.ParseHelpText(Localization.GetThingDescription(allPrefab.PrefabName));
```

Called from `Stationpedia.Initialize()` (line 231023). After this runs, `StationpediaPage.Description` is fixed for the session. A `GetThingDescription` postfix sees the call exactly once during page population and again on every separate caller (tooltips, build-state hover, etc.) -- those non-Stationpedia surfaces DO see live config values from a postfix; only the Stationpedia page itself is cache-locked until restart.

For mods that want live updates of the Stationpedia entry on a config change, the page would need to be re-built (either re-call `PopulateThingPages` or directly mutate `StationpediaPage.Description` for the affected key). The vanilla flow does not support this; it would be a custom mod feature.

## Render path: plain assignment
<!-- verified: 0.2.6228.27061 @ 2026-05-28 -->

`CheckAndSetTextElement(PageDescription, page.Description)` (line 234508) ends in `textMesh.text = text2;` (line 233983). No further string transformation between the cached `StationpediaPage.Description` and the TMP component's `text` field. Whatever the cache holds is what TMP renders.

## Worked example: PowerGridPlus footer (post-b3baffb)
<!-- verified: 0.2.6228.27061 @ 2026-05-28 -->

`Mods/PowerGridPlus/PowerGridPlus/StationpediaPatches.cs` `BuildApcFooter` and `BuildBatteryFooter` build footer text like:

```
\n\n--- Power Grid Plus ---\n
Charge rate: capped at 1 kW (server config "APC Battery Charge Rate"). ...\n
Output: capped at the output cable's MaxVoltage. ...\n
Cable tier: input and output cables must be the same tier; mismatched cables burn at the junction when power flows.\n
Bug fix: the vanilla idle-leak is closed. Battery does not slowly drain when nothing is connected downstream.
```

This passes ParseHelpText unchanged (no `{TOKEN:...}` patterns, no rich-text tags). TMP renders the `\n\n` separator as a blank line, then a divider line of dashes, then bullet-style sentences. Render correctness verified statically against the pipeline above; no in-game UI test required.

Optional polish to match vanilla section-header convention: replace the dashes divider with `{HEADER:POWER GRID PLUS}`. The token expands to `<size=120%><b>POWER GRID PLUS</b></size>` and renders as a bold heading matching vanilla's `POWER OUTPUT` blocks.

## Verification history

- 2026-05-28: page created during PowerGridPlus rate-cap rollout verification. The ScenarioRunner `pgp-rate-cap-probe` scenario confirmed the footer text reaches `Localization.GetThingDescription` correctly. This page documents the rest of the pipeline (ParseHelpText, page cache, TMP render path) so the question "will my footer render correctly?" can be answered by static analysis instead of requiring an in-game eyeball check. Sourced from decompile lines 194901 (ParseHelpText), 231023 / 231964 (Stationpedia.Initialize / PopulateThingPages), 232041 (Description assignment), 233798 (PageDescription field), 233983 / 234508 (CheckAndSetTextElement -> TMP.text). en.resx samples at lines 2756, 2816, 3270, 3534, 3804, 6576.

## Open questions

None at creation.
