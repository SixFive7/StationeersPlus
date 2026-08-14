using Xunit;

namespace TestRig.Tests.Cli;

/// <summary>Target resolution: defaulting, comma lists, unknown names, casing.</summary>
[Collection("cli")]
public sealed class TargetTests(CliFixture rig)
{
    private const int Usage = 2;

    private string HomeWithInstances(string label)
    {
        var home = rig.NewHome(label);
        CliFixture.Provision(home, "hostie", "joiner");
        return home;
    }

    [Theory]
    [InlineData("status")]
    [InlineData("list")]
    [InlineData("logs")]
    public void ARigWideVerbDefaultsToTheWholeRig(string verb)
    {
        var result = rig.RunIn(HomeWithInstances("default"), verb, "--json");
        using var doc = result.Json();
        var values = doc.RootElement.GetProperty("values");
        Assert.Equal("all", values.GetProperty("target").GetString());
        Assert.Equal("all", values.GetProperty("targetKind").GetString());
    }

    [Theory]
    [InlineData("snapshot")]
    [InlineData("create")]
    [InlineData("remove")]
    [InlineData("start")]
    [InlineData("stop")]
    [InlineData("save")]
    [InlineData("wait")]
    [InlineData("call")]
    [InlineData("send")]
    public void AVerbThatActsOnAThingWillNotGuess(string verb)
    {
        var result = rig.RunIn(HomeWithInstances("noguess"), verb);
        Assert.Equal(Usage, result.ExitCode);
        Assert.Contains("needs an explicit --target", result.StdErr, StringComparison.Ordinal);
        Assert.Contains("testrig list", result.StdErr, StringComparison.Ordinal);
    }

    [Fact]
    public void TheWholeRigResolvesToTheServerAndEveryInstance()
    {
        var result = rig.RunIn(HomeWithInstances("allshape"), "status", "--target", "all", "--json");
        using var doc = result.Json();
        var instances = doc.RootElement.GetProperty("values").GetProperty("instances")
            .EnumerateArray().Select(i => i.GetString()!).ToArray();
        Assert.Equal(["hostie", "joiner"], instances);
    }

    [Fact]
    public void TheServerAloneResolvesToNoInstances()
    {
        var result = rig.RunIn(HomeWithInstances("servershape"), "status", "--target", "server", "--json");
        using var doc = result.Json();
        Assert.Empty(doc.RootElement.GetProperty("values").GetProperty("instances").EnumerateArray());
    }

    [Fact]
    public void AnEmptyClientHalfResolvesRatherThanThrowing()
    {
        var result = rig.RunIn(rig.NewHome("emptyrig"), "status", "--target", "clients", "--json");
        using var doc = result.Json();
        Assert.Equal(0, doc.RootElement.GetProperty("exitCode").GetInt32());
        Assert.Empty(doc.RootElement.GetProperty("values").GetProperty("instances").EnumerateArray());
    }

    [Fact]
    public void ASingleNameStaysAOneElementList()
    {
        // A scalar would be enumerated character by character by everything downstream.
        var result = rig.RunIn(HomeWithInstances("single"), "status", "--target", "hostie", "--json");
        using var doc = result.Json();
        var instances = doc.RootElement.GetProperty("values").GetProperty("instances")
            .EnumerateArray().Select(i => i.GetString()!).ToArray();
        Assert.Equal(["hostie"], instances);
    }

    [Fact]
    public void ACommaListResolvesToEveryName()
    {
        var result = rig.RunIn(HomeWithInstances("comma"), "status", "--target", "hostie,joiner", "--json");
        using var doc = result.Json();
        var instances = doc.RootElement.GetProperty("values").GetProperty("instances")
            .EnumerateArray().Select(i => i.GetString()!).ToArray();
        Assert.Equal(["hostie", "joiner"], instances);
    }

    [Fact]
    public void WhitespaceAroundACommaListIsTrimmed()
    {
        var result = rig.RunIn(HomeWithInstances("commaspace"), "status", "--target", "hostie, joiner", "--json");
        using var doc = result.Json();
        Assert.Equal(2, doc.RootElement.GetProperty("values").GetProperty("instances").GetArrayLength());
    }

    [Fact]
    public void OneBadNameFailsTheWholeCommand()
    {
        var result = rig.RunIn(HomeWithInstances("badname"), "status", "--target", "hostie,ghost");
        Assert.Equal(Usage, result.ExitCode);
        Assert.Contains("'ghost' is not a provisioned instance", result.StdErr, StringComparison.Ordinal);
        Assert.Contains("Provisioned: hostie, joiner", result.StdErr, StringComparison.Ordinal);
        Assert.Contains("testrig create --target ghost", result.StdErr, StringComparison.Ordinal);
    }

    [Fact]
    public void AnUnknownNameOnAnEmptyRigSaysSo()
    {
        var result = rig.RunIn(rig.NewHome("nothing"), "status", "--target", "ghost");
        Assert.Equal(Usage, result.ExitCode);
        Assert.Contains("(none provisioned)", result.StdErr, StringComparison.Ordinal);
    }

    [Fact]
    public void InstanceNamesAreMatchedCaseInsensitively()
    {
        // PowerShell's -contains and -eq are case-insensitive for strings, so this has always
        // worked. An ordinal port would change it in silence.
        var result = rig.RunIn(HomeWithInstances("casing"), "status", "--target", "HOSTIE", "--json");
        using var doc = result.Json();
        Assert.Equal(0, doc.RootElement.GetProperty("exitCode").GetInt32());
    }

    [Fact]
    public void TheTargetKeywordsAreCaseInsensitiveAndKeepTheirTypedCasing()
    {
        var result = rig.RunIn(HomeWithInstances("keyword"), "status", "--target", "ALL", "--json");
        using var doc = result.Json();
        var values = doc.RootElement.GetProperty("values");
        Assert.Equal("ALL", values.GetProperty("target").GetString());
        Assert.Equal("all", values.GetProperty("targetKind").GetString());
    }

    [Fact]
    public void OnlyCreateMayNameAnInstanceThatDoesNotExistYet()
    {
        var home = HomeWithInstances("unknownok");

        var create = rig.RunIn(home, "create", "--target", "brandnew", "--as", "nobody");
        Assert.NotEqual(Usage, create.ExitCode);

        var start = rig.RunIn(home, "start", "--target", "brandnew", "--as", "nobody");
        Assert.Equal(Usage, start.ExitCode);
        Assert.Contains("not a provisioned instance", start.StdErr, StringComparison.Ordinal);
    }

    [Fact]
    public void ATargetOfNothingButCommasIsRejected()
    {
        var result = rig.RunIn(HomeWithInstances("empty"), "status", "--target", ",,");
        Assert.Equal(Usage, result.ExitCode);
        Assert.Contains("names nothing", result.StdErr, StringComparison.Ordinal);
    }

    [Fact]
    public void CreateAndRemoveTakeExactlyOneName()
    {
        var home = HomeWithInstances("onlyone");
        var (locked, owner) = rig.LockedHome("onlyone-locked", "hostie", "joiner");

        var create = rig.RunIn(locked, "create", "--target", "one,two", "--as", owner);
        Assert.Equal(Usage, create.ExitCode);
        Assert.Contains("builds one instance at a time", create.StdErr, StringComparison.Ordinal);

        var remove = rig.RunIn(locked, "remove", "--target", "hostie,joiner", "--as", owner);
        Assert.Equal(Usage, remove.ExitCode);
        Assert.Contains("deletes one instance at a time", remove.StdErr, StringComparison.Ordinal);

        Assert.NotNull(home);
    }
}
