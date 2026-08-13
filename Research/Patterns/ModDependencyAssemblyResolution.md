---
title: Mod dependency assembly resolution under BepInEx 5 and Mono
type: Patterns
created_in: 0.2.6403.27689
verified_in: 0.2.6428.27798
verified_at: 2026-08-13
sources:
  - steamapps\workshop\content\544550\3659911735\ (StationeersLua 1.0.0.0, MessagePack 3.1.8.0, 2026-08-13)
  - steamapps\workshop\content\544550\3666779631\ (ScriptedScreens 1.0.0.0, 2026-08-13)
  - .work/decomp/0.2.6403.27689/BepInEx.Preloader.decompiled.cs
  - .work/decomp/0.2.6403.27689/BepInEx.decompiled.cs
  - .work/decomp/0.2.6403.27689/StationeersLaunchPad.decompiled.cs
  - .work/decomp/0.2.6403.27689/ScriptedScreens.decompiled.cs
  - .work/decomp/0.2.6403.27689/StationeersLua.decompiled.cs
  - .work/decomp/0.2.6403.27689/Assembly-CSharp.decompiled.cs
  - .work/decomp/0.2.6403.27689/Assembly-CSharp.GameManager.decompiled.cs
  - https://github.com/MessagePack-CSharp/MessagePack-CSharp/issues/2174
related:
  - ../Workflows/DrivingTheGameClientProgrammatically.md
  - ./ILRepackPerModCopy.md
  - ./StaleModReferenceJitCrash.md
tags: [packaging, launchpad, harmony]
---

A mod that ships a third-party NuGet dependency alongside its own assembly is relying on three
different loaders agreeing: StationeersLaunchPad loads the files, BepInEx installs one process-wide
`AppDomain.AssemblyResolve` handler, and Unity's Mono runtime resolves everything else from its own
app base. This page documents what each of those actually does, using the worked example it was
written from: MessagePack 3.1.7.0 asking for
`System.Collections.Immutable, Version=8.0.0.0` and not getting it, on every frame, forever.

**That worked example is now historical. It was fixed upstream on 2026-08-13 and no longer
reproduces on this install; see "The worked example stopped reproducing" immediately below.**
Everything the page says about the three loaders is unaffected, because none of it was ever about
these two mods: it is read out of `StationeersLaunchPad.dll`, `BepInEx.Preloader.dll` and Mono's
own rules. Read the loader sections as current and the two named mods as the case that put them
under a microscope.

## The worked example stopped reproducing
<!-- verified: 0.2.6428.27798 @ 2026-08-13 -->

Both mods shipped **1.0.0.0 on 2026-08-13** (file timestamps 15:50,
`<Version>1.0.0.0</Version>` in each `About.xml`), and on that build the per-frame exception is
gone. Measured on a fresh client boot with both mods enabled: zero occurrences of
`DynamicAssemblyFactory`, zero of `TypeInitializationException`, zero of the
`System.Collections.Immutable` load failure. StationeersLaunchPad still logs
`Loading Assembly ...\System.Collections.Immutable.dll` for each mod folder and the assembly still
appears in the domain dump, but the bind that used to fail now succeeds. The client reaches the
menu and loads a save with both enabled.

What changed in the shipped payload, read from the assembly reference tables the same way the
tables below were:

| | 0.9.5.0 (2026-08-11) | 1.0.0.0 (2026-08-13) |
|---|---|---|
| `ScriptedScreens` / `StationeersLua` assembly version | 0.9.5.0 | 1.0.0.0 |
| `MessagePack` referenced and shipped | 3.1.7.0 | **3.1.8.0** |
| `MessagePack.Annotations` | 3.1.7.0 | 3.1.8.0 |
| `MessagePack` still references `System.Collections.Immutable` | 8.0.0.0 | **8.0.0.0, unchanged** |
| Bundled `System.Collections.Immutable.dll` | 8.0.0.0, 252,680 bytes, SHA-256 `5B1B1C83BA3D135C...` | **byte-identical, same hash and size** |
| Per-frame `TypeLoadException` | yes | no |

So the bundled dependency did not move and the reference that failed did not move. The only
dependency-side change is the MessagePack patch bump, 3.1.7.0 to 3.1.8.0. That is a correlation,
not a mechanism: this page never established why the bind failed in the first place (see Open
questions), so it cannot claim to know why it now succeeds. Recorded as an observation.

**What this does not retire.** The three-loader analysis below is about BepInEx, StationeersLaunchPad
and Mono, and stands. A mod shipping a NuGet dependency still gets `LocalResolve`'s simple-name
match with its highest-version fallback, still gets no directory fallback into Workshop folders, and
still cannot use a binding redirect. The next mod to hit this will hit it the same way.

## The symptom, as it was on 0.9.5.0
<!-- verified: 0.2.6403.27689 @ 2026-08-11 -->

```
TypeLoadException: Could not load type of field
  'MessagePack.Internal.DynamicAssemblyFactory:lastCreatedDynamicAssemblySkipVisibilityChecks' (2)
  due to: Could not load file or assembly
  'System.Collections.Immutable, Version=8.0.0.0, Culture=neutral, PublicKeyToken=b03f5f7f11d50a3a'
  or one of its dependencies.
```

The field named in the message is real and its type is the whole story:

```csharp
// MessagePack.Internal.DynamicAssemblyFactory, MessagePack 3.1.7.0
private ImmutableHashSet<AssemblyName> lastCreatedDynamicAssemblySkipVisibilityChecks =
    SkipClrVisibilityChecks.EmptySet.Add(Assembly.GetExecutingAssembly().GetName());
```

`ImmutableHashSet<T>` lives in `System.Collections.Immutable`, so the type cannot be laid out and
`DynamicAssemblyFactory` fails to load. `DynamicAssemblyFactory` is a static field of
`DynamicUnionResolver`, which is a member of `StandardResolverHelper`, which backs
`StandardResolver`, so the failure surfaces as a chain of `TypeInitializationException` from
whichever MessagePack entry point ran first. A failed type initializer is cached by the runtime and
rethrown on every later access, which is why one bad load turns into a per-frame exception for the
rest of the process lifetime.

## Which mod pulls in MessagePack
<!-- verified: 0.2.6428.27798 @ 2026-08-13 -->

Read from the `AssemblyRef` tables, not guessed. Both Workshop mods are by the same author (zedle).
The table below was taken at 0.9.5.0; **the shape is unchanged at 1.0.0.0**, re-read on 2026-08-13,
with `MessagePack` at 3.1.8.0 instead of 3.1.7.0 and every other column the same. StationeersLua is
still the only one of the two carrying `MessagePack.dll`, ScriptedScreens still declares
`StationeersLua, Version=1.0.0.0` as a reference, and both still bundle the same
`System.Collections.Immutable.dll`.

| Assembly | Ships `MessagePack.dll` | References `MessagePack` | Ships `System.Collections.Immutable.dll` |
|---|---|---|---|
| StationeersLua 0.9.5.0 (Workshop item 3659911735) | yes, 3.1.7.0 | yes, `MessagePack, Version=3.1.7.0, PublicKeyToken=b4a0369545f0a1be` | yes, 8.0.0.0 |
| ScriptedScreens 0.9.5.0 (Workshop item 3666779631) | no | yes, same reference | yes, 8.0.0.0 |

So the answer is both, with one copy of the library. StationeersLua is the only mod of the two that
carries `MessagePack.dll` and `MessagePack.Annotations.dll`; ScriptedScreens references the same
assembly identity and gets it from StationeersLua's folder, which is consistent with ScriptedScreens
declaring StationeersLua as a hard requirement ("install first; ScriptedScreens will not load
without it"). No other subscribed Workshop item in this install contains a file named
`MessagePack*.dll`; a recursive search over the whole Workshop content directory for this app
returns exactly the two files in item 3659911735.

`MessagePack.dll` 3.1.7.0 declares five assembly references, one of which is the one that fails:

```
netstandard, Version=2.1.0.0, PublicKeyToken=cc7b13ffcd2ddd51
System.Collections.Immutable, Version=8.0.0.0, PublicKeyToken=b03f5f7f11d50a3a
MessagePack.Annotations, Version=3.1.7.0, PublicKeyToken=b4a0369545f0a1be
System.Runtime.CompilerServices.Unsafe, Version=6.0.0.0, PublicKeyToken=b03f5f7f11d50a3a
Microsoft.NET.StringTools, Version=1.0.0.0, PublicKeyToken=b03f5f7f11d50a3a
```

## Required versus present
<!-- verified: 0.2.6403.27689 @ 2026-08-11 -->

- **Required**: `System.Collections.Immutable, Version=8.0.0.0, Culture=neutral, PublicKeyToken=b03f5f7f11d50a3a`.
- **Shipped by the game**: nothing under that name. `rocketstation_Data\Managed\` contains
  `Sentry.System.Collections.Immutable.dll`, whose assembly identity is
  `Sentry.System.Collections.Immutable, Version=5.0.0.0` with an empty public key. Different simple
  name, different version, unsigned. It cannot satisfy the reference under any binding policy, and
  no binding redirect can rename an assembly. The install also has no plain
  `System.Collections.Immutable.dll` anywhere in `rocketstation_Data\Managed\`.
- **Shipped by the mods**: both mods carry a `System.Collections.Immutable.dll` that is
  byte-identical between them (same size, same SHA-256) and whose identity is exactly
  `System.Collections.Immutable, Version=8.0.0.0, Culture=neutral, PublicKeyToken=b03f5f7f11d50a3a`.
  That is the assembly MessagePack asks for, with no version gap and no token gap to bridge.

So this is not a version mismatch. The correct file is on disk, twice.

## The file is not merely present, it is loaded
<!-- verified: 0.2.6403.27689 @ 2026-08-11 -->

StationeersLaunchPad loads every DLL in a mod folder, recursively, with no filter:

```csharp
// StationeersLaunchPad.ModInfo constructor
Assemblies.AddRange(Directory.GetFiles(DirectoryPath, "*.dll", SearchOption.AllDirectories));
AssetBundles.AddRange(Directory.GetFiles(DirectoryPath, "*.assets", SearchOption.AllDirectories));
```

```csharp
// StationeersLaunchPad.Loading.LoadedMod.LoadAssemblySingle
private UniTask<Assembly> LoadAssemblySingle(string path)
{
    return UniTask.RunOnThreadPool(delegate
    {
        Logger.LogDebug("Loading Assembly " + path);
        Assembly assembly = Assembly.LoadFrom(path);
        ModLoader.RegisterAssembly(assembly, this);
        Logger.LogInfo("Loaded Assembly");
        return assembly;
    });
}
```

The Unity player log confirms both loads succeed and that both copies end up in the domain:

```
[StationeersLua [StationeersLaunchPad]]: Loading Assembly ...\3659911735\System.Collections.Immutable.dll
[StationeersLua [StationeersLaunchPad]]: Loaded Assembly
[ScriptedScreens [StationeersLaunchPad]]: Loading Assembly ...\3666779631\System.Collections.Immutable.dll
[ScriptedScreens [StationeersLaunchPad]]: Loaded Assembly
```

and the same log's domain dump lists `Assembly System.Collections.Immutable` twice, once in each
mod's block, alongside `Assembly Sentry.System.Collections.Immutable` from the game.

This retires the "Mono never probes mod folders" explanation. Nothing has to probe: the assembly is
loaded by absolute path before any of it is needed.

## What resolves an assembly reference in this process
<!-- verified: 0.2.6403.27689 @ 2026-08-11 -->

Two mechanisms, and only two.

**Mono's own probing.** Unity's Mono resolves a reference by simple name against its app base,
`rocketstation_Data\Managed\`, plus anything already registered under a matching identity. No
`.config` file with `assemblyBinding` is loaded for the player, so app.config binding redirects are
not a lever here. Mono's `Assembly.LoadFrom` is also documented upstream as not participating in
binding-redirect resolution the way the Microsoft CLR does (mono/mono issue 8152, closed).

**BepInEx's one handler.** `BepInEx.Preloader.PreloaderRunner.PreloaderPreMain` installs a
process-wide handler and never removes it (there is exactly one `+= LocalResolve` and no matching
`-=` in `BepInEx.Preloader.dll` 5.4.23.5):

```csharp
AppDomain.CurrentDomain.AssemblyResolve += LocalResolve;
AppDomain.CurrentDomain.AssemblyResolve -= Entrypoint.ResolveCurrentDirectory;
```

```csharp
private static Assembly LocalResolve(object sender, ResolveEventArgs args)
{
    if (!Utility.TryParseAssemblyName(args.Name, out var assemblyName)) { return null; }
    var source = (from a in AppDomain.CurrentDomain.GetAssemblies()
        select new { assembly = a, name = (Utility.TryParseAssemblyName(a.FullName, out var assemblyName2) ? assemblyName2 : null) } into a
        where a.name != null && a.name.Name == assemblyName.Name
        orderby a.name.Version descending
        select a).ToList();
    Assembly assembly = (source.FirstOrDefault(a => a.name.Version == assemblyName.Version) ?? source.FirstOrDefault())?.assembly;
    if (assembly != null) { return assembly; }
    if (Utility.TryResolveDllAssembly(assemblyName, Paths.BepInExAssemblyDirectory, out assembly)
        || Utility.TryResolveDllAssembly(assemblyName, Paths.PatcherPluginPath, out assembly)
        || Utility.TryResolveDllAssembly(assemblyName, Paths.PluginPath, out assembly))
    { return assembly; }
    return null;
}
```

Three properties of that handler matter for mod authors:

- It matches on **simple name only**, then prefers an exact version and otherwise takes the highest
  version present. A mod that needs version 8 of something and finds version 4 already loaded gets
  version 4 handed to it without complaint.
- Its directory fallback covers `BepInEx\core`, `BepInEx\patchers` and `BepInEx\plugins`. It does
  **not** cover Workshop mod folders, which is why a dependency that StationeersLaunchPad did not
  load explicitly is unreachable.
- `BepInEx.dll`'s own `TypeLoader.Resolver` is a Mono.Cecil `DefaultAssemblyResolver` used for
  metadata scanning during plugin discovery. It is not an `AppDomain.AssemblyResolve` handler and
  has no effect on runtime binding. `BepInEx.dll` contains no `LocalResolve` and no
  `AppDomain.CurrentDomain.AssemblyResolve` subscription at all.

The unexplained step is that `LocalResolve` would find the loaded 8.0.0.0 copy on its first branch,
with an exact version match, if it were consulted. The failure therefore happens without the loaded
copy being reached. Whether Mono suppresses the managed resolve event on the field-type-loading path,
or caches the negative result per referencing image, was not established from the artifacts available
here. See Open questions.

## The two per-frame call sites on 0.9.5.0, and what each one cost
<!-- verified: 0.2.6403.27689 @ 2026-08-11 -->

Both mods touch MessagePack from an unguarded per-frame Harmony patch, and the two sites have very
different blast radii. This distinction is the reason "an exception every frame" is not by itself a
severity.

**Site A, ScriptedScreens, a prefix.** The failing initializer is the static constructor of
`ScriptedScreensScriptableUiSystem`, and the exact statement is the MessagePack options build:

```csharp
static ScriptedScreensScriptableUiSystem()
{
    ...
    MpOptions = MessagePackSerializerOptions.Standard
        .WithResolver((IFormatterResolver)(object)ContractlessStandardResolver.Instance)
        .WithCompression((MessagePackCompression)2);
    ...
}
```

Every static member of that class then rethrows forever, including the one read here:

```csharp
[HarmonyPatch(typeof(KeyWrap), "PollForInput")]
[HarmonyPrefix]
private static bool KeyWrap_PollForInput_Prefix()
{
    if (!ScriptedScreensScriptableUiSystem.IsInterfaceModeActive) { return true; }
    if (ScriptedScreensScriptableUiSystem.IsTextInputFocused()) { return false; }
    return true;
}
```

There is no try/catch in this prefix, none in the mod's patch application
(`_harmony = new Harmony("zedle.stationeers.scriptedscreens"); _harmony.PatchAll();`, unconditional),
and none anywhere in the chain above it:

```csharp
internal static void PollInputs()                 // KeyMap, Assembly-CSharp 44823
{
    if (PollingSet == null) { return; }
    foreach (KeyWrap item in PollingSet) { item.PollForInput(); }
}

public override void ManagerUpdate()              // KeyManager : ManagerBase, 43736
{
    base.ManagerUpdate();
    KeyMap.PollInputs();
    ResetState();
}

foreach (ManagerBase manager2 in Managers)        // GameManager.Update, tail
{
    manager2.ManagerUpdate();
}
BatchRenderer.RenderAll();
WindTurbineGenerator.UpdateWind();
```

Read literally, a throw at the first `KeyWrap` aborts `PollInputs` for the whole frame (so no
`KeyWrap`-bound action fires: Cancel, Help, SwapHands, Drop, Internals, ToggleLight, SmartStow,
InventorySelect, quicksave), aborts `KeyManager.ManagerUpdate`, and then aborts the `Managers` loop
inside `GameManager.Update`, taking with it every manager ordered after `KeyManager` plus
`BatchRenderer.RenderAll()` and `WindTurbineGenerator.UpdateWind()`.

**Site B, StationeersLua, a postfix.** StationeersLua hangs its own per-frame work off the end of
the same method:

```csharp
[HarmonyPatch(typeof(GameManager), "Update")]
internal static class McpServerTickPatch
{
    [HarmonyPostfix]
    private static void GameManager_Update_Postfix()
    {
        bool num = NetworkManager.IsServer && NetworkManager.IsActive;
        bool flag = McpMultiplayerDebugProxy.ShouldProxyRequestsLocally();
        bool flag2 = num && (McpServerConfig.Enabled || McpServerConfig.EnableExtensionApi || McpServerConfig.AllowMultiplayerDebugProxy);
        if (McpServerConfig.AnyListenerEnabled || flag2 || flag)
        {
            McpMultiplayerDebugProxy.Tick();
            McpGameBridge.Tick();
        }
    }
}
```

`ShouldProxyRequestsLocally()` is called unconditionally, ahead of every config gate, so this site
runs every frame regardless of whether any of the mod's optional listeners are enabled. A throw here
costs nothing in the game's own frame: a postfix runs after the original body has completed, so the
only casualties are other postfixes on `GameManager.Update` ordered after this one.

Both sites are per-frame and both are reached at the main menu as well as in world, because
`KeyManager` and `GameManager.Update` are both live before a world is loaded.

## The conflict between the code reading and the field evidence
<!-- verified: 0.2.6403.27689 @ 2026-08-11 -->

Site A above says the client should be visibly broken. The field evidence says it is not:

- The developer hosts a session with both mods enabled, a second player joins normally, and the
  exception fires throughout. That is recorded independently on
  [DrivingTheGameClientProgrammatically](../Workflows/DrivingTheGameClientProgrammatically.md).
- The game is played to completion with both mods enabled. If the `Managers` loop aborted at
  `KeyManager` every frame, `BatchRenderer.RenderAll()` would never run and every manager after
  `KeyManager` would never tick.
- The Unity player log from a session with both mods loaded contains **zero** occurrences of
  `DynamicAssemblyFactory` and zero occurrences of the `System.Collections.Immutable` load failure,
  while containing three unrelated `TypeLoadException` entries from a different mod
  (`IC10Inspector` failing to find `IC10Extender`). Unity logs an exception that escapes a
  MonoBehaviour message, so a throw escaping `GameManager.Update` should appear there and does not.

Three readings survive that are not distinguishable from the static artifacts: the throw never
escapes site A (something between the prefix and the frame loop swallows it), `KeyManager` sits last
in the `Managers` list so the loop truncation costs nothing observable, or the flood observed in the
game console comes from site B and a different, caught path rather than from site A. The manager
order cannot settle it, because `GameManager.Managers` is `public List<ManagerBase> Managers = new
List<ManagerBase>()` populated by Unity serialization: `Managers.Add` has zero occurrences in
`Assembly-CSharp`, so the order is not in the decompile at all.

Do not upgrade this to "the exception breaks multiplayer joins". The strongest supported statement
remains the one already on record: it is observationally log spam. A separate datum reinforces it
from outside this install, which is that the developer's friend runs the same mod set, sees the same
exception, and joins the developer's hosted world without trouble.

## Upstream status
<!-- verified: 0.2.6403.27689 @ 2026-08-11 -->

The exception is a known, open, unfixed MessagePack issue and is not specific to Stationeers.

**Not re-checked on 2026-08-13.** The shipped MessagePack moved 3.1.7.0 to 3.1.8.0 in the mod
update that made the exception stop reproducing here, so whether any of the issues below closed in
that release is an open question rather than a recorded fact. The issue states below are as of
2026-08-11.

- **MessagePack-CSharp issue 2174**, opened 2025-03-10, still open, no maintainer response. Same
  exception text down to the field name, same missing identity
  (`System.Collections.Immutable, Version=8.0.0.0, PublicKeyToken=b03f5f7f11d50a3a`), same cascade
  through `DynamicUnionResolver` and `StandardResolverHelper` into `StandardResolver`. Reported on
  MessagePack 3.1.3 under Unity Mono at API level .NET Standard 2.1, which is exactly the
  configuration here (MessagePack 3.1.7.0, both mod assemblies built against .NET Standard 2.1).
- **MessagePack-CSharp issue 2086**, closed, records that the dependency on
  `System.Collections.Immutable` 8.0.0 is what breaks under Unity and that referencing 6.0.0 locally
  works instead.
- **MessagePack-CSharp issue 2155**, open, is the same dependency-resolution class of failure on
  Unity 6 builds.
- **mono/mono issue 8152**, closed, records that `Assembly.LoadFrom` does not honour app.config
  binding redirects on Mono the way it does on the Microsoft CLR.

Angles searched and found empty: the StationeersLaunchPad issue tracker (30 issues, all states, none
about assembly resolution or missing dependencies), the LaunchPadBooster issue tracker (9 issues,
all states, none related), and general web search for the exception text paired with Stationeers or
BepInEx. The two mods' Workshop pages and comment threads were **not** checked: the repository rule
requires steamcommunity.com lookups to go through the Playwright browser, and no Playwright tool was
available in this session. Neither mod publishes a public source repository.

## Fix options, in order of how little they depend on unexplained behaviour
<!-- verified: 0.2.6403.27689 @ 2026-08-11 -->

**None of these is needed for StationeersLua or ScriptedScreens any more**, since 1.0.0.0 binds
successfully with nothing added to the install. They are kept because the failure class is generic:
the next mod that bundles a NuGet dependency Mono cannot bind gets the same five options, in the
same order.

1. **Put the assembly on Mono's own probing path.** Copy the mods' own
   `System.Collections.Immutable.dll` (8.0.0.0, token `b03f5f7f11d50a3a`) into
   `rocketstation_Data\Managed\`. That directory is the Mono app base, so the reference resolves
   through the runtime's normal lookup and never needs the managed `AssemblyResolve` event to fire.
   This is the only option that does not depend on the step this page could not explain.
2. **Put it in `BepInEx\core\`.** Reachable through `LocalResolve`'s directory fallback, but only if
   the event fires at all, which is precisely what is in doubt. Recorded on
   [DrivingTheGameClientProgrammatically](../Workflows/DrivingTheGameClientProgrammatically.md) as
   tried once with an ambiguous result, and since removed from the developer's install.
3. **A patch mod with its own `AppDomain.CurrentDomain.AssemblyResolve` handler** pointed at the
   bundled copy in either Workshop folder. One handler covers both mods. Same caveat as option 2
   about whether the event fires on this path, and it needs to be installed before either mod's
   first MessagePack touch.
4. **A binding redirect: not available.** There is no app.config in play for the Unity player, Mono
   does not honour redirects for `Assembly.LoadFrom` (mono/mono 8152), and no redirect could map
   `System.Collections.Immutable` onto the differently named `Sentry.System.Collections.Immutable`
   in any case.
5. **A different MessagePack build: upstream's problem.** MessagePack 3.x takes the
   `System.Collections.Immutable` 8.0.0 dependency by design; issue 2086 shows 6.0.0 works, but that
   is a rebuild of the mods, not something a downstream install can do.

## Verification history

- 2026-08-11: page created. Assembly identities and reference tables read directly from the PE
  metadata of the shipped DLLs; load evidence read from the Unity player log of a session with both
  mods enabled; loader and resolver code read from fresh decompiles of `StationeersLaunchPad.dll`,
  `BepInEx.dll`, `BepInEx.Preloader.dll`, `ScriptedScreens.dll` and `StationeersLua.dll` at
  0.2.6403.27689. Two claims previously in circulation in this repository are not repeated here
  because the artifacts contradict them. First, that nothing adds the mod folders to the resolution
  path: the DLL is loaded outright by absolute path and the domain holds two copies, per the loader
  code and the player log quoted above. Second, that the ScriptedScreens static constructor reaches
  MessagePack through `McpMultiplayerDebugProxy`: that symbol has zero occurrences in
  `ScriptedScreens.dll` (one grep re-checks it), it is a `StationeersLua` type and belongs to site
  B, and site A fails on `MessagePackSerializerOptions.Standard.WithResolver(...)` in the static
  constructor quoted above. The second claim also appears verbatim on
  [DrivingTheGameClientProgrammatically](../Workflows/DrivingTheGameClientProgrammatically.md),
  which has NOT been edited: correcting verified content on an existing page requires the fresh
  validator protocol in `Research/WORKFLOW.md` Rule 3, and that pass is still owed.
- 2026-08-13: the "still owed" clause immediately above is superseded, and this entry records that
  rather than rewriting it, because Verification History is append-only. The fresh-validator pass
  DID run, on 2026-08-11, and
  [DrivingTheGameClientProgrammatically](../Workflows/DrivingTheGameClientProgrammatically.md) was
  corrected in place the same day. Its own Verification History carries the entry
  ("2026-08-11: conflict on how the `ScriptedScreens` static ctor reaches MessagePack ... Fresh
  validator verdict: the new finding is correct"). The two pages agree; nothing is outstanding.
- 2026-08-13, 0.2.6428.27798: additive, contradicting nothing above. Both mods shipped 1.0.0.0 on
  2026-08-13 and the per-frame exception stopped reproducing on this install: a fresh client boot
  with both enabled logs zero `DynamicAssemblyFactory`, zero `TypeInitializationException` and zero
  `System.Collections.Immutable` load failures, and reaches the menu and a loaded save. Assembly
  reference tables re-read from the shipped DLLs: `MessagePack` moved 3.1.7.0 to 3.1.8.0, its
  reference to `System.Collections.Immutable, Version=8.0.0.0` is unchanged, and the bundled
  `System.Collections.Immutable.dll` is byte-identical to the 0.9.5.0 copy (252,680 bytes, SHA-256
  `5B1B1C83BA3D135C...`, still identical between the two mod folders). The loader analysis, the
  `LocalResolve` reading, the two per-frame call sites and the conflict section are all unchanged
  and were not re-derived; they are now historical for these two mods and current for the mechanism.
  Recorded as an observation, not as a mechanism: this page never established why the bind failed,
  so it makes no claim about why it now succeeds. The first Open question below is therefore no
  longer answerable from this install.

## Open questions

- Why the loaded 8.0.0.0 copy does not satisfy MessagePack's reference. `LocalResolve` would return
  it on an exact version match if the managed resolve event were raised for a failure on the
  field-type-loading path. Whether Mono skips the event there, or caches the negative reference
  result per referencing image so a later successful load cannot help, was not determined. Settling
  it needs a runtime probe (subscribe an `AssemblyResolve` handler that logs every request, then
  force a MessagePack touch), not more decompiling.

  **Unanswerable here as of 2026-08-13**, because the reproducer is gone: MessagePack 3.1.8.0 binds
  the same assembly successfully on the same install. Whoever picks this up needs a fresh case, and
  the honest state of it is that the failure was observed, the fix was not diagnosed, and the two
  are separated by a patch bump that nobody has read the diff of.
- Whether MessagePack 3.1.8.0 is what fixed it, and if so what changed in it. The bump is the only
  dependency-side difference between the failing and the working payload, but the mods' own code
  changed in the same release and the correlation is one observation.
- Which of the three readings in "The conflict" is correct. The cheapest discriminator is a runtime
  enter/exit counter on `KeyManager.ManagerUpdate` and on a manager known to sit late in the list,
  compared across frames while the exception is firing. The client rig already exposes that shape of
  measurement for the input chain. Also unreachable now without an older copy of the mods.
- Whether `Assembly.LoadFrom` on a ThreadPool thread (StationeersLaunchPad loads mod assemblies via
  `UniTask.RunOnThreadPool`, serial or parallel depending on the configured load strategy) changes
  anything about which context the loaded assembly lands in. Not investigated.
