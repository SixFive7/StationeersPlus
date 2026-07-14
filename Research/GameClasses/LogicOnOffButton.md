---
title: LogicOnOffButton
type: GameClasses
created_in: 0.2.6228.27061
verified_in: 0.2.6228.27061
verified_at: 2026-06-02
sources:
  - $(StationeersPath)\rocketstation_Data\Managed\Assembly-CSharp.dll :: Assets.Scripts.Objects.Pipes.LogicOnOffButton, Assets.Scripts.Objects.Pipes.LogicUnitButtonState
  - .work/decomp/0.2.6228.27061/Assembly-CSharp.decompiled.cs :: lines 174840-174964 (LogicOnOffButton + state enum), 384230 (LogicUnitBase.OnOffButton field), 384356-384358 + 384563-384570 (LogicUnitBase RefreshState trigger)
related:
  - ./Transformer.md
  - ../Patterns/UnityMaterialPerInstance.md
tags: [logic, ui, unity]
---

# LogicOnOffButton

The canonical "device button that goes orange when there's an error" widget. A `MonoBehaviour` attached to every `LogicUnitBase` prefab (Logic Reader, Logic Writer, Logic Memory, Logic Stopwatch, Logic Math, IC Housing, etc.). It is NOT attached to `ElectricalInputOutput` lineage prefabs (Transformer, Battery, AreaPowerControl, PowerTransmitter, PowerReceiver) -- so transformers do not flash on error in vanilla, and a mod that wants this behaviour on a transformer must either graft a custom component on (preferred: `MaterialPropertyBlock` emission lerp; see related Patterns page) or attach a `LogicOnOffButton` clone with its own shipped material assets.

## Where it lives
<!-- verified: 0.2.6228.27061 @ 2026-06-02 -->

Field declaration on `LogicUnitBase` (decompile L384230):

```csharp
public LogicOnOffButton OnOffButton;
```

`LogicUnitBase` exposes the field as a serialized reference; the actual widget is positioned and materialised in the prefab. Subclasses inherit the field (Logic Reader, Logic Writer, Logic Memory, Logic Stopwatch, Logic Math, IC Housing, all the other "logic unit" prefabs).

`Transformer : ElectricalInputOutput : Device : SmallGrid : Thing` (L373755, L403300). `LogicUnitBase : SmallDevice : Device : SmallGrid : Thing` (L384220). The two lineages diverge at `Device`; the `OnOffButton` field is unique to the `LogicUnitBase` branch.

## The four-state machine
<!-- verified: 0.2.6228.27061 @ 2026-06-02 -->

The button has three logical states and four materials:

```csharp
public enum LogicUnitButtonState
{
    Off,
    OnPowered,
    Error
}
```

(Decompile L174840-174845.)

Four `[SerializeField] private Material` assets bound in the prefab (L174849-174858): `powerOff`, `powerOn`, `error`, `errorEmissive`. The first three are stable steady-state materials; `error` and `errorEmissive` alternate at 2 Hz during the Error state.

## State transition trigger
<!-- verified: 0.2.6228.27061 @ 2026-06-02 -->

`RefreshState()` (decompile L174885-174932) reads `parentLogicUnit.Powered` and `parentLogicUnit.Error` to pick the new state:

```csharp
if (parentLogicUnit.Powered)
{
    logicUnitButtonState = LogicUnitButtonState.OnPowered;
    if (parentLogicUnit.Error == 1)
    {
        logicUnitButtonState = LogicUnitButtonState.Error;
    }
}
else
{
    logicUnitButtonState = LogicUnitButtonState.Off;
}
if (logicUnitButtonState == _currentState)
{
    return;
}
_currentState = logicUnitButtonState;
switch (_currentState)
{
case LogicUnitButtonState.Off:
    if (parentLogicUnit.ShouldPlayLogicSound)
        parentLogicUnit.PlayPooledAudioSound(Defines.Sounds.LogicOffBeep, SoundOffset);
    buttonRenderer.material = powerOff;
    break;
case LogicUnitButtonState.OnPowered:
    if (parentLogicUnit.ShouldPlayLogicSound)
        parentLogicUnit.PlayPooledAudioSound(Defines.Sounds.LogicOnBeep, SoundOffset);
    buttonRenderer.material = powerOn;
    break;
case LogicUnitButtonState.Error:
    if (_errorFlashTask.Status != UniTaskStatus.Pending)
        _errorFlashTask = ErrorFlashing();
    break;
}
```

`RefreshState()` is invoked from `LogicUnitBase.OnInteractableUpdated` (decompile L384563-384570) when the device's `Powered` or `Error` interactable state flips:

```csharp
if (OnOffButton != null
    && (interactable.Action == InteractableType.Powered || interactable.Action == InteractableType.Error))
    OnOffButton.RefreshState();
```

So the canonical signal source is the `Interactable` of type `Powered` or `Error`. Server-side mutation of `Thing.Error` via `OnServer.Interact(InteractError, ...)` fires the standard animator-state replication; the client receives it, `OnInteractableUpdated` runs on the client, `RefreshState()` runs, and the flash starts. Multiplayer is implicit.

## The 250 ms flash loop
<!-- verified: 0.2.6228.27061 @ 2026-06-02 -->

`ErrorFlashing()` (decompile L174942-174963) is an `async UniTask` driving the alternation:

```csharp
private async UniTask ErrorFlashing()
{
    while (parentLogicUnit != null && _currentState == LogicUnitButtonState.Error)
    {
        if (parentLogicUnit.ShouldPlayLogicSound)
            parentLogicUnit.PlayPooledAudioSound(Defines.Sounds.LogicErrorBeep, SoundOffset);
        buttonRenderer.material = errorEmissive;
        await UniTask.Delay(250);
        if (_currentState != LogicUnitButtonState.Error)
            break;
        buttonRenderer.material = error;
        await UniTask.Delay(250);
        if (_currentState != LogicUnitButtonState.Error)
            break;
    }
}
```

250 ms on, 250 ms off, 2 Hz cycle. The loop exits cleanly when `_currentState` leaves `Error` (another `RefreshState()` call switched it). It is a coroutine, not an animation curve and not an `Animator`; the in-prefab `MeshRenderer.material` field is reassigned each cycle.

Audio: `Defines.Sounds.LogicErrorBeep` plays once per 500 ms cycle when `ShouldPlayLogicSound` is true (gated to avoid spam in dense logic networks).

Bookkeeping fields (decompile L174869-174871):

```csharp
private UniTask _errorFlashTask;
private CancellationTokenSource _errorFlashCancel;
```

The `CancellationTokenSource` is declared but never used in the visible body of `ErrorFlashing()`; the loop self-terminates on the `_currentState` check.

## Implications for modders
<!-- verified: 0.2.6228.27061 @ 2026-06-02 -->

A mod that wants `LogicOnOffButton`-style flashing on a device outside `LogicUnitBase` (e.g. a Transformer) cannot inherit the behaviour. Three viable approaches:

1. **`MaterialPropertyBlock` emission lerp on a custom MonoBehaviour.** Add a small component to the device's prefab GameObject (via runtime `AddComponent` from a Harmony postfix on `Transformer.OnRegistered`), find the on / off button's `MeshRenderer` by name or hierarchy walk, and lerp the emission color of a `MaterialPropertyBlock` at 2 Hz. No prefab asset modification, no shipped materials. Robust against vanilla material updates. See related Patterns page.
2. **Clone the LogicOnOffButton class.** Ship four custom materials in the mod's AssetBundle; attach a `LogicOnOffButton`-shaped component referencing them; drive `RefreshState()` from your own logic. Heavyweight (asset import); strictly preserves the vanilla look.
3. **Reuse `Thing.Error == 1` and hope the prefab's existing animator picks it up.** Almost never works for non-`LogicUnitBase` devices because no animator state is bound; the field flip is silent. Verify per-prefab via InspectorPlus before relying on this.

## Verification history

- 2026-06-02: page created. Sourced from Agent 5's PowerGridPlus transformer-priority research turn (session scratch under `.work/`, since removed). Decompile lines verified against `.work/decomp/0.2.6228.27061/Assembly-CSharp.decompiled.cs`.

## Open questions

- Whether the four `Material` assets are reused across every `LogicUnitBase` prefab or each prefab has its own bound materials. Affects whether a clone-the-LogicOnOffButton approach can share asset references.
- Whether `LogicUnitBase` subclasses outside the "logic family" (e.g. cartridge readers, sensor lenses) reuse `OnOffButton` or omit it. Verify via InspectorPlus snapshot on a representative sample.
