---
title: Sleeper (OccupantAtmospherics)
type: GameClasses
created_in: 0.2.6228.27061
verified_in: 0.2.6228.27061
verified_at: 2026-06-01
sources:
  - $(StationeersPath)\rocketstation_Data\Managed\Assembly-CSharp.dll :: Assets.Scripts.Objects.Items.Sleeper
  - $(StationeersPath)\rocketstation_Data\Managed\Assembly-CSharp.dll :: Assets.Scripts.Objects.Items.OccupantAtmospherics
  - $(StationeersPath)\rocketstation_Data\Managed\Assembly-CSharp.dll :: Assets.Scripts.Objects.Items.SpawnPointAtmospherics
  - $(StationeersPath)\rocketstation_Data\Managed\Assembly-CSharp.dll :: Assets.Scripts.Objects.Thing.SetWorldAtmosphere
related:
  - ../GameSystems/BreathingAndOxygenation.md
  - ../GameSystems/StunStateMachine.md
  - ILifeSuspender.md
tags: [entity, slots, damage]
---

# Sleeper (OccupantAtmospherics)

The Sleeper is the device a player lies in to safely log out. This page documents its atmosphere subsystem: where the occupant's breathed air actually comes from, the device's safe-atmosphere check, its passive heater, and the conditions under which it suspends metabolism. The sleep/stun mechanic itself (how the occupant is put and held unconscious) lives in [StunStateMachine.md](../GameSystems/StunStateMachine.md); the breathing/oxygenation math lives in [BreathingAndOxygenation.md](../GameSystems/BreathingAndOxygenation.md).

Class hierarchy: `Sleeper : OccupantAtmospherics : SpawnPointAtmospherics : DeviceInputOutput`. `CryoTube` is a sibling (`CryoTube : OccupantAtmospherics, ILifeSuspender, ICryogenicRegenerator`) that adds a separate coolant loop; see Open Questions for what was not traced here.

## The occupant breathes the piped-in internal atmosphere, not the room

<!-- verified: 0.2.6228.27061 @ 2026-06-01 -->

This is the central and most misread fact. A human in a Sleeper does NOT breathe the room the sleeper sits in. They breathe the device's internal atmosphere, which is the gas on the connected pipe network.

The mechanism is `Thing.SetWorldAtmosphere()` (line 281183):

```csharp
public void SetWorldAtmosphere()
{
    Slot parentSlot = ParentSlot;
    if (parentSlot != null)
    {
        if (parentSlot.UseInternalAtmosphere)
        {
            WorldAtmosphere = parentSlot.Parent.InternalAtmosphere;
            return;
        }
        if ((bool)parentSlot.Parent.AsDynamicThing)
        {
            WorldAtmosphere = parentSlot.Parent.AsDynamicThing.WorldAtmosphere;
            return;
        }
        Thing thing = parentSlot.Parent?.RootParent ?? this;
        WorldAtmosphere = base.GridController.AtmosphericsController.SampleGlobalAtmosphere(thing.WorldGrid);
        return;
    }
    WorldAtmosphere = base.GridController.AtmosphericsController.SampleGlobalAtmosphere(base.WorldGrid);
    ...
}
```

When the occupant sits in a slot whose `UseInternalAtmosphere` flag is set (the sleeper's bed slot), the occupant's `WorldAtmosphere` is bound to `parentSlot.Parent.InternalAtmosphere`, i.e. the sleeper's internal atmosphere. A human with no sealed-suit internals reads that as its breathing atmosphere (`Human.BreathingAtmosphere` at line 340241 returns `base.BreathingAtmosphere` -> `Entity.BreathingAtmosphere` at 283661 -> `base.WorldAtmosphere`). See [BreathingAndOxygenation.md](../GameSystems/BreathingAndOxygenation.md) "Which atmosphere is breathed".

The device confirms this in its own UI: the info-panel tooltip (line 399496-399497) prints the `GameStrings.BreathingAtmosphere` label (localized "Occupant Atmosphere") and renders the gas contents of `base.InternalAtmosphere`:

```csharp
stringBuilder.AppendLine(GameStrings.BreathingAtmosphere.AsColor("white"));
AtmosphericsManager.MakeGasTooltip(base.InternalAtmosphere, stringBuilder, indent: true);
```

Consequence: to keep a sleeping occupant healthy you pipe a breathable atmosphere into the sleeper's gas input. The room around the sleeper is irrelevant while the door is closed.

Door open exception: `SpawnPointAtmospherics.OnAtmosphericTick` (line 401387) mixes the internal atmosphere with `GetWorldAtmosphere()` (the room) instead of the pipe when `IsOpen`, and an open sleeper drops to the Inactive mode and shows the red `SleeperDoorIsOpen` string (399513-399520). A closed, powered sleeper uses the pipe.

## Internal atmosphere volume and pipe wiring

<!-- verified: 0.2.6228.27061 @ 2026-06-01 -->

`Sleeper.InitInternalAtmosphere()` (line 399681) creates an 800 litre internal atmosphere:

```csharp
public override void InitInternalAtmosphere()
{
    if (base.InternalAtmosphere == null)
    {
        base.InternalAtmosphere = new Atmosphere(this, new VolumeLitres(800.0), 0L);
    }
}
```

The base `SpawnPointAtmospherics.InitInternalAtmosphere()` (line 401100) defaults to 10 litres; the Sleeper overrides to 800.

Each atmospheric tick (`SpawnPointAtmospherics.OnAtmosphericTick`, line 401381) the internal atmosphere is mixed toward the input network's atmosphere (gas), and liquids are drained from the pipe:

```csharp
if (base.InternalAtmosphere == null || InputNetwork?.Atmosphere == null)
{
    return;
}
AtmosphereHelper.Mix(base.InternalAtmosphere, IsOpen ? GetWorldAtmosphere() : InputNetwork.Atmosphere, AtmosphereHelper.MatterState.Gas);
if (!IsOpen)
{
    AtmosphereHelper.DrainLiquids(base.InternalAtmosphere, InputNetwork.Atmosphere, Chemistry.PipeVolume);
}
```

`Sleeper.AssessError()` (line 399702) sets `OutputNetwork = InputNetwork`, so the device sits on a single connected pipe network rather than pumping from a distinct input to a distinct output. A missing pipe yields the red `CryoNoAtmosphericInput` string (399521-399527).

## Safe-atmosphere check (IsUnsafeAtmosphere)

<!-- verified: 0.2.6228.27061 @ 2026-06-01 -->

`OccupantAtmospherics` holds the device limits (lines 399447-399449):

```csharp
private TemperatureKelvin _minimumTemperature = new TemperatureKelvin(263.15);  // -10 C
private PressurekPa _minimumPressure = new PressurekPa(30.0);                   // 30 kPa
```

`IsUnsafeAtmosphere` (lines 399457-399478) evaluates the device's own internal atmosphere:

```csharp
public bool IsUnsafeAtmosphere
{
    get
    {
        if (base.InternalAtmosphere == null) return true;
        if (base.InternalAtmosphere.PressureGassesAndLiquids < _minimumPressure) return true;   // < 30 kPa
        if (base.InternalAtmosphere.Temperature > Chemistry.Temperature.FiftyDegrees) return true; // > 50 C (323.15 K)
        // ... (intervening lines)
        if (base.InternalAtmosphere.Temperature < _minimumTemperature) return true;             // < -10 C (263.15 K)
        return false;
    }
}
```

So the device considers its internal (piped) atmosphere unsafe if pressure is below 30 kPa or temperature is outside the -10 C to +50 C band. An unsafe atmosphere yields the red `CryoAtmosphereIsUnsafe` string (399529-399535) and flips the device to the Error mode (`OnServerTick`, line 399596).

Important gap: `IsUnsafeAtmosphere` does NOT check oxygen content. A nitrogen-only atmosphere at 50 kPa and 20 C passes the device's safe check, but the occupant's lungs still need at least 16 kPa O2 partial pressure (`Chemistry.MinimumOxygenPartialPressure`) to refill oxygenation. The green device light is necessary but not sufficient for occupant health; the player must ensure O2 themselves. See [BreathingAndOxygenation.md](../GameSystems/BreathingAndOxygenation.md).

## Passive heater toward 24 C

<!-- verified: 0.2.6228.27061 @ 2026-06-01 -->

`SpawnPointAtmospherics` defines `_maxHeatKelvin = 297.15 K` (24 C, line 401094). At the end of `OnAtmosphericTick` (lines 401416-401421) the device adds heat to its internal atmosphere up to that target, but only when the atmosphere is above the Armstrong limit and currently below 24 C:

```csharp
if (base.InternalAtmosphere != null && base.InternalAtmosphere.IsAboveArmstrong() && base.InternalAtmosphere.GasMixture.Temperature <= _maxHeatKelvin && _maxHeatKelvin - base.InternalAtmosphere.GasMixture.Temperature > TemperatureKelvin.Zero)
{
    MoleEnergy val = IdealGas.Energy(base.InternalAtmosphere.GasMixture.HeatCapacity, _maxHeatKelvin - base.InternalAtmosphere.GasMixture.Temperature);
    base.InternalAtmosphere.GasMixture.AddEnergy(RocketMath.Min(MaxEnergyPull, val));
    _powerUsedDuringTick = RocketMath.Min(MaxEnergyPull, val).ToFloat();
}
```

`MaxEnergyPull = 250 MoleEnergy` (line 401084) caps heating per tick. The device warms cold piped gas toward 24 C but does not cool hot gas, so piping in atmosphere hotter than 50 C trips `IsUnsafeAtmosphere` with no relief.

## Operating conditions and metabolic suspension

<!-- verified: 0.2.6228.27061 @ 2026-06-01 -->

`Sleeper.IsSuspendingLife => Powered` (line 399670). The `ILifeSuspender` contract is what skips the occupant's nutrition, hydration, mood, and hygiene ticks while suspended (see [BreathingAndOxygenation.md](../GameSystems/BreathingAndOxygenation.md) and StunStateMachine for the life-tick short-circuit). Breathing and organ damage still run, so a suspended occupant can still suffocate, freeze, cook, or be poisoned through the piped atmosphere.

`Sleeper.IsOperable` (lines 399641-399645) gates the green "metabolic suspension" state on both a valid input and a safe atmosphere:

```csharp
bool flag = base.IsInputValid && !base.IsUnsafeAtmosphere;
```

`OnServerTick` (line 399596) maps the device to one of Standby / Error / Inactive / Occupied / Dead based on power, door state, `IsUnsafeAtmosphere`, and occupant presence/death.

The stun handling that puts and keeps the occupant asleep (STUN_PER_TICK = 10, STUN_MAX = 100, lines 401086-401088, 401396-401414) is in `SpawnPointAtmospherics.OnAtmosphericTick`; full detail in [StunStateMachine.md](../GameSystems/StunStateMachine.md).

## Practical: optimal atmosphere to pipe into a Sleeper

<!-- verified: 0.2.6228.27061 @ 2026-06-01 -->

Hard requirements that must all hold in the piped (internal) atmosphere:

- Total pressure at least 30 kPa (device `_minimumPressure`). Stay below `Chemistry.Limits.PressureMaximumSafe` (607.95 kPa).
- Oxygen partial pressure at least 16 kPa (`Chemistry.MinimumOxygenPartialPressure`) for full breathing efficiency; the device does not enforce this, the lungs do.
- Temperature between -10 C and +50 C (device `IsUnsafeAtmosphere`, and the human lung damage band is the same -10 C to +50 C). The device heats toward 24 C.
- Methane + Pollutant partial pressure below 0.5 kPa (warning) / 1.0 kPa (lung toxic damage). NitrousOxide below 5 kPa (stun damage).

Robust optimum: standard breathable station air, about 100-101 kPa total, roughly 21 kPa O2 partial pressure with the balance nitrogen, at 20-24 C, free of pollutants. Nitrogen is inert filler that contributes pressure without aiding breathing, but the pressure and temperature must sit inside the bands above. A minimal pure-O2 fill of about 50 kPa at 20-24 C also works (it clears both the 30 kPa device floor and the 16 kPa O2 floor with margin); keep it replenished because the occupant slowly consumes O2 and exhales CO2 into the closed internal volume.

## Verification history

<!-- verified: 0.2.6228.27061 @ 2026-06-01 -->

- 2026-06-01: page created from decompiled Assembly-CSharp.dll, game version 0.2.6228.27061. Source lines: Sleeper class 399619-399713 (InitInternalAtmosphere 800 L at 399681, IsOperable 399641, IsSuspendingLife 399670, AssessError/OutputNetwork 399699-399713), OccupantAtmospherics 399413-399618 (limits 399447-399449, IsUnsafeAtmosphere 399457-399478, tooltip "Occupant Atmosphere" 399496-399497, error strings 399503-399537, OnServerTick mode mapping 399592-399601), SpawnPointAtmospherics 401082-401423 (InitInternalAtmosphere 10 L at 401100, stun constants 401086-401088, OnAtmosphericTick mix/stun/heater 401381-401423, _maxHeatKelvin 24 C at 401094), Thing.SetWorldAtmosphere 281183-281202, Human.BreathingAtmosphere 340241-340262. Corrects an earlier sub-agent claim that a sleeper occupant breathes the room: the bed slot is a `UseInternalAtmosphere` slot, so the occupant breathes the device internal (piped) atmosphere.

## Open questions

- The bed slot's `UseInternalAtmosphere == true` is inferred from the coherent behavior of the whole subsystem (internal 800 L atmosphere, IsUnsafeAtmosphere evaluating it, the "Occupant Atmosphere" tooltip rendering it) plus `SetWorldAtmosphere`'s slot branch, rather than read directly off the prefab slot definition. In-game verification via InspectorPlus (snapshot the occupant's `WorldAtmosphere` reference and compare to the sleeper's `InternalAtmosphere`) would close this.
- CryoTube specifics (coolant loop via `InputNetwork2`, `ICryogenicRegenerator` regeneration behavior, its internal atmosphere volume) were not traced; only that it shares the `OccupantAtmospherics` base and `ILifeSuspender` contract.
