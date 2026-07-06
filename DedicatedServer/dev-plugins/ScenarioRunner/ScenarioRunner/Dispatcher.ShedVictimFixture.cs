using System;
using System.Collections.Generic;
using System.Reflection;

namespace ScenarioRunner
{
    // Scenario: pgp-shed-victim-fixture
    //
    // Synthetic-fixture proof of PowerGridPlus's shed-victim selection policy (POWER.md
    // 8.3 / 8.3.3): tier-major best-fit-decreasing. The policy, as shipped in
    // PowerAllocator.SelectShedVictims:
    //
    //   1. Tiers go priority ASC (lowest tier sheds first); selection moves to the next
    //      tier only when the current tier is exhausted with deficit remaining.
    //   2. Within a tier, against the remaining deficit D (whole Watts): if any candidate's
    //      quantised claim covers D alone, shed the SMALLEST such claim (tie: lowest
    //      ReferenceId) and stop; else shed the LARGEST claim (tie: lowest ReferenceId),
    //      subtract it, repeat within the tier.
    //   3. Step-up candidates never shed; claims floor to whole Watts ((int)Math.Floor);
    //      the deficit rounds up after the allocator's Eps tolerance
    //      ((int)Math.Ceiling(deficit - Eps)). Quantisation lives INSIDE the function
    //      (float claims in), so the quantisation checks here cover the real site.
    //
    // The selector is a pure static function of (candidates, deficit) with no live-net or
    // Unity dependency, exactly so this fixture can drive it headless with synthetic
    // candidate tuples: no save, no cable networks, no allocator tick required. The
    // fixture reflection-resolves PowerGridPlus.PowerAllocator.SelectShedVictims
    // (internal static; IReadOnlyList<(long refId, int priority, float claim, bool
    // stepUp)>, float -> List<long>; ValueTuple identity is shared via mscorlib) and
    // asserts exact ordered victim sets for 16 checks: the POWER.md 8.3.3 worked example,
    // exact-cover preference, multi-victim spill, both tie rules, tier precedence,
    // step-up exclusion, floor/ceil quantisation, and the degenerate shapes (zero /
    // negative / Eps deficit, empty candidates, insufficient total, sub-Watt claims).
    //
    // Emits "[ScenarioRunner] SVF P<n> PASS|FAIL ..." per check, an
    // "SVF END pass=N fail=M total=K" summary, and a single grep-able
    // "[ScenarioRunner] [SVF] VERDICT result=PASS|FAIL pass=N fail=M" verdict line.
    // One-shot; managed-state only; worker-safe; mutates nothing.
    internal static partial class Dispatcher
    {
        private static bool _svfFired;

        private static void Scenario_PgpShedVictimFixture()
        {
            if (!RequireModAssembly(PGP_ASSEMBLY, "pgp-shed-victim-fixture")) return;
            if (_svfFired) return;
            _svfFired = true;

            int pass = 0;
            int fail = 0;

            try
            {
                var asm = GetModAssembly(PGP_ASSEMBLY);
                var allocatorType = asm?.GetType("PowerGridPlus.PowerAllocator");
                var select = allocatorType?.GetMethod("SelectShedVictims",
                    BindingFlags.NonPublic | BindingFlags.Static);
                if (select == null)
                {
                    _log?.LogError("[ScenarioRunner] SVF FAIL: PowerGridPlus.PowerAllocator.SelectShedVictims not resolvable (renamed or removed?).");
                    fail++;
                    return;
                }
                var pars = select.GetParameters();
                if (pars.Length != 2 || pars[1].ParameterType != typeof(float))
                {
                    _log?.LogError($"[ScenarioRunner] SVF FAIL: SelectShedVictims signature changed (params={pars.Length}); fixture expects (IReadOnlyList<(long,int,float,bool)>, float).");
                    fail++;
                    return;
                }
                _log?.LogInfo("[ScenarioRunner] SVF START shed-victim fixture: resolved PowerGridPlus.PowerAllocator.SelectShedVictims; 16 checks.");

                // P1: the POWER.md 8.3.3 worked example. Same tier, claims 500/1000/2000
                // (RefIds ascending in that order), deficit 1000 -> exactly the 1000 W
                // device, one shed (the old flat walk shed 500 then 1000).
                SvfCase(select, "P1", "spec-8.3.3 500/1000/2000 D=1000 -> the 1000 W device only",
                    new (long, int, float, bool)[] { (101, 100, 500f, false), (102, 100, 1000f, false), (103, 100, 2000f, false) },
                    1000f, new long[] { 102 }, ref pass, ref fail);

                // P2: exact cover preferred over both the tighter-but-short and the looser cover.
                SvfCase(select, "P2", "exact-cover preference 999/1000/1001 D=1000 -> the 1000",
                    new (long, int, float, bool)[] { (201, 100, 999f, false), (202, 100, 1000f, false), (203, 100, 1001f, false) },
                    1000f, new long[] { 202 }, ref pass, ref fail);

                // P3: no single cover -> largest first, then the smallest cover of the rest.
                SvfCase(select, "P3", "no-single-cover 500/700 D=1000 -> 700 then 500",
                    new (long, int, float, bool)[] { (301, 100, 500f, false), (302, 100, 700f, false) },
                    1000f, new long[] { 302, 301 }, ref pass, ref fail);

                // P4: smallest-covering tie breaks to the lowest ReferenceId; one victim only.
                SvfCase(select, "P4", "smallest-cover tie 1000/1000 D=800 -> lower RefId only",
                    new (long, int, float, bool)[] { (401, 100, 1000f, false), (402, 100, 1000f, false) },
                    800f, new long[] { 401 }, ref pass, ref fail);

                // P5: largest tie breaks to the lowest ReferenceId; both shed, lower first.
                SvfCase(select, "P5", "largest tie 700/700 D=2000 -> both, lower RefId first",
                    new (long, int, float, bool)[] { (501, 100, 700f, false), (502, 100, 700f, false) },
                    2000f, new long[] { 501, 502 }, ref pass, ref fail);

                // P6: tier precedence. Tier A (priority 10) exhausts first even though tier B
                // (priority 90) covers alone. Input lists B first: order-independence too.
                SvfCase(select, "P6", "tier precedence A{200} B{5000} D=1000 -> 200 then 5000",
                    new (long, int, float, bool)[] { (602, 90, 5000f, false), (601, 10, 200f, false) },
                    1000f, new long[] { 601, 602 }, ref pass, ref fail);

                // P7: a step-up candidate with a covering claim is never selected; selection
                // falls through to best-fit over the remaining candidates.
                SvfCase(select, "P7", "step-up exclusion stepUp{5000}+{500,700} D=1000 -> 700 then 500",
                    new (long, int, float, bool)[] { (701, 100, 5000f, true), (702, 100, 500f, false), (703, 100, 700f, false) },
                    1000f, new long[] { 703, 702 }, ref pass, ref fail);

                // P8: quantisation site (folded into the selector). 999.6 floors to 999 (does
                // not cover D=1000); 1000.4 floors to 1000 (covers) -> the 1000.4 device.
                SvfCase(select, "P8", "quantisation 999.6/1000.4 D=1000 -> the 1000.4 device (floor to 999/1000)",
                    new (long, int, float, bool)[] { (801, 100, 999.6f, false), (802, 100, 1000.4f, false) },
                    1000f, new long[] { 802 }, ref pass, ref fail);

                // P9a-P9d: degenerate shapes.
                SvfCase(select, "P9a", "zero deficit -> empty set",
                    new (long, int, float, bool)[] { (901, 100, 500f, false) },
                    0f, new long[0], ref pass, ref fail);
                SvfCase(select, "P9b", "negative deficit -> empty set",
                    new (long, int, float, bool)[] { (902, 100, 500f, false) },
                    -250f, new long[0], ref pass, ref fail);
                SvfCase(select, "P9c", "empty candidates -> empty set",
                    new (long, int, float, bool)[0],
                    1000f, new long[0], ref pass, ref fail);
                SvfCase(select, "P9d", "insufficient total 300/200/100 D=1000 -> everything, largest first",
                    new (long, int, float, bool)[] { (911, 100, 300f, false), (912, 100, 200f, false), (913, 100, 100f, false) },
                    1000f, new long[] { 911, 912, 913 }, ref pass, ref fail);

                // P10: the cover rule applies to the REMAINING deficit after a tier spill:
                // tier 10 {400} spills 600 into tier 20 {300, 900} -> 900 covers; 300 survives.
                SvfCase(select, "P10", "second-tier best-fit A{400} B{300,900} D=1000 -> 400 then 900",
                    new (long, int, float, bool)[] { (1001, 10, 400f, false), (1002, 20, 300f, false), (1003, 20, 900f, false) },
                    1000f, new long[] { 1001, 1003 }, ref pass, ref fail);

                // P11: a deficit at or under the allocator's Eps tolerance (0.01 W) sheds
                // nothing, mirroring the live loop's claims > budget + Eps gate.
                SvfCase(select, "P11", "Eps-tolerance deficit 0.01 -> empty set",
                    new (long, int, float, bool)[] { (1101, 100, 500f, false) },
                    0.01f, new long[0], ref pass, ref fail);

                // P12: a fractional deficit above Eps rounds UP to 1 whole Watt, so one victim
                // sheds (never an under-shed that would leave Unmet > Eps); tie -> lowest RefId.
                SvfCase(select, "P12", "fractional deficit 0.8 rounds up 500.9/500.9 -> lower RefId only",
                    new (long, int, float, bool)[] { (1201, 100, 500.9f, false), (1202, 100, 500.9f, false) },
                    0.8f, new long[] { 1201 }, ref pass, ref fail);

                // P13: a sub-Watt claim (0.5 quantises to 0) can never cover, sheds last in the
                // exhaustion path, and cannot loop the selector.
                SvfCase(select, "P13", "sub-Watt claim 0.5+100 D=500 -> 100 then 0.5 (exhaustion)",
                    new (long, int, float, bool)[] { (1301, 100, 0.5f, false), (1302, 100, 100f, false) },
                    500f, new long[] { 1302, 1301 }, ref pass, ref fail);
            }
            catch (Exception e)
            {
                _log?.LogError($"[ScenarioRunner] SVF threw: {e}");
                fail++;
            }
            finally
            {
                _log?.LogInfo($"[ScenarioRunner] SVF END pass={pass} fail={fail} total={pass + fail}");
                bool verdict = fail == 0 && pass > 0;
                _log?.LogInfo($"[ScenarioRunner] [SVF] VERDICT result={(verdict ? "PASS" : "FAIL")} pass={pass} fail={fail}");
            }
        }

        // Invokes the reflected selector with one synthetic candidate set and asserts the
        // exact ordered victim list. Candidates are (refId, priority, claim, stepUp) value
        // tuples; List<ValueTuple<long,int,float,bool>> satisfies the selector's
        // IReadOnlyList parameter because both assemblies share mscorlib's ValueTuple.
        private static void SvfCase(MethodInfo select, string caseId, string label,
            (long refId, int priority, float claim, bool stepUp)[] candidates, float deficit,
            long[] expected, ref int pass, ref int fail)
        {
            var input = new List<(long, int, float, bool)>(candidates.Length);
            for (int i = 0; i < candidates.Length; i++) input.Add(candidates[i]);

            List<long> actual;
            try
            {
                actual = (List<long>)select.Invoke(null, new object[] { input, deficit });
            }
            catch (Exception e)
            {
                var root = e.InnerException ?? e;
                _log?.LogError($"[ScenarioRunner] SVF {caseId} FAIL: {label}: invoke threw {root.GetType().Name}: {root.Message}");
                fail++;
                return;
            }

            bool ok = actual != null && actual.Count == expected.Length;
            if (ok)
                for (int i = 0; i < expected.Length; i++)
                    if (actual[i] != expected[i]) { ok = false; break; }

            string actualStr = actual == null ? "null" : "[" + string.Join(",", actual) + "]";
            string expectedStr = "[" + string.Join(",", expected) + "]";
            if (ok)
            {
                _log?.LogInfo($"[ScenarioRunner] SVF {caseId} PASS: {label}: victims={actualStr} deficit={deficit}.");
                pass++;
            }
            else
            {
                _log?.LogError($"[ScenarioRunner] SVF {caseId} FAIL: {label}: victims={actualStr}, expected {expectedStr}, deficit={deficit}.");
                fail++;
            }
        }
    }
}
