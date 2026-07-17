---
title: Thing enumeration off the Unity main thread
type: Patterns
created_in: 0.2.6228.27061
verified_in: 0.2.6403.27689
verified_at: 2026-07-15
sources:
  - .work/decomp/0.2.6228.27061/Assembly-CSharp.decompiled.cs :: line 199822 (OcclusionManager.AllThings), 253430 (CableNetwork.AllCableNetworks), 417824 (AtmosphericsManager.AllAtmospheres)
  - .work/decomp/0.2.6403.27689/Assembly-CSharp.decompiled.cs :: line 317670 (Thing.DisplayName), 210256 (Localization.GetThingName)
  - Mods/PowerGridPlus/PowerGridPlus/Patches/AtomicElectricityTickPatch.cs (worker-thread pipeline header; GridSnapshot.Build SNAPSHOT phase)
  - Mods/PowerGridPlus/PowerGridPlus/DeliveryEffectClassifier.cs:32 (classifier runs on the power worker, GridSnapshot.Build)
  - Plans/PgpVerifyHelper/PgpVerifyHelper/ScenarioRunner.cs (the working enumeration pattern)
  - DedicatedServer/data/server.log (crash repro on the ElectricityTick worker thread)
related:
  - ../GameClasses/PowerTick.md
  - ../Workflows/InspectorPlusUsage.md
tags: [unity, threading, harmony, power]
---

# Thing enumeration off the Unity main thread

A Harmony postfix on `ElectricityManager.ElectricityTick` runs on a UniTask ThreadPool worker, not the Unity main thread. `UnityEngine.Object.FindObjectsOfType<T>()` is documented as main-thread-only, so calling it from such a postfix crashes the native side of the engine intermittently. The game has its own non-Unity collections that are safe to iterate from any thread; use those instead.

## The crash signature
<!-- verified: 0.2.6228.27061 @ 2026-05-25 -->

Symptom: the dedicated server crashes shortly after world load with a native unity stack ending in:

```
0x... (UnityPlayer) (function-name not available)
0x... (Mono JIT Code) (wrapper managed-to-native) UnityEngine.Object:FindObjectsOfType (System.Type,bool)
0x... (Mono JIT Code) UnityEngine.Object:FindObjectsOfType<T_REF> ()
0x... (Mono JIT Code) <YourPlugin>.YourScenario:DoStuff ()
0x... (Mono JIT Code) Cysharp.Threading.Tasks.SwitchToThreadPoolAwaitable/Awaiter:Callback (object)
0x... (Mono JIT Code) System.Threading.QueueUserWorkItemCallback:System.Threading.IThreadPoolWorkItem.ExecuteWorkItem ()
0x... (Mono JIT Code) System.Threading.ThreadPoolWorkQueue:Dispatch ()
```

The `SwitchToThreadPoolAwaitable` frame is the diagnostic: the game's `GameTick` (via `Cysharp.Threading.Tasks.AsyncUniTask`) switches to the ThreadPool before calling `ElectricityManager.ElectricityTick`. Any code running in a postfix on that tick is on the worker, not the main thread. The crash is intermittent because Unity's internal scene-graph mutex sometimes happens to be free, and the call returns clean; other times it is held by main-thread work and the native side dereferences a stale pointer.

`Object.FindObjectsOfType` is the most common offender, but anything that calls into the Unity scene graph (most `MonoBehaviour` lookups, `GameObject.Find`, instantiation, `gameObject.GetComponent`) has the same constraint.

## The non-Unity collections you can iterate safely
<!-- verified: 0.2.6228.27061 @ 2026-05-25 -->

The game maintains its own `ConcurrentDensePool<T>` collections that are thread-safe to iterate from any thread (they manage their own locking). The relevant pools for power-system work:

```csharp
// decompile line 199822, inside class OcclusionManager
public static readonly ConcurrentDensePool<Thing> AllThings = new ConcurrentDensePool<Thing>("AllThings", 65535);
public static readonly ConcurrentDensePool<DynamicThing> AllDynamicThings = new ConcurrentDensePool<DynamicThing>("AllDynamicThings", 65535);

// decompile line 253430, inside class CableNetwork
public static readonly ConcurrentDensePool<CableNetwork> AllCableNetworks = new ConcurrentDensePool<CableNetwork>("AllCableNetworks", 4096);

// decompile line 417824, inside class AtmosphericsManager (approximate; same pattern)
public static readonly ConcurrentDensePool<Atmosphere> AllAtmospheres = new ConcurrentDensePool<Atmosphere>("AllAtmospheres", 65535);
```

Idiomatic enumeration:

```csharp
using Assets.Scripts;                       // OcclusionManager
using Assets.Scripts.Objects;               // Thing
using Assets.Scripts.Objects.Electrical;    // Battery, Transformer, AreaPowerControl, CableFuse

var batteries     = new List<Battery>();
var transformers  = new List<Transformer>();
var apcs          = new List<AreaPowerControl>();
var fuses         = new List<CableFuse>();
OcclusionManager.AllThings.ForEach(t =>
{
    if (t == null) return;
    if (t is Battery b)          batteries.Add(b);
    if (t is Transformer x)      transformers.Add(x);
    if (t is AreaPowerControl a) apcs.Add(a);
    if (t is CableFuse f)        fuses.Add(f);
});
int cableNetCount = CableNetwork.AllCableNetworks.ActiveCount;
```

`AllThings` holds every `Thing` in the scene (registered through `Thing.OnRegistered` / `Thing.OnDeregistered`); filtering via `is` against concrete types is cheap, and the per-iteration cost is comparable to a single `FindObjectsOfType<Thing>` call would be on the main thread.

## The Unity-side accessors that ARE safe off-thread
<!-- verified: 0.2.6228.27061 @ 2026-05-25 -->

Most reads on a `Thing` after it has been fully registered are managed-memory accesses with no engine round-trip, so they are safe from the worker:

- `thing.ReferenceId`, `thing.PrefabName`, `thing.OnOff`, `thing.Mode`, `thing.Powered`
- `thing.IsBeingDestroyed` (`public bool IsBeingDestroyed => BeingDestroyed;`, Thing line 318452, re-verified at 0.2.6403.27689): a plain managed bool read, and the liveness guard vanilla itself pairs with the null check inside pooled `ForEach` callbacks (see [AtmosphericThingTickDispatch](../GameSystems/AtmosphericThingTickDispatch.md)); filter `AllThings` entries with `t == null || t.IsBeingDestroyed` before touching them
- `Battery.PowerStored`, `Battery.PowerMaximum`, `Battery.PowerDelta`
- `Transformer.Setting`, `Transformer.UsedPower`, `Transformer._powerProvided` (private field, reflect or use `___` prefix in Harmony)
- `Transformer.InputNetwork.CurrentLoad`, `OutputNetwork.CurrentLoad` (CableNetwork properties)
- `CableNetwork.RequiredLoad`, `CurrentLoad`, `PotentialLoad`

What is NOT safe off-thread:

- `UnityEngine.Object.FindObjectsOfType<T>()` (this page's headline crash).
- `GameObject.Find`, `Object.Instantiate`, `Object.Destroy`.
- `thing.transform.position` writes (reads of `position` and `rotation` happen to work but are not guaranteed by Unity docs; prefer the cached `WorldPosition` / `WorldRotation` if available).
- `thing.gameObject.GetComponent<T>()` if T is not already cached on the Thing.

If a scenario needs a Unity-API write, marshal back to the main thread via a `MainThreadDispatcher` (see `Mods/InspectorPlus/InspectorPlus/MainThreadDispatcher.cs` for the working implementation).

## Capturing a device name during off-thread snapshot build
<!-- verified: 0.2.6403.27689 @ 2026-07-15 -->

The worker-thread constraint is not limited to a postfix. PowerGridPlus retired the vanilla per-network `PowerTick` trio and replaced `ElectricityManager.ElectricityTick` with a single Harmony prefix, `AtomicElectricityTickPatch` (`Mods/PowerGridPlus/PowerGridPlus/Patches/AtomicElectricityTickPatch.cs`). The whole prefix pipeline runs on the same UniTask worker: the patch header states "vanilla calls ElectricityTick on the UniTask ThreadPool worker; the prefix runs on the same worker", and step 1, the SNAPSHOT phase, is `GridSnapshot.Build`, which does "the single boundary read of every device's demand / output / control". `Mods/PowerGridPlus/PowerGridPlus/DeliveryEffectClassifier.cs:32` restates it: "the classifier runs on the power worker (GridSnapshot.Build)". So every device field captured while building the snapshot is captured off the main thread, and the read rules above apply.

When the snapshot needs a device name, `Thing.PrefabName` is the correct source. It is a plain immutable managed string (see the safe-off-thread list above), so reading it from `GridSnapshot.Build` on the worker is fine. The two obvious alternatives are NOT confirmed worker-safe:

- `Thing.DisplayName` (Assembly-CSharp decompile 0.2.6403.27689, line 317670) is `virtual` and returns the player rename when one is set, otherwise falls through to `Localization.GetThingName(PrefabName)`:

```csharp
public virtual string DisplayName
{
    get
    {
        if (string.IsNullOrEmpty(CustomName))
        {
            return Localization.GetThingName(PrefabName);
        }
        return CustomName;
    }
}
```

  The fallback path (below) is not on the confirmed-safe list, and the `virtual` modifier means a subclass override could add further work. `Thing.TrackableName => DisplayName` (line 317682) carries the same caveat.

- `Localization.GetThingName(string thingPrefabName)` (line 210256) calls `Animator.StringToHash` on both the primary and the fallback lookup:

```csharp
public static string GetThingName(string thingPrefabName)
{
    ThingLocalized.TryGetValue(Animator.StringToHash(thingPrefabName), out var value);
    if (value == null)
    {
        FallbackThingsLocalized.TryGetValue(Animator.StringToHash(thingPrefabName), out value);
        if (value == null)
        {
            return string.Format(ErrorName, CurrentLanguage, thingPrefabName);
        }
        return value.PrefabName;
    }
    return value.PrefabName;
}
```

  `Animator.StringToHash` is a Unity API whose off-thread safety is unconfirmed, so `GetThingName` (and therefore `DisplayName`'s fallback) is not confirmed worker-safe.

Rule: during snapshot build, or any `OcclusionManager.AllThings.ForEach` on the power worker, capture `Thing.PrefabName` as the device identity. If a friendly display name is needed, resolve it later on the main thread; do not call `Thing.DisplayName` or `Localization.GetThingName` from the worker until their off-thread safety is confirmed.

## Repro and the fix that landed
<!-- verified: 0.2.6228.27061 @ 2026-05-25 -->

The pattern was discovered while implementing `Plans/PgpVerifyHelper/`, a scenario-driven verification plugin. The initial `ScenarioRunner.LogInventory()` called `UnityEngine.Object.FindObjectsOfType<Battery>()` from the `ElectricityManager.ElectricityTick` postfix. The first run on Luna succeeded (returned 23 Batteries cleanly). The second run, with the same code targeting Transformer, crashed inside the FindObjectsOfType native bridge. The fix was the OcclusionManager.AllThings pattern documented above; the second run with the fix completed without crashing and emitted 41 Transformers + 23 Batteries cleanly.

The reason the FIRST run happened to work but the second crashed is undetermined: most likely a race window where Unity's scene-graph mutex was free for the first call and held for the second. Either way, do not rely on FindObjectsOfType from any ElectricityTick-driven postfix; the OcclusionManager.AllThings path is correct.

## Verification history

- 2026-07-15: added the "Capturing a device name during off-thread snapshot build" section (additive; no existing section changed, so no fresh-validator pass). PowerGridPlus's `AtomicElectricityTickPatch` prefix (replacing `ElectricityManager.ElectricityTick`) and its `GridSnapshot.Build` SNAPSHOT phase run on the UniTask ThreadPool worker (patch header comment; `DeliveryEffectClassifier.cs:32`), so device fields read during snapshot build are off-thread reads. `Thing.PrefabName` (plain immutable string, already on the safe list) is the correct off-thread name source; `Thing.DisplayName` (Assembly-CSharp 0.2.6403.27689 line 317670: player-rename `CustomName` with a `Localization.GetThingName` fallback, and `virtual`) and `Localization.GetThingName` (line 210256, calls `Animator.StringToHash` on both the primary and the fallback lookup) are NOT confirmed worker-safe. Verified against the 0.2.6403.27689 decompile and the current PowerGridPlus source; top-level verified_in / verified_at advanced to 0.2.6403.27689 / 2026-07-15 (the pre-existing sections keep their earlier stamps).
- 2026-07-07: added `thing.IsBeingDestroyed` to the safe-off-thread reads list (game version 0.2.6403.27689: `public bool IsBeingDestroyed => BeingDestroyed;` on Thing, decompile line 318452). Occasion: PowerGridPlus partial-power forensics. Single-bullet addition with its own inline version note; the section stamp and the other entries keep their 0.2.6228.27061 basis.
- 2026-05-25: page created. Source: live crash repro on the dedicated server during PgpVerifyHelper development (`DedicatedServer/data/server.log` after the 2026-05-25 transformer-conservation run) plus the `Plans/PgpVerifyHelper/PgpVerifyHelper/ScenarioRunner.cs` fix. Decompile reference for the static pools verified in place at `OcclusionManager.AllThings` (line 199822), `CableNetwork.AllCableNetworks` (line 253430), `AtmosphericsManager.AllAtmospheres` (line 417824).

## Open questions

- Whether `Thing.OnRegistered` / `Thing.OnDeregistered` themselves are safe to call off the main thread. The current `OcclusionManager.AllThings.Add` / `Remove` look thread-safe (the pool is concurrent), but a worker-thread plugin that ALSO needs to call `Thing.OnRegistered` would risk Unity-side state mutation; needs validation if a future scenario needs to spawn Things at runtime.
