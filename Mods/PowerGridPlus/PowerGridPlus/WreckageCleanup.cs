using System.Collections.Generic;
using Assets.Scripts;
using Assets.Scripts.GridSystem;
using Assets.Scripts.Networks;
using Assets.Scripts.Objects;
using Assets.Scripts.Objects.Electrical;
using Assets.Scripts.Util;
using UnityEngine;

namespace PowerGridPlus
{
    /// <summary>
    ///     One-shot load-time cleanup sweep (POWER.md decision 32 + its 2026-07-15 extension). Two
    ///     jobs, one pass over AllThings:
    ///
    ///     WRECKAGE: remove burnt-cable wreckage (<c>CableRuptured : SmallGrid</c>, the *Burnt
    ///     prefabs Break() leaves behind) that shares a small-grid cell with a LIVE cable of any
    ///     tier or with OTHER wreckage. Both shapes are corruption artifacts, not legitimate burn
    ///     feedback: pre-guard blueprint prints stamped cables into occupied cells and the old
    ///     enforcement Break()ed stacked cells, leaving wreckage lying on top of live wiring (the
    ///     Luna_mixedwire census found exactly this, including cells the player later rebuilt
    ///     through). A LONE wreckage piece in its own cell stays: that is the game's normal "a
    ///     cable burned here" cue the player clears by hand. Wreckage is not a <c>Cable</c>, so it
    ///     occupies no cable slot and is invisible to the network model: removing it can never
    ///     change electrical state.
    ///
    ///     SAME-TIER DUPLICATES: collapse two or more live cables of the SAME tier sharing one cell
    ///     (a blueprint-print artifact: the meshes overlap exactly so the player cannot see the
    ///     twin, vanilla occupancy makes the shape unbuildable by hand, and a vanilla same-tier
    ///     replace never persists both to disk). The cable the grid actually seats is kept (it is
    ///     the authoritative one; the twin is a grid-invisible ghost); with no seated member the
    ///     lowest ReferenceId wins for determinism. Without this, the ghost survives every load and
    ///     resurrects through the orphan re-seat repair when its visible twin is later deconstructed
    ///     or burned. DIFFERENT-tier stacks are deliberately not handled here: the tier enforcer's
    ///     stacked-theft repair seats the higher tier within the first ticks after load.
    ///
    ///     Runs once per world LOAD (armed by FaultRegistryLoadPatches; fresh worlds have no
    ///     corruption), host-only, marshalled to the main thread; destroys replicate to clients
    ///     through the normal channel. Matching keys on the anchor position in rounded decimeters
    ///     (cable positions serialize jitter-free on 0.5 m cell centers; multi-cell variants match
    ///     on their anchor only, the same documented limitation the stack repair has).
    ///
    ///     Player messaging: one plain-text console line per removed piece up to a small cap, then a
    ///     single remainder line, so a heavily corrupted save cannot spam the console.
    /// </summary>
    internal static class WreckageCleanup
    {
        private const int AnnounceCap = 6;

        private static bool _armed;

        /// <summary>World-load arm (FaultRegistryLoadPatches, main thread, load start).</summary>
        internal static void Arm() => _armed = true;

        /// <summary>
        ///     Tick-head consumption (power worker): marshal the sweep to the main thread once the
        ///     simulation is running. Retries next tick while the dispatcher is not up yet.
        /// </summary>
        internal static void RunOnceAfterLoad()
        {
            if (!_armed) return;
            if (!GameManager.RunSimulation) { _armed = false; return; }   // clients never sweep
            if (!UnityMainThreadDispatcher.Exists()) return;              // keep armed; retry next tick
            _armed = false;
            UnityMainThreadDispatcher.Instance().Enqueue(Sweep);
        }

        private static void Sweep()
        {
            if (!GameManager.RunSimulation || GameManager.GameState != GameState.Running) return;

            var wrecksByCell = new Dictionary<(int x, int y, int z), List<CableRuptured>>();
            var cablesByCell = new Dictionary<(int x, int y, int z), List<Cable>>();
            OcclusionManager.AllThings.ForEach(t =>
            {
                if (t == null || t.IsBeingDestroyed) return;
                if (t is CableRuptured wreck)
                {
                    var key = Key(wreck.ThingTransformPosition);
                    if (!wrecksByCell.TryGetValue(key, out var list))
                        wrecksByCell[key] = list = new List<CableRuptured>(1);
                    list.Add(wreck);
                }
                else if (t is Cable cable)
                {
                    var key = Key(cable.ThingTransformPosition);
                    if (!cablesByCell.TryGetValue(key, out var list))
                        cablesByCell[key] = list = new List<Cable>(1);
                    list.Add(cable);
                }
            });

            int removed = 0;
            int announced = 0;
            foreach (var pair in wrecksByCell)
            {
                bool overLive = cablesByCell.ContainsKey(pair.Key);
                if (!overLive && pair.Value.Count < 2) continue;   // lone wreckage: legitimate cue, keep
                foreach (var wreck in pair.Value)
                {
                    if (wreck == null || wreck.IsBeingDestroyed) continue;
                    string reason = overLive ? "stacked with a live cable" : "stacked with other wreckage";
                    Plugin.Log?.LogInfo(
                        $"[PowerGridPlus] Wreckage cleanup: removing burnt cable wreckage at {VoltageTier.Coords(wreck)} ({reason}).");
                    if (announced < AnnounceCap)
                    {
                        PlayerConsole.Broadcast(
                            $"Cleaned up burnt cable wreckage at {VoltageTier.Coords(wreck)}: it was {reason}");
                        announced++;
                    }
                    OnServer.Destroy(wreck);
                    removed++;
                }
            }

            foreach (var pair in cablesByCell)
            {
                var cell = pair.Value;
                if (cell.Count < 2) continue;
                for (int i = 0; i < cell.Count; i++)
                {
                    var first = cell[i];
                    if (first == null || first.IsBeingDestroyed) continue;
                    List<Cable> group = null;
                    for (int j = i + 1; j < cell.Count; j++)
                    {
                        var other = cell[j];
                        if (other == null || other.IsBeingDestroyed) continue;
                        if (other.CableType != first.CableType) continue;
                        (group ?? (group = new List<Cable> { first })).Add(other);
                        cell[j] = null;   // consumed into this group; never anchors a group of its own
                    }
                    if (group == null) continue;
                    Cable keeper = null;
                    foreach (var c in group)
                        if (c.SmallCell != null && ReferenceEquals(c.SmallCell.Cable, c)) { keeper = c; break; }
                    if (keeper == null)
                    {
                        // No member holds the seat (a different-tier thief may; the enforcer's
                        // repair owns that conflict). Lowest ReferenceId wins for determinism.
                        keeper = group[0];
                        foreach (var c in group)
                            if (c.ReferenceId < keeper.ReferenceId) keeper = c;
                    }
                    foreach (var c in group)
                    {
                        if (ReferenceEquals(c, keeper)) continue;
                        Plugin.Log?.LogInfo(
                            $"[PowerGridPlus] Load cleanup: removing hidden duplicate {c.CableType} cable at {VoltageTier.Coords(c)} (stacked with an identical cable).");
                        if (announced < AnnounceCap)
                        {
                            PlayerConsole.Broadcast(
                                $"Cleaned up a hidden duplicate {VoltageTier.TierWord(c.CableType)} cable at {VoltageTier.Coords(c)}: it was stacked with an identical cable");
                            announced++;
                        }
                        OnServer.Destroy(c);
                        removed++;
                    }
                }
            }

            if (removed > announced)
                PlayerConsole.Broadcast(
                    $"Cleaned up {removed - announced} more stacked cables or wreckage pieces");
        }

        private static (int, int, int) Key(Vector3 p)
            => (Mathf.RoundToInt(p.x * 10f), Mathf.RoundToInt(p.y * 10f), Mathf.RoundToInt(p.z * 10f));
    }
}
