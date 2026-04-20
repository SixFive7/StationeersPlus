---
title: AccessTools recipes for private field access
type: Patterns
created_in: 0.2.6228.27061
verified_in: 0.2.6228.27061
verified_at: 2026-04-20
sources:
  - Plans/RepairPrototype/plan.md:823-840 (F0229m)
related:
  - ./HarmonyPatchTypes.md
  - ./HarmonyFieldOrProperty.md
tags: [harmony]
---

# AccessTools recipes for private field access

Two mechanisms for reading/writing a private field of a game class from a Harmony patch: cached `FieldInfo` via `AccessTools.Field(...)`, or the Harmony parameter-naming convention (four underscores + field name). Pick by call frequency and patch context.

## Recipes
<!-- verified: 0.2.6228.27061 @ 2026-04-20 -->

F0229m (Plans/RepairPrototype/plan.md:823-840):

> Private field access: `static FieldInfo myField = AccessTools.Field(typeof(WeatherManager), "_stormWindStrength"); myField.SetValue(__instance, newValue);` OR via parameter naming in patch method: `public static void Patch(float ____privateFieldName) { ... }` (Harmony convention: 4 underscores + field name). Key singletons: `GameManager.IsServer` / `NetworkManager.IsServer`, `WorldManager.IsPaused`, `WorldManager.Instance.GameMode`, `WeatherManager.Instance`, `WorldManager.Instance.SourcePrefabs` (master prefab list).

### Cached FieldInfo via AccessTools

```csharp
private static readonly FieldInfo _stormWindField =
    AccessTools.Field(typeof(WeatherManager), "_stormWindStrength");

public static void SomeMethod(WeatherManager wm)
{
    _stormWindField.SetValue(wm, 0.5f);
    var current = (float)_stormWindField.GetValue(wm);
}
```

Use when:
- Reading/writing the field from non-patch code.
- The same field is accessed from many places; one cached `FieldInfo` is more readable than scattered `typeof(X).GetField(...)` calls.

### Harmony parameter-naming convention

```csharp
[HarmonyPostfix]
[HarmonyPatch(typeof(WeatherManager), nameof(WeatherManager.Tick))]
public static void Postfix(float ____stormWindStrength)
{
    // read-only access to the field
}
```

Use when:
- Inside a Harmony patch body specifically.
- Reading is sufficient (the parameter is by-value unless declared `ref`).
- Writing also works with `ref ____fieldName`.

### Key game singletons

The F0229m excerpt also documents commonly-referenced singletons:

- `GameManager.IsServer` / `NetworkManager.IsServer`: role check. See `../GameSystems/NetworkRoles.md` and `./SinglePlayerNetworkRole.md`.
- `WorldManager.IsPaused`: game-pause state.
- `WorldManager.Instance.GameMode`: game-mode enum.
- `WeatherManager.Instance`: current-weather singleton.
- `WorldManager.Instance.SourcePrefabs`: master prefab list (the registry `PrefabCloning` writes to).

## Verification history
<!-- verified: 0.2.6228.27061 @ 2026-04-20 -->

- 2026-04-20: page created from the Research migration; single source (F0229m).

## Open questions

None at creation.
