namespace TestRig.Core.Abstractions;

/// <summary>Severity of a line the rig emits.</summary>
public enum OutputLevel
{
    Detail,
    Info,
    Warning,
    Error,
}

/// <summary>
/// Everything the rig says, as structure rather than prose.
/// </summary>
/// <remarks>
/// This exists so <c>--json</c> is not a second code path. The PowerShell rig printed
/// prose and callers scraped it: the playtest harness recovered the session's lock
/// owner id with a regex over launcher output, and that one line has in fact never
/// been printed, so every check would have failed to start. A caller must be able to
/// get a value without parsing a sentence.
///
/// Human rendering and JSON rendering are two sinks over the same events. A verb
/// never asks which mode it is in.
/// </remarks>
public interface IOutput
{
    /// <summary>Writes a human-facing line. Never carries a value a caller needs.</summary>
    void Line(OutputLevel level, string text);

    /// <summary>
    /// Records a machine-readable value. In JSON mode it lands in the result object;
    /// in human mode it is rendered per the verb's own formatting.
    /// </summary>
    void Value(string key, object? value);

    /// <summary>
    /// Records a refusal: what was attempted, why this target cannot do it, and a
    /// command that does work. All three are mandatory - a refusal missing any of
    /// them is the failure mode the refusal matrix exists to prevent.
    /// </summary>
    void Refusal(Refusal refusal);
}

/// <summary>
/// An output sink that also keeps a copy of what passed through it, on request.
/// </summary>
/// <remarks>
/// One caller: the playtest engine writes the between-session state reset into its evidence
/// bundle as <c>hygiene-reset.txt</c>, and that report is prose the lock service emits while
/// it acquires. Capturing it here rather than threading a return value through the lock
/// keeps the report EXACTLY what the rig said, which is the point of putting it in a bundle
/// nobody reads until something has already gone wrong.
///
/// Everything is forwarded unchanged, always. A sink that swallowed a line while recording
/// would make the bundle and the terminal disagree about the same run.
/// </remarks>
public sealed class CapturingOutput(IOutput inner) : IOutput
{
    private readonly List<string> _window = [];
    private bool _recording;

    public void Line(OutputLevel level, string text)
    {
        if (_recording) _window.Add(level == OutputLevel.Info ? text : $"[{level}] {text}");
        inner.Line(level, text);
    }

    public void Value(string key, object? value) => inner.Value(key, value);

    public void Refusal(Refusal refusal)
    {
        ArgumentNullException.ThrowIfNull(refusal);
        if (_recording)
        {
            _window.Add(refusal.What);
            _window.Add(refusal.Why);
            _window.Add(refusal.InsteadLabel + " " + refusal.Instead);
        }

        inner.Refusal(refusal);
    }

    /// <summary>Starts a fresh capture window, discarding any previous one.</summary>
    public void Begin()
    {
        _window.Clear();
        _recording = true;
    }

    /// <summary>Closes the window and returns everything it caught, as one block of text.</summary>
    public string End()
    {
        _recording = false;
        return string.Join(Environment.NewLine, _window);
    }
}

/// <summary>
/// A refusal: the rig declining to do something, in the shape that teaches.
/// </summary>
/// <param name="What">What was attempted, in the caller's terms.</param>
/// <param name="Why">Why this target cannot do it.</param>
/// <param name="InsteadLabel">Human description of the working alternative.</param>
/// <param name="Instead">A command line that actually works.</param>
/// <param name="Reference">Where the durable explanation lives.</param>
/// <remarks>
/// The measure of the whole port is that an uninformed agent cannot easily do the
/// wrong thing, because the tool refuses and explains rather than relying on a
/// document the agent did not read. A refusal without an <paramref name="Instead"/>
/// fails that test, so the type makes it non-optional.
///
/// One caution learned from the PowerShell matrix: the suite checked that a refusal
/// HAS an alternative, never that the alternative is real. Two entries pointed at
/// <c>/console/run</c>, an endpoint that does not exist. The C# suite resolves every
/// <paramref name="Instead"/> against the actual verb and endpoint tables.
/// </remarks>
public sealed record Refusal(
    string What,
    string Why,
    string InsteadLabel,
    string Instead,
    string Reference);
