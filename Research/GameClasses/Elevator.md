---
title: Elevator
type: GameClasses
created_in: 0.2.6228.27061
verified_in: 0.2.6228.27061
verified_at: 2026-05-18
sources:
  - .work/decomp/0.2.6228.27061/Assembly-CSharp.decompiled.cs :: lines 188139-188521 (ElevatorCarrageSaveData, ElevatorMode, ElevatorCarrage), 374128-374290 (ElevatorLevel), 374291-374587 (ElevatorShaft), 374588-374750 (ElevatorShaftNetwork), 297950-297970 + 299367-299379 (Thing.PaintableMaterial, Thing.IsPaintable)
related:
  - ./Thing.md
  - ./Structure.md
  - ./Device.md
  - ./CableNetwork.md
  - ./ISprayer.md
tags: [prefab, network]
---

## Class family
<!-- verified: 0.2.6228.27061 @ 2026-05-18 -->

Stationeers models an elevator with three placeable `Thing` classes plus one non-`Thing` coordinator and one serialization carrier. Note the in-code spelling `Carrage` (the game's own typo); mod code that interoperates with these types must match the spelling exactly.

| Class | Line | Base | Role |
|---|---|---|---|
| `ElevatorShaft` | 374291 | `Device, ISmartRotatable` | Generic shaft segment. Stackable vertical column piece. |
| `ElevatorLevel` | 374128 | `ElevatorShaft` | Specialized shaft segment marking a passenger stop. Adds door colliders, level digit displays, `BottomPosition`, `LevelCarragePosition`. Overrides `StopCarrage`, `CheckCarrageState`, and the `ShaftLevel` setter. |
| `ElevatorCarrage` | 188170 | `DynamicThing` | The moving cabin. Holds `CurrentShaft`, `ShaftNetwork`, `LevelTarget`, and the mode state machine. |
| `ElevatorShaftNetwork` | 374588 | (standalone class) | Non-`Thing` coordinator. Holds `List<ElevatorShaft> Shafts` and a single `ElevatorCarrage Carrage`. One per connected elevator stack. |
| `ElevatorCarrageSaveData` | 188139 | `DynamicThingSaveData` | Serialization snapshot for the carriage. Carries `LevelTarget`. |

In the in-game build menu these classes register multiple visual variants (for example with-cable and without-cable versions of the same C# class), so a player sees more entries than there are classes. All visual variants of one class share its behavior, paintability, and network membership.

## ElevatorShaftNetwork
<!-- verified: 0.2.6228.27061 @ 2026-05-18 -->

```csharp
public class ElevatorShaftNetwork
{
    public List<ElevatorShaft> Shafts = new List<ElevatorShaft>();
    public ElevatorCarrage Carrage;
    private float _speed;
    public const float DEFAULT_SPEED = 1.5f;

    public float Speed { get; set; }    // marks Carrage dirty on change
    public int PoweredValue { get; }    // 1 if any shaft has PoweredValue >= 1 AND PowerCable != null
    public bool Powered { get; }        // any shaft.Powered AND shaft.PowerCable != null
}
```

(decompile lines 374588-374644)

`Shafts` mixes both `ElevatorShaft` and `ElevatorLevel` instances. `ElevatorLevel` subclasses `ElevatorShaft`, so a `List<ElevatorShaft>` holds both, and the network code discriminates at use-time with `as ElevatorLevel` (for example, line 188386: `if ((bool)(ShaftNetwork.Shafts[i] as ElevatorLevel))`).

Constructors:

- `ElevatorShaftNetwork(ElevatorShaft)` registers the shaft only.
- `ElevatorShaftNetwork(ElevatorLevel, bool makeCarrage = true)` registers the level, and on the simulation server creates a carriage prefab at `elevatorShaft.BottomPosition` and registers it. The shaft that triggers carriage creation must be a `Level`, not a generic `Shaft`.

Back-references from pieces to network use the same field name on both `Thing` families:

- `ElevatorShaft.ShaftNetwork` (inherited by `ElevatorLevel`)
- `ElevatorCarrage.ShaftNetwork` (line 188176)

Identical naming means a mod that walks "from any elevator piece to the rest of its network" can read `.ShaftNetwork` without type discrimination.

## Registration
<!-- verified: 0.2.6228.27061 @ 2026-05-18 -->

`ElevatorShaftNetwork.Register(ElevatorCarrage)` (line 374685):

- If a different carriage was already registered, that one is `OnServer.Destroy`'d. The network holds at most one carriage at a time.
- The new carriage's `LevelTarget` is computed from `Shafts.IndexOf(CurrentShaft)`.
- The carriage's `ShaftNetwork` back-reference is set.
- `RefreshLevelState()` runs to update the level digits and lock interactions.

`ElevatorShaftNetwork.Register(ElevatorShaft)` (line 374703) appends to `Shafts` if not already present.

The carriage attaches itself to the local shaft network in `ElevatorCarrage.WaitThenRegister` (around line 188311):

```csharp
ElevatorShaft elevatorShaft = SmallCell.Get<ElevatorShaft>(base.ThingTransformPosition);
if ((bool)elevatorShaft && !CurrentShaft)
{
    CurrentShaft = elevatorShaft;
    CurrentShaft.ShaftNetwork.Register(this);
    ...
}
```

## Paintability
<!-- verified: 0.2.6228.27061 @ 2026-05-18 -->

Elevator pieces ARE paintable in vanilla, but the paintability is configured on the Unity prefab, not in C# code. A grep of the decompile for `PaintableMaterial =` assignments finds nothing on `ElevatorShaft` or `ElevatorCarrage` and yields the wrong conclusion if taken alone.

`Thing.PaintableMaterial` is a public, serialized field:

```csharp
[Header("Thing Colors")]
[Tooltip("If set, will allow any parts of the thing with this material to be spraypainted")]
public Material PaintableMaterial;
```

(decompile lines 297953-297955)

`Thing.IsPaintable` returns true if `PaintableMaterial` is non-null, or alternatively if the subclass overrides `HasPaintableMaskMaterial` to true:

```csharp
public bool IsPaintable
{
    get
    {
        if (!(PaintableMaterial != null))
        {
            return HasPaintableMaskMaterial;
        }
        return true;
    }
}

protected virtual bool HasPaintableMaskMaterial => false;
```

(decompile lines 299367-299379)

Because `PaintableMaterial` is public and serialized, Unity assigns it during prefab deserialization at scene load; each elevator prefab's `PaintableMaterial` is set in the prefab asset itself. `ElevatorShaft`, `ElevatorLevel`, and `ElevatorCarrage` do not override `IsPaintable` or `HasPaintableMaskMaterial`, so paintability flows from prefab data alone.

The same prefab-driven pattern applies to other vanilla paintables: `Wall`, `Pipe`, `Cable`, and `AreaPowerControl` do not assign `PaintableMaterial` in code either. Confirmed paintable in-game testing on 2026-05-18 (every shaft and level variant, with-cable and without, plus the carriage, accepted spray paint from the vanilla can).

## ElevatorMode state machine
<!-- verified: 0.2.6228.27061 @ 2026-05-18 -->

```csharp
public enum ElevatorMode : byte
{
    Stationary,
    Upward,
    Downward
}
```

(decompile line 188147)

`ElevatorCarrage._ElevatorMode` is backed by a `byte` field with `[ByteArraySync]`, written to the network in `Serialize` / `Deserialize` and on join. `SetElevatorMode(ElevatorMode)` (line 188294) dispatches to `OnServer.Interact(base.InteractActivate, 0)` for `Stationary` and to `OnServer.Interact(base.InteractActivate, 1)` for both `Upward` and `Downward`. The enum is exposed to IC10 via `new BasicEnum<ElevatorMode>("ElevatorMode")` at line 393584.

## Paint enumeration pattern
<!-- verified: 0.2.6228.27061 @ 2026-05-18 -->

Given any single elevator-piece `Thing` as a seed (a shaft, a level, or the carriage), the full paint set is reachable in one hop via the `.ShaftNetwork` back-reference:

```csharp
ElevatorShaftNetwork network = seed switch
{
    ElevatorShaft   shaft    => shaft.ShaftNetwork,
    ElevatorCarrage carriage => carriage.ShaftNetwork,
    _                        => null
};

if (network != null)
{
    foreach (ElevatorShaft piece in network.Shafts) // includes ElevatorLevel via inheritance
    {
        Paint(piece);
    }
    if (network.Carrage != null)
    {
        Paint(network.Carrage);
    }
}
```

`ElevatorLevel` does not need a separate branch: it inherits from `ElevatorShaft`, so the seed switch covers it and the `List<ElevatorShaft>` enumeration picks it up. Visual prefab variants (with-cable, without-cable) all instantiate one of these three classes and live in the same network, so the walk covers them uniformly.

The `SetCustomColor` setter on `ElevatorCarrage` is inherited from `DynamicThing` without override; the carriage's motion does not guard the color setter. Whether the assigned color persists visually through carriage motion is open (see Open questions).

## Verification history

- 2026-05-18: Initial writeup. Consolidated from two in-session drafts (`ElevatorPaintability.md`, `ElevatorSystem.md`, both unsanctioned and removed in the same change). All claims re-verified against `.work/decomp/0.2.6228.27061/Assembly-CSharp.decompiled.cs`. The "elevators are not paintable" claim from an earlier sub-agent pass was incorrect: it grepped for `PaintableMaterial =` assignments in code, which misses prefab-asset assignments by Unity serialization. In-game verification confirms paintability on all three classes and all known with-cable / without-cable visual variants.

## Open questions

- Does the custom color assigned to an `ElevatorCarrage` persist visually as the carriage moves vertically? `DynamicThing` color machinery is the same as for other moving items, so the expected answer is yes, but this has not been confirmed in-game.
- The exact vanilla in-game build-menu names for the with-cable and without-cable variants of each piece (e.g., for `ElevatorShaft`) are not captured here; they live in Stationpedia data and were not extracted in this pass.
