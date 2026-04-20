---
title: Custom logic-value injection on a vanilla device
type: Patterns
created_in: 0.2.6228.27061
verified_in: 0.2.6228.27061
verified_at: 2026-04-20
sources:
  - Plans/RepairPrototype/plan.md:727-755 (F0229i)
related:
  - ../GameSystems/LogicType.md
  - ./HarmonyInheritedMethods.md
  - ./AccessToolsRecipes.md
tags: [logic, harmony]
---

# Custom logic-value injection on a vanilla device

Pattern for adding a new readable logic value to a vanilla device (e.g. exposing a private field as an IC10-readable LogicType). Uses a Prefix on `CanLogicRead` (sets `__result = true`, returns false to skip original) and a Prefix on `GetLogicValue` that reads the private field via the Harmony parameter-naming convention. Originating code is from the Re-Volt mod's `TransformerLogicPatch.cs`.

## Problem
<!-- verified: 0.2.6228.27061 @ 2026-04-20 -->

F0229i (Plans/RepairPrototype/plan.md:727-755):

> Code pattern from Re-Volt `TransformerLogicPatch.cs` showing `[HarmonyPatch(typeof(Transformer))]` class with `[HarmonyPrefix]` on `CanLogicRead` (sets `__result = true` and returns false to skip original) and `GetLogicValue` (accesses private field `____powerProvided` via convention). Demonstrates adding custom logic values to an existing device via Harmony prefixes.

## Solution / recipe
<!-- verified: 0.2.6228.27061 @ 2026-04-20 -->

Skeleton:

```csharp
[HarmonyPatch(typeof(Transformer))]
internal static class TransformerLogicPatch
{
    [HarmonyPrefix]
    [HarmonyPatch(nameof(Transformer.CanLogicRead))]
    public static bool CanLogicReadPrefix(LogicType logicType, ref bool __result)
    {
        if (logicType == MyCustomLogicType)
        {
            __result = true;
            return false;  // skip original
        }
        return true;  // fall through to vanilla
    }

    [HarmonyPrefix]
    [HarmonyPatch(nameof(Transformer.GetLogicValue))]
    public static bool GetLogicValuePrefix(LogicType logicType, ref double __result, float ____powerProvided)
    {
        if (logicType == MyCustomLogicType)
        {
            __result = ____powerProvided;
            return false;
        }
        return true;
    }
}
```

Key elements:

- `ref` parameters `__result` let the prefix set the return value.
- The prefix returns `false` to block vanilla (see `./HarmonyPrefixReturnBool.md`).
- Private-field access via `____<fieldName>` (four underscores + field name) is Harmony's parameter-naming convention; the parameter is bound by name to the field. See `./AccessToolsRecipes.md`.

### Inheritance considerations

If the target device's `CanLogicRead` / `GetLogicValue` are inherited from a base class (e.g. `WirelessPower`), target the declaring class, not the subclass. See `./HarmonyInheritedMethods.md`.

### Registering the LogicType itself

Adding a `LogicType` value the game recognizes is a separate piece of work involving the three-registries mechanism. See `../GameSystems/LogicType.md`.

## Verification history
<!-- verified: 0.2.6228.27061 @ 2026-04-20 -->

- 2026-04-20: page created from the Research migration; single source (F0229i) generalized from Re-Volt's code.

## Open questions

None at creation.
