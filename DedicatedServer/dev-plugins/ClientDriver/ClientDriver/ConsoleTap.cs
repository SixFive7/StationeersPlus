using System;
using System.Collections.Generic;
using System.Text;
using BepInEx.Logging;
using HarmonyLib;
using ConsoleWindow = Assets.Scripts.ConsoleWindow;

namespace ClientDriver
{
    internal struct TappedLine
    {
        public long Seq;
        public double Time;      // seconds since the plugin started
        public string Source;    // "console" or "bepinex"
        public string Level;     // console: the ConsoleColor name. bepinex: the LogLevel name.
        public string Text;
    }

    /// <summary>
    /// Tees everything the in-game console prints, plus everything any BepInEx plugin
    /// logs, into one sequence-numbered ring buffer a test harness can poll.
    ///
    /// Every ConsoleWindow print overload funnels into
    /// <c>Print(string, ConsoleColor, bool, bool, bool)</c>: <c>PrintError</c> calls it
    /// with Red, <c>PrintAction</c> with Yellow, and all seven GameString overloads
    /// format then delegate. One postfix therefore catches the lot. The exceptions are
    /// <c>PrintBlock</c> / <c>PrintTable</c> / <c>PrintSegmentedBlock*</c>, which write
    /// straight into the console ring; read those through <c>/console/buffer</c>, which
    /// reads the game's own <c>ConsoleWindow.ConsoleBuffer</c> array.
    /// </summary>
    internal static class ConsoleTap
    {
        /// <summary>
        /// One ring per source. The BepInEx side sees every Debug.Log every mod
        /// makes (BepInEx.cfg has UnityLogListening on), which during mod load is
        /// thousands of lines in a couple of seconds. Sharing one ring with the game
        /// console would evict exactly the lines a test cares about. The sequence
        /// counter stays global across both rings so `since` polling still gives a
        /// single ordered stream.
        /// </summary>
        private sealed class Ring
        {
            private readonly TappedLine[] _slots;
            private int _count;
            private int _head;
            internal long Dropped;

            internal Ring(int capacity) { _slots = new TappedLine[capacity]; }

            internal void Add(TappedLine line)
            {
                if (_count == _slots.Length) Dropped++;
                _slots[_head] = line;
                _head = (_head + 1) % _slots.Length;
                if (_count < _slots.Length) _count++;
            }

            internal void CopyInto(List<TappedLine> into)
            {
                int start = (_head - _count + _slots.Length) % _slots.Length;
                for (int i = 0; i < _count; i++) into.Add(_slots[(start + i) % _slots.Length]);
            }

            internal void Clear() { _count = 0; _head = 0; Dropped = 0; }
        }

        private static readonly object _gate = new object();
        private static readonly Ring _console = new Ring(4000);
        private static readonly Ring _bepInEx = new Ring(4000);
        private static long _nextSeq = 1;
        private static DateTime _epoch = DateTime.UtcNow;

        internal static bool ConsolePatchApplied;
        internal static bool BepInExListenerAttached;

        internal static long Dropped { get { lock (_gate) { return _console.Dropped + _bepInEx.Dropped; } } }

        internal static void ResetEpoch() => _epoch = DateTime.UtcNow;

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

        internal static string ToJson(List<TappedLine> lines, long nextSeq)
        {
            var sb = new StringBuilder();
            sb.Append("{\"ok\":true,\"nextSeq\":").Append(nextSeq)
              .Append(",\"dropped\":").Append(Dropped)
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
                  .Append(",\"text\":").Append(Json.Escape(l.Text))
                  .Append('}');
            }
            sb.Append("]}");
            return sb.ToString();
        }

        // ---- the game's own console ring -------------------------------------

        /// <summary>
        /// Reads <c>ConsoleWindow.ConsoleBuffer</c> directly. Index 0 is the newest
        /// line. Covers everything printed before this plugin loaded, and the
        /// block/table printers that bypass <c>Print</c>. Main thread only.
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
    /// Postfix on the one console print funnel. Deliberately a postfix, so the line
    /// has already made it to the real console before it is recorded here.
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
