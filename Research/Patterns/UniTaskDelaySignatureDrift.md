---
title: UniTask.Delay signature drift (MissingMethodException)
type: Patterns
created_in: 0.2.6228.27061
verified_in: 0.2.6228.27061
verified_at: 2026-05-31
sources:
  - .work/decomp/0.2.6228.27061/UniTask.decompiled.cs (lines 19077-19117, the Delay / DelayFrame overloads)
  - .work/decomp/0.2.6228.27061/KeypadMod.decompiled.cs (lines 108-140 state machine, 251-298 kickoff)
  - .work/decomp/0.2.6228.27061/0Harmony.decompiled.cs (lines 880-1021, Cecil/ILHook body reader)
  - StationeersLaunchPad-bundled UniTask.dll, file-modified 2026-03-26 (game update 0.2.6228.27061)
  - KeypadMod Workshop item 3478434324 comment thread (runtime stack trace)
related:
  - ./AsyncHarmonyTrap.md
  - ./AsyncEnumerator472.md
  - ./HarmonyPrefixReturnBool.md
  - ../Workflows/ModProjectSetup.md
  - ../GameClasses/LogicUnitBase.md
tags: [threading, harmony, launchpad]
---

# UniTask.Delay signature drift (MissingMethodException)

## Summary
<!-- verified: 0.2.6228.27061 @ 2026-05-31 -->

The game update shipped on 2026-03-26 (version 0.2.6228.27061) updated the bundled
`UniTask.dll`. Every `UniTask.Delay` / `UniTask.DelayFrame` overload gained a trailing
optional parameter `bool cancelImmediately = false`. Adding an optional parameter is a
source-compatible but binary-incompatible change: a mod recompiled against the new DLL
just picks up the new default, but a mod that was already compiled against the OLD
signature has the old parameter list baked into its IL and throws
`MissingMethodException` at runtime.

This is the root cause of the "Method not found: Cysharp.Threading.Tasks.UniTask.Delay"
crash hitting Stationeers plugin mods built before 2026-03-26. The same mechanism can
recur on any future game update that adds an optional parameter to any bundled-library
method a mod calls.

## The current signatures (verified)
<!-- verified: 0.2.6228.27061 @ 2026-05-31 -->

From the current `UniTask.dll` decompile, `Cysharp.Threading.Tasks.UniTask` exposes
these time-based delay entry points (all now carry the trailing `cancelImmediately`):

```csharp
public static UniTask DelayFrame(int delayFrameCount, PlayerLoopTiming delayTiming = PlayerLoopTiming.Update, CancellationToken cancellationToken = default(CancellationToken), bool cancelImmediately = false)            // 19077
public static UniTask Delay(int millisecondsDelay, bool ignoreTimeScale = false, PlayerLoopTiming delayTiming = PlayerLoopTiming.Update, CancellationToken cancellationToken = default(CancellationToken), bool cancelImmediately = false)   // 19087
public static UniTask Delay(TimeSpan delayTimeSpan, bool ignoreTimeScale = false, PlayerLoopTiming delayTiming = PlayerLoopTiming.Update, CancellationToken cancellationToken = default(CancellationToken), bool cancelImmediately = false)   // 19092
public static UniTask Delay(int millisecondsDelay, DelayType delayType, PlayerLoopTiming delayTiming = PlayerLoopTiming.Update, CancellationToken cancellationToken = default(CancellationToken), bool cancelImmediately = false)            // 19098
public static UniTask Delay(TimeSpan delayTimeSpan, DelayType delayType, PlayerLoopTiming delayTiming = PlayerLoopTiming.Update, CancellationToken cancellationToken = default(CancellationToken), bool cancelImmediately = false)            // 19103
```

A mod compiled against an older UniTask emitted a `call` to the four-parameter form
`UniTask.Delay(int, bool, PlayerLoopTiming, CancellationToken)`. That exact four-param
method no longer exists; only the five-param method does.

## Failure mechanism
<!-- verified: 0.2.6228.27061 @ 2026-05-31 -->

C# resolves optional/default arguments at the call site at compile time. When the mod
author wrote `UniTask.Delay(550, false, PlayerLoopTiming.Update, token)`, the compiler
emitted IL referencing a `MemberRef` with the full four-parameter signature it saw at
build time. The default values are NOT looked up at runtime; they are frozen into the
caller's metadata.

When the caller method is JIT-compiled, the runtime resolves that `MemberRef` against
the currently loaded `UniTask.dll`, finds no method matching the four-parameter
signature (the method now takes five), and throws:

```
MissingMethodException: Method not found:
Cysharp.Threading.Tasks.UniTask Cysharp.Threading.Tasks.UniTask.Delay(int, bool, PlayerLoopTiming, CancellationToken)
```

When the call sits inside an `async UniTaskVoid` / `async UniTask` method, the throw
surfaces at `AsyncUniTaskVoidMethodBuilder.Start[TStateMachine]` (or the `UniTask`
equivalent), because that is where the compiler-generated state machine's `MoveNext`
first executes and the JIT first has to resolve the call.

## Concrete instance: KeypadMod
<!-- verified: 0.2.6228.27061 @ 2026-05-31 -->

KeypadMod (Workshop 3478434324, by WIKUS) was last built against game version
0.2.4767.21868. Its `keypadmod.Keypad : LogicUnitBase` runs a pulse animation in
`private async UniTaskVoid PulseMode()`, reached from `HandleNumberButton` when a digit
button is pressed. The compiler-generated state machine `<PulseMode>d__8.MoveNext()`
contains the two broken call sites:

```csharp
UniTask.Delay(550, false, (PlayerLoopTiming)8, UniTaskCancellationExtensions.GetCancellationTokenOnDestroy(keypad));  // line 112
UniTask.Delay(200, false, (PlayerLoopTiming)8, UniTaskCancellationExtensions.GetCancellationTokenOnDestroy(keypad));  // line 131
```

`(PlayerLoopTiming)8` is `PlayerLoopTiming.Update`. These are the ONLY `UniTask.Delay`
calls in the mod; the screen-input path (`ProcessInputValue`) sets `Setting` directly
and is unaffected. The observed runtime stack trace matches exactly:
`...Start -> Keypad.PulseMode -> Keypad.HandleNumberButton -> Keypad.InteractWith`.

## Additive fix (no source clone)
<!-- verified: 0.2.6228.27061 @ 2026-05-31 -->

The break can be repaired by a separate BepInEx/StationeersLaunchPad plugin that
Harmony-patches the affected mod; nothing from the affected mod has to be cloned or
redistributed.

The enabling fact: BepInEx ships HarmonyX (the `0Harmony.dll`), which reads target
method bodies through Mono.Cecil + MonoMod (`DynamicMethodDefinition`, `ILHook`,
`ILContext`), reading IL from the target assembly's OWN metadata rather than resolving
every call token against the live runtime. That means the broken method body can be
read and rewritten at the IL level even though one of its call tokens no longer resolves
at runtime.

Two additive shapes work:

1. **IL redirect (faithful).** Patch the state-machine `MoveNext` (or use a raw MonoMod
   `ILHook`) and retarget each `call UniTask.Delay(int, bool, PlayerLoopTiming, CancellationToken)`
   to a shim `static UniTask DelayShim(int, bool, PlayerLoopTiming, CancellationToken)`
   compiled against the CURRENT UniTask (so the shim emits the five-argument call). The
   shim takes the same four arguments the original IL pushes, so stack arity is
   preserved and no rebalancing is needed. Keeps the mod's exact logic.

2. **Prefix-skip + reimplement (simplest, cannot fail to apply).** Put a Harmony Prefix
   on the kickoff method (e.g. `PulseMode()`), whose own IL is clean (it only constructs
   the state machine and calls `builder.Start`; the broken call lives in `MoveNext`).
   Return `false` to skip the original and run a corrected `async UniTaskVoid`
   reimplementation that calls the present overload. The original state machine is never
   instantiated, so its `MoveNext` is never JIT-compiled and the exception never fires.
   See [HarmonyPrefixReturnBool](./HarmonyPrefixReturnBool.md) and
   [AsyncHarmonyTrap](./AsyncHarmonyTrap.md).

General prevention for our own mods: when a bundled library method gains an optional
parameter, rebuild against the current game DLLs. The break only bites pre-built
binaries, never source rebuilt against the new signature.

## Verification history
<!-- verified: 0.2.6228.27061 @ 2026-05-31 -->

- 2026-05-31: Page created at game version 0.2.6228.27061. UniTask.Delay overloads read
  from the current `UniTask.dll` decompile (lines 19077-19117); KeypadMod call sites read
  from its decompile (lines 112, 131); failure mechanism corroborated by the runtime
  stack trace on Workshop item 3478434324 and by the UniTask.dll file-modified date
  (2026-03-26) aligning with the first bug reports (2026-03-30 onward). HarmonyX
  Cecil/ILHook body-reading confirmed from the bundled `0Harmony.dll` decompile.

## Open questions
<!-- verified: 0.2.6228.27061 @ 2026-05-31 -->

- Whether a plain HarmonyX Transpiler on the state-machine `MoveNext` can convert the
  unresolvable `UniTask.Delay` call operand to a `CodeInstruction` without throwing
  during its Cecil-to-reflection step (some HarmonyX versions resolve operands eagerly).
  A raw MonoMod `ILHook` (pure Cecil operand surgery) and the prefix-skip approach both
  sidestep the question; this only matters if the in-place Transpiler form is chosen.
  Resolve by runtime test if that form is pursued.
