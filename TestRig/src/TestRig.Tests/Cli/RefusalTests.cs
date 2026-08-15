using System.Text.Json;
using System.Text.RegularExpressions;
using TestRig.Contracts;
using Xunit;

namespace TestRig.Tests.Cli;

/// <summary>
/// The refusal matrix, driven from real command lines.
/// </summary>
/// <remarks>
/// <para>
/// Every row is fired by running the binary, not by calling a table lookup, so a refusal that
/// exists in the table but is unreachable from the dispatcher fails here.
/// </para>
/// <para>
/// The last two tests are the ones the PowerShell suite never had. It asserted only that a
/// refusal HAD an alternative, matching the bare substring <c>"testrig "</c>, so two rows
/// pointed callers at <c>/console/run</c> for the whole life of the matrix. That endpoint has
/// never existed.
/// </para>
/// </remarks>
[Collection("cli")]
public sealed partial class RefusalTests(CliFixture rig)
{
    private const int Refused = 3;

    /// <summary>One provisioned instance, so an instance-kind target resolves at all.</summary>
    private string HomeWithInstance(string label)
    {
        var home = rig.NewHome(label);
        CliFixture.Provision(home, "hostie", "joiner");
        return home;
    }

    public static TheoryData<string, string[]> EveryRefusal() => new()
    {
        { "start/server/no-world", ["start", "--target", "server"] },
        { "start/all/no-world", ["start", "--target", "all"] },
        { "send/instance", ["send", "--target", "hostie", "--command", "help"] },
        { "send/clients", ["send", "--target", "clients", "--command", "help"] },
        { "send/all", ["send", "--target", "all", "--command", "help"] },
        { "create/server", ["create", "--target", "server"] },
        { "create/all", ["create", "--target", "all"] },
        { "create/clients", ["create", "--target", "clients"] },
        { "remove/server", ["remove", "--target", "server"] },
        { "remove/all", ["remove", "--target", "all"] },
        { "remove/clients", ["remove", "--target", "clients"] },
        { "snapshot/server", ["snapshot", "--target", "server"] },
        { "snapshot/all", ["snapshot", "--target", "all"] },
        { "wait/server/menu", ["wait", "--target", "server", "--stage", "menu"] },
        { "wait/all/menu", ["wait", "--target", "all", "--stage", "menu"] },
        { "save/server/no-name", ["save", "--target", "server"] },
        { "save/all/no-name", ["save", "--target", "all"] },
        { "lock/narrow", ["lock", "--target", "server", "--purpose", "x"] },
        { "unlock/narrow", ["unlock", "--target", "clients"] },
        { "refresh-lock/narrow", ["refresh-lock", "--target", "hostie", "--as", "abc"] },
        { "capture-baseline/narrow", ["capture-baseline", "--target", "server", "--as", "abc"] },
        { "reset/narrow", ["reset", "--target", "server", "--as", "abc"] },
        { "instance-flags/update-game", ["update-game", "--target", "server", "--desktop", "Other"] },
        { "instance-flags/start", ["start", "--target", "server", "--new", "Mars", "--width", "1024"] },
        { "playtest/narrow", ["playtest", "--target", "server"] },
    };

    [Theory]
    [MemberData(nameof(EveryRefusal))]
    public void ARefusalFiresAndExitsThree(string label, string[] args)
    {
        var result = rig.RunIn(HomeWithInstance("refuse"), args);
        Assert.True(result.ExitCode == Refused, $"{label}: expected exit 3, got {result.ExitCode}\n{result.All}");

        // The teaching shape: the command echoed back, the explanation, an alternative,
        // and where the durable answer lives.
        Assert.Contains("  x ", result.StdOut, StringComparison.Ordinal);
        Assert.Contains("testrig ", result.StdOut, StringComparison.Ordinal);
        Assert.Contains("    Why: ", result.StdOut, StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(EveryRefusal))]
    public void ARefusalCarriesAllFiveFieldsInJson(string label, string[] args)
    {
        var result = rig.RunIn(HomeWithInstance("refusejson"), [.. args, "--json"]);
        using var doc = result.Json();
        var refusal = doc.RootElement.GetProperty("refusal");

        Assert.True(refusal.ValueKind == JsonValueKind.Object, $"{label}: no refusal object");
        foreach (var field in new[] { "what", "why", "insteadLabel", "instead", "reference" })
        {
            var value = refusal.GetProperty(field).GetString();
            Assert.False(string.IsNullOrWhiteSpace(value), $"{label}: refusal.{field} is empty");
        }

        Assert.Equal(Refused, doc.RootElement.GetProperty("exitCode").GetInt32());
        Assert.False(doc.RootElement.GetProperty("ok").GetBoolean());
    }

    [Theory]
    // The complement of each refusal: the same verb where it does apply.
    [InlineData("start", "--target", "server", "--new", "Mars")]
    [InlineData("call", "--target", "clients", "--path", "/status")]
    [InlineData("send", "--target", "server", "--command", "help")]
    [InlineData("create", "--target", "brandnew")]
    [InlineData("remove", "--target", "hostie")]
    [InlineData("snapshot", "--target", "clients")]
    [InlineData("wait", "--target", "server", "--stage", "inWorld", "--wait-seconds", "1")]
    [InlineData("wait", "--target", "clients", "--stage", "menu", "--wait-seconds", "1")]
    [InlineData("save", "--target", "server", "--save-name", "World")]
    [InlineData("lock", "--target", "all", "--purpose", "x")]
    [InlineData("reset", "--target", "all", "--as", "abc")]
    public void TheComplementDoesNotRefuse(params string[] args)
    {
        var result = rig.RunIn(HomeWithInstance("allow"), [.. args, "--json"]);
        using var doc = result.Json();

        // The matrix firing is what "refused" means here, and the structured refusal object is
        // what says it fired: every row carries one and nothing else in the rig emits one.
        //
        // The exit code cannot be the discriminator any more. A half refuses with exit 3 too,
        // for its own reasons ("the dedicated server is not running, so there is no world to
        // wait for"), and those are correct answers to a command the matrix allowed through.
        // Asserting on the code would make this test demand that the complement SUCCEED, which
        // for half of these needs a running game.
        Assert.True(
            doc.RootElement.GetProperty("refusal").ValueKind == JsonValueKind.Null,
            $"testrig {string.Join(' ', args)} should not hit the refusal matrix\n{result.All}");
    }

    [Fact]
    public void TheMatrixHasExactlyTwentyRows()
    {
        // Pinned exactly. The PowerShell assertion was "at least 18" against a real 21; the
        // twenty-second was 'playtest' over half the rig, which arrived with the verb. Two
        // have since gone: both 'call' rows rested on "the dedicated server has no HTTP
        // control plane", which stopped being true when one plugin started loading into both
        // halves. A refusal whose reason is false teaches something false at the exact moment
        // a caller is forming a model of the rig, which is worse than no refusal.
        Assert.Equal(20, rig.Surface.RootElement.GetProperty("refusals").GetArrayLength());
    }

    [Fact]
    public void CallHasNoRowsAtAllBecauseItWorksOnBothHalves()
    {
        var verbs = Rows().Select(r => r.GetProperty("verb").GetString()).ToList();
        Assert.DoesNotContain("call", verbs);
    }

    [Fact]
    public void EveryRowHasAllFiveFields()
    {
        foreach (var row in Rows())
        {
            var verb = row.GetProperty("verb").GetString();
            foreach (var field in new[] { "what", "why", "insteadLabel", "instead", "reference" })
                Assert.False(
                    string.IsNullOrWhiteSpace(row.GetProperty(field).GetString()),
                    $"{verb}/{row.GetProperty("targetKind").GetString()}: {field} is empty");
        }
    }

    [Fact]
    public void EveryAlternativeNamesRealVerbs()
    {
        // Not "contains the word testrig", which is what the PowerShell assertion checked and
        // is satisfied by any string at all that happens to mention it.
        var verbs = rig.Surface.RootElement.GetProperty("verbs")
            .EnumerateArray()
            .Select(v => v.GetProperty("name").GetString()!)
            .ToHashSet(StringComparer.Ordinal);

        foreach (var row in Rows())
        {
            var instead = row.GetProperty("instead").GetString()!;
            var named = VerbCall().Matches(instead).Select(m => m.Groups[1].Value).ToArray();
            Assert.True(named.Length > 0, $"the alternative '{instead}' names no testrig command");

            foreach (var verb in named)
                Assert.True(verbs.Contains(verb), $"the alternative '{instead}' names '{verb}', which is not a verb");
        }
    }

    [Fact]
    public void EveryAlternativeNamesRealControlPlaneEndpoints()
    {
        // Two PowerShell rows pointed at /console/run, which the plugin's router has never
        // answered. A caller only found out at runtime, on a rig it had taken the lock for.
        var checkedAny = false;
        foreach (var row in Rows())
        {
            var instead = row.GetProperty("instead").GetString()!;
            foreach (Match match in EndpointArgument().Matches(instead))
            {
                var path = match.Groups[1].Value;
                checkedAny = true;
                Assert.True(
                    Endpoints.Exists(path),
                    $"the alternative '{instead}' names '{path}', which the router does not answer. "
                    + $"Closest: {string.Join(", ", Endpoints.Suggest(path))}");
            }
        }

        Assert.True(checkedAny, "no refusal named an endpoint; the assertion would pass vacuously");
    }

    [Fact]
    public void NoAlternativePointsAtTheEndpointThatNeverExisted()
    {
        foreach (var row in Rows())
            Assert.DoesNotContain("/console/run", row.GetProperty("instead").GetString()!, StringComparison.Ordinal);
    }

    [Fact]
    public void EveryReferenceNamesAFileThatExists()
    {
        var repoRoot = Path.GetFullPath(Path.Combine(rig.SourceRoot, "..", ".."));
        foreach (var row in Rows())
        {
            var reference = row.GetProperty("reference").GetString()!;
            var path = reference.Split(',')[0].Split(' ')[0].Trim();
            if (!path.Contains('/', StringComparison.Ordinal)) continue;

            Assert.True(
                File.Exists(Path.Combine(repoRoot, path)),
                $"refusal reference '{reference}' points at {path}, which is not in the repository");
        }
    }

    [Fact]
    public void TheExplanationIsWrappedAndTheAlternativeIsNot()
    {
        var result = rig.Run("send", "--target", "clients", "--command", "help");
        var lines = result.OutLines;

        var wrapped = lines.Where(l => l.StartsWith("  x ", StringComparison.Ordinal)
                                       || (l.StartsWith("    ", StringComparison.Ordinal)
                                           && !l.Contains("Why:", StringComparison.Ordinal)
                                           && !l.Contains("  testrig", StringComparison.Ordinal)))
            .ToArray();
        Assert.NotEmpty(wrapped);
        foreach (var line in wrapped) Assert.True(line.Length <= 78, $"'{line}' is wider than the wrap width");

        // The alternative is a command line and stays on one line so it can be copied.
        var alternative = lines.Single(l => l.Contains("Use the control plane:", StringComparison.Ordinal));
        Assert.Contains("/console/exec", alternative, StringComparison.Ordinal);
    }

    [Fact]
    public void TheEchoedCommandRepeatsWhatTheCallerTyped()
    {
        var result = rig.RunIn(HomeWithInstance("echo"), "send", "--target", "HOSTIE", "--command", "help");
        Assert.Equal(Refused, result.ExitCode);

        // Casing is preserved: the caller is being shown their own command, not a normalized
        // one they never wrote.
        Assert.Contains("testrig send --target HOSTIE", result.StdOut, StringComparison.Ordinal);
        Assert.Contains("--target HOSTIE", result.StdOut, StringComparison.Ordinal);
    }

    [Fact]
    public void TheInstanceFlagRefusalNamesTheFlagsThatWereTyped()
    {
        var result = rig.Run("start", "--target", "server", "--new", "Mars", "--width", "1024", "--role", "host");
        Assert.Equal(Refused, result.ExitCode);
        Assert.Contains("--width", result.StdOut, StringComparison.Ordinal);
        Assert.Contains("--role", result.StdOut, StringComparison.Ordinal);

        // game-port and update-port are deliberately NOT instance-shape flags: both are also
        // the dedicated server's own start-time flags.
        Assert.DoesNotContain("--game-port,", result.StdOut, StringComparison.Ordinal);
    }

    [Fact]
    public void InstanceFlagsAreFineWhenTheTargetIsTheWholeRig()
    {
        // Under --target all those flags legitimately describe the client half.
        var result = rig.Run("update-game", "--target", "all", "--desktop", "Other", "--json");
        using var doc = result.Json();
        Assert.Equal(JsonValueKind.Null, doc.RootElement.GetProperty("refusal").ValueKind);
    }

    [Fact]
    public void ARefusalBeatsTheLockGate()
    {
        // A refusal corrects the caller's model of the rig and is worth nothing once a side
        // effect has happened, so it fires before the lock is asserted: this exits 3, not 5.
        var result = rig.Run("create", "--target", "server");
        Assert.Equal(Refused, result.ExitCode);
    }

    private IEnumerable<JsonElement> Rows() => rig.Surface.RootElement.GetProperty("refusals").EnumerateArray();

    [GeneratedRegex(@"testrig ([a-z][a-z-]*)")]
    private static partial Regex VerbCall();

    [GeneratedRegex(@"--path (/[a-zA-Z0-9/_-]+)")]
    private static partial Regex EndpointArgument();
}
