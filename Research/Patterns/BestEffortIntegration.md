---
title: Best-effort integration with optional dependencies
type: Patterns
created_in: 0.2.6228.27061
verified_in: 0.2.6228.27061
verified_at: 2026-04-20
sources:
  - Mods/PowerTransmitterPlus/RESEARCH.md:748-760 (F0059)
  - Plans/StationpediaPlus/PLAN.md:3629-3644 (F0219t)
  - Plans/StationpediaPlus/PLAN.md:3549-3556 (F0219ab)
  - Mods/PowerTransmitterPlus/PowerTransmitterPlus/StationpediaPatches.cs:7-11 (F0367)
related:
  - ../GameSystems/ThirdPartyModIdentities.md
  - ./ConflictDetection.md
tags: [harmony]
---

# Best-effort integration with optional dependencies

When a mod integrates with another mod whose APIs are unstable or whose presence is optional (Stationpedia, Stationeers Logic Extended, Stationpedia Ascended), use imperative reflection with `TypeByName` fallback + `Prepare()` gating + try/catch around each reflection call. Missing targets degrade gracefully to "feature disabled," never to a crash.

## Problem
<!-- verified: 0.2.6228.27061 @ 2026-04-20 -->

Optional dependencies have three failure modes:

1. **Mod not installed.** The referenced types do not exist at runtime.
2. **Mod installed but version shifted.** A method was renamed, parameters changed, or a namespace moved.
3. **Mod installed and current but feature absent in this configuration.** The game version disabled a feature; the call path exists but returns null.

A Harmony patch written with compile-time typed references fails in case 1 (at `PatchAll` time with "type not found"). Attribute-based patches fail in case 2 with "undefined target method." Both produce hard errors visible in the log but invisible in-game; users see the mod silently not work without a clue why.

F0367 (Mods/PowerTransmitterPlus/PowerTransmitterPlus/StationpediaPatches.cs:7-11):

> Best-effort Stationpedia integration. We don't hard-fail if the game version refactored these methods. Just log and skip; the readouts still work without documentation entries. Pattern lifted from Stationeers Logic Extended (ThunderDuck).

## Solution / recipe
<!-- verified: 0.2.6228.27061 @ 2026-04-20 -->

Three mechanisms, applied together.

### 1. `AccessTools.TypeByName` with fallback candidates

Instead of `typeof(Stationpedia)` (compile-time bound), resolve at runtime with candidate name list:

```csharp
var type = AccessTools.TypeByName("Assets.Scripts.UI.Stationpedia")
    ?? AccessTools.TypeByName("Stationpedia");
if (type == null)
{
    Logger.LogInfo("[Mod] Stationpedia not available; skipping docs integration.");
    return;  // feature disabled, no error
}
```

The fallback candidates accommodate namespace changes across game versions. Each candidate is checked in order; the first non-null wins.

### 2. `Prepare()` gating on Harmony patches

A patch class whose `Prepare()` returns false is entirely skipped by `PatchAll`. Check the target exists before declaring the patch applicable:

```csharp
internal static class StationpediaPostfix
{
    static bool Prepare() => AccessTools.Method(ResolveStationpediaType(), "Register") != null;

    static MethodBase TargetMethod() => AccessTools.Method(ResolveStationpediaType(), "Register");

    static void Postfix() { /* ... */ }
}
```

`Prepare()` prevents the "undefined target method" error at `PatchAll` time. Combined with `TargetMethod()`, the patch is fully runtime-resolved.

### 3. Try/catch each reflection call, log and skip

Within the patch body, wrap each reflection call in try/catch and log at `Info` or `Debug` level on failure, never `Error`:

```csharp
try
{
    var register = type.GetMethod("Register", new[] { pageType, typeof(bool) });
    register?.Invoke(null, new object[] { page, false });
}
catch (Exception ex)
{
    Plugin.Log.LogDebug($"[Mod] Register call failed: {ex.Message}");
}
```

The caller keeps running; the feature that failed is disabled but the rest of the mod continues.

### Extended expressions of the pattern

F0059 (Mods/PowerTransmitterPlus/RESEARCH.md:748-760) describes the Stationeers Logic Extended reference pattern (author: ThunderDuck) that establishes this style, including its use for custom `LogicType` registration:

> Stationeers Logic Extended has NO public extensibility API. Every mod that wants custom LogicTypes reimplements the registration pattern from scratch.

F0219t (Plans/StationpediaPlus/PLAN.md:3629-3644) documents that Stationpedia Ascended itself adopts the same imperative style for the same reasons:

> SPA's `StationpediaAscendedMod.ApplyHarmonyPatches` carries a source comment: "Manual patching - more reliable than attribute-based patching for game assemblies." All SPA patches use imperative `_harmony.Patch(original, prefix: ..., postfix: ...)` rather than `[HarmonyPatch]` attributes. Consistent with "reflecting against a moving target" concerns: imperative `AccessTools.Method(...)` with fallback name candidates is friendlier to game-version drift than attributed patches that throw on missing targets.

F0219ab (Plans/StationpediaPlus/PLAN.md:3549-3556) adds a related decision: prefer a custom handler that avoids a vanilla hook when the vanilla hook references scene state that may not be valid at call time.

> Vanilla `HelpLinkHandler` would give hover-color feedback for free, but its `LateUpdate` references `WorldManager.IsGamePaused`, tying UI to scene state. Risk: opening Stationpedia from main menu (before world init) could throw NullReferenceException. Custom `SixFive7LinkHandler` avoids LateUpdate entirely (click-only scope), trading hover-color for reduced failure surface. Compensated by mandatory click-phrasing rule.

## Worked examples
<!-- verified: 0.2.6228.27061 @ 2026-04-20 -->

### Stationeers Logic Extended

F0059 (Mods/PowerTransmitterPlus/RESEARCH.md:748-760) establishes the defensive-integration posture that this page generalizes. The mod being consumed exposes no public extensibility API, so every consumer reimplements the registration pattern from scratch. The pattern-relevant shape of F0059 is that the integration is done by copying a set of concrete reflection points rather than calling into a stable API:

- Registry of `LogicTypeInfo` entries, hardcoded inline.
- Reflection injection into `ProgrammableChip.AllConstants`.
- Postfix on `Logicable.Initialize` to extend tablet UI arrays.
- Postfix on `Enum.GetName` and `EnumCollection<LogicType, ushort>.GetName / GetNameFromValue`.
- Per-device `CanLogicRead` postfix + `GetLogicValue` prefix.
- Postfix on `Stationpedia.PopulateLogicVariables`.

> Stationeers Logic Extended has NO public extensibility API. Every mod that wants custom LogicTypes reimplements the registration pattern from scratch.

Each of those reflection points lives behind a `Prepare()` gate + try/catch boundary in the consuming mod, so a game-version refactor of any one of them demotes the corresponding feature to a no-op instead of crashing the whole mod. This is the same three-part recipe documented above (runtime type resolution, `Prepare()` gating, try/catch around each reflection call) applied to a different target surface.

For the mod's identity details (assembly name, Workshop ID, etc.), see [../GameSystems/ThirdPartyModIdentities.md](../GameSystems/ThirdPartyModIdentities.md).

## Cited verifications
<!-- verified: 0.2.6228.27061 @ 2026-04-20 -->

- F0059: originating pattern (Stationeers Logic Extended) and PowerTransmitterPlus's adoption for custom LogicType registration.
- F0367: PowerTransmitterPlus's Stationpedia integration using `TypeByName` fallback + `Prepare` gating + try/catch.
- F0219t: Stationpedia Ascended's own use of imperative Harmony patches for the same game-version-drift reasons.
- F0219ab: Stationpedia Plus's preference for a custom link handler over the vanilla LateUpdate-based one; reduced failure surface is an instance of "degrade gracefully."

## Verification history
<!-- verified: 0.2.6228.27061 @ 2026-04-20 -->

- 2026-04-20: page created from the Research migration; four sources cited, covering both the generic pattern and its expression across three mods.
- 2026-04-20: added Stationeers Logic Extended worked example (F0059) per Phase 6 Pass A split-coverage fix.

## Open questions

None at creation.
