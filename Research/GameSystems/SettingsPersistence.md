---
title: Settings persistence
type: GameSystems
created_in: 0.2.6403.27689
verified_in: 0.2.6403.27689
verified_at: 2026-08-09
sources:
  - .work/decomp/0.2.6403.27689/Assembly-CSharp.decompiled.cs :: Assets.Scripts.Serialization.Settings (265338), SaveSettings (266578), SettingData.Path (265688), SettingData.Save (265704), SettingData.Load (265709), ValidateSavePath (266871), OnInitComplete (265904), CancelSetting (266307), OnValueChanged(SettingType) (266901)
  - .work/decomp/0.2.6403.27689/Assembly-CSharp.decompiled.cs :: StationSaveUtils (48571), DefaultPath (48577), GetSavePath (48599)
  - .work/decomp/0.2.6403.27689/Assembly-CSharp.decompiled.cs :: Util.Commands.SettingsCommand (101942), SettingsPathCommand (101969), ClassManipulator<T> (96357), LegacyCpuCommand (99015)
  - .work/decomp/0.2.6403.27689/Assembly-CSharp.decompiled.cs :: WorldManager.ManagerAwake (59699), GameManager.QuitGame (204532), WorldManager.OnApplicationQuit (205221), QuitCommand (100907), HelperHintsTextController.AutoExpand (259680)
  - .work/decomp/0.2.6403.27689/StationeersLaunchPad.decompiled.cs :: CustomSavePathPatches.StationSaveUtils_DefaultPath (2537-2561)
related:
  - ./DedicatedServerSettings.md
  - ./ListenHost.md
  - ./KeyBinding.md
  - ./SaveConsoleProtocol.md
  - ../Workflows/StationeersLaunchPadDedicatedServer.md
tags: [save-load, launchpad, network]
---

# Settings persistence

When `setting.xml` is actually written to disk, and when a change to `Settings.CurrentData` is purely in-memory. This is the difference between a settings tweak that survives the next launch and one that does not, and it is asymmetric in a way that is easy to get wrong: the `settings` console command persists everything, a direct field write persists nothing, and quitting the game persists nothing either.

The headline for anyone driving a game process programmatically: **`settings <Prop> <Value>` writes the ENTIRE settings blob to disk immediately.** A test harness that flips `StartLocalHost` that way has changed the instance's next boot, not just the current session.

## Everything funnels through `SettingData.Save()`

<!-- verified: 0.2.6403.27689 @ 2026-08-09 -->

`Settings` is `public class Settings : UserInterfaceBase` (:265338) in namespace `Assets.Scripts.Serialization`. The on-disk write is one method on the nested `SettingData`:

```csharp
public void Save()                     // :265704
{
	this.SaveXml(Path);
}
```

`SaveXml(Path)` is the only file write in the settings system. It has exactly two callers assembly-wide:

- `Settings.SaveSettings()` at :266596 (the normal route)
- `HelperHintsTextController.AutoExpand(bool)` at :259682 (a direct call that bypasses `SaveSettings`, see below)

`Settings.SaveSettings()` (:266578-266597), verbatim:

```csharp
public static void SaveSettings()
{
	CurrentData.SettingsVersion = GameManager.GetGameVersion();
	CurrentData.VerticalLookAxis = ControllerMap.VerticalLook.Serialize();
	CurrentData.VerticalMovementAxis = ControllerMap.VerticalMovement.Serialize();
	CurrentData.HorizontalLookAxis = ControllerMap.HorizontalLook.Serialize();
	CurrentData.HorizontalMovementAxis = ControllerMap.HorizontalMovement.Serialize();
	CurrentData.ForwardMovementAxis = ControllerMap.ForwardMovement.Serialize();
	CurrentData.KeyList = KeyManager.AllKeys;
	ApplyPixelLightCount();
	if (StatusUpdates.Save(out var data))
	{
		CurrentData.VoiceNotifications = data;
	}
	if (CurrentData.UserSpeakerMode == AudioSpeakerMode.Raw)
	{
		CurrentData.UserSpeakerMode = AudioSpeakerMode.Stereo;
	}
	CurrentData.Save();
}
```

Two consequences:

- **It serialises the whole `SettingData`, not the field that changed.** Every one of the 80-plus fields catalogued in [DedicatedServerSettings](./DedicatedServerSettings.md) goes to disk on every call. Any earlier in-memory edit to an unrelated field is persisted as a side effect.
- **It harvests live state before writing.** `SettingsVersion` is overwritten with the current game version, `KeyList` is replaced with `KeyManager.AllKeys`, the five controller axes are re-serialised from `ControllerMap`, and `UserSpeakerMode == Raw` is silently rewritten to `Stereo`. So calling it does not just persist `CurrentData`, it mutates `CurrentData` first. The `KeyList` half of this is the mechanism [KeyBinding](./KeyBinding.md) relies on.

## The six call sites of `SaveSettings()`

<!-- verified: 0.2.6403.27689 @ 2026-08-09 -->

A whole-file grep for `SaveSettings(` returns seven lines: the declaration at :266578 and six calls. The list is exhaustive at this game version.

| Line | Caller | Fires when |
|---|---|---|
| :59699 | `WorldManager.ManagerAwake` | First run only, and only outside batch mode. |
| :99047 | `LegacyCpuCommand.Execute` | The `legacycpu enable` / `legacycpu disable` console command. |
| :101952 | `SettingsCommand.OnValueChanged` | Every `settings <Prop> <Value>` set, from the console AND from a `-settings` launch flag. |
| :266309 | `Settings.CancelSetting` | Closing the settings panel. |
| :266889 | `Settings.ValidateSavePath` | ONLY when the configured `SavePath` is non-empty and not writable. |
| :267329 | `Settings.OnValueChanged(SettingType)` | Any settings-panel widget change. |

### :59699 first run, non-batch only

`WorldManager.ManagerAwake` (`WorldManager` at :59063), verbatim:

```csharp
if (!GameManager.IsBatchMode)
{
	_sparkerMainModule = Sparker.main;
	_sparkerTransform = Sparker.transform;
	if (Settings.CurrentData.FirstRun)
	{
		Settings.CurrentData.FirstRun = false;
		Settings.SaveSettings();
		if (Settings.GetIsAMDGPU())
		{
			DriverWarningScreen.SetActive(value: true);
		}
	}
}
```

Both guards matter. A dedicated server never reaches it because of `IsBatchMode`, and a client reaches it exactly once, on the boot where `FirstRun` is still `true`.

### :101952 the `settings` command, launch flag and runtime alike

```csharp
internal class SettingsCommand : ClassManipulator<Settings.SettingData>     // :101942
{
	public override string HelpText => "Reads or writes values in settings.xml at runtime. Use 'list' to enumerate property names, 'print' to dump values, '<PropertyName>' to read one, or '<PropertyName> <Value>' to set one (e.g. 'settings ServerMaxPlayers 5').";

	protected override Settings.SettingData ObjectInstance => Settings.CurrentData;

	protected override void OnValueChanged()
	{
		EnsureExistence();
		Assets.Scripts.Networking.NetworkManager.UpdateSessionData(ObjectInstance);
		Settings.SaveSettings();
	}

	private static void EnsureExistence()
	{
		FileInfo fileInfo = new FileInfo(Settings.SettingData.Path);
		DirectoryInfo directory = fileInfo.Directory;
		if (directory != null && !directory.Exists)
		{
			fileInfo.Directory.Create();
		}
		if (!fileInfo.Exists)
		{
			fileInfo.Create();
		}
	}
}
```

`SettingsCommand` declares no `IsLaunchCmd` of its own; it inherits `public override bool IsLaunchCmd => true;` from `ClassManipulator<T>` (:96363). So `-settings Foo Bar` on the launch line and `settings Foo Bar` typed at runtime are the same code path, and both persist. The multi-pair form (`settings A 1 B 2`, `ClassManipulator.Execute` default case at :96390-96403) calls `SetNewValue` once per pair, so it writes the file once per pair.

`EnsureExistence` runs before the write and creates the parent directory and an empty file if either is missing, which is why a `-settingspath` pointing at a not-yet-existing location still works.

### :266889 ValidateSavePath, the failure branch only

This one is easy to misread as "every boot". It is not.

```csharp
public static bool ValidateSavePath()                        // :266871
{
	string savePath = CurrentData.SavePath;
	if (string.IsNullOrEmpty(savePath))
	{
		return false;
	}
	savePath = savePath.SanitizePath();
	try
	{
		string path = $"{savePath}/{Guid.NewGuid()}.tmp";
		File.Create(path).Dispose();
		File.Delete(path);
	}
	catch
	{
		CurrentData.SavePath = string.Empty;
		StationSaveUtils.GetSavePath();
		SaveSettings();
		return true;
	}
	return false;
}
```

The method probes writability by creating and deleting a GUID-named `.tmp` file. `SaveSettings()` sits in the CATCH block, so it runs only when the probe throws, and the `true` return means "the save path was bad and has been reset to the default", not "all good". On a normal boot with a writable `SavePath` the method returns `false` and writes nothing; with an empty `SavePath` it returns `false` before doing anything at all.

The boot-time caller is `Settings.OnInitComplete` (:265904), hooked in `Settings.Awake` (:265892):

```csharp
public void Awake()
{
	WorldManager.OnGameDataLoaded = (Action)Delegate.Combine(WorldManager.OnGameDataLoaded, new Action(OnInitComplete));
}

public static void OnInitComplete()
{
	if (ValidateSavePath())
	{
		Instance.ShowWarningSavePathNotWritable();
		GetInputField(SettingType.SavePath).text = CurrentData.SavePath;
	}
}
```

`WorldManager.OnGameDataLoaded` is invoked at :59844. Two further `ValidateSavePath` callers exist, both in the settings panel: the folder-browser path (:266365) and the `SettingType.SavePath` input-field handler (:266940).

### :267329 the settings panel

`Settings.OnValueChanged(SettingType settingType)` (:266901) is the panel-wide widget handler. Its tail, after the per-setting switch:

```csharp
			Assets.Scripts.Networking.NetworkManager.UpdateSessionData(CurrentData);
			SaveSettings();
		}
		catch (System.Exception exception)
		{
			UnityEngine.Debug.LogException(exception);
		}
```

`Settings.CancelSetting` (:266307) is the other panel route and is reached from `CloseSetting()` (:266295):

```csharp
public void CancelSetting()
{
	SaveSettings();
	WorldManager.Instance.UpdateFrameRate();
}
```

Despite the name, `CancelSetting` SAVES. There is no discard-changes path.

## The seventh writer: `HelperHintsTextController.AutoExpand`

<!-- verified: 0.2.6403.27689 @ 2026-08-09 -->

`SaveSettings()` is not the only way `setting.xml` gets written. `HelperHintsTextController` (:259514) calls `SettingData.Save()` directly (:259680-259683):

```csharp
private void AutoExpand(bool value)
{
	Settings.CurrentData.AutoExpandHelperHints = value;
	Settings.CurrentData.Save();
}
```

This bypasses the whole `SaveSettings()` preamble, so it does NOT refresh `SettingsVersion`, `KeyList`, or the controller axes before writing. It still serialises the entire `SettingData`, because `Save()` writes the whole object. Practical effect: toggling helper-hint auto-expand persists every other pending in-memory settings edit too, while leaving a stale `SettingsVersion` on disk if the game version changed since the last real `SaveSettings()`.

## Nothing writes settings on quit

<!-- verified: 0.2.6403.27689 @ 2026-08-09 -->

None of the six `SaveSettings()` call sites, and neither `SettingData.Save()` call site, is on a shutdown path. That is the whole proof, since those eight sites are the complete write surface. The individual quit paths confirm it.

`GameManager.QuitGame()` (:204532-204540), verbatim:

```csharp
public static void QuitGame()
{
	Assets.Scripts.Networking.NetworkManager.Close();
	GameState = GameState.None;
	World.CurrentId = null;
	if (!Application.isEditor)
	{
		Process.GetCurrentProcess().Kill();
	}
}
```

`Process.GetCurrentProcess().Kill()` does not run finalizers, `OnApplicationQuit`, or `Application.quitting` handlers, so anything hoping to flush at shutdown does not even get invoked on this route.

`WorldManager.OnApplicationQuit()` (:205221-205224), verbatim:

```csharp
public override void OnApplicationQuit()
{
	StationAutoSave.Cancel();
}
```

The `quit` / `exit` / `leave` console command goes through `Application.Quit()` rather than `QuitGame`:

```csharp
public class QuitCommand : CommandBase                       // :100907
{
	public override string HelpText => "Immediately quits the game without prompting or saving.";
	...
	public override string Execute(string[] args)
	{
		ConsoleWindow.PrintAction("Quitting the game.");
		Application.Quit();
		return null;
	}
}
```

`Application.quitting` has two subscribers in the whole assembly, `ConsoleWindow.Shutdown` (:221928, unsubscribes the Unity log bridge) and `StationAutoSave.Cancel` (:267582, cancels the autosave timer). Neither writes settings. The help text says it plainly: quits without saving.

Consequence for a driven process: an in-memory-only settings change is lost on exit with no warning and no log line, and there is no supported "flush settings now" call other than `Settings.SaveSettings()` itself.

## `SettingData.Path` is a settable static, and it is a file path

<!-- verified: 0.2.6403.27689 @ 2026-08-09 -->

```csharp
[XmlIgnore]
private static string _path;                                 // :265685

[XmlIgnore]
public static string Path                                    // :265688
{
	get
	{
		if (string.IsNullOrEmpty(_path))
		{
			_path = System.IO.Path.Combine(StationSaveUtils.GetSavePath(), "setting.xml");
		}
		return _path;
	}
	set
	{
		_path = value;
	}
}
```

The lazy getter is already documented on [DedicatedServerSettings](./DedicatedServerSettings.md) and [KeyBinding](./KeyBinding.md). The addition here is the **setter**, which is what makes `-settingspath` work and what a mod or harness would use to redirect settings without touching `SavePath`:

```csharp
internal class SettingsPathCommand : CommandBase              // :101969
{
	public override string HelpText => "Overrides the path to settings.xml. Launch command only; falls back to the default location if not provided.";

	public override string[] Arguments => new string[1] { "<full-directory-path>" };

	public override bool IsLaunchCmd => true;

	public override string Execute(string[] args)
	{
		if (args.Length < 1)
		{
			return "Invalid syntax";
		}
		FileInfo fileInfo = new FileInfo(args[0]);
		Settings.SettingData.Path = fileInfo.FullName;
		ConsoleWindow.PrintAction("Set custom settings path: " + fileInfo.FullName + ".");
		return null;
	}
}
```

The argument is wrapped in `new FileInfo(args[0])` and `FullName` is assigned, so despite the `<full-directory-path>` argument label and the help text, the value is the path to the `setting.xml` FILE, not the folder containing it. Passing a directory produces a settings path with no file name. Confirmed still true at 0.2.6403.27689, matching the note already on [DedicatedServerSettings](./DedicatedServerSettings.md).

Because `_path` caches on first read, the setter only takes effect if it runs before anything reads `Path`. That is why `IsLaunchCmd => true` and the help text says launch command only.

## Where the default lands: `StationSaveUtils`

<!-- verified: 0.2.6403.27689 @ 2026-08-09 -->

`StationSaveUtils` is `public static class StationSaveUtils` at :48571, in the **global namespace** (no `namespace` block encloses it), unlike `Settings` / `SaveHelper` / `XmlSaveLoad`, which are in `Assets.Scripts.Serialization`. See [StationeersNamespaces](../Patterns/StationeersNamespaces.md).

```csharp
public static string DefaultPath                             // :48577
{
	get
	{
		if (!GameManager.IsBatchMode)
		{
			return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "My Games", "Stationeers");
		}
		return ExeDirectory.FullName;
	}
}
```

`ExeDirectory` is `new DirectoryInfo(Application.dataPath).Parent` (:48575).

`GetSavePath()` (:48599) resolves the effective root and has a side effect worth knowing:

```csharp
public static string GetSavePath()
{
	if (string.IsNullOrEmpty(Settings.CurrentData.SavePath))
	{
		Settings.CurrentData.SavePath = DefaultPath;
	}
	string savePath = Settings.CurrentData.SavePath;
	...
```

It WRITES `Settings.CurrentData.SavePath` in memory when the field is empty, and does not persist that write. So an empty `SavePath` becomes a concrete path in `CurrentData` on first use, and the next unrelated `SaveSettings()` (from any of the six call sites) bakes that resolved path into `setting.xml`. It also creates `saves`, `scripts` and `mods` subdirectories under the root, and on `UnauthorizedAccessException` falls back to `DefaultPath` after printing `Unauthorized Access: path(<x>) cannot be accessed. Falling back to default path(<y>)`.

### StationeersLaunchPad prefixes the getter

<!-- verified: 0.2.6403.27689 @ 2026-08-09 -->

`StationeersLaunchPad.decompiled.cs:2537-2561`, verbatim:

```csharp
internal static class CustomSavePathPatches
{
	public static string SavePath;

	[HarmonyPatch(typeof(StationSaveUtils), "DefaultPath", MethodType.Getter)]
	[HarmonyPrefix]
	private static bool StationSaveUtils_DefaultPath(ref string __result)
	{
		if (string.IsNullOrEmpty(SavePath))
		{
			return true;
		}
		if (Path.IsPathRooted(SavePath))
		{
			__result = SavePath;
		}
		else if (Platform.IsServer)
		{
			__result = Path.Combine(StationSaveUtils.ExeDirectory.FullName, SavePath);
		}
		else
		{
			__result = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "My Games", SavePath);
		}
		return false;
	}
}
```

When `CustomSavePathPatches.SavePath` is set and rooted, the vanilla getter never runs (`return false`) and `DefaultPath` is that value verbatim. When it is set and relative, the base differs by build: the exe directory on a server, `Documents\My Games` on a client (note: `My Games\<SavePath>`, not `My Games\Stationeers\<SavePath>`). When it is empty the prefix returns `true` and vanilla behaviour is unchanged. This is the mechanism behind the client rig's per-instance `SavePathOverride`.

## Why this matters for a driven process

<!-- verified: 0.2.6403.27689 @ 2026-08-09 -->

Two routes to the same in-memory value have opposite persistence behaviour:

| Route | In-memory | Written to `setting.xml` |
|---|---|---|
| `settings StartLocalHost true` (console or `-settings` launch flag) | yes | YES, the entire blob, immediately |
| `Settings.CurrentData.StartLocalHost = true` from mod code | yes | no |
| Settings-panel widget or closing the panel | yes | YES, the entire blob |
| Quitting the game by any route | n/a | no |

The failure mode that motivated this page: a harness that promotes a client to host by submitting `settings StartLocalHost true` through `ConsoleWindow.Submit` has also armed that instance to host on its NEXT boot, silently, because the value is now on disk. A direct field write to `Settings.CurrentData` gives the same session behaviour with no persistence, which is what a one-shot test usually wants. If the instance has its own `-settingspath`, the blast radius is that one file; without one, the write lands in the shared `<SavePath>/setting.xml`.

The inverse trap is just as real. Any in-memory field poked earlier in the session is persisted by the next `SaveSettings()` from ANY of the six call sites, including ones the harness did not trigger deliberately (a settings-panel close, a `legacycpu` command, an unwritable-`SavePath` reset). There is no per-field write.

`ConsoleWindow.Submit(string)` is the entry point for the console route; see [SaveConsoleProtocol](./SaveConsoleProtocol.md) for its body and the echo it prints, and [ListenHost](./ListenHost.md) for the `StartLocalHost` boot chain itself.

## Verification history

- 2026-08-09: page created. All call sites enumerated by whole-file grep against `.work/decomp/0.2.6403.27689/Assembly-CSharp.decompiled.cs` and each one read in context. Additive page; no existing verified claim on another page was changed. It does extend two: [DedicatedServerSettings](./DedicatedServerSettings.md) and [KeyBinding](./KeyBinding.md) both quote the `SettingData.Path` GETTER only, and neither records the public setter that `-settingspath` assigns through. Three corrections to the source material this page was written from are recorded rather than dropped. First, `ValidateSavePath()` does NOT write settings on every boot: `SaveSettings()` is inside its catch block, so it fires only when a non-empty `SavePath` fails a write probe, and the `true` return is the failure signal. Second, `SaveSettings()` is not the only writer of `setting.xml`; `HelperHintsTextController.AutoExpand` (:259680) calls `SettingData.Save()` directly, making eight write sites in total rather than six. Third, `GameManager.QuitGame()` also clears `World.CurrentId` before the `Process.Kill()`.

## Open questions

- Whether `Settings.Awake` runs on a batch-mode dedicated server. `Settings : UserInterfaceBase` is a MonoBehaviour, so `OnInitComplete` is only hooked to `WorldManager.OnGameDataLoaded` if that component exists in the loaded scene, and `OnInitComplete`'s body dereferences `Instance` and `GetInputField(...)`. Not established this pass; it only matters for the unwritable-`SavePath` reset path, which writes settings.
- Whether `NetworkManager.UpdateSessionData(CurrentData)`, called immediately before `SaveSettings()` at both :101951 and :267328, has any persistence effect of its own. Read only as far as confirming it is not a file write.
