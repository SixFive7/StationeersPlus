---
title: RocketPowerUmbilical
type: GameClasses
created_in: 0.2.6228.27061
verified_in: 0.2.6403.27689
verified_at: 2026-07-08
sources:
  - .work/decomp/0.2.6228.27061/Assembly-CSharp.decompiled.cs :: Objects.Rockets.RocketPowerUmbilicalFemale (L147895-148259), Objects.Rockets.RocketPowerUmbilicalMale (L148269-148810)
  - .work/decomp/0.2.6403.27689/Assembly-CSharp.decompiled.cs :: lines 157690+ (abstract RocketPowerUmbilical base), 157952+ (RocketPowerUmbilicalFemale), 158302+ (RocketPowerUmbilicalMale)
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
<!-- verified: 0.2.6403.27689 @ 2026-07-02 -->

Structure changed at 0.2.6403.27689: there is now an abstract shared base that owns the cell, the partner reference, and the sync plumbing. Both halves derive from it:

```csharp
public abstract class RocketPowerUmbilical : ElectricalInputOutput, IUmbilical, IRocketComponent,
    IReferencable, IEvaluable, IRocketActionProgressableTarget                                     // line 157690
public class RocketPowerUmbilicalFemale : RocketPowerUmbilical, IRocketInternals, IRocketComponent,
    IRocketTransferActionProgressable, IRocketActionProgressable, IReferencable, IEvaluable        // line 157952
public class RocketPowerUmbilicalMale : RocketPowerUmbilical                                       // line 158302
```

(Previously each half derived from `ElectricalInputOutput` directly and carried its own copy of the fields.) Via `ElectricalInputOutput` each half still has `InputNetwork` / `OutputNetwork` cable references and `InputConnection` / `OutputConnection`. The base owns:

- `[Header("Battery")] public float PowerMaximum = 10000f;` (157693), the internal cell default. The 4900 J prefab override observed at 0.2.6228 is prefab-serialized, not code; re-verify at 0.2.6403 (see Open Questions). `PowerStored` is clamped to `[0, PowerMaximum]`.
- The SINGLE private partner field `private RocketPowerUmbilical _partnerUmbilical;` (157695), exposed to subclasses as `protected RocketPowerUmbilical PartnerUmbilical` (157727-157741, setter flags `NetworkUpdateFlags |= 512` on the server). Previously each half declared its own partner field typed to the partner class.
- `TransferProgress` (157743-157757, also sync-flagged 512) and `protected long _savedPartnerId` (157713) for save/load pairing.
- Base `public bool CanTransfer => true;` (157719). The Male HIDES this with `public new bool CanTransfer` (158321-158331: `Powered && OnOff && Error == 0 && IsOpen && !IsBroken`). Because it is `new`, not an override, any read through a base-typed `RocketPowerUmbilical` reference (including the Female's own `PartnerValid`, which reads `base.PartnerUmbilical.CanTransfer`) statically binds to the base property and gets `true`.

The Female is the rocket-internal side (`IRocketInternals`; at 0.2.6228 it reported `InternalCellType => RocketInternalCellType.Umbilical`, `StrictlyInternal => true`, `UmbilicalType => UmbilicalType.Socket`, `PartnerType => typeof(RocketPowerUmbilicalMale)`; not re-located at 0.2.6403 but the interface split is unchanged); the Male is the external/dockable side (`UmbilicalType => UmbilicalType.Umbilical`, 158317). Pairing is by the base partner reference, found via `RocketUmbilicalHelper.FindAndSetOtherUmbilical`; the Male's `SetPartner` stores `partner as RocketPowerUmbilicalFemale` into the base property and re-checks error state (158691-158696), and `IsCompatibleWith` remains type-checked per half (Male: `other is RocketPowerUmbilicalFemale`, 158698+). `OnLaunch` severs the partner; `OnLanded` re-runs `FindAndSetOtherUmbilical` (Male 158680-158689).

## Partner pairing and connection state
<!-- verified: 0.2.6403.27689 @ 2026-07-02 -->

The partner lives in ONE private field on the abstract base (`RocketPowerUmbilical._partnerUmbilical`, line 157695, typed to the base), exposed as `protected PartnerUmbilical` (157727-157741). `IUmbilical` still exposes no public partner getter, so reading the partner from outside the hierarchy requires reflection on the base field. (At 0.2.6228 each half declared its own field typed to the partner class; superseded by the shared base field.)

"Connected" (docked) is exactly `_partnerUmbilical != null`. The field is assigned by `RocketUmbilicalHelper.FindAndSetOtherUmbilical` (geometry-based partner search, via `SetPartner`), nulled on launch, and restored on land. `_savedPartnerId` (protected long, base line 157713) persists the pairing across save / load.

Per-half transfer / validity gates (0.2.6403.27689 decompile):

```csharp
// Base (line 157719)
public bool CanTransfer => true;

// Female (157967-157977)
private bool PartnerValid
{
    get
    {
        if ((object)base.PartnerUmbilical != null)
            return base.PartnerUmbilical.CanTransfer;   // binds to the BASE property => true
        return false;
    }
}

// Male (158321-158331)
public new bool CanTransfer
{
    get
    {
        if (Powered && OnOff && Error == 0 && IsOpen) return !IsBroken;
        return false;
    }
}
// Male (158333-158350)
private bool PartnerValid
{
    get
    {
        if ((object)base.PartnerUmbilical != null && base.PartnerUmbilical.CanTransfer)
        {
            if (base.PartnerUmbilical.RocketNetwork != null)
            {
                Rocket rocket = base.PartnerUmbilical.RocketNetwork.Rocket;
                if (rocket == null) return false;
                return rocket.RocketState == RocketState.OnLaunchMount;
            }
            return true;
        }
        return false;
    }
}
```

Note the property-hiding trap: the Male's `CanTransfer` is `new` (hides the base), and `PartnerUmbilical` is typed as the base class, so both halves' `PartnerValid` checks resolve `base.PartnerUmbilical.CanTransfer` against the base `=> true`. The Female's `PartnerValid` is therefore effectively "partner is non-null"; the Male's adds the partner-rocket `OnLaunchMount` requirement. The Male's own transfer gate (`CanTransfer && PartnerValid` in its `OnPowerTick`) is where the Powered / OnOff / Open / unbroken conditions actually bite, via its own hidden `CanTransfer`. For "physically docked" alone, `_partnerUmbilical != null` remains the correct gate.

At 0.2.6228 the `IsOperable` overrides were: Male = `PartnerValid && InputNetwork != null`, Female = `OutputNetwork != null && base.IsOperable`; not re-located at 0.2.6403 (the Male's `CheckError` at 158520+ still keys off `IsOperable`).

## Power transfer: paired batteries, NOT a dumb wire; BOTH halves push at 0.2.6403
<!-- verified: 0.2.6403.27689 @ 2026-07-02 -->

The coupling across the gap is an explicit, per-tick transfer between the two internal cells, not a cable-network merge. The Male half drives its push from `OnPowerTick` (158511-158518):

```csharp
public override void OnPowerTick()          // Male, 158511
{
    base.OnPowerTick();
    if (CanTransfer && PartnerValid)
    {
        MovePowerToUmbilical();
    }
}

private void MovePowerToUmbilical()         // Male, 158621-158626
{
    float num = Mathf.Min(Mathf.Clamp(base.PartnerUmbilical.PowerMaximum - base.PartnerUmbilical.PowerStored, 0f, PowerMaximum), base.PowerStored);
    base.PartnerUmbilical.ReceivePower(null, num);   // null CableNetwork: not a wire
    base.PowerStored -= num;
}
```

So the Male moves `min(partner headroom clamped to own PowerMaximum, own stored)` into the Female's cell by calling `ReceivePower(null, ...)` directly on the partner object. The `null` `CableNetwork` argument is the tell: no network is involved in the coupling. Formula unchanged from 0.2.6228.

NEW at 0.2.6403.27689: the Female now has its OWN per-tick transfer (previously the Female had no `MovePowerToUmbilical` and the coupling was strictly station -> rocket):

```csharp
public override void OnPowerTick()          // Female, 158110-158118
{
    base.OnPowerTick();
    if (base.TransferProgress >= 1f && PartnerValid)
    {
        MovePowerToUmbilical();
        base.TransferProgress = 0f;
    }
}

private void MovePowerToUmbilical()         // Female, 158120-158141
{
    float num = Mathf.Clamp(base.PartnerUmbilical.PowerMaximum - base.PartnerUmbilical.PowerStored, 0f, PowerMaximum);
    float a = num;
    foreach (Battery battery in base.RocketNetwork.Batteries)
    {
        if (!battery.IsEmpty && battery.OnOff && battery.Error != 1)
        {
            float num2 = Mathf.Min(num, battery.PowerStored);
            battery.PowerStored -= num2;
            num -= num2;
            base.PowerStored += num2;
            if (num <= 0f)
            {
                break;
            }
        }
    }
    float num3 = Mathf.Min(a, base.PowerStored);
    base.PartnerUmbilical.ReceivePower(null, num3);
    base.PowerStored -= num3;
}
```

Direction and semantics of the Female path: it computes the PARTNER'S headroom (clamped to its own `PowerMaximum`), PULLS that much out of the rocket's batteries (`RocketNetwork.Batteries`, walking them in list order, skipping empty / off / errored cells) into its own cell, then PUSHES `min(original headroom, own stored)` into the partner's cell via the same direct `ReceivePower(null, ...)` call. So the umbilical pair moves power in BOTH directions at 0.2.6403: Male cell -> Female cell (station -> rocket, when the Male's `CanTransfer` gate passes) and rocket batteries -> Female cell -> Male cell (rocket -> station, gated on `TransferProgress >= 1f && PartnerValid`, with `TransferProgress` reset to 0 after each transfer, so the Female pushes one chunk per completed `TransferProgress` cycle rather than every tick; what advances `TransferProgress` toward 1 is in Open Questions). The old "one-way station -> rocket only" claim is superseded.

Cross-tick note: the direct `ReceivePower(null, ...)` crossing runs in phase 2 (`IPowered.OnPowerTick`, after ALL networks have settled; see [ElectricityManager](./ElectricityManager.md)), so unlike every ledger bridge the umbilical hop is pool-slot-order-INsensitive: the energy lands in the partner's cell the same tick regardless of which side's cable network ticked first.

The game also forbids wiring a plain cable straight onto an umbilical connector: `Cable.CanConstruct` rejects placement adjacent to an umbilical (`IsConnectingToUmbilical`, Cable 392625-392632), so the two halves cannot be bridged by an ordinary cable. The umbilical is its own transfer device.

Net model: `[external grid] <-> Male cell <--direct ReceivePower(null, ...)--> Female cell <-> rocket-internal grid`. Each cell is a small buffer (10000f code default at base line 157693; 4900 J prefab override observed at 0.2.6228, see Open Questions); the two cells decouple the two grids and meter flow across the gap.

## Per-tick power methods (battery-like, side-keyed)
<!-- verified: 0.2.6403.27689 @ 2026-07-02 -->

Each half exposes the standard `Device` power-tick quartet, side-keyed like `Battery` (charge on `InputNetwork`, discharge on `OutputNetwork`). Male verbatim (`UsePower` 158628-158637, `ReceivePower` 158639-158648, `GetUsedPower` 158650-158665, `GetGeneratedPower` 158667-158678):

```csharp
public override void UsePower(CableNetwork cableNetwork, float powerUsed)        // discharge bookkeeping
{
    if (!OnOff) { base.LastPowerRemoved = 0f; return; }
    base.LastPowerRemoved = powerUsed;
    base.PowerStored = Mathf.Clamp(base.PowerStored - powerUsed, 0f, PowerMaximum);
}

public override void ReceivePower(CableNetwork cableNetwork, float powerAdded)   // charge
{
    if (Error == 1 || !OnOff) { base.LastPowerAdded = 0f; return; }
    base.LastPowerAdded = powerAdded;
    base.PowerStored = Mathf.Clamp(powerAdded + base.PowerStored, 0f, PowerMaximum);
}

public override float GetUsedPower([NotNull] CableNetwork cableNetwork)          // charge demand on InputNetwork
{
    if (InputNetwork == null || cableNetwork != InputNetwork) return 0f;
    if (Error == 1 && OnOff) return UsedPower;
    if (!OnOff) return 0f;
    return UsedPower + Mathf.Clamp(PowerMaximum - base.PowerStored, 0f, PowerMaximum);   // line 158664
}

public override float GetGeneratedPower(CableNetwork cableNetwork)               // discharge supply on OutputNetwork
{
    if (OutputNetwork == null || Error == 1 || cableNetwork != OutputNetwork) return 0f;
    if (!OnOff) return 0f;
    return Mathf.Max(base.PowerStored, 0f);                                              // line 158677
}
```

The Female's quartet (158143-158175) differs in three ways: its `UsePower` (158143-158147) and `ReceivePower` (158149-158153) carry NO gates at all (no `OnOff`, no `Error`; they set `LastPowerRemoved` / `LastPowerAdded` and mutate `PowerStored` unconditionally, which is what lets the Male's direct `ReceivePower(null, ...)` push land regardless of the socket's switch state); its `GetUsedPower` (158155-158162) has no `OnOff` gate either (off-network or `Error == 1` returns 0, otherwise `UsedPower + Clamp(PowerMaximum - PowerStored, 0, PowerMaximum)` even while switched off); and its `GetGeneratedPower` (158164-158175) additionally returns 0 when the partner is a `RocketPowerUmbilicalFemale` (158170-158173), a socket-to-socket guard. Conversely the Male's `ReceivePower` gates on `Error == 1 || !OnOff`, and the Female's `MovePowerToUmbilical` decrements its own `PowerStored` by the pushed amount unconditionally after the call (158138-158140), so when the Male is off or errored the Female-side push is silently DESTROYED (pulled out of the rocket batteries, subtracted from the Female's cell, never added to the Male). Static-analysis conclusion from the verified bodies; not yet observed in-game.

Like a vanilla `Battery`, there is no per-tick rate cap on the umbilical's own methods: it offers full headroom (`PowerMaximum - PowerStored`) on charge and full stored energy on discharge each tick. PowerGridPlus's `RocketUmbilicalPatches` adds the rate caps and the soft-power LogicTypes; see that file. The `LastPowerAdded` / `LastPowerRemoved` accessors live on the abstract base now (backing fields 157705-157711) with the same divide-by-1000, one-tick decay, and 512-flag sync semantics observed at 0.2.6228 (accessor bodies not re-read line-by-line this pass).

### Gate table and credit semantics (delivered quiescent is not burned)
<!-- verified: 0.2.6403.27689 @ 2026-07-08 -->

Consolidated from the verbatim bodies above, re-read this pass (Female quartet 158143-158175, Male quartet 158628-158678). Two structural facts first: only the two QUERIES are network-keyed. All four SETTLE methods (`UsePower` / `ReceivePower` on both halves) ignore the `cableNetwork` argument entirely; nothing in their bodies reads it. That is what lets the docked crossing call `ReceivePower(null, ...)` with no network at all (Female push 158139, Male push 158624; phase-2 timing note above).

| Method | Female | Male |
|---|---|---|
| `UsePower` (settle) | no gates at all (158143-158147) | `!OnOff` returns early with `LastPowerRemoved = 0` (158628-158637) |
| `ReceivePower` (settle) | no gates at all (158149-158153) | `Error == 1` or `!OnOff` returns early with `LastPowerAdded = 0` (158639-158648) |
| `GetUsedPower` (query, InputNetwork-keyed) | null/wrong network or `Error == 1` returns 0; no OnOff gate; else `UsedPower + Clamp(PowerMaximum - PowerStored, 0, PowerMaximum)` (158155-158162) | null/wrong network returns 0; `Error == 1 && OnOff` returns the bare `UsedPower` (158656-158658); `!OnOff` returns 0; else `UsedPower + Clamp(headroom)` (158650-158665) |
| `GetGeneratedPower` (query, OutputNetwork-keyed) | null/wrong network or `Error == 1` returns 0; partner-is-Female returns 0 (158170-158173); no OnOff gate; else `Max(PowerStored, 0)` (158164-158175) | null/wrong network, `Error == 1`, or `!OnOff` returns 0; else `Max(PowerStored, 0)` (158667-158678) |

Notable rows: the Female quartet contains no OnOff gate anywhere (the socket's switch never gates its grid math), and the Male's errored-but-on `GetUsedPower` branch keeps requesting the bare quiescent `UsedPower` (158656-158658), so an errored Male still bills its idle wattage upstream while its `GetGeneratedPower` advertises nothing.

Credit semantics: both halves' `ReceivePower` credits are bare clamps into the cell, `PowerStored = Mathf.Clamp(powerAdded + PowerStored, 0f, PowerMaximum)` (Female 158152, Male 158647). Neither nets `UsedPower` out of the delivery first, even though both `GetUsedPower` bodies REQUEST `UsedPower + headroom` (158161, 158664). So the delivered quiescent component is not burned as overhead in vanilla: whatever the grid delivers for the `UsedPower` term lands in the cell as charge like the rest of the grant, clamp permitting. Contrast `WallLightBattery.ReceivePower`, which subtracts `UsedPower` from the delivery before charging its cell (see [LightSources](../GameSystems/LightSources.md), "F. WallLightBattery: battery backup"): vanilla burns delivery overhead elsewhere, just not here.

Why this matters in this repo: PowerGridPlus's delivery alignment generalizes over exactly this seam. The mod funds each half's quiescent `UsedPower` as rigid demand and burns it from the delivered stream once per tick, so the cell credits exactly the granted share instead of quietly absorbing the quiescent component as extra charge.

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

- 2026-07-08: added "Gate table and credit semantics" subsection consolidating the per-half power quartet (game version 0.2.6403.27689; re-read Female 158143-158175, Male 158628-158678, docked crossings 158139 / 158624). Facts made explicit: all four settle methods ignore the `cableNetwork` argument (only the queries are network-keyed), the Female quartet carries no OnOff gate anywhere, the Male's `Error == 1 && OnOff` `GetUsedPower` branch returns the bare quiescent `UsedPower` (158656-158658), and both halves' `ReceivePower` credits are bare clamps into the cell with no `UsedPower` netting (158152 / 158647), so a delivered quiescent component lands as charge (contrast `WallLightBattery.ReceivePower`, which nets it out). Occasion: the PowerGridPlus delivery-seam generalization (the mod funds each half's quiescent as rigid demand and burns it from the delivered stream once per tick, so the cell credits exactly the granted share). Every claim matches the verbatim bodies already on this page from the 2026-07-02 pass; additive consolidation, no conflict, no fresh validator.
- 2026-07-02: version-change pass against the 0.2.6403.27689 decompile after the game update from 0.2.6228.27061. STRUCTURE CHANGED: there is now an abstract base `RocketPowerUmbilical : ElectricalInputOutput, IUmbilical, ...` (157690) owning `PowerMaximum` (157693), the SINGLE private `_partnerUmbilical` (157695) exposed as `protected PartnerUmbilical` (157727-157741), `_savedPartnerId` (157713), `TransferProgress` (157743-157757), and a `CanTransfer => true` (157719) that the Male HIDES with `new` (158321); `RocketPowerUmbilicalFemale : RocketPowerUmbilical` (157952) and `RocketPowerUmbilicalMale : RocketPowerUmbilical` (158302). Superseded the old per-half-field class model. TRANSFER CHANGED (supersession): the Female now has its own `OnPowerTick` transfer (158110-158118, gated `TransferProgress >= 1f && PartnerValid`) with a Female `MovePowerToUmbilical` (158120-158141) that pulls the partner's headroom out of `RocketNetwork.Batteries` into its own cell and pushes it to the partner via `ReceivePower(null, ...)`; the previous "one-way station -> rocket only" claim is superseded, and the pair now moves power in both directions. Male push path re-verified with formula unchanged (`OnPowerTick` 158511-158518, `MovePowerToUmbilical` 158621-158626). Grid-facing methods re-verified (Male `GetUsedPower` headroom at 158664, `GetGeneratedPower` at 158677; Female 158143-158175 with its no-gate `UsePower` / `ReceivePower`, no-`OnOff` `GetUsedPower`, and Female-partner `GetGeneratedPower` guard at 158170-158173). Added the pool-slot-order-INsensitivity note (the direct `ReceivePower(null, ...)` crossing runs in phase 2, unlike every ledger bridge) and the static-analysis note that a Female push into an off/errored Male is destroyed. The 4900 J prefab `PowerMaximum` figure is prefab-serialized and could not be re-verified from the decompile; moved to Open Questions for an InspectorPlus re-check.
- 2026-06-21: corrected the umbilical cell size -- the prefab overrides `PowerMaximum` to 4900 J on both halves, not the 10000f class default; runtime-confirmed via a `Prefab.AllPrefabs` dump on the dedicated server. Restamped Class model + Power transfer.
- 2026-06-21: added "Partner pairing and connection state" section (private `_partnerUmbilical` fields typed to the partner class, L147900 / L148283; no public `IUmbilical` partner getter; `_savedPartnerId` L148295; exact `CanTransfer` / `PartnerValid` / `IsOperable` per half). Sourced from `.work/decomp/0.2.6228.27061/Assembly-CSharp.decompiled.cs` while implementing the PowerGridPlus umbilical logic-passthrough feature. Additive; no conflict with existing content.
- 2026-06-18: confirmed the connector layouts via a live `Prefab.AllPrefabs` dump (ScenarioRunner `connector-dump`, 0.2.6228.27061): Male = 2 connectors (`Power/Input` + dedicated `Data/None`), Female and Female-Side = 1 connector (`Power/Output`). Updated the Connectors section and resolved the OpenEnds open question.
- 2026-06-18: page created. Sourced from a PowerGridPlus rocket-device investigation reading `.work/decomp/0.2.6228.27061/Assembly-CSharp.decompiled.cs` L147890-148260 (Female) and L148260-148810 (Male) directly: class headers, the `PowerMaximum = 10000f` internal cells, `MovePowerToUmbilical` (L148715) doing `_partnerUmbilical.ReceivePower(null, n)`, the side-keyed power quartet, and the `IUmbilical` partner machinery. Confirms umbilicals are paired battery-buffer transfer devices, not dumb wires, and that connector layout / cable-type acceptance is prefab data.

## Open questions

- Which cable tiers (normal / heavy / super-heavy coil) each umbilical connector physically accepts. The `OpenEnds` `NetworkType` layout is confirmed (see Connectors above); coil-fit is connector grid-cell / collider geometry, not measured by the connector-type dump. Observe in-game or inspect the prefab colliders.
- Whether the umbilical's InputNetwork/OutputNetwork on the rocket side are ordinary `CableNetwork`s (rocket-internal cables) in all dock states, which determines whether `Cable`-tier rules apply to that side.
- The 4900 J prefab `PowerMaximum` override (both halves) was runtime-confirmed at 0.2.6228.27061; the 0.2.6403.27689 code default is still 10000f (line 157693) but the prefab value is serialized asset data. Re-verify via InspectorPlus at 0.2.6403 (request: types=[RocketPowerUmbilicalMale, RocketPowerUmbilicalFemale], fields=[PowerMaximum, PowerStored]).
- What drives `TransferProgress` toward 1 for the Female's rocket-to-station push cadence (the `IRocketTransferActionProgressable` machinery was not traced this pass).
- Runtime confirmation of the Female-push-into-off-Male energy destruction (static-analysis conclusion, see the transfer section).
