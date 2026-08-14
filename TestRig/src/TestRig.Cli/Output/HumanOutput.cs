using TestRig.Core.Abstractions;

namespace TestRig.Cli.Output;

/// <summary>Something both sinks agree on, so a verb never asks which mode it is in.</summary>
public interface IRigOutput : IOutput
{
    /// <summary>Emits whatever has been buffered. A no-op for the human sink.</summary>
    void Flush(string verb, int exitCode, string? error);
}

/// <summary>
/// Prose on stdout, warnings and errors on stderr.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="Value"/> is silent by default: the verb prints its own sentence and records
/// the value for the JSON sink. The exception is the small set of contract keys below,
/// which have a plain-text spelling a shell-out caller depends on.
/// </para>
/// <para>
/// <c>TESTRIG-OWNER &lt;id&gt;</c> is the only one today. It exists so the playtest harness
/// stops scraping the owner id out of a human-readable block with two regexes, and it has
/// never once printed: <c>New-RigLock</c> returned a bare string, so the launcher's
/// <c>$outcome.Owner</c> was always null and the guard around the line was always false.
/// Both pinning assertions grepped the launcher's source text, so the suite stayed green for
/// the entire life of a feature that never ran. Here the id is a value, the line is derived
/// from it, and the suite asserts by executing the command.
/// </para>
/// </remarks>
public sealed class HumanOutput(TextWriter stdout, TextWriter stderr, bool verbose) : IRigOutput
{
    /// <summary>
    /// Values with a plain-text spelling, and the one verb each belongs to.
    /// </summary>
    /// <remarks>
    /// Scoped by verb because <c>status</c> records an <c>owner</c> too, and that one is
    /// frequently somebody else's. A harness reading <c>TESTRIG-OWNER</c> out of a status
    /// report would take a lock it does not hold.
    /// </remarks>
    private static readonly Dictionary<string, (string Verb, string Format)> ContractLines =
        new(StringComparer.Ordinal)
        {
            ["owner"] = ("lock", "TESTRIG-OWNER {0}"),
        };

    private readonly Dictionary<string, string> _contract = new(StringComparer.Ordinal);

    public void Line(OutputLevel level, string text)
    {
        switch (level)
        {
            case OutputLevel.Detail:
                if (verbose) stdout.WriteLine(text);
                break;
            case OutputLevel.Warning:
                stderr.WriteLine("WARNING: " + text);
                break;
            case OutputLevel.Error:
                stderr.WriteLine("ERROR: " + text);
                break;
            default:
                stdout.WriteLine(text);
                break;
        }
    }

    /// <summary>
    /// Records a value. Almost all of them are JSON-only; the handful with a plain-text
    /// spelling are held back and written by <see cref="Flush"/>, so the contract line is the
    /// last thing a successful command says however many times the value was recorded.
    /// </summary>
    public void Value(string key, object? value)
    {
        if (value is null) return;
        if (!ContractLines.TryGetValue(key, out var contract)) return;
        _contract[key] = string.Format(
            System.Globalization.CultureInfo.InvariantCulture, contract.Format, value);
    }

    /// <summary>
    /// The teaching block: the explanation wrapped, then the alternative on one line, then
    /// where the durable answer lives.
    /// </summary>
    /// <remarks>
    /// Only the explanation is wrapped. The alternative is a command line and stays on one
    /// line so it can be copied.
    /// </remarks>
    public void Refusal(Refusal refusal)
    {
        ArgumentNullException.ThrowIfNull(refusal);
        var wrapped = Refusals.RefusalMatrix.Wrap(refusal.Why, Refusals.RefusalMatrix.WrapWidth);
        for (var i = 0; i < wrapped.Count; i++)
            stdout.WriteLine((i == 0 ? "  x " : "    ") + wrapped[i]);

        var label = refusal.InsteadLabel.Length > 0 ? refusal.InsteadLabel : "Instead:";
        stdout.WriteLine($"    {label}  {refusal.Instead}");
        if (refusal.Reference.Length > 0) stdout.WriteLine($"    Why: {refusal.Reference}");
        stdout.WriteLine();
    }

    public void Flush(string verb, int exitCode, string? error)
    {
        if (exitCode == 0)
        {
            foreach (var (key, line) in _contract)
                if (ContractLines[key].Verb == verb)
                    stdout.WriteLine(line);
        }

        if (!string.IsNullOrEmpty(error)) stderr.WriteLine(error);
        stdout.Flush();
        stderr.Flush();
    }
}
