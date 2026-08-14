using System.Text.Json;
using Xunit;

namespace TestRig.Tests.Cli;

/// <summary>
/// The verb table, the option catalogue and the printed surface.
/// </summary>
/// <remarks>
/// Counts are pinned exactly. The PowerShell suite asserted "at least 18" refusals against a
/// real 21 and "at least 20" verbs against a real 22, so a port could have shipped three
/// missing rows and two missing verbs and stayed green.
/// </remarks>
[Collection("cli")]
public sealed class SurfaceTests(CliFixture rig)
{
    private static readonly string[] ExpectedVerbs =
    [
        "help", "lock", "unlock", "refresh-lock", "capture-baseline", "reset",
        "status", "list", "logs", "snapshot",
        "update-game", "update-mods", "deploy", "create", "remove",
        "start", "stop", "save", "wait", "call", "send", "playtest", "host-mode",
    ];

    private static readonly string[] DefaultToAll =
    [
        "lock", "unlock", "refresh-lock", "capture-baseline", "reset",
        "status", "list", "logs", "update-game", "update-mods", "deploy", "playtest",
    ];

    private static readonly string[] RequireATarget =
    [
        "snapshot", "create", "remove", "start", "stop", "save", "wait", "call", "send",
    ];

    /// <summary>
    /// Twenty-three: the twenty-two the PowerShell rig had, plus <c>playtest</c>.
    /// </summary>
    /// <remarks>
    /// The harness used to be a separate launcher that shelled this one. It is a verb now
    /// because the checks are compiled into this binary, so there is nothing left to shell.
    /// </remarks>
    [Fact]
    public void TheVerbSetIsExactlyTwentyThree()
    {
        var verbs = Verbs().Select(v => v.GetProperty("name").GetString()!).ToArray();
        Assert.Equal(23, verbs.Length);
        Assert.Equal([.. ExpectedVerbs.OrderBy(v => v, StringComparer.Ordinal)],
            [.. verbs.OrderBy(v => v, StringComparer.Ordinal)]);
    }

    [Fact]
    public void TwelveVerbsDefaultToTheWholeRig()
    {
        var actual = Verbs()
            .Where(v => v.GetProperty("defaultTarget").GetString() == "all")
            .Select(v => v.GetProperty("name").GetString()!)
            .ToArray();

        Assert.Equal(12, actual.Length);
        Assert.Equal([.. DefaultToAll.OrderBy(v => v, StringComparer.Ordinal)],
            [.. actual.OrderBy(v => v, StringComparer.Ordinal)]);
    }

    [Fact]
    public void NineVerbsRefuseToGuessATarget()
    {
        var actual = Verbs()
            .Where(v => v.GetProperty("defaultTarget").GetString() == string.Empty)
            .Select(v => v.GetProperty("name").GetString()!)
            .ToArray();

        Assert.Equal(9, actual.Length);
        Assert.Equal([.. RequireATarget.OrderBy(v => v, StringComparer.Ordinal)],
            [.. actual.OrderBy(v => v, StringComparer.Ordinal)]);
    }

    [Theory]
    [InlineData("status")]
    [InlineData("list")]
    [InlineData("logs")]
    [InlineData("snapshot")]
    [InlineData("wait")]
    public void EveryReadOnlyVerbIsMarkedAsSuch(string verb)
    {
        var row = Verbs().Single(v => v.GetProperty("name").GetString() == verb);
        Assert.True(row.GetProperty("readOnly").GetBoolean(), $"'{verb}' should be read-only");
    }

    [Fact]
    public void NoVerbAndHelpPrintTheSameSurface()
    {
        var home = rig.NewHome("sameSurface");
        var bare = rig.RunIn(home);
        var help = rig.RunIn(home, "help");

        Assert.Equal(0, bare.ExitCode);
        Assert.Equal(0, help.ExitCode);
        Assert.Equal(bare.StdOut, help.StdOut);
        Assert.Equal(string.Empty, bare.StdErr);
    }

    [Fact]
    public void HelpIsCaseInsensitive()
    {
        Assert.Equal(0, rig.Run("HELP").ExitCode);
        Assert.Equal(0, rig.Run("Help").ExitCode);
    }

    [Fact]
    public void ThePrintedSurfaceNamesEveryPublicVerb()
    {
        var text = rig.Run().StdOut;
        foreach (var verb in ExpectedVerbs)
        {
            if (verb == "host-mode") continue;
            Assert.Contains(verb, text, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void ThePrintedSurfaceDoesNotAdvertiseTheInternalVerb()
    {
        // host-mode is the detached wrapper the server's start spawns. Offering it to a
        // caller is offering a way to bring the server up outside the lock.
        Assert.DoesNotContain("host-mode", rig.Run().StdOut, StringComparison.Ordinal);
        Assert.DoesNotContain("host-mode", rig.Run("bogus").StdErr, StringComparison.Ordinal);
    }

    [Fact]
    public void ThePrintedSurfaceNamesEveryOption()
    {
        var text = rig.Run().StdOut;
        foreach (var option in rig.Surface.RootElement.GetProperty("options").EnumerateArray())
        {
            var name = option.GetProperty("name").GetString()!;
            Assert.Contains("--" + name, text, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void ThePrintedSurfaceNamesTheInstancesRootAndWhereItCameFrom()
    {
        var home = rig.NewHome("surface");
        var text = rig.RunIn(home, "--target", "all").StdOut;
        Assert.Contains("instances root:", text, StringComparison.Ordinal);
        Assert.Contains("the default ClientRig/instances folder", text, StringComparison.Ordinal);
        Assert.Contains(Path.Combine(home, "ClientRig", "instances"), text, StringComparison.Ordinal);
    }

    [Fact]
    public void TheInstancesRootOverrideIsReportedAsTyped()
    {
        var text = rig.Run("--instances-root", @"D:\elsewhere").StdOut;
        Assert.Contains(@"D:\elsewhere", text, StringComparison.Ordinal);
        Assert.Contains("typed on this command", text, StringComparison.Ordinal);
    }

    [Fact]
    public void TheJsonSurfaceIsOneDocumentWithNoEnvelopeAroundIt()
    {
        var result = rig.Run("--json");
        using var doc = result.Json();
        Assert.True(doc.RootElement.TryGetProperty("verbs", out _));
        Assert.False(doc.RootElement.TryGetProperty("exitCode", out _),
            "the surface is its own answer and must not be wrapped in the per-command envelope");
    }

    [Fact]
    public void TheJsonSurfacePublishesTheWidenedExitCodes()
    {
        var codes = rig.Surface.RootElement.GetProperty("exitCodes");
        Assert.Equal(0, codes.GetProperty("ok").GetInt32());
        Assert.Equal(1, codes.GetProperty("failed").GetInt32());
        Assert.Equal(2, codes.GetProperty("usageError").GetInt32());
        Assert.Equal(3, codes.GetProperty("refused").GetInt32());
        Assert.Equal(4, codes.GetProperty("lockHeldByOther").GetInt32());
        Assert.Equal(5, codes.GetProperty("lockNotHeld").GetInt32());
        Assert.Equal(6, codes.GetProperty("rigBusy").GetInt32());
        Assert.Equal(7, codes.GetProperty("staleBinary").GetInt32());

        // Its own code, and not 'failed': a fail accuses the mod, an inconclusive says the rig
        // never got far enough to have an opinion, and a caller that cannot tell them apart
        // will eventually treat one as the other.
        Assert.Equal(8, codes.GetProperty("playtestInconclusive").GetInt32());
    }

    [Fact]
    public void EveryVerbDeclaresTheOptionsItReads()
    {
        var declared = rig.Surface.RootElement.GetProperty("options")
            .EnumerateArray()
            .Select(o => o.GetProperty("name").GetString()!)
            .ToHashSet(StringComparer.Ordinal);

        foreach (var verb in Verbs())
        {
            foreach (var option in verb.GetProperty("options").EnumerateArray())
            {
                var name = option.GetString()!;
                Assert.True(
                    declared.Contains(name),
                    $"'{verb.GetProperty("name").GetString()}' claims to read --{name}, which is not an option");
            }
        }
    }

    [Fact]
    public void TheTwoBooleanInstanceFlagsAreSwitchesWithANegation()
    {
        // Both were [bool] in PowerShell, so they could only be written with an explicit
        // value: -SeedMods alone was a binder error.
        foreach (var name in new[] { "force-gameplay-input", "seed-mods" })
        {
            var spec = rig.Surface.RootElement.GetProperty("options")
                .EnumerateArray()
                .Single(o => o.GetProperty("name").GetString() == name);

            Assert.Equal("flag", spec.GetProperty("kind").GetString());
            Assert.True(spec.GetProperty("defaultsTrue").GetBoolean());
            Assert.Contains("--no-" + name, rig.Run().StdOut, StringComparison.Ordinal);
        }
    }

    private IEnumerable<JsonElement> Verbs() => rig.Surface.RootElement.GetProperty("verbs").EnumerateArray();
}
