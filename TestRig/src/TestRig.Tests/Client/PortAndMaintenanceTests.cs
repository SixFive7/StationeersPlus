using TestRig.Core.Client;
using TestRig.Core.Rig;
using TestRig.Core.Session;
using Xunit;

namespace TestRig.Tests.Client;

/// <summary>
/// The port refusals, and the three maintenance verbs.
/// </summary>
public sealed class PortAndMaintenanceTests
{
    private static readonly IReadOnlyList<InstanceEntry> Peers =
    [
        new() { InstanceName = "peer", Index = 1, Port = 27701, GamePort = 27801 },
    ];

    // =====================================================================
    // port guards
    // =====================================================================

    [Fact]
    public void EveryReservedGamePortIsRefusedWithItsOwnReasonAndTheRakNetExplanation()
    {
        foreach (var (port, reason) in RigConstants.ReservedGamePorts)
        {
            var ex = Assert.Throws<RigRefusalException>(() => PortGuards.AssertGamePortFree(Peers, "x", port));
            Assert.Contains(reason, ex.Message, StringComparison.Ordinal);
            Assert.Contains("coexist and route by destination address", ex.Message, StringComparison.Ordinal);
            Assert.Contains("no error anywhere", ex.Message, StringComparison.Ordinal);
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1023)]
    [InlineData(65536)]
    [InlineData(-1)]
    public void AnOutOfRangePortIsRefusedOnBothSides(int port)
    {
        Assert.Contains("out of range",
            Assert.Throws<RigRefusalException>(() => PortGuards.AssertGamePortFree(Peers, "x", port)).Message,
            StringComparison.Ordinal);
        Assert.Contains("out of range",
            Assert.Throws<RigRefusalException>(() => PortGuards.AssertControlPortFree(Peers, "x", port)).Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public void APeersGamePortAndAPeersControlPortAreBothRefusedInBothDirections()
    {
        // CLIENT-043: the PowerShell checked the game port against four things and the control
        // port against one, so the checking was asymmetric and incomplete.
        Assert.Throws<RigRefusalException>(() => PortGuards.AssertGamePortFree(Peers, "x", 27801));
        Assert.Throws<RigRefusalException>(() => PortGuards.AssertGamePortFree(Peers, "x", 27701));
        Assert.Throws<RigRefusalException>(() => PortGuards.AssertControlPortFree(Peers, "x", 27701));
        Assert.Throws<RigRefusalException>(() => PortGuards.AssertControlPortFree(Peers, "x", 27801));
    }

    [Fact]
    public void AControlPortOnAReservedNumberIsRefusedToo()
    {
        var ex = Assert.Throws<RigRefusalException>(() => PortGuards.AssertControlPortFree(Peers, "x", 28016));
        Assert.Contains("means two things", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void OneInstanceCannotTakeTheSameNumberForBothOfItsOwnPorts()
    {
        // The PowerShell skipped the instance under construction on both sides, so
        // create --port N --game-port N was accepted: legal on the wire, and ambiguous in every
        // later reading of that port.
        Assert.Throws<RigRefusalException>(() => PortGuards.AssertGamePortFree(Peers, "x", 31000, ownControlPort: 31000));
        Assert.Throws<RigRefusalException>(() => PortGuards.AssertControlPortFree(Peers, "x", 31000, ownGamePort: 31000));
    }

    [Fact]
    public void TheInstanceUnderConstructionDoesNotCollideWithItsOwnRecordedEntry()
    {
        // A rebuild has to be able to keep the ports it already had.
        PortGuards.AssertGamePortFree(Peers, "peer", 27801);
        PortGuards.AssertControlPortFree(Peers, "peer", 27701);
    }

    [Fact]
    public void AnEmptyOrNullRegistryIsAllowedBecauseTheFirstCreateHasNoRigJson()
    {
        PortGuards.AssertGamePortFree(null, "x", 27801);
        PortGuards.AssertGamePortFree([], "x", 27801);
    }

    // =====================================================================
    // update-game
    // =====================================================================

    [Fact]
    public void UpdateGameIsGatedBEFOREItsPreflightAndBeforeTheInstallPathIsResolved()
    {
        // CLIENT-308: the PowerShell gated nothing here and relied on the gate inside create,
        // so a zero-target update-game was ungated entirely and the crash marker recorded the
        // session's first mutating action as 'create'.
        var fixture = new ClientFixture();
        Assert.Throws<RigRefusalException>(() => fixture.UpdateGame([]));
        Assert.False(fixture.Rig.MarkerExists());
    }

    [Fact]
    public void UpdateGameWithNothingProvisionedSaysSoRatherThanFailing()
    {
        var fixture = new ClientFixture();
        var owner = fixture.Lease();
        fixture.UpdateGame([], owner);
        Assert.True(fixture.Output.Said("nothing to re-link"));
    }

    [Fact]
    public void TheWholeSetIsPreflightedBeforeAnyOfItIsRebuilt()
    {
        var fixture = new ClientFixture();
        var owner = fixture.Lease();
        var a = fixture.Create("a", owner);
        var b = fixture.Create("b", owner);

        fixture.Processes.Add(4321, "rocketstation", fixture.Clock.UtcNow);
        PidFiles.Write(fixture.Fs, fixture.Layout.PathsFor("b").PidFile, 4321, fixture.Clock.UtcNow);

        var stampBefore = fixture.Fs.ReadAllText(fixture.Layout.PathsFor("a").Stamp);
        var ex = Assert.Throws<RigRefusalException>(() => fixture.UpdateGame([a, b], owner));

        Assert.Contains("'b' is running", ex.Message, StringComparison.Ordinal);
        // A half-updated rig is worse than one that refused, so 'a' is untouched.
        Assert.Equal(stampBefore, fixture.Fs.ReadAllText(fixture.Layout.PathsFor("a").Stamp));
    }

    [Fact]
    public void UpdateGameKeepsEveryIdentityFieldOnEveryInstance()
    {
        var fixture = new ClientFixture();
        var owner = fixture.Lease();
        var host = fixture.CreateWith(new CreateOptions
        {
            Instance = "host1", CallerId = owner, Role = "host",
            Port = 31000, GamePort = 32000, ClientId = "42424242", Username = "Ada",
        });

        fixture.UpdateGame([host], owner);

        var after = fixture.Registry.Find("host1")!;
        Assert.Equal(31000, after.Port);
        Assert.Equal(32000, after.GamePortOr(0));
        Assert.Equal("42424242", after.ClientIdOr());
        Assert.Equal("Ada", after.UsernameOr("x"));
        Assert.Equal("host", after.RoleOr());
    }

    [Fact]
    public void TheDirtyMarkerRecordsUpdateGameRatherThanCreate()
    {
        var fixture = new ClientFixture();
        var owner = fixture.Lease();
        var entry = fixture.Create("x", owner);
        fixture.Rig.Marker.Clear();

        fixture.UpdateGame([entry], owner);

        Assert.Contains("update-game", fixture.Rig.MarkerText(), StringComparison.Ordinal);
    }

    // =====================================================================
    // update-mods
    // =====================================================================

    [Fact]
    public void UpdateModsRefusesWhileAnInstanceHoldsItsModFilesOpen()
    {
        var fixture = new ClientFixture();
        var owner = fixture.Lease();
        var entry = fixture.Create("x", owner);

        fixture.Processes.Add(4321, "rocketstation", fixture.Clock.UtcNow);
        PidFiles.Write(fixture.Fs, fixture.Layout.PathsFor("x").PidFile, 4321, fixture.Clock.UtcNow);

        var ex = Assert.Throws<RigRefusalException>(() => fixture.Half.UpdateMods([entry], owner));
        Assert.Contains("holds its mod files open", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void UpdateModsReSeedsEveryInstanceAndNamesWhatTheWipeTook()
    {
        var fixture = new ClientFixture();
        var owner = fixture.Lease();
        fixture.AddRepositoryMod("SprayPaintPlus");
        var entry = fixture.Create("x", owner, seedMods: true);

        var deployed = Path.Combine(fixture.Layout.PathsFor("x").ModsDir, "Local_SprayPaintPlus", "SprayPaintPlus.dll");
        fixture.Fs.AddFile(deployed, "deployed");

        fixture.Output.Clear();
        fixture.Half.UpdateMods([entry], owner);

        Assert.False(fixture.Fs.FileExists(deployed));
        Assert.True(fixture.Output.Warned("Local_SprayPaintPlus"));
        Assert.True(fixture.Output.Said("1 instance(s) re-seeded"));
    }

    // =====================================================================
    // deploy
    // =====================================================================

    [Fact]
    public void DeployPutsAModIntoTheLaunchPadLoadPathAndAddsItsLocalEntry()
    {
        // ClientDriver already occupies the Chainloader path, and a DLL in both makes Awake
        // fire twice and every Harmony patch register twice.
        var fixture = new ClientFixture();
        var owner = fixture.Lease();
        fixture.AddRepositoryMod("SprayPaintPlus");
        var entry = fixture.Create("x", owner, seedMods: true);

        var counts = fixture.Half.Deploy([entry], ["SprayPaintPlus"], owner);

        var paths = fixture.Layout.PathsFor("x");
        var local = Path.Combine(paths.ModsDir, "Local_SprayPaintPlus");
        Assert.True(fixture.Fs.FileExists(Path.Combine(local, "SprayPaintPlus.dll")));
        Assert.True(fixture.Fs.FileExists(Path.Combine(local, "About", "About.xml")));
        Assert.Equal(new DeployCounts(1, 0), counts);

        var entries = ModConfig.Read(fixture.Fs, paths.ModConfig);
        Assert.Contains(entries, e => e.Kind == "Local" && e.Path.Equals(local, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void AStaleChainloaderCopyIsRemovedWholeAndTheReasonIsNamed()
    {
        var fixture = new ClientFixture();
        var owner = fixture.Lease();
        fixture.AddRepositoryMod("SprayPaintPlus");
        var entry = fixture.Create("x", owner, seedMods: true);

        var stale = Path.Combine(fixture.Layout.PathsFor("x").BepInEx, "plugins", "SprayPaintPlus");
        fixture.Fs.AddFile(Path.Combine(stale, "SprayPaintPlus.dll"), "old");
        fixture.Fs.AddFile(Path.Combine(stale, "About", "About.xml"), "<About />");

        fixture.Half.Deploy([entry], ["SprayPaintPlus"], owner);

        Assert.False(fixture.Fs.DirectoryExists(stale));
        Assert.True(fixture.Output.Said("two loaders double every Harmony patch"));
    }

    [Fact]
    public void AnUnresolvableModAndAnUnbuiltOneAreBothSkippedWithTheirOwnReasons()
    {
        var fixture = new ClientFixture();
        var owner = fixture.Lease();
        fixture.Fs.AddFile(
            Path.Combine(ClientFixture.RepoRoot, "Mods", "NotBuilt", "NotBuilt", "About", "About.xml"), "<About />");
        var entry = fixture.Create("x", owner);

        var counts = fixture.Half.Deploy([entry], ["Ghost", "NotBuilt"], owner);

        Assert.Equal(new DeployCounts(0, 2), counts);
        Assert.True(fixture.Output.Warned("not found under Mods/, Plans/"));
        Assert.True(fixture.Output.Warned("Build it first"));
    }

    [Fact]
    public void AModWithNoAboutFolderStillDeploysButWarns()
    {
        var fixture = new ClientFixture();
        var owner = fixture.Lease();
        fixture.AddRepositoryMod("NoAbout", about: null);
        var entry = fixture.Create("x", owner);

        var counts = fixture.Half.Deploy([entry], ["NoAbout"], owner);

        Assert.Equal(new DeployCounts(1, 0), counts);
        Assert.True(fixture.Output.Warned("may not load it without About.xml"));
    }

    [Fact]
    public void DeployRefusesWhileAnInstanceHoldsItsPluginDllsOpen()
    {
        var fixture = new ClientFixture();
        var owner = fixture.Lease();
        fixture.AddRepositoryMod("Mod");
        var entry = fixture.Create("x", owner);

        fixture.Processes.Add(4321, "rocketstation", fixture.Clock.UtcNow);
        PidFiles.Write(fixture.Fs, fixture.Layout.PathsFor("x").PidFile, 4321, fixture.Clock.UtcNow);

        var ex = Assert.Throws<RigRefusalException>(() => fixture.Half.Deploy([entry], ["Mod"], owner));
        Assert.Contains("half-written file", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void NoModsNamedMeansEveryReleasedModAndAnEmptyModsFolderRefuses()
    {
        var fixture = new ClientFixture();
        var owner = fixture.Lease();
        var entry = fixture.Create("x", owner);

        Assert.Contains("no mod folders other than Template",
            Assert.Throws<RigRefusalException>(() => fixture.Half.Deploy([entry], null, owner)).Message,
            StringComparison.Ordinal);

        fixture.AddRepositoryMod("A");
        fixture.AddRepositoryMod("B");
        Assert.Equal(new DeployCounts(2, 0), fixture.Half.Deploy([entry], null, owner));
    }

    [Fact]
    public void NoInstancesSelectedReturnsZeroesRatherThanThrowing()
    {
        var fixture = new ClientFixture();
        var owner = fixture.Lease();
        Assert.Equal(new DeployCounts(0, 0), fixture.Half.Deploy([], null, owner));
        Assert.True(fixture.Output.Said("No client instances selected"));
    }
}
