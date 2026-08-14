using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace TestRig.Playtest.Values;

/// <summary>Whether a matcher was satisfied, and the clause a failure message uses.</summary>
/// <param name="Satisfied">True when the observed value is the one the check required.</param>
/// <param name="Note">
///     Extra explanation appended after the observed value, for the two cases where "it was
///     [x]" is not enough on its own: an absent value and a non-numeric one.
/// </param>
public readonly record struct MatchVerdict(bool Satisfied, string Note)
{
    public static MatchVerdict Ok { get; } = new(true, string.Empty);

    public static MatchVerdict No { get; } = new(false, string.Empty);

    public static MatchVerdict Because(string note) => new(false, note);
}

/// <summary>
///     One comparison, and exactly one.
/// </summary>
/// <remarks>
///     <para>
///     PowerShell took the six comparisons as six optional switches and guarded the arity at
///     run time, with an unmarked throw, so a mis-called assertion reported
///     <c>inconclusive/unclassified-error</c> rather than telling the author what they did.
///     Here a matcher is one object built by one factory, so "two comparisons in one
///     assertion" is not expressible and the guard has nothing left to guard.
///     </para>
///     <para>
///     The <c>-Matches</c> collision with PowerShell's automatic <c>$Matches</c> variable
///     (defect P-09, which rendered a satisfied match as
///     <c>matches /System.Collections.Hashtable/</c>) has no analogue here: the pattern is a
///     field on this object and nothing writes to it.
///     </para>
/// </remarks>
public sealed class ValueMatcher
{
    private readonly Func<JsonNode?, MatchVerdict> _evaluate;

    private ValueMatcher(string wants, Func<JsonNode?, MatchVerdict> evaluate)
    {
        Wants = wants;
        _evaluate = evaluate;
    }

    /// <summary>The clause that goes into a message: "is at least [3]".</summary>
    public string Wants { get; }

    public MatchVerdict Evaluate(JsonNode? actual) => _evaluate(actual);

    /// <summary>Equal, under the harness's bool/number/text rule.</summary>
    public static ValueMatcher Is(object? expected) =>
        new($"is [{ValueText.RenderExpected(expected)}]",
            actual => ValueText.AreEqual(expected, actual) ? MatchVerdict.Ok : MatchVerdict.No);

    /// <summary>Not equal, under the same rule.</summary>
    public static ValueMatcher IsNot(object? expected) =>
        new($"is not [{ValueText.RenderExpected(expected)}]",
            actual => ValueText.AreEqual(expected, actual) ? MatchVerdict.No : MatchVerdict.Ok);

    /// <summary>Case-insensitive .NET regex over the rendered value.</summary>
    public static ValueMatcher Matches(string pattern)
    {
        ArgumentNullException.ThrowIfNull(pattern);
        var regex = new Regex(pattern, RegexOptions.IgnoreCase, TimeSpan.FromSeconds(2));
        return new ValueMatcher($"matches /{pattern}/",
            actual => regex.IsMatch(ValueText.Render(actual)) ? MatchVerdict.Ok : MatchVerdict.No);
    }

    /// <summary>Numeric lower bound.</summary>
    public static ValueMatcher AtLeast(object bound) => Numeric("is at least", bound, (a, b) => a >= b);

    /// <summary>Numeric upper bound.</summary>
    public static ValueMatcher AtMost(object bound) => Numeric("is at most", bound, (a, b) => a <= b);

    /// <summary>
    ///     Substring containment, through PowerShell's <c>-like</c>, over each element when
    ///     the value is a collection.
    /// </summary>
    public static ValueMatcher Contains(string operand)
    {
        ArgumentNullException.ThrowIfNull(operand);
        return new ValueMatcher($"contains [{operand}]",
            actual => ValueText.LikeContains(actual, operand) ? MatchVerdict.Ok : MatchVerdict.No);
    }

    /// <summary>
    ///     The numeric matchers, with both of their PowerShell defects closed.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///     <b>Defect P-10.</b> <c>-AtMost n</c> against an ABSENT value passed, because an
    ///     unresolved path rendered as the empty string and <c>[double]''</c> is 0 in
    ///     PowerShell, so <c>0 &lt;= n</c> held for every non-negative bound. A typo in a
    ///     select path therefore turned the assertion vacuous and the check still reported a
    ///     clean pass. Absent is now a distinct, explicitly handled case and it never
    ///     satisfies a bound.
    ///     </para>
    ///     <para>
    ///     <b>Defect P-11.</b> A bound against a non-numeric value threw
    ///     <c>Cannot convert value "listenHost" to type "System.Double"</c>, which was
    ///     unmarked and therefore landed as <c>inconclusive/unclassified-error</c> rather
    ///     than as a failure. The value WAS read and it is not a number, so the right answer
    ///     is a fail that says so.
    ///     </para>
    ///     <para>
    ///     A bound that is itself not numeric is a bug in the check rather than a reading, so
    ///     it throws unmarked and lands as inconclusive. That asymmetry is the point of the
    ///     whole model: only a reading may accuse the mod.
    ///     </para>
    /// </remarks>
    private static ValueMatcher Numeric(string label, object bound, Func<double, double, bool> compare)
    {
        ArgumentNullException.ThrowIfNull(bound);
        if (!ValueText.TryAsNumber(bound, out var boundValue))
        {
            throw new ArgumentException(
                $"{label} needs a numeric bound and was given [{ValueText.RenderExpected(bound)}]. " +
                "A non-numeric bound cannot compare against anything, so this is a mistake in the check rather than an observation about the mod.",
                nameof(bound));
        }

        return new ValueMatcher($"{label} [{ValueText.RenderExpected(bound)}]", actual =>
        {
            if (actual is null)
            {
                return MatchVerdict.Because(
                    "The value is ABSENT, which is not a number and cannot satisfy a bound. " +
                    "Check the -Select path: an unresolved path reads absent, and in the PowerShell harness that silently passed every at-most assertion.");
            }

            if (!ValueText.TryAsNumber(actual, out var actualValue))
            {
                return MatchVerdict.Because(
                    "The value is not numeric, so a bound cannot be applied to it. " +
                    "Compare it with Is or Matches instead, or select a field that carries a number.");
            }

            return compare(actualValue, boundValue) ? MatchVerdict.Ok : MatchVerdict.No;
        });
    }
}
