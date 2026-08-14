using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace TestRig.Playtest.Values;

/// <summary>
///     Rendering and comparison for observed values.
/// </summary>
/// <remarks>
///     A control plane answers in JSON, so <c>"True"</c> from one endpoint and a JSON
///     <c>true</c> from another are the same observation. Booleans compare as booleans,
///     numbers as numbers, and everything else case-insensitively, because an assertion that
///     turns on the casing of a role name is an assertion that will break for no reason.
/// </remarks>
public static class ValueText
{
    /// <summary>
    ///     How a value is rendered into a message, and into the string half of a comparison.
    /// </summary>
    /// <remarks>
    ///     Absent renders as the empty string, which is what PowerShell's <c>"$null"</c> did.
    ///     That rendering is fine for a message and was fatal for a comparison; the numeric
    ///     matchers therefore never go through it. See <see cref="ValueMatcher"/>.
    /// </remarks>
    public static string Render(JsonNode? node)
    {
        switch (node)
        {
            case null:
                return string.Empty;

            case JsonArray array:
            {
                var parts = new List<string>(array.Count);
                foreach (var item in array) parts.Add(Render(item));
                return string.Join(' ', parts);
            }

            case JsonObject obj:
                return obj.ToJsonString();

            case JsonValue value:
            {
                if (value.TryGetValue<bool>(out var flag)) return flag ? "True" : "False";
                if (value.TryGetValue<string>(out var text)) return text;
                if (value.TryGetValue<long>(out var integral)) return integral.ToString(CultureInfo.InvariantCulture);
                if (value.TryGetValue<double>(out var real)) return real.ToString("R", CultureInfo.InvariantCulture);
                return value.ToJsonString().Trim('"');
            }

            default:
                return node.ToJsonString();
        }
    }

    /// <summary>How an expected value, supplied by the check in C#, renders in a message.</summary>
    public static string RenderExpected(object? expected) => expected switch
    {
        null => string.Empty,
        bool flag => flag ? "True" : "False",
        string text => text,
        double real => real.ToString("R", CultureInfo.InvariantCulture),
        float real => real.ToString("R", CultureInfo.InvariantCulture),
        decimal real => real.ToString(CultureInfo.InvariantCulture),
        IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
        _ => Convert.ToString(expected, CultureInfo.InvariantCulture) ?? string.Empty,
    };

    /// <summary>True when the node carries a JSON boolean.</summary>
    public static bool IsBoolean(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue<bool>(out _);

    /// <summary>The node's boolean value under the harness's coercion rule.</summary>
    /// <remarks>
    ///     A non-boolean coerces true only for <c>true</c> (any casing) and <c>1</c>.
    ///     Everything else, <c>yes</c> and <c>2</c> included, is false. That is deliberate:
    ///     a permissive truthiness rule turns "the endpoint answered something unexpected"
    ///     into "the endpoint agreed".
    /// </remarks>
    public static bool AsBoolean(JsonNode? node)
    {
        if (node is JsonValue value && value.TryGetValue<bool>(out var flag)) return flag;
        return CoerceBoolean(Render(node));
    }

    /// <summary>The same coercion for a value the check supplied.</summary>
    public static bool AsBoolean(object? expected)
    {
        if (expected is bool flag) return flag;
        return CoerceBoolean(RenderExpected(expected));
    }

    private static bool CoerceBoolean(string text) =>
        string.Equals(text, "true", StringComparison.OrdinalIgnoreCase) || text == "1";

    /// <summary>Parses a node as a number, refusing anything that is not one.</summary>
    /// <remarks>
    ///     A boolean is deliberately not a number here. PowerShell coerced <c>$true</c> to 1
    ///     on the way through <c>[double]</c>, which made <c>-AtLeast 1</c> pass against
    ///     <c>hosting</c> and say nothing at all.
    /// </remarks>
    public static bool TryAsNumber(JsonNode? node, out double number)
    {
        number = 0;
        if (node is null) return false;
        if (node is JsonValue value)
        {
            if (value.TryGetValue<bool>(out _)) return false;
            if (value.TryGetValue<double>(out var real)) { number = real; return true; }
        }

        return double.TryParse(Render(node), NumberStyles.Float, CultureInfo.InvariantCulture, out number);
    }

    /// <summary>Parses a check-supplied value as a number.</summary>
    public static bool TryAsNumber(object? expected, out double number)
    {
        number = 0;
        switch (expected)
        {
            case null:
            case bool:
                return false;
            case double real: number = real; return true;
            case float real: number = real; return true;
            case decimal real: number = (double)real; return true;
            case int integral: number = integral; return true;
            case long integral: number = integral; return true;
        }

        return double.TryParse(RenderExpected(expected), NumberStyles.Float, CultureInfo.InvariantCulture, out number);
    }

    /// <summary>
    ///     The equality rule, in order: both absent, exactly one absent, either side boolean,
    ///     both numeric, otherwise ordinal case-insensitive text.
    /// </summary>
    public static bool AreEqual(object? expected, JsonNode? actual)
    {
        var expectedAbsent = expected is null;
        var actualAbsent = actual is null || actual is JsonValue jv && jv.GetValueKind() == JsonValueKind.Null;

        if (expectedAbsent && actualAbsent) return true;
        if (expectedAbsent || actualAbsent) return false;

        if (expected is bool || IsBoolean(actual)) return AsBoolean(expected) == AsBoolean(actual);

        if (TryAsNumber(expected, out var e) && TryAsNumber(actual, out var a)) return e.Equals(a);

        return string.Equals(RenderExpected(expected), Render(actual), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    ///     PowerShell's <c>-like</c>, which is what <c>-Contains</c> ran through.
    /// </summary>
    /// <remarks>
    ///     Kept verbatim so <c>*</c>, <c>?</c> and <c>[...]</c> in a check's operand stay
    ///     wildcard metacharacters rather than becoming literals, which would quietly change
    ///     what an existing check matches.
    /// </remarks>
    public static bool LikeContains(JsonNode? actual, string operand)
    {
        var pattern = WildcardToRegex("*" + operand + "*");
        var candidates = actual is JsonArray array
            ? array.Select(Render)
            : [Render(actual)];

        return candidates.Any(candidate => Regex.IsMatch(candidate, pattern, RegexOptions.IgnoreCase, TimeSpan.FromSeconds(2)));
    }

    private static string WildcardToRegex(string wildcard)
    {
        var builder = new StringBuilder("^");
        var index = 0;
        while (index < wildcard.Length)
        {
            var c = wildcard[index];
            switch (c)
            {
                case '*': builder.Append(".*"); index++; break;
                case '?': builder.Append('.'); index++; break;
                case '[':
                {
                    var close = wildcard.IndexOf(']', index + 1);
                    if (close < 0) { builder.Append(Regex.Escape("[")); index++; break; }
                    builder.Append('[').Append(wildcard.AsSpan(index + 1, close - index - 1)).Append(']');
                    index = close + 1;
                    break;
                }

                default: builder.Append(Regex.Escape(c.ToString())); index++; break;
            }
        }

        return builder.Append('$').ToString();
    }
}
