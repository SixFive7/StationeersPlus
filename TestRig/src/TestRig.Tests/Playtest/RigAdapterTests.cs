using TestRig.Contracts;
using TestRig.Core.Abstractions;
using TestRig.Core.Client;
using TestRig.Core.Session;
using TestRig.Playtest.Model;
using TestRig.Playtest.Seams;
using TestRig.Tests.Client;
using TestRig.Tests.Session.Fakes;
using Xunit;

namespace TestRig.Tests.Playtest;

/// <summary>
///     The four seams that join the playtest engine to the rig.
/// </summary>
/// <remarks>
///     The engine's own suite runs entirely against fakes, which is what makes it fast and
///     deterministic; these are the adapters between those fakes and the real thing, and they
///     are exactly where the PowerShell harness's three blocking defects lived. Every one of
///     them was in glue code with no tests at all: the owner id recovered by regex from a line
///     that never printed, a 409 arriving as a transport fault, and a tier-1 path computed in
///     a composition root nothing exercised.
/// </remarks>
public sealed class RigAdapterTests
{
    // ---- transport ---------------------------------------------------------

    /// <summary>
    ///     A refusal is a RESULT. It carries a status and a body, and both survive.
    /// </summary>
    /// <remarks>
    ///     The PowerShell transport threw on any non-2xx with the body in the message, so a
    ///     409 arrived wearing a transport fault's clothes: retried three times as a rig
    ///     flake, then reported under a detector that misdiagnosed it. The duplicate-identity
    ///     refusal on <c>/connect</c> is the one that has to reach its own detector intact.
    /// </remarks>
    [Fact]
    public void ANonSuccessAnswerComesBackWithItsStatusAndItsBody()
    {
        var transport = new FakeControlTransport();
        transport.Standing(27701, Endpoints.Connect, ScriptedAnswer.Refused("{\"ok\":false,\"error\":\"duplicate identity\"}"));

        var response = new CoreRigTransport(transport).Send(27701, Endpoints.Connect, "{}", TimeSpan.FromSeconds(5));

        Assert.Equal(RigStatus.Refused, response.HttpStatus);
        Assert.Contains("duplicate identity", response.Body, StringComparison.Ordinal);
    }

    [Fact]
    public void ARequestThatNeverCompletedIsTheOnlyThingThatThrows()
    {
        var transport = new FakeControlTransport { Default = ScriptedAnswer.Silent("connection refused") };

        var thrown = Assert.Throws<RigTransportException>(() =>
            new CoreRigTransport(transport).Send(27701, Endpoints.Status, null, TimeSpan.FromSeconds(1)));

        Assert.Contains("connection refused", thrown.Message, StringComparison.Ordinal);
        Assert.Contains("27701", thrown.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void TheBodyIsSentAsGivenAndTheTimeoutIsPassedThrough()
    {
        var transport = new FakeControlTransport();
        transport.Standing(27701, Endpoints.Host, ScriptedAnswer.Ok("{\"ok\":true}"));

        new CoreRigTransport(transport).Send(27701, Endpoints.Host, "{\"world\":\"Lunar\"}", TimeSpan.FromSeconds(42));

        var (port, path, body, timeout) = Assert.Single(transport.Sent);
        Assert.Equal(27701, port);
        Assert.Equal(Endpoints.Host, path);
        Assert.Equal("{\"world\":\"Lunar\"}", body);
        Assert.Equal(TimeSpan.FromSeconds(42), timeout);
    }

    // ---- registry ----------------------------------------------------------

    [Fact]
    public void TheRegistryRowsCarryThePortTheRoleAndTheRecordedRoot()
    {
        var fixture = new ClientFixture();
        var owner = fixture.Lease();
        fixture.Create("hostie", owner, role: "host");

        var row = Assert.Single(new CoreRigRegistry(fixture.Registry).Rows());

        Assert.Equal("hostie", row.InstanceName);
        Assert.Equal("host", row.Role);
        Assert.Equal(27701, row.Port);
        Assert.Equal(ClientFixture.InstancesRoot, row.InstancesRoot);
    }

    /// <summary>An unprovisioned rig is an empty list, not a throw.</summary>
    [Fact]
    public void AnEmptyRigIsAnEmptyRowSet()
    {
        var fixture = new ClientFixture();
        Assert.Empty(new CoreRigRegistry(fixture.Registry).Rows());
    }

    // ---- launcher ----------------------------------------------------------

    /// <summary>
    ///     The owner id is a field on the grant, which is the whole reason this seam is typed.
    /// </summary>
    /// <remarks>
    ///     The PowerShell harness recovered it with a regex over launcher prose, and that
    ///     line has in fact never printed, so every check in every suite would have thrown
    ///     inconclusive and then unlocked with the id it never received, leaving the rig
    ///     locked by a session that could not release it. Both pinning assertions covering it
    ///     were source-text greps, so the suite stayed green for the entire life of a feature
    ///     that never ran.
    /// </remarks>
    [Fact]
    public void AcquiringTheLockReturnsTheOwnerIdAsAField()
    {
        var fixture = new ClientFixture();
        var launcher = Launcher(fixture);

        var grant = launcher.AcquireLock("adapter test", ttlMinutes: 0, waitSeconds: 0);

        Assert.True(grant.Success, grant.Message);
        Assert.False(string.IsNullOrWhiteSpace(grant.Owner));
        Assert.Equal(grant.Owner, fixture.Rig.ReadLockFile()!.Get(LockFields.Owner));
    }

    /// <summary>
    ///     The state reset the acquisition performed reaches the bundle as text.
    /// </summary>
    /// <remarks>
    ///     It is written to <c>hygiene-reset.txt</c> BEFORE success is checked, so a refused
    ///     lock still leaves its explanation behind. An empty report would make that file a
    ///     placeholder rather than evidence.
    /// </remarks>
    [Fact]
    public void TheGrantCarriesWhatTheStateResetReported()
    {
        var fixture = new ClientFixture();
        var grant = Launcher(fixture).AcquireLock("adapter test", 0, 0);

        Assert.Contains("[Lock] Acquired the rig session lock", grant.StateResetReport, StringComparison.Ordinal);
        Assert.Contains(grant.Owner, grant.StateResetReport, StringComparison.Ordinal);
    }

    [Fact]
    public void ARefusedLockIsAGrantThatFailedRatherThanAThrow()
    {
        var fixture = new ClientFixture();
        fixture.Rig.WriteLockFile("beef1234", "somebody else");

        var grant = Launcher(fixture).AcquireLock("adapter test", 0, 0);

        Assert.False(grant.Success);
        Assert.Equal(string.Empty, grant.Owner);
        Assert.Equal(RigExitCodes.LockHeldByOther, grant.ExitCode);
        Assert.Contains("somebody else", grant.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ReleasingSomebodyElsesLockIsAFailedResultCarryingTheContentionCode()
    {
        var fixture = new ClientFixture();
        fixture.Rig.WriteLockFile("beef1234", "somebody else");

        var result = Launcher(fixture).ReleaseLock("notmine1");

        Assert.False(result.Success);
        Assert.Equal(RigExitCodes.LockHeldByOther, result.ExitCode);
    }

    /// <summary>
    ///     A launcher verb that refuses returns, and never throws past the engine.
    /// </summary>
    /// <remarks>
    ///     A teardown that threw would skip the lock release in the runner's finally. An
    ///     instance left up holds the rig; a lock left held blocks every other agent too.
    /// </remarks>
    [Fact]
    public void AStartThatCannotHappenIsAFailedResultRatherThanAThrow()
    {
        var fixture = new ClientFixture();
        var owner = fixture.Lease();
        fixture.Create("hostie", owner);

        // The tree is gone, which start refuses. It must come back as a result.
        fixture.Fs.DeleteDirectory(Path.Combine(ClientFixture.InstancesRoot, "hostie"), recursive: true);

        var result = Launcher(fixture).StartInstance("hostie", owner);

        Assert.False(result.Success);
        Assert.Contains("has no tree at", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AnInstanceTheRegistryDoesNotKnowIsAFailedResultThatNamesTheOnesItDoes()
    {
        var fixture = new ClientFixture();
        var owner = fixture.Lease();
        fixture.Create("hostie", owner);

        var result = Launcher(fixture).StopInstance("ghost", owner, 30, force: false);

        Assert.False(result.Success);
        Assert.Contains("'ghost' is not provisioned", result.Message, StringComparison.Ordinal);
        Assert.Contains("hostie", result.Message, StringComparison.Ordinal);
    }

    // ---- the tier-1 save folder (defect P-06) ------------------------------

    /// <summary>
    ///     The path is derived, and a user-data folder that cannot be resolved is refused.
    /// </summary>
    /// <remarks>
    ///     <b>Defect P-06.</b> This was computed in a composition root with no tests at all.
    ///     A wrong path produced two missing listings, which hashed to the same sentinel,
    ///     compared equal, and reported the tier-1 safety check as clean: the one check whose
    ///     whole job is to notice the rig writing into the developer's saves could never fail.
    /// </remarks>
    [Fact]
    public void TheTierOneSaveRootIsTheUserDataFolderPlusSaves()
    {
        var paths = new RigPaths(RigFixture.Home, userDataDir: RigFixture.UserData);
        Assert.Equal(Path.Combine(RigFixture.UserData, "saves"), Tier1SaveFolder.Require(paths));
    }

    [Fact]
    public void AnUnresolvableUserDataFolderRefusesRatherThanProducingAPlausiblePath()
    {
        var paths = new RigPaths(RigFixture.Home);

        var thrown = Assert.Throws<PlaytestUsageException>(() => Tier1SaveFolder.Require(paths));
        Assert.Contains("would watch nothing", thrown.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AResolvedRootThatDoesNotExistIsWarnedAboutRatherThanReportedClean()
    {
        var fs = new FakeFileSystem();
        var paths = new RigPaths(RigFixture.Home, userDataDir: RigFixture.UserData);

        var (missing, warning) = Tier1SaveFolder.Resolve(fs, paths);
        Assert.Equal(Path.Combine(RigFixture.UserData, "saves"), missing);
        Assert.NotNull(warning);
        Assert.Contains("watch NOTHING", warning, StringComparison.Ordinal);

        fs.AddDirectory(missing);
        var (present, none) = Tier1SaveFolder.Resolve(fs, paths);
        Assert.Equal(missing, present);
        Assert.Null(none);
    }

    // ---- the capturing sink ------------------------------------------------

    [Fact]
    public void TheCapturingSinkForwardsEverythingAndCopiesOnlyInsideItsWindow()
    {
        var inner = new Session.Fakes.RecordingOutput();
        var sink = new CapturingOutput(inner);

        sink.Line(OutputLevel.Info, "before");
        sink.Begin();
        sink.Line(OutputLevel.Info, "inside");
        sink.Line(OutputLevel.Warning, "also inside");
        var window = sink.End();
        sink.Line(OutputLevel.Info, "after");

        Assert.Contains("inside", window, StringComparison.Ordinal);
        Assert.Contains("also inside", window, StringComparison.Ordinal);
        Assert.DoesNotContain("before", window, StringComparison.Ordinal);
        Assert.DoesNotContain("after", window, StringComparison.Ordinal);

        // And nothing was swallowed: a bundle and a terminal that disagree about one run is
        // worse than either alone.
        Assert.True(inner.Said("before"));
        Assert.True(inner.Said("inside"));
        Assert.True(inner.Said("after"));
    }

    /// <summary>
    ///     A launcher whose lock service writes to the SAME capturing sink it reads back.
    /// </summary>
    /// <remarks>
    ///     That is the whole mechanism: the state-reset report is prose the lock emits while
    ///     it acquires, and the composition root builds every half on one sink so the report
    ///     can be captured without threading a return value through the lock. A launcher
    ///     pointed at a sink nothing writes to would report an empty reset on every run,
    ///     which is exactly the placeholder-instead-of-evidence failure the bundle exists to
    ///     avoid.
    /// </remarks>
    private static CoreRigLauncher Launcher(ClientFixture fixture)
    {
        var rig = fixture.Rig;
        var capture = new CapturingOutput(rig.Output);
        var sessionLock = new SessionLockService(
            rig.Fs, rig.Clock, rig.Sleeper, rig.Mutex, capture, rig.Paths, rig.Busy, rig.Marker, rig.Launcher,
            rig.Executor, rig.MintOwnerId);

        return new CoreRigLauncher(sessionLock, fixture.Half, capture);
    }
}
