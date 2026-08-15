using TestRig.Contracts;
using TestRig.Core.Rig;
using TestRig.Core.Session;
using Xunit;

namespace TestRig.Tests.Client;

/// <summary>
/// The refusal matrix and the target resolver.
/// </summary>
/// <remarks>
/// A refusal is a feature, not an error path, and its whole value is that it names a command
/// that works. The PowerShell suite only ever checked that an alternative was PRESENT, so two
/// entries pointed at an endpoint that has never existed and stayed green for the life of the
/// feature. Here every named endpoint is resolved against the real router table.
/// </remarks>
public sealed class RefusalMatrixTests
{
    // ---- the matrix as a whole --------------------------------------------

    [Fact]
    public void EveryEntryHasAnExplanationAnAlternativeAndAReference()
    {
        foreach (var entry in RefusalMatrix.Table)
        {
            Assert.False(string.IsNullOrWhiteSpace(entry.What), $"{entry.Verb}/{entry.TargetKind} has no explanation");
            Assert.False(string.IsNullOrWhiteSpace(entry.Instead), $"{entry.Verb}/{entry.TargetKind} has no alternative");
            Assert.False(string.IsNullOrWhiteSpace(entry.InsteadLabel), $"{entry.Verb}/{entry.TargetKind} has no label");
            Assert.False(string.IsNullOrWhiteSpace(entry.Reference), $"{entry.Verb}/{entry.TargetKind} has no reference");
        }
    }

    [Fact]
    public void EveryEndpointNamedInAnAlternativeIsOneThePluginActuallyAnswers()
    {
        // This is the assertion the PowerShell suite did not have. Two entries told callers to
        // drive /console/run, which the router has never served; the real path is
        // /console/exec, and a caller only ever discovered that at runtime, on a rig it had
        // taken the lock for.
        var pattern = new System.Text.RegularExpressions.Regex(@"--path\s+(?<path>/[A-Za-z0-9/_-]+)");

        var checkedAny = false;
        foreach (var entry in RefusalMatrix.Table)
        {
            foreach (System.Text.RegularExpressions.Match match in pattern.Matches(entry.Instead))
            {
                checkedAny = true;
                var path = match.Groups["path"].Value;
                Assert.True(Endpoints.Exists(path),
                    $"{entry.Verb}/{entry.TargetKind} tells the caller to use {path}, which the router does not serve");
            }
        }

        Assert.True(checkedAny, "no refusal named an endpoint at all, so this test proved nothing");
    }

    [Fact]
    public void TheTwoSendRefusalsNameConsoleExecAndNotTheEndpointThatNeverExisted()
    {
        foreach (var kind in new[] { TargetKind.Instance, TargetKind.Clients })
        {
            var entry = RefusalMatrix.Find("send", kind);
            Assert.NotNull(entry);
            Assert.Contains(Endpoints.ConsoleExec, entry!.Instead, StringComparison.Ordinal);
            Assert.DoesNotContain("/console/run", entry.Instead, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void EveryAlternativeIsSpelledWithTheDoubleDashFlagsTheBinaryActuallyTakes()
    {
        foreach (var entry in RefusalMatrix.Table)
        {
            Assert.DoesNotMatch(@"(?<![-\w])-[A-Z]\w+", entry.Instead);
        }
    }

    // ---- the matcher -------------------------------------------------------

    [Fact]
    public void AWildcardVerbEntryMatchesAnyVerb()
    {
        // How the instance-flags refusal covers the whole surface without being repeated.
        var found = RefusalMatrix.Find("deploy", TargetKind.Server, "instance-flags");
        Assert.NotNull(found);
        Assert.Equal("*", found!.Verb);
    }

    [Fact]
    public void AnAnyTargetKindEntryWouldMatchEveryKind()
    {
        // No entry uses TargetKind.Any today, and that is exactly why this exists: a port
        // implementing only the exact-match path would pass every other test here while
        // silently narrowing the matcher's contract.
        var table = new List<RefusalEntry>(RefusalMatrix.Table);
        Assert.DoesNotContain(table, e => e.TargetKind == TargetKind.Any);

        var probe = new RefusalEntry("probe", TargetKind.Any, "", "what", "instead", "Label:", "ref");
        foreach (var kind in Enum.GetValues<TargetKind>())
        {
            Assert.True(Matches(probe, "probe", kind, ""), $"an Any entry failed to match {kind}");
        }
    }

    private static bool Matches(RefusalEntry entry, string verb, TargetKind kind, string condition)
    {
        // Reproduces the matcher's three rules against a single entry, which is the only way
        // to exercise the Any branch while no table row uses it.
        if (!string.Equals(entry.Verb, verb, StringComparison.Ordinal) && entry.Verb != "*") return false;
        if (entry.TargetKind != kind && entry.TargetKind != TargetKind.Any) return false;
        return string.Equals(entry.Condition, condition, StringComparison.Ordinal);
    }

    [Fact]
    public void TheConditionMustMatchExactlyIncludingTheEmptyCase()
    {
        Assert.NotNull(RefusalMatrix.Find("snapshot", TargetKind.Server));
        Assert.Null(RefusalMatrix.Find("snapshot", TargetKind.Server, "some-condition"));
        Assert.NotNull(RefusalMatrix.Find("wait", TargetKind.Server, "client-stage"));
        Assert.Null(RefusalMatrix.Find("wait", TargetKind.Server));

        // 'call' has NO rows at all now: the merged plugin gives the dedicated server a
        // control plane, so the verb works on both halves and a refusal would teach a rig that
        // no longer exists.
        Assert.Null(RefusalMatrix.Find("call", TargetKind.Server));
        Assert.Null(RefusalMatrix.Find("call", TargetKind.All));
    }

    [Fact]
    public void NothingMatchingIsNullRatherThanAGuess()
    {
        Assert.Null(RefusalMatrix.Find("status", TargetKind.Server));
    }

    [Fact]
    public void AVerbWithNoEntryReportsAMatrixBugRatherThanBlamingTheCommand()
    {
        var ex = RefusalMatrix.Deny("status", TargetKind.Clients);
        Assert.Contains("bug in the refusal matrix", ex.Message, StringComparison.Ordinal);
        Assert.Contains("not a problem with the command", ex.Message, StringComparison.Ordinal);
    }

    // ---- rendering ---------------------------------------------------------

    [Fact]
    public void TheRenderedShapeIsCommandExplanationAlternativeReference()
    {
        var entry = RefusalMatrix.Find("send", TargetKind.Clients)!;
        var lines = RefusalMatrix.Format(entry, "send", "clients").Split('\n');

        Assert.Equal("testrig send --target clients", lines[0]);
        Assert.StartsWith("  x ", lines[1], StringComparison.Ordinal);
        Assert.Contains(lines, l => l.Contains("Use the control plane:", StringComparison.Ordinal));
        Assert.EndsWith("Why: TestRig/MANUAL.md (the endpoint catalogue)", lines[^1], StringComparison.Ordinal);
    }

    [Fact]
    public void WithNoTargetTheEchoedCommandCarriesNone()
    {
        var entry = RefusalMatrix.Find("lock", TargetKind.Narrow)!;
        var first = RefusalMatrix.Format(entry, "lock").Split('\n')[0];
        Assert.Equal("testrig lock", first);
    }

    [Fact]
    public void TheDisplayVerbIsTheOneTheCallerTypedAndNotTheWildcard()
    {
        var ex = RefusalMatrix.Deny(
            "*", TargetKind.Server, "instance-flags", "server",
            displayVerb: "start",
            substitutions: new Dictionary<string, string> { ["flags"] = "--role, --game-port" });

        Assert.Contains("testrig start --target server", ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("testrig * ", ex.Message, StringComparison.Ordinal);
        Assert.Contains("--role, --game-port", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void TheTargetPlaceholderIsSubstitutedInBothTheExplanationAndTheAlternative()
    {
        var ex = RefusalMatrix.Deny("send", TargetKind.Instance, target: "client1");
        Assert.Contains("--target client1", ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("{target}", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void EveryRenderedLineFitsTheWrapWidth()
    {
        foreach (var entry in RefusalMatrix.Table)
        {
            foreach (var chunk in RefusalMatrix.SplitText(entry.What))
            {
                Assert.True(chunk.Length <= RefusalMatrix.WrapWidth,
                    $"{entry.Verb}/{entry.TargetKind} produced a {chunk.Length} character chunk");
            }
        }
    }

    [Fact]
    public void WrappingCollapsesWhitespaceRunsSoAuthoredNewlinesRenderAsProse()
    {
        // Any refusal text carried across verbatim depends on this: the entries are authored
        // across several source lines with indentation.
        var wrapped = RefusalMatrix.SplitText("one\n   two\t\tthree\r\n\r\nfour", 74);
        Assert.Equal(["one two three four"], wrapped);
    }

    [Fact]
    public void ARefusalCarriesTheStructuredFormAsWellAsTheRenderedText()
    {
        // --json has to render a refusal without re-parsing prose.
        var ex = RefusalMatrix.Deny("snapshot", TargetKind.All, target: "all");

        Assert.NotNull(ex.Refusal);
        Assert.Equal("testrig snapshot --target all", ex.Refusal!.What);
        Assert.Contains("one row per client instance", ex.Refusal.Why, StringComparison.Ordinal);
        Assert.Contains("--target clients", ex.Refusal.Instead, StringComparison.Ordinal);
        Assert.False(string.IsNullOrWhiteSpace(ex.Refusal.Reference));
        Assert.StartsWith(RefusalMatrix.Sentinel, ex.Message, StringComparison.Ordinal);
        Assert.Equal(RigRefusalKind.Refused, ex.Kind);
    }
}
