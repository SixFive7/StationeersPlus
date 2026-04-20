---
title: DamageState
type: GameSystems
created_in: 0.2.6228.27061
verified_in: 0.2.6228.27061
verified_at: 2026-04-20
sources:
  - Plans/LLM/RESEARCH.md:339-353
  - Plans/LLM/RESEARCH.md:358-369
  - Plans/LLM/RESEARCH.md:371-374
  - Plans/RepairPrototype/plan.md:93-135
  - Plans/RepairPrototype/plan.md:142-156
  - Plans/RepairPrototype/plan.md:168-181
related:
  - ./StunStateMachine.md
  - ./RepairMechanics.md
  - ../GameClasses/Thing.md
  - ../Workflows/HealAllDamagedThings.md
tags: [damage, save-edit, save-format]
---

# DamageState

Every `Thing` carries a `DamageState` with up to nine channels (Burn, Brute, Oxygen, Hydration, Radiation, Starvation, Toxic, Stun, Decay). This page catalogs the channels, the class hierarchy (`IndestructableDamageState` -> `ThingDamageState` -> `OrganicDamageState` -> `EntityDamageState`), the `HealAll` behavior at each layer, the writable float fields, the save-edit disambiguation gotcha (same tag names appear inside `<DamageState>`, `<AtmosphereSaveData>`, and on player vital stats), and a real-save observed damage distribution.

## Damage channels + Total + IsBroken
<!-- verified: 0.2.6228.27061 @ 2026-04-20 -->

Each `Thing` has a `DamageState` with up to 9 damage channels stored as `ThingDamageValue` fields (float clamped to [0, MaxDamage], default MaxDamage = 200):

| Channel | Flag | Used by |
|---|---|---|
| Burn | 0x02 | All things (ThingDamageState) |
| Brute | 0x04 | All things (ThingDamageState) |
| Oxygen | 0x08 | Items, organs (OrganicDamageState) |
| Hydration | 0x10 | Items, organs |
| Radiation | 0x20 | Items, organs |
| Starvation | 0x40 | Entities (EntityDamageState) |
| Toxic | 0x80 | Items, organs |
| Stun | 0x100 | Entities (brain) |
| Decay | 0x200 | Items, organs |

`DamageState.Total` = sum of all channels except Stun, clamped to [0, MaxDamage]. When Total >= MaxDamage, the thing is destroyed (`IsBroken = true`).

## Class hierarchy
<!-- verified: 0.2.6228.27061 @ 2026-04-20 -->

```
IndestructableDamageState          (base: all 9 channels, Damage() gated by RunSimulation)
  ThingDamageState                 (destructible: Heal/HealAll for Burn+Brute, destroy logic)
    OrganicDamageState             (adds Oxygen/Toxic/Radiation/Hydration/Stun/Decay)
      EntityDamageState            (adds Starvation, routes Stun/Oxygen to Brain organ)
```

Runtime types:
- `Thing` base: creates `ThingDamageState` (Burn + Brute only)
- `Item` override: creates `OrganicDamageState` (all channels)
- `Entity`/`Human`: creates `EntityDamageState`

## HealAll methods at each layer
<!-- verified: 0.2.6228.27061 @ 2026-04-20 -->

`ThingDamageState.HealAll(float minDamageRemaining = 0f)`: sets Burn and Brute to 0, clears `_isDestroyed` flag. `OrganicDamageState.HealAll` adds: Oxygen, Toxic, Radiation, Hydration, Stun, Decay all set to 0. `EntityDamageState.HealAll` adds Starvation.

Calls `Damage(ChangeDamageType.Set, ...)` internally, so it goes through `GameManager.RunSimulation` gate. Server/host only.

## Writable float fields
<!-- verified: 0.2.6228.27061 @ 2026-04-20 -->

Every `Thing` in Stationeers has a `DamageState` property with these writable float fields:

| Field | Type | Description |
|---|---|---|
| `Brute` | float | Physical/structural damage (impact, overpressure) |
| `Burn` | float | Thermal/electrical damage (cable overloads, fires) |
| `Oxygen` | float | Oxygen-related damage |
| `Hydration` | float | Hydration-related damage |
| `Starvation` | float | Starvation damage |
| `Toxic` | float | Toxic damage (pollutants) |
| `Radiation` | float | Radiation damage |
| `Stun` | float | Stun damage (incapacitation) |
| `Decay` | float | Decay/rot damage |
| `MaxDamage` | float | Maximum damage threshold |

## DamageState vs Atmosphere vs vital stat disambiguation
<!-- verified: 0.2.6228.27061 @ 2026-04-20 -->

The XML contains `<Oxygen>`, `<Hydration>`, etc. in TWO completely different contexts:

1. **Inside `<DamageState>` blocks** (on Things) -- these ARE damage values, safe to zero out:
   ```xml
   <DamageState>
     <Brute>20.70604</Brute>
     <Burn>0</Burn>
     <Oxygen>0</Oxygen>
     <Hydration>0</Hydration>
     <Starvation>0</Starvation>
     <Toxic>0</Toxic>
     <Radiation>0</Radiation>
     <Stun>0</Stun>
     <Decay>0</Decay>
   </DamageState>
   ```

2. **Inside `<AtmosphereSaveData>` blocks** -- these are GAS AMOUNTS in rooms, NOT damage:
   ```xml
   <AtmosphereSaveData>
     <Oxygen>318.45480094784614</Oxygen>
     <Nitrogen>0</Nitrogen>
     <CarbonDioxide>197.48193765921076</CarbonDioxide>
     ...
   </AtmosphereSaveData>
   ```

3. **On Player entities** -- `<Hydration>5.65211868</Hydration>` is a VITAL STAT (how thirsty), not damage.

**A naive regex that zeroes all `<Oxygen>` tags will remove breathable air from rooms and kill players.** The correct approach uses a context-aware parser (e.g., Python regex with `re.DOTALL` matching inside `<DamageState>...</DamageState>` blocks only):

```python
import re
def zero_damage(match):
    block = match.group(0)
    block = re.sub(
        r'<(Brute|Burn|Oxygen|Hydration|Starvation|Toxic|Radiation|Stun|Decay)>[^<]+</\1>',
        r'<\1>0</\1>', block)
    return block
content = re.sub(r'<DamageState>.*?</DamageState>', zero_damage, content, flags=re.DOTALL)
```

## Typical observed values
<!-- verified: 0.2.6228.27061 @ 2026-04-20 -->

Distribution of non-zero damage in a real save (118 values): `StructureWallIron` Brute ~25 (10-27 range), `StructureCompositeWindowIron` Brute ~15 (11-22), cable corners/straights/junctions Burn ~6 (5-9), pipe corner/straight Brute 2 (37-58), `OrganLungs` Burn 1 (20.6), player entities Stun 2 (100, unconscious), player entities Burn 1 (13.8). Establishes realistic scale of minor-damage cleanup scenario.

## Verification history
<!-- verified: 0.2.6228.27061 @ 2026-04-20 -->

- 2026-04-20: page created from the Research migration; verbatim content lifted from F0082, F0083, F0084, F0221, F0222, and F0229a.

## Open questions

None at creation.
