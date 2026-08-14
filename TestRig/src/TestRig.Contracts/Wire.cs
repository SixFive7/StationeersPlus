using System.Text.Json.Serialization;

namespace TestRig.Contracts;

/// <summary>
///     Every JSON response the plugin produces carries <c>ok</c>. Implementing this on
///     every response record is what makes success detection a property read rather than
///     a status-code comparison.
/// </summary>
/// <remarks>
///     Read <see cref="Ok"/>, never the HTTP status. <c>ConfigAccess</c> returns
///     <c>{"ok":false,"error":...}</c> at <b>HTTP 200</b> for a config lookup failure,
///     while a refusal returns the identical body at <b>409</b>. A caller that treats 200
///     as success believes a failed <c>/config</c> read succeeded, and a caller that
///     treats non-200 as the only failure misses it entirely.
/// </remarks>
public interface IWireResult
{
    /// <summary>The plugin's own verdict on the request. This is the success signal.</summary>
    bool Ok { get; }
}

/// <summary>
///     The universal error body: <c>{"ok":false,"error":"&lt;message&gt;"}</c>, emitted by
///     <c>HttpResponse.Error</c> at whatever status the route chose.
/// </summary>
/// <remarks>
///     One body shape, five statuses. See <see cref="RigStatus"/> for which route uses
///     which, and <see cref="RigOutcome"/> for the classification a caller should branch on.
/// </remarks>
public sealed record WireError : IWireResult
{
    /// <summary>Always false on this shape. Present so the envelope round-trips unchanged.</summary>
    [JsonPropertyName("ok")]
    public bool Ok { get; init; }

    [JsonPropertyName("error")]
    public string? Error { get; init; }
}

/// <summary>
///     The HTTP statuses the plugin actually produces. 503 has a status-text entry in
///     <c>HttpServer</c> but is never emitted, so it is deliberately absent here.
/// </summary>
public static class RigStatus
{
    /// <summary>
    ///     Success, and also the status <c>ConfigAccess</c> uses for its own lookup
    ///     failures. 200 alone proves nothing.
    /// </summary>
    public const int Ok = 200;

    /// <summary>A malformed or missing parameter. Never a refusal.</summary>
    public const int BadRequest = 400;

    /// <summary>An unknown path. See <see cref="Endpoints.Exists"/>.</summary>
    public const int NotFound = 404;

    /// <summary>
    ///     A refusal: the request was understood and the plugin declined. The tier-1
    ///     savepath gate, the host isolation gate, the duplicate-identity gate, an
    ///     unconsumed input, an unconfirmed save.
    /// </summary>
    public const int Refused = 409;

    /// <summary>An unhandled throw inside a route, carrying <c>ex.ToString()</c>.</summary>
    public const int ServerError = 500;

    /// <summary>
    ///     The Unity main-thread pump did not run the work in time. Distinct from a
    ///     refusal: the game may be minimised with rendering stalled, on a modal, or
    ///     still loading.
    /// </summary>
    public const int MainThreadTimeout = 504;
}

/// <summary>
///     What actually happened, derived from the HTTP status <b>and</b> the body's
///     <c>ok</c>. Neither input alone is sufficient.
/// </summary>
public enum RigOutcome
{
    /// <summary>200 with <c>ok:true</c>. The only success.</summary>
    Success,

    /// <summary>
    ///     200 with <c>ok:false</c>. The <c>ConfigAccess</c> shape: a lookup failure
    ///     reported in band. A status-only caller reads this as success.
    /// </summary>
    InBandFailure,

    /// <summary>400. A caller mistake: a missing or unparseable parameter.</summary>
    BadRequest,

    /// <summary>404. The path does not exist. Check <see cref="Endpoints.Exists"/> first.</summary>
    UnknownEndpoint,

    /// <summary>409. Understood and declined, or an assertion the plugin could not confirm.</summary>
    Refused,

    /// <summary>500. A throw inside the route.</summary>
    ServerError,

    /// <summary>504. The main-thread pump timed out.</summary>
    MainThreadTimeout,

    /// <summary>Any status the plugin is not known to emit. Treat as a transport fault.</summary>
    Unexpected,
}

/// <summary>
///     A parsed response paired with the status it arrived at. Construct one per call and
///     branch on <see cref="Outcome"/>.
/// </summary>
/// <remarks>
///     There is deliberately no <c>IsSuccess</c> that reads only
///     <see cref="HttpStatus"/>. The two failure modes this type exists to separate are
///     <see cref="RigOutcome.InBandFailure"/> (200 with <c>ok:false</c>) and
///     <see cref="RigOutcome.Refused"/> (409 with the same body), and the PowerShell
///     harness routed them down completely different paths without ever noticing they
///     were the same shape: a non-2xx arrived as a transport throw and was retried as a
///     rig flake, while a 200 with <c>ok:false</c> was read as success.
/// </remarks>
/// <typeparam name="TBody">The endpoint's response record.</typeparam>
public readonly struct RigResult<TBody> where TBody : class, IWireResult
{
    public RigResult(int httpStatus, TBody? body, WireError? error)
    {
        HttpStatus = httpStatus;
        Body = body;
        Error = error;
    }

    /// <summary>The transport status. Diagnostic only: never branch on this alone.</summary>
    public int HttpStatus { get; }

    /// <summary>The parsed body, when it parsed as this endpoint's shape.</summary>
    public TBody? Body { get; }

    /// <summary>The error envelope, when the body was the universal failure shape.</summary>
    public WireError? Error { get; }

    /// <summary>
    ///     The plugin's own verdict. False when the body is missing, so an unparseable
    ///     response never reads as success.
    /// </summary>
    public bool Ok => Body is { Ok: true };

    /// <summary>The classification to branch on.</summary>
    public RigOutcome Outcome => Classify(HttpStatus, Ok);

    /// <summary>The error message when there is one, from either the body or the envelope.</summary>
    public string? ErrorMessage => Error?.Error;

    /// <summary>
    ///     Classification rule, exposed so a caller that hand-rolls its own transport
    ///     cannot invent a different one.
    /// </summary>
    public static RigOutcome Classify(int httpStatus, bool bodyOk) => httpStatus switch
    {
        RigStatus.Ok => bodyOk ? RigOutcome.Success : RigOutcome.InBandFailure,
        RigStatus.BadRequest => RigOutcome.BadRequest,
        RigStatus.NotFound => RigOutcome.UnknownEndpoint,
        RigStatus.Refused => RigOutcome.Refused,
        RigStatus.ServerError => RigOutcome.ServerError,
        RigStatus.MainThreadTimeout => RigOutcome.MainThreadTimeout,
        _ => RigOutcome.Unexpected,
    };
}
