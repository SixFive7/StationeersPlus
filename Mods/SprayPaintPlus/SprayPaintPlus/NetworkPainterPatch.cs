using Assets.Scripts;
using Assets.Scripts.GridSystem;
using Assets.Scripts.Networking;
using Assets.Scripts.Objects;
using Assets.Scripts.Objects.Electrical;
using Assets.Scripts.Objects.Pipes;
using Assets.Scripts.Objects.Structures;
using Objects.RoboticArm;
using HarmonyLib;
using JetBrains.Annotations;
using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace SprayPaintPlus
{
    /// <summary>
    /// Captures the painting player's Human ReferenceId when a local paint action
    /// (host or single-player) reaches OnServer.AttackWith. The attackParent arg
    /// is the player's controlled Entity (a Human).
    /// </summary>
    [HarmonyPatch(typeof(OnServer), nameof(OnServer.AttackWith))]
    public class PaintAttackerTracker_Local
    {
        [UsedImplicitly]
        public static void Prefix(Thing attackParent)
        {
            if (attackParent != null)
                SprayPaintHelpers.CurrentPaintingHumanId = attackParent.ReferenceId;
        }

        [UsedImplicitly]
        public static void Postfix()
        {
            SprayPaintHelpers.CurrentPaintingHumanId = -1;
        }
    }

    /// <summary>
    /// Captures the painting player's Human ReferenceId when the server processes
    /// a remote client's AttackWithMessage. The hostId parameter from vanilla
    /// message dispatch is unreliable on the server (NetworkManager._hostId is
    /// only maintained client-side); AttackParentId in the message body is the
    /// authoritative source identifier.
    /// </summary>
    [HarmonyPatch(typeof(AttackWithMessage), nameof(AttackWithMessage.Process))]
    public class PaintAttackerTracker_Remote
    {
        [UsedImplicitly]
        public static void Prefix(AttackWithMessage __instance)
        {
            SprayPaintHelpers.CurrentPaintingHumanId = __instance.AttackParentId;
        }

        [UsedImplicitly]
        public static void Postfix()
        {
            SprayPaintHelpers.CurrentPaintingHumanId = -1;
        }
    }

    /// <summary>
    /// Prefix on OnServer.SetCustomColor. Paints entire pipe/cable/chute networks.
    /// Looks up the painter's modifier state from PlayerModifiers using the
    /// Human ReferenceId captured by the trackers above.
    /// </summary>
    [HarmonyPatch(typeof(OnServer), nameof(OnServer.SetCustomColor))]
    public class NetworkPainterPatch
    {
        private static bool _painting;

        [UsedImplicitly]
        public static void Prefix(Thing thing, int colorIndex)
        {
            if (_painting)
                return;

            // The authoritative paint runs on the server and is broadcast back.
            // Running this prefix on a remote client would only repaint the
            // network locally via Thing.SetCustomColor, which does not set
            // NetworkUpdateFlags on clients, so it would be purely cosmetic
            // and invisible to other players.
            if (NetworkManager.IsActive && !NetworkManager.IsServer)
                return;

            if (!SprayPaintPlusPlugin.EnableNetworkPainting.Value)
                return;

            long humanId = SprayPaintHelpers.CurrentPaintingHumanId;

            // Read-and-reset guards against a stale id leaking into a later
            // OnServer.SetCustomColor from a non-attack path (UI color picker,
            // etc.) if an attack threw before its tracker postfix fired.
            SprayPaintHelpers.CurrentPaintingHumanId = -1;

            SprayPaintHelpers.PlayerModifiers.TryGetValue(humanId, out byte modifiers);
            bool wantsSingle = (modifiers & 1) != 0;
            bool ctrlHeld = (modifiers & 2) != 0;

            if (wantsSingle)
                return;

            _painting = true;
            try
            {
                PaintNetwork(thing, colorIndex, ctrlHeld);
            }
            finally
            {
                _painting = false;
            }
        }

        private static void PaintNetwork(Thing thing, int colorIndex, bool checkered)
        {
            if (SprayPaintPlusPlugin.NetworkPaintPipes.Value)
            {
                if (thing is HydroponicTray tray && tray.PipeNetwork?.StructureList != null)
                {
                    foreach (Pipe item in tray.PipeNetwork.StructureList.ToList())
                    {
                        // Fix #5b: Skip the original thing; vanilla paints it after the Prefix
                        if (ReferenceEquals(item, thing))
                            continue;
                        if (item is HydroponicTray && (!checkered || CheckeredCheck(thing, item)))
                            PaintSafe(item, colorIndex);
                    }
                    return;
                }

                if (thing is PassiveVent pv && pv.PipeNetwork?.StructureList != null)
                {
                    foreach (Pipe item in pv.PipeNetwork.StructureList.ToList())
                    {
                        if (ReferenceEquals(item, thing))
                            continue;
                        if (item is PassiveVent && (!checkered || CheckeredCheck(thing, item)))
                            PaintSafe(item, colorIndex);
                    }
                    return;
                }

                if (thing is Pipe pipe && pipe.PipeNetwork?.StructureList != null)
                {
                    foreach (Pipe item in pipe.PipeNetwork.StructureList.ToList())
                    {
                        if (ReferenceEquals(item, thing))
                            continue;
                        if (!(item is PassiveVent) && !(item is HydroponicTray)
                            && (!checkered || CheckeredCheck(thing, item)))
                            PaintSafe(item, colorIndex);
                    }
                    return;
                }
            }

            if (SprayPaintPlusPlugin.NetworkPaintCables.Value)
            {
                if (thing is Cable cable && cable.CableNetwork?.CableList != null)
                {
                    foreach (Cable item in cable.CableNetwork.CableList.ToList())
                    {
                        if (ReferenceEquals(item, thing))
                            continue;
                        if (!checkered || CheckeredCheck(thing, item))
                            PaintSafe(item, colorIndex);
                    }
                    return;
                }
            }

            if (SprayPaintPlusPlugin.NetworkPaintChutes.Value)
            {
                if (thing is Chute chute && chute.ChuteNetwork?.StructureList != null)
                {
                    foreach (Chute item in chute.ChuteNetwork.StructureList.ToList())
                    {
                        if (ReferenceEquals(item, thing))
                            continue;
                        if (!checkered || CheckeredCheck(thing, item))
                            PaintSafe(item, colorIndex);
                    }
                    return;
                }
            }

            // RoboticArmNetwork.RailList holds every member of the assembly:
            // rail pieces plus junctions, bypass, and docks. One walk paints
            // the whole loop. INetworkedRoboticArm is the network-accessor
            // interface implemented by every rail-family base class.
            if (SprayPaintPlusPlugin.NetworkPaintRails.Value)
            {
                if (thing is INetworkedRoboticArm armMember && armMember.RoboticArmNetwork?.RailList != null)
                {
                    foreach (IRoboticArmRail item in armMember.RoboticArmNetwork.RailList.ToList())
                    {
                        if (!(item is Structure s))
                            continue;
                        if (ReferenceEquals(s, thing))
                            continue;
                        if (checkered && !CheckeredCheck(thing, s))
                            continue;
                        PaintSafe(s, colorIndex);
                    }
                    return;
                }
            }

            // ElevatorShaftNetwork.Shafts holds every shaft + level segment of
            // one elevator (ElevatorLevel derives from ElevatorShaft, so the
            // single list covers both). With-cable and without-cable build
            // variants share these classes, so one walk covers every variant.
            // The carriage (network.Carrage, a DynamicThing) is deliberately
            // left out of the flood: it is a separate movable object, painted
            // on its own. Only shaft/level seeds flood; painting the carriage
            // directly falls through to a plain single-item paint.
            if (SprayPaintPlusPlugin.NetworkPaintElevators.Value
                && thing is ElevatorShaft elevatorShaft && elevatorShaft.ShaftNetwork != null)
            {
                PaintElevatorNetwork(elevatorShaft, elevatorShaft.ShaftNetwork, colorIndex, checkered);
                return;
            }

            // Ladders are SmallGrid structures (Ladder, plus LadderEnd caps) on the
            // 0.5 m small grid, not the large Cell grid, so the large-structure
            // flood never catches them; they need their own small-grid walk.
            if (SprayPaintPlusPlugin.NetworkPaintLadders.Value && thing is Ladder ladderSeed)
            {
                PaintLadderRun(ladderSeed, colorIndex, checkered);
                return;
            }

            // Stairs are plain Structures (not LargeStructure), linked into runs by
            // their Entry/Exit grid points rather than orthogonal adjacency.
            // `is Stairs` covers any stairwell prefab (a Stairs subclass).
            if (thing is Stairs stairsSeed)
            {
                // Stairwells (the eight passthrough/door variants) are a different
                // structure than angled stair flights: each floods by its own rules
                // under its own toggle.
                if (IsStairwell(stairsSeed))
                {
                    if (SprayPaintPlusPlugin.NetworkPaintStairwells.Value)
                        PaintStairwellRun(stairsSeed, colorIndex, checkered);
                }
                else if (SprayPaintPlusPlugin.NetworkPaintStairs.Value)
                {
                    PaintStairsRun(stairsSeed, colorIndex, checkered);
                }
                return;
            }

            // Wall branch must precede the LargeStructure branch because Wall
            // derives from LargeStructure. Walls flood by shared Room, not grid
            // adjacency. A wall with walls-painting disabled is *not* forwarded
            // to the grid flood, because walls would be painted anyway via
            // the LargeStructure path.
            if (thing is Wall wall)
            {
                if (SprayPaintPlusPlugin.NetworkPaintWalls.Value)
                    PaintWallsInRoom(wall, colorIndex, checkered);
                return;
            }

            if (SprayPaintPlusPlugin.NetworkPaintLargeStructures.Value && thing is LargeStructure largeStructure)
            {
                PaintLargeStructureGrid(largeStructure, colorIndex, checkered);
                return;
            }
        }

        private static void PaintWallsInRoom(Wall originalWall, int colorIndex, bool checkered)
        {
            Room targetRoom = GetRoomFor(originalWall);
            if (targetRoom == null)
                return;

            // Filter by PrefabHash, not GetType(). Visual wall variants
            // (Wall vs Wall Flat vs Wall Arched vs Wall Iron, etc.) all share
            // the same Wall C# class, so a GetType() filter would treat them
            // as equivalent and flood across visual variants. PrefabHash is
            // per-prefab and keeps the variants separate.
            int targetPrefabHash = originalWall.PrefabHash;

            // The room's interior cells are in room.Grids; walls sit on the
            // boundary, so expand one layer to cover both sides.
            var scanned = new HashSet<Cell>();
            foreach (WorldGrid wg in targetRoom.Grids)
            {
                Cell c = GridController.World?.GetCell(wg);
                if (c != null)
                    scanned.Add(c);
            }
            foreach (Cell seed in scanned.ToList())
            {
                foreach (Cell n in seed.NeighborCells)
                {
                    if (n != null && IsOrthogonalNeighbor(seed, n))
                        scanned.Add(n);
                }
            }

            foreach (Cell cell in scanned)
            {
                foreach (Structure s in cell.AllStructures)
                {
                    if (s == null || s.PrefabHash != targetPrefabHash)
                        continue;
                    if (ReferenceEquals(s, originalWall))
                        continue;
                    if (GetRoomFor(s) != targetRoom)
                        continue;
                    if (checkered && !CheckeredCheckGrid(originalWall, s))
                        continue;
                    PaintSafe(s, colorIndex);
                }
            }
        }

        private static void PaintLargeStructureGrid(LargeStructure origin, int colorIndex, bool checkered)
        {
            Cell startCell = GridController.World?.GetCell(origin.GridPosition);
            if (startCell == null)
                return;

            Type targetType = origin.GetType();
            var visited = new HashSet<Cell> { startCell };
            var queue = new Queue<Cell>();
            queue.Enqueue(startCell);

            while (queue.Count > 0)
            {
                Cell cell = queue.Dequeue();

                foreach (Structure s in cell.AllStructures)
                {
                    if (s == null || s.GetType() != targetType)
                        continue;
                    if (ReferenceEquals(s, origin))
                        continue;
                    if (checkered && !CheckeredCheckGrid(origin, s))
                        continue;
                    PaintSafe(s, colorIndex);
                }

                foreach (Cell neighbor in cell.NeighborCells)
                {
                    if (neighbor == null || visited.Contains(neighbor))
                        continue;
                    if (!IsOrthogonalNeighbor(cell, neighbor))
                        continue;
                    if (!CellContainsType(neighbor, targetType))
                        continue;
                    visited.Add(neighbor);
                    queue.Enqueue(neighbor);
                }
            }
        }

        /// <summary>
        /// Paints every shaft and level segment of one elevator. The carriage is
        /// deliberately excluded (see the call site): it is a separate movable
        /// object the player paints on its own.
        /// Elevator segments stack vertically several world units apart, so the
        /// world-position CheckeredCheck (tuned for 0.5-unit pipe/cable spacing)
        /// never flips parity between neighbours and the checkered pattern would
        /// collapse to a full flood. The checker therefore alternates by the
        /// segment's vertical order, keyed off the seed's own segment so the seed
        /// level always paints.
        /// </summary>
        private static void PaintElevatorNetwork(ElevatorShaft seed, ElevatorShaftNetwork network, int colorIndex, bool checkered)
        {
            List<ElevatorShaft> shafts = network.Shafts
                .Where(s => s != null)
                .OrderBy(s => s.GridPosition.y)
                .ToList();

            // Parity anchor is the seed's own segment so the seed level always
            // paints. IndexOf is -1 only in degenerate cases (seed missing from
            // the list); fall back to 0 so we still alternate cleanly.
            int anchorIdx = shafts.IndexOf(seed);
            if (anchorIdx < 0)
                anchorIdx = 0;

            for (int i = 0; i < shafts.Count; i++)
            {
                ElevatorShaft item = shafts[i];
                if (ReferenceEquals(item, seed))
                    continue;
                if (checkered && (((i - anchorIdx) & 1) != 0))
                    continue;
                PaintSafe(item, colorIndex);
            }
        }

        // The small grid is 0.5 m; Grid3 keys are world metres x 10, so one small
        // cell is 5 Grid3 units. Verified from live SmallCell.SmallGrid keys: two
        // ladders 2 m apart differ by exactly 20 in the key (4 small cells).
        private const int SmallCellKeyStep = 5;

        // A ladder occupies several small cells and the next rung's anchor sits one
        // 2 m pitch (4 small cells) away. Scan exactly one pitch: a directly-adjacent
        // rung is reached, but a full missing rung (two pitches off) is not, so a gap
        // in the column breaks it.
        private const int LadderScanCells = 4;

        // Ladders connect only to the ladder directly above or directly below in
        // the same column (and same facing, checked below). Ladders to the side are
        // a separate run, so the scan is vertical only.
        private static readonly Grid3[] SmallGridDirs =
        {
            new Grid3(0, 1, 0), new Grid3(0, -1, 0),
        };

        /// <summary>
        /// Paints a connected run of ladders. Ladders register on the 0.5 m small
        /// grid (one SmallCell each, in the Other slot) with no network object. We
        /// walk piece to piece in small-grid KEY space, not world space: the seed's
        /// own registered key (origin.SmallCell.SmallGrid) is exact, and we step it
        /// by whole small cells (SmallCellKeyStep) along each axis, scanning across
        /// empty cells up to LadderScanCells so the vertical pitch is not hard-coded.
        /// Probing world positions instead fails: SmallGrid.CenterPosition carries a
        /// 0.2 m forward offset and the key anchors off Position, so a world-space
        /// probe snaps into the wrong cell. Other-as-Ladder matches LadderEnd caps.
        /// </summary>
        private static void PaintLadderRun(Ladder origin, int colorIndex, bool checkered)
        {
            GridController world = GridController.World;
            if (world == null || origin.SmallCell == null)
                return;

            var visited = new HashSet<Ladder> { origin };
            var run = new List<Structure> { origin };
            var queue = new Queue<Ladder>();
            queue.Enqueue(origin);

            while (queue.Count > 0)
            {
                Ladder current = queue.Dequeue();
                if (current.SmallCell == null)
                    continue;
                Grid3 key = current.SmallCell.SmallGrid;

                foreach (Grid3 dir in SmallGridDirs)
                {
                    for (int step = 1; step <= LadderScanCells; step++)
                    {
                        Grid3 probe = key + dir * (SmallCellKeyStep * step);
                        Ladder neighbor = world.GetSmallCell(probe)?.Other as Ladder;
                        if (neighbor == null)
                            continue;               // empty sub-cell within one pitch: keep scanning
                        if (ReferenceEquals(neighbor, current))
                            continue;               // our own multi-cell footprint: scan past it
                        // first distinct ladder within one pitch (a full missing rung is
                        // two pitches away and so is never reached): connect if it is the
                        // same type and facing, then stop this axis either way.
                        if (Vector3.Dot(neighbor.Forward, origin.Forward) > 0.9f
                            && visited.Add(neighbor))
                        {
                            run.Add(neighbor);          // same facing; matches LadderEnd caps too (LadderEnd : Ladder)
                            queue.Enqueue(neighbor);
                        }
                        break;
                    }
                }
            }

            PaintRunByHeight(origin, run, colorIndex, checkered);
        }

        /// <summary>
        /// Paints a connected run of angled stair flights. Stairwell prefabs flood
        /// separately (see PaintStairwellRun); this path only runs for a flight with
        /// a real climb (non-zero Entry/Exit). Pieces connect when they share the same
        /// prefab and facing and sit in a widening (side by side) or lengthening (one
        /// step up or down the run) relationship; StairsConnect makes that call. That
        /// distinguishes a continuous staircase from a separate one beside it, facing
        /// another way, or crossing it.
        /// </summary>
        private static void PaintStairsRun(Stairs origin, int colorIndex, bool checkered)
        {
            var visited = new HashSet<Stairs> { origin };
            var run = new List<Structure> { origin };
            var queue = new Queue<Stairs>();
            queue.Enqueue(origin);

            while (queue.Count > 0)
            {
                Stairs current = queue.Dequeue();
                foreach (Stairs neighbor in StairNeighbors(current))
                {
                    if (neighbor == null || !visited.Add(neighbor))
                        continue;
                    run.Add(neighbor);
                    queue.Enqueue(neighbor);
                }
            }

            // A staircase spans a 2D plane (width x climb), so the checker is a true
            // checkerboard on that plane, not the 1D height-index alternation used for
            // single-file runs like ladders and elevators (which would stripe here).
            foreach (Structure m in run)
            {
                if (ReferenceEquals(m, origin))
                    continue;
                if (checkered && !StairCheckerSameColour(origin, (Stairs)m))
                    continue;
                PaintSafe(m, colorIndex);
            }
        }

        /// <summary>
        /// Checkerboard colour for a stair piece relative to the seed, across the plane
        /// the staircase spans: one axis is the width (side-by-side widening), the other
        /// is the climb (one level per lengthening step). Parity of (widthSteps +
        /// levelSteps) gives a true 2D checker, so width neighbours and climb neighbours
        /// are the opposite colour while diagonals match. True = shares the seed's
        /// colour (so it is painted under the checkered modifier).
        /// </summary>
        private static bool StairCheckerSameColour(Stairs seed, Stairs piece)
        {
            Grid3 d = piece.GridPosition - seed.GridPosition;
            Vector3 f = seed.Forward;
            bool runIsZ = Mathf.Abs(f.z) >= Mathf.Abs(f.x);
            int cell = Grid3.one.x * 2;                   // one 2 m cell in Grid3 units
            int widthSteps = (runIsZ ? d.x : d.z) / cell; // across the width axis
            int levelSteps = d.y / cell;                  // up the climb, one level per step
            return ((widthSteps + levelSteps) & 1) == 0;
        }

        private static bool IsStairwell(Stairs s)
        {
            // The eight stairwell variants are vertical pass/door pieces with no climb,
            // so the game leaves Entry/Exit unset; angled flights set them.
            return IsZero(s.Entry) && IsZero(s.Exit);
        }

        /// <summary>
        /// Floods a block of stairwells. Stairwells are a separate structure from
        /// angled flights: any of the eight variants, in any orientation, count as
        /// connected when spatially adjacent. So this is a plain cell flood over every
        /// adjacent stairwell with a simple 3D checkerboard, independent of the stair
        /// widening / lengthening logic.
        /// </summary>
        private static void PaintStairwellRun(Stairs origin, int colorIndex, bool checkered)
        {
            GridController world = GridController.World;
            Cell startCell = world?.GetCell(origin.GridPosition);
            if (startCell == null)
                return;

            var visitedCells = new HashSet<Cell> { startCell };
            var cellQueue = new Queue<Cell>();
            cellQueue.Enqueue(startCell);
            var seen = new HashSet<Stairs>();

            while (cellQueue.Count > 0)
            {
                Cell cell = cellQueue.Dequeue();
                foreach (Structure s in cell.AllStructures)
                {
                    if (!(s is Stairs sw) || !IsStairwell(sw) || !seen.Add(sw))
                        continue;
                    if (ReferenceEquals(sw, origin))
                        continue;
                    if (checkered && !CheckeredCheckGrid(origin, sw))
                        continue;
                    PaintSafe(sw, colorIndex);
                }

                // Expand to every neighbouring cell holding a stairwell. NeighborCells is
                // the full 26-cell set, so adjacency counts in any direction; an empty
                // cell stops the flood, so a gap separates two blocks.
                foreach (Cell n in cell.NeighborCells)
                {
                    if (n == null || visitedCells.Contains(n) || !CellHasStairwell(n))
                        continue;
                    visitedCells.Add(n);
                    cellQueue.Enqueue(n);
                }
            }
        }

        private static bool CellHasStairwell(Cell cell)
        {
            foreach (Structure s in cell.AllStructures)
                if (s is Stairs sw && IsStairwell(sw))
                    return true;
            return false;
        }

        // A flight spans several cells, and its lengthwise neighbour sits ~4 m (2
        // cells) away, so gather candidates from a small cube around the seed and
        // keep only those in a valid run relationship.
        private const int StairScanCells = 2;

        private static IEnumerable<Stairs> StairNeighbors(Stairs s)
        {
            GridController world = GridController.World;
            if (world == null)
                yield break;

            Grid3 baseKey = s.GridPosition;
            int step = Grid3.one.x * 2;          // one 2 m cell in Grid3 units
            var seen = new HashSet<Stairs>();

            for (int dx = -StairScanCells; dx <= StairScanCells; dx++)
                for (int dy = -StairScanCells; dy <= StairScanCells; dy++)
                    for (int dz = -StairScanCells; dz <= StairScanCells; dz++)
                    {
                        Cell c = world.GetCell(new Grid3(baseKey.x + dx * step, baseKey.y + dy * step, baseKey.z + dz * step));
                        if (c == null)
                            continue;
                        foreach (Structure st in c.AllStructures)
                        {
                            if (!(st is Stairs t) || ReferenceEquals(t, s))
                                continue;
                            if (t.PrefabHash != s.PrefabHash || Vector3.Dot(t.Forward, s.Forward) < 0.9f)
                                continue;
                            if (!seen.Add(t))
                                continue;
                            if (StairsConnect(s, t))
                                yield return t;
                        }
                    }
        }

        /// <summary>
        /// True when two same-prefab, same-facing angled flights belong to one
        /// staircase: either side by side at the same level (widening), or one
        /// run-step along the facing axis with a coupled level change in the direction
        /// the flight ascends (lengthening). Pieces adjacent in any other way (crossing
        /// runs, or a flight hovering a level above) do not connect. Stairwells never
        /// reach this method; they flood separately under their own path.
        /// </summary>
        private static bool StairsConnect(Stairs s, Stairs t)
        {
            Vector3 d = t.Position - s.Position;
            Vector3 f = s.Forward;
            bool runIsZ = Mathf.Abs(f.z) >= Mathf.Abs(f.x);
            float dRun = runIsZ ? d.z : d.x;     // along the facing axis (run length)
            float dWidth = runIsZ ? d.x : d.z;   // across the facing axis (width)
            float dy = d.y;
            const float tol = 0.5f;

            // Widening: same level, directly to the side, aligned along the run.
            if (Mathf.Abs(dy) < tol && Mathf.Abs(dRun) < tol && Mathf.Abs(dWidth) > tol)
                return true;

            // Lengthening: a run-step along the facing axis with a coupled level
            // change, in the same sense the flight ascends (Exit is above Entry).
            if (Mathf.Abs(dWidth) < tol && Mathf.Abs(dy) > tol && Mathf.Abs(dRun) > tol)
            {
                // Run direction and one-flight rise taken straight from the Grid3
                // ports (Exit/Entry are world x10). Dividing the rise by Grid3.one
                // puts oneLevel in the same world scale as dy, independent of any
                // Grid3.ToVector3 scaling.
                int runDir = runIsZ ? (s.Exit.z - s.Entry.z) : (s.Exit.x - s.Entry.x);
                float oneLevel = Mathf.Abs(s.Exit.y - s.Entry.y) / (float)Grid3.one.y;
                // A single lengthening step is exactly one level up/down. A piece two
                // levels up over the same run (d = (0,+4,+4)) is a different flight
                // hovering above, not the next step, so it must NOT connect.
                if (runDir != 0 && oneLevel > tol
                    && Mathf.Abs(Mathf.Abs(dy) - oneLevel) < tol)
                {
                    bool up = Mathf.Sign(dRun) == Mathf.Sign((float)runDir) && dy > 0f;
                    bool down = Mathf.Sign(dRun) == -Mathf.Sign((float)runDir) && dy < 0f;
                    return up || down;
                }
            }
            return false;
        }

        private static bool IsZero(Grid3 g)
        {
            return g.x == 0 && g.y == 0 && g.z == 0;
        }

        /// <summary>
        /// Checker helper for vertical runs (ladders, stairs). As with elevators, a
        /// world-position or grid-parity check degrades when pieces sit an even
        /// number of cells apart, so alternate by each piece's order along the run
        /// (sorted by height, then x/z), keyed off the seed so the seed always paints.
        /// </summary>
        private static void PaintRunByHeight(Structure seed, List<Structure> members, int colorIndex, bool checkered)
        {
            List<Structure> ordered = members
                .Where(m => m != null)
                .OrderBy(m => m.GridPosition.y)
                .ThenBy(m => m.GridPosition.x)
                .ThenBy(m => m.GridPosition.z)
                .ToList();

            int anchorIdx = ordered.IndexOf(seed);
            if (anchorIdx < 0)
                anchorIdx = 0;

            for (int i = 0; i < ordered.Count; i++)
            {
                Structure item = ordered[i];
                if (ReferenceEquals(item, seed))
                    continue;
                if (checkered && (((i - anchorIdx) & 1) != 0))
                    continue;
                PaintSafe(item, colorIndex);
            }
        }

        /// <summary>
        /// Replicates Structure.GetRoom() (which is protected) via public APIs.
        /// </summary>
        private static Room GetRoomFor(Structure s)
        {
            if (s == null)
                return null;
            return RoomController.World?.GetRoom(s.GridPosition);
        }

        /// <summary>
        /// Cell.NeighborCells contains all 26 surrounding cells (includeCorners:true
        /// in the Cell ctor). We want 6-orthogonal only: exactly one grid axis
        /// differs between the two cells.
        /// </summary>
        private static bool IsOrthogonalNeighbor(Cell a, Cell b)
        {
            Grid3 d = a.Grid - b.Grid;
            int axes = (d.x != 0 ? 1 : 0) + (d.y != 0 ? 1 : 0) + (d.z != 0 ? 1 : 0);
            return axes == 1;
        }

        private static bool CellContainsType(Cell cell, Type targetType)
        {
            foreach (Structure s in cell.AllStructures)
            {
                if (s != null && s.GetType() == targetType)
                    return true;
            }
            return false;
        }

        /// <summary>
        /// Individual SetCustomColor calls can throw. Most notably,
        /// Structure.SetCustomColor throws NotImplementedException on any
        /// structure whose structureRenderMode != Standard (batched-render
        /// structures share a combined mesh and can't be recolored per
        /// instance). A destroyed-mid-paint item can also trip a null deref.
        /// Without the catch, one unpaintable or stale item would abort
        /// painting the rest of the network.
        /// </summary>
        private static void PaintSafe(Thing item, int colorIndex)
        {
            try
            {
                item.SetCustomColor(colorIndex);
            }
            catch (Exception e)
            {
                SprayPaintPlusPlugin.Log.LogWarning(
                    $"Failed to paint {item?.ReferenceId}: {e.Message}");
            }
        }

        /// <summary>
        /// 3D checkerboard pattern. Cast Mathf.Round to int before modulo
        /// to avoid float modulo imprecision.
        /// </summary>
        private static bool CheckeredCheck(Thing original, Thing target)
        {
            int one = ((int)Mathf.Round(Mathf.Abs(original.Position.x) * 2) % 2)
                   == ((int)Mathf.Round(Mathf.Abs(target.Position.x) * 2) % 2) ? 1 : 0;
            int two = ((int)Mathf.Round(Mathf.Abs(original.Position.y) * 2) % 2)
                   == ((int)Mathf.Round(Mathf.Abs(target.Position.y) * 2) % 2) ? 1 : 0;
            int three = ((int)Mathf.Round(Mathf.Abs(original.Position.z) * 2) % 2)
                     == ((int)Mathf.Round(Mathf.Abs(target.Position.z) * 2) % 2) ? 1 : 0;
            return (one + two + three) % 2 != 0;
        }

        /// <summary>
        /// 3D checkerboard parity for grid-aligned structures. Two traps make
        /// the naive `(x+y+z) & 1` on GridPosition useless here:
        ///   1. Grid3 scales world coords x Grid3.one (10), so one world unit
        ///      is ten Grid3 units.
        ///   2. Walls and large structures snap to a GridSize-wide cell grid
        ///      (default 2 world units). One cell therefore spans
        ///      GridSize * Grid3.one Grid3 units (20 by default), and every
        ///      structure's GridPosition is a multiple of 20 (+ a fixed
        ///      offset). Parity on raw coords is always the same value.
        /// Working from the delta between the two positions sidesteps both
        /// the scale and the grid offset: the delta is always an exact
        /// multiple of cellSize, so integer division yields the cell-index
        /// distance and its parity is the checker answer.
        /// </summary>
        private static bool CheckeredCheckGrid(Structure original, Structure target)
        {
            int cellSize = Grid3.one.x * (int)original.GridSize;
            Grid3 d = target.GridPosition - original.GridPosition;
            int cellsApart = d.x / cellSize + d.y / cellSize + d.z / cellSize;
            return (cellsApart & 1) == 0;
        }
    }
}
