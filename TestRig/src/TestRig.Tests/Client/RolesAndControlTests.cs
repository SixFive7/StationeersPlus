using System.Text.Json;
using TestRig.Contracts;
using TestRig.Core.Client;
using TestRig.Core.Rig;
using TestRig.Core.Session;
using Xunit;

namespace TestRig.Tests.Client;

/// <summary>
/// Who is what, and the control plane's timeout and error handling.
/// </summary>
public sealed class RolesAndControlTests
{
    // =====================================================================
    // the live role
    // =====================================================================

    [Fact]
    public void ThePluginsOwnRoleIsPreferredSoNothingOutHereReDerivesIt()
    {
        // The IsClient trap: a listen host reports isServer TRUE and isClient FALSE, exactly
        // like a dedicated server.
        var status = new StatusResponse
        {
            Role = "listenHost",
            NetworkRole = "Client",
            IsClient = false,
            IsServer = true,
        };
        Assert.Equal("listenHost", InstanceRoles.LiveRoleOf(status));
    }

    [Fact]
    public void TheFallbackReadsNetworkRoleAndBatchModeRatherThanIsClient()
    {
        Assert.Equal("dedicated", InstanceRoles.LiveRoleOf(new StatusResponse { NetworkRole = "Server", BatchMode = true }));
        Assert.Equal("listenHost", InstanceRoles.LiveRoleOf(new StatusResponse { NetworkRole = "Server", BatchMode = false }));
        Assert.Equal("listenHost", InstanceRoles.LiveRoleOf(new StatusResponse { NetworkRole = "Server" }));
        Assert.Equal("joinedClient", InstanceRoles.LiveRoleOf(new StatusResponse { NetworkRole = "Client" }));
        Assert.Equal("singlePlayer", InstanceRoles.LiveRoleOf(new StatusResponse { Phase = "inWorld" }));
        Assert.Equal("menu", InstanceRoles.LiveRoleOf(new StatusResponse { Phase = "menu" }));
    }

    [Fact]
    public void AStatusThatAnswersNothingUsefulYieldsAnEmptyStringRatherThanAGuess()
    {
        Assert.Equal("", InstanceRoles.LiveRoleOf(null));
        Assert.Equal("", InstanceRoles.LiveRoleOf(new StatusResponse { Phase = "loading" }));
    }

    // =====================================================================
    // the attached-joiner count
    // =====================================================================

    [Fact]
    public void NullAndZeroAreDifferentAnswersAndBothCallersDependOnIt()
    {
        // Collapsing null to zero turns a teardown refusal into a silent proceed.
        Assert.Null(InstanceRoles.AttachedJoinerCount(null, "listenHost"));
        Assert.Null(InstanceRoles.AttachedJoinerCount(new StatusResponse(), "listenHost"));
        Assert.Equal(0, InstanceRoles.AttachedJoinerCount(
            new StatusResponse { ConnectedClients = [] }, "listenHost"));
    }

    [Fact]
    public void AJoinersOwnCountDescribesItsHostsSessionSoItIsNeverAnswered()
    {
        var status = new StatusResponse { PlayersInGame = 4 };
        Assert.Null(InstanceRoles.AttachedJoinerCount(status, "joinedClient"));
        Assert.Null(InstanceRoles.AttachedJoinerCount(status, "menu"));
        Assert.Equal(3, InstanceRoles.AttachedJoinerCount(status, "listenHost"));
    }

    [Fact]
    public void TheHostsOwnRosterEntryIsExcludedSoALoneHostReportsZero()
    {
        var status = new StatusResponse
        {
            Instance = new InstanceBlock { ClientId = "900000000001" },
            ConnectedClients = [new ConnectedClient { ClientId = "900000000001", IsHost = true }],
        };
        Assert.Equal(0, InstanceRoles.AttachedJoinerCount(status, "listenHost"));
    }

    [Fact]
    public void TheLosslessClientIdIsPreferredOverTheNumericOne()
    {
        // /status.localClientId is a JSON NUMBER and a ClientId is above 2^53, so a value read
        // through a double loses precision, which is exactly the failure these ids detect.
        var status = new StatusResponse
        {
            LocalClientId = 9007199254740993,
            Instance = new InstanceBlock { ClientId = "9007199254740993" },
        };
        Assert.Equal("9007199254740993", InstanceRoles.OwnClientId(status));
    }

    [Fact]
    public void WithoutARosterTheCountIsPlayersInGameMinusOneClampedAtZero()
    {
        Assert.Equal(0, InstanceRoles.AttachedJoinerCount(new StatusResponse { PlayersInGame = 1 }, "singlePlayer"));
        Assert.Equal(2, InstanceRoles.AttachedJoinerCount(new StatusResponse { PlayersInGame = 3 }, "singlePlayer"));
    }

    // =====================================================================
    // classification
    // =====================================================================

    private static InstanceRuntime Runtime(
        string name,
        bool alive = true,
        StatusResponse? status = null,
        string provisionedRole = "client")
    {
        var entry = new InstanceEntry { InstanceName = name, Port = 27700, Role = provisionedRole, ClientId = "1" };
        var paths = new InstancePaths(name, "t", "e", "b", "r", "s", "d", "m", "p", "st", "u", "l");
        return new InstanceRuntime
        {
            Name = name,
            Entry = entry,
            Paths = paths,
            Alive = alive,
            Status = status,
            ProvisionedRole = provisionedRole,
            LiveRole = InstanceRoles.LiveRoleOf(status),
            Phase = status?.Phase ?? "",
            JoinerCount = InstanceRoles.AttachedJoinerCount(status, InstanceRoles.LiveRoleOf(status)),
        };
    }

    [Fact]
    public void ADeadProcessIsStoppedAndNothingElseIsAsked()
    {
        var rt = Runtime("x", alive: false);
        InstanceRoles.Classify([rt]);
        Assert.Equal(InstanceClass.Stopped, rt.Class);
        Assert.Equal("process not running", rt.ClassSource);
    }

    [Fact]
    public void EachLiveRoleMapsToItsClassAndOwningAWorldNeedsThePhaseToo()
    {
        var host = Runtime("h", status: new StatusResponse { Role = "listenHost", Phase = "inWorld" });
        var hostLoading = Runtime("h2", status: new StatusResponse { Role = "listenHost", Phase = "menu" });
        var solo = Runtime("s", status: new StatusResponse { Role = "singlePlayer", Phase = "inWorld" });
        var joiner = Runtime("j", status: new StatusResponse { Role = "joinedClient", Phase = "inWorld" });
        var menu = Runtime("m", status: new StatusResponse { Role = "menu", Phase = "menu" });

        InstanceRoles.Classify([host, hostLoading, solo, joiner, menu]);

        Assert.Equal(InstanceClass.Host, host.Class);
        Assert.True(host.OwnsWorld);
        Assert.Equal(InstanceClass.Host, hostLoading.Class);
        Assert.False(hostLoading.OwnsWorld);
        Assert.Equal(InstanceClass.Standalone, solo.Class);
        Assert.True(solo.OwnsWorld);
        Assert.Equal(InstanceClass.Joiner, joiner.Class);
        Assert.True(joiner.NeedsDisconnect);
        Assert.Equal(InstanceClass.Standalone, menu.Class);
        Assert.False(menu.OwnsWorld);
    }

    [Fact]
    public void AnswersInAWorldButWillNotSayWhoseIsPossiblyHostAndOwnsIt()
    {
        // The plugin emits the literal "unknown" when it cannot compute a role, which is
        // answered, in a world, and will not say whose.
        var rt = Runtime("x", status: new StatusResponse { Role = "unknown", Phase = "inWorld" });
        InstanceRoles.Classify([rt]);
        Assert.Equal(InstanceClass.PossiblyHost, rt.Class);
        Assert.True(rt.OwnsWorld);
    }

    [Fact]
    public void AnsweredAndNotInAWorldIsBootingSoThereIsNoWorldToLose()
    {
        var rt = Runtime("x", status: new StatusResponse { Phase = "loading" });
        InstanceRoles.Classify([rt]);
        Assert.Equal(InstanceClass.Standalone, rt.Class);
        Assert.False(rt.OwnsWorld);
    }

    [Fact]
    public void ASilentInstanceProvisionedAsAHostIsAlwaysPossiblyHost()
    {
        var rt = Runtime("x", status: null, provisionedRole: "host");
        InstanceRoles.Classify([rt]);
        Assert.Equal(InstanceClass.PossiblyHost, rt.Class);
        Assert.Contains("provisioned as a host", rt.ClassSource, StringComparison.Ordinal);
    }

    [Fact]
    public void ASilentInstanceIsRelaxedOnAColdBootAndParanoidTheMomentAnybodyIsJoined()
    {
        // The single subtlest piece of logic on this half. anyoneJoined is computed across the
        // WHOLE rig, so a silent process cannot be ruled out as somebody's host.
        var silent = Runtime("silent", status: null);
        InstanceRoles.Classify([silent]);
        Assert.Equal(InstanceClass.Joiner, silent.Class);

        var silentAgain = Runtime("silent", status: null);
        var joiner = Runtime("joiner", status: new StatusResponse { Role = "joinedClient" });
        InstanceRoles.Classify([silentAgain, joiner]);
        Assert.Equal(InstanceClass.PossiblyHost, silentAgain.Class);
        Assert.Contains("cannot be ruled out as its host", silentAgain.ClassSource, StringComparison.Ordinal);
    }

    [Fact]
    public void ASilentInstanceWhoseEntryPredatesRolesSaysSoInItsOwnSource()
    {
        var rt = Runtime("x", status: null, provisionedRole: "");
        InstanceRoles.Classify([rt]);
        Assert.Contains("predates --role", rt.ClassSource, StringComparison.Ordinal);
    }

    [Fact]
    public void AnEmptySetIsALegitimateInput()
    {
        Assert.Empty(InstanceRoles.Classify([]));
    }

    // =====================================================================
    // teardown risk
    // =====================================================================

    [Fact]
    public void AttachedClientsOutsideTheTeardownAreNamedWithBothUsernameAndId()
    {
        var host = Runtime("host1", status: new StatusResponse
        {
            Role = "listenHost",
            Phase = "inWorld",
            Instance = new InstanceBlock { ClientId = "1" },
            ConnectedClients =
            [
                new ConnectedClient { ClientId = "1", Username = "host1", IsHost = true },
                new ConnectedClient { ClientId = "77", Username = "stranger" },
            ],
        });
        InstanceRoles.Classify([host]);

        var risk = InstanceRoles.HostTeardownRisk(host, [host], []);
        var reason = Assert.Single(risk.Reasons);
        Assert.Contains("stranger (77)", reason, StringComparison.Ordinal);
        Assert.True(risk.Blocked);
    }

    [Fact]
    public void ARosterEntryForAJoinerThatHasALREADYEXITEDIsReportedAndDoesNotBlock()
    {
        // CLIENT-160. The PowerShell built its "about to leave" set from every runtime in the
        // teardown INCLUDING the stopped ones, so a stale roster entry read as cleared and
        // vanished. Blocking on it instead would make a host whose joiner crashed untearable.
        var host = Runtime("host1", status: new StatusResponse
        {
            Role = "listenHost",
            Phase = "inWorld",
            Instance = new InstanceBlock { ClientId = "1" },
            ConnectedClients = [new ConnectedClient { ClientId = "99", Username = "ghost" }],
        });
        var departed = Runtime("ghost", alive: false);
        departed.Entry.GetType();

        var teardown = new List<InstanceRuntime>
        {
            host,
            new()
            {
                Name = "ghost",
                Entry = new InstanceEntry { InstanceName = "ghost", ClientId = "99", Port = 27702 },
                Paths = new InstancePaths("ghost", "t", "e", "b", "r", "s", "d", "m", "p", "st", "u", "l"),
                Alive = false,
            },
        };
        InstanceRoles.Classify(teardown);

        var risk = InstanceRoles.HostTeardownRisk(host, teardown, []);
        Assert.False(risk.Blocked);
        Assert.Contains("already exited", Assert.Single(risk.StaleRosterEntries), StringComparison.Ordinal);
    }

    [Fact]
    public void WithoutARosterAnUnattributableCountIsReportedAsSuch()
    {
        var host = Runtime("host1", status: new StatusResponse { Role = "listenHost", Phase = "inWorld", PlayersInGame = 3 });
        InstanceRoles.Classify([host]);

        var risk = InstanceRoles.HostTeardownRisk(host, [host], []);
        Assert.Contains("cannot be matched by id", Assert.Single(risk.Reasons), StringComparison.Ordinal);
    }

    [Fact]
    public void ALiveJoinerOrASilentProcessOutsideTheTeardownEachProduceAReason()
    {
        var host = Runtime("host1", status: new StatusResponse { Role = "listenHost", Phase = "inWorld" });
        var outsideJoiner = Runtime("j", status: new StatusResponse { Role = "joinedClient" });
        var outsideSilent = Runtime("s", status: null);
        var outsideDead = Runtime("d", alive: false);

        InstanceRoles.Classify([host, outsideJoiner, outsideSilent, outsideDead]);
        var risk = InstanceRoles.HostTeardownRisk(host, [host], [outsideJoiner, outsideSilent, outsideDead]);

        Assert.Equal(2, risk.Reasons.Count);
        Assert.Contains(risk.Reasons, r => r.Contains("is a joined client", StringComparison.Ordinal));
        Assert.Contains(risk.Reasons, r => r.Contains("does not answer", StringComparison.Ordinal));
    }

    [Fact]
    public void TheTeardownOrderIsFixedAndAnythingUnrecognisedGoesLast()
    {
        Assert.Equal(
            [InstanceClass.Stopped, InstanceClass.Joiner, InstanceClass.Standalone, InstanceClass.Host, InstanceClass.PossiblyHost],
            InstanceRoles.TeardownOrder);

        var host = Runtime("h");
        host.Class = InstanceClass.Host;
        var joiner = Runtime("j");
        joiner.Class = InstanceClass.Joiner;
        var odd = Runtime("o");
        odd.Class = InstanceClass.Unknown;

        var sequence = InstanceRoles.InTeardownOrder([host, odd, joiner]);
        Assert.Equal(["j", "h", "o"], sequence.Select(static r => r.Name));
    }

    // =====================================================================
    // the control plane's timeouts
    // =====================================================================

    [Fact]
    public void TheRequestedTimeoutIsReadFromTheQueryStringAsWellAsTheBody()
    {
        // Every body field can also be passed as a query parameter, and a Windows path HAS to
        // be: a JSON body decodes \b and \f, so C:\builds does not survive a round trip.
        Assert.Equal(5000, ControlPlane.RequestedTimeoutMs("/save?timeoutMs=5000", null));
        Assert.Equal(5000, ControlPlane.RequestedTimeoutMs("/save", """{"timeoutMs":5000}"""));
        Assert.Equal(5000, ControlPlane.RequestedTimeoutMs("/save", """{"timeoutMs":"5000"}"""));
        Assert.Equal(0, ControlPlane.RequestedTimeoutMs("/save", """{"wait":true}"""));
    }

    [Fact]
    public void TheLargerOfTheTwoWins()
    {
        Assert.Equal(9000, ControlPlane.RequestedTimeoutMs("/save?timeoutMs=9000", """{"timeoutMs":1000}"""));
        Assert.Equal(9000, ControlPlane.RequestedTimeoutMs("/save?timeoutMs=1000", """{"timeoutMs":9000}"""));
    }

    [Fact]
    public void AHandTypedBodyThatIsNotJsonNeverThrowsWhileWorkingOutATimeout()
    {
        // Read with a regex ON PURPOSE: working out a timeout must never be the thing that
        // throws on a body the plugin would have accepted, or refused with an explanation
        // worth reading.
        Assert.Equal(0, ControlPlane.RequestedTimeoutMs("/save", "{not json at all"));
        Assert.Equal(4000, ControlPlane.RequestedTimeoutMs("/save", "garbage \"timeoutMs\": 4000 more garbage"));
    }

    [Fact]
    public void ALongPathGetsItsFloorEvenWithAQueryStringOrATrailingSlash()
    {
        var fixture = new ClientFixture();
        foreach (var path in new[] { "/host", "/host/", "/host?timeoutMs=1", "/HOST" })
        {
            Assert.True(fixture.Control.TimeoutSecondsFor(path, null) >= RigConstants.ControlLongPathSeconds,
                $"{path} did not get the long-path floor");
        }

        Assert.Equal(RigConstants.ControlTimeoutFloorSeconds, fixture.Control.TimeoutSecondsFor("/status", null));
    }

    [Fact]
    public void TheDerivedTimeoutIsTheRequestPlusTheMarginAndTheFloorWinsWhenItIsLower()
    {
        var fixture = new ClientFixture();

        // 600 s asked plus a 30 s margin.
        Assert.Equal(630, fixture.Control.TimeoutSecondsFor("/status", """{"timeoutMs":600000}"""));
        // 1 s asked is under the floor, so the floor wins.
        Assert.Equal(RigConstants.ControlTimeoutFloorSeconds,
            fixture.Control.TimeoutSecondsFor("/status", """{"timeoutMs":1000}"""));
    }

    [Fact]
    public void AnExplicitOverrideWinsOverEverything()
    {
        var fixture = new ClientFixture();
        Assert.Equal(7, fixture.Control.TimeoutSecondsFor("/host", """{"timeoutMs":600000}""", 7));
    }

    [Fact]
    public void HittingTheCeilingWarnsWithTheACTUALNUMBER()
    {
        // CLIENT-242: the PowerShell interpolated a variable that did not exist in that scope,
        // so this rendered as "capping the launcher's HTTP timeout at s." with no number at
        // all, and was non-fatal only because strict mode was off.
        var fixture = new ClientFixture();
        var seconds = fixture.Control.TimeoutSecondsFor("/status", """{"timeoutMs":99000000}""");

        Assert.Equal(RigConstants.ControlTimeoutCeilingSeconds, seconds);
        Assert.True(fixture.Output.Warned($"at {RigConstants.ControlTimeoutCeilingSeconds}s"));
        Assert.False(fixture.Output.Warned("at s."));
    }

    // =====================================================================
    // the control plane's error extraction
    // =====================================================================

    [Fact]
    public void TheFourFieldNamesArePreferredInOrder()
    {
        Assert.Equal("E", ControlPlane.ErrorDetail(new ControlAnswer(409, """{"error":"E","warning":"W","result":"R","message":"M"}""", null)));
        Assert.Equal("W", ControlPlane.ErrorDetail(new ControlAnswer(409, """{"warning":"W","result":"R"}""", null)));
        Assert.Equal("R", ControlPlane.ErrorDetail(new ControlAnswer(409, """{"result":"R","message":"M"}""", null)));
        Assert.Equal("M", ControlPlane.ErrorDetail(new ControlAnswer(409, """{"message":"M"}""", null)));
    }

    [Fact]
    public void ABodyThatIsNotJsonComesBackRaw()
    {
        Assert.Equal("<html>gateway</html>",
            ControlPlane.ErrorDetail(new ControlAnswer(502, "<html>gateway</html>", null)));
    }

    [Fact]
    public void WithNoBodyAtAllTheTransportsOwnMessageIsTheAnswer()
    {
        Assert.Equal("connection refused",
            ControlPlane.ErrorDetail(new ControlAnswer(0, null, "connection refused")));
        Assert.Contains("HTTP 500", ControlPlane.ErrorDetail(new ControlAnswer(500, null, null)), StringComparison.Ordinal);
    }

    [Fact]
    public void ValidJsonCarryingNoneOfTheFourNamesStillComesBackRatherThanAStatusCode()
    {
        Assert.Equal("""{"ok":false,"detail":"something"}""",
            ControlPlane.ErrorDetail(new ControlAnswer(409, """{"ok":false,"detail":"something"}""", null)));
    }

    // =====================================================================
    // readiness over the wire
    // =====================================================================

    [Fact]
    public void PingUsesTheLivenessEndpointAndAThreeSecondBudget()
    {
        var fixture = new ClientFixture();
        fixture.Transport.Standing(27701, Endpoints.Ping, ScriptedAnswer.Ok("""{"ok":true}"""));

        Assert.True(fixture.ReachedStage(27701, ReadinessStage.Ping));

        var sent = Assert.Single(fixture.Transport.Sent);
        Assert.Equal(Endpoints.Ping, sent.Path);
        Assert.Equal(TimeSpan.FromSeconds(3), sent.Timeout);
    }

    [Fact]
    public void AnyFailureMeansNotThereYetRatherThanAnError()
    {
        var fixture = new ClientFixture();
        fixture.Transport.Standing(27701, Endpoints.Status, ScriptedAnswer.Silent());
        Assert.False(fixture.ReachedStage(27701, ReadinessStage.Menu));

        fixture.Transport.Standing(27701, Endpoints.Status, ScriptedAnswer.Ok("not json"));
        Assert.False(fixture.ReachedStage(27701, ReadinessStage.Menu));
    }

    [Fact]
    public void AParsedStatusIsEvaluatedAgainstTheSharedThresholds()
    {
        var fixture = new ClientFixture();
        var body = JsonSerializer.Serialize(
            new StatusResponse { Ok = true, Phase = "menu", GameInitialized = true, LoadedPluginCount = 42 },
            RigJsonContext.Default.StatusResponse);
        fixture.Transport.Standing(27701, Endpoints.Status, ScriptedAnswer.Ok(body));

        Assert.True(fixture.ReachedStage(27701, ReadinessStage.Menu));
        Assert.True(fixture.ReachedStage(27701, ReadinessStage.ModsLoaded));
        Assert.False(fixture.ReachedStage(27701, ReadinessStage.InWorld));
    }
}
