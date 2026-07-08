---
title: StationeersLaunchPad on a Dedicated Server
type: Workflows
created_in: 0.2.6228.27061
verified_in: 0.2.6403.27689
verified_at: 2026-07-08
sources:
  - DedicatedServer/install/BepInEx/plugins/StationeersLaunchPad/StationeersLaunchPad.dll (decompile at .work/decomp/0.2.6228.27061/StationeersLaunchPad.decompiled.cs)
  - rocketstation_Data/Managed/Assembly-CSharp.dll :: NetworkServer.VerifyConnection / PackageJoinData / ProcessJoinData, GameManager.GetGameVersion, NetworkMessages.VerifyPlayer (decompile at .work/decomp/0.2.6403.27689/Assembly-CSharp.decompiled.cs lines 204091, 212898-212902, 213188, 213684, 213768-213813, 279339-279438)
  - DedicatedServer/install/BepInEx/plugins/StationeersLaunchPad/StationeersLaunchPad.dll :: no join-path patch (decompile at .work/decomp/0.2.6403.27689/StationeersLaunchPad.decompiled.cs lines 3692-3717, 3763-3803, 16922, 17193, 17255)
  - rocketstation_Data/Managed/Assembly-CSharp.dll :: WorkshopMenu (decompile lines 38373-38491)
  - StationeersLaunchPad GitHub releases (server-side asset)
  - https://github.com/StationeersLaunchPad/StationeersLaunchPad at tag v0.4.0 :: StationeersLaunchPad/Platform.cs, StationeersLaunchPad/LaunchPadConfig.cs, StationeersLaunchPad/Configs.cs, StationeersLaunchPad/Loading/LoadStrategy.cs (fetched 2026-07-02)
related:
  - GameSystems/DedicatedServerSettings.md
  - GameSystems/ModLoadSequence.md
  - Patterns/ServerAuthoritativeSimulation.md
  - Protocols/LaunchPadBoosterNetworking.md
tags: [launchpad, network]
---

# StationeersLaunchPad on a Dedicated Server

How StationeersLaunchPad discovers and loads mods on a Stationeers Dedicated Server, why a verbatim copy of the client's `modconfig.xml` does NOT work, and the exact procedure to mirror a client's mod set onto a dedicated server without touching the client's filesystem.

The dedicated server build of Stationeers is the same Unity assembly as the client, run with `Application.platform == WindowsServer` (or `-batchmode`). StationeersLaunchPad detects the server build via `Platform.CheckIsServer()` and switches to `ServerPlatform`. The mod-loading mechanism is the same as on the client (`ModSource.ListAll` -> `ModConfigUtil.LoadConfig` -> `ModList.ApplyConfig` -> `StageLoading`), but two server-only configuration values change the behaviour materially.

## Server-side load orchestration
<!-- verified: 0.2.6228.27061 @ 2026-04-28 -->

`LaunchPadConfig.Run()` (StationeersLaunchPad.decompiled.cs line 3190) is called from `LaunchPadPlugin.Awake` -> `FinishAwake` -> `LaunchPadConfig.Run` (lines 3928-3970). It runs the full stage pipeline on every build, including dedicated server:

```csharp
public static async void Run()
{
    await UniTask.Yield();
    LoadState initState = Platform.InitLoadState;
    SteamDisabled = initState.SteamDisabled;
    AutoLoad &= initState.AutoLoad;
    AutoSort = Configs.AutoSortOnStart.Value;
    CustomSavePathPatches.SavePath = Configs.SavePathOnStart.Value;
    Settings.CurrentData.SavePath = LaunchPadPaths.SavePath;
    await StageInitializing();
    await StageUpdating();
    bool firstLoad = true;
    do
    {
        await StageSearching(firstLoad);
        firstLoad = false;
        await StageConfiguring();
    }
    while (Stage == LoadStage.Searching);
    await StageLoading();
    await StageFinal();
    StartGame();
    await StationeersLaunchPadCommand.MoveToStage(CommandStage.GameRunning);
}
```

The server log proves all stages run, in order: `Loading Mod Repos`, `Loading local repo cache`, `Fetching repo updates`, `Listing Mods`, `Loading Mod Config`, `Mod Config Initialized`, `Assemblies loading...`, `Asset loading...`, `Loading entrypoints...`, `Took 0:00.005 to load mods.`. There is NO server-only mod-loading skip.

## Server-only platform overrides
<!-- verified: 0.2.6228.27061 @ 2026-04-28 -->

`ServerPlatform` (StationeersLaunchPad.decompiled.cs line 4647) overrides:

```csharp
protected override LoadState PlatformInitLoadState => new LoadState
{
    AutoLoad = true,        // server progresses through stages without UI input
    SteamDisabled = true    // server skips all Steam API calls
};
```

`SteamDisabled = true` is the central fact. It is consumed by `ModSource.ListAll(state)` (line 10670) and propagated to each ModSource. `WorkshopModSource.ListMods` (line 10861) short-circuits to an empty list when Steam is disabled. The server therefore discovers ZERO Workshop-source mods.

`ServerPlatform.PlatformWait` (line 4709) is also distinct: any non-`Auto` wait logs `"An error occurred during loading. Exiting"` and calls `Application.Quit()`. The server is unattended; it has no UI to recover from a hang.

## Where modconfig.xml lives
<!-- verified: 0.2.6228.27061 @ 2026-04-28 -->

`WorkshopMenu.ConfigPath` (Assembly-CSharp.decompiled.cs line 38412):

```csharp
public static string ConfigPath => Path.Combine(StationSaveUtils.DefaultPath, "modconfig.xml");
```

`StationSaveUtils.DefaultPath` in batch mode is the directory containing the executable, NOT the user's `Documents\My Games\Stationeers`. So on a dedicated server the file is read from and written to `<install>/modconfig.xml` (next to `rocketstation_DedicatedServer.exe`). It is NOT consulted at `<SavePath>/modconfig.xml`, and it is NOT consulted at `<SavePath>/setting.xml`-style paths even though many other Stationeers files are.

`LaunchPadPaths.ConfigPath` (line 3884) delegates to `WorkshopMenu.ConfigPath`, so StationeersLaunchPad and the game agree on the location.

## Where local mods are scanned from
<!-- verified: 0.2.6228.27061 @ 2026-04-28 -->

`LocalModSource.ListMods` (StationeersLaunchPad.decompiled.cs lines 10422-10456):

```csharp
<localDir> = WorkshopUtils.GetLocalDirInfo(WorkshopType.Mod);
<fileName> = WorkshopUtils.GetLocalFileName(WorkshopType.Mod);  // "About.xml"
...
<>s__5 = <localDir>.GetDirectories("*", SearchOption.AllDirectories);
for each <dir> in <>s__5:
    <>s__8 = <dir>.GetFiles(<fileName>);
    for each <file> in <>s__8:
        <modDir> = <dir>.Parent;        // mod root is the parent of the dir holding About.xml
        <about> = XmlSerialization.Deserialize<ModAboutEx>(<file>.FullName, "ModMetadata");
        <mods>.Add(new LocalModDefinition(<modDir>.FullName, <about>));
```

The scan recurses every directory under `<localDir>` and collects each `*/About/About.xml` it finds. The mod's directory is the parent of the `About/` folder. So a mod laid out at `<localDir>/MyMod/About/About.xml` is registered as a Local mod with `DirectoryPath = <localDir>/MyMod`.

`WorkshopUtils.GetLocalDirInfo(WorkshopType.Mod)` resolves to `<SavePath>/mods/`, NOT `<DefaultPath>/mods/`. This was confirmed empirically at game version 0.2.6228.27061: a server with `-settings SavePath <DataDir>` produces `enabled mod not found at <DataDir>/mods/<modname>` log lines when modconfig entries cannot be matched. The decompiled `GetLocalDirInfo` in Assembly-CSharp (line 264071) uses `StationSaveUtils.GetSavePath()`, which honors `Settings.CurrentData.SavePath` and only falls back to `DefaultPath` when SavePath is empty.

If the directory does not exist, `LocalModSource.ListMods` logs `"local mod folder not found"` and returns an empty list.

**Important asymmetry to remember**: `modconfig.xml` is read from `<DefaultPath>/modconfig.xml` (next to the exe, NOT influenced by SavePath), while the local mods folder is at `<SavePath>/mods/` (influenced by SavePath). When the launcher passes `-settings SavePath <DataDir>` to redirect saves, the local mods folder moves with SavePath, but `modconfig.xml` stays next to the exe.

## How modconfig.xml entries match discovered mods
<!-- verified: 0.2.6228.27061 @ 2026-04-28 -->

`ModList.ApplyConfig(ModConfig config)` (StationeersLaunchPad.decompiled.cs line 13416) is the matcher:

```csharp
public void ApplyConfig(ModConfig config)
{
    Dictionary<string, ModInfo> dictionary = ...;
    foreach (ModInfo mod in mods)
        dictionary[NormalizePath(mod.DirectoryPath)] = mod;

    string fullName = WorkshopUtils.GetLocalDirInfo(WorkshopType.Mod).FullName;

    foreach (ModData mod2 in config.Mods)
    {
        ...
        string text = (string)mod2.DirectoryPath;
        if (!Path.IsPathRooted(text))
            text = Path.Combine(fullName, text);          // relative -> resolved against <localDir>

        string text2 = NormalizePath(text);
        if (dictionary.TryGetValue(text2, out value2))
        {
            value2.Enabled = mod2.Enabled;                // matched: enable
        }
        else if (mod2.Enabled)
        {
            Logger.Global.LogWarning("enabled mod not found at " + text);
        }
    }
}
```

So `<Local><Path Value="<X>" /></Local>` resolves to `<localDir>/<X>` (or stays absolute if already rooted) and is matched against the dictionary of discovered mods. A `<Workshop>` entry only matches a `WorkshopModDefinition` produced by `WorkshopModSource.ListMods`, which on a server is always empty.

## The Export Mod Package mechanism
<!-- verified: 0.2.6228.27061 @ 2026-04-28 -->

The `Export Mod Package` action in the StationeersLaunchPad UI (StationeersLaunchPad.decompiled.cs lines 3370-3429) writes a zip whose contents are exactly what a dedicated server needs. The relevant slice:

```csharp
ModConfig val = new ModConfig();
foreach (ModInfo enabledMod in modList.EnabledMods)
{
    if (enabledMod.Source == ModSourceType.Core)
    {
        val.Mods.Add(new CoreModData());
        continue;
    }
    string text = $"{enabledMod.Source}_{enabledMod.DirectoryName}";  // e.g. "Workshop_3702940349"
    string directoryPath = enabledMod.DirectoryPath;
    string[] files = Directory.GetFiles(directoryPath, "*", SearchOption.AllDirectories);
    foreach (string text2 in files)
    {
        string entryName = Path.Combine("mods", text, text2.Substring(directoryPath.Length + 1)).Replace('\\', '/');
        zipArchive.CreateEntryFromFile(text2, entryName);
    }
    val.Mods.Add(new LocalModData(text, true));   // every entry becomes a Local with relative Path
}
ZipArchiveEntry e = zipArchive.CreateEntry("modconfig.xml");
new XmlSerializer(typeof(ModConfig)).Serialize(e.Open(), val);
```

Three rules emerge:

1. Every enabled mod, regardless of original Source, is rewritten as a `<Local>` entry. Workshop entries lose their `<WorkshopId>` and become Local; the original Workshop subscription is irrelevant on the server.
2. The new `Path` value is the bare name `<Source>_<DirectoryName>` (no `mods/` prefix, no leading slash). `ApplyConfig` resolves it against `<localDir>` = `<install>/mods/`.
3. The mod's full directory contents (recursively) are copied into the zip under `mods/<Source>_<DirectoryName>/...`, preserving the relative subtree (so `About/About.xml` stays at `mods/<Source>_<DirectoryName>/About/About.xml`).

When the developer extracts the zip into the server's install directory, the result is `<install>/mods/<Source>_<DirectoryName>/About/About.xml` for every mod, plus a baked `<install>/modconfig.xml`. StationeersLaunchPad's `LocalModSource` discovers each, `ApplyConfig` matches each, and StationeersLaunchPad loads them all without ever calling Steam.

## The StationeersLaunchPad server-side release zip
<!-- verified: 0.2.6228.27061 @ 2026-04-28 -->

StationeersLaunchPad's GitHub releases ship two assets per version:

- `StationeersLaunchPad-client-v<version>.zip`
- `StationeersLaunchPad-server-v<version>.zip`

For v0.3.1 the server zip is `389,095` bytes and contains:

```
StationeersLaunchPad/LaunchPadBooster.dll          97792
StationeersLaunchPad/RG.ImGui.dll                 384512
StationeersLaunchPad/StationeersLaunchPad.dll     314368
StationeersLaunchPad/StationeersMods.Interface.dll 17408
StationeersLaunchPad/StationeersMods.Shared.dll     8704
```

The four shared DLLs are byte-identical (sha256-confirmed) to the client install's `BepInEx/plugins/StationeersLaunchPad/*`. The single difference is `RG.ImGui.dll` (the ImGui rendering library). It is bundled with the server zip and is not present on a client install (the client uses Unity's UI). StationeersLaunchPad's debug/log windows on the server depend on RG.ImGui being on the loader path; without it, the StationeersLaunchPad-side console UI disable-falls-back silently but the rest of the loader still runs.

Practically: mirroring the client's `BepInEx/plugins/StationeersLaunchPad/` to a dedicated server is sufficient for StationeersLaunchPad to bootstrap and load mods, but it is missing `RG.ImGui.dll`. Add the file from the server zip alongside the other DLLs to keep the server identical to a fresh server-zip install.

## Why a "headless client" is not a supported mode
<!-- verified: 0.2.6228.27061 @ 2026-04-29 -->

A natural follow-up question for automated multiplayer testing: can we run a second Stationeers instance headlessly that acts as a *client* (connects out to a server, executes scripted actions) without a human at a UI? The answer hinges on `Platform.CheckIsServer()` (StationeersLaunchPad.decompiled.cs line 4433):

```csharp
private static bool CheckIsServer()
{
    bool isBatchMode = Application.isBatchMode;
    bool flag = isBatchMode;
    if (!flag)
    {
        RuntimePlatform platform = Application.platform;
        bool flag2 = platform - 43 <= 1;  // matches WindowsServer / LinuxServer
        flag = flag2;
    }
    return flag;
}
```

Two paths put StationeersLaunchPad into `ServerPlatform`: either (1) the executable is the dedicated-server build (Unity reports `Application.platform` as `WindowsServer` (44) or `LinuxServer` (43)), or (2) the instance was launched with `-batchmode`, which sets `Application.isBatchMode == true`. So:

- Running a second `rocketstation_DedicatedServer.exe` in batch mode does NOT produce a client; the binary's `Application.platform` is hardcoded by Unity's "server" build target. The dedicated server is wired to host (`IsServer=true, IsClient=false, SteamDisabled=true`) and has no outbound-connect path.
- Running the regular `rocketstation.exe` with `-batchmode -nographics` to suppress the UI ALSO enters `ServerPlatform` in StationeersLaunchPad, because the `isBatchMode` short-circuit in `CheckIsServer` fires before the platform check. And `Application.isBatchMode` propagates to the engine's `GameManager.IsBatchMode`, which gates 50+ UI/audio/visual paths across `Assembly-CSharp` (e.g. `Assembly-CSharp.decompiled.cs` lines 32391, 33719, 38883, 39875, 46383, etc.) plus the `AutoPauseServer` setting (lines 38883, 38891).

So neither "headless mode" produces a client-mode runtime. Practical options for actually automating a client surface are:

- **A. Real client with full UI + UI-automation tooling** (AutoHotkey, pyautogui, Windows UI Automation API). Drives the actual game with synthetic input. Brittle to UI updates but exercises the real network and rendering pipelines end-to-end.
- **B. Server-side test harness mod**: a BepInEx mod on the dedicated server that scripts player-agent actions in-engine (move avatar, trigger attacks, etc.). Bypasses the network entirely, so it does NOT exercise the client/server message flow that is usually the point of a multiplayer test.
- **C. Custom "test client" BepInEx mod loaded on a real client**: on a trigger (file drop, log event, time-based), the mod calls the same internal Direct Connect, movement, interaction code paths that the UI calls. Real network, real protocol, real client; no human at the keyboard except for launching the client and the trigger drop. Requires identifying the right entry points to call without invoking UI elements that depend on rendering state.
- **D. Two separate client instances on one machine**. Requires two Steam logins; `PlayerCookie-v2.xml`, the Unity PlayerPrefs registry key under `HKCU\Software\Rocketwerkz Limited\rocketstation`, and the Steam runtime are shared per Windows account; family sharing prevents simultaneous use of the same game; running one Steam-authenticated and one with a Steam emulator (Goldberg) is theoretically possible but unsupported and likely breaks the StationeersLaunchPad / Workshop handshake.

Running a second dedicated-server instance to act as a client is NOT among the options: dedicated servers always run with `IsServer=true, IsClient=false` and never initiate outbound connections.

For the StationeersPlus repo's purposes, the realistic ranking is **C > A > B > D**. **C** has the best fidelity-to-effort ratio (real protocol exercise, no UI fragility, modest implementation effort against a known internal API). **A** is the fallback when a feature can only be exercised through the UI. **B** is useful only for tests that don't depend on the network. **D** has too many friction points for a single-developer setup.

## Why a verbatim copy of the client's modconfig.xml does NOT work
<!-- verified: 0.2.6228.27061 @ 2026-04-28 -->

Concrete failure mode observed at game version 0.2.6228.27061 with StationeersLaunchPad 0.3.1:

1. Mirror the client's `BepInEx/plugins/StationeersLaunchPad/` tree to the dedicated server install (skipping `RG.ImGui.dll` does not change behaviour for this failure mode).
2. Copy the user's `<USER_DOCUMENTS>\My Games\Stationeers\modconfig.xml` to `<install>/modconfig.xml`.
3. Start the server.

Result: StationeersLaunchPad's load pipeline runs through every stage (visible in server log: `Loading Mod Repos` ... `Took 0:00.005 to load mods`), but **zero mods load**. Reason chain:

- `WorkshopModSource.ListMods` returns empty because `SteamDisabled = true` on `ServerPlatform`.
- `LocalModSource.ListMods` returns empty because `<install>/mods/` has no subdirectories.
- `ModList.mods` is therefore empty (modulo Core).
- `ApplyConfig` walks the modconfig entries; each enabled `<Workshop>` resolves to its absolute path on the user's E: drive, fails the dictionary lookup (because `WorkshopModSource` did not enumerate it), and logs `"enabled mod not found at <abs path>"`.
- The modconfig's lone `<Local>` entry pointing at `C:\Users\<user>\Documents\My Games\Stationeers\mods\EquipmentPlus` resolves to its absolute path and also fails the dictionary lookup (because `LocalModSource` only scans `<install>/mods/`, not the user's Documents folder).
- The end-of-stage tally is "0 mods", `Took 0:00.005` is the entire mod-load duration.
- Loading a save that referenced any modded `*SaveData` type (e.g. `SpotlightSaveData`) then fails with `InvalidOperationException: The specified type was not recognized` mid-`world.xml` deserialization.

The verbatim copy approach assumes `modconfig.xml` is a SOURCE of truth for what to load. It is not. It is a FILTER applied on top of mods that ModSources have already discovered. With Steam disabled and `<install>/mods/` empty, there is nothing to filter, and the filter has no effect.

## The official procedure
<!-- verified: 0.2.6228.27061 @ 2026-04-28 -->

To mirror a client's mod set onto a dedicated server, three things are needed in the right places on the server:

1. `<install>/BepInEx/plugins/StationeersLaunchPad/` with all five DLLs from the **server** zip (mirroring the client's `BepInEx/plugins/StationeersLaunchPad/` and adding `RG.ImGui.dll` produces an equivalent tree).
2. `<SavePath>/mods/<Source>_<DirectoryName>/...` for every enabled mod on the client. The directory name follows the export convention: `Workshop_<PublishedFileId>` for Workshop mods, `Local_<DirName>` for Local mods. The contents are a recursive copy of the source mod folder. **`<SavePath>` is whatever you set via `-settings SavePath <path>`; if unset, it defaults to `<install>/`. Our launcher sets it to `<repo>/DedicatedServer/data/`.**
3. `<install>/modconfig.xml` with one `<Core Enabled="true">` entry plus optionally one `<Local Enabled="true"><Path Value="<Source>_<DirectoryName>" /></Local>` entry per mod. (See the auto-add note below: the file is not strictly required since `ApplyConfig` adds any discovered mod that is not in modconfig with `Enabled = true`. Including the file is still recommended to express intent and to disable specific mods if needed.)

The `<install>/modconfig.xml` lives at `<DefaultPath>` even when SavePath is redirected; it is a config file and the engine hardcodes its path to `Path.Combine(StationSaveUtils.DefaultPath, "modconfig.xml")`.

### Auto-add behaviour

`ApplyConfig` (decompile lines 13480-13485) iterates the dictionary of discovered mods AFTER processing modconfig entries. Any mod still in the dictionary at that point is added to the load list with `Enabled = true` and the log message `"new mod added at <path>"`. So a server with mods staged at `<SavePath>/mods/<X>/` and an EMPTY (or absent) modconfig.xml still loads every mod. modconfig.xml is best understood as a filter for disabling specific discovered mods, not as the primary "what to load" list.

In our setup, every mod we copy ends up logging `new mod added at <DataDir>/mods/<name>` rather than matching its modconfig entry. End result is the same (mod loaded, `Enabled = true`); the modconfig entries do not silently break anything when this happens.

Two paths to produce (1)-(3):

### Path A: Export Mod Package from the client UI

Click the `Export Mod Package` button in StationeersLaunchPad's UI on the client. StationeersLaunchPad writes a zip to `<USER_DOCUMENTS>\My Games\Stationeers\modpkg_<timestamp>.zip` with the layout above. Extract the zip into `<install>` on the server. Done. This is the documented intended path. It requires the developer to operate the client UI; an agent cannot trigger it without driving the UI.

### Path B: Replicate the export logic, no UI required

Read the client's `modconfig.xml` (read-only). For each enabled non-Core entry:

- Source path: from the entry's `<Path Value="..." />` (absolute path that already exists on the client).
- Destination name: `Workshop_<WorkshopId>` for `<Workshop>` entries, `Local_<basename(Path)>` for `<Local>` entries.
- Recursively copy the source folder's contents into `<install>/mods/<DestinationName>/`, preserving the relative subtree.

After all copies, write `<install>/modconfig.xml`:

```xml
<?xml version="1.0" encoding="utf-8"?>
<ModConfig xmlns:xsd="http://www.w3.org/2001/XMLSchema" xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance">
  <Core Enabled="true"><Path /></Core>
  <Local Enabled="true"><Path Value="<DestinationName1>" /></Local>
  <Local Enabled="true"><Path Value="<DestinationName2>" /></Local>
  ...
</ModConfig>
```

Path B produces a tree byte-equivalent to what Path A would produce (with the caveat that load order in modconfig.xml may differ from Path A's `modList.EnabledMods` order; StationeersLaunchPad's dependency graph reorders at load time anyway).

## Why the client's `BepInEx/config/*.cfg` mirror is not sufficient
<!-- verified: 0.2.6228.27061 @ 2026-04-28 -->

The client's `<install>/BepInEx/config/` contains `.cfg` files for every BepInEx-style mod the client has loaded (e.g. `BetterAdvancedTablet.cfg`, `JetpackHeightUnlocker.cfg`, dozens more). Mirroring this directory to the server is documented in `DedicatedServer/CLAUDE.md` as part of `-Bootstrap`.

These configs are written by mods AS THEY LOAD, not by BepInEx ahead of time. Their presence on the server does NOT cause those mods to load; they are written for mods that DO load and remain as no-ops if the corresponding mods are missing. Mirroring the configs is only valuable to preserve user-tuned settings across client / server boundary; the mod DLLs and `mods/<...>/` folders are still required for the mods to actually run.

## Verification history
<!-- verified: 0.2.6228.27061 @ 2026-04-28 -->

- 2026-04-28: page created from a fresh decompile of `Assembly-CSharp.dll` and the four StationeersLaunchPad-suite DLLs at game version 0.2.6228.27061 (decompile output at `.work/decomp/0.2.6228.27061/*.decompiled.cs`). The Export Mod Package code, `LocalModSource.ListMods`, `ModList.ApplyConfig`, `WorkshopMenu.ConfigPath`, `ServerPlatform.PlatformInitLoadState`, and the StationeersLaunchPad server-zip contents (downloaded from `https://github.com/StationeersLaunchPad/StationeersLaunchPad/releases/download/v0.3.1/StationeersLaunchPad-server-v0.3.1.zip`) are all directly cited above with line numbers and sha256 hashes. Failure mode "verbatim modconfig.xml copy yields zero mods loaded" was observed end-to-end on a running dedicated server at this game version.
- 2026-04-28: corrected the local-mods-folder location finding. Initial decompile-side reading said `<DefaultPath>/mods/`; runtime probe at game version 0.2.6228.27061 with `-settings SavePath <DataDir>` produced `enabled mod not found at <DataDir>/mods/<modname>` errors, proving `WorkshopUtils.GetLocalDirInfo(WorkshopType.Mod)` resolves to `<SavePath>/mods/` (which honours the SavePath override), not `<DefaultPath>/mods/`. Procedure section updated; auto-add behaviour subsection added based on the same end-to-end test (56 mods loaded, Luna save deserialized successfully, RakNet hosted on 27016).
- 2026-04-28: resolved both prior open questions. (1) `ApplyConfig` primary-loop matching: a clean run with `data/mods/` wiped and a fresh `-SyncMods` produced 0 "new mod added" and 0 "enabled mod not found" log lines, proving the primary loop does match relative-path entries cleanly; the earlier 56 auto-adds were a state-corruption artefact from interleaved debug steps. (2) `RG.ImGui.dll`: the StationeersLaunchPad source's ImGui calls are all gated on `!Platform.IsServer` (via `EssentialPatches.SplashDraw`) and the lazy-JIT model means missing ImGui types do not block server-side mod loading; an earlier test confirmed StationeersLaunchPad's full stage pipeline ran without `RG.ImGui.dll` present. Both findings curated into dedicated H2 sections above.
- 2026-04-29: documented RakNet's wildcard-fallback hosting behaviour when the dedicated server tries to bind a port already held by another process on the same machine. Empirical test (client in-session at `10.20.30.200:27016`, server started with default `GamePort 27016`) showed the server's specific-IP bind fails, falls back to `0.0.0.0:27016` (wildcard), and reports "RakNet successfully hosted" despite the conflict. The two processes coexist but route ambiguously. Recommended workaround: pass non-default `GamePort` / `UpdatePort` settings when both run on one machine.
- 2026-04-29: empirical confirmation of the workaround. Launcher gained `-GamePort` / `-UpdatePort` parameters with new defaults `28016` / `28015` (offset by +1000 from the Stationeers client defaults). Re-test with same client in-session (`10.20.30.200:27016`): server log shows clean "Attempting to host at 10.20.30.200:28016" -> "RakNet successfully hosted with Address: 10.20.30.200:28016", no "Hosting failed. Attempting fallback behaviour" line. UDP endpoints show both processes own distinct, exclusive specific-IP binds (`10.20.30.200:27016` for the client, `10.20.30.200:28016` for the server). Clean coexistence.
- 2026-04-29: documented why a "headless client" is not a supported automation mode. `Platform.CheckIsServer()` (line 4433) routes any `Application.isBatchMode` instance into `ServerPlatform`, which is `IsServer=true, IsClient=false, SteamDisabled=true` and has no outbound-connect path. Both batch-mode regular-client and any dedicated-server build land on the server side. Practical options A-D listed for client-side automation; recommended ranking C > A > B > D.
- 2026-07-02: added "Load-failure and self-update exits (StationeersLaunchPad 0.4.0)" from the StationeersLaunchPad GitHub source at tag v0.4.0 (Platform.cs, LaunchPadConfig.cs, Configs.cs, Loading/LoadStrategy.cs) plus the exit line observed on our dedicated server during the 0.2.6403.27689 boot investigation (broken mods from the 0.2.6403 API removals; see Research/Unsorted/Api-removals-0.2.6403.md). Confirms the 0.3.1-era PlatformWait decompile finding is unchanged at 0.4.0 and adds the per-platform update-config defaults and the self-update exit path. Earlier sections keep their 0.2.6228.27061 stamps.
- 2026-07-08: added "Client join: the mod set is NOT matched against the server" from a fresh decompile at game 0.2.6403.27689. Traced the full vanilla join accept/reject gate (`NetworkServer.VerifyConnection` line 213785: blacklist / password / game-build-version only), `GameManager.GetGameVersion` (line 204091, returns the Assembly-CSharp assembly version with no mod hash), the `VerifyPlayer` message fields (line 279339, no mod list), and `PackageJoinData` / `ProcessJoinData` (lines 213684 / 213188, world state only). Confirmed StationeersLaunchPad.dll adds no patch to the join path (16 `HarmonyPatch(typeof(...))` targets, none on `NetworkServer` / `NetworkClient` / `VerifyConnection` / `VerifyPlayer` / `GetGameVersion`; zero references to those types) and that failed mods (`LoadFailed = true`, line 17193) are skipped before entrypoints (line 17255) so they never register a LaunchPadBooster `ModNetworking` instance. Cross-referenced the opt-in per-mod handshake on `Protocols/LaunchPadBoosterNetworking.md`.

## ApplyConfig matching: clean state matches relative paths cleanly
<!-- verified: 0.2.6228.27061 @ 2026-04-28 -->

Resolution of the prior open question. Empirical test on a clean server install (data/mods/ wiped, fresh `-SyncMods`, fresh `-Start -New Lunar`) at game version 0.2.6228.27061 produced:

```
'enabled mod not found' count: 0
'new mod added' count:         0
'Mod has empty path' count:    0
'Invalid path in mod config' count: 0
```

Every modconfig entry matched in `ApplyConfig`'s primary loop (decompile lines 13441-13478). No auto-add fallback fired. NormalizePath behaviour (`path?.Replace("\\", "/").Trim().ToLowerInvariant()` at line 13709) on both sides is consistent. The primary-loop comparison succeeds when:

- `Path.IsPathRooted` returns false on the relative `<Path Value>`, so it gets prefixed with `<localDir>` (= `<SavePath>/mods/`);
- the resulting path normalizes identically to the discovered mod's `LocalModDefinition.DirectoryPath` (set from `<modDir>5__11.FullName` in `LocalModSource.ListMods`).

The earlier "56 new mod added at <abs-path>" observation that prompted the open question was an artefact of an interleaved debug state: mods had been physically moved between `install/mods/` and `data/mods/` mid-debug, modconfig.xml had been rewritten by StationeersLaunchPad's `SaveConfig` after a previously-failed-load run (changing relative paths to absolute paths pointing at the old install/mods/ location, then later not aligning with the new data/mods/ location), and the launcher's `-SyncMods` had run at least once with the stale `install/mods/` target. Once `data/mods/` was wiped and `-SyncMods` re-ran cleanly, the primary loop matched.

Practical consequence: the launcher's `-SyncMods` writes a working modconfig.xml. No path-format change is required. The auto-add fallback exists to gracefully handle drop-in mods that aren't listed in modconfig, but in normal operation it is not relied on.

## Port-binding behaviour with a running client on the same machine
<!-- verified: 0.2.6228.27061 @ 2026-04-29 -->

When a client and the dedicated server try to bind the same `GamePort` on one machine, the server does NOT crash. RakNet attempts a specific-IP bind first; on failure it falls back to a wildcard (`0.0.0.0`) bind, and the OS accepts the combination. The result is two simultaneous bindings on the same port that route by destination address.

Empirical at game version 0.2.6228.27061: client running and in-session (specific bind on `10.20.30.200:27016`, the LAN IPv4), dedicated server started with the launcher's default `-settings GamePort 27016`. Server log:

```
22:46:50: Attempting to host at 10.20.30.200:27016
22:46:51: Hosting failed. Attempting fallback behaviour
22:46:51: RakNet successfully hosted with Address: 10.20.30.200:27016
```

The "Address" reported in the second success line is the LAN IP the server intended (not what it actually bound). UDP endpoint inspection:

```
LocalAddress    LocalPort   OwningProcess
10.20.30.200    27016       <client PID>     ← specific bind, exclusive on that IP
0.0.0.0         27016       <server PID>     ← wildcard bind, catches everything else
```

Routing rules on Windows: specific-IP binds win over wildcard binds for traffic matching that specific IP. So:

- A remote peer connecting to `10.20.30.200:27016` reaches the client (the more-specific bind wins).
- Connections to `127.0.0.1:27016` or other interface IPs hit the server's wildcard catch-all.

This is technically not a crash and not an OS-level conflict, but it is unreliable: the dedicated server is unreachable at the LAN IP it logged, and which process actually serves a given client is determined by the client's exact destination IP. For clean coexistence, the dedicated server should bind a different port via `-settings GamePort <other> -settings UpdatePort <other>`. Defaults that avoid the conflict in normal Stationeers multiplayer setups: `GamePort 27018, UpdatePort 27017`.

## RG.ImGui.dll is not required on the dedicated server
<!-- verified: 0.2.6228.27061 @ 2026-04-28 -->

Resolution of the prior open question. The StationeersLaunchPad-suite DLL `RG.ImGui.dll` ships with the StationeersLaunchPad server-zip release but is not present in client installs. We add it via `-Bootstrap` to match the official server-zip layout. The dedicated server's mod-loading pipeline does NOT depend on it.

Evidence:

- Source-side: every `using ImGuiNET` / `ImGui.*` / `ImGuiHelper.Draw` call in `StationeersLaunchPad.decompiled.cs` is reachable only from UI draw paths. The two relevant sites:
  - `EssentialPatches.SplashDraw` (line 3548) gates `LaunchPadConfig.Draw()` on `if (!Platform.IsServer && !GameStarted)`. On a server, `Platform.IsServer == true`, so the guarded `LaunchPadConfig.Draw()` (which is the entry point for ImGui rendering) is never invoked.
  - The Mod Configuration / LaunchPad Configuration draw functions (lines 7536, 7855, 7876, 9745, 10034) are only called from `LaunchPadConfig.Draw()`'s subtree.
- The `LaunchPadConfig.Run()` async pipeline (`StageInitializing` -> `StageUpdating` -> `StageSearching` -> `StageConfiguring` -> `StageLoading` -> `StageFinal` -> `StartGame`, lines 3190-3213) reaches none of those draw paths.
- Lazy JIT means a method that is never called on the server doesn't have its referenced types resolved. Even though StationeersLaunchPad's compiled assembly imports `ImGuiNET`, missing `RG.ImGui.dll` does not produce a type-load exception until a method that mentions ImGui types is actually invoked.
- Empirical: in the first test pass (before we overlaid `RG.ImGui.dll`), StationeersLaunchPad's full stage pipeline ran successfully. The server log showed every `[Global]:` stage transition through `Took 0:00.005 to load mods.` (zero mods because the local mods folder was empty at that point), with no JIT or assembly-resolve errors.

Practical consequence: `RG.ImGui.dll` is the safe default to ship (the official server zip does, and a future StationeersLaunchPad version might invoke ImGui from a non-UI path), but its absence does not block mod loading on a server. If a server-zip download fails during `-Bootstrap`, the server still loads mods; a warning rather than an error is appropriate.

## Load-failure and self-update exits (StationeersLaunchPad 0.4.0)
<!-- verified: 0.2.6403.27689 @ 2026-07-02 -->

Behaviour of StationeersLaunchPad 0.4.0 on a dedicated server when a mod hard-fails during load, and what the update settings do server-side. Source: the StationeersLaunchPad GitHub repository at tag `v0.4.0` (`StationeersLaunchPad/Platform.cs`, `LaunchPadConfig.cs`, `Configs.cs`, `Loading/LoadStrategy.cs`), fetched 2026-07-02. The installed version on our dedicated server is proven by `BepInEx/LogOutput.log`: `[Info   :   BepInEx] Loading [StationeersLaunchPad 0.4.0]`.

**A mod load failure exits the server process.** The chain, with each link verbatim from the v0.4.0 source:

1. Each load step (assemblies, assets, entrypoints) wraps per-mod work in try/catch; an exception routes to `LoadFailed` (`Loading/LoadStrategy.cs`), which marks the mod and raises a strategy-wide flag but lets OTHER mods keep loading:

   ```csharp
   public void LoadFailed(LoadedMod mod, Exception ex)
   {
       mod.Logger.LogException(ex);
       mod.LoadFailed = true;
       mod.LoadFinished = false;
       failed = true;
   }
   ```

   `LoadMods()` returns `!failed`; subsequent steps skip mods with `mod.LoadFailed` set.
2. `LaunchPadConfig.StageLoading`: `if (!await loadStrategy.LoadMods()) StopAutoLoad();`
3. `LaunchPadConfig.StopAutoLoad()`: `AutoLoad = false; CurWait.Auto = false;`
4. `LaunchPadConfig.StageFinal` builds its stage wait from that flag (`CurWait = new(Configs.AutoLoadWaitTime.Value, AutoLoad);`) and calls `await Platform.Wait(CurWait, CommandStage.ModsLoaded);`, which dispatches to the active platform's `PlatformWait` override.
5. `ServerPlatform.PlatformWait` (`Platform.cs`):

   ```csharp
   protected override async UniTask PlatformWait(StageWait wait, CommandStage stage)
   {
       // don't wait on server
       if (wait.Auto)
           return;

       Logger.Global.LogError("An error occurred during loading. Exiting");
       Application.Quit();
   }
   ```

   `ClientPlatform.PlatformWait`, for contrast, actually waits: `while (!wait.Done && SLPCommand.QueuedStage <= stage) await UniTask.Yield();`.

In the server log the exit surfaces as `[Global]: An error occurred during loading. Exiting` (the `[Global]` prefix is the BepInEx source name of StationeersLaunchPad's `Logger.Global`), preceded by each failing mod's own exception from `LoadFailed`. Observed live on our dedicated server during the 2026-07-02 boot investigation at game 0.2.6403.27689: two mods threw `MissingFieldException` against an API removed at 0.2.6403 (see `../Unsorted/Api-removals-0.2.6403.md`), and the server exited with exactly that line instead of hosting.

**There is no config override.** `Configs.cs` at v0.4.0 defines `CheckForUpdate`, `AutoUpdateOnStart`, `AutoLoadOnStart`, `AutoLoadWaitTime`, `LoadStrategyType`, `LoadStrategyMode` (plus platform-fixup entries); nothing resembling continue-on-error exists, and `StopAutoLoad` forces the non-auto wait regardless of `AutoLoadOnStart`. On an unattended server the only fixes are removing or repairing the failing mod. Note the failure is per-mod isolated during loading (other mods finish loading and log normally) but process-fatal afterwards; diagnosing from the log means looking ABOVE the exit line for the first mod exception, not just at the tail.

**Update checks are off by default on servers.** Platform-specific config defaults (`Platform.cs`):

```csharp
// ClientPlatform.PlatformConfigDefaults
CheckForUpdate = true,
AutoUpdateOnStart = true,

// ServerPlatform.PlatformConfigDefaults
CheckForUpdate = false,
AutoUpdateOnStart = false,
```

`LaunchPadConfig.StageUpdating` early-outs on `if (Stage == LoadStage.Failed || !Configs.CheckForUpdate.Value) return;` and later on `if (!Configs.AutoUpdateOnStart.Value && !await LaunchPadUpdater.CheckShouldUpdate(release)) return;`. So a stock server never contacts GitHub for a StationeersLaunchPad update; the operator has to flip both config values on to get self-update.

**A headless self-update also exits.** If a server does self-update, `ServerPlatform.PlatformContinueAfterUpdate` (`Platform.cs`) refuses to continue on the old in-memory assemblies:

```csharp
Logger.Global.LogWarning("LaunchPad has updated. Exiting");
Application.Quit();
return false;
```

Operational takeaway for the launcher: both exit paths mean `rocketstation_DedicatedServer.exe` terminating early with a zero-drama log tail. Any supervisor script watching for "server up" must treat `An error occurred during loading. Exiting` and `LaunchPad has updated. Exiting` as terminal, not as transient startup noise. This section is the 0.4.0 confirmation and generalisation of the 0.3.1 decompile finding in "Server-only platform overrides" above (`ServerPlatform.PlatformWait`, decompile line 4709): the exit-on-error behaviour is unchanged between 0.3.1 and 0.4.0.

## Client join: the mod set is NOT matched against the server
<!-- verified: 0.2.6403.27689 @ 2026-07-08 -->

The vanilla multiplayer join handshake performs NO comparison of the client's mod set against the server's. A client may carry mods the server lacks (and the reverse) and still be accepted, as long as the game BUILD version matches. Mod-set matching exists only as an opt-in, per-mod layer that LaunchPadBooster adds on top (see `Protocols/LaunchPadBoosterNetworking.md`); StationeersLaunchPad.dll itself adds nothing to the join path.

**The accept/reject gate is `NetworkServer.VerifyConnection`** (Assembly-CSharp.decompiled.cs line 213785). It has exactly three rejection reasons, each sent as a `Handshake { HandshakeState = Rejected }`:

```csharp
public static void VerifyConnection(long hostId, NetworkMessages.VerifyPlayer msg)
{
    Client client = new Client(hostId, msg.OwnerConnectionId, msg.ClientId, msg.Name, msg.ClientConnectionMethod);
    ...
    if (Blacklist.Any((BlacklistedClient x) => x.Id == msg.ClientId)) { HandleBlacklisting(msg, client); return; }          // banned
    if (!string.IsNullOrEmpty(Settings.CurrentData.ServerPassword) && Settings.CurrentData.ServerPassword != msg.Password) // wrong password
    { HandleIncorrectPassword(client); return; }
    if (GameManager.GetGameVersion() != msg.Version) { HandleIncorrectVersion(client, msg); return; }                      // wrong game build
    NetworkBase.AddClient(client);
    ...
}
```

There is no fourth branch for mods. The three `HandshakeType.Rejected` sites in the file (lines 213750, 213761, 213772) are exactly these three handlers.

**The version compared is the game BUILD version, not a mod hash.** `GameManager.GetGameVersion()` (line 204091):

```csharp
public static string GetGameVersion()
{
    return _gameVersion ?? (_gameVersion = Assembly.GetExecutingAssembly().GetName().Version.ToString());
}
```

This returns the `Assembly-CSharp` assembly version string (for example `0.2.6403.27689`) and nothing else. It is not extended with a mod list, a content hash, or a mod-set fingerprint. A client and server running the same Stationeers build pass this check regardless of which mods each has loaded.

**The `VerifyPlayer` message carries no mod list.** Its wire fields (line 279339): `OwnerConnectionId`, `ClientId`, `Name`, `Password`, `Version` (set to `GetGameVersion()` at line 279404), `ClientConnectionMethod`. No mod data is serialized.

**The join data package carries no mod list either.** `NetworkServer.PackageJoinData` (line 213684) serializes only world / simulation state (game time, terrain seed, WorldManager, Vein, OrbitalSimulation, TerraForming, VoxelTerrain, StructureNetwork, CableNetwork, TraderContact, SpaceMap, Rocket, WorldLog, WorldObjectiveState, all Things, RocketLog, RoomController, AtmosphericsManager). `NetworkClient.ProcessJoinData` (line 213188) deserializes the same set. `OnServer.SendMetaData` (line 39928) sends only `GameMetaData { ConnectionId, BytesToReceive }` (the join-package byte count) and runs AFTER `VerifyConnection` already accepted the client, so it is not a gate.

**StationeersLaunchPad.dll does not touch the join path.** Its Harmony surface is 16 `HarmonyPatch(typeof(...))` attributes across six patch classes (`EssentialPatches`, `WorkshopPatches`, `SteamPatches`, `BugfixPatches`, `CustomSavePathPatches`, `LinuxPathPatch`; applied at StationeersLaunchPad.decompiled.cs lines 3692-3717). None target `NetworkServer`, `NetworkClient`, `VerifyConnection`, `VerifyPlayer`, or `GetGameVersion`. The only network-related patch is a transpiler on `NetworkManager.Init` that swaps in a shared `MetaServerTransport` (line 3763) plus a prefix on `MetaServerTransport.InitClient` (line 3798), neither of which compares mods. The decompiled StationeersLaunchPad assembly contains zero references to `NetworkServer` / `NetworkClient` / `VerifyConnection` / `VerifyPlayer`. So StationeersLaunchPad does NOT inject a mod hash into the game version and adds no global mod-set gate.

### Failed mods are excluded from every handshake
<!-- verified: 0.2.6403.27689 @ 2026-07-08 -->

The only mod-aware join gate is LaunchPadBooster's opt-in per-mod handshake (`Protocols/LaunchPadBoosterNetworking.md`): the `MOD.Networking.Required` version gate and `IJoinValidator`. Both iterate `ModNetworking.Instances`, which contains only mods that successfully loaded AND registered a networking instance from their entrypoint (`Plugin` `Awake`). A mod that hard-fails during load is routed through `LoadStrategy.LoadFailed` (StationeersLaunchPad.decompiled.cs line 17193), which sets `LoadFailed = true`; the load loop then skips it (guard at line 17255) before `LoadEntrypoints` (entrypoints instantiated at line 16922). A failed mod therefore never runs its plugin code, never registers a `ModNetworking` instance, and cannot appear in `ModNetworking.Instances`. It is invisible to both the `Required` gate and the symmetric `IJoinValidator` check.

Consequence: a mod that is subscribed / enabled on the client but BROKEN on the current game build (throws during load) is inert for join purposes. It is not compared by the vanilla gate (which ignores mods entirely) and not compared by LaunchPadBooster (which only sees loaded, registered mods). A broken client-only mod does not block the client from joining a server that lacks it.

### Rejection surface for the one check that exists
<!-- verified: 0.2.6403.27689 @ 2026-07-08 -->

On a game-build-version mismatch (the only vanilla check that can differ between two peers that are both running mods), the server sends `Handshake { HandshakeState = Rejected, Message = "MultiplayerIncorrectVersion" }` (localization key `MultiplayerIncorrectVersion`, line 212902) and closes the connection after 500 ms (`HandleIncorrectVersion`, line 213768; `CloseRejectedConnection`, line 213779). The server console logs ``client `{address}:{port}` attempted to connect with incorrect version: {msg.Version}`` (line 213775). The client surfaces the `MultiplayerIncorrectVersion` string. There is no mod-specific rejection message because there is no mod-set check.
