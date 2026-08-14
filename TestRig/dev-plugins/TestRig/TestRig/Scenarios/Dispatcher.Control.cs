using System;
using System.Collections.Generic;

namespace TestRig.Scenarios
{
    /// <summary>
    ///     The control surface the HTTP routes drive: live re-arming, transient one-shot
    ///     invocation, and the state that makes a disarmed probe a positive answer.
    ///
    ///     <para>
    ///     Everything here is a small addition to the existing dispatcher rather than a rewrite
    ///     of it, because the ~85 scenario bodies are the asset and their contract is
    ///     "called once per simulation tick, on the tick thread". None of that changes.
    ///     </para>
    /// </summary>
    internal static partial class Dispatcher
    {
        private sealed class Transient
        {
            internal string Id;
            internal long TicksLeft;
            internal long TicksRun;
        }

        private static readonly List<Transient> _transients = new List<Transient>();
        private static readonly HashSet<string> _unknown = new HashSet<string>(StringComparer.Ordinal);
        private static readonly HashSet<string> _seenThisSession = new HashSet<string>(StringComparer.Ordinal);
        private static readonly object _controlGate = new object();

        /// <summary>Simulation ticks the dispatcher has counted. NOT frames.</summary>
        internal static long TicksSeen => _ticksSeen;

        /// <summary>Ticks the dispatcher waits before firing anything after a world load.</summary>
        internal static int DelayTicks => _delayTicks;

        /// <summary>True once <see cref="Initialize"/> has run, which is at prefab load.</summary>
        internal static bool Armed => _log != null;

        /// <summary>The currently armed scenario string, live.</summary>
        internal static string ArmedString => _scenario ?? "";

        /// <summary>
        ///     Re-arms the dispatcher without a restart.
        ///
        ///     <c>Tick()</c> re-reads <c>_scenario</c> every simulation tick, so a change here
        ///     takes effect on the next tick. ScenarioRunner captured the string once at
        ///     <c>OnPrefabsLoaded</c> and never re-read it, which meant any change to what was
        ///     armed required a stop and start, and a restart ends the very session a
        ///     session-shaped test is about.
        /// </summary>
        internal static void SetArmed(string scenarios)
        {
            _scenario = scenarios ?? "";
        }

        /// <summary>
        ///     Arms <paramref name="id"/> for the next <paramref name="ticks"/> simulation ticks
        ///     and no longer. This is what <c>POST /scenario/run</c> uses.
        ///
        ///     Ticks, not frames. On the dedicated server the two clocks do not correspond:
        ///     frames advance with rendering and scenarios count simulation ticks, and
        ///     <c>OnSimTick</c> even dedupes by <c>Time.frameCount</c>. A caller waiting on a
        ///     frame budget would be measuring the wrong clock.
        /// </summary>
        internal static void RunTransient(string id, int ticks)
        {
            if (string.IsNullOrEmpty(id)) return;
            lock (_controlGate)
            {
                foreach (var t in _transients)
                {
                    if (!string.Equals(t.Id, id, StringComparison.Ordinal)) continue;
                    // Extend rather than stack: two overlapping runs of one scenario would
                    // interleave with its own _xFired guards and produce output no reader could
                    // attribute.
                    t.TicksLeft = Math.Max(t.TicksLeft, ticks);
                    return;
                }
                _transients.Add(new Transient { Id = id, TicksLeft = Math.Max(1, ticks) });
            }
        }

        /// <summary>Ticks a transient has actually been given, or -1 when it is not running.</summary>
        internal static long TransientTicksRun(string id)
        {
            lock (_controlGate)
            {
                foreach (var t in _transients)
                    if (string.Equals(t.Id, id, StringComparison.Ordinal)) return t.TicksRun;
            }
            return -1;
        }

        /// <summary>True while a transient invocation still has budget left.</summary>
        internal static bool TransientActive(string id)
        {
            lock (_controlGate)
            {
                foreach (var t in _transients)
                    if (string.Equals(t.Id, id, StringComparison.Ordinal)) return t.TicksLeft > 0;
            }
            return false;
        }

        private static void TickTransients()
        {
            Transient[] due;
            lock (_controlGate)
            {
                if (_transients.Count == 0) return;
                due = _transients.ToArray();
            }

            foreach (var t in due)
            {
                try
                {
                    TickOne(t.Id);
                }
                catch (Exception e)
                {
                    _log?.LogError($"[ScenarioRunner] scenario '{t.Id}' tick threw: {e}");
                }

                lock (_controlGate)
                {
                    t.TicksRun++;
                    t.TicksLeft--;
                    if (t.TicksLeft <= 0) _transients.Remove(t);
                }
            }
        }

        /// <summary>
        ///     Records a scenario id the switch did not recognise. See the default case in
        ///     <c>TickOne</c> for why a warning alone was not enough.
        /// </summary>
        private static void NoteUnknown(string id)
        {
            if (string.IsNullOrEmpty(id)) return;
            lock (_controlGate) { _unknown.Add(id); }
        }

        /// <summary>Every id that reached the switch and was not recognised, this session.</summary>
        internal static string[] UnknownIds()
        {
            lock (_controlGate)
            {
                var copy = new string[_unknown.Count];
                _unknown.CopyTo(copy);
                return copy;
            }
        }

        /// <summary>
        ///     Ids that have been dispatched at least once this session. Not the same as
        ///     "produced output": a one-shot whose <c>_xFired</c> guard already tripped, a
        ///     settle-gated probe that has not reached its settle tick, and a mod-specific probe
        ///     whose assembly is absent all appear here and all emit nothing. That distinction is
        ///     exactly what <c>GET /scenarios</c> exists to make visible.
        /// </summary>
        internal static string[] DispatchedIds()
        {
            lock (_controlGate)
            {
                var copy = new string[_seenThisSession.Count];
                _seenThisSession.CopyTo(copy);
                return copy;
            }
        }

        private static void NoteDispatched(string id)
        {
            if (string.IsNullOrEmpty(id)) return;
            lock (_controlGate) { _seenThisSession.Add(id); }
        }

        /// <summary>
        ///     Names of scenarios that called <c>RequireModAssembly</c> and were refused, with
        ///     the assembly they wanted. A missing mod assembly used to be one warning at one
        ///     tick and then silence forever.
        /// </summary>
        private static readonly Dictionary<string, string> _blockedByAssembly =
            new Dictionary<string, string>(StringComparer.Ordinal);

        internal static void NoteBlockedByAssembly(string id, string assembly)
        {
            if (string.IsNullOrEmpty(id)) return;
            lock (_controlGate) { _blockedByAssembly[id] = assembly ?? "(unnamed)"; }
        }

        internal static KeyValuePair<string, string>[] BlockedByAssembly()
        {
            lock (_controlGate)
            {
                var copy = new KeyValuePair<string, string>[_blockedByAssembly.Count];
                int i = 0;
                foreach (var kv in _blockedByAssembly) copy[i++] = kv;
                return copy;
            }
        }
    }
}
