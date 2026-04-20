---
title: Replace a protected method via public-API composition
type: Patterns
created_in: 0.2.6228.27061
verified_in: 0.2.6228.27061
verified_at: 2026-04-20
sources:
  - Mods/SprayPaintPlus/SprayPaintPlus/NetworkPainterPatch.cs:287-290 (F0352)
related:
  - ./AccessToolsRecipes.md
tags: []
---

# Replace a protected method via public-API composition

When a mod's patch needs to call a protected method on a game class (e.g. `Structure.GetRoom()`), composing the same result out of already-public APIs is usually cleaner than reflection. Faster, safer across game-version updates, and self-documenting.

## Problem
<!-- verified: 0.2.6228.27061 @ 2026-04-20 -->

F0352 (Mods/SprayPaintPlus/SprayPaintPlus/NetworkPainterPatch.cs:287-290):

> Replicates Structure.GetRoom() (which is protected) via public APIs using `RoomController.World?.GetRoom(s.GridPosition)`.

`Structure.GetRoom()` is protected, so a mod-side call requires reflection. The equivalent work can be done with one public call (`RoomController.World?.GetRoom(s.GridPosition)`) that reaches the same data without going through the protected member.

## Solution / recipe
<!-- verified: 0.2.6228.27061 @ 2026-04-20 -->

Before reaching for `AccessTools.Method(...)` to call a protected method:

1. Read the decompiled protected method body.
2. Check whether it composes public APIs internally.
3. If yes, compose the equivalent directly in mod code.
4. If no (protected method does unique work not reachable otherwise), fall back to reflection. See `./AccessToolsRecipes.md`.

Concrete example from F0352: `Structure.GetRoom()` internally resolves to `RoomController.World?.GetRoom(structure.GridPosition)`. The public-API compose works because `RoomController.World` and `IWorld.GetRoom(Vector3Int)` are both public, and `Structure.GridPosition` is the public input.

### When the compose is wrong

The pattern is NOT appropriate when the protected method has private or internal side-effects (state mutation, caching). Decompile the method body first; if it does work beyond the pure computation the caller wants, fall back to reflection or skip the call.

## Verification history
<!-- verified: 0.2.6228.27061 @ 2026-04-20 -->

- 2026-04-20: page created from the Research migration; single source (F0352).

## Open questions

None at creation.
