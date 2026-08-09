using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace ClientDriver
{
    /// <summary>
    /// Minimal JSON writer and reader. Deliberately hand rolled: the game ships no
    /// JSON library a BepInEx plugin can safely reference, and the payloads here are
    /// tiny and flat. Numbers always use the invariant culture so a machine on a
    /// comma-decimal locale still emits parseable JSON.
    /// </summary>
    internal static class Json
    {
        // ---- writing ---------------------------------------------------------

        public static string Escape(string s)
        {
            if (s == null) return "null";
            var sb = new StringBuilder(s.Length + 8);
            sb.Append('"');
            foreach (char c in s)
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\b': sb.Append("\\b"); break;
                    case '\f': sb.Append("\\f"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < ' ' || c > '~') sb.Append("\\u").Append(((int)c).ToString("x4"));
                        else sb.Append(c);
                        break;
                }
            }
            sb.Append('"');
            return sb.ToString();
        }

        public static string Num(float f)
        {
            if (float.IsNaN(f) || float.IsInfinity(f)) return "null";
            return f.ToString("R", CultureInfo.InvariantCulture);
        }

        public static string Num(double d)
        {
            if (double.IsNaN(d) || double.IsInfinity(d)) return "null";
            return d.ToString("R", CultureInfo.InvariantCulture);
        }

        public static string Bool(bool b) => b ? "true" : "false";

        /// <summary>Writes an object literal from an ordered list of already-encoded values.</summary>
        public sealed class Obj
        {
            private readonly StringBuilder _sb = new StringBuilder("{");
            private bool _first = true;

            private Obj Sep()
            {
                if (_first) _first = false;
                else _sb.Append(',');
                return this;
            }

            public Obj Raw(string key, string encodedValue)
            {
                Sep()._sb.Append(Escape(key)).Append(':').Append(encodedValue ?? "null");
                return this;
            }

            public Obj Str(string key, string value) => Raw(key, value == null ? "null" : Escape(value));
            public Obj Int(string key, long value) => Raw(key, value.ToString(CultureInfo.InvariantCulture));
            public Obj Flt(string key, float value) => Raw(key, Num(value));
            public Obj Dbl(string key, double value) => Raw(key, Num(value));
            public Obj Bit(string key, bool value) => Raw(key, Bool(value));

            public Obj Vec(string key, UnityEngine.Vector3 v) =>
                Raw(key, "[" + Num(v.x) + "," + Num(v.y) + "," + Num(v.z) + "]");

            public Obj StrArray(string key, IEnumerable<string> values)
            {
                var sb = new StringBuilder("[");
                bool first = true;
                if (values != null)
                {
                    foreach (var v in values)
                    {
                        if (!first) sb.Append(',');
                        first = false;
                        sb.Append(Escape(v));
                    }
                }
                sb.Append(']');
                return Raw(key, sb.ToString());
            }

            public override string ToString() => _sb.ToString() + "}";
        }

        // ---- reading ---------------------------------------------------------
        //
        // A tolerant recursive-descent parser producing Dictionary<string, object>,
        // List<object>, string, double, bool, or null. Good enough for request
        // bodies this plugin defines itself.

        public static object Parse(string text)
        {
            if (string.IsNullOrEmpty(text)) return null;
            int i = 0;
            var value = ParseValue(text, ref i);
            return value;
        }

        public static Dictionary<string, object> ParseObject(string text)
        {
            return Parse(text) as Dictionary<string, object> ?? new Dictionary<string, object>();
        }

        private static void SkipWs(string s, ref int i)
        {
            while (i < s.Length && char.IsWhiteSpace(s[i])) i++;
        }

        private static object ParseValue(string s, ref int i)
        {
            SkipWs(s, ref i);
            if (i >= s.Length) return null;
            char c = s[i];
            if (c == '{') return ParseObj(s, ref i);
            if (c == '[') return ParseArr(s, ref i);
            if (c == '"') return ParseStr(s, ref i);
            if (s.Length - i >= 4 && string.CompareOrdinal(s, i, "true", 0, 4) == 0) { i += 4; return true; }
            if (s.Length - i >= 5 && string.CompareOrdinal(s, i, "false", 0, 5) == 0) { i += 5; return false; }
            if (s.Length - i >= 4 && string.CompareOrdinal(s, i, "null", 0, 4) == 0) { i += 4; return null; }
            int start = i;
            while (i < s.Length && (char.IsDigit(s[i]) || s[i] == '-' || s[i] == '+' || s[i] == '.' || s[i] == 'e' || s[i] == 'E')) i++;
            if (i == start) { i++; return null; }
            double d;
            double.TryParse(s.Substring(start, i - start), NumberStyles.Float, CultureInfo.InvariantCulture, out d);
            return d;
        }

        private static Dictionary<string, object> ParseObj(string s, ref int i)
        {
            var result = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
            i++; // {
            SkipWs(s, ref i);
            if (i < s.Length && s[i] == '}') { i++; return result; }
            while (i < s.Length)
            {
                SkipWs(s, ref i);
                if (i >= s.Length || s[i] != '"') break;
                string key = ParseStr(s, ref i);
                SkipWs(s, ref i);
                if (i < s.Length && s[i] == ':') i++;
                result[key] = ParseValue(s, ref i);
                SkipWs(s, ref i);
                if (i < s.Length && s[i] == ',') { i++; continue; }
                if (i < s.Length && s[i] == '}') { i++; break; }
                break;
            }
            return result;
        }

        private static List<object> ParseArr(string s, ref int i)
        {
            var result = new List<object>();
            i++; // [
            SkipWs(s, ref i);
            if (i < s.Length && s[i] == ']') { i++; return result; }
            while (i < s.Length)
            {
                result.Add(ParseValue(s, ref i));
                SkipWs(s, ref i);
                if (i < s.Length && s[i] == ',') { i++; continue; }
                if (i < s.Length && s[i] == ']') { i++; break; }
                break;
            }
            return result;
        }

        /// <summary>
        /// Reads a string literal, preserving a lone backslash that is not part of a
        /// recognised escape.
        ///
        /// This is a deliberate divergence from strict JSON and it is the fix for a
        /// real data-loss bug. A Windows path in a hand-written body,
        /// <c>{"path": "C:\Rig\Scratch"}</c>, is not valid JSON: <c>\S</c> and <c>\r</c>
        /// are an unknown escape and a carriage return. A strict reader answers
        /// <c>C:Source[CR]ig</c>, and the previous version of this method did exactly
        /// that, silently. <c>/savepath</c> is the endpoint that decides where a driven
        /// session writes its worlds, so a silently mangled path there is the difference
        /// between a scratch directory and somewhere nobody intended.
        ///
        /// Two rules make the tolerant reading safe:
        ///
        ///   1. Every escape JSON actually defines still decodes normally, so anything
        ///      a real encoder produced round-trips unchanged. <c>\\</c> is still one
        ///      backslash, so a correctly encoded path is unaffected too.
        ///   2. An UNDEFINED escape keeps both characters rather than dropping the
        ///      backslash. That is the only behaviour change, and the discarded
        ///      alternative was losing data.
        ///
        /// <c>\b</c> and <c>\f</c> are the uncomfortable pair, because they are defined
        /// by JSON and also appear in paths as a directory beginning with b or f. They
        /// decode per spec; a path containing them must be sent with doubled
        /// backslashes or as a query parameter. <c>/savepath</c> reports the path it
        /// actually resolved so a caller can see which reading it got.
        /// </summary>
        private static string ParseStr(string s, ref int i)
        {
            var sb = new StringBuilder();
            i++; // opening quote
            while (i < s.Length)
            {
                char c = s[i++];
                if (c == '"') break;
                if (c != '\\') { sb.Append(c); continue; }
                if (i >= s.Length) { sb.Append('\\'); break; }
                char e = s[i++];
                switch (e)
                {
                    case '"': sb.Append('"'); break;
                    case '\\': sb.Append('\\'); break;
                    case '/': sb.Append('/'); break;
                    case 'n': sb.Append('\n'); break;
                    case 'r': sb.Append('\r'); break;
                    case 't': sb.Append('\t'); break;
                    case 'b': sb.Append('\b'); break;
                    case 'f': sb.Append('\f'); break;
                    case 'u':
                        int code;
                        if (i + 4 <= s.Length &&
                            int.TryParse(s.Substring(i, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out code))
                        {
                            sb.Append((char)code);
                            i += 4;
                        }
                        else
                        {
                            // Not a valid \uXXXX. Keep the literal text rather than
                            // eating four characters that were never an escape.
                            sb.Append('\\').Append('u');
                        }
                        break;
                    default:
                        // Undefined escape. Keep both characters: this is what makes
                        // "C:\Rig\Scratch" survive a hand-written request body.
                        sb.Append('\\').Append(e);
                        break;
                }
            }
            return sb.ToString();
        }

        // ---- typed accessors over a parsed object ----------------------------

        public static string GetStr(IDictionary dict, string key, string fallback = null)
        {
            if (dict == null || !dict.Contains(key)) return fallback;
            var v = dict[key];
            if (v == null) return fallback;
            if (v is string s) return s;
            if (v is double d) return d.ToString("R", CultureInfo.InvariantCulture);
            if (v is bool b) return b ? "true" : "false";
            return v.ToString();
        }

        public static float GetFloat(IDictionary dict, string key, float fallback = 0f)
        {
            if (dict == null || !dict.Contains(key)) return fallback;
            var v = dict[key];
            if (v is double d) return (float)d;
            if (v is string s)
            {
                float f;
                if (float.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out f)) return f;
            }
            return fallback;
        }

        public static int GetInt(IDictionary dict, string key, int fallback = 0)
        {
            if (dict == null || !dict.Contains(key)) return fallback;
            var v = dict[key];
            if (v is double d) return (int)Math.Round(d);
            if (v is string s)
            {
                int n;
                if (int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out n)) return n;
                double dd;
                if (double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out dd)) return (int)Math.Round(dd);
            }
            return fallback;
        }

        public static long GetLong(IDictionary dict, string key, long fallback = 0)
        {
            if (dict == null || !dict.Contains(key)) return fallback;
            var v = dict[key];
            if (v is double d) return (long)Math.Round(d);
            if (v is string s)
            {
                long n;
                if (long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out n)) return n;
            }
            return fallback;
        }

        public static bool GetBool(IDictionary dict, string key, bool fallback = false)
        {
            if (dict == null || !dict.Contains(key)) return fallback;
            var v = dict[key];
            if (v is bool b) return b;
            if (v is double d) return Math.Abs(d) > 0.0001;
            if (v is string s)
            {
                bool r;
                if (bool.TryParse(s, out r)) return r;
                if (s == "1") return true;
                if (s == "0") return false;
            }
            return fallback;
        }

        public static bool Has(IDictionary dict, string key) => dict != null && dict.Contains(key);
    }
}
