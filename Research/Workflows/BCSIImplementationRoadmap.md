---
title: BCSI v1 Implementation Roadmap
type: Workflows
created_in: 0.2.6228.27061
verified_in: 0.2.6228.27061
verified_at: 2026-04-20
sources:
  - Plans/RepairPrototype/plan.md:881-912
related:
  - ModProjectSetup.md
  - ../Patterns/MirroredDevices.md
  - ../Patterns/LogicChannels.md
tags: [damage, power, logic]
---

# BCSI v1 Implementation Roadmap

Seven-step build plan for BCSI v1 (Building Maintenance Inspector damage tax + passive auto-repair). Reach for this recipe when starting a new auto-repair-style device mod that clones a vanilla prefab, adds logic channels, runs a scan loop, posts chat reports, and ships with BepInEx config.

## When to use
<!-- verified: 0.2.6228.27061 @ 2026-04-20 -->

- Building a BepInEx plugin that clones a vanilla device (Mirrored Devices cloning pattern, without mirroring) and adds behaviour on top.
- Adding logic channels (`On`, `Setting`, `Mode`, `Ratio`, `Quantity`) to a cloned device.
- Running a timer-based periodic scan across `Thing.AllThings` that filters, sums damage, consumes materials, and reduces `DamageState` values.
- Shipping inspector-style chat messages: daily report, incident alert, overdue invoice, long no-damage streak.

## Prerequisites
<!-- verified: 0.2.6228.27061 @ 2026-04-20 -->

- BepInEx + Harmony + StationeersLaunchPad baseline wired up per [ModProjectSetup.md](ModProjectSetup.md).
- Familiarity with the Mirrored Devices cloning pattern and the game's `SourcePrefabs` / `MultiConstructor` registration flow.

## Steps
<!-- verified: 0.2.6228.27061 @ 2026-04-20 -->

### v1: BCSI Damage Tax (Passive Auto-Repair)

1. **Scaffold BepInEx plugin** (1 hour)
   - Plugin class, Harmony init, config binds
   - Reference: Battery Backup Light structure

2. **Clone vanilla device** (2 hours)
   - Implement Mirrored Devices cloning pattern (sans mirroring)
   - Pick source device (Decision 1)
   - Register with SourcePrefabs, add to MultiConstructor, register localization

3. **Add logic channels** (2 hours)
   - Patch `CanLogicRead`/`GetLogicValue`/`SetLogicValue`/`CanLogicWrite`
   - Channels: On, Setting (threshold), Mode (priority), Ratio (damage %), Quantity (total damage)

4. **Implement repair loop** (3 hours)
   - Timer-based periodic scan (every in-game day)
   - Iterate `Thing.AllThings`, filter structures, sum damage
   - Calculate material cost based on damage type and structure material
   - Search storage containers for materials, consume them
   - Reduce DamageState values proportionally

5. **Inspector chat messages** (2 hours)
   - Inspector name assignment and persistence
   - Message templates for: daily report, incident alert, overdue invoice, long no-damage streak
   - Post to chat via game's messaging system

6. **Config system** (1 hour)
   - All rates, thresholds, toggles via BepInEx Config

7. **Testing** (ongoing)
   - Single player, multiplayer (dedicated server), save/load cycles

## Verification
<!-- verified: 0.2.6228.27061 @ 2026-04-20 -->

- Step 2: confirm the cloned device appears in the kit's build menu alongside the source device and its name matches the localization entry.
- Step 3: drop an IC10 test script that reads / writes each logic channel on the cloned device; verify all five are reachable.
- Step 4: snapshot a representative damaged structure's `DamageState` before and after a scan cycle; confirm damage decreases proportionally and the corresponding material was consumed from a nearby container.
- Step 5: check chat after a full day elapses; verify the daily report fires.
- Step 7: save / load cycle. Confirm the cloned device's state (logic channel values, inspector state) persists across save and load. Test on a dedicated server to catch server-only code paths.

## Pitfalls
<!-- verified: 0.2.6228.27061 @ 2026-04-20 -->

- Server-side only for the scan loop: `DamageState.Damage()` checks `GameManager.RunSimulation`.
- `Thing.AllThings` can be large; filter early (structures only, damaged only) to avoid per-frame work scaling linearly with world size.
- Save / load of a cloned prefab relies on `Animator.StringToHash(name)` producing a deterministic PrefabHash. Pick a stable name and keep it stable across versions. If the mod is removed, things with unknown PrefabHash fail to load (silently disappear or cause errors).
- `Device.AllDevices` can contain duplicates, `AtmosphericsManager.AllAtmospheres` can contain nulls, power calculations can produce `NaN`, the power tick runs on a background thread (cannot use `UnityEngine.Random`), and atmosphere may be null until a player logs in on dedicated servers. Guard accordingly.

## Verification history
<!-- verified: 0.2.6228.27061 @ 2026-04-20 -->

- 2026-04-20: page created from the Research migration; verbatim content lifted from F0239 (`Plans/RepairPrototype/plan.md:881-912`).

## Open questions

None at creation.
