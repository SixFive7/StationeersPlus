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
/// <summary>
///     The plugin answered with something this launcher's wire contract cannot represent.
/// </summary>
/// <remarks>
///     <para>
///     Distinct from <see cref="RigTransportException"/>, which means the answer never
///     arrived. This one means it arrived and the two sides disagree about its shape, which
///     is a defect in one of them and is never transient.
///     </para>
///     <para>
///     The message names the field, because the failure it replaces named nothing. A
///     <c>long</c> connection id in an <c>int?</c> made the deserializer throw, the reader
///     return null for the entire response, and the harness conclude a joiner sitting in the
///     world had never arrived.
///     </para>
/// </remarks>
public sealed class RigWireFormatException : Exception
{
    public RigWireFormatException(string message) : base(message)
    {
    }

    public RigWireFormatException(string message, Exception inner) : base(message, inner)
    {
    }
}

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
    ///     <para>
    ///     <b>Null means the plugin sent nothing.</b> An empty body is the only thing that
    ///     produces one. Anything present that will not parse throws
    ///     <see cref="RigWireFormatException"/> naming the field, what the contract wanted
    ///     and what arrived.
    ///     </para>
    ///     <para>
    ///     This used to swallow every <see cref="JsonException"/> and return null, which made
    ///     the two cases indistinguishable and turned a one-field defect into a total one.
    ///     <c>ConnectedClient.ConnectionId</c> was typed <c>int?</c> against a <c>long</c>
    ///     RakNet id; the throw took out the WHOLE <c>/status</c> payload, the host's roster
    ///     read as empty, and four playtest checks reported
    ///     <c>inconclusive (joiner-not-in-roster)</c> against a rig that was joining
    ///     perfectly. Nothing anywhere said the word <c>connectionId</c>.
    ///     </para>
    ///     <para>
    ///     A throw is right rather than merely louder, because no caller can act on this.
    ///     A body that is well formed but does not fit the contract fits no better on the
    ///     next poll, so a reader loop that treated null as "not ready yet" burned its whole
    ///     timeout and then blamed the game. The runner classifies the throw as
    ///     <c>inconclusive/wire-format</c>, so it accuses the wire and never the mod.
    ///     </para>
    /// </remarks>
    /// <exception cref="RigWireFormatException">The body is present and does not parse as <typeparamref name="T"/>.</exception>
    public static T? Deserialize<T>(string json) where T : class
    {
        if (string.IsNullOrWhiteSpace(json)) return null;

        try
        {
            return JsonSerializer.Deserialize(json, typeof(T), RigJson.Context) as T;
        }
        catch (JsonException ex)
        {
            throw new RigWireFormatException(Diagnose(typeof(T), json, ex), ex);
        }
    }

    /// <summary>
    ///     Builds the sentence <see cref="Deserialize{T}"/> throws: which field, what the
    ///     contract wanted, what actually arrived.
    /// </summary>
    /// <remarks>
    ///     <see cref="JsonException.Path"/> carries the field, so the value at it can be read
    ///     back out of the body and quoted. That is the whole point: "expected string, got the
    ///     number 189151461494586169" names the fix, where "returned null" named nothing.
    /// </remarks>
    internal static string Diagnose(Type target, string json, JsonException ex)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(ex);

        // "$" is the root, which names nothing more than the type already does.
        var path = string.IsNullOrEmpty(ex.Path) || ex.Path == "$" ? null : ex.Path;
        var found = path is null ? null : ValueAt(json, path);

        var where = path is null
            ? $"'{target.Name}'"
            : $"'{target.Name}' at {path}";

        var what = found is null
            ? string.Empty
            : $" The value there is {found}.";

        return
            $"The plugin's answer does not fit {where}, so nothing could be read from it. " +
            "This is a wire contract defect, not a value: the field is typed more narrowly here than the " +
            "plugin can emit, or the two disagree about number against string." + what +
            $" {ex.Message}";
    }

    /// <summary>Renders the token at a <see cref="JsonException.Path"/>, or null when it cannot be found.</summary>
    private static string? ValueAt(string json, string path)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var element = document.RootElement;

            foreach (var step in Steps(path))
            {
                if (int.TryParse(step, NumberStyles.Integer, CultureInfo.InvariantCulture, out var index))
                {
                    if (element.ValueKind != JsonValueKind.Array || index >= element.GetArrayLength()) return null;
                    element = element[index];
                    continue;
                }

                if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(step, out element)) return null;
            }

            var raw = element.GetRawText();
            if (raw.Length > 120) raw = raw[..120] + "...";
            return $"the {element.ValueKind.ToString().ToLowerInvariant()} {raw}";
        }
        catch (JsonException)
        {
            // The body is not well-formed JSON at all, so there is no token to quote. The
            // serializer's own message already says where it gave up.
            return null;
        }
    }

    /// <summary>Splits <c>$.connectedClients[0].connectionId</c> into its steps.</summary>
    private static IEnumerable<string> Steps(string path)
    {
        foreach (var part in path.Split('.'))
        {
            if (part.Length == 0 || part == "$") continue;

            var bracket = part.IndexOf('[', StringComparison.Ordinal);
            if (bracket < 0)
            {
                yield return part;
                continue;
            }

            if (bracket > 0) yield return part[..bracket];

            foreach (var index in part[bracket..].Split('[', StringSplitOptions.RemoveEmptyEntries))
                yield return index.TrimEnd(']');
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
