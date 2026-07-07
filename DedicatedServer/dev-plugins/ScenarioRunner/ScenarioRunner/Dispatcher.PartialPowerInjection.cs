using System;
using System.Reflection;
using Assets.Scripts.Networks;
using Assets.Scripts.Objects.Electrical;
using HarmonyLib;

namespace ScenarioRunner
{
    // Scenario: pgp-partial-power-injection
    //
    // ============================ DELIBERATE FAULT INJECTOR ============================
    // This scenario BREAKS the grid on purpose. For a bounded window (~12 ticks) it
    // under-advertises one live transformer's published presentation entry, so vanilla
    // ENFORCE under-delivers on that transformer's output network: consumers there will
    // flicker dark / scale for the window. Run it ONLY on a save copy or throwaway world,
    // attended. Never leave it configured on a world anyone cares about: it re-injects on
    // every tick the scenario window is active.
    // ===================================================================================
    //
    // Purpose: positive control for PowerGridPlus's PartialPowerSentinel (the
    // no-partial-power contract detector). A detector nobody has ever seen fire is
    // untrustworthy; this scenario proves end-to-end that a real engaged vanilla ratio on
    // an allocator-served network raises the sentinel's counters and its aggregated
    // warning, then that recovery silences it.
    //
    // Mechanism. PowerGridPlus's allocator publishes per-contributor presentation totals
    // to TransformerSupplyCache in the ALLOCATE publish tail every tick (PowerAllocator
    // publish tail; the TransformerExploitPatches GetGeneratedPower prefix advertises the
    // cached OutThroughput verbatim during ENFORCE). This scenario Harmony-patches a
    // postfix onto PowerAllocator.RunAtomic (reflection-resolved, applied only for the
    // injection window, unpatched after) that overwrites the target transformer's cache
    // entry with OutThroughput = HALF the freshly published real value (floored at 1 W,
    // never above the real value) while keeping the real InDraw: exactly the
    // under-advertise presentation-bug class the sentinel exists to catch. The output
    // network's ENFORCE Potential then lands STRICTLY BETWEEN 0 and Required, vanilla
    // CacheState computes _powerRatio < 1, and the sentinel (ENFORCE tail) must count
    // it, because the untampered allocator still classifies the net Served.
    //
    // Why fractional and not zero (gate 11a): vanilla CacheState assigns ratio = 1
    // unless Potential > 0 AND Required > 0 (the zero-Potential guard, 0.2.6403
    // decompile lines 271846-271852), so zeroing the advertise of a net's SOLE supplier
    // drives Potential to exactly 0 and the net goes WHOLE-DARK at ratio 1: correct
    // sentinel silence, self-defeated injection (observed live on transformer 636304 /
    // net 637675). Halving keeps the division branch engaged even when the target is
    // the only supplier. Each injected tick halves that tick's freshly republished real
    // value, so there is no compounding across the window.
    //
    // Knock-ons are legitimate: partially darkening real delivery can produce one-tick
    // partial scaling on in-scope nets one hop downstream (observed live: a 3-net
    // cohort at ratio 0.9375 for exactly one tick). That is the sentinel catching
    // exactly the class it exists for, so the assertions tolerate extra distinct nets
    // and extra counter growth beyond the injected net.
    //
    // Self-restoring by construction: the next tick's ALLOCATE publish tail re-runs
    // TransformerSupplyCache.Set with the real totals for every routed seg (verified at
    // the PowerAllocator publish tail call sites), so the moment the postfix is
    // unpatched, one tick later the grid is back to exact presentation. Nothing else is
    // touched: no allocator internals, no device state, no fault registries.
    //
    // Checks (house style: "[ScenarioRunner] PPI P<n> PASS|FAIL", an
    // "PPI END pass=N fail=M total=K" summary, and one grep-able
    // "[ScenarioRunner] [PPI] VERDICT result=PASS|FAIL pass=N fail=M" line):
    //   P1-P5  synthetic cases against the pure PartialPowerSentinel.IsViolation
    //          predicate (in-scope served + sub-1 fires; ratio 1, Dry, Deadlock, and
    //          out-of-contract nets, wireless carriers / bridge-only hops / off-scope,
    //          do not).
    //   P6     a steady live target exists: a transformer with fresh published output
    //          >= 100 W on two consecutive ticks whose output net is allocator-Served,
    //          RATIO-CONTRACT IN-SCOPE (ShortfallDiagnostics.InRatioScope: has a
    //          deprivable member, so the sentinel actually watches it), with
    //          RequiredLoad >= 50 W, and whose published output covers AT LEAST HALF
    //          the net's RequiredLoad (dominant supplier: halving then pins the
    //          injected ratio into [0.5, 0.75], decisively below the ~0.94 knock-on
    //          class, so the worst-capture assert is deterministic).
    //   P7     baseline: every sentinel counter is zero (healthy world precondition;
    //          carrier and hop nets are out of scope, so a healthy world holds this).
    //   P8     injection: ViolationTicks / ViolationNetTicks rise by >= 5 and
    //          DistinctNetCount by >= 1 within the 12-tick window. AT LEAST the
    //          injected net; extra distinct nets and extra counter growth are
    //          permitted (legitimate one-tick knock-ons downstream).
    //   P9     the sentinel's WORST capture names the injected output network with a
    //          sub-1 ratio (reflection-read field assert, immune to the 600-tick
    //          warning throttle; the injected [0.5, 0.75] ratio band outranks the
    //          knock-on class). The LATEST capture and the aggregated warning line
    //          are reported informationally, not asserted: a knock-on can legally be
    //          the last violation visited in the final violating tick.
    //   P10    recovery: after unpatch, counters freeze and no further warnings accrue.
    //          The freeze baseline is taken 2 pump ticks after unpatch so a final
    //          in-flight knock-on tick cannot false-fail the assert.
    internal static partial class Dispatcher
    {
        private const int PPI_HUNT_TICKS = 40;      // max ticks to find a steady target
        private const int PPI_INJECT_TICKS = 12;    // bounded injection window
        private const int PPI_RECOVER_TICKS = 10;   // recovery observation window
        private const string PPI_HARMONY_ID = "net.scenariorunner.ppi";

        private static int _ppiPhase;               // 0 init, 1 hunt, 2 inject, 3 recover, 4 done
        private static int _ppiPhaseTicks;
        private static int _ppiPass;
        private static int _ppiFail;

        // Reflection handles (resolved once in phase 0).
        private static Type _ppiSentinelType;
        private static MethodInfo _ppiIsViolation;
        private static MethodInfo _ppiTryClassify;
        private static MethodInfo _ppiInRatioScope;
        private static MethodInfo _ppiCacheSet;
        private static MethodInfo _ppiCacheTryGetOutput;
        private static MethodInfo _ppiCacheTryGetInputDraw;
        private static MethodInfo _ppiRunAtomic;

        private static Harmony _ppiHarmony;
        private static bool _ppiPatched;

        // Injection state. Written from the pump, read from the RunAtomic postfix; both run
        // sequentially on the electricity worker, so plain fields suffice.
        private static bool _ppiInjecting;
        private static long _ppiTargetRefId;
        private static long _ppiTargetNetId;

        private static long _ppiPrevCandidate;
        private static int _ppiSteadyStreak;

        // Counter snapshots.
        private static long _ppiBaseViolTicks, _ppiBaseNetTicks;
        private static int _ppiBaseDistinct, _ppiBaseWarns;
        private static long _ppiPostViolTicks, _ppiPostNetTicks;
        private static int _ppiPostWarns;
        private static bool _ppiPostSnapped;
        private static bool _ppiRecoveryDirty;

        private static void Scenario_PgpPartialPowerInjection()
        {
            if (!RequireModAssembly(PGP_ASSEMBLY, "pgp-partial-power-injection")) return;
            if (_ppiPhase == 4) return;

            try
            {
                switch (_ppiPhase)
                {
                    case 0: PpiInit(); break;
                    case 1: PpiHuntTick(); break;
                    case 2: PpiInjectTick(); break;
                    case 3: PpiRecoverTick(); break;
                }
            }
            catch (Exception e)
            {
                _log?.LogError($"[ScenarioRunner] PPI threw: {e}");
                _ppiFail++;
                PpiFinish();
            }
        }

        // ---- Phase 0: resolve reflection surface, run the synthetic predicate fixture. ----
        private static void PpiInit()
        {
            var asm = GetModAssembly(PGP_ASSEMBLY);
            _ppiSentinelType = asm?.GetType("PowerGridPlus.PartialPowerSentinel");
            var cacheType = asm?.GetType("PowerGridPlus.TransformerSupplyCache");
            var shortfallType = asm?.GetType("PowerGridPlus.ShortfallDiagnostics");
            var allocatorType = asm?.GetType("PowerGridPlus.PowerAllocator");
            const BindingFlags F = BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Static;
            _ppiIsViolation = _ppiSentinelType?.GetMethod("IsViolation", F);
            _ppiTryClassify = shortfallType?.GetMethod("TryClassify", F);
            _ppiInRatioScope = shortfallType?.GetMethod("InRatioScope", F);
            _ppiCacheSet = cacheType?.GetMethod("Set", F);
            _ppiCacheTryGetOutput = cacheType?.GetMethod("TryGetOutput", F);
            _ppiCacheTryGetInputDraw = cacheType?.GetMethod("TryGetInputDraw", F);
            _ppiRunAtomic = allocatorType != null ? AccessTools.Method(allocatorType, "RunAtomic") : null;

            if (_ppiSentinelType == null || _ppiIsViolation == null || _ppiTryClassify == null
                || _ppiInRatioScope == null || _ppiCacheSet == null || _ppiCacheTryGetOutput == null
                || _ppiCacheTryGetInputDraw == null || _ppiRunAtomic == null)
            {
                _log?.LogError("[ScenarioRunner] PPI FAIL: reflection surface incomplete (PartialPowerSentinel / TransformerSupplyCache / ShortfallDiagnostics.TryClassify+InRatioScope / PowerAllocator.RunAtomic); PowerGridPlus too old or renamed.");
                _ppiFail++;
                PpiFinish();
                return;
            }

            _log?.LogWarning("[ScenarioRunner] PPI START partial-power injection: this scenario DELIBERATELY breaks one transformer's advertised output for "
                + PPI_INJECT_TICKS + " ticks to prove the sentinel fires. Run on a save copy only.");

            // Synthetic predicate fixture (byte classes: 0 Served, 1 Dry, 2 Throttled, 3 Deadlock).
            // The bool is the caller-computed ratio-contract scope: in the allocator snapshot
            // AND ratio-deprivable; wireless carrier nets and bridge-only hop nets read false.
            PpiPredicateCase("P1", "in-scope served net with engaged ratio fires", true, 0, 0.5f, true);
            PpiPredicateCase("P2", "in-scope served net at exactly ratio 1 is healthy", true, 0, 1f, false);
            PpiPredicateCase("P3", "Dry net is honest darkness, not a violation", true, 1, 0.25f, false);
            PpiPredicateCase("P4", "out-of-contract net (wireless carrier / bridge-only hop / off-scope) never fires", false, 0, 0.25f, false);
            PpiPredicateCase("P5", "Deadlock net belongs to the shortfall census", true, 3, 0.5f, false);

            RebuildCaches();
            _ppiPhase = 1;
            _ppiPhaseTicks = 0;
        }

        private static void PpiPredicateCase(string id, string label, bool inScope, byte cls, float ratio, bool expected)
        {
            bool actual;
            try
            {
                actual = (bool)_ppiIsViolation.Invoke(null, new object[] { inScope, cls, ratio });
            }
            catch (Exception e)
            {
                _log?.LogError($"[ScenarioRunner] PPI {id} FAIL: {label}: invoke threw {(e.InnerException ?? e).GetType().Name}.");
                _ppiFail++;
                return;
            }
            if (actual == expected)
            {
                _log?.LogInfo($"[ScenarioRunner] PPI {id} PASS: {label}: IsViolation(inScope={inScope}, class={cls}, ratio={ratio}) == {actual}.");
                _ppiPass++;
            }
            else
            {
                _log?.LogError($"[ScenarioRunner] PPI {id} FAIL: {label}: IsViolation(inScope={inScope}, class={cls}, ratio={ratio}) == {actual}, expected {expected}.");
                _ppiFail++;
            }
        }

        // ---- Phase 1: find a steady live target, then baseline the counters. ----
        private static void PpiHuntTick()
        {
            _ppiPhaseTicks++;
            if (_transformers.Count == 0) RebuildCaches();
            long candidateRef = 0L;
            long candidateNet = 0L;
            float candidateOut = 0f;
            float candidateReq = 0f;

            foreach (var t in _transformers)
            {
                if (t == null || t.OutputNetwork == null) continue;
                var args = new object[] { t.ReferenceId, 0f };
                if (!(bool)_ppiCacheTryGetOutput.Invoke(null, args)) continue;
                float outW = (float)args[1];
                if (outW < 100f) continue;
                var net = t.OutputNetwork;
                if (net.RequiredLoad < 50f) continue;
                // Dominant supplier only: with output covering share s >= 0.5 of the net's
                // demand, the halved advertise yields ratio = 1 - 0.5 * s in [0.5, 0.75],
                // below the observed ~0.94 knock-on class, so the injected net reliably
                // takes the sentinel's worst-capture slot (the P9 assert).
                if (outW < net.RequiredLoad * 0.5f) continue;
                var clsArgs = new object[] { net.ReferenceId, (byte)0 };
                if (!(bool)_ppiTryClassify.Invoke(null, clsArgs)) continue;
                if ((byte)clsArgs[1] != 0) continue;   // must be allocator-Served
                // Must be ratio-contract in-scope (a deprivable member exists): the sentinel
                // does not watch bridge-only nets, so injecting one would prove nothing.
                if (!(bool)_ppiInRatioScope.Invoke(null, new object[] { net.ReferenceId })) continue;
                if (outW > candidateOut)
                {
                    candidateOut = outW;
                    candidateRef = t.ReferenceId;
                    candidateNet = net.ReferenceId;
                    candidateReq = net.RequiredLoad;
                }
            }

            if (candidateRef != 0L && candidateRef == _ppiPrevCandidate) _ppiSteadyStreak++;
            else _ppiSteadyStreak = candidateRef != 0L ? 1 : 0;
            _ppiPrevCandidate = candidateRef;

            if (_ppiSteadyStreak >= 2)
            {
                _ppiTargetRefId = candidateRef;
                _ppiTargetNetId = candidateNet;
                _log?.LogInfo($"[ScenarioRunner] PPI P6 PASS: steady target transformer {candidateRef}: published output {candidateOut:F1} W onto Served, ratio-contract in-scope output network {candidateNet} (RequiredLoad {candidateReq:F1} W) on two consecutive ticks.");
                _ppiPass++;

                var c = PpiReadCounters();
                if (c.violTicks == 0 && c.netTicks == 0 && c.distinct == 0 && c.warns == 0)
                {
                    _log?.LogInfo("[ScenarioRunner] PPI P7 PASS: baseline sentinel counters are all zero (no violations before injection).");
                    _ppiPass++;
                }
                else
                {
                    _log?.LogError($"[ScenarioRunner] PPI P7 FAIL: baseline counters not zero (violTicks={c.violTicks} netTicks={c.netTicks} distinct={c.distinct} warns={c.warns}); the world is not healthy, injection evidence will be ambiguous.");
                    _ppiFail++;
                }
                _ppiBaseViolTicks = c.violTicks;
                _ppiBaseNetTicks = c.netTicks;
                _ppiBaseDistinct = c.distinct;
                _ppiBaseWarns = c.warns;

                // Arm the injector: postfix on PowerAllocator.RunAtomic so the understated
                // cache write lands AFTER the publish tail and BEFORE ENFORCE, every tick.
                _ppiHarmony = new Harmony(PPI_HARMONY_ID);
                _ppiHarmony.Patch(_ppiRunAtomic, postfix: new HarmonyMethod(
                    typeof(Dispatcher).GetMethod(nameof(PpiRunAtomicPostfix), BindingFlags.Public | BindingFlags.Static)));
                _ppiPatched = true;
                _ppiInjecting = true;
                _log?.LogWarning($"[ScenarioRunner] PPI INJECT BEGIN: halving transformer {_ppiTargetRefId}'s advertised output (>= 1 W floor, real input draw preserved) for {PPI_INJECT_TICKS} ticks; expect partial scaling on network {_ppiTargetNetId}.");
                _ppiPhase = 2;
                _ppiPhaseTicks = 0;
                return;
            }

            if (_ppiPhaseTicks >= PPI_HUNT_TICKS)
            {
                _log?.LogError($"[ScenarioRunner] PPI P6 FAIL: no steady target found in {PPI_HUNT_TICKS} ticks (need a transformer with >= 100 W published output onto a Served net with >= 50 W RequiredLoad). Wrong save for this scenario.");
                _ppiFail++;
                PpiFinish();
            }
        }

        // The injector. Runs inside the atomic tick immediately after ALLOCATE (and its
        // publish tail), so the understatement is what ENFORCE advertises this same tick.
        // Fractional, not zero: halve the freshly published real output, floored at 1 W
        // and never above the real value, so Potential stays strictly positive and the
        // CacheState division branch engages even on a sole-supplier net (the
        // zero-Potential guard assigns ratio = 1 on a dark net; see the header). The
        // real InDraw is preserved. Must never throw: an exception here would abort the
        // rest of the atomic tick.
        public static void PpiRunAtomicPostfix()
        {
            if (!_ppiInjecting || _ppiTargetRefId == 0L) return;
            try
            {
                var outArgs = new object[] { _ppiTargetRefId, 0f };
                if (!(bool)_ppiCacheTryGetOutput.Invoke(null, outArgs)) return;   // nothing published: nothing to understate
                float realOut = (float)outArgs[1];
                if (realOut <= 0f) return;
                float injected = realOut * 0.5f;
                if (injected < 1f) injected = 1f;
                if (injected > realOut) injected = realOut;   // never ADVERTISE MORE than real

                var drawArgs = new object[] { _ppiTargetRefId, 0f };
                float inDraw = (bool)_ppiCacheTryGetInputDraw.Invoke(null, drawArgs) ? (float)drawArgs[1] : 0f;
                _ppiCacheSet.Invoke(null, new object[] { _ppiTargetRefId, injected, inDraw });
            }
            catch
            {
                // Swallow: never poison the atomic tick. The pump-side asserts will fail
                // loudly if the injection stopped landing.
            }
        }

        // ---- Phase 2: the bounded injection window. ----
        private static void PpiInjectTick()
        {
            _ppiPhaseTicks++;
            var c = PpiReadCounters();
            _log?.LogInfo($"[ScenarioRunner] PPI inject t={_ppiPhaseTicks}/{PPI_INJECT_TICKS} violTicks={c.violTicks} netTicks={c.netTicks} distinct={c.distinct} warns={c.warns} worstNet={c.worstNet} worstRatio={c.worstRatio:F6}");
            if (_ppiPhaseTicks < PPI_INJECT_TICKS) return;

            // Window over: disarm and unpatch BEFORE judging, so a failed assert cannot
            // leave the injector running.
            _ppiInjecting = false;
            PpiUnpatch();
            _log?.LogInfo("[ScenarioRunner] PPI INJECT END: injector unpatched; next ALLOCATE republishes real totals.");

            long dViolTicks = c.violTicks - _ppiBaseViolTicks;
            long dNetTicks = c.netTicks - _ppiBaseNetTicks;
            int dDistinct = c.distinct - _ppiBaseDistinct;
            if (dViolTicks >= 5 && dNetTicks >= 5 && dDistinct >= 1)
            {
                _log?.LogInfo($"[ScenarioRunner] PPI P8 PASS: counters rose under injection (violTicks +{dViolTicks}, netTicks +{dNetTicks}, distinct +{dDistinct}).");
                _ppiPass++;
            }
            else
            {
                _log?.LogError($"[ScenarioRunner] PPI P8 FAIL: counters did not rise as expected (violTicks +{dViolTicks}, netTicks +{dNetTicks}, distinct +{dDistinct}; need >= 5 / >= 5 / >= 1).");
                _ppiFail++;
            }

            // P9 asserts the sentinel's WORST capture FIELD, not the log line: the aggregated
            // warning is globally throttled to one per 600 ticks, so any earlier violation
            // source would consume the immediate-fire and hold the line back for the whole
            // window (the gate-11 race). The dominant-supplier hunt criterion pins the
            // injected ratio into [0.5, 0.75], below the ~0.94 knock-on class, so the worst
            // slot deterministically belongs to the injected net. The LATEST capture is
            // informational only: a legitimate knock-on can be the last violation visited in
            // the final violating tick.
            bool worstIsTarget = c.worstNet == _ppiTargetNetId && c.worstRatio < 1f;
            if (worstIsTarget)
            {
                _log?.LogInfo($"[ScenarioRunner] PPI P9 PASS: sentinel worst capture names injected network {_ppiTargetNetId} (worst ratio {c.worstRatio:F6}).");
                _ppiPass++;
            }
            else
            {
                _log?.LogError($"[ScenarioRunner] PPI P9 FAIL: worst capture does not name the injected network (worstNet={c.worstNet} ratio={c.worstRatio:F6}, expected net {_ppiTargetNetId} with ratio in [0.5, 0.75]).");
                _ppiFail++;
            }
            // Informational only: the latest capture (knock-ons may legally own it) and
            // whether the throttle-raced aggregated warning also fired inside the window.
            bool warned = c.warns > _ppiBaseWarns;
            bool named = c.lastWarning != null && c.lastWarning.Contains(_ppiTargetNetId.ToString());
            _log?.LogInfo($"[ScenarioRunner] PPI note: latestNet={c.lastNet} ratio={c.lastRatio:F6}; aggregated warning fired inside window={warned} namesInjectedNet={named} (warns {_ppiBaseWarns} -> {c.warns}).");

            _ppiPhase = 3;
            _ppiPhaseTicks = 0;
            _ppiPostSnapped = false;
            _ppiRecoveryDirty = false;
        }

        // ---- Phase 3: recovery window; counters must freeze. ----
        private static void PpiRecoverTick()
        {
            _ppiPhaseTicks++;
            var c = PpiReadCounters();
            if (_ppiPhaseTicks == 2)
            {
                // Freeze baseline 2 pump ticks after unpatch: the first clean tick may still
                // absorb a final in-flight knock-on (a one-tick downstream flicker from the
                // last injected tick), which must not false-fail the freeze assert.
                _ppiPostViolTicks = c.violTicks;
                _ppiPostNetTicks = c.netTicks;
                _ppiPostWarns = c.warns;
                _ppiPostSnapped = true;
                return;
            }
            if (_ppiPostSnapped
                && (c.violTicks != _ppiPostViolTicks || c.netTicks != _ppiPostNetTicks || c.warns != _ppiPostWarns))
            {
                _ppiRecoveryDirty = true;
                _log?.LogWarning($"[ScenarioRunner] PPI recovery t={_ppiPhaseTicks}: counters still moving (violTicks={c.violTicks} netTicks={c.netTicks} warns={c.warns}).");
            }
            if (_ppiPhaseTicks < PPI_RECOVER_TICKS) return;

            if (_ppiPostSnapped && !_ppiRecoveryDirty)
            {
                _log?.LogInfo($"[ScenarioRunner] PPI P10 PASS: counters frozen after unpatch (violTicks={c.violTicks} netTicks={c.netTicks} warns={c.warns} over {PPI_RECOVER_TICKS - 2} clean ticks); the injection self-restored.");
                _ppiPass++;
            }
            else
            {
                _log?.LogError("[ScenarioRunner] PPI P10 FAIL: counters kept rising after the injector was removed; either the cache did not self-restore or a real violation source exists.");
                _ppiFail++;
            }
            PpiFinish();
        }

        private static void PpiFinish()
        {
            _ppiInjecting = false;
            PpiUnpatch();
            _log?.LogInfo($"[ScenarioRunner] PPI END pass={_ppiPass} fail={_ppiFail} total={_ppiPass + _ppiFail}");
            bool verdict = _ppiFail == 0 && _ppiPass > 0;
            _log?.LogInfo($"[ScenarioRunner] [PPI] VERDICT result={(verdict ? "PASS" : "FAIL")} pass={_ppiPass} fail={_ppiFail}");
            _ppiPhase = 4;
        }

        private static void PpiUnpatch()
        {
            if (!_ppiPatched) return;
            try { _ppiHarmony?.UnpatchSelf(); }
            catch (Exception e) { _log?.LogError($"[ScenarioRunner] PPI unpatch failed: {e.Message}"); }
            _ppiPatched = false;
        }

        private struct PpiCounters
        {
            public long violTicks;
            public long netTicks;
            public int distinct;
            public int warns;
            public long worstNet;
            public float worstRatio;
            public long lastNet;
            public float lastRatio;
            public string lastWarning;
        }

        private static PpiCounters PpiReadCounters()
        {
            const BindingFlags F = BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Static;
            object Read(string name) => _ppiSentinelType.GetProperty(name, F)?.GetValue(null);
            return new PpiCounters
            {
                violTicks = (long)Read("ViolationTicks"),
                netTicks = (long)Read("ViolationNetTicks"),
                distinct = (int)Read("DistinctNetCount"),
                warns = (int)Read("WarningsEmitted"),
                worstNet = (long)Read("WorstNetId"),
                worstRatio = (float)Read("WorstRatio"),
                lastNet = (long)Read("LastNetId"),
                lastRatio = (float)Read("LastRatio"),
                lastWarning = (string)Read("LastWarning"),
            };
        }
    }
}
