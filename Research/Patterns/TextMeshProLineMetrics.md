---
title: TextMeshProLineMetrics
type: Patterns
created_in: 0.2.6403.27689
verified_in: 0.2.6403.27689
verified_at: 2026-07-14
sources:
  - $(StationeersPath)\rocketstation_Data\Managed\Unity.TextMeshPro.dll :: TMPro.TMP_Text, TMPro.TextMeshProUGUI
  - .work/decomp/0.2.6403.27689/Unity.TextMeshPro.decompiled.cs :: lines 4791 (TextMeshProUGUI class), 5278-5281 (Awake, m_isOrthographic = true), 6181-6183 (baseScale + em-scale locals), 6224-6225 (per-pass m_lineHeight reset + lineGap), 7169-7178 (line-advance branches), 21325 (m_lineHeight field default), 22262-22276 (lineSpacing property), 22298 (paragraphSpacing property), 27673-27699 (line-height tag parse + close-tag reset)
related:
  - ../GameClasses/ElectricalInputOutput.md
  - ./StationpediaDescriptionRendering.md
tags: [ui, unity]
---

# TextMeshProLineMetrics

How the game's TextMeshPro build computes the vertical line advance (the distance one line break moves the next line down), and how a rich-text `<line-height>` tag drives it. Verified verbatim from the shipped `Unity.TextMeshPro.dll` (fresh decompile, 0.2.6403.27689 install). The motivating use: reproducing one TMP component's line spacing inside a different component of the same UI panel, as PowerGridPlus does to render casing-tooltip-sized fault lines inside the tooltip's name box (see [ElectricalInputOutput](../GameClasses/ElectricalInputOutput.md), Title-row note).

All line numbers below refer to `.work/decomp/0.2.6403.27689/Unity.TextMeshPro.decompiled.cs`.

## The per-break line advance
<!-- verified: 0.2.6403.27689 @ 2026-07-14 -->

`TextMeshProUGUI : TMP_Text, ILayoutElement` (class 4791) is the component behind every screen-space text row (the 3D `TextMeshPro` variant is class 1666 with mirrored code). The UGUI variant is orthographic: `Awake` sets `m_isOrthographic = true;` (5278-5281), which turns every `(m_isOrthographic ? 1f : 0.1f)` factor below into 1.

`GenerateTextMesh` computes two component-level scale factors once per pass, from the COMPONENT's font size, verbatim (6181-6183):

```csharp
float num = m_fontSize / (float)m_fontAsset.m_FaceInfo.pointSize * m_fontAsset.m_FaceInfo.scale * (m_isOrthographic ? 1f : 0.1f);  // baseScale, line 6181
float num3 = m_fontSize * 0.01f * (m_isOrthographic ? 1f : 0.1f);                                                                  // em scale, line 6183
```

Because both use `m_fontSize` (the component/property value) rather than the rich-text current size, `<size>` tags inside the string do NOT change the spacing contributions below. `lineGap` is derived from the font asset's face metrics (6225): `faceInfo.lineHeight - (ascentLine - descentLine)`.

Each line break then advances the line offset through one of two branches, verbatim (7169-7178; `num` = baseScale, `num3` = em scale, `num9` = lineGap, `num5` = the break character):

```csharp
if (m_lineHeight == -32767f)   // unset (field default, line 21325)
{
    float num63 = 0f - m_maxLineDescender + adjustedAscender2 + (num9 + m_lineSpacingDelta) * num + (m_lineSpacing + ((num5 == 10 || num5 == 8233) ? m_paragraphSpacing : 0f)) * num3;
    m_lineOffset += num63;
    m_IsDrivenLineSpacing = false;
}
else
{
    m_lineOffset += m_lineHeight + (m_lineSpacing + ((num5 == 10 || num5 == 8233) ? m_paragraphSpacing : 0f)) * num3;
    m_IsDrivenLineSpacing = true;
}
```

Consequences:

- **Natural advance** (no `<line-height>` in effect), for a uniform single-font, single-size line: ascent minus descent plus lineGap equals `faceInfo.lineHeight` in font units, so the advance is `faceInfo.lineHeight * baseScale + (lineSpacing + paragraphSpacing-if-newline) * emScale`, i.e. `faceInfo.lineHeight * (fontSize / faceInfo.pointSize * faceInfo.scale) + (lineSpacing + paragraphSpacing) * fontSize * 0.01`. (`m_lineSpacingDelta` is the auto-size adjustment, 0 with auto-sizing off.)
- **Driven advance** (a `<line-height>` value in effect): exactly `m_lineHeight + (lineSpacing + paragraphSpacing-if-newline) * emScale`. Glyph metrics drop out entirely; the component's own `lineSpacing` / `paragraphSpacing` still add on top.
- The paragraph term applies when the break character is `\n` (10) or U+2029 (8233), so literal `"\n"` breaks in a mod string include it on both branches.
- `lineSpacing` (property 22262-22276) and `paragraphSpacing` (22298) store raw values in font-size-relative units: the effective offset is `value * fontSize * 0.01`.

## The line-height rich-text tag
<!-- verified: 0.2.6403.27689 @ 2026-07-14 -->

The tag parser cases, verbatim (27673-27699; `num3` = the parsed number here, `num4` = a current-size scale):

```csharp
case -842693512:                          // <line-height=...>
case 1109349752:
    ...
    switch (tagUnitType)
    {
    case TagUnitType.Pixels:
        m_lineHeight = num3 * (m_isOrthographic ? 1f : 0.1f);
        break;
    case TagUnitType.FontUnits:
        m_lineHeight = num3 * (m_isOrthographic ? 1f : 0.1f) * m_currentFontSize;
        break;
    case TagUnitType.Percentage:
    {
        float num4 = m_currentFontSize / (float)m_currentFontAsset.faceInfo.pointSize * m_currentFontAsset.faceInfo.scale * (m_isOrthographic ? 1f : 0.1f);
        m_lineHeight = m_fontAsset.faceInfo.lineHeight * num3 / 100f * num4;
        break;
    }
    }
    return true;
case -445573839:                          // </line-height>
case 1897350193:
    m_lineHeight = -32767f;
    return true;
```

- **Plain number** (`<line-height=25>`, TagUnitType.Pixels): stored unscaled on an orthographic (UGUI) component. The value is in the component's local text units, the same units `fontSize` uses, so two sibling components on one canvas compare directly.
- **Percentage** (`<line-height=75%>`): computed from the font's natural `faceInfo.lineHeight` scaled by the CURRENT rich-text font size at the tag's position, so it couples to any `<size>` tag already open there.
- **Close tag** resets to unset (-32767), returning to the natural branch.
- **Scoping**: the advance into a line is computed at the break that starts it, from the `m_lineHeight` in effect at that character. A tag placed before a `"\n"` therefore governs the advance into the following line and stays in effect until closed. No cross-frame leakage is possible: `m_lineHeight` resets to unset at the start of every `GenerateTextMesh` pass (6224).

## Matching one component's advance inside another
<!-- verified: 0.2.6403.27689 @ 2026-07-14 -->

To make driven breaks inside component T (here: the tooltip name box, `TooltipTitle`) advance exactly like natural breaks in component E (the casing tooltip's `TooltipExtended`), both on the same canvas:

```
H = E.font.faceInfo.lineHeight * (E.fontSize / E.font.faceInfo.pointSize * E.font.faceInfo.scale)
  + (E.lineSpacing + E.paragraphSpacing) * E.fontSize * 0.01
  - (T.lineSpacing + T.paragraphSpacing) * T.fontSize * 0.01
```

emitted as a plain-number `<line-height=H>` (invariant culture; TMP's `ConvertToFloat` expects a dot decimal). The first two terms are E's natural advance; the subtraction pre-cancels T's own spacing term, which the driven branch adds back at render time. T's em scale uses T's component `fontSize` regardless of any `<size>` span in the string (6183), which is why the subtraction uses `T.fontSize`, not the size-tag value.

Shipped user: `Mods/PowerGridPlus/PowerGridPlus/Patches/FaultButtonTooltipPatches.cs`, which carries the formula's result as the hardcoded `<line-height=25.2>` constant (computed from the serialized row values two sections down) paired with an absolute `<size=18>` span so glyph size and line advance match together. An earlier iteration evaluated the formula at runtime by reading both components through `InventoryManager.Instance` (Assembly-CSharp 286106) `-> TooltipRef` (285975) `-> TooltipTitle` / `TooltipExtended` (253976 / 253982); the runtime reads are retired, but those member chains remain the way to reach the two components for a re-extraction.

## Auto-size mutates m_fontSize while rendered
<!-- verified: 0.2.6403.27689 @ 2026-07-14 -->

When `m_enableAutoSizing` is true, the text-fitting loop mutates `m_fontSize` itself between `m_fontSizeMin` and `m_fontSizeMax` across `GenerateTextMesh` iterations: the overflow branches shrink it (3363, 3482, 3530, retry gate 3620) and the underfill branch grows it back toward the max (`m_enableAutoSizing && num4 > 0.051f && m_fontSize < m_fontSizeMax && m_AutoSizeIterationCount < m_AutoSizeMaxIterationCount`, 4002; entry gates 2856 / 6128). Consequence: the public `fontSize` property of a rendered auto-sized component reads the FITTED size for the currently displayed content, not a stable configured value, and the fitted value changes with content length. A mod reading `fontSize` to mirror another component's glyph size must either confirm `enableAutoSizing` is false on the source component or read it while the reference content is rendered.

## The game tooltip rows' serialized values
<!-- verified: 0.2.6403.27689 @ 2026-07-14 -->

The cursor tooltip's Title and Extended rows (the `Tooltip` fields `TooltipTitle` / `TooltipExtended`, GameObjects `ItemTitle` / `InfoExtended`) ship with these serialized TextMeshProUGUI values. Verified via InspectorPlus on 2026-07-14 in game version 0.2.6403.27689, live while a body tooltip rendered content in both rows. Request: types=[TextMeshProUGUI], fields=[text, fontSize, enableAutoSizing, fontSizeMin, fontSizeMax, lineSpacing, paragraphSpacing], maxDepth=1.

| Row | GameObject | fontSize | enableAutoSizing | fontSizeMin | fontSizeMax | lineSpacing | paragraphSpacing |
|---|---|---|---|---|---|---|---|
| `TooltipTitle` | `ItemTitle` | 24 | false | 18 | 72 | 30 | 0 |
| `TooltipExtended` | `InfoExtended` | 18 | false | 18 | 72 | 30 | 50 |

Auto-sizing is OFF on both rows, so `fontSize` is the literal rendered point size and the advance formulas above apply with these constants directly. Note `paragraphSpacing` differs per row (0 vs 50): explicit `\n` breaks in Extended content advance by the paragraph term on top of the line term, so a cross-component advance match must use the per-row values, not assume symmetry.

A second probe pass (same date, request: types=[TextMeshProUGUI], fields=[font, fontStyle, fontWeight, characterSpacing, wordSpacing, outlineWidth, fontSharedMaterial]; plus types=[RectTransform], fields=[lossyScale, localScale, sizeDelta, anchoredPosition]) confirmed the rows are otherwise render-identical: both use the `font_english` TMP_FontAsset with the `RBBook SDF Material`, fontStyle Normal, fontWeight Regular, characterSpacing 10, wordSpacing 0, outlineWidth 0, localScale 1 and equal lossyScale (0.964 at the probed resolution). Equal point size therefore renders equal on-screen glyphs across the two rows.

Derived constants for the cross-row match (both extracted live the same day; PowerGridPlus hardcodes them in `Patches/FaultButtonTooltipPatches.cs`):

- The Extended row's natural advance at its own 18-point size is 32.4 Title-text units: face term 18.0 (so `font_english` has `faceInfo.lineHeight / faceInfo.pointSize * faceInfo.scale = 1.0` at this size) plus the spacing term (30 + 50) * 18 * 0.01 = 14.4. Driven inside the Title row (spacing term (30 + 0) * 24 * 0.01 = 7.2) that is `<line-height=25.2>`.
- The Title-to-Extended first-baseline distance (serialized prefab layout, not text) measured 28.6 Title-local units, so the Title-row driven form is `<line-height=21.4>`.

Re-extraction recipe when a game update re-tunes the tooltip prefab: re-run the two InspectorPlus probes above while a body tooltip renders content in both rows, recompute the two line-height values with the formulas on this page, and re-measure the first-baseline distance either from `textInfo.lineInfo[0].baseline` of each row transformed to a common space or from a temporary runtime capture like the 2026-07-14 one-shot.

## Character spacing also scales by the COMPONENT font size, and the cspace cancel
<!-- verified: 0.2.6403.27689 @ 2026-07-14 -->

The per-character x-advance (GenerateTextMesh, 3812) is:

```
m_xAdvance += ((metrics.horizontalAdvance * num58 + tMP_GlyphValueRecord.m_XAdvance) * num2
            + (m_currentFontAsset.normalSpacingOffset + num26 + num8) * num3 + m_cSpacing)
            * (1f - m_charWidthAdjDelta);
```

where `num2` is the glyph scale built from `m_currentFontSize` (the `<size>`-tag-affected size, via `num24` at 3157), `num26 = m_characterSpacing` (3188), `num8` is the bold-style spacing (0 for Normal style, declared 2926), and `num3 = m_fontSize * 0.01f * (m_isOrthographic ? 1f : 0.1f)` (2912): the COMPONENT font size, computed once per render pass before the character loop, exactly like the line-height em scale (6183). Consequence: a `<size>` span rendered inside a component with a larger `fontSize` draws identical glyphs but tracks WIDER, by `(normalSpacingOffset + characterSpacing) * (componentSize - spanSize) * 0.01` per character. On the game tooltip rows (both `characterSpacing` 10) an 18-point span inside the 24-point Title row tracks 0.6 units per character wider than the same text in the 18-point Extended row, which reads as a larger font at a glance (the PowerGridPlus button-vs-casing fault hover, screenshots 2026-07-14).

The `<cspace>` tag is the cancel: `m_cSpacing` is added RAW per character (unscaled by `num3` or `num2`; only the `(1 - m_charWidthAdjDelta)` factor applies). Tag parse (27265-27281): a plain number stores the value directly in the orthographic case (`m_cSpacing = num3 * 1f`, 27273; the `num3` there is the parsed literal); a `FontUnits` suffix multiplies by `m_currentFontSize` (27276); percentages are rejected. The closing `</cspace>` (27283-27294) subtracts the trailing character's `m_cSpacing` back off `m_xAdvance` and zeroes it, and `m_cSpacing` also resets at every render pass start (2955), so nothing leaks. So `<cspace=-0.6>text</cspace>` around the 18-point span inside the 24-point component restores the Extended row's tracking exactly, modulo `normalSpacingOffset * 0.06` per character if the font asset's `normalSpacingOffset` is nonzero. For `font_english` it is effectively 0 (the TextMeshPro default), resolved empirically 2026-07-14: the bare `-0.6` cancel rendered the two tooltip blocks pixel-identical in-game. Direct probing is not available: InspectorPlus requests for `types=[TMP_FontAsset]` return zero objects on BOTH the headless dedicated server and a graphical client (2026-07-14), because asset types take the `FindObjectsOfType` resolution path, which never sees ScriptableObject assets, and the reflection fallback only triggers on a null result, not an empty one (tracked in the InspectorPlus `TODO.md`).

## Verification history

- 2026-07-14 (fifth pass, same day): resolved the normalSpacingOffset open question empirically (the `-0.6` cancel rendered the button and casing blocks pixel-identical in-game, ruling out a meaningful nonzero offset) and recorded why direct probing fails on any process (InspectorPlus trusts an empty `FindObjectsOfType` result for Unity-derived types, and assets are invisible to it). Also updated the shipped-user note: the composition tags are hardcoded constants now, no runtime component reads remain.
- 2026-07-14 (fourth pass, same day): added "Character spacing also scales by the COMPONENT font size, and the cspace cancel" (advance formula 3812, num3 definition 2912, characterSpacing 3188, bold-spacing slot 2926, cspace tag parse 27265-27294, per-pass reset 2955; all re-read from the same Unity.TextMeshPro decompile). Driving work: the PowerGridPlus button fault block tracked visibly wider than the casing block despite identical glyph size; the cspace cancel now ships hardcoded. Open question added for font_english's normalSpacingOffset.
- 2026-07-14 (third pass, same day): extended "The game tooltip rows' serialized values" with the style / material / scale identity probe (font_english, RBBook SDF Material, Normal/Regular, characterSpacing 10, equal lossyScale), the derived cross-row constants (25.2 block advance, 21.4 first gap, face ratio 1.0 for font_english), and the re-extraction recipe. Driving work: hardcoding the PowerGridPlus button-tooltip composition tags from a live extraction and retiring the runtime reads.
- 2026-07-14 (second pass, same day): added "Auto-size mutates m_fontSize while rendered" (decompile lines 2856, 3363, 3482, 3530, 3620, 4002, 6128 re-read from the same Unity.TextMeshPro decompile) and "The game tooltip rows' serialized values" (InspectorPlus live read on a connected client, request pattern cited in the section). Also corrected the shipped-user file path (the class moved to Patches/FaultButtonTooltipPatches.cs in the fault-terminology rollout). Driving work: diagnosing a residual glyph-size mismatch between the PowerGridPlus button and casing fault hovers before hardcoding the tooltip metric constants.
- 2026-07-14: page created. All quoted bodies read verbatim from `.work/decomp/0.2.6403.27689/Unity.TextMeshPro.decompiled.cs`, a fresh ilspycmd decompile of the shipped `Unity.TextMeshPro.dll` produced this session. Driving work: matching the PowerGridPlus button-tooltip Title-box line spacing to the casing tooltip (in-game feedback that the block lines sat too far apart after the font-size match). Additive (new page); the TMP-default facts on [StationpediaDescriptionRendering](./StationpediaDescriptionRendering.md) are not contradicted.

## Open questions

None currently. (`font_english`'s `normalSpacingOffset` was assumed 0, the TextMeshPro default; resolved empirically 2026-07-14: the `<cspace=-0.6>` cancel rendered the two tooltip blocks pixel-identical in-game, which a nonzero offset would have prevented. Direct probing is not possible: InspectorPlus asset-typed requests return empty on BOTH the headless server and a graphical client, because `FindObjectsOfType` cannot see ScriptableObject assets and the reflection fallback only triggers when it returns null, not empty.)
