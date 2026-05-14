# Power Grid Plus -- RESEARCH

Project-scoped internals: how the mod is built, every patch site and what it does, the design decisions behind the choices, and the game-internals knowledge the mod relies on. Forward-looking work and pending decisions live in `TODO.md`, not here.

The mod is a derivative of Sukasa's [Re-Volt](https://github.com/sukasa/revolt) (MIT, Copyright (c) 2025 Sukasa) trimmed to a pure-patch subset, plus three original features on top. A read-only clone of the upstream lives at `.work/revolt-source/`.

---

## 1. Overview

Power Grid Plus rewrites the cable-network power tick and adds a three-tier transmission-voltage policy on top of the vanilla cable types. The simulation half (proportional source / load sharing, gradual probabilistic cable burnout, transformer / battery / APC fixes) is inherited from Re-Volt. The voltage-tier half (`normal`, `heavy`, `superHeavy` cables as three mutually exclusive voltages, bridged only by transformers / APCs; per-device tier rules; reactive cable-burn enforcement; burn-reason tooltips on wreckages) is original.

It is a **pure-patch mod**: no asset bundle, no new prefabs, no recipes for new items. The only added content is a `GameData/cable-recipes.xml` overlay that doubles the super-heavy cable coil's crafting cost. Removing the mod from a save reverts behaviour to vanilla; the side-car XMLs the mod writes into the save ZIP for per-Transformer `LogicPassthroughMode` and burn-reason wreckage state are skipped silently by the vanilla loader.

---

## 2. Mod identity

| Field | Value |
|---|---|
| Display name | Power Grid Plus |
| Code name | PowerGridPlus |
| Folder / `.sln` / `.csproj` | `Mods/PowerGridPlus/PowerGridPlus.sln` + `Mods/PowerGridPlus/PowerGridPlus/PowerGridPlus.csproj` |
| `RootNamespace` / `AssemblyName` / DLL | `PowerGridPlus` |
| Plugin GUID / `ModID` | `net.powergridplus` |
| C# namespace root | `PowerGridPlus` |
| Workshop ID | none yet (assigned at first publish; goes in `About.xml` `<WorkshopHandle>`) |
| Dependencies | BepInEx + StationeersLaunchPad (+ LaunchPadBooster). No LibConstruct, no StationeersMods asset-bundle infrastructure. |
| Multiplayer | `MOD.Networking.Required = true` -- the power-tick rewrite runs host-side and every client needs the matching DLL. |
| License | Apache 2.0 for original code (repo standard); Re-Volt-derived files retain their MIT notice (see section 11). |

The mod was seeded from `Mods/Template/` and graduated from `Plans/` to `Mods/` during the 2026-05-12 / 05-13 build sessions.

---

## 3. Architecture

The plugin is a `BaseUnityPlugin` (`PowerGridPlus.Plugin`) whose `Awake` binds the BepInEx config and registers `Prefab.OnPrefabsLoaded` as the deferred patch site. Inside that callback the plugin sets `MOD.Networking.Required = true`, runs `Harmony.PatchAll()`, calls `CableCostPatches.ApplyRecipeCost()` (runtime override of the cable-recipes XML overlay), and calls `Ic10ConstantsPatcher.Apply()` (extends `ProgrammableChip.AllConstants` and the syntax-highlighting `ScriptEnum` arrays). The whole block is wrapped in try/catch -- a failure logs fatal but does not crash the game.

Functional regions, each rooted at a single static class or registry:

- **Power tick replacement** (`Power/PowerGridTick.cs`, `Patches/CableNetworkPatches.cs`, `Patches/PowerTickPatches.cs`). Replaces the vanilla `PowerTick` per `CableNetwork` with the Re-Volt-derived `PowerGridTick`. The patches wire the swap and reverse-patch the private helpers the tick body relies on. Proportional source / load sharing, sliding-window probabilistic cable burnout, NaN-power guard, and the voltage-tier rebuild detection all live here.
- **Device-level power gating** (`Patches/DevicePatches.cs`). Re-implements `Device.AssessPower` so per-device power accounting routes through the new tick instead of the vanilla bookkeeping.
- **Stationary battery rate limits** (`Patches/StationaryBatteryPatches.cs`, `Patches/BatteryLogicPatches.cs`). Caps `GetUsedPower` and `GetGeneratedPower` by a configurable fraction of `PowerMaximum`; re-implements `Battery.ReceivePower` with a charge-efficiency factor and a low-power-trickle floor. Exposes the max charge / discharge rates as the `ImportQuantity` / `ExportQuantity` logic values.
- **Transformer / APC fixes** (`Patches/TransformerExploitPatches.cs`, `Patches/TransformerLogicPatches.cs`, `Patches/AreaPowerControlPatches.cs`). Closes Re-Volt's transformer free-power exploit, restores quiescent draw, exposes `PowerActual` as a logic value, and closes the APC's idle power leak.
- **Voltage tiers** (`VoltageTier.cs`, `Patches/VoltageTierPatches.cs`, `Patches/CableCostPatches.cs`). The tier policy itself (`IsAllowedOnTier`, `ResolveMixedTierNetwork`, `BurnCableForMisplacedDevice`) plus the placement-preview rejections (cable-on-cable and cable-on-device) and the super-heavy cable cost runtime override.
- **Burn-reason tooltip plumbing** (`BurnReasonRegistry.cs`, `Patches/BurnReasonPatches.cs`). Each `Cable.Break()` call site registers a reason string in a `ConcurrentDictionary<Grid3, string>` keyed by the dying cable's cell. A postfix on `CableRuptured.OnRegistered` consumes the entry and attaches it to the wreckage via a `ConditionalWeakTable<Thing, string>` sidecar. A postfix on `Thing.GetPassiveTooltip` filtered to `__instance is CableRuptured` appends "Burned: \<reason\>" to the wreckage's hover tooltip.
- **Logic passthrough** (`LogicTypeRegistry.cs`, `PassthroughModeStore.cs`, `PassthroughSideCar.cs`, `Patches/LogicPassthroughPatches.cs`, `Patches/TransformerPassthroughLogicPatches.cs`, `Patches/PassthroughSaveLoadPatches.cs`, `Patches/LogicableInitializePatch.cs`, `Patches/EnumNamePatches.cs`, `Ic10ConstantsPatcher.cs`). The writable `LogicPassthroughMode` slot on `Transformer` (ushort 6577 from the central `Patterns/Logic/LogicTypeNumbers.cs` catalogue), plus the postfix on `CableNetwork.RefreshPowerAndDataDeviceLists` that merges the other side's device list into the local one when the mode is 1, plus the save-load side-car that persists per-Transformer mode overrides across save / load via an XML entry in the save ZIP.

The shared central LogicType catalogue at `Patterns/Logic/LogicTypeNumbers.cs` is linked into the `.csproj` so the integer values come from one source of truth across every SixFive7 mod that registers a custom `LogicType`.

---

## 4. File walkthrough

Files under `Mods/PowerGridPlus/PowerGridPlus/`. Headers in Re-Volt-derived files retain the MIT notice; everything else is original Apache 2.0.

### Plugin / config

- **`Plugin.cs`** -- `BaseUnityPlugin` entry. `Awake` binds the config (`Settings.Bind(Config)`) and hooks `Prefab.OnPrefabsLoaded` so all patches are deferred until the game finishes loading prefabs. The callback sets `MOD.Networking.Required = true`, runs `Harmony.PatchAll()`, then `CableCostPatches.ApplyRecipeCost()`, then `Ic10ConstantsPatcher.Apply()`.
- **`Settings.cs`** -- every `ConfigEntry<T>` the mod binds. Section names follow the repo convention (`Server - <Topic>`); `(Server-authoritative)` description prefix; `("Order", int)` tags for in-panel ordering. Sections: `Server - Cable Simulation`, `Server - Cable Costs`, `Server - Voltage Tiers`, `Server - Batteries`, `Server - Transformers`, `Server - Area Power Control`.

### Power tick (Re-Volt-derived, MIT)

- **`Power/PowerGridTick.cs`** -- the rewritten power tick. Methods `Initialize_New`, `CalculateState_New`, `ApplyState_New`; private `TestBurnCable` (skips `Cable.Type.superHeavy` when `EnableUnlimitedSuperHeavyCables`); per-tick fields `_mixedTierDetected` and `_misplacedDeviceForBurn` collected during `CalculateState_New` and consumed in `ApplyState_New` (only when `powerFlow > 0`).
- **`Patches/CableNetworkPatches.cs`** -- injects a `PowerGridTick` instance into every `CableNetwork` on construction; marks the tick dirty on device-list change.
- **`Patches/PowerTickPatches.cs`** -- reroutes `PowerTick.Initialise` / `CalculateState` / `ApplyState` to the new tick; reverse-patches the private helpers `CacheState` and `CheckForRecursiveProviders`.
- **`Patches/DevicePatches.cs`** -- re-implements `Device.AssessPower` so per-device power gating routes through `PowerGridTick.EstimatedRemainingLoad` / `DuringTickLoad` instead of vanilla bookkeeping. Reverse-patches `Device.SetPower`.

### Stationary batteries (Re-Volt-derived, MIT)

- **`Patches/StationaryBatteryPatches.cs`** -- caps `Battery.GetUsedPower` at `PowerMaximum * MaxBatteryChargeRate` and `Battery.GetGeneratedPower` at `PowerMaximum * MaxBatteryDischargeRate`. Re-implements `Battery.ReceivePower` to multiply incoming watts by `BatteryChargeEfficiency` (with a low-power-trickle floor: incoming watts under 500 are stored at full efficiency regardless of the config). Reverse-patches `Battery.get_IsOperable`.
- **`Patches/BatteryLogicPatches.cs`** -- exposes `Battery.ImportQuantity` (= max charge rate, watts) and `Battery.ExportQuantity` (= max discharge rate, watts) as logic-readable values when `EnableBatteryLogicAdditions` is on.

### Transformer (Re-Volt-derived, MIT; plus tier-exempt rule)

- **`Patches/TransformerExploitPatches.cs`** -- closes the transformer free-power exploit. Re-implements `Transformer.GetGeneratedPower`, `GetUsedPower`, and `ReceivePower`: output clamped to `min(Setting, upstream Potential - already provided)`; transformer charges its own `UsedPower` upstream.
- **`Patches/TransformerLogicPatches.cs`** -- exposes `Transformer.PowerActual` (current throughput) as a logic-readable value when `EnableTransformerLogicAdditions` is on.

### Area Power Controller (Re-Volt-derived, MIT, with one apparent-bug fix)

- **`Patches/AreaPowerControlPatches.cs`** -- closes the ~10 W APC free-power leak and the slow-drain-with-no-output bug by re-implementing `ReceivePower`, `UsePower`, and `GetUsedPower`. **One deviation from Re-Volt source**: Re-Volt 1.4.0 declares two prefix patches both targeting `AreaPowerControl.GetUsedPower`, the first of which takes a `powerUsed` parameter only matching `AreaPowerControl.UsePower`. Power Grid Plus retargets that one at `UsePower` to match its body. If the in-game load reveals the original Re-Volt attribution was right after all, revert (see Pitfalls).

### Voltage tiers (original, NEW-1 / NEW-2 / NEW-3)

- **`VoltageTier.cs`** -- the tier policy. `IsTierExempt` (`Transformer`, `AreaPowerControl`, `WirelessPower`, `PowerTransmitterOmni`), `IsGenerator` (`IPowerGenerator` + named generator classes), `IsStationaryBattery`, `IsHighDrawMachine` (built-in whitelist + `ExtraHeavyCableDevices` config). `IsAllowedOnTier(device, tier)` is the single per-device gate. `IsAllHeavyNetwork` is used by `PowerGridTick.TestBurnCable` for the super-heavy / heavy-cable cable-burn skip logic. `ResolveMixedTierNetwork(network, preferVictim)` and `BurnCableForMisplacedDevice(device, network)` perform the actual cable burns; both register a reason string with `BurnReasonRegistry` before calling `cable.Break()`.
- **`Patches/VoltageTierPatches.cs`** -- placement-preview rejections. `Cable_CanConstruct_Postfix` checks both cable-on-cable (a fresh cable adjacent to an existing cable network of a different tier) and cable-on-device (a fresh cable adjacent to an existing device that doesn't accept the cable's tier). Device placement is never cursor-blocked -- the round-3 design is reactive: device placement is allowed, the cable next to it burns on the next power-flowing tick.
- **`Patches/CableCostPatches.cs`** -- NEW-2 runtime override. The committed `GameData/cable-recipes.xml` overlay patch-replaces the vanilla `ItemCableCoilSuperHeavy` recipe with a default 2.0x cost (Time 8, Energy 800, Constantan 1.0, Electrum 1.0). `CableCostPatches.ApplyRecipeCost()` runs at plugin load; when the configured `Super-Heavy Cable Cost Multiplier` differs from the overlay's 2.0, it builds a new `WorldManager.RecipeData` and calls `ElectronicsPrinter.RecipeComparable.AddRecipe(...)` + `GenerateRecipieList()`, so the config value wins over the overlay.
- **`GameData/cable-recipes.xml`** -- the recipe overlay. The game's recipe loader patch-replaces a vanilla `<RecipeData>` when a mod ships one with a matching `<PrefabName>`; documented in `Research/GameSystems/RecipeDataLoading.md`.

### Burn-reason tooltips (original)

- **`BurnReasonRegistry.cs`** -- two stores. `_pendingByCell` is a `ConcurrentDictionary<Grid3, string>` (a reason waiting for its wreckage to register; multiple power-tick worker threads can write concurrently). `_attached` is a `ConditionalWeakTable<object, ReasonHolder>` (the reason permanently attached to a `CableRuptured` instance; GC cleans up automatically when the wreckage is destroyed). Static helpers `RegisterPending(cable, reason)`, `TryConsumePending(cell, out reason)`, `Attach(wreckage, reason)`, `GetAttached(wreckage)`.
- **`Patches/BurnReasonPatches.cs`** -- two postfixes. `CableRuptured.OnRegistered` consumes the pending reason for the wreckage's cell and attaches it via `BurnReasonRegistry.Attach`. `Thing.GetPassiveTooltip` (the universal base) filters to `__instance is CableRuptured` and appends `"<color=#ffa500>Burned:</color> <reason>"` to the `Extended` field.

### Logic passthrough (original)

- **`LogicTypeRegistry.cs`** -- declares `LogicPassthroughMode` as `LogicType` ushort 6577 (the value is read from the central `StationeersPlus.Shared.LogicTypeNumbers` constant linked into this csproj from `Patterns/Logic/LogicTypeNumbers.cs`). `CustomLogicType` record carries name + description; `All` is the registry array consumed by every integration patch (Logicable arrays, IC10 constants, EnumGetName postfixes, Stationpedia -- the last one is pending, see TODO).
- **`PassthroughModeStore.cs`** -- the in-memory mode store. `ConcurrentDictionary<long, int>` keyed by `Transformer.ReferenceId`. `GetMode(transformer)` falls through to `GetDefaultMode(prefabName)` when no override exists; the default-1 list is `StructureTransformerSmall` and `StructureTransformerSmallReversed`, every other transformer defaults to 0.
- **`PassthroughSideCar.cs`** -- the XML side-car. Static `PendingSaveSnapshot` is captured in the `SaveHelper.Save` prefix; `WriteSideCar(zipPath, data)` rebuilds the save ZIP with an extra entry `pwrgridplus-passthrough.xml`; `ReadSideCarFromDir(tempDir)` reads it back during load from the temp-extracted save tree.
- **`Patches/TransformerPassthroughLogicPatches.cs`** -- the `Transformer`-specific logic glue. Prefix on `CanLogicRead` and `CanLogicWrite` returns true when `logicType == LogicPassthroughMode`. Prefix on `GetLogicValue` returns the stored mode (or default by prefab). Prefix on `SetLogicValue` writes the new mode, then dirties both `InputNetwork` and `OutputNetwork`'s data device lists so the merge re-runs on the next refresh.
- **`Patches/LogicPassthroughPatches.cs`** -- the actual passthrough mechanism. Prefix on `CableNetwork.RefreshPowerAndDataDeviceLists` captures the `DataDeviceListDirty` flag (so the postfix only acts when the list was actually rebuilt). Postfix walks the network's `DeviceList`; for each `Transformer` (gated by `EnableTransformerLogicPassthrough` and `PassthroughModeStore.GetMode == 1`) and `AreaPowerControl` (gated by `EnableAreaPowerControlLogicPassthrough`), finds the "other" `CableNetwork` (the side opposite the local one) and appends its `DeviceList` into the local `_dataDeviceList` (deduped via `Contains`). The bridging device's own logic ports are naturally visible because the device sits in BOTH networks' `DeviceList` already (one cable connection on each side).
- **`Patches/PassthroughSaveLoadPatches.cs`** -- save / load wiring. Three patches: `SaveHelper.Save` prefix snapshots the store on the main thread and postfix wraps the async save with a continuation that writes the side-car after the ZIP is sealed; `XmlSaveLoad.LoadWorld` postfix reads the side-car from the loose temp-extracted file; `Thing.OnFinishedLoad` postfix per-Transformer applies the saved mode via `PassthroughModeStore.RestoreFromSideCar`.

### UI integration plumbing (original, mirroring PowerTransmitterPlus's pattern)

- **`Ic10ConstantsPatcher.cs`** -- one-time reflection injection into `ProgrammableChip.AllConstants` (so `s d0 LogicPassthroughMode 1` compiles in an IC10 script) and `ProgrammableChip.InternalEnums` `ScriptEnum<LogicType>` / `BasicEnum<LogicType>` (so screen-rendered code highlights the name correctly). Called from `Plugin.OnPrefabsLoaded` after `Harmony.PatchAll()`. Guarded by an `_applied` flag.
- **`Patches/LogicableInitializePatch.cs`** -- postfix on `Logicable.Initialize` that appends `LogicPassthroughMode` into the static `Logicable.LogicTypes` / `LogicTypeNames` arrays plus `Logicable.LogicTypeNamesRedirects` (sort-order index) plus `EnumCollections.LogicTypes` (the tablet-UI dropdown source) plus `ScreenDropdownBase.LogicTypes` (the on-screen IC10 syntax-preview source). Guarded by an `_injected` flag; idempotent.
- **`Patches/EnumNamePatches.cs`** -- three postfixes. `Enum.GetName(typeof(LogicType), value)`, `EnumCollection<LogicType, ushort>.GetName(value)`, and `EnumCollection<LogicType, ushort>.GetNameFromValue(value)` all fall back to `LogicTypeRegistry.TryGetName` when the vanilla lookup returns null.

### Shared / linked

- **`Patterns/Logic/LogicTypeNumbers.cs`** (linked, lives at the repo root) -- the central LogicType ushort catalogue across every SixFive7 mod. PowerGridPlus's csproj has `<Compile Include="..\..\..\Patterns\Logic\LogicTypeNumbers.cs" Link="Patterns\LogicTypeNumbers.cs" />`.

---

## 5. Patch catalog

Every Harmony patch, with target method, prefix / postfix / reverse, gating config, and a one-line behaviour summary.

| File | Target | Kind | Gated by | What it does |
|---|---|---|---|---|
| `CableNetworkPatches` | `CableNetwork` ctor | injection | always | Constructs and stores a `PowerGridTick` instance per `CableNetwork`. |
| `PowerTickPatches` | `PowerTick.Initialise` | postfix | always | Routes to `PowerGridTick.Initialize_New`. |
| `PowerTickPatches` | `PowerTick.CalculateState` | postfix | always | Routes to `PowerGridTick.CalculateState_New`. |
| `PowerTickPatches` | `PowerTick.ApplyState` | postfix | always | Routes to `PowerGridTick.ApplyState_New`. |
| `PowerTickPatches` | `PowerTick.CacheState` (private) | reverse | always | Exposes the vanilla `CacheState` for `PowerGridTick` to call. |
| `PowerTickPatches` | `PowerTick.CheckForRecursiveProviders` (private) | reverse | always | Exposes the vanilla recursive-network check; only invoked when `EnableRecursiveNetworkLimits` is on. |
| `DevicePatches` | `Device.AssessPower` | prefix | always | Replaces vanilla per-device power gating with `PowerGridTick`-aware logic; respects `EstimatedRemainingLoad` and `DuringTickLoad`. |
| `DevicePatches` | `Device.SetPower` | reverse | always | Exposes the vanilla `SetPower` for `Device.AssessPower` to call. |
| `StationaryBatteryPatches` | `Battery.GetUsedPower` | prefix | `EnableBatteryLimits` | Caps charge rate at `PowerMaximum * MaxBatteryChargeRate`. |
| `StationaryBatteryPatches` | `Battery.GetGeneratedPower` | prefix | `EnableBatteryLimits` | Caps discharge rate at `PowerMaximum * MaxBatteryDischargeRate`. |
| `StationaryBatteryPatches` | `Battery.ReceivePower` | prefix | `EnableBatteryLimits` | Multiplies incoming watts by `BatteryChargeEfficiency`, with a sub-500 W trickle-charge floor. |
| `StationaryBatteryPatches` | `Battery.get_IsOperable` | reverse | always | Exposes the property for use by the prefix bodies. |
| `BatteryLogicPatches` | `Battery.CanLogicRead` | prefix | `EnableBatteryLogicAdditions` | Returns true for `ImportQuantity` / `ExportQuantity`. |
| `BatteryLogicPatches` | `Battery.GetLogicValue` | prefix | `EnableBatteryLogicAdditions` | Returns `PowerMaximum * MaxBatteryChargeRate` / `PowerMaximum * MaxBatteryDischargeRate`. |
| `TransformerExploitPatches` | `Transformer.GetGeneratedPower` | prefix | `EnableTransformerExploitMitigation` | Clamps output to `min(Setting, upstream Potential - already provided)`. |
| `TransformerExploitPatches` | `Transformer.GetUsedPower` | prefix | `EnableTransformerExploitMitigation` | Charges the transformer's own draw upstream. |
| `TransformerExploitPatches` | `Transformer.ReceivePower` | prefix | `EnableTransformerExploitMitigation` | Reroutes through the new tick. |
| `TransformerLogicPatches` | `Transformer.CanLogicRead` | prefix | `EnableTransformerLogicAdditions` | Returns true for `PowerActual`. |
| `TransformerLogicPatches` | `Transformer.GetLogicValue` | prefix | `EnableTransformerLogicAdditions` | Returns `_powerProvided`. |
| `AreaPowerControlPatches` | `AreaPowerControl.ReceivePower` | prefix | `EnableAreaPowerControlFix` | Closes the free-power leak by routing through the new tick. |
| `AreaPowerControlPatches` | `AreaPowerControl.UsePower` | prefix | `EnableAreaPowerControlFix` | Charges the APC's draw upstream (the apparent-bug fix vs Re-Volt's source: Re-Volt patches `GetUsedPower` with this body). |
| `AreaPowerControlPatches` | `AreaPowerControl.GetUsedPower` | prefix | `EnableAreaPowerControlFix` | Reports the upstream-charged draw correctly. |
| `VoltageTierPatches` | `Cable.CanConstruct` | postfix | `EnableVoltageTiers` | Rejects cable-on-cable mismatches (different tier adjacent to an existing cable network) AND cable-on-device mismatches (existing device that doesn't accept this cable's tier). |
| `CableCostPatches` | not a Harmony patch | (called from `Plugin.OnPrefabsLoaded`) | `SuperHeavyCableCostMultiplier != 2.0` | Runtime override of the `ItemCableCoilSuperHeavy` recipe when the multiplier differs from the GameData overlay's 2.0x default. |
| `BurnReasonPatches` | `CableRuptured.OnRegistered` | postfix | always | Consumes the pending burn reason for the wreckage's cell and attaches it via `BurnReasonRegistry.Attach`. |
| `BurnReasonPatches` | `Thing.GetPassiveTooltip` | postfix | always | Filters `__instance is CableRuptured` and appends the burn reason to the wreckage's hover tooltip. |
| `LogicPassthroughPatches` | `CableNetwork.RefreshPowerAndDataDeviceLists` | prefix + postfix | `EnableTransformerLogicPassthrough` / `EnableAreaPowerControlLogicPassthrough` | Per-device merge of the "other" network's `DeviceList` into the local `_dataDeviceList` when the device is a logic-transparent Transformer (mode 1) or any APC (feature-gated only). |
| `TransformerPassthroughLogicPatches` | `Transformer.CanLogicRead` | prefix | always | Returns true for `LogicPassthroughMode`. |
| `TransformerPassthroughLogicPatches` | `Transformer.CanLogicWrite` | prefix | always | Returns true for `LogicPassthroughMode`. |
| `TransformerPassthroughLogicPatches` | `Transformer.GetLogicValue` | prefix | always | Returns `PassthroughModeStore.GetMode(__instance)` for `LogicPassthroughMode`. |
| `TransformerPassthroughLogicPatches` | `Transformer.SetLogicValue` | prefix | always | Writes the mode to `PassthroughModeStore`, dirties both side networks' data device lists. |
| `PassthroughSaveLoadPatches` | `SaveHelper.Save` (private worker, 4-arg overload) | prefix + postfix | always | Snapshots the mode store on the main thread; wraps the async save with a side-car write continuation. |
| `PassthroughSaveLoadPatches` | `XmlSaveLoad.LoadWorld` | postfix | always | Reads the side-car XML from the temp-extracted save tree into `PassthroughSideCar.LoadedModes`. |
| `PassthroughSaveLoadPatches` | `Thing.OnFinishedLoad` | postfix | always | Per-Transformer restore from the loaded side-car. |
| `LogicableInitializePatch` | `Logicable.Initialize` | postfix | always (idempotent) | Appends the custom LogicType into `Logicable.LogicTypes` / `LogicTypeNames` / `LogicTypeNamesRedirects` / `EnumCollections.LogicTypes` / `ScreenDropdownBase.LogicTypes`. |
| `EnumNamePatches` | `Enum.GetName(typeof(LogicType), value)` | postfix | always | Returns the registered name when the vanilla lookup is null. |
| `EnumNamePatches` | `EnumCollection<LogicType, ushort>.GetName(value)` | postfix | always | Same. |
| `EnumNamePatches` | `EnumCollection<LogicType, ushort>.GetNameFromValue(value)` | postfix | always | Same. |

---

## 6. Multiplayer protocol

The mod sets `MOD.Networking.Required = true` (LaunchPadBooster). The connection handshake checks the mod version; clients without a matching DLL are rejected at join time.

There are **no custom network messages**. The mod has no `IJoinValidator` / `IJoinSuffixSerializer` implementation. All state that needs to reach clients does so through vanilla state-sync:

- The power tick runs on the host; clients see device `Powered` states via the normal `Device.SetPower` replication.
- `Transformer.LogicPassthroughMode` is stored host-side in `PassthroughModeStore` and serialised into the side-car at save time. A joining client does not directly read the host's store; instead, the host's data-device-list merge (the actual passthrough behaviour) runs server-side, and the resulting `DataDeviceList` membership is what clients observe when their IC10 chips iterate the list. Reading `LogicPassthroughMode` from an IC10 on a client returns whatever the chip's local replica of the device reports; since `SetLogicValue` runs the standard server-bound replication for logic writes, the write is authoritative on the host and the read on any client is consistent within the next tick.
- The burn-reason wreckage tooltip is computed server-side at the moment of `Cable.Break()` and persisted in the wreckage's `BurnReasonRegistry` attached state. Clients see the wreckage Thing via normal replication; the tooltip postfix runs locally on each client, reading from the client's own `BurnReasonRegistry`. **This means a joining client does not see burn reasons for wreckages that existed before the join** -- the registry's `_attached` table is populated on `CableRuptured.OnRegistered`, which only fires on the host. A late-joining client sees the wreckage but no reason. Not currently considered a bug since the visual debris itself is the more important feedback; logged as a known limitation.

Side-car XMLs persist server-only state and are part of the save ZIP. A vanilla loader (no mod) ignores unknown entries silently, so saves remain backwards-compatible if the mod is removed.

---

## 7. Pitfalls and gotchas

- **`CableNetwork.DataDeviceList.get` checks the wrong flag.** Vanilla code reads `if (PowerDeviceListDirty) RefreshPowerAndDataDeviceLists()` instead of `DataDeviceListDirty`. This means a `DirtyDataDeviceList()` call alone does not invalidate `DataDeviceList`-reads until something else (cable add / remove, power-side dirty) flips the power flag too. In practice the per-tick power-side rebuild covers this, so the bug is silent. Anyone touching this code should not rely on `DataDeviceList.get` triggering a refresh after a data-only dirty. See `Research/GameClasses/CableNetwork.md` "Field shape and accessor quirk" for the verbatim accessor code.
- **`Cable.Break()` runs on worker threads.** The Re-Volt-derived power tick runs in UniTask workers. `BurnReasonRegistry._pendingByCell` is `ConcurrentDictionary<Grid3, string>` precisely because multiple threads call `RegisterPending` concurrently. The matching `Attach` runs on the main thread in the `CableRuptured.OnRegistered` postfix (Unity Things are main-thread-only), so the registry's main-thread `ConditionalWeakTable` is fine. Adding new burn call sites needs to honour this split.
- **`Thing.GetPassiveTooltip` is a universal-base postfix.** The Burn-reason postfix patches the base method, not `CableRuptured`-specific (which does not override). Every tooltip in the game runs through this postfix; the `__instance is CableRuptured` filter is the first line. Sanity check before adding similar universal patches: is the work cheap enough to run on every hover?
- **APC patch deviation from Re-Volt 1.4.0.** Re-Volt declares the `UsePower`-bodied prefix as patching `GetUsedPower`; the parameter types only match `UsePower`. Power Grid Plus retargets at `UsePower`. If APC behaviour misbehaves in-game (battery doesn't drain to downstream, or upstream draw isn't reported), this is the first suspect; revert to the Re-Volt attribution and verify.
- **`SaveHelper.Save`'s private 4-arg overload is the only sound patch target.** The public `Save(string, CancellationToken)` overload would `AmbiguousMatchException` at `PatchAll` time without the explicit `new[] { typeof(DirectoryInfo), typeof(string), typeof(bool), typeof(CancellationToken) }` argument-type array. See `PassthroughSaveLoadPatches.cs`.
- **`ElectronicsPrinter.RecipeComparable.GenerateRecipieList` is the canonical typo.** The vanilla method is mis-spelled (`Recipie`). Do not silently fix it in calls or the override will not bind.
- **Run-once Ic10ConstantsPatcher / LogicableInitializePatch composition.** Both are guarded by static flags. If PowerTransmitterPlus also runs (it follows the same pattern), the second mod's postfix sees the first mod's already-injected array and appends on top. Order is stable as long as both mods guard with their own `_applied` flag; never `Array.Copy` over a fresh `new T[vanillaLength]`.
- **Loaded saves with pre-existing mixed-tier networks trigger a cascade.** D11 retroactive enforcement burns the boundary cable of every pre-existing mixed-tier network on the first power-flowing tick after load. On a base that mixed tiers freely under vanilla rules this is destructive. The Option-C TODO entry proposes a default-off opt-in.

---

## 8. Design decisions

The decision log spans three named phases (the 2026-05-12 first build pass, the 2026-05-13 round-2 follow-up that resolved every stubbed / deferred item, and the 2026-05-13 round-3 device-rejection -> reactive-cable-burn pivot) plus the 2026-05-14 LogicPassthroughMode addition. Entries are append-only; supersedes are explicit.

### D1 -- D13 (2026-05-12)

- **D1**: Pure-patch mod, no new prefabs. Trims dependencies (no LibConstruct, no asset bundle), simplifies game-update maintenance.
- **D2**: Derive from Sukasa's Re-Volt (MIT) for the simulation half rather than reimplement from scratch. Attribution per section 11.
- **D3**: All config is server-authoritative; `Server - *` sections only.
- **D4**: Code name `PowerGridPlus`, display name `Power Grid Plus`.
- **D5**: NEW-3 research spike done; verdict was build-time rejection via `Cable.CanConstruct` postfix + reactive burn-on-join in `OnRegistered` postfix.
- **D6**: Three-tier voltage gating -- `normal`, `heavy`, `superHeavy` cables mutually incompatible, bridged only by transformers.
- **D7**: Super-heavy-cable cost multiplier default 2.0; NEW-1 (no burn limit) applies only to `superHeavy`.
- **D8**: Tier-by-draw, "lowest cable tier rated for peak draw / output". Stationary batteries on `heavy`; the >5 kW machines (Advanced Furnace, Arc Furnace, Carbon Sequester 45 kW/unit, Furnace, Centrifuge, Recycler, Ice Crusher, Hydraulic Pipe Bender, electric Deep Miner) on `heavy`+.
- **D9 (SUPERSEDED in round 2 by the universal cable-tier rule)**: per-prefab transformer-tier mapping (Small = `heavy<->normal`, Medium = `heavy<->heavy`, Large = `superHeavy<->heavy`).
- **D10 (SUPERSEDED in round 2 by the universal cable-tier rule)**: bidirectional transformers re-implementing `GetGeneratedPower` / `GetUsedPower` / `ReceivePower` symmetrically.
- **D11**: enforcement = build-time rejection (primary UX) + reactive burn-on-join (authoritative backstop + save migration). Burn-on-join algorithm: while a `CableNetwork` contains more than one `CableType`, pick a lowest-tier cable that is grid-adjacent to a higher-tier cable (the boundary cable) and `Break()` it. Fuses are never substituted: a tier mismatch is a topology violation, not over-current.
- **D12**: NEW-2 multiplies only the coil-crafting XML recipe; nothing touches per-cable `BuildStates[0].Tool.EntryQuantity`. Insulated cable variants count as their base `CableType`. In-game tier names stay "Normal / Heavy / Super-Heavy cable"; rejection message leans on the lore ("Cannot connect: different transmission voltage -- use a transformer").
- **D13**: all electrical generators are `heavy`-cable-only (overriding the per-output tier-by-draw rule for generators specifically). Solar Panels, the small Wind Turbine, Large Wind Turbine, Stirling Engine, Solid / Gas Fuel Generators, Turbine Generator, RTG -- all `heavy`. The build-time `Device.CanConstruct` rejection (then planned, now superseded by reactive burn per round-3 below) was the enforcement vehicle.

### Round 2 (2026-05-13): universal cable-tier rule

The user's round-2 instruction simplified the design: drop D9 / D10's per-prefab transformer tier mapping in favour of the universal rule "a `CableNetwork` must be all one tier; transformers and APCs have separate input / output networks and are therefore tier-exempt". This kept the spirit of D6 (the three tiers are separate voltages) but removed the need to distinguish transformer prefabs at all. `VoltageTier.IsTierExempt` was added; `Transformer`, `AreaPowerControl`, `WirelessPower`, and `PowerTransmitterOmni` are exempt. The "ordinary device on heavy / super-heavy = hard reject + no power if it slips through" rule was implemented at the tick level (`PowerGridTick.CalculateState_New` zeroes a device's used and generated power on the wrong tier) but the build-time hard reject was deferred (resolved in round 3).

### Round 3 (2026-05-13): build-time-device-rejection -> reactive cable-burn

The remaining round-2 gap was the build-time cursor reject for misplaced devices. On reflection the user preferred to NOT gate device placement at all and instead burn the cable next to the misplaced device, symmetric with the cable-tier rule. Done:

- `PowerGridTick.CalculateState_New` no longer zeroes a misplaced device's power. Instead it records the first misplaced device of the tick (`_misplacedDeviceForBurn`); `ApplyState_New` burns the cable adjacent to it via `VoltageTier.BurnCableForMisplacedDevice`. The device itself is never destroyed.
- Both tier-burns moved to `ApplyState_New`, gated on `powerFlow > 0`. An idle / off network destroys nothing, even if mixed-tier or with a misplaced device. As soon as actual power flows, the burn fires. Mixed-tier burns take precedence over device-tier burns (root cause first).
- The previously-removed `Cable_OnRegistered_Postfix` was confirmed unneeded. Cable registration triggers `DirtyPowerAndDataDeviceLists` -> `IsDirty = true` -> `Initialize_New` re-detects mixed-tier on the next tick. One code path covers fresh placement and loaded saves.
- `Cable_CanConstruct_Postfix` extended with a second pass over `Cable.ConnectedDevices()` so that placing a cable next to an existing device on a tier the device doesn't accept is rejected at the cursor.
- Burn-reason tooltips wired (see Architecture). Four reason categories: overload, wrong voltage (mixed-tier bridge), wrong voltage (adjacent misplaced device), and an untagged fallback.

Net result: build-time gating is cable-side only; device placement is never blocked. The "you put it on the wrong cable" feedback is the cable burning, with a tooltip on the wreckage explaining why.

### 2026-05-14: LogicPassthroughMode + centralised LogicType catalogue

- `Transformer` gets a writable `LogicPassthroughMode` logic slot (`StationeersPlus.Shared.LogicTypeNumbers.LogicPassthroughMode`, ushort 6577). Per-Transformer override stored in `PassthroughModeStore`; defaults to 1 for `StructureTransformerSmall` + `StructureTransformerSmallReversed`, 0 for every other transformer prefab. Persists across save / load via an XML side-car in the save ZIP.
- Power Grid Plus joins the central `Patterns/Logic/` catalogue: `Patterns/Logic/LogicTypeNumbers.cs` is linked into the csproj; the integer 6577 lives there, not in `LogicTypeRegistry.cs`. The catalogue's reservation rules and the table of every SixFive7 LogicType assignment live in `Patterns/Logic/README.md`.
- Direction A vs B vs C for the Transformer-logic-transparency feature was decided: option B (per-mod patch on `CableNetwork.RefreshPowerAndDataDeviceLists`, no need to implement `ITransmitDataNetworkDevices` / `IReceiveDataNetworkDevices`). Symmetric: the bridging device acts as both transmitter and receiver against itself. The transformer's own logic ports (Setting, PowerActual) are visible from both sides because the transformer sits in BOTH networks' `DeviceList` already (one cable connection per side).

---

## 9. Config surface (current state)

All settings server-authoritative.

| Section | Setting | Default | Effect |
|---|---|---|---|
| Server - Cable Simulation | Cable Burn Factor | `1.0` | Scales per-tick cable burn chance. `0.0` disables gradual burnout. |
| Server - Cable Simulation | Enable Unlimited Super-Heavy Cables | `true` | Super-heavy cable never burns. |
| Server - Cable Simulation | Enable Recursive Network Limits | `false` | Restores the vanilla force-burn check for looped networks. |
| Server - Cable Costs | Super-Heavy Cable Cost Multiplier | `2.0` | Multiplies the super-heavy cable coil recipe cost. `1.0` = vanilla. |
| Server - Voltage Tiers | Enable Voltage Tiers | `true` | The three cable tiers are separate voltages; mixing them burns the lower-tier cable; per-device tier rules. |
| Server - Voltage Tiers | Extra Heavy-Cable Devices | (empty) | Comma-separated PrefabName list of extra devices allowed on heavy cable. |
| Server - Batteries | Enable Battery Limits | `true` | Charge / discharge-rate limit stationary batteries. |
| Server - Batteries | Max Battery Charge Rate | `0.002` | Max charge per tick, as a fraction of capacity. |
| Server - Batteries | Max Battery Discharge Rate | `0.007` | Max discharge per tick, as a fraction of capacity. |
| Server - Batteries | Battery Charge Efficiency | `1.0` | Fraction of incoming power stored. |
| Server - Batteries | Enable Battery Logic Additions | `true` | Expose ImportQuantity / ExportQuantity as logic values. |
| Server - Transformers | Enable Transformer Exploit Mitigation | `true` | Close the transformer free-power exploit. |
| Server - Transformers | Enable Transformer Logic Additions | `true` | Expose PowerActual as a logic value. |
| Server - Transformers | Enable Transformer Logic Passthrough | `true` | Master kill-switch over per-Transformer `LogicPassthroughMode`. |
| Server - Area Power Control | Enable APC Power Fix | `true` | Close the APC power leak and idle battery drain. |
| Server - Area Power Control | Enable APC Logic Passthrough | `true` | APCs are logic-transparent (logic readers on either side see the other side's devices). |

Section / key strings are the entry identity in BepInEx; renames re-seed the value at next launch.

---

## 10. Pure-patch invariants

- **No new prefabs.** The mod's only added content is the `GameData/cable-recipes.xml` overlay. Every patch targets a vanilla type. Removing the DLL from a save preserves every existing thing in the world.
- **Side-car XMLs are skipped by vanilla.** `pwrgridplus-passthrough.xml` and similar entries inside the save ZIP are unknown to the vanilla loader; uninstalling the mod and saving once rebuilds the ZIP with only the five known entries. The mod's per-Transformer overrides are forgotten silently; the transformers themselves revert to vanilla logic-opaque behaviour.
- **Multiplayer is required.** `MOD.Networking.Required = true`. Clients without the matching DLL are rejected at join time. Re-Volt itself swaps the `PowerTick` per network the same way; running both is not supported (see Appendix B for the compat table).

---

## 11. Licensing and attribution

This mod is a derivative of Re-Volt (https://github.com/sukasa/revolt), which is under the **MIT License, Copyright (c) 2025 Sukasa**. MIT permits copy, modify, trim, relicense, and ship, with one obligation: the MIT copyright + permission notice must travel with any substantial portion of the original code.

Therefore:

- Files ported substantially from Re-Volt (`PowerGridTick.cs`, the transformer / battery / APC / device / cable-network / power-tick patches) carry a short header crediting Sukasa.
- The repo-root `NOTICE` names SixFive7 and the project; the repo `LICENSE` is Apache 2.0; the README ends with a `## Credits` section linking the Re-Volt repo. The `About.xml` `<Description>` mirrors the credits and the `[h2]License[/h2]` section.
- Original code (NEW-1 / NEW-2 / NEW-3, LogicPassthroughMode, the renamed glue, config) is **Apache 2.0** per the repo standard. The MIT-covered ported portions remain MIT regardless of the surrounding Apache 2.0 (dual provenance, which Apache and MIT both permit).
- Clean reimplementations of Re-Volt mechanics (as opposed to copies) carry no MIT-notice obligation; mechanics are not copyrightable. Where copied, attribution; where rewritten, courtesy.

---

## 12. Open questions

Items not yet resolved either way. Implementation backlog (decided but not yet built) lives in `TODO.md`; this section is for genuinely unresolved questions.

- **Heavy-cable device whitelist tuning.** The hardcoded high-draw machine list (`CarbonSequester`, `FurnaceBase`, `ArcFurnace`, `Centrifuge`, `Recycler`, `IceCrusher`, `HydraulicPipeBender`, `DeepMiner`) was assembled from community consensus; only the first three are verified-by-decompile to draw >5 kW. An InspectorPlus `types=[Device], fields=[UsedPower, MaxUsedPower, GetUsedPower(network)]` sweep on a high-load save is the way to verify or trim.
- **Real `MaxVoltage` values** for `heavy` and `superHeavy` cable prefabs. Recorded as an Open Question in `Research/GameClasses/Cable.md`.
- **Heavy and insulated cable coil prefab names.** Super-heavy is resolved (`ItemCableCoilSuperHeavy`); the heavy and insulated names are needed if the NEW-2 cost rule ever extends to them.
- **Cross-mod interaction with `MoreCables`' extra tiers.** That mod adds two cable types above the vanilla set; the voltage-tier rule would need to classify them (treat as `superHeavy`-equivalent, reject, or configurable). Not specified yet because no in-game testing has been done alongside MoreCables.
- **Stationpedia for `LogicPassthroughMode`.** Every other UI integration is wired (IC10 constants, tablet dropdown, screen syntax preview, Enum.GetName fallback); the Stationpedia page is the one missing surface. Tracked as an Implementation backlog item in `TODO.md`.

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
| 1 | Vanilla power tick replaced (`PowerTick` -> custom) 📣 | ✅ | 🔧 ♻️ | glue |
| 2 | Proportional load sharing among all power sources 📣 | ✅ | ♻️ | |
| 3 | Proportional distribution to loads 📣 | ✅ | ♻️ | |
| 4 | Gradual, probabilistic cable burnout (not instant) 📣 | ✅ | ♻️ | heavy + superHeavy cables exempted by NEW-1 |
| 5 | Sliding-window (10-20s) overheat model 🔬 | ✅ | ♻️ | |
| 6 | Weakest fuse / weakest cable fails first 📣 | ✅ | ♻️ | |
| 7 | Fuses blow instantly and always pre-empt a cable burn 📣 | ✅ | ♻️ | |
| 8 | Breaker/cable-burn trip coordination (trip all interrupting breakers, power flows one tick) 🔬 | ✅ | ❌ | no breakers |
| 9 | Recursive/looped networks allowed; vanilla check optional 📣 | ✅ | ♻️ | config `Enable Recursive Network Limits` |
| 10 | NaN-power bugfix 🔬 | ✅ | ♻️ | |
| 11 | Power-Transmitter cable-less-network bugfix 🔬 | ✅ | ♻️ | |
| 12 | `Device.AssessPower` reimplemented for the new tick 🔬 | ✅ | 🔧 ♻️ | glue |
| 13 | Provider array / `InputOutputDevices` resync (Network Analyzer stays correct) 🔬 | ✅ | ♻️ | |
| 14 | Brownouts | 🟡 | ❌ | stub only in Re-Volt; not ported |
| 15 | Device power-class classification (Lights/Doors/Atmos/Equipment/Logic/Power/Misc) 🔬 | ✅ | ❌ | dropped with the Load Center |
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
| 28 | New devices filed under "Cable" Stationpedia category 🔬 | ✅ | ❌ | no new devices |
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
| **NEW-3** | **Three-tier voltage gating** -- `normal`/`heavy`/`superHeavy` cables mutually incompatible, bridged only by transformers; per-device tier rules; reactive cable-burn enforcement | ❌ | 🆕 | config `Enable Voltage Tiers` |
| **NEW-4** | **LogicPassthroughMode** -- writable per-Transformer logic slot making transformers logic-transparent | ❌ | 🆕 | master toggle `Enable Transformer Logic Passthrough`; default 1 on small transformer + reversed, 0 elsewhere; persists in save side-car |

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
| [**MoreCables**](https://steamcommunity.com/sharedfiles/filedetails/?id=3555588082) | 3555588082 | spacebuilder2020 | ~150 | 2025-12-05 | yes | distribution | Adds two new cable types (super-heavy 500 kW, super-conductor 1 MW); lets all cable voltages be reconfigured. | 🟧 Directly relevant: Power Grid Plus's heavy-cable tier logic must know whether MoreCables' tiers count as "backbone". Coexistence needs an explicit decision (treat their tiers as backbone? as `normal`? configurable?). |
| [**CableTypeSwitcher**](https://steamcommunity.com/sharedfiles/filedetails/?id=3470776044) | 3470776044 | Cihla_cz | ~58 | 2025-04-26 | yes | distribution / qol | `CABLETYPESWITCH` console command: inspect a cable network, convert it between `normal` / `heavy` / `super-heavy`, split long super-heavy runs. | 🟧 Converts whole networks between tiers at runtime -- bypasses NEW-3's build-time gate. NEW-3's reactive burn-on-flow needs to fire after this too, or document the interaction. |
| [**EL Switche**](https://steamcommunity.com/sharedfiles/filedetails/?id=3287183705) | 3287183705 | Kastuk | ~295 | 2025-10-17 | yes | distribution | Logic-controlled "switch" that spawns/removes cables (normal + heavy) in place to join or split power+data networks on a click. | 🟧 Programmatically creates cables -- could create heavy cables adjacent to devices, bypassing NEW-3's build-time gate. The reactive burn-on-flow needs to cover spawned cables too. |
| [**NetworkUpgrader**](https://steamcommunity.com/sharedfiles/filedetails/?id=3656955459) | 3656955459 | Jacksonthemaster | ~320 | 2026-01-31 | yes | qol / distribution | `upgrade pipes\|cables\|chutes\|all` console command: replaces chains of 3+ straight super-heavy cables/pipes/chutes with their long variants, preserving colors. | 🟩 Mesh/length consolidation only; doesn't change tier or topology. Orthogonal. |
| [**Network Painter**](https://steamcommunity.com/sharedfiles/filedetails/?id=2876605527) | 2876605527 | Elmo | ~5,880 | 2024-12-14 | yes | qol | Spray-painting a cable repaints the whole cable (pipe/chute) network; shift = single, ctrl = checkered. | 🟩 Pure cosmetic. Orthogonal. (Note: this repo's own Spray Paint Plus is in the same space.) |
| [**Battery Backup Light**](https://steamcommunity.com/sharedfiles/filedetails/?id=3569109044) | 3569109044 | alliephante | ~1,510 | 2026-02-23 | yes | distribution / qol | Wall Light Battery becomes a true emergency light: turns on when the grid loses power, runs off its cell; Mode 1 reverts to vanilla. | 🟩 Hooks one device's `OnPowerTick`; reads network power state. Coexists with Power Grid Plus (both read the same `CableNetwork.RequiredLoad`-style state). Low risk. |
| [**"Better" Battery Light**](https://steamcommunity.com/sharedfiles/filedetails/?id=3491770665) | 3491770665 | TrippleTrip | ~23 | 2025-06-01 | yes | qol / logic | Battery wall light turns yellow below 99.5% cell (unlocked) or on when grid wattage < 100 W (locked). | 🟩 Same niche as above; orthogonal. |
| [**Omni Transmitter Settings**](https://steamcommunity.com/sharedfiles/filedetails/?id=3643534844) | 3643534844 | RogerWaters | ~155 | 2026-01-10 | yes | wireless / balance | Reconfigures the vanilla Omni Transmitter: max range, distance falloff, min-power-per-battery floor; multiplayer / dedicated aware. | 🟩 Wireless-power tuning; orthogonal to wired-power overhaul. |
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
- The mods Power Grid Plus must explicitly account for: **Re-Volt** (PowerTick swap -- detect for NEW-1), **MoreCables** (extra cable tiers), **CableTypeSwitcher** / **EL Switche** (programmatic cable creation/conversion -- bypass NEW-3's build-time gate; reactive burn-on-flow must cover them), and **MorePowerMod** / big-battery mods (inherit Power Grid Plus's battery rate limits if enabled -- document, can't single them out).
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
| N | New Power-Grid-Plus-only feature (unlimited super-heavy cable / super-heavy cost / three-tier voltage gating / LogicPassthroughMode) |

| Mod | A | B | C | D | E | F | G | H | I | J | K | L | M | N |
|---|:-:|:-:|:-:|:-:|:-:|:-:|:-:|:-:|:-:|:-:|:-:|:-:|:-:|:-:|
| [Re-Volt](https://steamcommunity.com/sharedfiles/filedetails/?id=3587239682) | + | + | + | + | . | . | . | . | . | . | . | . | + | . |
| **Power Grid Plus** | + | + | + | (recursive only) | . | . | . | . | . | . | + | . | . | + |
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
- Column N is the Power-Grid-Plus-only features.
- For the per-feature detail of the A-D space (the 48 Re-Volt features), see Appendix A. For each mod's full feature bullets and the conflict / compat assessment, see Appendix B.
