---
title: Side-car file persistence in save ZIPs
type: GameSystems
created_in: 0.2.6228.27061
verified_in: 0.2.6228.27061
verified_at: 2026-04-21
sources:
  - rocketstation_Data/Managed/Assembly-CSharp.dll :: Assets.Scripts.Serialization.SaveHelper.Save
  - rocketstation_Data/Managed/Assembly-CSharp.dll :: Assets.Scripts.Serialization.XmlSaveLoad.LoadWorld
  - rocketstation_Data/Managed/Assembly-CSharp.dll :: Assets.Scripts.GameManager.UpdateThingsOnGameStartAction
  - rocketstation_Data/Managed/Assembly-CSharp.dll :: Assets.Scripts.GameManager.AutoSaveNow
  - rocketstation_Data/Managed/Assembly-CSharp.dll :: Assets.Scripts.Objects.Thing.OnFinishedLoad
  - Plans/SaveFixPrototype/terrain_reset.py
related:
  - ../Protocols/SaveFileStructure.md
  - ../GameSystems/SaveDataRegistration.md
  - ../GameSystems/UnregisteredSaveDataBehavior.md
tags: [save-load, save-format, launchpad]
---

# Side-car file persistence in save ZIPs

Feasibility study for mods to write auxiliary files into the Stationeers save ZIP to persist optional mod state without custom ThingSaveData subclasses.

## Core finding: read-safe, write-unsafe
<!-- verified: 0.2.6228.27061 @ 2026-04-21 -->

- **Load:** Unknown ZIP entries are **preserved silently** (reader only looks up known filenames by name).
- **Save:** Unknown ZIP entries are **stripped** (SaveHelper.Save rebuilds the ZIP from scratch, known entries only).
- **Implication:** Side-car persistence requires mod to write the file on every save, not just once.

## SaveHelper.Save overload disambiguation (Harmony pitfall)
<!-- verified: 0.2.6228.27061 @ 2026-04-21 -->

`SaveHelper` declares **two** methods named `Save`:

```csharp
// Assets.Scripts.Serialization.SaveHelper
// rocketstation_Data/Managed/Assembly-CSharp.dll

public static async UniTask<SaveResult> Save(string stationName, CancellationToken cancellationToken)
private static async UniTask<SaveResult> Save(DirectoryInfo saveDirectory, string saveFileName, bool newSave, CancellationToken cancellationToken)
```

The public overload is a thin wrapper that flows through `SaveGame(SaveMethod.Save, ...)` into `DoSave`, which calls the private worker. Every save path funnels through the private worker:

| Entry point | Routing |
|---|---|
| `Save(string, CancellationToken)` | public -> `SaveGame` -> `DoSave` -> private `Save` |
| `NewSave(string, CancellationToken)` | `SaveGame` -> `DoNewSave` -> private `Save(..., newSave: true)` |
| `SaveAs(...)` | `SaveGame` -> `DoSaveAs` -> private `Save` -> `CopyToHeadSave` |
| `QuickSave(string, CancellationToken)` | `SaveGame` -> `DoQuickSave` -> `RollingSave` -> private `Save` |
| `AutoSave(string, CancellationToken)` | `SaveGame` -> `DoAutoSave` -> `RollingSave` -> private `Save` |

The private worker is where the `ZipOutputStream` write happens, so that is the correct Harmony target.

**Harmony pitfall:** `[HarmonyPatch(typeof(SaveHelper), nameof(SaveHelper.Save))]` throws `HarmonyException: Ambiguous match for HarmonyMethod[... methodname=Save, type=Normal, args=undefined]` wrapping `System.Reflection.AmbiguousMatchException` at `PatchAll` time because HarmonyX's `AccessTools.DeclaredMethod` cannot choose between the two overloads by name alone. **Entire `PatchAll` call throws — no patches in the assembly apply.** Mods targeting either overload must specify the argument types:

```csharp
[HarmonyPatch(typeof(SaveHelper), "Save",
    new[] { typeof(DirectoryInfo), typeof(string), typeof(bool), typeof(CancellationToken) })]
```

Use the string literal `"Save"` because `nameof(SaveHelper.Save)` resolves via the publicly visible member (the `(string, CancellationToken)` overload); the private overload is what callers actually want to patch for ZIP-write interception.

## ZIP write path (private SaveHelper.Save)
<!-- verified: 0.2.6228.27061 @ 2026-04-21 -->

The private `Save(DirectoryInfo, string, bool, CancellationToken)` worker creates a temporary file, writes all known entries to a `ZipOutputStream`, then atomically moves it to the final location. Unknown entries in any pre-existing save at the destination are never read or copied.

Uses `ZipOutputStream` from ICSharpCode.SharpZipLib (not `System.IO.Compression.ZipArchive`). Only writes five known entries: `world_meta.xml`, `world.xml`, `terrain.dat`, `preview.png`, `screenshot.png`. No archive open or re-open in `ZipArchiveMode.Update` — completely fresh write to temp file.

Verbatim excerpt (relevant portion):

```csharp
// Assets.Scripts.Serialization.SaveHelper :: Save method
// rocketstation_Data/Managed/Assembly-CSharp.dll

_stopwatch.Restart();
await UniTask.SwitchToThreadPool();
FileInfo tempFile = new FileInfo(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString()));
try
{
    await using ZipOutputStream zipStream = new ZipOutputStream(tempFile.OpenWrite());
    zipStream.SetLevel(SaveLoadConstants.ZipCompressionLevel);
    AddZipFile(zipStream, metaMs, SaveLoadConstants.MetaFileName);
    AddZipFile(zipStream, worldMs, SaveLoadConstants.WorldFileName);
    AddZipFile(zipStream, terrainMs, SaveLoadConstants.TerrainFileName);
    if (!GameManager.IsBatchMode)
    {
        AddZipFile(zipStream, previewMs, SaveLoadConstants.PreviewFileName);
        AddZipFile(zipStream, screenShotMs, SaveLoadConstants.ScreenshotFileName);
    }
    zipStream.Finish();
    await zipStream.FlushAsync(cancellationToken);
}
catch (Exception arg)
{
    _stopwatch.Stop();
    return SaveResult.Fail($"Failed to write to temp file at path {tempFile} : {arg}");
}
finally
{
    metaMs.Close();
    worldMs.Close();
    terrainMs.Close();
    previewMs.Close();
    screenShotMs.Close();
}
FileStream copyStream = tempFile.OpenRead();
try
{
    await using FileStream destinationStream = File.Open($"{saveDirectory}/{saveFileName}", FileMode.Create, FileAccess.Write);
    await copyStream.CopyToAsync(destinationStream, cancellationToken);
}
catch (Exception ex2)
{
    _stopwatch.Stop();
    return SaveResult.Fail(ex2.Message);
}
copyStream.Close();
tempFile.Delete();
```

**Single seal point:** `zipStream.Finish()` followed by `zipStream.FlushAsync()`. Any side-car entry must be added before `zipStream.Finish()` is called.

## Load-time ZIP extraction (LoadHelper.ExtractToTemp)
<!-- verified: 0.2.6228.27061 @ 2026-04-21 -->

By the time `XmlSaveLoad.LoadWorld` runs, the save ZIP is already fully extracted to a temp directory. The game does NOT open the save ZIP at `LoadWorld` time. `LoadHelper.LoadGameTask` calls `ExtractToTemp(path)` first, which uses `ZipInputStream` to enumerate every entry via `GetNextEntry()` and writes each to a temp directory as a loose file.

```csharp
// Assets.Scripts.Serialization.LoadHelper
// rocketstation_Data/Managed/Assembly-CSharp.dll

private static async UniTaskVoid LoadGameTask(string path, string stationName)
{
    try
    {
        string text = ExtractToTemp(path);
        StationSaveContainer currentWorldSave = new StationSaveContainer(new DirectoryInfo(text));
        XmlSaveLoad.Instance.CurrentWorldSave = currentWorldSave;
        // ... sets up UI, then:
        await LoadWorldTask(text);
    }
    // ... catch
}

private static string ExtractToTemp(string path)
{
    string text = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
    Directory.CreateDirectory(text);
    using ZipInputStream zipInputStream = new ZipInputStream(File.OpenRead(path));
    while (true)
    {
        ZipEntry nextEntry = zipInputStream.GetNextEntry();
        if (nextEntry == null)
        {
            break;
        }
        using FileStream destination = File.Create(Path.Combine(text, Path.GetFileName(nextEntry.Name)));
        zipInputStream.CopyTo(destination);
    }
    return text;
}
```

Critical implications for mod authors:

- **Every ZIP entry is extracted, known and unknown alike.** Filenames not in the vanilla set (`world.xml`, `world_meta.xml`, `terrain.dat`, `preview.png`, `screenshot.png`) still land in the temp directory as loose files. A side-car entry named `sprayplus-glow.xml` is extracted to `<tempDir>/sprayplus-glow.xml`.
- **The save ZIP is closed before `LoadWorld` runs.** Any Harmony patch that tries to re-open the save ZIP during `LoadWorld` or `Thing.OnFinishedLoad` will race the file lock and may hit "End of Central Directory record could not be found" if the path is stale.
- **Side-car reads at load time should read the loose temp file**, not re-open the save ZIP. Compute the temp directory as `Path.GetDirectoryName(XmlSaveLoad.Instance.CurrentWorldSave.World.FullName)` and open `<tempDir>/<entryName>` directly.
- **Filename collision is possible.** `ExtractToTemp` uses `Path.GetFileName(nextEntry.Name)` so nested directories in the ZIP are flattened. Keep side-car entry names distinct from the five vanilla filenames.

## ZIP read path (LoadWorld)
<!-- verified: 0.2.6228.27061 @ 2026-04-21 -->

`LoadWorld` reads `world.xml` as a plain file from the temp directory populated by `ExtractToTemp`. It never touches a `ZipArchive` or `ZipInputStream`.

```csharp
// Assets.Scripts.Serialization.XmlSaveLoad :: LoadWorld (key excerpt)
// rocketstation_Data/Managed/Assembly-CSharp.dll

string fullName = Instance.CurrentWorldSave.World.FullName;
object obj = XmlSerialization.Deserialize(Serializers.WorldData, fullName);
if (!(obj is WorldData worldData))
{
    UpdateLoadingScreen(display: false);
    throw new NullReferenceException("Failed to load the world.xml: " + fullName);
}
```

`fullName` is `<tempDir>/world.xml`, a loose XML file on disk. `XmlSerialization.Deserialize(XmlSerializer, string path)` opens it with `new StreamReader(path)`; no ZIP involvement. `StationSaveContainer.World` is a `FileInfo` populated from the temp directory's contents:

```csharp
// Assets.Scripts.Serialization.StationSaveContainer (constructor body)
// rocketstation_Data/Managed/Assembly-CSharp.dll

World = files.FirstOrDefault((FileInfo x) => x.Name == "world.xml");
```

## Thing.OnFinishedLoad timing and caller
<!-- verified: 0.2.6228.27061 @ 2026-04-21 -->

**Method signature:**

```csharp
// Assets.Scripts.Objects.Thing :: OnFinishedLoad
// rocketstation_Data/Managed/Assembly-CSharp.dll

public virtual void OnFinishedLoad()
{
    if (!IsCursor)
    {
        if (InternalAtmosphere == null && GameManager.RunSimulation)
        {
            InitInternalAtmosphere();
        }
        if (!GameManager.RunSimulation)
        {
            RefreshAnimState(skipAnimation: true);
        }
        if (this is IRotatable rotatable)
        {
            rotatable.RotatableBehaviour?.OnClientStart();
        }
        SuppressSound = false;
    }
}
```

**Caller:** `GameManager.UpdateThingsOnGameStartAction`, invoked from `GameManager.UpdateThingsOnGameStart()` at the end of load:

```csharp
// Assets.Scripts.GameManager
// rocketstation_Data/Managed/Assembly-CSharp.dll

private static readonly Action<Thing> UpdateThingsOnGameStartAction = delegate(Thing thing)
{
    if ((object)thing == null)
    {
        return;
    }
    thing.OnFinishedLoad();
    foreach (Interactable interactable in thing.Interactables)
    {
        if (interactable.JoinInProgressSync && (bool)interactable.Animator)
        {
            interactable.SetState();
            thing.OnFinishedInteractionSync(interactable);
        }
    }
};

public static void UpdateThingsOnGameStart()
{
    OcclusionManager.AllThings.ForEach(UpdateThingsOnGameStartAction);
}
```

**Timing guarantee:** OnFinishedLoad() is called AFTER all ThingSaveData deserialized, child-parent relationships established, atmospheres loaded, and devices initialized. This occurs near the end of LoadWorld before `GameManager.OnReadyToPlay()`.

## Auto-save trigger and multiplayer guard
<!-- verified: 0.2.6228.27061 @ 2026-04-21 -->

Auto-save is triggered by `GameManager.AutoSaveNow()` with a strict multiplayer guard:

```csharp
// Assets.Scripts.GameManager :: AutoSaveNow
// rocketstation_Data/Managed/Assembly-CSharp.dll

private static void AutoSaveNow()
{
    if (!GameManager.IsTutorial && !GameManager.IsNewTutorial && !WorldManager.IsGamePaused && GameManager.GameState == GameState.Running && !Assets.Scripts.Networking.NetworkManager.IsClient)
    {
        AutoSaveTask().Forget();
    }
}

private static async UniTaskVoid AutoSaveTask()
{
    SaveResult saveResult = await SaveHelper.AutoSave(XmlSaveLoad.Instance.CurrentStationName, default(CancellationToken));
    if (!saveResult.Success)
    {
        ConsoleWindow.PrintError(saveResult.Message);
    }
}
```

**Critical guard:** `!Assets.Scripts.Networking.NetworkManager.IsClient`

This ensures auto-save only runs on the host/server, never on clients. Auto-save uses the same `SaveHelper.AutoSave()` → `SaveHelper.Save()` code path as manual saves.

## Save-copy behavior and unknown entry handling
<!-- verified: 0.2.6228.27061 @ 2026-04-21 -->

When the player uses "Save As" in the in-game UI, the game calls `SaveHelper.SaveAs()`, which invokes `SaveHelper.Save()` (creating a fresh archive with no unknown entries) and then copies that file to the head location. Unknown entries in the original save are already gone after `Save()`.

**Rename behavior** uses `Directory.MoveTo()` without touching the save file contents, so unknown entries in existing saves are preserved during station rename.

## LaunchPadBooster extension hooks
<!-- verified: 0.2.6228.27061 @ 2026-04-21 -->

Decompilation of LaunchPadBooster.dll reveals **no save/load hooks or extension points** for ZIP entry injection. The DLL provides world-search, mod management, and networking utilities, but does not expose `OnSaveWorld`, `OnLoadWorld`, `ISaveExtension`, or any `Action<ZipArchive>` event.

`Mod.AddSaveDataType<T>()` (from StationeersLaunchPad) registers only ThingSaveData subclasses in XmlSaveLoad.ExtraTypes. There is no public API for direct ZIP entry injection.

**Required workaround:** Harmony patch on SaveHelper.Save to intercept the ZipOutputStream or re-open the temp file in Update mode after Finish().

## State capture at save time
<!-- verified: 0.2.6228.27061 @ 2026-04-21 -->

At SaveHelper.Save invocation:
- `XmlSaveLoad.GetWorldData()` enumerates all Things via `OcclusionManager.AllThings.AsPooledSpan()`
- All mod state (e.g., GlowPaintHelpers.GlowingThingIds) is fully populated and accessible
- Main thread context (game tick is unpaused before Save is called)
- Serialization to MemoryStream is synchronous on thread pool before ZipOutputStream finalization

## Harmony interception strategy (verified in SprayPaintPlus v1.6.0)
<!-- verified: 0.2.6228.27061 @ 2026-04-21 -->

**Working pattern: Prefix + Postfix on the PRIVATE SaveHelper.Save worker, wrapping the returned UniTask.**

The private `Save(DirectoryInfo, string, bool, CancellationToken)` worker is async (returns `UniTask<SaveResult>`). A Harmony Postfix on an async method fires after the kick-off stub returns the task, BEFORE any of the async body has executed (per `Research/Patterns/AsyncHarmonyTrap.md`). Direct Postfix access to the written file at the right moment therefore requires wrapping the returned task with a continuation.

```csharp
[HarmonyPatch(typeof(SaveHelper), "Save",
    new[] { typeof(DirectoryInfo), typeof(string), typeof(bool), typeof(CancellationToken) })]
public class SaveHelperSaveSideCarPatch
{
    public static void Prefix()
    {
        // Snapshot mod state on the MAIN thread. SaveHelper.Save's body
        // switches to ThreadPool on its first await; reading the dictionary
        // directly from the ThreadPool worker would race gameplay mutations.
        MyMod.PendingSaveSnapshot = MyMod.SnapshotState();
    }

    public static void Postfix(
        DirectoryInfo saveDirectory,
        string saveFileName,
        ref UniTask<SaveResult> __result)
    {
        var originalTask = __result;
        var snapshot = MyMod.PendingSaveSnapshot;
        MyMod.PendingSaveSnapshot = null;
        var path = Path.Combine(saveDirectory.FullName, saveFileName);
        __result = WriteSideCarAfterSave(originalTask, path, snapshot);
    }

    private static async UniTask<SaveResult> WriteSideCarAfterSave(
        UniTask<SaveResult> saveTask, string path, StateType snapshot)
    {
        var result = await saveTask;
        if (!result.Success) return result;

        // The destination .save file is now sealed, and the async body's
        // using-blocks have closed the file handle. Safe to re-open in
        // ZipArchiveMode.Update and add the side-car entry.
        using (var fs = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        using (var archive = new ZipArchive(fs, ZipArchiveMode.Update))
        {
            archive.GetEntry("my-mod-sidecar.xml")?.Delete();
            var entry = archive.CreateEntry("my-mod-sidecar.xml", CompressionLevel.Optimal);
            using (var es = entry.Open())
            {
                // serialize snapshot into es
            }
        }
        return result;
    }
}
```

Why the earlier "open the temp file" idea does not work: the temp file is `await tempFile.Delete()`'d at the end of `SaveHelper.Save`'s async body, BEFORE the returned task completes. By the time any continuation runs, the temp file is gone. Only the final destination file (`$"{saveDirectory}/{saveFileName}"`) is available post-completion.

Why the public Save overload is not a valid target: it dispatches through `SaveGame` -> `DoSave` -> private `Save`. Patching the public entry misses autosave, quicksave, new-save, and save-as flows. The private worker is the single funnel (see "SaveHelper.Save overload disambiguation" section).

**Load side:** Postfix on `XmlSaveLoad.LoadWorld`. Read `<tempDir>/my-mod-sidecar.xml` directly as a loose file; do NOT attempt to reopen the save ZIP (see "Load-time ZIP extraction" section).

## Design consideration: user warning on mod removal
<!-- verified: 0.2.6228.27061 @ 2026-04-21 -->

If a mod writes side-car files (e.g., glow color state) and is later removed, those files remain in the save ZIP but are never read or applied. This is **safe and desirable** for cosmetic mods:

- No save breakage (side-car file is simply ignored on load)
- User can re-install the mod and state is preserved
- If side-car file becomes stale or corrupted, absence of the mod means graceful degradation

**Recommendation:** Do NOT warn the user. Side-car data is intentionally optional. Document in mod README that removal is non-breaking.

For **critical save state** (e.g., equipment modifications, world changes), use ThingSaveData registration instead, which ensures the game will refuse to load if the mod is absent—forcing the user to restore the mod or acknowledge data loss.

## Verdict
<!-- verified: 0.2.6228.27061 @ 2026-04-21 -->

**Side-car approach is viable for optional cosmetic state (glow colors)** with these constraints:
1. Mod must inject side-car file on every save (Harmony Postfix on SaveHelper.Save)
2. Mod must read and re-apply state on every load (Thing.OnFinishedLoad Postfix)
3. Without mod, side-car file is harmlessly ignored; glow state is simply lost (desired behavior for optional features)
4. Enables removal without save breakage, unlike custom ThingSaveData which breaks loads when mod is absent

Not a replacement for critical save-breaking state (use ThingSaveData + registration for that). For glow-paint, this approach is superior to current GlowThingSaveData method because removal becomes non-fatal.

## Verification history

- 2026-04-21: full verification pass against Assembly-CSharp.dll v0.2.6228.27061. Decompiled SaveHelper.Save (ZipOutputStream write path with 5 known entries), XmlSaveLoad.LoadWorld (no ZIP enumeration), Thing.OnFinishedLoad, GameManager.UpdateThingsOnGameStartAction (caller), GameManager.AutoSaveNow (multiplayer guard), and SaveHelper.CopyToHeadSave (file copy, not rebuild). Decompiled LaunchPadBooster.dll and confirmed no save/load extension hooks. Resolved: (A) confirmed ZipOutputStream-based complete rebuild with single seal point at Finish(), (B) confirmed save-copy via binary file copy (unknown entries already absent), (C) no user warning needed for optional side-car data. Recommended Postfix on SaveHelper.Save as concrete Harmony pattern.
- 2026-04-21: added "SaveHelper.Save overload disambiguation" section after a live in-game test surfaced `HarmonyException: Ambiguous match for ... methodname=Save` at `PatchAll` time. Decompile verified `SaveHelper` declares two `Save` methods (public `(string, CancellationToken)` and private `(DirectoryInfo, string, bool, CancellationToken)`); every save path funnels through the private worker via `SaveGame` -> `Do*Save` dispatch. Documented the routing table and the required argument-type array in the `[HarmonyPatch]` attribute. Consequence of the bug: `PatchAll` throws on first ambiguous overload, so the ENTIRE mod's Harmony patches fail to apply, not just the ambiguous one.
- 2026-04-21: conflict on "how the game reads save ZIPs at load time". Previous claim: "LoadWorld uses `ZipArchive.GetEntry(name)` for known filenames only; unknown entries remain in the archive untouched." New finding: the game does NOT open a ZipArchive at LoadWorld time. `LoadHelper.LoadGameTask` calls `ExtractToTemp(path)` first, which uses `ZipInputStream.GetNextEntry()` to extract every entry (known and unknown) to a temp directory as loose files. `CurrentWorldSave.World.FullName` is `<tempDir>/world.xml`, not a path into a ZIP. Fresh validator verdict: B is correct (the new finding). Result: added a "Load-time ZIP extraction (LoadHelper.ExtractToTemp)" section with the full decompile of `LoadHelper.LoadGameTask` and `ExtractToTemp`, rewrote the "ZIP read path (LoadWorld)" section to clarify that `LoadWorld` reads loose files not a ZIP, and updated the "Harmony interception strategy" section to replace the (wrong) "open the temp file in Update mode" recipe with the verified wrapped-UniTask pattern shipped in SprayPaintPlus v1.6.0. The side-car-on-load recipe was also corrected: consume `<tempDir>/<entry-name>` as a loose file in the `XmlSaveLoad.LoadWorld` postfix; attempting to reopen the save ZIP produces `End of Central Directory record could not be found` because the original ZIP was closed by `ExtractToTemp`.

## Open questions

None at creation. All three original open questions resolved in the 2026-04-21 verification pass.
