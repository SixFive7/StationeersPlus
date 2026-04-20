using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace InspectorPlus
{
    /// <summary>
    /// Parsed snapshot request. Null means "dump everything interesting."
    /// When populated, filters what the ObjectWalker inspects.
    /// </summary>
    internal class SnapshotRequest
    {
        /// <summary>
        /// Fully-qualified type names to inspect (e.g. "Assets.Scripts.Objects.Electrical.PowerTransmitter").
        /// Empty means all known game types.
        /// </summary>
        public List<string> Types { get; set; } = new List<string>();

        /// <summary>
        /// Specific field or property names to include. Empty means all public fields/properties.
        /// </summary>
        public List<string> Fields { get; set; } = new List<string>();

        /// <summary>
        /// Max depth for recursive object walking. Prevents runaway serialization.
        /// </summary>
        public int MaxDepth { get; set; } = 3;

        /// <summary>
        /// If true, include private and internal fields (via reflection).
        /// </summary>
        public bool IncludePrivate { get; set; } = false;

        /// <summary>
        /// Hard cap on how many top-level objects a snapshot will serialize.
        /// Defaults to 10000; raise for broad scene dumps on small scenes, lower
        /// for tightly-scoped probes. Works together with the walker's byte and
        /// nested-expansion caps to keep F8 dumps from exhausting memory.
        /// </summary>
        public int MaxMonoBehaviours { get; set; } = 10000;

        /// <summary>
        /// Parse a JSON request file. Minimal hand-rolled parser since we
        /// can't depend on Newtonsoft being available.
        /// </summary>
        public static SnapshotRequest Parse(string json)
        {
            var req = new SnapshotRequest();
            if (string.IsNullOrWhiteSpace(json))
                return req;

            req.Types = ParseStringArray(json, "types");
            req.Fields = ParseStringArray(json, "fields");

            var depthMatch = Regex.Match(json, @"""maxDepth""\s*:\s*(\d+)");
            if (depthMatch.Success)
                req.MaxDepth = int.Parse(depthMatch.Groups[1].Value);

            var privateMatch = Regex.Match(json, @"""includePrivate""\s*:\s*(true|false)");
            if (privateMatch.Success)
                req.IncludePrivate = privateMatch.Groups[1].Value == "true";

            var maxMbMatch = Regex.Match(json, @"""maxMonoBehaviours""\s*:\s*(\d+)");
            if (maxMbMatch.Success)
                req.MaxMonoBehaviours = int.Parse(maxMbMatch.Groups[1].Value);

            return req;
        }

        private static List<string> ParseStringArray(string json, string key)
        {
            var result = new List<string>();
            var pattern = $@"""{key}""\s*:\s*\[(.*?)\]";
            var match = Regex.Match(json, pattern, RegexOptions.Singleline);
            if (!match.Success) return result;

            var inner = match.Groups[1].Value;
            foreach (Match m in Regex.Matches(inner, @"""([^""]+)"""))
                result.Add(m.Groups[1].Value);

            return result;
        }
    }
}
