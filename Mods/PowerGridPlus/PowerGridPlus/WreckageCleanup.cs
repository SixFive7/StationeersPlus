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
    ///     One-shot load-time wreckage sweep (POWER.md decision 32): remove burnt-cable wreckage
    ///     (<c>CableRuptured : SmallGrid</c>, the *Burnt prefabs Break() leaves behind) that shares a
    ///     small-grid cell with a LIVE cable of any tier or with OTHER wreckage. Both shapes are
    ///     corruption artifacts, not legitimate burn feedback: pre-guard blueprint prints stamped
    ///     cables into occupied cells and the old enforcement Break()ed stacked cells, leaving
    ///     wreckage lying on top of live wiring (the Luna_mixedwire census found exactly this,
    ///     including cells the player later rebuilt through). A LONE wreckage piece in its own cell
    ///     stays: that is the game's normal "a cable burned here" cue the player clears by hand.
    ///
    ///     Wreckage is not a <c>Cable</c>, so it occupies no cable slot and is invisible to the
    ///     network model: removing it can never change electrical state, which is why this sweep is
    ///     unconditionally safe. Runs once per world LOAD (armed by FaultRegistryLoadPatches; fresh
    ///     worlds have no wreckage), host-only, marshalled to the main thread; destroys replicate to
    ///     clients through the normal channel. Matching keys on the anchor position in rounded
    ///     decimeters (cable positions serialize jitter-free on 0.5 m cell centers; multi-cell burnt
    ///     variants match on their anchor only, the same documented limitation the stack repair has).
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
            var liveCableCells = new HashSet<(int x, int y, int z)>();
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
                    liveCableCells.Add(Key(cable.ThingTransformPosition));
                }
            });

            int removed = 0;
            int announced = 0;
            foreach (var pair in wrecksByCell)
            {
                bool overLive = liveCableCells.Contains(pair.Key);
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
            if (removed > announced)
                PlayerConsole.Broadcast(
                    $"Cleaned up {removed - announced} more burnt cable wreckage pieces stacked with live wiring");
        }

        private static (int, int, int) Key(Vector3 p)
            => (Mathf.RoundToInt(p.x * 10f), Mathf.RoundToInt(p.y * 10f), Mathf.RoundToInt(p.z * 10f));
    }
}
