---
title: RocketPowerUmbilical
type: GameClasses
created_in: 0.2.6228.27061
verified_in: 0.2.6228.27061
verified_at: 2026-06-18
sources:
  - .work/decomp/0.2.6228.27061/Assembly-CSharp.decompiled.cs :: Objects.Rockets.RocketPowerUmbilicalFemale (L147895-148259), Objects.Rockets.RocketPowerUmbilicalMale (L148269-148810)
  - $(StationeersPath)\rocketstation_Data\Managed\Assembly-CSharp.dll :: Objects.Rockets.RocketPowerUmbilicalMale, Objects.Rockets.RocketPowerUmbilicalFemale
  - Mods/PowerGridPlus/PowerGridPlus/Patches/RocketUmbilicalPatches.cs (mod-side rate caps + logic exposure)
related:
  - ./Battery.md
  - ./Connection.md
  - ./Cable.md
tags: [power, prefab]
---

# RocketPowerUmbilical

The power umbilical pair that links a rocket's internal power grid to an external (station / gantry) grid. Player-facing names map to these classes and prefabs:

| Player-facing name | Prefab (confirm live) | C# class |
|---|---|---|
| Item Umbilical (Power) | `StructurePowerUmbilicalMale` | `RocketPowerUmbilicalMale` |
| Umbilical Socket (Power) | `StructurePowerUmbilicalFemale` | `RocketPowerUmbilicalFemale` |
| Umbilical Socket Angle (Power) | `StructurePowerUmbilicalFemaleSide` | `RocketPowerUmbilicalFemale` |

(The exact prefab names are prefab asset data; the class is decompile-confirmed. The "Angle" socket is a second prefab of the same `RocketPowerUmbilicalFemale` class.)

## Class model
<!-- verified: 0.2.6228.27061 @ 2026-06-18 -->

Both halves derive from `ElectricalInputOutput` (the same power base as `Battery` / `Transformer` / `AreaPowerControl`, so each has `InputNetwork` / `OutputNetwork` cable references and `InputConnection` / `OutputConnection`) and implement `IUmbilical`:

```csharp
public class RocketPowerUmbilicalFemale : ElectricalInputOutput, IRocketInternals, IRocketComponent, IUmbilical   // L147895
public class RocketPowerUmbilicalMale   : ElectricalInputOutput, IUmbilical, IRocketComponent                     // L148269
```

The Female is the rocket-internal side: `InternalCellType => RocketInternalCellType.Umbilical` and `StrictlyInternal => true` (L148027-L148029). The Male implements `IUmbilical, IRocketComponent` only (not `IRocketInternals`), i.e. it is the external/dockable side. Pairing is by partner reference, found via `RocketUmbilicalHelper.FindAndSetOtherUmbilical`:

- Female `UmbilicalType => UmbilicalType.Socket` (L147925), `PartnerType => typeof(RocketPowerUmbilicalMale)` (L148035), `IsCompatibleWith(other) => other is RocketPowerUmbilicalMale` (L148227).
- Male `IsCompatibleWith(other) => other is RocketPowerUmbilicalFemale` (L148792); on `SetPartner` it stores `partner as RocketPowerUmbilicalFemale` and re-checks error state (L148785-L148790).
- `OnLaunch` severs the partner (`_partnerUmbilical = null`); `OnLanded` re-runs `FindAndSetOtherUmbilical` (Female L148200-L148220, Male L148774-L148783).

Each half carries its own internal battery cell: `[Header("Battery")] public float PowerMaximum = 10000f;` (Female L147898, Male L148272). `PowerStored` is clamped to `[0, PowerMaximum]`; `AvailablePower => PowerStored`.

## Power transfer: paired batteries, NOT a dumb wire
<!-- verified: 0.2.6228.27061 @ 2026-06-18 -->

The coupling across the gap is an explicit, one-directional, per-tick transfer between the two internal cells, not a cable-network merge. The Male half drives it from `OnPowerTick` (L148573):

```csharp
public override void OnPowerTick()
{
    base.OnPowerTick();
    if (CanTransfer && PartnerValid)
        MovePowerToUmbilical();
}

private void MovePowerToUmbilical()   // L148715
{
    float num = Mathf.Min(Mathf.Clamp(_partnerUmbilical.PowerMaximum - _partnerUmbilical.PowerStored, 0f, PowerMaximum), PowerStored);
    _partnerUmbilical.ReceivePower(null, num);   // null CableNetwork: not a wire
    PowerStored -= num;
}
```

So the Male moves `min(partner headroom, own stored)` into the Female's cell each tick by calling `ReceivePower(null, ...)` directly on the partner object. The `null` `CableNetwork` argument is the tell: no network is involved in the coupling. There is no `MovePowerToUmbilical` on the Female; the Female is the receiving end of the coupling (it still discharges to its own `OutputNetwork` independently).

The game also forbids wiring a plain cable straight onto an umbilical connector: `Cable.CanConstruct` rejects placement adjacent to an umbilical (`IsConnectingToUmbilical`), so the two halves cannot be bridged by an ordinary cable. The umbilical is its own transfer device.

Net model: `[external grid] -> Male cell (10 kW) --MovePowerToUmbilical--> Female cell (10 kW) -> rocket-internal grid`. Each cell is a 10000 J buffer; the two cells decouple the two grids and meter flow across the gap.

## Per-tick power methods (battery-like, side-keyed)
<!-- verified: 0.2.6228.27061 @ 2026-06-18 -->

Each half exposes the standard `Device` power-tick quartet, side-keyed exactly like `Battery` (charge on `InputNetwork`, discharge on `OutputNetwork`). Male verbatim (L148722-L148772); Female is the same shape (L148170-L148198):

```csharp
public override void UsePower(CableNetwork cableNetwork, float powerUsed)        // discharge bookkeeping
{
    if (!OnOff) { LastPowerRemoved = 0f; return; }
    LastPowerRemoved = powerUsed;
    PowerStored = Mathf.Clamp(PowerStored - powerUsed, 0f, PowerMaximum);
}

public override void ReceivePower(CableNetwork cableNetwork, float powerAdded)   // charge
{
    if (Error == 1 || !OnOff) { LastPowerAdded = 0f; return; }
    LastPowerAdded = powerAdded;
    PowerStored = Mathf.Clamp(powerAdded + PowerStored, 0f, PowerMaximum);
}

public override float GetUsedPower(CableNetwork cableNetwork)                    // charge demand on InputNetwork
{
    if (InputNetwork == null || cableNetwork != InputNetwork) return 0f;
    if (Error == 1 && OnOff) return UsedPower;
    if (!OnOff) return 0f;
    return UsedPower + Mathf.Clamp(PowerMaximum - PowerStored, 0f, PowerMaximum);
}

public override float GetGeneratedPower(CableNetwork cableNetwork)               // discharge supply on OutputNetwork
{
    if (OutputNetwork == null || Error == 1 || cableNetwork != OutputNetwork) return 0f;
    if (!OnOff) return 0f;
    return Mathf.Max(PowerStored, 0f);
}
```

Like a vanilla `Battery`, there is no per-tick rate cap on the umbilical's own methods: it offers full headroom (`PowerMaximum - PowerStored`) on charge and full stored energy on discharge each tick. PowerGridPlus's `RocketUmbilicalPatches` adds the rate caps and the soft-power LogicTypes; see that file. The `LastPowerAdded` / `LastPowerRemoved` accessors divide by 1000 and decay one tick after the last write (Female L147974-L148025), and `LastPowerAdded` rides `NetworkUpdateFlags |= 256` for client sync.

## Connectors and cable type
<!-- verified: 0.2.6228.27061 @ 2026-06-18 -->

Each half connects to its grid through the `ElectricalInputOutput` `InputConnection` / `OutputConnection` (resolved in `CheckConnections`). The number and `NetworkType` of the connectors, and which cable coil (normal vs heavy, see [Cable](./Cable.md)) can snap onto a given connector, are prefab asset data, NOT in the decompile: there is no per-connector "accepted cable type" field anywhere in code. Cable type lives on the `Cable` (`Cable.Type { normal, heavy, superHeavy }`), and `SmallGrid.IsConnected` gates only on `NetworkType` overlap plus grid coincidence, never on `Cable.CableType`. So whether an umbilical connector accepts a heavy cable, a normal cable, or both is decided by the connector's prefab grid cell / collider geometry. See [Connection](./Connection.md) "Data-port discovery" for the connector model and how to read a prefab's `OpenEnds` live.

## Logic / data
<!-- verified: 0.2.6228.27061 @ 2026-06-18 -->

The Male declares its own `CanLogicRead`; the Female inherits the `Device` base logic methods (relevant to Harmony targeting: a base-`Device` patch with an `is RocketPowerUmbilicalFemale` filter is required for the Female, while the Male can be patched directly; see [HarmonyInheritedMethods](../Patterns/HarmonyInheritedMethods.md)). The umbilical does NOT carry logic/data across the coupling: `MovePowerToUmbilical` moves energy only, and the two halves sit on independent cable networks. Any logic bridging would have to go through the rocket's own data network, not the umbilical.

## Verification history

- 2026-06-18: page created. Sourced from a PowerGridPlus rocket-device investigation reading `.work/decomp/0.2.6228.27061/Assembly-CSharp.decompiled.cs` L147890-148260 (Female) and L148260-148810 (Male) directly: class headers, the `PowerMaximum = 10000f` internal cells, `MovePowerToUmbilical` (L148715) doing `_partnerUmbilical.ReceivePower(null, n)`, the side-keyed power quartet, and the `IUmbilical` partner machinery. Confirms umbilicals are paired battery-buffer transfer devices, not dumb wires, and that connector layout / cable-type acceptance is prefab data.

## Open questions

- Exact prefab names and `OpenEnds` layout (connector count, per-connector `NetworkType` / `ConnectionRole`, and which cable tiers each connector accepts) for the Male, Female, and Female-Side prefabs. Prefab asset data; read via InspectorPlus (`types=[RocketPowerUmbilicalMale, RocketPowerUmbilicalFemale]`, `fields=[OpenEnds, InputConnection, OutputConnection, HasDataConnection]`) on placed instances, or a `Prefab.AllPrefabs` ScenarioRunner `OpenEnds` dump.
- Whether the umbilical's InputNetwork/OutputNetwork on the rocket side are ordinary `CableNetwork`s (rocket-internal cables) in all dock states, which determines whether `Cable`-tier rules apply to that side.
