using System.Text;
using TestRig.Core.Rig;
using TestRig.Playtest.Attestation;
using TestRig.Playtest.Model;
using TestRig.Tests.Playtest.Fakes;
using Xunit;

namespace TestRig.Tests.Playtest;

/// <summary>
///     Attestation: derived from the check's own location, never declared.
/// </summary>
/// <remarks>
///     A live run once nearly measured a stale seeded assembly and was saved by luck, which is
///     why a check that never attests cannot report a pass.
/// </remarks>
public sealed class AttestationTests
{
    private static byte[] Build(string content) => Encoding.UTF8.GetBytes(content);

    private static (PlaytestFixture Fixture, string CheckFile) Seeded(byte[]? deployed = null)
    {
        var fixture = new PlaytestFixture().WithInstance("hostie", 27701);
        var checkFile = fixture.SeedMod("SprayPaintPlus", "net.spraypaintplus", Build("the build under test"), ["hostie"], deployed);
        return (fixture, checkFile);
    }

    private static CheckSpec Spec(string checkFile) =>
        new("a check", "s", [new InstanceSpec("hostie")], sourceFile: checkFile);

    // ---- derivation -------------------------------------------------------

    [Fact]
    public void TheModItsBuildAndItsDeployedPathAllComeFromTheChecksOwnLocation()
    {
        var (fixture, checkFile) = Seeded();
        var identity = ModIdentityResolver.Resolve(checkFile, fixture.Files);

        Assert.Equal("SprayPaintPlus", identity.ModName);
        Assert.Equal("net.spraypaintplus", identity.Guid);
        Assert.Equal(PlaytestFixture.RepoRoot, identity.RepoRoot);
        Assert.EndsWith(@"SprayPaintPlus\SprayPaintPlus\bin\Release\SprayPaintPlus.dll", identity.BuildDllPath, StringComparison.Ordinal);
        // The deploy writes Local_<Mod>, so this is where attestation has to look. It named
        // the unprefixed path, which made 'binary-not-deployed' the only possible answer on a
        // correctly deployed instance; it passed because the fixture seeded the same wrong
        // path, which is the assertion-written-from-the-code shape this port keeps finding.
        Assert.Equal(@"userdata\mods\Local_SprayPaintPlus\SprayPaintPlus.dll", identity.DeployedRelativePath);
        Assert.Equal(LaunchPadMods.DeployedRelativeDll("SprayPaintPlus"), identity.DeployedRelativePath);
    }

    [Fact]
    public void TheGuidComesFromTheModsOwnAboutFile()
    {
        // Already the single source of truth, read by the game itself, so nothing new has to
        // be kept in sync.
        var (fixture, checkFile) = Seeded();
        Assert.Equal("net.spraypaintplus", ModIdentityResolver.Resolve(checkFile, fixture.Files).Guid);
    }

    [Fact]
    public void ACheckThatIsNotUnderAModsPlaytestsFolderCannotBeAttested()
    {
        var fixture = new PlaytestFixture();
        var thrown = Assert.Throws<PlaytestSignal>(() => ModIdentityResolver.Resolve(@"C:\somewhere\else\Check.cs", fixture.Files));
        Assert.Equal(Detectors.ModIdentityUnresolved, thrown.Detector);
        Assert.Contains("Mods/<Mod>/playtests/", thrown.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ACheckWithNoRecordedLocationCannotBeAttested()
    {
        var fixture = new PlaytestFixture();
        var thrown = Assert.Throws<PlaytestSignal>(() => ModIdentityResolver.Resolve(string.Empty, fixture.Files));
        Assert.Contains("CallerFilePath", thrown.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AModWithNoAboutFileCannotBeAttested()
    {
        var fixture = new PlaytestFixture();
        var thrown = Assert.Throws<PlaytestSignal>(() =>
            ModIdentityResolver.Resolve(Path.Combine(PlaytestFixture.RepoRoot, "Mods", "Ghost", "playtests", "C.cs"), fixture.Files));

        Assert.Equal(Detectors.ModIdentityUnresolved, thrown.Detector);
        Assert.Contains("About.xml", thrown.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AnAboutFileWithNoModIdCannotBeAttested()
    {
        var fixture = new PlaytestFixture();
        var about = Path.Combine(PlaytestFixture.RepoRoot, "Mods", "Ghost", "Ghost", "About");
        fixture.Files.AddDirectory(about);
        fixture.Files.AddFile(Path.Combine(about, "About.xml"), "<ModMetadata><Name>Ghost</Name></ModMetadata>");

        var thrown = Assert.Throws<PlaytestSignal>(() =>
            ModIdentityResolver.Resolve(Path.Combine(PlaytestFixture.RepoRoot, "Mods", "Ghost", "playtests", "C.cs"), fixture.Files));

        Assert.Contains("<ModID>", thrown.Message, StringComparison.Ordinal);
    }

    // ---- the four checks --------------------------------------------------

    [Fact]
    public void AttestationPassesWhenEverythingLinesUp()
    {
        var (fixture, checkFile) = Seeded();
        var ctx = fixture.Context(Spec(checkFile));

        ctx.AssertBinaryUnderTest();

        Assert.True(ctx.BinaryAttested);
        Assert.NotNull(ctx.Attestation);
        Assert.Equal("SprayPaintPlus", ctx.Attestation.Mod.ModName);
        Assert.Single(ctx.Attestation.Instances);
        Assert.Equal(1, ctx.Attestation.Instances[0].ConfigEntryCount);
    }

    [Fact]
    public void AMissingBuildIsInconclusiveAndNamesTheBuildCommand()
    {
        var (fixture, checkFile) = Seeded();
        var identity = ModIdentityResolver.Resolve(checkFile, fixture.Files);
        fixture.Files.DeleteFile(identity.BuildDllPath);

        var thrown = Assert.Throws<PlaytestSignal>(() => fixture.Context(Spec(checkFile)).AssertBinaryUnderTest());
        Assert.Equal(Detectors.BinaryMissing, thrown.Detector);
        Assert.Contains("dotnet build Mods/SprayPaintPlus", thrown.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AMissingProvisionStampIsInconclusiveAndNamesTheCreateCommand()
    {
        var (fixture, checkFile) = Seeded();
        fixture.Files.DeleteFile(Path.Combine(BinaryAttestation.InstanceDataFolder(PlaytestFixture.RigHomePath, "hostie"), BinaryAttestation.ProvisionStampName));

        var thrown = Assert.Throws<PlaytestSignal>(() => fixture.Context(Spec(checkFile)).AssertBinaryUnderTest());
        Assert.Equal(Detectors.ProvisionStampMissing, thrown.Detector);
        Assert.Contains("testrig create -Target hostie -Force", thrown.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AModThatIsNotDeployedIntoTheInstanceIsInconclusive()
    {
        var (fixture, checkFile) = Seeded();
        var data = BinaryAttestation.InstanceDataFolder(PlaytestFixture.RigHomePath, "hostie");
        fixture.Files.DeleteFile(Path.Combine(data, LaunchPadMods.DeployedRelativeDll("SprayPaintPlus")));

        var thrown = Assert.Throws<PlaytestSignal>(() => fixture.Context(Spec(checkFile)).AssertBinaryUnderTest());

        // Its own reason, because the instance RECORDS this mod under test: it deliberately
        // carries no seeded copy either, so there is nothing at all rather than the wrong
        // thing, and a reader sent hunting a stale file would find none.
        Assert.Equal(Detectors.UnderTestNotDeployed, thrown.Detector);
        Assert.Contains("has no 'SprayPaintPlus' at all", thrown.Message, StringComparison.Ordinal);
        Assert.Contains("testrig deploy SprayPaintPlus", thrown.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AModTheInstanceIsNotTestingReportsThePlainNotDeployedReason()
    {
        // The other half of the same distinction. Here the instance carries the DEVELOPER'S
        // published copy of the mod, seeded from their folder, so "not deployed" means the
        // ordinary thing and the remedy is the ordinary one.
        var fixture = new PlaytestFixture().WithInstance("hostie", 27701, underTest: ["SomethingElse"]);
        var checkFile = fixture.SeedMod("SprayPaintPlus", "net.spraypaintplus", Build("the build"), ["hostie"]);
        var identity = ModIdentityResolver.Resolve(checkFile, fixture.Files);
        fixture.Files.DeleteFile(Path.Combine(
            BinaryAttestation.InstanceDataFolder(PlaytestFixture.RigHomePath, "hostie"),
            identity.DeployedRelativePath));

        var thrown = Assert.Throws<PlaytestSignal>(() => fixture.Context(Spec(checkFile)).AssertBinaryUnderTest());

        Assert.Equal(Detectors.BinaryNotDeployed, thrown.Detector);
    }

    [Fact]
    public void TheUnprefixedSeededCopyIsNotWhatAttestationLooksAt()
    {
        // The exact shape of the defect: the developer's mod seed leaves an unprefixed
        // <Mod>/ folder in the instance, and attestation used to resolve to that path. A rig
        // with the seeded copy and NO deployed build therefore attested cleanly against a
        // build nobody deployed, which is the one thing attestation exists to rule out.
        var (fixture, checkFile) = Seeded();
        var data = BinaryAttestation.InstanceDataFolder(PlaytestFixture.RigHomePath, "hostie");

        // Move the deployed file to where the seed would put it, leaving nothing at the real
        // deployed path. This must now be inconclusive; it used to be a clean pass.
        fixture.Files.DeleteFile(Path.Combine(data, LaunchPadMods.DeployedRelativeDll("SprayPaintPlus")));
        fixture.Files.AddDirectory(Path.Combine(data, "userdata", "mods", "SprayPaintPlus"));
        fixture.Files.AddFile(
            Path.Combine(data, "userdata", "mods", "SprayPaintPlus", "SprayPaintPlus.dll"),
            Build("the build under test"));

        var thrown = Assert.Throws<PlaytestSignal>(() => fixture.Context(Spec(checkFile)).AssertBinaryUnderTest());
        Assert.Equal(Detectors.UnderTestNotDeployed, thrown.Detector);
        Assert.Contains("Local_SprayPaintPlus", thrown.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ADifferentBuildOfADifferentLengthIsStale()
    {
        var (fixture, checkFile) = Seeded(deployed: Build("an older and shorter build"));
        var thrown = Assert.Throws<PlaytestSignal>(() => fixture.Context(Spec(checkFile)).AssertBinaryUnderTest());
        Assert.Equal(Detectors.BinaryStale, thrown.Detector);
    }

    [Fact]
    public void ADifferentBuildOfTheSAMELengthIsAlsoStale()
    {
        // Defect P-07, and the reason it survived. The docstring claimed the deployed file was
        // matched "by length and write time"; the write time was formatted into the report and
        // never compared anywhere, and the comparison was file LENGTH alone. The suite's own
        // stale case used 89,600 against 96,768 bytes, so the equal-length case had never once
        // been tried, and a same-length different build attested cleanly.
        var build = Build("build A");
        var deployed = Build("build B");
        Assert.Equal(build.Length, deployed.Length);

        var fixture = new PlaytestFixture().WithInstance("hostie", 27701);
        var checkFile = fixture.SeedMod("SprayPaintPlus", "net.spraypaintplus", build, ["hostie"], deployed);

        var thrown = Assert.Throws<PlaytestSignal>(() => fixture.Context(Spec(checkFile)).AssertBinaryUnderTest());
        Assert.Equal(Detectors.BinaryStale, thrown.Detector);
        Assert.Contains("two builds of the same length are not the same build", thrown.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AProcessThatLoadedNothingForTheGuidIsInconclusive()
    {
        // The file being correct and the process having loaded it are different facts, and
        // this is the only one of the two that can be read from inside the process. The
        // right assembly sits on disk beside a process that never loaded it.
        var fixture = new PlaytestFixture().WithInstance("hostie", 27701);
        var checkFile = fixture.SeedMod("SprayPaintPlus", "net.spraypaintplus", Build("the build"), ["hostie"], liveConfig: false);
        fixture.Transport.State("hostie").SetConfig("net.somethingelse", "s", "k", "v");

        var thrown = Assert.Throws<PlaytestSignal>(() => fixture.Context(Spec(checkFile)).AssertBinaryUnderTest());
        Assert.Equal(Detectors.BinaryConfigMismatch, thrown.Detector);
        Assert.Contains("has not loaded the mod", thrown.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void TheLiveConfigCheckIsACoarseSmokeTestAndNotACount()
    {
        // The PowerShell version compared a DECLARED number of config entries and a declared
        // number of distinct sections, which made every settings change a check edit and
        // diagnosed a wrong guid as a config mismatch. With a content hash doing the identity
        // work this is only what it ever honestly was.
        var fixture = new PlaytestFixture().WithInstance("hostie", 27701);
        var checkFile = fixture.SeedMod("SprayPaintPlus", "net.spraypaintplus", Build("the build"), ["hostie"]);
        fixture.Transport.State("hostie").SetConfig("net.spraypaintplus", "Server - Other", "Another", "false");

        var ctx = fixture.Context(Spec(checkFile));
        ctx.AssertBinaryUnderTest();

        Assert.True(ctx.Attestation!.Instances[0].ConfigEntryCount >= 1);
    }

    [Fact]
    public void EveryInstanceIsAttestedNotJustTheFirst()
    {
        var fixture = new PlaytestFixture().WithInstance("hostie", 27701).WithInstance("joiner", 27702);
        var checkFile = fixture.SeedMod("SprayPaintPlus", "net.spraypaintplus", Build("the build"), ["hostie", "joiner"]);
        var spec = new CheckSpec("a check", "s", [new InstanceSpec("hostie"), new InstanceSpec("joiner")], sourceFile: checkFile);

        var ctx = fixture.Context(spec);
        ctx.AssertBinaryUnderTest();

        Assert.Equal(2, ctx.Attestation!.Instances.Count);
    }

    [Fact]
    public void AStaleDeployOnTheSECONDInstanceIsStillCaught()
    {
        var fixture = new PlaytestFixture().WithInstance("hostie", 27701).WithInstance("joiner", 27702);
        var checkFile = fixture.SeedMod("SprayPaintPlus", "net.spraypaintplus", Build("the build"), ["hostie", "joiner"]);

        var joinerDll = Path.Combine(
            BinaryAttestation.InstanceDataFolder(PlaytestFixture.RigHomePath, "joiner"),
            LaunchPadMods.DeployedRelativeDll("SprayPaintPlus"));
        fixture.Files.WriteAllText(joinerDll, "an older build");

        var spec = new CheckSpec("a check", "s", [new InstanceSpec("hostie"), new InstanceSpec("joiner")], sourceFile: checkFile);
        var thrown = Assert.Throws<PlaytestSignal>(() => fixture.Context(spec).AssertBinaryUnderTest());

        Assert.Equal(Detectors.BinaryStale, thrown.Detector);
        Assert.Contains("joiner", thrown.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void TheAttestationReportRecordsWhatWasComparedWithWhat()
    {
        var (fixture, checkFile) = Seeded();
        var ctx = fixture.Context(Spec(checkFile));
        ctx.AssertBinaryUnderTest();

        var json = ctx.Attestation!.ToJson();
        Assert.Contains("\"guid\": \"net.spraypaintplus\"", json, StringComparison.Ordinal);
        Assert.Contains("\"buildSha256\"", json, StringComparison.Ordinal);
        Assert.Contains("\"deployedSha256\"", json, StringComparison.Ordinal);
        Assert.Contains("own source location", json, StringComparison.Ordinal);
    }

    [Fact]
    public void TheAttestationReportIsWrittenIntoTheBundle()
    {
        var (fixture, checkFile) = Seeded();
        var ctx = fixture.Context(Spec(checkFile));
        ctx.AssertBinaryUnderTest();

        Assert.Contains(fixture.Files.AllFiles(), f => f.EndsWith("binary.json", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ThereIsNoDeclarationBlockLeftToGetWrong()
    {
        // Defect P-08: a block that omitted the build path, the deployed path and both config
        // counts attested on a parseable provision stamp alone and reported a clean pass, and
        // nothing tested or warned about it. There is nothing to omit now.
        var properties = typeof(CheckSpec).GetProperties().Select(p => p.Name).ToList();
        Assert.DoesNotContain("Binary", properties);
        Assert.DoesNotContain("Mod", properties);
        Assert.DoesNotContain("DllPath", properties);
        Assert.DoesNotContain("ConfigEntryCount", properties);
        Assert.Contains("SourceFile", properties);
    }

    [Fact]
    public void HashingIsOverContentAndNotMetadata()
    {
        var fixture = new PlaytestFixture();
        fixture.Files.AddDirectory(@"C:\h");
        fixture.Files.AddFile(@"C:\h\a.bin", Build("same"));
        fixture.Files.AddFile(@"C:\h\b.bin", Build("same"));
        fixture.Files.AddFile(@"C:\h\c.bin", Build("diff"));

        Assert.Equal(BinaryAttestation.HashFile(fixture.Files, @"C:\h\a.bin"), BinaryAttestation.HashFile(fixture.Files, @"C:\h\b.bin"));
        Assert.NotEqual(BinaryAttestation.HashFile(fixture.Files, @"C:\h\a.bin"), BinaryAttestation.HashFile(fixture.Files, @"C:\h\c.bin"));
    }

    [Fact]
    public void TheInstanceDataFolderIsNotWhereTheGameTreeLives()
    {
        // Two roots and both are correct: the launcher puts the game TREE under the instances
        // root and the instance DATA under the rig home.
        Assert.Equal(@"C:\rig\ClientRig\data\hostie", BinaryAttestation.InstanceDataFolder(@"C:\rig", "hostie"));
    }
}
