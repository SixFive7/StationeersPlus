# StationeersPlus Repair Mod - Complete Design & Research Document

> **Purpose of this file:** This document captures the COMPLETE state of a multi-session research and design effort for a Stationeers auto-repair mod. It is written to be self-contained: an agent or developer reading only this file should be able to resume work from exactly where we stopped, with no context loss.
>
> **Last updated:** 2026-04-15
> **Status:** Research complete. Ready to begin implementation. Several open decisions remain (see Section 11).

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

### File Structure

Save files (e.g., `Luna.save`) are **ZIP archives** containing:

| File | Size (example) | Content |
|---|---|---|
| `world_meta.xml` | ~700 B | Save metadata: world name, version, stats |
| `world.xml` | ~2.6 MB | ALL game data: things, atmospheres, networks, players |
| `terrain.dat` | ~184 KB | Terrain/voxel data (binary) |
| `preview.png` | ~125 KB | Save thumbnail |
| `screenshot.png` | ~139 KB | Screenshot |

### World Meta Example (from user's save)

```xml
<WorldMetaData Id="af74fb5c-5fb7-466e-8269-ffa6820e7138">
  <Game>Assembly-CSharp</Game>
  <GameVersion>0.2.6228.27061</GameVersion>
  <DateTime>134201614819267958</DateTime>
  <DaysPast>46</DaysPast>
  <WorldName>Lunar</WorldName>
  <WorldFileName>Luna</WorldFileName>
  <NumberOfRooms>2</NumberOfRooms>
  <NumberOfPipeNetworks>24</NumberOfPipeNetworks>
  <NumberOfCableNetworks>9</NumberOfCableNetworks>
  <NumberOfThings>1212</NumberOfThings>
  <NumberOfAtmospheres>122</NumberOfAtmospheres>
</WorldMetaData>
```

### User's Save Details

- World: "Lunar", day 46, Normal difficulty, DefaultStartCommunity, LunarSpawnMonsArcanus
- 2 players (SteamIDs: 76561197970372584, 76561197965752767)
- 1,212 things, 2 rooms, 24 pipe networks, 9 cable networks
- One player was Unconscious (Stun=100), both had low Hydration (~5.6-5.8)

### Critical Lesson: DamageState vs Atmosphere Fields

The XML contains `<Oxygen>`, `<Hydration>`, etc. in TWO completely different contexts:

1. **Inside `<DamageState>` blocks** (on Things) -- these ARE damage values, safe to zero out:
   ```xml
   <DamageState>
     <Brute>20.70604</Brute>
     <Burn>0</Burn>
     <Oxygen>0</Oxygen>
     <Hydration>0</Hydration>
     <Starvation>0</Starvation>
     <Toxic>0</Toxic>
     <Radiation>0</Radiation>
     <Stun>0</Stun>
     <Decay>0</Decay>
   </DamageState>
   ```

2. **Inside `<AtmosphereSaveData>` blocks** -- these are GAS AMOUNTS in rooms, NOT damage:
   ```xml
   <AtmosphereSaveData>
     <Oxygen>318.45480094784614</Oxygen>
     <Nitrogen>0</Nitrogen>
     <CarbonDioxide>197.48193765921076</CarbonDioxide>
     ...
   </AtmosphereSaveData>
   ```

3. **On Player entities** -- `<Hydration>5.65211868</Hydration>` is a VITAL STAT (how thirsty), not damage.

**A naive regex that zeroes all `<Oxygen>` tags will remove breathable air from rooms and kill players.** The correct approach uses a context-aware parser (e.g., Python regex with `re.DOTALL` matching inside `<DamageState>...</DamageState>` blocks only):

```python
import re
def zero_damage(match):
    block = match.group(0)
    block = re.sub(
        r'<(Brute|Burn|Oxygen|Hydration|Starvation|Toxic|Radiation|Stun|Decay)>[^<]+</\1>',
        r'<\1>0</\1>', block)
    return block
content = re.sub(r'<DamageState>.*?</DamageState>', zero_damage, content, flags=re.DOTALL)
```

---

## 3. Damage System Internals

### DamageState Fields

Every `Thing` in Stationeers has a `DamageState` property with these writable float fields:

| Field | Type | Description |
|---|---|---|
| `Brute` | float | Physical/structural damage (impact, overpressure) |
| `Burn` | float | Thermal/electrical damage (cable overloads, fires) |
| `Oxygen` | float | Oxygen-related damage |
| `Hydration` | float | Hydration-related damage |
| `Starvation` | float | Starvation damage |
| `Toxic` | float | Toxic damage (pollutants) |
| `Radiation` | float | Radiation damage |
| `Stun` | float | Stun damage (incapacitation) |
| `Decay` | float | Decay/rot damage |
| `MaxDamage` | float | Maximum damage threshold |

### Damage in Code

Writable from mod code:
```csharp
thing.DamageState.Brute = 0f;    // Clear brute damage
thing.DamageState.Burn = 0f;     // Clear burn damage
// etc.
```

Also accessible: `thing.ThingHealth` (overall health property).

### What Had Non-Zero Damage in User's Save (118 values)

| Thing Type | Damage Type | Count | Range |
|---|---|---|---|
| `StructureWallIron` | Brute | ~25 | 10-27 |
| `StructureCompositeWindowIron` | Brute | ~15 | 11-22 |
| `StructureCableCorner/Straight/Junction` | Burn | ~6 | 5-9 |
| `StructurePipeCorner/Straight` | Brute | 2 | 37-58 |
| `OrganLungs` | Burn | 1 | 20.6 |
| Player entities | Stun | 2 | 100 (unconscious) |
| Player entities | Burn | 1 | 13.8 |

---

## 4. Repair Mechanics (Vanilla Game)

### Method 1: Duct Tape (Items & Devices)

- **Fixes:** Suits, solar panels, portable items, canisters -- anything "renamable"
- **Cost:** Tape consumed. Amount scales with damage severity.
- **Crafting:** Standard: 1g Iron (Fabricator) / 2g Iron (Tool Manufacturer). Mk II: 2g Iron + 1g Electrum
- **Usage:** Left-click and hold on damaged object (right-click for suits)
- **Suit caveat:** Repairs rupture but does NOT restore durability

### Method 2: Build-State Repair (Structures)

- **Fixes:** Walls, frames, pipes, cables
- **How:** Damage causes structures to revert to earlier build states. Re-apply original construction materials.
- **Cost:** Same materials as originally building it (iron sheets for iron walls, steel for steel, etc.)
- **Tools:** Welding Torch (fuel: 66% CH4 / 34% O2 canister) or Arc Welder (battery powered)

### Unrepairable Items

- AIMEe, Rover Mk I -- must be fully replaced
- Wreckage (fully destroyed) -- remove with Angle Grinder, rebuild from scratch

### Key Insight for Mod Design

The resources consumed by vanilla repair are trivial (a few grams of iron for duct tape). The real cost is player TIME. This means an auto-repair mod doesn't remove meaningful resource decisions -- it removes tedium. This is a strong argument for the mod's existence.

---

## 5. Existing Mod Landscape

### No Auto-Repair Mod Exists

As of 2026-04, no Stationeers mod provides automatic/passive structural damage repair. The Stationeers modding scene is small. This is a gap in the ecosystem.

### Damage-Adjacent Mods

| Mod | What It Does | Link |
|---|---|---|
| XRepairsInOne | Fixes broken weapon damage, solar panel storm damage bugs | [Steam](https://steamcommunity.com/sharedfiles/filedetails/?id=2945613328) |
| Configurable Storms (original) | Adjust/disable storm damage values | [GitHub](https://github.com/daniellovell/configurable-storms) |
| Configurable Storms (fork) | Maintained fork | [GitHub](https://github.com/Kastuk/configurable-storms-r) |
| Re-Volt | Overhauls cable damage/burnout (gradual instead of instant) | [Steam](https://steamcommunity.com/sharedfiles/filedetails/?id=3587239682) |
| Perishable Items | Food decay causes player health damage | [GitHub](https://github.com/ilodev/StationeersPerishableItems) |
| Incident: Godmode | Makes player indestructible (removed from Workshop) | [Steam](https://steamcommunity.com/sharedfiles/filedetails/?id=1871914430) |

**Note:** Stationeers has no Steam achievement system or save integrity checks. No ironman mode. Save editing and mods are consequence-free from an achievement perspective.

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

### Framework Stack

| Component | Version | Purpose |
|---|---|---|
| BepInEx | 5.4.21+ (x64, Mono) | Plugin loader |
| Harmony (HarmonyX) | 2.x (bundled with BepInEx) | Runtime method patching |
| StationeersLaunchPad | Latest | Mod loader UI + config |
| .NET Framework | 4.5.2 | Target framework |
| Unity | 2021.2.x (game version) | Engine (relevant for asset work only) |

### Assembly References Needed

From `rocketstation_Data/Managed/`:
- `Assembly-CSharp.dll` (game code)
- `UnityEngine.dll` + `UnityEngine.CoreModule.dll`
- `com.unity.multiplayer-hlapi.Runtime.dll` (networking)

From `BepInEx/core/`:
- `0Harmony.dll`
- `BepInEx.dll`

Community package for game assemblies: https://github.com/ilodev/stationeers.modding.assemblies

### Key Game Classes & APIs

#### Object Hierarchy
```
Assets.Scripts.Objects.Thing              (base of ALL game objects)
  |-- DynamicThing                        (non-fixed-position)
  |     |-- Item                          (inventory-storable)
  |     |-- Entity                        (living: Human, etc.)
  |-- Structure                           (player-built, fixed)
        |-- LargeStructure                (2m grid: frames, walls)
        |-- SmallGrid                     (0.5m grid: pipes, cables, devices)
              |-- Device                  (powered machines)
```

#### Iterating Things
- `Thing.AllThings` -- static list of ALL things in the world
- `Structure.AllStructures` -- all structures
- `Device.AllDevices` -- all devices
- `Thing.TryFind(referenceId, out var thing)` -- find by ID

#### Damage Access (writable)
```csharp
thing.DamageState.Brute = 0f;
thing.DamageState.Burn = 0f;
thing.DamageState.Toxic += 0.001f;
// etc.
```

#### Atmosphere Access
- `AtmosphericsManager.AllAtmospheres` -- static collection (may contain nulls)
- `atmosphere.Temperature` -- Kelvin
- `atmosphere.PressureGassesAndLiquidsInPa` -- total pressure
- `atmosphere.Room` -- the Room object (has RoomId)
- `thing.WorldAtmosphere` -- the atmosphere a thing is in

#### Pipe/Cable Networks
- `CableNetwork.AllCableNetworks` -- all cable networks
- `PipeNetwork.AllPipeNetworks` -- all pipe networks
- `network.DeviceList`, `network.CableList`, `network.FuseList`

#### Logic System
- `device.GetLogicValue(LogicType)` / `SetLogicValue(LogicType, double)`
- `device.CanLogicRead(LogicType)` / `CanLogicWrite(LogicType)`
- Existing `LogicType` enum values (reuse, don't add new): `On`, `Mode`, `Setting`, `Power`, `PowerActual`, `Ratio`, `Quantity`, `Temperature`, `Pressure`, `Error`, `Lock`, etc.

#### Game State Guards
```csharp
if (!GameManager.IsServer) return;            // Server-only logic
if (WorldManager.IsPaused) return;            // Skip when paused
if (WorldManager.Instance.GameMode != GameMode.Survival) return;  // Survival only
```

### Multiplayer Architecture

- **Server-authoritative** model. Game simulation runs on server, clients receive updates.
- **Server-only mods** (gameplay patches): Only need to be on the server. PerishableItems explicitly states this.
- **Both-sides mods** (custom prefabs/assets): Clients need the mod to recognize new PrefabHash values.
- **Key guard:** `if (!GameManager.IsServer) return;` at the top of every patch
- **Power system** runs on a **background thread** (not main thread). Use `System.Random` not `UnityEngine.Random`. Use `UniTask.SwitchToMainThread()` for visual updates.
- BepInEx installs on dedicated servers the same way (alongside `rocketstation_DedicatedServer.exe`)

### Periodic Processing Options

1. **Patch `DynamicThing.Update()`** -- runs every frame per thing (PerishableItems does this)
2. **Timer in plugin's `Update()`**:
   ```csharp
   float timer = 0f;
   void Update() {
       timer += Time.deltaTime;
       if (timer >= 5.0f) { timer = 0f; DoWork(); }
   }
   ```
3. **Coroutine**:
   ```csharp
   IEnumerator PeriodicCheck() {
       while (true) {
           yield return new WaitForSeconds(5f);
           DoWork();
       }
   }
   ```

### Save/Load for Cloned Prefabs

- Stationeers saves use `PrefabHash` (integer) to identify things
- `Animator.StringToHash(name)` is deterministic -- same name always produces same hash
- Cloned prefabs with stable names survive save/load automatically
- **If the mod is removed**, things with unknown PrefabHash will fail to load (silently disappear or cause errors)
- No custom save data types needed if the clone doesn't add new state fields

---

## 9. Prefab Cloning Pattern (Mirrored Devices Deep Dive)

### Source Repository

**GitHub:** https://github.com/VincentCharpentier/Stationeers-Mirrored-Devices
**Author:** Apolo (Vincent Charpentier), with contributions from Vanguard
**File count:** 3 C# source files (~950 lines total)

### File Structure

| File | Lines | Purpose |
|---|---|---|
| `MirroredAtmospherics.cs` | 41 | BepInEx plugin entry point |
| `MirrorDefinition.cs` | 52 | Declarative structure per device |
| `MirroredAtmosphericsPatch.cs` | 855 | All cloning/registration logic |

### Three Harmony Patches

1. **`Prefab.LoadAll` -- Prefix** (line 499): Runs before game loads prefabs. This is where cloning happens.
2. **`Localization.LanguageFolder.LoadAll` -- Prefix** (line 828): Registers display names.
3. **`InventoryManager.SetupConstructionCursors` -- Postfix** (line 455): Fixes placement cursor arrows.

### Complete Execution Flow

#### Step 1: Hidden Parent Setup
```csharp
// Static field (lives for entire game session)
private static readonly GameObject HiddenParent = new GameObject("~HiddenGameObject");

// In Prefab.LoadAll prefix:
UnityEngine.Object.DontDestroyOnLoad(HiddenParent.gameObject);
HiddenParent.SetActive(value: false);
```
The hidden parent prevents clones from being visible. `DontDestroyOnLoad` survives scene loads.

#### Step 2: FindMirrorInfos() -- Resolve Source Prefabs (line 614)
Scans `WorldManager.Instance.SourcePrefabs`. For each prefab:
- If it's a `MultiConstructor` with a matching `Constructable`, store the constructor reference
- If it's a plain `Constructor`, queue it for upgrade to `MultiConstructor`
- If it matches by name, store as `deviceToMirror`

Also walks all prefabs' `BuildStates` to find `Tool.ToolEntry`/`ToolEntry2` references that point at the old Constructor, and queues callbacks to update them.

#### Step 3: ConvertConstructorToMultiConstructor (line 526)
If a device uses a single-item `Constructor` kit:
```csharp
var buildStructure = ctor.BuildStructure;
var mctor = ctor.gameObject.AddComponent<MultiConstructor>();
mctor.Constructables = new List<Structure>() { buildStructure };
CopySharedFields((Stackable)ctor, (Stackable)mctor);
WorldManager.Instance.SourcePrefabs[prefabIndex] = mctor;
UnityEngine.Object.DestroyImmediate(ctor);
```

`CopySharedFields` (line 546) uses reflection to copy every field from `Stackable` up through `Item`, `DynamicThing`, to `Thing`. Has special handling for:
- Self-references (field value == source object): rewrites to target
- IList<Interactable> with Parent refs pointing to source: rewrites to target

#### Step 4: CreateMirroredThing -- THE CORE (line 750)
```csharp
// (1) Clone the entire GameObject hierarchy
GameObject mirroredGameObject = GameObject.Instantiate(source, HiddenParent.transform);
mirroredGameObject.name = mirrorDef.mirrorName;

// (2) Override identity
Thing mirroredThing = mirroredGameObject.GetComponent<Thing>();
mirroredThing.PrefabName = mirrorDef.mirrorName;
mirroredThing.PrefabHash = mirrorDef.mirrorHash;

// (3) [Mirror-specific: flip transform -- NOT NEEDED for repair mod]
FlipTransform(mirroredGameObject.transform);

// (4) Clone and process blueprint (placement ghost)
if (mirroredThing.Blueprint != null)
{
    mirroredThing.Blueprint = GameObject.Instantiate(mirroredThing.Blueprint, HiddenParent.transform);
    FlipTransform(mirroredThing.Blueprint.transform);
    Wireframe blueprintWireframe = mirroredThing.Blueprint.GetComponent<Wireframe>();
    FlipWireframe(blueprintWireframe);
}

// (5) REGISTER with the game
WorldManager.Instance.SourcePrefabs.Add(mirroredThing);
```

#### Step 5: AddToConstructor (line 736)
```csharp
int insertIndex = mirrorDef.constructor.Constructables.FindIndex(p => p.name == mirrorDef.deviceName);
mirrorDef.constructor.Constructables.Insert(insertIndex + 1, mirroredDevice as Structure);
```
Inserts the clone right after the original in the kit's constructable list.

#### Step 6: Per-device postfix delegate
```csharp
if (mirrorDef.postfix != null) mirrorDef.postfix(mirroredDevice);
```
Runs device-specific tweaks (e.g., flipping info screens back to readable orientation).

### MirrorDefinition Class
```csharp
internal class MirrorDefinition
{
    public string deviceName;
    public string mirrorName { get; private set; }
    public int mirrorHash { get; private set; }
    public string mirrorDescription;
    public ConnectionDescription[] connectionsToFlip = { };
    public Thing deviceToMirror;
    public MultiConstructor constructor;
    public delegate void MirrorPostFix(Thing mirroredThing);
    public MirrorPostFix postfix;

    public MirrorDefinition(string deviceName)
    {
        this.deviceName = deviceName;
        this.mirrorName = $"{deviceName}Mirrored";
        this.mirrorHash = Animator.StringToHash(this.mirrorName);
        this.mirrorDescription = $"Mirrored version of the {{THING:{deviceName}}}";
    }
}
```

### Localization Registration (line 828)
```csharp
[HarmonyPatch(typeof(Localization.LanguageFolder), nameof(Localization.LanguageFolder.LoadAll))]
[HarmonyPrefix]
private static void Localization_LanguageFolder_LoadAll_Prefix(Localization.LanguageFolder __instance)
{
    if (__instance.Code != LanguageCode.EN) return;

    foreach (var mirrorDef in atmoMirrorDefs)
    {
        var originalName = __instance.LanguagePages[0].Things
            .Find(x => x.Key == mirrorDef.deviceName)?.Value;
        __instance.LanguagePages[0].Things.Add(new Localization.RecordThing
        {
            Key = mirrorDef.mirrorName,
            Value = $"{originalName} (Mirrored)",
            ThingDescription = mirrorDef.mirrorDescription
        });
    }
}
```
No XML needed. Just manipulate the in-memory language page.

### Crafting Recipes: FREE

`GameObject.Instantiate()` copies the entire `BuildStates` chain. The clone inherits the exact same recipe as the original. No XML, no recipe code needed.

### Key Takeaways

1. The actual cloning is ~30 lines. Everything else is bookkeeping.
2. `Prefab.LoadAll` prefix is the universal hook for prefab manipulation.
3. `Animator.StringToHash(name)` is the hash function -- deterministic, stable across saves.
4. Hidden parent with `DontDestroyOnLoad` + inactive is essential.
5. The Constructor/MultiConstructor distinction is the trickiest bookkeeping.
6. Multiplayer and save/load "just work" because the clone shares all original components.
7. This breaks the moment you add custom state or behavior -- then all clients need the mod.

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
| Temporal Micro-Regression (#8) | Fun but risky (side effects involve runtime thing replacement -- MP bugs). |

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

From Re-Volt `TransformerLogicPatch.cs`:
```csharp
[HarmonyPatch(typeof(Transformer))]
internal class TransformerLogicPatch
{
    [HarmonyPrefix]
    [HarmonyPatch(nameof(Transformer.CanLogicRead))]
    public static bool CanLogicReadPatch(LogicType logicType, ref bool __result)
    {
        if (logicType == LogicType.PowerActual) {
            __result = true;
            return false;  // skip original
        }
        return true;  // run original
    }

    [HarmonyPrefix]
    [HarmonyPatch(nameof(Transformer.GetLogicValue))]
    public static bool GetLogicValuePatch(LogicType logicType, ref double __result, float ____powerProvided)
    {
        if (logicType == LogicType.PowerActual) {
            __result = ____powerProvided;  // access private field
            return false;
        }
        return true;
    }
}
```

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

From FPGA `FPGALogicHousing.cs`:
```csharp
public override void OnPrefabLoad()
{
    var src = PrefabUtils.FindPrefab<Structure>("StructureCircuitHousing");
    var srcOnOff = src.transform.Find("OnOffNoShadow");
    var onOff = GameObject.Instantiate(srcOnOff, this.transform);
    this.Interactables[2].Collider = onOff.GetComponent<SphereCollider>();
    this.OnOffButton = onOff.GetComponent<LogicOnOffButton>();
    base.OnPrefabLoad();
}
```

### Code Pattern: BepInEx Config

```csharp
var config = Config.Bind("Section", "Key", defaultValue, "Description");
// Auto-saved to BepInEx/Config/org.author.modname.cfg
```

---

## 14. Stationeers Modding Framework Reference

### BepInEx Plugin Template
```csharp
[BepInPlugin("com.author.modname", "Mod Name", "1.0")]
public class MyPlugin : BaseUnityPlugin
{
    public static MyPlugin Instance;
    void Awake()
    {
        Instance = this;
        var harmony = new Harmony("com.author.modname");
        harmony.PatchAll();
    }
}
```

### Harmony Patch Types

- **Prefix:** Runs before original. Return `false` to skip original. Gets `__instance`, can modify params.
- **Postfix:** Runs after original. Can modify `__result`.
- **Transpiler:** Modifies IL code at load time.
- **Reverse Patch:** Calls private/internal methods from mod code.

### Private Field Access
```csharp
// Harmony convention: 4 underscores + field name
static FieldInfo myField = AccessTools.Field(typeof(WeatherManager), "_stormWindStrength");
myField.SetValue(__instance, newValue);

// Or via parameter naming in patch method:
public static void Patch(float ____privateFieldName) { ... }
```

### Key Singletons

- `GameManager.IsServer` / `NetworkManager.IsServer`
- `WorldManager.IsPaused`, `WorldManager.Instance.GameMode`
- `WeatherManager.Instance`
- `WorldManager.Instance.SourcePrefabs` -- master prefab list

### Known Gotchas

- `Device.AllDevices` can contain duplicates (use HashSet to dedup)
- `AtmosphericsManager.AllAtmospheres` can contain nulls (must filter)
- Power calculations can produce `NaN` (guard against this)
- Power tick runs on background thread -- can't use `UnityEngine.Random`
- Atmosphere may be null until a player logs in on dedicated servers

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

1. **Scaffold BepInEx plugin** (1 hour)
   - Plugin class, Harmony init, config binds
   - Reference: Battery Backup Light structure

2. **Clone vanilla device** (2 hours)
   - Implement Mirrored Devices cloning pattern (sans mirroring)
   - Pick source device (Decision 1)
   - Register with SourcePrefabs, add to MultiConstructor, register localization

3. **Add logic channels** (2 hours)
   - Patch `CanLogicRead`/`GetLogicValue`/`SetLogicValue`/`CanLogicWrite`
   - Channels: On, Setting (threshold), Mode (priority), Ratio (damage %), Quantity (total damage)

4. **Implement repair loop** (3 hours)
   - Timer-based periodic scan (every in-game day)
   - Iterate `Thing.AllThings`, filter structures, sum damage
   - Calculate material cost based on damage type and structure material
   - Search storage containers for materials, consume them
   - Reduce DamageState values proportionally

5. **Inspector chat messages** (2 hours)
   - Inspector name assignment and persistence
   - Message templates for: daily report, incident alert, overdue invoice, long no-damage streak
   - Post to chat via game's messaging system

6. **Config system** (1 hour)
   - All rates, thresholds, toggles via BepInEx Config

7. **Testing** (ongoing)
   - Single player, multiplayer (dedicated server), save/load cycles

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

This document contains the complete state of research and design for the StationeersPlus Repair Mod. All technical details, code patterns, lore, game design, cost structures, and open decisions are captured here. An agent or developer can resume from this point by reading this file alone.
