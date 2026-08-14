using System.Text.RegularExpressions;

namespace TestRig.Core.Rig;

/// <summary>
/// How <c>logs --grep</c> and <c>logs --tail</c> combine. One implementation, both halves.
/// </summary>
/// <remarks>
/// <para>
/// The surface says "Combines with --tail: filter first, then tail the matches", and that is
/// the behaviour here. Both halves used to do the opposite: they tailed the file and then
/// grepped that window, so a pattern that matched a hundred times across a log returned
/// whatever happened to fall in the last fifty LINES rather than the last fifty MATCHES,
/// which for an error grep is usually nothing at all.
/// </para>
/// <para>
/// Worse, the window was applied only when <c>tail != 50</c>, a magic-number stand-in for
/// "was it typed" in a binary that has <see cref="TestRig.Cli"/>'s <c>WasTyped</c> for exactly
/// that question, so an explicit <c>--tail 50</c> silently searched the whole file. There is
/// no such question here any more: the tail is always the window over the MATCHES, and its
/// default of 50 reads as "the fifty most recent matches", which is what a reader expects.
/// </para>
/// <para>
/// Two copies of this logic in two halves is what let them drift in the first place, so there
/// is one, and each half's cap constant is defined from this one.
/// </para>
/// </remarks>
public static class LogFilter
{
    /// <summary>The most lines a grep will ever print, however large a tail is asked for.</summary>
    /// <remarks>
    /// A ceiling on output, not on searching. <c>logs --tail 20 --grep Error</c> against a
    /// soak run could otherwise return four thousand lines into an agent's context.
    /// </remarks>
    public const int MatchCap = 500;

    /// <param name="Shown">The lines to print, oldest first.</param>
    /// <param name="Matched">How many lines matched in the whole file.</param>
    public sealed record Result(IReadOnlyList<string> Shown, int Matched);

    /// <summary>Filters the whole file, then keeps the last <paramref name="tail"/> matches.</summary>
    /// <param name="tail">
    /// The window over the MATCHES. Zero or less means "every match", still capped at
    /// <see cref="MatchCap"/>.
    /// </param>
    public static Result Apply(IReadOnlyList<string> lines, Regex pattern, int tail)
    {
        ArgumentNullException.ThrowIfNull(lines);
        ArgumentNullException.ThrowIfNull(pattern);

        var matches = new List<string>();
        foreach (var line in lines)
        {
            if (pattern.IsMatch(line)) matches.Add(line);
        }

        var window = tail > 0 ? Math.Min(tail, MatchCap) : MatchCap;
        if (matches.Count <= window) return new Result(matches, matches.Count);

        return new Result(matches.GetRange(matches.Count - window, window), matches.Count);
    }

    /// <summary>What to say when the window hid some of the matches, or null when it did not.</summary>
    public static string? Trimmed(Result result)
    {
        ArgumentNullException.ThrowIfNull(result);
        if (result.Matched <= result.Shown.Count) return null;

        return $"[Logs] {result.Matched} lines matched and the last {result.Shown.Count} are shown "
               + "(newest last). Narrow the pattern, or raise --tail to widen the window over the matches.";
    }
}
