# Power Grid Plus -- Mod Plan

Status: Plans-stage. Not built, not published, no Workshop handle. This document is the design brief. It is a derivative-work plan: most of the simulation code is lifted from Sukasa's Re-Volt (MIT licensed), trimmed to a subset, plus three original features layered on top.

Reference upstream: https://github.com/sukasa/revolt (MIT, Copyright (c) 2025 Sukasa). A read-only clone lives at `.work/revolt-source/` during planning; treat it as the authoritative source for the inherited behaviour until it is ported.

---

## 1. What this mod is

A power-system overhaul for Stationeers. It is a **pure-patch mod**: it replaces the cable-network power tick and patches vanilla electrical devices (stationary batteries, transformers, APCs). It ships **no new prefabs**: no circuit breakers, no smart breakers, no heavy breaker, no Load Center, no item kits, no recipes, no custom meshes/materials.

On top of the inherited simulation it adds one original concept: heavy cables become a **transmission-voltage tier**, with their burn limit removed, a higher build cost, and connection rules that only let transformers bridge between cable tiers.

Why pure-patch: keeps the mod small, dependency-light (no LibConstruct, no asset bundle), and easy to keep working across game updates. New content can come later as a separate mod or a later phase if wanted.

---

## 2. Mod identity

| Field | Value |
|---|---|
| Display name | Power Grid Plus |
| Code name | PowerGridPlus |
| Folder / `.sln` / `.csproj` | `PowerGridPlus` |
| `RootNamespace` / `AssemblyName` / DLL | `PowerGridPlus` |
| Plugin GUID / `ModID` | `net.sixfive7.powergridplus` (confirm pattern against sibling mods before locking) |
| C# namespace root | `PowerGridPlus` |
| Workshop ID | none yet (assigned at first publish; goes in `About.xml` `<WorkshopHandle>`) |
| Dependencies | StationeersLaunchPad (+ BepInEx, Harmony). StationeersMods or LaunchPadBooster as the prefab/tool helper layer, matching whatever the sibling mods use. **No LibConstruct** (that was only Re-Volt's Heavy Breaker switchgear network, which we drop). |
| Multiplayer | `SetMultiplayerRequired()` -- a power-sim rewrite must run on host and every client. No custom save-data type (Re-Volt's `CircuitBreakerSaveData` is dropped with the breakers). |
| License | Apache 2.0 for our code (repo standard); Re-Volt-derived files keep their MIT notice. See section 8. |

Seed from `Mods/Template/` when the build scaffold is created, then fill placeholders. README/`About.xml` layout follow `Mods/Template/LAYOUT.md`. Settings-panel grouping follows the "mod settings panel grouping and ordering" rules in the root `CLAUDE.md`.

---

## 3. What we inherit from Re-Volt, and what we drop

### 3.1 Inherited (the subset requested)

Port these from `.work/revolt-source/` more or less as-is, renamespaced to `PowerGridPlus`:

| Re-Volt file(s) | What it gives us |
|---|---|
| `RevoltTick.cs` | The rewritten power tick: proportional load sharing across all providers, proportional distribution to loads, sliding-window cable-overheat burn (lerped 10-20s average, RNG-rolled, scaled by a config factor), weakest-fuse/weakest-cable-first failure, fuses blow instantly and always pre-empt a cable burn, NaN-power coercion, the "Power Transmitter creates a cable-less network" guard, provider-array / `InputOutputDevices` resync so the Network Analyzer cartridge stays correct, optional re-enable of the vanilla recursive-network check. |
| `PowerTickPatches.cs` | Reroutes `PowerTick.Initialise` / `CalculateState` / `ApplyState` to the new tick; reverse-patches `CacheState` and `CheckForRecursiveProviders`. **Mandatory glue** -- not on the user's feature list but required for the tick to function. |
| `CableNetworkPatches.cs` | Injects a `PowerGridTick` (our renamed `RevoltTick`) into every `CableNetwork` on construction; marks the tick dirty when device lists change. **Mandatory glue.** |
| `DevicePatches.cs` | Re-implements `Device.AssessPower` so per-device power gating goes through the new tick. **Almost certainly mandatory glue** -- verify the tick behaves without it before deciding to drop it. With no Load Center, the power-class gate is always "on", so this reduces to routing vanilla behaviour through our tick. |
| `StationaryBatteryPatches.cs` | Charge/discharge-rate limits on stationary batteries (`GetUsedPower` capped at `PowerMaximum * MaxChargeRate`, `GetGeneratedPower` capped at `PowerMaximum * MaxDischargeRate`); charge-efficiency reimplementation of `Battery.ReceivePower` with a low-power floor. |
| `BatteryLogicPatch.cs` | Adds `ImportQuantity` (= max charge rate, W) and `ExportQuantity` (= max discharge rate, W) as logic-readable values on `Battery`. |
| `TransformerExploitPatch.cs` | Free-power exploit mitigation + restored quiescent draw on `Transformer` (`GetGeneratedPower` / `GetUsedPower` / `ReceivePower` reimplemented; output clamped to `min(Setting, upstream potential - already provided)`; the transformer's own `UsedPower` charged upstream). |
| `TransformerLogicPatch.cs` | Adds `PowerActual` (current power provided) as a logic-readable value on `Transformer`. |
| `AreaPowerControllerPatches.cs` | APC fixes: eats its own quiescent current from incoming power, only charges its battery from genuine surplus, discharges its battery to satisfy downstream, reports `UsedPower` upstream correctly. Closes the ~10 W free-power gap and the slow-battery-drain-with-no-output bug. |
| Power-class device classification (`RevoltTick.ClassifyDevice`) | **Kept only if the tick needs it.** Without a Load Center the classes are never toggled, so this may collapse to a no-op. Decide during port: keep the method (cheap, harmless) or strip it. Tracked as open item O-7. |
| `ReVoltStrings.cs` localization plumbing | Trim hard. Only keep interface strings that survive (most are breaker/load-center strings that go away). New strings needed: voltage-tier rejection messages (see NEW-3). |

Also keep: `MOD.SetMultiplayerRequired()`. Drop: `MOD.AddSaveDataType<...>()` (no custom save data).

### 3.2 Dropped from Re-Volt

| Dropped | Why |
|---|---|
| Small Circuit Breaker, "Reversed" variant | No new content. |
| Smart Breaker, "Reversed" variant | No new content. |
| Heavy Breaker (`HeavyBreaker.cs`, switchgear pseudo-network, bolted door, LibConstruct dep) | No new content; also removes the LibConstruct dependency entirely. |
| Load Center (`LoadCenter.cs`, `ILoadCenter`, the 5 power-class toggles, indicator components) | No new content. |
| Item kits + `electronics.xml` recipes (`ItemKitSmallBreaker`, `ItemKitSmartBreaker`, `ItemKitLoadCenter`, the commented heavy-breaker recipe) | No new content. |
| `PrefabPatcher.cs` (custom-prefab loader + material remap to vanilla colours) | Nothing to load. |
| Visual components: `BreakerStatusScreen`, `AssignableBinaryPoweredMaterialChanger`, `ReVoltMultiStateAnimator`, `InfoScreen`, `MaterialChanger`, `Wireframe` | Only used by the dropped devices. |
| `ThingPatches.cs` (`Thing.HasState` Button3 bugfix + Button4/5 state support) | Only needed by the dropped multi-button devices. (If a future phase adds content, this comes back.) |
| Breaker/cable-burn trip coordination in `ApplyState_New` (the "if breakers can collectively interrupt this, trip them all and let power flow one tick" branch, plus the author's noted micro-exploit) | No breakers. The branch reduces to "burn the cable". |
| Brownout TODO stub | Was never implemented in Re-Volt either. |
| `CircuitBreakerSaveData.cs` | No breakers, no per-thing save state to add. |
| `IBreaker`, `IPatchable`, `ISwitchgearComponent`, `ISetable` usages, `MultiConstructor`/`SingleConstructor` subclasses | Tied to the dropped content. |
| Config: `Heavy Breaker Maximum Trip Setting`, `Enable Custom Objects` | No heavy breaker, no prefabs. |

---

## 4. New features (Power Grid Plus original)

The three new features all act on the **three vanilla cable tiers** (`Cable.Type { normal, heavy, superHeavy }`) and the **three vanilla transformer tiers** (one `Transformer` class; the small/medium transformer kit builds a Small or Medium transformer prefab, each with a reversed variant; the large transformer kit builds a Large transformer prefab, with a reversed variant -- exact prefab names to confirm, O-5).

### NEW-1. Unlimited super-heavy cables

Super-heavy cable stops having a burn limit. It never ruptures, regardless of throughput -- it is the long-haul backbone. (Normal cable keeps its 5 kW rating and heavy cable keeps its own rating; only super-heavy becomes "infinite".)

Implementation: inside our ported `PowerGridTick`, when building the burnable-cable list (Re-Volt's `_allCables` `SortedList<float, List<Cable>>` keyed by `MaxVoltage`), skip any cable with `Cable.CableType == superHeavy` -- it can never be the weakest burnable element. Vanilla decides rupture in `PowerTick.GetBreakableCables` (`cable.MaxVoltage < _actual`); Re-Volt replaces the whole `PowerTick`, so the hook lives in our port. The `CableType` enum is the clean discriminator -- no prefab-hash list needed. Fuses are untouched (a super-heavy run simply has no cable to protect, so a fuse on it is harmless).

Config: `Enable Unlimited Super-Heavy Cables` (default on). Off = super-heavy burns per the inherited sliding-window model (Re-Volt already gives it large headroom).

Residual (O-1): the real `MaxVoltage` for `heavy` and `superHeavy` is prefab serialized data, not in the decompile -- not needed since we key off `CableType`, only relevant if a "burn above N watts" config is ever exposed. Confirm the insulated-cable variant's tier (`CableType.normal` with a high `MaxVoltage`, presumably).

### NEW-2. Super-heavy cable costs more

Super-heavy cable's build cost is multiplied by 2.0 by default, so the backbone is a real investment, not a default choice.

Implementation: two cost levers exist (per section 4a) -- (1) the coil-crafting XML recipe (`GameData.*Recipes` -> `RecipeData.Recipe` for the super-heavy coil item; multiply the per-reagent quantities), and (2) the per-placement coil count (`<super-heavy-cable-structure-prefab>.BuildStates[0].Tool.EntryQuantity`, read by `MultiConstructor.Construct` / `CanBuild`). Default plan: multiply (1) (raw ore per coil) so the cost shows at the printer; leave (2) vanilla. Config: `Super-Heavy Cable Cost Multiplier` (default `2.0`; `1.0` = vanilla). See `Research/Patterns/AccessToolsRecipes.md`. The `Recipe` type lives outside `Assembly-CSharp`; confirm exact reagent fields on a follow-up. Mid-save recipe change affects only new crafts.

### NEW-3. Three-tier voltage gating

The three cable tiers represent three different transmission voltages and are **mutually incompatible**: a `normal` cable, a `heavy` cable, and a `superHeavy` cable may never be electrically joined. The only legal bridge between any two tiers is a **transformer**. The intended topology: device-level loads on `normal` (5 kW) cable -> transformer -> `heavy` cable for medium-distance distribution -> transformer -> `superHeavy` backbone for long-haul, and back down the same way. (Re-Volt's `IsOperable` already forces a transformer to bridge two *different* `CableNetwork`s; we are now also forcing the two networks it bridges to be *adjacent tiers*.)

#### 3.1 The ">5 kW device" problem (you asked me to double-check this)

**Yes -- several vanilla devices draw far more than 5 kW**, so the literal rule "all devices connect to the normal/5 kW network" cannot hold as stated. Confirmed from the decompile (recorded in `Research/GameSystems/DevicePowerDraw.md`):

- `CarbonSequester` (atmospheric carbon scrubber): `POWER_PER_UNIT_CARBON = 45000f` -- **45 kW per unit of carbon processed**.
- `AdvancedFurnace`: `GetUsedPower` returns up to **3 x `UsedPower`** at full settings; the Advanced Furnace's base `UsedPower` is already a large prefab value (it is the classic "needs heavy cable" machine).
- `ArcFurnace`: `GetUsedPower` is `UsedPower + recipe.Energy`-per-tick; spikes hard per smelt.
- The `Furnace`, `Centrifuge`, `Recycler`, `IceCrusher`, `HydraulicPipeBender`, electric `DeepMiner`, etc. each draw their per-prefab `UsedPower` while active; several exceed 5 kW. (Per-device `UsedPower` is prefab serialized data, so an exhaustive list needs InspectorPlus or a prefab extract -- but the multipliers and the 45 kW constant are enough to prove the problem exists.)

A transformer does **not** rescue this: a transformer steps voltage between two *networks*, but the cable segment physically feeding a 45 kW device still carries 45 kW, so that segment must itself be a >= 45 kW (`heavy`) cable. Therefore the design needs one of:

- **(a) Tier-by-draw, not "everything on normal".** A device connects to whatever tier of cable can carry its draw; the *tiers* are still mutually incompatible and still bridged only by transformers. So a light goes on `normal` cable, a 45 kW Carbon Sequester goes on `heavy` cable, and the `heavy` run feeding the Sequester cannot be directly joined to the `normal` run feeding the lights -- you transformer between them. This is coherent, keeps the three-tier gating, and matches how players already wire vanilla. **Recommended.** The "all devices on the smallest network" goal becomes "all devices on the *smallest network rated for their draw*", which for the vast majority of devices is still `normal`.
- **(b) A device power cap on `normal` plus a carve-out whitelist of high-draw machines allowed on `heavy`.** More explicit but needs a maintained list (and the list is prefab data).
- **(c) Allow nothing but transformers on `heavy`/`superHeavy`, and accept that the handful of >5 kW machines are simply unbuildable / need a different power model.** Probably too restrictive; flagged for completeness.

This is **Decision C** (see decision log) and it needs your call before NEW-3 is implementable.

#### 3.2 Which transformer tier bridges which cable tiers?

Three transformer tiers exist (Small / Medium / Large prefabs of the one `Transformer` class, plus reversed variants), and three cable tiers (`normal` / `heavy` / `superHeavy`). Options:

- **(i) Fixed mapping.** Small/Medium transformer = `normal <-> heavy` bridge; Large transformer = `heavy <-> superHeavy` bridge. Clean, teachable, matches the tier names. (A Small vs Medium transformer then differ only in `OutputMaximum`, both bridging the same tier pair.)
- **(ii) Any transformer bridges any *adjacent* pair, limited by its `OutputMaximum`.** More flexible; you cannot push more than the cable carries because the transformer's `Setting` is capped at its `OutputMaximum` and the downstream cable burns above its rating anyway.
- **(iii) Any transformer bridges any two tiers (even `normal <-> superHeavy` directly).** Simplest to implement; loses the "step through each voltage" flavor.

This is **Decision D**. Leaning **(i)** for the lore, **(ii)** as a softer alternative. ("No `normal <-> superHeavy` direct" is the one constraint I'd keep regardless.)

#### 3.3 Enforcement: build-time rejection vs burn-on-join vs both

You asked whether "burn the smaller cable when networks of different voltages join" is cleaner than the build-time `CanConstruct` rejection I proposed earlier. Here is the comparison:

- **Build-time rejection (`Cable.CanConstruct` / `Device.CanConstruct` postfix returning `CanConstructInfo.InvalidPlacement("...")`).** The placement-preview loop calls `CanConstruct()` every frame, gates the left-click on it, and shows a red error in the cursor tooltip (per `Research/GameSystems/StructurePlacementValidation.md`). So you simply *cannot place* a cable/device that would join two tiers, and you see why. **Pros:** cleanest UX, zero resource loss, no debris. **Cons:** client-side preview only -- the server's `Constructor.SpawnConstruct` doesn't re-validate, so it isn't authoritative; and it does NOT cover *non-build* ways networks can join (CableTypeSwitcher converting a network's tier at runtime, EL Switche logic-spawning cables, MoreCables, loading a save that already has a mixed junction). Also `Cable.OnRegistered` -> `CableNetwork.Merge(...)` ignores `CableType` today, so a network *does* form across the junction the instant the cable lands -- the rejection has to fire *before* that, which it does (preview gates the placement) for the interactive case but not for the others.
- **Burn-on-join (reactive: detect a mixed-tier `CableNetwork` and rupture the lowest-tier cable at the junction).** When two tiers do get joined -- by any means -- find the lowest-tier cable touching the junction and call `cable.Break()` on it. That cable becomes `CableRuptured` and is destroyed, which splits the networks back apart, self-healing the topology violation. **"How does that work if it's unpowered?"** -- `Cable.Break()` does not require power: it spawns the `CableRuptured` prefab and `OnServer.Destroy`s the cable regardless of load; the only power-dependent bit is the cosmetic `if (CableNetwork.RequiredLoad > 0f) WorldManager.Spark(...)`, i.e. an unpowered illegal junction just ruptures without a visible spark. So burning works fine on a cold network. **Where to hook:** a postfix on `Cable.OnRegistered` (and on whatever the merge path is), running on the server, that scans the resulting `CableNetwork.CableList` for mixed `CableType` and, if found, picks the lowest-tier cable at the offending cell and `Break()`s it. **Pros:** works no matter HOW the join happened (other mods, save load, angle-grinder merge) -- it is the authoritative backstop; "do a dumb electrical thing, a cable burns" is very Stationeers-flavored; "burn the *smaller* cable" is physically right (the lower-rated component is the one that fails). **Cons:** it costs the player the junction cable (a coil's worth -- `CableRuptured` may refund some material on cleanup, TBD); on loading an old save full of mixed junctions you'd get a cascade of ruptures on first tick (alarming, but it does converge); and the player only learns *after* the fact, not before.
- **Recommended: both.** Build-time rejection is the primary UX (clear, lossless, you just can't do it interactively). Burn-on-join is the authoritative server-side backstop for everything the preview can't catch (other mods, save migration -- it IS the O-8 reconciliation pass), and the only mechanism that's truly authoritative. The build-time check is "be nice"; the burn is "be correct". This is the same belt-and-braces design noted earlier; the burn doesn't *replace* the rejection, it backs it.

**How does the burn interact with fuses?** A fuse (`CableFuse`) blows on *over-current* (`PowerBreak < _actual` in `PowerTick.GetBreakableFuses`). A tier-mismatch is a *topology* violation, not an over-current event, so the fuse-priority logic (Re-Volt's "a fuse rated low enough always blows before a cable") does not apply: we rupture the offending cable directly, regardless of any fuses on the network, even if the network is cold. That is intentional -- a fuse is a current-protection device; it cannot and should not "save" you from wiring the wrong voltage together. (A flavour alternative: if there happens to be a `CableFuse` *at the junction cell*, burn the fuse instead of the cable -- "the fuse sacrificed itself." Cute, but fuses are cheap intentional safety devices and burning the cable is the better lesson; I'd rupture the cable. Open to the other choice.) Note one ordering subtlety: the burn-on-join pass runs at registration/merge time (off the power tick), so it happens before any power flows that tick; the normal fuse/cable over-current path in `ApplyState` is a separate, later thing and is unaffected.

#### 3.4 Implementation summary

1. Port `RevoltTick` -> `PowerGridTick`; NEW-1's super-heavy exemption lives there.
2. Harmony postfix on `Cable.CanConstruct` and `Device.CanConstruct`: reject (return `CanConstructInfo.InvalidPlacement`) when placing a cable/device that would put two different `CableType`s on one network without a transformer between them. Also postfix `Cable.CanReplace` (angle-grinder merge path). New interface strings for the messages.
3. Server-side postfix on `Cable.OnRegistered` (and the network-merge path): scan the resulting network for mixed `CableType`; if found, `Break()` the lowest-tier cable at the junction. Same pass, run on `OnFinishedLoad`, migrates old saves (= the O-8 reconciliation).
4. The transformer-tier mapping (Decision D) is a small lookup applied wherever "is this a legal bridge?" is asked (in the `CanConstruct` postfix and the merge scan).
5. Config: `Enable Voltage Tiers` (master toggle), and -- if Decision C lands on (b) -- a "Max Device Draw On Normal Cable" threshold.

Open items remaining: O-5 (exact transformer/cable prefab names), O-8 (now folded into the burn-on-join backstop above -- the remaining choice is just "rupture vs error vs warn" on save migration; rupture is the recommendation), O-9 (mod compat: MoreCables' extra tiers -- treat them as `superHeavy`-equivalent or as their own tier? -- and detecting Re-Volt for NEW-1 since it swaps the `PowerTick`), and Decisions C and D above. Full findings in section 4a.

---

## 4a. NEW-3 research findings (2026-05-12)

Result of the in-depth decompile spike (game version 0.2.6228.27061). Durable game-internals findings were committed to the central knowledge base: `Research/GameClasses/Cable.md`, `Research/GameClasses/PowerTick.md`, `Research/GameClasses/Transformer.md`, `Research/GameSystems/StructurePlacementValidation.md` (commit `bca0e53`). Summary:

- **Cable tiers already exist in code.** `Cable.Type { normal, heavy, superHeavy }` (decompile line 371285). `MaxVoltage` (rupture threshold in watts despite the name) is per-prefab serialized data; `5000f` is just the C# field default. `CableType` is used today only for *visual* merging / grid occupancy (`Cable._IsCollision`, `CanReplace`, `WillMergeWhenPlaced` all refuse to merge different-tier cables) -- it does NOT affect electrical-network membership: `IsConnected` / `ConnectedCables` / `CableNetwork.Merge` ignore it, so a heavy cable and a normal cable that touch are one `CableNetwork` today. Cable open ends are `NetworkType.Power` / `PowerAndData = 6`; there is no per-tier `NetworkType`.
- **Build-time rejection is feasible and clean.** `InventoryManager`'s placement-preview loop calls `ConstructionCursor.CanConstruct()` every frame (and `IGridMergeable.CanReplace` for cables), gates the left-click on the result, and writes a red `ErrorMessage` into the cursor tooltip on failure. `Cable.CanConstruct` and `Device.CanConstruct` already override the base, so they are the natural Harmony-postfix hook. `CanConstructInfo.InvalidPlacement(string)` is the rejection factory. **Caveat:** the gate is client-side preview only -- the server's `Constructor.SpawnConstruct` does not re-run `CanConstruct`. Acceptable for a balance/lore restriction; a server-side `OnRegistered` postfix that severs/errors an illegal connection would be the belt-and-braces add-on.
- **No "heavy transformer" exists.** Exactly one `Transformer` class (`: ElectricalInputOutput`); no `TransformerLarge` / `TransformerSmall` subclasses (a smaller transformer prefab is the same class with a different serialized `OutputMaximum`). `IsOperable` is already false when `InputNetwork == OutputNetwork`, so a transformer is already the only network-bridging device. A dedicated "heavy transformer" means one new prefab of the same class. -> decision Decision A.
- **Vanilla cable burn** (what NEW-1 must intercept): `PowerTick.GetBreakableCables` flags any cable with `MaxVoltage < _actual` (`_actual = min(Potential, Required)`); `BreakSingleCable` picks one at random and `Break()`s it; a fuse blows first if any `PowerBreak < _actual`. Re-Volt replaces the whole `PowerTick`. Since we port `RevoltTick`, NEW-1 lives inside our port (skip `heavy`/`superHeavy` cables when building the burnable-cable list).
- **Heavy-cable cost** (NEW-2): two levers -- the coil-crafting XML recipe (`GameData.*Recipes` -> `RecipeData.Recipe`) and the per-placement coil count (`BuildStates[0].Tool.EntryQuantity` on the cable structure prefab). Default: multiply the XML recipe.
- **One existing-research correction surfaced:** `Research/GameClasses/CableNetwork.md` describes `ConsumePower` / `CalculateState` / the "single-supplier-first" provider iteration as `CableNetwork` members; they are actually `PowerTick` members held in `CableNetwork.PowerTick`. The line numbers and code bodies on that page are correct; only the class name is imprecise. New `Research/GameClasses/PowerTick.md` documents the correct attribution; a wording fix on `CableNetwork.md` is deferred (additive-only this pass, so no fresh-validator needed).
- **Save migration (O-8) and mod compat (O-9) are the residual unknowns.** A save with a now-illegal heavy-to-device junction re-merges silently on load/join (no tier check in `Cable.DeserializeSave` / `DeserializeOnJoin`); NEW-3 must add an `OnFinishedLoad` reconciliation pass. MoreCables and CableTypeSwitcher (Appendix B) interact with cable tiers; Re-Volt swaps the `PowerTick` instance, bypassing a vanilla-`PowerTick` patch.

---

## 5. Requirements rollup

Build = inherited features (3.1) + mandatory glue + the three Power Grid Plus features.

| ID | Requirement | Source | Notes |
|---|---|---|---|
| R-1 | RevoltTick power-sim rewrite, ported and renamed `PowerGridTick` | Re-Volt `RevoltTick.cs` | Trim breaker-coordination branch and (probably) keep `ClassifyDevice` as a no-op-friendly stub. |
| R-2 | PowerTick reroute patches | Re-Volt `PowerTickPatches.cs` | Mandatory glue. |
| R-3 | CableNetwork tick injection | Re-Volt `CableNetworkPatches.cs` | Mandatory glue. |
| R-4 | Device.AssessPower reimpl | Re-Volt `DevicePatches.cs` | Verify still needed; almost certainly yes. |
| R-5 | Stationary battery limits + charge efficiency | Re-Volt `StationaryBatteryPatches.cs` | |
| R-6 | Battery logic additions | Re-Volt `BatteryLogicPatch.cs` | |
| R-7 | Transformer exploit mitigation + quiescent draw | Re-Volt `TransformerExploitPatch.cs` | |
| R-8 | Transformer logic addition | Re-Volt `TransformerLogicPatch.cs` | |
| R-9 | APC fixes | Re-Volt `AreaPowerControllerPatches.cs` | |
| R-10 | `SetMultiplayerRequired()` | Re-Volt `ReVolt.cs` | |
| R-11 | NEW-1 unlimited heavy cables | new | |
| R-12 | NEW-2 heavy cable cost | new | |
| R-13 | NEW-3 voltage-tier connection gating | new | Spike done (section 4a). `Cable.CanConstruct` / `Device.CanConstruct` postfixes + `OnFinishedLoad` reconciliation. Pending Decision A, O-8. |
| R-14 | Config surface + StationeersLaunchPad grouping | new + Re-Volt configs | See section 6. |
| R-15 | Stationpedia notes for the changed devices/cables | repo convention (StationpediaPlus integration) | Optional first pass; follow whatever pattern the sibling mods use. |

---

## 6. Config surface

Follow the root `CLAUDE.md` "mod settings panel grouping and ordering" rules: `<Scope> - <Topic>` section names, `("Order", int)` tags spaced by 10, `(Server-authoritative)` / `(Client-local)` description prefixes. Everything here is server-authoritative (power simulation), so all sections are `Server - *`.

| Section | Key | Default | Order |
|---|---|---|---|
| Server - Cable Simulation | Cable Burn Factor | `1.0` (0 disables cable burn) | 10 |
| Server - Cable Simulation | Enable Unlimited Super-Heavy Cables | `true` | 20 |
| Server - Cable Simulation | Enable Recursive Network Limits | `false` | 30 |
| Server - Cable Costs | Super-Heavy Cable Cost Multiplier | `2.0` | 10 |
| Server - Voltage Tiers | Enable Voltage Tiers | `true` | 10 |
| Server - Voltage Tiers | (further tier knobs once O-3..O-6 are settled) | -- | 20+ |
| Server - Batteries | Enable Battery Limits | `true` | 10 |
| Server - Batteries | Max Battery Charge Rate | `0.002` | 20 |
| Server - Batteries | Max Battery Discharge Rate | `0.007` | 30 |
| Server - Batteries | Battery Charge Efficiency | `1.0` | 40 |
| Server - Batteries | Enable Battery Logic Additions | `true` | 50 |
| Server - Transformers | Enable Transformer Exploit Mitigation | `true` | 10 |
| Server - Transformers | Enable Transformer Logic Additions | `true` | 20 |
| Server - APC | Enable APC Power Fix | `true` | 10 |

Gone vs. Re-Volt: `Heavy Breaker Maximum Trip Setting`, `Enable Custom Objects`. Note: section/key strings are the entry identity in BepInEx, so the names above are effectively final once shipped (renames re-seed the value).

---

## 7. Work phases

1. **Scaffold.** Copy `Mods/Template/` to `Plans/PowerGridPlus/PowerGridPlus/` (the build project lives one level down, matching `DeepMinerLogger`), rename project/namespace, fill placeholders, wire `Directory.Build.props` `$(StationeersPath)`. No `About.xml` Workshop handle yet.
2. **Port the inherited core.** Bring over R-1..R-10 from `.work/revolt-source/`, renamespaced. Strip breaker/load-center/prefab code as it comes. Get a clean build against the current game DLLs. Smoke-test in the dedicated server: power flows, batteries share load, cables burn probabilistically, transformers behave, APCs do not leak.
3. **Config + grouping.** Implement section 6 exactly. Verify the StationeersLaunchPad panel shows the groups in the intended order.
4. **NEW-1.** Unlimited heavy cables. Cheap, do it early; it is mostly a filter in the tick.
5. **NEW-2.** Heavy cable cost. After O-2 is answered.
6. **Research spike for NEW-3.** DONE (2026-05-12; section 4a; `Research/` pages committed at `bca0e53`). Residual: decide Decision A (heavy-transformer prefab or not) and O-8 (save-migration policy) before phase 7.
7. **NEW-3.** Voltage-tier gating: `Cable.CanConstruct` / `Device.CanConstruct` postfixes rejecting heavy-to-non-transformer adjacency, plus an `OnFinishedLoad` reconciliation pass. Two-tier (`heavy`+`superHeavy`) per D6. New interface strings for the rejection messages.
8. **Stationpedia + README + About.xml.** Per `Mods/Template/LAYOUT.md`. Then it can graduate to `Mods/`.

Implementation gate: phases 2 onward are code. Per repo policy, do not start them until the plan is approved; phase 6's research findings come back here for a decision before phase 7.

---

## 8. Licensing and attribution

This mod is a derivative of Re-Volt (https://github.com/sukasa/revolt), which is under the **MIT License, Copyright (c) 2025 Sukasa**. MIT lets us copy, modify, trim, relicense, and ship, with one obligation: the MIT copyright + permission notice must travel with any substantial portion of the original code.

Therefore:

- Files ported substantially from Re-Volt (`RevoltTick.cs` -> `PowerGridTick.cs`, the transformer/battery/APC/device/cable-network/power-tick patches, etc.) carry a short header crediting Sukasa and pointing at the MIT notice.
- The mod ships a `THIRD-PARTY-NOTICES` (or equivalent) file in its folder with the full Re-Volt MIT text. The `About.xml` `<Description>` and the README `## Credits` section credit Sukasa and link the Re-Volt repo. (This also matches the community norm and Re-Volt's own credits-heavy README.)
- Our original code (NEW-1/2/3, the renamed glue, config) is **Apache 2.0** per the repo standard. The repo `LICENSE`/`NOTICE` cover it; the README ends with the standard `## License` section. The MIT-covered ported portions remain MIT regardless of the surrounding Apache 2.0 (dual provenance, which Apache and MIT both permit).
- No copyleft obligation either way: we are not required to open-source, and a clean reimplementation of any Re-Volt mechanic (as opposed to a copy) carries no MIT notice obligation at all, since mechanics are not copyrightable. Where we copy, we attribute; where we rewrite from scratch, attribution is courtesy.

---

## 9. Open items rollup

- O-1 (RESOLVED, narrowed): NEW-1 keys off `Cable.CableType` (`heavy` / `superHeavy`), no prefab-hash list needed. Residual: real `MaxVoltage` numbers and the insulated variant's tier are prefab data (verify via InspectorPlus) -- only matters if a "burn above N watts" config is ever exposed. [NEW-1]
- O-2 (RESOLVED): two cost levers found -- the coil-crafting XML recipe (`GameData.*Recipes` -> `RecipeData.Recipe`) and the per-placement coil count (`BuildStates[0].Tool.EntryQuantity`). Default plan multiplies the XML recipe. Residual: exact reagent fields on the `Recipe` type (lives outside `Assembly-CSharp`) and the exact gamedata XML path. [NEW-2]
- O-3 (RESOLVED): cable connection model documented -- `Cable.OpenEnds` are `NetworkType.Power`/`PowerAndData`, no per-tier `NetworkType`; `CableType` is enforced only for visual merging, not network membership. See `Research/GameClasses/Cable.md`. [NEW-3]
- O-4 (RESOLVED): build-time rejection IS feasible -- Harmony postfix on `Cable.CanConstruct` / `Device.CanConstruct` returning `CanConstructInfo.InvalidPlacement(...)`; client-side preview only (server `SpawnConstruct` doesn't re-validate). See `Research/GameSystems/StructurePlacementValidation.md`. [NEW-3]
- O-5: does vanilla still ship a separate small-transformer *prefab* (same `Transformer` class, smaller `OutputMaximum`)? Affects naming of the "step-down" device, not feasibility. [NEW-3 / Decision A]
- O-6 (RESOLVED -> Decision B): vanilla cable tiers are `normal`/`heavy`/`superHeavy`; no small-vs-medium split exists. v1 ships two-tier gating (`heavy`+`superHeavy` = backbone, `normal` = everything else). A third tier is new content, deferred. [NEW-3]
- O-7 (RESOLVED): yes -- heavy cables never burn (NEW-1) and only transformers touch them, so the backbone's load is bounded by transformer `Setting`; "set-and-forget backbone" is the intended feel. Nothing else relies on heavy cables being burnable in vanilla beyond `PowerTick.GetBreakableCables`. [NEW-1 + NEW-3]
- O-8 (OPEN): migration of saves with now-illegal heavy-to-device junctions -- `Cable.DeserializeSave`/`DeserializeOnJoin` re-merge with no tier check, so NEW-3 needs an `OnFinishedLoad` reconciliation pass (split / error / warn -- decision pending). Host-authoritative, must replicate. [NEW-3]
- O-9 (OPEN): mod compat -- MoreCables adds `super-heavy`/`super-conductor` tiers, CableTypeSwitcher converts networks between tiers, Re-Volt swaps the `PowerTick` instance (bypassing a vanilla-`PowerTick` patch). Decide the fallback for unknown tiers and whether to detect Re-Volt. See Appendix B. [NEW-3 / NEW-1]
- O-10: is `DevicePatches.AssessPower` truly mandatory; verify the tick without it. [R-4]
- O-11: keep or strip `RevoltTick.ClassifyDevice` once the Load Center is gone. [R-1]
- O-12: confirm the `net.sixfive7.powergridplus` GUID pattern against sibling mods. [identity]
- O-13: first-pass Stationpedia integration scope (which sibling-mod pattern to follow). [R-15]
All design decisions are now made (user, 2026-05-12) -- summary below; full entries D8-D12 in the decision log:

- Decision A -- DECIDED: strictly pure-patch; the vanilla Small / Medium / Large transformer prefabs (+ reversed variants) are the tier bridges, no new prefab. [NEW-3]
- Decision B -- DECIDED (D6): three-tier gating -- `normal`, `heavy`, `superHeavy` cables mutually incompatible, bridged only by transformers. [NEW-3]
- Decision C -- DECIDED: (a) tier-by-draw -- a device sits on the *lowest cable tier rated for its peak draw/output* -- WITH a blanket carve-out (D13): **every electrical generator is `heavy`-tier-only** (yes, including the ~500 W solar panels and the small wind turbine), so all generation enters the grid at `heavy` and is stepped down to `normal` via Small transformers. Stationary batteries -> `heavy` (rate-limited discharge exceeds 5 kW); the >5 kW non-generator devices (section 3.1, `Research/GameSystems/DevicePowerDraw.md`) -> `heavy`+. README ships the list. [NEW-3]
- Decision D -- DECIDED (fixed, heavy-only Medium): **Small transformer (+rev) = `heavy <-> normal`** (no `superHeavy`); **Medium transformer (+rev) = `heavy <-> heavy`** same-tier flow limiter only (no `normal`, no `superHeavy`); **Large transformer (+rev) = `superHeavy <-> heavy`** (no `normal`). No `normal<->normal` or `superHeavy<->superHeavy` transformer (and none needed -- same-tier cable connects freely). `normal <-> superHeavy` direct impossible (route through `heavy`). [NEW-3]
- Decision step-up -- DECIDED: (b) transformers are *bidirectional* for tier-bridging (capped by `Setting`; the cable on each port defines the tier pair, not a direction) -> Power Grid Plus re-implements `Transformer.GetGeneratedPower`/`GetUsedPower`/`ReceivePower` symmetrically (the Re-Volt exploit fix folds into the new bidirectional impl). [NEW-3, R-7]
- Decision E -- DECIDED: (c) both -- build-time `Cable.CanConstruct`/`Device.CanConstruct`/`Cable.CanReplace` rejection (primary UX) + reactive burn-on-join in a server `Cable.OnRegistered` postfix and at `OnFinishedLoad` (authoritative backstop + save migration). Burn-on-join refinement: only the *single lowest-tier cable adjacent to a higher-tier cable* (the boundary cable) ruptures, repeated per touch point; old saves get a one-time boundary-cable cascade on first tick. Fuses never special-cased. [NEW-3]
- Decision #6 -- DECIDED: NEW-2 multiplies only the coil-crafting XML recipe. [NEW-2]

---

## 10. Decision log

- D1 (2026-05-12): Pure-patch mod, no new prefabs. Trims dependencies (drops LibConstruct), simplifies game-update maintenance, keeps the mod focused on simulation. New content, if ever, is a later phase or separate mod.
- D2 (2026-05-12): Derive from Re-Volt (MIT) rather than reimplement the power tick from scratch. Re-Volt's tick is already battle-tested; reimplementing it would be wasted effort and would still want the same fixes. Attribution obligations handled per section 8.
- D3 (2026-05-12): All config is server-authoritative; `Server - *` sections only.
- D4 (2026-05-12): Code name `PowerGridPlus`, display name `Power Grid Plus` (user choice from the name shortlist).
- D5 (2026-05-12): NEW-3 research spike done (section 4a; `Research/` pages committed at `bca0e53`, `a466c94`). Verdict: build-time rejection via a `Cable.CanConstruct`/`Device.CanConstruct` postfix is feasible (client-side preview only); burn-on-join in an `OnRegistered` postfix is the authoritative backstop and also the save-migration pass; `Cable.Break()` works on an unpowered cable. Remaining go/no-go: Decisions C, D, E.
- D6 (2026-05-12, REVISES the earlier D6): Three-tier voltage gating -- the three vanilla cable tiers (`normal`, `heavy`, `superHeavy`) are mutually incompatible, bridged only by transformers (user instruction). This is a pure patch: all three tiers are existing `Cable.Type` values, and vanilla already ships Small / Medium / Large transformer prefabs to bridge them. (The earlier "two-tier, a third tier needs new content" claim was wrong: it conflated "split normal into small+medium" -- which *would* be new content, still deferred -- with "keep the three existing tiers apart".)
- D7 (2026-05-12): Super-heavy-cable cost multiplier default set to `2.0` (user instruction); patches the coil-crafting XML recipe (option 1 of the two cost levers). NEW-1 (no burn limit) also applies only to `superHeavy`, not `heavy` (user instruction): `heavy` keeps its rating.
- D8 (2026-05-12): Decision C resolved -- tier-by-draw, "lowest cable tier rated for peak draw/output". Accepted consequences: stationary batteries are `heavy`-cable gear (Re-Volt's ~0.7%/tick discharge cap on a Station Battery's capacity exceeds 5 kW -- exact figure to be verified before the README claims it); the >5 kW machines (Advanced Furnace, Arc Furnace, Carbon Sequester 45 kW/unit, Furnace, Centrifuge, Recycler, Ice Crusher, Hydraulic Pipe Bender, electric Deep Miner, ...) and generators (RTG 50 kW, Solid Fuel Generator ~20 kW, Gas Fuel Generator tens of kW, Stirling Engine ~6 kW, Large Wind Turbine ~20 kW *storm peak*) live on `heavy`+; the field-total trap (many small `normal`-rated units summing past 5 kW on one network) applies. README ships the list; an InspectorPlus / prefab pass fills in the exact `UsedPower` figures first.
- D9 (2026-05-12): Decision D resolved -- fixed transformer-tier mapping, Medium is heavy-only: Small (+rev) bridges `heavy<->normal`, Medium (+rev) is a `heavy<->heavy` same-tier flow limiter (refuses `normal` and `superHeavy` connections entirely), Large (+rev) bridges `superHeavy<->heavy`. No transformer fits `normal<->normal` or `superHeavy<->superHeavy` -- intentional; those tiers are always one freely-connected network per island (fuses/APCs still do `normal` sub-circuit control). `normal<->superHeavy` direct is impossible.
- D10 (2026-05-12): Step-up resolved -- transformers are bidirectional for tier-bridging (one `Setting` cap applies whichever way power flows; the cable on each port defines the tier pair, no fixed input/output direction). Implementation: Power Grid Plus re-implements `Transformer.GetGeneratedPower`/`GetUsedPower`/`ReceivePower` symmetrically, folding in Re-Volt's exploit-mitigation logic (so R-7 becomes "port-and-extend" rather than "port verbatim"). Reversed transformer variants are mounting-orientation only, not a direction flip.
- D11 (2026-05-12): Decision E resolved -- enforcement = build-time rejection (`Cable.CanConstruct` / `Device.CanConstruct` / `Cable.CanReplace` postfixes returning `CanConstructInfo.InvalidPlacement`) as the primary UX, plus a server-side reactive burn-on-join in a `Cable.OnRegistered` postfix and re-run at `OnFinishedLoad`. Burn-on-join algorithm: while a `CableNetwork` contains more than one `CableType`, pick a lowest-tier cable that is grid-adjacent to a higher-tier cable (the boundary cable) and `Break()` it; the re-flood splits the network; repeat until no mixed-tier network remains. So one rupture per touch point, never the interior cables; old saves with pre-existing illegal junctions get a one-time cascade of boundary-cable ruptures on first tick (Decision E option i). Fuses are never substituted (Decision #5): a tier mismatch is a topology violation, not over-current, so `Cable.Break()` is called directly on the boundary cable even on a cold/unpowered network. Edge cases: a single lower-tier cable bridging two higher-tier networks (`heavy-normal-heavy`) burns and splits all three; a 1-cable boundary "network" just disappears, the higher-tier side intact.
- D12 (2026-05-12): Decision #6 resolved -- NEW-2 multiplies only lever (1), the coil-crafting XML recipe (`GameData.*Recipes` -> `RecipeData.Recipe` for the super-heavy coil), leaving lever (2) (`BuildStates[0].Tool.EntryQuantity`, coils per placed segment) at vanilla. Decision #5: at a tier junction the boundary *cable* burns, never a fuse at the junction. Insulated cable variants count as their base tier for gating (insulation is orthogonal to voltage; the gate keys off `Cable.CableType`, which is only `normal`/`heavy`/`superHeavy`, so any cosmetic cable variant is automatically its base tier). NEW-1/NEW-2/NEW-3 each get their own config toggle. In-game tier names stay "Normal / Heavy / Super-Heavy cable"; the rejection message leans on the lore ("Cannot connect: different transmission voltage -- use a transformer").
- D13 (2026-05-12): All electrical generators are `heavy`-cable-only (user instruction), overriding the per-output tier-by-draw rule for generators specifically. That means Solar Panels (all 8 fixed prefabs, ~500 W) and the small Wind Turbine (~500 W / ~1 kW storm) also require `heavy` cable, alongside the Large Wind Turbine, Stirling Engine, Solid/Gas Fuel Generators, Turbine Generator, and RTG. Net topology: `normal` cable is for loads only (devices <= 5 kW); `heavy` cable carries all generation, all stationary batteries, and the >5 kW machines; `superHeavy` is the long-haul backbone; generation enters at `heavy` and is stepped down to `normal` via Small transformers. Eliminates the "small-generator-field-total-trap" on `normal` cable (a heavy-tier solar field is fine -- `heavy` is ~100 kW rated). The build-time `Device.CanConstruct` postfix rejects any *generator* adjacent to a `normal` (or `superHeavy`) cable. The mod README must list this clearly -- "all power generation must be wired with Heavy cable" -- since it is a notable departure from vanilla (solar panels on normal cable is the vanilla default). Also: the Turbine Generator (~90 W) and Solar Panels on `heavy` cable is cosmetically odd (tiny generator on a fat cable) but mechanically clean and the rule is simple. Open implementation detail (O-14): the "is this a generator?" discriminator -- `IPowerGenerator` only covers `SolarPanel` and `WindTurbineGenerator`/`LargeWindTurbineGenerator`; `GasFuelGenerator` / `SolidFuelGenerator` / `RadioscopicThermalGenerator` / `TurbineGenerator` / `StirlingEngine` do NOT implement it. So the gate needs a broader test (e.g. `Device.IsPowerProvider` and a positive `GetGeneratedPower`, minus the transformer/battery/transmitter cases) or an explicit generator-prefab whitelist. Decide during implementation; the whitelist is the safe choice and matches the README list.

---

## Appendix A: Re-Volt vs Power Grid Plus feature matrix

Legend:

| Icon | Re-Volt column | Icon | Power Grid Plus column |
|---|---|---|---|
| ✅ | present | ♻️ | inherited (ported from Re-Volt) |
| 🟡 | stub / partial only | 🔧 | inherited, required infrastructure ("glue") |
| ❌ | not present | 🆕 | new, original to this mod |
| | | 🚧 | planned, research-gated (go/no-go pending) |
| | | ❔ | undecided (port-time decision) |
| | | ❌ | dropped / not present |

Doc tags on the feature name: 📣 = appears on Re-Volt's Steam Workshop page; 🔬 = source-only (not described there).

| # | Feature | Re-Volt | Power Grid Plus | Notes |
|---|---|:---:|:---:|---|
| 1 | Vanilla power tick replaced (`PowerTick` -> custom) 📣 | ✅ | 🔧 ♻️ | glue R-1..R-4 |
| 2 | Proportional load sharing among all power sources 📣 | ✅ | ♻️ | |
| 3 | Proportional distribution to loads 📣 | ✅ | ♻️ | |
| 4 | Gradual, probabilistic cable burnout (not instant) 📣 | ✅ | ♻️ | heavy cables exempted by NEW-1 |
| 5 | Sliding-window (10-20s) overheat model 🔬 | ✅ | ♻️ | |
| 6 | Weakest fuse / weakest cable fails first 📣 | ✅ | ♻️ | |
| 7 | Fuses blow instantly and always pre-empt a cable burn 📣 | ✅ | ♻️ | |
| 8 | Breaker/cable-burn trip coordination (trip all interrupting breakers, power flows one tick) 🔬 | ✅ | ❌ | no breakers |
| 9 | Recursive/looped networks allowed; vanilla check optional 📣 | ✅ | ♻️ | config `Enable Recursive Network Limits` |
| 10 | NaN-power bugfix 🔬 | ✅ | ♻️ | |
| 11 | Power-Transmitter cable-less-network bugfix 🔬 | ✅ | ♻️ | |
| 12 | `Device.AssessPower` reimplemented for the new tick 🔬 | ✅ | 🔧 ♻️ | glue R-4; verify under O-10 |
| 13 | Provider array / `InputOutputDevices` resync (Network Analyzer stays correct) 🔬 | ✅ | ♻️ | |
| 14 | Brownouts | 🟡 | ❌ | stub only in Re-Volt; not ported |
| 15 | Device power-class classification (Lights/Doors/Atmos/Equipment/Logic/Power/Misc) 🔬 | ✅ | ❔ | kept as no-op-friendly stub only if the tick needs it; O-11 |
| 16 | Load Center structure (mass on/off per power class) 📣 | ✅ | ❌ | |
| 17 | Load Center logic interface (per-class On read/write, per-class wattage read) 📣 | ✅ | ❌ | |
| 18 | Load Center conflict handling (2+ on one network -> all disabled) 🔬 | ✅ | ❌ | |
| 19 | All power classes default ON without a Load Center 🔬 | ✅ | ❌ | n/a -- no power-class toggling at all |
| 20 | Small Circuit Breaker (resettable, screwdriver-set trip point, probabilistic trip) 📣 | ✅ | ❌ | |
| 21 | "Reversed" mounting variants of Small/Smart breakers 🔬 | ✅ | ❌ | |
| 22 | Smart Breaker (instant trip, built-in cable-analyzer data, logic-readable) 📣 | ✅ | ❌ | |
| 23 | Breaker UI: trip-point cycling, on/off/open/close, "Test (trip)" stub, passive tooltips 🔬 | ✅ | ❌ | |
| 24 | Breaker persistence + multiplayer sync (`CircuitBreakerSaveData`, `ByteArraySync`) 🔬 | ✅ | ❌ | |
| 25 | Heavy Breaker (switchgear pseudo-network, bolted door, 4 setting buttons; recipe disabled) 📣 (listed as "future") | ✅ shipped but uncraftable | ❌ | |
| 26 | Construction kits + electronics-printer recipes (Small/Smart/Load Center; Heavy commented out) 🔬 | ✅ | ❌ | |
| 27 | Custom-prefab loader + material remap to vanilla colours 🔬 | ✅ | ❌ | |
| 28 | New devices filed under "Cable" Stationpedia category 🔬 | ✅ | ❌ | no new devices; may still add Stationpedia notes to changed vanilla devices -- R-15 |
| 29 | Stationary battery charge/discharge-rate limiting 📣 | ✅ | ♻️ | config |
| 30 | Battery charge-efficiency option 🔬 | ✅ | ♻️ | config, default 1.0 |
| 31 | Battery logic additions (`ImportQuantity` / `ExportQuantity`) 🔬 | ✅ | ♻️ | config toggle |
| 32 | Transformer free-power exploit mitigation + quiescent draw restored 📣 | ✅ | ♻️ | config toggle |
| 33 | Transformer logic addition (`PowerActual`) 🔬 | ✅ | ♻️ | config toggle |
| 34 | APC power-discrepancy + quiescent-draw + slow-drain fix 📣 | ✅ | ♻️ | config toggle |
| 35 | `Thing.HasState` Button3 bugfix + Button4/5 state support 🔬 | ✅ | ❌ | only needed by dropped multi-button devices |
| 36 | Visual/component infrastructure (status screen, indicator changers, multi-state animator) 🔬 | ✅ | ❌ | |
| 37 | Config: `Cable burn factor` | ✅ | ♻️ | |
| 38 | Config: `Enable Recursive Network Limits` | ✅ | ♻️ | |
| 39 | Config: `Max Battery charge rate` / `Max Battery discharge rate` / `Battery Charge Efficiency` | ✅ | ♻️ | |
| 40 | Config: `Enable Battery Limits` / `Enable Battery Logic Additions` | ✅ | ♻️ | |
| 41 | Config: `Enable Transformer Exploit Mitigation` / `Enable Transformer Logic Additions` | ✅ | ♻️ | |
| 42 | Config: `Enable APC Power Fix` | ✅ | ♻️ | |
| 43 | Config: `Heavy Breaker Maximum Trip Setting` | ✅ | ❌ | no heavy breaker |
| 44 | Config: `Enable Custom Objects` (prefab content toggle) | ✅ | ❌ | no prefabs |
| 45 | Dependency: LibConstruct | ✅ Heavy Breaker switchgear network | ❌ | |
| 46 | Dependency: custom asset bundle / prefabs | ✅ | ❌ | |
| 47 | Custom save-data type registered | ✅ `CircuitBreakerSaveData` | ❌ | |
| 48 | `SetMultiplayerRequired()` | ✅ | ♻️ | |
| **NEW-1** | **Unlimited super-heavy cable (never burns)** -- `superHeavy` only; `normal` and `heavy` keep their ratings | ❌ | 🆕 | config `Enable Unlimited Super-Heavy Cables` (default on) |
| **NEW-2** | **Super-heavy cable costs more to build** | ❌ | 🆕 | config `Super-Heavy Cable Cost Multiplier`, default 2.0 (super-heavy coil costs 2x vanilla material) |
| **NEW-3** | **Three-tier voltage gating** -- `normal`/`heavy`/`superHeavy` cables mutually incompatible, bridged only by transformers; high-draw devices and the transformer-tier mapping are open design choices (Decisions C, D, E) | ❌ | 🚧 | spike done (section 4a); enforcement = build-time `Cable.CanConstruct` postfix + reactive burn-on-join; config `Enable Voltage Tiers` |

---

## Appendix B: the Stationeers power-mod landscape

What else on the Steam Workshop touches power, and how Power Grid Plus relates to each. Surveyed 2026-05-12 (game-side appid 544550) across ~30 keyword vectors. Excludes IC10 scripts / blueprints (the Workshop has dozens of power-themed scripts -- PIAC is the most polished, listed below for reference -- but they are code/blueprint UGC, not BepInEx/LaunchPad mods). "Subs" = current subscribers at survey time; rough popularity only.

Legend, "Relation to Power Grid Plus":
- 🟥 **Competes / overlaps** -- does roughly what Power Grid Plus does (a power-sim overhaul); a player picks one.
- 🟧 **Conflicts likely** -- patches the same vanilla classes (`PowerTick` / `Cable` / `Battery` / `Transformer` / `AreaPowerControl`); load-order or behavior clashes probable; Power Grid Plus must detect or document.
- 🟨 **Adjacent, mostly compatible** -- adds power *content* (generators / batteries / cables) or tweaks balance via XML/config; coexists, but Power Grid Plus's config may want to know about it (e.g. MoreCables' extra tiers).
- 🟩 **Orthogonal** -- different corner of the power space (wireless dishes, QoL paint, monitoring); no real interaction.

| Mod | ID | Author | Subs | Updated | LaunchPad? | Category | What it does | Relation to Power Grid Plus |
|---|---|---|---:|---|:---:|---|---|:---:|
| [**Re-Volt**](https://steamcommunity.com/sharedfiles/filedetails/?id=3587239682) | 3587239682 | Sukasa | ~1,510 | 2025-12-28 | yes | overhaul | The mod Power Grid Plus is derived from: rewritten power tick, proportional source/load sharing, probabilistic cable burnout, rate-limited batteries, circuit/smart/heavy breakers, Load Center, recursive networks allowed, transformer/APC fixes. | 🟥 Power Grid Plus is a trimmed-down derivative; you run one or the other. Power Grid Plus must detect Re-Volt for NEW-1 (Re-Volt swaps the `PowerTick` instance). |
| [**Deadly Electricity**](https://steamcommunity.com/sharedfiles/filedetails/?id=3575959825) | 3575959825 | Hisha | ~235 | 2025-10-29 | yes | overhaul / balance | Cable networks lose efficiency (-> heat) with size; cutting live wires sparks/injures/kills; excess generation strains/damages/ignites generators unless dumped to heaters. | 🟧 Different mechanic, but likely patches `PowerTick` / `CableNetwork` / generator classes; stacking with Power Grid Plus's rewritten tick is risky. Document as incompatible-ish. |
| **Better Power Mod** (ex-ActualSolarIrradiance) | 3234916147 | Vivien | popular | 2025-03-29 | yes | balance / generation | Fixes solar 500 W cap; buffs wind turbines, Stirling engines (20 kW), turbine generators (x10); raises battery chargers / power controllers / omni transmitters to 2,500 W; solar-position tooltip. | 🟨 Mostly generation-output buffs (likely value patches on those classes); coexists with Power Grid Plus. Combine for a "buffed + overhauled" run. |
| **PowerOverhaul** (gas-device power) | 3579306377 | Xara Loft | n/a | 2025-10-03 | yes | balance | Scales power draw of Pressure/Back-Pressure Regulator, Volume Pump, Gas Mixer (5-100%, configurable), adjusting runtime and logic-reported values. | 🟨 Edits per-device `UsedPower`; orthogonal to the tick rewrite. Coexists. Despite the name, narrow scope. |
| [**Custom Power DeepMiner**](https://steamcommunity.com/sharedfiles/filedetails/?id=3491001740) | 3491001740 | Thunder | ~25 | 2025-05-31 | yes | balance | Configurable Deep Miner power draw (1 W - 100 kW; default 5 kW vs. vanilla 500 W). | 🟨 Per-device draw tweak; coexists. |
| [**MorePowerMod**](https://steamcommunity.com/sharedfiles/filedetails/?id=3243132734) | 3243132734 | WIKUS | ~1,910 | 2025-10-28 | yes | generation / storage / wireless | Big Omni Transmitter (40 m, 5 kW, logic-controlled, wall/ceiling); Nuclear Station Battery (230.4 MW); Nuclear Wireless Battery Cell (2.304 MW). (Internal assembly name `OmniTransmitterLargeMod`.) | 🟨 New prefabs subclassing vanilla `Battery` / wireless classes; works with Power Grid Plus's tick (which is class-agnostic). The new batteries inherit Power Grid Plus's rate limits if `Enable Battery Limits` is on -- that may or may not be desired (config can't single them out). |
| [**MoreCables**](https://steamcommunity.com/sharedfiles/filedetails/?id=3555588082) | 3555588082 | spacebuilder2020 | ~150 | 2025-12-05 | yes | distribution | Adds two new cable types (super-heavy 500 kW, super-conductor 1 MW); lets all cable voltages be reconfigured. | 🟧 Directly relevant: Power Grid Plus's heavy-cable tier logic must know whether MoreCables' tiers count as "backbone". O-9. Coexistence needs an explicit decision (treat their tiers as backbone? as `normal`? configurable?). |
| [**CableTypeSwitcher**](https://steamcommunity.com/sharedfiles/filedetails/?id=3470776044) | 3470776044 | Cihla_cz | ~58 | 2025-04-26 | yes | distribution / qol | `CABLETYPESWITCH` console command: inspect a cable network, convert it between `normal` / `heavy` / `super-heavy`, split long super-heavy runs. | 🟧 Converts whole networks between tiers at runtime -- bypasses NEW-3's build-time gate. NEW-3's `OnFinishedLoad`/post-conversion reconciliation (O-8) would need to fire after this too, or document the interaction. |
| [**EL Switche**](https://steamcommunity.com/sharedfiles/filedetails/?id=3287183705) | 3287183705 | Kastuk | ~295 | 2025-10-17 | yes | distribution | Logic-controlled "switch" that spawns/removes cables (normal + heavy) in place to join or split power+data networks on a click. | 🟧 Programmatically creates cables -- could create heavy cables adjacent to devices, bypassing NEW-3's build-time gate. The `OnFinishedLoad`/runtime reconciliation needs to cover spawned cables too. |
| [**NetworkUpgrader**](https://steamcommunity.com/sharedfiles/filedetails/?id=3656955459) | 3656955459 | Jacksonthemaster | ~320 | 2026-01-31 | yes | qol / distribution | `upgrade pipes\|cables\|chutes\|all` console command: replaces chains of 3+ straight super-heavy cables/pipes/chutes with their long variants, preserving colors. | 🟩 Mesh/length consolidation only; doesn't change tier or topology. Orthogonal. |
| [**Network Painter**](https://steamcommunity.com/sharedfiles/filedetails/?id=2876605527) | 2876605527 | Elmo | ~5,880 | 2024-12-14 | yes | qol | Spray-painting a cable repaints the whole cable (pipe/chute) network; shift = single, ctrl = checkered. | 🟩 Pure cosmetic. Orthogonal. (Note: this repo's own SprayPaintPlus is in the same space.) |
| [**Battery Backup Light**](https://steamcommunity.com/sharedfiles/filedetails/?id=3569109044) | 3569109044 | alliephante | ~1,510 | 2026-02-23 | yes | distribution / qol | Wall Light Battery becomes a true emergency light: turns on when the grid loses power, runs off its cell; Mode 1 reverts to vanilla. | 🟩 Hooks one device's `OnPowerTick`; reads network power state. Coexists with Power Grid Plus (both read the same `CableNetwork.RequiredLoad`-style state). Low risk. |
| [**"Better" Battery Light**](https://steamcommunity.com/sharedfiles/filedetails/?id=3491770665) | 3491770665 | TrippleTrip | ~23 | 2025-06-01 | yes | qol / logic | Battery wall light turns yellow below 99.5% cell (unlocked) or on when grid wattage < 100 W (locked). | 🟩 Same niche as above; orthogonal. |
| [**Omni Transmitter Settings**](https://steamcommunity.com/sharedfiles/filedetails/?id=3643534844) | 3643534844 | RogerWaters | ~155 | 2026-01-10 | yes | wireless / balance | Reconfigures the vanilla Omni Transmitter: max range, distance falloff, min-power-per-battery floor; MP/dedicated aware. | 🟩 Wireless-power tuning; orthogonal to wired-power overhaul. |
| [**Better Transmitter**](https://steamcommunity.com/sharedfiles/filedetails/?id=3582023521) | 3582023521 | Viroman | ~17 | 2025-10-06 | yes | wireless / balance | Single setting to change power transmitter range (up to 10k); vanilla loss still applies. | 🟩 Orthogonal. |
| [**Power Transmitter Plus**](https://steamcommunity.com/sharedfiles/filedetails/?id=3707677512) | 3707677512 | SixFive7 | new | 2026-05-06 | yes | wireless / logic | (This repo's mod.) Visible beam + scrolling pulses on the Microwave Power Transmitter, distance-cost source-draw model, 5 logic readouts + auto-aim logic type, wall/ceiling placement. | 🟩 Sibling mod; different device. Should be combinable with Power Grid Plus -- worth a smoke test. |
| [**3.6 Megawatt Battery**](https://steamcommunity.com/sharedfiles/filedetails/?id=3004087671) | 3004087671 | walkin_here | ~265 | 2023-07-29 | XML | storage | Nuclear Battery capacity -> 3.6 MWh; charging consumes a whole Station Battery. | 🟨 XML stat edit; coexists. |
| [**EGC - End Game Content**](https://steamcommunity.com/sharedfiles/filedetails/?id=3007388410) | 3007388410 | walkin_here | ~306 | 2023-07-29 | XML | storage / wireless | XL Wireless Battery recipe + capacity -> 1170 kWh; infinite-filter recipes. | 🟨 XML; coexists. |
| [**Super XL Wireless Battery (EGC Edit)**](https://steamcommunity.com/sharedfiles/filedetails/?id=3706534108) | 3706534108 | Nitz | ~14 | 2026-04-14 | yes | storage / wireless | XL Wireless Battery -> 90 MWh; expensive recipe. | 🟨 Coexists. |
| [**BuffWirelessBatteries**](https://steamcommunity.com/sharedfiles/filedetails/?id=3355912171) | 3355912171 | Grilled Salmon | ~118 | 2024-10-27 | XML | storage / wireless | Wireless cells: small 12 kJ -> 27 kJ, large 72 kJ -> 216 kJ. | 🟨 XML stat edit; coexists. |
| [**Mod: Jigawatt Battery**](https://steamcommunity.com/sharedfiles/filedetails/?id=1785747072) | 1785747072 | Kastuk | ~360 | 2019-09-24 | XML | storage | Adds a 1.21 GW creative-scale battery; needs Processed Uranium. | 🟨 Coexists; needs Power Grid Plus's `Enable Battery Limits` consideration (a 1.21 GW battery rate-limited at 0.7%/tick is still ~8.5 MW/tick discharge -- probably fine, but note it). |
| [**RRI - Boost Da Powa [Battery Upgrades]**](https://steamcommunity.com/sharedfiles/filedetails/?id=2359999429) | 2359999429 | ZimmiMane | ~84 | 2021-01-13 | XML | storage | Upgraded battery cells (200 kJ / 600 kJ / 4 MJ) crafted from copper/gold/silver. | 🟨 Coexists. |
| [**CraftableRTG**](https://steamcommunity.com/sharedfiles/filedetails/?id=3026880031) | 3026880031 | S.W.A.TGamer | ~404 | 2024-07-11 | XML | generation | Survival craft recipe for the RTG. | 🟨 Recipe add; coexists. |
| [**Survival RTG**](https://steamcommunity.com/sharedfiles/filedetails/?id=2464235697) | 2464235697 | Aaron Tigan | ~353 | 2021-04-29 | XML | generation | RTG craftable in Electronics Printer Mk2. | 🟨 Coexists. |
| [**RTG Start Conditions and Difficulty Mod**](https://steamcommunity.com/sharedfiles/filedetails/?id=3568945039) | 3568945039 | Zky | ~138 | 2025-10-16 | XML | generation / balance | "Standard RTG Start+" (12 RTGs) + relaxed difficulties. | 🟩 Worldgen/start; orthogonal. |
| [**Nuclear Recipes**](https://steamcommunity.com/sharedfiles/filedetails/?id=3004097828) | 3004097828 | walkin_here | ~317 | 2023-07-29 | XML | generation / balance | Reworks Nuclear Battery + RTG recipes to use Processed Uranium; expensive/slow. | 🟨 Recipe rework; coexists. |
| [**[OBSOLETE] Recipe - RTG**](https://steamcommunity.com/sharedfiles/filedetails/?id=1351511005) | 1351511005 | BoNes | ~940 | 2018-04-03 | XML | generation | First-gen RTG recipe mod. Abandoned. | 🟨 Obsolete; ignore. |
| [**2x Solar Power**](https://steamcommunity.com/sharedfiles/filedetails/?id=1669621266) | 1669621266 | MutatedPixel | ~400 | 2019-03-02 | XML | balance / generation | Doubles solar output (Moon/Mars/space/Europa). | 🟨 Solar-constant edit; coexists. |
| [**Realistic Solar Constants**](https://steamcommunity.com/sharedfiles/filedetails/?id=1475742702) | 1475742702 | FreezePop | ~23 | 2018-08-12 | XML | balance | Solar irradiance set to "realistic" levels (Mars 43%, Europa 3.7%). | 🟨 Solar-constant edit; coexists (opposite direction to the above). |
| [**ArcFurnace Overhaul**](https://steamcommunity.com/sharedfiles/filedetails/?id=2564615804) | 2564615804 | SCR00G3 | ~95 | 2021-08-03 | XML | balance | Arc Furnace smelts all ingots in one tick at 10 W/unit. | 🟩 Furnace power-cost tweak; orthogonal. |
| [**Power2Resources**](https://steamcommunity.com/sharedfiles/filedetails/?id=1328890332) | 1328890332 | Dark Phoenix | ~47 | 2020-02-08 | XML | balance | Tool Manufactory makes ingots/coal at the cost of power. | 🟩 Orthogonal. |
| [**Power Trading**](https://steamcommunity.com/sharedfiles/filedetails/?id=3420179579) | 3420179579 | Wilhelm W. Walrus | ~37 | 2025-02-03 | XML | misc | Trader buys uranium / charcoal / solid fuel. | 🟩 Economy; orthogonal. |
| [**Bins Power Rebalance**](https://steamcommunity.com/sharedfiles/filedetails/?id=2392722433) | 2392722433 | Binaryclock03 | ~11 | 2021-02-12 | (old loader) | balance | Console power use 50 W -> 1 W; flagged "maybe doesn't work". | 🟨 Per-device tweak; likely broken on current game. |
| [**More Stirling Logic**](https://steamcommunity.com/sharedfiles/filedetails/?id=2883817490) | 2883817490 | silentdeth | ~13 | 2022-11-05 | (old loader) | logic | Adds atmospheric logic reads on the Stirling engine. | 🟩 Logic-surface add; orthogonal. |
| [**Super Structures Mod**](https://steamcommunity.com/sharedfiles/filedetails/?id=3159841144) | 3159841144 | Nitz | ~32 | 2024-02-13 | XML | generation (enabler) | Raises pressure/temp limits on Turbine Generator + reinforced windows / iron window / active vents. | 🟨 Lets turbine generators run harder; orthogonal to the tick. |
| [**PIAC - Power Info and Control**](https://steamcommunity.com/sharedfiles/filedetails/?id=3687433072) | 3687433072 | 3xp | ~466 | 2026-04-05 | (IC10 script, no code) | logic | IC10 power dashboard: generation/usage stats, solar tracking, generator fallback/auto mode. | 🟩 Script package; orthogonal. (The Workshop also has many smaller power IC10 scripts: Power manager 2256532974, Power Displays 2843854351, Total-Power-Control 3630943416, etc. -- not enumerated here.) |
| [**Wind Vs Gravity**](https://steamcommunity.com/sharedfiles/filedetails/?id=2936575063) | 2936575063 | Thunder | ~436 | 2026-01-14 | yes | adjacent | Wind/pressure forces scale with planetary gravity (affects wind turbines indirectly via storms). | 🟩 Atmospherics, not power. Orthogonal. |
| [**LibConstruct**](https://steamcommunity.com/sharedfiles/filedetails/?id=3505115682) | 3505115682 | tom_is_unlucky | n/a | 2026-03-15 | yes | framework | Custom-placement library (Modular Consoles etc.). Not power; Re-Volt's Workshop page does NOT actually declare a LibConstruct dependency (only `StationeersLaunchPad`). | 🟩 Not relevant -- Power Grid Plus drops the breakers, so no LibConstruct. |

Takeaways for Power Grid Plus:
- Only **Re-Volt** and **Deadly Electricity** truly compete (both rewrite the tick / cable behavior). Power Grid Plus positions itself as "Re-Volt's simulation half, no new content, plus a heavy-cable backbone tier".
- The mods Power Grid Plus must explicitly account for: **Re-Volt** (PowerTick swap -- detect for NEW-1), **MoreCables** (extra cable tiers -- O-9), **CableTypeSwitcher** / **EL Switche** (programmatic cable creation/conversion -- bypass NEW-3's build-time gate; reconciliation must cover them -- O-8/O-9), and **MorePowerMod** / big-battery mods (inherit Power Grid Plus's battery rate limits if enabled -- document, can't single them out).
- Everything else is orthogonal or pure XML-stat tweaks that coexist fine.

---

## Appendix C: power-mod capability matrix (every power mod vs what it changes)

The fine-grained 48-feature breakdown lives in Appendix A (Re-Volt vs Power Grid Plus, the only two mods with that depth of overhaul). This is the wide view: every Stationeers power mod from the survey against the capability buckets that distinguish them. A literal 48-rows-by-37-mods matrix would be ~1800 mostly-empty cells; the buckets below collapse the overhaul rows (where only Re-Volt / Power Grid Plus / Deadly Electricity have anything) and expand the rows where the content mods differ. `+` = does it; `.` = doesn't; `(note)` = qualified.

Column legend:

| Col | Capability |
|---|---|
| A | Replaces the power tick / changes cable-burnout behaviour (instant -> gradual, efficiency loss, etc.) |
| B | Stationary-battery changes (charge/discharge-rate limits, charge efficiency, battery logic values) |
| C | Transformer / APC fixes (free-power exploit, quiescent draw, slow drain) |
| D | Recursive-network handling / Load Center / circuit breakers (overhaul-only structural features) |
| E | Buffs power-generation output (solar / wind / Stirling / turbine generators, or enables them to run harder) |
| F | Tweaks per-device power draw (regulators, pumps, console, deep miner, etc.) |
| G | Adjusts solar-irradiance constants |
| H | Rebalances electrical crafting recipes (RTG / nuclear battery / arc furnace / power-to-resources) |
| I | Adds a new wired generation/storage device (new battery, capacitor, etc.) |
| J | Buffs vanilla battery / cell capacity stats |
| K | New cable tier / reconfigures cable voltage ratings / converts a network's tier at runtime / logic-spawns cables |
| L | Adds a new wireless transmitter/receiver/battery device, or tunes wireless range/falloff |
| M | Power logic / monitoring additions, or power-management QoL (network paint, bulk cable upgrade, emergency lights, IC10 dashboards) |
| N | New Power-Grid-Plus-only feature (unlimited super-heavy cable / super-heavy cost / three-tier voltage gating) |

| Mod | A | B | C | D | E | F | G | H | I | J | K | L | M | N |
|---|:-:|:-:|:-:|:-:|:-:|:-:|:-:|:-:|:-:|:-:|:-:|:-:|:-:|:-:|
| [Re-Volt](https://steamcommunity.com/sharedfiles/filedetails/?id=3587239682) | + | + | + | + | . | . | . | . | . | . | . | . | + | . |
| **Power Grid Plus** (this plan) | + | + | + | (recursive only) | . | . | . | . | . | . | + | . | . | + |
| [Deadly Electricity](https://steamcommunity.com/sharedfiles/filedetails/?id=3575959825) | + | . | . | . | (surplus damages generators) | . | . | . | . | . | . | . | . | . |
| [Better Power Mod](https://steamcommunity.com/sharedfiles/filedetails/?id=3234916147) | . | . | . | . | + | (chargers/controllers/omni to 2.5 kW) | . | . | . | . | . | . | (solar tooltip) | . |
| [PowerOverhaul (gas devices)](https://steamcommunity.com/sharedfiles/filedetails/?id=3579306377) | . | . | . | . | . | + | . | . | . | . | . | . | . | . |
| [Custom Power DeepMiner](https://steamcommunity.com/sharedfiles/filedetails/?id=3491001740) | . | . | . | . | . | + | . | . | . | . | . | . | . | . |
| [Bins Power Rebalance](https://steamcommunity.com/sharedfiles/filedetails/?id=2392722433) | . | . | . | . | . | + (console 1 W) | . | . | . | . | . | . | . | . |
| [ArcFurnace Overhaul](https://steamcommunity.com/sharedfiles/filedetails/?id=2564615804) | . | . | . | . | . | + (arc furnace) | . | + | . | . | . | . | . | . |
| [Power2Resources](https://steamcommunity.com/sharedfiles/filedetails/?id=1328890332) | . | . | . | . | . | . | . | + (power -> ingots) | . | . | . | . | . | . |
| [Super Structures Mod](https://steamcommunity.com/sharedfiles/filedetails/?id=3159841144) | . | . | . | . | + (turbine gen limits) | . | . | . | . | . | . | . | . | . |
| [MorePowerMod](https://steamcommunity.com/sharedfiles/filedetails/?id=3243132734) | . | . | . | . | . | . | . | . | + (nuclear batteries) | . | . | + (big omni transmitter + nuclear wireless cell) | . | . |
| [MoreCables](https://steamcommunity.com/sharedfiles/filedetails/?id=3555588082) | . | . | . | . | . | . | . | . | . | . | + (super-heavy + super-conductor tiers; voltage reconfig) | . | . | . |
| [CableTypeSwitcher](https://steamcommunity.com/sharedfiles/filedetails/?id=3470776044) | . | . | . | . | . | . | . | . | . | . | + (convert network tier at runtime) | . | + (QoL) | . |
| [EL Switche](https://steamcommunity.com/sharedfiles/filedetails/?id=3287183705) | . | . | . | . | . | . | . | . | . | . | + (logic-spawns/removes cables) | . | + (logic device) | . |
| [NetworkUpgrader](https://steamcommunity.com/sharedfiles/filedetails/?id=3656955459) | . | . | . | . | . | . | . | . | . | . | (consolidates straight runs) | . | + (QoL command) | . |
| [Network Painter](https://steamcommunity.com/sharedfiles/filedetails/?id=2876605527) | . | . | . | . | . | . | . | . | . | . | . | . | + (network-wide cable paint) | . |
| [Battery Backup Light](https://steamcommunity.com/sharedfiles/filedetails/?id=3569109044) | . | . | . | . | . | . | . | . | . | . | . | . | + (emergency-light behaviour) | . |
| ["Better" Battery Light](https://steamcommunity.com/sharedfiles/filedetails/?id=3491770665) | . | . | . | . | . | . | . | . | . | . | . | . | + (battery-light indicator) | . |
| [More Stirling Logic](https://steamcommunity.com/sharedfiles/filedetails/?id=2883817490) | . | . | . | . | . | . | . | . | . | . | . | . | + (Stirling logic reads) | . |
| [Omni Transmitter Settings](https://steamcommunity.com/sharedfiles/filedetails/?id=3643534844) | . | . | . | . | . | . | . | . | . | . | . | + (omni transmitter range/falloff) | . | . |
| [Better Transmitter](https://steamcommunity.com/sharedfiles/filedetails/?id=3582023521) | . | . | . | . | . | . | . | . | . | . | . | + (transmitter range) | . | . |
| [Power Transmitter Plus](https://steamcommunity.com/sharedfiles/filedetails/?id=3707677512) (SixFive7) | . | . | . | . | . | . | . | . | . | . | . | + (microwave transmitter beam / distance-cost model / wall+ceiling) | + (5 logic readouts + auto-aim) | . |
| [3.6 Megawatt Battery](https://steamcommunity.com/sharedfiles/filedetails/?id=3004087671) | . | . | . | . | . | . | . | . | . | + (nuclear battery 3.6 MWh) | . | . | . | . |
| [EGC - End Game Content](https://steamcommunity.com/sharedfiles/filedetails/?id=3007388410) | . | . | . | . | . | . | . | + (XL wireless battery recipe) | . | + (XL wireless battery 1170 kWh) | . | . | . | . |
| [Super XL Wireless Battery (EGC Edit)](https://steamcommunity.com/sharedfiles/filedetails/?id=3706534108) | . | . | . | . | . | . | . | . | . | + (XL wireless 90 MWh) | . | . | . | . |
| [BuffWirelessBatteries](https://steamcommunity.com/sharedfiles/filedetails/?id=3355912171) | . | . | . | . | . | . | . | . | . | + (wireless cells 2.25-3x) | . | . | . | . |
| [Mod: Jigawatt Battery](https://steamcommunity.com/sharedfiles/filedetails/?id=1785747072) | . | . | . | . | . | . | . | + (recipe) | + (1.21 GW battery) | . | . | . | . | . |
| [RRI - Boost Da Powa](https://steamcommunity.com/sharedfiles/filedetails/?id=2359999429) | . | . | . | . | . | . | . | + (recipes) | + (upgraded battery cells) | . | . | . | . | . |
| [CraftableRTG](https://steamcommunity.com/sharedfiles/filedetails/?id=3026880031) | . | . | . | . | . | . | . | + (RTG recipe) | . | . | . | . | . | . |
| [Survival RTG](https://steamcommunity.com/sharedfiles/filedetails/?id=2464235697) | . | . | . | . | . | . | . | + (RTG recipe) | . | . | . | . | . | . |
| [Recipe - RTG (obsolete)](https://steamcommunity.com/sharedfiles/filedetails/?id=1351511005) | . | . | . | . | . | . | . | + (RTG recipe; abandoned) | . | . | . | . | . | . |
| [RTG Start Conditions and Difficulty Mod](https://steamcommunity.com/sharedfiles/filedetails/?id=3568945039) | . | . | . | . | . | . | . | . | . | . | . | . | (start config: 12 RTGs) | . |
| [Nuclear Recipes](https://steamcommunity.com/sharedfiles/filedetails/?id=3004097828) | . | . | . | . | . | . | . | + (nuclear battery + RTG recipes) | . | . | . | . | . | . |
| [2x Solar Power](https://steamcommunity.com/sharedfiles/filedetails/?id=1669621266) | . | . | . | . | . | . | + (2x) | . | . | . | . | . | . | . |
| [Realistic Solar Constants](https://steamcommunity.com/sharedfiles/filedetails/?id=1475742702) | . | . | . | . | . | . | + (realistic, weaker) | . | . | . | . | . | . | . |
| [PIAC - Power Info and Control](https://steamcommunity.com/sharedfiles/filedetails/?id=3687433072) | . | . | . | . | . | . | . | . | . | . | . | . | + (IC10 power dashboard) | . |
| [Power Trading](https://steamcommunity.com/sharedfiles/filedetails/?id=3420179579) | . | . | . | . | . | . | . | . | . | . | . | . | (trader buys fuel) | . |

Reading guide:
- Columns A-D are the "power-system overhaul" space. Only Re-Volt populates all four; Power Grid Plus populates A/B/C and the recursive-network bit of D (no Load Center, no breakers); Deadly Electricity populates A. Nothing else touches this space, which is exactly why Re-Volt and Deadly Electricity are the only true competitors.
- Columns E-H are "balance / per-device tuning", mostly XML or value-patch mods that coexist with everything.
- Columns I-L are "new power content". The ones that matter for Power Grid Plus: MoreCables (col K, adds super-heavy / super-conductor tiers the three-tier gating must classify), CableTypeSwitcher and EL Switche (col K, create/convert cables at runtime, bypassing the build-time gate).
- Column M is logic / monitoring / QoL, all orthogonal.
- Column N is the three Power-Grid-Plus-only features.
- For the per-feature detail of the A-D space (the 48 Re-Volt features), see Appendix A. For each mod's full feature bullets and the conflict / compat assessment, see Appendix B.

---

## Appendix D: complete feature inventory across all power mods

Every feature from every Stationeers power mod in the survey, one row each: which mod (linked), the feature, what it does, whether it is in Power Grid Plus's plan, and whether it would be a good fit. Supersedes the condensed views in Appendices A (Re-Volt vs Power Grid Plus, 48 features) and C (mods x capability buckets), which remain for quick reference. "In plan?" = Yes (ported) / Yes (new) / Yes (glue) / No / No (dropped). "Good fit?" judges whether the feature *should* be in Power Grid Plus, given the pure-patch / no-new-content / "Re-Volt's simulation half plus a voltage-tier backbone" scope.

| Mod | Feature | What it does | In plan? | Good fit? |
|---|---|---|---|---|
| [Re-Volt](https://steamcommunity.com/sharedfiles/filedetails/?id=3587239682) | Power tick replaced | Replaces vanilla `PowerTick` with a custom per-network tick | Yes (ported, glue) | Core -- it's the foundation everything else builds on |
| [Re-Volt](https://steamcommunity.com/sharedfiles/filedetails/?id=3587239682) | Proportional load sharing among sources | Load split evenly across every generator/battery on a network (no single one drained first) | Yes (ported) | Core |
| [Re-Volt](https://steamcommunity.com/sharedfiles/filedetails/?id=3587239682) | Proportional distribution to loads | Available power split proportionally across consumers | Yes (ported) | Core |
| [Re-Volt](https://steamcommunity.com/sharedfiles/filedetails/?id=3587239682) | Gradual probabilistic cable burnout | Wires don't burn instantly at +1 W; burn chance scales with overload magnitude | Yes (ported; `superHeavy` exempt via NEW-1) | Core |
| [Re-Volt](https://steamcommunity.com/sharedfiles/filedetails/?id=3587239682) | Sliding-window overheat model | Cable-burn uses a 10-20 s rolling average, so short surges never burn | Yes (ported) | Core |
| [Re-Volt](https://steamcommunity.com/sharedfiles/filedetails/?id=3587239682) | Weakest fuse / cable fails first | Sorted lists; the lowest-rated fuse/cable blows first | Yes (ported) | Core |
| [Re-Volt](https://steamcommunity.com/sharedfiles/filedetails/?id=3587239682) | Fuses blow instantly + pre-empt cable burn | A fuse rated low enough always blows before a cable, no delay | Yes (ported) | Core |
| [Re-Volt](https://steamcommunity.com/sharedfiles/filedetails/?id=3587239682) | Breaker / cable-burn trip coordination | If breakers can collectively interrupt a burn, trip them all and let power flow one tick | No (dropped -- no breakers) | No -- tied to circuit breakers, which are new content out of scope (revisit if a content phase ever adds breakers) |
| [Re-Volt](https://steamcommunity.com/sharedfiles/filedetails/?id=3587239682) | Recursive / looped networks allowed | The vanilla check that force-burns cables in transformer/battery loops is off by default | Yes (ported; config toggle) | Core (and the loop limit conflicts with our recursive-friendly tick anyway) |
| [Re-Volt](https://steamcommunity.com/sharedfiles/filedetails/?id=3587239682) | NaN-power bugfix | Devices reporting `NaN` watts coerced to 0 so the sim doesn't break | Yes (ported) | Yes -- free bugfix |
| [Re-Volt](https://steamcommunity.com/sharedfiles/filedetails/?id=3587239682) | Power-Transmitter cable-less-network bugfix | A `CableNetwork` with zero cables doesn't error | Yes (ported) | Yes -- free bugfix |
| [Re-Volt](https://steamcommunity.com/sharedfiles/filedetails/?id=3587239682) | `Device.AssessPower` reimplemented | Per-device "am I powered?" routes through the new tick instead of vanilla bookkeeping | Yes (ported, glue) | Required -- the tick mis-behaves without it |
| [Re-Volt](https://steamcommunity.com/sharedfiles/filedetails/?id=3587239682) | Provider array / `InputOutputDevices` resync | Keeps the Network Analyzer cartridge reporting providers correctly | Yes (ported) | Yes -- keeps a vanilla UI working |
| [Re-Volt](https://steamcommunity.com/sharedfiles/filedetails/?id=3587239682) | Brownouts | (stub only in Re-Volt; never implemented) | No | Maybe -- a future idea; Re-Volt never built it either |
| [Re-Volt](https://steamcommunity.com/sharedfiles/filedetails/?id=3587239682) | Device power-class classification | Buckets devices into Lights/Doors/Atmos/Equipment/Logic/Power/Misc for the Load Center | No (kept only as a no-op stub if the tick needs it) | No -- only meaningful with a Load Center, which is out of scope |
| [Re-Volt](https://steamcommunity.com/sharedfiles/filedetails/?id=3587239682) | Load Center structure | A wall device that toggles power to whole device classes en masse without a separate network | No | No -- new content / asset bundle; out of scope for a pure-patch mod; possible future content phase |
| [Re-Volt](https://steamcommunity.com/sharedfiles/filedetails/?id=3587239682) | Load Center logic interface | Per-class On read/write and per-class wattage read over the data network | No | No -- depends on the Load Center |
| [Re-Volt](https://steamcommunity.com/sharedfiles/filedetails/?id=3587239682) | Load Center conflict handling | 2+ Load Centers on one network -> all disabled | No | No -- depends on the Load Center |
| [Re-Volt](https://steamcommunity.com/sharedfiles/filedetails/?id=3587239682) | All power classes default ON without a Load Center | No (n/a) | No (n/a) | n/a |
| [Re-Volt](https://steamcommunity.com/sharedfiles/filedetails/?id=3587239682) | Small Circuit Breaker | Resettable over-current trip with a screwdriver-set trip point | No | No -- new content; out of scope; possible future content phase |
| [Re-Volt](https://steamcommunity.com/sharedfiles/filedetails/?id=3587239682) | "Reversed" mounting variants of Small/Smart breakers | Opposite-wall mounting prefabs | No | No -- new content |
| [Re-Volt](https://steamcommunity.com/sharedfiles/filedetails/?id=3587239682) | Smart Breaker | Instant trip + built-in cable-analyzer data; logic-readable | No | No -- new content |
| [Re-Volt](https://steamcommunity.com/sharedfiles/filedetails/?id=3587239682) | Breaker UI | Trip-point cycling, on/off/open/close, "Test (trip)" stub, passive tooltips | No | No -- depends on the breakers |
| [Re-Volt](https://steamcommunity.com/sharedfiles/filedetails/?id=3587239682) | Breaker persistence + MP sync (`CircuitBreakerSaveData`) | Saves trip point / mode / connections; `ByteArraySync` | No | No -- depends on the breakers |
| [Re-Volt](https://steamcommunity.com/sharedfiles/filedetails/?id=3587239682) | Heavy Breaker (switchgear pseudo-network, bolted door, 4 setting buttons) | A high-interrupt breaker with a LibConstruct "switchgear" network and a bolted access door (shipped uncraftable) | No | No -- new content + LibConstruct dependency; out of scope |
| [Re-Volt](https://steamcommunity.com/sharedfiles/filedetails/?id=3587239682) | Construction kits + electronics-printer recipes | Item kits + recipes for the Small/Smart breakers and Load Center | No | No -- depends on the new content |
| [Re-Volt](https://steamcommunity.com/sharedfiles/filedetails/?id=3587239682) | Custom-prefab loader + material remap to vanilla colours | Loads the mod's prefabs, remaps their materials to vanilla colours, makes them paintable | No | No -- only needed if we ship prefabs, which we don't |
| [Re-Volt](https://steamcommunity.com/sharedfiles/filedetails/?id=3587239682) | New devices under "Cable" Stationpedia category | The new devices file under the in-game "Cable" category | No | No -- no new devices |
| [Re-Volt](https://steamcommunity.com/sharedfiles/filedetails/?id=3587239682) | Stationary battery charge/discharge-rate limiting | Caps charge at ~0.2 %/tick and discharge at ~0.7 %/tick of the battery's capacity | Yes (ported; config) | Core |
| [Re-Volt](https://steamcommunity.com/sharedfiles/filedetails/?id=3587239682) | Battery charge-efficiency option | Optional energy loss when charging a battery | Yes (ported; config, default 1.0) | Yes -- cheap config option |
| [Re-Volt](https://steamcommunity.com/sharedfiles/filedetails/?id=3587239682) | Battery logic additions (`ImportQuantity` / `ExportQuantity`) | Logic-readable max charge / discharge rates on stationary batteries | Yes (ported; config toggle) | Yes -- small, fits the pattern |
| [Re-Volt](https://steamcommunity.com/sharedfiles/filedetails/?id=3587239682) | Transformer free-power exploit fix + quiescent draw restored | Output clamped to `min(Setting, upstream)`; the transformer charges its own draw upstream | Yes (ported -- and folded into our bidirectional transformer reimplementation) | Core -- bugfix and load-bearing for the tier design |
| [Re-Volt](https://steamcommunity.com/sharedfiles/filedetails/?id=3587239682) | Transformer logic addition (`PowerActual`) | Logic-readable current throughput on transformers | Yes (ported; config toggle) | Yes |
| [Re-Volt](https://steamcommunity.com/sharedfiles/filedetails/?id=3587239682) | APC power-gap + quiescent + slow-drain fix | Closes a ~10 W free-power gap; APC stops bleeding its battery when no output network is connected; charges its draw upstream | Yes (ported; config toggle) | Yes -- bugfix |
| [Re-Volt](https://steamcommunity.com/sharedfiles/filedetails/?id=3587239682) | `Thing.HasState` Button3 bugfix + Button4/5 state support | Fixes a vanilla animator-state bug and adds two animator states | No (only needed by the dropped multi-button devices) | No -- only useful if we add multi-button content |
| [Re-Volt](https://steamcommunity.com/sharedfiles/filedetails/?id=3587239682) | Visual / component infrastructure | Status screen, indicator material-changers, multi-state animator components | No | No -- only used by the dropped devices |
| [Re-Volt](https://steamcommunity.com/sharedfiles/filedetails/?id=3587239682) | Config: Cable burn factor | Scales cable-burn chance; 0 disables cable burn entirely | Yes (ported) | Yes |
| [Re-Volt](https://steamcommunity.com/sharedfiles/filedetails/?id=3587239682) | Config: Enable Recursive Network Limits | Re-enable the vanilla "loop through transformers/batteries -> burn" check | Yes (ported) | Yes |
| [Re-Volt](https://steamcommunity.com/sharedfiles/filedetails/?id=3587239682) | Config: Max Battery charge/discharge rate, Battery Charge Efficiency | The numeric knobs for the battery limits | Yes (ported) | Yes |
| [Re-Volt](https://steamcommunity.com/sharedfiles/filedetails/?id=3587239682) | Config: Enable Battery Limits / Enable Battery Logic Additions | Toggles for the two battery patches | Yes (ported) | Yes |
| [Re-Volt](https://steamcommunity.com/sharedfiles/filedetails/?id=3587239682) | Config: Enable Transformer Exploit Mitigation / Logic Additions | Toggles for the two transformer patches | Yes (ported) | Yes |
| [Re-Volt](https://steamcommunity.com/sharedfiles/filedetails/?id=3587239682) | Config: Enable APC Power Fix | Toggle for the APC patch | Yes (ported) | Yes |
| [Re-Volt](https://steamcommunity.com/sharedfiles/filedetails/?id=3587239682) | Config: Heavy Breaker Maximum Trip Setting | Cap for the Heavy Breaker's configurable trip current | No | No -- depends on the heavy breaker |
| [Re-Volt](https://steamcommunity.com/sharedfiles/filedetails/?id=3587239682) | Config: Enable Custom Objects (prefab content toggle) | Turns the breaker/Load-Center prefabs on/off | No | No -- no prefab content |
| [Re-Volt](https://steamcommunity.com/sharedfiles/filedetails/?id=3587239682) | Dependency: LibConstruct | The custom-placement library the Heavy Breaker's switchgear network used | No | No -- only the heavy breaker needed it |
| [Re-Volt](https://steamcommunity.com/sharedfiles/filedetails/?id=3587239682) | Dependency: custom asset bundle / prefabs | The mod's StationeersMods asset bundle | No | No -- nothing to load |
| [Re-Volt](https://steamcommunity.com/sharedfiles/filedetails/?id=3587239682) | Custom save-data type registered (`CircuitBreakerSaveData`) | Adds a per-thing save struct for breakers | No | No -- no per-thing save state to add |
| [Re-Volt](https://steamcommunity.com/sharedfiles/filedetails/?id=3587239682) | `SetMultiplayerRequired()` | Host and every client must have the mod | Yes (ported) | Required -- a power-sim rewrite must be |
| Power Grid Plus (this plan) | Unlimited super-heavy cable (NEW-1) | `superHeavy` cable never ruptures, regardless of throughput | Yes (new; config toggle) | Yes -- the headline feature |
| Power Grid Plus (this plan) | Super-heavy cable cost x2 (NEW-2) | Doubles the super-heavy coil's crafting recipe cost | Yes (new; config multiplier) | Yes |
| Power Grid Plus (this plan) | Three-tier voltage gating (NEW-3) | `normal`/`heavy`/`superHeavy` cables mutually incompatible, bridged only by transformers; build-time rejection + reactive boundary-cable burn | Yes (new; config toggle) | Yes -- the design's centrepiece |
| Power Grid Plus (this plan) | Tier-by-draw placement + generators-on-heavy | Devices sit on the lowest tier rated for their draw; all generators and stationary batteries require `heavy` cable | Yes (new) | Yes -- the rule that makes the tiers mean something |
| Power Grid Plus (this plan) | Bidirectional transformers | Power flows either way through a transformer, capped by `Setting`; the cable on each port defines the tier pair, not a direction | Yes (new -- reimplements the inherited transformer fix symmetrically) | Yes -- required so generation (which lives at `heavy`+) can step down to `normal` and up to `superHeavy` |
| Power Grid Plus (this plan) | Per-tier transformer mapping | Small = `heavy<->normal`; Medium = `heavy<->heavy` (same-tier limiter only); Large = `superHeavy<->heavy` | Yes (new) | Yes |
| [Deadly Electricity](https://steamcommunity.com/sharedfiles/filedetails/?id=3575959825) | Cable efficiency loss -> heat with network size | Bigger cable networks lose energy, dumped as heat into the cables | No | No -- an alternative overhaul; conflicts with our (Re-Volt-derived) tick. Interesting flavour, not adoptable as-is |
| [Deadly Electricity](https://steamcommunity.com/sharedfiles/filedetails/?id=3575959825) | Cutting a live wire sparks / injures / kills | Cutting a powered cable can throw your wirecutter, injure, or kill the player, scaling with power | No | Maybe -- small self-contained "safety" mechanic; could be a future opt-in config, but it's Deadly Electricity's signature, better left to that mod |
| [Deadly Electricity](https://steamcommunity.com/sharedfiles/filedetails/?id=3575959825) | Surplus generation strains / damages / ignites generators | Over-producing without dumping the surplus to heaters damages connected generators, eventually fires | No | Maybe -- an interesting "generation has consequences" mechanic; possible future opt-in; not core scope |
| [Better Power Mod](https://steamcommunity.com/sharedfiles/filedetails/?id=3234916147) | Solar panel 500 W cap removed | Panels can exceed 500 W in strong sunlight | No | Maybe -- a balance lever; could be a config option, but it's Better Power Mod's job and conflicts with "solar is a small generator" |
| [Better Power Mod](https://steamcommunity.com/sharedfiles/filedetails/?id=3234916147) | Wind turbine output buff + tooltip | Higher max output for wind turbines, plus a tooltip | No | Maybe -- same (balance lever) |
| [Better Power Mod](https://steamcommunity.com/sharedfiles/filedetails/?id=3234916147) | Stirling engine -> 20 kW | Stirling generates 20 kW like the Gas Fuel Generator | No | Maybe -- same |
| [Better Power Mod](https://steamcommunity.com/sharedfiles/filedetails/?id=3234916147) | Turbine Generator output x10 | The ~90 W vanilla Turbine Generator -> ~900 W (still small) | No | Maybe -- the vanilla 90 W is laughable; a config multiplier could be a nice add, but it's a different mod's domain |
| [Better Power Mod](https://steamcommunity.com/sharedfiles/filedetails/?id=3234916147) | Battery chargers / power controllers / omni transmitters -> 2.5 kW | Raises the wattage caps on those three device types | No | Maybe -- per-device balance tweak |
| [Better Power Mod](https://steamcommunity.com/sharedfiles/filedetails/?id=3234916147) | Solar-panel position info in tooltip | Shows how accurately a panel is sun-facing | No | No -- pure QoL tooltip; not power-sim scope |
| [PowerOverhaul (gas devices)](https://steamcommunity.com/sharedfiles/filedetails/?id=3579306377) | Configurable power draw on Pressure / Back-Pressure Regulator / Volume Pump / Gas Mixer | 5-100% scaling of those devices' draw, adjusting both runtime and logic-reported values | No | Maybe -- a per-device draw-scaling config; coexists with us; could fold in as a "device draw tweaks" config but it's narrow and PowerOverhaul does it standalone |
| [Custom Power DeepMiner](https://steamcommunity.com/sharedfiles/filedetails/?id=3491001740) | Configurable Deep Miner power draw | 1 W to 100 kW, default 5 kW (vs vanilla 500 W) | No | Maybe -- trivial single config; easy to fold in if we add a device-draw config section |
| [Bins Power Rebalance](https://steamcommunity.com/sharedfiles/filedetails/?id=2392722433) | Console power use 50 W -> 1 W | Reduces the console's idle draw | No | No -- per-device tweak; the mod is flagged "maybe doesn't work" / old loader |
| [ArcFurnace Overhaul](https://steamcommunity.com/sharedfiles/filedetails/?id=2564615804) | Arc Furnace smelts all ingots in one tick at 10 W/unit | A massive power-cost reduction + speed-up for the Arc Furnace | No | No -- a balance/recipe rework; orthogonal; conflicts with our "Arc Furnace is heavy-draw" framing |
| [Power2Resources](https://steamcommunity.com/sharedfiles/filedetails/?id=1328890332) | Tool Manufactory makes ingots / coal / tomato soup at the cost of power | A power-to-resources converter recipe | No | No -- content/recipe mod; orthogonal |
| [Super Structures Mod](https://steamcommunity.com/sharedfiles/filedetails/?id=3159841144) | Raises pressure/temperature limits on Turbine Generator + reinforced windows + iron window + active vents | Lets those structures survive harsher conditions | No | No -- enables harder generation conditions; orthogonal (and the Turbine Generator's 90 W cap is the real bottleneck) |
| [MorePowerMod](https://steamcommunity.com/sharedfiles/filedetails/?id=3243132734) | Big Omni Transmitter | 40 m range, 5 kW charge, logic-controlled, wall/ceiling, 75%->25% efficiency by distance | No | No -- new content; out of scope for a pure-patch mod |
| [MorePowerMod](https://steamcommunity.com/sharedfiles/filedetails/?id=3243132734) | Nuclear Station Battery (230.4 MW capacity) | A huge stationary battery | No | No -- new content; note it inherits our discharge-rate limit if installed alongside (230.4 MW x 0.7% is enormous and would want its own tuning) |
| [MorePowerMod](https://steamcommunity.com/sharedfiles/filedetails/?id=3243132734) | Nuclear Wireless Battery Cell (2.304 MW capacity) | A huge wireless cell | No | No -- new content |
| [MoreCables](https://steamcommunity.com/sharedfiles/filedetails/?id=3555588082) | Adds higher-capacity cable tiers (super-heavy 500 kW, super-conductor 1 MW) | Two new cable types above the vanilla set | No | No -- new content; vanilla's `superHeavy` is our top tier. Compat: our gating would need to classify MoreCables' tiers (treat as `superHeavy`-equivalent, or reject) -- a compat decision, not adoption |
| [MoreCables](https://steamcommunity.com/sharedfiles/filedetails/?id=3555588082) | Reconfigure vanilla cable voltage ratings | All cable types' burn thresholds become config values | No | Maybe -- the *idea* (configurable cable thresholds) is adjacent to NEW-1, but we chose a `CableType`-based exemption, not numeric thresholds; not adopting |
| [CableTypeSwitcher](https://steamcommunity.com/sharedfiles/filedetails/?id=3470776044) | `CABLETYPESWITCH` console command | Inspect a cable network, convert it between normal/heavy/super-heavy, split long super-heavy runs | No | No -- a QoL retype command; but it interacts with our tier gating (converting a network's tier could create an illegal junction -> our burn-on-join handles it). Compat note, not adoption |
| [EL Switche](https://steamcommunity.com/sharedfiles/filedetails/?id=3287183705) | Logic-controlled cable spawner | On a click/logic signal, spawns/removes cables (normal + heavy) to join or split power+logic networks | No | No -- new content; and it could create illegal tier junctions -> our burn-on-join handles it. Compat note |
| [NetworkUpgrader](https://steamcommunity.com/sharedfiles/filedetails/?id=3656955459) | `upgrade cables\|pipes\|chutes\|all` console command | Replaces chains of 3+ straight cables/pipes/chutes with their long variants, preserving colours | No | Maybe -- pure QoL, doesn't touch power mechanics; could fold in, but it's a Network-Purist-Plus-ish concern, not power-sim |
| [Network Painter](https://steamcommunity.com/sharedfiles/filedetails/?id=2876605527) | Network-wide cable paint | Spraying a cable repaints the whole cable (pipe/chute) network; Shift = single item; Ctrl = checkered | No | No -- cosmetic; this repo's Spray Paint Plus owns that space |
| [Battery Backup Light](https://steamcommunity.com/sharedfiles/filedetails/?id=3569109044) | Emergency-light behaviour for the Wall Light Battery | Turns the light on when the cable network loses power, runs from its cell; Mode 1 reverts to vanilla | No | Maybe -- small, self-contained device behaviour reading the same network-power state our tick exposes; could fold in as a minor feature -- but it's a standalone mod that works fine and isn't really "power-sim" |
| ["Better" Battery Light](https://steamcommunity.com/sharedfiles/filedetails/?id=3491770665) | Battery wall-light indicator | Light turns yellow below 99.5% cell (unlocked) or on when grid wattage < 100 W (locked) | No | No -- overlaps the above; cosmetic indicator |
| [More Stirling Logic](https://steamcommunity.com/sharedfiles/filedetails/?id=2883817490) | Stirling engine atmospheric logic reads | Adds logic-readable input/output/hot-side/cold-side atmospherics + pressure efficiency to the Stirling engine | No | Maybe -- a small logic-surface addition, same flavour as our transformer/battery logic additions; low priority but cheap; could fold in |
| [Omni Transmitter Settings](https://steamcommunity.com/sharedfiles/filedetails/?id=3643534844) | Reconfigure the vanilla Omni Transmitter | Configurable max range, distance-based charge falloff, minimum power per battery | No | No -- wireless tuning; out of scope (this repo's Power Transmitter Plus covers the microwave transmitter, not the omni) |
| [Better Transmitter](https://steamcommunity.com/sharedfiles/filedetails/?id=3582023521) | Power transmitter range setting | Single config to change the power transmitter's range (up to 10k) | No | No -- wireless tuning; out of scope |
| [Power Transmitter Plus](https://steamcommunity.com/sharedfiles/filedetails/?id=3707677512) | Visible coloured beam | A laser beam drawn between linked transmitter and receiver | No | No -- sibling mod (this repo), different device; listed for completeness |
| [Power Transmitter Plus](https://steamcommunity.com/sharedfiles/filedetails/?id=3707677512) | Scrolling pulse train | Pulses travel along the beam, speed scaling with throughput, frozen when no power flows | No | No -- sibling mod |
| [Power Transmitter Plus](https://steamcommunity.com/sharedfiles/filedetails/?id=3707677512) | Distance-cost source-draw model | Removes the vanilla 500 m cap; the source pays an overhead proportional to distance per watt delivered | No | No -- sibling mod |
| [Power Transmitter Plus](https://steamcommunity.com/sharedfiles/filedetails/?id=3707677512) | 5 read-only logic types | Source draw / destination draw / transmission loss / efficiency / linked partner | No | No -- sibling mod |
| [Power Transmitter Plus](https://steamcommunity.com/sharedfiles/filedetails/?id=3707677512) | Writable auto-aim logic type | Slews the dish to a Thing's ReferenceId, cached across save/load and MP join | No | No -- sibling mod |
| [Power Transmitter Plus](https://steamcommunity.com/sharedfiles/filedetails/?id=3707677512) | Wall / ceiling placement | The microwave transmitter can be mounted on walls/ceilings | No | No -- sibling mod |
| [3.6 Megawatt Battery](https://steamcommunity.com/sharedfiles/filedetails/?id=3004087671) | Nuclear Battery capacity -> 3.6 MWh; charging consumes a whole Station Battery | An XML stat edit | No | No -- content/stat mod; orthogonal; note it inherits our rate limits |
| [EGC - End Game Content](https://steamcommunity.com/sharedfiles/filedetails/?id=3007388410) | XL Wireless Battery recipe + capacity -> 1170 kWh; infinite-filter recipes | XML recipes/stats | No | No -- content mod; orthogonal |
| [Super XL Wireless Battery (EGC Edit)](https://steamcommunity.com/sharedfiles/filedetails/?id=3706534108) | XL Wireless Battery capacity -> 90 MWh; expensive recipe | XML stat edit | No | No -- content/stat mod; orthogonal |
| [BuffWirelessBatteries](https://steamcommunity.com/sharedfiles/filedetails/?id=3355912171) | Wireless cells: small 12 kJ -> 27 kJ, large 72 kJ -> 216 kJ | XML stat edit | No | No -- stat mod; orthogonal |
| [Mod: Jigawatt Battery](https://steamcommunity.com/sharedfiles/filedetails/?id=1785747072) | Adds a 1.21 GW creative-scale battery | New battery prefab (needs Processed Uranium) | No | No -- content mod; orthogonal; note 1.21 GW x 0.7% rate limit = ~8.5 MW/tick discharge, way past sane, but that's the mod's problem |
| [RRI - Boost Da Powa](https://steamcommunity.com/sharedfiles/filedetails/?id=2359999429) | Upgraded battery cells (small 200 kJ, large 600 kJ, nuclear 4 MJ), copper/gold/silver recipes | New cell items + recipes | No | No -- content/recipe mod; orthogonal |
| [CraftableRTG](https://steamcommunity.com/sharedfiles/filedetails/?id=3026880031) | Survival craft recipe for the RTG | XML recipe | No | No -- content/recipe mod; orthogonal |
| [Survival RTG](https://steamcommunity.com/sharedfiles/filedetails/?id=2464235697) | RTG craftable in Electronics Printer Mk2 | XML recipe | No | No -- content/recipe mod; orthogonal |
| [Recipe - RTG (obsolete)](https://steamcommunity.com/sharedfiles/filedetails/?id=1351511005) | First-gen RTG fabricator recipe | Abandoned XML recipe | No | No -- obsolete |
| [RTG Start Conditions and Difficulty Mod](https://steamcommunity.com/sharedfiles/filedetails/?id=3568945039) | "Standard RTG Start+" (12 RTGs) + relaxed difficulty presets | Worldgen / difficulty config | No | No -- worldgen/difficulty; orthogonal |
| [Nuclear Recipes](https://steamcommunity.com/sharedfiles/filedetails/?id=3004097828) | Reworks Nuclear Battery + RTG recipes to use Processed Uranium; expensive/slow | XML recipe rework | No | No -- content/recipe mod; orthogonal |
| [2x Solar Power](https://steamcommunity.com/sharedfiles/filedetails/?id=1669621266) | Doubles solar output (Moon / Mars / space / Europa) | Scales the solar-irradiance constants | No | Maybe -- a solar-output scaling lever; could be a config option in a "balance" section, but it's a different mod's job (with generators-on-heavy it just means your heavy-tier solar farm produces more) |
| [Realistic Solar Constants](https://steamcommunity.com/sharedfiles/filedetails/?id=1475742702) | Solar irradiance set to "realistic" levels (Mars 43%, Europa 3.7%) | Scales the solar-irradiance constants the other way (weaker) | No | Maybe -- same scaling lever, opposite direction |
| [PIAC - Power Info and Control](https://steamcommunity.com/sharedfiles/filedetails/?id=3687433072) | IC10 power dashboard / generator auto-fallback | A polished IC10 script: generation/usage stats, solar sun-tracking, solid/fuel generator fallback/auto mode with adjustable thresholds | No | No -- an IC10 script package, not a code mod; nothing to "fold in"; orthogonal |
| [Power Trading](https://steamcommunity.com/sharedfiles/filedetails/?id=3420179579) | Trader that buys uranium / charcoal / solid fuel | A power-fuel economy mod | No | No -- economy mod; orthogonal |
| [LibConstruct](https://steamcommunity.com/sharedfiles/filedetails/?id=3505115682) | (Framework: custom-placement library used by Modular Consoles etc.) | Not a power feature -- a placement library | n/a | n/a -- not a feature; a dependency only the dropped Heavy Breaker would have needed |

Notes:
- "Maybe" almost always means "a small, orthogonal config-able tweak we *could* fold in, but it's a different mod's natural job, so out of scope unless you want a 'balance tweaks' grab-bag section." The only "Maybe"s with real pull are the Deadly Electricity flavour mechanics (cutting live wires hurts; surplus damages generators) -- those would be opt-in config in a hypothetical future phase, not v1.
- "Compat note" rows (CableTypeSwitcher, EL Switche, MoreCables) are mods that, when installed alongside Power Grid Plus, can produce an illegal cable-tier junction by means other than placing a cable; the burn-on-join backstop (D11) is what handles them. Re-Volt (PowerTick swap) is the other compat case -- documented in Appendix B.
- The IC10-script ecosystem (PIAC and dozens of smaller power scripts) is out of scope by definition -- those aren't code mods.
