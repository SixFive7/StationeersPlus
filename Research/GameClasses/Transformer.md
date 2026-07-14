---
title: Transformer
type: GameClasses
created_in: 0.2.6228.27061
verified_in: 0.2.6403.27689
verified_at: 2026-07-13
sources:
  - $(StationeersPath)\rocketstation_Data\Managed\Assembly-CSharp.dll :: Assets.Scripts.Objects.Electrical.Transformer, Assets.Scripts.Objects.Electrical.ElectricalInputOutput
  - .work/decomp/0.2.6228.27061/Assembly-CSharp.decompiled.cs :: lines 373755-373766 (ElectricalInputOutput fields), 403300-403545 (Transformer core), 403579-403591 (SetKnob), 403593-403644 (InteractWith), 403646-403686 (CheckError + OnAddCableNetwork / OnRemoveCableNetwork / CheckStateNextFrame), 403295-403299 + 403547-403571 (TransformerSaveData round-trip), 403333-403349 + 403373-403389 (Setting setter + BuildUpdate / ProcessUpdate), 297646-297785 (Thing+DelayedActionInstance)
  - Plans/PowerGridPlus/PLAN.md (phase 3 research); Mods/.../revolt-source/Assets/Scripts/Patches/TransformerExploitPatch.cs (Re-Volt patches this class)
  - .work/decomp/0.2.6403.27689/Assembly-CSharp.decompiled.cs :: lines 424598 (Transformer class), 424615-424630 (fields incl. _powerProvided 424621), 424748-424805 (power methods incl. AllowSetPower), 370351 (base Device.UsedPower), 424944-424984 (CheckError + event callers)
related:
  - ./Cable.md
  - ./PowerTransmitter.md
  - ./PowerTick.md
  - ./ElectricalInputOutput.md
  - ./LogicOnOffButton.md
tags: [power, logic]
---

# Transformer

Vanilla power transformer. `Assets.Scripts.Objects.Electrical.Transformer : ElectricalInputOutput, ISetable, ILogicable, ...`. Sits between two cable networks (input and output), draws up to `Setting` watts from the input network and provides up to that much to the output network. This is the only step-down / network-bridging device in vanilla aside from APCs.

## There is exactly one Transformer class
<!-- verified: 0.2.6228.27061 @ 2026-05-12 -->

`grep` for `class Transformer`, `: Transformer`, `TransformerLarge`, `TransformerSmall` in the decompile returns only the single `Transformer` class at line 403300 (the string `"Transformer"` elsewhere is an audio-clip key). Vanilla historically ships more than one transformer *prefab* (a smaller one), but they are the same `Transformer` class with a different serialized `OutputMaximum` and mesh -- there is no subclass hierarchy. A mod that wants a "heavy transformer" tier would register a new prefab of this same class (different `OutputMaximum`, different input/output `Connection` configuration, different mesh) via `AddPrefabs`; it does not need a new class.

## Class hierarchy and fields
<!-- verified: 0.2.6403.27689 @ 2026-07-13 -->

`ElectricalInputOutput` (the base):

```csharp
public class ElectricalInputOutput : Device, ISmartRotatable, ISubmergeable, IPowered, IDensePoolable, IReferencable, IEvaluable
{
    public CableNetwork InputNetwork;
    public CableNetwork OutputNetwork;
    ...
}
```

`ElectricalInputOutput` resolves `InputNetwork` / `OutputNetwork` in `CheckConnections()` from its serialized `InputConnection` / `OutputConnection` (`Connection` objects with `ConnectionRole.Input` / `Output`), each pointing at whichever adjacent cable that open end faces. `IsOperable` is false when `InputNetwork == OutputNetwork` (a transformer must bridge two distinct networks). It is `IsPowerInputOutput` and `IsPowerProvider`.

`Transformer`:

```csharp
public class Transformer : ElectricalInputOutput, ISetable, ILogicable, IReferencable, IEvaluable, IRocketInternals, IRocketComponent
{
    [Tooltip("The needle (required)")]
    public GameObject Needle;
    public float NeedleMinimum = -160f;
    public float NeedleMaximum = 160f;

    public float OutputMaximum = 10000f;
    public float StepSmall = 100f;
    public float StepNormal = 1000f;

    private float _outputSetting;
    ...
    [ByteArraySync]
    public double Setting
    {
        get => _outputSetting;
        set
        {
            _outputSetting = Mathf.Clamp((float)value, 0f, OutputMaximum);
            if (NetworkManager.IsServer)
                base.NetworkUpdateFlags |= 256;
            SetKnob();
        }
    }
}
```

`OutputMaximum = 10000f` is the C# default; the real per-prefab value is serialized (the vanilla small transformer is lower). `Setting` is clamped to `[0, OutputMaximum]`. Logic surface: `LogicType.Setting` (read/write, write clamps to `[0, OutputMaximum]`), `LogicType.Maximum => OutputMaximum`, `LogicType.Ratio => Setting / OutputMaximum`. (Re-Volt additionally exposes `LogicType.PowerActual` = current power provided; vanilla does not.)

`GetGeneratedPower(cableNetwork)` returns 0 unless `cableNetwork == OutputNetwork && Error != 1 && OnOff && InputNetwork != null`; otherwise (vanilla) `Mathf.Min((float)Setting, InputNetwork.PotentialLoad)` drawn from the input network's available potential into the output network. Method bodies re-verified unchanged at 0.2.6403.27689 (class declaration 424598; `_powerProvided` 424621; `UsePower` 424757-424763; `ReceivePower` 424765-424771; `GetUsedPower` = `Min((float)Setting + UsedPower, _powerProvided)` 424773-424792; `GetGeneratedPower` 424794-424805; verbatim bodies quoted on [AreaPowerControl](./AreaPowerControl.md), "Pattern presence in other ledger-based classes"). (Re-Volt rewrites `GetGeneratedPower` / `GetUsedPower` / `ReceivePower` to clamp output to `min(Setting, InputNetwork.PotentialLoad - alreadyProvided)` and charge the transformer's own `UsedPower` quiescent draw to the upstream network.)

Two details completing the power surface. `AllowSetPower(cableNetwork)` returns true only for the input side (verbatim, 424748-424755: `if (InputNetwork == cableNetwork) { return true; } return false;`), so only the input network's `ApplyState` may un-power a transformer; the whole-game override census and the ON/OFF asymmetry live on [PowerTick](./PowerTick.md), "ApplyState un-powers zero-demand and unfed devices". And the `UsedPower` term inside `GetUsedPower` (including the `Error == 1` branch, which bills `OnOff ? UsedPower : 0f` while the ledger path is bypassed) is the base `Device.UsedPower` field (370351, code default `10f`, per-prefab serialized), the transformer's own quiescent draw billed to the input network on top of the pass-through debt.

## Relevance to a "voltage tier" mod
<!-- verified: 0.2.6228.27061 @ 2026-05-12 -->

A transformer is already the only clean "bridge" between two cable networks (it forces `InputNetwork != OutputNetwork` via `IsOperable`). For a voltage-tier scheme where heavy cables are a separate tier reachable only through transformers, the design choices are: (1) gate which cable tier the transformer's `InputConnection` / `OutputConnection` may face (via a `CanConstruct` postfix, see [StructurePlacementValidation](../GameSystems/StructurePlacementValidation.md)); (2) add one new "heavy transformer" prefab of this class with a "heavy" input side and a "normal" output side; (3) treat the existing transformer prefab(s) as the step-down device. None of this requires a new class.

## Setting setter, sync flag, save round-trip
<!-- verified: 0.2.6228.27061 @ 2026-06-02 -->

The `Setting` property is `[ByteArraySync]`-tagged (decompile L403333). On every server-side write, the setter dirties the `256` flag in `NetworkUpdateFlags` and calls `SetKnob()` (L403340-403348). The `BuildUpdate` path (L403373-403380) writes `Setting` as a `WriteDouble` when bit 256 is set; `ProcessUpdate` (L403382-403389) reads it back with `ReadDouble`. On client join, the same value rides `SerializeOnJoin` / `DeserializeOnJoin` (L403391-403401) as a `double`.

Save round-trip uses the lightweight per-class save-data record:

```csharp
public class TransformerSaveData : StructureSaveData      // L403295
{
    [XmlElement] public float OutputSetting;
}
```

`SerializeSave()` (L403547-403552) creates a `TransformerSaveData`, calls `InitialiseSaveData(ref savedData)`; the override at L403554-403561 calls `base.InitialiseSaveData`, then assigns `transformerSaveData.OutputSetting = (float)Setting`. `DeserializeSave` (L403563-403571) calls `base.DeserializeSave`, then `Setting = transformerSaveData.OutputSetting; SetKnob();`. Only `Setting` is persisted at the Transformer level. `OutputMaximum` comes from the prefab on load, not the save; `Error`, `OnOff`, `Powered` ride the inherited `Thing` / `Device` animator-state save path.

Implication for a "rewire `Setting` to be a different field" mod: even if read returns something else and writes redirect elsewhere, the underlying `_outputSetting` field still ticks the 256 sync flag on every write. Either leave `_outputSetting` at its default (0) so the sync is harmless, or rewire the field too; both are valid. The save schema is one float; touching the schema would require a corresponding override and a side-car (see PowerGridPlus's `PrioritySideCar.cs` for the parallel pattern).

### `_powerProvided` is runtime-only: not in TransformerSaveData, not on the wire
<!-- verified: 0.2.6403.27689 @ 2026-07-06 -->

Re-verified at 0.2.6403.27689: `TransformerSaveData` (424593-424597) is unchanged, `[XmlElement] public float OutputSetting;` is still its only member, so "only `Setting` is persisted at the Transformer level" holds at this version too. The `_powerProvided` ledger (424621) has no save field and no join or delta-sync field: the whole-decompile reference census shows it touched only by `UsePower` (424761), `ReceivePower` (424769), and `GetUsedPower` (424791). It restarts at 0 on save load and on client join; residual debt survives only within a session. Cross-class census and consequences on [Device](./Device.md), "Two per-device draw-state fields", and [AreaPowerControl](./AreaPowerControl.md), "The ledger is not serialized".

## InteractWith button model and DelayedActionInstance state messages
<!-- verified: 0.2.6228.27061 @ 2026-06-02 -->

The in-world dial is two discrete `Interactable` widgets (`Button1` for decrement, `Button2` for increment), NOT a continuous slider. There is no `SettingWheel` / `InteractableSlider` widget on Transformer. `InteractWith` (decompile L403593-403644) handles the labeller / multi-tool path first via `HandleButtonSetting`; if that returns non-null the labeller handled it, otherwise:

```csharp
DelayedActionInstance delayedActionInstance2 = new DelayedActionInstance
{
    Duration = 0f,
    ActionMessage = interactable.ContextualName
};
delayedActionInstance2.AppendStateMessage(GameStrings.OutputWatts, StringManager.Get((int)Setting));   // "Output 1000 W"
delayedActionInstance2.AppendStateMessage(GameStrings.HoldForSmallIncrements, Localization.QuantityModifierKey);
delayedActionInstance2.AppendStateMessage(GameStrings.UseLabelerToSet);
double num = Setting;
switch (interactable.Action)
{
case InteractableType.Button2:
    delayedActionInstance2.ActionMessage = GameStrings.GlobalIncrease.AsString();
    if (GameManager.RunSimulation && doAction)
    {
        if (num < (double)OutputMaximum)
            num += (double)(interaction.AltKey ? StepSmall : StepNormal);
        Setting = Mathf.Min((float)num, OutputMaximum);
    }
    return delayedActionInstance2.Succeed();
case InteractableType.Button1:
    ... // mirror with StepSmall / StepNormal subtracted, clamped to >= 0
}
```

Step constants on Transformer: `StepNormal = 1000f`, `StepSmall = 100f` (decompile L403313-403315). Default click = `StepNormal`; Alt-modifier (`interaction.AltKey`, sourced from `KeyManager.GetButton(KeyMap.QuantityModifier)`) = `StepSmall`. The 10x ratio is consistent across vanilla "button-driven" interactables.

The hover text reads `"Output 1000 W"`, NOT `"Setting: X kW"`. The label is from `GameStrings.OutputWatts` ("Output `<color=green>{0} W</color>`", decompile L265397) and the value is `StringManager.Get((int)Setting)`. There is no per-`LogicType` label table; the word "Output" originates from this hand-written `GameString`. The unit is `W`, not `kW`.

The hover panel itself is composed inside `Thing.DelayedActionInstance` (a nested class at decompile L297646; full type name `Assets.Scripts.Objects.Thing.DelayedActionInstance`). The state-message API has both `AppendStateMessage(GameString)` and `AppendStateMessage(GameString, string arg0)` overloads; a raw `string` is accepted through `GameString.AsString(...)` returning a string that the caller hands in directly (decompile L139653 `result.AppendStateMessage(GameStrings.DeviceOffOrUnpowered.AsString(DisplayName));`). Internally the body uses `_stateMessageBuilder.AppendLine(...)`.

`GameStrings.GlobalIncrease.AsString()` and `GlobalDecrease.AsString()` produce the top-line action header; the per-step audio plays via `PlayPooledAudioSound(Defines.Sounds.DialTurn, _needleTransform.localPosition)` on every successful click. `Setting` writes are gated on `GameManager.RunSimulation` (host-only).

## SetKnob needle math
<!-- verified: 0.2.6228.27061 @ 2026-06-03 -->

`SetKnob()` (decompile L403587-403597) drives the visible needle GameObject's local rotation. Verbatim:

```csharp
public void SetKnob()
{
    if (ThreadedManager.IsThread) { SetKnobFromThread().Forget(); }
    else if (!(_needleTransform == null))
    {
        _needleRotation = Mathf.Lerp(NeedleMinimum, NeedleMaximum, (float)Setting / OutputMaximum);
        _needleTransform.localRotation = _needleBaseRotation;
        _needleTransform.Rotate(0f, _needleRotation, 0f, Space.Self);
    }
}
```

Defaults: `NeedleMinimum = -160f`, `NeedleMaximum = 160f` (L403307-403309) with `[Tooltip("Minimum/Maximum degrees rotation on local Y")]` on the field declarations. The lerp domain is `Setting / OutputMaximum`, NOT `_outputSetting / OutputMaximum`; since the `Setting` getter returns `_outputSetting`, it amounts to the same thing.

**Axis is local Y, not Z, and the base rotation MUST be reset first.** The vanilla body does TWO operations: (1) reset `_needleTransform.localRotation` to the cached `_needleBaseRotation` (the prefab's mounted orientation, captured during Awake), then (2) `Rotate(0f, _needleRotation, 0f, Space.Self)` to apply the lerp result around local Y. A mod that does `localRotation = Quaternion.Euler(0f, angle, 0f)` directly wipes the prefab base rotation and leaves the knob 90 degrees off the mounted plane; doing `Quaternion.Euler(0f, 0f, angle)` is wrong on both counts (Z axis AND missing base reset).

Threading: `SetKnob` is safe to call from a non-main thread; it self-marshals via `SetKnobFromThread` -> `UniTask.SwitchToMainThread()`.

Called from: `Setting` setter (L403348), `DeserializeSave` (L403570), and any modder-side write through reflection. A mod that hardcodes `Setting` to a different domain (e.g. `_outputSetting` is now "priority", and `OutputMaximum` is now the literal throughput) gets a needle pinned to `OutputMaximum`'s 1 / OutputMaximum fraction unless `SetKnob` is also patched to lerp against the new domain (e.g. `priority / NeedleFullScale`). The mod patch MUST mirror BOTH the base-rotation reset AND the Y axis, or follow vanilla's `Rotate(0, x, 0, Space.Self)` pattern -- the prior version of this page incorrectly showed the rotation as Z and skipped the base-reset, which led to a 90-degree-sideways knob bug in PowerGridPlus's first attempt at this patch.

Loading: `Thing.OnFinishedLoad` does NOT call `SetKnob` for Transformers, so the visual knob is stuck at the prefab default until the player interacts. A mod that overrides the rotation domain (e.g. PowerGridPlus's Priority) needs to explicitly call `SetKnob` from a `Thing.OnFinishedLoad` postfix to land the right rotation immediately on save load.

## CheckError and Error write path
<!-- verified: 0.2.6403.27689 @ 2026-07-07 -->

`Transformer.Error` is the inherited `Thing.Error` (`int`, 0 / 1; backed by the animator integer `Interactable.ErrorState` plus a cached `_error` field; property mechanics and the whole-game write census on [Device](./Device.md), "Error is animator display state"). On Transformer it is written exclusively by `CheckError()` (verbatim-unchanged at 0.2.6403.27689, decompile 424944-424957; 0.2.6228 ref L403646-403659):

```csharp
private void CheckError()
{
    if (GameManager.RunSimulation)
    {
        if (!IsOperable && Error == 0)
            OnServer.Interact(base.InteractError, 1, skipAnimation: true);
        else if (IsOperable && Error == 1)
            OnServer.Interact(base.InteractError, 0, skipAnimation: true);
    }
}
```

`CheckError` is invoked from three places:

- `OnAddCableNetwork` (424959-424963; 0.2.6228 L403661): when a cable network attaches.
- `OnRemoveCableNetwork` (424965-424969; 0.2.6228 L403667): when a cable network detaches.
- `CheckStateNextFrame()` (424971-424975; 0.2.6228 L403673), itself scheduled from `OnInteractableUpdated` (424977-424984; 0.2.6228 L403679-403686) when, under `GameManager.RunSimulation`, the on / off `Interactable` fires `State == 1` for `InteractableType.OnOff`.

`!IsOperable` (the trigger for `Error = 1`) means: `InputNetwork == OutputNetwork` (self-shorted; the `ElectricalInputOutput.IsOperable` rule at 0.2.6228 L373803-373813, not re-read at 0.2.6403) OR `base.IsOperable` is false (Device-level: `!IsStructureCompleted` or `IsBroken`). There is no per-tick `CheckError` call: a transient overload during steady-state operation is NOT mapped to `Error` by vanilla. A mod that wants Error-driven UX during steady-state operation (e.g. a deprioritization lockout) needs its own write path and must not rely on `CheckError` to fire.

Vanilla never writes `Error` to values other than 0 or 1. The field is `int` so higher codes survive the round-trip, but vanilla animator states for `Interactable.ErrorState` are only bound for 0 / 1; unmapped integer values silently produce no animation transition.

## Rocket-internal variant (same class, prefab-distinguished)
<!-- verified: 0.2.6228.27061 @ 2026-06-18 -->

`Transformer` implements `IRocketInternals, IRocketComponent` (class header, decompile L403300) and carries two `[SerializeField]` fields the rocket prefab variant sets (decompile L403327-L403331):

```csharp
[SerializeField] private RocketInternalCellType _rocketInternalCellType;
[SerializeField] private bool _strictlyInternal;
```

"Transformer Rocket (Small)" and the station "Transformer (Small)" are the SAME `Transformer` class; the rocket variant is a separate prefab with `_strictlyInternal = true`, a non-`None` `_rocketInternalCellType`, and its own serialized `OpenEnds`. No `RocketTransformer` subclass exists (consistent with "There is exactly one Transformer class" above). A Harmony patch on `typeof(Transformer)` catches both; code that wants to treat only the rocket variant differently must branch at runtime on `StrictlyInternal` / `InternalCellType` / the prefab name, not on the C# type.

A reported "extra data port" on the rocket transformer is a property of that prefab's `OpenEnds`. Confirmed by a live `Prefab.AllPrefabs` dump (ScenarioRunner `connector-dump`, game 0.2.6228.27061), measured per transformer prefab:

| Prefab | OpenEnds |
|---|---|
| `StructureTransformerSmall` | 2: `PowerAndData/Input`, `Power/Output` (data folded onto the input power connector) |
| `StructureTransformerSmallReversed` | 2: `Power/Output`, `PowerAndData/Input` |
| `StructureTransformerMedium` (+ `(Reversed)`) | 2: `Power/Input`, `PowerAndData/Output` (data on the output) |
| `StructureTransformer` (the large transformer) | 3: `Data/None`, `Power/Input`, `Power/Output` (a dedicated Data port) |
| `StructureRocketTransformerSmall` | 3: `Power/Input`, `Power/Output`, `Data/None` (a dedicated Data port) |

So the rocket small transformer carries a dedicated `Data` connector (3 connectors) where the station small transformer folds data onto its input power connector (2 connectors). The large station transformer has the same separate-Data-port shape as the rocket one; the small and medium are the only transformers whose data rides on an Input/Output power connector. See [Connection](./Connection.md) "Data-port discovery".

## Verification history

- 2026-07-13: fresh-validation pass at 0.2.6403.27689 (decompile-claim audit) re-read the five power surfaces verbatim at 424748-424805: `AllowSetPower` (input-side gate), `UsePower` (output-side `_powerProvided += powerUsed`, gated `Error != 1 && OnOff && cableNetwork == OutputNetwork`), `ReceivePower` (input-side `_powerProvided -= powerAdded`, gated on OnOff and a non-null `InputNetwork`), `GetUsedPower` (including the `Error == 1 -> OnOff ? UsedPower : 0f` branch the section's one-line formula omits), and `GetGeneratedPower`. All byte-identical to the bodies quoted on [AreaPowerControl](./AreaPowerControl.md). Restamped "Class hierarchy and fields" and added the `AllowSetPower` + base `Device.UsedPower` (370351, default 10f) paragraph, the two surfaces this page did not yet name. The ledger's tick-phase flow (query at `CalculateState` 271777 / 271782, consumer settle at `ConsumePower -> ReceivePower` 271832, producer settle at `PowerProvider.ApplyPower -> UsePower` 271690-271696 from the tail loop 271941-271945) stays documented on [PowerTick](./PowerTick.md) and [Device](./Device.md). No content contradicted.
- 2026-07-07: re-verified "CheckError and Error write path" against the 0.2.6403.27689 decompile and restamped it. `CheckError` (424944-424957) and its three event callers (`OnAddCableNetwork` 424959-424963, `OnRemoveCableNetwork` 424965-424969, `CheckStateNextFrame` 424971-424975 scheduled from `OnInteractableUpdated` 424977-424984) are verbatim-unchanged from 0.2.6228; added the `GameManager.RunSimulation` gate detail on `OnInteractableUpdated` and updated line refs (the `ElectricalInputOutput.IsOperable` sub-claim keeps its 0.2.6228 ref, not re-read). Occasion: the PowerGridPlus partial-power forensics floated the claim that "the whole-decompile census shows no vanilla Error writer for Transformer"; the re-read REJECTS that literal claim (CheckError exists and writes 0/1 via `OnServer.Interact(InteractError, ...)`) while confirming the section's standing operative claim: all writers are event-driven, there is no per-tick call, and steady-state overload never raises `Error`. Existing verified content confirmed, not changed, so no fresh validator was required; the whole-game write census lives on [Device](./Device.md).

- 2026-07-06: added the "`_powerProvided` is runtime-only" subsection under the save round-trip section (game version 0.2.6403.27689). Re-read `TransformerSaveData` (424593-424597, still only `OutputSetting`) and re-ran the whole-decompile `_powerProvided` census (Transformer sites: declaration 424621, `UsePower` 424761, `ReceivePower` 424769, `GetUsedPower` 424791; no serialization member anywhere). Additive: confirms and sharpens the existing "Only `Setting` is persisted at the Transformer level" claim, which was already consistent; no fresh validator needed. The parent section's other content (Setting setter, sync flags, 0.2.6228 line refs) was not re-read and keeps its stamp.
- 2026-07-02: re-verification pass against the 0.2.6403.27689 decompile after the game update from 0.2.6228.27061. The power-method bodies are unchanged; restamped "Class hierarchy and fields" with the new refs (class 424598, `_powerProvided` 424621, `UsePower` / `ReceivePower` 424757-424771, `GetUsedPower` 424773-424792, `GetGeneratedPower` = `Min(Setting, InputNetwork.PotentialLoad)` 424794-424805). The Setting-sync, InteractWith, SetKnob, CheckError, and rocket-variant sections were not re-read this pass and keep their 0.2.6228.27061 stamps (their old `L403xxx` refs are 0.2.6228 decompile lines).
- 2026-06-18: added "Rocket-internal variant" section (the `_rocketInternalCellType` / `_strictlyInternal` serialized fields at L403327-L403331; rocket and station transformers are one class differing by prefab). Connector layouts confirmed the same day by a live `Prefab.AllPrefabs` dump (ScenarioRunner `connector-dump`, 0.2.6228.27061): rocket small transformer = 3 connectors with a dedicated `Data` port, station small = 2 with `PowerAndData/Input`, large `StructureTransformer` = 3 with a dedicated `Data` port, medium = 2 with `PowerAndData/Output`. Additive; no conflict with existing content.
- 2026-06-03: corrected SetKnob needle math section. The prior decompile excerpt incorrectly showed `Needle.transform.localRotation = Quaternion.Euler(0f, 0f, _needleRotation)` (Z axis, no base reset). Re-verified against L403587-403597: vanilla actually does `_needleTransform.localRotation = _needleBaseRotation; _needleTransform.Rotate(0f, _needleRotation, 0f, Space.Self)` -- local Y axis with mandatory base-rotation reset first. Also added the explicit note that `Thing.OnFinishedLoad` doesn't call SetKnob, so a mod overriding the rotation domain (Priority instead of Setting) must call SetKnob from an OnFinishedLoad postfix to land the right rotation on save load. Triggered by a 90-degree-sideways knob bug in PowerGridPlus's first attempt at the Priority patch.

- 2026-05-12: page created. Sourced from a phase 3 research dive (planned mod "Power Grid Plus") into `.work/decomp/0.2.6228.27061/Assembly-CSharp.decompiled.cs` lines 373755-373766 and 403300-403545; verbatim excerpts of the `ElectricalInputOutput` `InputNetwork`/`OutputNetwork` fields, the `Transformer` class header + `Setting` clamp + logic getters + `GetGeneratedPower` head. Confirmed no `TransformerLarge`/`TransformerSmall` class exists. Re-Volt mod source (`TransformerExploitPatch` / `TransformerLogicPatch` targeting `Transformer`) corroborates the class name and the patch surface.
- 2026-06-02: added "Setting setter, sync flag, save round-trip", "InteractWith button model and DelayedActionInstance state messages", "SetKnob needle math", and "CheckError and Error write path" sections. Sourced from Agent 1 + Agent 2's PowerGridPlus transformer-priority research turn; decompile lines re-read directly. No conflict with existing verified content; additive only.

## Open questions

- Whether vanilla still ships a separate small-transformer *prefab* (same class, smaller `OutputMaximum`) and its prefab name -- affects whether a "step-down transformer" can reuse an existing prefab. Verify against the Stationpedia / `Prefab.AllPrefabs`.
- Exact `OutputMaximum` values on the shipped transformer prefab(s) (serialized data, not in the decompile).
