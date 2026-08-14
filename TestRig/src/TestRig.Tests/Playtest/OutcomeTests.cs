using TestRig.Playtest.Model;
using TestRig.Playtest.Runner;
using Xunit;

namespace TestRig.Tests.Playtest;

/// <summary>
///     The three-outcome model: what may produce a fail, what may not, and how a result reads.
/// </summary>
/// <remarks>
///     The whole harness rests on one asymmetry. An inconclusive costs a re-run; a false fail
///     costs a developer a day chasing a bug that is not there. So only an assert verb that
///     read a value and found the wrong one may produce a fail, and everything else is
///     inconclusive, including a bug in the check itself.
/// </remarks>
public sealed class OutcomeTests
{
    [Fact]
    public void AnInconclusiveSignalCarriesItsOwnDetectorAndMessage()
    {
        var signal = PlaytestSignal.Inconclusive("the pool is empty", "entitlement-not-in-pool");
        Assert.Equal(SignalKind.Inconclusive, signal.Kind);
        Assert.Equal("entitlement-not-in-pool", signal.Detector);
        Assert.Equal("the pool is empty", signal.Message);
    }

    [Fact]
    public void AnInconclusiveWithNoDetectorDefaultsToCheckDeclined()
    {
        Assert.Equal(Detectors.CheckDeclined, PlaytestSignal.Inconclusive("no").Detector);
        Assert.Equal(Detectors.CheckDeclined, PlaytestSignal.Inconclusive("no", string.Empty).Detector);
    }

    [Fact]
    public void TheClassifierKeepsASignalsOwnKind()
    {
        var inconclusive = SignalClassifier.Classify(PlaytestSignal.Inconclusive("x", "custom"));
        Assert.Equal(CheckOutcome.Inconclusive, inconclusive.Outcome);
        Assert.Equal("custom", inconclusive.Detector);
    }

    [Fact]
    public void AnythingUnmarkedIsInconclusiveAndSaysWhy()
    {
        var classified = SignalClassifier.Classify(new InvalidOperationException("object reference"));
        Assert.Equal(CheckOutcome.Inconclusive, classified.Outcome);
        Assert.Equal(Detectors.UnclassifiedError, classified.Detector);
        Assert.Contains("does not classify", classified.Message, StringComparison.Ordinal);
        Assert.Contains("object reference", classified.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ABugInTheCheckIsInconclusiveAndNeverAFailure()
    {
        // A null reference in a check is a rig-side problem as far as the mod is concerned.
        Assert.Equal(CheckOutcome.Inconclusive, SignalClassifier.Classify(new NullReferenceException()).Outcome);
        Assert.Equal(CheckOutcome.Inconclusive, SignalClassifier.Classify(new PlaytestUsageException("wrong instance")).Outcome);
    }

    [Fact]
    public void TheClassifierWalksTheInnerChain()
    {
        // A check that catches and rethrows inside a finally nests the signal, and a wrapped
        // signal read as unclassified would turn a real assertion failure into an
        // inconclusive.
        var wrapped = new InvalidOperationException("outer",
            new InvalidOperationException("middle", PlaytestSignal.Inconclusive("inner", "lock-lost")));

        var classified = SignalClassifier.Classify(wrapped);
        Assert.Equal("lock-lost", classified.Detector);
        Assert.Equal("inner", classified.Message);
    }

    [Fact]
    public void TheChainWalkIsBounded()
    {
        Exception current = PlaytestSignal.Inconclusive("deep", "lock-lost");
        for (var i = 0; i <= SignalClassifier.MaxInnerDepth + 2; i++) current = new InvalidOperationException("wrap", current);

        Assert.Equal(Detectors.UnclassifiedError, SignalClassifier.Classify(current).Detector);
    }

    [Fact]
    public void TheClassifierLooksInsideAnAggregate()
    {
        var aggregate = new AggregateException(new InvalidOperationException("a"), PlaytestSignal.Inconclusive("b", "boot-timeout"));
        Assert.Equal("boot-timeout", SignalClassifier.Classify(aggregate).Detector);
    }

    [Fact]
    public void FindReturnsNullWhenThereIsNoSignal()
    {
        Assert.Null(SignalClassifier.Find(null));
        Assert.Null(SignalClassifier.Find(new InvalidOperationException("nothing here")));
    }

    [Fact]
    public void AFailureCarriesTheAssertionDetector()
    {
        var classified = SignalClassifier.Classify(PlaytestSignal.Failure("hosting was False", "{}"));
        Assert.Equal(CheckOutcome.Fail, classified.Outcome);
        Assert.Equal(Detectors.Assertion, classified.Detector);
    }

    [Fact]
    public void OutcomesRenderTheWayAReportPrintsThem()
    {
        Assert.Equal("pass", CheckResult.Format(CheckOutcome.Pass, degraded: false, 1, string.Empty));
        Assert.Equal("fail", CheckResult.Format(CheckOutcome.Fail, degraded: true, 3, "assertion"));
        Assert.Equal("inconclusive (lock-lost)", CheckResult.Format(CheckOutcome.Inconclusive, degraded: false, 1, "lock-lost"));
        Assert.Equal("inconclusive", CheckResult.Format(CheckOutcome.Inconclusive, degraded: false, 1, string.Empty));
    }

    [Fact]
    public void ADegradedPassIsStillAPassAndNeverACleanOne()
    {
        Assert.Equal("pass (degraded, 3 attempts)", CheckResult.Format(CheckOutcome.Pass, degraded: true, 3, string.Empty));
    }

    [Fact]
    public void ADegradedPassNeverRendersAsFewerThanTwoAttempts()
    {
        // The floor exists so a degraded pass cannot read as a clean run that was somehow
        // still degraded.
        Assert.Equal("pass (degraded, 2 attempts)", CheckResult.Format(CheckOutcome.Pass, degraded: true, 1, string.Empty));
        Assert.Equal("pass (degraded, 2 attempts)", CheckResult.Format(CheckOutcome.Pass, degraded: true, 0, string.Empty));
    }

    [Fact]
    public void OutcomeTextIsTheThreeWordsAReportUses()
    {
        Assert.Equal("pass", CheckResult.OutcomeText(CheckOutcome.Pass));
        Assert.Equal("fail", CheckResult.OutcomeText(CheckOutcome.Fail));
        Assert.Equal("inconclusive", CheckResult.OutcomeText(CheckOutcome.Inconclusive));
    }

    [Fact]
    public void ThereIsNoWayForACheckToDeclareItselfFailed()
    {
        // Deliberate. A check that believes the mod is wrong states that as an assertion
        // against the authority, so the report can say what was compared with what.
        var factories = typeof(PlaytestSignal).GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
        Assert.DoesNotContain(factories, m => m.Name.Contains("Fail", StringComparison.OrdinalIgnoreCase));
    }
}
