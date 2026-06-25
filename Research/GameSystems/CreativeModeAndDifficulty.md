---
title: Creative Mode and Difficulty
type: GameSystems
created_in: 0.2.6228.27061
verified_in: 0.2.6228.27061
verified_at: 2026-06-25
sources:
  - .work/decomp/0.2.6228.27061/Assembly-CSharp.decompiled.cs :: GameMode enum (line 57660)
  - .work/decomp/0.2.6228.27061/Assembly-CSharp.decompiled.cs :: WorldManager.GameMode / SetCreativeMode / IsCreative (lines 58749, 58892, 60217)
  - .work/decomp/0.2.6228.27061/Assembly-CSharp.decompiled.cs :: DifficultySetting (line 55054) + SetCurrent->SetCreativeMode bridge (line 55251)
  - .work/decomp/0.2.6228.27061/Assembly-CSharp.decompiled.cs :: XmlSaveLoad.WorldData / LoadWorld (lines 250559, 251347)
  - .work/decomp/0.2.6228.27061/Assembly-CSharp.decompiled.cs :: DifficultySettingsCommand (line 95347)
  - <StationeersPath>/rocketstation_Data/StreamingAssets/Data/difficultySettings.xml
related:
  - ./DedicatedServerSettings.md
  - ./WorldStateAPIs.md
  - ./NetworkRoles.md
  - ../Protocols/WorldXml.md
tags: [save-edit, save-load, network, entity]
---

# Creative Mode and Difficulty

How Stationeers represents "Creative Mode" (free/instant build, no resource consumption, item spawning, immortality in toxic environments). There are two linked representations: a runtime `GameMode` enum and a persisted `DifficultySetting`. Creative is derived from the difficulty, not stored as a standalone field.

## The GameMode enum and WorldManager.GameMode field
<!-- verified: 0.2.6228.27061 @ 2026-06-25 -->

The enum has exactly two values (line 57660). There is no `Sandbox` member.

```csharp
public enum GameMode : byte
{
    Survival,    // 0
    Creative     // 1
}
```

`WorldManager` carries it as a plain public instance field (line 58749), NOT a property. No getter/setter, no backing field, no change event:

```csharp
public GameMode GameMode;
```

`WorldManager.Instance` is the singleton, so `WorldManager.Instance.GameMode` is directly assignable. Helper accessor `WorldManager.IsCreative()` (static, line 58892) returns `Instance.GameMode == GameMode.Creative`. The canonical mutator is `WorldManager.SetCreativeMode(bool)` (static, line 60217):

```csharp
public static void SetCreativeMode(bool isCreative)
{
    if (isCreative && Instance.GameMode != GameMode.Creative)
    {
        ConsoleWindow.PrintAction("Enabling Creative Mode");
    }
    Instance.GameMode = (isCreative ? GameMode.Creative : GameMode.Survival);
}
```

There is no `SetGameMode(GameMode)` method and no static `CurrentGameMode`. The field is assigned in exactly three places: `SetCreativeMode` (line 60223), `SyncGameMode.Process` (line 260680, the network message), and via `DifficultySetting.SetCurrent` (line 55254). There is no `OnGameModeChanged` / `CreativeModeChanged` event anywhere.

## DifficultySetting: the persisted source of truth
<!-- verified: 0.2.6228.27061 @ 2026-06-25 -->

`DifficultySetting : SettingBase` (line 55054) is a data-driven preset loaded from XML, registered by Id/hash. `Creative` is one `BoolReference` toggle among ~25 independent rate/toggle fields. Selected fields (full list at lines 55056-55119):

| Member | Type | Default | Role |
|---|---|---|---|
| `Creative` (line 55095) | `BoolReference` | false | The flag that drives `GameMode.Creative`. |
| `HungerRate` | `FloatReference` | 1f | Hunger consumption multiplier. |
| `HydrationRate` | `FloatReference` | 1f | Thirst multiplier. |
| `BreathingRate` | `FloatReference` | 2f | Oxygen consumption multiplier. |
| `RobotBatteryRate` | `FloatReference` | 1f | Suit/robot battery drain. |
| `MiningYield` | `FloatReference` | 1f | Ore yield multiplier. |
| `LungDamageRate` | `FloatReference` | 1f | Toxic-atmosphere lung damage. |
| `FoodDecayRate` | `FloatReference` | 1f | Food spoilage rate. |
| `Achievements` | `BoolReference` | true | Whether achievements fire. |
| `EatWhileHelmetClosed` / `DrinkWhileHelmetClosed` | `BoolReference` | false | |
| `RespawnCondition` | `SerializedId` | | Respawn ruleset id. |

Statics: `DifficultySetting.Current` (line 55160, backing `_current` at line 55132, private), `DifficultySetting.Default` (line 55125), `DifficultySetting.Fallback` (non-creative, lines 55134-55141), `DifficultySetting.Find(string)` (hashes the name via `Animator.StringToHash`, lines 55227-55230; unknown ids fall back to `Fallback`).

### The bridge: SetCurrent derives GameMode from Creative (line 55251)

```csharp
public static void SetCurrent(DifficultySetting difficultySetting)
{
    _current = difficultySetting;
    WorldManager.SetCreativeMode(difficultySetting.Creative);
}
```

So `GameMode` is a cached projection of `DifficultySetting.Current.Creative`. Whenever the difficulty is set (on world load, new game, the `difficulty` console command, or a multiplayer join), `WorldManager.GameMode` is reconstructed from it.

## What "Creative" actually gates: two distinct checks
<!-- verified: 0.2.6228.27061 @ 2026-06-25 -->

Creative behavior is gated by TWO different reads that are NOT equivalent. This matters for any runtime flip.

1. Checks reading `WorldManager.Instance.GameMode == GameMode.Creative` / `WorldManager.IsCreative()`:
   - Creative item-spawn menu visibility (`ImguiCreativeSpawnMenu.ShowMenu`, line 268494).
   - `Human.SpawnDynamicThing` (spawn item from nothing, line 343286).

2. Checks reading `DifficultySetting.Current.Creative` directly (these do NOT consult `WorldManager.GameMode`):
   - Lava immunity for dropped things (line 199905: `!DifficultySetting.Current.Creative`).
   - Creative-only console commands: `power chargeall` (line 98050), rocket cheats (line 98297), `teleport` (line 99223), debug minimap (line 99293), each printing "only available in creative mode" otherwise.
   - Ash-storm / first-day weather suppression on Vulcan (lines 93594, 211595).

Crucial nuance: "no consumption / free build / infinite-ish resources" is NOT gated by the `Creative` bool at all. Those come from the difficulty's RATE fields (HungerRate, BreathingRate, MiningYield, etc.). The stock `Creative` difficulty preset zeroes those rates AND sets `Creative=true` together as data. So `Creative=true` alone unlocks spawning/teleport/lava-immunity/creative-commands; the zero-consumption feel comes from the preset's rate values, set independently.

All these reads are live (read fresh each call, never snapshotted at load), so a runtime change to the underlying state takes effect on the next read.

## The stock Creative difficulty preset (difficultySettings.xml)
<!-- verified: 0.2.6228.27061 @ 2026-06-25 -->

Difficulty presets are defined in `<StationeersPath>/rocketstation_Data/StreamingAssets/Data/difficultySettings.xml` (root `<GameData>` -> `<DifficultySettings>` -> `<DifficultySetting>` entries). It is plain XML, not a binary asset bundle. The serialized shape uses an attribute-on-element form (`<Creative Value="true"/>`, NOT `<Creative>true</Creative>`); `Id` is an XML attribute on `<DifficultySetting>`.

Complete list of stock difficulty Ids and their `Creative` flag:

| Id | Creative | Notes |
|---|---|---|
| `Creative` | **true** (`<Creative Value="true"/>`) | All consumption rates zeroed; `MiningYield=5`; `Achievements=false`. |
| `Easy` | false (no `<Creative>` element) | |
| `Normal` | false | `Default="true"` (the game default). |
| `Stationeer` | false | |

Note: the `WorldXml.md` Protocols page lists difficulty values as `Easy / Normal / Hard / Stationeer`; the live `difficultySettings.xml` in this install ships `Easy / Normal / Stationeer / Creative` (no `Hard` entry found this pass). The exact ship list is data-defined and can vary; `Normal` is the `Default="true"` entry and the value real Luna saves carry.

The `Creative` entry verbatim:

```xml
<DifficultySetting Id="Creative">
    <Name Key="DifficultyCreative"/>
    <Description Key="DifficultyDescriptionCreative" Value="Access item spawning functions and no resource usage. You will survive forever in toxic environments. Respawning is no issue." />
    <PreviewButton Path="Interface/button_creative.png" Format="RGB24"/>
    <HungerRate Value="0"/>
    <HydrationRate Value="0"/>
    <BreathingRate Value="0"/>
    <RobotBatteryRate Value="0"/>
    <MoodRate Value="0"/>
    <HygieneRate Value="0"/>
    <JetpackRate Value="0"/>
    <LungDamageRate Value="0"/>
    <FoodDecayRate Value="0"/>
    <MiningYield Value="5"/>
    <OfflineMetabolism Value="0.0"/>
    <EatWhileHelmetClosed Value="true"/>
    <DrinkWhileHelmetClosed Value="true"/>
    <WeatherLanderDamageRate Value="0"/>
    <Creative Value="true"/>
    <SpaceMapDistanceMultiplier Value="1"/>
    <Achievements Value="false"/>
    <StartingWeatherMultiplier Value="2"/>
    <RespawnStressTime Value="0" />
    <RespawnStressConsumptionSpeed Value="1" />
    <RespawnStressToolUseSpeed Value="1" />
    <RespawnStressTradePenalty Value="1" />
    <GoodHygieneToolSpeedMultiplier Value="1" />
    <MoodToolSpeedMultiplier Value="1" />
</DifficultySetting>
```

The Id string is case-sensitive (`Creative`, capital C). An unknown Id resolves to `Fallback` (non-creative).

## Save persistence: world.xml stores DifficultySetting, not GameMode
<!-- verified: 0.2.6228.27061 @ 2026-06-25 -->

`GameMode` is NEVER serialized into the save. The save records only the difficulty by Id, and `GameMode` is reconstructed from it on load.

The serializer is `XmlSaveLoad.WorldData` (nested class, `[XmlRoot("WorldData")]`, line 250559). The difficulty field (lines 250587-250588):

```csharp
[XmlElement("DifficultySetting")]
public SerializedId DifficultySetting = new SerializedId("Fallback");
```

`SerializedId` (line 54964) serializes its `Id` as a single `[XmlAttribute]`. So the on-disk XML is exactly `<DifficultySetting Id="Normal" />` (self-closing, `Id` attribute only), a direct child of root `<WorldData>`. Confirmed against a real Luna save: the top of world.xml carries `<WorldSetting Id="Lunar" />` then `<DifficultySetting Id="Normal" />` as adjacent children of `<WorldData>`.

`WorldData` also stores the world/planet id separately as `<WorldSetting Id="..." />` (line 250578, default `"Space"`). GameMode is NOT part of WorldSetting; `WorldSetting` covers the planet (skybox, gravity, celestial, terrain). The `[XmlInclude(typeof(GameMode))]` at line 60591 sits on `WorldSettingData : DataCollection` (content definitions), not on the save's `WorldData`, so it does not put GameMode into world.xml.

### Load path (XmlSaveLoad.LoadWorld, line 251347)

- Line 251362: `XmlSerialization.Deserialize(Serializers.WorldData, ...)` reads world.xml verbatim into `WorldData`.
- Line 251395: `WorldSetting.SetCurrent(...)` applies the planet.
- Line 251396: `DifficultySetting.SetCurrent(worldData.DifficultySetting)` reads the edited difficulty Id and runs the `SetCreativeMode` bridge. This is the line that makes an offline edit take effect.
- Line 251437-251440: the ONE override: if `GameManager.IsNewTutorial`, difficulty is force-reset to `"Normal"`. Does not fire for normal saves.

There is no recompute that silently overwrites a normal save's difficulty. An offline edit of `<DifficultySetting Id="...">` is honored on next load (outside the tutorial path).

Write path: `GetWorldData` (line 251197) emits `worldData.DifficultySetting = new SerializedId(DifficultySetting.Current)`.

### Editing a save to creative (offline)

Change the world.xml element `<DifficultySetting Id="Normal" />` to `<DifficultySetting Id="Creative" />`. On next load the host resolves it against `difficultySettings.xml`, sets `_current` to the Creative preset (zero rates + `Creative=true`), and derives `GameMode.Creative`. This persists across reloads (it IS the saved state). Editing to `Easy`/`Normal`/`Stationeer` yields survival. The `tools/save-edit/` Python editor can do this by changing the `Id` attribute of the `<DifficultySetting>` element.

## Networking: how Creative reaches clients
<!-- verified: 0.2.6228.27061 @ 2026-06-25 -->

The server is authoritative. `GameMode` propagates to clients in two ways:

1. Join handshake (the durable path). `WorldManager.SerializeOnJoin` (line 60194) writes `World.CurrentId`, `WorldSetting.GetHash()`, `DifficultySetting.GetHash()`, and `IsCreative()` (a bool). `WorldManager.DeserializeOnJoin` (line 60202) reads them back, calls `WorldSetting.SetCurrent` + `DifficultySetting.SetCurrent`, and `SetCreativeMode(true)` if the bool was set. So a freshly joining client picks up whatever difficulty/creative the host currently holds. This is why a save-edit (which changes what the host loads) correctly reaches a player who connects afterward.

2. Live mid-session sync. `SyncGameMode` message (message-table id 78, line 179621; class at line 260674). Its `Process` sets `WorldManager.Instance.GameMode = GameMode;` (line 260680). This is how a host toggling creative WHILE clients are connected pushes the change. Note the `difficulty` console command path (`SetCurrent` -> `SetCreativeMode`) does NOT itself send `SyncGameMode`; a console difficulty change made after clients connect does not auto-push to them (they would need to reconnect). `Settings.ServerInfo.ServerGameMode` (line 262224) is server-listing metadata, not a live push.

## Console command: difficulty <name>
<!-- verified: 0.2.6228.27061 @ 2026-06-25 -->

The ONLY console command that changes game mode is `difficulty` (`DifficultySettingsCommand`, line 95347, registered at line 94995). There is no `creative`, `sandbox`, `gamemode`, or `cheat` command in the dispatch table, and no `AllowCheats`/`CheatsEnabled` toggle anywhere in the assembly.

```csharp
public override string HelpText => "Prints the current difficulty setting to the console or sets a new one if provided as an argument";
public override string[] Arguments => new string[1] { "<?difficulty>" };
public override bool IsLaunchCmd => true;
```

- `difficulty` (no args): prints `DifficultySetting.Current.DebugPrint()`.
- `difficulty <name>`: `DifficultySetting.Find(name)` then `DifficultySetting.SetCurrent(...)`. Passing `difficulty Creative` puts the world into creative.
- Host-only: calls `CommandBase.CannotAsClient("difficulty")` (line 95379), so a remote client is blocked. No single-player block. `IsLaunchCmd => true` (also a launch flag; defers via `WaitExecute` until `GameManager.IsInitialized` if not ready).
- No network push inside Execute (see Networking above). Logic-only, no UI dependency, so it works headless IF stdin reaches the dispatcher.

`worldsetting` (`WorldSettingWindowCommand`, line 100254) only opens an ImGui authoring window (a no-op on `-batchmode -nographics`); `world` (`PrintWorldSettingsCommand`, line 98090) only prints. Neither sets game mode.

`NewGameCommand` (line 97302, `IsLaunchCmd => true`) takes `<worldname> <difficulty> <startcondition>`, so a brand-new world can launch directly into creative via `-new <Map> Creative <startcondition>` (verify the start-condition id). It does not change an already-running world.

## Runtime flip (worker-thread safety)
<!-- verified: 0.2.6228.27061 @ 2026-06-25 -->

For a Harmony postfix running on a worker thread (e.g. on `ElectricityManager.ElectricityTick`), where only pure managed-state writes are safe and Unity-API calls are not:

- `WorldManager.Instance.GameMode = GameMode.Creative;` is a single enum (`byte`) field write with no setter body, no Unity call, no event subscriber. It is worker-thread-safe and takes effect on the next read.
- Do NOT call `WorldManager.SetCreativeMode(...)` on a worker thread: it calls `ConsoleWindow.PrintAction("Enabling Creative Mode")` on the creative transition, which touches console/UI and is not guaranteed thread-safe. Write the field directly instead (it reproduces the only state change `SetCreativeMode` makes minus the print).
- Do NOT call `DifficultySetting.SetCurrent(...)` on a worker thread: it ends in `SetCreativeMode` (same `ConsoleWindow` issue).

Completeness caveat: flipping ONLY `WorldManager.Instance.GameMode` enables the `GameMode`-gated features (spawn menu, `SpawnDynamicThing`, anything via `IsCreative()`) but does NOT waive build costs or grant lava immunity (those read `DifficultySetting.Current.Creative`) and does NOT zero consumption (that is the difficulty's rate fields). For full creative parity at runtime, also point `DifficultySetting._current` (private static, line 55132) at the Creative preset via reflection (a pure managed write, no Unity call) so the rate fields and the `DifficultySetting.Current.Creative` reads also flip. Both writes are worker-safe; only the `ConsoleWindow`-touching helpers are not. In multiplayer a worker-thread field write does NOT send `SyncGameMode`, so already-connected clients would not see it; a fresh join would (via `SerializeOnJoin`).

## Verification history
<!-- verified: 0.2.6228.27061 @ 2026-06-25 -->

- 2026-06-25: page created from a decompile read of `Assembly-CSharp` at game version 0.2.6228.27061 (`.work/decomp/0.2.6228.27061/Assembly-CSharp.decompiled.cs`) plus the live `difficultySettings.xml` content data. The `GameMode` enum, `WorldManager.GameMode`/`SetCreativeMode`/`IsCreative`, the `DifficultySetting.SetCurrent` -> `SetCreativeMode` bridge, the `XmlSaveLoad.WorldData`/`LoadWorld` save path, the `DifficultySettingsCommand`, the join handshake (`SerializeOnJoin`/`DeserializeOnJoin`), and the `SyncGameMode` (id 78) live-sync are all verbatim from the decompile with line numbers. The stock `Creative` difficulty preset block is verbatim from `difficultySettings.xml`. The `<DifficultySetting Id="Normal" />` / `<WorldSetting Id="Lunar" />` save shape was confirmed against a real Luna save's world.xml.

## Open questions
<!-- verified: 0.2.6228.27061 @ 2026-06-25 -->

- Whether `difficultySettings.xml` ever ships a `Hard` entry. The `WorldXml.md` page lists `Easy/Normal/Hard/Stationeer`; this install's file has `Easy/Normal/Stationeer/Creative` (no `Hard`). The set is data-defined and may differ by version or DLC.
- The exact `startcondition` id to pair with `-new <Map> Creative` for a fresh creative world (the `NewGameCommand` third argument); not enumerated this pass.
