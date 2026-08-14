using Xunit;

namespace TestRig.Tests.Cli;

/// <summary>
/// The playtest verb's own options, and the status line that says nothing is stale.
/// </summary>
/// <remarks>
/// Every one of these renderers already existed and was tested inside the library; what was
/// missing was any way to type the option that reaches it, which is precisely the kind of gap
/// a suite that never runs the binary cannot see.
/// </remarks>
[Collection("cli")]
public sealed class PlaytestSurfaceTests(CliFixture rig)
{
    [Fact]
    public void ListChecksRendersTheCompiledInChecksAndRunsNothing()
    {
        // PLAYTEST-006. PlaytestListing.Checks existed, rendered exactly this, and was tested;
        // no CLI option called it, so --list-checks could not be typed.
        var home = rig.NewHome("listchecks");
        var result = rig.RunIn(home, "playtest", "--list-checks");

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("Registered checks:", result.All, StringComparison.Ordinal);

        // No lock was taken and no evidence bundle was written, because nothing ran.
        Assert.False(File.Exists(Path.Combine(home, "session.lock")));
        Assert.False(Directory.Exists(Path.Combine(home, "playtest", "evidence")));
    }

    [Fact]
    public void ListFlakesAnswersWithNoRigAtAll()
    {
        // PLAYTEST-007, and PLAYTEST-011's property: the taxonomy is a fact about the code, so
        // it answers without a rig, without a lock and without a game.
        var home = rig.NewHome("listflakes");
        var result = rig.RunIn(home, "playtest", "--list-flakes");

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("Flake taxonomy, in resolution order", result.All, StringComparison.Ordinal);
        Assert.Contains("restart-instance", result.All, StringComparison.Ordinal);
        Assert.Contains("never as a failure", result.All, StringComparison.Ordinal);
        Assert.False(File.Exists(Path.Combine(home, "session.lock")));
    }

    [Fact]
    public void TheOnlyFilterIsAcceptedAlongsideTheListingRatherThanRejectedAsUnread()
    {
        // The listing marks a check the filter excludes with '-', so --only has to reach it.
        var result = rig.RunIn(rig.NewHome("listonly"), "playtest", "--list-checks", "--only", "no-such-check-*");

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("Registered checks:", result.All, StringComparison.Ordinal);
        Assert.DoesNotContain("is not read by", result.All, StringComparison.Ordinal);
    }

    [Fact]
    public void TheSuiteNameAndKeepStateAreTypeableOnPlaytest()
    {
        // PLAYTEST-002 and PLAYTEST-247. Both are honoured everywhere downstream; neither had
        // an option, so every run's report was named 'testrig' and a staged rig could not be
        // handed to the next check.
        var options = rig.Surface.RootElement
            .GetProperty("verbs")
            .EnumerateArray()
            .First(v => v.GetProperty("name").GetString() == "playtest")
            .GetProperty("options")
            .EnumerateArray()
            .Select(o => o.GetString())
            .ToArray();

        Assert.Contains("suite-name", options);
        Assert.Contains("keep-state", options);
        Assert.Contains("list-checks", options);
        Assert.Contains("list-flakes", options);

        // And the option-applicability check accepts them rather than calling them unread.
        var typed = rig.RunIn(rig.NewHome("suitename"), "playtest", "--list-checks", "--suite-name", "nightly");
        Assert.Equal(0, typed.ExitCode);
        Assert.DoesNotContain("is not read by 'playtest'", typed.All, StringComparison.Ordinal);
    }

    [Fact]
    public void StatusSaysSoWhenThereIsNothingStaleToReport()
    {
        // CLI-086. With nothing stale the section printed NOTHING, which reads exactly like a
        // section that failed to run. "The rig is current" is the thing a caller most wants
        // confirmed before a test.
        var result = rig.RunIn(rig.NewHome("nothingstale"), "status");

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("(nothing to report)", result.All, StringComparison.Ordinal);
    }

    [Fact]
    public void StatusNamesTheSharedPerUserSourcesItWouldReportDriftFrom()
    {
        // RESET-004 and RESET-009. Neither is ever written by the rig and neither can be
        // isolated from the developer's own client, so naming them is the only way an operator
        // can tell which folder and which key a drift line came from.
        var result = rig.RunIn(rig.NewHome("sharedstate"), "status", "--json");

        using var doc = result.Json();
        var values = doc.RootElement.GetProperty("values");

        Assert.Contains("fake-sharedstate", values.GetProperty("sharedDataDir").GetString()!, StringComparison.Ordinal);
        Assert.StartsWith("HKCU:", values.GetProperty("playerPrefsKey").GetString()!, StringComparison.Ordinal);
        Assert.Equal(0, values.GetProperty("staleRows").GetInt32());
    }
}
