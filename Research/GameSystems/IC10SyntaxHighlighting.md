---
title: IC10SyntaxHighlighting
type: GameSystems
created_in: 0.2.6228.27061
verified_in: 0.2.6228.27061
verified_at: 2026-04-20
sources:
  - Mods/PowerTransmitterPlus/RESEARCH.md:427-464
  - Mods/PowerTransmitterPlus/PowerTransmitterPlus/Ic10ConstantsPatcher.cs:69-75
related:
  - ./LogicType.md
  - ../GameClasses/ProgrammableChip.md
tags: [ic10, logic, ui]
---

# IC10SyntaxHighlighting

The in-game screen (computer, laptop, wall-mounted screen) syntax-highlighting pipeline for IC10 source code. All rendered IC10 code flows through a single `Localization.ParseScript()` call; `ProgrammableChip.InternalEnums` snapshots `Enum.GetValues(typeof(LogicType))` at static-init time, so runtime-added LogicType values render red as "invalid" unless their private `_types` / `_names` arrays are extended via reflection.

## Pipeline
<!-- verified: 0.2.6228.27061 @ 2026-04-20 -->

All IC10 code rendered on in-game screens (wall-mounted computers, laptops, tablets) flows through a single pipeline. There are no separate code paths per screen type.

```
ProgrammableChipMotherboard.SetSourceCode(string)
  -> Localization.ParseDefines()          // collect alias/define names and jump labels
  -> Localization.ParseScript()           // produce colored rich text
       -> ParseScriptLine() per line
            -> ReplaceCommands()          // main token coloring
                 for each entry in ProgrammableChip.InternalEnums:
                   entry.Parse(ref masterString)
            -> ReplaceNumbers()
            -> ReplaceDeviceReferences()
```

## ProgrammableChip.InternalEnums
<!-- verified: 0.2.6228.27061 @ 2026-04-20 -->

`ProgrammableChip.InternalEnums` is a `public static List<IScriptEnum>` populated in the static constructor. The LogicType-relevant entries:

| Index | Type | Color | What it highlights |
|---|---|---|---|
| 0 | `ScriptEnum<LogicType>` | orange | Bare names: `MicrowaveSourceDraw`, `Temperature`, etc. |
| 4 | `BasicEnum<LogicType>` | `#20B2AA` (teal) | Dotted names: `LogicType.MicrowaveSourceDraw`, `LogicType.Temperature` |

Both classes snapshot `Enum.GetValues(typeof(LogicType))` / `Enum.GetNames(typeof(LogicType))` into private readonly `_types` (`T[]`) and `_names` (`string[]`) arrays at construction time. `Parse()` iterates these arrays and wraps each matching whole word with `<color=X>`:

```csharp
// ScriptEnum<T>.Parse (simplified)
for (int i = 0; i < _types.Length; i++)
    masterString = masterString.ReplaceWholeWord(
        _names[i],
        string.Format("<color={1}>{0}</color>", _names[i], _color));
```

Tokens that match no entry in any `IScriptEnum` receive no `<color>` tag and inherit the screen prefab's default `Text` component color, which is red. This is why unrecognized LogicType names appear red on the screen preview even though they compile and execute correctly.

`BasicEnum<LogicType>` prefixes each name with its type string plus a dot during construction: `_names[i] = "LogicType." + _names[i]`. When extending its arrays, the same prefix must be applied.

Since the arrays are captured at static init (before any mod runs), extending them requires reflection writes to the readonly fields on the existing instances in `InternalEnums`. This is done by `Ic10ConstantsPatcher.ExtendSyntaxHighlighting()`.

## ExtendSyntaxHighlighting patcher note
<!-- verified: 0.2.6228.27061 @ 2026-04-20 -->

From `Ic10ConstantsPatcher.cs`:

```
// ProgrammableChip.InternalEnums holds IScriptEnum instances that
// Localization.ParseScript iterates to wrap known tokens in <color>
// tags. ScriptEnum<LogicType> (bare names like "MicrowaveSourceDraw")
// and BasicEnum<LogicType> (dotted names like "LogicType.Microwave...")
// both snapshot Enum.GetValues/GetNames at construction, so our
// runtime-added values are missing. Extend their private _types and
// _names arrays via reflection.
```

## Verification history
<!-- verified: 0.2.6228.27061 @ 2026-04-20 -->

- 2026-04-20: page created from the Research migration; verbatim content lifted from F0041 (primary) and F0303.

## Open questions

None at creation.
