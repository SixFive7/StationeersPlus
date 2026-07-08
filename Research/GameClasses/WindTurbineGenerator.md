---
title: WindTurbineGenerator
type: GameClasses
created_in: 0.2.6403.27689
verified_in: 0.2.6403.27689
verified_at: 2026-07-08
sources:
  - .work/decomp/0.2.6403.27689/Assembly-CSharp.decompiled.cs:146360 (LargeWindTurbineGenerator), 146848-147049 (WindTurbineGenerator), 205218 (GameManager.Update call site)
  - Mods/PowerGridPlus/PowerGridPlus/Patches/WindTurbineOutputLatchPatches.cs (consumer of these facts)
related:
  - ../GameSystems/PowerTickThreading.md
  - ../GameSystems/PowerTickThreading.md#generator-output-stability-census-mid-electricity-tick-mutability
tags: [power, threading]
---

# WindTurbineGenerator

The wind turbine producer (`Assets.Scripts.Objects.Electrical` namespace, decompile line 146848). Its distinguishing property among generators: the value it reports to the power tick is recomputed on every call from global wind state that the main thread rewrites every frame, so two reads of the same turbine within one electricity tick can disagree. Every other generator class returns a field that only mutates in `OnAtmosphericTick`, which runs sequentially with the electricity tick inside the same GameTick chain (see the census section in `PowerTickThreading.md`).

## Class shape and inheritance
<!-- verified: 0.2.6403.27689 @ 2026-07-08 -->

```csharp
// line 146848
public class WindTurbineGenerator : Device, ISmartRotatable, IPowered, IDensePoolable, IReferencable, IEvaluable, IPowerGenerator

// line 146360
public class LargeWindTurbineGenerator : WindTurbineGenerator
```

`LargeWindTurbineGenerator` (146360-146847) overrides only tuning members; it has NO `GetGeneratedPower` override (the first `GetGeneratedPower` override after its body starts is line 147040, which belongs to `WindTurbineGenerator` itself; verified by scanning every `public override float GetGeneratedPower` site in the decompile). A Harmony patch on `WindTurbineGenerator.GetGeneratedPower` therefore covers both the small and the large turbine.

## GetGeneratedPower recomputes per call
<!-- verified: 0.2.6403.27689 @ 2026-07-08 -->

```csharp
// lines 147040-147049
public override float GetGeneratedPower(CableNetwork cableNetwork)
{
    if (cableNetwork != base.PowerCableNetwork || base.PowerCableNetwork == null)
    {
        _generatedPower = 0f;
        return _generatedPower;
    }
    _generatedPower = CalculateGenerationRate();
    return _generatedPower;
}
```

There is no caching: each call reruns `CalculateGenerationRate()` against live wind, weather, and atmosphere state. The `_generatedPower` field is a scratch of the latest call, not a per-tick latch.

## CalculateGenerationRate formula
<!-- verified: 0.2.6403.27689 @ 2026-07-08 -->

```csharp
// lines 146959-146996
public float CalculateGenerationRate()
{
    if (!IsOperable || GetRoom() != null || !base.IsStructureCompleted)
    {
        return 0f;
    }
    if (!base.HasOpenGrid)
    {
        return 0f;
    }
    PressurekPa worldAtmospherePressureClamped = GetWorldAtmospherePressureClamped();
    if (worldAtmospherePressureClamped <= PressurekPa.Zero)
    {
        return 0f;
    }
    int num;
    float num2;
    if (WeatherManager.CurrentWeatherEvent != null && WeatherManager.IsWeatherEventRunning)
    {
        num = ((WeatherManager.CurrentWeatherEvent.StormEffect != null) ? 1 : 0);
        if (num != 0)
        {
            num2 = (float)WeatherManager.CurrentWeatherEvent.WindStrength * WeatherUtilisationMultiplier;
            goto IL_0084;
        }
    }
    else
    {
        num = 0;
    }
    num2 = 1f;
    goto IL_0084;
    IL_0084:
    float num3 = num2;
    float max = ((num != 0) ? MaxPowerOutputStorm : MAXPowerOutput);
    float min = ((num != 0) ? ((float)WeatherManager.CurrentWeatherEvent.WindStrength * (MaxPowerOutputStorm / 100f)) : 0f);
    return Mathf.Clamp(worldAtmospherePressureClamped.ToFloat() * WindStrength * num3 * NoiseIntensity, min, max);
}
```

Inputs, and who mutates them:

- `WindStrength`: static property on the class, rewritten every frame on the main thread (next section).
- `WeatherManager.CurrentWeatherEvent` / `IsWeatherEventRunning` / `WindStrength` (the weather event's own field): main-thread weather state; a storm starting or ending mid-electricity-tick changes the branch, the multiplier, and the clamp band.
- `GetWorldAtmospherePressureClamped()` -> `AtmosphericsController.ReadonlyGlobalAtmosphere(WorldGrid).PressureGassesAndLiquids` (lines 147021-147038): world atmosphere read, clamped to `[_minPressure, _maxPressure]`, zero below 1 kPa or when the turbine is in a room.
- `IsOperable`, `IsStructureCompleted`, `HasOpenGrid`: device state gates.

## Wind state: rewritten every frame by the main thread
<!-- verified: 0.2.6403.27689 @ 2026-07-08 -->

```csharp
// line 146908
public static float WindStrength { get; private set; }

// lines 146998-147018
public static void UpdateWind()
{
    WindStrength = GetNoise(1f);
    WindDirection = GetWindDirection();
}

public static float GetNoise(float noiseIntensity)
{
    return Mathf.Abs(PowerGenerationSimplexNoise.Evaluate(0f, NetworkTime.time * 0.01f) * noiseIntensity);
}

public static Vector3 GetWindDirection()
{
    float value = PowerGenerationSimplexNoise.Evaluate(0f, NetworkTime.time * 0.005f);
    float num = RocketMath.MapToScale(-1f, 1f, -180f, 180f, value);
    Vector3 result = new Vector3(Mathf.Cos(num * (MathF.PI / 180f)), 0f, Mathf.Sin(num * (MathF.PI / 180f)));
    if (WeatherManager.IsWeatherEventRunning)
    {
        return WeatherManager.StormDirectionVector * -1f;
    }
    return result;
}
```

The call site is the last line of `GameManager.Update`:

```csharp
// lines 205213-205219 (GameManager.Update tail)
    foreach (ManagerBase manager2 in Managers)
    {
        manager2.ManagerUpdate();
    }
    Assets.Scripts.Objects.BatchRenderer.RenderAll();
    WindTurbineGenerator.UpdateWind();
}
```

So `WindStrength` is simplex noise over `NetworkTime.time`, re-evaluated once per rendered frame on the main thread. At 60 fps that is ~30 rewrites per 500 ms electricity tick. Wind is global (one static value for every turbine in the world), not per-turbine.

## Consequence: mid-tick read tearing, and the PowerGridPlus latch
<!-- verified: 0.2.6403.27689 @ 2026-07-08 -->

The electricity tick runs on a UniTask ThreadPool worker (see `PowerTickThreading.md`) while `UpdateWind()` keeps firing on the main thread. Any consumer that calls `GetGeneratedPower` more than once per tick (the vanilla `Initialise` / `CalculateState` / `ApplyState` sequence does, and PowerGridPlus's allocator observes generation as well) can read two different values for the same turbine in one tick: the noise input moved between the calls.

PowerGridPlus therefore applies the same tick-scoped first-read latch to `WindTurbineGenerator.GetGeneratedPower` that it applies to solar panels (`WindTurbineOutputLatchPatches.cs`): the first read in an atomic tick is cached per turbine and replayed for every subsequent read in that tick, cleared at tick end and on world load. Because `LargeWindTurbineGenerator` does not override the method, the single patch covers both classes. No other generator class needs this treatment; see the "Generator output stability census" section of `PowerTickThreading.md` for the per-class verdicts.

## Verification history

- 2026-07-08: page created during the PowerGridPlus elastic-to-soft + auditor round. All excerpts read directly from `.work/decomp/0.2.6403.27689/Assembly-CSharp.decompiled.cs`; the LargeWindTurbineGenerator no-override claim verified by scanning every `GetGeneratedPower` override site (none inside 146360-146847).

## Open questions

None at creation.
