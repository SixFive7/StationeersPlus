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
    // classes in the PowerGridPlus allocator) plus the Stage 3 Powered semantics and wireless ledger
    // adoption. The live deadlock case: batteries 511274 / 511135 on the Luna save sit behind two
    // 50 kW charger transformers fed by 7 wireless links; pre-fix the allocator granted their 50 kW
    // charge every tick while the vanilla-facing caches advertised / billed 0 for the transformer
    // and wireless segs, so the batteries never charged and no fault ever fired. Post-fix their
    // PowerStored must rise. Stage 3 adds two checks: the charger transformers must read
    // Powered=True while the charge is flowing (and, policy-wide, while idle-healthy), and the
    // wireless halves' vanilla _powerProvided ledgers must stay non-negative (the load sweep plus
    // the per-tick ENFORCE-tail ledger settle kill the stale-credit free-energy class).
    //
    // Phases (t = ticks since the scenario started, after the configured Delay Ticks):
    //   A (t == 0):    one-shot inventory of the allocator's segmenter roster by concrete type
    //                  (reflection on PowerGridPlus.SegmentingDeviceRegistry.EnumerateSorted; falls
    //                  back to plain device counts when unreachable), plus baseline PowerStored for
    //                  the two target batteries. Absent targets (a different save) log a notice and
    //                  the verdict becomes SKIP, not FAIL. Stage 3 additions: resolve the CHARGER
    //                  transformers pragmatically as the Transformers whose OutputNetwork is a
    //                  target battery's InputNetwork (the transformers directly powering the
    //                  batteries' charge network; 511047/511050 on the Luna testbed), log their
    //                  Powered flags, cache every WirelessPower half in the scene, and log the
    //                  minimum _powerProvided across the halves (read via this plugin's own cached
    //                  FieldInfo on the vanilla per-class private field; no PGP/PTP compile refs).
    //   B (t 1..120):  per tick, read both batteries' PowerStored, logging every 10 ticks:
    //                    [RearchSuite] t=<n> bat511274=<J> bat511135=<J> dPowerStored10t=<delta>
    //                  and census the vanilla supply shortfalls each tick (census v2):
    //                    [RearchSuite] t=<n> shortfallNets=<deadlockCount> (dry=<a> throttled=<b>
    //                      faulted=<c> offscope=<d> served=<e>)
    //                  A shortfall net is RequiredLoad > PotentialLoad + 0.5. Each one lands in
    //                  exactly one bucket: faulted = a shed/overload/cycle/VVF registry hit on any
    //                  ElectricalInputOutput member (checked first; a net both faulted and
    //                  classified keeps this bucket); otherwise the net joins
    //                  PowerGridPlus.ShortfallDiagnostics.TryClassify (the per-net classification
    //                  snapshot published at the ALLOCATE tail; cached MethodInfo, byte contract
    //                  0=Served 1=Dry 2=Throttled 3=Deadlock) into served / dry / throttled /
    //                  deadlock, and a net absent from the snapshot is offscope. Only DEADLOCK
    //                  (the allocator's own accounting says supply existed yet the net stayed
    //                  unmet) is the invisible-deadlock regression shape the rearchitecture
    //                  removes; dry (source-side exhaustion: dead-input chains, unaimed solar
    //                  islands), throttled (Setting=0 "firewalled" / rate-limited-to-zero /
    //                  lockout-locked feeds), served (allocator met all rigid demand; vanilla
    //                  presentation disagrees) and faulted are honest darkness, reported for
    //                  visibility only. When ShortfallDiagnostics is unreachable (older
    //                  PowerGridPlus build) every fault-free shortfall counts as deadlock, the
    //                  pre-v2 census semantics, and a notice logs once.
    //                  Stage 3 additions, every tick: poweredHealthyViolations counts ticks where
    //                  ANY resolved charger reads Powered=false while the combined battery
    //                  PowerStored rose since the previous tick (charging progressing; per the spec
    //                  this is deliberately blind to fault state, acceptable on the fault-free
    //                  testbed), DEBOUNCED to >= 3 consecutive such ticks: PowerGridPlus asserts
    //                  the rising Powered edge via the vanilla-safe Device.SetPowerFromThread,
    //                  which marshals the interactable write to the main thread, so the flag lands
    //                  one or two frames after the tick that granted the flow (observed: charge
    //                  begins t=18, Powered=True lands t=19/20). A 1-2 tick False blip on the
    //                  rising edge is therefore inherent to the write path and not a violation;
    //                  the permanent-False deadlock regression still accrues violations on every
    //                  tick from the third onward. minLedger tracks the window minimum of
    //                  _powerProvided across all wireless halves (sampled from the ElectricityTick
    //                  postfix pump, i.e. after PowerGridPlus's ENFORCE-tail ledger settle). A
    //                  third 10-tick log line reports both:
    //                    [RearchSuite] t=<n> chargersPowered=<k>/<n> violations=<v> minLedger=<W>
    //   C (t >= 130):  one-shot verdict:
    //                    [RearchSuite] VERDICT charge=<PASS|FAIL|SKIP> shortfalls=<max deadlock seen>
    //                      powered=<PASS|FAIL|SKIP> ledger=<PASS|FAIL|SKIP>
    //                  preceded by a shortfall detail line carrying the window max of every census
    //                  bucket (maxDeadlock / maxDry / maxThrottled / maxFaulted / maxOffscope /
    //                  maxServed). shortfalls= carries max deadlockCount ONLY: the regression
    //                  signal, expected 0 on a healthy build. charge PASS = PowerStored rose on
    //                  BOTH batteries and the combined rise exceeds 100 kJ over the window.
    //                  powered PASS = zero poweredHealthyViolations (SKIP when no charger
    //                  resolved). ledger PASS = minLedger >= -0.5 W across the window (SKIP when
    //                  no wireless half was readable). The line still matches the Stage 1 greps
    //                  ('RearchSuite] VERDICT' and 'charge='); the two fields are appended.
    //
    // Threading: PowerStored / RequiredLoad / PotentialLoad / Powered are managed state, the
    // _powerProvided reads are cached-FieldInfo reflection, PowerDeviceList is read under its lock,
    // registries via reflection, and the ShortfallDiagnostics join is a cached-MethodInfo call into
    // a volatile-swapped immutable snapshot the allocator publishes at the ALLOCATE tail (the pump
    // is an ElectricityTick postfix, so it always joins the same tick's snapshot) -- all safe on
    // the UniTask sim-tick worker (Research/Patterns/ThingEnumerationOffMainThread.md). Read-only;
    // touches no world state.
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
        private static int _rsMaxShortfalls;          // window max of the DEADLOCK bucket (the regression signal)
        private static bool _rsFaultCheckAvailable;
        private static bool _rsFaultCheckNoticeLogged;

        // ---- Census v2 state: per-bucket window maxima + the classification join ----
        private static int _rsMaxDry;
        private static int _rsMaxThrottled;
        private static int _rsMaxFaulted;
        private static int _rsMaxOffscope;
        private static int _rsMaxServed;
        // Cached MethodInfo for PowerGridPlus.ShortfallDiagnostics.TryClassify(long, out byte),
        // resolved once on the first census pass (null = unreachable, e.g. an older PowerGridPlus
        // build: the census falls back to counting every fault-free shortfall as deadlock). The
        // args array is reused across calls; the census runs on the single sim-tick pump thread.
        private static bool _rsClassifierResolved;
        private static System.Reflection.MethodInfo _rsClassifyMethod;
        private static readonly object[] _rsClassifyArgs = new object[2];

        // ---- Stage 3 state: Powered policy + wireless ledger ----
        // A charger must read Powered=false for at least this many CONSECUTIVE
        // charging-progressing ticks before a violation is counted. PowerGridPlus asserts the
        // rising Powered edge through the vanilla-safe Device.SetPowerFromThread, which
        // self-marshals to the main thread, so the flag lands one or two frames after the tick
        // that granted the flow; a 1-2 tick False blip on the rising edge is inherent to that
        // write path, while the pre-fix deadlock held Powered=false for the WHOLE window and
        // still trips the debounced counter on every tick from the third onward.
        private const int RS_POWERED_DEBOUNCE_TICKS = 3;

        private static readonly List<Transformer> _rsChargers = new List<Transformer>();
        private static readonly List<WirelessPower> _rsWirelessHalves = new List<WirelessPower>();
        private static int _rsPoweredViolations;
        private static int _rsUnpoweredStreak;          // consecutive charging ticks with a dark charger
        private static float _rsPrevTickSum = -1f;      // previous tick's combined PowerStored (-1 = unseeded)
        private static float _rsMinLedger = float.MaxValue;
        private static bool _rsLedgerSampled;

        // Cached FieldInfo for the vanilla per-class private _powerProvided ledgers
        // (PowerTransmitter and PowerReceiver each declare their own). This plugin has no
        // PowerGridPlus / PowerTransmitterPlus compile references, so it reads the vanilla field
        // directly; the values it sees are post-settle because the pump is an ElectricityTick
        // postfix.
        private static readonly System.Reflection.FieldInfo _rsTxLedgerField =
            typeof(PowerTransmitter).GetField("_powerProvided",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        private static readonly System.Reflection.FieldInfo _rsRxLedgerField =
            typeof(PowerReceiver).GetField("_powerProvided",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);

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
                _rsPrevTickSum = _rsSumAtLastLog;
                _log?.LogInfo(
                    $"[ScenarioRunner] [RearchSuite] baseline bat{RS_BAT_A}={_rsBaseA:F0} " +
                    $"bat{RS_BAT_B}={_rsBaseB:F0} (PowerStored, J)");
            }

            RearchSuite_PhaseAStage3();
        }

        // Stage 3 baseline: resolve the charger transformers, cache the wireless halves, log the
        // chargers' Powered flags and the minimum ledger across the halves. Charger resolution is
        // pragmatic: a charger is any Transformer whose OutputNetwork is a target battery's
        // InputNetwork (the transformer directly powering the batteries' charge network). On the
        // Luna testbed both batteries charge from net 511271, fed by transformers 511047/511050.
        private static void RearchSuite_PhaseAStage3()
        {
            RebuildCaches();

            var chargeNets = new HashSet<long>();
            if (_rsBatA != null && _rsBatA.InputNetwork != null) chargeNets.Add(_rsBatA.InputNetwork.ReferenceId);
            if (_rsBatB != null && _rsBatB.InputNetwork != null) chargeNets.Add(_rsBatB.InputNetwork.ReferenceId);

            _rsChargers.Clear();
            foreach (var t in _transformers)
            {
                if (t == null || t.OutputNetwork == null) continue;
                if (chargeNets.Contains(t.OutputNetwork.ReferenceId)) _rsChargers.Add(t);
            }

            _rsWirelessHalves.Clear();
            OcclusionManager.AllThings.ForEach(thing =>
            {
                if (thing is WirelessPower w) _rsWirelessHalves.Add(w);
            });

            var chargerParts = new List<string>();
            foreach (var c in _rsChargers)
                chargerParts.Add($"{c.ReferenceId}:Powered={c.Powered}");
            float minLedger = RearchSuite_MinLedger(out int halvesRead);
            _log?.LogInfo(
                "[ScenarioRunner] [RearchSuite] stage3 baseline chargers=[" + string.Join(" ", chargerParts) + "] " +
                $"wirelessHalves={_rsWirelessHalves.Count} ledgerReadable={halvesRead} " +
                $"minLedger={(halvesRead > 0 ? minLedger.ToString("F2") : "n/a")}");
            if (_rsChargers.Count == 0)
                _log?.LogWarning(
                    "[ScenarioRunner] [RearchSuite] no charger transformer resolved (no Transformer outputs onto a " +
                    "target battery's InputNetwork); powered verdict will be SKIP.");
        }

        // Minimum _powerProvided across every cached wireless half, via the cached vanilla
        // FieldInfos. halvesRead reports how many halves were actually readable; 0 means the
        // ledger verdict cannot be judged (SKIP).
        private static float RearchSuite_MinLedger(out int halvesRead)
        {
            halvesRead = 0;
            float min = float.MaxValue;
            foreach (var w in _rsWirelessHalves)
            {
                if (w == null) continue;
                var field = w is PowerTransmitter ? _rsTxLedgerField
                    : w is PowerReceiver ? _rsRxLedgerField : null;
                if (field == null) continue;
                float v;
                try { v = field.GetValue(w) is float f ? f : float.NaN; }
                catch { continue; }
                if (float.IsNaN(v)) continue;
                halvesRead++;
                if (v < min) min = v;
            }
            return min;
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

            int deadlock = RearchSuite_CountFaultFreeShortfalls(
                out int dry, out int throttled, out int faulted, out int offscope, out int served);
            if (deadlock > _rsMaxShortfalls) _rsMaxShortfalls = deadlock;
            if (dry > _rsMaxDry) _rsMaxDry = dry;
            if (throttled > _rsMaxThrottled) _rsMaxThrottled = throttled;
            if (faulted > _rsMaxFaulted) _rsMaxFaulted = faulted;
            if (offscope > _rsMaxOffscope) _rsMaxOffscope = offscope;
            if (served > _rsMaxServed) _rsMaxServed = served;
            _log?.LogInfo(
                $"[ScenarioRunner] [RearchSuite] t={t} shortfallNets={deadlock} " +
                $"(dry={dry} throttled={throttled} faulted={faulted} offscope={offscope} served={served})");

            RearchSuite_PhaseBStage3(t);
        }

        // Stage 3 per-tick tracking. poweredHealthyViolations, DEBOUNCED: a tick counts as a
        // violation only when the combined battery PowerStored rose since the previous tick
        // (charging is progressing, so the chargers are demonstrably routing power), ANY resolved
        // charger reads Powered=false, AND that condition has held for at least
        // RS_POWERED_DEBOUNCE_TICKS consecutive ticks. The debounce absorbs the 1-2 tick rising-
        // edge lag inherent to PowerGridPlus's main-thread-marshalled SetPowerFromThread write
        // (observed: charge begins at t=18, the Powered=True edge lands t=19/20); the pre-fix
        // permanent-False deadlock still accrues a violation on every tick from the third onward.
        // Any powered / non-progressing tick resets the streak.
        // minLedger: the window minimum of _powerProvided across every wireless half, sampled
        // post-tick (the pump is an ElectricityTick postfix), i.e. after PowerGridPlus's
        // ENFORCE-tail ledger settle; a healthy Stage 3 build keeps this >= 0 within tolerance.
        private static void RearchSuite_PhaseBStage3(long t)
        {
            int poweredCount = 0;
            bool violatedThisTick = false;
            if (_rsBatA != null && _rsBatB != null && _rsChargers.Count > 0)
            {
                float sumNow = _rsBatA.PowerStored + _rsBatB.PowerStored;
                bool chargingProgressing = _rsPrevTickSum >= 0f && sumNow - _rsPrevTickSum > 1f;
                foreach (var c in _rsChargers)
                    if (c != null && c.Powered) poweredCount++;
                if (chargingProgressing && poweredCount < _rsChargers.Count)
                {
                    _rsUnpoweredStreak++;
                    if (_rsUnpoweredStreak >= RS_POWERED_DEBOUNCE_TICKS)
                    {
                        _rsPoweredViolations++;
                        violatedThisTick = true;
                    }
                }
                else
                {
                    _rsUnpoweredStreak = 0;
                }
                _rsPrevTickSum = sumNow;
            }
            else
            {
                foreach (var c in _rsChargers)
                    if (c != null && c.Powered) poweredCount++;
            }

            float tickMin = RearchSuite_MinLedger(out int halvesRead);
            if (halvesRead > 0)
            {
                _rsLedgerSampled = true;
                if (tickMin < _rsMinLedger) _rsMinLedger = tickMin;
            }

            if (t % RS_LOG_EVERY == 0 || violatedThisTick)
            {
                _log?.LogInfo(
                    $"[ScenarioRunner] [RearchSuite] t={t} chargersPowered={poweredCount}/{_rsChargers.Count} " +
                    $"violations={_rsPoweredViolations} " +
                    $"minLedger={(_rsLedgerSampled ? _rsMinLedger.ToString("F2") : "n/a")}");
            }
        }

        // Census v2: bucket every vanilla-shortfall net (RequiredLoad > PotentialLoad + 0.5) by
        // joining the allocator's per-net classification snapshot. Fault-registry hits are checked
        // FIRST, so a net both faulted and classified keeps the faulted bucket (today's behavior).
        // The remaining nets join PowerGridPlus.ShortfallDiagnostics.TryClassify(netRefId, out cls)
        // via the cached MethodInfo (byte contract: 0=Served 1=Dry 2=Throttled 3=Deadlock); a net
        // absent from the snapshot is offscope. The return value is the DEADLOCK bucket alone: the
        // invisible-deadlock regression signal (allocator accounting says supply existed yet the
        // net stayed unmet). When the classifier is unreachable (older PowerGridPlus build) every
        // fault-free shortfall counts as deadlock, matching the pre-v2 census.
        private static int RearchSuite_CountFaultFreeShortfalls(
            out int dryOut, out int throttledOut, out int faultedOut, out int offscopeOut, out int servedOut)
        {
            var asm = GetModAssembly(PGP_ASSEMBLY);
            if (!_rsFaultCheckAvailable && !_rsFaultCheckNoticeLogged)
            {
                _rsFaultCheckNoticeLogged = true;
                _log?.LogWarning("[ScenarioRunner] [RearchSuite] fault registries unreachable; " +
                                 "shortfall census skips the fault filter (faulted bucket stays 0).");
            }
            if (!_rsClassifierResolved)
            {
                _rsClassifierResolved = true;
                try
                {
                    _rsClassifyMethod = asm?.GetType("PowerGridPlus.ShortfallDiagnostics")?.GetMethod(
                        "TryClassify",
                        System.Reflection.BindingFlags.Static
                        | System.Reflection.BindingFlags.Public
                        | System.Reflection.BindingFlags.NonPublic);
                }
                catch { _rsClassifyMethod = null; }
                if (_rsClassifyMethod == null)
                    _log?.LogWarning("[ScenarioRunner] [RearchSuite] ShortfallDiagnostics.TryClassify " +
                                     "unreachable; every fault-free shortfall counts as deadlock " +
                                     "(pre-classification census semantics).");
            }

            int deadlock = 0, dry = 0, throttled = 0, faulted = 0, offscope = 0, served = 0;
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
                    if (anyFaulted) { faulted++; return; }   // visible fault explains the shortfall
                }

                if (_rsClassifyMethod == null) { deadlock++; return; }   // pre-v2 fallback

                bool inScope;
                byte cls;
                try
                {
                    _rsClassifyArgs[0] = net.ReferenceId;
                    _rsClassifyArgs[1] = (byte)0;
                    inScope = _rsClassifyMethod.Invoke(null, _rsClassifyArgs) is bool b && b;
                    cls = _rsClassifyArgs[1] is byte cb ? cb : (byte)255;
                }
                catch { deadlock++; return; }   // reflection failure: count it, never hide it

                if (!inScope) { offscope++; return; }
                switch (cls)
                {
                    case 0: served++; break;      // ShortfallDiagnostics.Served
                    case 1: dry++; break;         // ShortfallDiagnostics.Dry
                    case 2: throttled++; break;   // ShortfallDiagnostics.Throttled
                    default: deadlock++; break;   // Deadlock (3) or an unknown value: the regression bucket
                }
            });

            dryOut = dry;
            throttledOut = throttled;
            faultedOut = faulted;
            offscopeOut = offscope;
            servedOut = served;
            return deadlock;
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

            // Stage 3 verdicts. powered PASS = zero ticks where a resolved charger read
            // Powered=false while charging progressed; SKIP when no charger resolved (different
            // save). ledger PASS = the window minimum of _powerProvided across every wireless half
            // stayed >= -0.5 W; SKIP when no half was readable.
            string powered;
            if (_rsChargers.Count == 0)
            {
                powered = "SKIP";
                _log?.LogInfo("[ScenarioRunner] [RearchSuite] powered detail: no charger transformer resolved.");
            }
            else
            {
                powered = _rsPoweredViolations == 0 ? "PASS" : "FAIL";
                var finalStates = new List<string>();
                foreach (var c in _rsChargers)
                    finalStates.Add(c == null ? "gone" : $"{c.ReferenceId}:Powered={c.Powered}");
                _log?.LogInfo(
                    $"[ScenarioRunner] [RearchSuite] powered detail: violations={_rsPoweredViolations} " +
                    $"(debounce {RS_POWERED_DEBOUNCE_TICKS} consecutive ticks) " +
                    "final=[" + string.Join(" ", finalStates) + "] (need zero violations)");
            }

            string ledger;
            if (!_rsLedgerSampled)
            {
                ledger = "SKIP";
                _log?.LogInfo("[ScenarioRunner] [RearchSuite] ledger detail: no wireless half readable.");
            }
            else
            {
                ledger = _rsMinLedger >= -0.5f ? "PASS" : "FAIL";
                _log?.LogInfo(
                    $"[ScenarioRunner] [RearchSuite] ledger detail: minLedger={_rsMinLedger:F2} across " +
                    $"{_rsWirelessHalves.Count} wireless halves over the window (need >= -0.5)");
            }

            // Census v2 detail: the window max of every shortfall bucket. shortfalls= on the
            // VERDICT line carries maxDeadlock only (the regression signal); the other buckets
            // are honest darkness (or, for served, a vanilla-presentation disagreement) and are
            // reported here for visibility.
            _log?.LogInfo(
                $"[ScenarioRunner] [RearchSuite] shortfall detail: maxDeadlock={_rsMaxShortfalls} " +
                $"maxDry={_rsMaxDry} maxThrottled={_rsMaxThrottled} maxFaulted={_rsMaxFaulted} " +
                $"maxOffscope={_rsMaxOffscope} maxServed={_rsMaxServed}");

            _log?.LogInfo(
                $"[ScenarioRunner] [RearchSuite] VERDICT charge={charge} shortfalls={_rsMaxShortfalls} " +
                $"powered={powered} ledger={ledger}");
        }
    }
}
