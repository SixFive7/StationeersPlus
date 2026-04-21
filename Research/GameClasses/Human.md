---
title: Human
type: GameClasses
created_in: 0.2.6228.27061
verified_in: 0.2.6228.27061
verified_at: 2026-04-20
sources:
  - Mods/SprayPaintPlus/RESEARCH.md:215-217
  - $(StationeersPath)\rocketstation_Data\Managed\Assembly-CSharp.dll :: Human
related:
  - ./Entity.md
  - ./ChatMessage.md
  - ./ILifeSuspender.md
  - ../GameSystems/StunStateMachine.md
tags: [entity]
---

# Human

Vanilla game class representing the player-controlled character entity. Lives under `Assets.Scripts.Objects.Entities` in the Entity hierarchy.

## Player identification
<!-- verified: 0.2.6228.27061 @ 2026-04-20 -->

Source: F0024.

The original mods used the LaunchPadBooster connection id to track which player pressed which modifier keys. But `AttackWithMessage` on the server does not carry the LaunchPadBooster connection id; it carries `AttackParentId`, which is the Human ReferenceId. Keying `PlayerModifiers` by Human ReferenceId matches the identifier available at paint time.

## Enumeration: Human.AllHumans
<!-- verified: 0.2.6228.27061 @ 2026-04-21 -->

Source: `$(StationeersPath)\rocketstation_Data\Managed\Assembly-CSharp.dll :: Assets.Scripts.Objects.Entities.Human`.

`Human` exposes a static collection of every registered human (alive or not, local or remote):

```csharp
public static readonly List<Human> AllHumans = new List<Human>();
```

Fully qualified: `Assets.Scripts.Objects.Entities.Human.AllHumans`. Field, not property.

Inhabitants are added / removed by `Human` load and destroy paths; the list contains every `Human` instance currently loaded in the scene, including the local player, remote players, dead bodies (state `Dead`), and unconscious players. NPCs of type `Npc` are not in this list (different subclass; `Npc : Entity` diverges from `Human : Entity`).

To enumerate connected, alive human players only, filter by state:

```csharp
foreach (var h in Human.AllHumans)
{
    if (h == null || h.State != EntityState.Alive) continue;
    // h is a live player (local or remote)
}
```

`EntityState` lives at `Assets.Scripts.Objects.Entities.EntityState` and has values `Alive, Dead, Unconscious, Decay` (byte). `EntityState.Alive == 0`.

`Entity.IsDead` (inherited by `Human`) is `State == Dead || State == Decay`. `Entity.Unconscious` is `State == Unconscious`. Neither maps directly to "is the connection active"; use `Human.OrganBrain.IsOnline` to distinguish an online vs offline player (online = a real network connection is present; offline = the body is still simulated but no client is driving it).

To enumerate every player that has a live network connection (including unconscious):

```csharp
foreach (var h in Human.AllHumans)
{
    if (h == null) continue;
    if (h.State == EntityState.Dead || h.State == EntityState.Decay) continue;
    if (h.OrganBrain == null || !h.OrganBrain.IsOnline) continue;
    // h is an online, non-dead player
}
```

`Human.LocalHuman` (static property on `Human`) returns the local player's `Human` instance or null when running headless / before spawn.

## Verification history

- 2026-04-20: page created from the Research migration; verbatim content lifted from F0024. No conflicts.
- 2026-04-21: added "Enumeration: Human.AllHumans" section. Additive only; no existing content changed. Verified against `Assets.Scripts.Objects.Entities.Human` (line ~45: `public static readonly List<Human> AllHumans = new List<Human>();`) in game version 0.2.6228.27061.

## Open questions

None.
