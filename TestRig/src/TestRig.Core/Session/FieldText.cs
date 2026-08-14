using System.Diagnostics.CodeAnalysis;
using System.Text;

namespace TestRig.Core.Session;

/// <summary>
/// The <c>key=value</c> format shared by <c>session.lock</c> and <c>session.dirty</c>.
/// </summary>
/// <remarks>
/// One parser for both files, deliberately, so the two cannot disagree about comments
/// or about what an empty value means. Field order is preserved and is load bearing on
/// write: <c>Write-RigLock</c> iterated the ordered dictionary's keys, and a reader
/// comparing two lock files byte for byte would otherwise see spurious differences.
/// PowerShell's <c>[ordered]@{}</c> is case-insensitive on keys, so this is too.
///
/// There is no escaping and no quoting. A value cannot contain a newline. That is why
/// world names go through <see cref="WorldKey.IsRoundTrippable"/> before they are
/// recorded, rather than being trimmed on the way back in: spec 03-reset H.5 item 4
/// records a world directory named " Luna" (legal on NTFS) being trimmed on read,
/// matching nothing, and being deleted.
/// </remarks>
public sealed class FieldText
{
    private readonly List<string> _order = [];
    private readonly Dictionary<string, string> _values = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Line separator on write. The rig is Windows-only; fixed rather than
    /// <see cref="Environment.NewLine"/> so a file written under test is byte-identical.</summary>
    public const string NewLine = "\r\n";

    public FieldText() { }

    private FieldText(List<string> order, Dictionary<string, string> values)
    {
        _order = order;
        _values = values;
    }

    /// <summary>Keys in insertion order.</summary>
    public IReadOnlyList<string> Keys => _order;

    public int Count => _order.Count;

    public bool Contains(string key) => _values.ContainsKey(key);

    public bool TryGet(string key, [NotNullWhen(true)] out string? value) => _values.TryGetValue(key, out value);

    /// <summary>Reads a field, or null when it is absent.</summary>
    public string? Get(string key) => _values.TryGetValue(key, out var v) ? v : null;

    /// <summary>Reads a field, or the empty string when it is absent. Matches PowerShell's
    /// habit of interpolating a missing key as empty rather than throwing.</summary>
    public string GetOrEmpty(string key) => Get(key) ?? string.Empty;

    /// <summary>Sets a field, appending it at the end when it is new.</summary>
    public void Set(string key, string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        if (!_values.ContainsKey(key)) _order.Add(key);
        _values[key] = value;
    }

    public string this[string key]
    {
        get => GetOrEmpty(key);
        set => Set(key, value);
    }

    /// <summary>
    /// Parses the shared format.
    /// </summary>
    /// <remarks>
    /// Exactly the PowerShell rules, in order: split on <c>\r?\n</c>; trim each line;
    /// skip empty; skip lines whose trimmed form starts with '#'; find the FIRST '=' and
    /// skip the line when its index is below 1 (absent, or the line begins with '='); key
    /// and value are both trimmed; later keys overwrite earlier ones.
    /// </remarks>
    public static FieldText Parse(string? text)
    {
        var order = new List<string>();
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrEmpty(text)) return new FieldText(order, values);

        foreach (var rawLine in text.Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.Length == 0) continue;
            if (line[0] == '#') continue;

            var eq = line.IndexOf('=');
            if (eq < 1) continue;

            var key = line[..eq].Trim();
            var value = line[(eq + 1)..].Trim();
            if (key.Length == 0) continue;

            if (!values.ContainsKey(key)) order.Add(key);
            values[key] = value;
        }

        return new FieldText(order, values);
    }

    /// <summary>Renders header comment lines followed by one <c>key=value</c> per field.</summary>
    public string Render(IReadOnlyList<string> headerComments)
    {
        var sb = new StringBuilder();
        foreach (var comment in headerComments)
        {
            sb.Append(comment).Append(NewLine);
        }
        foreach (var key in _order)
        {
            sb.Append(key).Append('=').Append(_values[key]).Append(NewLine);
        }
        return sb.ToString();
    }

    /// <summary>A copy that preserves order, so a caller can mutate without aliasing.</summary>
    public FieldText Clone() => new([.. _order], new Dictionary<string, string>(_values, StringComparer.OrdinalIgnoreCase));
}
