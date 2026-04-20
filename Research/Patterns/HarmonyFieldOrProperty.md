---
title: Harmony field-or-property reflection tolerance
type: Patterns
created_in: 0.2.6228.27061
verified_in: 0.2.6228.27061
verified_at: 2026-04-20
sources:
  - Mods/PowerTransmitterPlus/RESEARCH.md:621-623 (F0065)
related:
  - ./AccessToolsRecipes.md
tags: [harmony]
---

# Harmony field-or-property reflection tolerance

When reflecting against a game member whose declaration (field vs property) is unclear or might change between game versions, use `Traverse.Create(target).Field("Name").GetValue<T>()` with a property fallback. The decompile may read a field one version and a property the next; accepting either keeps the patch resilient.

## Problem
<!-- verified: 0.2.6228.27061 @ 2026-04-20 -->

F0065 (Mods/PowerTransmitterPlus/RESEARCH.md:621-623):

> `PowerTransmitter.UsePower` checks `cableNetwork == WirelessOutputNetwork`, but it's unclear whether `WirelessOutputNetwork` is a public field or property. Use `Traverse.Create(t).Field("WirelessOutputNetwork").GetValue<CableNetwork>()` with a property fallback.

A decompile showing the member on the right-hand side of an assignment can look like either a field or an auto-property; C# compiles them similarly but reflection must pick the right one. A direct `typeof(X).GetField("WirelessOutputNetwork")` returns null for a property, and vice versa.

## Solution / recipe
<!-- verified: 0.2.6228.27061 @ 2026-04-20 -->

Try the field path first; fall back to the property path if null.

```csharp
var field = typeof(PowerTransmitter).GetField("WirelessOutputNetwork",
    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
CableNetwork value;
if (field != null)
{
    value = (CableNetwork)field.GetValue(transmitter);
}
else
{
    var prop = typeof(PowerTransmitter).GetProperty("WirelessOutputNetwork",
        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
    value = (CableNetwork)prop?.GetValue(transmitter);
}
```

`Traverse.Create(obj).Field("Name").GetValue<T>()` (Harmony utility) hides the two-step check. F0065 recommends it as the concise form.

The pattern is specifically for members whose declaration-kind is uncertain. When the game version is pinned and the member is observably a field, a plain `AccessTools.Field` is simpler.

## Verification history
<!-- verified: 0.2.6228.27061 @ 2026-04-20 -->

- 2026-04-20: page created from the Research migration; single source (F0065).

## Open questions

None at creation.
