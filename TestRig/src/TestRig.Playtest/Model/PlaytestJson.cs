using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace TestRig.Playtest.Model;

/// <summary>
///     JSON for the engine's own artifacts: evidence records, detail blobs, run reports.
/// </summary>
/// <remarks>
///     <para>
///     Every document here is assembled as a <see cref="JsonObject"/> with the keys written
///     in a fixed order rather than serialized from a record. Two reasons, both learned the
///     hard way. First, the evidence bundle's field order is part of what a human auditing a
///     run they did not watch relies on, and a record's property order is not a contract.
///     Second, the launcher publishes NativeAOT, where reflection-based serialization is
///     trimmed away entirely; hand-built nodes need no type resolver at all.
///     </para>
///     <para>
///     Wire payloads do NOT come through here. Those go through
///     <c>TestRig.Contracts.RigJson</c>, whose source-generated context is the whole point
///     of the Contracts assembly.
///     </para>
/// </remarks>
public static class PlaytestJson
{
    private static readonly JsonSerializerOptions Pretty = new() { WriteIndented = true };
    private static readonly JsonSerializerOptions Compact = new() { WriteIndented = false };

    /// <summary>Renders a node as indented JSON, or the literal <c>null</c>.</summary>
    public static string Write(JsonNode? node) => node?.ToJsonString(Pretty) ?? "null";

    /// <summary>Renders a node as one line, or the literal <c>null</c>.</summary>
    public static string WriteCompact(JsonNode? node) => node?.ToJsonString(Compact) ?? "null";

    /// <summary>
    ///     A detail blob for a signal: a flat map of primitives, in insertion order.
    /// </summary>
    /// <remarks>
    ///     Anything that is not a primitive is rendered with <see cref="Value"/> rather than
    ///     serialized, because a detail blob that throws while being built would replace a
    ///     real finding with a serialization error. The PowerShell original had the same
    ///     property through a try/catch around <c>ConvertTo-Json</c>.
    /// </remarks>
    public static string Detail(IReadOnlyDictionary<string, object?> fields)
    {
        ArgumentNullException.ThrowIfNull(fields);

        var obj = new JsonObject();
        foreach (var pair in fields) obj[pair.Key] = Value(pair.Value);
        return WriteCompact(obj);
    }

    /// <summary>
    ///     A JSON node for one .NET value, without reflection.
    /// </summary>
    /// <remarks>
    ///     A <see cref="JsonNode"/> passed in is deep-cloned, because a node that is already
    ///     parented throws when it is assigned a second parent, and an evidence record that
    ///     throws on the way to disk loses the finding it was carrying.
    /// </remarks>
    public static JsonNode? Value(object? value) => value switch
    {
        null => null,
        JsonNode node => node.DeepClone(),
        string text => JsonValue.Create(text),
        bool flag => JsonValue.Create(flag),
        int number => JsonValue.Create(number),
        long number => JsonValue.Create(number),
        double number => JsonValue.Create(number),
        float number => JsonValue.Create(number),
        decimal number => JsonValue.Create(number),
        DateTimeOffset stamp => JsonValue.Create(Stamps.Format(stamp)),
        IEnumerable<string> items => new JsonArray([.. items.Select(i => (JsonNode?)JsonValue.Create(i))]),
        _ => JsonValue.Create(Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty),
    };

    /// <summary>An array node from a sequence of strings.</summary>
    public static JsonArray Array(IEnumerable<string> items) =>
        new([.. items.Select(i => (JsonNode?)JsonValue.Create(i))]);

    /// <summary>Parses text, returning null rather than throwing on anything unparseable.</summary>
    public static JsonNode? TryParse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        try { return JsonNode.Parse(text); }
        catch (JsonException) { return null; }
    }
}

/// <summary>The one timestamp format the whole bundle uses.</summary>
public static class Stamps
{
    /// <summary>ISO 8601 to the second, always UTC, always Z-suffixed.</summary>
    public const string Format8601 = "yyyy-MM-ddTHH:mm:ssZ";

    public static string Format(DateTimeOffset moment) =>
        moment.UtcDateTime.ToString(Format8601, CultureInfo.InvariantCulture);
}
