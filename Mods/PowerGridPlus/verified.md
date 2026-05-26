# Power Grid Plus -- verified

Behavioural verifications that have already been confirmed. The sister record
to `TODO.md`: this file lists things that are no longer open, so future agents
do not redo work. Add entries here as items leave `TODO.md`; do not remove
entries unless a re-test invalidates them.

Each entry names the claim, the method, the date and game version, and the
commit where the corresponding tooling change lives. The actual log lines and
probe output are in git history under those commits.

Game versions referenced: 0.2.6228.27061 unless otherwise noted.

## Refuse-to-load incompatible mods

- **PowerGridPlus refuses to load when Re-Volt is also loaded.** 2026-05-25.
  Mirrored `Workshop_3587239682` (Re-Volt 1.3.5) into the dedicated server's
  `data/mods/` and added the matching `<Local Enabled="true">` entry to
  `install/modconfig.xml`. Started the dedi server. PGP's
  `Plugin.OnPrefabsLoaded` -> `TryFindIncompatibleMod` walked
  `AppDomain.CurrentDomain.GetAssemblies()`, matched assembly name `ReVolt`,
  and emitted the expected fatal-level line
  (`Power Grid Plus refuses to load: incompatible mod 'Re-Volt' is also
  loaded. Both mods rewrite or extend the same vanilla power-tick /
  cable-type surface and would silently fight or guess. Disable one of them.
  No patches applied.`) without applying any Harmony patches. Reverted the
  modconfig entry and deleted Re-Volt from `data/mods/` after the test.
  Commit: `1247d99`. MoreCables (3555588082) is not currently subscribed;
  the same detection path matches its assembly names `MoreCables` and
  `MoreCablesMod`. Once MoreCables is subscribed, a single load attempt is
  enough to confirm the same code branch fires for it.

- **Negative path is silent.** 2026-05-25. With neither Re-Volt nor
  MoreCables loaded, `TryFindIncompatibleMod` returns false and PGP's
  `Plugin.OnPrefabsLoaded` proceeds with `Harmony.PatchAll`. Observed
  alongside every ScenarioRunner (then PgpVerifyHelper, then RuntimeProbe) run that did not have Re-Volt mirrored
  in: PGP logs `Power Grid Plus patches applied` with no preceding refuse
  line.

## Round 1: ported Re-Volt simulation

All eight sub-checks against the developer's Luna save (a populated multi-mod
station with 23 batteries, 41 transformers, 16 APCs, 121 cable networks, and
14 fuses). Driven by `DedicatedServer/dev-plugins/ScenarioRunner/` (was `Plans/PgpVerifyHelper/` at test time; renamed to RuntimeProbe and moved 2026-05-26, then renamed again to ScenarioRunner 2026-05-26). Commit: `1247d99`.

- **Power flows on a normal-cable network with loads + a small generator.**
  Seven `StructureTransformerSmall` / `StructureTransformerSmallReversed`
  instances on Luna are actively conducting (`UsedPower=10`,
  `OutputNetwork.CurrentLoad` between 100 W and 1200 W). Power propagates
  source -> heavy spine -> transformer -> normal spur -> load through PGP's
  rewritten tick. Captured via ScenarioRunner `pgp-transformer-conservation`
  scenario.

- **Transformer does not generate free power.**
  `StructureTransformerSmallReversed` ref=390962 reports
  `InputNetwork.CurrentLoad = 20 W = OutputNetwork.CurrentLoad (10 W) + UsedPower (10 W)`.
  Conservation holds: PGP's `TransformerExploitPatches.GetUsedPowerPatch`
  (charges the quiescent draw upstream) and `ReceivePowerPatch` (subtracts
  incoming watts from `____powerProvided`) are firing as intended.

- **Stationary battery `Charge Efficiency < 1.0` actually loses energy.**
  Two paired runs of ScenarioRunner's `pgp-battery-efficiency-probe`. The probe
  directly calls `Battery.ReceivePower(InputNetwork, powerAdded)` and reads
  the resulting `PowerStored` delta.

  | `BatteryChargeEfficiency` | Probe A (powerAdded=5000) | Probe B (powerAdded=200) |
  |---|---|---|
  | 1.0 | delta = 5000 | delta = 200 |
  | 0.5 | delta = 2500 | delta = 200 |

  At 1.0 every watt is stored. At 0.5 the >=500 W path stores half (Probe A
  drops to 2500), the sub-500 W trickle floor keeps full credit (Probe B
  stays at 200). Both arms of
  `StationaryBatteryPatches.ChargeEfficiencyControl` confirmed.

- **Stationary battery `Max Battery Charge Rate` and `Max Battery
  Discharge Rate` cap the per-tick rate.** Code review of
  `StationaryBatteryPatches.LimitMaxChargeRate` / `LimitMaxDischargeRate`
  (postfix on `Battery.GetUsedPower` / `Battery.GetGeneratedPower` clamps
  to `PowerMaximum * Settings.MaxBattery{Charge,Discharge}Rate.Value`).
  Implementation verified. The runtime IC10 `ImportQuantity` /
  `ExportQuantity` logic values (computed in
  `BatteryLogicPatches.GetLogicValuePatch`) are the same per-prefab
  numbers and were not separately read with an in-game chip; they fall
  out of the patch trivially.

- **APC has no idle power drain.** ScenarioRunner's `pgp-apc-idle-probe`
  logged each `AreaPowerControl`'s attached `Battery.PowerStored` every
  five ElectricityTicks across 145 ticks (29 snapshots per APC). 13 of 16
  APCs on Luna show exactly `+0.00` delta over the snapshot window. The
  three APCs that did drain (-160, -100, -94 W) are supplying real
  downstream draw (PGP's `AreaPowerControlPatches.UsePowerPatch` correctly
  pulls from `Battery.PowerStored` when the output network has demand).

- **Recursive / looped networks with `Enable Recursive Network Limits =
  false` do not force-burn cables.** 121 active CableNetworks on Luna (a
  station with many bridged sub-networks via transformers and APCs).
  Across multiple multi-minute runs, zero `CableRuptured` instances were
  observed except for the deliberate NEW-3 tier-burn below. PGP's
  `PowerTickPatches.CheckForRecursiveProviders` reverse-patch is not
  invoked because `EnableRecursiveNetworkLimits` defaults off.

- **Cable burn at sustained > 5 kW.** ScenarioRunner's `pgp-cable-burn-probe`
  reflect-invokes `PowerGridPlus.Power.PowerGridTick.TestBurnCable(10000,
  10000)` against every CableNetwork:

  | Total networks probed | Would-burn | normalNets (5 kW) | heavyNets (100 kW) | superHeavyNets (500 kW) |
  |---|---|---|---|---|
  | 121 | 38 | 42 | 29 | 30 |

  10 kW exceeds normal-cable `MaxVoltage` (5 kW); the formula
  `burnChance = (10000 / 5000) - 1.0 = 1.0` plus default
  `CableBurnFactor = 1.0` makes the burn deterministic, returning a
  Cable for every normal-cable network with at least one cable in its
  `CableList`. Heavy networks return null (10 kW below their 100 kW
  threshold). Super-heavy networks return null (NEW-1 carve-out:
  `EnableUnlimitedSuperHeavyCables = true` skips them even at the
  threshold). The 4-network gap (42 normal -> 38 wouldBurn) is normal
  networks with zero cables (wireless or temporarily empty).

- **End-to-end `Cable.Break()` works on a real Luna network.** Observed
  side effect of the battery-efficiency-probe Run A2 (efficiency=1.0):
  the probe charged `StructureBatteryMedium` ref=113956 from
  `PowerStored=0` to `PowerStored=5200`, energising a battery that
  happens to be on a normal-cable network. The next tick PGP's NEW-3
  device-tier rule fired the misplaced-device burn:
  `[Info :Power Grid Plus] Voltage tiers: burning a normal cable adjacent
  to misplaced Battery (Medium) (network 125729).` The
  `VoltageTier.BurnCableForMisplacedDevice` path is exercised
  end-to-end including `Cable.Break()` and `BurnReasonRegistry`
  attachment.

## Clean-load smoke on a populated multi-mod save

- **PowerGridPlus 0.1.0 Release loads cleanly on a populated 60+ mod
  dedicated server.** 2026-05-25. PGP's BepInEx log shows
  `Power Grid Plus v0.1.0 loaded; patches deferred to prefab load` ->
  `Registered 1 IC10 constants (9 -> 10)` ->
  `Extended 2 InternalEnums entries for syntax highlighting` ->
  `Power Grid Plus patches applied` ->
  `Injected 1 entries into Logicable arrays`, no `[Error]` or `[Fatal]`
  lines from PGP. The recipe overlay applies
  (`Power Grid Plus is patching recipe for ItemCableCoilSuperHeavy`).
  PGP cooperatively yields the emergency-light path when third-party
  `BatteryLight.Scripts.BatteryLightPlugin` is detected
  (`Battery Backup Light (third-party, ...) detected; Power Grid Plus
  emergency-light patches are inactive.`).

- **NEW-2 super-heavy cable cost multiplier respects the configured value.**
  2026-05-26. Three rounds on a Luna copy at multiplier 2.0 / 3.0 / 1.0.
  Initial run revealed a defect: `Plugin.OnPrefabsLoaded` fires before
  `WorldManager.LoadGameDataAsync` reads the mod's `GameData/cable-recipes.xml`,
  so the runtime patch was set first and the overlay then clobbered it back
  to 2x in every round (InspectorPlus snapshots all showed Constantan=1,
  Electrum=1 regardless of config). Fix: subscribe `CableCostPatches.ApplyRecipeCost`
  to `WorldManager.OnGameDataLoaded` (decompile line 58771, fires at line 59106
  after every overlay and `GenerateRecipieList` has run). After the fix:
  multiplier=3.0 produces two recipe-patches in server.log (overlay first,
  PGP runtime second 7s later) with BepInEx line
  `Super-Heavy Cable Cost Multiplier 3 applied: ... Constantan 1.5, Electrum 1.5`;
  multiplier=1.0 InspectorPlus snapshot of `DynamicThingRecipeComparable.AllRecipes`
  shows `ItemCableCoilSuperHeavy`: Constantan=0.5, Electrum=0.5, Time=8, Energy=800
  (the configured runtime value, replacing the overlay's 1.0/1.0). Plugin.cs +
  CableCostPatches.cs header comment updated to reflect the OnGameDataLoaded
  ordering.

- **Luna ground-truth inventory.** ScenarioRunner's `inventory` scenario:
  23 batteries (18 `StationBatteryNuclear` from MorePowerMod + 5 vanilla
  `StructureBattery` / `StructureBatteryMedium`), 41 transformers, 16
  AreaPowerControl, 121 CableNetwork (in `OcclusionManager.AllThings` and
  `CableNetwork.AllCableNetworks`), 14 CableFuse, 10 WirelessNetwork.
  Earlier InspectorPlus runs that reported 0 APCs and 0 fuses were
  truncation artifacts of `FindObjectsOfType<MonoBehaviour>` hitting the
  50 MB / 10000-entry walker caps before reaching them; the
  game-internal `OcclusionManager.AllThings` enumeration is the
  authoritative count.

## Static analysis with no further dynamic check needed

- **APC patch `UsePower` retarget is correct.** 2026-05-22. Static
  analysis on `Research/GameClasses/AreaPowerControl.md` showed four
  independent signals that Re-Volt 1.4.0's `UsePowerPatch` body
  matches `UsePower`, not `GetUsedPower`: the `powerUsed` parameter only
  exists on `UsePower`, the body mutates `_powerProvided` (a pure float
  reader would not), the filter `cableNetwork != OutputNetwork` mirrors
  `UsePower`'s vanilla `OutputNetwork` guard (not `GetUsedPower`'s
  `InputNetwork` guard), and `UsePower` is the only call site that
  decrements `Battery.PowerStored` on the output side (`PowerProvider.ApplyPower`,
  decompile line 254504, once per provider per tick). Reverting to
  Re-Volt 1:1 would either silent-no-op (HarmonyX rejects the
  parameter-mismatched prefix and vanilla `UsePower` still drains the
  battery) or bind `powerUsed = 0` (body becomes a no-op). The PGP
  retarget at `UsePower` is correct. The 2026-05-25 ScenarioRunner (then PgpVerifyHelper, then RuntimeProbe)
  apc-idle-probe run also serves as dynamic corroboration: idle APCs
  do not bleed `Battery.PowerStored` and APCs with downstream draw do.

- **`Thing.GetPassiveTooltip` universal-base postfix is perf-safe.**
  2026-05-22. The negative path (instance is not `CableRuptured`) is one
  `isinst` IL + return, no allocation, no weak-table lookup, no string
  work. Decompile cross-reference confirmed `Thing.GetPassiveTooltip` is
  called only from `WorldMouseManager.Idle` / `NormalModeThing` (cursor
  raycast, once per frame at most) and event-driven UI tooltip
  refreshes; not from any tick loop. Worst-case per-frame overhead is
  single-digit nanoseconds, not measurable on a Unity profiler.

## Research findings pinned (durable knowledge, not behaviour)

- **Cable coil prefab names resolved.** 2026-05-26.
  `$(StationeersPath)\rocketstation_Data\StreamingAssets\Data\electronics.xml`
  declares exactly three cable-coil printer recipes: `ItemCableCoil`
  (normal), `ItemCableCoilHeavy` (heavy), `ItemCableCoilSuperHeavy`
  (super-heavy). No `ItemCableCoilInsulated`: insulated cable is a
  structure-side variant of the normal cable family (same
  `Cable.Type.normal`, same `MaxVoltage = 5000 W`), crafted from the
  same `ItemCableCoil`. Documented in `Research/GameClasses/Cable.md`.

- **Cable `MaxVoltage` per tier.** 2026-05-22. Extracted from
  `$(StationeersPath)\rocketstation_Data\resources.assets` via UnityPy +
  generated type tree. Normal = 5000 W, heavy = 100000 W, super-heavy
  = 500000 W. Documented in `Research/GameClasses/Cable.md`.

- **Thing enumeration off the Unity main thread.** 2026-05-25. The
  `ElectricityManager.ElectricityTick` postfix runs on a UniTask
  ThreadPool worker; `UnityEngine.Object.FindObjectsOfType<T>()` from
  that worker crashes the engine native side intermittently. The
  game's `OcclusionManager.AllThings`
  (`ConcurrentDensePool<Thing>`), `CableNetwork.AllCableNetworks`, and
  `AtmosphericsManager.AllAtmospheres` are safe to iterate from any
  thread. Documented in
  `Research/Patterns/ThingEnumerationOffMainThread.md`.

## Tooling acceptance

- **`tools/save-edit/stationeers_save.py` round-trips a save zip
  losslessly.** 2026-05-25. Extract `Luna.zip` -> repack to
  `Luna-roundtrip.zip`; the resulting `world.xml` still parses for all
  861 cables, 23 batteries, 16 APCs etc. via the same `list` command.
  Size dropped from 1405 KB to 1336 KB (Python `ElementTree` re-emits
  XML more compactly than the game's serializer); the loader accepts
  both shapes. Commit: `d688e3e`.

- **`DedicatedServer/dev-plugins/ScenarioRunner/` runs scenarios from a Harmony postfix on
  `ElectricityManager.ElectricityTick`.** 2026-05-25. Five working
  scenarios so far: `inventory`, `battery-charge-snapshot`,
  `transformer-conservation`, `battery-efficiency-probe`,
  `apc-idle-probe`, `cable-burn-probe`. Configured via
  `install/BepInEx/config/net.scenariorunner.cfg` (was `net.pgpverifyhelper.cfg`
  at the time of the test). Output lives in
  `install/BepInEx/LogOutput.log`, NOT `data/server.log`. Commit:
  `1247d99`.

- **`dedicated-server.ps1 -DeployMods -Mod <name>` accepts Plans/
  targets.** 2026-05-25. When `Mods/<name>/` does not exist, falls
  through to `Plans/<name>/`. Mods/ wins on a tie. Plans/ entries are
  still not auto-deployed when `-Mod` is omitted (manual opt-in only).
  Commit: `d688e3e`.

- **Avoiding the BepInEx + StationeersLaunchPad duplicate-load trap.**
  2026-05-25. When the same plugin DLL exists at BOTH
  `install/BepInEx/plugins/<X>/<X>.dll` AND
  `data/mods/Local_<X>/<X>.dll`, BepInEx Chainloader and
  StationeersLaunchPad each load it, the plugin's `Awake` fires twice,
  and every Harmony prefix is registered twice -> each prefix's side
  effects double. Observed first-hand: `Battery.ReceivePower` deltas of
  10000 instead of 5000 at `BatteryChargeEfficiency = 1.0`. Fix: keep
  the DLL in exactly one path. For ScenarioRunner (then PgpVerifyHelper, then RuntimeProbe) and PowerGridPlus on
  this dedi, the canonical path is `data/mods/Local_<X>/` (loaded via
  StationeersLaunchPad through `install/modconfig.xml`); the
  `install/BepInEx/plugins/<X>/` copies are removed.
