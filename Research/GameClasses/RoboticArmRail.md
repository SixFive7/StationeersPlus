---
title: RoboticArmRail family (rail pieces built by ItemKitLinearRail, plus docks / junctions / bypass)
type: GameClasses
created_in: 0.2.6228.27061
verified_in: 0.2.6228.27061
verified_at: 2026-04-20
sources:
  - E:\Steam\steamapps\common\Stationeers\rocketstation_Data\Managed\Assembly-CSharp.dll :: Objects.RoboticArm.RoboticArmRailBase
  - E:\Steam\steamapps\common\Stationeers\rocketstation_Data\Managed\Assembly-CSharp.dll :: Objects.RoboticArm.RoboticArmRailDeviceBase
  - E:\Steam\steamapps\common\Stationeers\rocketstation_Data\Managed\Assembly-CSharp.dll :: Objects.RoboticArm.RoboticArmRail
  - E:\Steam\steamapps\common\Stationeers\rocketstation_Data\Managed\Assembly-CSharp.dll :: Objects.RoboticArm.RoboticArmRailStraight
  - E:\Steam\steamapps\common\Stationeers\rocketstation_Data\Managed\Assembly-CSharp.dll :: Objects.RoboticArm.RoboticArmRailInnerCorner
  - E:\Steam\steamapps\common\Stationeers\rocketstation_Data\Managed\Assembly-CSharp.dll :: Objects.RoboticArm.RoboticArmJunction
  - E:\Steam\steamapps\common\Stationeers\rocketstation_Data\Managed\Assembly-CSharp.dll :: Objects.RoboticArm.RoboticArmBypass
  - E:\Steam\steamapps\common\Stationeers\rocketstation_Data\Managed\Assembly-CSharp.dll :: Objects.RoboticArm.RoboticArmDock
  - E:\Steam\steamapps\common\Stationeers\rocketstation_Data\Managed\Assembly-CSharp.dll :: Objects.RoboticArm.RoboticArmDockAtmos
  - E:\Steam\steamapps\common\Stationeers\rocketstation_Data\Managed\Assembly-CSharp.dll :: Objects.RoboticArm.RoboticArmDockCargo
  - E:\Steam\steamapps\common\Stationeers\rocketstation_Data\Managed\Assembly-CSharp.dll :: Objects.RoboticArm.RoboticArmDockCollector
  - E:\Steam\steamapps\common\Stationeers\rocketstation_Data\Managed\Assembly-CSharp.dll :: Objects.RoboticArm.RoboticArmDockHydroponics
  - E:\Steam\steamapps\common\Stationeers\rocketstation_Data\Managed\Assembly-CSharp.dll :: Objects.RoboticArm.IRoboticArmRail
  - E:\Steam\steamapps\common\Stationeers\rocketstation_Data\Managed\Assembly-CSharp.dll :: Objects.RoboticArm.IRoboticArmJunction
  - E:\Steam\steamapps\common\Stationeers\rocketstation_Data\Managed\Assembly-CSharp.dll :: Objects.RoboticArm.IRoboticArmBypass
  - E:\Steam\steamapps\common\Stationeers\rocketstation_Data\Managed\Assembly-CSharp.dll :: Objects.RoboticArm.INetworkedRoboticArm
  - E:\Steam\steamapps\common\Stationeers\rocketstation_Data\Managed\Assembly-CSharp.dll :: Objects.RoboticArm.RoboticArmNetwork
  - E:\Steam\steamapps\common\Stationeers\rocketstation_Data\Managed\Assembly-CSharp.dll :: Networks.StructureNetwork
  - E:\Steam\steamapps\common\Stationeers\rocketstation_Data\resources.assets :: GameObject prefab names
related:
  - ItemKitLinearRail.md
  - Structure.md
tags: [prefab, network, transforms]
---

## Overview
<!-- verified: 0.2.6228.27061 @ 2026-04-20 -->

The robotic-arm rail family is the Stationeers "Larre" robotic arm track: a chain of small-grid structures on which a `RoboticArm` traverses between dock positions. Rail pieces are placed by the `ItemKitLinearRail` multi-constructor (see [ItemKitLinearRail.md](ItemKitLinearRail.md)). The four dock variants (atmos / cargo / collector / hydroponics) and the junction / bypass / base dock have their own separate kits (`ItemKitLarreDockAtmos`, `ItemKitLarreDockBypass`, `ItemKitLarreDockCargo`, `ItemKitLarreDockCollector`, `ItemKitLarreDockHydroponics`), but all 15-ish prefabs share the same rail-graph infrastructure and all implement `IRoboticArmRail`.

The family sits on the **half-size "small grid"** (`SmallGrid.SmallGridSize = 0.5f`), not the default 2.0-unit world grid that walls and large structures use. This matters for any code that wants to flood-fill rails via grid neighbours: the standard `GridController.World.GetCell(worldGrid)` + 6-neighbour walk covers world-grid structures only; rails live in a separate small-cell lattice addressed by `smallCell.Rail`.

## Class hierarchy
<!-- verified: 0.2.6228.27061 @ 2026-04-20 -->

```
Structure (Assets.Scripts.Objects.Structure)
  SmallGrid : Structure, ISmallGrid, ITooltip
    RoboticArmRailBase : SmallGrid, INetworkedRoboticArm, INetworkedStructure, ISmartRotatable
      RoboticArmRail : RoboticArmRailBase, IRoboticArmRail
        RoboticArmRailStraight : RoboticArmRail
        RoboticArmRailInnerCorner : RoboticArmRail
      RoboticArmJunction : RoboticArmRailBase, IRoboticArmJunction, IRoboticArmRail
    Device : SmallGrid, ILogicable, IPowered, ...
      SmallDevice : Device
        RoboticArmRailDeviceBase : SmallDevice, INetworkedRoboticArm, INetworkedStructure, ISmartRotatable
          RoboticArmBypass : RoboticArmRailDeviceBase, IRoboticArmBypass, IRoboticArmJunction, IRoboticArmRail
          RoboticArmDock : RoboticArmRailDeviceBase, IRoboticArmBypass, IRoboticArmJunction, IRoboticArmRail
            RoboticArmDockAtmos : RoboticArmDock, IThermal, IVolume
            RoboticArmDockCargo : RoboticArmDock, IProxySlot
            RoboticArmDockCollector : RoboticArmDock, IPhysical, IProfile, IDensePoolable, ICollector
            RoboticArmDockHydroponics : RoboticArmDock, IProxySlot
```

Verified by reading each class declaration line: `SmallGrid : Structure, ISmallGrid, ITooltip, IReferencable, IEvaluable` at `Assets/Scripts/Objects/SmallGrid.cs:19`; `Device : SmallGrid, ILogicable, ...` at `Assets/Scripts/Objects/Pipes/Device.cs:21`; `SmallDevice : Device` (abstract) at `Assets/Scripts/Objects/SmallDevice.cs:7`. Every rail-family class derives from `Structure`, NOT from `LargeStructure`. This is the single most important fact for SprayPaintPlus integration: the existing `PaintLargeStructureGrid` branch in `NetworkPainterPatch` does not catch rails, docks, junctions, or bypass.

**Paintability**: since every member descends from `Structure`, every member inherits `Structure.SetCustomColor(int index, bool emissive)` and the `CustomColor` / `PaintableMaterial` fields. Individual paintability hinges on `structureRenderMode == StructureRenderMode.Standard`; for the rail-family prefabs observed in vanilla this is the case (the player can paint any of them with a spray paint can).

## Complete family roster (enumerated)
<!-- verified: 0.2.6228.27061 @ 2026-04-20 -->

Decompile scan of `/Objects/RoboticArm/*.cs` for `class * : IRoboticArm*` or `class * : RoboticArm*` yielded exactly these concrete classes. No others exist in Assembly-CSharp at game version 0.2.6228.27061:

| Class | File | Base chain (to Structure) | Rail interfaces |
|---|---|---|---|
| `RoboticArmRailStraight` | `Objects/RoboticArm/RoboticArmRailStraight.cs` | `RoboticArmRail -> RoboticArmRailBase -> SmallGrid -> Structure` | `IRoboticArmRail` |
| `RoboticArmRailInnerCorner` | `Objects/RoboticArm/RoboticArmRailInnerCorner.cs` | `RoboticArmRail -> RoboticArmRailBase -> SmallGrid -> Structure` | `IRoboticArmRail` |
| `RoboticArmJunction` | `Objects/RoboticArm/RoboticArmJunction.cs` | `RoboticArmRailBase -> SmallGrid -> Structure` | `IRoboticArmJunction, IRoboticArmRail` |
| `RoboticArmBypass` | `Objects/RoboticArm/RoboticArmBypass.cs` | `RoboticArmRailDeviceBase -> SmallDevice -> Device -> SmallGrid -> Structure` | `IRoboticArmBypass, IRoboticArmJunction, IRoboticArmRail` |
| `RoboticArmDock` | `Objects/RoboticArm/RoboticArmDock.cs` | `RoboticArmRailDeviceBase -> SmallDevice -> Device -> SmallGrid -> Structure` | `IRoboticArmBypass, IRoboticArmJunction, IRoboticArmRail` |
| `RoboticArmDockAtmos` | `Objects/RoboticArm/RoboticArmDockAtmos.cs` | `RoboticArmDock -> ...` | inherited |
| `RoboticArmDockCargo` | `Objects/RoboticArm/RoboticArmDockCargo.cs` | `RoboticArmDock -> ...` | inherited |
| `RoboticArmDockCollector` | `Objects/RoboticArm/RoboticArmDockCollector.cs` | `RoboticArmDock -> ...` | inherited |
| `RoboticArmDockHydroponics` | `Objects/RoboticArm/RoboticArmDockHydroponics.cs` | `RoboticArmDock -> ...` | inherited |

The `RoboticArm` class itself (`Objects/RoboticArm/RoboticArm.cs`) is the traversing arm unit that slides along the rail; it is NOT a chain member and does NOT implement `IRoboticArmRail`. Painting "the assembly" does not include `RoboticArm` unless SprayPaintPlus explicitly opts in.

No intermediate "RoboticArmBase" / "RoboticArmMember" mixin interface exists. The lowest common denominator across every chain member is `IRoboticArmRail` (and therefore also `INetworkedRoboticArm` since `RoboticArmRailBase` and `RoboticArmRailDeviceBase` both implement it, but `INetworkedRoboticArm` is a member-of-network marker rather than a "thing on the chain" marker).

## Concrete rail structures (Assembly-CSharp vs prefab naming)
<!-- verified: 0.2.6228.27061 @ 2026-04-20 -->

Assembly-CSharp declares only two concrete `RoboticArmRail` subclasses, but `resources.assets` carries eight rail-only prefab variants (plus nine more across junctions/docks). The 8 "pure rail" prefabs all bind to one of the two rail classes; the prefab differences are mesh/transform only.

Confirmed via `UnityPy.load('resources.assets')` and GameObject-name filter on `Structure` + `RoboticArm`:

Rail-only prefabs (built by `ItemKitLinearRail`, confirmed 8 entries):

- `StructureRoboticArmRailStraight`
- `StructureRoboticArmRailStraightStop`
- `StructureRoboticArmRailCorner`
- `StructureRoboticArmRailCornerStop`
- `StructureRoboticArmRailInnerCorner`
- `StructureRoboticArmRailOuterCorner`
- `StructureRoboticArmRailScrewLeft`
- `StructureRoboticArmRailScrewRight`

Additional rail-graph prefabs with their own kits:

- `StructureRoboticArmDock` (base dock)
- `StructureLarreDockAtmos`
- `StructureLarreDockBypass`
- `StructureLarreDockCargo`
- `StructureLarreDockCollector`
- `StructureLarreDockHydroponics`

Plus junction variants if present (junction prefab not observed under the `StructureRoboticArm` prefix in the name scan, suggesting the junction is integrated into dock prefabs or uses a different name).

C# class binding by prefab-name suffix:

| Prefab suffix | Class |
|---|---|
| `RailStraight`, `RailStraightStop`, `RailCorner`, `RailCornerStop`, `RailOuterCorner`, `RailScrewLeft`, `RailScrewRight` | `RoboticArmRailStraight` |
| `RailInnerCorner` | `RoboticArmRailInnerCorner` (has dedicated behaviour in `RoboticArmNetwork.HandleInnerCorners`) |
| Dock variants | `RoboticArmDock` and its four subclasses |
| Bypass | `RoboticArmBypass` |

The mapping of "7 straight-style prefabs to the same `RoboticArmRailStraight` class" is inferred from the decompiled class declarations: `RoboticArmRailStraight` has zero body overrides (its file contains only `public class RoboticArmRailStraight : RoboticArmRail {}`), and `RoboticArmRailInnerCorner` is the only other rail subclass that exists. Prefab probing to confirm the exact MonoBehaviour class bound to each of the 8 prefabs was attempted but UnityPy's typetree failed on these MonoBehaviours (script resolution silently errored); the class roster is reliable from decompilation, and the per-prefab mapping assumption is consistent with the absence of any other `RoboticArmRail*` class.

## Connection model: SmallGrid, not world grid
<!-- verified: 0.2.6228.27061 @ 2026-04-20 -->

Key fact: rails occupy `SmallCell` positions, NOT `Cell` positions. The two lattices are separate; 6-neighbour walking on `Cell.NeighborCells` will never traverse a rail chain.

`SmallGrid` constants:

```csharp
public static float SmallGridSize = 0.5f;
public static float SmallGridOffset = 0.25f;
```

`SmallCell` carries a dedicated `Rail` reference (not just a generic structure list):

```csharp
// Assets.Scripts.GridSystem.SmallCell
public class SmallCell
{
    ...
    public IRoboticArmRail Rail;
    ...
}
```

`RoboticArmNetwork.ConnectedRoboticArm` walks the chain by sampling the small-cell lattice at a rail's `OpenEnd` world position:

```csharp
// Objects.RoboticArm.RoboticArmNetwork
private bool ConnectedRoboticArm(IRoboticArmRail current, Connection end,
    out IRoboticArmRail connected, out Connection connectedEnd)
{
    Grid3 localGrid = GridController.World.WorldToLocalGrid(
        end.Transform.position, SmallGrid.SmallGridSize, SmallGrid.SmallGridOffset);
    SmallCell smallCell = GridController.World.GetSmallCell(localGrid);
    if (smallCell == null) { connected = null; connectedEnd = null; return false; }
    if (smallCell.Rail != null && !smallCell.Rail.BeingDestroyed && smallCell.Rail != current
        && ((SmallGrid)smallCell.Rail).IsConnected(end, out var connectedEnd2))
    {
        connected = smallCell.Rail;
        connectedEnd = connectedEnd2;
        return true;
    }
    if (smallCell.Device != null && !smallCell.Device.BeingDestroyed && smallCell.Device != current
        && smallCell.Device.IsConnected(end, out var connectedEnd3)
        && smallCell.Device is IRoboticArmRail roboticArmRail)
    {
        connected = roboticArmRail;
        connectedEnd = connectedEnd3;
        return true;
    }
    connected = null; connectedEnd = null;
    return false;
}
```

For a connected flood-fill over a rail chain, the canonical walk is the network's own `RebuildNetwork` style BFS via `INetworkedStructure.ConnectedStructures()`.

## Network-level chain traversal
<!-- verified: 0.2.6228.27061 @ 2026-04-20 -->

Each rail piece reports connected neighbours via `RoboticArmRailBase.ConnectedStructures()`:

```csharp
public List<INetworkedStructure> ConnectedStructures()
{
    return ConnectedRoboticArms();
}
```

`ConnectedRoboticArms()` is inherited from `SmallGrid`/`Structure` plumbing and returns the list of rail-family pieces whose connections touch the current piece. The network itself is a `RoboticArmNetwork : StructureNetwork` instance referenced by each rail via `RoboticArmRailBase.RoboticArmNetwork`:

```csharp
public StructureNetwork StructureNetwork { get; set; }
public RoboticArmNetwork RoboticArmNetwork => StructureNetwork as RoboticArmNetwork;
```

`RoboticArmRailDeviceBase` (the parent of `RoboticArmBypass` and `RoboticArmDock`) declares the exact same two members verbatim; both base classes share the `StructureNetwork`/`RoboticArmNetwork` pair, so the accessor works uniformly from every family member.

`RoboticArmNetwork` maintains four parallel lists once `OnNetworkChanged` has walked the chain:

```csharp
// Objects.RoboticArm.RoboticArmNetwork
public readonly List<IRoboticArmJunction> JunctionList = new List<IRoboticArmJunction>();
public readonly List<RailNode>             RailNodeList = new List<RailNode>();
public readonly List<RoboticArmDock>       DockList     = new List<RoboticArmDock>();
public readonly List<IRoboticArmRail>      RailList     = new List<IRoboticArmRail>();
```

Critical fact re-confirmed against the `OnNetworkChanged` body: **`RailList` holds every single member of the chain, regardless of subclass**, because the walk adds `roboticArmRail` unconditionally. The typed lists (`DockList`, `JunctionList`) are filtered subsets populated inside the same loop:

```csharp
// Objects.RoboticArm.RoboticArmNetwork.OnNetworkChanged excerpt
RailList.Add(roboticArmRail);
if (roboticArmRail is RoboticArmDock roboticArmDock)
{
    roboticArmDock.Flipped = flag;
    DockList.Add(roboticArmDock);
}
if (roboticArmRail is IRoboticArmJunction roboticArmJunction)
{
    roboticArmJunction.JunctionIndex = JunctionList.Count;
    JunctionList.Add(roboticArmJunction);
}
```

Important: `RoboticArmBypass` and `RoboticArmDock` both implement `IRoboticArmJunction` (see interface section below), so the "junction" members of `JunctionList` include bypass and dock pieces in addition to the plain `RoboticArmJunction` class. `JunctionList` is not a pure "junction-only" filter; it is "every member that implements `IRoboticArmJunction`."

`RailList` is the authoritative superset in traversal order, starting from the "leftmost" rail discovered from the starting dock. For "paint every member connected to this one" this is the correct single collection: iterate `anyMember.RoboticArmNetwork?.RailList` and cast each entry to `Structure` for painting. No secondary list walk is needed.

`RailList` is rebuilt whenever the chain topology changes; consumers should re-fetch after any `OnNetworkChanged` cycle.

## IsConnected override: angular alignment matters
<!-- verified: 0.2.6228.27061 @ 2026-04-20 -->

Both `RoboticArmRailBase` and `RoboticArmRailDeviceBase` override `IsConnected` to add an angular-alignment check for rail-to-rail connections (world grid coincidence is not sufficient):

```csharp
public override bool IsConnected(Connection otherEnd)
{
    Grid3 facingGrid = otherEnd.GetFacingGrid();
    foreach (Connection openEnd in OpenEnds)
    {
        if ((openEnd.ConnectionType & otherEnd.ConnectionType) != NetworkType.None
            && !(facingGrid != openEnd.GetLocalGrid()))
        {
            if (!(otherEnd.Parent is IRoboticArmRail))
                return true;
            return ThreadedManager.IsThread
                ? RocketMath.CompareVectors(openEnd.TransformUp, otherEnd.TransformUp)
                : RocketMath.CompareVectors(openEnd.Transform.up, otherEnd.Transform.up);
        }
    }
    return false;
}
```

Two rail pieces only connect if their `Transform.up` vectors align. This is what prevents a rail chain from branching arbitrarily: the chain follows a single continuous "belt" orientation through each piece.

## Painting behaviour
<!-- verified: 0.2.6228.27061 @ 2026-04-20 -->

Rails inherit `PaintableMaterial` and `CustomColor` from `Structure`. Whether they actually accept paint depends on `Structure.structureRenderMode`: the base `SetCustomColor` throws `NotImplementedException` for any mode other than `Standard` (batched-mesh structures share a combined renderer and cannot be coloured per-instance):

```csharp
// Assets.Scripts.Objects.Structure.cs
public StructureRenderMode structureRenderMode;
...
public override void SetCustomColor(int index, bool emissive = false)
{
    if (structureRenderMode == StructureRenderMode.Standard)
    {
        base.SetCustomColor(index, emissive);
        ...
    }
    ...
}
```

Rail prefabs use Standard render mode in the vanilla game (the player can paint a rail individually with the current spray paint can), so `SetCustomColor` works. SprayPaintPlus already guards against the Batched exception in `PaintSafe`; rails will route through the catch only if a specific rail prefab's render mode is configured otherwise, which has not been observed.

## Interface hierarchy among chain members
<!-- verified: 0.2.6228.27061 @ 2026-04-20 -->

The three rail-family interfaces form a strict subset chain:

```csharp
// Objects.RoboticArm.IRoboticArmRail
public interface IRoboticArmRail : IReferencable, IEvaluable
{
    List<RailNode> RailNodes { get; }
    SmallCell SmallCell { get; set; }
    Vector3 Pivot { get; }
    SmallGrid AsSmallGrid { get; }
    Connection OtherEnd(Connection end);
    void RailNetworkUpdated();
}

// Objects.RoboticArm.IRoboticArmJunction
public interface IRoboticArmJunction : IRoboticArmRail, IReferencable, IEvaluable
{
    int JunctionIndex { get; set; }
}

// Objects.RoboticArm.IRoboticArmBypass
public interface IRoboticArmBypass : IRoboticArmJunction, IRoboticArmRail, IReferencable, IEvaluable
{
    Vector3 BypassPosition { get; }
    bool CanOpen { get; }
    bool CanClose { get; }
    void SetOpen(int state);
}
```

Subset relationships (via interface inheritance, verified by reading the three interface files):

- Every `IRoboticArmBypass` is an `IRoboticArmJunction` is an `IRoboticArmRail`.
- `RoboticArmJunction` implements `IRoboticArmJunction` (therefore `IRoboticArmRail`).
- `RoboticArmBypass` and `RoboticArmDock` implement `IRoboticArmBypass` (therefore `IRoboticArmJunction` and `IRoboticArmRail`).
- Plain rails (`RoboticArmRail` and subclasses) implement only `IRoboticArmRail`.

Consequence: a single `is IRoboticArmRail` test matches every chain member. Casting to `SmallGrid` via the `AsSmallGrid` getter or casting to `Structure` directly works for all members because every one of them is `Structure`-derived.

## Network ownership and enumeration entry points
<!-- verified: 0.2.6228.27061 @ 2026-04-20 -->

Two parallel ownership structures exist:

**1. Per-member back-reference.** Every chain member exposes `RoboticArmNetwork` as a property returning `StructureNetwork as RoboticArmNetwork`. This works from any starting point (rail, junction, bypass, dock):

```csharp
// RoboticArmRailBase AND RoboticArmRailDeviceBase both declare:
public StructureNetwork StructureNetwork { get; set; }
public RoboticArmNetwork RoboticArmNetwork => StructureNetwork as RoboticArmNetwork;
```

**2. Global static registry.** `RoboticArmNetwork` maintains a static list of all live networks, updated via `OnAssignedReference` / `OnDeregister`:

```csharp
// Objects.RoboticArm.RoboticArmNetwork
public static readonly List<RoboticArmNetwork> AllRoboticArmNetworks = new List<RoboticArmNetwork>();

public override void OnAssignedReference()
{
    base.OnAssignedReference();
    AllRoboticArmNetworks.Add(this);
}

protected override void OnDeregister()
{
    base.OnDeregister();
    AllRoboticArmNetworks.Remove(this);
}

public static long[] AllNetworkIds
{
    get
    {
        List<long> list = new List<long>();
        foreach (RoboticArmNetwork allRoboticArmNetwork in AllRoboticArmNetworks)
        {
            list.Add(allRoboticArmNetwork.ReferenceId);
        }
        return list.ToArray();
    }
}
```

**Canonical enumeration API for "every member of this chain, given any starting member":**

```csharp
// 'start' is any IRoboticArmRail / RoboticArmJunction / RoboticArmBypass / RoboticArmDock / RoboticArmDockAtmos etc.
RoboticArmNetwork network = ((INetworkedRoboticArm)start).RoboticArmNetwork;
if (network == null) return;  // piece was just placed and not yet networked
foreach (IRoboticArmRail member in network.RailList)
{
    Structure s = (Structure)member;   // safe: every IRoboticArmRail implementer is Structure-derived
    s.SetCustomColor(colorIndex);
}
```

`RailList` is the complete, ordered traversal of every chain member (rails + junctions + bypass + docks) as populated by `OnNetworkChanged`. One walk, no secondary lists needed.

Alternative (unordered) superset: `RoboticArmNetwork` inherits `StructureNetwork.StructureList` (a `List<INetworkedStructure>`) from `Networks.StructureNetwork`:

```csharp
// Networks.StructureNetwork
public List<INetworkedStructure> StructureList = new List<INetworkedStructure>();
```

This is populated by `RebuildNetwork` via BFS on `ConnectedStructures()`:

```csharp
protected override void RebuildNetwork(INetworkedStructure iNetworkedStructure, StructureNetwork oldNetwork)
{
    Queue<INetworkedStructure> queue = new Queue<INetworkedStructure>(iNetworkedStructure.ConnectedStructures());
    HashSet<INetworkedStructure> hashSet = new HashSet<INetworkedStructure>();
    while (queue.Count > 0)
    {
        INetworkedStructure networkedStructure = queue.Dequeue();
        if (hashSet.Contains(networkedStructure) || queue.Contains(networkedStructure) || networkedStructure.IsBeingDestroyed)
            continue;
        hashSet.Add(networkedStructure);
        foreach (INetworkedStructure item in networkedStructure.ConnectedStructures())
            if (!hashSet.Contains(item))
                queue.Enqueue(item);
        Add(networkedStructure);
    }
}
```

For painting the entire assembly, `RailList` is preferred over `StructureList` because it is the list the network itself considers canonical and is already filtered to `IRoboticArmRail` members; casting each entry to `Structure` is trivially safe. `StructureList` would require an extra `as IRoboticArmRail` check per element.

## Rail nodes (arm traversal, not painting)
<!-- verified: 0.2.6228.27061 @ 2026-04-20 -->

`IRoboticArmRail` exposes `RailNodes`, a list of `RailNode` control points that the arm slides between:

```csharp
public interface IRoboticArmRail : IReferencable, IEvaluable
{
    List<RailNode> RailNodes { get; }
    SmallCell SmallCell { get; set; }
    Vector3 Pivot { get; }
    SmallGrid AsSmallGrid { get; }
    Connection OtherEnd(Connection end);
    void RailNetworkUpdated();
}
```

Rail nodes are a motion-path abstraction used by `RoboticArmDock.MoveOnRail()`. They are NOT relevant to painting; painting cares about the `RailList` on the `RoboticArmNetwork` only.

## Verification history

- 2026-04-20: page creation. Class hierarchy and rail-family enumeration verified against decompiled Assembly-CSharp.dll in game version 0.2.6228.27061. Prefab name list verified via UnityPy scan of `resources.assets`.
- 2026-04-20: expansion pass. Added the complete family roster table, interface hierarchy section (`IRoboticArmRail` / `IRoboticArmJunction` / `IRoboticArmBypass`), network ownership and enumeration entry points (`RailList`, `AllRoboticArmNetworks` static list, `StructureNetwork.StructureList` fallback). Re-confirmed via direct read of `RoboticArmNetwork.OnNetworkChanged` that `RailList` is the complete membership set (every call `RailList.Add(roboticArmRail)` is unconditional; `DockList` / `JunctionList` are subsets filtered by `is`-checks inside the same loop). Re-confirmed against `RoboticArmRailBase.cs` and `RoboticArmRailDeviceBase.cs` that both bases declare the identical `RoboticArmNetwork` accessor, so traversal from any chain member works the same way. No prior claims contradicted; all additions are additive.

## Open questions

- Exact C# class binding for each of the 8 rail-only prefabs (e.g. does `StructureRoboticArmRailCorner` really use `RoboticArmRailStraight` or a third class that the decompile missed). UnityPy typetree extraction silently failed on these MonoBehaviours; resolving the m_Script PPtr per rail prefab would confirm the mapping. Functionally the distinction does not matter for a flood-fill painter, because every rail-family piece is reachable through `RoboticArmNetwork.RailList` regardless of its concrete class.
- Whether `StructureRoboticArmJunction` prefab exists under a name the scan did not cover, or the junction functionality is only ever built via dock prefabs. The `RoboticArmJunction` C# class exists and is instantiable, so a prefab should exist.
