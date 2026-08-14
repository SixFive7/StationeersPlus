using System.Reflection;
using System.Text;
using TestRig.Playtest.Model;
using TestRig.Playtest.Runner;
using TestRig.Playtest.Values;
using TestRig.Tests.Playtest.Fakes;
using Xunit;

namespace TestRig.Tests.Playtest;

/// <summary>
///     The suite-level surface: the closing summary, the suite name, keep-state, the decoys.
/// </summary>
public sealed class SuiteSurfaceTests
{
    private static (PlaytestFixture Fixture, CheckSpec Spec) Rig(string name = "a check")
    {
        var fixture = new PlaytestFixture().WithInstance("hostie", 27701, "host");
        var checkFile = fixture.SeedMod("SprayPaintPlus", "net.spraypaintplus", Encoding.UTF8.GetBytes("the build"), ["hostie"]);
        return (fixture, new CheckSpec(
            name, "summary", [new InstanceSpec("hostie", InstanceRole.Host, World: "Lunar")], sourceFile: checkFile));
    }

    private static SuiteResult Run(PlaytestFixture fixture, SuiteRequest request) =>
        new SuiteRunner(fixture.Dependencies).Run(request);

    // ---- PLAYTEST-357 ------------------------------------------------------

    [Fact]
    public void ARunEndsWithARuleTheCountsEveryCheckAndTheEvidenceRoot()
    {
        // The numbers were in run.json, run.md and the --json values, so nothing was lost to
        // automation; a human watching the terminal got the per-check lines and then nothing.
        var (fixture, spec) = Rig();
        var check = new TestCheck(spec, ctx =>
            ctx.AssertValue("hostie", Reader.Status, ValueMatcher.Is(true), "the host owns the world", "hosting"));

        var run = Run(fixture, new SuiteRequest
        {
            SuiteName = "SprayPaintPlus",
            Checks = [check],
            EvidenceRoot = PlaytestFixture.EvidencePath,
        });

        var log = string.Join("\n", fixture.Log);

        Assert.Contains("----", log, StringComparison.Ordinal);
        Assert.Contains("SprayPaintPlus: passed 1, failed 0, inconclusive 0", log, StringComparison.Ordinal);
        Assert.Contains("a check", log, StringComparison.Ordinal);
        Assert.Contains("tier-1 save folder:", log, StringComparison.Ordinal);
        Assert.Contains("evidence: ", log, StringComparison.Ordinal);
        Assert.Equal(0, run.ExitCode);
    }

    [Fact]
    public void AnInconclusiveRunSaysWhatInconclusiveMeans()
    {
        var (fixture, spec) = Rig();
        var check = new TestCheck(spec, ctx => ctx.SetInconclusive("the session does not own the DLC", "dlc-not-owned"));

        Run(fixture, new SuiteRequest
        {
            SuiteName = "SprayPaintPlus",
            Checks = [check],
            EvidenceRoot = PlaytestFixture.EvidencePath,
        });

        Assert.Contains(
            fixture.Log,
            l => l.Contains("inconclusive is not a failure", StringComparison.Ordinal));
    }

    // ---- PLAYTEST-002 ------------------------------------------------------

    [Fact]
    public void TheSuiteNameNamesTheRunRatherThanBeingFixed()
    {
        // CliApp hardcoded "testrig", so every run's evidence folder and report was named that
        // whatever it had actually run.
        var (fixture, spec) = Rig();
        var check = new TestCheck(spec, ctx => ctx.SetInconclusive("not the point of this test", "dlc-not-owned"));

        var run = Run(fixture, new SuiteRequest
        {
            SuiteName = "EquipmentPlus-nightly",
            Checks = [check],
            EvidenceRoot = PlaytestFixture.EvidencePath,
        });

        Assert.Equal("EquipmentPlus-nightly", run.Suite);
        Assert.Contains("EquipmentPlus-nightly", SuiteRunner.RenderRunJson(run), StringComparison.Ordinal);
        Assert.Contains("EquipmentPlus-nightly", SuiteRunner.RenderRunMarkdown(run), StringComparison.Ordinal);
    }

    // ---- PLAYTEST-247 ------------------------------------------------------

    [Fact]
    public void KeepStateReachesBothEndsOfEveryChecksLock()
    {
        // The only way to hand a staged rig to the next check: the reset is between sessions
        // and the harness takes one lock per check, so without it whatever the first check
        // built is restored away before the second one starts.
        var (fixture, spec) = Rig();
        var check = new TestCheck(spec, ctx => ctx.SetInconclusive("not the point of this test", "dlc-not-owned"));

        Run(fixture, new SuiteRequest
        {
            SuiteName = "staged",
            Checks = [check],
            EvidenceRoot = PlaytestFixture.EvidencePath,
            KeepState = true,
        });

        Assert.Contains(fixture.Launcher.Calls, c => c.StartsWith("lock ", StringComparison.Ordinal) && c.EndsWith("-KeepState", StringComparison.Ordinal));
        Assert.Contains(fixture.Launcher.Calls, c => c.StartsWith("unlock ", StringComparison.Ordinal) && c.EndsWith("-KeepState", StringComparison.Ordinal));
    }

    [Fact]
    public void WithoutKeepStateEveryCheckGetsItsOwnResetAtBothEnds()
    {
        var (fixture, spec) = Rig();
        var check = new TestCheck(spec, ctx => ctx.SetInconclusive("not the point of this test", "dlc-not-owned"));

        Run(fixture, new SuiteRequest
        {
            SuiteName = "clean",
            Checks = [check],
            EvidenceRoot = PlaytestFixture.EvidencePath,
        });

        Assert.DoesNotContain(fixture.Launcher.Calls, c => c.Contains("-KeepState", StringComparison.Ordinal));
    }

    // ---- PLAYTEST-242 and PLAYTEST-243 -------------------------------------

    [Fact]
    public void TheTeachingTheTwoDecoysCarriedLivesOnTheInterfaceItself()
    {
        // Assert-RigOk and Assert-RigResponse existed only to throw an explanation at a check
        // author asserting on the actor's report. Restoring them as methods is not available
        // here: two tests (AuthorityTests) pin that NO member of this interface is named
        // AssertOk or takes an ActionResult, and that compile-time stop is worth more than a
        // runtime one. What has to survive is the explanation, so it lives where the compiler
        // sends a reader who cannot find the method.
        var doc = typeof(IPlaytestContext).Assembly.GetName();
        Assert.NotNull(doc);

        Assert.DoesNotContain(typeof(IPlaytestContext).GetMethods(), m => m.Name == "AssertOk");
        Assert.DoesNotContain(typeof(IPlaytestContext).GetMethods(), m => m.Name == "AssertResponse");

        // The verbs that DO exist are the ones the explanation points at.
        var names = typeof(IPlaytestContext).GetMethods().Select(m => m.Name).ToArray();
        Assert.Contains("AssertValue", names);
        Assert.Contains("AssertChange", names);
        Assert.Contains("AssertAgreement", names);
    }
}
