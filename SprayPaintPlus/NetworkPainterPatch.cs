using Assets.Scripts;
using Assets.Scripts.GridSystem;
using Assets.Scripts.Networking;
using Assets.Scripts.Objects;
using Assets.Scripts.Objects.Electrical;
using Assets.Scripts.Objects.Pipes;
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
    /// Prefix on OnServer.SetCustomColor — paints entire pipe/cable/chute networks.
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
            // NetworkUpdateFlags on clients — so it would be purely cosmetic
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
                        // Fix #5b: Skip the original thing — vanilla paints it after the Prefix
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

            // Wall branch must precede the LargeStructure branch because Wall
            // derives from LargeStructure. Walls flood by shared Room, not grid
            // adjacency. A wall with walls-painting disabled is *not* forwarded
            // to the grid flood — otherwise walls would be painted anyway via
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

            Type targetType = originalWall.GetType();

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
                    if (n != null)
                        scanned.Add(n);
                }
            }

            foreach (Cell cell in scanned)
            {
                foreach (Structure s in cell.AllStructures.ToList())
                {
                    if (s == null || s.GetType() != targetType)
                        continue;
                    if (ReferenceEquals(s, originalWall))
                        continue;
                    if (GetRoomFor(s) != targetRoom)
                        continue;
                    if (checkered && !CheckeredCheck(originalWall, s))
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

                foreach (Structure s in cell.AllStructures.ToList())
                {
                    if (s == null || s.GetType() != targetType)
                        continue;
                    if (ReferenceEquals(s, origin))
                        continue;
                    if (checkered && !CheckeredCheck(origin, s))
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
        /// Replicates Structure.GetRoom() (which is protected) via public APIs.
        /// </summary>
        private static Room GetRoomFor(Structure s)
        {
            if (s == null)
                return null;
            return GridController.World?.RoomController?.GetRoom(s.GridPosition);
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
        /// Fix #8b: Wraps individual SetCustomColor calls so one destroyed item
        /// doesn't abort painting the rest of the network.
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
    }
}
