using System;
using System.Collections.Generic;
using System.Linq;
using Assets.Scripts;
using Assets.Scripts.Networks;
using Assets.Scripts.Objects;
using Assets.Scripts.Objects.Electrical;
using BepInEx.Logging;
using UnityEngine;

namespace ScenarioRunner
{
    /// <summary>
    ///     Drives scenarios from the simulation tick. State is global; one instance per
    ///     server run. Pumped from <see cref="SimTickPump"/>.
    ///
    ///     Two kinds of scenarios:
    ///       - General: work against vanilla types without depending on any specific mod.
    ///         The framework just reads / iterates them. Examples: 'inventory',
    ///         'battery-charge-snapshot'.
    ///       - Mod-specific: exercise patched code paths on a specific mod (PowerGridPlus
    ///         today). Prefix with the mod tag (e.g. 'pgp-' for PowerGridPlus) so it's
    ///         clear which mod must be loaded. If the mod is absent, the scenario logs a
    ///         warning and no-ops.
    ///
    ///     Adding a new scenario:
    ///       1. Add a case to <see cref="Tick"/> matching the scenario id.
    ///       2. Add a Scenario_* method.
    ///       3. Document the id in <c>README.md</c>.
    ///
    ///     For mod-specific scenarios, gate on
    ///     <see cref="IsAssemblyLoaded"/>(assemblyName) and log + return if the mod is
    ///     absent. Use reflection (no build-time dependency) to reach the mod's
    ///     internals; <see cref="GetModType"/> / <see cref="GetModInstanceField"/> are
    ///     starting points.
    /// </summary>
    internal static class Dispatcher
    {
        private static ManualLogSource _log;
        private static string _scenario = "";
        private static int _delayTicks;
        private static bool _logInventoryOnFirstTick;
        private static long _ticksSeen;
        private static int _lastTickFrame = -1;
        private static bool _firstTickFired;

        public static void Initialize(ManualLogSource log, string scenario, int delayTicks, bool logInventoryOnFirstTick)
        {
            _log = log;
            _scenario = scenario ?? "";
            _delayTicks = Mathf.Max(0, delayTicks);
            _logInventoryOnFirstTick = logInventoryOnFirstTick;
            _ticksSeen = 0;
            _lastTickFrame = -1;
            _firstTickFired = false;
            _batteries.Clear();
            _transformers.Clear();
            _apcs.Clear();
            _fuses.Clear();
        }

        /// <summary>
        ///     Called from every pumping hook (see <see cref="SimTickPump"/>).
        ///     Deduplicates by <see cref="Time.frameCount"/> so multiple hooks
        ///     converge to one scenario tick per simulation frame.
        /// </summary>
        public static void OnSimTick()
        {
            int frame = Time.frameCount;
            if (frame == _lastTickFrame) return;
            _lastTickFrame = frame;

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
                _log?.LogError($"[ScenarioRunner] scenario '{_scenario}' tick threw: {e}");
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

                case "pgp-transformer-conservation":
                    Scenario_PgpTransformerConservation();
                    return;

                case "pgp-battery-efficiency-probe":
                    Scenario_PgpBatteryEfficiencyProbe();
                    return;

                case "pgp-apc-idle-probe":
                    Scenario_PgpApcIdleProbe();
                    return;

                case "pgp-cable-burn-probe":
                    Scenario_PgpCableBurnProbe();
                    return;

                default:
                    if (_ticksSeen == _delayTicks)
                        _log?.LogWarning($"[ScenarioRunner] unknown scenario '{_scenario}'; doing nothing.");
                    return;
            }
        }

        // ---- General: cached per-type Thing lists ----
        //
        // Battery / Transformer / AreaPowerControl / CableFuse caches gathered via
        // OcclusionManager.AllThings (a ConcurrentDensePool<Thing>). We avoid
        // UnityEngine.Object.FindObjectsOfType<T> from the simulation-tick worker
        // because that hook runs on a UniTask ThreadPool worker (see
        // Cysharp.Threading.Tasks.SwitchToThreadPoolAwaitable in the call stack of
        // GameManager.GameTick) and FindObjectsOfType is Unity-main-thread-only;
        // calling it off-thread crashes the engine native side intermittently.
        // OcclusionManager.AllThings is safe to iterate from any thread.
        // See Research/Patterns/ThingEnumerationOffMainThread.md.

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
            OcclusionManager.AllThings.ForEach(t =>
            {
                if (t == null) return;
                if (t is Battery b) _batteries.Add(b);
                if (t is Transformer x) _transformers.Add(x);
                if (t is AreaPowerControl a) _apcs.Add(a);
                if (t is CableFuse f) _fuses.Add(f);
            });
        }

        // ---- General scenario: inventory ----

        private static void LogInventory()
        {
            RebuildCaches();
            var cableNetCount = CableNetwork.AllCableNetworks.ActiveCount;

            _log?.LogInfo(
                $"[ScenarioRunner] inventory @ tick {_ticksSeen}: " +
                $"Battery={_batteries.Count}, Transformer={_transformers.Count}, " +
                $"AreaPowerControl={_apcs.Count}, CableNetwork={cableNetCount}, " +
                $"CableFuse={_fuses.Count}");

            // Per-concrete-type breakdown of batteries (useful when subclasses are loaded,
            // e.g. MorePowerMod's StationBatteryNuclear).
            var byType = _batteries.GroupBy(b => b.GetType().Name).OrderByDescending(g => g.Count());
            foreach (var g in byType)
            {
                _log?.LogInfo($"[ScenarioRunner]   {g.Key}: {g.Count()}");
            }
        }

        // ---- General scenario: battery-charge-snapshot ----
        //
        // Every BCS_LOG_EVERY_TICKS ticks, log PowerStored / PowerMaximum / OnOff / Mode
        // for every Battery. Useful for any rate / efficiency delta diff over a window.
        // Works without PowerGridPlus.

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
                    $"[ScenarioRunner] BCS tick={_ticksSeen} ref={b.ReferenceId} " +
                    $"prefab={b.PrefabName} OnOff={b.OnOff} Mode={b.Mode} " +
                    $"PowerStored={b.PowerStored:F2} PowerMaximum={b.PowerMaximum:F2}");
            }
        }

        // ---- Reflection helpers for mod-specific scenarios ----

        private static System.Reflection.Assembly GetModAssembly(string assemblyName)
        {
            return AppDomain.CurrentDomain.GetAssemblies()
                .FirstOrDefault(a => a.GetName().Name == assemblyName);
        }

        private static bool RequireModAssembly(string assemblyName, string scenarioId)
        {
            if (GetModAssembly(assemblyName) != null) return true;
            if (_ticksSeen == _delayTicks)
                _log?.LogWarning($"[ScenarioRunner] scenario '{scenarioId}' requires mod assembly '{assemblyName}' to be loaded; skipping.");
            return false;
        }

        // ---- PowerGridPlus-specific scenarios ----
        //
        // These require the 'PowerGridPlus' assembly to be loaded. They reach into PGP
        // via reflection (no build-time dependency) so this plugin stays independent.

        private const string PGP_ASSEMBLY = "PowerGridPlus";

        // PGP scenario: transformer-conservation.
        // For every Transformer, log Setting / UsedPower / InputNetwork.CurrentLoad /
        // OutputNetwork.CurrentLoad so an agent can verify
        //   InputNetwork.CurrentLoad ~= OutputNetwork.CurrentLoad + UsedPower.
        // The four fields read here are vanilla; PGP's exploit-mitigation patch
        // affects the values, not the field set. Strictly speaking this scenario does
        // not depend on PGP being loaded -- but it is specifically aimed at verifying
        // PGP's TransformerExploitPatches behaviour, so we tag it pgp-.

        private static int _tcLastLogTick = int.MinValue;
        private const int TC_LOG_EVERY_TICKS = 5;

        private static void Scenario_PgpTransformerConservation()
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
                    $"[ScenarioRunner] TC tick={_ticksSeen} ref={t.ReferenceId} " +
                    $"prefab={t.PrefabName} OnOff={t.OnOff} Setting={t.Setting} " +
                    $"UsedPower={t.UsedPower} InCurrentLoad={inLoad} OutCurrentLoad={outLoad}");
            }
        }

        // PGP scenario: battery-efficiency-probe.
        // One-shot. Directly invokes Battery.ReceivePower(InputNetwork, powerAdded)
        // against the first OnOff Battery with headroom, logs PowerStored before /
        // after each call. Used to verify PGP's
        //   StationaryBatteryPatches.ChargeEfficiencyControl
        // math:
        //   charged = BatteryChargeEfficiency * powerAdded
        //   if (charged < 500) charged = powerAdded     // sub-500 W trickle floor
        //   PowerStored += charged (clamped)
        // Run twice across two server starts, once at BatteryChargeEfficiency = 1.0
        // and once at 0.5; the two log lines compared confirm the math.

        private static bool _bepProbeFired;

        private static void Scenario_PgpBatteryEfficiencyProbe()
        {
            if (!RequireModAssembly(PGP_ASSEMBLY, "pgp-battery-efficiency-probe")) return;
            if (_bepProbeFired) return;
            _bepProbeFired = true;

            if (_batteries.Count == 0) RebuildCaches();
            Battery target = null;
            foreach (var b in _batteries)
            {
                if (b != null && b.OnOff && b.InputNetwork != null && b.PowerStored + 5000f <= b.PowerMaximum)
                {
                    target = b;
                    break;
                }
            }
            if (target == null)
            {
                _log?.LogWarning("[ScenarioRunner] pgp-battery-efficiency-probe: no OnOff Battery with headroom + InputNetwork; nothing to probe.");
                return;
            }

            _log?.LogInfo(
                $"[ScenarioRunner] BEP target: ref={target.ReferenceId} prefab={target.PrefabName} " +
                $"PowerStored={target.PowerStored:F2} PowerMaximum={target.PowerMaximum:F2} " +
                $"BatteryChargeEfficiency_setting={PgpBatteryChargeEfficiency()}");

            // Probe A: large powerAdded above the 500 W trickle floor.
            //   Expected at eff=1.0: delta = 5000.  At eff=0.5: delta = 2500.
            var beforeA = target.PowerStored;
            target.ReceivePower(target.InputNetwork, 5000f);
            var afterA = target.PowerStored;
            _log?.LogInfo(
                $"[ScenarioRunner] BEP A: powerAdded=5000 before={beforeA:F2} after={afterA:F2} delta={afterA - beforeA:F2}");

            // Probe B: small powerAdded below the 500 W trickle floor.
            //   Expected at any eff: delta = 200 (trickle floor: charged = powerAdded).
            var beforeB = target.PowerStored;
            target.ReceivePower(target.InputNetwork, 200f);
            var afterB = target.PowerStored;
            _log?.LogInfo(
                $"[ScenarioRunner] BEP B: powerAdded=200  before={beforeB:F2} after={afterB:F2} delta={afterB - beforeB:F2}");
        }

        private static double PgpBatteryChargeEfficiency()
        {
            try
            {
                var asm = GetModAssembly(PGP_ASSEMBLY);
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

        // PGP scenario: apc-idle-probe.
        // Logs each AreaPowerControl's attached Battery.PowerStored every 5 ticks.
        // PGP's AreaPowerControlPatches stops APCs leaking battery when nothing is
        // downstream. Diff first vs last across the window; idle APCs should hold
        // constant.

        private static int _apcLastLogTick = int.MinValue;
        private const int APC_LOG_EVERY_TICKS = 5;

        private static void Scenario_PgpApcIdleProbe()
        {
            if (!RequireModAssembly(PGP_ASSEMBLY, "pgp-apc-idle-probe")) return;
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
                    $"[ScenarioRunner] APC tick={_ticksSeen} ref={a.ReferenceId} " +
                    $"prefab={a.PrefabName} OnOff={a.OnOff} UsedPower={a.UsedPower} " +
                    $"BatteryStored={stored:F2} BatteryMax={pmax:F2}");
            }
        }

        // PGP scenario: cable-burn-probe.
        // Two parts. Periodic: list every CableNetwork with Required or Current > 5 kW
        // (where normal cables would naturally burn). One-shot at tick (Delay+25):
        // reflect-invoke PowerGridPlus.Power.PowerGridTick.TestBurnCable(10000,10000)
        // against every network's PowerTick and tally would-burn counts by tier.
        // Verifies PGP's burn-decision formula plus the NEW-1 super-heavy carve-out
        // without needing a real overload.

        private static int _cbpLastLogTick = int.MinValue;
        private const int CBP_LOG_EVERY_TICKS = 25;
        private static bool _cbpReflectionFired;

        private static void Scenario_PgpCableBurnProbe()
        {
            if (!RequireModAssembly(PGP_ASSEMBLY, "pgp-cable-burn-probe")) return;
            if (_ticksSeen - _cbpLastLogTick < CBP_LOG_EVERY_TICKS) return;
            _cbpLastLogTick = (int)_ticksSeen;

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
                        $"[ScenarioRunner] CBP tick={_ticksSeen} netRef={n.ReferenceId} " +
                        $"Required={req:F2} Current={cur:F2} Potential={n.PotentialLoad:F2}");
                }
            });
            _log?.LogInfo($"[ScenarioRunner] CBP tick={_ticksSeen} summary: {overloaded} networks at >5kW (Required or Current)");

            if (!_cbpReflectionFired)
            {
                _cbpReflectionFired = true;
                ProbePgpTestBurnCableViaReflection();
            }
        }

        private static void ProbePgpTestBurnCableViaReflection()
        {
            try
            {
                var asm = GetModAssembly(PGP_ASSEMBLY);
                if (asm == null) { _log?.LogWarning("[ScenarioRunner] CBP reflection probe: PowerGridPlus assembly missing."); return; }
                var tickType = asm.GetType("PowerGridPlus.Power.PowerGridTick");
                if (tickType == null) { _log?.LogWarning("[ScenarioRunner] CBP reflection probe: PowerGridPlus.Power.PowerGridTick type not found."); return; }
                var testBurnCable = tickType.GetMethod("TestBurnCable",
                    System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public);
                if (testBurnCable == null) { _log?.LogWarning("[ScenarioRunner] CBP reflection probe: TestBurnCable method not found."); return; }

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

                    var result = testBurnCable.Invoke(pt, new object[] { 10000.0f, 10000.0f });
                    if (result != null) wouldBurn++;
                });

                _log?.LogInfo(
                    $"[ScenarioRunner] CBP reflection probe: probedNets={probedNets} " +
                    $"wouldBurn={wouldBurn} normalNets={normalCableNets} heavyNets={heavyCableNets} " +
                    $"superHeavyNets={superHeavyCableNets} (synthetic powerUsed=10000 W against TestBurnCable)");
            }
            catch (Exception e)
            {
                _log?.LogError($"[ScenarioRunner] CBP reflection probe threw: {e.Message}");
            }
        }
    }
}
