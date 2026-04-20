---
title: StationpediaMarkup
type: GameSystems
created_in: 0.2.6228.27061
verified_in: 0.2.6228.27061
verified_at: 2026-04-20
sources:
  - Plans/StationpediaPlus/PLAN.md:804-831
related:
  - ../GameClasses/StationpediaPage.md
  - ./StationpediaPageRendering.md
tags: [stationpedia, ui]
---

# StationpediaMarkup

Full replacement table for Stationpedia's `{TAG:value}` markup tokens. `ParsePage` expands these to TextMeshPro rich-text spans (`<link>`, `<color>`, `<size>`, `<indent>`). TMP tags pass through unchanged; bare identifiers do not auto-link.

## Markup tokens replacement table
<!-- verified: 0.2.6228.27061 @ 2026-04-20 -->

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

- **No auto-linking.** Bare identifiers render as plain text. Use `{LOGICTYPE:X}` or `{LINK:K;D}` explicitly.
- **No literal `[` or `]`.** `ParsePage` rewrites them to `<` / `>`.
- TMP rich text tags (`<b>`, `<i>`, `<color>`, `<size>`, `<link>`, `<indent>`, `<pos>`, `<sup>`, `<sub>`, `<sprite>`) pass through unchanged.

## Verification history
<!-- verified: 0.2.6228.27061 @ 2026-04-20 -->

- 2026-04-20: page created from the Research migration; verbatim content lifted from F0209 (Plans/StationpediaPlus/PLAN.md:804-831).

## Open questions

None at creation.
