# Force Field Door Mod Fix: Research

Durable internals for this mod. Audience: whoever picks it up next. All game line numbers are against the `0.2.6403.27689` decompile (`.work/decomp/0.2.6403.27689/Assembly-CSharp.decompiled.cs`) unless noted.

## Central research pointers

- [Research/GameSystems/AtmosphericThingTickDispatch.md](../../Research/GameSystems/AtmosphericThingTickDispatch.md): how `Thing.OnAtmosphericTick` is dispatched each tick and the exact interception point this mod uses.
- [Research/Unsorted/Api-removals-0.2.6403.md](../../Research/Unsorted/Api-removals-0.2.6403.md): the `GridController.CanContainAtmos(WorldGrid)` removal that breaks the mod, with the other 0.2.6403 API removals.
- [Research/Patterns/StaleModReferenceJitCrash.md](../../Research/Patterns/StaleModReferenceJitCrash.md): the general mechanism of a stale member reference crashing at JIT from a vanilla caller frame.

## What this mod is

A temporary third-party compatibility shim for **ForceFieldDoorMod** (Steam Workshop `3328065049`, by WIKUS and BoNes). It keeps that mod from crashing the simulation on Stationeers 0.2.6403+, without modifying its files. It is meant to be retired once the original mod is updated.

## The defect

ForceFieldDoorMod's `forcefielddoormod.ForceFieldDoor : Airlock` overrides `OnAtmosphericTick()`. In the shipped build (compiled against Assembly-CSharp 0.2.5259.23818) that method contains two calls to a **one-argument** `GridController.CanContainAtmos(WorldGrid)` (mod decompile `.work/decomp/0.2.6403.27689/ForceFieldDoorMod.decompiled.cs:110`).

Stationeers 0.2.6403 removed that overload. `GridController` now declares only:

- `public bool CanContainAtmos(WorldGrid worldGrid, bool allowCrewModules = true)` at Assembly-CSharp:207165
- `public bool CanContainAtmos(Grid3 localGrid, bool allowCrewModules = true)` at Assembly-CSharp:207170

The `allowCrewModules = true` default means a plain source recompile of the mod would bind cleanly to the two-argument overload. But the shipped binary carries a metadata reference to a method that no longer exists. Mono resolves member references lazily, at JIT time of the containing method, so the mod loads fine; the failure fires the first time `ForceFieldDoor.OnAtmosphericTick` is JIT-compiled, which is the first atmospheric tick of any world containing a force field door.

Observed on the dedicated server (Luna save, 15 force field doors): 837 occurrences in a single boot of

```
MissingMethodException: Method not found: bool Assets.Scripts.GridController.CanContainAtmos(Assets.Scripts.GridSystem.WorldGrid)
  at Assets.Scripts.<GameTick>d__135:MoveNext()
```

The throw is attributed to the vanilla caller frame (Mono pins it to the `callvirt` offset because the callee never finished compiling), and `GameManager.GameTick`'s per-tick try/catch aborts the whole simulation section every tick. The world is up but nothing simulates. Background: `Research/Patterns/StaleModReferenceJitCrash.md`.

## Why the obvious fixes do not work

- **Rewrite the mod DLL before load.** Correct in principle (retarget the two call sites to the two-argument overload with Cecil), but only possible before the CLR loads the assembly. ForceFieldDoorMod is loaded by StationeersLaunchPad, whose mod-load pipeline runs after the BepInEx chainloader. A Workshop mod loads at StationeersLaunchPad's entrypoint phase, which is after every mod assembly is already loaded, so it can never intercept the load.
- **Harmony-patch `ForceFieldDoor.OnAtmosphericTick`.** Any HarmonyX patch (even a prefix that returns false) calls `from.Pin()` in the `ILHook` constructor, which calls `RuntimeHelpers.PrepareMethod`, which JIT-compiles the target: the same `MissingMethodException`. Confirmed against the shipped MonoMod.RuntimeDetour 22.1.29.1 / HarmonyX 2.9.0.0. `Detour`, `Hook`, and `NativeDetour(MethodBase, ...)` all `Pin()` too. Only raw `NativeDetour(IntPtr, IntPtr)` avoids it, but it needs the target's native entry (a Mono-version-specific detail) and the method is virtual, so it is fragile and was not used.

So the broken method can neither be rewritten nor patched after load. The only lever is to make sure it is never called, so it never JITs.

## The chosen approach: intercept the dispatch, reimplement the tick

`Thing.OnAtmosphericTick()` is dispatched from exactly one place. `AtmosphericsManager` holds a per-thing delegate and a pool:

- `private static readonly Action<Thing> ThingAtmosphereTickAction` at Assembly-CSharp:439615, whose body is `if (thing != null && !thing.IsBeingDestroyed) thing.OnAtmosphericTick();` (the `thing.OnAtmosphericTick()` call is at :439619). This is the compiler-generated lambda that the crash stack blames.
- `public static void ThingAtmosphereTick()` at :439955 runs `AtmosphericThings.ForEach(ThingAtmosphereTickAction)`.
- `public static readonly DensePool<Thing> AtmosphericThings` at :439561 holds every thing that registered for atmospheric ticks. A `ForceFieldDoor` is in it only because the mod's own `OnRegistered` calls `AtmosphericsManager.Instance.Register(this)`; vanilla `Airlock`/`Door` neither register nor override `OnAtmosphericTick`.

`ThingAtmosphereTickAction` is the single call site for the no-argument virtual (grep confirms the only external `thing.OnAtmosphericTick()` dispatch is :439619).

The fix (`ForceFieldDoorPatch.cs`):

1. Resolve the delegate through the named static field `ThingAtmosphereTickAction` (never by the mangled lambda name) and Harmony-prefix its `Method`.
2. In the prefix, when the argument's runtime type is `forcefielddoormod.ForceFieldDoor`, run a reimplementation and return `false`. Returning false skips the vanilla `thing.OnAtmosphericTick()`, so the broken override is never invoked and never JITs. For every other thing the prefix returns true and the game dispatches normally.

Because every patched method is vanilla game code (the lambda and, in the fallback, `ThingAtmosphereTick`), nothing the fix touches trips the stale reference. And because the interception acts at simulation time, long after all mods have loaded, **load order is irrelevant**: the mod works whether it loads at StationeersLaunchPad's entrypoint phase (the Workshop path) or during the BepInEx chainloader (a manual `BepInEx/plugins` drop).

### The reimplementation

`TickForceFieldDoor` is a faithful copy of `ForceFieldDoor.OnAtmosphericTick` (mod decompile :97-130) with the two `CanContainAtmos(WorldGrid)` calls replaced by `CanContainAtmos(WorldGrid, true)`:

- Base call: `((Device)this).OnAtmosphericTick()` (mod :103) is `base.OnAtmosphericTick()`; the nearest base that overrides it is `Device.OnAtmosphericTick` at :371821 (Airlock/Door do not override it). A plain virtual call would recurse into the broken override, so the fix builds a `DynamicMethod` that emits `ldarg.0; call Device::OnAtmosphericTick; ret` and invokes that: a non-virtual call to the base, matching the original IL.
- `_open` short-circuit to 10 W, the pressure-differential power formula (`clamp(100 + floor(|kPa_front - kPa_rear|) * 10, 100, 100000)`), and the `FLUCTUATES` random bump are copied verbatim. The tuning constants (`POWERUSAGE_BASE=100`, `POWERUSAGE_MAX=100000`, `POWERUSAGE_RATE=10`, `FLUCTUATES=true`) are private fields on the mod's type (mod :49-55); they are hardcoded here since a build that changed them would also have to recompile (which drops the stale reference and trips the stand-down path).
- Members used: `Device.UsedPower` (:370351, public), `Thing.GridController` (:317576, `=> GridController.World`), `GridController.CanContainAtmos(WorldGrid, bool)` (:207165), `GridController.AtmosphericsController` (:206251, public field), `AtmosphericsController.SampleGlobalAtmosphere(WorldGrid)` (:196564), `Atmosphere.PressureGassesAndLiquidsInPa`, `AtmosphereHelper._random` (:438762, public static). The door's `_open`, `_facingGrid`, `_rearGrid` privates are read by reflection (`AccessTools.Field`).

`OnAtmosphericTick` is the **only** stale reference in the whole mod; every other method it declares resolves cleanly against 0.2.6403, so this single-method fix is complete.

### Self-retirement and idempotency

Before doing anything, `IsOnAtmosphericTickBroken` reads the mod's on-disk DLL with Mono.Cecil (BepInEx bundles it at `BepInEx/core/Mono.Cecil.dll`) and scans `OnAtmosphericTick`'s IL for a `call`/`callvirt` to a one-parameter `GridController.CanContainAtmos`. Reading IL does not compile it, so this is safe. If the stale call is absent (author updated) or the mod type is missing, the fix logs one line and installs nothing. This is what makes the shim self-retiring: a corrected ForceFieldDoorMod is left entirely alone.

### Load-order handling

Normal (Workshop) path: the entrypoint runs after StationeersLaunchPad has loaded every mod assembly, so `Install` finds `forcefielddoormod.ForceFieldDoor` immediately and sets up in `Awake`. Fallback path: if the type is not present yet (the plugin ran during the chainloader, before StationeersLaunchPad loaded the mod), `Install` defers the one-time setup to a prefix on `AtmosphericsManager.ThingAtmosphereTick`, which runs before that method's `ForEach` on the first atmospheric tick, by which point every mod is loaded and no door has ticked yet.

## File walkthrough

- `Plugin.cs`: BepInEx entrypoint. Exposes a static `Log` and calls `ForceFieldDoorPatch.Install`.
- `ForceFieldDoorPatch.cs`: everything above. `Install` (setup + deferral), `TrySetup` (find mod, self-retire check, install the dispatch prefix), `DispatchPrefix` (the per-thing filter), `TickForceFieldDoor` (the reimplementation), `BuildDeviceBaseTick` (the non-virtual base call), `IsOnAtmosphericTickBroken` (the Cecil scan).

## Verification

Dedicated server, Luna save (15 force field doors), fix loaded through StationeersLaunchPad's `data/mods` pipeline (the real Workshop path):

- Broken original DLL, no fix: 837 `MissingMethodException` in one boot, simulation dead.
- Fix loaded: zero `MissingMethodException`, the `Force Field Door Mod Fix active` load line and the `first ForceFieldDoor atmospheric tick handled (UsedPower set to ...)` runtime line both present, simulation alive.
- Hand-patched (already-fixed) DLL, fix loaded: the fix logs the stand-down line and installs nothing.

## Pitfalls

- Do not try to Harmony-patch or detour `ForceFieldDoor.OnAtmosphericTick`; it will throw at patch time (see above). The whole design exists to avoid ever compiling that method.
- The `DispatchPrefix` runs on a thread-pool thread (the atmospheric tick runs after `UniTask.SwitchToThreadPool`). The reimplementation touches the same state the vanilla override did on the same thread, so it is no less thread-safe than the original, but do not add Unity main-thread-only calls to it.
- `ThingAtmosphereTickAction` and `ThingAtmosphereTick` are internal names. If a future StationeersLaunchPad or game build renames them, `TrySetup` throws and logs the failure loudly rather than silently doing nothing.
