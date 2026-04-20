---
title: Extending Logicable's initialized LogicType arrays
type: Patterns
created_in: 0.2.6228.27061
verified_in: 0.2.6228.27061
verified_at: 2026-04-20
sources:
  - Mods/PowerTransmitterPlus/PowerTransmitterPlus/LogicableInitializePatch.cs:11-16 (F0304)
related:
  - ../GameSystems/LogicType.md
  - ./BestEffortIntegration.md
tags: [logic, harmony]
---

# Extending Logicable's initialized LogicType arrays

`Logicable` exposes the per-device LogicType list as a set of parallel arrays the configuration-tablet UI uses to build its dropdowns: `LogicType[]`, `string[]` (display names), and a redirect index for binary-search lookup. Appending a custom LogicType requires extending all three arrays consistently.

## Problem
<!-- verified: 0.2.6228.27061 @ 2026-04-20 -->

F0304 (Mods/PowerTransmitterPlus/PowerTransmitterPlus/LogicableInitializePatch.cs:11-16):

```text
    // Appends our custom LogicType values + names into the static arrays the
    // configuration tablet UI uses to populate its dropdowns. Logicable holds
    // parallel LogicType[] / string[] arrays plus a redirect index for binary
    // search lookup; we extend all three.
    //
    // Pattern lifted from Stationeers Logic Extended (ThunderDuck).
```

Leaving any one array unextended produces visible artifacts: missing entries in the dropdown, out-of-range lookups returning "Unknown," or NullReferenceExceptions from the UI.

## Solution / recipe
<!-- verified: 0.2.6228.27061 @ 2026-04-20 -->

Postfix `Logicable.Initialize` (or the specific device initialize), and use reflection to append to each of the three arrays in order:

1. The `LogicType[]` value array.
2. The parallel `string[]` display-name array.
3. The redirect index (`LogicTypeNamesRedirects` or equivalent binary-search lookup).

Appending to the value and name arrays is straightforward; the redirect index must preserve sort order for binary search to return correct results.

Full mechanism for introducing a new `LogicType` value into the game (including the three cross-class registries beyond Logicable) is on `../GameSystems/LogicType.md`. This page is narrower; it documents the specific `Logicable.Initialize`-extension step that the LogicType page cross-references.

### Why the pattern is lifted from Stationeers Logic Extended

Stationeers Logic Extended has no public extensibility API. Every mod that adds custom LogicTypes reinvents this extension mechanism. The F0304 comment explicitly cites that origin; see `./BestEffortIntegration.md` for the generalized "optional-dependency integration" pattern.

## Verification history
<!-- verified: 0.2.6228.27061 @ 2026-04-20 -->

- 2026-04-20: page created from the Research migration; single source (F0304).

## Open questions

None at creation.
