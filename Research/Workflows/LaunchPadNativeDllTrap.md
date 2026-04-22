---
name: LaunchPadNativeDllTrap
description: StationeersLaunchPad aborts the entire mod load on the first native (non-managed) DLL it finds in the mod folder. Implications for mods with NuGet packages that bundle native libraries (LLamaSharp, SkiaSharp, SQLite, etc.).
type: Workflow
created_in: 0.2.6228.27061
verified_in: 0.2.6228.27061
---

# LaunchPad native-DLL trap

<!-- verified: 0.2.6228.27061 @ 2026-04-22 -->

Short version: StationeersLaunchPad's assembly loader recursively calls `Assembly.LoadFrom` on every `.dll` under the mod deploy folder. The first native (C/C++) DLL it hits throws `BadImageFormatException`, which aborts the entire mod load. The plugin's `Awake()` never runs. No error reaches `BepInEx\LogOutput.log`; the full trace lands only in the Unity Player log.

## Symptoms

- BepInEx log (`BepInEx\LogOutput.log`) shows only a shim warning:

  ```
  [Warning:   BepInEx] Failed to shim <...>\runtimes\win-x64\native\ggml.dll:
    System.BadImageFormatException: Format of the executable (.exe) or library (.dll) is invalid.
  ```

  This line is cosmetic; BepInEx's Chainloader tolerates non-managed DLLs and continues.

- Unity Player log (`%USERPROFILE%\AppData\LocalLow\Rocketwerkz\rocketstation\Player.log`) shows LaunchPad walking the mod folder, loading every managed DLL successfully, then failing on the first native DLL:

  ```
  [Global]: new mod added at <deploy path>
  [ModDisplayName]: Loading Assembly <path>\<ManagedDll>.dll
  [ModDisplayName]: Loaded Assembly
  ... (one pair per managed DLL)
  [ModDisplayName]: Loading Assembly <path>\runtimes\win-x64\native\ggml.dll
  BadImageFormatException: Invalid Image: <...>\ggml.dll
    at ... System.Reflection.Assembly.LoadFile_internal(...)
    at StationeersLaunchPad.Loading.LoadedMod+<>c__DisplayClass15_0.<LoadAssemblySingle>b__0 () at /_/StationeersLaunchPad/Loading/LoadedMod.cs:49
    at StationeersLaunchPad.Loading.LoadedMod.LoadAssembliesSerial () at /_/StationeersLaunchPad/Loading/LoadedMod.cs:58
    at StationeersLaunchPad.Loading.LoadStrategyLinearSerial.LoadAssemblies () at /_/StationeersLaunchPad/Loading/LoadStrategy.cs:83
  StationeersLaunchPad.Loading.LoadStrategy:LoadFailed(LoadedMod, Exception) at /_/StationeersLaunchPad/Loading/LoadStrategy.cs:60
  ```

- The plugin's `Awake()` never runs. Harmony patches never apply. BepInEx settings entries never bind.

## Source

StationeersLaunchPad 0.3.1. Stack trace captured from Player.log during the first MaintenanceBureauPlus deploy attempt. The relevant LaunchPad entry points are `StationeersLaunchPad.Loading.LoadedMod.LoadAssemblySingle` at `LoadedMod.cs:49` and `LoadedMod.LoadAssembliesSerial` at `LoadedMod.cs:58`.

## Why it happens

LaunchPad recursively scans the mod's deploy folder for `*.dll` and calls `Assembly.LoadFrom` on each one. `Assembly.LoadFrom` throws `BadImageFormatException` for any file that is not a managed IL image, including every native C/C++ library. `LoadAssembliesSerial` does not catch per-file; it aborts the whole mod on the first exception.

This is specifically a LaunchPad constraint, not a BepInEx constraint. BepInEx's own Chainloader calls `HarmonyInterop.TryShimInternal` which catches `BadImageFormatException` and emits only a warning.

## Implication for mods

Any mod whose NuGet package graph bundles native DLLs under `runtimes/<rid>/native/` or adjacent to a managed DLL will hit this wall. Examples:

- **LLamaSharp** (`runtimes/win-x64/native/ggml.dll`, `llama.dll`, `llava_shared.dll`)
- **SkiaSharp** (`runtimes/win-x64/native/libSkiaSharp.dll`)
- **Microsoft.Data.Sqlite** (`runtimes/win-x64/native/e_sqlite3.dll`)
- **SIPSorcery**, **Magick.NET**, **LibVLCSharp**, and anything else that wraps a native library on Windows.

The managed NuGet package itself (e.g. `LLamaSharp.dll`) loads fine. The native runtimes do not.

## Workarounds (from cheapest to cleanest)

### 1. Ship through BepInEx only (skip LaunchPad)

Deploy the entire mod to `BepInEx/plugins/<ModName>/` instead of `<user>/Documents/My Games/Stationeers/mods/<ModName>/`. BepInEx's Chainloader finds the managed plugin DLL and tolerates the native DLLs next to it with a shim warning. LLamaSharp (or equivalent) finds its natives via the standard `runtimes/<rid>/native/` probe or adjacent lookup.

Cost: the mod is not in LaunchPad's catalogue, so no Workshop publish pipeline, no `About.xml`-driven in-game metadata, no mod settings panel. Acceptable for playtest, not for release.

### 2. Split native DLLs out of the mod folder

Deploy the managed plugin and `About.xml` under LaunchPad's `mods/<ModName>/` path (which LaunchPad scans). Deploy the native DLLs under `BepInEx/plugins/` (which LaunchPad does NOT scan; BepInEx scans but tolerates). At `Plugin.Awake()`, call Win32 `SetDllDirectory` on the native folder so the CRT's `LoadLibrary` resolver finds the natives when LLamaSharp's first P/Invoke fires:

```csharp
[System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
private static extern bool SetDllDirectory(string lpPathName);

void Awake()
{
    var nativeDir = Path.Combine(Paths.PluginPath, "<ModName>-natives");
    SetDllDirectory(nativeDir);
    // ... usual Awake logic ...
}
```

Cost: a second deploy target to keep in sync. A build step (`AfterTargets="Build"`) can handle it.

### 3. Ship natives with a non-DLL extension and rename at load

Ship `ggml.bin` / `llama.bin` in the mod folder. Plugin.Awake copies them to a temp directory with the `.dll` extension, then `SetDllDirectory`s that temp path. LaunchPad's scan sees `.bin` files and skips them.

Cost: opaque to readers, requires a file-copy step on every launch, permissions-sensitive on restricted systems.

### 4. Patch LaunchPad (not this mod's problem)

The underlying bug is in LaunchPad: per-file exception handling in `LoadedMod.LoadAssembliesSerial` would let mods ship arbitrary files. Upstream fix territory. Track via LaunchPad's issue tracker; not a blocker workaround for shipping a mod today.

## Recommended pattern

Workaround 2 for Workshop-ready mods. Workaround 1 for quick playtest iterations.

The `*.csproj` targets that produced the trap layout:

```xml
<Target Name="CopyNativeLibraries" AfterTargets="Build">
  <ItemGroup>
    <NativeRuntime Include="$(NuGetPackageRoot)llamasharp.backend.cpu\0.19.0\runtimes\**\*.*" />
  </ItemGroup>
  <Copy SourceFiles="@(NativeRuntime)"
        DestinationFiles="@(NativeRuntime->'$(OutputPath)runtimes\%(RecursiveDir)%(Filename)%(Extension)')"
        SkipUnchangedFiles="true" />
</Target>
```

For workaround 2, change the `DestinationFiles` to point at a sibling folder (e.g. `$(OutputPath)..\<ModName>-natives\`) or a separate output staging path. The deploy script then copies the two halves into the two final locations.

## Open questions

- Does LaunchPad scan `BepInEx/plugins/` at all? The presence of our mod's shim warning in `BepInEx\LogOutput.log` confirms BepInEx scans the My-Games path, but LaunchPad's stack trace only shows it walking the mod's own deploy folder. Needs confirmation by dropping a native DLL into `BepInEx/plugins/` with no managed sibling and verifying LaunchPad does not abort.
- Does LaunchPad respect a hypothetical `.launchpadignore` file or `About.xml` element for file exclusion? Not documented; needs a source read of LaunchPad 0.3.1.

## Verification history

<!-- verified: 0.2.6228.27061 @ 2026-04-22 -->

- 2026-04-22: page created from MaintenanceBureauPlus first-deploy failure. Stack trace captured verbatim from `%USERPROFILE%\AppData\LocalLow\Rocketwerkz\rocketstation\Player.log` lines 49, 200-243, 256-281. StationeersLaunchPad 0.3.1. Additive; no prior content on this topic.
