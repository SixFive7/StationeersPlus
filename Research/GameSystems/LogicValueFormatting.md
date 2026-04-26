---
title: LogicValueFormatting
type: GameSystems
created_in: 0.2.6228.27061
verified_in: 0.2.6228.27061
verified_at: 2026-04-26
sources:
  - $(StationeersPath)\rocketstation_Data\Managed\Assembly-CSharp.dll :: Assets.Scripts.Objects.Motherboards.ConfigCartridge.ReadLogicText
  - $(StationeersPath)\rocketstation_Data\Managed\Assembly-CSharp.dll :: Assets.Scripts.UI.LogicValueDisplay.Refresh
  - $(StationeersPath)\rocketstation_Data\Managed\Assembly-CSharp.dll :: ExtensionMethods.ToStringExact
related:
  - ../GameClasses/ConfigCartridge.md
  - ./LogicType.md
tags: [logic, ui, equipment]
---

# LogicValueFormatting

How the vanilla game converts a `double` returned from `GetLogicValue` / `GetLogicSlotValue` into the display string shown on screen. Covers every surface that prints a live logic value.

## Summary

The Stationeers game uses two distinct formatting patterns for displaying logic values (double returned from GetLogicValue or GetLogicSlotValue) across UI surfaces. Most editable/input surfaces use ToStringExact() preserving full precision, while display-only surfaces like the cartridge scanner use Math.Round(value, 3).

The game is not uniform. Different surfaces use different precisions and format strategies.

<!-- verified: 0.2.6228.27061 @ 2026-04-26 -->

## ConfigCartridge Scanner Display

Location: Assets.Scripts.Objects.Motherboards.ConfigCartridge.ReadLogicText() (line 323217)

Format spec:
```csharp
Math.Round(_scannedDevice.GetLogicValue(logicType), 3, MidpointRounding.AwayFromZero)
```
Then appended directly to StringBuilder (implicit ToString() with current culture).

Precision: 3 decimal places, away-from-zero rounding.

Culture: Current culture (not explicitly specified).

Context: Displays the current logic value for a scanned device on the tablet cartridge screen. This is read-only display.

Source: Assembly-CSharp.dll :: Assets.Scripts.Objects.Motherboards.ConfigCartridge :: ReadLogicText (line 323243)

<!-- verified: 0.2.6228.27061 @ 2026-04-26 -->

## Rocket Motherboard Logic Value Display Panel

Location: Assets.Scripts.UI.LogicValueDisplay.Refresh() (line 152753)

Format spec:
```csharp
double num = Math.Round(model.Value, 3, MidpointRounding.AwayFromZero);
_valueInputField.SetTextWithoutNotify(num.ToString());
```

Precision: 3 decimal places, away-from-zero rounding, then ToString() with default culture.

Culture: Current culture (default).

Context: Displays logic values in the rocket motherboard's editable UI panel. This field is editable and fires OnValueChanged when the user types a new value.

Source: Assembly-CSharp.dll :: Assets.Scripts.UI.LogicValueDisplay :: Refresh (lines 152757-152758)

<!-- verified: 0.2.6228.27061 @ 2026-04-26 -->

## Setting/Logic Chip Configuration Input Dialog

Location: Assets.Scripts.Objects.Motherboards.ConfigCartridge.Set() (line 329784)

Format spec:
```csharp
setable.GetLogicValue(logicType).ToStringExact()
```

Where ToStringExact() is defined in ExtensionMethods (lines 213424-213427):
```csharp
public static string ToStringExact(this double value)
{
    return value.ToString("0." + new string('#', 339), CultureInfo.CurrentCulture);
}
```

Precision: Format string 0. + 339 hash marks - retains all significant digits up to 339 decimal places.

Culture: Current culture (via CultureInfo.CurrentCulture).

Context: When user clicks Set Value on a LogicChip to enter a new logic value, this formatter populates the input dialog with the current value.

Source: Assembly-CSharp.dll :: Assets.Scripts.Objects.Motherboards.ConfigCartridge :: Set (line 329784)

<!-- verified: 0.2.6228.27061 @ 2026-04-26 -->

## Tooltip and Extended Information Display

Location: Multiple IC10-related instruction classes use ToStringExact() in tooltips:
- Assets.Scripts.Objects.Motherboards.SampleLogicReader.GetStateMessage (line 378457)
- Assets.Scripts.Objects.Motherboards.SampleSlotLogicReader.GetStateMessage (line 378934)
- Assets.Scripts.Objects.Motherboards.ComparisonLogic.GetStateMessage (line 380846)

Format spec:
```csharp
Setting.ToStringExact()
```

Precision: Full precision (format string with 339 hash marks).

Culture: Current culture.

Context: Tooltips and extended information panels shown when hovering over or inspecting logic-related devices. Read-only for information purposes.

Source: Assembly-CSharp.dll :: ExtensionMethods.ToStringExact(double) (line 213424)

<!-- verified: 0.2.6228.27061 @ 2026-04-26 -->

## Stationpedia Logic Type Information

Location: Assets.Scripts.Objects.Motherboards.UniversalPage.AddLogicTypeInfo() (line 231184)

Format spec: No direct value formatting. Stationpedia documents which LogicTypes are readable/writable but does not display example or current values from GetLogicValue(). Only displays LogicType enum names and access modes.

Context: Stationpedia pages are informational.

Source: Assembly-CSharp.dll :: Assets.Scripts.Objects.Motherboards.UniversalPage :: AddLogicTypeInfo (line 231208)

<!-- verified: 0.2.6228.27061 @ 2026-04-26 -->

## Consistency Summary

| Surface | Format Spec | Precision | Culture | Source |
|---------|-------------|-----------|---------|--------|
| ConfigCartridge scanner | Math.Round(v, 3).ToString() | 3 decimals | Current | ConfigCartridge.ReadLogicText:323243 |
| Rocket motherboard display | Math.Round(v, 3).ToString() | 3 decimals | Current | LogicValueDisplay.Refresh:152758 |
| Setting/chip input dialog | ToStringExact() | 339 decimals | Current | ConfigCartridge.Set:329784 |
| Tooltips (IC10 devices) | ToStringExact() | 339 decimals | Current | ExtensionMethods.ToStringExact:213424 |

<!-- verified: 0.2.6228.27061 @ 2026-04-26 -->

## Verdict: Inconsistent

The game uses two different formatting strategies:

1. Display-only surfaces (ConfigCartridge scanner, rocket motherboard display) use Math.Round(value, 3) limiting display to 3 decimal places.

2. Input dialogs and tooltips use ToStringExact() with format string preserving full precision (339 decimal places).

All use CultureInfo.CurrentCulture (not InvariantCulture), meaning decimal separators respect the user's system locale.

### Implications for EquipmentPlus

The EquipmentPlus ConfigCartridgeSlotDisplay currently uses Math.Round(rawValue, 2).ToString(). This diverges from both game patterns:

- Display-only variant: use Math.Round(rawValue, 3, MidpointRounding.AwayFromZero) to match ConfigCartridge
- Editable variant: use ToStringExact() to match input dialogs

Current 2-decimal rounding should be updated to align with one of these canonical patterns.

<!-- verified: 0.2.6228.27061 @ 2026-04-26 -->

## Verification history

- 2026-04-26: Initial research. Surveyed ConfigCartridge, LogicValueDisplay, ExtensionMethods.ToStringExact, and Stationpedia. Found two distinct patterns: 3-decimal rounding for display surfaces vs. full precision for input and tooltips.

## Open questions

None at creation.
