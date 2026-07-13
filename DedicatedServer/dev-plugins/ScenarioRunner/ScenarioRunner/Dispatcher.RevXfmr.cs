using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using Assets.Scripts.Networks;
using Assets.Scripts.Objects;
using Assets.Scripts.Objects.Electrical;
using Assets.Scripts.Objects.Pipes;

namespace ScenarioRunner
{
    // pgp-reversed-transformer-probe
    //
    // Investigates the bug: a cable network fed by a small REVERSED transformer
    // (StructureTransformerSmallReversed) shows every device "Unpowered" while the
    // Network Analyzer reports Actual = Potential = Required (balanced). User reports
    // normal small transformers work, reversed ones do not, and that power rapidly
    // toggles on/off for ~5 s after load before settling dark.
    //
    // This probe traces the FIRST RX_TRACE_TICKS simulation ticks after load (set
    // "Delay Ticks = 1" so it starts at the first tick -- the earlier campaign's
    // Delay Ticks = 8 skipped past the transient). Per tick it logs:
    //
    //   Per small transformer (normal AND reversed):
    //     OnOff, Error, Setting, OutputMaximum, _powerProvided (private),
    //     input net ref + cable tier + PotentialLoad,
    //     output net ref + cable tier + PotentialLoad + RequiredLoad,
    //     GetGeneratedPower(OutputNetwork), GetUsedPower(InputNetwork),
    //     IsStepUp classification (PGP PowerAllocator reflection),
    //     shed / overload lockout membership.
    //
    //   Per network of interest (focus net 492209 + every small-transformer in/out net):
    //     RequiredLoad / CurrentLoad(=Actual) / PotentialLoad / ShortfallLoad,
    //     PowerTick private _isPowerMet / _powerRatio / _actual / _netPower,
    //     device powered count (PowerDeviceList: how many Powered vs total) and the
    //     type tally of the UNpowered ones.
    //
    // This discriminates the three candidate causes:
    //   #1 vanilla strict _isPowerMet boundary: _isPowerMet=F & _powerRatio=1 at Potential==Required
    //   #2 _powerProvided undershoot:           _powerRatio fractionally < 1, Potential just under Required
    //   #3 shed/overload lockout:               GetGen forced 0 -> Potential=0 (would NOT read balanced)
    // and proves the normal-vs-reversed divergence directly (powered=N/N on a normal-fed net,
    // powered=0/M on the reversed-fed focus net).
    internal static partial class Dispatcher
    {
        private const long RX_FOCUS_NET = 492209L;
        private const int RX_TRACE_TICKS = 24;
        private static int _rxLastTick = int.MinValue;

        private static void Scenario_PgpReversedTransformerProbe()
        {
            // Deliberately does NOT hard-require PowerGridPlus: this same probe is run a
            // second time with PGP DISABLED to compare vanilla vs PGP behaviour on the
            // identical save. When PGP is absent the Rx* helpers degrade gracefully
            // (class=?, shed/over=False, pgpTick=-1) and we observe pure vanilla power flow.
            // Trace only the first RX_TRACE_TICKS ticks after warmup (the transient lives here),
            // then go quiet so the log does not grow without bound.
            if (_ticksSeen > _delayTicks + RX_TRACE_TICKS) return;
            if (_ticksSeen == _rxLastTick) return;
            _rxLastTick = (int)_ticksSeen;

            var asm = GetModAssembly(PGP_ASSEMBLY);
            bool pgpLoaded = asm != null;
            int pgpTick = pgpLoaded ? ReadPgpStaticIntProp(asm, "PowerGridPlus.ElectricityTickCounter", "CurrentTick") : -1;
            if (_ticksSeen == _delayTicks)
                _log?.LogInfo($"[ScenarioRunner] RX  (PowerGridPlus loaded = {pgpLoaded})");

            if (_transformers.Count == 0) RebuildCaches();

            _log?.LogInfo($"[ScenarioRunner] RX ==== tick={_ticksSeen} pgpTick={pgpTick} ====");

            // Networks of interest: focus net + every small-transformer in/out net.
            var netsOfInterest = new Dictionary<long, CableNetwork>();
            void Note(CableNetwork n)
            {
                if (n != null && !netsOfInterest.ContainsKey(n.ReferenceId)) netsOfInterest[n.ReferenceId] = n;
            }

            foreach (var t in _transformers)
            {
                if (t == null) continue;
                var prefab = t.PrefabName ?? "";
                bool small = prefab.IndexOf("TransformerSmall", StringComparison.OrdinalIgnoreCase) >= 0;
                if (!small) continue;
                bool reversed = prefab.IndexOf("Reversed", StringComparison.OrdinalIgnoreCase) >= 0;

                Note(t.InputNetwork);
                Note(t.OutputNetwork);

                long inRef = t.InputNetwork != null ? t.InputNetwork.ReferenceId : -1;
                long outRef = t.OutputNetwork != null ? t.OutputNetwork.ReferenceId : -1;
                float inPot = t.InputNetwork != null ? t.InputNetwork.PotentialLoad : float.NaN;
                float outPot = t.OutputNetwork != null ? t.OutputNetwork.PotentialLoad : float.NaN;
                float outReq = t.OutputNetwork != null ? t.OutputNetwork.RequiredLoad : float.NaN;
                float pp = RxFloat(t, "_powerProvided");

                float gen = float.NaN, used = float.NaN;
                try { if (t.OutputNetwork != null) gen = t.GetGeneratedPower(t.OutputNetwork); } catch { }
                try { if (t.InputNetwork != null) used = t.GetUsedPower(t.InputNetwork); } catch { }

                string cls = RxStepUp(asm, t.InputNetwork, t.OutputNetwork);
                bool shed = PgpIsLocked(asm, "PowerGridPlus.BrownoutRegistry", t.ReferenceId);
                bool over = PgpIsLocked(asm, "PowerGridPlus.OverloadRegistry", t.ReferenceId);

                _log?.LogInfo(
                    $"[ScenarioRunner] RX  XFMR ref={t.ReferenceId} {(reversed ? "REVERSED" : "normal  ")} prefab={prefab} " +
                    $"OnOff={t.OnOff} Err={t.Error} Setting={t.Setting:0.##} OutMax={t.OutputMaximum:0.##} _pwrProvided={pp:0.##} " +
                    $"| in[ref={inRef} tier={RxTier(t.InputConnection)} pot={inPot:0.##}] " +
                    $"out[ref={outRef} tier={RxTier(t.OutputConnection)} pot={outPot:0.##} req={outReq:0.##}] " +
                    $"| GetGen(out)={gen:0.##} GetUsed(in)={used:0.##} class={cls} shed={shed} over={over}");
            }

            // Ensure the focus net is reported even if no small transformer touches it.
            CableNetwork.AllCableNetworks.ForEach(n => { if (n != null && n.ReferenceId == RX_FOCUS_NET) Note(n); });

            foreach (var kv in netsOfInterest)
            {
                var net = kv.Value;
                var pt = net.PowerTick;
                float ptReq = pt != null ? pt.Required : float.NaN;
                float ptPot = pt != null ? pt.Potential : float.NaN;
                string isMet = pt != null ? RxBoolStr(pt, "_isPowerMet") : "?";
                float ratio = pt != null ? RxFloat(pt, "_powerRatio") : float.NaN;
                float actual = pt != null ? RxFloat(pt, "_actual") : float.NaN;
                float netp = pt != null ? RxFloat(pt, "_netPower") : float.NaN;

                int total = 0, powered = 0;
                var unpoweredTally = new Dictionary<string, int>();
                lock (net.PowerDeviceList)
                {
                    for (int i = 0; i < net.PowerDeviceList.Count; i++)
                    {
                        var d = net.PowerDeviceList[i];
                        if (d == null) continue;
                        total++;
                        if (d.Powered) powered++;
                        else
                        {
                            var tn = d.GetType().Name;
                            unpoweredTally.TryGetValue(tn, out var c);
                            unpoweredTally[tn] = c + 1;
                        }
                    }
                }
                var sb = new StringBuilder();
                foreach (var u in unpoweredTally) sb.Append(u.Key).Append("x").Append(u.Value).Append(" ");

                _log?.LogInfo(
                    $"[ScenarioRunner] RX  NET ref={net.ReferenceId}{(net.ReferenceId == RX_FOCUS_NET ? "*FOCUS" : "")} " +
                    $"Required={net.RequiredLoad:0.##} Actual={net.CurrentLoad:0.##} Potential={net.PotentialLoad:0.##} Short={net.ShortfallLoad:0.##} " +
                    $"| pt.Req={ptReq:0.##} pt.Pot={ptPot:0.##} _isPowerMet={isMet} _powerRatio={ratio:0.###} _actual={actual:0.##} _netPower={netp:0.####} " +
                    $"| devices powered={powered}/{total} unpowered=[{sb.ToString().Trim()}]");
            }
        }

        // ---- RevXfmr-local reflection helpers (Rx prefix to avoid colliding with other partials) ----

        private static object RxFieldRaw(object obj, string name)
        {
            if (obj == null) return null;
            for (var t = obj.GetType(); t != null && t != typeof(object); t = t.BaseType)
            {
                var f = t.GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
                if (f != null) return f.GetValue(obj);
            }
            return null;
        }

        private static float RxFloat(object obj, string name)
        {
            var v = RxFieldRaw(obj, name);
            if (v is float f) return f;
            if (v is double d) return (float)d;
            return float.NaN;
        }

        private static string RxBoolStr(object obj, string name)
        {
            var v = RxFieldRaw(obj, name);
            return v is bool b ? (b ? "T" : "F") : "?";
        }

        private static string RxStepUp(Assembly asm, CableNetwork inNet, CableNetwork outNet)
        {
            try
            {
                // IsStepUp moved from PowerAllocator to the SegAdapters helper when the
                // adapters became the physical-description layer. "?" signals a miss.
                var t = asm?.GetType("PowerGridPlus.SegAdapters");
                var m = t?.GetMethod("IsStepUp",
                    BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Static,
                    null, new[] { typeof(CableNetwork), typeof(CableNetwork) }, null);
                if (m == null) return "?";
                return m.Invoke(null, new object[] { inNet, outNet }) is bool b ? (b ? "StepUp" : "down/same") : "?";
            }
            catch { return "err"; }
        }

        private static string RxTier(Connection conn)
        {
            try
            {
                var cable = conn != null ? conn.GetCable() : null;
                return cable != null ? cable.CableType.ToString() : "none";
            }
            catch { return "err"; }
        }
    }
}
