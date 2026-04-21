---
title: CursorManager
type: GameClasses
created_in: 0.2.6228.27061
verified_in: 0.2.6228.27061
verified_at: 2026-04-21
sources:
  - rocketstation_Data/Managed/Assembly-CSharp.dll :: Assets.Scripts.CursorManager
related:
  - ./Thing.md
  - ./InventoryManager.md
  - ../Patterns/UnityFakeNull.md
tags: [ui]
---

# CursorManager

<!-- verified: 0.2.6228.27061 @ 2026-04-21 -->

Vanilla game class at `Assets.Scripts.CursorManager`. Singleton that raycasts the player's cursor each tick and caches the `Thing` (or terrain feature) currently under the crosshair. Mod code that needs to answer "what is the local player looking at right now?" reads from this class rather than performing its own raycast.

## Static accessors

<!-- verified: 0.2.6228.27061 @ 2026-04-21 -->

| Accessor | Line | Returns | Notes |
|---|---|---|---|
| `CursorThing` | CursorManager.cs:135 | `Thing` | `Instance.FoundThing`. Null when the cursor is on empty space, terrain, or a non-Thing collider; also null when the inventory UI is blocking input or the game is paused. |
| `CursorTerrain` | CursorManager.cs:136 | Terrain feature | `Instance.FoundTerrain`. Alternative when the cursor is on terrain. |
| `CursorHit` | CursorManager.cs:129 | `RaycastHit` | Most recent raycast hit (collision point, normal, collider). |
| `CursorHitMask` | CursorManager.cs:47 | `LayerMask` | Layer mask governing which objects are cursor-targetable. |

`Instance` is the standard singleton backing field typical of the `ManagerBase`-style pattern used across the game's manager classes.

## Usage

<!-- verified: 0.2.6228.27061 @ 2026-04-21 -->

Reading the currently-hovered Thing from a BepInEx plugin's `Update()` or a Harmony patch:

```
Thing target = CursorManager.CursorThing;
if (target != null)
{
    // act on target
}
```

Returns directly as `Thing`; no cast from `Interactable` needed. Works identically in single-player and multiplayer: the raycast is local to each peer, so each player sees their own cursor target.

## Null-safety expectations

<!-- verified: 0.2.6228.27061 @ 2026-04-21 -->

`CursorThing` is null-able by design. Callers MUST null-check on every read; the cursor frequently points at nothing. The returned `Thing` may additionally be a Unity fake-null; for maximum safety compare via `!target` as documented in `../Patterns/UnityFakeNull.md`.

`CursorManager.Instance` itself may be null during very early initialization (before the scene loads); for code running after `Prefab.OnPrefabsLoaded` this is rarely a concern, but deep Awake-time access should guard.

## Verification history

- 2026-04-21: page created. Findings from decompile of Assembly-CSharp.dll (CursorManager.cs line 47 for `CursorHitMask`, line 129 for `CursorHit`, line 135 for `CursorThing`, line 136 for `CursorTerrain`).

## Open questions

- Does `CursorThing` respect interactability filtering, or does it return any Thing hit by the raycast regardless of whether the player can reach / interact with it? If unreachable Things appear (for example, behind a transparent collider), callers that care must supplement with a distance / line-of-sight check.
- Which specific UI-state flag flips `CursorThing` to null when "inventory UI is blocking input"? Candidates: `InventoryManager.CurrentMode`, a `Modal.IsActive` flag, or something in `CursorManager.Instance` directly.
