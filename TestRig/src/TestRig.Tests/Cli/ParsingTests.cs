using Xunit;

namespace TestRig.Tests.Cli;

/// <summary>
/// Argument parsing, and the defects the PowerShell binder shipped.
/// </summary>
/// <remarks>
/// Every one of these ran against the real binder and produced the wrong answer in silence.
/// The worst is <c>testrig status server</c>, which bound <c>server</c> to <c>-Mod</c>, left
/// the target empty, defaulted it to <c>all</c> and reported the whole rig.
/// </remarks>
[Collection("cli")]
public sealed class ParsingTests(CliFixture rig)
{
    private const int Usage = 2;

    [Theory]
    [InlineData("status", "server")]
    [InlineData("logs", "server")]
    [InlineData("list", "server")]
    [InlineData("stop", "hostie")]
    [InlineData("start", "clients")]
    public void ASecondBareArgumentIsRejectedAndTheMessageNamesTheTargetFlag(string verb, string bare)
    {
        var result = rig.Run(verb, bare);
        Assert.Equal(Usage, result.ExitCode);
        Assert.Contains("--target", result.StdErr, StringComparison.Ordinal);
        Assert.Contains(bare, result.StdErr, StringComparison.Ordinal);
    }

    [Fact]
    public void OnlyDeployTakesAPositionalName()
    {
        var result = rig.Run("deploy", "--target", "server", "SprayPaintPlus", "--as", "nobody");

        // It gets past parsing: the failure is the lock gate, not the binder.
        Assert.NotEqual(Usage, result.ExitCode);
        Assert.DoesNotContain("bare argument", result.All, StringComparison.Ordinal);
    }

    [Fact]
    public void APositionalNameAndTheFlagTogetherAreRejected()
    {
        var result = rig.Run("deploy", "SprayPaintPlus", "--mod", "InspectorPlus");
        Assert.Equal(Usage, result.ExitCode);
        Assert.Contains("both name the mods", result.StdErr, StringComparison.Ordinal);
    }

    [Fact]
    public void AThirdBareArgumentIsRejected()
    {
        var result = rig.Run("deploy", "SprayPaintPlus", "InspectorPlus");
        Assert.Equal(Usage, result.ExitCode);
        Assert.Contains("third bare argument", result.StdErr, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("--target")]
    [InlineData("-target")]
    [InlineData("-Target")]
    [InlineData("--TARGET")]
    [InlineData("--targ")]
    [InlineData("-Targ")]
    public void OptionNamesAreCaseInsensitiveDashInsensitiveAndPrefixMatched(string spelling)
    {
        // Every recipe and every refusal string written before the port spells options
        // -Target, and -Wait 60 is in the shipped sequences.
        var result = rig.Run("snapshot", spelling, "clients", "--json");
        using var doc = result.Json();
        Assert.Equal("clients", doc.RootElement.GetProperty("values").GetProperty("target").GetString());
    }

    [Fact]
    public void AnAmbiguousPrefixIsRejectedRatherThanGuessed()
    {
        // --ta could be --target or --tail.
        var result = rig.Run("logs", "--ta", "server");
        Assert.Equal(Usage, result.ExitCode);
        Assert.Contains("ambiguous", result.StdErr, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("--target=clients")]
    [InlineData("--target:clients")]
    [InlineData("-Target:clients")]
    public void AValueMayBeAttachedWithEitherSeparator(string spelling)
    {
        var result = rig.Run("snapshot", spelling, "--json");
        using var doc = result.Json();
        Assert.Equal("clients", doc.RootElement.GetProperty("values").GetProperty("target").GetString());
    }

    [Fact]
    public void ThePowerShellSwitchSpellingStillBinds()
    {
        // -Force:$false appears in recipes written before the port. Binding the literal
        // string "$false" would read as true.
        var result = rig.Run("unlock", "--force:$false", "--json");
        using var doc = result.Json();
        Assert.Equal(0, doc.RootElement.GetProperty("exitCode").GetInt32());
    }

    [Fact]
    public void ABooleanInstanceFlagIsASwitchWithANoForm()
    {
        // -SeedMods alone was a PowerShell binder error, because the parameter was [bool].
        var withValue = rig.Run("create", "--target", "newbie", "--seed-mods", "--as", "nobody");
        Assert.NotEqual(Usage, withValue.ExitCode);

        var negated = rig.Run("create", "--target", "newbie", "--no-seed-mods", "--as", "nobody");
        Assert.NotEqual(Usage, negated.ExitCode);
    }

    [Fact]
    public void AnUnknownOptionIsRejectedWithACandidateList()
    {
        var result = rig.Run("status", "--targert", "server");
        Assert.Equal(Usage, result.ExitCode);
        Assert.Contains("not a testrig option", result.StdErr, StringComparison.Ordinal);
        Assert.Contains("--target", result.StdErr, StringComparison.Ordinal);
    }

    [Fact]
    public void AnOptionMissingItsValueIsRejected()
    {
        var result = rig.Run("logs", "--tail");
        Assert.Equal(Usage, result.ExitCode);
        Assert.Contains("expects a value", result.StdErr, StringComparison.Ordinal);
    }

    [Fact]
    public void ANumberOptionRejectsNonNumbers()
    {
        var result = rig.Run("logs", "--tail", "lots");
        Assert.Equal(Usage, result.ExitCode);
        Assert.Contains("whole number", result.StdErr, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("--configuration", "Sideways", "Release, Debug")]
    [InlineData("--stage", "nearly", "ping, modsLoaded, menu, inWorld, process")]
    [InlineData("--role", "overlord", "client, host")]
    public void AChoiceOptionListsWhatItAccepts(string option, string bad, string expected)
    {
        var verb = option == "--stage" ? "wait" : option == "--role" ? "create" : "deploy";
        var result = rig.Run(verb, "--target", "clients", option, bad);
        Assert.Equal(Usage, result.ExitCode);
        Assert.Contains(expected, result.StdErr, StringComparison.Ordinal);
    }

    [Fact]
    public void AChoiceValueIsNormalizedToItsCanonicalCasing()
    {
        var (home, owner) = rig.LockedHome("configcase", "hostie");
        CliFixture.SeedRepositoryMod(home, "Fake");

        var result = rig.RunIn(
            home, "deploy", "--target", "clients", "--mod", "Fake", "--configuration", "debug", "--as", owner);

        // 'debug' becomes 'Debug', because the build folder is named that way on disk, and the
        // half says which build it looked for.
        Assert.Contains("the Debug build of 'Fake'", result.All, StringComparison.Ordinal);
        Assert.Contains(Path.Combine("bin", "Debug", "Fake.dll"), result.All, StringComparison.Ordinal);
    }

    [Fact]
    public void DryRunIsOnlyAcceptedByReset()
    {
        // In PowerShell it bound on all twenty-two verbs and was honoured by reset alone.
        var stop = rig.Run("stop", "--target", "all", "--dry-run");
        Assert.Equal(Usage, stop.ExitCode);
        Assert.Contains("--dry-run is not read by 'stop'", stop.StdErr, StringComparison.Ordinal);

        var reset = rig.Run("reset", "--target", "all", "--dry-run", "--as", "nobody");
        Assert.NotEqual(Usage, reset.ExitCode);
    }

    [Theory]
    [InlineData("status", "clients", "--purpose", "why")]
    [InlineData("list", "clients", "--tail", "10")]
    [InlineData("snapshot", "clients", "--grep", "Error")]
    [InlineData("logs", "clients", "--save-name", "World")]
    [InlineData("send", "server", "--body", "{}")]
    public void AnOptionAVerbDoesNotReadIsAUsageErrorNotASilentNoOp(string verb, string target, params string[] rest)
    {
        var result = rig.Run([verb, "--target", target, .. rest]);
        Assert.Equal(Usage, result.ExitCode);
        Assert.Contains("is not read by", result.StdErr, StringComparison.Ordinal);
        Assert.Contains("reads:", result.StdErr, StringComparison.Ordinal);
    }

    [Fact]
    public void TheVerbIsMatchedExactlyAfterLowerCasing()
    {
        Assert.Equal(0, rig.Run("STATUS").ExitCode);
        Assert.Equal(0, rig.Run("Status").ExitCode);
    }

    [Fact]
    public void ThereAreNoAbbreviationsForVerbs()
    {
        // A near miss gets a suggestion, never a match.
        var result = rig.Run("stat");
        Assert.Equal(Usage, result.ExitCode);
        Assert.Contains("Did you mean: status, start?", result.StdErr, StringComparison.Ordinal);
    }

    [Fact]
    public void AVerbWithNoNeighbourGetsNoSuggestion()
    {
        var result = rig.Run("bogusverb");
        Assert.Equal(Usage, result.ExitCode);
        Assert.DoesNotContain("Did you mean", result.StdErr, StringComparison.Ordinal);
        Assert.Contains("Verbs: ", result.StdErr, StringComparison.Ordinal);
    }

    [Fact]
    public void ANegativeNumberIsAValueNotAnOption()
    {
        var result = rig.Run("logs", "--tail", "-5", "--target", "server");
        Assert.NotEqual(Usage, result.ExitCode);
    }
}
