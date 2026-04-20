---
title: Mod conflict detection via assembly scan
type: Patterns
created_in: 0.2.6228.27061
verified_in: 0.2.6228.27061
verified_at: 2026-04-20
sources:
  - Mods/SprayPaintPlus/RESEARCH.md:15-17 (F0007, primary)
  - Mods/SprayPaintPlus/SprayPaintPlus/Plugin.cs:50-53 (F0368)
related:
  - ../Protocols/LaunchPadBoosterNetworking.md
  - ./BestEffortIntegration.md
tags: [launchpad, harmony]
---

# Mod conflict detection via assembly scan

BepInEx's `BepInIncompatibility` attribute covers conflicts detectable at plugin-load time, but StationeersLaunchPad loads mods progressively, so a conflicting assembly may not exist yet when the current plugin's `Awake()` runs. The documented pattern is a deferred scan of `AppDomain.CurrentDomain.GetAssemblies()` after StationeersLaunchPad finishes loading.

## Problem
<!-- verified: 0.2.6228.27061 @ 2026-04-20 -->

F0007 (Mods/SprayPaintPlus/RESEARCH.md:15-17, primary):

> The mod replaces Color Cycler and Network Painter. It cannot coexist with them because they patch the same methods. `BepInIncompatibility` attributes cover load-time detection, but StationeersLaunchPad loads mods progressively, so those assemblies may not exist when `Awake()` runs. A second check runs on `Prefab.OnPrefabsLoaded`, scanning `AppDomain.CurrentDomain.GetAssemblies()` for the conflicting assembly names. If found, the mod logs a fatal error and starts a coroutine that repeats the warning every 5 seconds. No Harmony patches are applied.

F0368 (Mods/SprayPaintPlus/SprayPaintPlus/Plugin.cs:50-53) restates the same timing requirement:

> StationeersLaunchPad loads mods progressively; conflicting assemblies may not exist yet when our `Awake()` fires. `Prefab.OnPrefabsLoaded` fires after StationeersLaunchPad finishes loading all mods. No patches are applied until the check passes.

## Solution / recipe
<!-- verified: 0.2.6228.27061 @ 2026-04-20 -->

Two-tier detection:

### Tier 1: BepInEx attributes

`[BepInIncompatibility("<guid>")]` on the plugin class catches conflicts between mods whose plugin GUIDs are known at build time and whose assemblies are loaded before the current plugin's `Awake()`. Use for well-known legacy conflicts; free of charge.

### Tier 2: deferred assembly scan

Register an `OnPrefabsLoaded` handler (or equivalent post-StationeersLaunchPad hook) that walks `AppDomain.CurrentDomain.GetAssemblies()` and matches by assembly name. If a conflict is found:

1. Log a fatal error identifying both mods.
2. Do NOT apply Harmony patches; leave the game unmodified.
3. Start a coroutine that repeats the warning every few seconds so the player sees the message even if they missed the initial log line.

Skeleton:

```csharp
private void Awake()
{
    Prefab.OnPrefabsLoaded += CheckConflicts;
}

private void CheckConflicts()
{
    var conflicts = AppDomain.CurrentDomain.GetAssemblies()
        .Select(a => a.GetName().Name)
        .Intersect(ConflictingAssemblyNames)
        .ToList();

    if (conflicts.Any())
    {
        Logger.LogError($"[SprayPaintPlus] Conflicting mods: {string.Join(", ", conflicts)}");
        StartCoroutine(RepeatWarning(conflicts));
        return;  // no patches applied
    }

    harmony.PatchAll();
}
```

## Cited verifications
<!-- verified: 0.2.6228.27061 @ 2026-04-20 -->

- F0007: primary source covering the full pattern (BepInEx attribute insufficient, `OnPrefabsLoaded` scan, repeat-warning coroutine, patches withheld).
- F0368: plugin code comment on progressive loading + deferred detection, confirming that `OnPrefabsLoaded` is the correct deferred hook.

## Verification history
<!-- verified: 0.2.6228.27061 @ 2026-04-20 -->

- 2026-04-20: page created from the Research migration; F0007 primary, F0368 confirms timing.

## Open questions

None at creation.
