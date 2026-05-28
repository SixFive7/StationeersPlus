# Patterns/Threading

Shared threading helpers more than one mod needs to agree on. Today this is one file: `MainThreadDispatcher.cs`.

## MainThreadDispatcher.cs

A `MonoBehaviour` that parks a `ConcurrentQueue<Action>` on a `DontDestroyOnLoad` GameObject and drains it in `Update()` on the Unity main thread. Code on any thread calls `MainThreadDispatcher.Enqueue(closure)`; the closure runs on the main thread one frame later.

Why it exists: Stationeers runs the power tick (and any Harmony postfix on it) on a UniTask ThreadPool worker. Touching a Unity API (GameObject, Transform, LineRenderer, or the UI rebuilds that a device-list refresh triggers) from that worker thread hard-crashes the native player. Marshal that work to the main thread through this dispatcher. Background reads of plain managed fields are safe and do not need marshaling; only Unity API calls do.

The game ships its own `UnityMainThreadDispatcher`, but a mod-local one is the safer default: the game's `Instance()` throws when the `MainThreadExecutor` manager is not in the current scene, and its `Enqueue` coroutine-wraps actions and silently drops target-less delegates. The decompiled comparison lives in `Research/Patterns/MainThreadDispatcher.md`.

### How to use it

Link the file into the mod's `.csproj` (it is not a project reference; each mod compiles its own copy):

```xml
<Compile Include="..\..\..\Patterns\Threading\MainThreadDispatcher.cs" Link="Patterns\MainThreadDispatcher.cs" />
```

Initialize once in the plugin's `Awake`, then enqueue from anywhere:

```csharp
StationeersPlus.Shared.MainThreadDispatcher.Init(
    "YourMod_MainThreadDispatcher", msg => Log?.LogError(msg));
// ... later, from any thread:
StationeersPlus.Shared.MainThreadDispatcher.Enqueue(() => DoUnityWorkOnMainThread());
```

### Per-mod static state

Because the file is linked (not a shared assembly reference), each mod compiles `StationeersPlus.Shared.MainThreadDispatcher` into its own DLL. The `static` queue and instance are therefore per-mod: two mods that both link this file each get their own dispatcher GameObject and queue, with no cross-mod sharing. Pass a unique `gameObjectName` per mod so the scene hierarchy stays readable. This mirrors how `Patterns/Logic/LogicTypeNumbers.cs` is linked.

### Consumers

- PowerGridPlus (logic-passthrough device-list refresh cascade).

PowerTransmitterPlus still carries its own private `MainThreadDispatcher.cs`; migrating it to this shared file is tracked in `Mods/PowerTransmitterPlus/TODO.md`.
