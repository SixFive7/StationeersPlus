using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using TestRig.Contracts;
using TestRig.Playtest.Model;

namespace TestRig.Playtest.Seams;

/// <summary>
///     Every request body and every query string, through the shared Contracts types.
/// </summary>
/// <remarks>
///     <para>
///     Nothing here accepts a hand-built dictionary, and that is the point of the whole
///     port. The PowerShell fake answered <c>/dlc</c> with <c>{ok, owned}</c> while the real
///     checks read <c>state.removedOwned</c> and <c>state.shared</c>; 399 assertions stayed
///     green through 54 field-level divergences of exactly that shape. A body that is not a
///     registered Contracts record does not serialize, so a renamed field is a compile error
///     on both sides of the wire instead of a silent null at run time.
///     </para>
///     <para>
///     Everything routes through the source-generated context, because the launcher
///     publishes NativeAOT where reflection-based serialization is trimmed away entirely.
///     </para>
/// </remarks>
public static class RigWire
{
    /// <summary>Serializes a Contracts request record to a JSON body.</summary>
    public static string Serialize(object body)
    {
        ArgumentNullException.ThrowIfNull(body);

        try
        {
            return JsonSerializer.Serialize(body, body.GetType(), RigJson.Context);
        }
        catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException)
        {
            throw new PlaytestUsageException(
                $"'{body.GetType().FullName}' is not a TestRig.Contracts wire type, so it cannot be sent. " +
                "Use the request record for the endpoint (ConfigSetRequest, SpawnStructureRequest, PlayerUseRequest, ...). " +
                "Hand-built payloads are what this port exists to remove: a field renamed in the plugin has to be a compile error here, not a null at run time.");
        }
    }

    /// <summary>Serializes a Contracts record to a node, for narrowing and path selection.</summary>
    public static JsonNode? ToNode(object value)
    {
        ArgumentNullException.ThrowIfNull(value);

        try
        {
            return JsonSerializer.SerializeToNode(value, value.GetType(), RigJson.Context);
        }
        catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException)
        {
            throw new PlaytestUsageException(
                $"'{value.GetType().FullName}' is not a TestRig.Contracts wire type and cannot be rendered.");
        }
    }

    /// <summary>Parses a response body as one of the Contracts response records.</summary>
    /// <remarks>
    ///     Returns null when the body is not that shape at all. A caller that gets null has a
    ///     transport or routing problem, not a value; it never gets to conclude anything.
    /// </remarks>
    public static T? Deserialize<T>(string json) where T : class
    {
        if (string.IsNullOrWhiteSpace(json)) return null;

        try
        {
            return JsonSerializer.Deserialize(json, typeof(T), RigJson.Context) as T;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    ///     A query string from a Contracts request record: <c>?a=1&amp;b=2</c>, keys sorted.
    /// </summary>
    /// <remarks>
    ///     A query parameter is percent-decoded by the HTTP layer and never goes through the
    ///     plugin's JSON string reader, which is the only way a Windows path survives a
    ///     request intact. Keys are sorted so the same read produces the same evidence file
    ///     name on every run. Unset (null) members are omitted, so a record with one field
    ///     set produces a one-parameter query.
    /// </remarks>
    public static string Query(object? request)
    {
        if (request is null) return string.Empty;

        if (ToNode(request) is not JsonObject obj || obj.Count == 0) return string.Empty;

        var parts = new List<string>(obj.Count);
        foreach (var pair in obj.OrderBy(p => p.Key, StringComparer.Ordinal))
        {
            if (pair.Value is null) continue;
            parts.Add($"{Uri.EscapeDataString(pair.Key)}={Uri.EscapeDataString(QueryValue(pair.Value))}");
        }

        return parts.Count == 0 ? string.Empty : "?" + string.Join('&', parts);
    }

    private static string QueryValue(JsonNode node)
    {
        if (node is JsonArray array)
        {
            var builder = new StringBuilder();
            foreach (var item in array)
            {
                if (builder.Length > 0) builder.Append(',');
                builder.Append(item is null ? string.Empty : QueryValue(item));
            }

            return builder.ToString();
        }

        if (node is JsonValue value)
        {
            if (value.TryGetValue<bool>(out var flag)) return flag ? "true" : "false";
            if (value.TryGetValue<string>(out var text)) return text;
            if (value.TryGetValue<long>(out var integral)) return integral.ToString(CultureInfo.InvariantCulture);
            if (value.TryGetValue<double>(out var real)) return real.ToString("R", CultureInfo.InvariantCulture);
        }

        return node.ToJsonString();
    }
}
