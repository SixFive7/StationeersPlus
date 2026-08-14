using System;
using System.Collections;
using System.Collections.Generic;
using System.Threading;
using TestRig.Scenarios;

namespace TestRig
{
    /// <summary>
    ///     The scenario endpoints. These are what turn ScenarioRunner's half of the merge from a
    ///     restart-and-grep tool into something a harness can call.
    ///
    ///     <para>
    ///     Both paths from the plan exist and neither replaces the other. Roughly seven scenarios
    ///     are genuinely load-ordered (a light freeze that has to be in force before anything
    ///     measures light, the construction-event traces, the write half of a two-phase save/load
    ///     pair, and the multi-tick state machines that need an uninterrupted tick stream from a
    ///     known start), and no HTTP call can be timed reliably against a world load. Those stay
    ///     armed at boot through <see cref="ScenarioHost"/>. Everything else is a one-shot probe
    ///     over settled state and is invoked here.
    ///     </para>
    ///
    ///     <para>
    ///     <b>How a run is observed.</b> The scenario bodies log; they do not return values. Rather
    ///     than rewrite ~85 of them, the run route brackets the invocation with the console tee's
    ///     own sequence number and returns every line the scenario produced as JSON. That kills the
    ///     failure mode where a grep targeted <c>data/server.log</c>, which carries Unity output,
    ///     while <c>[ScenarioRunner]</c> lines land in <c>install/BepInEx/LogOutput.log</c>. The
    ///     caller never picks a file.
    ///     </para>
    ///
    ///     <para>
    ///     <b>Ticks, not frames.</b> A scenario advances on <c>ElectricityManager.ElectricityTick</c>
    ///     and gates itself on <c>_ticksSeen</c>. On the dedicated server frames and simulation
    ///     ticks do not correspond, so waiting on <c>Time.frameCount</c> would be measuring a
    ///     different clock. This route waits on <c>Dispatcher.TicksSeen</c>.
    ///     </para>
    /// </summary>
    internal static partial class Router
    {
        /// <summary>Log prefix every scenario body writes. The capture filter.</summary>
        private const string ScenarioTag = "[ScenarioRunner]";

        private static HttpResponse ScenariosRoute()
        {
            // Pure managed state; no Unity call, so no main-thread hop. It answers while the world
            // is parked, which is exactly the moment a caller most wants to know why nothing fired.
            return HttpResponse.Json(ScenarioHost.Json());
        }

        private static HttpResponse ScenarioRun(IDictionary body)
        {
            string id = Json.GetStr(body, "id") ?? Json.GetStr(body, "scenario");
            if (string.IsNullOrEmpty(id))
                return HttpResponse.Error("missing 'id'. GET /scenarios lists every id, what is armed, " +
                                          "and what has been dispatched.", 400);
            id = id.Trim();

            var entry = ScenarioHost.Find(id);
            if (entry == null)
            {
                var near = ScenarioHost.Suggest(id);
                return HttpResponse.Error(
                    "unknown scenario '" + id + "'. " +
                    (near.Count == 0
                        ? "GET /scenarios lists all " + ScenarioHost.Catalogue.Length + " of them."
                        : "Did you mean one of: " + string.Join(", ", near.ToArray()) + "? " +
                          "GET /scenarios lists all " + ScenarioHost.Catalogue.Length + "."), 400);
            }

            if (!Dispatcher.Armed)
                return Fail("the scenario dispatcher is not armed yet: Prefab.OnPrefabsLoaded has not " +
                            "fired, so the prefab registry several scenarios read on their first tick is " +
                            "still empty. Wait for the world to start loading, then retry. " +
                            "GET /scenarios reports dispatcherArmed.");

            if (entry.Poller)
                return Fail("'" + id + "' is a passive request-file poller, not a one-shot probe: it sits " +
                            "armed for a whole session and does nothing until a file arrives. Arm it with " +
                            "POST /scenario/arm?id=" + id + " instead. Its work is also reachable directly: " +
                            "give-item is POST /inventory/give and config-set is POST /config/set, and both " +
                            "now run the same single implementation the poller calls.");

            int ticks = Json.GetInt(body, "ticks", entry.SuggestedTicks);
            if (ticks < 1) ticks = 1;
            int timeoutMs = Json.GetInt(body, "timeoutMs", Math.Max(30000, ticks * 2000));

            long sinceSeq = ConsoleTap.NextSeq;
            long ticksAtStart = Dispatcher.TicksSeen;

            if (entry.BootOrdered)
            {
                // Not refused, because running it late is often still informative, but the caller
                // is told plainly that what they get is not what the boot-armed run would produce.
                // Silently returning a partial result is how a probe gets believed.
            }

            Dispatcher.RunTransient(id, ticks);

            var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
            long ran = 0;
            while (DateTime.UtcNow < deadline)
            {
                ran = Dispatcher.TransientTicksRun(id);
                if (ran < 0 || ran >= ticks) break;   // -1 means it finished and was removed
                Thread.Sleep(50);
            }

            bool completed = ran < 0 || ran >= ticks;
            long ticksAdvanced = Dispatcher.TicksSeen - ticksAtStart;
            var lines = ConsoleTap.Snapshot(sinceSeq, 0, ScenarioTag, "bepinex");

            var o = new Json.Obj()
                .Bit("ok", completed && ticksAdvanced > 0)
                .Str("id", id)
                .Str("host", HostProfile.Name)
                .Int("ticksRequested", ticks)
                .Int("ticksRun", ran < 0 ? ticks : ran)
                .Int("simTicksAdvanced", ticksAdvanced)
                .Int("ticksSeen", Dispatcher.TicksSeen)
                .Bit("completed", completed)
                .Bit("bootOrdered", entry.BootOrdered)
                .Int("count", lines.Count);

            if (entry.RequiresAssembly != null) o.Str("requiresAssembly", entry.RequiresAssembly);

            var rows = new List<string>();
            foreach (var l in lines) rows.Add(new Json.Obj().Int("seq", l.Seq).Str("text", l.Text).ToString());
            o.RawArray("lines", rows);

            o.Str("verdict", Verdict(lines));

            if (ticksAdvanced == 0)
                o.Str("error",
                    "the simulation did not tick once during this call, so the scenario cannot have run. " +
                    "On a dedicated server with no client attached that is the normal state and it is " +
                    "total: measured over 287 s with Force Unpause Without Client off, the tick count " +
                    "stayed at 0 and ElectricityTick never fired once. Connect a client, or set Force " +
                    "Unpause Without Client, then retry. This says nothing about the control plane: the " +
                    "Unity main thread keeps running at about 24 Hz while the world is parked, which is " +
                    "why this endpoint answered at all.");
            else if (lines.Count == 0)
                o.Str("note",
                    "the scenario was dispatched for " + (ran < 0 ? ticks : ran) + " simulation ticks and " +
                    "emitted nothing. Three things produce that and they are all silent: a one-shot whose " +
                    "own fired-guard already tripped earlier this session, a settle-gated probe that has " +
                    "not reached its settle tick yet (raise 'ticks'), and a probe whose required mod " +
                    "assembly is not loaded. GET /scenarios tells you which of the three this is.");
            else if (entry.BootOrdered)
                o.Str("warning",
                    "'" + id + "' is load-ordered: it is meant to be armed before the world loads, and " +
                    "run here it starts from whatever state the world is already in. Treat the result as " +
                    "indicative. To run it properly: POST /scenario/arm?id=" + id + " then restart the host.");

            return HttpResponse.Json(o.ToString(), completed && ticksAdvanced > 0 ? 200 : 409);
        }

        /// <summary>
        ///     Reduces the captured lines to one word. Scenario bodies are not uniform, so this
        ///     reads the markers they all actually use rather than pretending there is a schema:
        ///     an explicit VERDICT line, then FAIL, then PASS.
        /// </summary>
        private static string Verdict(List<TappedLine> lines)
        {
            if (lines == null || lines.Count == 0) return "none";
            bool sawPass = false;
            foreach (var l in lines)
            {
                string t = l.Text ?? "";
                if (t.IndexOf("VERDICT: FAIL", StringComparison.OrdinalIgnoreCase) >= 0) return "fail";
                if (t.IndexOf("FAILURES PRESENT", StringComparison.OrdinalIgnoreCase) >= 0) return "fail";
                if (t.IndexOf("FAIL", StringComparison.Ordinal) >= 0) return "fail";
                if (t.IndexOf("VERDICT: PASS", StringComparison.OrdinalIgnoreCase) >= 0) sawPass = true;
                if (t.IndexOf("ALL PASS", StringComparison.OrdinalIgnoreCase) >= 0) sawPass = true;
                if (t.IndexOf("PASS", StringComparison.Ordinal) >= 0) sawPass = true;
            }
            return sawPass ? "pass" : "inconclusive";
        }

        private static HttpResponse ScenarioArm(IDictionary body)
        {
            string ids = Json.GetStr(body, "id") ?? Json.GetStr(body, "ids") ?? Json.GetStr(body, "scenario");
            if (string.IsNullOrEmpty(ids))
                return HttpResponse.Error("missing 'id'. Pass one id, or several separated by commas. " +
                                          "GET /scenarios lists them.", 400);

            var unknown = new List<string>();
            foreach (string part in ids.Split(',', ';'))
            {
                string one = part.Trim();
                if (one.Length == 0) continue;
                if (ScenarioHost.Find(one) == null) unknown.Add(one);
            }
            if (unknown.Count > 0)
                return HttpResponse.Error(
                    "unknown scenario id(s): " + string.Join(", ", unknown.ToArray()) +
                    ". Nothing was armed. GET /scenarios lists all " + ScenarioHost.Catalogue.Length + ".", 400);

            // persist defaults true. The whole point of moving the armed set out of BepInEx/config
            // is that it survives the session boundary the reset blanks, and an arm that does not
            // survive a restart cannot serve the load-ordered probes it exists for.
            bool persist = Json.GetBool(body, "persist", true);
            string effective = ScenarioHost.SetArmed(ids, persist);

            return HttpResponse.Json(new Json.Obj()
                .Bit("ok", true)
                .Str("armed", effective)
                .Str("armedSource", ScenarioHost.Source)
                .Str("armedFile", ScenarioHost.ArmedFile)
                .Bit("persisted", persist)
                .Bit("liveFromNextTick", true)
                .Str("note", persist
                    ? "written to armedFile, which is outside BepInEx/config and is NOT blanked by the " +
                      "rig's state reset. It takes effect on the next simulation tick without a restart, " +
                      "and again at the next boot for the load-ordered probes."
                    : "applied to this run only. It takes effect on the next simulation tick and is gone " +
                      "at the next boot, so it cannot serve a load-ordered probe.")
                .ToString());
        }

        private static HttpResponse ScenarioDisarm(IDictionary body)
        {
            bool persist = Json.GetBool(body, "persist", true);
            string effective = ScenarioHost.SetArmed("", persist);
            return HttpResponse.Json(new Json.Obj()
                .Bit("ok", true)
                .Str("armed", effective)
                .Str("armedFile", ScenarioHost.ArmedFile)
                .Bit("persisted", persist)
                .Str("note", "nothing is armed. Scenarios already dispatched this session keep whatever " +
                             "state they set; disarming stops future ticks, it does not undo a probe.")
                .ToString());
        }
    }
}
