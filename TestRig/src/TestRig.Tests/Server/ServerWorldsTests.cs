using TestRig.Core.Server;
using TestRig.Core.Session;
using TestRig.Tests.Session.Fakes;
using Xunit;

namespace TestRig.Tests.Server;

/// <summary>
///     Which worlds a <c>--new</c> may name, and what happens when it names another.
/// </summary>
/// <remarks>
///     Measured 2026-08-15: <c>--new Moon</c> was accepted by the launcher, the server booted
///     for ninety seconds, logged
///     <c>No such world name: Moon. Valid worlds: Europa3, Lunar, Mars2, MimasHerschel, Venus,
///     Vulcan (Deprecated), Vulcan2.</c> and then ran indefinitely with no world at all. The
///     set it would have accepted is on disk before launch.
/// </remarks>
public sealed class ServerWorldsTests
{
    /// <summary>The four world files whose id is not their folder name, plus two that are.</summary>
    private static ServerFixture WithWorldFiles()
    {
        var fixture = new ServerFixture().Installed();
        var worlds = Path.Combine(
            fixture.Paths.InstallDir, "rocketstation_DedicatedServer_Data", "StreamingAssets", "Worlds");

        void World(string folder, string file, string body) =>
            fixture.Fs.AddFile(Path.Combine(worlds, folder, file + ".xml"),
                "<GameData><WorldSettings>" + body + "</WorldSettings></GameData>");

        // The folder name is NOT the accepted name in four of the nine real cases. A scan of
        // folder names would refuse Europa3 and MimasHerschel and accept Europa and Mimas.
        World("Europa", "Europa", "<World Id=\"Europa3\" Priority=\"4\" Hidden=\"false\"><Name Key=\"x\" /></World>");
        World("Lunar", "Lunar", "<World Id=\"Lunar\" Priority=\"2\"><Name Key=\"x\" /></World>");
        World("Mars2", "Mars2", "<World Id=\"Mars2\" Priority=\"1\"><Name Key=\"x\" /></World>");
        World("Mimas", "MimasHerschel", "<World Id=\"MimasHerschel\" Priority=\"3\"><Name Key=\"x\" /></World>");

        // Two worlds in one folder, in two files, one of them deprecated but still accepted.
        World("Vulcan", "Vulcan", "<World Id=\"Vulcan\" Hidden=\"true\" Deprecated=\"true\"><Name Key=\"x\" /></World>");
        World("Vulcan", "VulcanV2", "<World Id=\"Vulcan2\" Priority=\"6\"><Name Key=\"x\" /></World>");

        // Tutorials are excluded, which is what makes the parsed set match what the server
        // prints. They are marked by an IsTutorial element, not by their folder name.
        World("Tutorial1", "Tutorial1",
            "<World Id=\"Tutorial1\" Hidden=\"false\"><IsTutorial Value=\"true\"/><Name Key=\"x\" /></World>");
        World("Tutorial4", "Tutorial4_Airlock",
            "<World Id=\"Airlock\" Hidden=\"false\"><IsTutorial Value=\"true\" /><Name Key=\"x\" /></World>");

        return fixture;
    }

    [Fact]
    public void TheCatalogueIsTheWorldIdInsideEachFileAndNotTheFolderName()
    {
        var fixture = WithWorldFiles();
        var catalogue = ServerWorlds.Read(fixture.Fs, fixture.Paths.InstallDir);

        Assert.True(catalogue.Readable);
        Assert.Equal(["Europa3", "Lunar", "Mars2", "MimasHerschel", "Vulcan", "Vulcan2"], catalogue.Names);

        // Neither the folder names nor the tutorial ids are in it.
        Assert.False(catalogue.Accepts("Europa"));
        Assert.False(catalogue.Accepts("Mimas"));
        Assert.False(catalogue.Accepts("Tutorial1"));
        Assert.False(catalogue.Accepts("Airlock"));
    }

    [Fact]
    public void MatchingIsCaseInsensitiveTheWayTheGameMatches()
    {
        var fixture = WithWorldFiles();
        var catalogue = ServerWorlds.Read(fixture.Fs, fixture.Paths.InstallDir);

        Assert.True(catalogue.Accepts("lunar"));
        Assert.True(catalogue.Accepts("LUNAR"));
    }

    [Fact]
    public void AWorldTheInstallDoesNotHaveIsRefusedBeforeAnythingIsLaunched()
    {
        var fixture = WithWorldFiles();

        var ex = Assert.Throws<RigRefusalException>(() => fixture.Half.AssertMapIsReal("Moon"));

        Assert.Contains("'Moon' is not a world this install has", ex.Message, StringComparison.Ordinal);
        Assert.Contains("run forever with no world", ex.Message, StringComparison.Ordinal);

        // The valid set, which is the useful part: a refusal that only says no leaves the
        // caller guessing, and the answer is right there on disk.
        Assert.Contains("Europa3, Lunar, Mars2, MimasHerschel, Vulcan, Vulcan2", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AWorldTheInstallDoesHaveIsAccepted()
    {
        var fixture = WithWorldFiles();
        fixture.Half.AssertMapIsReal("MimasHerschel");
        fixture.Half.AssertMapIsReal("lunar");
    }

    [Fact]
    public void AnUnreadableCatalogueValidatesNothingAndSaysSo()
    {
        // The game is the authority. A data-file layout this reader does not recognise, or a
        // game update that moves the folder, must never turn into a refusal of a world the
        // server would have started: that failure is worse than the one this prevents.
        var fixture = new ServerFixture().Installed();

        fixture.Half.AssertMapIsReal("AnythingAtAll");

        Assert.True(fixture.Output.Warned("Could not read the world catalogue"));
        Assert.False(ServerWorlds.Read(fixture.Fs, fixture.Paths.InstallDir).Readable);
    }

    [Fact]
    public void AWorldsFolderThatParsesToNothingIsUnknownRatherThanEmpty()
    {
        // An install whose Worlds folder exists but yields no ids is a shape this code no
        // longer understands, not a game with no worlds. Reporting it as an empty accepted set
        // would refuse every start.
        var fixture = new ServerFixture().Installed();
        fixture.Fs.AddFile(
            Path.Combine(fixture.Paths.InstallDir, "rocketstation_DedicatedServer_Data", "StreamingAssets", "Worlds",
                "Something", "Something.xml"),
            "<GameData><WorldSettings /></GameData>");

        Assert.False(ServerWorlds.Read(fixture.Fs, fixture.Paths.InstallDir).Readable);
        fixture.Half.AssertMapIsReal("Whatever");
    }

    [Fact]
    public void StartRefusesABadMapRatherThanLaunchingTheWrapper()
    {
        var fixture = WithWorldFiles();
        var owner = fixture.Lease();

        var ex = Assert.Throws<RigRefusalException>(() => fixture.Start(new ServerStartWorld(null, null, "Moon"), owner));

        Assert.Contains("not a world this install has", ex.Message, StringComparison.Ordinal);
        Assert.Empty(fixture.Launcher.Wrappers);
    }

    [Fact]
    public void AWorldIdIsNotExcludedByATutorialMarkerBelongingToTheNextWorld()
    {
        // Two worlds in one file, the SECOND a tutorial. Scanning the whole document for the
        // marker would drop both.
        var ids = ServerWorlds.IdsIn(
            "<WorldSettings>"
            + "<World Id=\"Real\"><Name Key=\"x\" /></World>"
            + "<World Id=\"Teaching\"><IsTutorial Value=\"true\" /></World>"
            + "</WorldSettings>");

        Assert.Equal(["Real"], ids);
    }
}
