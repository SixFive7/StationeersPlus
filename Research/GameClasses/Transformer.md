---
title: Transformer
type: GameClasses
created_in: 0.2.6228.27061
verified_in: 0.2.6228.27061
verified_at: 2026-05-12
sources:
  - $(StationeersPath)\rocketstation_Data\Managed\Assembly-CSharp.dll :: Assets.Scripts.Objects.Electrical.Transformer, Assets.Scripts.Objects.Electrical.ElectricalInputOutput
  - .work/decomp/0.2.6228.27061/Assembly-CSharp.decompiled.cs :: lines 373755-373766 (ElectricalInputOutput fields), 403300-403545 (Transformer)
  - Plans/PowerGridPlus/PLAN.md (PGP-3 research); Mods/.../revolt-source/Assets/Scripts/Patches/TransformerExploitPatch.cs (Re-Volt patches this class)
related:
  - ./Cable.md
  - ./PowerTransmitter.md
  - ./PowerTick.md
tags: [power, logic]
---

# Transformer

Vanilla power transformer. `Assets.Scripts.Objects.Electrical.Transformer : ElectricalInputOutput, ISetable, ILogicable, ...`. Sits between two cable networks (input and output), draws up to `Setting` watts from the input network and provides up to that much to the output network. This is the only step-down / network-bridging device in vanilla aside from APCs.

## There is exactly one Transformer class
<!-- verified: 0.2.6228.27061 @ 2026-05-12 -->

`grep` for `class Transformer`, `: Transformer`, `TransformerLarge`, `TransformerSmall` in the decompile returns only the single `Transformer` class at line 403300 (the string `"Transformer"` elsewhere is an audio-clip key). Vanilla historically ships more than one transformer *prefab* (a smaller one), but they are the same `Transformer` class with a different serialized `OutputMaximum` and mesh -- there is no subclass hierarchy. A mod that wants a "heavy transformer" tier would register a new prefab of this same class (different `OutputMaximum`, different input/output `Connection` configuration, different mesh) via `AddPrefabs`; it does not need a new class.

## Class hierarchy and fields
<!-- verified: 0.2.6228.27061 @ 2026-05-12 -->

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

`GetGeneratedPower(cableNetwork)` returns 0 unless `cableNetwork == OutputNetwork && Error != 1 && OnOff && InputNetwork != null`; otherwise (vanilla) `Mathf.Min((float)Setting, ...)` drawn from the input network's available potential into the output network. (Re-Volt rewrites `GetGeneratedPower` / `GetUsedPower` / `ReceivePower` to clamp output to `min(Setting, InputNetwork.PotentialLoad - alreadyProvided)` and charge the transformer's own `UsedPower` quiescent draw to the upstream network.)

## Relevance to a "voltage tier" mod
<!-- verified: 0.2.6228.27061 @ 2026-05-12 -->

A transformer is already the only clean "bridge" between two cable networks (it forces `InputNetwork != OutputNetwork` via `IsOperable`). For a voltage-tier scheme where heavy cables are a separate tier reachable only through transformers, the design choices are: (1) gate which cable tier the transformer's `InputConnection` / `OutputConnection` may face (via a `CanConstruct` postfix, see [StructurePlacementValidation](../GameSystems/StructurePlacementValidation.md)); (2) add one new "heavy transformer" prefab of this class with a "heavy" input side and a "normal" output side; (3) treat the existing transformer prefab(s) as the step-down device. None of this requires a new class.

## Verification history

- 2026-05-12: page created. Sourced from a PGP-3 research dive (planned mod "Power Grid Plus") into `.work/decomp/0.2.6228.27061/Assembly-CSharp.decompiled.cs` lines 373755-373766 and 403300-403545; verbatim excerpts of the `ElectricalInputOutput` `InputNetwork`/`OutputNetwork` fields, the `Transformer` class header + `Setting` clamp + logic getters + `GetGeneratedPower` head. Confirmed no `TransformerLarge`/`TransformerSmall` class exists. Re-Volt mod source (`TransformerExploitPatch` / `TransformerLogicPatch` targeting `Transformer`) corroborates the class name and the patch surface.

## Open questions

- Whether vanilla still ships a separate small-transformer *prefab* (same class, smaller `OutputMaximum`) and its prefab name -- affects whether a "step-down transformer" can reuse an existing prefab. Verify against the Stationpedia / `Prefab.AllPrefabs`.
- Exact `OutputMaximum` values on the shipped transformer prefab(s) (serialized data, not in the decompile).
