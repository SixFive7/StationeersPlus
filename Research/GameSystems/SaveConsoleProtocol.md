---
title: Save console protocol
type: GameSystems
created_in: 0.2.6403.27689
verified_in: 0.2.6403.27689
verified_at: 2026-08-09
sources:
  - .work/decomp/0.2.6403.27689/Assembly-CSharp.decompiled.cs :: Util.Commands.SaveCommand (98726), CreateSave (98760), SaveTask (98777), NewSaveTask (98790)
  - .work/decomp/0.2.6403.27689/Assembly-CSharp.decompiled.cs :: Assets.Scripts.Serialization.SaveHelper (264734), SaveGame (264835), PrepareToSave (264943), private Save (264972), GetHeadSave (265106), SanitizeSaveName (264824)
  - .work/decomp/0.2.6403.27689/Assembly-CSharp.decompiled.cs :: Assets.Scripts.Serialization.SaveMethod (264704), SaveResult (264713), SaveLoadConstants.SaveFileExtension (265218)
  - .work/decomp/0.2.6403.27689/Assembly-CSharp.decompiled.cs :: Util.Commands.CommandScope (96671), CommandBase.EnforceScope (96702), CommandBase.CannotAsClient (96839)
  - .work/decomp/0.2.6403.27689/Assembly-CSharp.decompiled.cs :: Util.Commands.FileCommand (97808), Util.Commands.LoadGameCommand.NewGameTask (98922)
  - .work/decomp/0.2.6403.27689/Assembly-CSharp.decompiled.cs :: NetworkBase.AutoSaveOnLastClientLeave (39256), Assets.Scripts.Serialization.StationAutoSave.AutoSaveTask (267620)
  - .work/decomp/0.2.6403.27689/Assembly-CSharp.decompiled.cs :: Assets.Scripts.ConsoleWindow.Submit (222487)
related:
  - ./StationAutoSave.md
  - ./SaveZipExtension.md
  - ./DedicatedServerSettings.md
  - ./SettingsPersistence.md
  - ../GameClasses/ConsoleWindow.md
tags: [save-load, save-format, network]
---

# Save console protocol

Which console lines a world save emits, in what order, and which single line means the save actually succeeded. Written for tooling that drives a save and then greps a log to decide whether it completed, so the exact format strings matter and are quoted verbatim.

The short version: `Starting <Method> for <name>` means the request was ACCEPTED, not that it worked. Only `Saved <name>` (existing save) or `Created new save` (first save under that name) is a completion signal, and both are printed by the command layer, never by `SaveHelper` itself.

## The command chain

<!-- verified: 0.2.6403.27689 @ 2026-08-09 -->

`Assets.Scripts.ConsoleWindow.Submit(string)` (:222487-222491) is a direct pass into the command dispatcher, so any code that can call one method on the Unity main thread can drive a save:

```csharp
public static void Submit(string input)
{
	_consoleInput = input;
	Submit();
}

private static void Submit()
{
	_snapNextFrame = true;
	_lastTabResult = string.Empty;
	_tabMatches.Clear();
	if (string.IsNullOrEmpty(_consoleInput))
	{
		Hide();
		return;
	}
	Print(_consoleInput, ConsoleColor.Cyan, clearLine: false, aged: true, unformatted: true);
	CommandLine.Process(_consoleInput);
	_clearConsoleInput = true;
}
```

Note the echo: the submitted text itself is printed Cyan before dispatch, so `save "Foo"` appears in the log as its own line ahead of any save output.

`SaveCommand` (`Util.Commands.SaveCommand`, :98726) dispatches:

```csharp
public override string Execute(string[] args)
{
	if (!EnforceScope("save"))
	{
		return null;
	}
	if (args.Length == 0)
	{
		return CreateSave(XmlSaveLoad.Instance.CurrentStationName);
	}
	switch (args[0])
	{
	case "delete":
	case "rm":
	case "d":
		return DeleteSave(args);
	case "list":
	case "l":
		return ListSaves();
	default:
		return CreateSave(args[0]);
	}
}
```

`CreateSave` (:98760) is the fork between the new-save and existing-save paths:

```csharp
public static string CreateSave(string saveName)
{
	GameState gameState = GameManager.GameState;
	if (gameState != GameState.Running && gameState != GameState.Paused)
	{
		ConsoleWindow.PrintError($"Cannot save game in GameState '{GameManager.GameState}'.", suppressStacktrace: true);
		return null;
	}
	if (!FileCommand.GetStationDirectory(saveName).Exists)
	{
		NewSaveTask(saveName).Forget();
		return null;
	}
	SaveTask(saveName).Forget();
	return null;
}
```

`FileCommand.GetStationDirectory` (:97884) is a pure path build, no I/O beyond the existence test the caller does:

```csharp
public static DirectoryInfo GetStationDirectory(string folderName)
{
	return new DirectoryInfo($"{StationSaveUtils.GetSavePathSavesSubDir()}/{folderName}");
}
```

Both tasks are fire-and-forget (`.Forget()`), so `Execute` returns `null` immediately and every save line arrives asynchronously afterwards.

```csharp
private static async UniTaskVoid SaveTask(string stationName)          // :98777
{
	SaveResult saveResult = await SaveHelper.Save(stationName, default(CancellationToken));
	if (saveResult.Success)
	{
		ConsoleWindow.Print("Saved " + stationName);
	}
	else
	{
		ConsoleWindow.PrintError(saveResult.Message, suppressStacktrace: true);
	}
}

private static async UniTaskVoid NewSaveTask(string stationName)       // :98790
{
	stationName = SaveHelper.SanitizeSaveName(stationName);
	SaveResult saveResult = await SaveHelper.NewSave(stationName, default(CancellationToken));
	if (saveResult.Success)
	{
		XmlSaveLoad.Instance.CurrentStationName = stationName;
		ConsoleWindow.Print("Created new save");
	}
	else
	{
		ConsoleWindow.PrintError(saveResult.Message, suppressStacktrace: true);
	}
}
```

Asymmetry worth knowing: only the new-save path runs `SanitizeSaveName` and only the new-save path assigns `XmlSaveLoad.Instance.CurrentStationName`. A `save "<existing>"` never touches the current station name.

```csharp
public static string SanitizeSaveName(string saveName)                 // :264824
{
	string pattern = "[?:*<>|\\\\/\"]";
	return Regex.Replace(saveName, pattern, "_");
}
```

Because the directory-existence test runs on the RAW argument and the sanitize runs afterwards, a name containing any of those characters can test as "does not exist", get sanitized to a name that DOES exist, and then fail inside `DoNewSave` with `Save Failed: Could not create save directory.`

Both `SaveHelper.Save` and `SaveHelper.NewSave` are one-line wrappers onto the single dispatcher `SaveHelper.SaveGame` (:264835):

```csharp
public static async UniTask<SaveResult> NewSave(string stationName, CancellationToken cancellationToken)
{
	return await SaveGame(SaveMethod.NewSave, stationName, null, cancellationToken);
}

public static async UniTask<SaveResult> Save(string stationName, CancellationToken cancellationToken)
{
	return await SaveGame(SaveMethod.Save, stationName, null, cancellationToken);
}
```

The routing below `SaveGame` (`Do*Save` into the private `Save(DirectoryInfo, string, bool, CancellationToken)` worker at :264972) is documented in [SaveZipExtension](./SaveZipExtension.md).

## Console lines, verbatim and in order

<!-- verified: 0.2.6403.27689 @ 2026-08-09 -->

Head of `SaveHelper.SaveGame` (:264835-264848):

```csharp
private static async UniTask<SaveResult> SaveGame(SaveMethod saveMethod, string stationName, string saveFileName, CancellationToken cancellationToken)
{
	if (string.IsNullOrWhiteSpace(stationName))
	{
		return SaveResult.Fail("Save Failed: Folder name is empty.");
	}
	if (IsSaving)
	{
		return SaveResult.Fail("Save Failed: Already saving.");
	}
	ConsoleWindow.Print($"Starting {saveMethod} for {stationName}");
	SaveResult result = await PrepareToSave(cancellationToken);
	...
```

`saveMethod` is the `SaveMethod` enum (:264704), so the interpolation renders the enum member name:

```csharp
public enum SaveMethod
{
	Uninitialised,
	NewSave,
	Save,
	SaveAs,
	AutoSave,
	QuickSave
}
```

Every line a save can produce:

| Line (verbatim format) | Emitter | Meaning |
|---|---|---|
| `Starting Save for <name>` | `SaveHelper.SaveGame` :264844 | Request accepted. NOT a success. |
| `Starting NewSave for <name>` | `SaveHelper.SaveGame` :264844 | Same, on the first-save-under-this-name path. |
| `Starting AutoSave for <name>` | `SaveHelper.SaveGame` :264844 | Same, from the autosave timer or the last-client-leave hook. |
| `Starting QuickSave for <name>` / `Starting SaveAs for <name>` | `SaveHelper.SaveGame` :264844 | Same, from the quick-save and save-as paths. |
| `Saving - game tick paused in <n>ms` | `PrepareToSave` :264954 | Progress. Simulation is now parked. |
| `Saving - atmospheres cleaned up in <n>ms` | `PrepareToSave` :264960 | Progress. |
| `Saving - got world data in <n>ms` | private `Save` :264986 | Progress. |
| `Saving - unpausing game tick` | private `Save` :264993 | Progress. Simulation resumes BEFORE the ZIP is written. |
| `Saving - created preview images in <n>ms` | private `Save` :265003 | Progress. Non-batch only (`if (!GameManager.IsBatchMode)`), so a dedicated server never prints it. |
| `Saving - serialized, zipped and file created in <n>ms` | private `Save` :265038 | Last progress line. The `.save` file is on disk at this point, but the result has not yet reached the caller. |
| `Saved <name>` | `SaveCommand.SaveTask` :98782 | SUCCESS. Only printed after `SaveResult.Success`. |
| `Created new save` | `SaveCommand.NewSaveTask` :98797 | SUCCESS on the first save under that name. Carries no name. |
| `Cannot save game in GameState '<state>'.` | `SaveCommand.CreateSave` :98765 | Rejected before any save work. `PrintError`. |
| `Save Failed: Folder name is empty.` | `SaveGame` :264839 | Rejected. Reaches the console through the caller's `PrintError`. |
| `Save Failed: Already saving.` | `SaveGame` :264843 | Rejected, a save is in flight. |
| `Save Failed: Game must be running to save` | `PrepareToSave` :264947 | Second GameState check, inside the helper. Note: no trailing period. |
| `Save Failed: Save type invalid` | `SaveGame` :264859 | Unreachable from the console; `SaveMethod.Uninitialised` only. |
| `Save Failed: Could not create save directory.` | `DoNewSave` :264869 | New-save path, directory already exists or could not be made. |
| `Save Failed: Could not find directory with name <name>.` | `DoSave` :264879, `DoSaveAs` :264897 | |
| `Save Failed: Failed to get head save at <fullpath>.` | `DoSave` :264883, `DoSaveAs` :264902 | The folder holds zero `*.save` files. |
| `Save Failed: Save file name is empty.` | `DoSaveAs` :264893 | |
| `Save Failed: File name <name> is not unique.` | `DoSaveAs` :264906 | |
| `Failed to write save file at path <path> : <exception>` | private `Save` :265031 | The ZIP write itself threw. |

Every `Save Failed: ...` string and the `Failed to write save file ...` string is a `SaveResult.Message`, not a direct print. It reaches the console only because a caller passes it to `ConsoleWindow.PrintError(saveResult.Message, suppressStacktrace: true)`. A caller that discards the result prints nothing at all (see the last-client-leave hook below).

Two grep hazards:

- `Starting AutoSave for <name>` contains the substring `Save for `, so a loose pattern such as `Save for` or `Starting.*Save for` matches autosaves as well as manual saves. Match the exact prefix `Starting Save for ` when the intent is "the manual save I asked for".
- On a batch-mode process every one of these lines is prefixed with a timestamp by `ConsoleWindow.Print` (`output = $"{DateTime.Now:HH:mm:ss}: {output}"`), so anchor patterns to the end or the middle of the line, not to the start. See [ConsoleWindow](../GameClasses/ConsoleWindow.md).

## Autosave cannot produce a false confirmation

<!-- verified: 0.2.6403.27689 @ 2026-08-09 -->

Neither autosave path can print `Saved <name>` or `Created new save`, so a harness that waits for one of those two lines cannot be fooled by an autosave that happens to land during the wait.

There are exactly two autosave drivers, and both call `SaveHelper.AutoSave` directly rather than going through `SaveCommand`.

Timer-driven, `StationAutoSave.AutoSaveTask` (:267620-267627):

```csharp
private static async UniTaskVoid AutoSaveTask()
{
	SaveResult saveResult = await SaveHelper.AutoSave(XmlSaveLoad.Instance.CurrentStationName, default(CancellationToken));
	if (!saveResult.Success)
	{
		ConsoleWindow.PrintError(saveResult.Message);
	}
}
```

Only the failure branch prints. See [StationAutoSave](./StationAutoSave.md) for the timer, the fire-time gates, and the `Save Failed: Folder name is empty.` loop a fresh `-new` world produces.

Last-client-leave, `NetworkBase.AutoSaveOnLastClientLeave` (:39256-39268), reached from `OnClientRemoved` (:39247) when `GameManager.IsBatchMode && Settings.CurrentData.AutoPauseServer && Clients.Count <= 0`:

```csharp
private static async UniTaskVoid AutoSaveOnLastClientLeave(CancellationToken cancellationToken)
{
	ConsoleWindow.PrintAction("No clients connected. Will save and pause in 10 seconds.");
	await UniTask.Delay(10000, ignoreTimeScale: false, PlayerLoopTiming.Update, cancellationToken);
	if (!cancellationToken.IsCancellationRequested)
	{
		await SaveHelper.AutoSave(XmlSaveLoad.Instance.CurrentStationName, cancellationToken);
		if (!cancellationToken.IsCancellationRequested)
		{
			ConsoleWindow.PrintAction("Server Paused");
			WorldManager.SetGamePause(pauseGame: true);
		}
	}
}
```

This one discards the `SaveResult` entirely, so it prints neither a success nor a failure line. Its two observable markers are `No clients connected. Will save and pause in 10 seconds.` and `Server Paused`, plus the `Starting AutoSave for <name>` line from `SaveGame`. A failed save here is completely silent, and `Server Paused` still prints.

What autosave DOES share with a manual save: the `Starting AutoSave for ...` line and every `Saving - ...` progress line, because all of them live in `SaveGame` / `PrepareToSave` / the private `Save`. Only the terminal confirmation differs.

## Other emitters of the same two success lines

<!-- verified: 0.2.6403.27689 @ 2026-08-09 -->

`Saved <name>` and `Created new save` are not unique to `SaveCommand`. Both strings appear at four sites each, all gated on `saveResult.Success`:

| Line | Site | Reached by |
|---|---|---|
| `Saved <name>` | `SaveCommand.SaveTask` :98782 | `save [name]` |
| `Saved <name>` | `FileCommand.SaveTask` :98024 | `file save` |
| `Saved <name>` | `FileCommand.SaveAsTask` :98056 | `file saveas <savename>` |
| `Saved <name>` | `FileCommand.QuickSaveTask` :98084 | `file quicksave` |
| `Created new save` | `SaveCommand.NewSaveTask` :98797 | `save <newname>` |
| `Created new save` | `FileCommand.NewSaveTask` :97996 | `file new <stationname>` |
| `Created new save` | `FileCommand.NewGameTask` :97967 | `file start <stationname> ...` |
| `Created new save` | `LoadGameCommand.NewGameTask` :98931 | `load <name> <worldid>` where `<name>` does not resolve to an existing save |

The last row is the one that surprises: a `-load <SaveName> <Map>` launch that falls through to starting a new world finishes by saving and printing `Created new save`, preceded by `Started new game. Saving...` (`ConsoleWindow.PrintAction`, :98925). So on a dedicated server, `Created new save` is a normal world-creation line, not necessarily a response to a `save` command.

Two other `Saved <name>` strings exist in the assembly and are unrelated to world saves: `Saved <x> successfully` at :61619 and :448833. Anchoring on the exact `"Saved " + stationName` form avoids both.

`FileCommand` also carries `forceallowsave` (:97851 dispatch table), which calls the `SaveHelper.ForceIsSavingToFalse()` escape hatch (:264830) for a stuck `Save Failed: Already saving.` state.

## Where the file lands, and the head-save rule

<!-- verified: 0.2.6403.27689 @ 2026-08-09 -->

```csharp
public static readonly string SaveFileExtension = ".save";     // SaveLoadConstants :265218
```

`DoNewSave` (:264864) names the file `stationName + ".save"` inside a freshly created station directory. `DoSave` (:264874) instead overwrites the existing HEAD save, whose name it resolves through `GetHeadSave` (:265106):

```csharp
private static bool GetHeadSave(DirectoryInfo stationDirectory, out FileInfo headSaveFile)
{
	FileInfo[] files = stationDirectory.GetFiles(SaveLoadConstants.SaveFileSearchPattern);
	if (files.Length < 1)
	{
		headSaveFile = null;
		return false;
	}
	headSaveFile = files[0];
	return true;
}
```

`files[0]` with no sort. `DirectoryInfo.GetFiles` ordering is not guaranteed by the framework, so a station folder containing more than one top-level `*.save` file has an undefined head. The only guard is `Length < 1`, which yields `Save Failed: Failed to get head save at <fullpath>.` Keep exactly one `*.save` at the top level of a station folder; the rolling autosave and quicksave files live in the `AutoSaveFolder` / `QuickSaveFolder` subdirectories created by `CreateSaveDirectory` (:264782), not next to the head save.

This is the same one-file invariant the `load` command enforces from the other side, documented in [SaveZipExtension](./SaveZipExtension.md) under "Save folder layout and the `load` command's resolution".

## Scope: `save` is refused on a remote client

<!-- verified: 0.2.6403.27689 @ 2026-08-09 -->

`SaveCommand.Scope => CommandScope.HostOrSinglePlayer` (:98734), and the first statement of `Execute` is `if (!EnforceScope("save")) return null;`.

```csharp
public enum CommandScope                    // :96671
{
	None = 0,
	InGame = 1,
	HostOrSinglePlayer = 2,
	MultiplayerOnly = 4,
	SinglePlayerOnly = 8,
	CreativeOnly = 0x10
}
```

```csharp
protected bool EnforceScope(string key)     // :96702
{
	CommandScope scope = Scope;
	if ((scope & CommandScope.HostOrSinglePlayer) != CommandScope.None && CannotAsClient(key))
	{
		return false;
	}
	if ((scope & CommandScope.MultiplayerOnly) != CommandScope.None && CannotInSinglePlayer(key))
	{
		return false;
	}
	if ((scope & CommandScope.SinglePlayerOnly) != CommandScope.None && (Assets.Scripts.Networking.NetworkManager.IsClient || Assets.Scripts.Networking.NetworkManager.IsServer))
	{
		ConsoleWindow.PrintError("cannot use '" + key + "' while in multiplayer", suppressStacktrace: true);
		return false;
	}
	...
```

```csharp
protected static bool CannotAsClient(string commandKey)     // :96839
{
	if (Assets.Scripts.Networking.NetworkManager.IsActiveAsClient)
	{
		ConsoleWindow.PrintError(ConsoleStrings.Error.CannotAsClient.AsString(commandKey.ToLower()), suppressStacktrace: true);
		return true;
	}
	return false;
}
```

The predicate is `NetworkManager.IsActiveAsClient`. A listen host and a single-player session are both allowed; only a process joined to someone else's server is refused. The rejection message is a localized `ConsoleStrings.Error.CannotAsClient` template with the lower-cased command key substituted in, so its English text is not a compile-time constant and should not be matched on. `FileCommand.Execute` (:97850) applies the same gate by calling `CommandBase.CannotAsClient("save")` directly rather than through `EnforceScope`.

To make a joined client save, route the command through `serverrun` so the host runs it; see [DedicatedServerSettings](./DedicatedServerSettings.md).

## Verification history

- 2026-08-09: page created. Every format string, line number and code excerpt read directly from `.work/decomp/0.2.6403.27689/Assembly-CSharp.decompiled.cs` in this pass. Established that `Starting <Method> for <name>` is an acceptance marker rather than a success marker, that `Saved <name>` and `Created new save` are the only completion signals and are printed exclusively by the command layer after `SaveResult.Success`, and that neither autosave driver (`StationAutoSave.AutoSaveTask`, `NetworkBase.AutoSaveOnLastClientLeave`) can print either one. Additive page: no existing verified claim was changed. Two corrections to the source material this page was written from are recorded here rather than silently dropped: `SaveHelper.SaveGame` is at :264835, not :264979 (that line is inside the private `Save` worker); and `SaveCommand.CreateSave` reaches `SaveGame` through `SaveHelper.Save` / `SaveHelper.NewSave`, not directly.

## Open questions

- Whether `DirectoryInfo.GetFiles` returns entries in a stable order on NTFS in practice. `GetHeadSave` takes `files[0]` with no sort, so a station folder with two top-level `*.save` files has an undefined head. Not worth relying on either way; keep one file.
- Whether stdin console commands reach `CommandLine.Process` on a batch-mode dedicated server. [StationAutoSave](./StationAutoSave.md) records two separate observations (0.2.6228.27061 and 0.2.6403.27689) of a queued `save` producing no output and no save folder. That is a transport question about the batch-mode stdin reader, not about the chain on this page, and it is unresolved.
