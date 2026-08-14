using System.Text.Json;
using TestRig.Contracts;
using TestRig.Core.Client;
using TestRig.Core.Rig;
using TestRig.Core.Session;
using TestRig.Tests.Session.Fakes;
using Xunit;

namespace TestRig.Tests.Client;

/// <summary>
/// The verbs that read: call, snapshot, list, logs, and the two staleness reports.
/// </summary>
public sealed class CallAndReportTests
{
    private static ClientFixture RigWith(params string[] names)
    {
        var fixture = new ClientFixture();
        var owner = fixture.Lease();
        foreach (var name in names) fixture.Create(name, owner);
        fixture.Output.Clear();
        return fixture;
    }

    private static void StartLive(ClientFixture fixture, string name, int pid)
    {
        fixture.Processes.Add(pid, "rocketstation", fixture.Clock.UtcNow);
        PidFiles.Write(fixture.Fs, fixture.Layout.PathsFor(name).PidFile, pid, fixture.Clock.UtcNow);
    }

    // =====================================================================
    // call
    // =====================================================================

    [Fact]
    public void CallIsGatedBecauseItDrivesLiveClients()
    {
        // /quit ends one, and /savepath retargets where one writes its saves.
        var fixture = RigWith("x");
        fixture.Rig.Lock.Release(fixture.Owner);

        Assert.Throws<RigRefusalException>(() => fixture.Call(fixture.Registry.Read(), Endpoints.Status));
    }

    [Fact]
    public void AnEmptyTargetSetNamesBothWaysToNameOne()
    {
        var fixture = RigWith();
        var ex = Assert.Throws<RigRefusalException>(() => fixture.Call([], Endpoints.Status, owner: fixture.Owner));
        Assert.Contains("--target <name>", ex.Message, StringComparison.Ordinal);
        Assert.Contains("--target clients", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ASingleTargetRefusalShowsThePluginsOWNEXPLANATION()
    {
        // CLIENT-261. The PowerShell's single-target branch had no try/catch at all, so every
        // hosting refusal documented in the manual surfaced as "Response status code does not
        // indicate success: 409 (Conflict)" and the diagnostic body was discarded, through the
        // verb an agent actually types.
        var fixture = RigWith("host1");
        fixture.Transport.Standing(27701, Endpoints.Host,
            ScriptedAnswer.Refused("""{"ok":false,"error":"save root is not isolated from the developer's folder"}"""));

        var ex = Assert.Throws<RigRefusalException>(() =>
            fixture.Call(fixture.Registry.Read(), Endpoints.Host, """{"world":"Lunar"}""", fixture.Owner));

        Assert.True(fixture.Output.Warned("save root is not isolated"));
        Assert.False(fixture.Output.Warned("409"));
        Assert.Contains("1 of 1 instance(s) failed", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ASingleTargetSuccessPrintsTheParsedAnswer()
    {
        var fixture = RigWith("x");
        fixture.Transport.Standing(27701, Endpoints.Status, ScriptedAnswer.Ok("""{"ok":true,"phase":"menu"}"""));

        fixture.Call(fixture.Registry.Read(), Endpoints.Status, owner: fixture.Owner);

        Assert.True(fixture.Output.Said("\"phase\": \"menu\""));
        // The value carries the RAW body, so a caller consuming --json gets exactly what the
        // plugin sent rather than something this rig re-rendered.
        Assert.Equal("""{"ok":true,"phase":"menu"}""", fixture.Output.ValueOf("response"));
    }

    [Fact]
    public void BothBranchesAgreeThatAMissingOkFieldIsSuccessAndAnExplicitFalseIsNot()
    {
        // CLIENT-263: the PowerShell's two branches disagreed about what failure meant, because
        // the single-target branch did not inspect the body at all.
        var fixture = RigWith("a", "b");
        fixture.Transport.Standing(27701, Endpoints.Nearby, ScriptedAnswer.Ok("""{"count":3}"""));
        fixture.Transport.Standing(27702, Endpoints.Nearby, ScriptedAnswer.Ok("""{"count":4}"""));

        fixture.Call(fixture.Registry.Read(), Endpoints.Nearby, owner: fixture.Owner);
        Assert.Equal("0", fixture.Output.ValueOf("callFailed"));

        var single = RigWith("a");
        single.Transport.Standing(27701, Endpoints.Nearby, ScriptedAnswer.Ok("""{"count":3}"""));
        single.Call(single.Registry.Read(), Endpoints.Nearby, owner: single.Owner);
        Assert.Equal("0", single.Output.ValueOf("callFailed"));
    }

    [Fact]
    public void AFanOutFailureNamesThePluginsExplanationAndThrowsAtTheEnd()
    {
        // CLIENT-264: the PowerShell's fan-out printed the exception message rather than the
        // plugin's explanation, which is the whole reason the extractor exists.
        var fixture = RigWith("a", "b");
        fixture.Transport.Standing(27701, Endpoints.Save, ScriptedAnswer.Ok("""{"ok":true}"""));
        fixture.Transport.Standing(27702, Endpoints.Save, ScriptedAnswer.Refused("""{"ok":false,"error":"world is loading"}"""));

        var ex = Assert.Throws<RigRefusalException>(() =>
            fixture.Call(fixture.Registry.Read(), Endpoints.Save, owner: fixture.Owner));

        Assert.True(fixture.Output.Warned("world is loading"));
        Assert.Contains("mixed state", ex.Message, StringComparison.Ordinal);
        Assert.Equal("1", fixture.Output.ValueOf("callFailed"));
    }

    [Fact]
    public void AnUnknownPathIsNamedBeforeAnythingIsSentAndTheRealOnesAreSuggested()
    {
        // The 404 body a caller would otherwise only see at runtime, on a rig it had to take
        // the lock for.
        var fixture = RigWith("x");
        fixture.Transport.Standing(27701, "/console/run", ScriptedAnswer.Ok("""{"ok":true}"""));

        fixture.Call(fixture.Registry.Read(), "/console/run", owner: fixture.Owner);

        Assert.True(fixture.Output.Warned("is not a path the plugin answers"));
        Assert.True(fixture.Output.Warned(Endpoints.ConsoleExec));
    }

    [Fact]
    public void TheDerivedTimeoutIsWhatActuallyReachesTheTransport()
    {
        var fixture = RigWith("x");
        fixture.Transport.Standing(27701, Endpoints.Connect, ScriptedAnswer.Ok("""{"ok":true}"""));

        fixture.Call(fixture.Registry.Read(), Endpoints.Connect, """{"timeoutMs":300000}""", fixture.Owner);

        var sent = Assert.Single(fixture.Transport.Sent);
        Assert.Equal(TimeSpan.FromSeconds(330), sent.Timeout);
    }

    // =====================================================================
    // snapshot
    // =====================================================================

    [Fact]
    public void ASnapshotIsAlwaysAJsonArrayEvenForOneInstance()
    {
        // The playtest harness's before-and-after diffing depends on the shape being stable.
        var fixture = RigWith("x");
        fixture.Transport.Standing(27701, Endpoints.Status, ScriptedAnswer.Ok("""{"ok":true,"phase":"menu"}"""));

        var json = fixture.Snapshot(fixture.Registry.Read());
        Assert.StartsWith("[", json.TrimStart(), StringComparison.Ordinal);

        var rows = JsonSerializer.Deserialize(json, ClientJsonContext.Default.SnapshotRowArray)!;
        Assert.Single(rows);
        Assert.Equal("x", rows[0].InstanceName);
        Assert.Equal(27701, rows[0].Port);
        Assert.Equal("menu", rows[0].Status?.Phase);
    }

    [Fact]
    public void AnInstanceThatDoesNotAnswerCarriesItsErrorRatherThanBeingOmitted()
    {
        var fixture = RigWith("a", "b");
        fixture.Transport.Standing(27701, Endpoints.Status, ScriptedAnswer.Ok("""{"ok":true,"phase":"menu"}"""));

        var rows = JsonSerializer.Deserialize(
            fixture.Snapshot(fixture.Registry.Read()), ClientJsonContext.Default.SnapshotRowArray)!;

        Assert.Equal(2, rows.Length);
        Assert.Null(rows[1].Status);
        Assert.False(string.IsNullOrEmpty(rows[1].Error));
    }

    [Fact]
    public void SnapshotIsNotLockGated()
    {
        var fixture = RigWith("x");
        fixture.Rig.Lock.Release(fixture.Owner);
        fixture.Snapshot(fixture.Registry.Read());
    }

    // ---- where a snapshot lands --------------------------------------------

    [Fact]
    public void ARelativeOutFileIsRootedAtTheRigFolderAndNotTheShellsWorkingDirectory()
    {
        // The rig folder is gitignored deny-all, precisely so a stray snapshot cannot be
        // committed by accident.
        var fixture = RigWith();
        var resolved = fixture.Half.ResolveOutFile("before.json");
        Assert.Equal(Path.Combine(fixture.Layout.ClientRoot, "before.json"), resolved);
    }

    [Fact]
    public void ADriveRelativePathIsRefusedBecauseNothingCanSayWhereItWouldLand()
    {
        var fixture = RigWith();
        var ex = Assert.Throws<RigRefusalException>(() => fixture.Half.ResolveOutFile("C:before.json"));
        Assert.Contains("drive-relative", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ARelativePathThatClimbsOutOfTheRigIsRefusedAndNamesWhereItWouldHaveGone()
    {
        var fixture = RigWith();
        var ex = Assert.Throws<RigRefusalException>(() => fixture.Half.ResolveOutFile(@"..\..\before.json"));
        Assert.Contains("climbs out of the rig folder", ex.Message, StringComparison.Ordinal);
        Assert.Contains("resolves to", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AFullyQualifiedPathOutsideTheRigIsHonouredWithAWarning()
    {
        // The rule is that an explicit full path is the caller taking responsibility. Porting
        // all three cases as one uniform refusal would lose a working case.
        var fixture = RigWith();
        var resolved = fixture.Half.ResolveOutFile(@"D:\reports\before.json");

        Assert.Equal(@"D:\reports\before.json", resolved);
        Assert.True(fixture.Output.Warned("deny-all gitignore does not cover it"));
    }

    [Fact]
    public void WritingASnapshotCreatesTheParentAndReportsTheCountAndThePath()
    {
        var fixture = RigWith("x");
        fixture.Transport.Standing(27701, Endpoints.Status, ScriptedAnswer.Ok("""{"ok":true}"""));

        fixture.Snapshot(fixture.Registry.Read(), @"reports\before.json");

        var expected = Path.Combine(fixture.Layout.ClientRoot, "reports", "before.json");
        Assert.True(fixture.Fs.FileExists(expected));
        Assert.Equal(expected, fixture.Output.ValueOf("snapshotPath"));
        Assert.Equal("1", fixture.Output.ValueOf("snapshotCount"));
    }

    // =====================================================================
    // list
    // =====================================================================

    [Fact]
    public void ListingAColdRigMakesNoHttpCallAtAllAndStillAnswers()
    {
        // list is documented as free and instant; a port that probed unconditionally would
        // make it neither.
        var fixture = RigWith("a", "b");

        var rows = fixture.List(fixture.Registry.Read());

        Assert.Empty(fixture.Transport.Sent);
        Assert.Equal(2, rows.Count);
        Assert.All(rows, r => Assert.Equal("-", r.LiveRole));
        Assert.All(rows, r => Assert.Equal("-", r.Hosting));
        Assert.All(rows, r => Assert.Equal("-", r.Clients));
    }

    [Fact]
    public void ALiveInstanceWithASilentControlPlaneShowsNoAnswerRatherThanABlank()
    {
        var fixture = RigWith("x");
        StartLive(fixture, "x", 6001);

        var row = Assert.Single(fixture.List(fixture.Registry.Read()));
        Assert.Equal("no answer", row.LiveRole);
    }

    [Fact]
    public void RowsAreOrderedByIndexAndCarryEveryIdentityColumn()
    {
        var fixture = new ClientFixture();
        var owner = fixture.Lease();
        fixture.Create("second", owner);
        fixture.Create("first", owner);

        var rows = fixture.List(fixture.Registry.Read().Reverse().ToList());

        Assert.Equal(["second", "first"], rows.Select(static r => r.InstanceName));
        Assert.Equal(1, rows[0].Index);
        Assert.Equal(27701, rows[0].Port);
        Assert.Equal(27801, rows[0].GamePort);
        Assert.Equal("900000000001", rows[0].ClientId);
        Assert.Equal("second", rows[0].Username);
        Assert.False(string.IsNullOrEmpty(rows[0].ProvisionedUtc));
    }

    [Fact]
    public void AnEmptyRigListsNothing()
    {
        Assert.Empty(new ClientFixture().List([]));
    }

    // =====================================================================
    // logs
    // =====================================================================

    [Fact]
    public void TheDefaultIsTheBepInExLogTailed()
    {
        var fixture = RigWith("x");
        var log = fixture.Layout.PathsFor("x").BepInExLog;
        fixture.Fs.AddFile(log, string.Join("\r\n", Enumerable.Range(1, 200).Select(i => $"line {i}")));

        fixture.Half.Logs("x", tail: 3);

        Assert.True(fixture.Output.Said("line 200"));
        Assert.True(fixture.Output.Said("line 198"));
        Assert.False(fixture.Output.Said("line 197"));
    }

    [Fact]
    public void AMissingBepInExLogNamesTheUnityLogThatWouldExplainAHardBootFailure()
    {
        // CLIENT-302. Every failure BEFORE BepInEx loads lands in the Unity log, which no verb
        // ever printed, so the launcher could not show a hard boot failure at all.
        var fixture = RigWith("x");
        var unity = Path.Combine(fixture.Layout.PathsFor("x").LogDir, "unity-20260814-120000.log");
        fixture.Fs.AddFile(unity, "Mono path[0] = ...\r\nFatal error");

        fixture.Half.Logs("x");

        Assert.True(fixture.Output.Said("--unity"));
        Assert.True(fixture.Output.Said(unity));
    }

    [Fact]
    public void TheUnityLogCanBeReadDirectlyAndTheNewestRunWins()
    {
        var fixture = RigWith("x");
        var dir = fixture.Layout.PathsFor("x").LogDir;

        fixture.Fs.AddFile(Path.Combine(dir, "unity-20260101-000000.log"), "old run");
        fixture.Fs.SetLastWrite(Path.Combine(dir, "unity-20260101-000000.log"), new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        fixture.Fs.AddFile(Path.Combine(dir, "unity-20260814-120000.log"), "current run");
        fixture.Fs.SetLastWrite(Path.Combine(dir, "unity-20260814-120000.log"), new DateTimeOffset(2026, 8, 14, 12, 0, 0, TimeSpan.Zero));

        fixture.Half.Logs("x", unity: true);

        Assert.True(fixture.Output.Said("current run"));
        Assert.False(fixture.Output.Said("old run"));
    }

    [Fact]
    public void GrepAndTailAreIndependentRatherThanOneSilentlyIgnoringTheOther()
    {
        var fixture = RigWith("x");
        var log = fixture.Layout.PathsFor("x").BepInExLog;
        fixture.Fs.AddFile(log, string.Join("\r\n",
        [
            "Saved Luna",
            .. Enumerable.Range(1, 50).Select(i => $"noise {i}"),
            "Saved Titan",
        ]));

        // Grep alone searches the whole file.
        fixture.Half.Logs("x", grep: "^Saved ");
        Assert.True(fixture.Output.Said("Saved Luna"));
        Assert.True(fixture.Output.Said("Saved Titan"));

        // Grep with a tail searches only that window.
        fixture.Output.Clear();
        fixture.Half.Logs("x", tail: 5, grep: "^Saved ");
        Assert.False(fixture.Output.Said("Saved Luna"));
        Assert.True(fixture.Output.Said("Saved Titan"));
    }

    [Fact]
    public void AGrepThatMatchesTooMuchStopsAndSaysHowMuchItSuppressed()
    {
        var fixture = RigWith("x");
        var log = fixture.Layout.PathsFor("x").BepInExLog;
        fixture.Fs.AddFile(log, string.Join("\r\n", Enumerable.Range(1, ClientHalf.GrepMatchCap + 50).Select(i => $"hit {i}")));

        fixture.Half.Logs("x", grep: "hit");

        Assert.True(fixture.Output.Warned("Narrow the pattern"));
    }

    [Fact]
    public void AnInvalidPatternIsReportedRatherThanThrown()
    {
        var fixture = RigWith("x");
        fixture.Fs.AddFile(fixture.Layout.PathsFor("x").BepInExLog, "content");

        fixture.Half.Logs("x", grep: "[unclosed");
        Assert.True(fixture.Output.Warned("not a valid regular expression"));
    }

    // =====================================================================
    // version and staleness
    // =====================================================================

    [Fact]
    public void AnInstanceBuiltFromAnOlderGameIsReportedStale()
    {
        var fixture = RigWith("x");
        fixture.Fs.AddFile(
            Path.Combine(RigFixture.SourceInstall, "rocketstation_Data", "StreamingAssets", "version.ini"),
            "UPDATEVERSION=Update 0.2.9999.99999\r\n");
        fixture.Env.ForgetInstallCache();

        var row = Assert.Single(fixture.Half.VersionReport(fixture.Registry.Read()));
        Assert.True(row.Stale);
        Assert.Equal("0.2.6428.27798", row.Version);
        Assert.Equal("0.2.9999.99999", row.Source);
        Assert.Contains("testrig update-game --target x", row.Remedy, StringComparison.Ordinal);
    }

    [Fact]
    public void AnUnknownVersionOnEitherSideMeansCannotTellRatherThanDiffers()
    {
        var fixture = RigWith("x");
        fixture.Fs.DeleteFile(fixture.Layout.PathsFor("x").Stamp);

        var row = Assert.Single(fixture.Half.VersionReport(fixture.Registry.Read()));
        Assert.Equal(RigEnvironment.UnknownVersion, row.Version);
        Assert.False(row.Stale);
    }

    [Fact]
    public void ADeployedRepositoryModOlderThanItsBuildIsReportedWithADeployRemedy()
    {
        var fixture = RigWith("x");
        fixture.AddRepositoryMod("SprayPaintPlus");

        var deployed = Path.Combine(fixture.Layout.PathsFor("x").ModsDir, "Local_SprayPaintPlus", "SprayPaintPlus.dll");
        fixture.Fs.AddFile(deployed, "old");
        fixture.Fs.SetLastWrite(deployed, new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));

        var build = Path.Combine(ClientFixture.RepoRoot, "Mods", "SprayPaintPlus", "SprayPaintPlus", "bin", "Release", "SprayPaintPlus.dll");
        fixture.Fs.SetLastWrite(build, new DateTimeOffset(2026, 8, 14, 0, 0, 0, TimeSpan.Zero));

        var row = Assert.Single(fixture.Half.ModStaleness(fixture.Registry.Read()));
        Assert.Equal("deployed mod", row.Kind);
        Assert.Contains("testrig deploy SprayPaintPlus", row.Remedy, StringComparison.Ordinal);
    }

    [Fact]
    public void AWorkshopModCANReportStalenessBecauseItsSourceComesFromTheModConfig()
    {
        // CLIENT-338 and its server twin. The PowerShell stripped Workshop_<id> down to the
        // published-file id and then looked for that id under the LOCAL mods folder, where it
        // can never be, so every Workshop mod was silently exempt. That is 93% of a seeded set.
        var fixture = RigWith("x");

        fixture.Fs.AddFile(@"C:\workshop\2345\mod.dll", "new");
        fixture.Fs.SetLastWrite(@"C:\workshop\2345\mod.dll", new DateTimeOffset(2026, 8, 14, 0, 0, 0, TimeSpan.Zero));

        var deployed = Path.Combine(fixture.Layout.PathsFor("x").ModsDir, "Workshop_2345", "mod.dll");
        fixture.Fs.AddFile(deployed, "old");
        fixture.Fs.SetLastWrite(deployed, new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));

        var row = Assert.Single(fixture.Half.ModStaleness(fixture.Registry.Read()));
        Assert.Equal("seeded mod", row.Kind);
        Assert.Equal("Workshop_2345", row.Name);
        Assert.Contains("testrig update-mods --target x", row.Remedy, StringComparison.Ordinal);
    }

    [Fact]
    public void StalenessIsOnlyEverReportedAndNeverFixed()
    {
        // Deleting a payload to signal staleness would break a rig rather than describe it.
        var fixture = RigWith("x");
        var deployed = Path.Combine(fixture.Layout.PathsFor("x").ModsDir, "Workshop_2345", "mod.dll");
        fixture.Fs.AddFile(@"C:\workshop\2345\mod.dll", "new");
        fixture.Fs.SetLastWrite(@"C:\workshop\2345\mod.dll", new DateTimeOffset(2026, 8, 14, 0, 0, 0, TimeSpan.Zero));
        fixture.Fs.AddFile(deployed, "old");
        fixture.Fs.SetLastWrite(deployed, new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));

        fixture.Fs.DeletedTrees.Clear();
        fixture.Half.ModStaleness(fixture.Registry.Read());

        Assert.True(fixture.Fs.FileExists(deployed));
        Assert.Empty(fixture.Fs.DeletedTrees);
    }

    [Fact]
    public void AFreshPayloadIsNotReported()
    {
        var fixture = RigWith("x");
        var deployed = Path.Combine(fixture.Layout.PathsFor("x").ModsDir, "Workshop_2345", "mod.dll");
        fixture.Fs.AddFile(@"C:\workshop\2345\mod.dll", "src");
        fixture.Fs.SetLastWrite(@"C:\workshop\2345\mod.dll", new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        fixture.Fs.AddFile(deployed, "dst");
        fixture.Fs.SetLastWrite(deployed, new DateTimeOffset(2026, 8, 14, 0, 0, 0, TimeSpan.Zero));

        Assert.Empty(fixture.Half.ModStaleness(fixture.Registry.Read()));
    }
}
