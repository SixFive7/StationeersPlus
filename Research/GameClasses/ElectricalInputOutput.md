---
title: ElectricalInputOutput
type: GameClasses
created_in: 0.2.6228.27061
verified_in: 0.2.6403.27689
verified_at: 2026-07-14
sources:
  - $(StationeersPath)\rocketstation_Data\Managed\Assembly-CSharp.dll :: Assets.Scripts.Objects.Electrical.ElectricalInputOutput
  - .work/decomp/0.2.6228.27061/Assembly-CSharp.decompiled.cs :: lines 373755-373933 (ElectricalInputOutput class, fields, IsPowerProvider/IsPowerInputOutput, IsOperable, AvailablePower/CurrentLoad/PotentialLoad, CheckConnections, CheckPower, IsProviderToDevice, OnAddCableNetwork), 349623 (Device.MaxProviderRecursionIterations), 350691 (Device.IsProviderToDevice base)
  - .work/decomp/0.2.6403.27689/Assembly-CSharp.decompiled.cs :: lines 394930-395006 (CheckConnections, CheckPower, IsProviderToDevice, OnAddCableNetwork, OnRemoveCableNetwork), 390636-390998 (AreaPowerControl NoPower / CheckPower / AllowSetPower), 391963-391969 (Battery.CheckPower)
  - .work/decomp/0.2.6403.27689/Assembly-CSharp.decompiled.cs :: lines 395008-395023 (GetPassiveTooltip), 371547-371557 (Device.GetPassiveTooltip), 314440-314465 (Structure.GetPassiveTooltip), 319731-319739 (Thing.GetPassiveTooltip / GetPassiveUITooltip), 390800-390826 (AreaPowerControl.GetPassiveTooltip / GetContextualName), 424598-424993 (Transformer negative census), 307029-307155 (PassiveUITooltip + PassiveTooltip structs), 253966-254375 (Tooltip UI class), 287864-287869 + 285975 (InventoryManager.NormalModeThing + TooltipRef), 239691-239721 (InputMouse route)
  - .work/decomp/0.2.6403.27689/Assembly-CSharp.decompiled.cs :: lines 44018/44086/44166/44282 (KeyMap.MouseControl = LeftAlt), 201299-201391 (MouseModeController), 239369 + 239462-239484 + 239647-239677 (InputMouse class / SetMouseControl / Update gate), 239679-239759 (InputMouse.Idle), 287864-287987 (NormalModeThing display gate), 254408-254433 (Tooltip.SetValuesForInteractable), 307109-307129 (PassiveTooltip DelayedActionInstance constructor), 250399-250428 + 250451 + 250633 + 250640 (#008AE6 census)
  - .work/decomp/0.2.6403.27689/Assembly-CSharp.decompiled.cs :: lines 254298-254320 (SetUpToolTip) + 254377-254380 (ColorToHex) + 254081-254100 (Action property) + 253998 (Color field) + 254025-254029 (row renderer fields) + 254345-254362 + 254389 (per-row renderer gates / all-empty hide), 287898-287958 (NormalModeThing action sub-branches), 254102-254121 (Title property) + whole-assembly negative census for TooltipTitle references and ContentSizeFitter
related:
  - ./Device.md
  - ./Transformer.md
  - ./Battery.md
  - ./AreaPowerControl.md
  - ./WirelessPower.md
  - ./PowerTick.md
  - ./Thing.md
  - ./UniversalPage.md
  - ../GameSystems/KeyBinding.md
  - ../Patterns/HarmonyBaseCallDetourMultiFire.md
tags: [power, network, ui]
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
<!-- verified: 0.2.6403.27689 @ 2026-07-02 -->

```csharp
protected override void CheckConnections()                                      // 0.2.6403.27689 line 394930
{
    Cable cable = InputConnection.GetCable();
    Cable cable2 = OutputConnection.GetCable();
    InputNetwork = (cable ? cable.CableNetwork : null);
    OutputNetwork = (cable2 ? cable2.CableNetwork : null);
}
```

Each network is whatever `CableNetwork` the adjacent cable on that connection currently belongs to, or null if no cable faces that open end. `CheckConnections` is called from `InitializeDevice` and from `OnAddCableNetwork` / `OnRemoveCableNetwork` (see the CheckPower section below). So the input/output network pointers refresh on device init and whenever a cable network attaches or detaches. NEW at 0.2.6403.27689: `OnRemoveCableNetwork(oldNetwork)` (394993-395006) first nulls `InputNetwork` / `OutputNetwork` when they match the departing network, before re-running `CheckConnections` (see [PowerReceiver](./PowerReceiver.md), "Unlink behavior", for the consequence on the wireless side). The wireless subclasses override `CheckConnections` (the receiver resolves only its `OutputConnection` cable, the transmitter only its input side; the other side is the `WirelessNetwork`, see [PowerReceiver](./PowerReceiver.md) / [PowerTransmitter](./PowerTransmitter.md)).

## CheckPower: event-driven un-power outside the tick
<!-- verified: 0.2.6403.27689 @ 2026-07-02 -->

[PowerTick.ApplyState](./PowerTick.md) un-powers devices once per tick, but a bridge can also lose `Powered` immediately, between ticks, through the `CheckPower` family. The base is declared here (0.2.6403.27689 lines 394945-394951):

```csharp
public virtual void CheckPower()
{
    if (GameManager.RunSimulation && InputNetwork == null && Powered)
    {
        OnServer.Interact(base.InteractPowered, 0);
    }
}
```

A powered bridge whose INPUT side has no network (input cable cut, input network dissolved) is flipped off on the spot, host-side. Call sites on the bridge base (both run `CheckConnections` first, so `InputNetwork` is fresh):

```csharp
public override void OnAddCableNetwork(CableNetwork newNetwork)      // 394985-394991
{
    base.OnAddCableNetwork(newNetwork);
    CheckConnections();
    CheckPower();
}

public override void OnRemoveCableNetwork(CableNetwork oldNetwork)   // 394993-395006
{
    base.OnRemoveCableNetwork(oldNetwork);
    if (oldNetwork == InputNetwork)  { InputNetwork = null; }
    if (oldNetwork == OutputNetwork) { OutputNetwork = null; }
    CheckConnections();
    CheckPower();
}
```

Overrides at 0.2.6403.27689:

- `AreaPowerControl.CheckPower` (390983-390989) un-powers on `NoPower && Powered`, where `NoPower` (390636-390650) is "no cell or empty cell, AND (no input network OR `InputNetwork.PotentialLoad <= 0`)": the APC counts its battery as a power source, so pulling the input cable does not un-power an APC with a charged cell. The APC also calls `CheckPower()` from its `OnOff` interaction handler and from `OnChildEnterInventory` / `OnChildExitInventory` when the battery cell is inserted or removed (call sites in the 390880-390960 region).
- `Battery.CheckPower` (391963-391969) is a re-sync rather than an un-power: `if (RunSimulation && (InteractPowered.State == 1) != Powered) OnServer.Interact(InteractPowered, Powered ? 1 : 0)`, called from the station battery's tick when `HasPowerState`.

Disambiguation: an unrelated `CheckPower` family exists on the handheld `PowerTool` side (virtual around line 353114 at 0.2.6403.27689, overridden by `SensorLenses` at 354109); that one checks the tool's battery slot and has nothing to do with cable networks.

Mod consequence: a patch that manages `Powered` semantics on bridges must account for BOTH write paths: the per-tick `ApplyState` else-branch (gated by `AllowSetPower`, see [PowerTick](./PowerTick.md)) and these event-driven `CheckPower` calls that fire on wiring changes, OnOff toggles, and cell insert/remove without waiting for a tick.

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

## GetPassiveTooltip: body-hover tooltip resolution chain
<!-- verified: 0.2.6403.27689 @ 2026-07-14 -->

`Thing.GetPassiveTooltip(Collider hitCollider)` is the virtual hover-tooltip hook the HUD polls while the cursor rests on a thing. Two UI call sites route into it: `InputMouse` (`Assets.Scripts.UI`, class at 239369) calls `passiveTooltip = CursorThing.GetPassiveTooltip(hitInfo.collider);` (239691) and hands the result to `InventoryManager.Instance.TooltipRef.HandleToolTipDisplay(passiveTooltip);` (239721), and `InventoryManager.NormalModeThing` (class 285881, method 287864) evaluates both the null-collider and target-collider forms each poll:

```csharp
PassiveTooltip cursorPassiveTooltip = ((CursorManager.CursorThing != null) ? CursorManager.CursorThing.GetPassiveTooltip(null) : default(PassiveTooltip));                            // line 287868
PassiveTooltip cursorPassiveTooltip2 = ((CursorManager.CursorThing != null) ? CursorManager.CursorThing.GetPassiveTooltip(cursorTargetCollider) : default(PassiveTooltip));           // line 287869
```

The bridge base declares the override that actually runs for `Transformer` and `Battery` body hovers, because neither subclass declares its own (see the census below). Full body (395008-395023):

```csharp
public override PassiveTooltip GetPassiveTooltip(Collider hitCollider)               // line 395008
{
    if (InputConnection.ConnectionType != NetworkType.None && hitCollider == InputConnection.Collider)
    {
        PassiveTooltip result = new PassiveTooltip(true);
        result.Title = InterfaceStrings.ConnectionInput;
        return result;
    }
    if (OutputConnection.ConnectionType != NetworkType.None && hitCollider == OutputConnection.Collider)
    {
        PassiveTooltip result = new PassiveTooltip(true);
        result.Title = InterfaceStrings.ConnectionOutput;
        return result;
    }
    return base.GetPassiveTooltip(hitCollider);
}
```

It answers ONLY the two port-collider cases (the "Input" / "Output" labels). Any other collider, including the device body, falls through the base chain, which at 0.2.6403.27689 runs in this dispatch order:

**1. `Device.GetPassiveTooltip` (371547-371557)**: answers when the hit collider is one of the device's `OpenEnds` connection colliders (delegates to `Connection.Populate`), else calls base:

```csharp
public override PassiveTooltip GetPassiveTooltip(Collider hitCollider)               // line 371547
{
    foreach (Connection openEnd in OpenEnds)
    {
        if (!(hitCollider != openEnd.Collider) && !(hitCollider == null))
        {
            return new PassiveTooltip(true).Populate(openEnd);
        }
    }
    return base.GetPassiveTooltip(hitCollider);
}
```

**2. `SmallGrid` (312025-313014) declares no override**, so Device's base call lands in `Structure.GetPassiveTooltip` (314440), the damage / build-state tooltip. Its head (314440-314452):

```csharp
public override PassiveTooltip GetPassiveTooltip(Collider hitCollider)               // line 314440
{
    bool flag = ShowBuildTooltip();
    bool flag2 = ShowDeconstructTooltip();
    bool flag3 = ShowRepairTooltip();
    if (DamageState.Total <= 0f && !flag && !flag2 && !flag3)
    {
        return base.GetPassiveTooltip(hitCollider);
    }
    PassiveTooltip passiveTooltip = new PassiveTooltip(true);
    passiveTooltip.Title = DisplayName;
    passiveTooltip.Extended = GetExtendedText().ToString();
    ...
```

The remainder (314453-314465+) fills `ConstructString` from `NextBuildState.Tool.GetToolsAsString()` and `DeconstructString` from `BrokenBuildStates[index]` when `IsBroken`, else from `CurrentBuildState.Tool.GetExitToolAsString()`. An undamaged, fully built structure with no build/deconstruct/repair tooltip to show falls through again.

**3. `Thing.GetPassiveTooltip` (319731-319734)** ends the chain with the all-empty struct:

```csharp
public virtual PassiveTooltip GetPassiveTooltip(Collider hitCollider)                // line 319731
{
    return new PassiveTooltip(true);
}
```

(`new PassiveTooltip(true)` initializes every string field to `string.Empty`; see the struct section below. The adjacent `Thing.GetPassiveUITooltip` at 319736-319739 returns the separate `PassiveUITooltip` readonly struct, 307029, and is not part of this chain.)

Net effect: hovering the BODY of a healthy, fully built `Transformer` or `Battery` produces an empty tooltip; vanilla shows nothing there. `AreaPowerControl` is the one bridge subclass that overrides for the body case, and BOTH of its paths call `base.GetPassiveTooltip` (390800-390810):

```csharp
public override PassiveTooltip GetPassiveTooltip(Collider hitCollider)               // line 390800
{
    if (hitCollider != null)
    {
        return base.GetPassiveTooltip(hitCollider);
    }
    PassiveTooltip passiveTooltip = base.GetPassiveTooltip(hitCollider);
    passiveTooltip.Title = DisplayName;
    passiveTooltip.Extended = GetExtendedText().ToString();
    return passiveTooltip;
}
```

A real collider hit defers entirely to the chain; the APC's charge readout (`GetExtendedText`, 390796 region) ships only through the null-collider poll (`InventoryManager.NormalModeThing` line 287868).

### Override census: which electrical classes declare GetPassiveTooltip
<!-- verified: 0.2.6403.27689 @ 2026-07-14 -->

Method-name census over the class spans in the 0.2.6403.27689 decompile ("none" = no `GetPassiveTooltip` occurrence inside the class span, so the class inherits the nearest base override; class-declaration line in parentheses):

| Class | Declares override | Body-hover entry point |
|---|---|---|
| `Electrical` (394786) | none | `Device` (371547) |
| `ElectricalInputOutput` (394813) | 395008 | own |
| `AreaPowerControl` (390555) | 390800 | own (both paths call base) |
| `Battery` (391662) | none | `ElectricalInputOutput` (395008) |
| `Transformer` (424598, span 424598-424993) | none | `ElectricalInputOutput` (395008) |
| `WirelessPower` (426779) | none | `ElectricalInputOutput` (395008) |
| `PowerTransmitter` (408269) | none | `ElectricalInputOutput` (395008) |
| `PowerReceiver` (408065) | none | `ElectricalInputOutput` (395008) |
| `BatteryCellCharger` (392218) | none | `Device` (371547) |
| `PowerTransmitterOmni` (408582) | none | `Device` (371547) |
| `Gyroscope` (397102) | none | `Device` (371547) |
| `PowerConnection` (407942) | none | `Device` (371547) |
| `PowerConnector` (408002) | 408050 | own (tail base call at 408058) |
| `SolarPanel` (421087) | 421384 | own (base call at 421386) |
| `RadioscopicThermalGenerator` (416897) | none | `Device` (371547) |
| `LandingPadDeprecated` (398068) | none | `Device` (371547) |
| `LargeElectrical` (207539, abstract) | none | `Device` (371547) |
| `SatelliteDish` (417919) | none | `Device` (371547) |
| `GroundTelescope` (207752) | 208070 | own |

`Transformer` also declares no `GetContextualName` override (no occurrence in 424598-424993; the virtual is `Thing.GetContextualName` at 319699). `AreaPowerControl` does override `GetContextualName` (390817-390826) for its three area buttons. Power producers outside this namespace branch that declare their own `GetPassiveTooltip`: `PowerGeneratorPipe` (396655) and `StirlingEngine` (424241); `Cable` declares one as well (392747).

Mod consequence: a Harmony postfix that annotates the BODY hover of a `Transformer` or `Battery` must attach to `ElectricalInputOutput.GetPassiveTooltip`, the override that actually runs. Attaching at more than one level of this chain makes the postfix fire once per patched level per poll, because the derived overrides call `base.GetPassiveTooltip` and Harmony detours base calls too; see [HarmonyBaseCallDetourMultiFire](../Patterns/HarmonyBaseCallDetourMultiFire.md) for the trap and the shipped depth-guard mitigation.

## Display routes: the ALT mouse-control gate vs the crosshair poll
<!-- verified: 0.2.6403.27689 @ 2026-07-14 -->

The two call sites named in the section above sit on two different input routes, and they gate a returned `PassiveTooltip` differently. Which route polls is decided by mouse-control mode, whose default key is ALT.

**The ALT identity.** The default key map binds `KeyMap.MouseControl` to left ALT (both the legacy field and the rebindable key-list entry):

```csharp
KeyMap.MouseControl = KeyCode.LeftAlt;                    // line 44018
KeyMap._MouseControl.AssignKey(KeyCode.LeftAlt);          // line 44086
```

It is registered as a rebindable binding (`AddKey("MouseControl", KeyMap.MouseControl, controlsGroup4)`, 44166) and re-read from saved bindings (`KeyMap.MouseControl = GetKey("MouseControl")`, 44282); see [KeyBinding](../GameSystems/KeyBinding.md) for the key-map system. The static `MouseModeController` (201299) defines `public static bool AltKeyDown => KeyManager.GetButton(KeyMap.MouseControl);` (201313). Its `Check()` (201335) unlocks the cursor while the key is held:

```csharp
if (AltKeyDown)
{
    SetState(locked: false);                              // lines 201347-201351
    return;
}
```

and `SetState` (201363) forwards every lock transition to the mouse layer: `InputMouse.SetMouseControl(!locked);` (201375). `InputMouse.SetMouseControl(true)` (239462-239484) sets the public static flag `IsMouseControl = true` (239466). Open ImGui windows with `MouseControlMode` reach the same flag through `CheckInputState` (111158-111175), so "mouse-control mode" is slightly broader than "ALT held", but ALT is the standard in-game trigger.

**Route 1 (ALT): `InputMouse.Idle` displays the struct unconditionally.** `InputMouse : UserInterfaceBase` (class 239369) runs its world-mouse state machine only in mouse-control mode; `Update` (239647) returns early otherwise:

```csharp
private void Update()                                     // line 239647
{
    if (GameManager.IsBatchMode || !IsMouseControl || InputWindowBase.IsInputWindow || Stationpedia.IsOpenAndLocked || CursorManager.Instance.BlockCursorRaycast || WorldManager.IsGamePaused)
    {
        return;
    }
    // ... slot-display and drag handling ...
    switch (WorldMode)
    {
    case WorldMouseMode.Idle:
        Idle();                                           // line 239665
        break;
    // ... Click / Drag / DragSlot ...
```

`Idle()` (239679-239759) raycasts under the free cursor and hands whatever `GetPassiveTooltip` returned straight to the HUD:

```csharp
private void Idle()                                       // line 239679
{
    PassiveTooltip passiveTooltip = default(PassiveTooltip);
    DraggedThing = null;
    if (Physics.Raycast(CameraController.CurrentCamera.ScreenPointToRay(Input.mousePosition), out var hitInfo, MaxInteractDistance, CursorManager.Instance.CursorHitMask))
    {
        CursorTransform = hitInfo.transform;
        CursorThing = Thing.Find(hitInfo.collider);
        CursorItem = CursorThing as Assets.Scripts.Objects.Item;
        Interactable interactable = null;
        if (CursorThing != null && !IsMouseOverUi)
        {
            passiveTooltip = CursorThing.GetPassiveTooltip(hitInfo.collider);     // line 239691
            interactable = CursorThing.GetInteractable(hitInfo.collider);         // line 239692
            // ... selection color / cursor handling, 239693-239715 ...
            if (interactable != null)
            {
                Tooltip.SetValuesForInteractable(ref passiveTooltip, CursorThing, interactable);   // line 239718
            }
            passiveTooltip.FollowMouseMovement = true;
            InventoryManager.Instance.TooltipRef.HandleToolTipDisplay(passiveTooltip);             // line 239721
        }
```

No field of the struct is inspected before display on this route: an empty `Title` with a non-empty `Extended` still reaches `HandleToolTipDisplay` (239721), whose `_hasExtended` gate (see the struct section below) then makes the panel visible. The one substitution: when the hit collider maps to an `Interactable` (`GetInteractable`, a strict dictionary lookup, see [Thing](./Thing.md)), `SetValuesForInteractable` (239716-239719) REPLACES the struct with the interactable's action preview first (see the SetValuesForInteractable subsection below).

**Route 2 (no ALT): `InventoryManager.NormalModeThing` is Title-gated for body hovers.** The crosshair poll is `NormalModeThing` (287864). It evaluates both tooltip forms up front (null-collider 287868, target-collider 287869; quoted in the section above), dry-runs `AttackWith` / `InteractWith` / `DragInto` for the action previews (287871-287895), then displays through a three-branch gate:

1. **Action branch** (287898): `if ((item != null && !item.IsChild) || interactable != null || delayedActionInstance != null || delayedActionInstance3 != null)`. Anything actionable under the crosshair (pickup-able item, interactable, attackable target, draggable Human) shows an action tooltip built from the preview `DelayedActionInstance` plus `SetValuesForInteractable`, not the raw `GetPassiveTooltip` struct (sub-branches 287917-287958). One sub-case does use the passive struct: a plain item with no interactable or attack action displays the null-collider form after `SetColorForItemAction` (287954-287957), without a Title check.
2. **Collider branch** (287963): displays the target-collider form only when its `Title` is non-empty.
3. **Body branch** (287968-287981): displays the null-collider form only when ITS `Title` is non-empty (plus not-self checks), and sets `TooltipRef.Mode = TooltipMode.ActionLast` first.

```csharp
else if (!cursorPassiveTooltip2.Title.Equals(string.Empty))           // line 287963
{
    TooltipRef.HandleToolTipDisplay(cursorPassiveTooltip2);
    CursorManager.ClearLastSelectionId();
}
else
{
    CursorManager.SetSelectionVisibility(isVisible: false);
    CursorManager.ClearLastSelectionId();
    if (cursorPassiveTooltip.Title != string.Empty && CursorManager.CursorThing.RootParent != Parent && !IsLookingAtParent())   // line 287972
    {
        TooltipRef.Mode = TooltipMode.ActionLast;                     // line 287974
        TooltipRef.HandleToolTipDisplay(cursorPassiveTooltip);
        // ...
    }
}
```

Branch 2 is the branch that renders this class's port labels on the plain crosshair: the port override (395008) fills `Title` with `InterfaceStrings.ConnectionInput` / `ConnectionOutput`, and connection colliders are not interactable colliders, so branch 1 does not intercept. Branch 3 is how the APC body readout displays without ALT: `AreaPowerControl.GetPassiveTooltip` fills `Title` / `Extended` only for the null-collider form (390800-390810, section above), so branch 2's collider form stays empty and branch 3 fires in `ActionLast` mode.

Gate asymmetry: the Title-empty gates exist only on the non-actionable else-chain. Inside branch 1 every sub-branch hands its struct to `HandleToolTipDisplay` without inspecting a single field first: drag (287917-287923, display 287922), interactable (287924-287946, display 287929), attack (287947-287953, display 287952), plain item (287954-287958, display 287957). In this method `delayedActionInstance` is the `AttackWith` preview (287871-287874), `delayedActionInstance2` the `InteractWith` preview (287886), `delayedActionInstance3` the Human `DragInto` preview (287892-287894). The interactable sub-branch, the one that runs for button and switch hovers, verbatim (287924-287929):

```csharp
else if (interactable != null && delayedActionInstance2 != null && delayedActionInstance == null)   // line 287924
{
    PassiveTooltip cursorPassiveTooltip4 = new PassiveTooltip(delayedActionInstance2, string.Empty, CursorManager.CursorThing);
    Tooltip.SetValuesForInteractable(ref cursorPassiveTooltip4, CursorManager.CursorThing, interactable);
    color = ((delayedActionInstance2.IsDisabled || !CursorManager.CursorThing.AllowInteraction) ? UnityEngine.Color.red : ((!WillStackFromInteractable(interactable)) ? ((delayedActionInstance2.Duration > 0f) ? UnityEngine.Color.yellow : UnityEngine.Color.green) : UnityEngine.Color.yellow));
    TooltipRef.HandleToolTipDisplay(cursorPassiveTooltip4);                                         // line 287929: no Title gate
```

So the Title-empty gates (287963, 287972) cover only collider and body hovers with nothing actionable under the crosshair; an interactable hover displays whatever `SetValuesForInteractable` produced, unconditionally. Emptying `Title` on an interactable hover therefore does not suppress the tooltip display; it only hides the name box row via the `TitleRenderer` gate (see the render-path section below).

Mod consequence: a `GetPassiveTooltip` postfix on a Structure/Device that fills only `Extended` (leaving `Title` empty) renders ONLY in ALT mouse-control hover; the plain crosshair drops it at both Title gates (287963 / 287972). Filling `Title` (typically with `DisplayName`) makes the crosshair display it too: via branch 2 when the collider form carries the Title, via branch 3 (`ActionLast`) when only the null-collider form does. This is why the PowerGridPlus fault-hover block fills `Title` alongside `Extended` (`Mods/PowerGridPlus/PowerGridPlus/Patches/FaultHoverPatches.cs`).

## PassiveTooltip: the struct and its TextMeshPro render path
<!-- verified: 0.2.6403.27689 @ 2026-07-14 -->

`PassiveTooltip` is a mutable struct (`Assets.Scripts.Objects`, decompile line 307045). Fields, the `Extended` accessors, and the default constructor, verbatim (307045-307107):

```csharp
public struct PassiveTooltip                     // line 307045
{
    public string Title;
    public string Action;
    public string State;
    public string Extended;
    public string RepairString;
    public string DeconstructString;
    public string ConstructString;
    public string PlacementString;
    public string BuildStateIndexMessage;
    public bool ShowRotate;
    public bool ShowConstructionRotate;
    public bool ShowScroll;
    public bool ShowAction;
    public float Slider;
    public UnityEngine.Color color;
    public bool FollowMouseMovement;

    public string GetExtendedText()              // line 307079
    {
        return Extended;
    }

    public void SetExtendedText(string text)     // line 307084
    {
        Extended = text;
    }

    public PassiveTooltip(bool toDefault = true) // line 307089
    {
        Title = string.Empty;
        Action = string.Empty;
        State = string.Empty;
        Extended = string.Empty;
        RepairString = string.Empty;
        DeconstructString = string.Empty;
        ConstructString = string.Empty;
        PlacementString = string.Empty;
        ShowRotate = false;
        ShowScroll = false;
        ShowConstructionRotate = false;
        ShowAction = true;
        BuildStateIndexMessage = string.Empty;
        color = UnityEngine.Color.white;
        Slider = -1f;
        FollowMouseMovement = false;
    }
}
```

Two further constructors exist (from a `Thing.DelayedActionInstance`, 307109-307129, quoted in the SetValuesForInteractable subsection below; full-argument, 307131-307149) plus `Populate(Connection end) => end.Populate(this)` (307151-307154). Do not confuse it with `PassiveUITooltip` (307029), the two-field readonly struct returned by `Thing.GetPassiveUITooltip`.

Every HUD hover path lands the struct in `Assets.Scripts.UI.Tooltip : UserInterfaceBase` (class 253966), held as `public Tooltip TooltipRef;` on `InventoryManager` (285975). The display fields are TextMeshPro components (253976-253982):

```csharp
public TextMeshProUGUI TooltipTitle;             // line 253976
public TextMeshProUGUI TooltipAction;            // line 253978
public TextMeshProUGUI TooltipState;             // line 253980
public TextMeshProUGUI TooltipExtended;          // line 253982
```

`Tooltip.HandleToolTipDisplay(PassiveTooltip cursorPassiveTooltip)` (254322) calls `SetUpToolTip` (254298-254320), which copies `Extended = cursorPassiveTooltip.Extended;` (254305) into the `Extended` property. The property setter writes the TextMeshPro text directly (254144-254163, verbatim):

```csharp
public string Extended                           // line 254144
{
    get
    {
        return _extended;
    }
    set
    {
        if (Mode == TooltipMode.Hidden)
        {
            Mode = TooltipMode.ActionFirst;
        }
        if (_extended != value)
        {
            Dirty = true;
        }
        _extended = value;
        TooltipExtended.text = _extended;        // line 254161
    }
}
```

So the `Extended` string is rendered by a `TextMeshProUGUI`, which parses `\n` line breaks and rich-text tags (`<color>`, nested spans) in `.text`. The game relies on exactly that in this component: `SetUpToolTip` itself wraps the action line in a color tag, `Action = "<color=#" + ColorToHex(Color) + ">" + Action + "</color> ";` (254319), and vanilla overrides write tags into the struct too (`LogicMirror.GetPassiveTooltip` puts `$"Mirroring <color=green>{...}</color>"` into `State`, see [LogicMirror](./LogicMirror.md)). Confirmed in-game during the PowerGridPlus fault-hover work: multi-line blocks appended to `Extended` with `"\n"` separators and `<color>` tags render as colored lines in the body tooltip.

### SetUpToolTip: the Action color wrap and the per-row renderer gates
<!-- verified: 0.2.6403.27689 @ 2026-07-14 -->

`SetUpToolTip` copies every struct field into the Tooltip properties (each setter writes its TextMeshPro `.text` directly; `TooltipAction.text = value;` at 254098), computes the `_has*` emptiness flags, and THEN wraps the ENTIRE Action string in color markup. Verbatim (254298-254320):

```csharp
public void SetUpToolTip(string action, PassiveTooltip cursorPassiveTooltip)     // line 254298
{
    Action = cursorPassiveTooltip.Action;                                        // line 254300
    Title = cursorPassiveTooltip.Title;
    State = cursorPassiveTooltip.State;
    Color = cursorPassiveTooltip.color;                                          // line 254303
    Slider = cursorPassiveTooltip.Slider;
    Extended = cursorPassiveTooltip.Extended;
    BuildStateInfo = cursorPassiveTooltip.ConstructString;
    DeconstructBuildState = cursorPassiveTooltip.DeconstructString;
    RepairBuildState = cursorPassiveTooltip.RepairString;
    PlacementType = cursorPassiveTooltip.PlacementString;
    TooltipNumberOfBuildStates.text = cursorPassiveTooltip.BuildStateIndexMessage;
    _hasTitle = !string.IsNullOrEmpty(_title) && _title.Length > 0;              // line 254311
    _hasState = !string.IsNullOrEmpty(_state) && _state.Length > 0;              // line 254312
    _hasAction = !string.IsNullOrEmpty(_action) && _action.Length > 0;           // line 254313
    _hasExtended = !string.IsNullOrEmpty(_extended) && _extended.Length > 0;     // line 254314
    _hasConstruction = !string.IsNullOrEmpty(_buildStateInfo);
    _hasDeconstruction = !string.IsNullOrEmpty(_deconstructBuildState);
    _hasRepair = !string.IsNullOrEmpty(_repairStateInfo);
    _hasPlacement = !string.IsNullOrEmpty(PlacementType);
    Action = "<color=#" + ColorToHex(Color) + ">" + Action + "</color> ";        // line 254319
}
```

Consequences of the 254319 wrap:

- The action line's color is applied OUTSIDE the caller's string: it comes from the struct's `color` field, copied into the class field `public UnityEngine.Color Color = UnityEngine.Color.white;` (253998). A mod that appends text to `Action` inherits the wrap color for the whole line unless it closes and reopens its own `<color>` spans inside the string.
- `ColorToHex` (254377-254380) is `return $"{color.r:X2}{color.g:X2}{color.b:X2}";` over the `Color32` conversion of the color (alpha dropped), so `UnityEngine.Color.green` (0, 1, 0) renders as `<color=#00FF00>`. #00FF00 is therefore the vanilla action-text green, the value the interaction-color ladders pick for an allowed instant click (254426; inline copies 287919 / 287928 / 287949).
- The `_has*` flags are computed from the raw strings BEFORE the wrap (254311-254314 vs 254319), so an empty `Action` stays `_hasAction == false` even though the markup shell is still written into `TooltipAction.text`. The wrap appends a trailing space after `</color>`.

Visibility gating: `_hasExtended = !string.IsNullOrEmpty(_extended) && _extended.Length > 0;` (254314) feeds both the panel-visible decision (`flag2` at 254331 ORs `_hasExtended` in) and `ExtendedRenderer.SetVisible(_hasExtended)` (254350), so appending a non-empty `Extended` to an otherwise empty tooltip makes the panel appear. Caveat: `HandleToolTipDisplay` returns early when the player's active hand holds a `Tablet` (254325-254328), so body tooltips are suppressed while a tablet is out.

The visible-panel block of `HandleToolTipDisplay` toggles one `UiComponentRenderer` per row (fields declared 254025-254029), verbatim (254349-254351):

```csharp
StateRenderer.SetVisible(_hasState);         // line 254349
ExtendedRenderer.SetVisible(_hasExtended);   // line 254350
TitleRenderer.SetVisible(_hasTitle);         // line 254351
```

An empty `Title` (`_hasTitle`, 254311) hides the tooltip's name box entirely (in the on-screen button-hover layout this is the boxed thing name beside the action text, observed in-game during the PowerGridPlus button-tooltip layout work), and an empty `State` (`_hasState`, 254312) hides the state row the same way. There is no action-row entry in this block; the action-side elements hang off `flag` (254330 / 254341 / 254363-254369). Emptying `Title` alone cannot hide the panel while an action line is present: `DrawTooltip` (254389) forces `TooltipMode.Hidden` only when Title, State, Action, and Extended are ALL empty.

Mod consequence: a Harmony postfix on `Tooltip.SetValuesForInteractable` (subsection below) that sets `tooltip.Title = string.Empty` suppresses the name box for exactly that hover poll and nothing else. It is self-restoring because the struct is rebuilt from scratch on every poll (full replacement at 254415, fresh struct at 287926, `Idle` re-polls per frame at 239679-239721), and the postfix runs after the `SwitchTitleForTooltip` title write (254429-254432), so the emptied Title wins for that poll and vanishes with it.

The Title row renders mod markup untouched: `SetUpToolTip` copies `Title` verbatim (254301) with no color wrap (the 254319 wrap is Action-only), and the property setter (254102-254121) writes the TextMeshPro text directly (`TooltipTitle.text = _title;`, 254119), so `\n` breaks and explicit `<color>` / `<align>` tags in a mod-written Title render as authored, with un-tagged spans keeping the name box's prefab styling. Code puts no size constraint on the row: `TooltipTitle` has exactly two code references in Assembly-CSharp (the field declaration 253976 and that setter write 254119; the only other grep hits for the substring are the unrelated `GameString PlayerStatsTooltipTitle`, 284402 / 104449), and the assembly contains no `ContentSizeFitter` at all (whole-file census, 0 hits), so whether the name box background grows vertically for a multi-line Title is decided by serialized prefab layout, invisible in the decompile. In-game check (2026-07-14, PowerGridPlus merged-layout playtest, game version 0.2.6403.27689): the box DOES accommodate a three-line Title (all three lines rendered inside the background), and the Title row's effective font size is visibly larger than the Extended row's (identical markup rendered larger in the name box than on the casing tooltip), so a composition that wants casing-sized lines inside the Title needs an absolute `<size=N>` span with N read at runtime from `TooltipExtended.fontSize` via `InventoryManager.Instance` (static field, 286106) `.TooltipRef` (285975) `.TooltipExtended` (253982).

### SetValuesForInteractable: an interactable hover REPLACES the struct
<!-- verified: 0.2.6403.27689 @ 2026-07-14 -->

When the hovered collider maps to an `Interactable` (via `Thing.GetInteractable(Collider)`, see [Thing](./Thing.md)), both display routes call `Tooltip.SetValuesForInteractable`, which dry-runs the click (`InteractWith(..., doAction: false)`) and, when that preview returns a `DelayedActionInstance`, throws away the incoming struct and rebuilds it. Verbatim (254408-254433):

```csharp
public static void SetValuesForInteractable(ref PassiveTooltip tooltip, Thing CursorThing, Interactable interactable)   // line 254408
{
    Interaction interaction = new Interaction(InventoryManager.Parent, InventoryManager.ActiveHandSlot, CursorThing, KeyManager.GetButton(KeyMap.QuantityModifier));
    Thing.DelayedActionInstance delayedActionInstance = null;
    delayedActionInstance = ((!CursorThing.PreventInteraction(out var failResult, interactable, interaction)) ? CursorThing.InteractWith(interactable, interaction, doAction: false) : failResult);
    if (delayedActionInstance != null)
    {
        tooltip = new PassiveTooltip(delayedActionInstance, string.Empty, CursorThing);     // line 254415: full-struct replacement
        if ((delayedActionInstance != null && delayedActionInstance.IsDisabled) || !CursorThing.AllowInteraction)
        {
            tooltip.color = UnityEngine.Color.red;
        }
        else if (InventoryManager.WillStackFromInteractable(interactable))
        {
            tooltip.color = UnityEngine.Color.yellow;
        }
        else
        {
            tooltip.color = ((delayedActionInstance.Duration > 0f) ? UnityEngine.Color.yellow : UnityEngine.Color.green);
        }
    }
    if (delayedActionInstance != null && delayedActionInstance.SwitchTitleForTooltip)
    {
        tooltip.Title = interactable.ContextualName;                                        // line 254431
    }
}
```

The color ladder (254416-254427, quoted above) is the vanilla interaction-color policy: green for an instantly allowed click, yellow or red for every other state. Exact ladder:

- red when `(delayedActionInstance != null && delayedActionInstance.IsDisabled) || !CursorThing.AllowInteraction` (254416-254419): the previewed action is disabled, or interaction with the thing is disallowed.
- else yellow when `InventoryManager.WillStackFromInteractable(interactable)` (254420-254423).
- else `tooltip.color = ((delayedActionInstance.Duration > 0f) ? UnityEngine.Color.yellow : UnityEngine.Color.green);` (254424-254427): yellow for a timed action, green only for an enabled, allowed, non-stacking, zero-duration click.

`InventoryManager.NormalModeThing` re-derives the identical ladder inline in its interactable sub-branch (287928) and reduced no-stack forms in the drag and attack sub-branches (287919, 287949), so the policy holds on both display routes. The chosen struct `color` is what `SetUpToolTip` wraps around the whole Action line (254319, subsection above); `Color.green` there renders as #00FF00.

The replacement constructor `PassiveTooltip(Thing.DelayedActionInstance actionInstance, string actionOverride, Thing cursorThing)` maps the action preview onto the struct fields. Verbatim (307109-307129):

```csharp
public PassiveTooltip(Thing.DelayedActionInstance actionInstance, string actionOverride, Thing cursorThing)   // line 307109
{
    string title = ((actionInstance.OverrideTitle != string.Empty) ? actionInstance.OverrideTitle : cursorThing.DisplayName);   // line 307111
    string action = (string.IsNullOrEmpty(actionOverride) ? actionInstance.ActionMessage : actionOverride);
    Title = title;
    Action = action;
    State = actionInstance.GetStateMessage();                          // line 307115
    Extended = actionInstance.GetExtendedText();                       // line 307116
    RepairString = string.Empty;
    DeconstructString = string.Empty;
    ConstructString = string.Empty;
    PlacementString = string.Empty;
    ShowRotate = false;
    ShowScroll = false;
    ShowConstructionRotate = false;
    ShowAction = true;
    FollowMouseMovement = false;
    color = actionInstance.color;
    Slider = actionInstance.Slider;
    BuildStateIndexMessage = string.Empty;
}
```

So on a button/switch hover: `Title` is the action's `OverrideTitle` (or the Thing's `DisplayName` when unset), `Action` is the `ActionMessage` (which `Thing.InteractWith` seeds from `interactable.ContextualName`; see [Thing](./Thing.md) for the action-word semantics), `State` is `GetStateMessage()`, and `Extended` is `GetExtendedText()` FROM THE ACTION INSTANCE, not from the Thing's passive tooltip. Anything a `GetPassiveTooltip` patch wrote is discarded on interactable hovers; a mod that wants its lines on the button hover too must also cover the `InteractWith` / `GetContextualName` side (PowerGridPlus does both: `FaultHoverPatches` fills the passive struct, `TransformerHoverErrorPatches` covers the on/off-button hover).

### #008AE6: the informational blue vanilla UI text uses
<!-- verified: 0.2.6403.27689 @ 2026-07-14 -->

`#008AE6` occurs exactly four times in the 0.2.6403.27689 decompile, all as UI text color markup, making it the vanilla informational-blue accent to match when coloring tooltip or Stationpedia lines:

- `StationSuitProperties` (class 250399, constructor 250413-250428) colors the cold-side minimums of the suit readouts with it: `RocketMath.KelvinToCelsius(suitBase.MinCoolantTemperatureK).ToStringPrefix("°C", "#008AE6")` (250418) and `suitBase.SuitConvectionData.MinimumTemperatureC.ToStringPrefix("°C", "#008AE6")` (250421), against `"red"` / `"green"` / `"orange"` for the hot-side and operating values on the adjacent lines. (The decompile file renders the degree sign in these literals as an encoding artifact; restored to `°C` here.)
- `UniversalPage : UserInterfaceBase` (class 250451, the Stationpedia page renderer, see [UniversalPage](./UniversalPage.md)) uses it for every hyperlink: `private const string LINK_COLOR_FORMAT = "<link={0}><color=#008AE6>{1}</color></link>";` (250633), plus the identical inline format string in `CheckAndSetTextElement` (250640).

PowerGridPlus adopts the same hex for the informational (non-fault) value in its hover blocks (`Mods/PowerGridPlus/PowerGridPlus/FaultHover.cs`, `CapBlue`).

## Verification history

- 2026-07-14 (sixth pass, same day): the fifth-pass Open question is RESOLVED by an in-game check (PowerGridPlus merged-layout playtest, game version 0.2.6403.27689): the tooltip name box background accommodates a three-line `Title` (all three lines rendered inside the box on a faulted transformer's button hover). Same observation, new prefab fact: the Title row's effective font size is larger than the Extended row's (identical markup rendered visibly larger in the name box than on the casing tooltip), recorded in the Title-row note under the SetUpToolTip subsection together with the runtime read path for matching sizes (`InventoryManager.Instance` 286106 -> `TooltipRef` 285975 -> `TooltipExtended` 253982, `.fontSize`). Additive resolution; no decompile-sourced claim changed.
- 2026-07-14 (fifth pass, same day): Title-row render-path note added to the "SetUpToolTip" subsection, from the PowerGridPlus button-tooltip title-carry layout work (game version 0.2.6403.27689). New facts, census-checked against the full decompile: `Title` is copied verbatim at 254301 with no color wrap (the 254319 wrap is Action-only); the Title property body 254102-254121 writes `TooltipTitle.text = _title;` (254119); `TooltipTitle` has exactly two code references in Assembly-CSharp (declaration 253976, setter write 254119; the only other substring hits are the unrelated `GameString PlayerStatsTooltipTitle`, 284402 / 104449); `ContentSizeFitter` has zero occurrences in the assembly, so tooltip-row vertical sizing is prefab-serialized, not code-driven. One Open question added (does the name box background grow for a multi-line Title). Additive; no prior claim contradicted.
- 2026-07-14 (fourth pass, same day): four precision additions from the PowerGridPlus button-tooltip layout work, all ranges re-read verbatim from the 0.2.6403.27689 decompile. (1) New subsection "SetUpToolTip: the Action color wrap and the per-row renderer gates": full body 254298-254320 (Action/Color copies 254300/254303, `_has*` flags 254311-254314 computed before the wrap, whole-Action color wrap 254319 with trailing space), `ColorToHex` 254377-254380 (RGB hex over the `Color32` conversion, alpha dropped, so `Color.green` renders as #00FF00, the vanilla action-text green), `Color` field default white 253998, `TooltipAction.text` write 254098. (2) Same subsection: per-row renderer gates 254349-254351 (`StateRenderer` / `ExtendedRenderer` / `TitleRenderer` on `_hasState` / `_hasExtended` / `_hasTitle`, fields 254025-254029): an empty Title hides the name box entirely, an empty State hides the state row; `DrawTooltip` all-empty hide 254389; mod note that emptying `Title` in a `SetValuesForInteractable` postfix is self-restoring (struct rebuilt every poll, postfix runs after the `SwitchTitleForTooltip` write 254429-254432). The pre-existing "Visibility gating" paragraph moved under the new subsection unchanged. (3) SetValuesForInteractable subsection: interaction-color policy reading of the 254416-254427 ladder (red = disabled or `!AllowInteraction`, yellow = `WillStackFromInteractable` or `Duration > 0f`, green only for an instantly allowed click), with the inline parity ladders in `NormalModeThing` (287928 identical, 287919/287949 reduced). (4) Display routes section: gate-asymmetry note that every branch-1 sub-branch displays unconditionally (drag 287917-287923, interactable 287924-287946 with display 287929 quoted verbatim, attack 287947-287953, item 287954-287958; `delayedActionInstance` = AttackWith preview 287871-287874, `delayedActionInstance2` = InteractWith preview 287886, `delayedActionInstance3` = DragInto preview 287892-287894), so the Title-empty gates 287963/287972 cover only non-actionable hovers. Additive; no prior claim contradicted (the third-pass note that sub-case 287954-287957 displays "without a Title check" generalizes to the whole action branch).
- 2026-07-14 (third pass, same day): added "Display routes: the ALT mouse-control gate vs the crosshair poll" and, under the PassiveTooltip section, the subsections "SetValuesForInteractable: an interactable hover REPLACES the struct" and "#008AE6: the informational blue vanilla UI text uses" (game version 0.2.6403.27689, all bodies re-read verbatim from the decompile). New anchors: `KeyMap.MouseControl = KeyCode.LeftAlt` (44018 legacy field, 44086 key-list assign, 44166 AddKey registration, 44282 saved-bindings re-read); `MouseModeController` (201299) with `AltKeyDown` (201313), the `Check()` ALT unlock (201347-201351), and `SetState -> InputMouse.SetMouseControl(!locked)` (201375); `InputMouse.SetMouseControl` (239462-239484, `IsMouseControl` write 239466); the `InputMouse.Update` mouse-control gate (239647-239652); `Idle` (239679-239759: `GetPassiveTooltip` 239691, `GetInteractable` 239692, `SetValuesForInteractable` 239716-239719, unconditional `HandleToolTipDisplay` 239721, no Title gate); `InventoryManager.NormalModeThing` three-branch display gate (action branch 287898 with sub-branches 287917-287958, Title-gated collider branch 287963, Title-gated body branch 287972 with `TooltipMode.ActionLast` 287974); `Tooltip.SetValuesForInteractable` (254408-254433, struct replacement 254415, `SwitchTitleForTooltip` title swap 254429-254432); the `PassiveTooltip(DelayedActionInstance, string, Thing)` constructor (307109-307129: Title = OverrideTitle or DisplayName, State = GetStateMessage, Extended = GetExtendedText); `#008AE6` census (exactly 4 occurrences: `StationSuitProperties` 250418/250421, `UniversalPage` `LINK_COLOR_FORMAT` 250633 plus inline 250640). Additive; completes the render-path story from earlier today. No prior claim on this page contradicted; the earlier statement that the APC charge readout ships only through the null-collider poll is now explained mechanically by the body branch (287972, ActionLast). Thing-level members referenced here (`GetInteractable`, `InteractWith`, `GetContextualName`, `GetExtendedText`) are quoted in full on [Thing](./Thing.md), curated in the same pass.
- 2026-07-14 (later the same day): the cross-page flag at the end of the entry below is RESOLVED. A Rule 3 fresh validator confirmed against the 0.2.6403.27689 decompile that `CableRuptured : SmallGrid` (392848) declares no `GetPassiveTooltip` (full body 392848-392881) and that a wreckage body hover dispatches to `Structure.GetPassiveTooltip` (314440), with `Thing.GetPassiveTooltip` (319731) reached only by the no-damage / no-build-tooltip fall-through. [Cable](./Cable.md)'s Wreckage section is corrected and restamped to 0.2.6403.27689; its patch recommendation now targets `Structure.GetPassiveTooltip`, matching this page and the shipped `Mods/PowerGridPlus/PowerGridPlus/Patches/BurnReasonPatches.cs`. The "conflict protocol pending" note below no longer applies.
- 2026-07-14: added "GetPassiveTooltip: body-hover tooltip resolution chain" (with the override census) and "PassiveTooltip: the struct and its TextMeshPro render path" (game version 0.2.6403.27689), from the PowerGridPlus fault-hover work. All bodies read verbatim from the 0.2.6403.27689 decompile: ElectricalInputOutput override 395008-395023 (port labels only, tail base call), Device 371547-371557 (open-end labels, tail base call), Structure 314440-314465 (damage/build tooltip; healthy structures fall through to base), Thing 319731-319734 (returns the all-empty `new PassiveTooltip(true)`), AreaPowerControl 390800-390810 (both paths call base; the charge readout ships via the null-collider poll only); negative census for Transformer (no `GetPassiveTooltip`, no `GetContextualName` anywhere in 424598-424993) and the family table. Render path: PassiveTooltip struct 307045-307155, InputMouse route 239691/239721, InventoryManager.NormalModeThing 287868-287869 with `TooltipRef` 285975, Tooltip class 253966 with `TooltipExtended` TextMeshProUGUI 253982, SetUpToolTip 254298-254320 (Extended copy 254305, Action color-wrap 254319), Extended setter 254144-254163 (`TooltipExtended.text` write 254161), visibility gates 254314/254331/254350, tablet early-out 254325. Additive; no prior content on this page contradicted. Cross-page flag (not edited here, conflict protocol pending): [Cable](./Cable.md)'s CableRuptured section (stamped 0.2.6228.27061) states the wreckage inherits "the base `Thing.GetPassiveTooltip`"; at 0.2.6403.27689 the dispatch target for any SmallGrid subclass without its own override is `Structure.GetPassiveTooltip` (314440), with Thing's base only reached by fall-through.
- 2026-07-02: added "CheckPower: event-driven un-power outside the tick" and restamped "CheckConnections" against the 0.2.6403.27689 decompile. `CheckConnections` verbatim-unchanged at 394930-394936; NEW at 0.2.6403.27689: `OnRemoveCableNetwork` (394993-395006) nulls the matching `InputNetwork` / `OutputNetwork` before `CheckConnections` + `CheckPower` (the 0.2.6228 version had no such null-out; consequence documented on [PowerReceiver](./PowerReceiver.md)). CheckPower facts: base virtual at 394945-394951 (`RunSimulation && InputNetwork == null && Powered` -> `OnServer.Interact(InteractPowered, 0)`), call sites `OnAddCableNetwork` 394985-394991 / `OnRemoveCableNetwork` 394993-395006; `AreaPowerControl.CheckPower` override 390983-390989 gated on `NoPower` (390636-390650: no/empty cell AND (no input net OR `PotentialLoad <= 0`)) with extra call sites on OnOff interaction and battery-cell insert/remove; `Battery.CheckPower` override 391963-391969 re-syncs `InteractPowered.State` to the computed `Powered`; the handheld `PowerTool` `CheckPower` family (virtual ~353114, `SensorLenses` override 354109) flagged as unrelated. Additive plus one supersession-by-game-change (the OnRemoveCableNetwork null-out); no fresh validator needed. Driving work: Powered-semantics stage of the power rearchitecture session. Sections not re-read this pass (class header/fields, IsOperable, load accessors, IsProviderToDevice body, submergeable block) keep their 0.2.6228.27061 stamps; `IsProviderToDevice` was spot-checked shape-identical at 394953+.
- 2026-06-29: page created. Consolidates the `ElectricalInputOutput` bridge base, previously documented piecemeal on [Transformer](./Transformer.md) (fields + IsOperable), [Battery](./Battery.md) (class hierarchy), and [Device](./Device.md) (IsOperable collision). Sourced verbatim from `.work/decomp/0.2.6228.27061/Assembly-CSharp.decompiled.cs` lines 373755-373933: class header + fields, `IsPowerProvider => true` (373783), `IsPowerInputOutput => true` (373801), `IsOperable` self-short rule (373803-373813), `AvailablePower` / `CurrentLoad` / `PotentialLoad` load accessors (373815-373839), `CheckConnections` (373872-373878), `CheckPower` (373887-373893), `IsProviderToDevice` recursive cycle walk bounded by `MaxProviderRecursionIterations` (373895-373926, cap value 349623), `OnAddCableNetwork` (373928), and the submergeable block (373773-373870). Additive (new page); no existing verified content contradicted -- the IsOperable and field facts match what Transformer.md / Battery.md / Device.md already state, this page is the canonical home and they cross-link to it.

## Open questions

None currently. (The fifth-pass question, whether the tooltip name box background grows vertically for a multi-line `Title`, was resolved in-game the same day; see the sixth-pass Verification history entry and the Title-row note under the SetUpToolTip subsection.)
