using System.Text;
using System.Text.Json;
using TestRig.Core.Abstractions;

namespace TestRig.Core.Session;

/// <summary>
/// <c>session.state.json</c>: when the rig was last reset, plus whatever snapshot of
/// shared per-user state the caller supplies.
/// </summary>
/// <remarks>
/// <c>LastResetUtc</c> is the only reference point the ConfigTouched report has, which is
/// why every non-performing path carries the previous value forward rather than
/// overwriting it with nothing: a refused or skipped reset that erased it would make the
/// next session's config-drift report silently empty.
///
/// Scope note for the port: the shared per-user state itself (PlayerCookie-v2.xml, the
/// PlayerPrefs registry key, Blueprints\) is snapshotted and REPORTED, never restored,
/// and deliberately has no counterpart writer. Reading the registry needs a seam that
/// does not exist in the frozen abstractions, so this type stores and returns whatever
/// values it is handed rather than gathering them itself.
/// </remarks>
public sealed class SessionStateStore
{
    private readonly IFileSystem _fs;
    private readonly IClock _clock;
    private readonly RigPaths _paths;

    public SessionStateStore(IFileSystem fs, IClock clock, RigPaths paths)
    {
        _fs = fs;
        _clock = clock;
        _paths = paths;
    }

    /// <summary>The recorded reset timestamp, or null when there is no usable file.</summary>
    public string? ReadLastResetUtc()
    {
        if (!_fs.FileExists(_paths.SessionStateFile)) return null;
        try
        {
            using var doc = JsonDocument.Parse(_fs.ReadAllText(_paths.SessionStateFile));
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return null;
            if (!doc.RootElement.TryGetProperty("lastResetUtc", out var value)) return null;
            if (value.ValueKind != JsonValueKind.String) return null;
            var text = value.GetString();
            return string.IsNullOrEmpty(text) ? null : text;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>The snapshotted shared-state values, empty when there are none.</summary>
    public IReadOnlyDictionary<string, string> ReadValues()
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (!_fs.FileExists(_paths.SessionStateFile)) return result;
        try
        {
            using var doc = JsonDocument.Parse(_fs.ReadAllText(_paths.SessionStateFile));
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return result;
            if (!doc.RootElement.TryGetProperty("values", out var values) || values.ValueKind != JsonValueKind.Object)
            {
                return result;
            }
            foreach (var property in values.EnumerateObject())
            {
                result[property.Name] = property.Value.ValueKind == JsonValueKind.String
                    ? property.Value.GetString() ?? ""
                    : property.Value.ToString();
            }
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }
        return result;
    }

    /// <summary>Writes the snapshot. Called on EVERY reset path, including refusal and skip.</summary>
    public void Save(string? lastResetUtc, IReadOnlyDictionary<string, string>? values = null)
    {
        var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = true }))
        {
            writer.WriteStartObject();
            writer.WriteString("capturedUtc", RigTime.Stamp(_clock.UtcNow));
            writer.WriteString("lastResetUtc", lastResetUtc ?? "");
            writer.WriteStartObject("values");
            foreach (var pair in (values ?? ReadValues()).OrderBy(static p => p.Key, StringComparer.Ordinal))
            {
                writer.WriteString(pair.Key, pair.Value);
            }
            writer.WriteEndObject();
            writer.WriteEndObject();
        }

        _fs.CreateDirectory(_paths.RigHome);
        _fs.WriteAllText(_paths.SessionStateFile, Encoding.UTF8.GetString(buffer.ToArray()));
    }
}
