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

                case "battery-efficiency-probe":
                    Scenario_BatteryEfficiencyProbe();
                    return;

                case "apc-idle-probe":
                    Scenario_ApcIdleProbe();
                    return;

                case "cable-burn-probe":
                    Scenario_CableBurnProbe();
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

        // ---- Scenario: battery-efficiency-probe ----
        //
        // Directly exercises PowerGridPlus's `Battery.ReceivePower` prefix patch
        // (StationaryBatteryPatches.ChargeEfficiencyControl). The patch math is:
        //   charged = BatteryChargeEfficiency * powerAdded
        //   if (charged < 500) charged = powerAdded   // sub-500 W trickle floor
        //   PowerStored += charged (clamped)
        // The probe fires the first scenario tick only, against the first OnOff
        // Battery in the scene. It calls ReceivePower with TWO well-chosen powerAdded
        // values and logs PowerStored before/after each, so an agent can diff against
        // the expected delta for the configured BatteryChargeEfficiency.
        //
        // Run twice across two -Start cycles, once with BatteryChargeEfficiency=1.0
        // and once with 0.5; the two log lines compared show the patch is firing.

        private static bool _bepProbeFired;

        private static void Scenario_BatteryEfficiencyProbe()
        {
            if (_bepProbeFired) return;
            _bepProbeFired = true;

            if (_batteries.Count == 0) RebuildCaches();
            Battery target = null;
            foreach (var b in _batteries)
            {
                if (b != null && b.OnOff && b.InputNetwork != null)
                {
                    // Prefer one with headroom so the clamp at PowerMaximum does not eat the delta.
                    if (b.PowerStored + 5000f <= b.PowerMaximum)
                    {
                        target = b;
                        break;
                    }
                }
            }
            if (target == null)
            {
                _log?.LogWarning("[PgpVerifyHelper] efficiency-probe: no OnOff Battery with headroom + InputNetwork; nothing to probe.");
                return;
            }

            _log?.LogInfo(
                $"[PgpVerifyHelper] efficiency-probe target: ref={target.ReferenceId} prefab={target.PrefabName} " +
                $"PowerStored={target.PowerStored:F2} PowerMaximum={target.PowerMaximum:F2} " +
                $"BatteryChargeEfficiency_setting={Settings_BatteryChargeEfficiency()}");

            // Probe A: large powerAdded (5000 W) above the 500 W trickle floor.
            //   Expected at eff=1.0: delta = 5000.
            //   Expected at eff=0.5: delta = 2500.
            var beforeA = target.PowerStored;
            target.ReceivePower(target.InputNetwork, 5000f);
            var afterA = target.PowerStored;
            _log?.LogInfo(
                $"[PgpVerifyHelper] efficiency-probe A: powerAdded=5000 before={beforeA:F2} after={afterA:F2} delta={afterA - beforeA:F2}");

            // Probe B: small powerAdded (200 W) BELOW the 500 W trickle floor.
            //   Expected at any eff: delta = 200 (trickle floor: charged = powerAdded).
            var beforeB = target.PowerStored;
            target.ReceivePower(target.InputNetwork, 200f);
            var afterB = target.PowerStored;
            _log?.LogInfo(
                $"[PgpVerifyHelper] efficiency-probe B: powerAdded=200  before={beforeB:F2} after={afterB:F2} delta={afterB - beforeB:F2}");
        }

        // The probe needs to read PowerGridPlus's `BatteryChargeEfficiency` value to
        // print it in the log line. Reach for it via reflection so PgpVerifyHelper
        // does not need a build-time dependency on PowerGridPlus.
        private static double Settings_BatteryChargeEfficiency()
        {
            try
            {
                var asm = System.AppDomain.CurrentDomain.GetAssemblies()
                    .FirstOrDefault(a => a.GetName().Name == "PowerGridPlus");
                if (asm == null) return double.NaN;
                var settingsType = asm.GetType("PowerGridPlus.Settings");
                if (settingsType == null) return double.NaN;
                var field = settingsType.GetField("BatteryChargeEfficiency",
                    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
                var configEntry = field?.GetValue(null);
                if (configEntry == null) return double.NaN;
                var valueProp = configEntry.GetType().GetProperty("Value");
                var v = valueProp?.GetValue(configEntry);
                return v is float f ? (double)f : double.NaN;
            }
            catch { return double.NaN; }
        }

        // ---- Scenario: apc-idle-probe ----
        //
        // Logs each AreaPowerControl's attached `Battery.PowerStored` (PGP's APC
        // power-leak fix protects this on idle output networks) every 5 ticks across
        // the whole run. Run for ~60 seconds (12 snapshots per APC). Diff the first
        // and last value for every APC; if PGP's `AreaPowerControlPatches` is doing
        // its job, an APC with no downstream draw should NOT decrease its battery.
        //
        // The Luna save has 16 APCs; some are upstream-of-load, some are isolated.
        // Filter the log output to APCs whose `_powerProvided` was zero on the
        // first tick (no downstream draw observed) and check that THEIR
        // Battery.PowerStored stayed constant across the snapshot window.

        private static int _apcLastLogTick = int.MinValue;
        private const int APC_LOG_EVERY_TICKS = 5;

        private static void Scenario_ApcIdleProbe()
        {
            if (_ticksSeen - _apcLastLogTick < APC_LOG_EVERY_TICKS) return;
            _apcLastLogTick = (int)_ticksSeen;

            if (_apcs.Count == 0) RebuildCaches();
            foreach (var a in _apcs)
            {
                if (a == null) continue;
                var bat = a.Battery;
                var stored = bat != null ? bat.PowerStored : float.NaN;
                var pmax = bat != null ? bat.PowerMaximum : float.NaN;
                _log?.LogInfo(
                    $"[PgpVerifyHelper] APC tick={_ticksSeen} ref={a.ReferenceId} " +
                    $"prefab={a.PrefabName} OnOff={a.OnOff} UsedPower={a.UsedPower} " +
                    $"BatteryStored={stored:F2} BatteryMax={pmax:F2}");
            }
        }

        // ---- Scenario: cable-burn-probe ----
        //
        // The vanilla and PGP cable burn checks both gate on `powerUsed > MaxVoltage`.
        // To verify that PGP burns a normal cable when its network actually does
        // sustained >5 kW throughput, we need a CableNetwork whose RequiredLoad and
        // PotentialLoad both exceed 5000 W on a normal-cable spur. Luna does not
        // naturally have one (its normal-cable networks all idle <500 W).
        //
        // This scenario logs every CableNetwork's RequiredLoad / CurrentLoad /
        // PotentialLoad once at first tick + at second probe (every 25 ticks
        // thereafter), with the weakest cable's MaxVoltage. Use it to confirm or
        // refute "any network on this save sees >5 kW sustained": if none does,
        // the cable-burn sub-check is fundamentally blocked on scene construction
        // and the appropriate next step is to build a dedicated minimal save.

        private static int _cbpLastLogTick = int.MinValue;
        private const int CBP_LOG_EVERY_TICKS = 25;

        private static void Scenario_CableBurnProbe()
        {
            if (_ticksSeen - _cbpLastLogTick < CBP_LOG_EVERY_TICKS) return;
            _cbpLastLogTick = (int)_ticksSeen;

            // Pass 1: observe what's actually loaded.
            int overloaded = 0;
            CableNetwork.AllCableNetworks.ForEach(n =>
            {
                if (n == null) return;
                var req = n.RequiredLoad;
                var cur = n.CurrentLoad;
                if (req > 5000f || cur > 5000f)
                {
                    overloaded++;
                    _log?.LogInfo(
                        $"[PgpVerifyHelper] CBP tick={_ticksSeen} netRef={n.ReferenceId} " +
                        $"Required={req:F2} Current={cur:F2} Potential={n.PotentialLoad:F2}");
                }
            });
            _log?.LogInfo($"[PgpVerifyHelper] CBP tick={_ticksSeen} summary: {overloaded} networks at >5kW (Required or Current)");

            // Pass 2: directly exercise PowerGridTick.TestBurnCable via reflection with a
            // synthetic powerUsed=10000 (2x the normal-cable MaxVoltage of 5000 W) so the
            // burn-decision formula returns a Cable IFF a normal-cable network exists. This
            // does not actually burn anything (we discard the returned Cable; Break() is not
            // called). It verifies the burn-decision math in isolation from the natural
            // simulation, since Luna's networks all idle below the 5 kW threshold.
            if (_ticksSeen == _delayTicks + CBP_LOG_EVERY_TICKS)  // probe once
            {
                ProbeTestBurnCableViaReflection();
            }
        }

        private static void ProbeTestBurnCableViaReflection()
        {
            try
            {
                var asm = System.AppDomain.CurrentDomain.GetAssemblies()
                    .FirstOrDefault(a => a.GetName().Name == "PowerGridPlus");
                if (asm == null) { _log?.LogWarning("[PgpVerifyHelper] CBP: PowerGridPlus assembly not loaded; skipping reflection probe."); return; }
                var tickType = asm.GetType("PowerGridPlus.Power.PowerGridTick");
                if (tickType == null) { _log?.LogWarning("[PgpVerifyHelper] CBP: PowerGridPlus.Power.PowerGridTick type not found."); return; }
                var testBurnCable = tickType.GetMethod("TestBurnCable", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public);
                if (testBurnCable == null) { _log?.LogWarning("[PgpVerifyHelper] CBP: TestBurnCable method not found."); return; }

                int probedNets = 0;
                int wouldBurn = 0;
                int normalCableNets = 0;
                int heavyCableNets = 0;
                int superHeavyCableNets = 0;

                CableNetwork.AllCableNetworks.ForEach(n =>
                {
                    if (n == null) return;
                    var pt = n.PowerTick;
                    if (pt == null || pt.GetType() != tickType) return;
                    probedNets++;

                    // Best effort: classify by min MaxVoltage across CableList.
                    float minMaxVoltage = float.PositiveInfinity;
                    if (n.CableList != null)
                    {
                        foreach (var c in n.CableList)
                        {
                            if (c == null) continue;
                            if (c.MaxVoltage < minMaxVoltage) minMaxVoltage = c.MaxVoltage;
                        }
                    }
                    if (minMaxVoltage == 5000f) normalCableNets++;
                    else if (minMaxVoltage == 100000f) heavyCableNets++;
                    else if (minMaxVoltage == 500000f) superHeavyCableNets++;

                    // Synthetic 10 kW into a normal-cable network produces burnChance = 1.0;
                    // _rng.NextDouble() >= 1.0 is always false so TestBurnCable always returns
                    // a Cable when the cable list has any cables and the network's weakest
                    // cable rating is <= 5 kW. (Super-heavy networks return null per NEW-1.)
                    var result = testBurnCable.Invoke(pt, new object[] { 10000.0f, 10000.0f });
                    if (result != null) wouldBurn++;
                });

                _log?.LogInfo(
                    $"[PgpVerifyHelper] CBP reflection probe: probedNets={probedNets} " +
                    $"wouldBurn={wouldBurn} normalNets={normalCableNets} heavyNets={heavyCableNets} " +
                    $"superHeavyNets={superHeavyCableNets} (synthetic powerUsed=10000 W against TestBurnCable)");
            }
            catch (System.Exception e)
            {
                _log?.LogError($"[PgpVerifyHelper] CBP reflection probe threw: {e.Message}");
            }
        }
    }
}
