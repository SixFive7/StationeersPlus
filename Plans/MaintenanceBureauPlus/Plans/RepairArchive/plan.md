# StationeersPlus Repair Mod - Complete Design & Research Document

> **Purpose of this file:** This document captures the COMPLETE state of a multi-session research and design effort for a Stationeers auto-repair mod. It is written to be self-contained: an agent or developer reading only this file should be able to resume work from exactly where we stopped, with no context loss.
>
> **Last updated:** 2026-04-15
> **Status:** Research complete. Ready to begin implementation. Several open decisions remain (see Section 11).
>
> **Note on drained sections:** During the Phase 5 research migration (2026-04-XX), game-internals and reusable-pattern content was lifted from this plan to central pages under `Research/`. Drained sections below keep their original headings but the body is replaced by a short summary plus a pointer list. Implementation-strategy content (mod concepts, open decisions, roadmap, appendices B and C) remains in place. This doc is still usable as an implementation brief; for durable game-internals facts, follow the pointer links.

---

## Table of Contents

1. [Problem Statement](#1-problem-statement)
2. [Stationeers Save Format](#2-stationeers-save-format)
3. [Damage System Internals](#3-damage-system-internals)
4. [Repair Mechanics (Vanilla Game)](#4-repair-mechanics-vanilla-game)
5. [Existing Mod Landscape](#5-existing-mod-landscape)
6. [Mod Concept: BCSI Damage Tax](#6-mod-concept-bcsi-damage-tax)
7. [Mod Concept: Scheduled Maintenance Protocol](#7-mod-concept-scheduled-maintenance-protocol)
8. [Technical Architecture](#8-technical-architecture)
9. [Prefab Cloning Pattern (Mirrored Devices Deep Dive)](#9-prefab-cloning-pattern-mirrored-devices-deep-dive)
10. [Legal 3D Asset Sources](#10-legal-3d-asset-sources)
11. [Open Decisions](#11-open-decisions)
12. [Dropped Options & Dead Ends](#12-dropped-options--dead-ends)
13. [Reference Mods & Code Patterns](#13-reference-mods--code-patterns)
14. [Stationeers Modding Framework Reference](#14-stationeers-modding-framework-reference)
15. [Top Mods Analysis Summary](#15-top-mods-analysis-summary)
16. [Implementation Roadmap](#16-implementation-roadmap)
17. [Appendix A: Reusable Prefab Cloning Template](#appendix-a-reusable-prefab-cloning-template)
18. [Appendix B: Inspector Personality Samples](#appendix-b-inspector-personality-samples)
19. [Appendix C: All 10 Original Concepts](#appendix-c-all-10-original-concepts)

---

## 1. Problem Statement

### The Core Gameplay Pain

When there is an accident in Stationeers (overpressure event, storm, electrical overload), many structures in a room take minor damage simultaneously. A single overpressure event can damage every pipe, cable, wall, and device in a large room.

**The tedium chain:**
1. Half-broken things emit noise and generate constant popup warnings
2. Repairing each item individually requires walking up to it, holding duct tape or a welder, waiting
3. For a room with 50+ slightly damaged pipes and cables, this takes 10-30 minutes of pure tedium
4. The resources consumed (a few grams of iron for duct tape) are trivial -- the cost is entirely player time
5. Because it's so tedious, most players just ignore the damage
6. But ignoring it means constant noise/popups degrading the experience

**What the mod should do:** Provide a mechanism that automatically repairs minor structural damage, consuming appropriate resources. Major damage (above a threshold) still requires manual intervention.

**Multiplayer requirement:** The mod must work in multiplayer. Stationeers uses a server-authoritative model. Gameplay mods that only modify values can be server-only.

---

## 2. Stationeers Save Format

Stationeers `.save` files are ZIP archives containing `world_meta.xml`, `world.xml`, `terrain.dat`, and preview images. Save-edit work that targets damage must operate on `world.xml` inside the ZIP, and the test save ("Lunar", day 46, 1212 things, 2 rooms, 24 pipe networks, 9 cable networks) is the reference used throughout this plan.

Full content moved to:
- [SaveFileStructure](../../Research/Protocols/SaveFileStructure.md) - Byte-level layout of the save ZIP: file inventory, `world_meta.xml` schema, and the world.xml / terrain.dat ordering.

### Critical Lesson: DamageState vs Atmosphere Fields

The XML carries `<Oxygen>`, `<Hydration>`, `<Stun>`, etc. under three different parents (`<DamageState>` on Things, `<AtmosphereSaveData>` on rooms, and directly on player entities for vital stats), so a naive regex that zeros all `<Oxygen>` tags will remove breathable air and kill players. Safe save-edit requires a context-aware parser matching only inside `<DamageState>` blocks.

Full content moved to:
- [DamageState](../../Research/GameSystems/DamageState.md) - DamageState vs Atmosphere vs vital-stat disambiguation, save-edit safety rules, and the Python regex pattern for zeroing damage inside `<DamageState>` blocks only.

---

## 3. Damage System Internals

### DamageState Fields

Every `Thing` carries a `DamageState` with nine writable float fields (`Brute`, `Burn`, `Oxygen`, `Hydration`, `Starvation`, `Toxic`, `Radiation`, `Stun`, `Decay`) plus `MaxDamage`, all writable from mod code as `thing.DamageState.Brute = 0f` and similar.

Full content moved to:
- [DamageState](../../Research/GameSystems/DamageState.md) - Full writable-field inventory with types and descriptions, code examples for reading and writing, and the `thing.ThingHealth` overall-health property.

### What Had Non-Zero Damage in User's Save (118 values)

The reference save's 118 non-zero damage values are dominated by brute on iron walls and windows (around 40 entries, 10-27 damage), burn on cables (6 entries, 5-9), and two pipe segments at 37-58 brute. Useful as a realistic scale test for the repair loop.

Full content moved to:
- [DamageState](../../Research/GameSystems/DamageState.md) - "Typical observed values" subsection: the per-thing-type damage distribution from the reference save, as a scale sanity-check for repair-loop design.

---

## 4. Repair Mechanics (Vanilla Game)

Vanilla repair has two paths: duct tape for items / devices / suits (consumes a small amount of tape scaled to damage, 1-2g iron to craft), and build-state repair for walls / frames / pipes / cables (re-apply the original construction material with a welder or arc welder). Unrepairable items (AIMEe, Rover Mk I) must be fully replaced; fully destroyed wreckage needs angle-grinder removal and rebuild.

The key design insight for the mod: vanilla repair costs almost no resources, the real cost is player time. An auto-repair mod removes tedium, not meaningful resource decisions.

Full content moved to:
- [RepairMechanics](../../Research/GameSystems/RepairMechanics.md) - Duct-tape mechanics, build-state repair flow, unrepairable-item list, welder and arc-welder fuel details, and the "cost is player time, not resources" design observation.

---

## 5. Existing Mod Landscape

As of 2026-04, no Stationeers mod provides automatic passive structural damage repair; this is a gap in the ecosystem. Adjacent damage mods exist (XRepairsInOne, Configurable Storms, Re-Volt, Perishable Items) but none target the auto-repair use case. Stationeers has no Steam achievement system or save integrity checks.

Full content moved to:
- [ThirdPartyModIdentities](../../Research/GameSystems/ThirdPartyModIdentities.md) - Damage-mod-ecosystem survey section: mod names, workshop / GitHub links, and what each mod actually does.

---

## 6. Mod Concept: BCSI Damage Tax

### Lore

The **Bureau of Colonial Structural Integrity** (BCSI) is a bureaucratic arm of the Stationeers Colonial Authority. Early colonial outposts had a habit of catastrophically decompressing, and dead colonists don't pay taxes. Rather than send qualified engineers, the Authority developed an unmanned orbital maintenance relay: a satellite constellation that scans outpost structures via radar tomography, identifies micro-fractures, and beams down targeted molecular repair pulses.

To activate the service, colonists establish an uplink via their communications array and file Form CSI-7734. The Bureau assigns a **Compliance Inspector** -- an AI persona that monitors the station remotely. The Inspector has opinions. The Inspector shares them.

The service costs raw materials -- iron, copper, gold -- automatically requisitioned from storage. If you can't pay, the Bureau doesn't repair.

Nobody likes the Bureau. Everyone uses the Bureau.

### Inspector Personality

The Inspector is the voice of the mod. It speaks through chat messages. It has a name randomly assigned on first activation from a list: "Inspector Voss," "Inspector Huang," "Inspector Delacroix," etc. Its tone is dry, mildly condescending, occasionally impressed against its will.

(See Appendix B for full message examples.)

### Game Loop

1. **Activation:** Player builds/places the repair device. Interacts to "Subscribe to BCSI." Toggle on/off.
2. **Daily Scan:** Every in-game day, iterate all structures. Tally total damage across all DamageState fields.
3. **Cost Calculation:** Each damage point costs configured amount of materials. Primary currency matches the damaged structure's material.
4. **Material Requisition:** Search all connected storage for required materials. If found, consume. If partial, partial repair (worst first). If none, no repair + snarky message.
5. **Repair Application:** Damage values reduced proportionally. Minor damage (below threshold, default 50%) fully repaired. Major damage reduced but not fully fixed.
6. **Report:** Chat message summarizing repairs, cost, and Inspector commentary.

### Cost Structure

| Damage Type | Material | Rate |
|---|---|---|
| Brute on iron structures | Iron Ingot | 1g per 10 damage |
| Brute on steel structures | Steel Ingot | 1g per 10 damage |
| Burn (cables) | Copper Ingot | 1g per 10 damage |
| Burn (other) | Iron Ingot | 1g per 10 damage |
| Toxic / Radiation | Gold Ingot | 1g per 20 damage |
| Any (fallback) | Iron Ingot | 1g per 10 damage |

**Caps:** Only repairs below severity threshold (default 50%). Minimum invoice: 1g. All rates configurable.

### Config Schema

```ini
[BCSI Settings]
Enabled = true
RepairThreshold = 50          # Max % damage the Bureau will repair (0-100)
CostMultiplier = 1.0          # Scale all costs
ScanIntervalDays = 1          # Scan frequency (game days)
InspectorName = random        # Or set specific name
VerboseReports = true         # Detailed vs summary messages
SarcasticMode = true          # Inspector personality on/off
```

---

## 7. Mod Concept: Scheduled Maintenance Protocol

### Lore

Every Stationeers outpost ships with a **Maintenance Override Protocol** buried in the station computer's firmware -- a relic from when outposts were crewed by trained engineers. The protocol sends low-voltage diagnostic pulses through cable and pipe networks, identifies micro-damage via impedance changes, then applies localized resistive heating to anneal stress fractures. For walls/frames, embedded piezoelectric actuators are activated.

The catch: it consumes power (a lot), needs raw materials in designated storage, and the sweep takes time.

### Game Loop

1. **Activation:** Console command (`/maintenance`) or device interaction. Initiates a Maintenance Cycle.
2. **Diagnostic Sweep (Phase 1):** Scan all structures, produce damage report to chat:
   ```
   -- MAINTENANCE PROTOCOL: Diagnostic sweep complete --
   Structures: 14 damaged (8 minor, 4 moderate, 2 severe)
   Cables: 6 damaged (6 minor)
   Pipes: 2 damaged (1 minor, 1 moderate)
   Estimated material cost: 12g Iron, 4g Copper
   Estimated power cost: 8.4 kW over 1 hour
   Estimated time: 1h 15m
   Stage materials and type /maintenance confirm
   ```
3. **Confirmation:** Player reviews, ensures materials available, confirms.
4. **Repair Cycle (Phase 2):** Repairs spread over configurable game-time period. During this:
   - Continuous power draw (configurable kW)
   - Materials consumed from designated storage
   - Damage ticks down gradually
   - Progress messages at milestones (25%, 50%, 75%, 100%)
5. **Priority order:** Pipes first (atmosphere containment), cables second (power), structures third, devices last. Within each: worst damage first.
6. **Interruption handling:** If power drops, cycle pauses (not cancels). If materials run out, complete what's possible, report remainder.
7. **Cooldown:** Configurable (default 1 game day) between cycles.

### Cost Structure

| Resource | Rate |
|---|---|
| Electrical power | 2 kW continuous during cycle |
| Iron Ingot | 1g per 15 damage |
| Copper Ingot | 1g per 15 damage |
| Steel Ingot | 1g per 15 damage |
| Duct Tape (optional bonus) | If present, reduces ingot cost by 50%. 1 tape per 30 damage. |

### Config Schema

```ini
[Maintenance Protocol Settings]
Enabled = true
RepairThreshold = 50          # Max % damage to repair
CostMultiplier = 1.0          # Scale material costs
PowerDrawKW = 2.0             # Power draw during cycle
CooldownDays = 1              # Min days between cycles
RepairSpeedMultiplier = 1.0   # Repair speed (1.0 ~ 1 game hour typical)
RequireConfirmation = true    # Two-step activation
Priority = pipes,cables,structures,devices
```

---

## 8. Technical Architecture

The mod targets BepInEx 5.4.21+ (x64 Mono) with HarmonyX 2.x, StationeersLaunchPad, and .NET Framework 4.5.2 against Unity 2021.2.x. Required game assemblies (`Assembly-CSharp`, `UnityEngine.*`, `com.unity.multiplayer-hlapi.Runtime`) and BepInEx core (`0Harmony`, `BepInEx`) are referenced via `$(StationeersPath)` per the monorepo's build rules.

Full content moved to:
- [ModProjectSetup](../../Research/Workflows/ModProjectSetup.md) - Framework stack, assembly-reference recipe, `Directory.Build.props` inheritance, and the `$(StationeersPath)` externalization convention.

### Object Hierarchy

`Thing` is the base of every game object; `DynamicThing` branches to `Item` and `Entity`, and `Structure` branches to `LargeStructure` (2m grid) and `SmallGrid` (0.5m grid, with `Device` underneath). Any repair-mod iterator needs to know which branch covers which candidate.

Full content moved to:
- [Thing](../../Research/GameClasses/Thing.md) - Class-hierarchy diagram (Thing / DynamicThing / Item / Entity / Structure / LargeStructure / SmallGrid / Device) with the grid-size notes.

### Iterating Things

`Thing.AllThings`, `Structure.AllStructures`, and `Device.AllDevices` are the static collections a periodic scan walks; `Thing.TryFind(referenceId, out var thing)` looks up by ID.

Full content moved to:
- [PooledSpanEnumeration](../../Research/Patterns/PooledSpanEnumeration.md) - Safe iteration patterns for the `AllThings` / `AllStructures` / `AllDevices` collections, including the known `Device.AllDevices` duplicates trap.

### Damage Access

```csharp
thing.DamageState.Brute = 0f;
thing.DamageState.Burn = 0f;
thing.DamageState.Toxic += 0.001f;
```

Full field inventory and semantics live on the central `DamageState` page already cited from Section 3.

### Atmosphere, Pipe, Cable, Logic APIs

The mod reads `AtmosphericsManager.AllAtmospheres` (may contain nulls), `CableNetwork.AllCableNetworks`, `PipeNetwork.AllPipeNetworks`, and the `Device.GetLogicValue` / `SetLogicValue` / `CanLogicRead` / `CanLogicWrite` quartet for logic channels. Game-state guards (`GameManager.IsServer`, `WorldManager.IsPaused`, `WorldManager.Instance.GameMode`) fence the repair loop to the right conditions.

Full content moved to:
- [WorldStateAPIs](../../Research/GameSystems/WorldStateAPIs.md) - Public APIs for atmospheres, pipe networks, cable networks, and the logic-channel quartet, plus the canonical game-state guard snippet.

### Multiplayer Architecture

Stationeers is server-authoritative. Gameplay-only patches run server-side (`if (!GameManager.IsServer) return;`); mods that add custom prefabs or assets must ship to clients too. `PowerTick` runs on a background thread, so the repair loop needs a main-thread dispatcher if it touches Unity APIs.

Full content moved to:
- [ServerAuthoritativeSimulation](../../Research/Patterns/ServerAuthoritativeSimulation.md) - Server / client split, the `!GameManager.IsServer` guard pattern, and the rule for when a mod must install on both sides.

### Periodic Processing Options

Three options: patch `DynamicThing.Update()` (per-frame per-thing, as PerishableItems does), a timer inside the plugin's `Update()`, or a coroutine with `WaitForSeconds`.

Full content moved to:
- [PeriodicProcessing](../../Research/Patterns/PeriodicProcessing.md) - Three periodic-processing options with code examples and trade-offs for each (update-patch, plugin timer, coroutine).

### Save/Load for Cloned Prefabs

Stationeers saves reference things by `PrefabHash` (an integer). `Animator.StringToHash(name)` is deterministic, so cloned prefabs with stable names survive save/load automatically. If the mod is removed, unknown hashes fail to load.

Full content moved to:
- [PrefabCloning](../../Research/Patterns/PrefabCloning.md) - "PrefabHash stability" subsection: why `Animator.StringToHash` is safe to use, what happens when the mod is uninstalled, and why no custom save-data type is needed for identity-only clones.

---

## 9. Prefab Cloning Pattern (Mirrored Devices Deep Dive)

The Mirrored Devices mod (Apolo / Vincent Charpentier) is the canonical example of runtime whole-prefab cloning in Stationeers: 3 C# files, ~950 lines. It installs three Harmony patches (`Prefab.LoadAll` prefix to clone, `Localization.LanguageFolder.LoadAll` prefix for names, `InventoryManager.SetupConstructionCursors` postfix for the placement cursor), uses a hidden `DontDestroyOnLoad` parent for clones, handles `Constructor` / `MultiConstructor` conversion, and carries MirrorDefinition as the declarative per-device shape.

For the repair mod, the full recipe (HiddenParent setup, `FindMirrorInfos`, `ConvertConstructorToMultiConstructor`, `CreateMirroredThing`, `AddToConstructor`, localization hook, free crafting recipes, key takeaways) is the single largest re-use target. Appendix A contains the reusable C# template derived from this analysis.

Full content moved to:
- [PrefabCloning](../../Research/Patterns/PrefabCloning.md) - Complete recipe: Mirrored Devices three-patch structure, HiddenParent, `FindMirrorInfos` + `ConvertConstructorToMultiConstructor`, `CreateMirroredThing` core Instantiate-rename-register flow, `AddToConstructor` + MirrorDefinition class, PrefabHash stability, free crafting recipes, and key takeaways.
- [Localization](../../Research/GameSystems/Localization.md) - How to register clone names via a `Localization.LanguageFolder.LoadAll` prefix without any XML edits.

---

## 10. Legal 3D Asset Sources

### Context

If we want to ship a custom 3D model (rather than cloning a vanilla prefab's appearance), we need legally reusable assets. Most Stationeers mods have NO license (= all rights reserved).

### MIT-Licensed Assets (Can Use With Attribution)

| Source | License | Assets | Best For |
|---|---|---|---|
| **Re-Volt** (sukasa/revolt) | MIT (2025 Sukasa) | .blend + .fbx: CircuitBreakerHeavy, CircuitBreakerSmall, LoadCenter, Outlets. **CircuitBreakerSmart_LitInfoPanel.fbx** = lit display screen. | Wall-mounted device with screen. Best candidate. |
| **Hardwired** (stbowers/Hardwired) | MIT (2025 ilodev) | .blend + .fbx: Transformer, VoltageSource, Capacitor, etc. | Professional wall-mounted device boxes |
| **LogicMemory16** (DesignStreaks/Stationeers-LogicMemory16) | MIT (2026 lorexcold) | .fbx only: LogicalMemory16.fbx (wall-mounted data device with buttons) | Small logic terminal |
| **ModdingTools** (StationeersModding/StationeersModdingTools) | MIT (2025 ilodev) | .obj: ActivateButton, Knob, LeverOpen, SettingWheel, SlidingPanel, SwitchOnOff, ValveHandle | Interactive parts kit |

### Attribution Template
```
This mod includes 3D assets from [Mod Name] by [Author], used under the MIT License.
Copyright (c) [Year] [Author]. See [URL] for the original license text.
```

### Recommendation

For v1, clone a vanilla prefab (no custom 3D needed). If a custom look is desired later, modify Re-Volt's `CircuitBreakerSmart` .blend file (enlarge lit panel, remove breaker switch, rename to "Communications Terminal").

---

## 11. Open Decisions

### Decision 1: Which Vanilla Device to Clone?

| Candidate | Pros | Cons |
|---|---|---|
| **Area Power Controller** | Most logic channels, wall-mounted, well-known | Very commonly used -- clone might confuse |
| **Wall Heater** | On/off + power + Setting, simple box | Moderately used, atmospheric device |
| **Cable Analyser** | Rarely used, diagnostic theme fits lore | Read-only logic in vanilla, needs write patches |
| **Satellite Dish** | Fits "Bureau comms uplink" lore perfectly | Larger structure, different grid placement |
| **Gas Sensor** | Small, wall-mounted, has logic | Very commonly used |

**Current lean:** Cable Analyser (rare use, diagnostic theme) or Area Power Controller (most logic support).

### Decision 2: Ship Both Modes or Just One?

- **Option A:** Ship BCSI (passive) only as v1. Add Maintenance Protocol as v2.
- **Option B:** Ship both in one mod with a mode toggle (via LogicType.Mode or config).
- **Current lean:** Option A (simpler v1).

### Decision 3: Cost Balancing

Current draft rates (all configurable):
- BCSI: 1g material per 10 damage points
- Protocol: 1g material per 15 damage points + 2kW power

Need playtesting to validate these feel right.

### Decision 4: Repair Threshold Default

Default 50% -- only repairs damage below this percentage of max health. Above this, player must manually repair. Is 50% the right default? Could be 30% (more conservative) or 75% (more generous).

### Decision 5: Custom 3D Model vs Vanilla Clone Appearance

- **Option A:** Clone a vanilla device, it looks identical to the original but has different name/behavior
- **Option B:** Eventually make a custom model using MIT-licensed Re-Volt assets
- **Current lean:** Option A for v1. Option B later if desired.

---

## 12. Dropped Options & Dead Ends

### Permanently Dropped

| Option | Why Dropped |
|---|---|
| **stationeers-web-display** | No license (all rights reserved). Unreleased mod. NOT to be referenced or used. |
| **CefSharp / Chromium Embedded Framework** | Massive dependency, extreme complexity, not appropriate for this use case. Dead route. |
| **Custom gas type (Nanite gas, Sealant Fog)** | Adding a new gas type requires patching ~20+ systems (CustomGasMod). Way too invasive. |
| **Custom motherboard with screen UI** | Medium-hard: requires decompiling, Unity prefab work, screen rendering pipeline. Not worth it for v1. |
| **ScriptedScreens dependency** | Adds a large dependency chain (StationeersLua + ScriptedScreens). Overkill for v1. |

### Concepts Explored But Not Selected for v1

| Concept | Status |
|---|---|
| Nanite Atmospheric Suspension (#1) | Feasible as simplified version (no custom gas). Could be v2 lore wrapper. |
| Pressurized Sealant Fog (#4) | Hard (needs custom gas). Repurposing Pollutant gas is easy but sacrifices identity. |
| Sympathetic Resonance Field (#5) | Medium-hard (custom device). Logic interference mechanic is compelling. |
| Temporal Micro-Regression (#8) | Fun but risky (side effects involve runtime thing replacement -- multiplayer bugs). |

---

## 13. Reference Mods & Code Patterns

### Primary References (Keep Open While Building)

| Mod | Use It For | Source |
|---|---|---|
| **Mirrored Devices** | Prefab cloning pattern | https://github.com/VincentCharpentier/Stationeers-Mirrored-Devices |
| **Battery Backup Light** | Modify-existing-device pattern (simplest example: 2 files, ~200 lines) | https://github.com/alliephante/StationeersEmergencyBatteryLight |
| **More Gas Display Console Options** | Extend device logic with companion dictionary | https://github.com/Vespinian/stationeers-mgdco |
| **Re-Volt** | Advanced: custom devices, power system, network sync, save data | https://github.com/sukasa/revolt |
| **PerishableItems** | Damage/decay mechanics, DynamicThing.Update patching | https://github.com/ilodev/StationeersPerishableItems |
| **FPGA** | Sub-component grafting (Instantiate child from vanilla prefab) | https://github.com/tsholmes/StationeersFPGA |

### Code Pattern: Adding Logic Values to Existing Device

The Re-Volt `TransformerLogicPatch` demonstrates the universal recipe for adding custom logic channels to a vanilla device: a `[HarmonyPrefix]` on `CanLogicRead` sets `__result = true` and returns `false` to skip the original, and a parallel prefix on `GetLogicValue` reads a private field via the `____privateField` four-underscore convention. This is the shape every subsequent logic patch in this mod should follow.

Full content moved to:
- [CustomLogicValueInjection](../../Research/Patterns/CustomLogicValueInjection.md) - Re-Volt TransformerLogicPatch pattern in full: Harmony prefix shape, `__result = true` + `return false` to skip original, private-field access via parameter-name convention, and the matching `GetLogicValue` pair.

### Code Pattern: Server-Only Guard

From PerishableItems:
```csharp
if (!GameManager.IsServer || WorldManager.IsPaused ||
    WorldManager.Instance.GameMode != GameMode.Survival) return;
```

### Code Pattern: Looking Up Vanilla Prefab

From Battery Backup Light:
```csharp
[HarmonyPatch(typeof(Prefab), "LoadAll")]
[HarmonyPrefix]
private static void FindPrefab()
{
    foreach (var thing in WorldManager.Instance.SourcePrefabs.Where(t => t != null))
        if (thing.PrefabName == "StructureWallLightBattery")
            wallLightBatteryPrefab = thing;
}
```

### Code Pattern: Sub-Component Grafting

FPGA's `FPGALogicHousing.OnPrefabLoad` lifts a specific child GameObject (`OnOffNoShadow`) from a vanilla prefab and grafts it into the mod's own prefab, wiring up its `SphereCollider` and `LogicOnOffButton`. This is the alternative to whole-prefab cloning when only one part of the vanilla prefab is desired.

Full content moved to:
- [SubcomponentGrafting](../../Research/Patterns/SubcomponentGrafting.md) - FPGA sub-component grafting pattern: `PrefabUtils.FindPrefab`, `transform.Find` for the specific child, `GameObject.Instantiate` onto the mod's own prefab, and Interactable wiring.

### Code Pattern: BepInEx Config

The standard BepInEx config pattern is `Config.Bind("Section", "Key", defaultValue, "Description")`, which auto-persists to `BepInEx/Config/<plugin-guid>.cfg`. Every configurable value in the mod goes through this call.

Full content moved to:
- [ModProjectSetup](../../Research/Workflows/ModProjectSetup.md) - "Config.Bind" subsection: signature, the auto-save path, and the BepInEx config reload semantics.

---

## 14. Stationeers Modding Framework Reference

### BepInEx Plugin Template + Harmony Patch Types

The standard plugin shape is a `BaseUnityPlugin` subclass with `[BepInPlugin("guid", "name", "version")]`, an `Awake()` that stashes `Instance` and calls `new Harmony(guid).PatchAll()`. Harmony patch types are Prefix (can skip original by returning `false`), Postfix (can modify `__result`), Transpiler (IL edits), and Reverse Patch (call private methods).

Full content moved to:
- [ModProjectSetup](../../Research/Workflows/ModProjectSetup.md) - "Plugin template" subsection: `BaseUnityPlugin` scaffold, `[BepInPlugin]` attribute, Harmony init order.
- [HarmonyPatchTypes](../../Research/Patterns/HarmonyPatchTypes.md) - Patch-type taxonomy (Prefix / Postfix / Transpiler / Reverse Patch) with the "return false to skip original" semantics and the `__result` / `__instance` / `____privateField` naming conventions.

### Private Field Access

Two recipes: the `AccessTools.Field(typeof(T), "_name").SetValue(instance, value)` approach for explicit reflection, and the Harmony four-underscore parameter-naming convention (`float ____privateFieldName`) for inline patch access.

Full content moved to:
- [AccessToolsRecipes](../../Research/Patterns/AccessToolsRecipes.md) - AccessTools recipe set, parameter-naming convention for private fields, and the trade-offs between explicit reflection and inline access.

### Key Singletons

- `GameManager.IsServer` / `NetworkManager.IsServer`
- `WorldManager.IsPaused`, `WorldManager.Instance.GameMode`
- `WeatherManager.Instance`
- `WorldManager.Instance.SourcePrefabs` -- master prefab list

### Known Gotchas

A catch-all of cross-cutting Stationeers modding traps: `Device.AllDevices` can contain duplicates (dedupe with a HashSet), `AtmosphericsManager.AllAtmospheres` can contain nulls, power calculations can produce NaN, the power tick runs on a background thread (do not use `UnityEngine.Random` there), and atmosphere may be null until a player logs in on dedicated servers.

Full content moved to:
- [StationeersModdingGotchas](../../Research/Patterns/StationeersModdingGotchas.md) - Known-gotchas reference: AllDevices duplicates, AllAtmospheres nulls, power NaN guards, background-thread Random trap, dedicated-server atmosphere null.

---

## 15. Top Mods Analysis Summary

We analyzed ~120 top-subscribed Stationeers mods. Key findings grouped by technique:

### Group A: Runtime Whole-Prefab Cloning (1 mod)
- **Mirrored Devices** -- The only mod using `GameObject.Instantiate` on whole vanilla prefabs. Our primary reference.

### Group B: Modify Existing Devices via Harmony (8+ mods)
- Battery Backup Light, Better Power Mod, Terraforming, Color Cycler, MesonScannerMod, Automated Hydroponics, More Gas Display Console Options, Better Active Vent Button Redux, Structure Thermodynamics

### Group C: Custom Unity Prefabs / Asset Bundles (15+ mods)
- All WIKUS mods (closed source), Re-Volt, FPGA, Advanced Computing, Light Posts, Player Communications, etc.

### Group D: Sub-Component Reuse (1 mod)
- FPGA -- Instantiates a specific child GameObject from vanilla prefab into mod's own prefab.

### Group E: XML-Only (3+ mods)
- Stacked!, Jigawatt Battery, Traders Reimagined

### Key Lesson: The Dominant Pattern

For "new device without 3D models": there IS no dominant pattern because almost nobody does it. Mirrored Devices is the sole example. Most mods either ship custom Unity assets (Group C) or patch existing behavior (Group B). Our approach (clone prefab + add behavior) is novel in the ecosystem.

---

## 16. Implementation Roadmap

### v1: BCSI Damage Tax (Passive Auto-Repair)

Seven-step implementation plan: scaffold the BepInEx plugin, clone a vanilla device via the Mirrored Devices recipe, add logic channels for On / Setting / Mode / Ratio / Quantity, implement the periodic scan-and-repair loop, write the Inspector chat-message system, wire the BepInEx config, and iterate testing (single-player, dedicated-server multiplayer, save/load cycles). Rough estimate: 11 hours of build time plus ongoing test rounds.

Full content moved to:
- [BCSIImplementationRoadmap](../../Research/Workflows/BCSIImplementationRoadmap.md) - Seven-step v1 roadmap with per-step time estimates and the "which reference mod to keep open for this step" annotations.

### v2: Maintenance Protocol (Active Repair)

- Add console command or mode toggle
- Phase 1 diagnostic scan with cost estimation
- Phase 2 gradual repair with power draw
- Progress messages
- Cooldown system

### v3: Polish

- Custom 3D model (modify Re-Volt SmartCircuitBreaker .blend)
- Multiple language support
- Stationpedia integration
- Steam Workshop publishing

---

## Appendix A: Reusable Prefab Cloning Template

Complete C# template extracted from Mirrored Devices analysis. This can be used as the starting scaffold for the repair mod.

```csharp
using System;
using System.Collections.Generic;
using System.Reflection;
using Assets.Scripts;
using Assets.Scripts.Inventory;
using Assets.Scripts.Objects;
using Assets.Scripts.Objects.Items;
using BepInEx;
using HarmonyLib;
using JetBrains.Annotations;
using UnityEngine;

namespace CloneDeviceTemplate
{
    [BepInPlugin("com.yourname.stationeers.clonedevicetemplate", "Clone Device Template", "1.0.0")]
    public class CloneDeviceTemplatePlugin : BaseUnityPlugin
    {
        public static CloneDeviceTemplatePlugin Instance;
        public void Log(string line) => Debug.Log("[CloneDeviceTemplate]: " + line);

        void Awake()
        {
            Instance = this;
            try
            {
                new Harmony("com.yourname.stationeers.clonedevicetemplate").PatchAll();
                Log("Patch succeeded");
            }
            catch (Exception e) { Log("Patch failed: " + e); }
        }
    }

    public class CloneDefinition
    {
        public string sourceDeviceName;
        public string cloneSuffix;
        public string cloneDisplayNameSuffix;
        public Action<Thing> onClonedDeviceCreated;

        public string cloneName { get; private set; }
        public int cloneHash { get; private set; }
        public Thing sourceThing;
        public MultiConstructor targetConstructor;

        public CloneDefinition(string sourceDeviceName, string cloneSuffix)
        {
            this.sourceDeviceName = sourceDeviceName;
            this.cloneSuffix = cloneSuffix;
            this.cloneName = sourceDeviceName + cloneSuffix;
            this.cloneHash = Animator.StringToHash(this.cloneName);
            this.cloneDisplayNameSuffix = $" ({cloneSuffix})";
        }
    }

    [HarmonyPatch]
    public static class ClonePrefabPatch
    {
        private static readonly CloneDefinition[] Clones = new[]
        {
            new CloneDefinition("StructureAreaPowerControl", "Maintenance"),
            // Add more here
        };

        private static readonly GameObject HiddenParent = new GameObject("~ClonedDeviceHiddenParent");

        private static void Log(string m) => CloneDeviceTemplatePlugin.Instance.Log(m);

        [HarmonyPatch(typeof(Prefab), "LoadAll")]
        [HarmonyPrefix]
        [UsedImplicitly]
        private static void LoadClonePrefabs()
        {
            UnityEngine.Object.DontDestroyOnLoad(HiddenParent);
            HiddenParent.SetActive(false);
            FindSources();

            foreach (var def in Clones)
            {
                if (def.sourceThing == null) { Log($"Source not found: {def.sourceDeviceName}"); continue; }
                CloneAndRegister(def);
            }
        }

        private static void FindSources()
        {
            var ctorsToUpgrade = new List<(CloneDefinition def, Constructor ctor, int idx)>();
            var sourcePrefabs = WorldManager.Instance.SourcePrefabs;

            for (int i = 0; i < sourcePrefabs.Count; i++)
            {
                var thing = sourcePrefabs[i];
                if (thing == null) continue;

                var multiCtor = thing.GetComponent<MultiConstructor>();
                var singleCtor = thing.GetComponent<Constructor>();

                if (multiCtor != null && multiCtor.Constructables != null)
                {
                    foreach (var def in Clones)
                        if (multiCtor.Constructables.Find(p => p != null && p.name == def.sourceDeviceName) != null)
                            def.targetConstructor = multiCtor;
                }
                else if (singleCtor != null)
                {
                    foreach (var def in Clones)
                        if (singleCtor.BuildStructure != null && singleCtor.BuildStructure.name == def.sourceDeviceName)
                        { ctorsToUpgrade.Add((def, singleCtor, i)); break; }
                }
                else
                {
                    foreach (var def in Clones)
                        if (thing.name == def.sourceDeviceName) { def.sourceThing = thing; break; }
                }
            }

            foreach (var (def, ctor, idx) in ctorsToUpgrade)
            {
                var mctor = UpgradeConstructor(ctor, idx);
                def.targetConstructor = mctor;
            }
        }

        private static MultiConstructor UpgradeConstructor(Constructor ctor, int prefabIndex)
        {
            var buildStructure = ctor.BuildStructure;
            var mctor = ctor.gameObject.AddComponent<MultiConstructor>();
            mctor.Constructables = new List<Structure> { buildStructure };
            CopySharedFields((Stackable)ctor, (Stackable)mctor);
            WorldManager.Instance.SourcePrefabs[prefabIndex] = mctor;
            UnityEngine.Object.DestroyImmediate(ctor);
            return mctor;
        }

        private static void CopySharedFields(Stackable source, Stackable target)
        {
            var currentType = typeof(Stackable);
            var thingType = typeof(Thing);
            while (currentType != null && thingType.IsAssignableFrom(currentType))
            {
                foreach (var field in currentType.GetFields(
                    BindingFlags.Public | BindingFlags.Instance | BindingFlags.NonPublic))
                {
                    if (field.IsNotSerialized || field.Name.Contains("<")) continue;
                    try
                    {
                        var value = field.GetValue(source);
                        if (ReferenceEquals(value, source))
                            field.SetValue(target, target);
                        else
                            field.SetValue(target, value);
                    }
                    catch { }
                }
                if (currentType == thingType) break;
                currentType = currentType.BaseType;
            }
        }

        private static Thing CloneAndRegister(CloneDefinition def)
        {
            var cloneGO = GameObject.Instantiate(def.sourceThing.gameObject, HiddenParent.transform);
            cloneGO.name = def.cloneName;

            var cloneThing = cloneGO.GetComponent<Thing>();
            cloneThing.PrefabName = def.cloneName;
            cloneThing.PrefabHash = def.cloneHash;

            if (cloneThing.Blueprint != null)
                cloneThing.Blueprint = GameObject.Instantiate(cloneThing.Blueprint, HiddenParent.transform);

            WorldManager.Instance.SourcePrefabs.Add(cloneThing);

            if (def.targetConstructor != null)
            {
                int insertAt = def.targetConstructor.Constructables.FindIndex(p => p.name == def.sourceDeviceName);
                def.targetConstructor.Constructables.Insert(insertAt + 1, cloneThing as Structure);
            }

            def.onClonedDeviceCreated?.Invoke(cloneThing);
            Log($"Cloned {def.sourceDeviceName} -> {def.cloneName} (hash {def.cloneHash})");
            return cloneThing;
        }

        [HarmonyPatch(typeof(Localization.LanguageFolder), nameof(Localization.LanguageFolder.LoadAll))]
        [HarmonyPrefix]
        [UsedImplicitly]
        private static void Localization_LoadAll_Prefix(Localization.LanguageFolder __instance)
        {
            if (__instance.Code != LanguageCode.EN) return;
            foreach (var def in Clones)
            {
                var originalName = __instance.LanguagePages[0].Things
                    .Find(x => x.Key == def.sourceDeviceName)?.Value ?? "Unknown Device";
                __instance.LanguagePages[0].Things.Add(new Localization.RecordThing
                {
                    Key = def.cloneName,
                    Value = originalName + def.cloneDisplayNameSuffix,
                    ThingDescription = $"A maintenance variant of the {{THING:{def.sourceDeviceName}}}"
                });
            }
        }
    }
}
```

---

## Appendix B: Inspector Personality Samples

### Inspector Names (randomly assigned)
Inspector Voss, Inspector Huang, Inspector Delacroix, Inspector Mwangi, Inspector Petrov, Inspector Tanaka, Inspector Okafor, Inspector Brennan, Inspector Solberg, Inspector Reyes

### Message Templates

**Daily report (low damage):**
> Inspector Voss -- Daily Report: 4 micro-fractures detected across 3 wall segments. Repair pulse applied. Cost: 2g Iron. You're almost running a competent operation.

**Daily report (heavy damage):**
> Inspector Voss -- Daily Report: 47 structural deficiencies logged. 12 cable burns. 2 pipe segments at >50% failure. Total repair cost: 18g Iron, 4g Copper. I've seen worse. Not often.

**After incident:**
> Inspector Voss -- INCIDENT ALERT: Overpressure event detected in Room 2. 31 new damage entries. The Bureau reminds you that atmospheric containment is not optional. Estimated repair cost at next cycle: 24g Iron.

**Can't pay:**
> Inspector Voss -- INVOICE OVERDUE: Insufficient materials in connected storage. 14 repairs deferred. The Bureau does not operate a charity. Please deposit Iron.

**Long no-damage streak:**
> Inspector Voss -- Weekly Summary: Zero structural deficiencies for 7 consecutive days. The Bureau acknowledges your... adequacy.

**First activation:**
> You have been assigned Inspector Voss, BCSI License #4471-C. May your walls hold and your invoices be paid.

**Mod installed, not yet activated:**
> BCSI Orbital Relay detected. Build and activate a Maintenance Terminal to subscribe.

---

## Appendix C: All 10 Original Concepts

These were brainstormed as possible lore/mechanics for the mod. Numbers 1, 4, 5, 7, 8, 10 received feasibility studies. Numbers 7 and 10 were selected for implementation.

1. **Nanite Atmospheric Suspension** -- Nanite canisters released into rooms. Nanites suspended in atmosphere repair things. Need right temp/pressure/O2 conditions. Deplete over time.

2. **Self-Annealing Alloys** -- Alloys that remember crystalline structure. Self-repair only at correct temperature band. Too hot = permanent memory loss.

3. **The Tinker Bot** -- Small autonomous drone that patrols pipes/cables fixing micro-damage. Needs charging dock and repair materials.

4. **Pressurized Sealant Fog** -- Aerosolized polymer gas piped into rooms. Polymerizes on contact with micro-fractures. Side effects: degrades filters, tints windows, mildly toxic.

5. **Sympathetic Resonance Field** -- EM device vibrates micro-fractures back into alignment. Interferes with logic circuits while active. Strategic on/off.

6. **Mycelial Hull Coating** -- Bioengineered fungus feeds on CO2, fills micro-cracks. If conditions too good, overgrows and clogs vents.

7. **Scheduled Maintenance Protocol** -- Station computer firmware sends diagnostic pulses through networks. Player-initiated, consumes power + materials, takes game-time. **SELECTED FOR IMPLEMENTATION.**

8. **Temporal Micro-Regression Field** -- Localized temporal distortion reverts matter to previous state. Random side effects (reset device settings, un-smelt ingots, revert plants).

9. **Piezoelectric Self-Repair Circuit** -- Network vibrations generate trickle power for molecular actuators. More active = faster healing. Elegant feedback loop.

10. **The Damage Tax (Insurance Bureau)** -- Bureaucratic AI auto-repairs daily, charges materials, sends passive-aggressive reports. **SELECTED FOR IMPLEMENTATION.**

---

## End of Document

This document contains the complete state of research and design for the StationeersPlus Repair Mod. Durable game-internals facts have been migrated to the central `Research/` store (see section pointers above); the implementation strategy, mod concepts, open decisions, and roadmap remain here. An agent or developer can resume work by reading this file plus the linked central pages.
