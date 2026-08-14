using TestRig.Contracts;
using TestRig.Playtest.Evidence;
using TestRig.Playtest.Model;
using TestRig.Playtest.Values;
using TestRig.Tests.Playtest.Fakes;
using Xunit;

namespace TestRig.Tests.Playtest;

/// <summary>
///     The evidence bundle: a human must be able to audit a run they did not watch.
/// </summary>
public sealed class EvidenceBundleTests
{
    private static (PlaytestFixture Fixture, PlaytestRunnerHarness Harness) Rig()
    {
        var fixture = new PlaytestFixture().WithInstance("hostie", 27701);
        return (fixture, new PlaytestRunnerHarness(fixture));
    }

    private sealed class PlaytestRunnerHarness
    {
        public PlaytestRunnerHarness(PlaytestFixture fixture)
        {
            Bundle = new EvidenceBundle(fixture.Files, PlaytestFixture.EvidencePath, "tests", fixture.Clock.UtcNow);
            Check = Bundle.NewCheck(1, "the first-use notice cap");
        }

        public EvidenceBundle Bundle { get; }

        public CheckEvidence Check { get; }
    }

    [Fact]
    public void TheBundleRootAndItsChecksFolderAreCreated()
    {
        var (fixture, _) = Rig();
        Assert.True(fixture.Files.DirectoryExists(PlaytestFixture.EvidencePath));
        Assert.True(fixture.Files.DirectoryExists(Path.Combine(PlaytestFixture.EvidencePath, "checks")));
    }

    [Fact]
    public void ACheckFolderIsNamedFromItsIndexAndItsSlug()
    {
        var (_, harness) = Rig();
        Assert.EndsWith(@"checks\01-the-first-use-notice-cap", harness.Check.Root, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TwoChecksWithTheSameNameGetDifferentFoldersBecauseTheIndexDiffers()
    {
        var (_, harness) = Rig();
        var second = harness.Bundle.NewCheck(2, "the first-use notice cap");
        Assert.NotEqual(harness.Check.Root, second.Root);
    }

    [Fact]
    public void AllFourSubfoldersExistWhetherOrNotTheyAreUsed()
    {
        var (fixture, harness) = Rig();
        foreach (var folder in new[] { "requests", "observations", "console", "launcher" })
        {
            Assert.True(fixture.Files.DirectoryExists(Path.Combine(harness.Check.Root, folder)), folder);
        }
    }

    [Fact]
    public void AnEvidenceReferenceIsBundleRelative()
    {
        Assert.Equal("requests/0001-a.json", CheckEvidence.Reference(EvidenceKind.Requests, "0001-a.json"));
        Assert.Equal("check.json", CheckEvidence.Reference(EvidenceKind.Root, "check.json"));
    }

    [Fact]
    public void AppendingAddsRatherThanReplacing()
    {
        var (fixture, harness) = Rig();
        harness.Check.Write(EvidenceKind.Console, "hostie.tail.txt", "first\n");
        harness.Check.Write(EvidenceKind.Console, "hostie.tail.txt", "second\n", append: true);

        var text = fixture.Files.ReadAllText(harness.Check.PathOf(EvidenceKind.Console, "hostie.tail.txt"));
        Assert.Equal("first\nsecond\n", text);
    }

    [Fact]
    public void EveryRequestIsRecordedIncludingTheAttemptsThatFailed()
    {
        var fixture = new PlaytestFixture().WithInstance("hostie", 27701);
        fixture.Transport.TransportFailureMessage = "the operation timed out";
        fixture.Transport.TransportFailures[Endpoints.Status] = 2;

        var ctx = fixture.Context(new CheckSpec("a check", "s", [new InstanceSpec("hostie")]));
        ctx.Act("hostie", Endpoints.Status);

        var requests = fixture.Files.AllFiles().Where(f => f.Contains(@"\requests\", StringComparison.OrdinalIgnoreCase)).ToList();
        Assert.Equal(3, requests.Count);
    }

    [Fact]
    public void ARequestRecordCarriesTheAttemptTheStatusTheBodyAndTheError()
    {
        var fixture = new PlaytestFixture().WithInstance("hostie", 27701);
        var ctx = fixture.Context(new CheckSpec("a check", "s", [new InstanceSpec("hostie")]));
        ctx.Act("hostie", Endpoints.ConfigSet, new ConfigSetRequest { Guid = "g", Section = "s", Key = "k", Value = "true" });

        var path = fixture.Files.AllFiles().Single(f => f.Contains(@"\requests\", StringComparison.OrdinalIgnoreCase));
        var json = fixture.Files.ReadAllText(path);

        Assert.Contains("\"instance\": \"hostie\"", json, StringComparison.Ordinal);
        Assert.Contains("\"method\": \"POST\"", json, StringComparison.Ordinal);
        Assert.Contains("\"attempt\": 1", json, StringComparison.Ordinal);
        Assert.Contains("\"httpStatus\": 200", json, StringComparison.Ordinal);
        Assert.Contains("\"requestBody\"", json, StringComparison.Ordinal);
        Assert.Contains("\"error\": \"\"", json, StringComparison.Ordinal);
    }

    [Fact]
    public void AnObservationRecordCarriesTheQueryThatProducedIt()
    {
        // Defect P-05: the reader args were carried on the in-memory observation and omitted
        // from the file, so a bundle reader could not tell a baseline from its re-read.
        var fixture = new PlaytestFixture().WithInstance("hostie", 27701);
        fixture.Transport.State("hostie").Things["442"] = new FakeThing { ReferenceId = 442, CustomColorIndex = 4 };

        var ctx = fixture.Context(new CheckSpec("a check", "s", [new InstanceSpec("hostie")]));
        ctx.Read("hostie", Reader.Thing, "customColorIndex", "442", new ThingRequest { RefIds = "442", Fields = "CustomColor" });

        var path = fixture.Files.AllFiles().Single(f => f.Contains(@"\observations\", StringComparison.OrdinalIgnoreCase));
        var json = fixture.Files.ReadAllText(path);

        Assert.Contains("\"readerArgs\"", json, StringComparison.Ordinal);
        Assert.Contains("\"refIds\": \"442\"", json, StringComparison.Ordinal);
        Assert.Contains("\"fields\": \"CustomColor\"", json, StringComparison.Ordinal);
        Assert.Contains("\"of\": \"442\"", json, StringComparison.Ordinal);
        Assert.Contains("\"request\": \"requests/", json, StringComparison.Ordinal);
    }

    [Fact]
    public void RequestsAndObservationsShareOneSequenceSoTheBundleReplaysInOrder()
    {
        var fixture = new PlaytestFixture().WithInstance("hostie", 27701);
        var ctx = fixture.Context(new CheckSpec("a check", "s", [new InstanceSpec("hostie")]));

        ctx.Act("hostie", Endpoints.Status);
        ctx.Read("hostie", Reader.Status, "hosting");

        var names = fixture.Files.AllFiles()
            .Where(f => f.Contains(@"\requests\", StringComparison.OrdinalIgnoreCase) || f.Contains(@"\observations\", StringComparison.OrdinalIgnoreCase))
            .Select(Path.GetFileName)
            .OrderBy(f => f, StringComparer.Ordinal)
            .ToList();

        Assert.Equal(3, names.Count);
        Assert.StartsWith("0001-", names[0], StringComparison.Ordinal);
        Assert.StartsWith("0002-", names[1], StringComparison.Ordinal);
        Assert.StartsWith("0003-", names[2], StringComparison.Ordinal);
    }

    [Fact]
    public void AConsoleTailIsAppendedOncePerStepAndIsLabelled()
    {
        var fixture = new PlaytestFixture().WithInstance("hostie", 27701);
        fixture.Transport.State("hostie").Print("console", "a console line");

        var ctx = fixture.Context(new CheckSpec("a check", "s", [new InstanceSpec("hostie")]));
        ctx.SaveConsoleTail("after bring-up");
        ctx.SaveConsoleTail("after check body");

        var path = fixture.Files.AllFiles().Single(f => f.EndsWith("hostie.tail.txt", StringComparison.OrdinalIgnoreCase));
        var text = fixture.Files.ReadAllText(path);

        Assert.Contains("===== after bring-up (", text, StringComparison.Ordinal);
        Assert.Contains("===== after check body (", text, StringComparison.Ordinal);
        Assert.Contains("a console line", text, StringComparison.Ordinal);
    }

    [Fact]
    public void AnUnreachableConsoleBecomesANoteAndNeverThrows()
    {
        var fixture = new PlaytestFixture().WithInstance("hostie", 27701);
        fixture.Transport.TransportFailures[Endpoints.ConsoleLog] = 99;

        var ctx = fixture.Context(new CheckSpec("a check", "s", [new InstanceSpec("hostie")]));
        ctx.SaveConsoleTail("after check body");

        var path = fixture.Files.AllFiles().Single(f => f.EndsWith("hostie.tail.txt", StringComparison.OrdinalIgnoreCase));
        Assert.Contains("<console unreachable:", fixture.Files.ReadAllText(path), StringComparison.Ordinal);
    }

    [Fact]
    public void ACheckCanWriteItsOwnEvidenceFile()
    {
        var fixture = new PlaytestFixture().WithInstance("hostie", 27701);
        var ctx = fixture.Context(new CheckSpec("a check", "s", [new InstanceSpec("hostie")]));

        var reference = ctx.WriteEvidence("conflict-stub-seeded.txt", "seeded");
        Assert.Equal("conflict-stub-seeded.txt", reference);
        Assert.Contains(fixture.Files.AllFiles(), f => f.EndsWith("conflict-stub-seeded.txt", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void AContextWithNoBundleStillWorksAndWritesNothing()
    {
        var fixture = new PlaytestFixture().WithInstance("hostie", 27701);
        var ctx = fixture.Context(new CheckSpec("a check", "s", [new InstanceSpec("hostie")]), withEvidence: false);

        Assert.Null(ctx.WriteEvidence("anything.txt", "content"));
        ctx.SaveConsoleTail("after bring-up");
        ctx.Act("hostie", Endpoints.Status);
        Assert.DoesNotContain(fixture.Files.AllFiles(), f => f.Contains(@"\checks\", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ALauncherInvocationIsRecordedWithItsExitCodeAndOutput()
    {
        var fixture = new PlaytestFixture().WithInstance("hostie", 27701);
        var ctx = fixture.Context(new CheckSpec("a check", "s", [new InstanceSpec("hostie")]));
        ctx.RestartInstance("hostie", "the workshop park");

        var files = fixture.Files.AllFiles().Where(f => f.Contains(@"\launcher\", StringComparison.OrdinalIgnoreCase)).ToList();
        Assert.Equal(2, files.Count);
        Assert.Contains("# testrig stop -Target hostie", fixture.Files.ReadAllText(files[0]), StringComparison.Ordinal);
        Assert.Contains("# exit    : 0", fixture.Files.ReadAllText(files[0]), StringComparison.Ordinal);
    }

    [Fact]
    public void ARenderedValueSurvivesIntoTheObservationFile()
    {
        var fixture = new PlaytestFixture().WithInstance("hostie", 27701);
        fixture.Transport.State("hostie").Hosting = true;

        var ctx = fixture.Context(new CheckSpec("a check", "s", [new InstanceSpec("hostie")]));
        ctx.Read("hostie", Reader.Status, "hosting");

        var path = fixture.Files.AllFiles().Single(f => f.Contains(@"\observations\", StringComparison.OrdinalIgnoreCase));
        Assert.Contains("\"value\": true", fixture.Files.ReadAllText(path), StringComparison.Ordinal);
    }

    [Fact]
    public void JsonHelpersRenderPrimitivesWithoutReflection()
    {
        Assert.Equal("null", PlaytestJson.Write(null));
        Assert.Equal("""{"a":1}""", PlaytestJson.WriteCompact(new System.Text.Json.Nodes.JsonObject { ["a"] = 1 }));
        Assert.Null(PlaytestJson.TryParse("not json at all {"));
        Assert.Null(PlaytestJson.TryParse(null));
        Assert.NotNull(PlaytestJson.TryParse("""{"a":1}"""));
    }

    [Fact]
    public void ADetailBlobIsAFlatMapThatCannotThrowWhileBeingBuilt()
    {
        var detail = PlaytestJson.Detail(new Dictionary<string, object?>
        {
            ["instance"] = "hostie",
            ["attempts"] = 3,
            ["hosting"] = true,
            ["missing"] = null,
        });

        Assert.Contains("\"instance\":\"hostie\"", detail, StringComparison.Ordinal);
        Assert.Contains("\"attempts\":3", detail, StringComparison.Ordinal);
        Assert.Contains("\"hosting\":true", detail, StringComparison.Ordinal);
        Assert.Contains("\"missing\":null", detail, StringComparison.Ordinal);
    }

    [Fact]
    public void StampsAreTheOneFormatTheWholeBundleUses()
    {
        var stamp = Stamps.Format(new DateTimeOffset(2026, 8, 14, 9, 30, 0, TimeSpan.FromHours(2)));
        Assert.Equal("2026-08-14T07:30:00Z", stamp);
    }
}
