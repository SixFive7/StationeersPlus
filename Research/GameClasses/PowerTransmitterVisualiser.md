---
title: PowerTransmitterVisualiser
type: GameClasses
created_in: 0.2.6228.27061
verified_in: 0.2.6228.27061
verified_at: 2026-05-26
sources:
  - $(StationeersPath)\rocketstation_Data\Managed\Assembly-CSharp.dll :: PowerTransmitterVisualiser
  - $(StationeersPath)\BepInEx\LogOutput.log (NRE captured 2026-05-26, in-session repro of teardown race)
related:
  - ./PowerTransmitter.md
  - ./WirelessPower.md
  - ../Patterns/MainThreadDispatcher.md
  - ../GameSystems/PowerTickThreading.md
tags: [power, threading, unity]
---

# PowerTransmitterVisualiser

`MonoBehaviour` in the **global namespace** (no `Assets.Scripts...` prefix) attached to the dish prefab of every `WirelessPower` descendant (transmitter and receiver). Drives the line-renderer beam material between the dish and its linked partner: color, emission, wave-scroll direction, alpha-as-intensity. Owned by `WirelessPower` via the protected field `PowerTransmitterVisualiser PowerTransmitterVisualiser`.

## Class definition
<!-- verified: 0.2.6228.27061 @ 2026-05-26 -->

Verbatim from `Assembly-CSharp.dll :: PowerTransmitterVisualiser` (decompiled at game version 0.2.6228.27061):

```csharp
public class PowerTransmitterVisualiser : MonoBehaviour
{
    public enum Direction { In, Out }

    [SerializeField] private LineRenderer _lineRenderer;

    private static readonly int COLOR = Shader.PropertyToID("_Color");
    private static readonly int WAVESPEED = Shader.PropertyToID("_WaveSpeed");

    [SerializeField] private UnityEngine.Color InnerColor = UnityEngine.Color.white;

    [ColorUsage(false, true)]
    [SerializeField] private UnityEngine.Color EmissionColor = UnityEngine.Color.red * 10f;

    public void Activate() {
        InnerColor.a = 1f;
        EmissionColor.a = 1f;
        _lineRenderer.material.DOColor(InnerColor,    COLOR,                 0f);
        _lineRenderer.material.DOColor(EmissionColor, Thing.EMISSION_COLOR,  0f);
    }

    public void Deactivate() {
        InnerColor.a = 0f;
        EmissionColor.a = 0f;
        _lineRenderer.material.DOColor(InnerColor,    COLOR,                 0f);
        _lineRenderer.material.DOColor(EmissionColor, Thing.EMISSION_COLOR,  0f);
    }

    public void SetDirection(Direction direction) {
        int num = ((direction != Direction.In) ? 1 : (-1));
        _lineRenderer.material.SetFloat(WAVESPEED, num);
    }

    public void SetIntensity(float intensity) {
        if (ThreadedManager.IsThread) {
            UnityMainThreadDispatcher.Instance().Enqueue(delegate {
                SetMaterialPropertiesForIntensity(intensity);
            });
        } else {
            SetMaterialPropertiesForIntensity(intensity);
        }
    }

    private void SetMaterialPropertiesForIntensity(float intensity) {
        intensity = Mathf.Clamp01(intensity);
        InnerColor.a    = intensity;
        EmissionColor.a = intensity;
        _lineRenderer.material.DOColor(InnerColor,    COLOR,                 1f);
        _lineRenderer.material.DOColor(EmissionColor, Thing.EMISSION_COLOR,  1f);
    }
}
```

`Thing.EMISSION_COLOR = Shader.PropertyToID("_EmissionColor")` (vanilla constant on `Thing`). Inner-color and emission-color defaults are overridden per prefab at the serialized-field layer; for the Microwave Power Transmitter dish prefab the values are `InnerColor = (1, 1, 1, 1)` white and `EmissionColor = (0, 0.4915, 10, 10)` HDR cyan-blue. See [PowerTransmitter.md](./PowerTransmitter.md) "Serialized values" for the extracted prefab data.

## SetIntensity dispatch pattern
<!-- verified: 0.2.6228.27061 @ 2026-05-26 -->

`SetIntensity` is the only method on this class that handles thread routing. `Activate`, `Deactivate`, `SetDirection` and the private `SetMaterialPropertiesForIntensity` assume the caller is on the Unity main thread; they will hard-crash the native player if invoked off-thread.

The dispatch path is fire-and-forget:

1. Caller invokes `SetIntensity(intensity)` from any thread.
2. `ThreadedManager.IsThread` distinguishes the Unity main thread from the UniTask ThreadPool worker.
3. Off main thread: a `delegate { SetMaterialPropertiesForIntensity(intensity); }` closure is enqueued on the singleton `UnityMainThreadDispatcher` and `SetIntensity` returns immediately. The dispatcher drains the queue in its own `Update()` on the next main-thread frame.
4. On main thread: `SetMaterialPropertiesForIntensity` is called directly, no queueing.

The closure captures `intensity` (by value) and `this` (by reference, since `SetMaterialPropertiesForIntensity` is an instance method). C# synthesizes the closure as a `<>c__DisplayClass9_0` instance with a `<SetIntensity>b__0` method; both names appear in stack traces under this exact pattern.

The primary off-thread caller in vanilla is `WirelessPower.VisualizerIntensity` setter, which calls `_visualizer.SetIntensity(value)` whenever the underlying `PowerTransmitter` / `PowerReceiver` updates its visualizer intensity during a `PowerTick.ApplyState` pass on the ThreadPool worker. See [../GameSystems/PowerTickThreading.md](../GameSystems/PowerTickThreading.md).

## Scene-unload race
<!-- verified: 0.2.6228.27061 @ 2026-05-26 -->

`PowerTransmitterVisualiser.SetIntensity`'s dispatch is not safe across scene unload. When the player exits to the main menu (or any other scene-replacing transition), the world scene unloads. `PowerTransmitterVisualiser` instances live on transmitter / receiver dish prefabs in the world scene and are destroyed along with it; their `_lineRenderer` SerializeField becomes a fake-null (and the native Renderer behind it is gone).

The `UnityMainThreadDispatcher` itself is on a `DontDestroyOnLoad` object, so it survives the unload and drains whatever was queued on the next main-thread `Update()`. Any closures queued *before* exit but not yet drained will fire *after* unload. Each such closure runs `SetMaterialPropertiesForIntensity`, which calls `_lineRenderer.material`. Accessing `Renderer.material` on a destroyed Renderer throws `NullReferenceException` inside the Unity native getter (`UnityEngine.Renderer.GetMaterial`). The exception is logged via the standard Unity logger and does not crash the player.

Stack trace (verbatim from F0429, BepInEx LogOutput.log captured 2026-05-26, 6 identical entries during exit-to-main-menu transition from a save with 10 actively-transmitting transmitters):

```
NullReferenceException
  at (wrapper managed-to-native) UnityEngine.Renderer.GetMaterial(UnityEngine.Renderer)
  at UnityEngine.Renderer.get_material () [0x00001] in <c39a522eee05469b8171a6cfeb646c59>:0
  at PowerTransmitterVisualiser.SetMaterialPropertiesForIntensity (System.Single intensity) [0x00020] in <5c8a6c1acb0b448b9ae2986cadb6d3d0>:0
  at PowerTransmitterVisualiser+<>c__DisplayClass9_0.<SetIntensity>b__0 () [0x00000] in <5c8a6c1acb0b448b9ae2986cadb6d3d0>:0
  at Assets.Scripts.Util.UnityMainThreadDispatcher+<ActionWrapper>d__7.MoveNext () [0x00017] in <5c8a6c1acb0b448b9ae2986cadb6d3d0>:0
  at UnityEngine.SetupCoroutine.InvokeMoveNext (System.Collections.IEnumerator enumerator, System.IntPtr returnValueAddress) [0x00026] in <c39a522eee05469b8171a6cfeb646c59>:0
UnityEngine.MonoBehaviour:StartCoroutine(IEnumerator)
Assets.Scripts.Util.<>c__DisplayClass4_0:<Enqueue>b__0()
Assets.Scripts.Util.UnityMainThreadDispatcher:ManagerUpdate()
Assets.Scripts.GameManager:DMD<Assets.Scripts.GameManager::Update>(GameManager)
```

The exception count matches the number of transmitters that were still actively delivering power at the moment of exit (6 of 10 in the captured repro). Transmitters whose `VisualizerIntensity` had already settled to 0 before exit (no in-flight queue entry) do not produce the NRE.

Mitigation options for downstream mods that want to suppress the noise:

- Prefix `PowerTransmitterVisualiser.SetMaterialPropertiesForIntensity` to early-return when `_lineRenderer == null` (Unity fake-null check). Smallest patch.
- Prefix `PowerTransmitterVisualiser.SetIntensity` to wrap the enqueued closure in a `this != null` check before invoking `SetMaterialPropertiesForIntensity`. More correct.
- Hook `SceneManager.sceneUnloaded` and short-circuit the vanilla `WirelessPower.VisualizerIntensity` setter during teardown so the queue never receives new entries during the unload window. Most general; addresses other potential teardown races as well.

The exceptions are cosmetic only: the material write is harmless once the renderer is gone, and the queue empties on the next frame. Vanilla bug, not a downstream-mod bug.

## Cited verifications
<!-- verified: 0.2.6228.27061 @ 2026-05-26 -->

- F0428 (Assembly-CSharp.dll :: PowerTransmitterVisualiser): full class body, decompiled at game version 0.2.6228.27061 from .work/decomp/0.2.6228.27061/Assembly-CSharp.decompiled.cs:40416-40483.
- F0429 (BepInEx LogOutput.log, in-session repro 2026-05-26): six identical NREs on exit-to-main-menu from a save with 10 transmitters; stack reproduced above.

## Verification history
<!-- verified: 0.2.6228.27061 @ 2026-05-26 -->

- 2026-05-26: page created from F0428 (decompile) and F0429 (live repro NREs captured during exit-to-main-menu). Documents the SetIntensity dispatch pattern and the scene-unload race on the queued closures.

## Open questions

None at creation.
