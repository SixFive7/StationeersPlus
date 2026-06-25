---
title: OrbitalSimulation
type: GameClasses
created_in: 0.2.6228.27061
verified_in: 0.2.6228.27061
verified_at: 2026-06-25
sources:
  - .work/decomp/0.2.6228.27061/Assembly-CSharp.decompiled.cs:56302-56913 (OrbitalSimulation)
  - .work/decomp/0.2.6228.27061/Assembly-CSharp.decompiled.cs:53098-53120 (OrbitSimulationSaveData)
  - .work/decomp/0.2.6228.27061/Assembly-CSharp.decompiled.cs:53594-53700 (RotatingCelestialBody)
  - .work/decomp/0.2.6228.27061/Assembly-CSharp.decompiled.cs:57327-57346 (Serialize/DeserializeSave)
  - .work/decomp/0.2.6228.27061/Assembly-CSharp.decompiled.cs:97405-97800 (OrbitalCommand / CelestialCommand)
related:
  - ./SolarPanel.md
  - ./WeatherEvent.md
  - ../Workflows/TimeSkipWorldManipulation.md
  - ../Protocols/WorldXml.md
tags: [power, worldgen, timeskip, transforms]
---

# OrbitalSimulation

`OrbitalSimulation` is the authority for the sun direction and the day/night cycle. It is a plain C# class (not a MonoBehaviour) exposing a static singleton `OrbitalSimulation.System`. There is no stored "sun angle" scalar: the sun is a derived unit direction vector `OrbitalSimulation.WorldSunVector`, recomputed every frame from an orbital/rotational simulation driven by a `TimeScale`-scaled clock. Everything sun-facing (solar panels, daylight sensors, atmosphere temperature curve, eclipse, ambient light) reads `WorldSunVector`.

This page covers the runtime mechanism, the freeze and set-time levers, and the save serialization. Solar-panel power math lives in [SolarPanel.md](./SolarPanel.md); per-body solar irradiance lives in [WeatherEvent.md](./WeatherEvent.md); the time-skip recipe lives in [TimeSkipWorldManipulation.md](../Workflows/TimeSkipWorldManipulation.md).

## Core members and the per-frame advance
<!-- verified: 0.2.6228.27061 @ 2026-06-25 -->

`OrbitalSimulation` (declared line 56302) singleton: `public static OrbitalSimulation System { get; } = new OrbitalSimulation("Sol", ...);` (line 56386). The player's planet is `OrbitalSimulation.System.PlayerBody` (a `RotatingCelestialBody`, field at line 56316).

| Member | Kind | Line | Meaning |
|---|---|---|---|
| `OrbitalSimulation.WorldSunVector` | `public static Vector3` | 56358 (set 56603) | Current sun direction, normalized. Derived each frame. There is no angle scalar. |
| `OrbitalSimulation.System` | `public static` singleton | 56386 | The "Sol" simulation instance. |
| `OrbitalSimulation.PlayerBody` | `RotatingCelestialBody` | 56316 | The player's planet body. |
| `OrbitalSimulation.SimulationTimeSeconds` | `public double` | 56336 | Absolute simulation clock in seconds. Drives `WorldSunVector`. |
| `OrbitalSimulation.TotalRealTimeSeconds` | `public double` | (set 56584) | Accumulated real time. Persisted as `AccumulatedTime`. |
| `OrbitalSimulation.TimeScale` | `public` property (private setter) | 56411 (`_timeScale` 56378) | Per-frame multiplier converting real delta to simulation delta. `0` freezes the sun. |
| `OrbitalSimulation.TimeOfDay` / `_timeOfDay` | `public static float` property | 56390 / 56318 | Derived 0..1 day-fraction = `Abs(AccumulatedAngle/360) % 1`. Not centered at 0.5 for noon. |
| `OrbitalSimulation.SolarIntensity` | `public static float` | 56366 (set 56779) | Directional light brightness, `Clamp(WorldSunVector.y / 0.01, 0, MaxSunIntensity)`. |
| `OrbitalSimulation.DayLengthSeconds` | auto-property, default `1200` | 56448 | Real-time seconds per in-game day. `DEFAULT_DAY_SECONDS = 1200` (line 56348). |
| `RotatingCelestialBody._baseRotationSpeed` | `public double`, default `15.0` | 53600 (seed 56190) | Raw axial spin rate (deg per orbit unit). Lowest-level rotation knob. |
| `RotatingCelestialBody.CurrentAngle` | `float` | 53647 | Planet rotation angle, derived 0..360 degrees. |

Driver chain, frame by frame:

`WorldManager.ManagerUpdate()` (line 59773) calls `OrbitalSimulation.UpdateEachFrame()` only while `GameManager.IsRunning`:

```csharp
public override void ManagerUpdate()        // line 59773
{
    base.ManagerUpdate();
    if (GameManager.IsRunning)
        OrbitalSimulation.UpdateEachFrame();   // line 59778
}
```

`UpdateEachFrame()` (line 56706):

```csharp
public static void UpdateEachFrame()        // line 56706
{
    if (System != null && IsValid)
    {
        System.UpdateAllBodies(Time.deltaTime);   // line 56710  advances the sun
        CameraController.SetCameraPosition();
        HandleUpdate();                            // line 56712  recomputes _timeOfDay, sun transform
        SetSunState(Time.unscaledDeltaTime);       // line 56713  eclipse + light intensity
    }
}
```

`UpdateAllBodies(double deltaRealTime)` (line 56582) is where the clock advances, scaled by `TimeScale`:

```csharp
private void UpdateAllBodies(double deltaRealTime)   // line 56582
{
    TotalRealTimeSeconds += deltaRealTime;
    double num = deltaRealTime * TimeScale;           // line 56585  <-- the freeze point
    SetAllBodies(SimulationTimeSeconds + num);        // line 56586
}
```

`SetAllBodies(double simulationTime)` (line 56594) rebuilds the sun vector:

```csharp
private void SetAllBodies(double simulationTime)   // line 56594
{
    if (GameManager.IsRunning && OrbitalDebugger)
        HandleDebugInput();
    SimulationTimeSeconds = simulationTime;        // line 56600
    PrimaryBody.Set(simulationTime);
    PrimaryBody.SetPlayerVectorTo(PlayerBody);
    WorldSunVector = PrimaryBody.WorldVector.normalized;   // line 56603
    ...
}
```

The single line that, neutralized, freezes the sun: **line 56585**, `double num = deltaRealTime * TimeScale;`. With `TimeScale == 0`, `num == 0`, so `SetAllBodies` keeps recomputing the same `SimulationTimeSeconds` and `WorldSunVector` is constant.

There is **no dedicated `PauseSun` / `RotationEnabled` / `DisableOrbit` boolean.** The only gates are: `GameManager.IsRunning` (whole update, line 59776), `OrbitalDebugger` (debug overlay + dev scrub keys only, line 56346), `GameManager.IsBatchMode` (skips `SetSunState` visuals only, line 56735). The idiomatic sun-only freeze is `TimeScale = 0`.

## Day length and TimeScale derivation
<!-- verified: 0.2.6228.27061 @ 2026-06-25 -->

Two distinct knobs control how fast the sun advances:

1. `OrbitalSimulation.DayLengthSeconds` (line 56448, default 1200 = 20 min real-time per in-game day). Chosen at world creation from `WorldConfigurationMenu.GetDayLengthSeconds()` (line 48574), passed into `OrbitalSimulation.GetSimulation(worldSetting, dayLengthSeconds)` (line 57369, assigns at 57379).
2. `RotatingCelestialBody._baseRotationSpeed` (line 53600, default 15.0). The raw spin; `celestial <body> rotation <v>` sets it directly.

`DayLengthSeconds` feeds `CalculateTimeScale` (line 56503), which sets the actual per-frame multiplier:

```csharp
public static double CalculateTimeScale(OrbitalSimulation simulation)   // line 56503
{
    double num = Math.Abs(simulation.PlayerBody.DegreesPerPlanetYear / 360.0);
    num /= 360.0 / simulation.PlayerBody.RotationalDegreesForSiderealDay();
    if (simulation.PlayerBody.TidallyLocked) num = 1.0;
    double num2 = num * ((double)simulation.DayLengthSeconds / 60.0) * 60.0;
    return 360.0 / num2;                                                 // line 56512
}
```

`TimeScale` is set from this at world build: `orbitalSimulation.TimeScale = CalculateTimeScale(orbitalSimulation);` (line 57383). The setter fires `OnTimeScaleChanged()` for network sync (line 56515).

Related `RotatingCelestialBody` derived quantities: `DegreesPerPlanetYear => Orbit.Period * _baseRotationSpeed` (line 53633); `SolDegreesPerDay => 360.0 / DaysPerPlanetYear` (line 53637); `GetDayLength() => new TimeLength(360.0 / Math.Abs(_baseRotationSpeed) * 86400.0)` (line 53675, note the division by `_baseRotationSpeed`, so do not zero it).

## Setting and freezing the sun (runtime APIs)
<!-- verified: 0.2.6228.27061 @ 2026-06-25 -->

Static entry points:

| API | Line | Effect | Caveat |
|---|---|---|---|
| `OrbitalSimulation.SetTimeScale(float)` | 57409 | Sets `System.TimeScale`, prints confirmation. `0` freezes the sun. | Network-synced; host-only path. |
| `OrbitalSimulation.SetSimulationTime(double seconds, bool publish=false)` | 56573 | `SetAllBodies(seconds)` then `HandleUpdate`. Sets absolute sun position directly. | Bypasses `TimeScale` (uses the value as-is). |
| `OrbitalSimulation.SetRealTime(double realTime, bool publish=false)` | 56564 | `realTime += GetAdjustedOffsetTime(); SetSimulationTime(realTime * System.TimeScale, ...)`. | **Multiplies by `TimeScale`.** If `TimeScale == 0`, simulation time becomes 0. Set time BEFORE freezing, never after. |
| `OrbitalSimulation.SimulateTime(double realTimeDelta)` | 56555 | `UpdateAllBodies(delta)` then `HandleUpdate`. Advances by a delta. | Same `TimeScale` scaling inside `UpdateAllBodies`. |
| `OrbitalSimulation.SetDayTime(float clampedSunTime)` | 56527 | Scrubs `_timeOfDay` to a 0..1 target by stepping `UpdateAllBodies(+/-0.1)` in a loop until `_timeOfDay` reaches the target. | Target is a day-fraction, not a sun-height. See noon note below. |

```csharp
public static void SetRealTime(double realTime, bool publish = false)   // line 56564
{
    if (IsValid)
    {
        realTime += System.GetAdjustedOffsetTime();
        SetSimulationTime(realTime * System.TimeScale, publish);   // line 56569: scaled by TimeScale
    }
}

public static void SetDayTime(float clampedSunTime)   // line 56527
{
    if (!IsValid) return;
    if (clampedSunTime > _timeOfDay)
        while (_timeOfDay < clampedSunTime) System.UpdateAllBodies(0.1);
    else if (clampedSunTime < _timeOfDay) { /* step down then up to land on target */ }
    HandleUpdate();
}
```

Ordering trap: to "set noon then freeze," position the sun first (while `TimeScale != 0`), then `SetTimeScale(0)`. Freezing first makes `SetRealTime` zero out the clock.

Thread-safety (positioning the sun from a non-main thread, e.g. an `ElectricityManager.ElectricityTick` postfix on a headless server): `SetAllBodies(double)` (line 56594) is pure managed math: it sets `SimulationTimeSeconds`, recomputes `WorldSunVector`, and updates the celestial bodies, with no Unity scene-graph access, so it is safe to call from a worker thread. `SetSimulationTime(double)` (line 56573) wraps `SetAllBodies` but then calls `HandleUpdate()` (line 56717), which writes `WorldSunTransform.position` and `.LookAt` (Unity, main-thread only; guarded by `(object)WorldSun != null`, so it no-ops when the sun GameObject is absent on a `-batchmode` server). To scan for or set a sun position off the main thread, call `SetAllBodies` directly and skip `HandleUpdate`. Setting `TimeScale = 0` before the scan makes any concurrent main-thread `UpdateEachFrame` idempotent on the `SimulationTimeSeconds` writes (`delta * 0 == 0`), so the scan cannot be raced.

## What "noon" is (sun at zenith)
<!-- verified: 0.2.6228.27061 @ 2026-06-25 -->

The game has no "noon = 90 degrees" constant. The highest sun is encoded as `WorldSunVector` pointing straight up, i.e. `WorldSunVector.y` at its maximum, approaching `Vector3.up` `(0,1,0)`. Evidence:

- `SolarIntensity = Mathf.Clamp(WorldSunVector.y / 0.01f, 0f, MaxSunIntensity) * ...` (line 56779): brightest when `WorldSunVector.y` is largest. Night = `WorldSunVector.y <= 0`.
- Ambient light: `Mathf.Clamp((WorldSunVector.y + 0.02f) / 0.05f, ...)` (line 28340).
- Atmosphere "curve time" maps the angle between `Vector3.up` and the sun onto 0.25..0.75:

```csharp
public static float GetWorldAtmosphereCurveTime()        // line 56816
{
    float value = Vector3.Angle(Vector3.up, WorldSunVector);   // 0 deg = zenith, 180 deg = deepest night
    return RocketMath.MapToScale(0f, 180f, 0.25f, 0.75f, value);
}
```

So `angle(up, sun) = 0 deg` is noon (scalar 0.25); `90 deg` is the horizon (0.5); `180 deg` is deepest night (0.75).

The `TimeOfDay` 0..1 scalar (`_timeOfDay`) is `Abs(AccumulatedAngle/360) % 1` (`GetTimeOfDay()`, line 56811), a fractional-day position that is offset by `SunriseOffset` and the body's `LongitudeAtEpoch`. The day-fraction that produces the sun's highest point is latitude/axis dependent, so the robust noon test is `WorldSunVector.y` maximal (sun nearest `Vector3.up`), not a fixed `_timeOfDay` value. `SetDayTime(0.5)` does not reliably mean "noon".

Consumers detecting "facing the sun": `SolarPanel.CalculateSolarEfficiency()` (line 400354) `Vector3.Dot(PanelCells.forward, WorldSunVector) > 0` plus `Clamp(1 - (forward - WorldSunVector).magnitude, ...)`; `DaylightSensor.OnThreadUpdate()` (line 373335) reads `_solarAngle` from the spherical decomposition of `WorldSunVector`, default mode = `Vector3.Angle(Forward, WorldSunVector)`.

## Save serialization (world.xml `<Celestial>`)
<!-- verified: 0.2.6228.27061 @ 2026-06-25 -->

Sun/world-time persists through `OrbitSimulationSaveData`, serialized under the `<Celestial>` element of `WorldData`.

`WorldData.CelestialData` (line 250599):

```csharp
[XmlElement("Celestial")]
public OrbitSimulationSaveData CelestialData = new OrbitSimulationSaveData();
```

`OrbitSimulationSaveData` (line 53098):

```csharp
public class OrbitSimulationSaveData          // line 53098
{
    public DoubleReference AccumulatedTime;   // line 53100
    public DoubleReference SimulationTime;    // line 53102

    public void Deserialize(OrbitalSimulation system)   // line 53104
    {
        if (SimulationTime != null)
            OrbitalSimulation.SetSimulationTime(SimulationTime.Value);   // line 53108  authoritative
        else if (AccumulatedTime != null)
            OrbitalSimulation.SetRealTime(AccumulatedTime.Value);        // line 53112  fallback
        if (AccumulatedTime != null)
            OrbitalSimulation.System.TotalRealTimeSeconds = AccumulatedTime.Value;
    }
}
```

Written by `OrbitalSimulation.SerializeSave(...)` (line 57327):

```csharp
worldData.CelestialData = new OrbitSimulationSaveData
{
    SimulationTime = new DoubleReference { Value = System.SimulationTimeSeconds },   // line 57331-57334
    AccumulatedTime = new DoubleReference { Value = System.TotalRealTimeSeconds }    // line 57335-57338
};
```

Read by `DeserializeSave(...)` (line 57342). When `CelestialData == null`, the fallback is `SetRealTime(DaysPast * siderealDaySeconds)` (line 57346).

On-disk shape (attribute-valued `DoubleReference` children, direct child of root `<WorldData>`):

```xml
<Celestial>
  <AccumulatedTime Value="407548.122450768" />
  <SimulationTime Value="122597.43671865921" />
</Celestial>
```

Field map:

| world.xml path | Serialized member | Role |
|---|---|---|
| `WorldData/Celestial/SimulationTime/@Value` | `OrbitSimulationSaveData.SimulationTime` (`DoubleReference`) | **Authoritative.** On load, `SetSimulationTime` rebuilds `WorldSunVector` from this. Editing it moves the sun. |
| `WorldData/Celestial/AccumulatedTime/@Value` | `OrbitSimulationSaveData.AccumulatedTime` (`DoubleReference`) | Total real time; fallback time source and `TotalRealTimeSeconds` restore. |
| `WorldData/DaysPast` | `WorldData.DaysPast` (`uint`, line 250575) | Day counter; fallback if `<Celestial>` absent. Also at `WorldMetaData/DaysPast`. |
| `WorldData/DateTime`, `WorldMetaData/DateTime` | `.NET DateTime.Ticks` (100ns units) | Calendar/clock display only. Not read by `OrbitalSimulation` for sun position. Decoding gives the wall-clock time-of-day shown in the HUD. |

Note: a `<Celestial>` save edit sets sun POSITION on load but does not freeze it; the sun resumes advancing once `TimeScale` (restored at world build from `CalculateTimeScale`, line 57383) ticks. Freezing requires `TimeScale = 0` at runtime.

World-definition-level offsets live in the world TEMPLATE/setting XML, not the per-save world.xml: `<TimeOffset>` (`TimeSpanReference`, line 54389, loaded into `_offsetTime` at line 57105), `<SunriseOffset>` (`FloatReference`, line 54477), `LongitudeAtEpoch` (`float`, line 54472). The `orbital makeoffset` console command (line 97593) prints a ready-to-paste `<TimeOffset Days=".." Hours=".." .../>` snippet of the current time.

## Console commands (orbital, celestial)
<!-- verified: 0.2.6228.27061 @ 2026-06-25 -->

Commands derive from `CommandBase`; the menu token is the lowercased class name minus `Command`.

`OrbitalCommand` (token `orbital`, line 97551). Subcommand list (line 97569): `debug, view, celestials, simulate, set, timescale, makeoffset`.

| Command | Line | Effect |
|---|---|---|
| `orbital timescale <float>` | 97631 | Sets `TimeScale` via `SetTimeScale`. **`orbital timescale 0` freezes the sun.** Restore with `orbital timescale 1`. Blocked for multiplayer clients (line 97639). |
| `orbital set <value> [span]` | 97700 | Sets absolute world time via `SetRealTime(result, publish:true)` (line 97724). Moves the sun / time of day. `span` in `{Seconds, Minutes, Hours, Days, Weeks, Months, Years}` (enum `SimulationSpan`, line 97505; default Seconds). Blocked for clients ("Cannot set time as client", line 97704). Example: `orbital set 6 Hours`. |
| `orbital simulate <value> [span]` | 97672 | Advances time by a delta via `SimulateTime` (line 97696). Example: `orbital simulate 1 Days`. |
| `orbital makeoffset` | 97593 | Copies a `<TimeOffset .../>` XML snippet of current time to clipboard (template authoring). |
| `orbital debug` | 97768 | Toggles `OrbitalDebugger` (ImGui overlay + `=`/`-` scrub keys). Does not freeze. |
| `orbital view` | 97758 | Toggles the in-world orbit visualization. |
| `orbital` (no args) | 97577 | `PrintDebug()`: sun vector horizontal/vertical angles, timescale, etc. |

`CelestialCommand` (token `celestial`, line 97405). Subcommand list (line 97425): `eccentricity, semimajoraxisau, semimajoraxiskm, inclination, periapsis, period, ascendingnode, rotation`. Syntax `celestial <bodyName> <field> <value>`.

| Command | Line | Effect |
|---|---|---|
| `celestial <body> rotation <speed>` | 97490-97496 | Sets `rotatingCelestialBody._baseRotationSpeed = float.Parse(args[2])`. `celestial <body> rotation 0` zeroes axial spin (another freeze path, but risks division-by-zero in `GetDayLength`). Blocked entirely in multiplayer ("Cannot change celestials in multiplayer", line 97455). |
| `celestial <body> period <days>` | 97484 | Orbital period. |

Multiplayer: the orbital state delta-syncs to clients through `SerializeDeltaState` (line 57283) / `DeserializeDeltaState` (line 57307). `TimeScale` rides `NetworkUpdateFlags` bit 1 (set by `OnTimeScaleChanged`, line 56523; written at 57298). `TotalRealTimeSeconds` + `SimulationTimeSeconds` ride bit 2, which `SerializeDeltaState` sets on every sync (line 57290), so the sun POSITION syncs continuously, not just at join; the client applies it via `System.SetAllBodies(simTime)` + `HandleUpdate()` (lines 57321-57323). A host-side set of both `SimulationTimeSeconds` (via `SetAllBodies`) and `TimeScale = 0` therefore yields a frozen-at-position sun on every connected client. `orbital set/simulate/timescale` are host-only (clients rejected); `celestial` is single-player only. A freeze/set must run host-side and broadcast.

## Verification history
<!-- verified: 0.2.6228.27061 @ 2026-06-25 -->

- 2026-06-25: page created from a decompile read of `Assembly-CSharp.decompiled.cs` (game version 0.2.6228.27061) while researching "fix the sun to noon and freeze it" on a Luna dedicated-server save. Captures the `WorldSunVector` derivation, the `TimeScale` freeze point (line 56585), the day-length / `CalculateTimeScale` derivation, the noon = `Vector3.up` convention, the `<Celestial>` save serialization, and the `orbital` / `celestial` console commands. Consistent with the existing `OrbitalSimulation.SetDayTime(float)` "0-1 range" note in `../Workflows/TimeSkipWorldManipulation.md` (no conflict).
- 2026-06-25: added the runtime thread-safety split (`SetAllBodies` is worker-safe pure math; `SetSimulationTime` -> `HandleUpdate` writes `WorldSunTransform`, main-thread only) and the delta-state network-sync detail (`SimulationTimeSeconds` / `TotalRealTimeSeconds` ride `SerializeDeltaState` bit 2, set every sync, so sun position syncs continuously). Captured while building the ScenarioRunner `sun-noon` freeze for a Luna dedicated-server debug session. Sources: `Assembly-CSharp.decompiled.cs` lines 56594 (`SetAllBodies`), 56717 (`HandleUpdate`), 57283-57325 (`SerializeDeltaState` / `DeserializeDeltaState`).

## Open questions
<!-- verified: 0.2.6228.27061 @ 2026-06-25 -->

- The exact `SimulationTimeSeconds` value that places `WorldSunVector` at maximum `y` (true zenith) for the Lunar body is latitude/axis/offset dependent and was not solved analytically. In practice, position with `orbital set ... Hours` (or `SetDayTime`) and check `WorldSunVector.y`, then freeze with `orbital timescale 0`.
