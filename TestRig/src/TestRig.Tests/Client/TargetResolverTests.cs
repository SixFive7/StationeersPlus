using TestRig.Core.Rig;
using TestRig.Core.Session;
using Xunit;

namespace TestRig.Tests.Client;

/// <summary>
/// Turning <c>--target</c> into a decision, and refusing before any work happens.
/// </summary>
public sealed class TargetResolverTests
{
    private static readonly string[] Rig = ["client1", "client2"];

    // ---- defaults (COMMON-102, COMMON-103) --------------------------------

    [Theory]
    [InlineData("lock")]
    [InlineData("unlock")]
    [InlineData("refresh-lock")]
    [InlineData("capture-baseline")]
    [InlineData("reset")]
    [InlineData("status")]
    [InlineData("list")]
    [InlineData("update-game")]
    [InlineData("update-mods")]
    [InlineData("deploy")]
    [InlineData("logs")]
    public void TheElevenRigWideVerbsDefaultToAll(string verb) => Assert.Equal("all", TargetResolver.DefaultTarget(verb));

    [Theory]
    [InlineData("start")]
    [InlineData("stop")]
    [InlineData("save")]
    [InlineData("call")]
    [InlineData("send")]
    [InlineData("create")]
    [InlineData("remove")]
    [InlineData("snapshot")]
    [InlineData("wait")]
    public void EverythingThatActsOnARunningThingHasNoDefault(string verb) =>
        Assert.Equal("", TargetResolver.DefaultTarget(verb));

    [Fact]
    public void ExactlyElevenVerbsDefaultToAll()
    {
        // The default is the fix for the failure that started the consolidation. Adding a
        // twelfth without noticing widens the blast radius of a verb nobody meant to widen.
        Assert.Equal(11, TargetResolver.RigWideDefaultAll.Count);
    }

    [Fact]
    public void AVerbWithNoTargetAndNoDefaultRefusesAndNamesTheThreeForms()
    {
        var ex = Assert.Throws<RigRefusalException>(() => TargetResolver.Resolve(null, "start", Rig));
        Assert.Contains("'server'", ex.Message, StringComparison.Ordinal);
        Assert.Contains("'clients'", ex.Message, StringComparison.Ordinal);
        Assert.Contains("testrig list", ex.Message, StringComparison.Ordinal);
    }

    // ---- resolution (COMMON-105 to COMMON-111) ----------------------------

    [Fact]
    public void AllIsBothHalvesAndServerIsNeitherInstance()
    {
        var all = TargetResolver.Resolve("all", "status", Rig);
        Assert.Equal(TargetKind.All, all.Kind);
        Assert.True(all.Server);
        Assert.Equal(Rig, all.Names);

        var server = TargetResolver.Resolve("server", "status", Rig);
        Assert.Equal(TargetKind.Server, server.Kind);
        Assert.True(server.Server);
        Assert.Empty(server.Names);

        var clients = TargetResolver.Resolve("clients", "status", Rig);
        Assert.Equal(TargetKind.Clients, clients.Kind);
        Assert.False(clients.Server);
        Assert.Equal(Rig, clients.Names);
    }

    [Fact]
    public void TheThreeKeywordsMatchCaseInsensitively()
    {
        Assert.Equal(TargetKind.All, TargetResolver.Resolve("ALL", "status", Rig).Kind);
        Assert.Equal(TargetKind.Server, TargetResolver.Resolve("Server", "status", Rig).Kind);
        Assert.Equal(TargetKind.Clients, TargetResolver.Resolve("CLIENTS", "status", Rig).Kind);
    }

    [Fact]
    public void ACommaListIsSplitTrimmedAndEmptyElementsDropped()
    {
        var resolved = TargetResolver.Resolve(" client1 , , client2 ", "stop", Rig);
        Assert.Equal(TargetKind.Instance, resolved.Kind);
        Assert.Equal(["client1", "client2"], resolved.Names);
        Assert.Equal(" client1 , , client2 ", resolved.Spec);
    }

    [Fact]
    public void ASpecThatNamesNothingRefuses()
    {
        var ex = Assert.Throws<RigRefusalException>(() => TargetResolver.Resolve(" , , ", "stop", Rig));
        Assert.Contains("names nothing", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AnUnknownInstanceRefusesAndNamesWhatIsProvisioned()
    {
        // NEVER a silent empty set: an empty set makes a stop look successful and a start look
        // done, and a typo once fell through to stopping the whole rig.
        var ex = Assert.Throws<RigRefusalException>(() => TargetResolver.Resolve("clint1", "stop", Rig));
        Assert.Contains("client1, client2", ex.Message, StringComparison.Ordinal);
        Assert.Contains("testrig create --target clint1", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AnUnknownInstanceOnAnEmptyRigSaysSo()
    {
        var ex = Assert.Throws<RigRefusalException>(() => TargetResolver.Resolve("client1", "stop", []));
        Assert.Contains("(none provisioned)", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AllowUnknownIsWhatLetsCreateNameSomethingThatDoesNotExistYet()
    {
        var resolved = TargetResolver.Resolve("brandnew", "create", Rig, allowUnknown: true);
        Assert.Equal(["brandnew"], resolved.Names);
    }

    // ---- the refusals AssertVerbApplies fires -----------------------------

    [Fact]
    public void TheFiveRigWideVerbsRefuseAnythingButAll()
    {
        foreach (var verb in RefusalMatrix.RigWideVerbs)
        {
            foreach (var spec in new[] { "server", "clients", "client1" })
            {
                var resolved = TargetResolver.Resolve(spec, verb, Rig);
                var ex = Assert.Throws<RigRefusalException>(() => TargetResolver.AssertVerbApplies(verb, resolved));

                // The alternative a rig-wide refusal offers must never narrow the target
                // again, which is the entire point of the refusal.
                Assert.NotNull(ex.Refusal);
                Assert.DoesNotContain("--target", ex.Refusal!.Instead, StringComparison.Ordinal);
                Assert.StartsWith($"testrig {verb}", ex.Refusal.Instead, StringComparison.Ordinal);
            }

            // And they are happy with all, which is what proves the refusal is about the
            // narrowing rather than about the verb.
            TargetResolver.AssertVerbApplies(verb, TargetResolver.Resolve("all", verb, Rig));
        }
    }

    [Fact]
    public void ARigWideVerbReturnsImmediatelyAndNeverReachesTheInstanceFlagRule()
    {
        // The early return is load-bearing: without it, 'reset --target all' with a typed
        // instance flag would fall through to a rule that does not apply to it.
        TargetResolver.AssertVerbApplies(
            "reset",
            TargetResolver.Resolve("all", "reset", Rig),
            new VerbOptions { TypedInstanceFlags = ["role"] });
    }

    [Fact]
    public void CallRefusesTheServerAndAllButNotClientsOrAnInstance()
    {
        Assert.Throws<RigRefusalException>(() =>
            TargetResolver.AssertVerbApplies("call", TargetResolver.Resolve("server", "call", Rig)));
        Assert.Throws<RigRefusalException>(() =>
            TargetResolver.AssertVerbApplies("call", TargetResolver.Resolve("all", "call", Rig)));

        TargetResolver.AssertVerbApplies("call", TargetResolver.Resolve("clients", "call", Rig));
        TargetResolver.AssertVerbApplies("call", TargetResolver.Resolve("client1", "call", Rig));
    }

    [Fact]
    public void SendRefusesEveryClientShapeAndAcceptsOnlyTheServer()
    {
        foreach (var spec in new[] { "client1", "clients", "all" })
        {
            Assert.Throws<RigRefusalException>(() =>
                TargetResolver.AssertVerbApplies("send", TargetResolver.Resolve(spec, "send", Rig)));
        }

        TargetResolver.AssertVerbApplies("send", TargetResolver.Resolve("server", "send", Rig));
    }

    [Fact]
    public void CreateAndRemoveRefuseEveryRigWideShape()
    {
        foreach (var spec in new[] { "server", "all", "clients" })
        {
            Assert.Throws<RigRefusalException>(() =>
                TargetResolver.AssertVerbApplies("create", TargetResolver.Resolve(spec, "create", Rig, allowUnknown: true)));
            Assert.Throws<RigRefusalException>(() =>
                TargetResolver.AssertVerbApplies("remove", TargetResolver.Resolve(spec, "remove", Rig)));
        }
    }

    [Fact]
    public void SnapshotRefusesTheServerAndAllOnly()
    {
        Assert.Throws<RigRefusalException>(() =>
            TargetResolver.AssertVerbApplies("snapshot", TargetResolver.Resolve("server", "snapshot", Rig)));
        Assert.Throws<RigRefusalException>(() =>
            TargetResolver.AssertVerbApplies("snapshot", TargetResolver.Resolve("all", "snapshot", Rig)));
        TargetResolver.AssertVerbApplies("snapshot", TargetResolver.Resolve("clients", "snapshot", Rig));
    }

    [Fact]
    public void WaitSaveAndStartFireOffTheResolvedServerFlagSoTheyAlsoFireUnderAll()
    {
        // This is the rule the two specs never wrote down: these three fire off
        // Resolved.Server, not off a target kind of exactly 'server', which is correct and is
        // a DIFFERENT rule from the one the other five verbs use.
        foreach (var spec in new[] { "server", "all" })
        {
            Assert.Throws<RigRefusalException>(() => TargetResolver.AssertVerbApplies(
                "wait", TargetResolver.Resolve(spec, "wait", Rig), new VerbOptions { Stage = ReadinessStage.Menu }));

            Assert.Throws<RigRefusalException>(() => TargetResolver.AssertVerbApplies(
                "save", TargetResolver.Resolve(spec, "save", Rig), new VerbOptions()));

            Assert.Throws<RigRefusalException>(() => TargetResolver.AssertVerbApplies(
                "start", TargetResolver.Resolve(spec, "start", Rig), new VerbOptions()));
        }
    }

    [Fact]
    public void WaitAcceptsInWorldOnTheServerAndEveryStageOnAClient()
    {
        TargetResolver.AssertVerbApplies(
            "wait", TargetResolver.Resolve("server", "wait", Rig), new VerbOptions { Stage = ReadinessStage.InWorld });
        TargetResolver.AssertVerbApplies(
            "wait", TargetResolver.Resolve("clients", "wait", Rig), new VerbOptions { Stage = ReadinessStage.Menu });
    }

    [Fact]
    public void SaveAndStartArePermittedOnceTheyHaveWhatTheServerNeeds()
    {
        TargetResolver.AssertVerbApplies(
            "save", TargetResolver.Resolve("server", "save", Rig), new VerbOptions { SaveName = "Luna" });
        TargetResolver.AssertVerbApplies(
            "start", TargetResolver.Resolve("server", "start", Rig), new VerbOptions { HasWorld = true });
    }

    [Fact]
    public void InstanceShapeFlagsRefuseOnServerAndNeverUnderAll()
    {
        var options = new VerbOptions { TypedInstanceFlags = ["role", "game-port"] };

        var ex = Assert.Throws<RigRefusalException>(() => TargetResolver.AssertVerbApplies(
            "start", TargetResolver.Resolve("server", "start", Rig), options with { HasWorld = true }));
        Assert.Contains("--role, --game-port", ex.Message, StringComparison.Ordinal);

        // Under all they legitimately describe the client half.
        TargetResolver.AssertVerbApplies(
            "deploy", TargetResolver.Resolve("all", "deploy", Rig), options);
    }

    [Fact]
    public void NoTypedFlagsMeansTheInstanceShapeRefusalNeverFires()
    {
        // The PowerShell needed a filter here because an empty collection had a count of one,
        // which would have fired this refusal on every single server-targeted command.
        TargetResolver.AssertVerbApplies(
            "deploy", TargetResolver.Resolve("server", "deploy", Rig), new VerbOptions());
        TargetResolver.AssertVerbApplies("deploy", TargetResolver.Resolve("server", "deploy", Rig));
    }
}
