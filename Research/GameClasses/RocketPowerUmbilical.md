---
title: RocketPowerUmbilical
type: GameClasses
created_in: 0.2.6228.27061
verified_in: 0.2.6228.27061
verified_at: 2026-06-21
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
<!-- verified: 0.2.6228.27061 @ 2026-06-21 -->

Both halves derive from `ElectricalInputOutput` (the same power base as `Battery` / `Transformer` / `AreaPowerControl`, so each has `InputNetwork` / `OutputNetwork` cable references and `InputConnection` / `OutputConnection`) and implement `IUmbilical`:

```csharp
public class RocketPowerUmbilicalFemale : ElectricalInputOutput, IRocketInternals, IRocketComponent, IUmbilical   // L147895
public class RocketPowerUmbilicalMale   : ElectricalInputOutput, IUmbilical, IRocketComponent                     // L148269
```

The Female is the rocket-internal side: `InternalCellType => RocketInternalCellType.Umbilical` and `StrictlyInternal => true` (L148027-L148029). The Male implements `IUmbilical, IRocketComponent` only (not `IRocketInternals`), i.e. it is the external/dockable side. Pairing is by partner reference, found via `RocketUmbilicalHelper.FindAndSetOtherUmbilical`:

- Female `UmbilicalType => UmbilicalType.Socket` (L147925), `PartnerType => typeof(RocketPowerUmbilicalMale)` (L148035), `IsCompatibleWith(other) => other is RocketPowerUmbilicalMale` (L148227).
- Male `IsCompatibleWith(other) => other is RocketPowerUmbilicalFemale` (L148792); on `SetPartner` it stores `partner as RocketPowerUmbilicalFemale` and re-checks error state (L148785-L148790).
- `OnLaunch` severs the partner (`_partnerUmbilical = null`); `OnLanded` re-runs `FindAndSetOtherUmbilical` (Female L148200-L148220, Male L148774-L148783).

Each half carries its own internal battery cell. The class field default is `[Header("Battery")] public float PowerMaximum = 10000f;` (Female L147898, Male L148272), but the prefab overrides it to **4900 J** for both `StructurePowerUmbilicalMale` and `StructurePowerUmbilicalFemale` (runtime-confirmed via a `Prefab.AllPrefabs` dump, 2026-06-21), so the real in-game cell is 4.9 kJ, not 10 kJ. `PowerStored` is clamped to `[0, PowerMaximum]`; `AvailablePower => PowerStored`.

## Partner pairing and connection state
<!-- verified: 0.2.6228.27061 @ 2026-06-21 -->

Each half stores its partner in a private field typed to the partner class; `IUmbilical` exposes no public partner getter (interface members L140614-L140631: `AsThing`, `PartnerType`, `PartnerDistance`, `FirstPartnerSearchPosition`, `UmbilicalType`, `PartnerRemoved()`, `IsCompatibleWith(IUmbilical)`, `SetPartner(IUmbilical)`). Reading the partner from outside the class requires reflection on the field:

- `RocketPowerUmbilicalFemale._partnerUmbilical` : `RocketPowerUmbilicalMale` (L147900).
- `RocketPowerUmbilicalMale._partnerUmbilical` : `RocketPowerUmbilicalFemale` (L148283).

"Connected" (docked) is exactly `_partnerUmbilical != null`. The field is assigned by `RocketUmbilicalHelper.FindAndSetOtherUmbilical` (geometry-based partner search, via `SetPartner`), nulled on launch, and restored on land. The Male also persists `_savedPartnerId` (long, L148295) so the pairing survives save / load.

Per-half transfer / validity gates (decompile-verified):

```csharp
// Female (L147929-147941)
public bool CanTransfer => true;
private bool PartnerValid => (object)_partnerUmbilical != null && _partnerUmbilical.CanTransfer;

// Male (L148309-148340)
public bool CanTransfer
{
    get
    {
        if (Powered && OnOff && Error == 0 && IsOpen) return !IsBroken;
        return false;
    }
}
private bool PartnerValid
{
    get
    {
        if ((object)_partnerUmbilical != null && _partnerUmbilical.CanTransfer)
        {
            if (_partnerUmbilical.RocketNetwork != null)
            {
                Rocket rocket = _partnerUmbilical.RocketNetwork.Rocket;
                if (rocket == null) return false;
                return rocket.RocketState == RocketState.OnLaunchMount;
            }
            return true;
        }
        return false;
    }
}
```

`IsOperable` differs per half: Male = `PartnerValid && InputNetwork != null` (L148342-148352); Female = `OutputNetwork != null && base.IsOperable` (L147962-147972). `CanTransfer` / `PartnerValid` gate the per-tick `MovePowerToUmbilical` (so power flow also requires the rocket to be on the launch mount). For "physically docked" alone, `_partnerUmbilical != null` is the correct gate: it is non-null whenever the two halves are coupled, regardless of power or launch state.

## Power transfer: paired batteries, NOT a dumb wire
<!-- verified: 0.2.6228.27061 @ 2026-06-21 -->

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

Net model: `[external grid] -> Male cell --MovePowerToUmbilical--> Female cell -> rocket-internal grid`. Each cell is a 4900 J buffer (the prefab value; the class default is 10000f, see Class model); the two cells decouple the two grids and meter flow across the gap.

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

## Connectors (confirmed) and cable type
<!-- verified: 0.2.6228.27061 @ 2026-06-18 -->

Each half connects to its grid through the `ElectricalInputOutput` `InputConnection` / `OutputConnection` (resolved in `CheckConnections`). The per-prefab `OpenEnds` layout, confirmed by a live `Prefab.AllPrefabs` dump (ScenarioRunner `connector-dump`, game 0.2.6228.27061):

| Prefab (player-facing) | OpenEnds |
|---|---|
| `StructurePowerUmbilicalMale` (Item Umbilical (Power)) | 2: `Power/Input`, `Data/None` (a dedicated data port) |
| `StructurePowerUmbilicalFemale` (Umbilical Socket (Power)) | 1: `Power/Output` |
| `StructurePowerUmbilicalFemaleSide` (Umbilical Socket Angle (Power)) | 1: `Power/Output` |

So only the male half carries a data connector; both sockets are power-only (one `Power/Output` connector each). Which cable coil (normal vs heavy, see [Cable](./Cable.md)) can physically snap onto a given connector is a separate matter of the connector's prefab grid-cell / collider geometry, NOT its `NetworkType`: there is no per-connector "accepted cable type" field in code (`SmallGrid.IsConnected` gates on `NetworkType` overlap plus grid coincidence only; cable type lives on the `Cable`, `Cable.Type { normal, heavy, superHeavy }`). The connector-dump measures `NetworkType`/`ConnectionRole`, not coil-fit geometry. See [Connection](./Connection.md) "Data-port discovery".

## Logic / data
<!-- verified: 0.2.6228.27061 @ 2026-06-18 -->

The Male declares its own `CanLogicRead`; the Female inherits the `Device` base logic methods (relevant to Harmony targeting: a base-`Device` patch with an `is RocketPowerUmbilicalFemale` filter is required for the Female, while the Male can be patched directly; see [HarmonyInheritedMethods](../Patterns/HarmonyInheritedMethods.md)). The umbilical does NOT carry logic/data across the coupling: `MovePowerToUmbilical` moves energy only, and the two halves sit on independent cable networks. Any logic bridging would have to go through the rocket's own data network, not the umbilical.

## Verification history

- 2026-06-21: corrected the umbilical cell size -- the prefab overrides `PowerMaximum` to 4900 J on both halves, not the 10000f class default; runtime-confirmed via a `Prefab.AllPrefabs` dump on the dedicated server. Restamped Class model + Power transfer.
- 2026-06-21: added "Partner pairing and connection state" section (private `_partnerUmbilical` fields typed to the partner class, L147900 / L148283; no public `IUmbilical` partner getter; `_savedPartnerId` L148295; exact `CanTransfer` / `PartnerValid` / `IsOperable` per half). Sourced from `.work/decomp/0.2.6228.27061/Assembly-CSharp.decompiled.cs` while implementing the PowerGridPlus umbilical logic-passthrough feature. Additive; no conflict with existing content.
- 2026-06-18: confirmed the connector layouts via a live `Prefab.AllPrefabs` dump (ScenarioRunner `connector-dump`, 0.2.6228.27061): Male = 2 connectors (`Power/Input` + dedicated `Data/None`), Female and Female-Side = 1 connector (`Power/Output`). Updated the Connectors section and resolved the OpenEnds open question.
- 2026-06-18: page created. Sourced from a PowerGridPlus rocket-device investigation reading `.work/decomp/0.2.6228.27061/Assembly-CSharp.decompiled.cs` L147890-148260 (Female) and L148260-148810 (Male) directly: class headers, the `PowerMaximum = 10000f` internal cells, `MovePowerToUmbilical` (L148715) doing `_partnerUmbilical.ReceivePower(null, n)`, the side-keyed power quartet, and the `IUmbilical` partner machinery. Confirms umbilicals are paired battery-buffer transfer devices, not dumb wires, and that connector layout / cable-type acceptance is prefab data.

## Open questions

- Which cable tiers (normal / heavy / super-heavy coil) each umbilical connector physically accepts. The `OpenEnds` `NetworkType` layout is confirmed (see Connectors above); coil-fit is connector grid-cell / collider geometry, not measured by the connector-type dump. Observe in-game or inspect the prefab colliders.
- Whether the umbilical's InputNetwork/OutputNetwork on the rocket side are ordinary `CableNetwork`s (rocket-internal cables) in all dock states, which determines whether `Cable`-tier rules apply to that side.
