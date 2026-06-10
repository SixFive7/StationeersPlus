---
title: Power Segmenting Devices
type: GameSystems
created_in: 0.2.6228.27061
verified_in: 0.2.6228.27061
verified_at: 2026-06-10
sources:
  - .work/decomp/0.2.6228.27061/Assembly-CSharp.decompiled.cs :: 373755 (ElectricalInputOutput), 403300 (Transformer), 370616 (Battery), 369509 (AreaPowerControl), 387065 (PowerTransmitter), 386861 (PowerReceiver), 148269 (RocketPowerUmbilicalMale), 147895 (RocketPowerUmbilicalFemale), 386738 (PowerConnection), 405441 (WirelessPower), 373728 (Electrical), 139947 (namespace Objects.Rockets)
related:
  - ../GameClasses/Transformer.md
  - ../GameClasses/Battery.md
  - ../GameClasses/PowerTransmitter.md
  - ../GameClasses/Cable.md
tags: [power]
---

# Power Segmenting Devices

A "segmenting device" is any device that holds two distinct cable / wireless network references AND has Input/Output power-flow semantics, so it bridges (segments) the power-flow graph. PowerGridPlus treats these as the level boundaries of its allocator cascade and as the edges of its cycle-detection graph. This page is the verified catalogue of the concrete classes, their namespaces, base chain, and the members the power simulation reads off them.

## The seven real segmenting classes + one vestigial
<!-- verified: 0.2.6228.27061 @ 2026-06-09 -->

Seven concrete instantiable classes segment the power graph. All seven derive from `ElectricalInputOutput` (so they expose `InputNetwork` / `OutputNetwork` / `InputConnection` / `OutputConnection` / `OnOff` / `ReferenceId` uniformly):

| Class | Namespace | Base chain | Decompile line |
|---|---|---|---|
| Transformer | `Assets.Scripts.Objects.Electrical` | ElectricalInputOutput : Device | 403300 |
| Battery | `Assets.Scripts.Objects.Electrical` | ElectricalInputOutput : Device | 370616 |
| AreaPowerControl | `Assets.Scripts.Objects.Electrical` | ElectricalInputOutput : Device | 369509 |
| PowerTransmitter | `Assets.Scripts.Objects.Electrical` | WirelessPower : ElectricalInputOutput : Device | 387065 |
| PowerReceiver | `Assets.Scripts.Objects.Electrical` | WirelessPower : ElectricalInputOutput : Device | 386861 |
| RocketPowerUmbilicalMale | `Objects.Rockets` | ElectricalInputOutput : Device | 148269 |
| RocketPowerUmbilicalFemale | `Objects.Rockets` | ElectricalInputOutput : Device | 147895 |

**Namespace correction worth noting:** `RocketPowerUmbilicalMale` / `RocketPowerUmbilicalFemale` are in the bare `Objects.Rockets` namespace (decompile `namespace Objects.Rockets` at line 139947), NOT `Assets.Scripts.Objects.Rockets`. The bare-`Objects` namespace family is the same one that holds `Objects.SwitchOnOff` and `Objects.DevicePart`; Stationeers uses both `Objects.*` and `Assets.Scripts.Objects.*` for different classes.

`PowerConnection : Electrical` (line 386738) is the eighth two-network candidate but is **vestigial dead code**: no prefab, no recipe, no build-menu entry, never `is`-checked anywhere in vanilla or any surveyed mod, and it does NOT derive from `ElectricalInputOutput` (it derives from `Electrical` at line 373728, which has no `InputNetwork`/`OutputNetwork`; it carries a `ConnectedCableNetworks` list and a never-called `GetOtherNetwork` helper). PowerGridPlus ignores it entirely.

## Shared base: ElectricalInputOutput
<!-- verified: 0.2.6228.27061 @ 2026-06-09 -->

```csharp
// line 373755
public class ElectricalInputOutput : Device, ISmartRotatable, ISubmergeable, IPowered, IDensePoolable, IReferencable, IEvaluable
{
    [Header("Electrical I/O")]
    public Connection InputConnection;      // line 373758
    public Connection OutputConnection;     // line 373760
    [ReadOnly] public CableNetwork InputNetwork;    // line 373763
    [ReadOnly] public CableNetwork OutputNetwork;   // line 373766

    public override bool IsPowerInputOutput => true;   // line 373801

    // Short-circuit gate (line 373803): both terminals on the same network -> not operable.
    protected override bool IsOperable
    {
        get
        {
            if (OutputNetwork != null && InputNetwork == OutputNetwork) return false;
            return base.IsOperable;
        }
    }
}
```

- The connected cable is reached via `InputConnection.GetCable()` / `OutputConnection.GetCable()`, and the per-instance Watts cap via that cable's `MaxVoltage` field (see `Cable.md`).
- `OnOff` is a `public virtual bool` declared on `Device` (line ~299160). Read it to know whether the device is switched on.
- All seven override `GetGeneratedPower(CableNetwork)` and `GetUsedPower(CableNetwork)` (public, returns float; the parameter is `[NotNull]`). Because each concrete class overrides these, a Harmony patch must target each concrete override, not the base.
- Counterintuitive namespace: the base `Device` class is `Assets.Scripts.Objects.Pipes.Device : SmallGrid` (decompile line 349588), NOT `Assets.Scripts.Objects.Device`. Power devices, pipe devices, and chutes all derive from this one `Device` in the `Pipes` namespace.

## Wireless pair anatomy (PowerTransmitter / PowerReceiver)
<!-- verified: 0.2.6228.27061 @ 2026-06-09 -->

- `PowerTransmitter` (line 387065): `InputNetwork` is the source cable network; `OutputNetwork` is the wireless network (`WirelessOutputNetwork => OutputNetwork as WirelessNetwork`, line 387087). `public PowerReceiver LinkedReceiver` (line 387089), null when unlinked. `MaxPowerTransmission = 5000f` static (387071); `PowerLossOverDistance` AnimationCurve (387073).
- `PowerReceiver` (line 386861): `OutputNetwork` is the destination cable network; `InputNetwork` is the wireless network. `private PowerTransmitter _linkedPowerTransmitter` (386865) exposed via `public PowerTransmitter LinkedPowerTransmitter` (386871) whose setter also writes `InputNetwork = value.OutputNetwork`.
- Because the setter ties `PR.InputNetwork` to `PT.OutputNetwork`, every receiver linked to a transmitter shares that one `WirelessNetwork` object. The wireless link is therefore representable in a topology graph as the single shared `WirelessNetwork` node bracketed by `PT.InputNetwork` (cable) and `PR.OutputNetwork` (cable); multi-PR fan-out from one PT all join through that shared node.

## Internal-cell devices (Battery, AreaPowerControl, RocketPowerUmbilical*)
<!-- verified: 0.2.6228.27061 @ 2026-06-09 -->

- `Battery` (370616): `public float PowerStored { get; set; }` (370665, clamped `[0, PowerMaximum]`), `public float PowerMaximum = 3600000f` (370629). `UsePower` / `ReceivePower` overrides at 371098 / 371106. Prefabs: small `StationBatterySmall` (PrefabName `StructureBattery`), large `StationBatteryLarge` (`StructureBatteryLarge`); the nuclear variant ships from the third-party MorePowerMod.
- `AreaPowerControl` (369509): inserted cell via `public BatteryCell Battery => BatterySlot.Get<BatteryCell>()` (369548); `public float BatteryChargeRate = 1000f` (369540, per-instance serialized); `private float _powerProvided` (369546) couples per-tick charge XOR discharge. No vanilla discharge-rate field.
- `RocketPowerUmbilicalMale` (148269) / `RocketPowerUmbilicalFemale` (147895): both `public float PowerMaximum = 10000f` and `public float PowerStored { get; set; }` (clamped). The Female has NO OnOff interactable button on the device itself (fault state must be surfaced by hover text only); the Male has the on-button affordance. Prefab names (verified from a live save's `<PrefabName>` entries): `StructurePowerUmbilicalMale` / `StructurePowerUmbilicalFemale`. Power methods are battery-shaped: `GetUsedPower(input)` returns `UsedPower + clamp(PowerMaximum - PowerStored, ...)` (the Male additionally gates on OnOff and returns just `UsedPower` when `Error == 1 && OnOff`); `GetGeneratedPower(output)` returns `max(PowerStored, 0)` (Female L148182-148199, Male L148744-148772). Logic-method declarations: the Male declares `CanLogicRead` / `CanLogicWrite` / `SetLogicValue` but NOT `GetLogicValue`; the Female declares none of them (both halves reach `GetLogicValue` through the `Device` base declaration). The `Device` base also declares slot-keyed overloads of `CanLogicRead` / `GetLogicValue`, so a Harmony patch on the base MUST pass an explicit `typeof(LogicType)` argument array or it raises `AmbiguousMatchException` and takes the whole `PatchAll` down.

## Power producer classes (not segmenters)
<!-- verified: 0.2.6228.27061 @ 2026-06-09 -->

Producers are the rigid power sources on the graph (a generator produces its `GetGeneratedPower` value whether or not anything consumes it). They are NOT segmenters (single network). PowerGridPlus's producer-isolation rule (a producer may connect only to a transformer or to other producers, else it faults) classifies them as below. Namespaces and base chains verified from the decompile:

| Class | Namespace | Base | Has OnOff button? |
|---|---|---|---|
| SolarPanel | `Assets.Scripts.Objects.Electrical` | Electrical, IPowerGenerator | No (hover/burn only) |
| WindTurbineGenerator | `Objects` | Device, IPowerGenerator | No |
| LargeWindTurbineGenerator | `Objects` | WindTurbineGenerator | No |
| RadioscopicThermalGenerator | `Assets.Scripts.Objects.Electrical` | Electrical | No |
| PowerGeneratorPipe | `Assets.Scripts.Objects.Electrical` | DeviceInputOutput, IThermal | Yes |
| GasFuelGenerator | `Assets.Scripts.Objects.Electrical` | PowerGeneratorPipe | Yes (inherited) |
| PowerGeneratorSlot | `Assets.Scripts.Objects.Electrical` | DeviceImport | Yes |
| SolidFuelGenerator | `Assets.Scripts.Objects.Electrical` | PowerGeneratorSlot | Yes (inherited) |
| StirlingEngine | `Assets.Scripts.Objects.Electrical` | DeviceInputOutput, IThermal | Yes |

`WindTurbineGenerator` and `LargeWindTurbineGenerator` are in the bare `Objects` namespace (like the umbilicals and `Objects.SwitchOnOff`), NOT `Assets.Scripts.Objects`. Because `LargeWindTurbineGenerator : WindTurbineGenerator`, `GasFuelGenerator : PowerGeneratorPipe`, and `SolidFuelGenerator : PowerGeneratorSlot`, a base-class `is` check covers each pair. "Has OnOff button" determines whether a producer-isolation fault can flash on the device (button-bearing producers) or must burn a cable / show hover only (SolarPanel, both wind turbines, RTG).

Two additional power-generating classes that the producer table above originally omitted:

| Class | Namespace | Base | Notes |
|---|---|---|---|
| TurbineGenerator | `Assets.Scripts.Objects.Electrical` | Device, ISmartRotatable (line 403819) | The small wall wind turbine. Declares its own `GetGeneratedPower` (line 403973). No OnOff button. |
| PowerConnector | `Assets.Scripts.Objects.Electrical` | Electrical (line 386798) | The dynamic-generator dock (portable generator connection point). Declares its own `GetGeneratedPower` (line 386810); supplies the network from the docked portable generator. Distinct from the vestigial `PowerConnection`. |

## GetGeneratedPower override map
<!-- verified: 0.2.6228.27061 @ 2026-06-10 -->

Every class that supplies power to a cable network declares its own `GetGeneratedPower(CableNetwork)` override; none of the producer classes inherit it from an intermediate base. The complete declaration list in the 0.2.6228.27061 decompile (`grep "float GetGeneratedPower"`, 16 hits):

| Line | Declaring class |
|---|---|
| 350696 | `Device` (virtual base) |
| 138898 | `WindTurbineGenerator` (inherited by `LargeWindTurbineGenerator`) |
| 148191 | `RocketPowerUmbilicalFemale` |
| 148761 | `RocketPowerUmbilicalMale` |
| 370000 | `AreaPowerControl` |
| 371127 | `Battery` |
| 375517 | `PowerGeneratorPipe` (inherited by `GasFuelGenerator`) |
| 386810 | `PowerConnector` |
| 387028 | `PowerReceiver` |
| 387268 | `PowerTransmitter` |
| 395580 | `RadioscopicThermalGenerator` |
| 400139 | `SolarPanel` |
| 400512 | `PowerGeneratorSlot` (inherited by `SolidFuelGenerator`) |
| 402686 | `StirlingEngine` |
| 403496 | `Transformer` |
| 403973 | `TurbineGenerator` |

Implication for Harmony: a postfix aimed at a producer class resolves to that class's own declared method (no inherited-method trap for these), and one patch on `WindTurbineGenerator` / `PowerGeneratorPipe` / `PowerGeneratorSlot` covers the respective subclass for free. The full producer patch set is 8 declared methods: SolarPanel, WindTurbineGenerator, RadioscopicThermalGenerator, PowerGeneratorPipe, PowerGeneratorSlot, StirlingEngine, TurbineGenerator, PowerConnector.

## Verification history

- 2026-06-09: Page created (0.2.6228.27061). Class catalogue and members gathered during the PowerGridPlus power refactor from the 0.2.6228.27061 decompile; `Objects.Rockets` namespace for the umbilical classes confirmed directly (nearest `namespace` decl above line 148269 is `namespace Objects.Rockets` at line 139947, correcting an earlier `Assets.Scripts.Objects.Rockets` guess).
- 2026-06-09: Added the power-producer class catalogue (namespaces, base chains, OnOff-button presence) verified from the same decompile, for the producer-isolation rule.
- 2026-06-10: Added the `GetGeneratedPower` override map (all 16 declarations) and the two previously-omitted power-generating classes `TurbineGenerator` (small wall wind turbine, line 403819) and `PowerConnector` (dynamic-generator dock, line 386798). Both declare their own `GetGeneratedPower`; both are single-network suppliers and therefore subject to producer-isolation, neither has an OnOff button.
- 2026-06-10 (correction): `Battery` is in `Assets.Scripts.Objects.Electrical`, NOT `Assets.Scripts.Objects.Pipes` as this page previously stated. Two independent machine witnesses: the C# compiler rejected `Assets.Scripts.Objects.Pipes.Battery` with CS0234 during the PowerGridPlus build, and the nearest `namespace` declaration above the class at decompile line 370616 reads `namespace Assets.Scripts.Objects.Electrical`. The fresh-validator sub-agent step was skipped because compiler evidence is conclusive; recorded here per the conflict-resolution rule.
- 2026-06-10: Added the rocket umbilical prefab names (`StructurePowerUmbilicalMale` / `StructurePowerUmbilicalFemale`, read from a live save's PrefabName entries), their battery-shaped power-method bodies (Female L148182-148199, Male L148744-148772), the logic-method declaration map for both halves, and the slot-keyed-overload `AmbiguousMatchException` trap on `Device.CanLogicRead` / `Device.GetLogicValue` base patches (hit live: it silently disabled every PowerGridPlus patch until the argument-type array was added).

## Open questions

- None outstanding for the segmenting-device catalogue itself. Per-class power-method formulas (the bodies of `GetGeneratedPower` / `GetUsedPower`) are documented on the individual `GameClasses/` pages where present.
