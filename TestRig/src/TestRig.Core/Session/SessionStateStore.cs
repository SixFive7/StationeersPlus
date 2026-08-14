using System.Text;
using System.Text.Json;
using TestRig.Core.Abstractions;

namespace TestRig.Core.Session;

/// <summary>
/// <c>session.state.json</c>: when the rig was last reset, and the shared per-user state as
/// it stood when this session began.
/// </summary>
/// <remarks>
/// <para>
/// <c>LastResetUtc</c> is the only reference point the ConfigTouched report has, which is
/// why every non-performing path carries the previous value forward rather than
/// overwriting it with nothing: a refused or skipped reset that erased it would make the
/// next session's config-drift report silently empty.
/// </para>
/// <para>
/// The snapshot beside it is the shared per-user state (<c>PlayerCookie-v2.xml</c>, the
/// PlayerPrefs key, <c>Blueprints\</c>), which is REPORTED and never restored: see
/// <see cref="SharedStateReader"/>. It is written on EVERY reset path, refusal and skip
/// included, because a session whose unlock diffed against a PREVIOUS session's snapshot
/// would report that session's changes as its own, which is worse than no report at all.
/// </para>
/// </remarks>
public sealed class SessionStateStore
{
    private readonly IFileSystem _fs;
    private readonly IClock _clock;
    private readonly RigPaths _paths;
    private readonly SharedStateReader? _shared;

    public SessionStateStore(IFileSystem fs, IClock clock, RigPaths paths, SharedStateReader? shared = null)
    {
        _fs = fs;
        _clock = clock;
        _paths = paths;
        _shared = shared;
    }

    /// <summary>The recorded reset timestamp, or null when there is no usable file.</summary>
    public string? ReadLastResetUtc() => ReadString("lastResetUtc");

    /// <summary>The folder the recorded snapshot was taken from, or null.</summary>
    public string? ReadSharedDataDir() => ReadString("sharedDataDir");

    /// <summary>The registry key the recorded snapshot was taken from, or null.</summary>
    public string? ReadPlayerPrefsKey() => ReadString("playerPrefsKey");

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

    /// <summary>
    /// This session's baseline snapshot, or null when there is none to compare against.
    /// </summary>
    /// <remarks>
    /// RESET-148: a missing, unreadable or invalid state file is null rather than an empty
    /// baseline. An empty baseline would make every value on the rig read as "new", which is a
    /// drift report that is entirely noise.
    /// </remarks>
    public IReadOnlyDictionary<string, string>? ReadBaseline()
    {
        if (!_fs.FileExists(_paths.SessionStateFile)) return null;
        var values = ReadValues();
        return values.Count == 0 ? null : values;
    }

    /// <summary>
    /// Prints the drift report for this session (RESET-150, 151, 152).
    /// </summary>
    /// <remarks>
    /// Takes the "after" snapshot itself (RESET-146). Without a reader wired there is nothing
    /// to compare and the report says so rather than claiming everything is unchanged.
    /// </remarks>
    public void WriteDrift(IOutput output)
    {
        ArgumentNullException.ThrowIfNull(output);

        if (_shared is null)
        {
            output.Line(OutputLevel.Info, SharedStateReport.NoBaseline);
            return;
        }

        var baseline = ReadBaseline();
        var now = _shared.Capture().Values;

        foreach (var line in SharedStateReport.Render(baseline, now)) output.Line(OutputLevel.Info, line);
    }

    /// <summary>Writes the snapshot. Called on EVERY reset path, including refusal and skip.</summary>
    /// <param name="values">
    /// A snapshot to store. When null a fresh one is taken, so the session's comparison starts
    /// from the state the session actually begins with (RESET-149).
    /// </param>
    public void Save(string? lastResetUtc, IReadOnlyDictionary<string, string>? values = null)
    {
        var snapshot = _shared?.Capture();
        var stored = values ?? snapshot?.Values ?? ReadValues();

        var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = true }))
        {
            writer.WriteStartObject();
            writer.WriteString("capturedUtc", snapshot?.CapturedUtc ?? RigTime.Stamp(_clock.UtcNow));

            // Both carried, so a drift report can say WHICH folder and WHICH key it read and a
            // baseline taken against a different one is visibly not comparable.
            writer.WriteString("sharedDataDir", snapshot?.SharedDataDir ?? ReadSharedDataDir() ?? "");
            writer.WriteString("playerPrefsKey", snapshot?.PlayerPrefsKey ?? ReadPlayerPrefsKey() ?? "");

            writer.WriteString("lastResetUtc", lastResetUtc ?? "");
            writer.WriteStartObject("values");
            foreach (var pair in stored.OrderBy(static p => p.Key, StringComparer.Ordinal))
            {
                writer.WriteString(pair.Key, pair.Value);
            }
            writer.WriteEndObject();
            writer.WriteEndObject();
        }

        _fs.CreateDirectory(_paths.RigHome);
        _fs.WriteAllText(_paths.SessionStateFile, Encoding.UTF8.GetString(buffer.ToArray()));
    }

    private string? ReadString(string property)
    {
        if (!_fs.FileExists(_paths.SessionStateFile)) return null;
        try
        {
            using var doc = JsonDocument.Parse(_fs.ReadAllText(_paths.SessionStateFile));
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return null;
            if (!doc.RootElement.TryGetProperty(property, out var value)) return null;
            if (value.ValueKind != JsonValueKind.String) return null;
            var text = value.GetString();
            return string.IsNullOrEmpty(text) ? null : text;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
