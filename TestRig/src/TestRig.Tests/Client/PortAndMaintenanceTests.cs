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
        var entry = fixture.Create("x", owner, seedMods: true, underTest: ["SprayPaintPlus"]);

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
    public void TheSeedDoesNotProvideTheDevelopersCopyOfAModThisInstanceTests()
    {
        // The whole fix for the double load, and it is a fix by construction rather than a
        // cleanup afterwards. An instance that records a mod under test never gets the
        // developer's copy of it, so deploy's Local_<Mod> is the only copy there can be.
        var fixture = new ClientFixture();
        var owner = fixture.Lease();
        fixture.AddDeveloperMod("SprayPaintPlus");
        fixture.AddRepositoryMod("SprayPaintPlus");

        var entry = fixture.Create("x", owner, seedMods: true, underTest: ["SprayPaintPlus"]);
        var paths = fixture.Layout.PathsFor("x");
        var seeded = Path.Combine(paths.ModsDir, "SprayPaintPlus");

        Assert.False(fixture.Fs.DirectoryExists(seeded));
        Assert.DoesNotContain(
            ModConfig.Read(fixture.Fs, paths.ModConfig),
            e => e.Path.Equals(seeded, StringComparison.OrdinalIgnoreCase));
        Assert.True(fixture.Output.Said("is under test here, so the developer's copy was NOT seeded"));

        // And after the deploy there is exactly one copy, at the deployed path.
        fixture.Half.Deploy([entry], ["SprayPaintPlus"], owner);

        var local = Path.Combine(paths.ModsDir, "Local_SprayPaintPlus");
        Assert.True(fixture.Fs.FileExists(Path.Combine(local, "SprayPaintPlus.dll")));
        Assert.False(fixture.Fs.DirectoryExists(seeded));
        Assert.Single(
            ModConfig.Read(fixture.Fs, paths.ModConfig),
            e => e.Path.Contains("SprayPaintPlus", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void EveryModOUTSIDETheSetIsStillSeededAtItsPublishedState()
    {
        // The reason the set is explicit rather than "every mod this repository builds". A rig
        // is normally testing ONE mod, and this repository carries work in progress for the
        // others: seeding them from the developer's folder is what keeps an unrelated
        // half-finished mod from changing the behaviour of the one under test.
        var fixture = new ClientFixture();
        var owner = fixture.Lease();
        fixture.AddDeveloperMod("SprayPaintPlus");
        fixture.AddDeveloperMod("EquipmentPlus");
        fixture.AddRepositoryMod("SprayPaintPlus");
        fixture.AddRepositoryMod("EquipmentPlus");

        fixture.Create("x", owner, seedMods: true, underTest: ["SprayPaintPlus"]);
        var paths = fixture.Layout.PathsFor("x");

        var other = Path.Combine(paths.ModsDir, "EquipmentPlus");
        Assert.True(fixture.Fs.FileExists(Path.Combine(other, "EquipmentPlus.dll")));
        Assert.Equal("the developer's published build", fixture.Fs.ReadAllText(Path.Combine(other, "EquipmentPlus.dll")));
        Assert.Contains(
            ModConfig.Read(fixture.Fs, paths.ModConfig),
            e => e.Path.Equals(other, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void TheUnderTestSetSurvivesARebuildTheWayTheRoleAndThePortsDo()
    {
        // create --force is the routine way to pick up a new plugin build. Emptying the set in
        // passing would put the developer's copy of the mod under test back beside the
        // deployed one, which is the state this whole mechanism exists to make impossible.
        var fixture = new ClientFixture();
        var owner = fixture.Lease();
        fixture.AddDeveloperMod("SprayPaintPlus");
        fixture.AddRepositoryMod("SprayPaintPlus");
        fixture.Create("x", owner, seedMods: true, underTest: ["SprayPaintPlus"]);

        fixture.CreateWith(new CreateOptions
        {
            Instance = "x", CallerId = owner, Force = true, SeedMods = true,
        });

        Assert.Equal(["SprayPaintPlus"], fixture.Registry.Find("x")!.UnderTestMods);
        Assert.False(fixture.Fs.DirectoryExists(Path.Combine(fixture.Layout.PathsFor("x").ModsDir, "SprayPaintPlus")));
    }

    [Fact]
    public void AnEmptyUnderTestClearsTheSetBecauseItIsATypedAnswer()
    {
        var fixture = new ClientFixture();
        var owner = fixture.Lease();
        fixture.AddDeveloperMod("SprayPaintPlus");
        fixture.AddRepositoryMod("SprayPaintPlus");
        fixture.Create("x", owner, seedMods: true, underTest: ["SprayPaintPlus"]);

        fixture.CreateWith(new CreateOptions
        {
            Instance = "x", CallerId = owner, Force = true, SeedMods = true, UnderTest = [],
        });

        Assert.Empty(fixture.Registry.Find("x")!.UnderTestMods);
        Assert.True(fixture.Fs.DirectoryExists(Path.Combine(fixture.Layout.PathsFor("x").ModsDir, "SprayPaintPlus")));
    }

    [Fact]
    public void AStaleChainloaderCopyIsRemovedWholeAndTheReasonIsNamed()
    {
        var fixture = new ClientFixture();
        var owner = fixture.Lease();
        fixture.AddRepositoryMod("SprayPaintPlus");
        var entry = fixture.Create("x", owner, seedMods: true, underTest: ["SprayPaintPlus"]);

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
        var entry = fixture.Create("x", owner, underTest: ["NoAbout"]);

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
    public void NoModsNamedMeansTHISINSTANCESUnderTestSetAndNeverEveryReleasedMod()
    {
        // It used to mean "every mod under Mods/", and that is the shape that produced the
        // double load: it deployed builds beside the developer's seeded copies of mods nobody
        // was testing, and StationeersLaunchPad loads both of every such pair.
        var fixture = new ClientFixture();
        var owner = fixture.Lease();
        fixture.AddRepositoryMod("A");
        fixture.AddRepositoryMod("B");

        var bare = fixture.Create("bare", owner);
        Assert.Contains("records no mods under test",
            Assert.Throws<RigRefusalException>(() => fixture.Half.Deploy([bare], null, owner)).Message,
            StringComparison.Ordinal);

        var entry = fixture.Create("x", owner, underTest: ["A"]);
        Assert.Equal(new DeployCounts(1, 0), fixture.Half.Deploy([entry], null, owner));

        // B is on this rig and is NOT deployed here, because this instance does not test it.
        var paths = fixture.Layout.PathsFor("x");
        Assert.True(fixture.Fs.DirectoryExists(Path.Combine(paths.ModsDir, "Local_A")));
        Assert.False(fixture.Fs.DirectoryExists(Path.Combine(paths.ModsDir, "Local_B")));
    }

    [Fact]
    public void DeployingAModTheInstanceDoesNotTestRefusesAndNamesHowToRecordIt()
    {
        // A refusal rather than a cleanup: "this instance tests SprayPaintPlus" is a decision
        // about the whole instance and belongs at create. A deploy re-deciding it in passing
        // would change what every other mod on that instance is, one command at a time.
        var fixture = new ClientFixture();
        var owner = fixture.Lease();
        fixture.AddRepositoryMod("A");
        var entry = fixture.Create("x", owner, underTest: ["B"]);

        var ex = Assert.Throws<RigRefusalException>(() => fixture.Half.Deploy([entry], ["A"], owner));

        Assert.Contains("is not provisioned to test 'A'", ex.Message, StringComparison.Ordinal);
        Assert.Contains("it records B", ex.Message, StringComparison.Ordinal);
        Assert.Contains("--under-test A", ex.Message, StringComparison.Ordinal);
        Assert.False(fixture.Fs.DirectoryExists(Path.Combine(fixture.Layout.PathsFor("x").ModsDir, "Local_A")));
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
