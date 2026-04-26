---
title: ConfigCartridge
type: GameClasses
created_in: 0.2.6228.27061
verified_in: 0.2.6228.27061
verified_at: 2026-04-26
sources:
  - Plans/EquipmentPlus/RESEARCH.md:187-195
  - $(StationeersPath)\rocketstation_Data\Managed\Assembly-CSharp.dll :: ConfigCartridge
related:
  - ./Cartridge.md
  - ./AdvancedTablet.md
  - ../GameSystems/LogicValueFormatting.md
tags: [equipment, ic10]
---

# ConfigCartridge

Vanilla game class for the Configuration Cartridge tablet cartridge. Builds a per-tick readout of the currently-scanned device's logic values.

## Screen plumbing
<!-- verified: 0.2.6228.27061 @ 2026-04-26 -->

Source: F0117.

- `ConfigCartridge.OnScreenUpdate` unconditionally writes `_displayTextMesh.text = _outputText` every call, then sets `_scrollPanel.SetContentHeight(_displayTextMesh.preferredHeight)`. Any mod-side edit of the mesh text is wiped every frame and must be re-applied in a postfix.
- `ConfigCartridge._outputText` is built in `ReadLogicText` (called from `OnMainTick`, slower cadence than `OnScreenUpdate`). Format: line 0 is `ReferenceId ... $HEXID`, then one line per readable `LogicType` emitted as `{Name} ... <color=grey|green>{value}</color>`. Writable uses grey, read-only uses green, `ReferenceId` uses `#20B2AA`. Separator is a literal 5-character `" ... "` (space, three dots, space). The `{value}` is the verbatim expression `Math.Round(_scannedDevice.GetLogicValue(logicType), 3, MidpointRounding.AwayFromZero)` appended to a `StringBuilder` (implicit `ToString()` under `CultureInfo.CurrentCulture`); see [`../GameSystems/LogicValueFormatting.md`](../GameSystems/LogicValueFormatting.md) for the cross-surface comparison.
- `ConfigCartridge.ScannedDevice` is computed live from `CursorManager.CursorThing as Device` with an `IsMasterAuthority` gate. The cursor must still be pointing at the device at the instant of the click, or `ScannedDevice` is null and the click path bails.

## Verification history
<!-- verified: 0.2.6228.27061 @ 2026-04-20 -->

- 2026-04-20: page created from the Research migration; verbatim content lifted from F0117 (ConfigCartridge-side facts). No conflicts.
- 2026-04-26: added the verbatim format expression for `{value}` in the per-line emit string (`Math.Round(_scannedDevice.GetLogicValue(logicType), 3, MidpointRounding.AwayFromZero)`, `CultureInfo.CurrentCulture`) and a cross-link to `../GameSystems/LogicValueFormatting.md`. Re-decompiled `ConfigCartridge.ReadLogicText` against `$(StationeersPath)\rocketstation_Data\Managed\Assembly-CSharp.dll` at v0.2.6228.27061 via `ilspycmd`. Additive only; no contradiction with prior content.

## Open questions

None at creation.
