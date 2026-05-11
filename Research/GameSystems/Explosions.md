---
title: Explosions
type: GameSystems
created_in: 0.2.6228.27061
verified_in: 0.2.6228.27061
verified_at: 2026-04-29
sources:
  - $(StationeersPath)\rocketstation_Data\Managed\Assembly-CSharp.dll :: Explosion (static class)
  - $(StationeersPath)\rocketstation_Data\Managed\Assembly-CSharp.dll :: Assets.Scripts.Objects.Items.ItemExplosive
  - $(StationeersPath)\rocketstation_Data\Managed\Assembly-CSharp.dll :: Assets.Scripts.Objects.Items.ItemRemoteDetonator
  - .work/decomp/0.2.6228.27061/Assembly-CSharp.decompiled.cs (lines 55558-55673, 328008-328700)
related:
  - ./DamageState.md
  - ../GameClasses/Structure.md
  - ../GameClasses/Thing.md
  - ../GameClasses/OnServer.md
tags: [damage]
---

# Explosions

How explosives detonate in Stationeers and why a vanilla Demolition / Mining Charge shatters glass and knocks out players but leaves frames, walls, and machines standing. Covers the `Explosion` static class, the single `ItemExplosive` item class behind every explosive prefab, the `ItemRemoteDetonator`, the total-damage budget cap, the falloff math, and multiplayer authority.

## ItemExplosive: the one class behind every explosive prefab
<!-- verified: 0.2.6228.27061 @ 2026-04-29 -->

There is exactly one explosive item class: `Assets.Scripts.Objects.Items.ItemExplosive`. Inheritance chain `ItemExplosive : Stackable : Item : DynamicThing : Thing`. It implements `IExplosive` (empty marker interface) and `IGenerateMinables` (so detonating near an ore vein spawns ore). The game ships several prefabs off this one class (timed "Mining Charge / Dynite", remote-detonated "Demolition Charge"); they differ only by serialized fields, not by class. There is no separate `DemolitionCharge` C# type. A mod targeting "the demolition charge" should match by `is ItemExplosive` (or by `PrefabHash` / `PrefabName` if it needs to distinguish the remote variant from the timed variant), never by a hard-coded hash literal in code.

The remote/timed discriminator is the bool `CanRemoteDetonator`:

- `CanRemoteDetonator == false` -> the charge is *activated* (lever / use-secondary) and runs `ExplosiveCountdownTime` seconds, then explodes. This is the timed Mining Charge.
- `CanRemoteDetonator == true` -> the charge has no on-board timer; it links to an `ItemRemoteDetonator` and explodes only when the detonator's "Detonate" interaction fires. This is the Demolition Charge.

Serialized tuning fields on `ItemExplosive` (all `public`, so reachable from a Harmony patch or a prefab override):

| Field | Default | Meaning |
|---|---|---|
| `ExplosionRadius` | `5.3f` | radius passed to `Explosion.Explode` (per-prefab serialized) |
| `ExplosionForce` | `2000f` | force passed to `Explosion.Explode` (per-prefab serialized) |
| `ExplosiveCountdownTime` | `10f` | timer for non-remote (timed) charges |
| `CanRemoteDetonator` | `false` | true on the Demolition Charge prefab |
| `EXPLOSION_CHAIN_DELAY` (const) | `0.03f` | delay applied when one explosion triggers a neighbouring explosive |
| `MINING_CHARGE_MAX_DAMAGE` (const) | `2000f` | the `maxDamage` argument passed to `Explosion.Explode` (see "The total-damage budget cap" below; this is the load-bearing number) |
| `RENDER_DISTANCE` / `SHADOW_DISTANCE` (const) | `30f` / `6f` | cosmetic |

`ItemExplosive.MinablesGenerationRange` reads `GameConstants.MINABLES_GENERATION_RANGE_EXPLOSIVE` (how far ore-vein generation reaches when an explosive detonates; not damage, but relevant if a mod widens the radius).

### Detonation path
<!-- verified: 0.2.6228.27061 @ 2026-04-29 -->

```csharp
// ItemExplosive
private async UniTaskVoid ExplosionCountdown(float delay, CancellationToken token)
{
    if (GameManager.RunSimulation)
    {
        AtmosphericsManager.Instance.Register(this);
        float countDownTimer = delay;
        while (countDownTimer > 0f && GameManager.GameState != GameState.None)
        {
            await UniTask.NextFrame(token);
            countDownTimer -= Time.deltaTime;
        }
        if (!base.BeingDestroyed && !token.IsCancellationRequested)
        {
            global::Explosion.Explode(ExplosionForce, base.transform.position, ExplosionRadius, 2000f, mineTerrain: true);
            CancelCountdown();
            OnServer.Destroy(this);
        }
    }
}
```

Entry points that start a detonation:

- `ItemExplosive.TriggerExplosionCountdown(float delay)` spawns the `ExplosionCountdown` UniTask.
- Timed charge: the Activate interaction (`OnInteractableUpdated`) calls `TriggerExplosionCountdown(ExplosiveCountdownTime)`.
- Remote charge: `ItemRemoteDetonator.TriggerExplosives()` iterates `_linkedExplosives` and calls `itemExplosive.TriggerExplosionCountdown(0.15f * (index + 1))` on each, staggered.
- Chain reaction: `ItemExplosive.Explosion(Vector3, float)` override calls `TriggerExplosionCountdown(0.03f)` (an explosive caught in another blast goes off).
- `ItemExplosive.OnDestroy()` detonates if the item is destroyed while a countdown had been initialised.

`ItemRemoteDetonator : PowerTool`. `energyPerExplosive = 1000f` (battery joules consumed per linked charge). `EXPLOSION_DELAY = 0.15f`. The "Detonate" action: `InteractWith` -> `OnInteractableUpdated` -> `TriggerExplosives()`. Linking a charge to a detonator goes through `ItemExplosive.AttackWith` / `OnUseSecondary` -> `ItemExplosive.Link(ItemRemoteDetonator)`; link state replicates via `NetworkUpdateFlags |= 256` (`ExplosiveLinkMessage`), unrelated to damage.

## The Explosion static class
<!-- verified: 0.2.6228.27061 @ 2026-04-29 -->

```csharp
public static class Explosion
{
    private const int   MAX_EXPLOSION_COLLIDERS = 1000;
    private static readonly Collider[] Results = new Collider[1000];
    private const int   MIN_RADIUS = 1;
    private const float MAX_DAMAGE_FACTOR = 100f;
    private const float REMOVE_ALL_THRESHOLD = 0.8f;

    public static void Explode(float force, Vector3 pos, float radius,
                               float maxDamage = float.MaxValue, bool mineTerrain = false)
    {
        if (!GameManager.RunSimulation) return;                       // server / host / single-player only
        maxDamage = Mathf.Min(force * 100f, maxDamage);               // <-- TOTAL damage budget for the whole blast
        Room room = RoomController.World.GetRoom(pos);
        if (room != null && room.RoomType == RoomType.Hydroponics)
            Achievements.AchieveYesItExplodedMark();
        EffectManager.CreateExplosionEffect(pos, radius);             // VFX: pooled particle prefab, startSize = radius
        Vector3Int vector3Int = pos.FloorToInt();
        int num = Mathf.Max(1, (int)radius);                          // integer radius: 5.3 -> 5
        float damageApplied = 0f;
        for (int i = 1; i < num &&
             DamageThingsInSphere(force, pos, i, maxDamage, radius, ref damageApplied, mineTerrain); i++)
        { }                                                          // expanding shells: i = 1 .. num-1

        if (mineTerrain) { /* voxel-terrain carving in a falloff sphere + Vein.TryMineServer; see GetSphereOffsets */ }

        if (Assets.Scripts.Networking.NetworkManager.IsServer && NetworkServer.HasClients())
            ExplosionEvent.NewEvents.Add(new ExplosionEvent(pos, radius));  // replicate VFX to clients (no damage replication here)
    }

    public static void DamageThing(Thing thing, Vector3 position, float force)
    {
        thing.DamageState.Damage(ChangeDamageType.Increment, force, DamageUpdateType.Brute);
        thing.DamageState.Damage(ChangeDamageType.Increment, force, DamageUpdateType.Stun);
        thing.Explosion(position, force);                            // physics knockback / chain trigger; NOT where structure HP is removed
    }

    private static bool DamageThingsInSphere(float force, Vector3 position, int radius,
        float maxDamage, float maxRadius, ref float damageApplied, bool mineTerrain)
    {
        int num = Physics.OverlapSphereNonAlloc(position, radius, Results);   // default layer mask
        for (int i = 0; i < num; i++)
        {
            if (damageApplied > maxDamage) return false;            // <-- budget exhausted -> stop the whole blast
            Thing thing = Thing.Find(Results[i]);                   // collider -> Thing via Thing._colliderLookup
            if ((!mineTerrain || (!(thing is IExplosive) && !(thing is Ore)))
                && !(thing is Structure { IsBroken: not false })
                && (object)thing != null)
            {
                float num2 = Mathf.Lerp(force, 0f, Vector3.Distance(thing.Position, position) / maxRadius);
                DamageThing(thing, position, num2);
                damageApplied += num2;
            }
        }
        return true;
    }
}
```

Full call chain (charge detonates -> HP removed from a Thing):

`ItemExplosive.ExplosionCountdown` (or `OnDestroy`) -> `Explosion.Explode(force=2000, pos, radius=5.3, maxDamage=2000, mineTerrain=true)` -> loop `i = 1 .. 4` -> `DamageThingsInSphere(force, pos, i, ...)` -> `Physics.OverlapSphereNonAlloc` -> for each collider `Thing.Find(collider)` -> `Explosion.DamageThing(thing, pos, scaledForce)` -> `thing.DamageState.Damage(Increment, scaledForce, Brute)` + `thing.DamageState.Damage(Increment, scaledForce, Stun)` + `thing.Explosion(pos, scaledForce)`.

`Thing.Explosion(Vector3 position, float force = 0f)` is `virtual`, default empty. Overrides: `DynamicThing` -> `RigidBody.AddExplosionForce(force, position, 5f)`; `Human` -> `AddExplosionForce(force * 2.2f, ...)`; `ItemExplosive` -> `TriggerExplosionCountdown(0.03f)` (chain reaction); `Grenade` / `Dynamite` -> arm themselves; `Ore` -> empty (terrain veins handled separately). So `thing.Explosion` is *physics push + chain detonation*, not structural HP removal; HP is removed only by the two `DamageState.Damage(...)` calls.

## How damage is distributed across the blast
<!-- verified: 0.2.6228.27061 @ 2026-04-29 -->

- **Enumeration**: `Physics.OverlapSphereNonAlloc(position, radius, Results)` with no layer-mask argument, so it uses Unity's `DefaultRaycastLayers` (every layer except "Ignore Raycast"). Up to `MAX_EXPLOSION_COLLIDERS = 1000` colliders per shell. Collider -> Thing via `Thing.Find(Collider)` (a `Dictionary<Collider, Thing>` populated by `Thing.CacheColliders()`). Anything whose collider is not in that dict (raw scenery, terrain colliders) is skipped because `Thing.Find` returns null.
- **Shells, not one sphere**: `Explode` calls `DamageThingsInSphere` once per integer radius `i = 1 .. (int)radius - 1`. A Thing within 1 m of the blast is enumerated on *every* shell (~4 hits for radius 5.3); a Thing 3-4 m out is hit on one shell. Nearby things take damage multiple times.
- **Per-hit damage = linear falloff to zero at `maxRadius`**: `num2 = Mathf.Lerp(force, 0f, Vector3.Distance(thing.Position, position) / maxRadius)`. Point-blank ~= `force` (2000), at `maxRadius` ~= 0. Distance is measured from `thing.Position` (the Thing's transform origin), not the nearest point of its collider. For a wall or frame on a 2 m grid the origin is the cell, so a structure adjacent to a charge floating in air is already ~2 m away: `Lerp(2000, 0, 2/5.3)` ~= 1245 per hit.
- **What is applied**: `DamageState.Damage(Increment, num2, Brute)` and `... Stun`. For a `Structure` the Stun call is silently dropped (`ThingDamageState.DamageAllowed` returns true only for `Burn` and `Brute`); only the Brute call matters for structures. For a `Human` the Stun call routes to `OrganBrain.DamageState` and that is what knocks the player out (see `../GameSystems/StunStateMachine.md`). When `DamageState.Total >= DamageState.MaxDamage` the Thing is "broken" and the async destroy path runs (see `../GameSystems/DamageState.md` and `../GameClasses/Structure.md`).

### The total-damage budget cap (why a vanilla charge does not level a base)
<!-- verified: 0.2.6228.27061 @ 2026-04-29 -->

`Explosion.Explode`'s `maxDamage` parameter is the **total** damage budget for the *entire* blast, not a per-target cap. For the Demolition / Mining Charge it is `Mathf.Min(force * 100f, MINING_CHARGE_MAX_DAMAGE)` = `Mathf.Min(2000 * 100, 2000)` = `2000f`. `DamageThingsInSphere` keeps a running `damageApplied` and returns `false` (which stops the shell loop in `Explode`) the moment `damageApplied > maxDamage`. Because `force = 2000`, a single point-blank hit on *anything* already adds ~2000 of Brute (plus another ~2000 of Stun, which also counts toward `damageApplied` even when the target ignores Stun), so the budget is exhausted after the first one or two colliders the `OverlapSphere` happens to return. Whatever those colliders are (a player, a glass window with tiny HP, the explosive item itself, a loose item on the floor, the floor plate) soaks the whole 2000, and every sturdier structure further down the iteration order never gets touched. Even structures that *are* touched get only a thin slice. The cap, not any invulnerability flag, is the dominant reason frames, walls, and machines survive. `MIN_RADIUS = 1` means a radius below 1 still does one shell at radius 1. `MAX_DAMAGE_FACTOR = 100f` is the `force * 100` term. `REMOVE_ALL_THRESHOLD = 0.8f` and the `Mathf.Pow(1f - dist/radius, 0.4f)` falloff curve are used only by `GetSphereOffsets` for terrain carving when `mineTerrain` is true.

### There is no explosion-immunity flag
<!-- verified: 0.2.6228.27061 @ 2026-04-29 -->

Structures take Brute damage from explosions normally. `Structure` does not override `InitializeDamageState`, so it gets `Thing.InitializeDamageState()` -> `new ThingDamageState(this, ThingHealth)`, and `ThingDamageState.DamageAllowed` returns true for `Burn` and `Brute`. There is no `Invincible` / `IsInvulnerable` / `CanBeDamaged` / explosion-immune flag in the path. (`Thing.Indestructable` exists and swaps in `IndestructableDamageState` whose `Damage()` no-ops, but normal structures do not set it.) Structures simply never receive enough Brute because of the budget cap and the falloff, and what little they do receive is far below their per-prefab `ThingHealth` (= `DamageState.MaxDamage`; `Thing.ThingHealth` defaults to `100f` but structural prefabs are authored much higher in the Unity asset). See `../GameSystems/DamageState.md` for the channel/`DamageAllowed` matrix.

## Multiplayer authority
<!-- verified: 0.2.6228.27061 @ 2026-04-29 -->

Server-authoritative throughout:

- `Explosion.Explode` first line: `if (!GameManager.RunSimulation) return;`. `RunSimulation` is true only on the host / dedicated server / single-player. Clients never run the damage or terrain logic.
- `ItemExplosive.ExplosionCountdown`, `TriggerExplosionCountdown`, and `ItemRemoteDetonator.TriggerExplosives()` are all wrapped in `if (GameManager.RunSimulation)`.
- Damage is applied server-side via `DamageState.Damage`, which sets `Parent.NetworkUpdateFlags |= 4`; the per-Thing damage state replicates to clients through the normal Thing networking (`IndestructableDamageState.Write` / `Read`). There is no bespoke "explosion damage" RPC.
- Destruction replicates via `Thing.OnDestroy()` queuing a `DestroyEvent`, and `Structure.StructureDestroyed` raising a `ConstructionEventInstance` through normal construction-event networking. Broken-mesh conversion replicates via the structure build-state networking.
- Clients receive only cosmetics: `ExplosionEvent.NewEvents` (a `SyncList<ExplosionEvent>` of `(position, radius)`) is appended on the server when `NetworkServer.HasClients()`, deserialized on clients to call `EffectManager.CreateExplosionEffect`. `NetworkMessages.ExplosionMessage` (message id 118) carries `ExplosionForce` / `ExplosionRadius` for effect replication.

Implication for a mod: do all explosion logic server-side (gate on `GameManager.RunSimulation` and/or `NetworkManager.IsServer`). `DamageState.Damage` and `OnServer.Destroy` already replicate. Do not apply damage or destroy Things on a connected client or you will desync (and `OnServer.Destroy` logs an error if called on a client).

## Notes for a "bigger / actually-destructive Demolition Charge" mod
<!-- verified: 0.2.6228.27061 @ 2026-04-29 -->

Levers, in order of impact:

1. **Remove or massively raise the damage budget.** The `maxDamage` argument to `Explosion.Explode` is sourced from `ItemExplosive.MINING_CHARGE_MAX_DAMAGE = 2000f`. Passing `float.MaxValue` (or a large number) so `damageApplied > maxDamage` never short-circuits makes the existing radius hit *everything* in range instead of stopping after the first collider. Patch target: the two `Explosion.Explode(ExplosionForce, ..., 2000f, true)` call sites in `ItemExplosive.ExplosionCountdown` / `ItemExplosive.OnDestroy`, or a transpiler/prefix on `Explosion.DamageThingsInSphere` to ignore the cap.
2. **Raise `ExplosionRadius` and `ExplosionForce`** on the `ItemExplosive` prefab(s) or in a postfix that bumps the fields on spawn. Radius is `(int)`-floored for the shell loop, so go to e.g. `8.x` for eight shells. Force scales the per-hit `Lerp(force, 0, ...)`.
3. **Optional: replace the damage application** with a custom routine that does one `Physics.OverlapSphere(pos, radius)` and, for each `Thing.Find(col)`, either `DamageThing(thing, pos, hugeForce)` or directly `thing.DamageState.Damage(ChangeDamageType.Set, thing.DamageState.MaxDamage, DamageUpdateType.Brute)` to one-shot it, then let the vanilla `ThingDamageState.Destroy()` / `Structure.OnDamageDestroyed()` chain handle wreckage + networking. Note that walls/frames with a "broken mesh" build state are converted to wreckage by the damage path rather than removed; for guaranteed removal call `OnServer.Destroy(thing)` or `Thing.Delete(...)` directly (see `../GameClasses/OnServer.md`, `../GameClasses/Thing.md`, `../GameClasses/Structure.md`).
4. **Distance metric**: vanilla uses `thing.Position`. For large structures, `collider.ClosestPoint(pos)` gives near-full damage to a wall whose origin is 2 m from a charge stuck to it.
5. **Keep it server-side** (`GameManager.RunSimulation`); damage and destroy both already replicate.

## Verification history
<!-- verified: 0.2.6228.27061 @ 2026-04-29 -->

- 2026-04-29: page created from a research pass on the demolition-charge / explosion system (game version 0.2.6228.27061). Sources: `Explosion` static class (decompile lines 55558-55673), `Assets.Scripts.Objects.Items.ItemExplosive` (lines 328008-328400), `Assets.Scripts.Objects.Items.ItemRemoteDetonator` (lines 328411-328700). No prior page on explosions existed; no conflicts. Damage-channel / `DamageAllowed` details cross-referenced against the existing `../GameSystems/DamageState.md` (consistent).

## Open questions

None at creation.
