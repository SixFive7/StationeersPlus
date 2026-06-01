---
title: Breathing and Oxygenation
type: GameSystems
created_in: 0.2.6228.27061
verified_in: 0.2.6228.27061
verified_at: 2026-06-01
sources:
  - $(StationeersPath)\rocketstation_Data\Managed\Assembly-CSharp.dll :: Assets.Scripts.Objects.Entities.Entity
  - $(StationeersPath)\rocketstation_Data\Managed\Assembly-CSharp.dll :: Assets.Scripts.Objects.Entities.Human
  - $(StationeersPath)\rocketstation_Data\Managed\Assembly-CSharp.dll :: Assets.Scripts.Objects.Entities.Lungs
  - $(StationeersPath)\rocketstation_Data\Managed\Assembly-CSharp.dll :: Chemistry
related:
  - ../GameClasses/Entity.md
  - ../GameClasses/Human.md
  - ../GameClasses/Atmosphere.md
tags: [entity, damage]
---

# Breathing and Oxygenation

The breathing system governs how humans extract oxygen from breathed atmosphere and how oxygenation (a 0-100 vital stat) changes per breath and decays between breaths. This page documents the exact mechanics governing which atmospheres support healthy breathing, the minimum O2 partial pressure, the role of carbon dioxide and toxic gases, and how N2O (nitrous oxide) causes stun damage.

## Oxygenation range and vital signs

<!-- verified: 0.2.6228.27061 @ 2026-06-01 -->

`Oxygenation` is a float stat tracking oxygen availability. Range is 0 to 100 (or higher).

Thresholds (from Entity class at line 283419-283421):
- `WarningOxygen = 1f` (1.0 out of 100, displayed as 1%)
- `CriticalOxygen = 0.75f` (0.75 out of 100, displayed as 0.75%)

A player is in the "Warning" state if `OxygenQuality < 1.0` and "Critical" if `OxygenQuality < 0.75`. When oxygenation reaches 0, the player takes both Oxygen damage and Stun damage per tick (line 321993-321994).

Note: `OxygenQuality` is normalized to 0-1 range (or higher) per breath; `Oxygenation` is the accumulated vital stat.

## How a breath works: intake, O2 quality, and oxygenation gain

<!-- verified: 0.2.6228.27061 @ 2026-06-01 -->

Source: Entity.TakeBreath() at lines 284513-284517, and Human.TakeBreath() override at lines 342197-342219.

### Breath volume calculation

Per breath, the player attempts to inhale:

`csharp
MoleQuantity moleQuantity = new MoleQuantity(
    0.0048f * BreathingEfficiency * (float)DifficultySetting.Current.BreathingRate
);
`

Constants and defaults:
- `0.0048f` moles is the base mole count per breath (ENTITY_MOLE_PER_BREATH constant, line 283415).
- `BreathingEfficiency` (0 to 1.5, clamped) is derived from O2 partial pressure in the lungs (see below).
- `DifficultySetting.Current.BreathingRate` default is `2f` (FloatReference at line 55073). This is a multiplier; higher difficulty = faster breathing = more moles per breath.

The actual moles extracted depend on what lungs contain:

`csharp
MoleQuantity moleQuantity2 = OrganLungs?.TakeBreath(BreathingAtmosphere, moleQuantity) 
    ?? MoleQuantity.Zero;
`

The `Lungs.TakeBreath()` method (line 330151-330157) extracts only what is available:

`csharp
public MoleQuantity TakeBreath(Atmosphere breathingAtmosphere, MoleQuantity molesPerBreath)
{
    MoleQuantity moleQuantity = RocketMath.Min(
        molesPerBreath, 
        base.InternalAtmosphere.GasMixture.Oxygen.Quantity * BreathingEfficiency
    );
    breathingAtmosphere.GasMixture.CarbonDioxide.AddTwentyDegreeC(
        base.InternalAtmosphere.GasMixture.Oxygen.Remove(moleQuantity).Quantity * 0.5
    );
    base.InternalAtmosphere.GasMixture.AddEnergy(Entity.EnergyReleasedPerTick);
    return moleQuantity;
}
`

The actual O2 extracted is `min(molesPerBreath, LungsOxygen * BreathingEfficiency)`. The lungs then exhale CO2 at 50% of the O2 removed (line 330154).

### OxygenQuality calculation

After extracting `moleQuantity2`, the game computes:

`csharp
base.OxygenQuality = (moleQuantity2 / 0.004800000227987766 / (float)DifficultySetting.Current.BreathingRate).ToFloat();
`

Simplifying:
`OxygenQuality = (moles_actually_extracted / 0.0048 / BreathingRate)`

When `BreathingRate = 2f` (default):
`OxygenQuality = moles_actually_extracted / 0.0096`

If a player extracts the full 0.0048 moles per breath with BreathingRate=2, then:
`OxygenQuality = 0.0048 / 0.0048 / 2 = 0.5 (50%)`

`OxygenQuality` directly reflects the fraction of a full, efficient breath actually taken.

### Oxygenation gain per breath

After calculating `OxygenQuality`, oxygenation increases:

`csharp
base.Oxygenation += (moleQuantity2 / (float)DifficultySetting.Current.BreathingRate).ToFloat();
`

With `BreathingRate = 2f`:
`Oxygenation += moles_actually_extracted / 2`

So a full breath (0.0048 moles at BreathingRate=2) yields:
`Oxygenation += 0.0048 / 2 = 0.0024`

A player breathing perfect air at default difficulty gains 0.0024 oxygenation per breath.

## Breathing Efficiency: how O2 partial pressure affects breath quality

<!-- verified: 0.2.6228.27061 @ 2026-06-01 -->

Source: Entity.BreathingEfficiency property (lines 283598-283607), and Lungs.AtmosphericEfficiency property (lines 330067, 330073).

`BreathingEfficiency` is the product of two factors:

`csharp
private float BreathingEfficiency => AtmosphericEfficiency * DamageEfficiency;
`

### AtmosphericEfficiency: O2 partial pressure ratio

`csharp
public virtual float AtmosphericEfficiency => 
    Mathf.Clamp(
        (base.InternalAtmosphere.PartialPressureO2 / Chemistry.MinimumOxygenPartialPressure).ToFloat(),
        0f,
        1.5f
    );
`

**Minimum O2 partial pressure for any breathing efficiency:**

`Chemistry.MinimumOxygenPartialPressure = 16.0 kPa` (PressurekPa at line 418990)

**Efficiency calculation:**

`AtmosphericEfficiency = Clamp(PartialPressureO2 / 16.0, 0, 1.5)`

Examples:
- At 0 kPa O2: Efficiency = 0 (cannot breathe).
- At 16 kPa O2 (minimum): Efficiency = 1.0 (100% efficiency, a full breath).
- At 32 kPa O2 (Earth normal): Efficiency = 2.0, clamped to 1.5.
- At 8 kPa O2 (half minimum): Efficiency = 0.5 (50% efficiency, half a breath).

**Critical takeaway:** A human cannot breathe at all if O2 partial pressure is below 16 kPa. Between 16-24 kPa, efficiency ramps up linearly. Above 24 kPa (1.5x minimum), the lungs cannot extract more oxygen (clamped).

### DamageEfficiency: lung damage reduces breathing

`csharp
public float DamageEfficiency => Mathf.Max(0f, 100f - DamageState.Total) / 100f;
`

Lung damage total ranges 0-100. At 0 damage, DamageEfficiency = 1.0. At 50 damage, DamageEfficiency = 0.5. At 100 damage, DamageEfficiency = 0.0 (cannot breathe).

### Total BreathingEfficiency

`BreathingEfficiency = AtmosphericEfficiency * DamageEfficiency`

A human in an atmosphere with 16 kPa O2 with 50 lung damage:
`BreathingEfficiency = 1.0 * 0.5 = 0.5`

Each breath extracts only half the normal moles.

## Total pressure and inert gases do not directly affect breathing

<!-- verified: 0.2.6228.27061 @ 2026-06-01 -->

Source: Lungs.BreathingEfficiency calculation (line 330073) and Lungs.AtmosphericEfficiency (lines 330067, 330073).

The efficiency calculation depends **only** on `PartialPressureO2 / MinimumOxygenPartialPressure`. It does not reference total pressure, nitrogen, or any inert gas.

Therefore:
- An atmosphere at 16 kPa O2 + 1 kPa N2 (total 17 kPa) breathes as well as 16 kPa O2 in a vacuum.
- An atmosphere at 16 kPa O2 + 100 kPa N2 (total 116 kPa) breathes identically.
- Nitrogen is inert to the breathing calculation; only the O2 partial pressure matters.

**Pressure sickness from high total pressure** is handled separately (not part of breathing; see Armstrong Limit).

## Oxygenation decay between breaths

<!-- verified: 0.2.6228.27061 @ 2026-06-01 -->

Source: Line 321990 (brain organ on-life-tick handler).

Every tick, oxygenation decays:

`csharp
ParentHuman.Oxygenation -= 0.0015840002f * ((IsOnline || flag2 || flag) ? 1f : ((float)DifficultySetting.Current.OfflineMetabolism));
`

**Decay rate per tick:**
- Online (player connected or in party): `0.001584` per tick.
- Offline (body simulated without player): `0.001584 * OfflineMetabolism`. Default `OfflineMetabolism = 0.1f`, so offline decay is `0.0001584` per tick.

With 60 ticks per second, online decay is ~0.095 oxygenation per second, or ~5.7 per minute.

With a full breath yielding ~0.0024 oxygenation at default difficulty, a player must take ~7.5 breaths per second to stay even. At realistic breathing rates (12-20 breaths per minute), oxygenation will decay significantly unless the player is in high-O2 atmosphere.

## Carbon dioxide and toxic-gas effects

<!-- verified: 0.2.6228.27061 @ 2026-06-01 -->

Source: Lungs.OnLifeTick() at lines 330083-330149, and line 330154 (TakeBreath exhale).

### CO2 exhaled per breath

When lungs extract `moleQuantity` of O2, they add CO2 to the breathing atmosphere at 50% the molar rate:

`csharp
breathingAtmosphere.GasMixture.CarbonDioxide.AddTwentyDegreeC(
    base.InternalAtmosphere.GasMixture.Oxygen.Remove(moleQuantity).Quantity * 0.5
);
`

So extracting 0.002 moles of O2 adds 0.001 moles of CO2 to the breathed air.

### CO2 does NOT directly reduce oxygen uptake

The breathing calculation (line 330153) is:

`csharp
MoleQuantity moleQuantity = RocketMath.Min(
    molesPerBreath, 
    base.InternalAtmosphere.GasMixture.Oxygen.Quantity * BreathingEfficiency
);
`

It depends only on lung O2 and breathing efficiency. CO2 in the lungs is not checked. High CO2 does not reduce the amount of O2 extracted per breath.

### Methane and Pollutant toxicity cause lung damage

Source: Lungs.OnLifeTick() at lines 330090-330104.

Each tick, lungs check `ToxinLevel`, defined for humans as `PartialPressureHumanToxins` (methane + pollutant).

Thresholds:
- `Entity.ToxicPartialPressureForDamage = 1.0 kPa` (line 283449).
- `Entity.ToxicPartialPressureForWarning = 0.5 kPa` (line 283451).

If `ToxinLevel > 1.0 kPa`:
`csharp
float num2 = Mathf.Min(toxinLevel.ToFloat() * 0.2f, b);
DamageState.Damage(ChangeDamageType.Increment, num2, DamageUpdateType.Toxic);
`

Lung toxic damage increases at 20% of the partial pressure per tick. At 5 kPa toxins, lungs take 1.0 damage per tick.

If `ToxinLevel < 1.0 kPa but > 0f`, lungs slowly heal toxic damage (0.1 per tick).

### Breath quality degrades with toxins

Toxins reduce BreathingEfficiency -> fewer moles extracted -> lower OxygenQuality per breath. But the mechanism is indirect (via DamageState, not via direct toxin-gas interaction in the breath formula).

## N2O (nitrous oxide) and stun damage

<!-- verified: 0.2.6228.27061 @ 2026-06-01 -->

Source: Human.N2OExceedsSafeLimit() at lines 342231-342245, and Human.SafeN2OPartialPressureHuman at line 339919.

### Safe N2O limit for humans

`SafeN2OPartialPressureHuman = 5.0 kPa` (PressurekPa)

Constants:
`csharp
public const float N2O_PARTIAL_PRESSURE_HUMAN = 5f;
public static PressurekPa SafeN2OPartialPressureHuman = new PressurekPa(5.0);
`

### Stun damage from excessive N2O

When `LungAtmosphere.PartialPressureNitrousOxide > 5.0 kPa` for humans:

`csharp
stunDamage = LungAtmosphere.PartialPressureNitrousOxide.ToFloat() / 5f;
return stunDamage > 1f;
`

Stun damage is applied per tick:

`csharp
float num = ((this is Npc || OrganBrain.IsOnline) ? 2f : (2f * Mathf.Clamp(DifficultySetting.Current.OfflineMetabolism, 0.1f, 1f)));
DamageState.Damage(ChangeDamageType.Increment, stunDamage * num, DamageUpdateType.Stun);
`

**Examples:**
- At 6 kPa N2O: stunDamage = 6/5 = 1.2. Online stun damage = 1.2 * 2 = 2.4 per tick.
- At 10 kPa N2O: stunDamage = 10/5 = 2.0. Online stun damage = 2.0 * 2 = 4.0 per tick.
- At 5 kPa N2O (exactly the limit): stunDamage = 5/5 = 1.0, not > 1, so no stun damage triggered.

### N2O does NOT reduce oxygen uptake

Like CO2, N2O in the lungs does not appear in the breathing efficiency or mole-extraction formulas. Only the stun damage mechanic is affected.

## Initialization: starting oxygenation

<!-- verified: 0.2.6228.27061 @ 2026-06-01 -->

Source: Lines 342069, 342192.

When a human is created or respawned:

`csharp
base.Oxygenation = 0.024f;
`

A new player starts at 0.024 oxygenation, near the critical threshold of 0.75.

## Summary: maintaining healthy oxygenation

To keep a human healthy:

1. **Atmosphere must have at least 16 kPa O2 partial pressure.** Below this, breathing efficiency falls to 0.
2. **At 16-24 kPa O2, efficiency ramps linearly.** At 24+ kPa, efficiency caps at 1.5.
3. **Oxygenation decays ~0.001584 per tick (online).** With default BreathingRate=2, each good breath yields ~0.0024 oxygenation.
4. **Total pressure and nitrogen are irrelevant** to breathing; only O2 partial pressure matters.
5. **Carbon dioxide accumulates from exhalation but does not chemically reduce O2 uptake.**
6. **Methane and Pollutant above 1 kPa cause lung damage**, reducing BreathingEfficiency.
7. **N2O above 5 kPa causes stun damage**, independent of oxygenation.

## Verification history

<!-- verified: 0.2.6228.27061 @ 2026-06-01 -->

- 2026-06-01: page created from scratch, verified against decompiled Assembly-CSharp.dll in game version 0.2.6228.27061. All formulas, constants, and mechanical interactions extracted from lines 284513-284527 (Entity base breathing), 342197-342219 (Human TakeBreath), 330151-330157 (Lungs.TakeBreath), 330083-330149 (Lungs.OnLifeTick), 330067 (AtmosphericEfficiency), 283598-283607 (Entity.BreathingEfficiency), 321990 (oxygenation decay), 342231-342245 (N2O damage), 283415-283421 (vital thresholds), 418990 (MinimumOxygenPartialPressure = 16 kPa), 339919-339921 (SafeN2OPartialPressureHuman = 5 kPa), 283449-283451 (ToxicPartialPressure thresholds).

## Open questions

None at creation.
