---
title: Entity Hydration and Needs
type: GameSystems
created_in: 0.2.6403.27689
verified_in: 0.2.6403.27689
verified_at: 2026-07-06
sources:
  - .work/decomp/0.2.6403.27689/Assembly-CSharp.Entity.decompiled.cs :: Entity.Hydrate / Hydration / HydrationRatio / GetHydrationStorage
  - .work/decomp/0.2.6403.27689/Assembly-CSharp.Human.decompiled.cs :: Human.Hydrate / OrganStomach / CanDrink
  - .work/decomp/0.2.6403.27689/Assembly-CSharp.decompiled.cs :: Entity.Hydrate (line 302750), Entity.Hydration (302102), GetHydrationStorage (302858), Human.Hydrate (363073), StructureDrinkingFountain.HandleActivate (147743), HydrationBase (346856) / OnUseItem (346903), Mole ctor (446287)
related:
  - ./CreativeModeAndDifficulty.md
  - ../Workflows/TimeSkipWorldManipulation.md
tags: [entity]
---

# Entity Hydration and Needs

`Entity` carries the player "needs" stats (Hydration, Nutrition, Hygiene, Sanitation) as direct members; the Sanitation update did not move them into a separate needs/component object. What the Sanitation update DID change is the `Hydrate` method: the old `void Hydrate(float)` overload was removed and replaced by `void Hydrate(Mole)`. Any code that still calls `Entity.Hydrate(float)` throws `MissingMethodException: Method not found: void Assets.Scripts.Objects.Entity.Hydrate(single)` at JIT time (so it fires on every invocation, not only when the drink actually happens).

## The Hydrate signature change (Sanitation update)
<!-- verified: 0.2.6403.27689 @ 2026-07-06 -->

`Hydrate(float)` no longer exists. The only two `Hydrate` definitions in the entire assembly are the `Mole` overloads below.

`Assets.Scripts.Objects.Entity.Hydrate` (whole-assembly line 302750):

```csharp
public virtual void Hydrate(Mole water)
{
    Hydration += water.Quantity.ToFloat() * HydrationBase.HydrationPerMole;
    Achievements.AssessSomeHighQualityH2O(this);
}
```

`Assets.Scripts.Objects.Entities.Human.Hydrate` override (line 363073) also pushes the water into the stomach atmosphere:

```csharp
public override void Hydrate(Mole water)
{
    base.Hydrate(water);
    if (!(OrganStomach == null) && OrganStomach.InternalAtmosphere != null && water.Quantity > MoleQuantity.Zero)
    {
        AtmosphericEventInstance.CreateAdd(OrganStomach.InternalAtmosphere, new GasMixture(water));
    }
}
```

`Human` also exposes `public Stomach OrganStomach;` and `public override bool CanDrink()` (a helmet/suit gate). Vanilla drink paths call `CanDrink()` before hydrating; a suit-internal drink system may intentionally skip that check.

`Mole` is a `struct` in `Assets.Scripts.Atmospherics`. Constructor: `public Mole(Chemistry.GasType gasType, MoleQuantity quantity, MoleEnergy energy)`; property `public MoleQuantity Quantity { get; }`; static `Mole.SpecificHeat(Chemistry.GasType)`. `MoleQuantity` (readonly struct, same namespace): `new MoleQuantity(double)`, `.ToFloat()`, `MoleQuantity.Zero`.

## Needs members on Entity
<!-- verified: 0.2.6403.27689 @ 2026-07-06 -->

These are UNCHANGED by the Sanitation update (still readable/writable exactly as before):

| Member | Signature | Notes |
|---|---|---|
| `Hydration` | `public float Hydration { get; set; }` | Backing `_hydration`; setter clamps `Mathf.Clamp(value, 0f, 8.75f)`. |
| `HydrationRatio` | `public float HydrationRatio => Hydration / 5f;` | |
| `GetHydrationStorage()` | `public float GetHydrationStorage()` → `return 5f * GetFoodQualityMultiplier();` | Returns `float`. |
| `Nutrition` | `public float Nutrition` | |
| `NutritionRatio` | `public float NutritionRatio` | |
| `GetNutritionStorage()` | `public float GetNutritionStorage()` | `Human` overrides `BaseNutritionStorage => 50f`. |
| `Hygiene` | `public float Hygiene` | |
| `HygieneRatio` | `public float HygieneRatio` | |
| `SanitationRatio` | `public float SanitationRatio { get; protected set; }` | Added by the Sanitation update. |

Because `Hydration` is a plain clamped `float` property, `entity.Hydration += amount;` still compiles and works; only the `Hydrate(float)` METHOD was removed.

## Hydration conversion constants (HydrationBase)
<!-- verified: 0.2.6403.27689 @ 2026-07-06 -->

`Assets.Scripts.Objects.Items.HydrationBase` (static members, line 346856):

```csharp
public static float        HydrationPerLitreWater      => 5f;
public static MoleQuantity WaterMolesPerUnitHydration  => new MoleQuantity(55.55555555555556 / HydrationPerLitreWater); // ~= 11.111 mol
public static float        HydrationPerMole            => 1f / WaterMolesPerUnitHydration.ToFloat();                    // ~= 0.09
```

Derived facts:

- 1 mole of Water gas -> `+0.09` hydration.
- 55.556 mol of Water (1 litre) -> `+5` hydration.
- Hydration clamps to `[0, 8.75]`.

## Vanilla drink / hydrate flows
<!-- verified: 0.2.6403.27689 @ 2026-07-06 -->

`Objects.Structures.StructureDrinkingFountain.HandleActivate` (line 147743) is the closest analog to "drink water out of a stored atmosphere": it scales a `GasMixture` of the source water, removes those moles, then hydrates with the removed `Mole`:

```csharp
if (GameManager.RunSimulation)
{
    GasMixture gasMixture = new GasMixture(ConnectedPipeNetwork.Atmosphere.GasMixture.Water);
    gasMixture.Scale((val / gasMixture.GetTotalMolesLiquids).ToFloat());
    AtmosphericEventInstance.CreateRemove(ConnectedPipeNetwork.Atmosphere, gasMixture);
    human.Hydrate(gasMixture.Water);   // gasMixture.Water is a Mole
}
```

Its serving size: `Mathf.Max(0f, consumer.GetHydrationStorage() - consumer.Hydration) / 5f`.

`HydrationBase.OnUseItem` (line 346903) is the canonical "build a Mole and hydrate" for drinkable items:

```csharp
public override bool OnUseItem(float quantityToEat, Thing useOnThing)
{
    if (!(useOnThing is Human human)) return false;
    quantityToEat = Mathf.Min(quantityToEat, base.Quantity);
    MoleQuantity moleQuantity = new MoleQuantity((double)quantityToEat / 0.018); // kg water -> moles
    MoleEnergy energy = IdealGas.Energy(Chemistry.Temperature.TwentyDegrees,
                                        Mole.SpecificHeat(Chemistry.GasType.Water), moleQuantity);
    human.Hydrate(new Mole(Chemistry.GasType.Water, moleQuantity, energy));
    base.Quantity -= quantityToEat;
    OnStateChanged();
    return true;
}
```

## Adding hydration correctly (for mods)
<!-- verified: 0.2.6403.27689 @ 2026-07-06 -->

Three ways to replace an old `entity.Hydrate(amount)` call, in order of fidelity:

1. If you already removed water moles from a source atmosphere, pass a `Mole<Water>` straight in (mirrors the fountain; also feeds the stomach via `Human`'s override):

   ```csharp
   var moles  = new MoleQuantity(nMolesOfWaterRemoved);
   var energy = IdealGas.Energy(Chemistry.Temperature.TwentyDegrees,
                                Mole.SpecificHeat(Chemistry.GasType.Water), moles);
   entity.Hydrate(new Mole(Chemistry.GasType.Water, moles, energy));
   ```

2. If `amount` was a hydration-unit float and you just want the old effect, write the property directly (setter clamps 0..8.75). Skips the stomach-atmosphere add and the achievement:

   ```csharp
   entity.Hydration += amount;
   ```

3. If `amount` was hydration units but you want to route through `Hydrate`, convert units -> moles first:

   ```csharp
   double waterMoles = amount * HydrationBase.WaterMolesPerUnitHydration.ToFloat(); // == amount / HydrationBase.HydrationPerMole ~= amount * 11.111
   var moles  = new MoleQuantity(waterMoles);
   var energy = IdealGas.Energy(Chemistry.Temperature.TwentyDegrees, Mole.SpecificHeat(Chemistry.GasType.Water), moles);
   entity.Hydrate(new Mole(Chemistry.GasType.Water, moles, energy));
   ```

## Verification history
<!-- verified: 0.2.6403.27689 @ 2026-07-06 -->

- 2026-07-06: page created from a decompile read of `Assembly-CSharp` at game version 0.2.6403.27689 (`.work/decomp/0.2.6403.27689/`, targeted `Entity`/`Human` dumps plus the whole-assembly file). The `Hydrate(Mole)` signature (Entity + Human overrides), the `Hydration`/`HydrationRatio`/`GetHydrationStorage` members, the `HydrationBase` conversion constants, and the `StructureDrinkingFountain.HandleActivate` + `HydrationBase.OnUseItem` drink flows are verbatim from the decompile with line numbers. The removal of the `Hydrate(float)` overload is independently confirmed by a live `MissingMethodException: Method not found: void Assets.Scripts.Objects.Entity.Hydrate(single)` thrown by Marky's Suit Drink System (Workshop 3644610659) on this version.

## Open questions
<!-- verified: 0.2.6403.27689 @ 2026-07-06 -->

- Whether `GetHydrationStorage()` returning `5f * GetFoodQualityMultiplier()` while `HydrationRatio` divides by a hardcoded `5f` is intentional (ratio ignores the food-quality multiplier) or a game-side inconsistency. Not load-bearing for the drink calls above.
