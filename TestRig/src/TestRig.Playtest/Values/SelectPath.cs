using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace TestRig.Playtest.Values;

/// <summary>
///     Reads a dotted path out of a reader's response.
/// </summary>
/// <remarks>
///     <para>
///     Supports <c>a.b.c</c>, array indexing (<c>connectedClients[0].username</c>) and the
///     pseudo-member <c>count</c> on any collection. <b>A path that does not resolve returns
///     null rather than throwing</b>, because "the field is absent" is a legitimate
///     observation and the assert verb is what decides whether absent is wrong. <c>.</c> or
///     an empty path returns the whole node.
///     </para>
///     <para>
///     Absent is exactly where the PowerShell original went wrong: an unresolved path
///     produced <c>$null</c>, <c>"$null"</c> was <c>''</c>, and <c>[double]''</c> is 0, so
///     <c>-AtMost n</c> against a typo'd path passed for every non-negative n. See
///     <see cref="ValueMatcher"/> for the fix; this class only has to make absent
///     distinguishable, which a null node is.
///     </para>
/// </remarks>
public static partial class SelectPath
{
    /// <summary>The pseudo-member that means "how many".</summary>
    public const string CountMember = "count";

    /// <summary>Reads <paramref name="path"/> out of <paramref name="node"/>.</summary>
    public static JsonNode? Select(JsonNode? node, string? path)
    {
        if (string.IsNullOrEmpty(path) || path == ".") return node;

        var current = node;
        foreach (var rawPart in path.Split('.'))
        {
            if (current is null) return null;

            var name = rawPart;
            var indexes = new List<int>();

            var match = PartPattern().Match(rawPart);
            if (match.Success)
            {
                name = match.Groups[1].Value;
                foreach (Match index in IndexPattern().Matches(match.Groups[2].Value))
                {
                    indexes.Add(int.Parse(index.Groups[1].Value, CultureInfo.InvariantCulture));
                }
            }

            if (name.Length > 0)
            {
                current = Member(current, name);
                if (current is null) return null;
            }

            foreach (var index in indexes)
            {
                current = Index(current, index);
                if (current is null) return null;
            }
        }

        return current;
    }

    private static JsonNode? Member(JsonNode node, string name)
    {
        switch (node)
        {
            case JsonArray array:
                // A collection has no members of its own except the count pseudo-member.
                return IsCount(name) ? JsonValue.Create(array.Count) : null;

            case JsonObject obj:
            {
                if (obj.TryGetPropertyValue(name, out var exact)) return exact?.DeepClone();

                // The PowerShell original resolved members through PSObject.Properties,
                // which is case-insensitive. A check that wrote 'HostPort' would have kept
                // working there and would silently read absent here, so the fallback stays.
                foreach (var property in obj)
                {
                    if (string.Equals(property.Key, name, StringComparison.OrdinalIgnoreCase))
                        return property.Value?.DeepClone();
                }

                // A single object standing in for a one-element collection.
                return IsCount(name) ? JsonValue.Create(1) : null;
            }

            default:
                return IsCount(name) ? JsonValue.Create(1) : null;
        }
    }

    private static JsonNode? Index(JsonNode node, int index)
    {
        if (node is JsonArray array)
            return index < array.Count ? array[index]?.DeepClone() : null;

        // A scalar indexed at [0] is itself, which is what wrapping in @() did.
        return index == 0 ? node.DeepClone() : null;
    }

    private static bool IsCount(string name) =>
        string.Equals(name, CountMember, StringComparison.OrdinalIgnoreCase);

    [GeneratedRegex(@"^([^\[\]]*)((\[\d+\])*)$")]
    private static partial Regex PartPattern();

    [GeneratedRegex(@"\[(\d+)\]")]
    private static partial Regex IndexPattern();
}
