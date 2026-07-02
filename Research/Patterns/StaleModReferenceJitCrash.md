---
title: Stale mod reference JIT crash (caller-frame attribution)
type: Patterns
created_in: 0.2.6403.27689
verified_in: 0.2.6403.27689
verified_at: 2026-07-02
sources:
  - DedicatedServer/data/server.log, 2026-07-02 Luna_rearch boot at game 0.2.6403.27689 (3,363+ repeats of the quoted exception)
  - .work/decomp/0.2.6403.27689/ForceFieldDoorMod.decompiled.cs (Workshop_3328065049, mod About version 0.2.4767.21868.1, assembly ref Assembly-CSharp 0.2.5259.23818)
  - ilspycmd -il dumps of the server Assembly-CSharp (AtmosphericsManager <>c lambda) and of ForceFieldDoorMod.dll, 2026-07-02
related:
  - ../Unsorted/Api-removals-0.2.6403.md
  - ../GameSystems/SimulationTickDriverHooks.md
tags: [harmony]
---

# Stale mod reference JIT crash (caller-frame attribution)

A mod compiled against an old game version can carry a member reference (MemberRef) to a method whose signature no longer exists. Mono resolves member refs lazily, at JIT time of the method containing them, so the mod loads fine and the world loads fine; the crash comes later, when something first CALLS the stale method, and the resulting stack trace points at VANILLA code. This page is the diagnosis and mitigation recipe, with the ForceFieldDoorMod case as the worked example.

## The deceptive failure shape
<!-- verified: 0.2.6403.27689 @ 2026-07-02 -->

Observed on the dedicated server, repeating every simulation tick of the loaded save Luna_rearch (3,363+ occurrences in one server.log):

```
MissingMethodException: Method not found: bool Assets.Scripts.GridController.CanContainAtmos(Assets.Scripts.GridSystem.WorldGrid)
  at Assets.Scripts.Atmospherics.AtmosphericsManager+<>c.<.cctor>b__118_1 (Assets.Scripts.Objects.Thing thing) [0x0000c] in <34ec4ca08b584312830de78906df3608>:0 
  at Assets.Scripts.Util.DensePool`1[T].ForEach (System.Action`1[T] action) [0x00027] in <34ec4ca08b584312830de78906df3608>:0 
  at Assets.Scripts.GameManager.GameTick (System.Threading.CancellationToken cancellationToken) [0x0030c] in <34ec4ca08b584312830de78906df3608>:0 
```

Every frame is vanilla, every frame carries the game module's MVID (`34ec4ca0...` is the running Assembly-CSharp; the same MVID appears on unrelated healthy frames in the same log), and the named lambda's own IL is clean. The IL at the failing offset of `b__118_1` is:

```
IL_000c: ldarg.1
IL_000d: callvirt instance void Assets.Scripts.Objects.Thing::OnAtmosphericTick()
```

The exception is thrown while JIT-compiling the virtual-dispatch TARGET of that `callvirt`: a mod subclass's `OnAtmosphericTick()` override whose body contains the stale ref. The callee never gets a stack frame because it never finished compiling; Mono attributes the throw to the caller's frame at the callvirt offset. Nothing in the trace names the mod.

Two corollaries:

- A fresh world does not reproduce the crash even with the mod loaded: JIT is lazy, so the broken override only compiles when a Thing of that mod type is first dispatched. Only saves containing the mod's Things break (Luna_rearch has 15 `StructureForceFieldDoor`).
- The repeat is per-call-attempt: Mono re-attempts the JIT on every dispatch, and `GameTick`'s per-tick try/catch (see [SimulationTickDriverHooks](../GameSystems/SimulationTickDriverHooks.md)) logs the exception and moves on, so the whole simulation section (everything in the try, `ElectricityManager.ElectricityTick` included) is aborted EVERY tick while the tick loop itself keeps running. The world looks alive (tick count rises, autosaves fire) but no simulation phase completes.

## Diagnosis recipe
<!-- verified: 0.2.6403.27689 @ 2026-07-02 -->

1. IL-dump the module of the innermost stack frame (`ilspycmd -il <dll> -o <dir>` on the on-disk assembly whose MVID matches) and read the caller's IL at the reported offset. If it is a `callvirt` to a virtual method, the broken code is an OVERRIDE of that method on the concrete type being visited, not the method in the trace.
2. Find who defines such an override with the missing member: metadata strings are plain ASCII inside the DLL, so a byte-level grep for the missing member name across candidate DLLs works without decompiling anything. On 2026-07-02, scanning `install/rocketstation_DedicatedServer_Data/Managed/*.dll`, `install/BepInEx/**/*.dll`, and `data/mods/**/*.dll` for `CanContainAtmos` returned exactly two files: the game assembly (defines it) and `data/mods/Workshop_3328065049/ForceFieldDoorMod.dll` (references it).
3. IL-dump the suspect mod and compare its exact ref signature against the current game IL. The worked example: ForceFieldDoorMod's `forcefielddoormod.ForceFieldDoor : Airlock` override `OnAtmosphericTick()` contains, twice,
   `callvirt instance bool ['Assembly-CSharp']Assets.Scripts.GridController::CanContainAtmos(valuetype ['Assembly-CSharp']Assets.Scripts.GridSystem.WorldGrid)`
   while the 0.2.6403.27689 game defines only `CanContainAtmos(WorldGrid, bool)` and `CanContainAtmos(Grid3, bool)` (both with `allowCrewModules = true` default; default arguments are compile-time, so old binaries keep the 1-arg call). Details of the API change: [Api-removals-0.2.6403](../Unsorted/Api-removals-0.2.6403.md).
4. Before fixing one member, sweep the mod's other externally-referenced members in code paths that will run (other overrides), or the fix just moves the crash. In the worked example every other `['Assembly-CSharp']` ref in `OnAtmosphericTick`, `get_CanAirPass`, `PoweredChanged`, and `OnInteractableUpdated` resolved against the current game IL (including the private `AtmosphereHelper::_random` field: Unity's Mono does not enforce member visibility at JIT, which is also why publicized-assembly mods run at all).

## Mitigation calculus
<!-- verified: 0.2.6403.27689 @ 2026-07-02 -->

- Removing the mod also removes its Things from the loaded save (unregistered save data), changing world content; wrong when the save matters.
- A Harmony patch ON the broken method was not attempted and is expected to fail: HarmonyX builds its replacement by copying the original body through MonoMod's DynamicMethodDefinition, which resolves every operand token; the stale MemberRef should throw the same MissingMethodException at patch time (see Open questions). The same reasoning applies to MonoMod detours and `RuntimeHelpers.PrepareMethod`, both of which force compilation of the broken body.
- A caller-side Harmony filter (prefix on the clean vanilla dispatch site skipping the mod's Things) works but must cover every dispatch site of the virtual and silently disables the mod objects' behavior.
- The chosen fix: rewrite the stale call sites in the mod DLL with Mono.Cecil (ships with BepInEx at `BepInEx/core/Mono.Cecil.dll`). For a parameter-added signature change this is two instructions per site: insert `ldc.i4.1` (the new parameter's default value) before the call and retarget the operand to the current overload. Cecil branch targets are `Instruction` object references, so inserting before a non-branch-target instruction needs no fixups, and `AssemblyDefinition.Write` needs a `DefaultAssemblyResolver` with the game's Managed directory on its search path (without it, Write fails with `Failed to resolve assembly: 'Assembly-CSharp, Version=<mod's compile-time version>'`). Verified result IL:

```
IL_002c: ldc.i4.1
IL_002d: callvirt instance bool ['Assembly-CSharp']Assets.Scripts.GridController::CanContainAtmos(valuetype ['Assembly-CSharp']Assets.Scripts.GridSystem.WorldGrid, bool)
```

After swapping the rewritten DLL into `data/mods/Workshop_3328065049/` (server stopped first; the running server holds the file), the same Luna_rearch boot produced zero MissingMethodException over sustained ticking and the full simulation section ran (ScenarioRunner scenario fired). The rewrite is to the dedicated server's LOCAL mod copy only; the client's Workshop copy stays broken, and a `-SyncMods` re-mirror clobbers the fix. Original DLL preserved next to the fix note in the session stash.

## Verification history

- 2026-07-02: page created during the Luna_rearch dead-sim investigation at game 0.2.6403.27689. Exception block quoted verbatim from `DedicatedServer/data/server.log`; caller IL and mod IL quoted from same-day `ilspycmd -il` dumps; the two-file binary-grep result, the 15-door save census (`world.xml` PrefabName scan), and the post-fix clean boot are from the same session.

## Open questions

- Whether HarmonyX/MonoMod actually throws when patching a method whose body contains an unresolvable MemberRef (asserted here from MonoMod token-resolution behavior, not tested; testing it costs a boot with a throwaway patch). If someone verifies it either way, restamp the Mitigation calculus section.
