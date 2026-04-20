---
title: Harmony patch types (Prefix / Postfix / Transpiler / Reverse Patch)
type: Patterns
created_in: 0.2.6228.27061
verified_in: 0.2.6228.27061
verified_at: 2026-04-20
sources:
  - Plans/RepairPrototype/plan.md:804-818 (F0229l)
related:
  - ./HarmonyPrefixReturnBool.md
  - ./HarmonyInheritedMethods.md
  - ./AccessToolsRecipes.md
tags: [harmony]
---

# Harmony patch types (Prefix / Postfix / Transpiler / Reverse Patch)

Quick reference for the four Harmony patch kinds, their intended use, and the implicit parameters Harmony provides. Split out from the RepairPrototype plugin template (see also `../Workflows/ModProjectSetup.md` for the BepInEx scaffold).

## Reference
<!-- verified: 0.2.6228.27061 @ 2026-04-20 -->

F0229l (Plans/RepairPrototype/plan.md:804-818):

> Harmony patch types: Prefix (runs before, return false to skip, gets `__instance`, can modify params), Postfix (runs after, can modify `__result`), Transpiler (modifies IL at load), Reverse Patch (calls private/internal methods from mod code).

### Prefix

Runs before the target method. Can:

- Access and modify parameters via matching parameter names (`ref T foo`).
- Set `ref __result` directly to override the return value.
- Return `false` to block the original from running (requires the prefix be declared as `bool`-returning). See `./HarmonyPrefixReturnBool.md`.
- Access `__instance` for instance methods.

Use when the mod needs to intercept a call, decide whether vanilla runs, and potentially replace the result.

### Postfix

Runs after the target method. Can:

- Read or modify `ref __result`.
- Access parameters (read-only semantics are typical, though `ref` parameters are still `ref`).
- Access `__instance`.

Use when the mod augments vanilla's output (add a bonus, clamp a value, register side-effects). Cannot block vanilla.

### Transpiler

Modifies the target method's IL at load time. Receives an `IEnumerable<CodeInstruction>` and returns a modified enumerable.

Use only when Prefix/Postfix can't express the change: inserting code mid-method, replacing a specific opcode, rewriting a loop body. Transpilers are brittle against game-version updates; prefer Prefix/Postfix when they suffice.

### Reverse Patch

Provides a mod-owned delegate that calls a private/internal game method without reflection at call-time. The Reverse Patch class's static method has the same signature as the target; Harmony redirects the call into the target at load time.

Use when the mod needs to call a private/internal vanilla method hot-path frequently; Reverse Patch is faster than `MethodInfo.Invoke` reflection. See `./AccessToolsRecipes.md` for the reflection alternative.

## Implicit parameter names
<!-- verified: 0.2.6228.27061 @ 2026-04-20 -->

- `__instance`: the target object for instance methods.
- `__result`: the method's return value (Prefix / Postfix may modify).
- `__state`: Prefix-to-Postfix state handoff (Prefix's out becomes Postfix's in).
- `____<fieldName>` (four underscores + field name): private field of the target class. Harmony binds the parameter to the field automatically.

## Verification history
<!-- verified: 0.2.6228.27061 @ 2026-04-20 -->

- 2026-04-20: page created from the Research migration; single source (F0229l).

## Open questions

None at creation.
