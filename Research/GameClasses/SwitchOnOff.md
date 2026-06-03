---
title: SwitchOnOff
type: GameClasses
created_in: 0.2.6228.27061
verified_in: 0.2.6228.27061
verified_at: 2026-06-03
sources:
  - .work/decomp/0.2.6228.27061/Assembly-CSharp.decompiled.cs :: lines 32327-32365 (DevicePart abstract base, parentThing field, SetParentThing, abstract RefreshState), 137865 (namespace Objects opens), 138371 (DevicePart declaration), 138394-138497 (SwitchOnOff : DevicePart class header, 4 SerializeField materials off/on/onPowered/error[], switchRenderer reference, _currentColorState, IsOn, RefreshState wrapper, RefreshPositionState body, RefreshColorState body with full material-swap on state change)
  - Investigated while triaging a PowerGridPlus flash-color bug: per-property emission writes to the vanilla SwitchOnOff material were silently ineffective because the on/onPowered materials carry baked _EmissionMap textures that mask any per-property tint. The fix required understanding that RefreshColorState swaps the whole material (not just a color) and only on state CHANGE, not per-frame.
related:
  - ./Interactable.md
  - ./Transformer.md
tags: [prefab, unity]
---

# SwitchOnOff

`Objects.SwitchOnOff : DevicePart` (decompile L138394) is the Stationeers MonoBehaviour that paints the on/off button face on most powered devices (transformers, batteries, APCs, most appliances). It owns four materials (`off`, `on`, `onPowered`, `error[]`) and one `switchRenderer` MeshRenderer, and on a state transition it does a full `switchRenderer.material = <slot>` swap. It is NOT an `Interactable` and does NOT extend `LogicOnOffButton`; on a Stationeers transformer the on/off button has only Transform + `SwitchOnOff` + BoxCollider + MeshFilter + MeshRenderer.

The class is in the bare `Objects` namespace, NOT `Assets.Scripts.Objects`. The two parents (`Objects` and `Assets.Scripts.Objects`) coexist in vanilla; a mod that consumes `SwitchOnOff` by name in a `using Assets.Scripts.Objects;` context will fail to compile and needs an explicit `using global::Objects;` or qualified reference like `global::Objects.SwitchOnOff`.

## DevicePart base + parentThing
<!-- verified: 0.2.6228.27061 @ 2026-06-03 -->

`DevicePart` (L138371) is `public abstract class DevicePart : MonoBehaviour` in the same `Objects` namespace as `SwitchOnOff`. Its only state is the `[SerializeField] private Thing parentThing` field (L32328) plus a `_animationCancellation`. The base exposes `SetParentThing(Thing)` to override the auto-discovered parent and `RefreshState(bool skipAnim)` as the abstract entry point each subclass overrides.

Auto-discovery: `DevicePart.OnEnable` (around L32327-32352) does `if (parentThing == null) parentThing = GetComponentInParent<Thing>();` so prefabs without an explicit `parentThing` SerializeField assignment resolve at GameObject-enable time.

For a Harmony patch on `SwitchOnOff.RefreshColorState` that needs to filter by parent type, the `parentThing` field on `DevicePart` is the right access point: reach it via `AccessTools.Field(typeof(Objects.DevicePart), "parentThing")`. The field is `private` on `DevicePart` so the subclass `SwitchOnOff` only sees it through inheritance; direct reflection on `typeof(SwitchOnOff).GetField("parentThing")` returns null without `BindingFlags.NonPublic | BindingFlags.Instance` because the field is declared on the base.

## Four serialized materials and the SwitchColorState enum
<!-- verified: 0.2.6228.27061 @ 2026-06-03 -->

`SwitchOnOff` carries four material fields (L138406-138415):

```csharp
[SerializeField] protected Material off;
[SerializeField] protected Material on;
[SerializeField] protected Material onPowered;
[SerializeField] protected Material[] error;
[SerializeField] protected MeshRenderer switchRenderer;
```

Plus `_currentColorState` (a `SwitchColorState` enum: `Off`, `On`, `OnPowered`, `Error`, declared L138358 in the same namespace) and `IsOn` (cached `parentThing.OnOff`).

The four materials are baked at prefab-author time: on Stationeers Luna transformers, the `off` and `on` slots typically use a flat-coloured material with no emission, while `onPowered` carries an `_EmissionMap` texture (a green glowing decal) that produces the visible green LED. A mod that writes the `_EmissionColor` property on the `onPowered` material has its tint MULTIPLIED by the emission map texture sample at each pixel; the texture's green channel dominates and the tint is barely visible. This is why a naive "tint the LED orange when shed" approach against the live material produces the same green glow regardless of the colour written.

## RefreshState fan-out
<!-- verified: 0.2.6228.27061 @ 2026-06-03 -->

`RefreshState(bool skipAnim)` (L138436) is the public entry point:

```csharp
public override void RefreshState(bool skipAnim = false)
{
    if (!(parentThing == null))
    {
        RefreshColorState(skipAnim);
        RefreshPositionState(skipAnim);
    }
}
```

`RefreshPositionState` (L138442) toggles a translation between two cached `Vector3`s (default `offPosition = (-13.5, 0, 0)`, `onPosition = (13.5, 0, 0)`) when `parentThing.OnOff != IsOn`. It also fires `Defines.Sounds.SwitchOn` / `SwitchOff` if within `AudibleSqrDistance = 121f` of the player and not `skipAnim`.

## RefreshColorState: state-change full material swap
<!-- verified: 0.2.6228.27061 @ 2026-06-03 -->

`RefreshColorState` (L138462) decides the target colour state from the parent's `OnOff`, `Powered`, `HasPowerState`, and `Error`:

```csharp
protected virtual void RefreshColorState(bool skipAnim)
{
    SwitchColorState switchColorState = SwitchColorState.Off;
    if (parentThing.OnOff)
        switchColorState = ((parentThing.Powered || !parentThing.HasPowerState)
                            ? SwitchColorState.OnPowered : SwitchColorState.On);
    if ((parentThing.Error != 0 && parentThing.Powered)
        || (parentThing.Error != 0 && !parentThing.HasPowerState))
        switchColorState = SwitchColorState.Error;

    if (switchColorState != _currentColorState)
    {
        _currentColorState = switchColorState;
        CancelErrorAnimation();
        switch (_currentColorState)
        {
        case SwitchColorState.Off:       switchRenderer.material = off; break;
        case SwitchColorState.On:        switchRenderer.material = on; break;
        case SwitchColorState.OnPowered: switchRenderer.material = onPowered; break;
        case SwitchColorState.Error:
            _errorStateCancellationTokenSource = new CancellationTokenSource();
            ErrorAnimation(_errorStateCancellationTokenSource.Token).Forget();
            break;
        }
    }
}
```

Key observations for any mod that wants to override the LED colour:

- **Full material swap, not property write.** `switchRenderer.material = <slot>` replaces the entire material reference, blowing away any prior MPB or sharedMaterial-instance state from the previous frame. A mod that mutates the live material is wiped on the next state transition.
- **Only on state CHANGE.** The swap is gated on `switchColorState != _currentColorState`. Between transitions the material reference is stable, so a mod that REPLACES `switchRenderer.sharedMaterial` (or `material`) once per shed-enter survives until the next genuine OnOff / Powered / Error transition. This is the override window.
- **Error path is separate.** Error doesn't swap to a single material; it kicks off a `ErrorAnimation` UniTask (the 250 ms flash documented under "4 materials" notes in earlier research) that paints `error[i]` materials in a loop. Cancelling the token (via `CancelErrorAnimation`) stops the animation; a Harmony prefix that returns false from `RefreshColorState` ALSO needs to call `CancelErrorAnimation` if it might transition the parent into an Error state mid-shed.

## Mod override strategy (verified pattern)
<!-- verified: 0.2.6228.27061 @ 2026-06-03 -->

The pattern PowerGridPlus uses to paint the LED orange during a shed lockout:

1. **Harmony prefix on `RefreshColorState`** that returns false when the parent is a target Thing in the special state (here: `parentThing is Transformer && BrownoutRegistry.IsShedding(refId, currentTick)`). This stops vanilla from swapping back to the on/off/onPowered material while the mod is painting.
2. **A companion MonoBehaviour on the parent Thing's GameObject** that, on shed-enter, swaps `switchRenderer.sharedMaterial = customOrangeInstance`. The runtime material is a clone of the prefab's original sharedMaterial with `_EmissionMap` cleared (so the baked green texture doesn't multiply against the orange) and the `_EMISSION` shader keyword enabled. The orange `_EmissionColor` is then animated per frame.
3. **On shed-exit**, restore `switchRenderer.sharedMaterial = originalSharedMaterial` (cached at Init). The Harmony prefix stops short-circuiting, vanilla's next `RefreshColorState` call resumes the normal On/Off/OnPowered swap.

The prefix is mandatory: without it, the FIRST OnOff or Powered transition during shed (e.g. the player toggling the button mid-shed, or the grid hiccupping the powered state) restores vanilla's material in step 1's place and the orange disappears until the next shed-enter.

Per-property writes against the `on` / `onPowered` materials don't work for the green-LED case because of the baked `_EmissionMap`. Per-renderer `MaterialPropertyBlock` writes against `_EmissionColor` also don't survive the multiplication by the emission texture. The full material swap is the only path that bypasses both.

## Verification history

- 2026-06-03: page created. Sourced from `.work/decomp/0.2.6228.27061/Assembly-CSharp.decompiled.cs`. Driving question: why does writing `_EmissionColor` on the on/off button material fail to override the green glow on a Stationeers transformer LED. Verbatim extracts: `DevicePart` header (L138371), `DevicePart.parentThing` field (L32328) and OnEnable auto-discovery (L32327-32352), `Objects` namespace opens (L137865) and `Objects.Structures` close (L139049) so SwitchOnOff is anchored in bare `Objects`, `SwitchOnOff` class header + 4 material fields + switchRenderer (L138394-138418), `_currentColorState`/`IsOn`/`_errorStateCancellationTokenSource` (L138423-138436), `RefreshState` wrapper (L138436-138441), `RefreshPositionState` body with switchTransform.localRotation + audible sound (L138442-138461), `RefreshColorState` decision logic and state-gated full material swap (L138462-138497). Diagnostic confirmation via PowerGridPlus's ScenarioRunner BFB-HIER probe: GameObject component list on a live transformer's `SwitchOnOff` child was `[Transform, SwitchOnOff, BoxCollider, MeshFilter, MeshRenderer]` with no LogicOnOffButton, no Animator, on materials `ColorYellow (Instance)` (housing) and `ColorGreenEmissive (Instance)` (LED).

## Open questions

None.
