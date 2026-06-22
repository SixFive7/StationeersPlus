# Power Grid Plus — Power System Implementation Checklist

Companion to POWER.md. Lists every code change, file affected, validation step, and ordering constraint required to land the spec. Tasks are ordered for risk-controlled landing: each phase is independently testable and rolls back cleanly if validation fails.

## 2026-06-10 decisions and current status (READ THIS FIRST)

> **PASS 2 COMPLETE (2026-06-10, later the same day):** every phase below is implemented under the
> locked decisions, built green at v0.2.0, and dedicated-server-verified on the Luna save (full
> baseline plus a forced-overload scenario). `POWER_DEVIATIONS.md` now records THIS pass's
> deviations and judgment calls (P1-P16) for developer review; the client-visual and multiplayer
> verification queue lives in `Mods/PowerGridPlus/PLAYTEST.md`. The checklist below is retained as
> the spec map; treat "deferred" wording anywhere below as historical.
>
> **P1 REWORK (2026-06-13, Option B+C):** during the deviation review the developer reworked wrong-tier
> burns. The fixed 4-tick burn cooldown is gone, replaced by state-based `SplitPendingRegistry`
> (split-landed detected by cable-count change). Tier detection runs on the worker thread (per-tick
> backstop) plus a main-thread `CableNetwork.OnNetworkChanged` subscription (immediate, robust to
> modded/future build paths); the burn itself runs on the main thread so the split lands before the
> next tick. The allocator now defers durable shed/overload lockouts on a burn-pending network
> (Option C), and the §5.7 generator-overflow burn marks its network pending too. Built green and
> dedi-verified (Luna mixed-tier injection on network 429366: one main-thread burn, correct victim,
> clean split, no spurious lockout, no regression). Full design + verification in POWER_DEVIATIONS.md
> P1; spec text updated in POWER.md §3 and §4.3.

A first implementation pass ran on 2026-06-09/10. The foundation plus the two fault subsystems (CYCLE_FAULT and producer-isolation/VVF) landed and were dedicated-server-tested on the Luna save; the larger core-math rework remained and was completed by pass 2 (see the banner above). The developer reviewed pass 1 and locked the decisions below.

**Implement ALL of it. Do NOT defer the large or risky parts — the developer was explicit about this.** Build and test after each step. Where a phase says "deferred" further down in this file, it is no longer deferred: it is required.

### Resolved decisions (locked)

- **D1 -> single architecture (option b).** Move to ONE inner-tick path: run vanilla `PowerTick.Initialise / CalculateState / ApplyState` in atomic Phases 1 and 3, NOT the injected `PowerGridTick` subclass. Deliver every PowerGridPlus behaviour through device-method postfixes plus the atomic Phase 1.5. Relocate `PowerGridTick`'s current responsibilities first -- wrong-tier cable burn (-> Phase 1.5a), the generator-overflow cable-burn rule (POWER.md §5.7), the vanilla recursive-provider belt-and-braces -- then DELETE `Power/PowerGridTick.cs` and the `PowerTickPatches` routing prefixes. **Retest every moved feature** (voltage tiers, cable burn, battery/APC caps, shed/overload, logic passthrough). This reverses pass-1 deviation D1, where `PowerGridTick` was kept and built on additively.
- **D2 -> non-mutating cable cap (option b).** Do NOT rewrite `Cable.MaxVoltage`. Remove `CableMaxApplier`'s field mutation and the cable branch of `RateApplierLoadPatches`. Add a helper (e.g. `CableMax.ForTier(Cable)`) returning the configured per-tier watt cap (0 = unlimited -> `float.MaxValue`), and route EVERY cable-cap read through it: the battery/APC headroom formulas, the §5.7 generator-overflow check, AND vanilla's own cable-burn check (it must be patched, because under D1=b vanilla `PowerTick` reads `cable.MaxVoltage` directly). No save contamination; removing the mod reverts cables to vanilla ratings. **Mention this in the README**: cable watt caps are enforced at runtime by the mod, not baked into the save. (APC `BatteryChargeRate` is also a per-instance field; keep `ApcRateApplier` OR move it to the same helper pattern -- pick one and be consistent.)
- **D3 -> cable defaults 5000 / 100000 / 0, all configurable.** Normal `5000`, heavy **`100000`** (true vanilla -- correct the spec's wrong 50000), super-heavy **`0` = unlimited**. These are the existing `CableNormalMaxWatts` / `CableHeavyMaxWatts` / `CableSuperHeavyMaxWatts` settings; only the heavy default changes.
- **D4 -> distinct failure colours (option a).** SHED = orange `#ffa500`; OVERLOAD / CYCLE_FAULT / VARIABLE_VOLTAGE_FAULT = red `#ff2626`. Precedence CYCLE > VVF > OVERLOAD > SHED. Done for transformers in pass 1; extend to all devices under D7. Resolves the spec's self-contradiction on colour.
- **D5 -> directed-SCC cycle detection (option a).** Keep the directed multigraph + Tarjan SCC (`CycleGraphBuilder`), NOT the spec's undirected bipartite DFS. The undirected model false-positives on parallel same-direction transformers / batteries; directed-SCC verified 0 false positives on the 199-segmenter Luna grid. Already implemented. Update POWER.md §4.2.5 wording to match.
- **D6 -> VVF fault+zero, PLUS a generic cable-burn fallback (option a + fallback).** KNOWN producer classes (the `ProducerClassifier` list) fault and stop generating (reversible, no cable burn); button-bearing ones flash, solar/wind/RTG are hover-only. ADDITIONALLY: a producer-LIKE device that is NOT in our known list -- a new vanilla class in a future game version, or a modded producer we have not classified -- falls back to the spec's original cable-burn handling, so it is still caught rather than silently ignored. Detect "producer-like but unknown" via the game's power-generator marker interface (candidate: `IPowerGenerator`, which the vanilla producers implement -- verify it covers all of them, see `Research/GameSystems/PowerSegmentingDevices.md`) minus our known-class set: such a device on a faulting network burns the adjacent cable with a clear reason. Document the classifier + the fallback.
- **D7 -> flash + hover on every faultable device (option a).** Generalize `BrownoutFlashBehaviour` from `Transformer` to any `Device` with an `InteractableType.OnOff`, and `TransformerFlashAttachPatches` to attach it to Battery / AreaPowerControl / PowerTransmitter / PowerReceiver / RocketPowerUmbilicalMale plus button-bearing producers (coal / gas / solid-fuel / stirling). Add the hover-countdown lines (shed / overload / cycle / VVF) to all of them, and hover-only lines on solar / wind / RTG / RocketPowerUmbilicalFemale (no button). Colour follows D4 precedence.
- **D8 -> implement everything, do not defer.** Every "deferred" item in POWER_DEVIATIONS.md is required: allocator per-input-network priority + joint shed/overload fixed-point iteration (Phase 4/5); the IC10 `Setting`-vanilla revert and the allocator reading `Setting` not `OutputMaximum` (Phase 5.3 / 0.2.7.3); the elastic-supply discharge + surplus distribution walk that populates `SoftSupplyShareCache` / `SoftDemandShareCache` (Phase 0.2.5 allocator side / 7); `SoftDemandHeadroomCalculator` (Phase 2); the soft supply/demand `GetGeneratedPower` / `GetUsedPower` postfixes (Phase 0.2.5 / 3); PT/PR transformer surrogates (Phase 6); `BurnReasonSideCar` (Phase 0.3); RocketUmbilical logic + rate-cap patches (Phase 0.2.7.5b); full-snapshot MP sync + `FaultRegistryJoinSnapshotMessage` join handshake (Phase 1.8 / 1.9); the VVF LogicType read on producer classes; README / About.xml / CHANGELOG / RESEARCH.md updates (Phase 10); and the version bump + release (Phase 12).
- **Producer-isolation is ALWAYS-ON (no toggle).** The `EnableProducerIsolation` setting added in pass 1 was removed per the developer. On the Luna save it faults ~108 SolarPanels because 8 `WallLightBattery` lights share 4 solar networks with no transformer (verified via `[PGP-VVF-DIAG]`). That is correct firing; the developer accepts it (most panels on that save are wired correctly behind transformers). Keep the one-shot `[PGP-VVF-DIAG]` BepInEx-log line that reports the producer + rigid-consumer class breakdown.

### New requirements from this session

- **Emergency-light target list (new setting).** `EnableEmergencyLights` currently applies to the hardcoded `StructureWallLightBattery` prefab only. Add a server setting -- a comma-separated prefab-name list (same shape as `ExtraHeavyCableDevices`, default `StructureWallLightBattery`) -- naming which light prefabs get the emergency-backup behaviour, so the operator can add other / modded battery lights or restrict the set. `WallLightBatteryPrefabPatch` (the Mode-interactable add) and `WallLightBatteryEmergencyTickPatch` (the toggle postfix) must iterate the configured list instead of the single hardcoded name. Note: the tick postfix targets `WallLightBattery.OnPowerTick`; if the list can name non-`WallLightBattery` classes, that needs a base-class hook or per-class patches -- scope it when implementing.
- **`[PGP-VVF-DIAG]` log location** (informational, no change requested): it is BepInEx `LogOutput.log`, NOT the in-game console. The in-game surface for fault reasons is the device hover text (delivered by D7).

### What pass-1 already landed (build-verified + dedi-tested on Luna)

Full list in POWER_DEVIATIONS.md. Summary: settings cleanup (removed `EnableVoltageTiers` / `EnableRecursiveNetworkLimits` / `EnableUnlimitedSuperHeavyCables`); cable-max settings + appliers (to be REWORKED per D2/D3); `ApcBatteryDischargeRate` + applier + registry; client-lockout fix; flash-colour split (transformers only so far); the CYCLE_FAULT subsystem (directed-SCC `CycleGraphBuilder`, `SegmentingDeviceRegistry`, `CycleFaultRegistry`, enforcement on all 7 segmenter classes, `CycleFaultStateMessage`, `LogicType.CycleFault`, OFF-reset); the VVF producer-isolation subsystem (`ProducerClassifier`, `VariableVoltageFaultDetector`, registry, enforcement via `CalculateState_New`, message; now always-on); fault-registry clear-on-load; battery LogicType repurpose removal + the 6 new LogicTypes (6581-6586); Stationpedia transformer footer cleanup; burn-reason hover Structure-target fix; the `SoftSupplyShareCache` / `SoftDemandShareCache` files (inert until the elastic pass). `ScenarioRunner` gained `pgp-fault-state-probe`.

## Phase 0 — Pre-refactor safety fixes (independent of the spec)

These land BEFORE Phase 1 because they fix existing bugs that compound with the larger refactor or that the user explicitly requested as standalone fixes. All low-risk, small-blast-radius.

### 0.1 Fix latent client-side `IsLockedOut` -> `IsShedding` bug

File: `Mods/PowerGridPlus/PowerGridPlus/Patches/TransformerExploitPatches.cs:59-66`. The current code checks `BrownoutRegistry.IsLockedOut(refId, tick)` on the client side. On clients `_lockoutUntilTick` is always empty (host-side only), so the check always returns false; the client computes vanilla output for a transformer the host has shed, producing one tick of "Powered" downstream of the broadcast.

- Change `IsLockedOut(refId, tick)` to `IsShedding(refId, tick)`. The `IsShedding` method already returns the client-mirror state when called on a client peer (see `BrownoutRegistry.cs:98-106`).
- Same fix on `OverloadRegistry`: `IsLockedOut` -> `IsOverloaded`.
- Validation: dedicated server + 1 client. Force a shed via IC10 priority rewrite. Confirm client's transformer goes to 0 W on the same tick as the host's broadcast (no one-tick lag).

### 0.2 Fix the broken burn-reason hover-tooltip injection (virtual-dispatch trap)

The existing `BurnReasonPatches.Thing_GetPassiveTooltip_Postfix` does NOT fire on `CableRuptured` hovers in-game. Diagnosis: `Thing.GetPassiveTooltip(Collider)` is `virtual` and `Structure : Thing` overrides it. `CableRuptured : SmallGrid : Structure`, so virtual dispatch routes the call to `Structure.GetPassiveTooltip`. The Postfix attached to the Thing-level method never runs. Same trap the file header of `TransformerHoverErrorPatches.cs` documents explicitly and avoids.

Decompile evidence:
- `Thing.GetPassiveTooltip(Collider hitCollider)` virtual at `Assembly-CSharp.decompiled.cs:300658`.
- `Structure.GetPassiveTooltip(Collider hitCollider)` override at `Assembly-CSharp.decompiled.cs:295853`.
- `CableRuptured : SmallGrid` (L371821), `SmallGrid : Structure` (L293474).
- `MouseManager.Idle` calls `CursorThing.GetPassiveTooltip(hitInfo.collider)` (L223130) via virtual dispatch on the runtime type.

**Primary fix**:
- File: `Mods/PowerGridPlus/PowerGridPlus/Patches/BurnReasonPatches.cs:27`.
- Change `[HarmonyPatch(typeof(Thing), nameof(Thing.GetPassiveTooltip))]` to `[HarmonyPatch(typeof(Structure), nameof(Structure.GetPassiveTooltip))]`.
- The `__instance is CableRuptured` type filter still gates correctly; the only difference is which virtual method Harmony attaches to.
- Add a header comment to the patch file mirroring `TransformerHoverErrorPatches.cs:22-27` to document the trap so a future agent does not repeat it.

**Secondary fix (re-apply after Tooltip.SetValuesForInteractable clobber)**:
- `MouseManager.Idle` at L223155-L223158 calls `Tooltip.SetValuesForInteractable(ref passiveTooltip, CursorThing, interactable)` whenever the cursor thing exposes any interactable affordance (deconstruct, wrench, pickup). `SetValuesForInteractable` at L237486 replaces the entire `PassiveTooltip` struct via `new PassiveTooltip(actionInstance, "", CursorThing)` (constructor at L288646), erasing our injected `Extended`.
- Mitigation: add a parallel Postfix on `Tooltip.SetValuesForInteractable`:
  ```csharp
  [HarmonyPostfix, HarmonyPatch(typeof(Tooltip), "SetValuesForInteractable")]
  public static void SetValuesForInteractable_Postfix(ref PassiveTooltip tooltip, Thing CursorThing, Interactable interactable) {
      if (!(CursorThing is CableRuptured)) return;
      var reason = BurnReasonRegistry.GetAttached(CursorThing);
      if (string.IsNullOrEmpty(reason)) return;
      var prefix = string.IsNullOrEmpty(tooltip.Extended) ? string.Empty : (tooltip.Extended + "\n");
      tooltip.Extended = prefix + "<color=#ffa500>Burned:</color> " + reason;
  }
  ```
  Whether this secondary patch is needed depends on whether `CableRuptured` actually exposes any Interactable affordance (the agent could not confirm without InspectorPlus). Check at probe time: snapshot the wreckage and verify whether `GetInteractable(Collider)` returns null. If null, skip the secondary patch.

**Complete `GetPassiveTooltip` patch target list (verified against the 0.2.6228.27061 decompile)**:

Per the inheritance walk: `Thing.GetPassiveTooltip` is virtual base; `Structure`, `Device`, `ElectricalInputOutput`, and five concrete classes override it. The minimal-but-exhaustive set of Harmony Postfix targets is **8 methods**:

Three catch-all targets:
1. **`Device.GetPassiveTooltip(Collider)`** (decompile L350742). Catches: `PowerConnection` (vestigial, harmless), `WindTurbineGenerator`, `LargeWindTurbineGenerator`, `RadioscopicThermalGenerator`, `PowerGeneratorSlot`, `SolidFuelGenerator`. Also catches modded producers that extend `Device` / `Electrical` / `DeviceInputOutput` without their own override.
2. **`ElectricalInputOutput.GetPassiveTooltip(Collider)`** (L373950). Catches: `Battery`, `Transformer`, `WirelessPower`, `PowerTransmitter`, `PowerReceiver`, `RocketPowerUmbilicalFemale`, `RocketPowerUmbilicalMale`. Modded EIO subclasses too.
3. **`Structure.GetPassiveTooltip(Collider)`** (L295853). Catches: `Cable`, `CableRuptured`. (`SmallGrid`/`SmallSingleGrid` don't override.)

Five per-class targets (each shorts the chain at its own body):
4. **`AreaPowerControl.GetPassiveTooltip(Collider)`** (L369754).
5. **`SolarPanel.GetPassiveTooltip(Collider)`** (L400076).
6. **`PowerGeneratorPipe.GetPassiveTooltip(Collider)`** (L375598). Also catches `GasFuelGenerator` (no own override).
7. **`StirlingEngine.GetPassiveTooltip(Collider)`** (L402943).
8. **`PowerConnector.GetPassiveTooltip(Collider)`** (L386846). PowerConnector is the dynamic-generator dock, distinct from PowerConnection.

For PowerGridPlus's two hover concerns:

- **Burn-reason hover on `CableRuptured`** (Phase 0.2): target #3 (`Structure.GetPassiveTooltip`). Filter `__instance is CableRuptured`.
- **VariableVoltageFault hover on SolarPanel / WindTurbine / RTG** (Phase 1.6.5): target #1 (catches Wind and RTG) AND target #5 (catches Solar). Filter by class in each Postfix.

For modded producer coverage: target #1 catches any modded class extending `Device` / `Electrical` / `DeviceInputOutput` etc. without its own `GetPassiveTooltip` override. A modded class that DOES override needs its own per-class patch; unavoidable in C# virtual model.

**Multi-condition precedence (decision locked)**: each Postfix target above MUST apply this precedence when more than one fault registry has the same `ReferenceId` locked at the same tick: `CYCLE_FAULT > VARIABLE_VOLTAGE_FAULT > OVERLOAD > SHED`. Emit exactly ONE fault line per hover, the highest-precedence current fault. No stacking, no "(Shedding ... 60s) (Overloaded ... 60s)" double-line. The same precedence governs the flash colour decision in `BrownoutFlashBehaviour` / `OverloadFlashBehaviour` / `CycleFaultFlashBehaviour` / `VariableVoltageFaultFlashBehaviour`: the highest-precedence currently-active fault picks the colour. Document this in each Postfix file's header so a future agent who reads only one file still sees the rule.

Per-class inheritance reference:

| Class | Override-bearing ancestor that runs at runtime |
|---|---|
| Cable, CableRuptured | Structure (L295853) |
| Battery, Transformer, WirelessPower, PT, PR, RocketUmbilicalMale, RocketUmbilicalFemale | ElectricalInputOutput (L373950) |
| AreaPowerControl | self (L369754) |
| PowerConnection | Device (L350742) |
| PowerConnector | self (L386846) |
| SolarPanel | self (L400076) |
| WindTurbineGenerator, LargeWindTurbineGenerator | Device (L350742) |
| PowerGeneratorPipe | self (L375598) |
| GasFuelGenerator | PowerGeneratorPipe (L375598, inherited) |
| PowerGeneratorSlot, SolidFuelGenerator | Device (L350742) |
| StirlingEngine | self (L402943) |
| RadioscopicThermalGenerator | Device (L350742) |

**Update RESEARCH.md**:
- `Mods/PowerGridPlus/RESEARCH.md:95` and `:162` document the patch as targeting `Thing.GetPassiveTooltip` "the universal base." After the fix, update both lines to reflect the actual targets (`Structure` + per-producer-class patches).

Validation probes:
- `pgp-burn-reason-hover-fires-probe`: burn a cable (any reason). Hover the wreckage. Assert text contains `Burned: ...` line. Before fix: fails. After fix: passes.
- `pgp-burn-reason-hover-with-interactable-probe`: same scenario where the wreckage has a deconstruct interactable. Check whether the SetValuesForInteractable clobber happens. If yes, secondary patch needed.
- `pgp-producer-hover-fires-probe`: trigger solar VARIABLE_VOLTAGE_FAULT, hover the solar panel. Assert fault line appears. Verifies the hover patch correctly targets `SolarPanel.GetPassiveTooltip` rather than the (overridden) `Thing.GetPassiveTooltip`.

### 0.2.5 Critical: SoftSupplyShareCache + supply-side postfixes (closes the partial-power gap)

Per the adversarial review of the "no partial power possible" claim, the central gap is that POWER.md §7.3's elastic discharge cap is allocator math that never reaches Phase 3's vanilla `CalculateState`. Without this lane, `Battery.GetGeneratedPower` returns the raw `PowerStored` value, and any time `0 < PowerStored < rigid_demand` on a battery-fed output net the rigid devices partial-power. This is the most common gameplay state (battery almost empty during blackout).

Same shape applies to APC, RocketUmbilical (the latter has an internal cell exactly like a battery).

Mirrors the soft-demand cache plumbing already specced in Phase 3.

- New file: `Mods/PowerGridPlus/PowerGridPlus/SoftSupplyShareCache.cs`. Parallel to `SoftDemandShareCache`. Decision locked: cache is freshness-stamped, in-memory only, self-cleaning, no explicit invalidation step. API:
  - `static Dictionary<long, (long tickWritten, float share)> _shareByRef`. Keyed by `ReferenceId`.
  - `static void SetShare(long refId, float share)`: writes `(currentNetworkTick, share)`. Phase 2 calls this for every active supplier as the joint iteration converges.
  - `static float GetShare(long refId)`: returns the cached share iff `tickWritten >= currentNetworkTick - 1`; else returns `float.MaxValue` so the GetGeneratedPower postfix falls back to vanilla (`_powerRatio * stored`). The one-tick staleness window covers the case where a supplier was active last tick but the current tick has not yet reached Phase 2.
  - No `Reset()` method. Entries age out naturally as their `tickWritten` falls behind. The dict's size is bounded by the live supplier count.
- New file: `Mods/PowerGridPlus/PowerGridPlus/Patches/BatterySupplyCapPatch.cs`. Late-priority Postfix on `Battery.GetGeneratedPower`:
  ```csharp
  __result = Mathf.Min(__result, SoftSupplyShareCache.GetShare(__instance.ReferenceId));
  ```
- New file: `Mods/PowerGridPlus/PowerGridPlus/Patches/AreaPowerControlSupplyCapPatch.cs`. Same shape on `AreaPowerControl.GetGeneratedPower`.
- New file: `Mods/PowerGridPlus/PowerGridPlus/Patches/RocketUmbilicalSupplyCapPatches.cs`. Same shape on both `RocketPowerUmbilicalMale` and `RocketPowerUmbilicalFemale`.
- File: `Mods/PowerGridPlus/PowerGridPlus/TransformerAllocator.cs`. At end of the elastic-supply pass (POWER.md §7.3), write per-device shares into `SoftSupplyShareCache.SetShare(refId, share)`.
- Validation:
  - `pgp-battery-supply-cap-postfix-probe`: battery on N with PowerStored = 100, no rate cap. Rigid load 200 on N. Tick. Phase 3 reads `Battery.GetGeneratedPower(N) = 100` from cache (the elastic decision was "deliver 100 W to fill what's possible"), Required = 200. Vanilla path: would have read raw PowerStored which equals 100 anyway in this contrived case. Useful probe shape: PowerStored = 5000, but elastic share = 100 (because the elastic algorithm decided 100 is enough or cap-limited). Assert `GetGeneratedPower(N)` returns 100, NOT 5000.
  - `pgp-battery-empty-no-partial-probe`: battery on N with PowerStored = 50, rigid load 200. Elastic algorithm computes effective_discharge = 50 (stored-limited). SoftSupplyShareCache[B] = 50. Phase 3: Potential = 50, Required = 200. With the safety-net retained (§8.0.0.2): vanilla `_powerRatio = 0.25`. Devices partial-power. UNLESS POWERTODO Phase 5 adds a Battery-as-segmenting-device shed that catches this case and zeroes the battery instead. Verify which option ships first; without Phase 5, the safety net catches this; with Phase 5, the battery enters SHED (or some new "BATTERY_DEPLETED" fault).
  - `pgp-apc-supply-cap-postfix-probe`: APC on output net N with insufficient internal cell + cable cap. Verify `AreaPowerControl.GetGeneratedPower(N)` returns the elastic-decided share.

### 0.2.7 LogicType refactor + Stationpedia documentation sync

For every LogicType PowerGridPlus adds, removes, or modifies semantics on, the corresponding Stationpedia entry must be updated. PowerGridPlus already has Stationpedia infrastructure at `Mods/PowerGridPlus/PowerGridPlus/StationpediaPatches.cs` (verified via Read). Two registration paths exist:

1. `RegisterCustomLogicTypePages()` walks `LogicTypeRegistry.All` and registers each as a Stationpedia page with `Name` + `Description`. Called via Postfix on vanilla `Stationpedia.PopulateLogicVariables`.
2. `GetDescriptionFooter(prefabName)` appends a PowerGridPlus footer to each device's Stationpedia description (Transformer / APC / Battery variants / super-heavy cable). Called via Postfix on `Localization.GetThingDescription`.

Decision locked: footers are built LAZILY with NO cache. Every `Localization.GetThingDescription` postfix call invokes `BuildTransformerFooter()` / `BuildApcFooter()` / `BuildBatteryFooter()` / `BuildSuperHeavyCableFooter()` / `BuildRocketUmbilicalFooter()` (Phase 0.2.7.5b) anew, with live `Settings.*.Value` reads inside each builder. No static `Dictionary<string, string>` cache, no `WorldManager.OnLoadComplete` warming hook, no per-prefab memoisation.

Rationale: per the Q11 architectural invariant (see Phase 0.2.7.10 below), settings are immutable for the duration of a session. Lazy reads and cached reads therefore produce identical text during play; the cache adds complexity without a behavioural win. The Stationpedia screen is opened rarely enough that rebuilding the footer string on each open has no measurable cost.

### 0.2.7.1 Add `LogicTypeRegistry` entries

- File: `Mods/PowerGridPlus/PowerGridPlus/LogicTypeRegistry.cs`. Add entries (Name + ushort + Description) for:
  - `CycleFault = 6581` — `(read-only) 1 when this device is in CYCLE_FAULT lockout (part of a closed power loop). Auto-clears after 60 s.`
  - `VariableVoltageFault = 6582` — `(read-only) 1 when this producer is in VARIABLE_VOLTAGE_FAULT lockout (wired to anything other than producers or transformers). Auto-clears after 60 s.`
  - `MaxChargeSpeed = 6583` — `(read-only) Configured per-prefab charge rate cap in Watts.`
  - `MaxDischargeSpeed = 6584` — `(read-only) Configured per-prefab discharge rate cap in Watts.`
  - `ChargeSpeed = 6585` — `(read-only) Actual charge rate this tick in Watts, after elastic-supply allocation.`
  - `DischargeSpeed = 6586` — `(read-only) Actual discharge rate this tick in Watts, after elastic-supply allocation.`
- File: `Patterns/Logic/LogicTypeNumbers.cs`. Append all six constants.
- File: `Patterns/Logic/README.md`. Add the catalogue rows. Bump "Next free slot" from 6583 to 6587.

### 0.2.7.2 Remove the prior PGP repurpose on Battery

- File: `Mods/PowerGridPlus/PowerGridPlus/Patches/BatteryLogicPatches.cs`. Remove the `ImportQuantity` (29) and `ExportQuantity` (31) exposure on `Battery.CanLogicRead` and `Battery.GetLogicValue`. Vanilla meaning reverts (which is nothing meaningful on Battery; the LogicTypes are slot-related and Battery has no slots).
- Replace with `MaxChargeSpeed` and `MaxDischargeSpeed` (new PGP LogicTypes) that return the same values from `StationaryBatteryPatches.GetChargeCap` / `GetDischargeCap`.
- Also add `ChargeSpeed` and `DischargeSpeed` reads that look up `SoftDemandShareCache[refId]` and `SoftSupplyShareCache[refId]` respectively. Default 0 if no entry.
- Decision locked: `ChargeSpeed` and `DischargeSpeed` are LIVE reads, not latched copies. The `GetLogicValue` postfix reads the SAME internal field that Phase 2 writes during allocator iteration (specifically: the `SoftDemandShareCache[refId].share` and `SoftSupplyShareCache[refId].share` values themselves). NO separate latch field is created, NO copy step from cache to a latched mirror at end-of-tick. IC10 reads see the most recent post-Phase-2 value as written by the cache. Between ticks the value reflects the most recent allocator decision (the cache freshness stamp from Q4 guarantees the value is at most one tick stale).
- Same decision applies to APC (Phase 0.2.7.5) and RocketUmbilical (Phase 0.2.7.5b) `ChargeSpeed` / `DischargeSpeed` reads. All three device families share this rule.

### 0.2.7.3 Update `BuildTransformerFooter()`

- File: `Mods/PowerGridPlus/PowerGridPlus/StationpediaPatches.cs:119-144`. Rewrite the transformer footer to reflect:
  - `Setting` is the active throttle (vanilla writable from IC10, `[0, OutputMaximum]`); default init `Setting = OutputMaximum`. NOT redirected.
  - In-world knob writes Priority. Labeller tool writes Priority. Other in-world writers redirect to Priority.
  - All allocator and shed/overload formulas use `Setting` (not OutputMaximum directly).
  - VVF, CycleFault, Shedding, Overloaded LogicTypes available.
- Verify there is no leftover "Setting reads return OutputMaximum, writes redirect to Priority" wording.

### 0.2.7.3b Priority value range: zero = lowest, no upper cap

Decision locked: Priority accepts non-negative integers with no upper cap. `Priority = 0` is the lowest possible priority (the device is still allocated, just last). It is NOT a sentinel meaning "disabled" or "always shed". Allocator sort: lowest priority sheds first, ties broken by demand DESC then ReferenceId ASC.

- In-world knob writes: clamp at 0 on the low end. Negative input (only reachable via IC10 `s` write) clamps to 0. No max-cap clamp; any positive integer is accepted.
- Labeller tool writes: same validation -- clamp at 0, no upper cap.
- IC10 `s Priority N` writes: same validation. Negative N clamps to 0; positive N stored as-is (subject to int max but no PGP-imposed cap).
- File: `Mods/PowerGridPlus/PowerGridPlus/Patches/TransformerPriorityLogicPatches.cs`. The `SetLogicValuePatch` for `LogicType.Setting` (redirected to Priority) and any direct Priority writer must apply `Mathf.Max(0, requestedValue)`. NO `Mathf.Min(maxCap, ...)`.
- File: any knob-handler patch (`KnobInteractablePatch` or equivalent). Same clamp.
- File: any Labeller tool patch. Same clamp.
- Validation:
  - `pgp-priority-zero-lowest-probe`: T1 with Priority = 0, T2 with Priority = 100 on the same input net. Force shed cascade. Assert T1 sheds first (lower priority sheds first).
  - `pgp-priority-no-upper-cap-probe`: write `s Priority 999999` via IC10. Assert stored value is 999999 (no clamp). Allocator sort treats it as highest priority.
  - `pgp-priority-negative-clamp-probe`: write `s Priority -50` via IC10. Assert stored value is 0 (clamped from below).
  - `pgp-priority-labeller-clamp-probe`: use the Labeller tool to set Priority to -10. Assert stored value is 0.

### 0.2.7.4 Update `BuildBatteryFooter()`

- File: `Mods/PowerGridPlus/PowerGridPlus/StationpediaPatches.cs:159-175`. Append a paragraph mentioning the four new Battery LogicTypes (`MaxChargeSpeed`, `MaxDischargeSpeed`, `ChargeSpeed`, `DischargeSpeed`) and explicitly note that the previous `ImportQuantity` / `ExportQuantity` repurpose is REMOVED (breaking change for legacy IC10 scripts).

### 0.2.7.5 Update `BuildApcFooter()` + add APC LogicType exposure

- File: `Mods/PowerGridPlus/PowerGridPlus/Patches/AreaPowerControlPatches.cs` (or a new `AreaPowerControlLogicPatches.cs`). Extend `CanLogicRead` and `GetLogicValue` to expose the four new LogicTypes on APC: `MaxChargeSpeed`, `MaxDischargeSpeed`, `ChargeSpeed`, `DischargeSpeed`. Reads return:
  - MaxChargeSpeed = `Settings.ApcBatteryChargeRate.Value` (or the inserted cell-aware effective cap if one is computed).
  - MaxDischargeSpeed = `Settings.ApcBatteryDischargeRate.Value`.
  - ChargeSpeed = `SoftDemandShareCache.GetShare(__instance.ReferenceId)`, defaulting to 0.
  - DischargeSpeed = `SoftSupplyShareCache.GetShare(__instance.ReferenceId)`, defaulting to 0.
- File: `Mods/PowerGridPlus/PowerGridPlus/StationpediaPatches.cs:146-157` (`BuildApcFooter`). Append a paragraph documenting the four new LogicTypes and the soft-power system explanation (see 0.2.7.9 below).

### 0.2.7.5b Add RocketUmbilical LogicType exposure + settings + Stationpedia entries

Decisions locked:
- Three new settings in `Server - Rocket Umbilical` section: `EnableRocketUmbilicalLimits` (true), `RocketUmbilicalChargeRate` (10000 W default, matches vanilla `PowerMaximum`), `RocketUmbilicalDischargeRate` (10000 W default).
- The master toggle `EnableRocketUmbilicalLimits` gates the four LogicType exposures: if false, the four LogicTypes are not exposed on Male / Female and the umbilical reverts to vanilla rate behaviour (transfer up to `PowerMaximum` per tick implicit).
- One pair = one set of settings (shared by Male and Female halves).

Implementation:

- File: `Mods/PowerGridPlus/PowerGridPlus/Settings.cs`. Bind the three new entries in the `Server - Rocket Umbilical` section. Each `ConfigDescription` starts with `(Server-authoritative)`. Order tags 10 / 20 / 30 per the project's standard spacing.
- New file: `Mods/PowerGridPlus/PowerGridPlus/Patches/RocketUmbilicalLogicPatches.cs`. Extend `CanLogicRead` and `GetLogicValue` on `RocketPowerUmbilicalMale` and `RocketPowerUmbilicalFemale` to expose the four LogicTypes when `Settings.EnableRocketUmbilicalLimits.Value` is true. Reads:
  - `MaxChargeSpeed` -> `min(Settings.RocketUmbilicalChargeRate.Value, InputConnection.GetCable()?.MaxVoltage ?? float.MaxValue)`.
  - `MaxDischargeSpeed` -> `min(Settings.RocketUmbilicalDischargeRate.Value, OutputConnection.GetCable()?.MaxVoltage ?? float.MaxValue)`.
  - `ChargeSpeed` -> `SoftDemandShareCache.GetShare(__instance.ReferenceId)`, default 0.
  - `DischargeSpeed` -> `SoftSupplyShareCache.GetShare(__instance.ReferenceId)`, default 0.
- New file: `Mods/PowerGridPlus/PowerGridPlus/Patches/RocketUmbilicalRateCapPatches.cs`. Same pattern as `StationaryBatteryPatches`: Postfix on `GetUsedPower` / `GetGeneratedPower` to clamp the vanilla "full PowerMaximum per tick" behaviour down to the configured rate cap. Bypassed when `EnableRocketUmbilicalLimits = false`.
- Add new entries to `StationpediaPatches.GetDescriptionFooter` for prefab names `StructureRocketPowerUmbilicalMale` / `StructureRocketPowerUmbilicalFemale` (verify exact prefab names via InspectorPlus probe). Footer includes the same four-LogicType wording, the soft-power explanation (see 0.2.7.9), and a note that the master toggle controls participation.
- **Female has no OnOff button (decision locked)**: the Female side gets ONLY a `GetPassiveTooltip` postfix that surfaces the fault hover text + countdown (hover-only fault visual, same as Solar / Wind / RTG). Extend the universal-base `Thing.GetPassiveTooltip` postfix in Phase 1.6.5 to also recognise `RocketPowerUmbilicalFemale`. NO material-color hook, NO LED indicator, NO panel-switch swap, NO other visual on the Female. Male keeps the on-button flash path via `BrownoutFlashBehaviour` attached to its OnOff button.
- **Female idle hover (Q10 carry-over, decision locked)**: when the Female is NOT in any fault state, the hover text is exactly what vanilla provides (whatever `RocketPowerUmbilicalFemale.GetPassiveTooltip` returns natively after the `ElectricalInputOutput` base + any docking-state text). PGP injects NO text on the Female when no fault is active. The Female's fault-line append is gated on `IsShedding || IsOverloaded || IsCycleFaulted || IsVariableVoltageFaulted` returning true for the Female's ReferenceId at the current tick; otherwise the Postfix is a no-op pass-through. Idle hover = vanilla-only.
- Validation:
  - `pgp-rocket-umbilical-rate-cap-probe`: dock a rocket. Probe the RocketUmbilical Female's `GetGeneratedPower` and confirm it caps at `Settings.RocketUmbilicalDischargeRate.Value` regardless of how much PowerStored it holds.
  - `pgp-rocket-umbilical-toggle-off-probe`: set `EnableRocketUmbilicalLimits = false`. Reload. Confirm vanilla behaviour (full PowerMaximum per tick) and that the four LogicTypes are not exposed.
  - `pgp-rocket-umbilical-female-hover-fault-probe`: force a SHED on the umbilical pair (cut upstream supply). Hover the Female: text shows `(Shedding: Insufficient upstream supply! Xs)` with countdown. Hover the Male: button flashes orange + same hover text.

### 0.2.7.9 Soft-power system explanation (cross-device Stationpedia text)

Every device that participates in the soft-power surplus system (Battery family, APC, RocketUmbilical pair) gets an additional Stationpedia paragraph that explains why `ChargeSpeed` < `MaxChargeSpeed` is the normal steady state. Single source string to append to each footer:

```
{HEADER:SOFT POWER SYSTEM}
Charge and discharge rates are elastic. `MaxChargeSpeed` and `MaxDischargeSpeed` report the configured upper caps; `ChargeSpeed` and `DischargeSpeed` report the ACTUAL rate this tick after PowerGridPlus allocates the network's surplus.

When upstream power supply has plenty of slack, `ChargeSpeed` approaches `MaxChargeSpeed`. When other batteries on the same input network are competing for the same surplus, each receives a proportional share, so `ChargeSpeed` is lower than `MaxChargeSpeed` by design.

Similarly, `DischargeSpeed` stays at 0 when downstream rigid demand is fully covered by generators or upstream transformers (the battery only discharges to fill a shortfall). `DischargeSpeed` approaches `MaxDischargeSpeed` when the device alone has to cover the full downstream rigid demand.

This is intentional: the elastic system prevents producers and other batteries from cycling rapidly on / off, and avoids wasted energy round-trips through batteries. Use `ChargeSpeed` / `DischargeSpeed` in IC10 scripts to monitor live flow; use `MaxChargeSpeed` / `MaxDischargeSpeed` to read the configured caps.
```

Apply this paragraph to:
- `BuildBatteryFooter()` for all three battery prefab variants.
- `BuildApcFooter()`.
- New `BuildRocketUmbilicalFooter()` for both umbilical halves.

A future modder adding a new soft-power device should copy the same paragraph into their device's footer.

### 0.2.7.6 Update `BuildSuperHeavyCableFooter()`

- File: `Mods/PowerGridPlus/PowerGridPlus/StationpediaPatches.cs:103-117`. Replace the `EnableUnlimitedSuperHeavyCables` mention (removed setting) with the new `CableSuperHeavyMaxWatts` setting wording: "Super-heavy cable's Watts cap is configurable (server setting `Super Heavy Cable Max Watts`, default 0 = unlimited)." Add a per-tier note for normal and heavy cable too.

### 0.2.7.7 Audit Stationpedia per-device descriptions for stale text

Walk every footer string in `StationpediaPatches.cs` for references to removed concepts:
- "10 seconds" -> "60 seconds" (the lockout window changed).
- "2-tick shortfall tolerance" -> removed (we now use the joint fixed-point iteration; no tolerance).
- "Setting redirect" -> removed (Setting is now vanilla).
- "ImportQuantity / ExportQuantity" -> replace with the new four Battery LogicTypes.
- Any reference to flashing orange button for transformer overload -> RED (per the §11 colour split).

### 0.2.7.8 Validation

- `pgp-stationpedia-logictypes-registered-probe`: after load, walk the Stationpedia logic-types page list and assert every new LogicType (CycleFault, VariableVoltageFault, MaxChargeSpeed, MaxDischargeSpeed, ChargeSpeed, DischargeSpeed) appears with the correct name + description.
- `pgp-stationpedia-footers-current-probe`: assert each device's Stationpedia entry contains the post-refactor wording (no stale "Setting redirect" text, etc.).

### 0.2.7.10 Settings-immutable-mid-session architectural invariant (Q11)

Decision locked: PowerGridPlus settings cannot change mid-game. Once a world is loaded, every `Settings.*.Value` read returns the same value for the rest of the session. Implementation may assume settings are immutable and skip any "settings changed" event hookup, change-listener registration, or cache-invalidation logic.

Practical consequences:
- Stationpedia footers (Phase 0.2.7) read settings lazily without caching.
- `CableMaxApplier.Apply()` (Phase 1.5) and `ApcRateApplier.Apply()` (Phase 1.5.5) run once at mod load; no re-application path.
- Per-tick allocator math reads settings directly without staleness concerns.
- Player-facing wording: any mid-game settings edit (via StationeersLaunchPad panel) takes effect only after a world reload. Document this once in the README.md Settings section, not on every individual footer.

### 0.3 Implement `BurnReasonSideCar` for save/load persistence

Goal: per-instance burned-cable reasons survive save/load via the established `PrioritySideCar` / `PassthroughSideCar` / `AutoAimSideCar` / `GlowSideCar` pattern. Verified mod-removal safe (orphan side-car is dropped on next vanilla save; world.xml never tainted).

- New file: `Mods/PowerGridPlus/PowerGridPlus/BurnReasonSideCar.cs`. Mirrors `PassthroughSideCar.cs` exactly. Components:
  - `const string SideCarEntryName = "pwrgridplus-burnreason.xml";`
  - `internal static BurnReasonSideCarData PendingSaveSnapshot;`
  - `internal static Dictionary<long, string> LoadedReasons;`
  - `internal static BurnReasonSideCarData Snapshot()`: iterates `BurnReasonRegistry.SnapshotAttached()`, emits `(ReferenceId, Reason)` pairs.
  - `internal static void WriteSideCar(string zipPath, BurnReasonSideCarData data)`: writes XML to the ZIP, deletes entry if data is empty.
  - `internal static Dictionary<long, string> ReadSideCarFromDir(string tempDirPath)`: reads XML.
  - POCO: `BurnReasonSideCarData { List<BurnReasonEntry> Entries }`, `BurnReasonEntry { long ReferenceId; string Reason }`. NO `Version` field, NO schema-versioning element on either the root or per-entry. Follows the SprayPaintPlus side-car pattern (`Mods/SprayPaintPlus/SprayPaintPlus/PaintSideCar.cs`): a future schema change is handled by adding new XML elements with sensible defaults rather than bumping a version number.
  - Parse-failure handling: every read path wraps `XmlSerializer.Deserialize` in `try { ... } catch (Exception ex) { Logger.LogWarning($"BurnReasonSideCar parse failed: {ex.Message}"); return null; }`. The per-Thing tooltip postfix treats a null return as "no side-car"; the hover falls back to whatever the generic Postfix produces, which on a CableRuptured Thing degrades to the vanilla cable-burned hover with no Burned: line. No exceptions escape into the save/load pipeline.
- New file: `Mods/PowerGridPlus/PowerGridPlus/Patches/BurnReasonSaveLoadPatches.cs`. Three Harmony patch classes:
  - `SaveHelperSaveBurnReasonSideCarPatch`: `[HarmonyPatch(typeof(SaveHelper), "Save", new[] { typeof(DirectoryInfo), typeof(string), typeof(bool), typeof(CancellationToken) })]`. Prefix: `BurnReasonSideCar.PendingSaveSnapshot = BurnReasonSideCar.Snapshot()`. Postfix: wraps `__result` with `WriteSideCarAfterSave`. Argument-type array required to disambiguate from public `Save(string, CancellationToken)` (HarmonyX would raise `AmbiguousMatchException` otherwise).
  - `XmlSaveLoadLoadWorldBurnReasonSideCarPatch`: `[HarmonyPatch(typeof(XmlSaveLoad), nameof(XmlSaveLoad.LoadWorld))]` postfix. Reads `<tempDir>/pwrgridplus-burnreason.xml`, populates `BurnReasonSideCar.LoadedReasons`.
  - `ThingOnFinishedLoadBurnReasonPatch`: `[HarmonyPatch(typeof(Thing), nameof(Thing.OnFinishedLoad))]` postfix. Filter `if (!(__instance is CableRuptured)) return;`. Look up `__instance.ReferenceId` in `LoadedReasons`. Call `BurnReasonRegistry.RestoreFromSideCar(__instance, reason)`. Critical: do NOT use `RegisterPending` (which is for live burns, not save restoration).
- Modify `Mods/PowerGridPlus/PowerGridPlus/BurnReasonRegistry.cs`:
  - Add `private static readonly ConcurrentDictionary<long, string> _attachedByReference = new ConcurrentDictionary<long, string>();`. Updated alongside the existing `_attached` `ConditionalWeakTable` (which has no enumeration support on .NET Framework 4.7.2).
  - Wherever `Attach(object, string)` is called, also `_attachedByReference[refId] = reason`. Today the only call site is `BurnReasonPatches.CableRuptured_OnRegistered_Postfix`. Change `Attach`'s signature to take `Thing wreckage` (so the ReferenceId is recoverable), OR keep `object` and cast internally.
  - Add `internal static IEnumerable<KeyValuePair<long, string>> SnapshotAttached() => _attachedByReference.ToArray();`.
  - Add `internal static void RestoreFromSideCar(Thing wreckage, string reason)`: wraps `Attach` plus `_attachedByReference[wreckage.ReferenceId] = reason`.
  - Optional purge: on `Snapshot()` call, walk `_attachedByReference` and drop entries where `Thing.Find(refId)` returns null. Prevents unbounded growth across cable churn.
- Validation:
  - `pgp-burn-reason-save-load-probe`: trigger a cable burn with a known reason. Save the world. Reload. Assert the burned cable's hover text still shows the original reason.
  - `pgp-burn-reason-mod-removal-probe`: trigger a burn with reason. Save. Disable PowerGridPlus. Load the save in vanilla. Assert world loads cleanly (no XSD error, no missing-type exception). Assert wreckage shows blank vanilla hover (no Burned: prefix).
  - `pgp-burn-reason-reinstall-probe`: continuing from the above, re-enable PowerGridPlus without making a vanilla save in between. Load. Assert the burn reason is back on the hover.
  - `pgp-burn-reason-vanilla-save-purge-probe`: continuing from removal: save in vanilla once, then re-enable PGP and load. Assert the side-car was dropped on the vanilla save and reasons are gone.

### 0.4 Fault registries are in-memory only (NOT serialized)

Decision locked: CYCLE_FAULT, VARIABLE_VOLTAGE_FAULT, OVERLOAD, SHED registries are transient runtime state. They are NOT serialized to the save side-car and they DO NOT persist across world load. The registries clear on world load; the first tick after load recomputes any active faults from current topology.

- Files: `BrownoutRegistry.cs`, `OverloadRegistry.cs`, `CycleFaultRegistry.cs` (Phase 1.7), `VariableVoltageFaultRegistry.cs` (Phase 1.6.5). Each registry's `_lockoutUntilTick` dict starts empty at every world load. No save-side-car path exists for any of them. No XML schema, no serializer, no Harmony patch on `SaveHelper.Save` for these dicts.
- Contrast with `BurnReasonSideCar` (Phase 0.3): burn reasons DO persist because the burned cable wreckage itself persists. The fault registries are conceptually different: the fault is a transient lockout, not a permanent state of a permanent entity.
- Implementation note: every registry exposes `ClearAll()`. Wire `ClearAll()` into a Harmony Postfix on `XmlSaveLoad.LoadWorld` (the same patch site as the burn-reason load) so that re-loading a world wipes any leftover in-memory state from a prior session. Avoids surprises when a host hot-swaps saves without restarting the game.
- First-tick recompute applies the FULL 60s lockout (120 ticks) to any rediscovered violation. NO grace period, NO shortened first-load duration. A save written with an active cycle, overload, or producer-isolation condition produces a fresh full-duration CYCLE_FAULT / OVERLOAD / VARIABLE_VOLTAGE_FAULT lockout on the first tick after load. SHED is the same: if the topology still warrants undersupply on first tick, SHED lights up immediately with the full 60s remaining.
- Validation:
  - `pgp-fault-registry-not-persisted-probe`: trigger a SHED on a transformer. Save the world. Reload. Assert `BrownoutRegistry.IsShedding(refId, currentTick)` returns false immediately after load. Assert the transformer's on/off button is NOT flashing.
  - `pgp-fault-registry-recomputes-after-load-probe`: same scenario but the underlying topology still warrants the shed. Save, reload, tick once. Assert the registry re-fires the SHED on the same transformer.
  - `pgp-fault-registry-hot-swap-probe`: load save A (with active shed). Without exiting to main menu, load save B (no shed). Assert no leftover shed state from A bleeds into B.
  - `pgp-fault-registry-full-60s-after-load-probe`: build a save with an active cycle (two transformers in a loop, both currently CYCLE_FAULT'd with 30s remaining). Save. Reload. Tick once. Assert both transformers carry CYCLE_FAULT with 60s remaining (NOT 30s, NOT 0s, NOT a grace-period reduction). Verifies the first-tick recompute applies full lockout duration.

## Phase 1 — Cleanup of dead toggles and code

Goal: remove the two settings that the spec mandates always-on, plus their conditional code paths. Smallest blast radius; no behaviour change for default config.

### 1.1 Remove `EnableVoltageTiers` setting

- File: `Mods/PowerGridPlus/PowerGridPlus/Settings.cs`. Delete the `EnableVoltageTiers` field and its `Config.Bind` call. Delete the `ExtraHeavyCableDevices` field if it was only meaningful under the toggle (verify with grep first).
- File: `Mods/PowerGridPlus/PowerGridPlus/VoltageTier.cs`. Remove any `if (!Settings.EnableVoltageTiers.Value)` short-circuits; the rules apply unconditionally.
- File: `Mods/PowerGridPlus/PowerGridPlus/Patches/VoltageTierPatches.cs`. Same cleanup.
- File: `Mods/PowerGridPlus/PowerGridPlus/Patches/CableNetworkPatches.cs`. Same cleanup.
- Validation: dedi load with a save that has mixed cable tiers should burn at junctions every tick. Existing voltage-tier scenario (if any) should still pass.

### 1.2 Remove `EnableRecursiveNetworkLimits` setting (decision locked)

Decision locked: delete the suppression Prefix AND the setting entirely. Vanilla cycle burn runs unsuppressed as belt-and-braces alongside PowerGridPlus's CYCLE_FAULT detection (Phase 1.7). No toggle gates either path.

- File: `Mods/PowerGridPlus/PowerGridPlus/Patches/PowerTickPatches.cs`. Delete the Harmony Prefix on `PowerTick.CheckForRecursiveProviders` (currently around lines 65-69). The Prefix that suppressed vanilla's burn was added on 2026-06-07; remove it. Vanilla's check now runs unconditionally.
- File: `Mods/PowerGridPlus/PowerGridPlus/Settings.cs`. Delete the `EnableRecursiveNetworkLimits` ConfigEntry `Config.Bind` line.
- File: `Mods/PowerGridPlus/PowerGridPlus/Power/PowerGridTick.cs`. Find the `if (Settings.EnableRecursiveNetworkLimits.Value) PowerTickPatches.CheckForRecursiveProviders(this);` line. Either delete the whole file (it's dead code post-atomic-refactor anyway) or simplify the conditional out.
- Context: the prefix exists only because the atomic refactor (which inlines vanilla `CalculateState` instead of subclassing it) lost the prior `PowerGridTick` subclass path that could conditionally skip the check. With the toggle going away the prefix has nothing to do; delete both together.
- Grep step: search the codebase for `EnableRecursiveNetworkLimits` (case-sensitive) and remove every remaining reference. Targets include any `Settings.EnableRecursiveNetworkLimits.Value` reader, any `RESEARCH.md` mention, any commit-message log inside `RESEARCH.md`, and any test/probe condition that touched the toggle. After the sweep, a fresh `grep -r EnableRecursiveNetworkLimits Mods/PowerGridPlus/` returns zero hits.
- Validation: dedi load with a cycle-bearing save (build one). After tick 1 a fuse or cable should break, splitting the cycle.

### 1.5 Cable max settings (replaces `EnableUnlimitedSuperHeavyCables`)

Goal: expose the three cable tier Watts caps as server-authoritative settings, with `0` meaning unlimited. Subsumes the existing `EnableUnlimitedSuperHeavyCables` toggle.

- File: `Mods/PowerGridPlus/PowerGridPlus/Settings.cs`. Add three settings in the `Server - Cable Simulation` group:
  - `CableNormalMaxWatts` (default 5000, range int 0..int.MaxValue, description "Watts cap for normal cable. 0 = unlimited.").
  - `CableHeavyMaxWatts` (default 50000, range int 0..int.MaxValue, description "Watts cap for heavy cable. 0 = unlimited.").
  - `CableSuperHeavyMaxWatts` (default 0, range int 0..int.MaxValue, description "Watts cap for super-heavy cable. 0 = unlimited (default; super-heavy never burns).").
- File: `Mods/PowerGridPlus/PowerGridPlus/Settings.cs`. Delete the `EnableUnlimitedSuperHeavyCables` field and its `Bind` call. The behaviour is now `CableSuperHeavyMaxWatts = 0`.
- New file: `Mods/PowerGridPlus/PowerGridPlus/CableMaxApplier.cs`. Static method `Apply()` called at mod load (from `Plugin.cs`'s `OnAllModsLoaded`). Walks every `Cable` prefab via `Object.FindObjectsOfType<Cable>(includeInactive: true)` (or vanilla prefab lookup) and rewrites `cable.MaxVoltage` based on `cable.CableType`. Conversion: `0 => float.MaxValue` (or a large sentinel like `1e9f` to keep arithmetic stable); non-zero stays as-is.
- File: `Mods/PowerGridPlus/PowerGridPlus/Plugin.cs`. Hook `CableMaxApplier.Apply()` in the mod's startup, after Harmony patching but before the first electricity tick.
- File: any code that previously checked `Settings.EnableUnlimitedSuperHeavyCables.Value` (likely in cable burn patches): remove the conditional. The per-prefab `MaxVoltage` is now authoritative; super-heavy at 0/`float.MaxValue` never satisfies `MaxVoltage < _actual` and never burns.
- File: any code that uses cable max for the headroom formula (transformer, APC, soft demand caps in `StationaryBatteryPatches.cs`): keep reading `cable.MaxVoltage` directly. With the prefab values patched at startup, the read returns the right number.
- Validation:
  - Setting `CableNormalMaxWatts = 0` makes normal cables never burn. Verify by overloading a normal-tier network and confirming no cable breaks.
  - Setting `CableSuperHeavyMaxWatts = 100000` makes super-heavy cables burn above 100 kW. Verify by overloading a super-heavy network and confirming cable break.
  - Default settings (5000/50000/0) match historical behaviour exactly. Existing scenarios pass.

### 1.5.5 Add `ApcBatteryDischargeRate` setting + APC field handling

Goal: add the discharge counterpart for APCs (vanilla has no `BatteryDischargeRate` field) and arrange for `AreaPowerControl.BatteryChargeRate` to actually reflect the configured value at runtime.

- File: `Mods/PowerGridPlus/PowerGridPlus/Settings.cs`. Add `ApcBatteryDischargeRate` ConfigEntry in `Server - Area Power Control` group, default 1000 W, Order 17 (between `ApcBatteryChargeRate` and `EnableAreaPowerControlLogicPassthrough`). Description: "(Server-authoritative) Maximum Watts the APC's inserted battery cell can discharge per tick to the output network."
- New file: `Mods/PowerGridPlus/PowerGridPlus/ApcRateApplier.cs`. Static method `Apply()` called at mod load. Walks every `AreaPowerControl` prefab via `UnityEngine.Object.FindObjectsOfType<AreaPowerControl>(includeInactive: true)` and writes `apc.BatteryChargeRate = Settings.ApcBatteryChargeRate.Value`. Re-applies on settings change.
- New file: `Mods/PowerGridPlus/PowerGridPlus/ApcDischargeRateRegistry.cs`. Static `Dictionary<long, float>` keyed by `AreaPowerControl.ReferenceId`. Default lookup returns `Settings.ApcBatteryDischargeRate.Value`. The elastic supply allocator (§7.3 in POWER.md) reads from this.
- File: `Mods/PowerGridPlus/PowerGridPlus/Patches/AreaPowerControlPatches.cs`. The existing `ReceivePower` and `GetUsedPower` patches read from `Settings.ApcBatteryChargeRate.Value` directly today; after `ApcRateApplier.Apply()` runs the vanilla field carries the same value, so these patches can either keep reading from settings (no functional change) or switch to reading `__instance.BatteryChargeRate` (consistency). Document the choice.
- Validation:
  - `pgp-apc-charge-rate-applied-probe`: load mod with `ApcBatteryChargeRate = 2500`. Snapshot any `AreaPowerControl` instance via InspectorPlus. Assert `BatteryChargeRate == 2500`.
  - `pgp-apc-discharge-rate-applied-probe`: load mod with `ApcBatteryDischargeRate = 1500`. Force an undersupply on an APC's output net. Assert the APC's discharge per tick caps at 1500 W (not at the cable cap or stored energy alone).

### 1.6 Fix vanilla `_networkTraversalRecord` reuse bug

Vanilla's `PowerTick.CheckForRecursiveProviders` calls `_networkTraversalRecord.Clear()` once before the foreach loop, then reuses the visited set across anchor iterations. This can prune a legitimate cycle starting from a later anchor if the earlier anchor's walk already touched the later anchor's ReferenceId. Fix by clearing the record at the top of each anchor iteration.

- File: `Mods/PowerGridPlus/PowerGridPlus/Patches/CycleDetectionFixPatch.cs` (new). Harmony Transpiler or full-body Prefix on `PowerTick.CheckForRecursiveProviders`:
  - Transpiler approach: insert a `_networkTraversalRecord.Clear()` call at the top of the foreach body.
  - Full-body Prefix approach: rewrite the method body in C#, calling `_networkTraversalRecord.Clear()` before each `IsProviderToDevice` invocation. Use reflection to set the private list. Either works; transpiler is leaner.
- Gate behind a setting? No. The bug is benign-to-correct in all cases; no reason to gate. Apply unconditionally.
- Validation:
  - Build a save with two transformers A1 and A2 on the same input network, both with separate downstream cycles back to the input network through disjoint chains. Vanilla unfixed: one of the two cycles may not be detected on a given tick. PowerGridPlus fixed: both detected within one tick, both cables marked breakable.
  - Headless test: instantiate a `PowerTick`, populate `InputOutputDevices` with two synthetic anchors, run `CheckForRecursiveProviders`, assert both anchors got an independent walk (instrument via reflection peeking at `_networkTraversalRecord` count between iterations).

### 1.6.5 Producer-isolation check (Phase 1.5b: VARIABLE_VOLTAGE_FAULT and cable-burn fallback)

Goal: implement the §8.5 producer-isolation rule. Producers on a network with a rigid consumer trigger VARIABLE_VOLTAGE_FAULT on the producer (if it has an OnOff button) or cable burn (if not).

- New file: `Mods/PowerGridPlus/PowerGridPlus/VariableVoltageFaultRegistry.cs`. Parallel to BrownoutRegistry / OverloadRegistry / CycleFaultRegistry. Same API shape (NoteVariableVoltageFault, IsVariableVoltageFaulted, ClearLockout, ClearAll, client mirror dict).
- New file: `Mods/PowerGridPlus/PowerGridPlus/ProducerClassifier.cs`. Static utility:
  - `bool IsProducer(Device d)`: returns true if d is one of SolarPanel, WindTurbineGenerator, PowerGeneratorPipe, GasFuelGenerator, PowerGeneratorSlot, SolidFuelGenerator, StirlingEngine, RadioscopicThermalGenerator (and inheritance chain).
  - `bool IsFlashableProducer(Device d)`: returns true if d has an InteractableType.OnOff (PowerGeneratorPipe, GasFuelGenerator, PowerGeneratorSlot, SolidFuelGenerator, StirlingEngine). False for SolarPanel, WindTurbineGenerator, RadioscopicThermalGenerator.
  - `bool IsRigidConsumer(Device d)`: returns true if d is neither a segmenting device nor a producer AND has `GetUsedPower(net) > 0`. Segmenting devices (Transformer / Battery / APC / PT / PR / RocketUmbilicalMale / RocketUmbilicalFemale / PowerConnection) explicitly do NOT count as rigid consumers for this check.
  - Producer-only network rule (decision locked): a cable network containing ONLY producers (e.g. SolarPanel wired to SolarPanel with no other device on the same network) is a valid idle configuration. The `hasProducer && hasRigid` predicate in the Phase 1.5b walk already guarantees no VVF fires when there is no rigid consumer to violate the rule. Two producers wired together with no other device = no VVF, no warning, no cable burn. Document this explicitly to head off "should we warn the player?" speculation.
  - Faulted-transformer-as-isolator rule (decision locked): the producer-isolation check considers any Transformer (including small / reversed variants) on the same network as satisfying isolation, REGARDLESS of the transformer's CYCLE_FAULT state. Solar wired to a faulted T1 passes the isolation check; no new VVF fires on the solar. The player traces power flow to discover T1's fault via T1's own visual feedback (red flash on T1's button + hover countdown). PGP does NOT cascade a producer-isolation fault onto a producer whose only fault path is "the transformer that isolates me is currently faulted."
  - Only-Transformer-isolates rule (Q1 strengthening, decision locked): ONLY `Transformer` (and the small / reversed transformer variants) on the same cable network counts as "isolation" between a producer and rigid consumers. Other segmenting devices DO NOT satisfy isolation: `Battery`, `AreaPowerControl`, `PowerTransmitter`, `PowerReceiver`, `RocketPowerUmbilicalMale`, `RocketPowerUmbilicalFemale`, `PowerConnection` are all transparent to the rule. So a network containing `SolarPanel + Battery + Light` still triggers VVF on the solar; the battery does NOT suppress it. Implementation: the Phase 1.5b walk's `hasProducer && hasRigid` check already produces this outcome because rigid consumers (Light) are still counted. To make this explicit, add a `hasTransformer` flag set during the per-network walk; the VVF fires only when `hasProducer && hasRigid && !hasTransformer`. The transformer presence ALONE silences VVF; battery/APC/PT/PR/etc. presence does NOT. Document this as the canonical rule in `ProducerClassifier`.
- File: `Mods/PowerGridPlus/PowerGridPlus/Patches/AtomicElectricityTickPatch.cs`. After Phase 1.5a (cycle-fault detection), add Phase 1.5b:
  ```csharp
  // Phase 1.5b: producer-isolation check.
  int currentTick = ElectricityTickCounter.CurrentTick;
  CableNetwork.AllCableNetworks.ForEach(net => {
      if (net?.DeviceList == null || net.DeviceList.Count == 0) return;
      bool hasProducer = false, hasRigid = false, hasTransformer = false;
      List<Device> producers = new List<Device>();
      List<Device> rigid = new List<Device>();
      foreach (var d in net.DeviceList) {
          if (ProducerClassifier.IsProducer(d))      { hasProducer = true; producers.Add(d); }
          else if (d is Transformer)                 { hasTransformer = true; }  // ONLY Transformer satisfies isolation per Q1; Battery / APC / PT / PR / RocketUmbilical do NOT count
          else if (ProducerClassifier.IsRigidConsumer(d)) { hasRigid = true; rigid.Add(d); }
      }
      if (hasProducer && hasRigid && !hasTransformer) {
          // Collect violator class names (rule per Decision M): all rigid consumer class names on net, dedup, comma-separated, capped at 3 + "..." if more.
          var violatorList = rigid.Select(r => r.GetType().Name).Distinct().Take(4).ToList();
          string violatorNames = violatorList.Count > 3
              ? string.Join(", ", violatorList.Take(3)) + ", ..."
              : string.Join(", ", violatorList);
          foreach (var p in producers) {
              if (ProducerClassifier.IsFlashableProducer(p)) {
                  VariableVoltageFaultRegistry.NoteVariableVoltageFault(p.ReferenceId, currentTick, violatorNames);
              } else {
                  // Cable-burn fallback.
                  var cable = FindCableAdjacent(rigid.First(), net);   // adjacent to any rigid consumer
                  if (cable != null) {
                      BurnReasonRegistry.RegisterPending(cable, $"Power producing devices can only connect to a transformer (adjacent {violatorNames})");
                      cable.Break();
                  }
              }
          }
      }
  });
  ```
- File: `Patterns/Logic/LogicTypeNumbers.cs`. Append `public const ushort VariableVoltageFault = 6582;`. Update `Patterns/Logic/README.md` and bump "Next free slot" to 6583.
- File: `Mods/PowerGridPlus/PowerGridPlus/Patches/VariableVoltageFaultLogicPatches.cs` (new). Postfixes on flashable-producer classes to expose `LogicType.VariableVoltageFault` read returning `VariableVoltageFaultRegistry.IsVariableVoltageFaulted(ReferenceId, currentTick) ? 1 : 0`.
- File: `Mods/PowerGridPlus/PowerGridPlus/VariableVoltageFaultStateMessage.cs` (new). Host-to-clients FULL snapshot per tick (per Phase 1.8 Q5 carry-over). Carries `(refId, remainingTicks, violatorNames)` tuples. Parallel to ShedStateMessage / OverloadStateMessage / CycleFaultStateMessage.
- File: `Mods/PowerGridPlus/PowerGridPlus/VariableVoltageFaultFlashBehaviour.cs` (new). Subclass or sibling of BrownoutFlashBehaviour with `FlashColour = Color(1f, 0.15f, 0.15f)` (red, same as overload + cycle-fault).
- File: `Mods/PowerGridPlus/PowerGridPlus/Patches/TransformerFlashAttachPatches.cs`. Rename internal `_transformer` to `_device`. Extend attach logic to GasFuelGenerator, PowerGeneratorPipe, SolidFuelGenerator, StirlingEngine. NOT attached to SolarPanel, WindTurbineGenerator, RadioscopicThermalGenerator (no OnOff button); see VariableVoltageFaultHoverPatches below for those.
- New file: `Mods/PowerGridPlus/PowerGridPlus/Patches/VariableVoltageFaultHoverPatches.cs`. Universal-base Harmony Postfix on `Thing.GetPassiveTooltip(Collider)`. Pattern:
  ```csharp
  [HarmonyPatch(typeof(Thing), nameof(Thing.GetPassiveTooltip))]
  public static class VariableVoltageFaultHoverPatches {
      [HarmonyPostfix]
      public static void Postfix(Thing __instance, ref PassiveTooltip __result) {
          if (__instance == null) return;
          int tick = ElectricityTickCounter.CurrentTick;
          switch (__instance) {
              case SolarPanel _:
              case WindTurbineGenerator _:
              case RadioscopicThermalGenerator _:
                  if (VariableVoltageFaultRegistry.TryGetFault(__instance.ReferenceId, tick, out float secondsLeft, out string violatorName)) {
                      string line = $"<color=#ff2626>(Variable Voltage Fault: connected to {violatorName} without transformer. {secondsLeft.ToString("0.00", CultureInfo.CurrentCulture)}s)</color>";
                      var prefix = string.IsNullOrEmpty(__result.Extended) ? string.Empty : (__result.Extended + "\n");
                      __result.Extended = prefix + line;
                  }
                  break;
          }
      }
  }
  ```
  Per-frame poll cadence via `WorldCursor.Idle()` makes the countdown tick smoothly.

- Violator-naming rule (decision locked): when the Phase 1.5b producer-isolation walk fires a VVF on a producer, record the violator(s) -- the rigid-consumer class names that triggered the rule -- onto the registry entry. Rule choice: record ALL rigid violator class names on the same network, deduplicated, comma-separated (limit 3 names + "..." if more). Rationale: a network with a coal-genny producer plus four lights plus a door is most diagnostically helpful when the hover names all of them so the player can scan the obvious offender. Single-violator case stays clean ("Light"). The class-name string is the unqualified C# type name (`Light`, `ArcFurnace`, `KitchenStove`, `LogicReader`, etc.); strip any "Structure" prefix and any vanilla-namespace noise.
- `VariableVoltageFaultRegistry`: extend the entry record to carry `string violatorNames` alongside the existing `int lockoutUntilTick`. `TryGetFault(refId, tick, out float secondsLeft, out string violatorNames)` returns both. Fault-emitter sites (Phase 1.5b producer-isolation walk) supply the violator string at `NoteVariableVoltageFault` time; carries through to all hover patches.
- Flashable-producer hover (the on-button-flash producers: coal, gas, stirling, etc.) uses the same `Variable Voltage Fault: connected to {violatorName} without transformer. {seconds}s` line via their respective per-class GetPassiveTooltip postfixes. Single source-of-truth string format across hover and flash producers.
- `VariableVoltageFaultRegistry`: add `TryGetFault(refId, tick, out float secondsLeft, out string violatorNames)` returning `true` and computing `secondsLeft = (lockoutUntilTick - tick) * 0.5f` if locked (float, not int -- gives sub-second granularity for smooth countdown), else `false`. The format string is `secondsLeft.ToString("0.00", CultureInfo.CurrentCulture)` so the decimal separator follows the player's culture (`.` in en-US, `,` in de-DE, etc.). Apply the same format string in every fault-line emission site (Brownout, Overload, CycleFault, VariableVoltageFault hover Postfixes). The Brownout / Overload / CycleFault `TryGetFault` equivalents do not need the `violatorNames` out-param (those faults are per-device self-contained).
- Hover-only producers (SolarPanel, WindTurbineGenerator, RTG) get their `GetGeneratedPower(net)` postfixed via a separate small patch in `VariableVoltageFaultEnforcementPatches.cs`: when the registry has the device locked, return 0. So the device visibly stops producing AND the hover line explains why.
- Validation probes (added to POWERTODO §9.4 below):
  - `pgp-producer-isolation-coal-and-light-probe`: coal generator + light directly on the same network. Tick. Assert coal enters VariableVoltageFaultRegistry (it has OnOff). Assert RED flash on coal's button. Assert hover "(Variable Voltage Fault: connected to Light without transformer. 60.00s)" per Decision M wording.
  - `pgp-producer-isolation-solar-and-light-probe`: solar panel + light directly on the same network. Tick. Assert NO VariableVoltageFault entry (solar has no OnOff). Assert one cable burns. Assert CableRuptured's hover text contains "Power producing devices can only connect to a transformer (adjacent Light)" per Decision M violator-naming rule.
  - `pgp-producer-isolation-no-rigid-no-fault-probe`: solar panel connected only to a transformer (no rigid consumers on the solar's network). Tick. Assert NO fault, NO burn.
  - `pgp-producer-isolation-with-battery-no-rigid-probe`: solar panel + battery on the same network with NO rigid consumers. Assert NO fault, NO burn (producer-only-network rule per Decision G; battery presence is irrelevant when no rigid load present).
  - `pgp-producer-isolation-battery-does-not-isolate-probe`: solar + battery + light (rigid present). Assert VVF / burn fires per Q1: Battery does NOT satisfy isolation.
  - `pgp-producer-isolation-rtg-and-machine-probe`: RTG + arc furnace on the same network. Assert one cable burns adjacent to the arc furnace. Burn-reason text names the violator (ArcFurnace).

### 1.7 Atomic flow Phase 1.5: wrong-tier cable burn (1.5a) then pre-allocator cycle-fault detection (1.5b)

Goal: insert a new Phase 1.5 between Phase 1 (OBSERVE) and Phase 2 (DECIDE) split into two ordered sub-passes.

**Phase 1.5a: wrong-tier cable burn (runs first).** Vanilla voltage-tier junction check (re-enabled and unconditional after Phase 1.1) burns any cable connecting two incompatible tiers. Runs FIRST because a wrong-tier burn may shrink the network into smaller pieces, possibly dissolving what otherwise looked like a cycle in Phase 1.5b's walk. Splitting an apparent cycle into two disconnected segments is the correct outcome; we want Phase 1.5b to see the post-burn topology.

**Phase 1.5b: cycle-fault detection (runs second).** Detects every powered cycle in the post-Phase-1.5a topology, identifies every segmenting device participating, and marks them all for CYCLE_FAULT. The allocator (Phase 2) then sees the cycle dissolved because each faulted device contributes 0 on both sides.

Implementation order in `AtomicElectricityTickPatch`:
1. Phase 1 (OBSERVE).
2. Phase 1.5a (wrong-tier burn via vanilla `Cable.CheckForJunctionTierBreaks` path).
3. **Re-enumerate networks** (decision locked): after Phase 1.5a burns complete, fully re-enumerate `CableNetwork.AllCableNetworks` before Phase 1.5b walks. A wrong-tier burn can split one network into two; the DFS in 1.5b needs the post-burn list, otherwise it walks a stale graph and misses cycles that were previously hidden behind a now-burned junction. The existing per-network `Initialise + CalculateState` re-call for changed networks (already in the post-1.5b re-observe block) stays as a separate step; this re-enumeration runs BEFORE 1.5b's graph build.
4. Phase 1.5b (CYCLE_FAULT detection per the block below).
5. Phase 1.6.5 (producer-isolation, which is independent of cycle detection but conceptually sits with the pre-allocator faults; ordering between 1.5b and 1.6.5 does not matter for correctness because they operate on disjoint device sets).
6. Phase 2 (DECIDE).

**Phase 1.5b: NO cables burn from cycles. Cable burn is reserved for direct-generator overflow per POWER.md §5.7 and wrong-tier junctions per Phase 1.5a.**

- File: `Mods/PowerGridPlus/PowerGridPlus/CycleFaultRegistry.cs` (new). Parallel to BrownoutRegistry/OverloadRegistry. API:
  - `static Dictionary<long, int> _lockoutUntilTick`. Keyed by `ReferenceId` of the segmenting device. Value = `currentTick + 120` (literal 120, the cooldown is 60 seconds at the 2 Hz electricity tick). Decision locked: do NOT derive 120 from any `TickRate` / `TicksPerSecond` constant; use the literal. // Assumes 2 Hz electricity tick. If game tick rate changes, review.
  - `static void NoteCycleFault(long refId, int currentTick)`.
  - `static bool IsCycleFaulted(long refId, int tick)`.
  - `static void ClearLockout(long refId)`. For OFF-as-reset.
  - `static void ClearAll()`.
  - Client mirror dict `_clientCycleFault` (set via `SetClientCycleFault`).
- File: `Mods/PowerGridPlus/PowerGridPlus/Patches/AtomicElectricityTickPatch.cs`. After Phase 1, insert Phase 1.5. Critical change from earlier drafts: PowerGridPlus uses its OWN undirected bipartite-graph DFS to find cycles. It does NOT read `PowerTick.BreakableCables` and does NOT depend on vanilla's `CheckForRecursiveProviders` populating anything. Vanilla's check still runs unsuppressed (per Phase 1.2) as belt-and-braces; PGP's DFS is the primary cycle detector for CYCLE_FAULT.

  ```csharp
  // Phase 1.5: pre-allocator cycle-fault detection via PGP's own DFS. NO cable burn.
  int currentTick = ElectricityTickCounter.CurrentTick;
  HashSet<long> cycleFaultedSegmenters = CycleGraphBuilder.BuildAndFindCycles(currentTick);
  if (cycleFaultedSegmenters.Count > 0) {
      foreach (long refId in cycleFaultedSegmenters) {
          CycleFaultRegistry.NoteCycleFault(refId, currentTick);
      }
      // Re-observe to refresh Potential / Required reflecting the faulted devices contributing 0.
      CableNetwork.AllCableNetworks.ForEach(net => {
          var pt = net?.PowerTick;
          if (pt == null || net.DeviceList.Count == 0) return;
          pt.Initialise(net);
          pt.CalculateState();
      });
  }
  ```

- New file: `Mods/PowerGridPlus/PowerGridPlus/CycleGraphBuilder.cs`. Builds the undirected bipartite graph and runs DFS. Graph shape:
  - Nodes: `CableNetwork` instances AND `SegmentingDeviceRegistry.AllSegmentingDevices` entries. Two disjoint node types (bipartite).
  - Edges (undirected):
    - For each segmenter `S`: one edge to `S.InputNetwork` (if non-null) and one edge to `S.OutputNetwork` (if non-null).
    - For each linked PowerTransmitter `PT` with `PT.LinkedReceiver != null`: a synthetic wireless edge connecting `PT.InputNetwork` to `PT.LinkedReceiver.OutputNetwork`, modelled as a virtual segmenter node. See #F (Phase 6.5) for the exact wireless edge predicate.
  - Walk: iterative DFS from each unvisited segmenter, sorted by `ReferenceId ASC` (MP determinism, see #E below). A back edge to an ancestor on the current stack = cycle. Every segmenter on the back-edge path is added to `cycleFaultedSegmenters`.
  - Graph is rebuilt fresh each tick (subject to the topology-unchanged skip in Phase 1.7's task block J).

  Drop the following from any earlier draft of the spec:
  - Any task that reads `PowerTick.BreakableCables.Count > 0` as a cycle trigger.
  - Any task that clears `BreakableCables` / `BreakableFuses` after marking. PGP never touches those lists; vanilla manages them.
  - Any task that calls into `WirelessCycleDetector.AddCycleFaultedNets`. Replaced by `CycleGraphBuilder` adding wireless edges directly per #F.

- Topology-unchanged skip optimization (Phase 1.5b cost reduction):
  - Per-network `lastTopologyChangeTick` tracker stored on a static `Dictionary<long, int>` keyed by `CableNetwork.ReferenceId`. Updated on cable place / destroy / burn AND on segmenting-device add / remove / rewire.
  - Subscribe to whatever vanilla event signals these changes. Investigate hooking `CableNetwork.OnDeviceAdded` / `OnDeviceRemoved` and `Cable.OnRegistered` / `Cable.OnDestroyed` or equivalent; fall back to a Postfix on `CableNetwork.Initialise` / `CableNetwork.Rebuild` if no granular event exists.
  - Also bump the tracker on cable burn from Phase 1.5a (wrong-tier) and on the post-1.5a re-enumeration (Decision K below).
  - At `CycleGraphBuilder.BuildAndFindCycles` entry, for each network in the candidate set: if `lastTopologyChangeTick < lastCheckedTick`, skip DFS for this network's connected component. The graph builder maintains `lastCheckedTick` per network alongside the change tracker.
  - On skip: re-emit any previously-cached `cycleFaultedSegmenters` from the prior walk's result for the unchanged components, so the registry refresh still re-notes the lockout (which is a no-op until lockout expires, per Decision D Mode A semantics).
  - Probes:
    - `pgp-cycle-skip-stable-grid-probe`: build a large stable grid (100+ segmenters, complex but acyclic, no topology changes for 60s). Measure Phase 1.5b cost per tick. Assert near-zero (skip fires for every component after the first tick).
    - `pgp-cycle-skip-flicker-probe`: place + destroy a cable every tick on one component. Measure Phase 1.5b cost. Assert DFS DOES run every tick on that affected component (and only that component).
    - `pgp-cycle-skip-unrelated-change-probe`: large stable grid plus one isolated unrelated cable being placed every tick. Assert Phase 1.5b skips the stable components and only DFS-walks the affected component.
- File: `Mods/PowerGridPlus/PowerGridPlus/SegmentingDeviceRegistry.cs` (new). Static registry that enumerates every concrete segmenting device on the map: Transformer, Battery, AreaPowerControl, PowerTransmitter, PowerReceiver, RocketPowerUmbilicalFemale, RocketPowerUmbilicalMale, PowerConnection. Built at scene-load and maintained on device add/remove. MP determinism: the registry exposes `AllSegmentingDevices` as an `IEnumerable<Device>` sorted by `ReferenceId ASC`. `CycleGraphBuilder` MUST iterate via this sorted enumeration (do NOT iterate via dictionary or hashset enumeration order). Same key the allocator mandates per POWER.md §8.0.1.
- File: `Mods/PowerGridPlus/PowerGridPlus/Patches/CycleFaultEnforcementPatches.cs` (new). Postfixes on `GetGeneratedPower` and `GetUsedPower` of every segmenting device class. When `CycleFaultRegistry.IsCycleFaulted(__instance.ReferenceId, currentTick)` returns true, set `__result = 0`. Same shape as the shed / overload zero-out patches. Uniformity requirement (Q2 carry-over, decision locked): Phase 1.7 and Phase 1.7.5 MUST treat all SEVEN segmenter classes uniformly for CYCLE_FAULT eligibility: `Transformer`, `Battery` (StationaryBattery + StationBatteryLarge), `AreaPowerControl`, `PowerTransmitter`, `PowerReceiver`, `RocketPowerUmbilicalMale`, `RocketPowerUmbilicalFemale`. All seven can enter CYCLE_FAULT, all seven get the per-class zero-out Postfix, all seven contribute to the graph build, all seven have the same 60s lockout window and OFF-as-reset behaviour. Do NOT special-case any of them; do NOT exempt any class from cycle detection. PowerConnection (the dynamic-generator coupler) appears in `SegmentingDeviceRegistry` for graph-build completeness but does not have an OnOff button or per-class hover, so its CYCLE_FAULT entry is silent (no flash, no hover); it still contributes to the topology and dissolves cycles correctly.
- File: `Patterns/Logic/LogicTypeNumbers.cs`. Append `public const ushort CycleFault = 6581;`. Update `Patterns/Logic/README.md` table: add row "CycleFault | 6581 | PowerGridPlus | Read-only. Set to 1 while a transformer is in cycle-fault lockout."; bump "Next free slot" from 6581 to 6582.
- File: `Mods/PowerGridPlus/PowerGridPlus/Patches/TransformerPriorityLogicPatches.cs`. Extend `CanLogicReadPatch` and `GetLogicValuePatch` to also handle `LogicTypeNumbers.CycleFault` (reads from `CycleFaultRegistry.IsCycleFaulted`).
- File: `Mods/PowerGridPlus/PowerGridPlus/CycleFaultStateMessage.cs` (new). Network message carrying the FULL `CycleFaultRegistry` snapshot per tick (per Phase 1.8 Q5 carry-over). Sent host -> clients each tick when the registry is non-empty; zero-byte when empty. Client-side: atomic replace of mirror dict on receive.
- File: `Mods/PowerGridPlus/PowerGridPlus/CycleFaultFlashBehaviour.cs` (new). Subclass or sibling of `BrownoutFlashBehaviour` with `FlashColour = Color(1f, 0.15f, 0.15f)` (red, same as OverloadFlashBehaviour).
- File: `Mods/PowerGridPlus/PowerGridPlus/Patches/SwitchOnOffShedPatches.cs`. Extend the OFF-as-reset logic: when `OnOff == false` AND (`shed || overload || cycle_fault`) is active, clear all three registries for the device.
- Validation:
  - `pgp-cycle-fault-marks-all-devices-probe`: build a cycle through T1, T2, T3 (three transformers in a ring). Tick once. Assert ALL THREE enter `CycleFaultRegistry`, NOT just one. Assert no cable burns.
  - `pgp-cycle-fault-mixed-devices-probe`: cycle through T1, APC, Battery (mixed segmenting devices). Assert all three are in `CycleFaultRegistry`.
  - `pgp-cycle-fault-wireless-probe`: cycle closing through a PT/PR wireless link. Assert the PT enters CycleFaultRegistry (and the PR transitively because they share the wireless link).
  - `pgp-cycle-fault-unpowered-no-action-probe`: build a cycle on a network with no power flow. Tick. Assert no entries in `CycleFaultRegistry`. Assert no cable burns.
  - `pgp-cycle-fault-becomes-powered-probe`: same unpowered cycle, then turn on a device that draws power through it. Tick. Phase 1.5 marks all devices in the loop for CycleFault same tick.
  - `pgp-cycle-fault-60s-timeout-probe`: trigger a cycle-fault. Advance 120 ticks (60 s). Devices auto-clear. Cycle still exists: tick once more, Phase 1.5 re-fires CycleFault on all of them. Assert.
  - `pgp-cycle-fault-off-as-reset-probe`: trigger cycle-fault. Toggle one device OFF. Assert that device's CycleFault clears immediately. Toggle ON; next tick re-fires if cycle still exists.
  - `pgp-cycle-fault-visual-red-probe`: snapshot the on/off button material during cycle-fault. Assert `Color(1f, 0.15f, 0.15f)` red emission (same as overload, not orange).
  - `pgp-cycle-fault-hover-text-probe`: hover the on/off button. Assert text reads "(Cycle Fault: this device is part of a closed loop)" in `#ff2626`.
  - `pgp-cycle-fault-allocator-clean-state-probe`: instrument Phase 2 (allocator) to log received Potential / Required per network. Assert no inflated values from the cycle (Phase 1.5 dissolved it before Phase 2 ran).

### 1.7.5 Lockout timer semantics and skip-while-faulted optimization

Decision locked: the 60-second lockout is timer-only. A faulted device stays locked for the full 120-tick window even if the underlying condition would otherwise clear earlier. Mid-cooldown topology re-checks do NOT shorten the timer. The implementation MUST NOT compute "is this fault still warranted?" mid-cooldown and clear early; the only way out before timer expiry is OFF-as-reset (player toggles the device off and back on).

Skip-while-faulted optimization (required for correctness AND performance):
- Cycle detection DFS (Phase 1.5b via `CycleGraphBuilder`) uses a dual-mode DFS:
  - **Mode A: full-graph cycle discovery.** The walk visits faulted segmenters normally and recurses through their other side. This is necessary so that new non-faulted participants joining an existing cycle get newly faulted as the loop persists or grows; the existing fault timers on already-faulted members are NOT affected by Mode A's visits (a re-`NoteCycleFault` on an already-locked refId is a no-op until lockout expiry).
  - **Mode B: "is the original loop still here" check.** A separate pass that DOES treat faulted segmenters as non-conducting edges. Used to decide whether the cycle is still topologically present so that we do NOT extend timers on the original loop members beyond their natural 60s expiry. If Mode B reports the original loop is gone (e.g. a member was OFF-as-reset or its cable was deconstructed), do not re-note CYCLE_FAULT on the remaining originals.
  - The dual-mode walk applies the rule "skip-while-faulted means original-loop check only, not new-cycle discovery." Document this in `CycleGraphBuilder` header comments.
- Producer-isolation walk (Phase 1.6.5) skips producers that are already in VARIABLE_VOLTAGE_FAULT. Continue past them when scanning the producer list per network. Avoids re-noting the same fault every tick during the lockout.
- Shed/overload allocator math (Phase 5) treats faulted devices as supplying 0 and capacity 0. This already falls out of the existing per-device zero-out postfixes (`CycleFaultEnforcementPatches`, `BatterySupplyCapPatch` etc. with the registry check), so faulted devices naturally drop out of allocation math without a separate skip pass. Document this as an invariant the implementation relies on; do NOT remove the per-device zero-out postfixes thinking the allocator pre-filter is enough.

Logic-passthrough invariant (decision locked): a segmenter's logic-passthrough behaviour is SOLELY configured by the `LogicPassthrough` LogicType value (or the per-class server master toggle such as `EnableAreaPowerControlLogicPassthrough`). Fault state (CYCLE_FAULT / VARIABLE_VOLTAGE_FAULT / OVERLOAD / SHED) does NOT affect logic passthrough. A faulted segmenter still bridges logic reads / writes across its input and output sides if `LogicPassthrough = 1`. OnOff state ALSO does not affect logic passthrough (a powered-off transformer with `LogicPassthrough = 1` still passes logic). The power lane and the logic lane are independent. Do NOT add any task that says "faulted segmenters stop being logic-passthrough bridges"; that behaviour was considered and rejected.

Validation:
- `pgp-fault-timer-full-window-probe`: trigger SHED at tick T. Clear the underlying undersupply at tick T+10. Assert the SHED stays locked until tick T+120 (60s). The fault does NOT auto-clear when the underlying condition resolves; only OFF-as-reset clears early.
- `pgp-cycle-fault-skip-during-lockout-probe`: trigger CYCLE_FAULT on T1. Verify Phase 1.5's cycle DFS during the next 119 ticks does NOT re-walk the now-empty loop (which would still appear "loopy" if T1 contributed normally). Confirms the DFS treats T1 as non-conducting.
- `pgp-producer-isolation-skip-during-lockout-probe`: trigger VVF on a coal generator. Verify Phase 1.5b's producer scan during the lockout window does NOT re-add it to the registry every tick.

### 1.8 MP sync semantics for fault registries (Q5 carry-over)

Decision locked: every fault registry (`BrownoutRegistry`, `OverloadRegistry`, `CycleFaultRegistry`, `VariableVoltageFaultRegistry`) uses the same MP sync rule:
- Per-tick FULL registry snapshot sent host -> clients when the registry is non-empty.
- NO sync when the registry is empty (zero-byte tick).
- The "full snapshot" carries every active `(ReferenceId, remainingTicks)` pair; clients overwrite their mirror dict on receive. No diff'ing, no delta encoding. Cost is bounded by the active-fault count, which is small in steady state.
- The per-tick full sync replaces any earlier "diff broadcast" language in `CycleFaultStateMessage` / `OverloadStateMessage` / `VariableVoltageFaultStateMessage`. Diff'ing was rejected because of state-divergence risk when a client misses a delta packet; full-snapshot is self-healing.
- ShedStateMessage already uses this pattern; the other three follow suit.

Implementation:
- Each `*StateMessage` carries `List<(long refId, int remainingTicks)>`. Wire to BepInEx ZeroTier-style message routing (or whatever the project's MP layer uses).
- Host-side: at end of each Phase 2 tick, if any registry is non-empty, send its full snapshot to all connected clients.
- Client-side: on receive, atomically replace the mirror dict with the snapshot. The `IsShedding / IsOverloaded / IsCycleFaulted / IsVariableVoltageFaulted` reads compute `remainingTicks > 0` against the current tick.

Probes:
- `pgp-mp-fault-sync-nonempty-probe`: dedi + 1 client. Trigger SHED on a transformer. Assert ShedStateMessage payload contains the transformer's refId + remainingTicks. Assert client mirror dict updates same tick.
- `pgp-mp-fault-sync-empty-probe`: dedi + 1 client. No active faults. Assert NO message sent (zero network traffic per tick on the fault-sync channel).
- `pgp-mp-fault-sync-self-healing-probe`: dedi + 1 client. Simulate a dropped packet at tick T. At tick T+1 the full snapshot arrives; assert client mirror is fully consistent regardless of T's loss.

### 1.9 MP join handshake for fault registries (Q6 carry-over)

Decision locked: when a client joins, the host sends a one-shot HANDSHAKE message containing the current fault-registry state for all four registries (Shed, Overload, CycleFault, VVF). Each entry carries `(ReferenceId, remainingTicks)`. The client populates its mirror dicts so the visual state (flash colour + hover countdown) reflects the in-progress faults immediately on join, NOT only after the next per-tick sync.

Implementation:
- `FaultRegistryJoinSnapshotMessage` (new). Serialises all four registry contents.
- Sent host -> joining client during connection handshake, after the join is accepted and before the first per-tick electricity sync.
- Client populates mirror dicts before first visual frame; flash + hover read correct state immediately.

Probes:
- `pgp-mp-join-mid-shed-handshake-probe`: host has T1 in SHED with 30s remaining when client joins. Client receives handshake. Assert T1 button is flashing orange on client AND hover countdown reads ~30s, not 60s.
- `pgp-mp-join-no-fault-handshake-probe`: host has empty registries when client joins. Assert handshake payload is empty / 4 empty lists. No client visual fault state.

### 1.3 Verify `PowerGridTick.cs` and `PowerTickPatches.cs` cleanup

- After 1.1 and 1.2, `PowerGridTick.cs` may be entirely unreferenced. If so, delete the file.
- Same check for `PowerTickPatches.cs`'s reverse patches if nothing else calls them.
- Validation: full rebuild succeeds with no warnings about unreachable code.

### 1.4 Update `Settings.cs` and `About.xml`

- Remove the dead settings from the in-game settings panel description. About.xml `<Description>` and `<ChangeLog>` need a one-liner about the removal (player-facing).
- Bump version (decide v0.2.0 or similar).
- Update Mods/PowerGridPlus/CHANGELOG.md with the removal.

## Phase 2 — Shared `SoftDemandHeadroomCalculator` helper

Goal: extract the APC's "dynamic charge rate capped by upstream headroom" math into a reusable helper, so the same algorithm applies to every soft-demand device.

### 2.1 Locate existing APC logic

- File: `Mods/PowerGridPlus/PowerGridPlus/Patches/AreaPowerControlPatches.cs`. Find the headroom calculation that scales APC's charge demand by available input headroom. Document the algorithm in a comment.
- Subject to: PGP currently has `EnableAreaPowerControlFix` toggling this. The new helper does not honour that toggle (the spec says all soft devices use this math); keep `EnableAreaPowerControlFix` for legacy users but document that headroom-capping is unconditional going forward.

### 2.2 Create `SoftDemandHeadroomCalculator`

- New file: `Mods/PowerGridPlus/PowerGridPlus/SoftDemandHeadroomCalculator.cs`.
- API: `static float ComputeHeadroom(CableNetwork net)` and `static float ClampToHeadroom(float requested, float headroom)`.
- The `ComputeHeadroom` reads `net.PowerTick.Potential` minus non-soft-demand rigid demand, returning max(0, ...).
- Document that this is called from soft-demand `GetUsedPower` postfixes during Phase 1 (when only previous-tick `PotentialLoad` is fresh) and during Phase 3 (after Phase 1's `CalculateState` populated current-tick values).

### 2.3 Refactor APC to use the helper

- `AreaPowerControlPatches.cs`: replace the embedded headroom math with calls to `SoftDemandHeadroomCalculator`.
- Validation: existing PGP behaviour for APC (rate cap, fixed leak) unchanged. Run any APC scenarios.

## Phase 3 — Soft-demand `GetUsedPower` postfixes

Goal: install postfixes on every soft-demand device that cap the reported demand to the allocator's per-tick share (`SoftDemandShareCache`).

### 3.1 Create `SoftDemandShareCache`

- New file: `Mods/PowerGridPlus/PowerGridPlus/SoftDemandShareCache.cs`.
- API: `static Dictionary<long, float> _shareByRef`, `static volatile bool IsPreWalking`, `static void Reset()`, `static void SetShare(long, float)`, `static bool TryGetShare(long, out float)`.
- Per-tick lifecycle: `Reset()` at start of `RunAtomic`; populated during the surplus allocation walk; read during `Battery.GetUsedPower` postfix.

### 3.2 Patch `StationaryBattery.GetUsedPower`

- New file: `Mods/PowerGridPlus/PowerGridPlus/Patches/BatteryElasticDemandPatch.cs`.
- Postfix on `Assets.Scripts.Objects.Pipes.StationaryBattery.GetUsedPower`. Reads `SoftDemandShareCache.TryGetShare(ReferenceId, out share)`. If found and `!IsPreWalking`, set `__result = share`. Otherwise pass through.
- HarmonyPriority: late, so PGP's existing battery rate-cap patches run first and our cap fires last.

### 3.3 Patch `LargeBattery.GetUsedPower` (if a separate type)

- Same postfix shape. Verify whether `StationBatteryLarge` inherits from `StationaryBattery`; if yes, one patch covers both.

### 3.4 Patch `NuclearBattery.GetUsedPower` via reflection

- The Nuclear Battery type ships from MorePowerMod. Detected at prefab-load time via assembly scan.
- If detected: `Harmony.Patch(detectedType.GetMethod("GetUsedPower"), postfix: ...)`. Same postfix logic.
- If not detected: no-op silently.
- Document the assembly name(s) we look for.

### 3.5 Split APC `GetUsedPower` into passthrough + internal-charge

- File: `Mods/PowerGridPlus/PowerGridPlus/Patches/AreaPowerControlPatches.cs`. Add (or extend) a postfix that:
  - Computes passthrough = downstream net's rigid demand (read via `PowerTick.Required` on the APC's output net minus any soft demand on that net).
  - Computes internal-charge = original `__result − passthrough`.
  - Looks up `SoftDemandShareCache.TryGetShare(ReferenceId, out share)`. This share is for the internal-charge portion only.
  - Sets `__result = passthrough + share` if share found; otherwise `__result = passthrough + internal_charge_capped_by_headroom` (fallback).
- This is the most subtle patch in the refactor; needs unit test.

### 3.6 Validation

- New scenario: `pgp-soft-demand-cap-probe`. Sets up a battery on an undersupplied subnet via headless save edit. Asserts that battery's reported `GetUsedPower` equals its allocated share over 60 ticks. Verifies `_powerProvided` on the upstream transformer stays bounded.

## Phase 4 — Per-input-network priority comparison

Goal: replace global `CompareForAllocation` with a sort that groups transformers by input network first and sorts within each group.

### 4.1 Refactor `TransformerAllocator.RunAtomic`

- File: `Mods/PowerGridPlus/PowerGridPlus/TransformerAllocator.cs`.
- Replace the single sorted list of contributors with `Dictionary<long, List<Transformer>>` keyed by input network RefId.
- Within each list, sort by `(Priority DESC, subtreeRigidDemand DESC, RefId ASC)` — note that "demand DESC" is the new tiebreak from the spec; today's code uses RefId ASC as the only tiebreak.
- The shed cascade then walks each group independently (no cross-group priority comparison).

### 4.2 Update `CompareForAllocation` or its callers

- The method `CompareForAllocation(Transformer a, Transformer b)` was a global comparator. Either delete it and inline the new comparator, or keep it but document it's only valid for siblings on the same input net.

### 4.3 Validation

- Scenario: `pgp-priority-per-network-probe`. Create a save where T1 (priority 200, on Grid) and T2 (priority 100, on a Mid-net deeper down) coexist. Force undersupply. Assert that T2 shed-eligibility is decided ONLY against T2's siblings on Mid-net, not against T1 on Grid.

## Phase 5 — Joint shed + overload fixed-point iteration

Goal: implement the §8.0 joint shed + overload algorithm. Each transformer's operational state is binary (ON or LOCKED-OUT). The iteration converges in at most N rounds where N is the segmenting-device count.

### 5.0 Iteration loop in `TransformerAllocator.RunAtomic`

- File: `Mods/PowerGridPlus/PowerGridPlus/TransformerAllocator.cs`. The `RunAtomic` body becomes a fixed-point loop:
  ```csharp
  var sheds = new HashSet<long>();
  var overloads = new HashSet<long>();
  for (int iter = 0; iter < MAX_ITERATIONS; iter++) {
      var (newSheds, newOverloads) = EvaluateOneRound(sheds, overloads);
      if (newSheds.SetEquals(sheds) && newOverloads.SetEquals(overloads)) break;
      sheds = newSheds; overloads = newOverloads;
  }
  CommitToRegistries(sheds, overloads);
  ```
- `MAX_ITERATIONS = 2 * segmentingDeviceCount + 4` (slack for safety).
- `EvaluateOneRound` runs two sub-passes per round: shed evaluation (bottom-up, per §8.0 step 1) and overload evaluation (top-down, per §8.0 step 2), each adding to the result sets.
- **Determinism (POWER.md §8.0.1)**: iteration order within a sub-pass is by `(depth ASC, priority DESC, ReferenceId ASC)` — INTEGER-ONLY. No float (demand) enters the sort key.
- If a magnitude tiebreaker is genuinely needed for sibling selection within the same `(depth, priority)`, quantise demand to int Watts before entering the key: `int demandWatts = (int)Math.Floor(actual_demand_float); key = (depth ASC, priority DESC, demandWatts DESC, ReferenceId ASC)`. The cast erases ulp-level float divergence between peers.
- Also fix the existing latent bug at `TransformerExploitPatches.cs:59-66`: change client-side `IsLockedOut` check to `IsShedding` so clients honour the host's broadcast during their own Phase 1/3 walks (sub-agent finding, separate from Phase 2 but related).
- Replace dictionary iteration in OVERLOAD/SHED Phase C with explicit `OrderBy(pair.Key)` walks (`TransformerAllocator.cs:124, 262`). The keys are `long` network ids; sorting is cheap; future runtime changes to dictionary iteration order won't break MP determinism.
- Headless MP determinism probe (POWER.md §8.0.1):
  - `pgp-mp-determinism-replay-probe`: run a save twice with the same binary, dump `(ReferenceId, depth, priority, BitConverter.SingleToInt32Bits(demand))` per transformer per Phase 2 entry, plus the final shed/overload sets. Assert byte-for-byte equality between runs.
  - `pgp-mp-determinism-dual-peer-probe`: dedi server + 1 client. Dump per-tick state from both peers. Force a shed via IC10 priority rewrite. Capture 60 ticks. Assert host and client dumps match byte-for-byte (the integer-only key makes this achievable; the demand bits may or may not match but never feed into a sort decision).

### 5.1 `subtreeRigidDemand` precompute

(Same as before, but now sub-numbered.) Inside `EvaluateOneRound`, after gathering contributors, run the recursive walk:

- Algorithm: depth-first from leaves. Leaf transformer's subtree demand = `OutputNetwork`'s rigid demand (after soft exclusion). Non-leaf transformer's subtree demand = `OutputNetwork`'s rigid demand + sum of children transformers' `subtreeRigidDemand` (excluding shed/overloaded children, who contribute 0).
- Cache per round: `Dictionary<long, float>` keyed by `ReferenceId`.

### 5.2 Bottom-up shed evaluation (per round)

Visit segmenting devices in depth-descending order. For each, check input network availability:

- `device_input_need = actual_throughput + UsedPower` where `actual_throughput = min(effective_cap, downstream_demand_with_current_state)`.
- Input net rigid supply ceiling = `generators + battery_discharge_effective_caps + supplying_transformers_not_in_state` (excluding self).
- If ceiling < sum of siblings' input needs at this level, sort siblings by `(priority ASC, subtreeRigidDemand DESC, ReferenceId ASC)` and add the first to SHED. Recompute. Repeat until fit, or until all shed-eligible siblings are in SHED.

### 5.3 Top-down overload evaluation (per round)

Visit segmenting devices in depth-ascending order. For each ON device:

- Compute output network total demand under current `state`.
- If `output_demand_share > OutputMaximum`, add device to OVERLOAD.
- For each cable on this output network: if `actual_flow > cable.MaxVoltage`, check generator-only contribution. If generators alone do NOT exceed cable cap, add EVERY upstream segmenting device feeding this network to OVERLOAD (per §5.7).

### 5.4 Convergence and commit

- Loop terminates when no state change in one round.
- After loop, write final `sheds` to `BrownoutRegistry`, `overloads` to `OverloadRegistry`. Set lockout = `currentTick + 120` for each new entry. Literal 120 (60 seconds at 2 Hz tick); do not derive from a tick-rate variable. // Assumes 2 Hz electricity tick. If game tick rate changes, review.
- Existing locked entries (from prior ticks) carry forward; they are not re-evaluated until their lockout expires.

### 5.5 Depth computation

(Unchanged from prior spec.) BFS from generator-bearing networks. Cache per `ReferenceId`. Skip cycle members during the tick they exist (Phase 1.5 burn handles them next round).

### 5.6 Validation

- `pgp-shed-overload-cascade-probe`: T1 (max 500) and T2 (max 500) parallel on output net with rigid demand 900. T1 input net loses supply (forced). Round 1: T1 sheds. Round 2: T2 overloads (alone can't supply 900). Both registered within one tick. Assert.
- `pgp-iteration-convergence-probe`: build a topology where shed cascade triggers 5 layers of consequential sheds and overloads. Assert convergence in <= 2N rounds. Assert no oscillation.
- `pgp-no-throttled-state-probe`: T1 in a configuration that vanilla would "throttle" (input cable limits T1 to 200 W of its 500 W max). PowerGridPlus must SHED T1 instead of throttling. Assert `actual_throughput == 0` (not 200).
- `pgp-binary-state-probe`: enumerate every segmenting device after Phase 2. Assert each is exactly one of {ON, SHED, OVERLOAD}. Assert no "partial" states.
- `pgp-shed-triggers-overload-probe`: paired transformers feeding a shared output. Force one to shed; assert the other goes to OVERLOAD in the same tick (not next tick).
- `pgp-overload-triggers-shed-probe`: an overload that reduces downstream demand to where a previously-shed transformer could now be ON. Assert this DOES NOT happen (state can only grow, not shrink, within a round).

## Phase 5old — `subtreeRigidDemand` precompute and bottom-up cascade (historical)

Goal: each transformer carries an aggregate rigid demand summing its whole downstream. Shed decisions cascade leaf-first.

### 5.1 Precompute pass

- Inside `RunAtomic`, after gathering contributors and rigid demand per network, run a recursive walk to populate `subtreeRigidDemand[refId]`.
- Algorithm: depth-first from leaves. Leaf transformer's subtree demand = its `OutputNetwork`'s rigid demand (after soft exclusion). Non-leaf transformer's subtree demand = its `OutputNetwork`'s rigid demand + sum of children transformers' `subtreeRigidDemand`.
- Cache: `Dictionary<long, float> subtreeRigidDemandByRef`.

### 5.2 Bottom-up cascade

- After precompute, group transformers by depth (root = depth 0, increasing downstream). Walk from highest depth (deepest) upward.
- At each depth level, for each input network at that level, check whether siblings' summed subtree demands fit the input's budget.
- If fit: no sheds. Move up.
- If not fit: sort siblings by (Priority ASC, subtreeRigidDemand DESC, RefId ASC) — note ASC priority because we shed lowest first. Shed siblings one-by-one. After each shed, the shed sibling's subtreeRigidDemand drops to 0. Recompute the level's claim. Stop when fit.
- Propagate the shed effect: as transformers shed, their parents' subtreeRigidDemand (already cached) decreases. Update parents' cached values for the next level up.

### 5.3 Depth computation

- A transformer's depth = max depth of any transformer feeding its input network + 1. Generators define depth 0 (no transformer above).
- Compute via BFS/DFS from generator-bearing networks. Cache per RefId.
- Edge case: cycles. If a cycle exists, vanilla's cable burn (1.2) will resolve it within a tick. Skip cycle members in the depth computation by tracking visited nodes.

### 5.4 Validation

- Scenario: `pgp-leaf-cascade-probe`. Save with multi-level chain (3 deep). Force undersupply at the root. Assert that the deepest-level lowest-priority subnet sheds first; only escalates upward if deeper sheds insufficient.
- Scenario: `pgp-cascade-edge-cases-probe`. Test:
  - Single-level grid (no cascade).
  - Two-level chain where leaf shed is sufficient.
  - Three-level chain where leaf + mid shed is needed.
  - Three-level chain where root shed is unavoidable (lowest-priority root, all leaves are higher-priority).
  - Same-priority siblings tiebreak by demand.

## Phase 6 — Power transmitters as transformers

Goal: PT/PR pairs participate in the allocator identically to wired transformers, AT THE ALLOCATOR LAYER ONLY. No PT/PR per-device power method gets replaced. PowerTransmitterPlus's distance-cost quartet, auto-aim, link visibility, save side-cars, and IC10 readouts continue to run unchanged.

### 6.1 PT/PR discovery

- Inside `RunAtomic`'s gather phase, after wired-transformer collection, enumerate linked PowerTransmitters: walk every `PowerTransmitter` instance and read `LinkedReceiver`. Skip unlinked.
- For each linked transmitter, construct a synthetic `TransformerSurrogate` carrying: input = `PT.InputNetwork`, output = `PR.OutputNetwork`, anchor = the PT.
- The pair's wireless network (`PT.OutputNetwork === PR.InputNetwork` by vanilla guarantee) is NOT modelled as a transformer; the synthetic transformer's input and output are the two cable networks bracketing the wireless link.
- Cache surrogates and treat them as contributors alongside wired transformers.

### 6.2 New `TransformerSurrogate` type

- New file: `Mods/PowerGridPlus/PowerGridPlus/TransformerSurrogate.cs`.
- Fields: `PowerTransmitter Anchor` (the PT), `PowerReceiver Partner` (`Anchor.LinkedReceiver`), `CableNetwork InputNetwork` (`Anchor.InputNetwork`), `CableNetwork OutputNetwork` (`Partner.OutputNetwork`), `long ReferenceId` (Anchor.ReferenceId).
- Methods: same interface the allocator uses on `Transformer` (priority lookup, OutMax read, etc.).
- `EffectiveCap` is computed at evaluation time (Phase 2) by reading `Anchor.GetGeneratedPower(Anchor.OutputNetwork)` (which returns the live distance-loss-adjusted number from vanilla or PowerTransmitterPlus), capped further by both cables' `MaxVoltage`, minus `Anchor.UsedPower + Partner.UsedPower`. See POWER.md §6.3.

### 6.3 Allocator generalisation

- Replace `List<Transformer> contributors` with `List<ITransformerLike> contributors` where `ITransformerLike` is an interface implemented by both `Transformer` and `TransformerSurrogate`.
- All allocator math uses the interface; concrete-type checks are minimised.

### 6.4 Shed enforcement for PT

- New file: `Mods/PowerGridPlus/PowerGridPlus/Patches/PowerTransmitterShedPatches.cs`.
- Late-priority Postfix on `PowerTransmitter.GetGeneratedPower`: when `BrownoutRegistry` or `OverloadRegistry` has the PT.ReferenceId locked out, set `__result = 0`. Harmony priority MUST be late (lower than PowerTransmitterPlus's `DistanceCostPatches.GeneratedPowerNoDistanceDeratePatch`) so PowerTransmitterPlus computes its distance-loss number first and our lockout overrides last.
- No patch on `PowerTransmitter.UsePower` or `PowerTransmitter.ReceivePower`. PowerTransmitterPlus's bookkeeping (`UsePowerInflateDebtPatch`, `ReceivePowerVisualizerFixPatch`) must run unchanged. A locked-out PT's `_powerProvided` should reach 0 naturally because no source drains it on the wireless side after `GetGeneratedPower` returns 0.
- No patch on `PowerReceiver.GetGeneratedPower` either; the PR has its own wireless tick semantics tied to the PT's `_powerProvided`, and zeroing the PT at the source naturally zeroes the PR at the destination.
- Document in code that this is intentionally the ONLY per-device PT/PR patch PowerGridPlus owns.

### 6.5 Flash on transmitter button

- `TransformerFlashAttachPatches.cs`: extend to also attach `BrownoutFlashBehaviour` to PowerTransmitters at registration.
- The flash behaviour's renderer discovery already walks the `InteractableType.OnOff` `Interactable`; should work for transmitters without changes.

### 6.6 Validation

- Scenario: `pgp-transmitter-shed-probe`. Build a linked PT/PR pair feeding a subnet. Force input undersupply. Assert that the PT's button flashes orange and PR generates 0 during lockout.
- Scenario: `pgp-transmitter-distance-efficiency-probe`. Build a linked PT/PR pair at known distance. With PowerTransmitterPlus loaded, verify that `Anchor.GetGeneratedPower(Anchor.OutputNetwork)` returns the PowerTransmitterPlus distance-derated value (matching `1/(1 + k*d_km)` math). Without PowerTransmitterPlus, verify the vanilla `PowerLossOverDistance` curve value. The allocator's surrogate `EffectiveCap` should match whichever is active without any branch.
- Scenario: `pgp-transmitter-pgp-compat-probe`. With PowerTransmitterPlus loaded and the atomic tick running, perform a known-good distance-loss test from PowerTransmitterPlus's RESEARCH.md ("source_draw = 2000, delivered = 1000, efficiency 0.5 at k=5, distance=200m") and confirm the same numbers reproduce. Failure indicates a Phase 1/Phase 3 double-call interaction with PowerTransmitterPlus's stateful Postfixes.

## Phase 6.5 — Wireless cycle detection extension

Goal: extend cycle detection to cover wireless PT/PR links, which vanilla's `CheckForRecursiveProviders` misses entirely (PowerTransmitter / PowerReceiver are not `ElectricalInputOutput`).

### 6.5.1 Wireless edges injected into Phase 1.5b's `CycleGraphBuilder`

Wireless detection is NOT a separate detector. It is a set of additional edges injected into the same `CycleGraphBuilder` graph that Phase 1.5b walks. NO cables are burned by the wireless lane (per Decision C above, PGP burns no cables for cycles); the wireless edges contribute to CYCLE_FAULT detection exactly the same way wired segmenter edges do.

Wireless edge predicate (decision locked):

- A wireless edge exists iff ALL FOUR of: `PT.LinkedReceiver != null && PR._linkedPowerTransmitter != null && PT.IsOn && PR.IsOn`.
- Fault state does NOT affect edge existence. Per Decision D's Mode A walk, the DFS visits faulted PT/PR endpoints normally; a CYCLE_FAULT'd PT does not break its own wireless edge in the graph.
- OFF-as-reset on either endpoint REMOVES the edge (the `IsOn` predicate fails). This is how a player breaks a wireless cycle without waiting 60s.

Pair enumeration (critical for multi-PR fan-out):

- Walk BOTH the `PowerTransmitter` instance list AND the `PowerReceiver` instance list.
- For each PT with `LinkedReceiver != null && PT.IsOn`: emit edge `(PT.InputNetwork.ReferenceId, PT.LinkedReceiver.OutputNetwork.ReferenceId)` keyed by `(PT.ReferenceId, PT.LinkedReceiver.ReferenceId)`.
- For each PR with `_linkedPowerTransmitter != null && PR.IsOn`: emit edge `(PR._linkedPowerTransmitter.InputNetwork.ReferenceId, PR.OutputNetwork.ReferenceId)` keyed by `(PR._linkedPowerTransmitter.ReferenceId, PR.ReferenceId)`.
- Build a `HashSet<(long, long)>` of the keys to deduplicate. A one-directional walk (PT list only) misses the multi-PR fan-out case where one PT links to multiple PRs (vanilla allows this; see RESEARCH.md PT/PR pairing internals).

Probe added to Phase 6.5.2:
- `pgp-wireless-cycle-multi-pr-fanout-probe`: PT with two linked PRs (PR-A and PR-B). PR-A's output network is wired back to PT's input network (cycle through PR-A). PR-B's output network goes to an unrelated subnet (no cycle). Tick. Assert PT enters `CycleFaultRegistry` (because PR-A forms a cycle). Assert PR-A enters `CycleFaultRegistry`. Assert PR-B does NOT (no cycle in its branch). Verifies the bidirectional enumeration finds the cycle via PT->PR-A even though PT itself does not "know" it has two PRs to walk through separately.

### 6.5.2 Validation

- Scenario: `pgp-wireless-cycle-probe`. Build a save where a network goes through a step-down transformer to a sub-net, then through a PT/PR pair back to the original network. Vanilla detection: cycle MISSED (transmitter is not `ElectricalInputOutput`). PowerGridPlus detection: cycle detected, cable burned within one tick.
- Scenario: `pgp-wireless-cycle-multi-pair-probe`. Build a save where two PT/PR pairs form a wireless ring (PT1 -> PR1 -> wired -> PT2 -> PR2 -> wired back to PT1's input). Detection should find the cycle within 512-hop limit. One cable burns.
- Scenario: `pgp-non-cycle-wireless-tree-probe`. Build a save where multiple PT/PR pairs form a tree (no cycles). Detection should find NO cycle; no cable burns.

## Phase 7 — Surplus distribution walk

Goal: distribute surplus to soft-demand devices via single-pass priority-blind pure-proportional fair-share.

### 7.1 Surplus aggregation

- After the shed cascade decides every shed, compute per-network surplus: `rigidSupply − rigidDemand`. Cache.
- Compute per-non-shed-transformer propagated request: sum of soft requests below, scaled by efficiency if PT/PR, capped by OutputMaximum × efficiency.
- Walk bottom-up; each transformer's propagated request includes its descendants' requests.

### 7.2 Surplus allocation walk

- For each network, compute total request = local soft requests + sum of propagated requests from non-shed children.
- If sum ≤ surplus: each gets full. If sum > surplus: each gets `request × (surplus / sum)`.
- For propagated children: their share recursively splits among their descendants by the same rule.
- Write each leaf soft-demand device's allocated share into `SoftDemandShareCache`.
- Single-pass: no iteration to redistribute waste.

### 7.3 Headroom caps

- At every hop (cable wire, transformer pass-through), the allocated amount caps at the hop's physical capacity. Excess is lost (not redirected — that's iterative, which we're not doing).
- Cable capacity: `Cable.MaxVoltage`.
- Transformer pass-through: `OutputMaximum × TransmissionEfficiency` (efficiency = 1 for wired transformers).

### 7.4 Validation

- Scenario: `pgp-surplus-proportional-probe`. Set up two batteries on the same subnet with different requested shares. Surplus < total request. Assert each gets `(request / total_request) × surplus`.
- Scenario: `pgp-surplus-via-non-shed-transformer-probe`. Battery behind a non-shed transformer. Surplus passes through, allocates to battery.
- Scenario: `pgp-surplus-blocked-by-shed-probe`. Battery behind a shed transformer. Surplus does NOT reach the battery (the path is closed). Battery receives 0.

## Phase 8 — Issue 1 and 2 verifications

Already implemented today (2026-06-07). Adding probes:

### 8.1 `pgp-off-as-reset-probe`

- Set a synthetic shed on a sample transformer. Verify `IsLockedOut = true`.
- Set `transformer.OnOff = false`. Invoke `SwitchOnOff.RefreshColorState` (directly, headless).
- Assert: `BrownoutRegistry.IsLockedOut(refId) == false`. Assert: `_lockoutUntilTick` dict has no entry for refId.

### 8.2 `pgp-button-color-after-off-probe`

- Set a synthetic shed. Set `OnOff = false`. Wait one tick. Force exit (clear shed).
- Assert: the SwitchOnOff's switchRenderer material is the OFF-state material (not the cached ON-state).
- Requires inspecting material identity; may need reflection on SwitchOnOff's `off` / `on` / `onPowered` material fields.

## Phase 9 — Test probe refresh

### 9.1 Update probes for new API

- `pgp-priority-shedding-probe` (the big PSP probe in Dispatcher.cs): heavily uses removed API (`NoteShortfall`, `GetAllocatedSupply`, `InvalidateAll`, `TrimCache`). Either delete or rewrite end-to-end for the new architecture.
- `pgp-priority-shedding-topology-probe`: same. Delete or rewrite.
- `pgp-priority-shedding-network-breakdown-probe`: same.
- `pgp-priority-shedding-hover-probe` P3: stale (tests Thing.GetPassiveTooltip which we no longer patch). Delete this probe-step; rely on OP P7 for hover validation.

### 9.2 Add the new probes from sections 3-7 above

- `pgp-soft-demand-cap-probe`
- `pgp-priority-per-network-probe`
- `pgp-leaf-cascade-probe`
- `pgp-cascade-edge-cases-probe`
- `pgp-transmitter-shed-probe`
- `pgp-transmitter-distance-efficiency-probe`
- `pgp-transmitter-pgp-compat-probe`
- `pgp-surplus-proportional-probe`
- `pgp-surplus-via-non-shed-transformer-probe`
- `pgp-surplus-blocked-by-shed-probe`
- `pgp-off-as-reset-probe`
- `pgp-button-color-after-off-probe`
- `pgp-cable-max-settings-probe` (Phase 1.5)
- `pgp-cycle-detection-bug-fix-probe` (Phase 1.6)
- `pgp-wireless-cycle-probe` (Phase 6.5)
- `pgp-wireless-cycle-multi-pair-probe` (Phase 6.5)
- `pgp-non-cycle-wireless-tree-probe` (Phase 6.5)

### 9.4 Edge-case probes from POWER.md invariants

Probes that verify the invariants listed in POWER.md §17. Each invariant should have at least one probe.

#### Battery dual-terminal anatomy (Invariant 11)

- `pgp-battery-dual-terminal-probe`: place a `StationaryBattery` with Input and Output cables on two distinct networks. Snapshot via InspectorPlus the four power methods. Assert `GetUsedPower(Input)` returns headroom, `GetUsedPower(Output)` returns 0, `GetGeneratedPower(Output)` returns stored, `GetGeneratedPower(Input)` returns 0.
- `pgp-battery-simultaneous-charge-discharge-probe`: build a topology where the battery's Input network has surplus AND the Output network has rigid demand. Set the battery to a partial state (50%). Run one tick. Assert `PowerStored_after = PowerStored_before + charge_in - discharge_out`, with both `charge_in` and `discharge_out` non-zero on the same tick, each at their respective rate cap.

#### APC in-tick interlock (Invariant 12)

- `pgp-apc-no-simultaneous-charge-discharge-probe`: same setup as above with an APC instead. Assert that on any single tick the inserted cell either charges OR discharges, never both. Verify `_powerProvided` sign at start of tick gates the direction.

#### Short-circuit gate (Invariant 13)

- `pgp-short-circuit-gate-probe`: wire both terminals of a battery (and a transformer, and an APC) to the same cable network. Assert: `GetUsedPower`, `GetGeneratedPower`, `ReceivePower`, `UsePower` all return 0 / no-op. Assert hover text shows "Device Short Circuited" via `Thing.GetExtendedText`. No allocator code path mishandles the device.

#### Single-transformer no-overdraw (Invariant 14)

- `pgp-single-transformer-cable-cap-probe`: place a transformer with `OutputMaximum = 50000 W` whose output cable is normal-tier (`MaxVoltage = 5000 W`). Downstream demand 50000 W. Assert that the transformer pulls at most 5000 W from input and delivers at most 5000 W to output. Cable does not burn. `actual_throughput` matches `min(OutputMaximum, cable.MaxVoltage)`.
- `pgp-multi-transformer-cable-burn-probe`: place TWO transformers (each `OutputMaximum = 3000 W`) both feeding the same normal-tier (`MaxVoltage = 5000 W`) output network. Combined output 6000 W with demand 6000 W. Cable should burn within a tick or two (standard vanilla cable-burn rolling probability).

#### Cable max settings (Invariant 15)

- `pgp-cable-max-settings-zero-unlimited-probe`: set `CableNormalMaxWatts = 0`. Reload mod. Push 100000 W through a normal cable. Assert no burn.
- `pgp-cable-max-settings-override-probe`: set `CableHeavyMaxWatts = 10000`. Reload mod. Push 15000 W through a heavy cable. Assert burn.
- `pgp-cable-max-vanilla-reads-same-value-probe`: after settings rewrite, snapshot a `Cable` instance via InspectorPlus. Assert `cable.MaxVoltage` matches the configured value (after `0 -> float.MaxValue` normalisation).

#### PT/PR distance-loss model agnosticism (Invariant 16)

- Covered by `pgp-transmitter-distance-efficiency-probe` and `pgp-transmitter-pgp-compat-probe` (Phase 6.6).

#### Watts-only sim (Invariant 17)

- `pgp-watts-only-units-probe`: snapshot network values via InspectorPlus during a tick. Assert all `Potential`, `Required`, `Consumed`, `cable.MaxVoltage` are scalar Watts numbers; no unit conversion code paths exist.

#### Failure colour: orange shed, red overload (POWER.md §11)

- `pgp-failure-colour-shed-probe`: force a shed. Snapshot the on/off button's renderer material via InspectorPlus. Assert emission colour matches `Color(1f, 0.55f, 0f)` orange band.
- `pgp-failure-colour-overload-probe`: force an overload. Assert emission colour matches `Color(1f, 0.15f, 0.15f)` red band (NOT orange).
- `pgp-failure-colour-co-occurrence-probe`: force both shed and overload on one transformer. Assert RED flash (overload precedence). Assert hover text shows the overload message first then shed second. Assert `Shedding == 1 AND Overloaded == 1` via IC10.

#### Overload per-transformer hit-max trigger (POWER.md §8.4)

- `pgp-overload-single-hit-max-probe`: T1 (max 500 W) feeds output network with rigid demand 800 W. Assert T1 enters `OverloadRegistry` after Phase 2 settles.
- `pgp-overload-both-hit-max-probe`: T1 (max 500 W) and T2 (max 500 W) feed output network with rigid demand 1500 W. Both hit max. Assert both enter `OverloadRegistry`.
- `pgp-overload-throttled-not-tripped-probe`: T1 (input-cable-throttled to 200 W of its 500 W max), T2 (max 500 W), output rigid 900 W. T1 runs at 200 (below its OutputMaximum), T2 runs at 500 (at max). Assert T1 NOT in `OverloadRegistry`, T2 IS in `OverloadRegistry`. This is the key option-(b) discriminator.

#### Cycle detection coverage (POWER.md §4.1, §4.2)

- `pgp-cycle-2-transformer-probe`: T1 N1->N2, T2 N2->N1. Vanilla detection should fire. Assert one cable burns within Phase 3 of the first tick the cycle exists.
- `pgp-cycle-3-transformer-ring-probe`: T1 N1->N2, T2 N2->N3, T3 N3->N1. Same expectation.
- `pgp-cycle-transformer-apc-mix-probe`: T1, APC, T2 in a ring. Same expectation.
- `pgp-cycle-bug-fix-paired-anchors-probe`: two anchors with disjoint cycle chains. Without our fix one is missed; with our fix both detected. Validates Phase 1.6 fix.
- `pgp-cycle-wireless-probe`: cycle closing through a PT/PR. Without our extension this would be missed; with Phase 6.5 it is detected.

#### Battery elastic supply on output net (POWER.md §7.3)

- `pgp-battery-discharge-fills-shortfall-probe`: output network with rigid 1000 W, generator supply 600 W, battery (discharge cap 500, stored 5000). Assert battery discharges exactly 400 W (the shortfall), not its 500 W cap.
- `pgp-battery-no-discharge-when-supplied-probe`: rigid 1000, generator 1500, battery available. Assert battery discharge = 0 (no need).
- `pgp-multi-battery-discharge-split-probe`: two batteries on same output net, discharge caps 500 and 300, shortfall 600. Assert proportional split: 500*(600/800)=375, 300*(600/800)=225. Both within cap.
- `pgp-battery-discharge-cap-reallocates-probe`: same as above but shortfall 1000. Naive proportional says 625/375 but the 300-cap battery overflows. After reallocation: 500 cap (saturated) + 300 cap (saturated) + 200 unmet rigid. Assert both batteries at their caps, 200 W rigid still unmet (which then triggers shed/overload).
- `pgp-battery-stored-energy-cap-probe` (POWER.md §7.3): battery A discharge rate 200 stored 1000; battery B discharge rate 200 stored 19; shortfall 300 W. effective_A = min(200, 1000) = 200, effective_B = min(200, 19) = 19. effective_total = 219 < 300, each delivers effective: A = 200, B = 19. Residue 81 unmet. Assert A NOT throttled to 150 (equal share) just because B is low.
- `pgp-battery-failsafe-probe` (POWER.md §7.3.1): topology `N0 (generator) -> T1 -> N1 (lights + battery B input) -> B output -> N2 (lights)`. Force T1 to SHED. Assert N1 lights go dark. Assert N2 lights stay lit (B discharges from Output side). Assert B's PowerStored decreases at the expected rate.

#### Cable burn rule: transformer-overload, not cable-burn (POWER.md §5.7)

- `pgp-cable-burn-generator-only-probe`: 3 coal generators on a normal-tier cable (MaxVoltage 5000). Combined supply 9000 W > MaxVoltage. Assert cable burns within a tick (vanilla behaviour kept). NO transformers involved.
- `pgp-cable-burn-transformer-no-burn-probe`: two transformers feeding a single normal-tier output network with rigid demand 9000 W. Combined transformer output exceeds cable cap. Assert cable does NOT burn. Assert BOTH transformers enter `OverloadRegistry`.
- `pgp-cable-burn-mixed-no-burn-probe`: 1 coal generator (4000 W) + 1 transformer (delivering 4000 W) on the same normal-tier cable. Total 8000 W > 5000 cap. Generator alone (4000) is under cap. Assert cable does NOT burn. Assert the transformer enters `OverloadRegistry`.
- `pgp-cable-burn-mixed-burn-probe`: 2 coal generators (6000 W each) on a normal-tier cable. Combined generator supply 12000 W > 5000 cap. Assert cable burns regardless of any transformers also on the net.
- `pgp-battery-never-burns-cable-probe`: battery (discharge rate 6000, stored full) on a normal-tier cable with rigid demand 6000 W on the same net. Battery elastically capped at demand. Assert battery delivers <= 5000 W (cable cap), cable does NOT burn.

#### Producer-isolation rule (POWER.md §8.5, replaces vanilla brownout)

The vanilla brownout case is eliminated. Probes verify the new producer-isolation rule:

- `pgp-producer-isolation-solar-light-hover-probe`: solar panel directly on a network with a light. Tick. Assert solar enters `VariableVoltageFaultRegistry`. Assert NO cable burns. Hover the solar panel: text appended with `(Variable Voltage Fault: connected to Light without transformer. 60.00s)` per Decision M wording. Countdown ticks down each second.
- `pgp-producer-isolation-wind-machine-hover-probe`: wind turbine directly on a network with an arc furnace. Tick. Wind enters registry. Hover the turbine: blank vanilla tooltip now contains the fault line + countdown.
- `pgp-producer-isolation-rtg-light-hover-probe`: RTG directly on a network with a light. Tick. RTG enters registry. Hover the RTG: fault line + countdown.
- `pgp-producer-isolation-coal-light-fault-probe`: coal generator directly on a network with a light. Tick. Assert coal genny enters `VariableVoltageFaultRegistry`. Assert red flash on coal's button. Assert hover `(Variable Voltage Fault: connected to Light without transformer. 60.00s)` per Decision M wording, then countdown.
- `pgp-producer-isolation-valid-config-no-fault-probe`: solar -> transformer -> light cluster. Tick. Assert NO VariableVoltageFault, NO cable burn. Vanilla power flow normal.
- `pgp-producer-isolation-with-battery-no-rigid-no-fault-probe`: solar + battery on the same network with NO rigid loads. Assert NO fault, NO burn (producer-only-network rule per Decision G).
- `pgp-producer-isolation-with-apc-no-rigid-no-fault-probe`: same with APC as the only segmenter and NO rigid loads. Assert no fault, NO burn.
- `pgp-producer-isolation-battery-does-not-isolate-probe` (NEW per Q1): solar + battery + light on the same cable network (rigid load PRESENT). Battery does NOT satisfy isolation per Decision Q1. Assert VVF fires on solar; cable burns adjacent to light (or VVF on coal if substituted). Battery presence does not silence the rule.
- `pgp-producer-isolation-apc-does-not-isolate-probe` (NEW per Q1): solar + APC + light on the same cable network. Assert VVF fires; APC does not satisfy isolation.
- `pgp-producer-isolation-rtg-and-machine-burn-probe`: RTG + arc furnace on the same network. Assert cable burns adjacent to the furnace. Hover text contains `(adjacent RadioscopicThermalGenerator)`.
- `pgp-producer-isolation-multiple-rigid-probe`: coal + light + IC10 chip + door (multiple rigid devices). One VariableVoltageFault on coal (per producer, not per rigid). One fault entry; many rigid devices visibly de-powered as a consequence.
- `pgp-producer-isolation-multiple-producers-probe`: coal + solar + light on one network. Both producers fault (coal via VariableVoltageFaultRegistry, solar via cable burn). Both reasons surface.
- `pgp-producer-isolation-mp-broadcast-probe`: dedicated server + 1 client. Force producer fault. Assert client receives VariableVoltageFaultStateMessage and sees red flash on the producer within one tick of broadcast.

#### Fault countdown hover (POWER.md §11.2)

- `pgp-fault-countdown-shed-probe`: trigger SHED at tick T. Poll hover at tick T+0, T+2 (1s), T+4 (2s), ... T+118 (59s). Assert displayed `{n}s` decrements from 60 to 0.
- `pgp-fault-countdown-overload-probe`: same with OVERLOAD.
- `pgp-fault-countdown-cycle-fault-probe`: same with CYCLE_FAULT.
- `pgp-fault-countdown-producer-fault-probe`: same with VARIABLE_VOLTAGE_FAULT.
- `pgp-fault-countdown-off-reset-probe`: trigger SHED at T. At T+10 (5s), toggle OFF. Hover poll at T+12 (6s): assert NO fault prefix at all (registry cleared, countdown gone).

#### Flash visuals on every segmenting device (POWER.md §11.4)

- `pgp-flash-on-battery-probe`: force SHED on a battery via its input network being undersupplied. Assert ORANGE flash on the battery's on/off button. Hover text "(Shedding: ...60s)".
- `pgp-flash-on-apc-probe`: same with APC.
- `pgp-flash-on-transmitter-probe`: same with PT (linked to PR).
- `pgp-flash-on-receiver-probe`: same with PR (linked from PT). The PR has its own on/off button so it flashes independently.
- `pgp-flash-on-rocket-umbilical-probe`: dock a rocket, force a shed on the umbilical pair. Both M and F flash.
- `pgp-flash-on-coal-genny-probe`: force VARIABLE_VOLTAGE_FAULT on a coal generator. Assert RED flash.
- `pgp-flash-on-stirling-probe`: same with stirling.
- `pgp-flash-on-gas-genny-probe`: same with gas fuel generator.
- `pgp-no-flash-on-solar-probe`: solar panel can never enter a flashable state (no OnOff button). Force the producer-isolation scenario and verify the cable burns instead (no flash anywhere).
- `pgp-no-flash-on-wind-probe`: same with wind turbine.
- `pgp-no-flash-on-rtg-probe`: same with RTG.

#### Segmenting devices: full coverage (POWER.md §5.0)

- `pgp-segmentation-rocket-umbilical-probe`: dock a rocket with `RocketPowerUmbilicalMale`. The umbilical's male + female pair should be treated as level boundaries by the cascade (each holds InputNetwork + OutputNetwork). Force a shed upstream of the umbilical and verify the cascade walks through the umbilical bridge.
- `pgp-segmentation-power-connection-probe`: `PowerConnection` (dynamic-generator coupler) is not `ElectricalInputOutput` but holds two networks. Verify it appears in the cascade's segmenting device list.
- `pgp-segmentation-exhaustive-probe`: enumerate every device on the map; assert that the set of "two-network devices" matches exactly the eight classes listed in POWER.md §5.0 (Transformer, Battery, AreaPowerControl, PowerTransmitter, PowerReceiver, RocketPowerUmbilicalMale, RocketPowerUmbilicalFemale, PowerConnection).

#### Battery soft demand on input net (POWER.md §7.4)

- `pgp-battery-charge-headroom-cap-probe`: battery near-full (95%), input network has surplus. Assert reported charge demand caps at `min(charge_rate, PowerMaximum - PowerStored)`, not the full charge_rate.
- `pgp-battery-charge-proportional-probe`: two batteries on same input net, charge caps 500 and 300, surplus 200. Pure proportional: 500*(200/800)=125 and 300*(200/800)=75. Assert exact match.

#### APC split passthrough vs internal charge (POWER.md §7.5)

- `pgp-apc-passthrough-rigid-probe`: APC downstream has rigid 800. APC reports 850 GetUsedPower (passthrough 800 + internal 50). Assert allocator splits correctly: passthrough = 800 rigid, internal = 50 soft.
- `pgp-apc-passthrough-negative-floor-probe`: contrived case where APC's reported GetUsedPower is less than passthrough (transient mid-tick). Assert internal-charge floors at 0, no negative demand.

#### PT/PR power flow (POWER.md §6)

- `pgp-pt-pr-power-flow-direction-probe`: trace `_powerProvided` on PT and PR across a tick. Assert PT side debits (consumer) on its InputNetwork. Assert PR side generates (provider) on its OutputNetwork.
- `pgp-pt-pr-distance-loss-respected-probe`: at distance 0, full power delivers. At distance 400m (vanilla curve) or with PowerTransmitterPlus's k=5, the derate matches the active model's number.

#### Transformer own quiescent draw (POWER.md §5.5)

- `pgp-transformer-quiescent-draw-probe`: transformer drawing 1000 W downstream. Snapshot `transformer.UsedPower` (e.g. 10 W). Assert input-side `GetUsedPower(InputNetwork)` returns 1000 + 10 = 1010 W, not 1000 W.
- `pgp-transformer-zero-downstream-quiescent-probe`: transformer with zero downstream demand. Assert input-side `GetUsedPower(InputNetwork)` returns 0 (not 10 W; the quiescent only applies when the transformer is passing real power per the `Mathf.Min(Setting + UsedPower, _powerProvided)` formula).

### 9.3 Update the `pgp-atomic-all` aggregator

- Add calls to every new probe.
- Remove the obsolete probes.
- Validation goal: 100% pass on `pgp-atomic-all`.

## Phase 10 — Documentation update

### 10.1 Update `Mods/PowerGridPlus/RESEARCH.md`

- Reflect the new architecture (atomic 5-phase, voltage tier and recursive invariants, soft-demand classification, surplus distribution).
- This RESEARCH.md is the eventual home for the POWER.md content; consolidate POWER.md into RESEARCH.md once implementation lands and delete POWER.md.

### 10.2 Update `Mods/PowerGridPlus/README.md`

- Player-facing feature description: shed cascade, surplus to batteries, OFF-as-reset, voltage tier permanence, cycle burn permanence.
- Settings section: remove the dead toggles.
- Recursive/looped networks wording (decision locked, Phase 1.2): the existing README.md line "Recursive and looped power networks are allowed. The vanilla check that force-burns cables in such layouts is off by default; re-enable it in settings" MUST be REPLACED. The new wording describes CYCLE_FAULT behaviour: loops put their segmenting devices (transformers / APCs / batteries / PT/PR pairs / rocket umbilicals) into CYCLE_FAULT for 60 seconds; cables are NOT burned by PowerGridPlus for loops. Vanilla's cable-burn check still runs unsuppressed as a belt-and-braces backstop, but the primary handling is the CYCLE_FAULT lockout on the segmenting devices, so player-facing copy describes the lockout as the expected behaviour.
- Settings table row for "Enable Recursive Network Limits" MUST be removed entirely (the setting is gone per Phase 1.2).
- About.xml `<Description>` and `<ChangeLog>` carry matching wording on release (covered by Phase 10.4 below).

### 10.3 Update `Mods/PowerGridPlus/CHANGELOG.md`

- One top-entry: removed toggles, added bottom-up cascade, added soft-demand cap, added OFF-as-reset, added PT/PR as transformer, atomic tick architecture.

### 10.4 Update `About.xml`

- `<Description>`, `<ChangeLog>`, `<InGameDescription>` reflect the changes (player-facing).
- Verify Description ≤ 8000 chars, ChangeLog ≤ 8000, InGameDescription ≤ ~1450 visual.
- Mirror the CYCLE_FAULT wording from Phase 10.2 in `<Description>` (replace any "Recursive and looped power networks ..." sentence with CYCLE_FAULT phrasing). `<ChangeLog>` carries a one-liner noting that the `EnableRecursiveNetworkLimits` setting was removed and replaced by always-on CYCLE_FAULT lockouts on the segmenting devices.

### 10.5 Update `PLAYTEST.md`

- Add manual-test items for the in-game player to verify:
  - OFF on a shed transformer clears the orange flash immediately.
  - ON re-engages; shed re-fires if conditions still warrant.
  - Solar undersupply with multiple subnets sheds the lowest priority subnet first.
  - Battery in a subnet behind a transformer charges at residual rate when grid has slack.
  - Cycle build burns a cable.
  - Mixed voltage tier connection burns the lower-tier cable.

## Phase 11 — Multiplayer verification

### 11.1 Manual MP playtest

- Start dedi with the new build. Connect two clients.
- Force a shed via the allocator. Verify both clients see the orange flash on the same transformer.
- Toggle OFF on one client. Verify both clients see the flash stop and the button material change.
- Toggle ON. Verify both clients see the shed re-fire if conditions still warrant.

### 11.2 Join-suffix snapshot test

- Have a client join mid-shed. Verify the freshly-joining client sees the active shed/overload state correctly.

## Phase 12 — Release prep

### 12.1 Version bump

- `Mods/PowerGridPlus/PowerGridPlus/Plugin.cs` → `PluginVersion = "0.2.0"` (or whatever next major).
- `Mods/PowerGridPlus/PowerGridPlus/About/About.xml` → matching version.

### 12.2 Steam Workshop publish

- After PLAYTEST manual verification: bundle, publish, update METRICS.md baseline.

### 12.3 Git release commit

- Per `Mods/Template/RELEASE.md`: single-mod release commit, touches only Plugin.cs + About.xml + CHANGELOG.md. Tag `mods/PowerGridPlus/v0.2.0`.

---

## Open questions

### Resolved (decisions locked into POWER.md)

- **Single-pass vs iterate (spec point E).** Resolved: single-pass with cap-aware bottom-up precomputation is optimal under pure-proportional priority-blind surplus distribution. Iteration cannot recover allocations because cap-aware precomputation ensures no over-allocation, and under proportional fairness leftover is by definition unwanted (everyone proportionally short under scarcity, or everyone fully satisfied with leftover unused under abundance). See POWER.md §9.6.
- **PT/PR efficiency source.** Resolved: read `PowerTransmitter.GetGeneratedPower(WirelessOutputNetwork)` at runtime. Vanilla's `PowerLossOverDistance` curve and PowerTransmitterPlus's `MicrowaveEfficiency` formula both feed into this single live value. No model-aware branch in PowerGridPlus. See POWER.md §6.3.
- **PT/PR Harmony patches.** Resolved: PowerTransmitterPlus fully compatible with the atomic tick. The atomic Prefix replaces only the outer scheduler; per-device PT/PR methods still run via Phase 1/3 `CalculateState`/`ApplyState`. PowerGridPlus adds ONE late-priority Postfix on `PowerTransmitter.GetGeneratedPower` for the lockout override; nothing else.
- **Battery anatomy.** Resolved: stationary batteries are `ElectricalInputOutput` (dual-terminal). Charge on Input, discharge on Output, simultaneous in-tick allowed (no `_powerProvided` interlock). APCs are dual-terminal too but their `_powerProvided` makes per-tick charge/discharge in-tick exclusive. Short-circuit (both terminals on same network) caught by vanilla `IsOperable`.
- **Failure colour.** Resolved: same orange for shed and overload. Only the hover text differs.
- **Overload granularity.** Resolved: option (b) per-transformer hit-max. A transformer trips when `actual_throughput == OutputMaximum AND output network has unmet rigid demand`. Cable-throttled transformers running below their OutputMaximum do NOT trip even if downstream is short.
- **Headroom formula.** Resolved: `effective_cap = min(OutputMaximum, InputCable.MaxVoltage, OutputCable.MaxVoltage) - UsedPower`. Actual throughput further bounded by downstream draw. See POWER.md §5.5.
- **Cable max per tier.** Resolved: server-authoritative settings `CableNormalMaxWatts` / `CableHeavyMaxWatts` / `CableSuperHeavyMaxWatts` with `0` meaning unlimited (normalised internally to `float.MaxValue`). Mod-load patches per-prefab `MaxVoltage` so vanilla cable-burn and PowerGridPlus headroom formula read the same number. See POWER.md §5.6.
- **Cycle detection (`ElectricalInputOutput` chains).** Resolved: vanilla covers arbitrary-depth cycles through transformer/APC/battery chains. No extension needed for these. Bug fix: `_networkTraversalRecord` must be cleared per anchor (Phase 1.6).
- **Cycle detection (wireless).** Resolved: vanilla does NOT detect cycles through PT/PR links. PowerGridPlus extends detection (Phase 6.5).
- **All values are Watts.** Resolved: confirmed in vanilla decompile. `Cable.MaxVoltage` is misleadingly named but is a Watts cap.
- **Tiebreak final key.** Resolved: ReferenceId ascending. Applied after (priority, demand) ordering.

### Newly resolved (this round)

- **Same-tier transformer level.** Same-tier transformers count as level boundaries (every dual-network device is a boundary). Confirmed against the 0.2.6228.27061 decompile.
- **Multiple supply sources on one network.** Aggregate. Sum generator + battery discharge into per-net `rigidSupplyByNet`. Vanilla cable burn check kept for direct-generator overflow.
- **Battery elastic supply algorithm.** `effective_discharge_i = min(DischargeRateCap_i, PowerStored_i)`. Proportional by effective_cap. A high-rate battery is not throttled to a "fair share" just because a low-stored sibling can't carry its share.
- **APC discharge cap.** Add new `ApcBatteryDischargeRate` setting (default 1000 W). PowerGridPlus rewrites vanilla `AreaPowerControl.BatteryChargeRate` to the configured value at mod load (so vanilla code paths align). Discharge cap is PowerGridPlus-only (vanilla has no field for it).
- **Battery failsafe.** A battery's discharge to its OUTPUT network is independent of any state on its INPUT network. The dual-terminal model makes this automatic; no special-case code.
- **Transformer state is binary.** No "throttled" intermediate state. A transformer is ON, SHED, or OVERLOAD. The single-tick joint fixed-point iteration (§8.0 in POWER.md, Phase 5 in POWERTODO) converges shed and overload together.
- **Cable burn rule.** Generator-only overflow burns the cable (vanilla). Transformer/battery overflow trips upstream segmenting devices into OVERLOAD. Cable burning from transformer overdraw is eliminated.
- **Cycle burn timing.** Pre-allocator (Phase 1.5 in POWERTODO). Only powered loops burn. Unpowered cycles persist silently until current flows through them.
- **Failure colour.** Shed = orange, Overload = red (distinct).

### Still open

1. **Cycle burn target selection (Phase 1.7).** Which cable in the loop burns? Options: (a) vanilla random `.Pick()`, (b) deterministic by segmenting-device ReferenceId, (c) lowest MaxVoltage cable, (d) highest-throughput network, (e) prefer fuses. See Phase 1.7 for the full list. Needs user pick before implementation.

2. **APC charge demand passthrough math.** With our surplus + cap architecture, downstream is never scaled (Required <= Potential by construction). So APC's passthrough always equals downstream rigid demand exactly. Confirm assumption and what happens during the first tick after a topology change when Phase 1's numbers are stale.

3. **`_powerProvided` accumulator drift.** When a transformer delivers surplus + rigid downstream, its `_powerProvided` rises. Next tick, `GetUsedPower(input)` reflects the sum. Does this play nicely with the per-tick atomic allocation, or does drift accumulate? Probably fine, but trace through with a probe.

4. **Settings panel section grouping.** With several settings removed and added, some `Server - *` sections may have only one entry or be reorganised. Stylistic; keep as-is unless cleanup desired.

5. **MorePowerMod detection.** Assembly is `MorePowerMod`; type is presumably `StationBatteryNuclear` in the `Assets.Scripts.Objects.Pipes` namespace. Verify in their published mod (Workshop ID) when implementing Phase 3.4.

6. **Backward-compatibility for saves with shed-state at save time.** Shed state is transient (lockouts clear on load). No save format change. But verify by saving mid-shed and loading.

7. **RocketPowerUmbilical and PowerConnection treatment.** These are segmenting devices but rarely encountered. Do we need special handling beyond "treat as transformer in the cascade"? Specifically: the umbilical pair (Male/Female) is across a dock boundary; the cascade should not try to traverse before the rocket is docked. PowerConnection is a generator coupler; should it participate in the cascade as a transformer or be excluded?

8. **Iteration ordering determinism.** Phase 5's fixed-point iteration needs a deterministic visit order. Specified: `(depth ASC, priority ASC, demand DESC, ReferenceId ASC)`. Confirm this is sufficient for MP-safe identical state across peers.

9. **Settings rename impact on saved values.** Renaming a `(Section, Key)` pair in BepInEx orphans the stored value and resets to default. The cable max settings, `ApcBatteryDischargeRate`, and any new section names are NEW (no rename). If any existing setting changes Section or Key in this refactor, the About.xml `<ChangeLog>` carries a player-facing note per project CLAUDE.md guidance.

10. **APC discharge cap dictionary persistence.** The PowerGridPlus-side `ApcDischargeRateRegistry` (static `Dictionary<long, float>` keyed by APC ReferenceId) defaults to the server setting. Should per-APC overrides be persisted to save (e.g., for IC10 writability), or stay session-only? Default: session-only; the server setting governs.

These get answered as Phase 1+ implementation progresses; not all need answers before starting.
