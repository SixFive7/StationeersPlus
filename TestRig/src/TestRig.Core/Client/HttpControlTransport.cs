using System.Net;
using System.Text;

namespace TestRig.Core.Client;

/// <summary>
/// The real control-plane transport: one loopback HTTP request.
/// </summary>
/// <remarks>
/// <para>
/// This is the one place in the client half that touches a socket, which is why it is a
/// separate type behind <see cref="IControlTransport"/>. Everything above it is pure and
/// exercised by the suite.
/// </para>
/// <para>
/// <b>The proxy is disabled explicitly (CLIENT-234 fixed).</b> The PowerShell used
/// <c>Invoke-RestMethod</c>, which honours <c>HTTP_PROXY</c>. A proxy that does not bypass
/// loopback breaks every control-plane call on the machine with an error that names the
/// proxy rather than the rig, and neither the launcher nor the plugin logs anything useful.
/// A rig talking to 127.0.0.1 has no business consulting a proxy at all.
/// </para>
/// <para>
/// <b>A non-2xx is a result, not an exception.</b> The plugin answers a refusal with 409
/// AND the diagnosis in the body, and <c>Invoke-RestMethod</c> turned that into a throw
/// whose default message was the status code alone. The whole reason
/// <see cref="ControlPlane.ErrorDetail"/> exists is to recover the body, so the transport
/// hands it over rather than discarding it.
/// </para>
/// </remarks>
public sealed class HttpControlTransport : IControlTransport, IDisposable
{
    private readonly HttpClient _client;

    public HttpControlTransport()
    {
        var handler = new HttpClientHandler
        {
            // Never a proxy on loopback.
            UseProxy = false,
            Proxy = null,
            AllowAutoRedirect = false,
            UseCookies = false,
            AutomaticDecompression = DecompressionMethods.None,
        };

        // Timeout.InfiniteTimeSpan on the client, and a per-request CancellationTokenSource
        // instead: HttpClient.Timeout is a single value for the whole instance, and this
        // transport serves calls whose budgets range from three seconds to an hour.
        _client = new HttpClient(handler, disposeHandler: true)
        {
            Timeout = Timeout.InfiniteTimeSpan,
        };
    }

    public async Task<ControlAnswer> SendAsync(
        int port,
        string path,
        string? bodyJson,
        TimeSpan timeout,
        CancellationToken ct = default)
    {
        // Loopback only, always. The plane binds 127.0.0.1 and nothing off this machine can
        // reach it, so a hostname here would be a bug rather than a feature (CLIENT-232).
        var uri = new Uri($"http://127.0.0.1:{port}{path}");

        using var request = new HttpRequestMessage(
            bodyJson is null ? HttpMethod.Get : HttpMethod.Post,
            uri);

        if (bodyJson is not null)
        {
            request.Content = new StringContent(bodyJson, Encoding.UTF8, "application/json");
        }

        using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
        budget.CancelAfter(timeout);

        try
        {
            using var response = await _client
                .SendAsync(request, HttpCompletionOption.ResponseContentRead, budget.Token)
                .ConfigureAwait(false);

            var body = await response.Content.ReadAsStringAsync(budget.Token).ConfigureAwait(false);
            return new ControlAnswer((int)response.StatusCode, body, null);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return new ControlAnswer(0, null,
                $"the control plane on port {port} did not answer {path} within {timeout.TotalSeconds:F0}s");
        }
        catch (HttpRequestException ex)
        {
            return new ControlAnswer(0, null, ex.Message);
        }
    }

    public void Dispose() => _client.Dispose();
}
