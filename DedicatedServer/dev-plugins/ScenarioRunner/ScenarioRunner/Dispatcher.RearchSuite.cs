using System;
using System.Collections.Generic;
using Assets.Scripts;
using Assets.Scripts.Networks;
using Assets.Scripts.Objects.Electrical;

namespace ScenarioRunner
{
    // Scenario: pgp-rearch-suite
    //
    // Grep-able PASS/FAIL regression for the Stage 1 power rearchitecture (unified rigid + soft flow
    // classes in the PowerGridPlus allocator). The live deadlock case: batteries 511274 / 511135 on
    // the Luna save sit behind two 50 kW charger transformers fed by 7 wireless links; pre-fix the
    // allocator granted their 50 kW charge every tick while the vanilla-facing caches advertised /
    // billed 0 for the transformer and wireless segs, so the batteries never charged and no fault
    // ever fired. Post-fix their PowerStored must rise.
    //
    // Phases (t = ticks since the scenario started, after the configured Delay Ticks):
    //   A (t == 0):    one-shot inventory of the allocator's segmenter roster by concrete type
    //                  (reflection on PowerGridPlus.SegmentingDeviceRegistry.EnumerateSorted; falls
    //                  back to plain device counts when unreachable), plus baseline PowerStored for
    //                  the two target batteries. Absent targets (a different save) log a notice and
    //                  the verdict becomes SKIP, not FAIL.
    //   B (t 1..120):  per tick, read both batteries' PowerStored, logging every 10 ticks:
    //                    [RearchSuite] t=<n> bat511274=<J> bat511135=<J> dPowerStored10t=<delta>
    //                  and census the fault-free supply shortfalls each tick:
    //                    [RearchSuite] t=<n> shortfallNets=<count>
    //                  A shortfall net is RequiredLoad > PotentialLoad + 0.5 with ZERO PowerGridPlus
    //                  faults (shed / overload / cycle / VVF) on its ElectricalInputOutput members;
    //                  when the fault registries are unreachable the census counts every shortfall
    //                  net and says so once. Fault-free shortfalls are the invisible-deadlock shape
    //                  the rearchitecture removes (Setting=0 "Firewalled" transformers on Luna fault
    //                  visibly, so they do not count here).
    //   C (t >= 130):  one-shot verdict:
    //                    [RearchSuite] VERDICT charge=<PASS|FAIL|SKIP> shortfalls=<max count seen>
    //                  charge PASS = PowerStored rose on BOTH batteries and the combined rise
    //                  exceeds 100 kJ over the window.
    //
    // Threading: PowerStored / RequiredLoad / PotentialLoad are managed floats, PowerDeviceList is
    // read under its lock, registries via reflection -- all safe on the UniTask sim-tick worker
    // (Research/Patterns/ThingEnumerationOffMainThread.md). Read-only; touches no world state.
    internal static partial class Dispatcher
    {
        private const long RS_BAT_A = 511274L;
        private const long RS_BAT_B = 511135L;
        private const int RS_WINDOW_TICKS = 120;
        private const int RS_VERDICT_TICK = 130;
        private const int RS_LOG_EVERY = 10;

        private static bool _rsStarted;
        private static bool _rsDone;
        private static long _rsStartTick;
        private static Battery _rsBatA;
        private static Battery _rsBatB;
        private static float _rsBaseA;
        private static float _rsBaseB;
        private static float _rsSumAtLastLog;
        private static int _rsMaxShortfalls;
        private static bool _rsFaultCheckAvailable;
        private static bool _rsFaultCheckNoticeLogged;

        private static readonly (string label, string type)[] _rsRegistries =
        {
            ("shed", "PowerGridPlus.BrownoutRegistry"),
            ("overload", "PowerGridPlus.OverloadRegistry"),
            ("cycle", "PowerGridPlus.CycleFaultRegistry"),
            ("vvf", "PowerGridPlus.VariableVoltageFaultRegistry"),
        };

        private static void Scenario_PgpRearchSuite()
        {
            if (_rsDone) return;

            // Deterministic full solar for the charge regression: ride the existing sun-noon
            // freeze (self-managing one-shot zenith scan + per-tick TimeScale re-arm in
            // Dispatcher.SunNoon.cs). Runs before the PGP gate so the sun is frozen either way.
            Scenario_SunNoon();

            if (!RequireModAssembly(PGP_ASSEMBLY, "pgp-rearch-suite")) return;

            try
            {
                if (!_rsStarted)
                {
                    _rsStarted = true;
                    _rsStartTick = _ticksSeen;
                    RearchSuite_PhaseA();
                    return;
                }

                long t = _ticksSeen - _rsStartTick;
                if (t <= RS_WINDOW_TICKS)
                {
                    RearchSuite_PhaseB(t);
                }
                else if (t >= RS_VERDICT_TICK)
                {
                    _rsDone = true;
                    RearchSuite_PhaseC();
                }
            }
            catch (Exception e)
            {
                _log?.LogError($"[ScenarioRunner] [RearchSuite] threw: {e}");
            }
        }

        // ---- Phase A: roster inventory + target baselines ----

        private static void RearchSuite_PhaseA()
        {
            // Segmenter roster by concrete type, via PGP's own deterministic enumeration.
            var byType = new SortedDictionary<string, int>(StringComparer.Ordinal);
            bool rosterOk = false;
            try
            {
                var asm = GetModAssembly(PGP_ASSEMBLY);
                var reg = asm?.GetType("PowerGridPlus.SegmentingDeviceRegistry");
                var enumerate = reg?.GetMethod("EnumerateSorted",
                    System.Reflection.BindingFlags.Static
                    | System.Reflection.BindingFlags.Public
                    | System.Reflection.BindingFlags.NonPublic);
                if (enumerate?.Invoke(null, null) is System.Collections.IEnumerable roster)
                {
                    rosterOk = true;
                    foreach (var item in roster)
                    {
                        if (item == null) continue;
                        string name = item.GetType().Name;
                        byType.TryGetValue(name, out int c);
                        byType[name] = c + 1;
                    }
                }
            }
            catch (Exception e)
            {
                _log?.LogWarning($"[ScenarioRunner] [RearchSuite] roster reflection threw: {e.Message}");
            }

            if (rosterOk)
            {
                var parts = new List<string>();
                int total = 0;
                foreach (var kv in byType) { parts.Add($"{kv.Key}={kv.Value}"); total += kv.Value; }
                _log?.LogInfo($"[ScenarioRunner] [RearchSuite] inventory segmenters total={total} " +
                              (parts.Count > 0 ? string.Join(" ", parts) : "(none)"));
            }
            else
            {
                RebuildCaches();
                _log?.LogInfo(
                    "[ScenarioRunner] [RearchSuite] inventory (roster unreachable, device counts) " +
                    $"Battery={_batteries.Count} Transformer={_transformers.Count} AreaPowerControl={_apcs.Count}");
            }

            // Fault registries reachable? Decides whether the shortfall census can exclude faulted nets.
            _rsFaultCheckAvailable = GetModAssembly(PGP_ASSEMBLY)?.GetType(_rsRegistries[0].type) != null;

            // Resolve the two target batteries by ReferenceId.
            OcclusionManager.AllThings.ForEach(thing =>
            {
                if (!(thing is Battery b)) return;
                if (b.ReferenceId == RS_BAT_A) _rsBatA = b;
                else if (b.ReferenceId == RS_BAT_B) _rsBatB = b;
            });

            if (_rsBatA == null || _rsBatB == null)
            {
                _log?.LogWarning(
                    $"[ScenarioRunner] [RearchSuite] target batteries {RS_BAT_A}/{RS_BAT_B} " +
                    $"(foundA={_rsBatA != null} foundB={_rsBatB != null}) absent from this world; " +
                    "charge verdict will be SKIP. Shortfall census still runs.");
            }
            else
            {
                _rsBaseA = _rsBatA.PowerStored;
                _rsBaseB = _rsBatB.PowerStored;
                _rsSumAtLastLog = _rsBaseA + _rsBaseB;
                _log?.LogInfo(
                    $"[ScenarioRunner] [RearchSuite] baseline bat{RS_BAT_A}={_rsBaseA:F0} " +
                    $"bat{RS_BAT_B}={_rsBaseB:F0} (PowerStored, J)");
            }
        }

        // ---- Phase B: charge trace + fault-free shortfall census ----

        private static void RearchSuite_PhaseB(long t)
        {
            if (_rsBatA != null && _rsBatB != null && t % RS_LOG_EVERY == 0)
            {
                float a = _rsBatA.PowerStored;
                float b = _rsBatB.PowerStored;
                float sum = a + b;
                _log?.LogInfo(
                    $"[ScenarioRunner] [RearchSuite] t={t} bat{RS_BAT_A}={a:F0} bat{RS_BAT_B}={b:F0} " +
                    $"dPowerStored10t={sum - _rsSumAtLastLog:F0}");
                _rsSumAtLastLog = sum;
            }

            int shortfalls = RearchSuite_CountFaultFreeShortfalls();
            if (shortfalls > _rsMaxShortfalls) _rsMaxShortfalls = shortfalls;
            _log?.LogInfo($"[ScenarioRunner] [RearchSuite] t={t} shortfallNets={shortfalls}");
        }

        private static int RearchSuite_CountFaultFreeShortfalls()
        {
            var asm = GetModAssembly(PGP_ASSEMBLY);
            if (!_rsFaultCheckAvailable && !_rsFaultCheckNoticeLogged)
            {
                _rsFaultCheckNoticeLogged = true;
                _log?.LogWarning("[ScenarioRunner] [RearchSuite] fault registries unreachable; " +
                                 "shortfall census counts ALL undersupplied nets (fault filter skipped).");
            }

            int count = 0;
            CableNetwork.AllCableNetworks.ForEach(net =>
            {
                if (net == null) return;
                if (net.RequiredLoad <= net.PotentialLoad + 0.5f) return;

                if (_rsFaultCheckAvailable)
                {
                    bool anyFaulted = false;
                    lock (net.PowerDeviceList)
                    {
                        for (int i = 0; i < net.PowerDeviceList.Count && !anyFaulted; i++)
                        {
                            if (!(net.PowerDeviceList[i] is ElectricalInputOutput eio)) continue;
                            foreach (var (_, type) in _rsRegistries)
                            {
                                if (!PgpIsLocked(asm, type, eio.ReferenceId)) continue;
                                anyFaulted = true;
                                break;
                            }
                        }
                    }
                    if (anyFaulted) return;   // visible fault explains the shortfall: not the deadlock shape
                }

                count++;
            });
            return count;
        }

        // ---- Phase C: verdict ----

        private static void RearchSuite_PhaseC()
        {
            string charge;
            if (_rsBatA == null || _rsBatB == null)
            {
                charge = "SKIP";
                _log?.LogInfo($"[ScenarioRunner] [RearchSuite] charge detail: targets absent, nothing to measure.");
            }
            else
            {
                float dA = _rsBatA.PowerStored - _rsBaseA;
                float dB = _rsBatB.PowerStored - _rsBaseB;
                bool pass = dA > 0f && dB > 0f && dA + dB > 100000f;
                charge = pass ? "PASS" : "FAIL";
                _log?.LogInfo(
                    $"[ScenarioRunner] [RearchSuite] charge detail: dBat{RS_BAT_A}={dA:F0} " +
                    $"dBat{RS_BAT_B}={dB:F0} combined={dA + dB:F0} (need both > 0 and combined > 100000)");
            }

            _log?.LogInfo($"[ScenarioRunner] [RearchSuite] VERDICT charge={charge} shortfalls={_rsMaxShortfalls}");
        }
    }
}
