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

Shipped user: `Mods/PowerGridPlus/PowerGridPlus/Patches/FaultButtonTooltipPatches.cs` (`FaultButtonTooltipPatches.BlockOpenTags`), reading both components at runtime through `InventoryManager.Instance` (Assembly-CSharp 286106) `-> TooltipRef` (285975) `-> TooltipTitle` / `TooltipExtended` (253976 / 253982), paired with an absolute `<size=E.fontSize>` span so glyph size and line advance match together.

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

## Verification history

- 2026-07-14 (second pass, same day): added "Auto-size mutates m_fontSize while rendered" (decompile lines 2856, 3363, 3482, 3530, 3620, 4002, 6128 re-read from the same Unity.TextMeshPro decompile) and "The game tooltip rows' serialized values" (InspectorPlus live read on a connected client, request pattern cited in the section). Also corrected the shipped-user file path (the class moved to Patches/FaultButtonTooltipPatches.cs in the fault-terminology rollout). Driving work: diagnosing a residual glyph-size mismatch between the PowerGridPlus button and casing fault hovers before hardcoding the tooltip metric constants.
- 2026-07-14: page created. All quoted bodies read verbatim from `.work/decomp/0.2.6403.27689/Unity.TextMeshPro.decompiled.cs`, a fresh ilspycmd decompile of the shipped `Unity.TextMeshPro.dll` produced this session. Driving work: matching the PowerGridPlus button-tooltip Title-box line spacing to the casing tooltip (in-game feedback that the block lines sat too far apart after the font-size match). Additive (new page); the TMP-default facts on [StationpediaDescriptionRendering](./StationpediaDescriptionRendering.md) are not contradicted.

## Open questions

None at creation.
