---
title: PowerGridPlus cross-mod compatibility
type: Unsorted
created_in: 0.2.6228.27061
verified_in: 0.2.6228.27061
verified_at: 2026-05-22
sources:
  - Mods/PowerGridPlus/RESEARCH.md (Appendix B / D, the mod-landscape catalogue)
  - Mods/PowerGridPlus/TODO.md (eighth bullet under "## Verification tasks")
  - .work/revolt-source/Assets/Scripts/Patches/*.cs (Re-Volt patch attributes)
  - E:\Steam\steamapps\workshop\content\544550\3243132734 (MorePowerMod installed)
  - E:\Steam\steamapps\workshop\content\544550\3579306377 (PowerOverhaul installed)
  - DedicatedServer/data/server.log (live test logs from 2026-05-22 sessions)
related:
  - ../../Mods/PowerGridPlus/RESEARCH.md
  - ../GameClasses/PowerTick.md
  - ../GameClasses/Battery.md
  - ../GameClasses/Cable.md
tags: [power, harmony]
---

# PowerGridPlus cross-mod compatibility

Cross-mod compatibility findings for Power Grid Plus against the power-related Steam Workshop mods catalogued in `Mods/PowerGridPlus/RESEARCH.md` Appendix B / D, with the specific compat-flagged cases called out by the eighth verification task in `Mods/PowerGridPlus/TODO.md`.

This page covers two kinds of finding:

- **Live-tested** against game version 0.2.6228.27061 on `DedicatedServer/` (the user's full enabled-mod set, fresh Mars2 world): MorePowerMod (3243132734) and PowerOverhaul (3579306377).
- **Code-analytical** for priority mods that are not subscribed in the developer's modconfig.xml: assessment based on the public information in Appendix B / D plus, for Re-Volt, direct reading of the source clone at `.work/revolt-source/`.

The eighth TODO bullet explicitly states "If a mod from the priority list is not enabled in the developer's modconfig, document the gap in the compat page and skip; do NOT install Workshop mods autonomously."

## Test setup
<!-- verified: 0.2.6228.27061 @ 2026-05-22 -->

Two server runs, both `-Start -New Mars2` on the dedicated server with InspectorPlus `Force Unpause Without Client = true`:

1. **Run A (with PGP)**: full user mod set including a freshly built `Mods/PowerGridPlus/PowerGridPlus/bin/Release/PowerGridPlus.dll` overlaid into `DedicatedServer/data/mods/Local_PowerGridPlus/`. Hash D034FEB497..., 91648 bytes, mtime 2026-05-22 03:20:32. World started at 03:55:56 game-clock.
2. **Run B (without PGP)**: same mod set with the PGP DLL moved out of `Local_PowerGridPlus/` (folder remains so StationeersLaunchPad still discovers it but loads no plugin). World started at 04:02 game-clock.

Both runs used the same set of currently-enabled mods from the developer's `%USERPROFILE%\Documents\My Games\Stationeers\modconfig.xml` (snapshot 2026-05-22): MorePowerMod, PowerOverhaul, Omni Transmitter Settings, BetterAdvancedTablet, JetpackHeightUnlocker, InspectorPlus, EquipmentPlus, PowerTransmitterPlus, plus a long tail of unrelated Workshop entries (~60 mods total). The dedicated server's mirror was sync'd at 03:05 by an earlier session.

## Live test results
<!-- verified: 0.2.6228.27061 @ 2026-05-22 -->

### Power Grid Plus itself loads cleanly

Run A log markers:

```
03:55:38: loading the 'Power Grid Plus' mod
03:55:38: Power Grid Plus is patching recipe for ItemCableCoilSuperHeavy
03:55:38: successfully loaded the 'Power Grid Plus' mod
```

No `[Error]` / `[Fatal]` entries attributable to PGP. Harmony `PatchAll` succeeded; the `CableCostPatches.ApplyRecipeCost()` runtime override fired for the configured `Super-Heavy Cable Cost Multiplier`. PGP was loaded only from `<DedicatedServer>/data/mods/Local_PowerGridPlus/PowerGridPlus.dll`; the `install/BepInEx/plugins/PowerGridPlus/PowerGridPlus.dll` overlay (deployed earlier via `-DeployMods`) was NOT a second load — StationeersLaunchPad's LocalModSource owned the load and BepInEx's Chainloader silently deduplicated the install/plugins copy. **Recommendation**: prefer `-SyncMods` alone when testing cross-mod compat (per `DedicatedServer/CLAUDE.md`'s duplicate-load guidance), and skip `-DeployMods` unless the user's modconfig entry points at a stale build.

### MorePowerMod (3243132734) NRE on fresh-world spawn -- not PGP's fault

Both runs produced the same NRE within ~0 seconds of `Started new game in world Mars2`:

```
NullReferenceException: Object reference not set to an instance of an object
  at omnitransmitterlargemod.StationBatteryNuclear.PoweredChanged () [0x00043] in <a16d9ebb5ea44ed5b9bd11a5f4909f60>:0
  at omnitransmitterlargemod.StationBatteryNuclear.OnInteractableUpdated (Assets.Scripts.Objects.Interactable interactable) [0x00007] in <a16d9ebb5ea44ed5b9bd11a5f4909f60>:0
  at Assets.Scripts.Objects.Interactable.set_State (System.Int32 value) [0x0007f] in <0525d3f912224509836c1d7b1b652746>:0
  at Assets.Scripts.Objects.Thing.set_Mode (System.Int32 value) [0x0002a] in <0525d3f912224509836c1d7b1b652746>:0
  at Assets.Scripts.Objects.Electrical.Battery.WaitThenUpdateChargeDisplay () [0x00120] in <0525d3f912224509836c1d7b1b652746>:0
```

Assembly GUID `a16d9ebb5ea44ed5b9bd11a5f4909f60` belongs to `omnitransmitterlargemod.dll` (MorePowerMod's internal assembly name; the file on disk is `OmniTransmitterLargeMod.dll`). The trigger path is vanilla `Battery.WaitThenUpdateChargeDisplay -> Thing.set_Mode -> Interactable.set_State`, which fires `OnInteractableUpdated`; the MorePowerMod-side `StationBatteryNuclear.OnInteractableUpdated` handler then invokes `PoweredChanged`, which dereferences a null field at IL offset 0x43.

**Causation isolation**: Run B (without PGP) reproduces the same NRE with identical call stack and identical IL offset. Therefore the NRE is a **pre-existing MorePowerMod bug, independent of Power Grid Plus**. PGP's `StationaryBatteryPatches` (which patches `Battery.GetUsedPower`, `GetGeneratedPower`, `ReceivePower`) is not on the call stack and does not influence whether `WaitThenUpdateChargeDisplay` fires nor whether the MorePowerMod handler succeeds.

**Compat verdict for MorePowerMod**: coexists with PGP at load time. The MorePowerMod NRE is a separate report worth filing with MorePowerMod's author. PGP need take no action.

**Note on rate limits**: per Appendix B, MorePowerMod's `StationBatteryNuclear` (230.4 MW capacity) and `Nuclear Wireless Battery Cell` (2.304 MW) subclass vanilla `Battery`, so PGP's `MaxBatteryChargeRate=0.002` / `MaxBatteryDischargeRate=0.007` apply automatically. The resulting numbers are 0.46 MW charge and 1.6 MW discharge per tick for the StationBatteryNuclear, and 4.6 kW charge / 16 kW discharge for the wireless cell. The wireless number is reasonable; the StationBatteryNuclear's 1.6 MW discharge-per-tick translates to a sustained ~80 MW load supported by one unit (50 tick/sec). That is intended by the mod's design ("nuclear battery for end-game megabases"), so the rate cap does not produce absurd values. Numbers were not directly snapshotted because fresh Mars2 has no spawned StationBatteryNuclear instances; InspectorPlus returned empty `objects` arrays for the battery types.

### PowerOverhaul (3579306377) coexists cleanly

Run A log: no PowerOverhaul-attributed errors. PowerOverhaul patches `PressureRegulator`, `BackPressureRegulator`, `VolumePump`, and `GasMixer` for power-draw scaling, all in namespace `Assets.Scripts.Objects.Pipes` and unrelated to PGP's electrical-class patches (Battery, Transformer, AreaPowerControl, Device, Cable, CableNetwork, PowerTick). The two mods have **no method overlap** in their Harmony attribute target sets.

**Compat verdict for PowerOverhaul**: orthogonal. Coexists cleanly with PGP. Confirmed live.

## Re-Volt (3587239682): code-analytical, must refuse-to-load
<!-- verified: 0.2.6228.27061 @ 2026-05-22 -->

Re-Volt is subscribed on the developer's machine but currently disabled in modconfig.xml, so it was not loaded in either run. The compat assessment is code-analytical, against the Re-Volt source clone at `.work/revolt-source/`.

### Patch-target overlap

Both mods inject their own `PowerTick` subclass into every `CableNetwork.PowerTick` field via Harmony constructor postfixes on `CableNetwork`, then patch `PowerTick.Initialise / CalculateState / ApplyState` with prefixes that short-circuit only when `__instance is <ThisMod>Tick`. The complete overlap table:

| Target method | Re-Volt | PowerGridPlus |
|---|---|---|
| `CableNetwork..ctor()` postfix | inject `RevoltTick` | inject `PowerGridTick` |
| `CableNetwork..ctor(Cable)` postfix | inject `RevoltTick` | inject `PowerGridTick` |
| `CableNetwork..ctor(long)` postfix | inject `RevoltTick` | inject `PowerGridTick` |
| `CableNetwork.DirtyPowerAndDataDeviceLists` postfix | yes | yes |
| `PowerTick.Initialise` prefix | reroute to `RevoltTick.Initialize_New` | reroute to `PowerGridTick.Initialize_New` |
| `PowerTick.CalculateState` prefix | reroute to `RevoltTick.CalculateState_New` | reroute to `PowerGridTick.CalculateState_New` |
| `PowerTick.ApplyState` prefix | reroute to `RevoltTick.ApplyState_New` | reroute to `PowerGridTick.ApplyState_New` |
| `PowerTick.CacheState` reverse patch | yes | yes |
| `PowerTick.CheckForRecursiveProviders` reverse patch | yes | yes |
| `Battery.GetUsedPower` | postfix (Re-Volt) / prefix (PGP) | yes |
| `Battery.GetGeneratedPower` | postfix | prefix |
| `Battery.ReceivePower` prefix | yes | yes |
| `Battery.get_IsOperable` reverse patch | yes | yes |
| `Battery.CanLogicRead` prefix | yes | yes |
| `Battery.GetLogicValue` prefix | yes | yes |
| `Transformer.GetGeneratedPower` prefix | yes | yes |
| `Transformer.GetUsedPower` prefix | yes | yes |
| `Transformer.ReceivePower` prefix | yes | yes |
| `AreaPowerControl.ReceivePower` prefix | yes | yes |
| `AreaPowerControl.GetUsedPower` prefix | yes | yes (PGP also patches `UsePower`) |
| `Device.AssessPower` prefix | yes | yes |
| `Device.SetPower` reverse patch | yes | yes |

### Failure mode

Constructor postfixes both run on every `CableNetwork` allocation; the LAST one wins the `PowerTick` field assignment. Harmony's `PatchAll` iteration order between mods is not defined (depends on which assembly loads first under StationeersLaunchPad's load sequence), so the surviving subclass is non-deterministic across sessions.

After that, both mods' prefix patches on the same vanilla method run in some order:

- Re-Volt's prefix: `if (__instance is not RevoltTick) return true;` — falls through (calls original) when `PowerGridTick` won. Returns `false` (suppresses original) when `RevoltTick` won.
- PGP's prefix: `if (!(__instance is PowerGridTick)) return true;` — falls through when `RevoltTick` won, suppresses original when `PowerGridTick` won.

Net: only ONE of the two rewrites is active per network. The other mod's prefix is dead code. This is "graceful coexistence" in the sense that nothing throws; it is **not** behavioral coexistence — the user loses whichever mod's behavior the constructor injection order discarded, with no log signal.

For `Battery.ReceivePower` / `Transformer.GetGeneratedPower` etc., the conflict is worse: both prefixes run, both mutate the same private fields (`_powerProvided`, etc.), and the original is skipped if ANY prefix returns `false`. Both prefixes WILL run regardless of return value; the last-write-wins outcome is undefined.

### Recommendation: detect and refuse-to-load

The TODO bullet lists three options:

1. Detect Re-Volt and refuse to load.
2. Patch `RevoltTick` instead.
3. Document as incompatible.

**Recommend option 1.** Option 2 is brittle (depends on Re-Volt's internal class names and field shapes, both of which can drift across Re-Volt versions). Option 3 leaves the user with undefined behavior and no log signal that anything is wrong.

A detection check in `Plugin.OnPrefabsLoaded` before `Harmony.PatchAll()` is one line:

```csharp
if (BepInEx.Bootstrap.Chainloader.PluginInfos.ContainsKey("net.sukasa.revolt") /* verify GUID */)
{
    Logger.LogFatal("Power Grid Plus is incompatible with Re-Volt; both rewrite the vanilla PowerTick. Disable one. Power Grid Plus will NOT apply its patches.");
    return;
}
```

The exact plugin GUID for Re-Volt is not verified at the time of writing; before implementing, decompile `E:\Steam\steamapps\workshop\content\544550\3587239682\*.dll` for the `[BepInPlugin]` attribute on Re-Volt's plugin class (live decompile gates Rule 2 in `Research/WORKFLOW.md`).

## Priority mods not currently subscribed
<!-- verified: 0.2.6228.27061 @ 2026-05-22 -->

The remaining compat-flagged mods from the TODO are not subscribed in the developer's Workshop folder (`E:\Steam\steamapps\workshop\content\544550\<id>/` absent). They could not be live-tested in this session. The assessments below are taken from `Mods/PowerGridPlus/RESEARCH.md` Appendix B and from public Workshop descriptions (no live decompilation done in this session).

| Workshop ID | Mod | Status | Code-analytical assessment | Action |
|---|---|---|---|---|
| 3555588082 | MoreCables | not subscribed | Adds two cable types above vanilla (super-heavy 500 kW, super-conductor 1 MW). PGP's `VoltageTier.IsAllowedOnTier` switch only covers the vanilla three (`normal` / `heavy` / `superHeavy`). MoreCables' tiers fall through to the default arm, which (per `VoltageTier.cs`) treats them as their nearest vanilla peer — likely OK for `super-conductor` (treated as `superHeavy`) but the **classification rule is unspecified**. Open question in `Mods/PowerGridPlus/RESEARCH.md` §12. | Test when subscribed. Decide whether to classify them as `superHeavy`, reject, or expose a config string. |
| 3470776044 | CableTypeSwitcher | not subscribed | Adds a `CABLETYPESWITCH` console command that converts a whole `CableNetwork` between tiers at runtime, bypassing PGP's `Cable.CanConstruct` cursor reject. The reactive burn-on-flow in `PowerGridTick.Initialize_New` (mixed-tier detection) should still fire because the conversion mutates `Cable.CableType` and the next tick's rebuild sees the mismatch. | Test when subscribed. Verify the burn fires after a CABLETYPESWITCH conversion. |
| 3287183705 | EL Switche | not subscribed | Adds a logic-controlled "switch" that spawns / removes vanilla cables (normal + heavy) at runtime. The spawned cables go through `Cable.OnRegistered`, which triggers `DirtyPowerAndDataDeviceLists`, which triggers PGP's reactive mixed-tier detection on the next tick. Should be caught by the existing backstop. | Test when subscribed. Confirm the reactive burn fires on logic-spawned cables. |
| 3575959825 | Deadly Electricity | not subscribed | Per Appendix B, patches `PowerTick` / `CableNetwork` / generator classes to add efficiency-loss-as-heat and cable-spark-on-cut mechanics. Same kind of conflict as Re-Volt: rewrites the same vanilla tick path. **Likely incompatible.** | Test when subscribed. Likely action: detect and refuse-to-load, mirroring the Re-Volt detection. |
| 3004087671 | 3.6 Megawatt Battery | not subscribed | XML stat edit; vanilla Nuclear Battery capacity raised to 3.6 MWh. Subclasses vanilla `Battery`, so inherits PGP's rate cap (3600 kJ * 0.002/tick = 7.2 kJ/tick charge, 25.2 kJ/tick discharge — small). Expected fine. | Spot-check when subscribed. |
| 3007388410 | EGC | not subscribed | XL Wireless Battery raised to 1170 kWh + new recipes. XL wireless inherits vanilla `WirelessBattery`; PGP does not rate-cap wireless cells. Expected orthogonal. | Spot-check when subscribed. |
| 3706534108 | Super XL Wireless Battery (EGC Edit) | not subscribed | Same family; raises capacity to 90 MWh. Same expected behaviour. | Spot-check when subscribed. |
| 3355912171 | BuffWirelessBatteries | not subscribed | XML stat edit on the small/large wireless cells (12 kJ -> 27 kJ, 72 kJ -> 216 kJ). Orthogonal. | Spot-check when subscribed. |
| 1785747072 | Mod: Jigawatt Battery | not subscribed | New 1.21 GW creative-scale battery, subclass of vanilla `Battery`. With PGP's `MaxBatteryDischargeRate=0.007`, that is **~8.5 MW per tick discharge** sustained, i.e. **~424 MW** continuous output at 50 tick/sec. The cap likely keeps the number "sane" in the sense of avoiding overflow, but a single such battery still powers a fleet of bases — that is the mod's intent. **May want a per-mod special case or a config-driven absolute cap** for very high `PowerMaximum` values; left as a future-design call. | Sanity-check when subscribed. |
| 2359999429 | RRI - Boost Da Powa | not subscribed | Upgraded battery cells via XML recipe + capacity tweak (200 kJ / 600 kJ / 4 MJ). Cells, not station batteries; PGP does not patch `BatteryCell`. Orthogonal. | Spot-check when subscribed. |
| 3234916147 | Better Power Mod | not subscribed | Solar / wind / Stirling / turbine buffs + battery-charger / APC / omni-transmitter raises. PGP does not patch generator output magnitudes; it patches network simulation. **Expected orthogonal** -- a buffed solar farm just lights more loads on the heavy-tier backbone. | Spot-check when subscribed. |
| 3579306377 | PowerOverhaul (gas devices) | enabled (live-tested above) | Orthogonal -- different class targets. Confirmed clean. | — |
| 3491001740 | Custom Power DeepMiner | not subscribed | Per-device draw tweak on the (combustion) deep miner only. Orthogonal. | Spot-check when subscribed. |
| 3159841144 | Super Structures Mod | not subscribed | Pressure / temp limit edits on turbine generators and reinforced windows; lets vanilla generators run harder. Orthogonal to the tick. | Spot-check when subscribed. |
| 1669621266 | 2x Solar Power | not subscribed | Solar-irradiance constant edit. Orthogonal. | Spot-check when subscribed. |
| 1475742702 | Realistic Solar Constants | not subscribed | Solar-irradiance constant edit (opposite direction). Orthogonal. | Spot-check when subscribed. |

The rest of Appendix B (recipe / content / economy / QoL mods, IC10 scripts) is expected fully orthogonal and is not enumerated here.

## Recommendations for Power Grid Plus
<!-- verified: 0.2.6228.27061 @ 2026-05-22 -->

1. **Detect Re-Volt and refuse to load.** This is the one concrete code change emerging from this compat pass. The detection lives in `Plugin.OnPrefabsLoaded` before `Harmony.PatchAll()`. The exact Re-Volt plugin GUID needs verifying via a one-time decompile (see "Recommendation: detect and refuse-to-load" above).
2. **Same pattern likely applies to Deadly Electricity** once it is subscribed and patches can be inspected. Apply the same detect-and-refuse pattern under a separate detection if the patch overlap is confirmed.
3. **Document the rest as expected-orthogonal.** Appendix B already covers the relationship per mod; this compat page is the live verification supplement.
4. **MoreCables classification rule remains open.** Until live-testable (mod subscribed), the rule should default to "treat unknown `CableType` enum values as `superHeavy`-equivalent" in `VoltageTier.IsAllowedOnTier`'s switch default; that maximises compatibility (unknown tiers act as the unrestricted backbone) without needing per-mod opt-in.
5. **Big-battery cap consideration.** Jigawatt Battery's effective ~424 MW continuous output is within the float wattage range, but a `Server - Batteries` config like `Absolute Maximum Battery Discharge Watts` could cap it independently of `PowerMaximum * MaxBatteryDischargeRate`. Left as a future-design call.

## Verification history
<!-- verified: 0.2.6228.27061 @ 2026-05-22 -->

- 2026-05-22: page created as the eighth-verification-task result for Power Grid Plus. Live tests on `DedicatedServer/` with fresh Mars2 worlds in game version 0.2.6228.27061. Two runs (with and without PGP) isolated the MorePowerMod `StationBatteryNuclear.PoweredChanged` NRE as a pre-existing MorePowerMod bug. Re-Volt assessment is code-analytical against `.work/revolt-source/Assets/Scripts/Patches/*.cs` (`PowerTickPatches.cs`, `CableNetworkPatches.cs`, `StationaryBatteryPatches.cs`, `TransformerExploitPatch.cs`, `AreaPowerControllerPatches.cs`, `DevicePatches.cs`, `BatteryLogicPatch.cs`). The remaining 15 priority mods could not be live-tested because they are not subscribed in the developer's modconfig.xml; their assessments are taken from `Mods/PowerGridPlus/RESEARCH.md` Appendix B and the public Workshop descriptions.

## Open questions
<!-- verified: 0.2.6228.27061 @ 2026-05-22 -->

- **MoreCables tier classification.** `VoltageTier.IsAllowedOnTier`'s switch should be extended to cover MoreCables' two extra `CableType` enum values once the mod is subscribed and the enum values are visible. The default-to-`superHeavy` fallback is the recommended interim policy but has not been live-tested.
- **Re-Volt plugin GUID.** The recommended detection check needs the actual `[BepInPlugin]` GUID from Re-Volt's plugin class. Verification requires a decompile of `E:\Steam\steamapps\workshop\content\544550\3587239682\*.dll`, deferred until the detection is implemented.
- **Deadly Electricity overlap detail.** The claim that Deadly Electricity patches the same `PowerTick` path is taken from Appendix B's category note; a direct read of the mod's assembly is needed to confirm and to identify which exact methods overlap. Deferred until the mod is subscribed.
- **Jigawatt Battery sanity cap.** Whether to add a separate config-driven absolute cap on per-tick battery discharge is an open design call; not blocking.
