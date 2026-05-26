# ScenarioRunner

Developer tooling. A scenario-driven runtime probe for the Stationeers dedicated server. Loads as a BepInEx plugin via StationeersLaunchPad; each scenario is a small read-and-log or reflection-driven probe that fires from a Harmony postfix on a simulation tick. Lives next to the dedi launcher at `DedicatedServer/dev-plugins/ScenarioRunner/`; ships nowhere else.

## Why this exists

Some questions about how a mod actually behaves on a live save are easier to answer with a runtime probe than with an InspectorPlus snapshot:

- "Does this patch site fire?" -> have a scenario call the patched method directly with synthetic inputs and log before / after.
- "What state does this collection settle to over N ticks?" -> have a scenario log it every N ticks and diff first vs last offline.
- "If I flip this config, what changes in the simulation?" -> run twice across two `-Start` cycles with the config flipped between them.

`tools/save-edit/` is the offline counterpart: it manipulates persisted save state. `ScenarioRunner` observes the running simulation. The two compose: edit the save offline to set up a controlled scenario, then load it with ScenarioRunner active to observe behaviour at known tick offsets. Full overview in `DedicatedServer/CLAUDE.md` under *Manipulating world state without a client*.

## Build and deploy

```powershell
dotnet build DedicatedServer/dev-plugins/ScenarioRunner/ScenarioRunner.sln -c Release
DedicatedServer/dedicated-server.ps1 -Lock -Purpose "ScenarioRunner scenario run"
DedicatedServer/dedicated-server.ps1 -DeployMods -As <id> -Mod ScenarioRunner -Configuration Release
```

The `-DeployMods -Mod ScenarioRunner` resolves under `DedicatedServer/dev-plugins/` (the launcher searches `Mods/`, then `Plans/`, then `DedicatedServer/dev-plugins/`). Detecting a dev-plugins target, the launcher mirrors the DLL and `About/` folder into `DedicatedServer/data/mods/Local_ScenarioRunner/` (the StationeersLaunchPad load path) and appends a matching `<Local Enabled="true">` entry to `DedicatedServer/install/modconfig.xml` if one is not already present. Re-running `-DeployMods` is idempotent on the modconfig side. No manual file copy or hand-edit is required.

Dev-plugin deploys deliberately do NOT write to `DedicatedServer/install/BepInEx/plugins/ScenarioRunner/`. With the same DLL in both `install/BepInEx/plugins/` and `data/mods/Local_<X>/`, BepInEx Chainloader and StationeersLaunchPad each load it, the plugin's `Awake` fires twice, every Harmony prefix is registered twice, and side-effecting patches double. We hit this exact trap during PGP battery-efficiency verification and got delta=10000 instead of 5000. The launcher additionally removes any stale `install/BepInEx/plugins/<X>/<X>.dll` left over from a pre-mirror layout, so a repo that was previously deployed the other way self-heals on the next `-DeployMods`.

## Configuration

Settings appear in `DedicatedServer/install/BepInEx/config/net.scenariorunner.cfg` after first launch.

| Key | Default | What it does |
|---|---|---|
| `Probe / Scenario` | `""` | Scenario id. Empty disables the probe. |
| `Probe / Delay Ticks` | `5` | Wait this many simulation ticks after world load before the scenario starts. |
| `Probe / Log Inventory On First Tick` | `true` | One-line dump of `Battery / Transformer / AreaPowerControl / CableNetwork / CableFuse` counts on the first scenario tick, plus a per-concrete-type Battery breakdown. Runs regardless of which Scenario is selected. |

## Pump

A Harmony postfix on `ElectricityManager.ElectricityTick` (`DedicatedServer/dev-plugins/ScenarioRunner/ScenarioRunner/SimTickPump.cs`) calls `Dispatcher.OnSimTick()` each simulation tick the world is running. `OnSimTick` deduplicates by `Time.frameCount` so multiple pump sources converge to one scenario call per simulation frame. To add a second pump (for example, on an atmospheric tick), add another `[HarmonyPatch]` class that targets that method and calls `Dispatcher.OnSimTick()` from its postfix.

ElectricityTick is the chosen pump because (a) it is a public static method on a manager class, (b) it is the same pump InspectorPlus uses for its request poller, and (c) it fires whenever `GameManager.RunSimulation` is true. On a headless dedicated server `MonoBehaviour.Update` does not reliably fire after world load, so an Update-based pump cannot replace this. See `Research/Patterns/ThingEnumerationOffMainThread.md` for the threading constraints on what the postfix can safely read.

## Scenarios

### General (work without any specific mod)

| Id | What it does |
|---|---|
| `""` (default) | Probe is dormant. Plugin still loads; no scenario fires. |
| `inventory` | Counts of every power entity in the loaded scene, plus a per-concrete-type Battery breakdown (so subclasses like `StationBatteryNuclear` show separately). Fired once on the first scenario tick. |
| `battery-charge-snapshot` | Every five simulation ticks, log `PowerStored`, `PowerMaximum`, `OnOff`, `Mode` for every `Battery`. Diff offline to compute rate / efficiency deltas over a window. |

### PowerGridPlus-specific (require `net.powergridplus` loaded)

All of these gracefully no-op and log a warning if PowerGridPlus is not loaded.

| Id | What it does |
|---|---|
| `pgp-transformer-conservation` | Every five ticks, log `Setting`, `UsedPower`, `InputNetwork.CurrentLoad`, `OutputNetwork.CurrentLoad` per Transformer. Verifies `Input draw ~= Output throughput + UsedPower` (PGP's `TransformerExploitPatches`). |
| `pgp-battery-efficiency-probe` | One-shot. Picks the first OnOff Battery with headroom and an `InputNetwork`, then calls `Battery.ReceivePower(net, 5000)` and `Battery.ReceivePower(net, 200)` directly, logging the `PowerStored` delta after each. Run twice across two `-Start` cycles (`BatteryChargeEfficiency = 1.0` vs `0.5`) to verify the efficiency clamp and the sub-500 W trickle floor. |
| `pgp-apc-idle-probe` | Every five ticks, log each `AreaPowerControl`'s attached `Battery.PowerStored`. Diff first vs last; idle APCs should hold constant (PGP's `AreaPowerControlPatches`). |
| `pgp-cable-burn-probe` | Two parts. Periodic: list every `CableNetwork` with `RequiredLoad` or `CurrentLoad > 5 kW`. One-shot at tick `Delay + 25`: reflect-invoke `PowerGridPlus.Power.PowerGridTick.TestBurnCable(10000, 10000)` against every network's `PowerTick` and tally `wouldBurn` counts by tier. Verifies PGP's burn-decision formula in isolation. |
| `pgp-tooltip-filter-probe` | One-shot. Sweeps `OcclusionManager.AllThings`, calls `thing.GetPassiveTooltip(null)` on each, inspects `PassiveTooltip.Extended` for the orange `Burned:` marker that `BurnReasonPatches.Thing_GetPassiveTooltip_Postfix` appends. Verifies the postfix's `__instance is CableRuptured` filter holds: any non-CableRuptured Thing with a `Burned:` line is a filter bug. Reports total / failed / CableRupturedSeen / CableRupturedWithBurned / OtherWithBurned plus up to five offender samples when `OtherWithBurned > 0`. Threading: calls run on a UniTask worker (overrides that touch Unity APIs may throw and get counted as failed; the aim is broad coverage, not 100 %). |

### PowerTransmitterPlus-specific (require `net.powertransmitterplus` loaded)

All of these gracefully no-op and log a warning if PowerTransmitterPlus is not loaded.

| Id | What it does |
|---|---|
| `ptp-autoaim-cache-probe` | One-shot. Finds every transmitter with a non-zero cached auto-aim target (`AutoAimState.GetCachedTarget(tx)` via reflection; loaded from the save's auto-aim side-car), then for each: clears the dish's `AutoAimUpdateFlag` (0x2000) bit, writes the current `RotatableBehaviour.TargetHorizontal` value back (same value, no slew change), and re-reads both the cache and the flag. The Harmony postfix `RotatableTargetHorizontalResetPatch` (PTP commit `14946c5`) fires regardless of value change, passes its `NetworkManager.IsServer` gate on the dedi, and calls `AutoAimState.ClearCache` which clears the cache box to 0 and raises `dish.NetworkUpdateFlags \|= 0x2000`. PASS iff every probed dish ends with cache 0 AND the flag bit set. Verifies PTP TODO #1 (SP manual-override clears the cache) and the host-side half of TODO #3 (override propagates via the per-tick payload). The client-side receive cannot be verified server-side. |
| `ptp-long-distance-link-probe` | One-shot. Enumerates linked TX-RX pairs in the scene, reads `PowerTransmitter._linkedReceiverDistance` (set by `LinkPatch` on every successful link probe) via reflection, reports distance distribution. PASS iff at least one linked pair is at >= 150 m, which is evidence that the joint mutual-aim solver (v1.7.1) and the widened SphereCast link probe still establish links at the user-tested range. Observational, not proactive: the post-load auto-aim re-solve pass (`AutoAimSaveLoadPatches`) re-runs the joint solver against every cached pair after `Thing.OnFinishedLoad`, so a link being present at scenario tick time means it survived both initial deserialisation AND the solver's fixed-point iteration. Verifies PTP TODO #4. |
| `ptp-beam-predicate-probe` | One-shot. For every `PowerTransmitter` in the scene, calls `BeamVisibility.ShouldShow(tx)` via reflection and cross-checks against an independent classification by link state and `OnOff` (managed fields, safe from the worker thread). PASS iff `ShouldShow` returned false for every unlinked TX, every linked-but-tx-off TX, and every linked-but-rx-off TX. The aim-validity gate (7 degree forward-antiparallel) is a Unity-transform read and is not independently computed from the worker, so the linked + both-on case (`ShouldShow=true` ratio) is reported as informational only; a misaimed pair can legitimately produce `ShouldShow=false` there. Verifies the v1.7.3 predicate correctness server-side against a real save. |
| `ptp-all` | One-shot. Runs `ptp-autoaim-cache-probe`, `ptp-long-distance-link-probe`, and `ptp-beam-predicate-probe` in sequence on the first scenario tick. Convenience for capturing all three results in one `-Start` cycle. |

## Adding a new scenario

Edit `ScenarioRunner/Dispatcher.cs`:

1. Add a `case` to the `Tick` switch matching the scenario id.
2. Add a `Scenario_*` method.
3. If the scenario depends on a specific mod's assembly, gate it with `RequireModAssembly(assemblyName, scenarioId)`. Use reflection (`GetModAssembly`) to reach mod-internal types and methods; no build-time dependency on other mods.
4. Document the id in this README under the right section.
5. Rebuild and redeploy. The next `-Start` picks up the new scenario.

Naming: mod-specific scenarios get a mod-tag prefix. `pgp-` for PowerGridPlus today. Adding scenarios for SprayPaintPlus / EquipmentPlus / etc would use `spp-` / `eqp-` / etc.

## Output

Every scenario emits structured `[ScenarioRunner] ...` lines to the BepInEx log at `DedicatedServer/install/BepInEx/LogOutput.log`. NOT `DedicatedServer/data/server.log`: that file carries Unity / game logs, not BepInEx plugin output. Grep:

```powershell
Select-String -Path DedicatedServer/install/BepInEx/LogOutput.log -Pattern '\[ScenarioRunner\]'
```

## Limitations

- **Reads and logs only.** Some scenarios call existing patched methods (`Battery.ReceivePower`, `PowerGridTick.TestBurnCable`) for side-effect probing, but the plugin does not spawn new Things in the world. For that, use `tools/save-edit/` to write the world state offline before `-Start`.
- **Server-side only.** Reads `OcclusionManager.AllThings` and `CableNetwork.AllCableNetworks` from the simulation thread. A connected client reads from its own simulation; clients see effects of the scenario (e.g. cable burns, battery state changes) via normal state-sync but do not receive ScenarioRunner log lines.
- **No client-UI assertions.** Cursor previews, wreckage tooltips, settings-panel rendering, IC10 chip reads -- all require a connected client.
