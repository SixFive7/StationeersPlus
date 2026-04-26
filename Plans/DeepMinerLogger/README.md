# Deep Miner Logger

Temporary diagnostic mod. Logs the full runtime state of every `CombustionDeepMiner` to CSV on every atmospheric tick where any tracked field changes. Not for Workshop; not for `Mods/`.

## Build

Requires the monorepo's `Directory.Build.props` (filled in from the template) so `$(StationeersPath)` resolves. Open `DeepMinerLogger.sln` in Visual Studio or run `msbuild DeepMinerLogger.sln /p:Configuration=Release`. Output lands in `DeepMinerLogger/bin/Release/DeepMinerLogger.dll` plus the `About/` folder.

## Install

Copy the `About/` folder and `DeepMinerLogger.dll` into `<StationeersInstall>\BepInEx\plugins\DeepMinerLogger\`:

```
BepInEx\plugins\DeepMinerLogger\
  DeepMinerLogger.dll
  About\About.xml
```

Launch the game. A line "`DeepMinerLogger v0.1.0 loaded. Log directory: ...`" should appear in `BepInEx\LogOutput.log`.

## Output

One CSV file per `CombustionDeepMiner` per game session:

```
<StationeersInstall>\BepInEx\DeepMinerLogger\logs\miner_<ReferenceId>_<yyyyMMdd_HHmmss>.csv
```

A row is written only when any tracked field differs from the previously written row for that miner, beyond a per-field epsilon. Tick counter starts at 0 when the miner is first seen this session and increments monotonically every atmospheric tick (20 Hz).

## Columns

| Column | Meaning |
|---|---|
| `tick` | Monotonic atmospheric-tick counter for this miner (20 per game-second) |
| `rpm` | `_internalCombustion.Rpm` |
| `stress` | `_internalCombustion.Stress` |
| `throttle` | `_internalCombustion.Throttle` |
| `cl` | `_internalCombustion.CombustionLimiter` |
| `didCombustion` | `_internalCombustion.DidCombustionLastTick` |
| `gainedStress` | `_internalCombustion._gainedStress` (private) |
| `targetPkPa` | `_internalCombustion._targetPressure` (private) in kPa |
| `normalCombustionEnergyJ` | `_internalCombustion._normalCombustionEnergyCache` (private) in J |
| `chamberTK` | chamber `InternalAtmosphere.Temperature` in K |
| `chamberPkPa` | chamber `InternalAtmosphere.PressureGassesAndLiquids` in kPa |
| `chamberMoles` | chamber `InternalAtmosphere.TotalMoles` |
| `combustionEnergyJ` | chamber `InternalAtmosphere.CombustionEnergy` in J |
| `rO2, rH2, rSteam, rPollutant, rCO2, rN2, rN2O, rO3, rVolatiles` | chamber gas ratios |
| `onoff, powered, error, structureCompleted` | device-level flags |
| `isInputValid, isOutputValid, isInput2Valid` | pipe-validity gates |
| `inputNull, inputStructureCount, inputAwaitingEvent, inputPkPa, inputTK, inputMoles` | input pipe network details |
| `outputNull, outputStructureCount, outputAwaitingEvent, outputPkPa, outputTK, outputMoles` | output pipe network details |
| `thingInTheWay` | `DeepMiner.ThingInTheWay != null` |
| `deepMinablesSet` | `DeepMiner._deepMinables != null` |
| `canMine` | result of `DeepMiner.CanMine()` |
| `reachedBedRock` | `DeepMiner._isReachedBedRock` |
| `chipPresent, codeErrorState, compilationError` | built-in ProgrammableChip slot state |

## Change-detection epsilons

- Ratios: 0.001
- Pressures, temperatures, RPM, stress, throttle, CL, target pressure: 0.01
- Moles: 0.0001
- Energy (J): 0.1
- Ints and bools: exact

## Workflow for diagnosing the crash

1. Install. Launch game. Load the save.
2. Kick off the IC10 script and let the miner crash normally.
3. Exit the game (cleanly; auto-flush is on but a cleaner exit guarantees the buffer is closed).
4. Read the CSV at `BepInEx\DeepMinerLogger\logs\miner_<refId>_<stamp>.csv`. The row immediately before `error` transitions from 0 to 1, and the row at the transition, should identify which gate flipped.

## Scope

- Server/solo only. Skipped silently on remote clients.
- `CombustionDeepMiner` only. `ElectricDeepMiner` and other `DeepMiner` subclasses are not hooked.
- Always on, no settings panel entry. Delete the plugin folder to disable.

## License

Apache 2.0; see root `LICENSE` / `NOTICE`.
