---
title: Harmony inherited-method patching
type: Patterns
created_in: 0.2.6228.27061
verified_in: 0.2.6228.27061
verified_at: 2026-04-20
sources:
  - Plans/EquipmentPlus/RESEARCH.md:239-240 (F0119, primary)
  - Mods/PowerTransmitterPlus/RESEARCH.md:645-646 (F0051)
  - Mods/PowerTransmitterPlus/PowerTransmitterPlus/LogicReadoutPatches.cs:64-67 (F0307)
  - Plans/EquipmentPlus/EquipmentPlus/SensorLensesSyncPatches.cs:17-24 (F0338)
  - Plans/EquipmentPlus/EquipmentPlus/AdvancedTabletPatches.cs:17-18 (F0370)
  - Plans/EquipmentPlus/EquipmentPlus/SensorLensesPatches.cs:42-49 (F0371)
  - Plans/EquipmentPlus/EquipmentPlus/AdvancedTabletPatches.cs:56-60 (F0372, counter-example)
related:
  - ../GameClasses/WirelessPower.md
  - ../GameClasses/PowerTransmitter.md
  - ../GameClasses/PowerReceiver.md
  - ../GameClasses/SensorLenses.md
  - ../GameClasses/AdvancedTablet.md
  - ../GameClasses/Thing.md
tags: [harmony]
---

# Harmony inherited-method patching

The most-cited Harmony pitfall in the repo. `[HarmonyPatch(typeof(Subclass), "InheritedMethod")]` fails at `PatchAll` time when the named method is inherited without override. Six independent findings confirm the rule across three mods; a counter-example clarifies when the pitfall does not apply.

## Problem
<!-- verified: 0.2.6228.27061 @ 2026-04-20 -->

`[HarmonyPatch(typeof(Subclass), "InheritedMethodName")]` throws `Undefined target method for patch method ...` at `PatchAll` time because HarmonyX's attribute path calls `AccessTools.DeclaredMethod`, which only finds methods declared directly on the target type. If the method is inherited without override, the lookup fails.

Documented instances in this repo:

- `WirelessPower.CanLogicRead` / `GetLogicValue` / `SetLogicValue` / `CanLogicWrite`, all inherited by `PowerTransmitter` and `PowerReceiver` without override (F0051).
- `Thing.OnChildEnterInventory` / `OnChildExitInventory`, inherited by `AdvancedTablet` (F0370).
- `Thing.SerializeSave` / `DeserializeSave`, inherited by `SensorLenses` (F0371).

## Solution / recipe
<!-- verified: 0.2.6228.27061 @ 2026-04-20 -->

Two options; both produce virtual-dispatch patches that fire for every subclass instance of the declaring class.

### Option 1: target the declaring class

```csharp
[HarmonyPatch(typeof(WirelessPower), nameof(WirelessPower.CanLogicRead))]
```

Harmony's attribute path resolves the method on `WirelessPower` directly. The postfix/prefix fires for any subclass instance (`PowerTransmitter`, `PowerReceiver`, future subclasses). Best when you want the patch to run on every subclass.

F0307 (code comment from `LogicReadoutPatches.cs:64-67`):

```text
    // CanLogicRead and GetLogicValue are declared on WirelessPower, not on
    // PowerTransmitter/PowerReceiver (those inherit without override). Harmony
    // attribute patching uses DeclaredMethod and won't resolve inherited methods,
    // so we target the base class and branch on instance type.
```

### Option 2: `TargetMethod()` resolving inherited MethodInfo

Override `TargetMethod()` in the patch class and return `typeof(Subclass).GetMethod("MethodName", ...)`. `Type.GetMethod` walks inheritance and returns the inherited `MethodInfo`. Harmony patches that `MethodInfo`, which is the base-class method body, so the patch still fires for every subclass instance of the declaring class.

F0370 (AdvancedTablet OnChildEnter/ExitInventory):

> AdvancedTablet doesn't declare OnChildEnter/ExitInventory (inherited from Thing), so `[HarmonyPatch(typeof, nameof)]` fails with `AccessTools.DeclaredMethod`. Use `TargetMethod()` to resolve via `Type.GetMethod` which walks inheritance.

F0371 (SensorLenses Save/Load):

> SensorLenses inherits `SerializeSave`/`DeserializeSave` from Thing (doesn't override). Using `[HarmonyPatch(typeof(SensorLenses), nameof(...))]` fails because Harmony's `AccessTools.DeclaredMethod` only looks at methods declared on the exact type. The `TargetMethod()` pattern resolves the inherited `MethodInfo` via `Type.GetMethod`, which walks inheritance.

### `__instance` typing trap
<!-- verified: 0.2.6228.27061 @ 2026-04-20 -->

F0119 (primary source), Plans/EquipmentPlus/RESEARCH.md:239-240:

> When `TargetMethod()` returns an inherited `MethodInfo`, Harmony patches the base class method and the patch fires for every subclass instance. Declaring `__instance` as the concrete type (e.g. `SensorLenses`) causes Harmony to emit a `castclass` instruction that throws `InvalidCastException` for any other Thing subclass. Always use `Thing __instance` and filter with `is`.

F0338 (code comment from `SensorLensesSyncPatches.cs:17-24`, corroborating):

```text
    // NOTE ON __instance TYPING:
    // When TargetMethod returns an inherited MethodInfo, Harmony patches the base class's method
    // body and the Postfix fires for *every* call on any instance of that base.
    // Declaring __instance as SensorLenses causes Harmony to emit a castclass
    // to SensorLenses; when the actual instance is any other Thing subclass the
    // cast throws InvalidCastException, which surfaces during load as a crash.
    // Using Thing as the declared type avoids the cast; we filter with `is`.
```

Recipe:

```csharp
public static void Postfix(Thing __instance)
{
    if (!(__instance is SensorLenses lenses)) return;
    // ... subclass-specific work on `lenses`
}
```

### Deliberate `__instance` typing as a suppression filter

F0371 records the inverse strategy: when the patch is *only* relevant to one subclass, typing `__instance` as that subclass lets Harmony's `castclass` throw for other subclasses, which Harmony catches and treats as "skip this postfix for this call." From `SensorLensesPatches.cs:42-49`:

> `__instance` is typed as `SensorLenses` so Harmony skips the Postfix for non-SensorLenses Things.

This flips the trap into a feature: the failing `castclass` becomes a single-line filter instead of an `if (__instance is Sub)` branch. Use with care; the failure is silent, so a typo in the declared type still "works" but never runs.

## Counter-example: declared-on-subclass methods
<!-- verified: 0.2.6228.27061 @ 2026-04-20 -->

F0372 (Plans/EquipmentPlus/EquipmentPlus/AdvancedTabletPatches.cs:56-60):

> AdvancedTablet declares `SerializeSave` and `DeserializeSave` itself (not inherited), so plain `[HarmonyPatch(typeof, nameof)]` works.

The pitfall is specific to inherited methods. When the subclass overrides or newly declares the method, `AccessTools.DeclaredMethod` finds it and the attribute path succeeds. Check the decompiled class before assuming inheritance. `SensorLenses` and `AdvancedTablet` are both `Thing` subclasses with similar shapes but differ on this exact point: `SensorLenses` inherits `SerializeSave`/`DeserializeSave` (needs `TargetMethod()`), `AdvancedTablet` declares them (attribute patch works).

## Cited verifications
<!-- verified: 0.2.6228.27061 @ 2026-04-20 -->

Six independent findings and one counter-example, spanning three mods (PowerTransmitterPlus, EquipmentPlus, with code-comment echoes), all reach the same conclusion:

| Finding | Source | Methods / context |
|---|---|---|
| F0119 | Plans/EquipmentPlus/RESEARCH.md:239-240 | Primary: `__instance` typing trap statement |
| F0051 | Mods/PowerTransmitterPlus/RESEARCH.md:645-646 | First discovery context: `WirelessPower` inherited logic methods |
| F0307 | Mods/PowerTransmitterPlus/PowerTransmitterPlus/LogicReadoutPatches.cs:64-67 | Code comment: base-class targeting rationale |
| F0338 | Plans/EquipmentPlus/EquipmentPlus/SensorLensesSyncPatches.cs:17-24 | Code comment: castclass InvalidCastException on load |
| F0370 | Plans/EquipmentPlus/EquipmentPlus/AdvancedTabletPatches.cs:17-18 | `Thing.OnChildEnter/ExitInventory` via `TargetMethod()` |
| F0371 | Plans/EquipmentPlus/EquipmentPlus/SensorLensesPatches.cs:42-49 | Inherited `Save/Load` + deliberate typing suppression |
| F0372 | Plans/EquipmentPlus/EquipmentPlus/AdvancedTabletPatches.cs:56-60 | Counter-example: declared-on-subclass methods |

## Verification history
<!-- verified: 0.2.6228.27061 @ 2026-04-20 -->

- 2026-04-20: page created from the Research migration; verbatim content lifted from F0119 (primary), with additional verifications from F0051, F0307, F0338, F0370, F0371, and counter-example F0372.

## Open questions

None at creation.
