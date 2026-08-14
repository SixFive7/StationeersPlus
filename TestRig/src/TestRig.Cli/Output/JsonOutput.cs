using System.Globalization;
using System.Text.Json;
using TestRig.Core.Abstractions;

namespace TestRig.Cli.Output;

/// <summary>
/// One JSON object per invocation, written at exit.
/// </summary>
/// <remarks>
/// <para>
/// This is the reason <see cref="IOutput"/> exists. Automation must be able to get a value
/// without parsing a sentence: the playtest harness scraped the session's owner id out of
/// launcher prose with a regex, the line it wanted was never printed, and a wording change
/// would have broken every check anyway. Values land in <c>values</c> as fields; prose lands
/// in <c>lines</c> and nothing needs to read it.
/// </para>
/// <para>
/// Written with <see cref="Utf8JsonWriter"/> rather than a serializer: an AOT binary has no
/// reflection to fall back on, and hand-writing a handful of scalars needs no source
/// generator at all.
/// </para>
/// </remarks>
public sealed class JsonOutput(Stream stdout) : IRigOutput
{
    private readonly List<(OutputLevel Level, string Text)> _lines = [];
    private readonly List<KeyValuePair<string, object?>> _values = [];
    private readonly Dictionary<string, int> _valueIndex = new(StringComparer.Ordinal);
    private Refusal? _refusal;

    public void Line(OutputLevel level, string text) => _lines.Add((level, text));

    /// <summary>
    /// Records a value, first write wins its position and the last write wins its content.
    /// </summary>
    /// <remarks>
    /// A repeat is not an error: the lock service records the owner id and the CLI may record
    /// it again from the typed result. Emitting the key twice would produce an object whose
    /// meaning depends on which duplicate a reader's parser keeps.
    /// </remarks>
    public void Value(string key, object? value)
    {
        if (_valueIndex.TryGetValue(key, out var at))
        {
            _values[at] = new KeyValuePair<string, object?>(key, value);
            return;
        }

        _valueIndex[key] = _values.Count;
        _values.Add(new KeyValuePair<string, object?>(key, value));
    }

    public void Refusal(Refusal refusal) => _refusal = refusal;

    public void Flush(string verb, int exitCode, string? error)
    {
        using var writer = new Utf8JsonWriter(stdout, new JsonWriterOptions { Indented = true });
        writer.WriteStartObject();
        writer.WriteBoolean("ok", exitCode == 0);
        writer.WriteString("verb", verb);
        writer.WriteNumber("exitCode", exitCode);

        if (error is null) writer.WriteNull("error");
        else writer.WriteString("error", error);

        writer.WriteStartObject("values");
        foreach (var (key, value) in _values)
        {
            writer.WritePropertyName(key);
            WriteValue(writer, value);
        }

        writer.WriteEndObject();

        if (_refusal is null)
        {
            writer.WriteNull("refusal");
        }
        else
        {
            writer.WriteStartObject("refusal");
            writer.WriteString("what", _refusal.What);
            writer.WriteString("why", _refusal.Why);
            writer.WriteString("insteadLabel", _refusal.InsteadLabel);
            writer.WriteString("instead", _refusal.Instead);
            writer.WriteString("reference", _refusal.Reference);
            writer.WriteEndObject();
        }

        writer.WriteStartArray("lines");
        foreach (var (level, text) in _lines)
        {
            writer.WriteStartObject();
            writer.WriteString("level", level.ToString().ToLowerInvariant());
            writer.WriteString("text", text);
            writer.WriteEndObject();
        }

        writer.WriteEndArray();
        writer.WriteEndObject();
        writer.Flush();
        stdout.Flush();
    }

    /// <summary>
    /// The value shapes a verb may record. Anything else is written as its text, which is
    /// lossy but never wrong.
    /// </summary>
    private static void WriteValue(Utf8JsonWriter writer, object? value)
    {
        switch (value)
        {
            case null:
                writer.WriteNullValue();
                break;
            case string s:
                writer.WriteStringValue(s);
                break;
            case bool b:
                writer.WriteBooleanValue(b);
                break;
            case int i:
                writer.WriteNumberValue(i);
                break;
            case long l:
                writer.WriteNumberValue(l);
                break;
            case double d:
                writer.WriteNumberValue(d);
                break;
            case IReadOnlyDictionary<string, object?> map:
                writer.WriteStartObject();
                foreach (var (k, v) in map)
                {
                    writer.WritePropertyName(k);
                    WriteValue(writer, v);
                }

                writer.WriteEndObject();
                break;
            case IReadOnlyDictionary<string, string> text:
                writer.WriteStartObject();
                foreach (var (k, v) in text) writer.WriteString(k, v);
                writer.WriteEndObject();
                break;
            case System.Collections.IEnumerable items:
                writer.WriteStartArray();
                foreach (var item in items) WriteValue(writer, item);
                writer.WriteEndArray();
                break;
            default:
                writer.WriteStringValue(Convert.ToString(value, CultureInfo.InvariantCulture));
                break;
        }
    }
}
