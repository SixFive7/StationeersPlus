---
title: WeatherEvent
type: GameClasses
created_in: 0.2.6228.27061
verified_in: 0.2.6228.27061
verified_at: 2026-05-15
sources:
  - rocketstation_Data/Managed/Assembly-CSharp.dll :: Assets.Scripts.WeatherEvent (line 52768)
  - rocketstation_Data/Managed/Assembly-CSharp.dll :: Assets.Scripts.WeatherManager (line 93340)
  - rocketstation_Data/Managed/Assembly-CSharp.dll :: Assets.Scripts.OrbitalSimulation (line 56452, 56822-56830)
  - rocketstation_Data/StreamingAssets/Data/weather.xml
  - rocketstation_Data/StreamingAssets/Data/celestialbodies.xml
related:
  - ./SolarPanel.md
tags: [power, prefab]
---

# WeatherEvent

Vanilla data class `Assets.Scripts.WeatherEvent : DataCollection` (line 52768). XML-loaded definition for each weather event the game can schedule (dust storm, ash storm, rain, snow, solar storm, etc.). Held one-at-a-time on the static `WeatherManager.CurrentWeatherEvent` (line 93482) while `IsWeatherEventRunning` is true; consumed by `SolarPanel.PowerGenerated`, `OrbitalSimulation.RecalculateWorldSun`, `WeatherManager.DoWeatherDamage`, `SolarRadiators.CheckSolarRadiatorWeatherDamageAction`, and the camera filter pipeline.

The class definition only declares the schema; the actual numeric values for every shipped event are in `StreamingAssets/Data/weather.xml`.

## Class schema
<!-- verified: 0.2.6228.27061 @ 2026-05-15 -->

```csharp
public class WeatherEvent : DataCollection
{
    private const int DEFAULT_FIRST_STORM_DELAY = 7;

    [XmlAttribute("FirstStormDelay")] public int FirstStormDelayDays = 7;
    [XmlAttribute("WindSound")]       public bool WindSound = true;

    [XmlElement("ParticleId")]   public StringReference ParticleEffectId;
    [XmlElement("CoolDown")]     public IntRangeData CoolDownDays;
    [XmlElement("StartDelay")]   public FloatRangeData EventStartDelaySeconds;
    [XmlElement("Duration")]     public FloatRangeData EventDurationSeconds;
    [XmlElement("StormEffect")]  public StormEffectData StormEffect;
    [XmlElement("Fog")]          public FogData Fog;

    [XmlElement("TemperatureOffset",      typeof(GlobalTemperatureFloatOffset))]
    [XmlElement("TemperatureOffsetCurve", typeof(GlobalTemperatureCurveOffset))]
    public GlobalTemperatureOffsetData TemperatureOffset;

    [XmlElement("SolarRatio")]              public FloatReference SolarRatio;
    [XmlElement("WindStrength")]            public FloatReference WindStrength;
    [XmlElement("DamageMultiplier")]        public FloatReference WeatherDamageMultiplier;
    [XmlElement("MovementSpeedMultiplier")] public FloatReference MovementSpeedMultiplier;
    [XmlElement("DirectionalLight")]        public DirectionalLightData DirectionalLight;
    [XmlElement("SolarStormCameraEffect")]  public FloatReference SolarStormCameraEffect;

    [XmlElement("Shell1Sound")] public StringReference Shell1Sound;
    [XmlElement("Shell2Sound")] public StringReference Shell2Sound;
    [XmlElement("Shell3Sound")] public StringReference Shell3Sound;
    [XmlElement("Shell4Sound")] public StringReference Shell4Sound;
}
```

`IsValid()` (lines 52859-52866) requires `Id`, `CoolDownDays`, `EventStartDelaySeconds`, `EventDurationSeconds`, `TemperatureOffset`, `SolarRatio`, and `WindStrength` to be non-null. The other XML elements are optional. `DEFAULT_FIRST_STORM_DELAY = 7` is a one-week real-time grace before the first storm of a session can fire (per-event override available via the `FirstStormDelay` XML attribute).

## Vanilla weather events from weather.xml
<!-- verified: 0.2.6228.27061 @ 2026-05-15 -->

Verbatim from `rocketstation_Data/StreamingAssets/Data/weather.xml` (eight events shipped):

| Id | Name key | SolarRatio | DamageMultiplier | Duration (s) | CoolDown (days) | StartDelay (s) | WindStrength | MovementSpeedMultiplier | TemperatureOffset Day / Night |
|---|---|---|---|---|---|---|---|---|---|
| `MarsDustStorm`    | `DustStorm`        | 0.3  | 1 | 120-600 | 3-12 | 120-900 | 15 | 0.1 | -20 / 30 |
| `EuropaSnowStorm`  | `SnowStorm`        | 0.3  | 1 | 120-600 | 3-12 | 120-900 | 15 | 0.1 | -29 / -24 |
| `VulcanAshStorm`   | `AshStorm`         | 0.05 | 1 | 120-600 | 3-12 | 120-900 | 15 | 0.1 | -275 / 150 |
| `VenusStorm`       | `DustStorm`        | 0.3  | 1 | 120-600 | 3-12 | 120-900 | 15 | 0.1 | -98 / -98 |
| `Rain`             | `RainWeather`      | 0.5  | 0 | 120-240 | 3-12 | 30-60  | 2  | 1   | 0 / 0 |
| `Snow`             | `SnowWeather`      | 0.5  | 0 | 120-240 | 3-12 | 30-60  | 2  | 1   | 0 / 0 |
| `SolarStorm`       | `SolarStormWeather`| 4    | 0 | 600-900 | 3-12 | 30-60  | 1  | 1   | 50 / 50 |
| `VulcanSolarStorm` | `SolarStormWeather`| 4    | 0 | 600-900 | 3-12 | 30-60  | 1  | 1   | 500 / 100 |

Key observations:

- **Solar storms BUFF panels, not debuff them.** `SolarRatio = 4` is the multiplier in `SolarPanel.PowerGenerated()` (`rawIrradiance = SolarIrradiance * _panelArea * SolarRatio`). The four atmospheric storms (Mars dust, Europa snow, Vulcan ash, Venus dust) attenuate sun to 5-30 percent. Rain and Snow attenuate to 50 percent. Solar storms multiply by 4x.
- **Solar storms deal NO weather damage.** Both `SolarStorm` and `VulcanSolarStorm` carry `DamageMultiplier = 0`, so `WeatherManager.DoWeatherDamage` and `SolarRadiators.CheckSolarRadiatorWeatherDamageAction` both no-op on solar storms. The "heavy" solar panel WeatherDamageScale=0 distinction is irrelevant during a solar storm; standard panels are equally safe. (Damage during a solar storm comes from temperature offset and EVA suit thermal load, not the panel-damage code path.)
- **Solar storms last longer.** Duration 600-900 s vs 120-600 s for atmospheric storms (10-15 minutes vs 2-10 minutes).
- **Solar storms have a flickering directional light.** Both solar-storm events carry a `<DirectionalLight>` block with `Intensity Min=8 Max=12` (Vulcan: 11-14) and `Frequency 0.3`; `OrbitalSimulation` at line 56774 takes `Mathf.Max(num2, WeatherManager.CurrentWeatherEvent.DirectionalLight.GetIntensity())` so the sun's color/brightness flickers visibly.
- **`Direction Value="None"` on solar storms** means no particle "rain direction" applies (rain/snow are `Down`; the four dust/ash/snow storms have no Direction tag, defaulting to particle-system default).
- **MovementSpeedMultiplier 0.1 in atmospheric storms** is what slows the player walking around in a storm; rain/snow/solar leave it at 1.0.
- **First storm delay 7 days** (real-time game-world days) is the class default; no event overrides it in the shipped XML.

## OrbitalSimulation.SolarIrradiance formula
<!-- verified: 0.2.6228.27061 @ 2026-05-15 -->

`OrbitalSimulation.CalculateSolarIrradiance(double distanceInAu)` (line 56827):

```csharp
private float CalculateSolarIrradiance(double distanceInAu)
{
    return (float)((double)SolarConstant / (distanceInAu * distanceInAu));
}
```

Public entry: `CalculateSolarIrradiance()` (line 56822) calls the private overload with `GetPrimaryBody()?.DistanceToPlayer`. Result is assigned to the static `OrbitalSimulation.SolarIrradiance` property (line 56723) once per orbital tick.

`SolarConstant` (line 56368): class default `1367f`, `DEFAULT_SOLAR_CONSTANT = 1367f` (line 56370). Per-body override is via `CelestialBody.SolarConstant` (`<CelestialBody>` XML attribute `[XmlAttribute("SolarConstant")]`, line 54317, default 1367), applied at world setup (line 57086-57096: when `worldSetting.PrimaryBody?.SolarConstant` is non-NaN it's copied into `OrbitalSimulation.SolarConstant`).

The shipped `celestialbodies.xml` (verified at v0.2.6228.27061) **does not override `SolarConstant` on any body**. Every body uses 1367 W/m² as the at-1-AU solar constant. Irradiance variation comes from orbital distance squared.

`EarthSolarRatio => SolarIrradiance / 1367f` (line 56450) is the "fraction of Earth-sun irradiance" used by `PortableSolar.PowerGenerated` to scale handheld output by body distance.

## Per-body solar irradiance (vanilla)
<!-- verified: 0.2.6228.27061 @ 2026-05-15 -->

Computed `1367 / SemiMajorAxisAu^2` from `celestialbodies.xml` (using the orbit's semi-major axis, the long-term-average orbital distance). Bodies that orbit a planet take the parent planet's SemiMajorAxisAu (Moon orbits Earth at 1 AU; Europa orbits Jupiter at 5.2044 AU; etc.).

| Body | SemiMajorAxisAu | SolarIrradiance (W/m²) | Notes |
|---|---|---|---|
| Mercury / `Vulcan` (Vulcan system) | 0.39 / 1.2 | 8985 / 949 | Vulcan is its own celestial system (line 182), 1.2 AU from its sun. Mercury is at 0.39 AU. |
| Venus | 0.72 | 2637 | |
| Earth / Moon / LowEarthOrbit | 1.0 | 1367 | |
| Mars / Phobos / Deimos | 1.52 | 591 | |
| AsteroidBelt | 2.5 | 219 | (in-game "Loulan" maps to this orbit, not verified here) |
| Jupiter / Io / Europa / Ganymede / Callisto / Himalia | 5.2044 | 50.5 | |
| Saturn and moons (Mimas, Enceladus, Tethys, Dione, Rhea, Titan, Hyperion, Iapetus, Phoebe) | 9.58 | 14.9 | |
| Uranus | 19.22 | 3.70 | |
| Neptune | 30.05 | 1.51 | |
| Pluto / Eris / Haumea | 39.48 / 67.78 / 43.13 | 0.877 / 0.297 / 0.735 | |

Eccentric orbits (Mercury e=0.21, Mars e=0.09, Vulcan e=0.3) vary their actual `DistanceToPlayer` over the orbital period; the table uses the semi-major axis as the long-term average. Perihelion is roughly `1367 / (a*(1-e))^2`, aphelion `1367 / (a*(1+e))^2`. The `OrbitalSimulation` recalculates `SolarIrradiance` each orbital tick from the current `DistanceToPlayer`, so a body's value drifts over its year.

## WeatherManager scheduling and state
<!-- verified: 0.2.6228.27061 @ 2026-05-15 -->

`Assets.Scripts.WeatherManager : ManagerBase` (line 93340). Statics:

- `CurrentWeatherEvent : WeatherEvent { get; private set; }` (line 93482). Server-authoritative.
- `IsWeatherEventRunning : bool` (line 93459). Server-authoritative, network-synced.
- `IsWeatherEventScheduled => CurrentWeatherEvent != null` (line 93484). True when an event is queued but not yet started (within `StartDelay` window).
- `WeatherStartTime`, `WeatherEventLength` (lines 93544-93545). Set when `ScheduleWeatherEvent` is called from `MainRandom.random`.
- `WeatherState` enum (RainScheduled / Rain / SnowScheduled / Snow / StormScheduled / Storm) used for client-side cosmetics; mapped from the current event's `IdHash` (lines 93523-93534).
- `DoWeatherDamage(IWeatherDamagable thing)` (line 93422): `thing.DoWeatherDamage((float)CurrentWeatherEvent.WeatherDamageMultiplier)`. Only called by things that implement `IWeatherDamagable`. Note this is separate from `SolarRadiators.DamageSolarRadiators` (which has its own `WeatherDamageMultiplier` consumption with the `WeatherDamageScale * 0.005` per-tick formula for solar panels / radiators).
- `WeatherManagerSavedData` (line 93301) persists `CurrentWeatherEventId : string` and `IsWeatherEventRunning : bool` across save/load.

## Sun color override during a weather event
<!-- verified: 0.2.6228.27061 @ 2026-05-15 -->

`OrbitalSimulation.RecalculateWorldSun` (lines 56769-56786) overrides the directional sun's intensity and color when a weather event is running:

```csharp
if (WeatherManager.CurrentWeatherEvent != null && WeatherManager.IsWeatherEventRunning && WeatherManager.CurrentWeatherEvent.SolarRatio != null)
{
    num2 = WeatherManager.CurrentWeatherEvent.SolarRatio;
    if (WeatherManager.CurrentWeatherEvent.DirectionalLight != null)
    {
        num2 = Mathf.Max(num2, WeatherManager.CurrentWeatherEvent.DirectionalLight.GetIntensity());
    }
}
// ...
WorldSun.color = ((WeatherManager.CurrentWeatherEvent?.DirectionalLight != null)
    ? ((Color)WeatherManager.CurrentWeatherEvent.DirectionalLight.Color)
    : WorldManager.Instance.WorldSun.Color);
```

So the visible-on-screen sun brightness during a solar storm uses `max(SolarRatio=4, DirectionalLight.GetIntensity())`. `DirectionalLightData.GetIntensity()` (line 52763) is an OpenSimplex-noise driven range `[Intensity.Min, Intensity.Max]` sampled at `Time.time * Frequency`, hence the flicker. For `SolarStorm` that range is 8-12; for `VulcanSolarStorm` it is 11-14. The power-generation `SolarRatio` is the static `4` regardless of the visual flicker.

## Source citations
<!-- verified: 0.2.6228.27061 @ 2026-05-15 -->

- `rocketstation_Data/Managed/Assembly-CSharp.dll :: Assets.Scripts.WeatherEvent` (class def line 52768).
- `rocketstation_Data/Managed/Assembly-CSharp.dll :: Assets.Scripts.WeatherManager` (line 93340).
- `rocketstation_Data/Managed/Assembly-CSharp.dll :: Assets.Scripts.OrbitalSimulation` (SolarIrradiance get/set line 56452; CalculateSolarIrradiance line 56822; SolarConstant default line 56368).
- `rocketstation_Data/Managed/Assembly-CSharp.dll :: Assets.Scripts.WeatherManagerSavedData` (line 93301).
- `rocketstation_Data/StreamingAssets/Data/weather.xml` (all eight WeatherEvent entries, lines 4-201).
- `rocketstation_Data/StreamingAssets/Data/celestialbodies.xml` (no SolarConstant overrides on any body; orbit semi-major axes per body).

## Open questions
<!-- verified: 0.2.6228.27061 @ 2026-05-15 -->

- `DirectionalLight.GetIntensity()` produces a range using `OpenSimplexNoise.Evaluate(0f, Time.time * Frequency)` (line 52765). Whether `Time.time` is server-authoritative or per-client means the flicker pattern may desync across clients. Not tested.
- `WeatherEventLength` is server-rolled per event. Verifying it network-replicates to clients (vs. each client rolling separately and visually de-syncing) requires reading the `WeatherManager` packet path. Not done here.
- Which `IWeatherDamagable` implementers exist beyond solar panels and radiators. The `WeatherManager.DoWeatherDamage` path is a different damage channel than the `SolarRadiators` per-tick check, so there may be additional damage targets (plants, players in EVA, etc.) routed through it. Not enumerated.

## Verification history
<!-- verified: 0.2.6228.27061 @ 2026-05-15 -->

- 2026-05-15: page created from `Assembly-CSharp.decompiled.cs` (WeatherEvent class line 52768, WeatherManager line 93340, OrbitalSimulation SolarIrradiance get/CalculateSolarIrradiance lines 56452 / 56822) and verbatim data from `StreamingAssets/Data/weather.xml` and `StreamingAssets/Data/celestialbodies.xml`. Per-body irradiance table computed from semi-major axes; no `SolarConstant` override exists on any vanilla body so 1367 W/m² is uniform at 1 AU. Key finding for power-grid math: `SolarStorm` and `VulcanSolarStorm` both carry `SolarRatio = 4` (BUFF, not nerf) and `DamageMultiplier = 0` (panels safe), which combines with `SolarPanel.PowerGenerated`'s 1.6 efficiency scalar during a weather event to soft-cap output above rated max.
