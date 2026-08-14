using System.Text.Json.Nodes;
using TestRig.Contracts;

namespace TestRig.Playtest.Flakes;

/// <summary>Which kind of call produced a probe. Fixed, so a detector never has to ask.</summary>
public enum ProbeKind
{
    /// <summary>An action endpoint answered, and the answer was not a success.</summary>
    Action,

    /// <summary>A request did not complete at the transport layer.</summary>
    Transport,

    /// <summary>A readiness barrier gave up.</summary>
    Barrier,

    /// <summary>State read back after an action, which disagreed with it.</summary>
    PostState,

    /// <summary>The rig session lock could not be refreshed.</summary>
    Lock,
}

/// <summary>
///     What a flake detector is given. One shape for every call site.
/// </summary>
/// <param name="Kind">Which call produced it.</param>
/// <param name="Instance">The instance name, when there is one.</param>
/// <param name="Path">The endpoint, query string included.</param>
/// <param name="Attempt">1-based attempt number.</param>
/// <param name="Response">The parsed response body, when one arrived.</param>
/// <param name="Status">A status blob, when the site read one.</param>
/// <param name="Error">Transport error text, when the call threw.</param>
/// <param name="Stage">The readiness stage, for a barrier probe.</param>
/// <param name="Blocking">
///     True when the endpoint freezes that instance's whole control plane. This is what
///     lets silence during a blocking call be explained rather than read as a dead process.
/// </param>
public sealed record FlakeProbe(
    ProbeKind Kind,
    string Instance = "",
    string Path = "",
    int Attempt = 1,
    JsonNode? Response = null,
    StatusResponse? Status = null,
    string Error = "",
    string Stage = "",
    bool Blocking = false);

/// <summary>
///     What to do about a matched flake.
/// </summary>
/// <remarks>
///     <b>There were four of these and there are now three.</b> PowerShell declared a
///     <c>wait</c> remedy alongside <c>retry</c>, and the two were behaviourally identical:
///     both slept <c>GapSeconds</c> and re-issued the same call, with no code path anywhere
///     distinguishing them. <c>wait</c> was documentation wearing a remedy's clothes, and its
///     real content, "be patient for a long time", lived entirely in the detector's own
///     <c>MaxAttempts</c> and <c>GapSeconds</c>.
///     <para>
///     Making the distinction real would need a liveness probe that can answer while a
///     blocking call holds the control plane, and no such probe exists: a blocking endpoint
///     freezes the listener, <c>/ping</c> included. So the honest move is to collapse them
///     and keep <c>control-plane-silent</c>'s patience where it always actually was, in
///     6 attempts 10 seconds apart. A remedy name no code path honours is exactly the kind
///     of documentation-pretending-to-be-behaviour this port exists to remove.
///     </para>
/// </remarks>
public enum FlakeRemedy
{
    /// <summary>Sleep the gap and re-issue the same call, up to MaxAttempts total attempts.</summary>
    Retry,

    /// <summary>Stop and start that ONE instance by name, then sleep the gap, then re-issue.</summary>
    RestartInstance,

    /// <summary>End the check as inconclusive at once, without sleeping.</summary>
    Abort,
}

/// <summary>
///     A real detector over a real probe, not a category name.
/// </summary>
/// <param name="Name">The detector's identity, and the detector recorded on the check.</param>
/// <param name="Summary">Prose, embedded verbatim in the inconclusive message.</param>
/// <param name="Remedy">What to do about it.</param>
/// <param name="MaxAttempts">Bound on attempts. Always at least 1; there is no unbounded retry anywhere.</param>
/// <param name="GapSeconds">Wait between attempts.</param>
/// <param name="Reference">A document pointer, printed by the taxonomy listing.</param>
/// <param name="Test">The test itself.</param>
public sealed record FlakeDetector(
    string Name,
    string Summary,
    FlakeRemedy Remedy,
    int MaxAttempts,
    double GapSeconds,
    string Reference,
    Func<FlakeProbe, bool> Test);

/// <summary>Endpoint path handling, matching the plugin router's own rule.</summary>
public static class Paths
{
    /// <summary>
    ///     The endpoint without its query string or trailing slash, lower case.
    /// </summary>
    /// <remarks>
    ///     Query parameters are how a Windows path is sent, so matching on the raw path would
    ///     miss every request that carried one.
    /// </remarks>
    public static string Bare(string? path)
    {
        if (string.IsNullOrEmpty(path)) return string.Empty;

        var cut = path.IndexOf('?', StringComparison.Ordinal);
        var withoutQuery = cut >= 0 ? path[..cut] : path;
        return Endpoints.Normalize(withoutQuery);
    }
}
