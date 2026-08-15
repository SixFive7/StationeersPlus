using System.Text;
using TestRig.Playtest.Evidence;
using TestRig.Playtest.Model;
using TestRig.Core.Session;
using TestRig.Playtest.Runner;
using TestRig.Playtest.Values;
using TestRig.Tests.Playtest.Fakes;
using Xunit;

namespace TestRig.Tests.Playtest;

/// <summary>
///     The runner, end to end: three outcomes, the two gates, the exit codes and the bundle.
/// </summary>
public sealed class RunnerEndToEndTests
{
    private static (PlaytestFixture Fixture, string CheckFile) Rig()
    {
        var fixture = new PlaytestFixture().WithInstance("hostie", 27701, "host");
        var checkFile = fixture.SeedMod("SprayPaintPlus", "net.spraypaintplus", Encoding.UTF8.GetBytes("the build"), ["hostie"]);
        return (fixture, checkFile);
    }

    private static CheckSpec Spec(string checkFile, string name = "a check") =>
        new(name, "summary", [new InstanceSpec("hostie", InstanceRole.Host, World: "Lunar")], sourceFile: checkFile);

    private static SuiteResult Run(PlaytestFixture fixture, params IPlaytestCheck[] checks) =>
        new SuiteRunner(fixture.Dependencies).Run(new SuiteRequest
        {
            SuiteName = "SprayPaintPlus",
            Checks = checks,
            EvidenceRoot = PlaytestFixture.EvidencePath,
        });

    [Fact]
    public void ACheckThatAssertsSomethingTrueIsACleanPassAndExitsZero()
    {
        var (fixture, checkFile) = Rig();
        var check = new TestCheck(Spec(checkFile), ctx =>
            ctx.AssertValue("hostie", Reader.Status, ValueMatcher.Is(true), "the host owns the world", "hosting"));

        var run = Run(fixture, check);

        Assert.Equal(CheckOutcome.Pass, run.Results[0].Outcome);
        Assert.Equal("pass", run.Results[0].Text);
        Assert.Equal(1, run.Passed);
        Assert.Equal(0, run.ExitCode);
    }

    [Fact]
    public void ACheckThatReadsTheWrongValueFailsAndExitsOne()
    {
        var (fixture, checkFile) = Rig();
        var check = new TestCheck(Spec(checkFile), ctx =>
            ctx.AssertValue("hostie", Reader.Status, ValueMatcher.Is("joinedClient"), "the mod is the suspect", "role"));

        var run = Run(fixture, check);

        Assert.Equal(CheckOutcome.Fail, run.Results[0].Outcome);
        Assert.Equal(Detectors.Assertion, run.Results[0].Detector);
        Assert.Equal(1, run.Failed);
        Assert.Equal(SuiteRunner.ExitFailed, run.ExitCode);
    }

    [Fact]
    public void ACheckThatDeclinesIsInconclusiveAndExitsTwo()
    {
        var (fixture, checkFile) = Rig();
        var check = new TestCheck(Spec(checkFile), ctx =>
            ctx.SetInconclusive("the session does not own the DLC", "dlc-not-owned"));

        var run = Run(fixture, check);

        Assert.Equal(CheckOutcome.Inconclusive, run.Results[0].Outcome);
        Assert.Equal("dlc-not-owned", run.Results[0].Detector);
        Assert.Equal("inconclusive (dlc-not-owned)", run.Results[0].Text);
        Assert.Equal(SuiteRunner.ExitInconclusive, run.ExitCode);
    }

    [Fact]
    public void AFailureBeatsAnInconclusiveInTheExitCode()
    {
        var (fixture, checkFile) = Rig();
        var failing = new TestCheck(Spec(checkFile, "failing"), ctx =>
            ctx.AssertValue("hostie", Reader.Status, ValueMatcher.Is("joinedClient"), "why", "role"));
        var declining = new TestCheck(Spec(checkFile, "declining"), ctx => ctx.SetInconclusive("nope"));

        var run = Run(fixture, failing, declining);

        Assert.Equal(1, run.Failed);
        Assert.Equal(1, run.Inconclusive);
        Assert.Equal(SuiteRunner.ExitFailed, run.ExitCode);
    }

    [Fact]
    public void ACheckWithAnEmptyBodyIsNotAPass()
    {
        // Defect P-02, and the shape the PowerShell suite registered twice while asserting
        // only the result count. There was no assertion counter anywhere in the library, so a
        // check with a valid binary block and an empty body reported a clean pass.
        var (fixture, checkFile) = Rig();
        var run = Run(fixture, new TestCheck(Spec(checkFile), _ => { }));

        Assert.Equal(CheckOutcome.Inconclusive, run.Results[0].Outcome);
        Assert.Equal(Detectors.NoAssertions, run.Results[0].Detector);
        Assert.Contains("without making a single assertion", run.Results[0].Message, StringComparison.Ordinal);
        Assert.Equal(0, run.Results[0].AssertionCount);
    }

    [Fact]
    public void ACheckThatOnlyDrivesThingsIsNotAPassEither()
    {
        var (fixture, checkFile) = Rig();
        var check = new TestCheck(Spec(checkFile), ctx =>
        {
            ctx.Act("hostie", TestRig.Contracts.Endpoints.Status);
            ctx.Read("hostie", Reader.Status, "hosting");
        });

        var run = Run(fixture, check);
        Assert.Equal(Detectors.NoAssertions, run.Results[0].Detector);
    }

    [Fact]
    public void ACheckThatCouldNotAttestIsNotAPass()
    {
        var (fixture, checkFile) = Rig();
        var identity = TestRig.Playtest.Attestation.ModIdentityResolver.Resolve(checkFile, fixture.Files);
        fixture.Files.DeleteFile(identity.BuildDllPath);

        var check = new TestCheck(Spec(checkFile), ctx =>
            ctx.AssertValue("hostie", Reader.Status, ValueMatcher.Is(true), "why", "hosting"));

        var run = Run(fixture, check);
        Assert.Equal(CheckOutcome.Inconclusive, run.Results[0].Outcome);
        Assert.Equal(Detectors.BinaryMissing, run.Results[0].Detector);
    }

    [Fact]
    public void BothGatesOnlyEverDowngradeAPass()
    {
        // A fail from an unattested check is still a fail: an assertion that read a wrong
        // value read a wrong value.
        var (fixture, checkFile) = Rig();
        var identity = TestRig.Playtest.Attestation.ModIdentityResolver.Resolve(checkFile, fixture.Files);
        fixture.Files.DeleteFile(identity.BuildDllPath);

        var check = new TestCheck(Spec(checkFile), ctx =>
            ctx.AssertValue("hostie", Reader.Status, ValueMatcher.Is("joinedClient"), "why", "role"));

        // Attestation runs before the body, so the body never runs and this is inconclusive.
        // The point of the assertion below is that the gate did not turn a fail INTO a pass.
        var run = Run(fixture, check);
        Assert.NotEqual(CheckOutcome.Pass, run.Results[0].Outcome);
    }

    [Fact]
    public void ADegradedRunIsStillAPassAndStillExitsZero()
    {
        var (fixture, checkFile) = Rig();
        fixture.Transport.TransportFailureMessage = "the operation timed out";
        fixture.Transport.TransportFailures[TestRig.Contracts.Endpoints.ConsoleExec] = 1;

        var check = new TestCheck(Spec(checkFile), ctx =>
        {
            ctx.Act("hostie", TestRig.Contracts.Endpoints.ConsoleExec, new TestRig.Contracts.ConsoleExecRequest { Command = "help" });
            ctx.AssertValue("hostie", Reader.Status, ValueMatcher.Is(true), "the host owns the world", "hosting");
        });

        var run = Run(fixture, check);
        Assert.Equal(CheckOutcome.Pass, run.Results[0].Outcome);
        Assert.True(run.Results[0].Degraded);
        Assert.Equal("pass (degraded, 2 attempts)", run.Results[0].Text);
        Assert.Equal(0, run.ExitCode);
    }

    [Fact]
    public void EveryCheckTakesAndReleasesTheLockItself()
    {
        // That buys a state reset per check, since the reset is between sessions by design and
        // two checks under one lock would get none.
        var (fixture, checkFile) = Rig();
        Run(fixture,
            new TestCheck(Spec(checkFile, "one"), _ => { }),
            new TestCheck(Spec(checkFile, "two"), _ => { }),
            new TestCheck(Spec(checkFile, "three"), _ => { }));

        Assert.Equal(3, fixture.Launcher.Calls.Count(c => c.StartsWith("lock ", StringComparison.Ordinal)));
        Assert.Equal(3, fixture.Launcher.Calls.Count(c => c.StartsWith("unlock ", StringComparison.Ordinal)));
    }

    [Fact]
    public void TheStateResetIsWrittenIntoTheBundleBeforeSuccessIsEvenChecked()
    {
        var (fixture, checkFile) = Rig();
        fixture.Launcher.LockSucceeds = false;
        fixture.Launcher.StateResetReport = "rig state: DIRTY\nrestoring 3 things";

        Run(fixture, new TestCheck(Spec(checkFile), _ => { }));

        var path = fixture.Files.AllFiles().Single(f => f.EndsWith("hygiene-reset.txt", StringComparison.OrdinalIgnoreCase));
        Assert.Contains("rig state: DIRTY", fixture.Files.ReadAllText(path), StringComparison.Ordinal);
    }

    [Fact]
    public void TheLockRecordCarriesTheOwnerThePurposeAndBothTimes()
    {
        var (fixture, checkFile) = Rig();
        Run(fixture, new TestCheck(Spec(checkFile), _ => { }));

        var path = fixture.Files.AllFiles().Single(f => f.EndsWith("lock.txt", StringComparison.OrdinalIgnoreCase));
        var text = fixture.Files.ReadAllText(path);

        Assert.Contains("owner   : a1b2c3", text, StringComparison.Ordinal);
        Assert.Contains("purpose : Playtest: a check", text, StringComparison.Ordinal);
        Assert.Contains("acquired:", text, StringComparison.Ordinal);
        Assert.Contains("released:", text, StringComparison.Ordinal);
    }

    [Fact]
    public void EveryCheckWritesItsOwnRecordWhateverTheOutcome()
    {
        var (fixture, checkFile) = Rig();
        Run(fixture,
            new TestCheck(Spec(checkFile, "passing"), ctx => ctx.AssertValue("hostie", Reader.Status, ValueMatcher.Is(true), "why", "hosting")),
            new TestCheck(Spec(checkFile, "failing"), ctx => ctx.AssertValue("hostie", Reader.Status, ValueMatcher.Is(false), "why", "hosting")),
            new TestCheck(Spec(checkFile, "declining"), ctx => ctx.SetInconclusive("nope")));

        Assert.Equal(3, fixture.Files.AllFiles().Count(f => f.EndsWith("check.json", StringComparison.OrdinalIgnoreCase)));
    }

    [Fact]
    public void TheCheckRecordCarriesEnoughToAuditItWithoutTheRun()
    {
        var (fixture, checkFile) = Rig();
        Run(fixture, new TestCheck(Spec(checkFile), ctx =>
            ctx.AssertValue("hostie", Reader.Status, ValueMatcher.Is(true), "why", "hosting")));

        var path = fixture.Files.AllFiles().Single(f => f.EndsWith("check.json", StringComparison.OrdinalIgnoreCase));
        var json = fixture.Files.ReadAllText(path);

        Assert.Contains("\"outcome\": \"pass\"", json, StringComparison.Ordinal);
        Assert.Contains("\"assertions\": 1", json, StringComparison.Ordinal);
        Assert.Contains("\"lockOwner\": \"a1b2c3\"", json, StringComparison.Ordinal);
        Assert.Contains("\"startedUtc\"", json, StringComparison.Ordinal);
        Assert.Contains("\"endedUtc\"", json, StringComparison.Ordinal);
    }

    [Fact]
    public void TheRunReportCountsEverythingAndNamesTheTierOneVerdict()
    {
        var (fixture, checkFile) = Rig();
        var run = Run(fixture, new TestCheck(Spec(checkFile), ctx =>
            ctx.AssertValue("hostie", Reader.Status, ValueMatcher.Is(true), "why", "hosting")));

        var json = fixture.Files.ReadAllText(Path.Combine(PlaytestFixture.EvidencePath, "run.json"));
        Assert.Contains("\"passed\": 1", json, StringComparison.Ordinal);
        Assert.Contains("\"exitCode\": 0", json, StringComparison.Ordinal);
        Assert.Contains("\"verdict\": \"IDENTICAL\"", json, StringComparison.Ordinal);
        Assert.Equal(0, run.ExitCode);
    }

    [Fact]
    public void TheHumanReportIsATableAndATierOneSentence()
    {
        var (fixture, checkFile) = Rig();
        Run(fixture, new TestCheck(Spec(checkFile), ctx =>
            ctx.AssertValue("hostie", Reader.Status, ValueMatcher.Is(true), "why", "hosting")));

        var markdown = fixture.Files.ReadAllText(Path.Combine(PlaytestFixture.EvidencePath, "run.md"));
        Assert.Contains("| Check | Outcome | Retries | Assertions | Detectors | Evidence |", markdown, StringComparison.Ordinal);
        Assert.Contains("Passed 1, failed 0, inconclusive 0. Exit code 0.", markdown, StringComparison.Ordinal);
        Assert.Contains("is unchanged across this run", markdown, StringComparison.Ordinal);
    }

    [Fact]
    public void BothSaveInventoriesAndTheVerdictAreWritten()
    {
        var (fixture, checkFile) = Rig();
        Run(fixture, new TestCheck(Spec(checkFile), _ => { }));

        foreach (var name in new[] { "save-inventory-before.txt", "save-inventory-after.txt", "save-inventory.verdict.txt" })
        {
            Assert.True(fixture.Files.FileExists(Path.Combine(PlaytestFixture.EvidencePath, name)), name);
        }
    }

    [Fact]
    public void AWrongTierOneRootIsReportedLoudlyRatherThanAsAGreenResult()
    {
        var (fixture, checkFile) = Rig();
        fixture.Tier1SaveRoot = @"C:\no\such\folder";

        var run = Run(fixture, new TestCheck(Spec(checkFile), _ => { }));

        Assert.Equal(Tier1Verdict.RootMissing, run.Tier1.Verdict);
        Assert.False(run.Tier1.Identical);
        Assert.Contains(fixture.Log, l => l.Contains("watched NOTHING", StringComparison.Ordinal));
    }

    [Fact]
    public void AChangedTierOneFolderIsShoutedAbout()
    {
        var (fixture, checkFile) = Rig();
        var check = new TestCheck(Spec(checkFile), _ =>
            fixture.Files.AddFile(Path.Combine(PlaytestFixture.Tier1Path, "leaked.save"), "oh no"));

        var run = Run(fixture, check);

        Assert.Equal(Tier1Verdict.Changed, run.Tier1.Verdict);
        Assert.Contains(fixture.Log, l => l.Contains("CHANGED across this run", StringComparison.Ordinal));
    }

    [Fact]
    public void TheFilterSelectsASubsetAndTheRightOne()
    {
        var (fixture, checkFile) = Rig();
        var run = Run(fixture,
            new TestCheck(Spec(checkFile, "alpha check"), ctx => ctx.AssertValue("hostie", Reader.Status, ValueMatcher.Is(true), "why", "hosting")),
            new TestCheck(Spec(checkFile, "beta check"), ctx => ctx.AssertValue("hostie", Reader.Status, ValueMatcher.Is(true), "why", "hosting")));

        Assert.Equal(2, run.Results.Count);

        var selected = SuiteRunner.Select(
        [
            new TestCheck(Spec(checkFile, "alpha check"), _ => { }),
            new TestCheck(Spec(checkFile, "beta check"), _ => { }),
        ], "alpha*");

        Assert.Single(selected);
        Assert.Equal("alpha check", selected[0].Spec.Name);
    }

    [Fact]
    public void TheFilterIsAWildcardAndIsAppliedOnce()
    {
        IPlaytestCheck[] checks =
        [
            new TestCheck(new CheckSpec("the host own client half", "s", [new InstanceSpec("hostie")]), _ => { }),
            new TestCheck(new CheckSpec("the join summary", "s", [new InstanceSpec("hostie")]), _ => { }),
        ];

        Assert.Equal(2, SuiteRunner.Select(checks, "*").Count);
        Assert.Single(SuiteRunner.Select(checks, "the host*"));
        Assert.Single(SuiteRunner.Select(checks, "*summary"));
        Assert.Empty(SuiteRunner.Select(checks, "nothing*"));
    }

    [Fact]
    public void ARunThatSelectsNothingSaysWhatWasRegistered()
    {
        var (fixture, checkFile) = Rig();
        var thrown = Assert.Throws<PlaytestUsageException>(() =>
            new SuiteRunner(fixture.Dependencies).Run(new SuiteRequest
            {
                SuiteName = "SprayPaintPlus",
                Checks = [new TestCheck(Spec(checkFile, "alpha check"), _ => { })],
                EvidenceRoot = PlaytestFixture.EvidencePath,
                Only = "gamma*",
            }));

        Assert.Contains("alpha check", thrown.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void EachCheckGetsItsOwnFolderNumberedInRunOrder()
    {
        var (fixture, checkFile) = Rig();
        var run = Run(fixture,
            new TestCheck(Spec(checkFile, "alpha check"), _ => { }),
            new TestCheck(Spec(checkFile, "beta check"), _ => { }));

        Assert.Equal("01-alpha-check", run.Results[0].EvidenceFolder);
        Assert.Equal("02-beta-check", run.Results[1].EvidenceFolder);
    }

    [Fact]
    public void ACheckThatThrowsSomethingUnclassifiedNeverAccusesTheMod()
    {
        var (fixture, checkFile) = Rig();
        var run = Run(fixture, new TestCheck(Spec(checkFile), _ => throw new InvalidOperationException("a bug in the check")));

        Assert.Equal(CheckOutcome.Inconclusive, run.Results[0].Outcome);
        Assert.Equal(Detectors.UnclassifiedError, run.Results[0].Detector);
        Assert.Equal(SuiteRunner.ExitInconclusive, run.ExitCode);
    }

    [Fact]
    public void TheSuiteUsesTheProcessExitCodesAndNotAPrivateNumbering()
    {
        // A caller that cannot tell them apart will eventually treat one as the other, and
        // they mean opposite things about the mod. They also have to be the codes the PROCESS
        // returns: these were a local 1 and 2 that the CLI translated on the way out, so a run
        // that correctly exited 8 wrote "Exit code 2" into run.md, run.json and the console
        // summary. The bundle is what somebody reads afterwards, having not watched the run.
        Assert.NotEqual(SuiteRunner.ExitFailed, SuiteRunner.ExitInconclusive);
        Assert.Equal(RigExitCodes.Failed, SuiteRunner.ExitFailed);
        Assert.Equal(RigExitCodes.PlaytestInconclusive, SuiteRunner.ExitInconclusive);
        Assert.Equal(8, SuiteRunner.ExitInconclusive);
    }

    [Fact]
    public void TheEvidenceBundleReportsTheCodeTheProcessReturns()
    {
        // Read out of run.md and run.json rather than off the constant, because the defect was
        // in what the bundle SAID: the number reached those files through SuiteResult.ExitCode
        // and was translated only at the process boundary.
        var (fixture, checkFile) = Rig();
        var check = new TestCheck(Spec(checkFile), ctx => ctx.SetInconclusive("nothing to measure", "check-declined"));

        var run = Run(fixture, check);

        Assert.Equal(RigExitCodes.PlaytestInconclusive, run.ExitCode);
        Assert.Contains(
            SuiteRunner.RenderConsoleSummary(run, "evidence"),
            line => line.Contains("(exit 8)", StringComparison.Ordinal));
        Assert.Contains("Exit code 8.", SuiteRunner.RenderRunMarkdown(run), StringComparison.Ordinal);
        Assert.Contains("\"exitCode\": 8", SuiteRunner.RenderRunJson(run), StringComparison.Ordinal);
    }

    [Fact]
    public void TheDetectorsARunSawAreAllCarriedForward()
    {
        var (fixture, checkFile) = Rig();
        fixture.Transport.TransportFailureMessage = "the operation timed out";
        fixture.Transport.TransportFailures[TestRig.Contracts.Endpoints.ConsoleExec] = 1;

        var check = new TestCheck(Spec(checkFile), ctx =>
        {
            ctx.Act("hostie", TestRig.Contracts.Endpoints.ConsoleExec, new TestRig.Contracts.ConsoleExecRequest { Command = "help" });
            ctx.SetInconclusive("and then it declined", "check-declined");
        });

        var run = Run(fixture, check);
        Assert.Contains("transport-error", run.Results[0].Detectors);
        Assert.Contains("check-declined", run.Results[0].Detectors);
    }
}
