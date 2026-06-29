---
title: ElectricalInputOutput
type: GameClasses
created_in: 0.2.6228.27061
verified_in: 0.2.6228.27061
verified_at: 2026-06-29
sources:
  - $(StationeersPath)\rocketstation_Data\Managed\Assembly-CSharp.dll :: Assets.Scripts.Objects.Electrical.ElectricalInputOutput
  - .work/decomp/0.2.6228.27061/Assembly-CSharp.decompiled.cs :: lines 373755-373933 (ElectricalInputOutput class, fields, IsPowerProvider/IsPowerInputOutput, IsOperable, AvailablePower/CurrentLoad/PotentialLoad, CheckConnections, CheckPower, IsProviderToDevice, OnAddCableNetwork), 349623 (Device.MaxProviderRecursionIterations), 350691 (Device.IsProviderToDevice base)
related:
  - ./Device.md
  - ./Transformer.md
  - ./Battery.md
  - ./AreaPowerControl.md
  - ./WirelessPower.md
  - ./PowerTick.md
tags: [power, network]
---

# ElectricalInputOutput

Vanilla two-port power-device base. `Assets.Scripts.Objects.Electrical.ElectricalInputOutput : Device, ISmartRotatable, ISubmergeable, IPowered, IDensePoolable, IReferencable, IEvaluable` (decompile line 373755). The shared base of every device that bridges two distinct cable networks: [Transformer](./Transformer.md), [Battery](./Battery.md), [AreaPowerControl](./AreaPowerControl.md), and the wireless dish family ([WirelessPower](./WirelessPower.md) -> [PowerTransmitter](./PowerTransmitter.md) / [PowerReceiver](./PowerReceiver.md)). A plain [Device](./Device.md) has one `PowerCable`; an `ElectricalInputOutput` has two named ports, `InputNetwork` and `OutputNetwork`, and is both a power provider and a power input/output node in the [PowerTick](./PowerTick.md) model.

This page is the canonical reference for the bridge base. Per-subclass power formulas live on each subclass page (see the cross-links); this page documents only what `ElectricalInputOutput` itself declares.

## Class header and fields
<!-- verified: 0.2.6228.27061 @ 2026-06-29 -->

```csharp
public class ElectricalInputOutput : Device, ISmartRotatable, ISubmergeable, IPowered, IDensePoolable, IReferencable, IEvaluable
{
    [Header("Electrical I/O")]
    public Connection InputConnection;
    public Connection OutputConnection;

    [ReadOnly] public CableNetwork InputNetwork;
    [ReadOnly] public CableNetwork OutputNetwork;

    [Header("ISmartRotation")]
    public SmartRotate.ConnectionType ConnectionType = SmartRotate.ConnectionType.Exhaustive;
    public int[] OpenEndsPermutation = new int[6] { 0, 1, 2, 3, 4, 5 };

    private bool _isSubmerged;
    private const int SUBMERGED_TICKS_BEFORE_BREAK = 60;
    private const float SUBMERGED_BREAK_CHANCE = 0.5f;
    private uint _inputSubmerged;
    private uint _outputSubmerged;

    public override bool IsPowerProvider    => true;     // line 373783
    public override bool IsPowerInputOutput => true;     // line 373801
}
```

`InputConnection` / `OutputConnection` are the two serialized `Connection` open ends (prefab data, role-tagged `ConnectionRole.Input` / `Output`). `InputNetwork` / `OutputNetwork` are the resolved `CableNetwork` references each connection currently faces; they are `[ReadOnly]` (Inspector decorator only) and rebuilt by `CheckConnections`.

Both `IsPowerProvider` and `IsPowerInputOutput` are `true` here (the base `Device` returns `false` for both, decompile lines 349647 / 349687). These two flags drive [PowerTick.CalculateState](./PowerTick.md): a device with `IsPowerInputOutput == true` whose `GetGeneratedPower > 0` is added to the network's `InputOutputDevices[]` array (used by the recursion-cycle check below), and `IsPowerProvider == true` is one of the conditions under which `ApplyState` powers the device even when the network is in brownout (`_isPowerMet == false`).

## CheckConnections: how InputNetwork / OutputNetwork resolve
<!-- verified: 0.2.6228.27061 @ 2026-06-29 -->

```csharp
protected override void CheckConnections()                                      // line 373872
{
    Cable cable = InputConnection.GetCable();
    Cable cable2 = OutputConnection.GetCable();
    InputNetwork = (cable ? cable.CableNetwork : null);
    OutputNetwork = (cable2 ? cable2.CableNetwork : null);
}
```

Each network is whatever `CableNetwork` the adjacent cable on that connection currently belongs to, or null if no cable faces that open end. `CheckConnections` is called from `InitializeDevice` (line 373847) and from `OnAddCableNetwork` (line 373928, which also calls `CheckPower`). So the input/output network pointers refresh on device init and whenever a cable network attaches. The wireless subclasses override `CheckConnections` (the receiver resolves only its `OutputConnection` cable, the transmitter only its input side; the other side is the `WirelessNetwork`, see [PowerReceiver](./PowerReceiver.md) / [PowerTransmitter](./PowerTransmitter.md)).

## IsOperable: a bridge must join two DISTINCT networks
<!-- verified: 0.2.6228.27061 @ 2026-06-29 -->

```csharp
protected override bool IsOperable                                              // line 373803
{
    get
    {
        if (OutputNetwork != null && InputNetwork == OutputNetwork)
        {
            return false;        // input and output shorted to the same network
        }
        return base.IsOperable;  // Device: IsStructureCompleted && !IsBroken
    }
}
```

`IsOperable` is `protected` (not a public predicate). It means "structurally complete, not broken, and not self-shorted." It does NOT consult `OnOff` or `Powered`. It is the trigger for the `Error` flag on subclasses that call `CheckError` (e.g. [Transformer.CheckError](./Transformer.md) writes `Error = 1` when `!IsOperable`). The self-short rule (`InputNetwork == OutputNetwork`) is what makes a transformer/APC error out when both its ports are wired into the same cable run: a bridge that does not actually bridge is non-operable. Subclasses generally gate their power methods on `IsOperable` in addition to `OnOff` / `Error` (Battery does: `... && IsOperable` in all four power methods, see [Battery](./Battery.md)).

Naming-collision warning: a parallel `DraggableThing` family declares `IsOperable` as a public METHOD `bool IsOperable()` (decompile lines 277715 / 279310 / 289260) returning `true` by default. That method is unrelated to this property and is not on the device path. See [Device](./Device.md), "IsOperable", for the full collision note.

## Load accessors: the bridge re-exposes its networks' loads
<!-- verified: 0.2.6228.27061 @ 2026-06-29 -->

`ElectricalInputOutput` exposes three convenience load getters that simply forward to the underlying `CableNetwork` load fields (the per-tick display mirrors written by [CableNetwork.OnPowerTick](./CableNetwork.md)):

```csharp
public virtual float AvailablePower => PotentialLoad;                            // line 373815

public float CurrentLoad                                                         // line 373817
{
    get
    {
        if (OutputNetwork == null) return 0f;
        return OutputNetwork.CurrentLoad;
    }
}

public float PotentialLoad                                                       // line 373829
{
    get
    {
        if (InputNetwork == null) return 0f;
        return InputNetwork.PotentialLoad;
    }
}
```

- **`PotentialLoad`** reads the INPUT network's `PotentialLoad` (the total generation standing on the upstream network). This is the number a transformer's `GetGeneratedPower` and a transmitter's `GetGeneratedPower` clamp their output against (`Min(Setting, InputNetwork.PotentialLoad)` / `Min(MaxPowerTransmission, InputNetwork.PotentialLoad) - loss`). So a bridge can never put more onto its output network than its input network currently offers as potential.
- **`CurrentLoad`** reads the OUTPUT network's `CurrentLoad` (the power actually consumed downstream last tick).
- **`AvailablePower`** is just `PotentialLoad` (the input-side potential), `virtual` so subclasses can refine it.

Because `CableNetwork.PotentialLoad` is a once-per-tick display mirror (the live value is `PowerTick.Potential`; see [CableNetwork](./CableNetwork.md) / [PowerTick](./PowerTick.md)), `ElectricalInputOutput.PotentialLoad` is reading LAST tick's settled potential when consulted during the current tick's `CalculateState`. This is one source of the cross-network one-tick lag documented on [ElectricityManager](./ElectricityManager.md) and [Device](./Device.md).

## IsProviderToDevice: bounded recursive cycle detection
<!-- verified: 0.2.6228.27061 @ 2026-06-29 -->

The base [Device.IsProviderToDevice](./Device.md) returns `false` (line 350691). `ElectricalInputOutput` overrides it with a recursive walk that detects whether `this` bridge is (transitively) a power provider to a given target device, used by [PowerTick.CheckForRecursiveProviders](./PowerTick.md) to force-burn a fuse/cable when the grid loops back on itself through a chain of bridges:

```csharp
public override bool IsProviderToDevice(Device device, ref List<long> evaluatedDeviceReferences)   // line 373895
{
    if (device == null || InputNetwork?.PowerTick?.InputOutputDevices == null)
        return false;
    if (evaluatedDeviceReferences.Contains(base.ReferenceId))
        return false;
    evaluatedDeviceReferences.Add(base.ReferenceId);
    for (int num = InputNetwork.PowerTick.InputOutputDevices.Length - 1; num >= 0; num--)
    {
        if (InputNetwork.PowerTick.InputOutputDevices[num].Device == device)
            return true;
    }
    for (int num2 = InputNetwork.PowerTick.InputOutputDevices.Length - 1; num2 >= 0; num2--)
    {
        PowerProvider powerProvider = InputNetwork.PowerTick.InputOutputDevices[num2];
        if (evaluatedDeviceReferences.Count >= Device.MaxProviderRecursionIterations)
            return false;
        if (powerProvider.Device.IsProviderToDevice(device, ref evaluatedDeviceReferences))
            return true;
    }
    return false;
}
```

Key facts:

- The walk follows the INPUT side: it asks "is `device` among the input/output providers feeding MY input network, or feeding any of THEIR input networks, recursively?" A `true` result means a cycle exists where this bridge's output ultimately feeds back into its own input chain through `device`.
- It is bounded by `Device.MaxProviderRecursionIterations = 512` (decompile line 349623). When `evaluatedDeviceReferences.Count` reaches 512 the walk gives up and returns `false` (treats it as "no cycle found"), so a pathologically large bridged grid will not stack-overflow but may miss a very deep cycle.
- `evaluatedDeviceReferences` is the visited set (by `ReferenceId`); a device already visited short-circuits to `false`. This is the only guard against infinite recursion besides the 512 cap.
- The cycle, when found, drives the burn in [PowerTick.CheckForRecursiveProviders](./PowerTick.md) (it adds the offending device's first fuse, or its `PowerCable`, to `BreakableFuses`/`BreakableCables`). This is the "you wired your transformers/batteries into a loop" protection: vanilla burns one link to break the loop rather than letting power circulate.

## Submergeable behaviour
<!-- verified: 0.2.6228.27061 @ 2026-06-29 -->

`ElectricalInputOutput` implements `ISubmergeable`. `IsSubmerged` (line 373785) sets `NetworkUpdateFlags |= 512` on change (synced to clients). `CanShortOut => OnOff` (line 373841): a powered-on submerged device sparks. `HandleUnderWaterFX` (line 373859) emits sparks + an electrical-failure sound at 25% chance per `Update100MS` when `CanShortOut && IsSubmerged`. The constants `SUBMERGED_TICKS_BEFORE_BREAK = 60` and `SUBMERGED_BREAK_CHANCE = 0.5f` gate the eventual break of a submerged device (the per-tick submerge counters `_inputSubmerged` / `_outputSubmerged` drive the break path in `OnSubmergeableTick`, which the subclass opts into via `DoSubmergableTick`, default `false` on the base, line 373845). Cosmetic-plus-eventual-break; not part of the steady-state power arithmetic.

## Verification history

- 2026-06-29: page created. Consolidates the `ElectricalInputOutput` bridge base, previously documented piecemeal on [Transformer](./Transformer.md) (fields + IsOperable), [Battery](./Battery.md) (class hierarchy), and [Device](./Device.md) (IsOperable collision). Sourced verbatim from `.work/decomp/0.2.6228.27061/Assembly-CSharp.decompiled.cs` lines 373755-373933: class header + fields, `IsPowerProvider => true` (373783), `IsPowerInputOutput => true` (373801), `IsOperable` self-short rule (373803-373813), `AvailablePower` / `CurrentLoad` / `PotentialLoad` load accessors (373815-373839), `CheckConnections` (373872-373878), `CheckPower` (373887-373893), `IsProviderToDevice` recursive cycle walk bounded by `MaxProviderRecursionIterations` (373895-373926, cap value 349623), `OnAddCableNetwork` (373928), and the submergeable block (373773-373870). Additive (new page); no existing verified content contradicted -- the IsOperable and field facts match what Transformer.md / Battery.md / Device.md already state, this page is the canonical home and they cross-link to it.

## Open questions

None at creation.
