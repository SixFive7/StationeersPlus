# PgpVerifyHelper

Developer tooling. Drives scenario-based verification of Power Grid Plus and related power mods from the simulation thread of a running dedicated server. Not a release mod; lives in `Plans/`.

## What it does

PgpVerifyHelper is a BepInEx plugin that, after world load, runs one of a known list of scenarios picked by a config string. Each scenario logs structured `[PgpVerifyHelper] ...` lines to the server log; an agent or developer can grep these out of the log instead of staging InspectorPlus request files. Pumped from a Harmony postfix on `ElectricityManager.ElectricityTick`, same way InspectorPlus drives its request poller on the headless server.

The point: most Power Grid Plus verifications are "snap a value, change a config, snap again, compute delta". Scripting them inside a plugin removes the InspectorPlus pump-quirk dance and gets reproducible numbers per server run.

## Build and deploy

```powershell
dotnet build Plans/PgpVerifyHelper/PgpVerifyHelper.sln -c Release
DedicatedServer/dedicated-server.ps1 -Lock -Purpose "PGP verify session"
DedicatedServer/dedicated-server.ps1 -DeployMods -As <id> -Mod PgpVerifyHelper -Configuration Release
```

`-DeployMods -Mod PgpVerifyHelper` resolves under `Plans/` as well as `Mods/` (the launcher checks both).

## Configuration

Settings appear in `<dedi>/install/BepInEx/config/net.pgpverifyhelper.cfg` after first launch.

| Key | Default | What it does |
|---|---|---|
| `Verify / Scenario` | `""` | Scenario id. Empty disables the helper. |
| `Verify / Delay Ticks` | `5` | Wait this many `ElectricityTicks` after world load before the scenario starts. |
| `Verify / Log Inventory On First Tick` | `true` | One-line dump of `Battery / Transformer / AreaPowerControl / CableNetwork / CableFuse` counts on the first scenario tick. |

## Scenarios

| Id | What it does |
|---|---|
| `""` (default) | Helper is dormant. Plugin still loads; nothing happens after world load. |
| `inventory` | Counts of every power entity in the loaded scene, plus a per-subtype Battery breakdown (so `StationBatteryNuclear` from MorePowerMod shows separately). One log line per category, fired once on the first scenario tick. |
| `battery-charge-snapshot` | Every five ticks, log `PowerStored`, `PowerMaximum`, `OnOff`, `Mode` for every `Battery`. Lets an agent compute charge-rate / efficiency deltas by diffing log lines over a window. |
| `transformer-conservation` | Every five ticks, log `Setting`, `_powerProvided`, `UsedPower`, `InputNetwork.CurrentLoad`, `OutputNetwork.CurrentLoad` per transformer. Verifies `InputNetwork.CurrentLoad >= _powerProvided + UsedPower`. |

Unknown scenario ids log a warning and do nothing. Add new scenarios by adding a case to `ScenarioRunner.Tick` and documenting the id here.

## Why a plugin instead of more save-edit?

Save-edit (the `tools/save-edit/stationeers_save.py` workflow) is the right tool for changing world state OFFLINE: add Things, edit fields, drop networks. PgpVerifyHelper is the right tool for taking ORDERED, RUNTIME snapshots from a known, post-tick state of the running simulation. The two compose: edit the save offline to set up a known scenario, then load it with PgpVerifyHelper active to observe behaviour.

## Limitations

- Read and log only at this stage. The scenario runner does not yet spawn new Things or mutate the world; runtime spawn of structures needs the `GameObject.Instantiate(prefab) -> OnRegistered` path mapped, which is the next iteration.
- Server-side only by design. Reads `UnityEngine.Object.FindObjectsOfType<T>()` and `CableNetwork.AllCableNetworks`; no message replication. A connected client reads these from its own simulation.
