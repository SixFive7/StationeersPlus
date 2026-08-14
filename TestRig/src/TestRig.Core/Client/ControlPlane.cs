using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using TestRig.Contracts;
using TestRig.Core.Rig;

namespace TestRig.Core.Client;

/// <summary>One raw answer from an instance's control plane.</summary>
/// <param name="HttpStatus">0 when nothing was received at all.</param>
/// <param name="Body">The response body, whatever its status. A 409 carries the diagnosis.</param>
/// <param name="TransportError">Set when the request never completed: refused, timed out, DNS.</param>
public readonly record struct ControlAnswer(int HttpStatus, string? Body, string? TransportError)
{
    /// <summary>Whether anything came back at all, at any status.</summary>
    public bool Answered => TransportError is null;
}

/// <summary>
/// The transport under the control plane.
/// </summary>
/// <remarks>
/// An interface so the suite can drive every branch of the timeout derivation, the error
/// extraction and the fan-out without a listening socket. The PowerShell suite faked this
/// with a script block that answered <c>/dlc</c> with <c>{ok, owned}</c> while the real
/// checks read <c>state.removedOwned</c>, so 399 assertions passed against a shape the
/// plugin has never emitted. Here the fake and the real implementation both answer with
/// bytes, and the typed layer above deserialises the same way for both.
/// </remarks>
public interface IControlTransport
{
    /// <summary>Sends one request. A non-2xx status is a RESULT, never an exception.</summary>
    /// <param name="bodyJson">Null makes it a GET. Anything else makes it a POST.</param>
    Task<ControlAnswer> SendAsync(
        int port,
        string path,
        string? bodyJson,
        TimeSpan timeout,
        CancellationToken ct = default);
}

/// <summary>
/// Talking to one instance's control plane from outside the game.
/// </summary>
/// <remarks>
/// Loopback only, always (CLIENT-232). The plane binds 127.0.0.1 and nothing off this
/// machine can reach it.
/// </remarks>
public sealed class ControlPlane
{
    private readonly IControlTransport _transport;
    private readonly Abstractions.IOutput _output;

    public ControlPlane(IControlTransport transport, Abstractions.IOutput output)
    {
        _transport = transport;
        _output = output;
    }

    // ---- timeouts ----------------------------------------------------------

    /// <summary>
    /// The <c>timeoutMs</c> the CALLER asked the endpoint for, from the query string OR the
    /// body. 0 when the request names none.
    /// </summary>
    /// <remarks>
    /// Both are read because every body field can also be passed as a query parameter, and
    /// a Windows path HAS to be: a JSON body decodes <c>\b</c> and <c>\f</c>, so
    /// <c>C:\builds</c> does not survive a body round trip (CLIENT-235). A port that parsed
    /// only the body would silently reinstate the timeout bug this derivation exists to fix.
    ///
    /// Read with a regex rather than a JSON parse ON PURPOSE (CLIENT-236): this runs on a
    /// hand-typed body, and working out a timeout must never be the thing that throws on a
    /// body the plugin would have accepted, or refused with an explanation worth reading.
    ///
    /// The larger of the two wins (CLIENT-237).
    /// </remarks>
    public static long RequestedTimeoutMs(string? path, string? bodyJson)
    {
        long best = 0;

        foreach (var (text, pattern) in new[]
                 {
                     (path, @"[?&]timeoutMs=(\d+)"),
                     (bodyJson, "\"timeoutMs\"\\s*:\\s*\"?(\\d+)"),
                 })
        {
            if (string.IsNullOrEmpty(text)) continue;

            var match = Regex.Match(text, pattern, RegexOptions.IgnoreCase, TimeSpan.FromSeconds(1));
            if (!match.Success) continue;

            if (long.TryParse(match.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
                && parsed > best)
            {
                best = parsed;
            }
        }

        return best;
    }

    /// <summary>
    /// How long ONE request gets before the HTTP client gives up.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This used to be a constant, and the constant WON: a request telling the endpoint to
    /// take up to five minutes was cut off by the launcher at two. Every long endpoint was
    /// therefore unusable through the launcher, and the plugin's own answer, which is the
    /// only thing that says WHY a join or a host attempt failed, was thrown away with the
    /// connection (CLIENT-240).
    /// </para>
    /// <para>
    /// The rule: the caller's own <c>timeoutMs</c> rounded up to seconds plus a margin,
    /// capped at the ceiling, never below a floor. The floor is 300 s for a long path and
    /// 120 s otherwise, matched with the query string stripped and a trailing slash trimmed,
    /// case-insensitively, so <c>/host?timeoutMs=300000</c> and <c>/host/</c> both get it
    /// (CLIENT-239).
    /// </para>
    /// </remarks>
    /// <param name="overrideSeconds">An explicit override wins over everything (CLIENT-238).</param>
    public int TimeoutSecondsFor(string path, string? bodyJson, int overrideSeconds = 0)
    {
        if (overrideSeconds > 0) return overrideSeconds;

        var bare = (path ?? "").Split('?')[0].TrimEnd('/');
        var floor = RigConstants.ControlLongPaths.Contains(bare.ToLowerInvariant(), StringComparer.Ordinal)
            ? RigConstants.ControlLongPathSeconds
            : RigConstants.ControlTimeoutFloorSeconds;

        var asked = RequestedTimeoutMs(path, bodyJson);
        if (asked <= 0) return floor;

        var derived = (int)Math.Min(
            RigConstants.ControlTimeoutCeilingSeconds,
            Math.Ceiling(asked / 1000.0) + RigConstants.ControlTimeoutMarginSeconds);

        if (derived >= RigConstants.ControlTimeoutCeilingSeconds)
        {
            // CLIENT-242 fixed: the PowerShell interpolated a variable that did not exist in
            // that scope, so this rendered as "capping the launcher's HTTP timeout at s."
            // with no number at all, and was non-fatal only because strict mode was off.
            _output.Line(Abstractions.OutputLevel.Warning,
                $"[Call] The request asks for timeoutMs {asked}; capping the launcher's HTTP timeout at "
                + $"{RigConstants.ControlTimeoutCeilingSeconds}s. The instance may still be working when this returns.");
        }

        return derived > floor ? derived : floor;
    }

    // ---- calls -------------------------------------------------------------

    /// <summary>Sends one request and hands back the raw answer.</summary>
    public Task<ControlAnswer> RawAsync(
        int port, string path, string? bodyJson, int timeoutSeconds, CancellationToken ct = default) =>
        _transport.SendAsync(port, path, bodyJson, TimeSpan.FromSeconds(timeoutSeconds), ct);

    /// <summary>
    /// Sends one request and deserialises it into the endpoint's own response record.
    /// </summary>
    /// <remarks>
    /// The result carries the status AND the body's own <c>ok</c>, because neither alone is
    /// sufficient: a config lookup failure arrives as <c>{"ok":false}</c> at HTTP 200, while
    /// a refusal arrives with the identical body at 409.
    /// </remarks>
    public async Task<RigResult<TResponse>> CallAsync<TResponse>(
        int port,
        string path,
        string? bodyJson,
        int timeoutSeconds,
        System.Text.Json.Serialization.Metadata.JsonTypeInfo<TResponse> typeInfo,
        CancellationToken ct = default)
        where TResponse : class, IWireResult
    {
        var answer = await RawAsync(port, path, bodyJson, timeoutSeconds, ct).ConfigureAwait(false);

        if (!answer.Answered)
        {
            return new RigResult<TResponse>(0, null, new WireError { Ok = false, Error = answer.TransportError });
        }

        TResponse? body = null;
        WireError? error = null;

        if (!string.IsNullOrWhiteSpace(answer.Body))
        {
            try
            {
                body = JsonSerializer.Deserialize(answer.Body, typeInfo);
            }
            catch (JsonException)
            {
                body = null;
            }

            if (body is null || !body.Ok)
            {
                error = new WireError { Ok = false, Error = ErrorDetail(answer) };
            }
        }
        else
        {
            error = new WireError { Ok = false, Error = $"HTTP {answer.HttpStatus} with no body." };
        }

        return new RigResult<TResponse>(answer.HttpStatus, body, error);
    }

    /// <summary>
    /// A <c>/status</c> read with a short timeout, or null when nothing answered.
    /// </summary>
    /// <remarks>
    /// The five seconds is the PowerShell's, and it is the number that makes a wedged
    /// instance expensive to classify. It stays a parameter so a caller that is already
    /// over its budget can shorten it.
    /// </remarks>
    public async Task<(StatusResponse? Status, string Error)> StatusAsync(
        int port, int timeoutSeconds = 5, CancellationToken ct = default)
    {
        var answer = await RawAsync(port, Endpoints.Status, null, timeoutSeconds, ct).ConfigureAwait(false);
        if (!answer.Answered) return (null, answer.TransportError ?? "no answer");
        if (string.IsNullOrWhiteSpace(answer.Body)) return (null, $"HTTP {answer.HttpStatus} with no body");

        try
        {
            var parsed = JsonSerializer.Deserialize(answer.Body, RigJsonContext.Default.StatusResponse);
            return parsed is null ? (null, "empty /status payload") : (parsed, "");
        }
        catch (JsonException ex)
        {
            return (null, $"/status did not parse: {ex.Message}");
        }
    }

    /// <summary>
    /// The useful part of a failed call.
    /// </summary>
    /// <remarks>
    /// The plugin answers a refusal or a timeout with 409 AND a diagnostic body, and the
    /// status code alone throws away the only explanation there is (CLIENT-166). The four
    /// field names are tried in order (CLIENT-167); a body that is not JSON is returned raw
    /// (CLIENT-168); with no body at all the transport's own message is the answer
    /// (CLIENT-169).
    /// </remarks>
    public static string ErrorDetail(ControlAnswer answer)
    {
        if (!string.IsNullOrWhiteSpace(answer.Body))
        {
            try
            {
                using var doc = JsonDocument.Parse(answer.Body);
                if (doc.RootElement.ValueKind == JsonValueKind.Object)
                {
                    foreach (var field in new[] { "error", "warning", "result", "message" })
                    {
                        if (!doc.RootElement.TryGetProperty(field, out var value)) continue;
                        var text = value.ValueKind == JsonValueKind.String ? value.GetString() : value.ToString();
                        if (!string.IsNullOrEmpty(text)) return text;
                    }
                }
            }
            catch (JsonException)
            {
                return answer.Body;
            }

            // Valid JSON carrying none of the four names. The body is still the only
            // diagnosis there is, so it goes back rather than being replaced by a status.
            return answer.Body;
        }

        if (!string.IsNullOrEmpty(answer.TransportError)) return answer.TransportError;
        return $"HTTP {answer.HttpStatus} with no body.";
    }

    // ---- readiness ---------------------------------------------------------

    /// <summary>
    /// Whether an instance is at or past a stage.
    /// </summary>
    /// <remarks>
    /// Any failure means "not there yet", never an error (CLIENT-246): a barrier polls this
    /// against an instance that is still booting, and a throw would end the wait.
    /// </remarks>
    public async Task<bool> ReachedStageAsync(int port, ReadinessStage stage, CancellationToken ct = default)
    {
        try
        {
            if (stage == ReadinessStage.Ping)
            {
                // /ping never touches the Unity main thread, so it answers while the game is
                // wedged, and three seconds is enough for a loopback socket (CLIENT-244).
                var answer = await RawAsync(port, Endpoints.Ping, null, 3, ct).ConfigureAwait(false);
                return answer.Answered && answer.HttpStatus == RigStatus.Ok;
            }

            var (status, _) = await StatusAsync(port, 5, ct).ConfigureAwait(false);
            return ReadinessStages.Reached(status, stage);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return false;
        }
    }
}
