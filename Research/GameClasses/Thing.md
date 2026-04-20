---
title: Thing
type: GameClasses
created_in: 0.2.6228.27061
verified_in: 0.2.6228.27061
verified_at: 2026-04-20
sources:
  - Plans/RepairPrototype/plan.md:373-383
  - $(StationeersPath)\rocketstation_Data\Managed\Assembly-CSharp.dll :: Assets.Scripts.Objects.Thing
related:
  - ./Structure.md
  - ./Entity.md
tags: [prefab, slots]
---

# Thing

Vanilla game class at `Assets.Scripts.Objects.Thing`. The base class of every in-world game object. All prefab types derive from `Thing` either as `DynamicThing` (non-fixed-position) or `Structure` (player-built, fixed).

## Object hierarchy
<!-- verified: 0.2.6228.27061 @ 2026-04-20 -->

Source: F0223.

```
Assets.Scripts.Objects.Thing              (base of ALL game objects)
  |-- DynamicThing                        (non-fixed-position)
  |     |-- Item                          (inventory-storable)
  |     |-- Entity                        (living: Human, etc.)
  |-- Structure                           (player-built, fixed)
        |-- LargeStructure                (2m grid: frames, walls)
        |-- SmallGrid                     (0.5m grid: pipes, cables, devices)
              |-- Device                  (powered machines)
```

## Verification history
<!-- verified: 0.2.6228.27061 @ 2026-04-20 -->

- 2026-04-20: page created from the Research migration; verbatim content lifted from F0223. No conflicts.

## Open questions

None at creation.
