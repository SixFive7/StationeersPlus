using System;
using System.Collections.Generic;
using System.Linq;
using Assets.Scripts;
using Assets.Scripts.Networks;
using Assets.Scripts.Objects;
using Assets.Scripts.Objects.Electrical;
using BepInEx.Logging;
using UnityEngine;

namespace PgpVerifyHelper
{
    /// <summary>
    ///     Drives scenarios from the simulation tick. State is global; one instance per server run.
    ///     Pumped from <see cref="ElectricityTickHook"/>.
    ///
    ///     Adding a new scenario:
    ///       1. Add a case to <see cref="Tick"/> matching the scenario id.
    ///       2. Document the id in <c>Plans/PgpVerifyHelper/README.md</c>.
    ///
    ///     Anything that mutates world state (turning devices on or off, setting a transformer
    ///     `Setting`, writing logic values) belongs in a scenario method here. Anything that only
    ///     reads state should also live here so the output is a single, traceable log line per
    ///     scenario tick: a snapshot taken at a known offset from a scenario action is far easier
    ///     to interpret than a snapshot taken at an unknown moment.
    /// </summary>
    internal static class ScenarioRunner
    {
        private static ManualLogSource _log;
        private static string _scenario = "";
        private static int _delayTicks;
        private static bool _logInventoryOnFirstTick;
        private static long _ticksSeen;
        private static bool _firstTickFired;

        public static void Initialize(ManualLogSource log, string scenario, int delayTicks, bool logInventoryOnFirstTick)
        {
            _log = log;
            _scenario = scenario ?? "";
            _delayTicks = Mathf.Max(0, delayTicks);
            _logInventoryOnFirstTick = logInventoryOnFirstTick;
            _ticksSeen = 0;
            _firstTickFired = false;
        }

        public static void OnElectricityTick()
        {
            _ticksSeen++;
            if (_ticksSeen < _delayTicks) return;

            try
            {
                if (!_firstTickFired)
                {
                    _firstTickFired = true;
                    if (_logInventoryOnFirstTick)
                        LogInventory();
                }

                Tick();
            }
            catch (Exception e)
            {
                _log?.LogError($"[PgpVerifyHelper] scenario '{_scenario}' tick threw: {e}");
            }
        }

        private static void Tick()
        {
            if (string.IsNullOrEmpty(_scenario)) return;

            switch (_scenario)
            {
                case "inventory":
                    // Already covered by LogInventory on first tick; nothing to do per-tick.
                    return;

                case "battery-charge-snapshot":
                    Scenario_BatteryChargeSnapshot();
                    return;

                case "transformer-conservation":
                    Scenario_TransformerConservation();
                    return;

                default:
                    if (_ticksSeen == _delayTicks)
                        _log?.LogWarning($"[PgpVerifyHelper] unknown scenario '{_scenario}'; doing nothing.");
                    return;
            }
        }

        // ---- Scenario: inventory ----

        // Cached snapshots of Things gathered via Thing.AllThings. We avoid
        // UnityEngine.Object.FindObjectsOfType from the ElectricityTick postfix
        // because that hook runs on a UniTask ThreadPool worker (see
        // Cysharp.Threading.Tasks.SwitchToThreadPoolAwaitable in the call stack)
        // and FindObjectsOfType is Unity-main-thread-only; calling it off-thread
        // crashes the engine native side intermittently. Thing.AllThings is a
        // ConcurrentDensePool<Thing> and is safe to iterate from a worker.
        private static readonly List<Battery> _batteries = new List<Battery>();
        private static readonly List<Transformer> _transformers = new List<Transformer>();
        private static readonly List<AreaPowerControl> _apcs = new List<AreaPowerControl>();
        private static readonly List<CableFuse> _fuses = new List<CableFuse>();

        private static void RebuildCaches()
        {
            _batteries.Clear();
            _transformers.Clear();
            _apcs.Clear();
            _fuses.Clear();
            // OcclusionManager.AllThings is a ConcurrentDensePool<Thing> at decompile line
            // 199822; safe to iterate from a UniTask ThreadPool worker (unlike
            // UnityEngine.Object.FindObjectsOfType).
            OcclusionManager.AllThings.ForEach(t =>
            {
                if (t == null) return;
                if (t is Battery b) _batteries.Add(b);
                if (t is Transformer x) _transformers.Add(x);
                if (t is AreaPowerControl a) _apcs.Add(a);
                if (t is CableFuse f) _fuses.Add(f);
            });
        }

        private static void LogInventory()
        {
            RebuildCaches();
            var cableNetCount = CableNetwork.AllCableNetworks.ActiveCount;

            _log?.LogInfo(
                $"[PgpVerifyHelper] inventory @ tick {_ticksSeen}: " +
                $"Battery={_batteries.Count}, Transformer={_transformers.Count}, " +
                $"AreaPowerControl={_apcs.Count}, CableNetwork={cableNetCount}, " +
                $"CableFuse={_fuses.Count}");

            // Per-tier breakdown of batteries (useful when MorePowerMod nuclear cells are present).
            var byType = _batteries.GroupBy(b => b.GetType().Name).OrderByDescending(g => g.Count());
            foreach (var g in byType)
            {
                _log?.LogInfo($"[PgpVerifyHelper]   {g.Key}: {g.Count()}");
            }
        }

        // ---- Scenario: battery-charge-snapshot ----
        //
        // Logs PowerStored / PowerMaximum / Mode for every Battery, once per tick.
        // Used to compute charge-rate or efficiency deltas over a window. The agent
        // greps the server log for these lines and computes the delta offline.

        private static int _bcsLastLogTick = int.MinValue;
        private const int BCS_LOG_EVERY_TICKS = 5;

        private static void Scenario_BatteryChargeSnapshot()
        {
            if (_ticksSeen - _bcsLastLogTick < BCS_LOG_EVERY_TICKS) return;
            _bcsLastLogTick = (int)_ticksSeen;

            if (_batteries.Count == 0) RebuildCaches();
            foreach (var b in _batteries)
            {
                if (b == null) continue;
                _log?.LogInfo(
                    $"[PgpVerifyHelper] BCS tick={_ticksSeen} ref={b.ReferenceId} " +
                    $"prefab={b.PrefabName} OnOff={b.OnOff} Mode={b.Mode} " +
                    $"PowerStored={b.PowerStored:F2} PowerMaximum={b.PowerMaximum:F2}");
            }
        }

        // ---- Scenario: transformer-conservation ----
        //
        // For every transformer, log Setting / _powerProvided / UsedPower plus the
        // InputNetwork.CurrentLoad and OutputNetwork.CurrentLoad at the same moment so
        // an agent can verify input draw equals output throughput + UsedPower.

        private static int _tcLastLogTick = int.MinValue;
        private const int TC_LOG_EVERY_TICKS = 5;

        private static void Scenario_TransformerConservation()
        {
            if (_ticksSeen - _tcLastLogTick < TC_LOG_EVERY_TICKS) return;
            _tcLastLogTick = (int)_ticksSeen;

            if (_transformers.Count == 0) RebuildCaches();
            foreach (var t in _transformers)
            {
                if (t == null) continue;
                var inLoad = t.InputNetwork != null ? t.InputNetwork.CurrentLoad : float.NaN;
                var outLoad = t.OutputNetwork != null ? t.OutputNetwork.CurrentLoad : float.NaN;
                _log?.LogInfo(
                    $"[PgpVerifyHelper] TC tick={_ticksSeen} ref={t.ReferenceId} " +
                    $"prefab={t.PrefabName} OnOff={t.OnOff} Setting={t.Setting} " +
                    $"UsedPower={t.UsedPower} InCurrentLoad={inLoad} OutCurrentLoad={outLoad}");
            }
        }
    }
}
