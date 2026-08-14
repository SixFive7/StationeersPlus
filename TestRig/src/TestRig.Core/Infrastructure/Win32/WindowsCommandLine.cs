using System.Text;

namespace TestRig.Core.Infrastructure;

/// <summary>
/// Quoting for a Windows command line, the port of ConvertTo-RigProcessArgument and
/// ConvertTo-RigCommandLine.
/// </summary>
/// <remarks>
/// This is not decoration. An unquoted argument list joined with plain spaces is what
/// broke every lock acquisition the playtest harness attempted: a purpose string contains
/// spaces by nature, so -Purpose the first-use notice cap arrived at the launcher as
/// -Purpose the followed by positional junk that bound to an int parameter, and every
/// check in every suite died before it started.
///
/// CreateProcessW takes one string, not an argument vector, and the receiving program
/// splits it again with the rules CommandLineToArgvW implements. Those rules are not
/// "wrap it in quotes": backslashes are escape characters, but only when they precede a
/// quote. Getting that half right is what the PowerShell did.
/// </remarks>
public static class WindowsCommandLine
{
    /// <summary>
    /// Quotes one argument so CommandLineToArgvW hands it back unchanged.
    /// </summary>
    /// <remarks>
    /// The fix over the PowerShell version, which was a plain replace of " with \" inside
    /// a pair of quotes: a run of backslashes immediately before the closing quote has to
    /// be doubled, or the last one escapes the quote that was supposed to end the
    /// argument.
    ///
    /// Concretely, an instances root of E:\Stationeers Rig\ produced
    /// "E:\Stationeers Rig\" and the receiving program read one argument that began
    /// E:\Stationeers Rig" and then swallowed every remaining token on the line. Trailing
    /// separators are not exotic here: an instances root comes from an environment
    /// variable or from -InstancesRoot typed by hand, and both routinely carry one.
    ///
    /// The same doubling applies to backslashes before an EMBEDDED quote, which the
    /// PowerShell also got wrong. Neither case had a test.
    ///
    /// All five assertions the PowerShell suite made still hold: 'a b' quotes, 'plain'
    /// does not, 'C:\rig\x.ps1' keeps its backslashes untouched, 'say "hi"' becomes
    /// "say \"hi\"", and an empty argument survives as "".
    /// </remarks>
    public static string QuoteArgument(string? value)
    {
        // An empty argument still has to occupy a position, so it becomes an empty pair
        // of quotes rather than nothing at all.
        if (string.IsNullOrEmpty(value)) return "\"\"";

        if (!NeedsQuoting(value)) return value;

        var sb = new StringBuilder(value.Length + 8);
        sb.Append('"');

        var i = 0;
        while (i < value.Length)
        {
            var backslashes = 0;
            while (i < value.Length && value[i] == '\\')
            {
                backslashes++;
                i++;
            }

            if (i == value.Length)
            {
                // The run ends the argument, so the quote we are about to append would be
                // escaped by an odd count. Double them: the parser reads 2n backslashes
                // as n literal ones and leaves the quote as a delimiter.
                sb.Append('\\', backslashes * 2);
                break;
            }

            if (value[i] == '"')
            {
                // 2n + 1: n literal backslashes, then an escaped quote.
                sb.Append('\\', (backslashes * 2) + 1);
                sb.Append('"');
            }
            else
            {
                // Backslashes not followed by a quote are literal. Doubling them here
                // would be wrong and is the mistake in the other direction.
                sb.Append('\\', backslashes);
                sb.Append(value[i]);
            }

            i++;
        }

        sb.Append('"');
        return sb.ToString();
    }

    /// <summary>
    /// Joins an argument vector into one command line, argv[0] first.
    /// </summary>
    public static string Build(IEnumerable<string?> arguments)
    {
        ArgumentNullException.ThrowIfNull(arguments);

        var sb = new StringBuilder();
        foreach (var argument in arguments)
        {
            if (sb.Length > 0) sb.Append(' ');
            sb.Append(QuoteArgument(argument));
        }

        return sb.ToString();
    }

    /// <inheritdoc cref="Build(IEnumerable{string})"/>
    public static string Build(params string?[] arguments) => Build((IEnumerable<string?>)arguments);

    /// <remarks>
    /// Whitespace or a quote. char.IsWhiteSpace matches what the PowerShell's [\s"] did,
    /// which is wider than the space and tab the parser actually treats as delimiters.
    /// Quoting more than strictly necessary is harmless; quoting less is not.
    /// </remarks>
    private static bool NeedsQuoting(string value)
    {
        foreach (var c in value)
        {
            if (char.IsWhiteSpace(c) || c == '"') return true;
        }

        return false;
    }
}
