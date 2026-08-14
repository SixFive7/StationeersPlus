using System;
using System.Collections.Generic;
using System.Text;
using BepInEx.Logging;
using HarmonyLib;
using ConsoleWindow = Assets.Scripts.ConsoleWindow;

namespace TestRig
{
    internal struct TappedLine
    {
        public long Seq;
        public double Time;      // seconds since the plugin started
        public string Source;    // "console" or "bepinex"
        public string Level;     // console: the ConsoleColor name. bepinex: the LogLevel name.
        public string Text;
        public bool Truncated;
    }

    /// <summary>
    ///     Tees everything the in-game console prints, plus everything any BepInEx plugin logs, into
    ///     one sequence-numbered ring a test harness can poll.
    ///
    ///     Every ConsoleWindow print overload funnels into
    ///     <c>Print(string, ConsoleColor, bool, bool, bool)</c>: <c>PrintError</c> calls it with Red,
    ///     <c>PrintAction</c> with Yellow, and all seven GameString overloads format then delegate.
    ///     One postfix therefore catches the lot. The exceptions are <c>PrintBlock</c> /
    ///     <c>PrintTable</c> / <c>PrintSegmentedBlock*</c>, which write straight into the console
    ///     ring; read those through <c>/console/buffer</c>, which reads the game's own
    ///     <c>ConsoleWindow.ConsoleBuffer</c> array.
    ///
    ///     BOUNDED, ON THREE AXES. This tee once took a client to a 12.75 GB working set with a
    ///     frozen pump after ingesting over 500,000 lines, and a later run reported 654 dropped
    ///     lines within five minutes of a fresh launch, so the pressure is real even without an
    ///     exception storm. With N instances the risk multiplies by N. A line count alone is not
    ///     enough of a bound, because the lines that arrive during a storm are stack traces and a
    ///     single one can be megabytes: 8,000 unbounded strings is not a bounded amount of memory.
    ///     So there are three caps and all of them are reported:
    ///
    ///       <list type="number">
    ///         <item>LINES per source, the ring capacity. Oldest out first.</item>
    ///         <item>CHARACTERS per line. A longer line is truncated with a marker and counted in
    ///               <c>truncated</c>, so a stack trace costs a bounded amount and stays
    ///               recognisable.</item>
    ///         <item>CHARACTERS in total per source, a budget enforced by evicting oldest lines
    ///               until the ring is back under it. This is the cap that actually holds when the
    ///               lines are large.</item>
    ///       </list>
    ///
    ///     Dropping is never silent: <c>dropped</c> and <c>truncated</c> ride on every
    ///     <c>/console/log</c> response, and <c>/status</c> carries the live buffer size.
    /// </summary>
    internal static class ConsoleTap
    {
        // Defaults chosen so a normal session never notices them and a storm cannot grow past a few
        // megabytes per source. Overridable from config; see Plugin.cs.
        internal static int MaxLinesPerSource = 2000;
        internal static int MaxCharsPerLine = 4000;
        internal static int MaxCharsPerSource = 4 * 1024 * 1024;

        /// <summary>
        ///     One ring per source. The BepInEx side sees every Debug.Log every mod makes (BepInEx
        ///     is configured with UnityLogListening on), which during mod load is thousands of lines
        ///     in a couple of seconds. Sharing one ring with the game console would evict exactly the
        ///     lines a test cares about. The sequence counter stays global across both rings, so
        ///     <c>since</c> polling still yields a single ordered stream.
        /// </summary>
        private sealed class Ring
        {
            private TappedLine[] _slots;
            private int _count;
            private int _head;
            private long _chars;

            internal long Dropped;
            internal long Truncated;

            internal Ring(int capacity) { _slots = new TappedLine[Math.Max(16, capacity)]; }

            internal int Count { get { return _count; } }
            internal long Chars { get { return _chars; } }
            internal int Capacity { get { return _slots.Length; } }

            /// <summary>Resizes the ring, keeping the newest lines that still fit.</summary>
            internal void SetCapacity(int capacity)
            {
                capacity = Math.Max(16, capacity);
                if (capacity == _slots.Length) return;
                var kept = new List<TappedLine>();
                CopyInto(kept);
                if (kept.Count > capacity) kept.RemoveRange(0, kept.Count - capacity);
                _slots = new TappedLine[capacity];
                _count = 0; _head = 0; _chars = 0;
                foreach (var l in kept) Append(l);
            }

            internal void Add(TappedLine line)
            {
                if (line.Text != null && MaxCharsPerLine > 0 && line.Text.Length > MaxCharsPerLine)
                {
                    int kept = Math.Max(64, MaxCharsPerLine);
                    line.Text = line.Text.Substring(0, kept) +
                                "... [truncated " + (line.Text.Length - kept) + " chars]";
                    line.Truncated = true;
                    Truncated++;
                }

                if (_count == _slots.Length) DropOldest();
                Append(line);

                // The byte budget is what actually bounds this under a storm. Keep at least one
                // line so a single enormous entry still shows up rather than vanishing.
                while (MaxCharsPerSource > 0 && _chars > MaxCharsPerSource && _count > 1) DropOldest();
            }

            private void Append(TappedLine line)
            {
                _slots[_head] = line;
                _head = (_head + 1) % _slots.Length;
                if (_count < _slots.Length) _count++;
                _chars += line.Text == null ? 0 : line.Text.Length;
            }

            private void DropOldest()
            {
                if (_count == 0) return;
                int start = (_head - _count + _slots.Length) % _slots.Length;
                _chars -= _slots[start].Text == null ? 0 : _slots[start].Text.Length;
                // Release the string so the ring does not pin evicted text.
                _slots[start] = default(TappedLine);
                _count--;
                Dropped++;
            }

            internal void CopyInto(List<TappedLine> into)
            {
                int start = (_head - _count + _slots.Length) % _slots.Length;
                for (int i = 0; i < _count; i++) into.Add(_slots[(start + i) % _slots.Length]);
            }

            internal void Clear()
            {
                Array.Clear(_slots, 0, _slots.Length);
                _count = 0; _head = 0; _chars = 0; Dropped = 0; Truncated = 0;
            }
        }

        private static readonly object _gate = new object();
        private static readonly Ring _console = new Ring(2000);
        private static readonly Ring _bepInEx = new Ring(2000);
        private static long _nextSeq = 1;
        private static DateTime _epoch = DateTime.UtcNow;

        internal static bool ConsolePatchApplied;
        internal static bool BepInExListenerAttached;

        internal static long Dropped { get { lock (_gate) { return _console.Dropped + _bepInEx.Dropped; } } }
        internal static long Truncated { get { lock (_gate) { return _console.Truncated + _bepInEx.Truncated; } } }
        internal static long BufferedChars { get { lock (_gate) { return _console.Chars + _bepInEx.Chars; } } }
        internal static int BufferedLines { get { lock (_gate) { return _console.Count + _bepInEx.Count; } } }

        internal static void ResetEpoch() => _epoch = DateTime.UtcNow;

        /// <summary>Applies the configured caps. Safe at any time; resizing keeps the newest lines.</summary>
        internal static void ApplyLimits(int maxLines, int maxCharsPerLine, int maxCharsPerSource)
        {
            lock (_gate)
            {
                MaxLinesPerSource = Math.Max(16, maxLines);
                MaxCharsPerLine = Math.Max(0, maxCharsPerLine);
                MaxCharsPerSource = Math.Max(0, maxCharsPerSource);
                _console.SetCapacity(MaxLinesPerSource);
                _bepInEx.SetCapacity(MaxLinesPerSource);
            }
        }

        internal static void Add(string source, string level, string text)
        {
            if (text == null) return;
            lock (_gate)
            {
                var line = new TappedLine
                {
                    Seq = _nextSeq++,
                    Time = (DateTime.UtcNow - _epoch).TotalSeconds,
                    Source = source,
                    Level = level,
                    Text = text,
                };
                if (source == "console") _console.Add(line);
                else _bepInEx.Add(line);
            }
        }

        internal static long NextSeq { get { lock (_gate) { return _nextSeq; } } }

        internal static List<TappedLine> Snapshot(long sinceSeq, int limit, string contains, string source)
        {
            var pool = new List<TappedLine>();
            lock (_gate)
            {
                bool wantConsole = string.IsNullOrEmpty(source) ||
                                   string.Equals(source, "console", StringComparison.OrdinalIgnoreCase);
                bool wantBepInEx = string.IsNullOrEmpty(source) ||
                                   string.Equals(source, "bepinex", StringComparison.OrdinalIgnoreCase);
                if (wantConsole) _console.CopyInto(pool);
                if (wantBepInEx) _bepInEx.CopyInto(pool);
            }

            pool.Sort((a, b) => a.Seq.CompareTo(b.Seq));

            var result = new List<TappedLine>();
            foreach (var line in pool)
            {
                if (line.Seq < sinceSeq) continue;
                if (!string.IsNullOrEmpty(contains) &&
                    (line.Text == null || line.Text.IndexOf(contains, StringComparison.OrdinalIgnoreCase) < 0)) continue;
                result.Add(line);
            }
            if (limit > 0 && result.Count > limit) result.RemoveRange(0, result.Count - limit);
            return result;
        }

        internal static void Clear()
        {
            lock (_gate)
            {
                _console.Clear();
                _bepInEx.Clear();
            }
        }

        internal static string LimitsJson()
        {
            lock (_gate)
            {
                return new Json.Obj()
                    .Int("maxLinesPerSource", MaxLinesPerSource)
                    .Int("maxCharsPerLine", MaxCharsPerLine)
                    .Int("maxCharsPerSource", MaxCharsPerSource)
                    .Int("consoleLines", _console.Count)
                    .Int("consoleChars", _console.Chars)
                    .Int("bepInExLines", _bepInEx.Count)
                    .Int("bepInExChars", _bepInEx.Chars)
                    .ToString();
            }
        }

        internal static string ToJson(List<TappedLine> lines, long nextSeq)
        {
            var sb = new StringBuilder();
            sb.Append("{\"ok\":true,\"nextSeq\":").Append(nextSeq)
              .Append(",\"dropped\":").Append(Dropped)
              .Append(",\"truncated\":").Append(Truncated)
              .Append(",\"bufferedLines\":").Append(BufferedLines)
              .Append(",\"bufferedChars\":").Append(BufferedChars)
              .Append(",\"count\":").Append(lines.Count)
              .Append(",\"lines\":[");
            for (int i = 0; i < lines.Count; i++)
            {
                if (i > 0) sb.Append(',');
                var l = lines[i];
                sb.Append("{\"seq\":").Append(l.Seq)
                  .Append(",\"t\":").Append(Json.Num(l.Time))
                  .Append(",\"src\":").Append(Json.Escape(l.Source))
                  .Append(",\"level\":").Append(Json.Escape(l.Level))
                  .Append(",\"text\":").Append(Json.Escape(l.Text));
                if (l.Truncated) sb.Append(",\"truncated\":true");
                sb.Append('}');
            }
            sb.Append("]}");
            return sb.ToString();
        }

        // ---- the game's own console ring -------------------------------------

        /// <summary>
        ///     Reads <c>ConsoleWindow.ConsoleBuffer</c> directly. Index 0 is the newest line. Covers
        ///     everything printed before this plugin loaded, and the block/table printers that
        ///     bypass <c>Print</c>. Main thread only.
        /// </summary>
        internal static string ReadGameBuffer(int limit, string contains)
        {
            var buffer = ConsoleWindow.ConsoleBuffer;
            var sb = new StringBuilder();
            sb.Append("{\"ok\":true,\"lines\":[");
            int emitted = 0;
            if (buffer != null)
            {
                for (int i = 0; i < buffer.Length; i++)
                {
                    if (limit > 0 && emitted >= limit) break;
                    var line = buffer[i];
                    if (line == null) continue;
                    string text = line.Text;
                    if (string.IsNullOrEmpty(text)) continue;
                    if (line.Continuations != null && line.Continuations.Length > 0)
                        text = text + "\n" + string.Join("\n", line.Continuations);
                    if (!string.IsNullOrEmpty(contains) &&
                        text.IndexOf(contains, StringComparison.OrdinalIgnoreCase) < 0) continue;
                    if (emitted > 0) sb.Append(',');
                    sb.Append("{\"i\":").Append(i)
                      .Append(",\"time\":").Append(Json.Escape(line.Time ?? ""))
                      .Append(",\"color\":").Append(line.Color)
                      .Append(",\"text\":").Append(Json.Escape(text))
                      .Append('}');
                    emitted++;
                }
            }
            sb.Append("],\"count\":").Append(emitted)
              .Append(",\"bufferSize\":").Append(buffer == null ? 0 : buffer.Length)
              .Append('}');
            return sb.ToString();
        }

        // ---- BepInEx log listener --------------------------------------------

        private sealed class BepInExTap : ILogListener
        {
            public LogLevel LogLevelFilter => LogLevel.All;

            public void LogEvent(object sender, LogEventArgs eventArgs)
            {
                try
                {
                    // Never re-enter on our own lines, or a logging loop is one typo away.
                    var srcName = eventArgs.Source?.SourceName ?? "?";
                    if (srcName == Plugin.PluginName) return;
                    Add("bepinex", eventArgs.Level.ToString(), "[" + srcName + "] " + eventArgs.Data);
                }
                catch { }
            }

            public void Dispose() { }
        }

        private static BepInExTap _bepInExTap;

        internal static void AttachBepInExListener()
        {
            if (_bepInExTap != null) return;
            try
            {
                _bepInExTap = new BepInExTap();
                BepInEx.Logging.Logger.Listeners.Add(_bepInExTap);
                BepInExListenerAttached = true;
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning("BepInEx log listener not attached: " + ex.Message);
                _bepInExTap = null;
            }
        }

        internal static void DetachBepInExListener()
        {
            if (_bepInExTap == null) return;
            try { BepInEx.Logging.Logger.Listeners.Remove(_bepInExTap); } catch { }
            _bepInExTap = null;
            BepInExListenerAttached = false;
        }
    }

    /// <summary>
    ///     Postfix on the one console print funnel. Deliberately a postfix, so the line has already
    ///     made it to the real console before it is recorded here.
    /// </summary>
    [HarmonyPatch]
    internal static class ConsolePrintPatch
    {
        internal static System.Reflection.MethodBase TargetMethod()
        {
            return AccessTools.Method(
                typeof(ConsoleWindow),
                nameof(ConsoleWindow.Print),
                new[] { typeof(string), typeof(ConsoleColor), typeof(bool), typeof(bool), typeof(bool) });
        }

        internal static void Postfix(string output, ConsoleColor color)
        {
            try { ConsoleTap.Add("console", color.ToString(), output); }
            catch { }
        }
    }
}
