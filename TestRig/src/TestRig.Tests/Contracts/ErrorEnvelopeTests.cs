using System.Text.Json;
using TestRig.Contracts;
using Xunit;

namespace TestRig.Tests.Contracts;

/// <summary>
///     One error body, five statuses, and two of them mean completely different things.
///     A config lookup failure is <c>{ok:false, error}</c> at HTTP <b>200</b>, while a
///     refusal is the identical body at <b>409</b>. The PowerShell harness routed the two
///     down different paths (a non-2xx arrived as a transport throw and was retried as a
///     rig flake, a 200 was read as success) and nothing noticed they were the same shape.
/// </summary>
public sealed class ErrorEnvelopeTests
{
    private static T Parse<T>(string json) =>
        JsonSerializer.Deserialize<T>(json, RigJson.Options)!;

    [Fact]
    public void WireErrorRoundTrips()
    {
        const string json = """{"ok":false,"error":"unknown endpoint '/console/run'. GET /help lists them all."}""";

        var parsed = Parse<WireError>(json);

        Assert.False(parsed.Ok);
        Assert.StartsWith("unknown endpoint '/console/run'", parsed.Error);
    }

    /// <summary>The classification table, in full. Nothing else may invent its own.</summary>
    [Theory]
    [InlineData(RigStatus.Ok, true, RigOutcome.Success)]
    [InlineData(RigStatus.Ok, false, RigOutcome.InBandFailure)]
    [InlineData(RigStatus.BadRequest, false, RigOutcome.BadRequest)]
    [InlineData(RigStatus.NotFound, false, RigOutcome.UnknownEndpoint)]
    [InlineData(RigStatus.Refused, false, RigOutcome.Refused)]
    [InlineData(RigStatus.ServerError, false, RigOutcome.ServerError)]
    [InlineData(RigStatus.MainThreadTimeout, false, RigOutcome.MainThreadTimeout)]
    [InlineData(503, false, RigOutcome.Unexpected)]
    public void ClassifyCoversEveryStatusThePluginEmits(int status, bool ok, RigOutcome expected)
    {
        Assert.Equal(expected, RigResult<StatusResponse>.Classify(status, ok));
    }

    /// <summary>
    ///     The measured hazard, stated as a test: the same body at 200 and at 409 must not
    ///     classify the same way, and neither may be decided by the status alone.
    /// </summary>
    [Fact]
    public void TheSameFailureBodyAtTwoHundredAndAtFourZeroNineAreDifferentOutcomes()
    {
        var body = Parse<ConfigResponse>(
            """{"ok":false,"error":"no plugin with GUID 'net.missing' found in any loaded assembly"}""");

        var inBand = new RigResult<ConfigResponse>(RigStatus.Ok, body, null);
        var refused = new RigResult<ConfigResponse>(RigStatus.Refused, body, null);

        Assert.Equal(RigOutcome.InBandFailure, inBand.Outcome);
        Assert.Equal(RigOutcome.Refused, refused.Outcome);
        Assert.NotEqual(inBand.Outcome, refused.Outcome);

        // Both are failures, and both would read as success to anything that tests the
        // status for 200 or tests it for "not an error status".
        Assert.False(inBand.Ok);
        Assert.False(refused.Ok);
        Assert.Equal(RigStatus.Ok, inBand.HttpStatus);
    }

    /// <summary>A success is 200 AND <c>ok:true</c>, never one of the two.</summary>
    [Fact]
    public void SuccessNeedsBothTheStatusAndTheBody()
    {
        var ok = Parse<StatusResponse>("""{"ok":true,"role":"listenHost"}""");
        var notOk = Parse<StatusResponse>("""{"ok":false}""");

        Assert.Equal(RigOutcome.Success, new RigResult<StatusResponse>(RigStatus.Ok, ok, null).Outcome);
        Assert.Equal(RigOutcome.InBandFailure, new RigResult<StatusResponse>(RigStatus.Ok, notOk, null).Outcome);
        Assert.Equal(RigOutcome.Refused, new RigResult<StatusResponse>(RigStatus.Refused, ok, null).Outcome);
    }

    /// <summary>An unparseable response must never read as success.</summary>
    [Fact]
    public void AMissingBodyIsNotOk()
    {
        var result = new RigResult<StatusResponse>(RigStatus.Ok, null, null);

        Assert.False(result.Ok);
        Assert.Equal(RigOutcome.InBandFailure, result.Outcome);
    }

    [Fact]
    public void ErrorMessageComesOffTheEnvelope()
    {
        var envelope = Parse<WireError>("""{"ok":false,"error":"cannot host from gameState=Running."}""");
        var result = new RigResult<HostResponse>(RigStatus.Refused, null, envelope);

        Assert.Equal("cannot host from gameState=Running.", result.ErrorMessage);
        Assert.Equal(RigOutcome.Refused, result.Outcome);
    }

    /// <summary>
    ///     The endpoints known to answer <c>ok:false</c> at HTTP 200. Listed explicitly so
    ///     that a caller writing a transport has the set in front of it, and so the list
    ///     fails here if one of these response types loses its error member.
    /// </summary>
    [Fact]
    public void EveryInBandFailureShapeCarriesAnErrorMember()
    {
        const string failure = """{"ok":false,"error":"boom"}""";

        Assert.Equal("boom", Parse<ConfigResponse>(failure).Error);
        Assert.Equal("boom", Parse<ConfigSetResponse>(failure).Error);
        Assert.Equal("boom", Parse<ConfigReloadResponse>(failure).Error);
        Assert.Equal("boom", Parse<ReflectResponse>(failure).Error);
        Assert.Equal("boom", Parse<ReflectMembersResponse>(failure).Error);
        Assert.Equal("boom", Parse<ColorsResponse>(failure).Error);
        Assert.Equal("boom", Parse<PluginsResponse>(failure).Error);
        Assert.Equal("boom", Parse<SavesResponse>(failure).Error);
        Assert.Equal("boom", Parse<ConsoleCommandsResponse>(failure).Error);
        Assert.Equal("boom", Parse<ModSettingsResponse>(failure).Error);
        Assert.Equal("boom", Parse<ModSettingsListResponse>(failure).Error);
        Assert.Equal("boom", Parse<ModalClickResponse>(failure).Error);
    }

    /// <summary>
    ///     The 504 body is not a refusal and not a server error. It says the Unity main
    ///     thread never ran the work, which is a different thing to retry and a different
    ///     thing to report.
    /// </summary>
    [Fact]
    public void MainThreadTimeoutIsItsOwnOutcome()
    {
        var envelope = Parse<WireError>(
            """{"ok":false,"error":"timed out after 20000 ms waiting for the Unity main thread. framesSeen=0 itemsRun=0 lastPump=none"}""");

        var result = new RigResult<StatusResponse>(RigStatus.MainThreadTimeout, null, envelope);

        Assert.Equal(RigOutcome.MainThreadTimeout, result.Outcome);
        Assert.NotEqual(RigOutcome.Refused, result.Outcome);
        Assert.NotEqual(RigOutcome.ServerError, result.Outcome);
    }

    /// <summary>An unknown path is its own outcome, and the catalogue can answer before the request goes out.</summary>
    [Fact]
    public void UnknownEndpointIsDetectableBeforeAndAfterTheRequest()
    {
        Assert.False(Endpoints.Exists("/console/run"));

        var envelope = Parse<WireError>(
            """{"ok":false,"error":"unknown endpoint '/console/run'. GET /help lists them all."}""");
        var result = new RigResult<ConsoleExecResponse>(RigStatus.NotFound, null, envelope);

        Assert.Equal(RigOutcome.UnknownEndpoint, result.Outcome);
    }
}
