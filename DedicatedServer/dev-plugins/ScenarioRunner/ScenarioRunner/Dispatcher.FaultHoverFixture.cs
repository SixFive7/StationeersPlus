using System;
using System.Reflection;
using System.Text;
using Assets.Scripts.Objects;
using Assets.Scripts.Objects.Electrical;

namespace ScenarioRunner
{
    // Scenario: pgp-fault-hover-fixture
    //
    // Headless proof that FaultHover.TryGetMergedBlock renders the widened fault wording.
    // FaultHover accepts a null Thing (the "On - " switch prefix is simply omitted), so every
    // kind except the throttle info state is driven with a null Thing: seed the owning registry
    // via its Note* method, resolve TryGetMergedBlock via reflection, and assert the returned
    // block string carries the locked-template wording. Each kind uses its own synthetic refId so
    // FaultHover.ActiveFault never crosses kinds (precedence is proven by pgp-overload-split-fixture
    // and pgp-pt-hover-all).
    //
    // Registry payloads (POWER.md 11.1). OverloadRegistry: ValueW = downstream demand, CapW =
    // available total, StorageW = the internal-storage slice of CapW, so the hover splits the cap
    // into an upstream part (CapW - StorageW) and a teal internal-storage part. DeprioritizedRegistry:
    // ShortfallW = the decision-time deficit at this victim's cut, VictimPriority = the device's own
    // priority, Reason = which of the three DeprioritizeReason cases the allocator recorded.
    //
    // The throttle info state cannot render with a null Thing (TransformerThrottleHover needs a live
    // Transformer whose Setting sits below OutputMaximum), so it uses a scene transformer with a
    // transient Setting throttle, restored afterwards (the pgp-pt-hover-all P13 pattern); it SKIPs
    // (never FAILs) when the save has no transformer.
    //
    // Assertions run against a tag-stripped copy of the block for wording (a " - Name" bullet has a
    // </color><color> seam between the dash and the name) and against the raw block for the specific
    // colour tags the task pins (teal #2DD4BF, logic-orange #FF8000).
    //
    // Emits "FHF P<n> PASS/FAIL" lines and "[ScenarioRunner] FHF SUMMARY pass=N fail=M". Synthetic
    // refs live in a 99000004xx band. Managed-state reflection only; worker-safe; needs no save (the
    // throttle case SKIPs on a fresh -New with no transformer). Clears the fault registries
    // transiently between kinds like pgp-pt-hover-all; live faults recompute on the next tick.
    internal static partial class Dispatcher
    {
        private static bool _fhfFired;
        private static int _fhfPass;
        private static int _fhfFail;

        // Synthetic ids (99000004xx band), one per kind so ActiveFault never crosses kinds.
        private const long FHF_OVER_STORAGE = 9900000401L;
        private const long FHF_OVER_NOSTORE = 9900000402L;
        private const long FHF_CABLE = 9900000403L;
        private const long FHF_DEP_LOWER = 9900000411L;
        private const long FHF_DEP_BESTFIT = 9900000412L;
        private const long FHF_DEP_LARGEST = 9900000413L;
        private const long FHF_MISMATCH = 9900000421L;
        private const long FHF_CYCLE = 9900000431L;
        private const long FHF_DEADINPUT = 9900000441L;

        private static MethodInfo _fhfTryGetMerged;      // FaultHover.TryGetMergedBlock(long, int, Thing, out string, out Kind)
        private static MethodInfo _fhfNoteDeprioritized;  // 8-arg NoteDeprioritized (needs the enum type)
        private static Type _fhfReasonEnum;               // PowerGridPlus.DeprioritizeReason

        private static void FhfCheck(string id, bool ok, string detail)
        {
            if (ok) { _fhfPass++; _log?.LogInfo($"[ScenarioRunner] FHF {id} PASS: {detail}"); }
            else { _fhfFail++; _log?.LogError($"[ScenarioRunner] FHF {id} FAIL: {detail}"); }
        }

        private static void Scenario_PgpFaultHoverFixture()
        {
            if (_fhfFired) return;
            if (!RequireModAssembly(PGP_ASSEMBLY, "pgp-fault-hover-fixture")) return;
            _fhfFired = true;
            _fhfPass = 0;
            _fhfFail = 0;

            var asm = GetModAssembly(PGP_ASSEMBLY);
            try
            {
                _log?.LogInfo("[ScenarioRunner] FHF START fault-hover-fixture");

                var hoverT = asm?.GetType("PowerGridPlus.FaultHover");
                _fhfTryGetMerged = hoverT?.GetMethod("TryGetMergedBlock",
                    BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Static);
                _fhfReasonEnum = asm?.GetType("PowerGridPlus.DeprioritizeReason");
                var depT = asm?.GetType("PowerGridPlus.DeprioritizedRegistry");
                _fhfNoteDeprioritized = (_fhfReasonEnum == null || depT == null) ? null : depT.GetMethod("NoteDeprioritized",
                    BindingFlags.NonPublic | BindingFlags.Static, null,
                    new[]
                    {
                        typeof(long), typeof(int), typeof(float), typeof(float), typeof(float),
                        typeof(float), _fhfReasonEnum, typeof(int)
                    }, null);
                int tick = PgpTick(asm);

                bool seamsOk = _fhfTryGetMerged != null && _fhfReasonEnum != null && _fhfNoteDeprioritized != null;
                FhfCheck("P1", seamsOk,
                    $"seams: FaultHover.TryGetMergedBlock={_fhfTryGetMerged != null} DeprioritizeReason={_fhfReasonEnum != null} " +
                    $"NoteDeprioritized(8-arg)={_fhfNoteDeprioritized != null}.");
                if (!seamsOk)
                {
                    _log?.LogInfo($"[ScenarioRunner] FHF SUMMARY pass={_fhfPass} fail={_fhfFail}");
                    return;
                }

                FhfDeviceOverload(asm, tick);
                FhfCableOverload(asm, tick);
                FhfDeprioritized(asm, tick);
                FhfCurrentMismatch(asm, tick);
                FhfCycle(asm, tick);
                FhfDeadInput(asm, tick);
                FhfThrottled(asm, tick);
                PgpClearAllFaults(asm);
            }
            catch (Exception e)
            {
                _log?.LogError($"[ScenarioRunner] FHF threw: {e}");
                _fhfFail++;
                try { PgpClearAllFaults(asm); } catch { }
            }
            _log?.LogInfo($"[ScenarioRunner] FHF SUMMARY pass={_fhfPass} fail={_fhfFail}");
        }

        // Render TryGetMergedBlock(refId, tick, thing, out block, out kind); returns the block text
        // and the resolved kind name (or "<null>").
        private static bool FhfRender(long refId, int tick, Thing thing, out string block, out string kind)
        {
            var args = new object[] { refId, tick, thing, null, null };
            bool got = _fhfTryGetMerged.Invoke(null, args) is bool b && b;
            block = args[3] as string ?? "";
            kind = args[4]?.ToString() ?? "<null>";
            return got;
        }

        // Strip Unity rich-text <...> tags so a wording assertion is not defeated by an intervening
        // <color> tag; colour-tag presence is asserted against the raw block instead.
        private static string FhfStrip(string s)
        {
            if (string.IsNullOrEmpty(s)) return s ?? "";
            var sb = new StringBuilder(s.Length);
            bool inTag = false;
            foreach (char c in s)
            {
                if (c == '<') { inTag = true; continue; }
                if (c == '>') { inTag = false; continue; }
                if (!inTag) sb.Append(c);
            }
            return sb.ToString();
        }

        // ---- Device overloaded (capacity family): storage vs no-storage breakdown ----

        private static void FhfDeviceOverload(Assembly asm, int tick)
        {
            // storageW > 0 and (capW - storageW) > 0: the teal internal-storage breakdown renders.
            PgpClearAllFaults(asm);
            PgpNoteOverload(asm, FHF_OVER_STORAGE, tick, 3000f, 1800f, 800f);   // upstream 1000 + storage 800
            bool gotS = FhfRender(FHF_OVER_STORAGE, tick, null, out string blockS, out string kindS);
            string plainS = FhfStrip(blockS);
            bool okStorage = gotS && kindS == "DeviceOverload"
                && plainS.Contains("Downstream demand of")
                && plainS.Contains("internal storage = ")
                && blockS.Contains("#2DD4BF")
                && plainS.Contains("Short by");
            FhfCheck("P2a", okStorage,
                $"device overload with storage renders 'Downstream demand of' + the teal 'internal storage = ' breakdown (#2DD4BF) + 'Short by' " +
                $"(kind={kindS} block='{Truncate(blockS, 240)}').");

            // storageW == 0: no internal-storage line, no teal tag, still Downstream demand + Short by.
            PgpClearAllFaults(asm);
            PgpNoteOverload(asm, FHF_OVER_NOSTORE, tick, 3000f, 1800f, 0f);
            bool gotN = FhfRender(FHF_OVER_NOSTORE, tick, null, out string blockN, out string kindN);
            string plainN = FhfStrip(blockN);
            bool okNoStore = gotN && kindN == "DeviceOverload"
                && plainN.Contains("Downstream demand of")
                && !plainN.Contains("internal storage")
                && !blockN.Contains("#2DD4BF")
                && plainN.Contains("Short by");
            FhfCheck("P2b", okNoStore,
                $"device overload with zero storage omits the internal-storage line and keeps 'Downstream demand of' + 'Short by' " +
                $"(kind={kindN} block='{Truncate(blockN, 240)}').");
            PgpClearAllFaults(asm);
        }

        // ---- Cable overloaded (cable-overflow family) ----

        private static void FhfCableOverload(Assembly asm, int tick)
        {
            PgpClearAllFaults(asm);
            PgpNoteCableOverload(asm, FHF_CABLE, tick, 5000f, 2000f);   // 5 kW flow onto a 2 kW wire
            bool got = FhfRender(FHF_CABLE, tick, null, out string block, out string kind);
            string plain = FhfStrip(block);
            bool ok = got && kind == "CableOverload"
                && plain.Contains("onto a wire rated for")
                && plain.Contains("Overloaded by");
            FhfCheck("P3", ok,
                $"cable overload renders 'onto a wire rated for' + 'Overloaded by' (kind={kind} block='{Truncate(block, 240)}').");
            PgpClearAllFaults(asm);
        }

        // ---- Deprioritized: the shared demand/supply/shortfall lines plus each reason case ----

        private static void FhfDeprioritized(Assembly asm, int tick)
        {
            FhfDeprioritizedCase("P4a", asm, tick, FHF_DEP_LOWER, 0, 80, new[]
            {
                "Upstream demand of", "exceeded the upstream supply of",
                "Due to the shortfall of", "share was cut",
                "Its priority of 80 was below other consumers",
            });
            FhfDeprioritizedCase("P4b", asm, tick, FHF_DEP_BESTFIT, 1, 50, new[]
            {
                "Its priority was equal to other consumers",
                "Its draw alone covered the remaining shortfall",
            });
            FhfDeprioritizedCase("P4c", asm, tick, FHF_DEP_LARGEST, 2, 50, new[]
            {
                "No single consumer's draw could cover the shortfall",
                "Its draw was the largest consumer",
            });
        }

        private static void FhfDeprioritizedCase(string id, Assembly asm, int tick, long refId,
            int reasonValue, int victimPriority, string[] wants)
        {
            PgpClearAllFaults(asm);
            object reason = Enum.ToObject(_fhfReasonEnum, reasonValue);
            // NoteDeprioritized(refId, tick, needsW, upstreamDemandW, upstreamSupplyW, shortfallW, reason, victimPriority)
            _fhfNoteDeprioritized.Invoke(null, new object[]
                { refId, tick, 5000f, 30000f, 20000f, 8000f, reason, victimPriority });
            bool got = FhfRender(refId, tick, null, out string block, out string kind);
            string plain = FhfStrip(block);
            bool ok = got && kind == "Deprioritized";
            string missing = "";
            foreach (var w in wants)
                if (!plain.Contains(w)) { ok = false; missing += " [" + w + "]"; }
            FhfCheck(id, ok,
                $"deprioritized reason={reasonValue} renders its lines (kind={kind} missing={(missing == "" ? "none" : missing)} block='{Truncate(block, 280)}').");
            PgpClearAllFaults(asm);
        }

        // ---- Current mismatch: the transformer hint plus the incompatible-devices block ----

        private static void FhfCurrentMismatch(Assembly asm, int tick)
        {
            PgpClearAllFaults(asm);
            PgpNoteVvf(asm, FHF_MISMATCH, tick, "A\nB\nC\n+4");
            bool got = FhfRender(FHF_MISMATCH, tick, null, out string block, out string kind);
            string plain = FhfStrip(block);
            bool ok = got && kind == "CurrentMismatch"
                && plain.Contains("Put a transformer between the generator DC and the AC grid")
                && plain.Contains("Incompatible devices on this network:")
                && plain.Contains(" - A")
                && plain.Contains(" - And 4 more devices...");
            FhfCheck("P5", ok,
                $"current mismatch renders the transformer hint, the incompatible-devices header, a ' - A' bullet, and the '+4' overflow row " +
                $"(kind={kind} block='{Truncate(block, 280)}').");
            PgpClearAllFaults(asm);
        }

        // ---- Cycle ----

        private static void FhfCycle(Assembly asm, int tick)
        {
            PgpClearAllFaults(asm);
            PgpNoteCycle(asm, FHF_CYCLE, tick);
            bool got = FhfRender(FHF_CYCLE, tick, null, out string block, out string kind);
            string plain = FhfStrip(block);
            bool ok = got && kind == "CycleFault"
                && plain.Contains("power loop that feeds back into itself");
            FhfCheck("P6", ok,
                $"cycle fault renders the power-loop line (kind={kind} block='{Truncate(block, 200)}').");
            PgpClearAllFaults(asm);
        }

        // ---- Dead input (info state: no registry fault, just the mark, so ActiveFault stays None) ----

        private static void FhfDeadInput(Assembly asm, int tick)
        {
            PgpClearAllFaults(asm);
            PgpMarkDeadInput(asm, FHF_DEADINPUT);
            bool got = FhfRender(FHF_DEADINPUT, tick, null, out string block, out string kind);
            string plain = FhfStrip(block);
            bool ok = got && kind == "DeadInput"
                && plain.Contains("No power is reaching this device from upstream");
            FhfCheck("P7", ok,
                $"dead-input cue renders the no-upstream line (kind={kind} block='{Truncate(block, 200)}').");
            PgpClearAllFaults(asm);
        }

        // ---- Throttled (info state; needs a live Transformer with Setting < OutputMaximum) ----

        private static void FhfThrottled(Assembly asm, int tick)
        {
            if (_transformers.Count == 0) RebuildCaches();
            Transformer xf = null;
            foreach (var t in _transformers) { if (t != null) { xf = t; break; } }
            if (xf == null)
            {
                _log?.LogInfo("[ScenarioRunner] FHF P8 SKIP: no live transformer in the scene to drive the throttle info state.");
                return;
            }

            PgpClearAllFaults(asm);
            double savedSetting = xf.Setting;
            double max = xf.OutputMaximum;
            try
            {
                // Throttle below rated so TransformerThrottleHover.TryGetThrottle fires; a real
                // Setting write on the worker, restored in the finally (the pgp-pt-hover-all P13 pattern).
                double throttled = Math.Max(0.0, max - 2000.0);
                xf.Setting = throttled;
                long refId = PgpResolveFaultRefId(asm, xf);
                bool got = FhfRender(refId, tick, xf, out string block, out string kind);
                string plain = FhfStrip(block);
                bool ok = got && kind == "Throttled"
                    && plain.Contains("maximum by the")
                    && block.Contains("#FF8000")
                    && plain.Contains("Use IC10 to set the Setting value");
                FhfCheck("P8", ok,
                    $"throttled transformer renders 'maximum by the' + the orange Setting tag (#FF8000) + 'Use IC10 to set the Setting value' " +
                    $"(kind={kind} setting={throttled:0} max={max:0} block='{Truncate(block, 280)}').");
            }
            finally
            {
                try { xf.Setting = savedSetting; }
                catch (Exception e) { _log?.LogWarning($"[ScenarioRunner] FHF P8 could not restore transformer {xf.ReferenceId} Setting to {savedSetting}: {e.GetBaseException().Message}"); }
                PgpClearAllFaults(asm);
            }
        }
    }
}
