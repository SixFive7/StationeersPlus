# KeypadMod Fix Research

Durable internals for KeypadMod Fix, a temporary compatibility patch for the third-party KeypadMod (by WIKUS, Workshop item 3478434324).

## Why this mod exists

The 2026-03-26 Stationeers update (game version 0.2.6228.27061) updated the bundled `UniTask.dll`. Every `UniTask.Delay` overload gained a trailing optional parameter `bool cancelImmediately = false`. Adding an optional parameter is source-compatible but binary-incompatible: KeypadMod (last built September 2025 against game 0.2.4767) has the old four-parameter `UniTask.Delay(int, bool, PlayerLoopTiming, CancellationToken)` baked into its IL, so the call no longer resolves and throws `MissingMethodException` at runtime. Full background: `Research/Patterns/UniTaskDelaySignatureDrift.md`.

A reference-resolution audit of `KeypadMod.dll` against game 0.2.6228.27061 (Mono.Cecil, every type and member reference) found exactly one broken reference: that `UniTask.Delay` call, in the compiler-generated `<PulseMode>d__8` async state machine. Nothing else in the mod is binary-broken (all 106 type references and all but that one of 147 member references resolve). A separate semantic review found one pre-existing, not-update-related bug: the multiplayer screen-input path. KeypadMod Fix addresses both.

## Architecture

A single BepInEx plugin (`Plugin.cs`) with one patch class (`KeypadPatches.cs`). The plugin hard-depends on StationeersLaunchPad and soft-depends on KeypadMod (resolved by name at runtime). It adds no prefabs, assets, settings, or network messages.

## File walkthrough

- `Plugin.cs`: BepInEx entry point. In `Awake` it subscribes to `Prefab.OnPrefabsLoaded`. KeypadMod is a StationeersMods content mod loaded by StationeersLaunchPad, so its assembly is not present at BepInEx `Awake` time. `OnPrefabsLoaded` fires once on the main thread after every mod is loaded, at which point `KeypadPatches.Apply` runs (wrapped in try/catch so a failure logs instead of crashing load).
- `KeypadPatches.cs`: resolves `keypadmod.Keypad` via `AccessTools.TypeByName`. If absent, it logs and returns. Otherwise it applies two Harmony prefixes imperatively (the target type is not a compile-time reference). Each target method and field is resolved defensively; a missing one disables only its own fix and logs a warning.

## Patch catalog

### PulseMode prefix (fix 1: the crash)

`keypadmod.Keypad.PulseMode()` is an `async UniTaskVoid` whose compiled state machine calls the now-missing four-parameter `UniTask.Delay`. The prefix returns false to skip the broken original (its returned `UniTaskVoid` is a no-op default, confirmed empty in the decompile) and runs `RunPulse`, an identical reimplementation compiled against the current UniTask so the `Delay` call binds to the present five-parameter overload. The original behavior is preserved exactly: guard on `!_interactionLocked && Powered && _initialized`, set `LogicType.Mode` (3) high, wait 550 ms, set it low, wait 200 ms, release the lock. The two private bools are read and written through `AccessTools.FieldRef<object, bool>`. Because the original `PulseMode` is skipped, its broken state machine is never instantiated and its `MoveNext` is never JIT-compiled, so the exception never occurs.

### ProcessInputValue prefix (fix 2: multiplayer screen input)

`keypadmod.Keypad.ProcessInputValue(string, string)` builds a `SetLogicFromClient` message but never sets its `LogicType`, leaving it at `LogicType.None`. The server's `SetLogicFromClient.Process` gates on `setable.CanLogicWrite(LogicType)`; the keypad does not allow writing `None`, so the value is dropped and a multiplayer-client / dedicated-server keypad keeps showing its old value. The prefix re-runs the method with `LogicType.Setting` (12) on the message, the channel `keypadmod.Keypad` overrides `CanLogicWrite` / `SetLogicValue` to accept. Single-player and host take the `else` branch (set `Setting` directly), which already worked, so behavior there is unchanged.

## Decompiled game internals

- `UniTask.Delay` current signatures and the optional-parameter binary-break mechanism: `Research/Patterns/UniTaskDelaySignatureDrift.md`.
- `Prefab.OnPrefabsLoaded` is `public static event Action`, invoked once at the end of `Prefab.LoadAll` after all prefabs are registered: the canonical "all mods loaded" main-thread hook. `Research/GameSystems/ModLoadSequence.md`.
- `SetLogicFromClient` (`Assets.Scripts.Networking`) has public `long LogicId`, `LogicType LogicType`, `double Value`; `Process` applies the value only when `CanLogicWrite(LogicType)` is true. `Research/Protocols/GameMessageFactory.md`.
- Namespace notes used here: `LogicType` is in `Assets.Scripts.Objects.Motherboards`; `MessageBase<T>` (with public `SendToServer`) is in `UnityEngine.Networking`; `LogicUnitBase` is in `Assets.Scripts.Objects.Electrical` while `Device` is not. `Research/Patterns/StationeersNamespaces.md`.

## Pitfalls

- Patch timing: patching in BepInEx `Awake` fails because `keypadmod.Keypad` is not loaded yet. Defer to `Prefab.OnPrefabsLoaded`.
- The target is a third-party type with no compile-time reference, so patches are applied imperatively (`harmony.Patch(AccessTools.Method(...), prefix: ...)`), not via `[HarmonyPatch(typeof(...))]`.
- `Device` is not in `Assets.Scripts.Objects.Electrical`; type the patched instance as `LogicUnitBase` (which is) to reach the inherited `SetLogicValue`.

## Design decisions

- Prefix-skip + reimplement, rather than an IL transpiler redirect on the state machine: `PulseMode` (the kickoff) has clean IL with no reference to the missing method, so Harmony can read and patch it safely; the broken call lives only in the state machine's `MoveNext`, which is never reached once the kickoff is skipped. An in-place transpiler on `MoveNext` would be more faithful but risks HarmonyX failing to resolve the missing-method operand during its Cecil-to-reflection step. The pulse logic is trivial, so the reimplementation cost is negligible.
- Soft dependency: KeypadMod loads through StationeersMods, not BepInEx, so it cannot be a `[BepInDependency]`. Resolution by name with a graceful no-op keeps this mod safe to ship even to users who do not have KeypadMod, and keeps it from erroring if KeypadMod's shape changes.
