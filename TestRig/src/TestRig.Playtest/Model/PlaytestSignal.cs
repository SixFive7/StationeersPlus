using TestRig.Playtest.Seams;

namespace TestRig.Playtest.Model;

/// <summary>
///     Thrown when a check asks the engine to do something it cannot: an unknown endpoint, a
///     body that is not a wire type, an instance that is not one of the check's own, two
///     mutually exclusive fields on one instance spec.
/// </summary>
/// <remarks>
///     Deliberately NOT a <see cref="PlaytestSignal"/>. A mistake in the check is classified
///     as <c>inconclusive/unclassified-error</c> like any other unmarked throw, because a bug
///     in a test may never get to accuse the mod.
/// </remarks>
public sealed class PlaytestUsageException : Exception
{
    public PlaytestUsageException(string message) : base(message)
    {
    }
}

/// <summary>
///     The exception a check ends on, carrying which of the three outcomes it is.
/// </summary>
/// <remarks>
///     <para>
///     The classification lives in typed members rather than in message text, so a message
///     can be reworded without silently reclassifying a result. That was already true in
///     PowerShell (the kind travelled in <c>Exception.Data</c>); here it is a type, so a
///     caller cannot forget to stamp it.
///     </para>
///     <para>
///     <b>Only the assert verbs may construct a <see cref="SignalKind.Fail"/>.</b> The
///     constructor is internal and <see cref="Inconclusive"/> is the only factory a check
///     can reach, so "the check declared itself failed" is not expressible.
///     </para>
/// </remarks>
public sealed class PlaytestSignal : Exception
{
    internal PlaytestSignal(SignalKind kind, string message, string detector, string detail)
        : base(message)
    {
        Kind = kind;
        Detector = detector;
        Detail = detail;
    }

    public SignalKind Kind { get; }

    /// <summary>The label a report carries. See <see cref="Detectors"/>.</summary>
    public string Detector { get; }

    /// <summary>A JSON blob of whatever the raiser thought a reader would need.</summary>
    public string Detail { get; }

    /// <summary>
    ///     Declare that the check cannot make its observation. Always throws.
    /// </summary>
    /// <remarks>
    ///     There is deliberately no <c>Fail</c> counterpart. A check that believes the mod
    ///     is wrong states that as an assertion against the authority, so the report can
    ///     say what was compared with what.
    /// </remarks>
    public static PlaytestSignal Inconclusive(string because, string detector = Detectors.CheckDeclined, string detail = "null") =>
        new(SignalKind.Inconclusive, because, string.IsNullOrEmpty(detector) ? Detectors.CheckDeclined : detector, detail);

    internal static PlaytestSignal Failure(string message, string detail) =>
        new(SignalKind.Fail, message, Detectors.Assertion, detail);
}

/// <summary>How an exception out of a check body was classified.</summary>
/// <param name="Outcome">Fail or Inconclusive. Never Pass.</param>
/// <param name="Detector">The label the report carries.</param>
/// <param name="Message">What to print.</param>
/// <param name="Detail">A JSON blob for the evidence bundle.</param>
public sealed record ErrorClassification(CheckOutcome Outcome, string Detector, string Message, string Detail);

/// <summary>The single classifier. Nothing else decides what an exception means.</summary>
public static class SignalClassifier
{
    /// <summary>
    ///     How far to walk an exception's inner chain looking for a signal.
    /// </summary>
    /// <remarks>
    ///     The PowerShell original walked 12 levels because PowerShell wraps some throws,
    ///     and a wrapped signal read as unclassified would turn a real assertion failure
    ///     into an inconclusive. .NET wraps far less, but a check that catches and rethrows
    ///     inside a <c>finally</c> still nests, so the walk stays.
    /// </remarks>
    public const int MaxInnerDepth = 12;

    /// <summary>Finds the signal in an exception or its inner chain, or null.</summary>
    public static PlaytestSignal? Find(Exception? exception)
    {
        var current = exception;
        for (var depth = 0; current is not null && depth <= MaxInnerDepth; depth++)
        {
            if (current is PlaytestSignal signal) return signal;

            if (current is AggregateException aggregate)
            {
                foreach (var inner in aggregate.InnerExceptions)
                {
                    var found = Find(inner);
                    if (found is not null) return found;
                }
            }

            current = current.InnerException;
        }

        return null;
    }

    /// <summary>Finds a wire-format failure in an exception or its inner chain, or null.</summary>
    /// <remarks>
    ///     The same walk as <see cref="Find"/>, and for the same reason: a check that catches
    ///     and rethrows inside a <c>finally</c> nests the original.
    /// </remarks>
    public static RigWireFormatException? FindFormat(Exception? exception)
    {
        var current = exception;
        for (var depth = 0; current is not null && depth <= MaxInnerDepth; depth++)
        {
            if (current is RigWireFormatException format) return format;

            if (current is AggregateException aggregate)
            {
                foreach (var inner in aggregate.InnerExceptions)
                {
                    var found = FindFormat(inner);
                    if (found is not null) return found;
                }
            }

            current = current.InnerException;
        }

        return null;
    }

    /// <summary>
    ///     Classify anything thrown out of a check body.
    /// </summary>
    /// <remarks>
    ///     A marked signal keeps its own kind. <b>Anything else is inconclusive</b>, with
    ///     detector <see cref="Detectors.UnclassifiedError"/>. A null reference in the
    ///     check itself is a rig-side problem as far as the mod is concerned, and reporting
    ///     it as a failure would accuse the mod of a bug in the test.
    /// </remarks>
    public static ErrorClassification Classify(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);

        var signal = Find(exception);
        if (signal is not null)
        {
            return new ErrorClassification(
                signal.Kind == SignalKind.Fail ? CheckOutcome.Fail : CheckOutcome.Inconclusive,
                signal.Detector,
                signal.Message,
                signal.Detail);
        }

        // A wire-contract mismatch is inconclusive like anything else unmarked, but it gets
        // its own name because the remedy is a code change rather than a re-run, and because
        // the failure it replaced reported a joiner as absent from a roster it was in.
        var format = FindFormat(exception);
        if (format is not null)
        {
            return new ErrorClassification(
                CheckOutcome.Inconclusive,
                Detectors.WireFormat,
                format.Message,
                PlaytestJson.Detail(new Dictionary<string, object?>
                {
                    ["type"] = format.GetType().FullName,
                    ["inner"] = format.InnerException?.Message,
                }));
        }

        return new ErrorClassification(
            CheckOutcome.Inconclusive,
            Detectors.UnclassifiedError,
            "The check threw something the harness does not classify, so its result is inconclusive rather than a failure: " + exception.Message,
            PlaytestJson.Detail(new Dictionary<string, object?>
            {
                ["type"] = exception.GetType().FullName,
                ["stack"] = exception.StackTrace,
            }));
    }
}
