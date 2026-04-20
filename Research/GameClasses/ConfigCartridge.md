---
title: ConfigCartridge
type: GameClasses
created_in: 0.2.6228.27061
verified_in: 0.2.6228.27061
verified_at: 2026-04-20
sources:
  - Plans/EquipmentPlus/RESEARCH.md:187-195
  - $(StationeersPath)\rocketstation_Data\Managed\Assembly-CSharp.dll :: ConfigCartridge
related:
  - ./Cartridge.md
  - ./AdvancedTablet.md
tags: [equipment, ic10]
---

# ConfigCartridge

Vanilla game class for the Configuration Cartridge tablet cartridge. Builds a per-tick readout of the currently-scanned device's logic values.

## Screen plumbing
<!-- verified: 0.2.6228.27061 @ 2026-04-20 -->

Source: F0117.

- `ConfigCartridge.OnScreenUpdate` unconditionally writes `_displayTextMesh.text = _outputText` every call, then sets `_scrollPanel.SetContentHeight(_displayTextMesh.preferredHeight)`. Any mod-side edit of the mesh text is wiped every frame and must be re-applied in a postfix.
- `ConfigCartridge._outputText` is built in `ReadLogicText` (called from `OnMainTick`, slower cadence than `OnScreenUpdate`). Format: line 0 is `ReferenceId ... $HEXID`, then one line per readable `LogicType` emitted as `{Name} ... <color=grey|green>{value}</color>`. Writable uses grey, read-only uses green, `ReferenceId` uses `#20B2AA`. Separator is a literal 5-character `" ... "` (space, three dots, space).
- `ConfigCartridge.ScannedDevice` is computed live from `CursorManager.CursorThing as Device` with an `IsMasterAuthority` gate. The cursor must still be pointing at the device at the instant of the click, or `ScannedDevice` is null and the click path bails.

## Verification history
<!-- verified: 0.2.6228.27061 @ 2026-04-20 -->

- 2026-04-20: page created from the Research migration; verbatim content lifted from F0117 (ConfigCartridge-side facts). No conflicts.

## Open questions

None at creation.
