using TestRig.Core.Infrastructure;
using TestRig.Core.Rig;
using TestRig.Playtest;
using TestRig.Playtest.Attestation;
using TestRig.Playtest.Model;
using TestRig.Tests.Infrastructure;
using Xunit;

namespace TestRig.Tests.Playtest;

/// <summary>
///     The checks that are actually compiled into this build.
/// </summary>
/// <remarks>
///     These assertions run against the real repository layout, because that is what
///     attestation derives from: a check that moves out from under
///     <c>Mods/&lt;Mod&gt;/playtests/</c> stops being attestable, and this is what says so
///     before a rig session finds out.
/// </remarks>
public sealed class ShippedChecksTests
{
    private static IReadOnlyList<IPlaytestCheck> Checks => TestRig.Playtests.Playtests.All;

    [Fact]
    public void AnEmptyCheckSetIsARefusalAndNotAnAnswer()
    {
        // The shape that let the whole defect hide: --list-checks printed "Registered checks:"
        // and nothing else, and exited 0. An empty answer read as a clean one, and the run
        // went on to diagnose the wrong thing. Held in the renderer rather than in the CLI's
        // branch ordering, so moving a branch cannot bring it back.
        var thrown = Assert.Throws<InvalidOperationException>(() => PlaytestListing.Checks([]));

        Assert.Contains("NO playtest checks compiled into it", thrown.Message, StringComparison.Ordinal);
        Assert.Contains("build defect and not an empty rig", thrown.Message, StringComparison.Ordinal);
        Assert.Contains("dotnet publish", thrown.Message, StringComparison.Ordinal);

        Assert.Throws<InvalidOperationException>(() => PlaytestListing.AssertAnyCompiledIn([]));
        PlaytestListing.AssertAnyCompiledIn(Checks);
    }

    [Fact]
    public void TheListingSaysHowManyChecksAreCompiledIn()
    {
        // A count a caller can read without counting lines, and the thing that would have made
        // an empty listing obvious at a glance.
        var listing = PlaytestListing.Checks(Checks);
        Assert.Contains($"{Checks.Count} check(s) compiled in", listing, StringComparison.Ordinal);
    }

    [Fact]
    public void EveryCompiledCheckTypeIsInTheStaticList()
    {
        // The guard that replaces self-registration. Checks are named one by one in
        // Playtests.All because that direct reference is what roots them for the AOT trimmer;
        // a [ModuleInitializer] is not a root, and the shipped binary carried zero checks as a
        // result. The cost of a central list is that somebody forgets to extend it, so this
        // reflects over the assembly and fails on any check type nothing in the list builds.
        var assembly = typeof(TestRig.Playtests.Playtests).Assembly;
        var compiled = assembly.GetTypes()
            .Where(t => t is { IsClass: true, IsAbstract: false } && typeof(IPlaytestCheck).IsAssignableFrom(t))
            .ToList();

        Assert.NotEmpty(compiled);

        var listed = Checks.Select(c => c.GetType()).ToHashSet();
        var missing = compiled.Where(t => !listed.Contains(t)).Select(t => t.FullName).ToList();

        Assert.True(missing.Count == 0,
            "These check types are compiled in but are not in TestRig.Playtests.Playtests.All, so the shipped "
            + "binary will never run them and the AOT trimmer may remove them entirely: "
            + string.Join(", ", missing));
    }

    [Fact]
    public void NoCheckFileIsCompiledInWithoutBeingReachable()
    {
        // The other direction of the same guard: every check on disk under Mods/*/playtests/
        // is compiled in, so a file that declares a check the list forgot shows up above.
        // Counting the files here is what makes "the list is complete" a claim about the
        // repository rather than about the list.
        var repoRoot = Directory.GetParent(RigSources.SrcRoot)!.Parent!.FullName;
        var checkFiles = Directory
            .EnumerateFiles(Path.Combine(repoRoot, "Mods"), "*.cs", SearchOption.AllDirectories)
            .Where(f => f.Contains(Path.Combine("Mods", "SprayPaintPlus", "playtests"), StringComparison.OrdinalIgnoreCase))
            .ToList();

        Assert.NotEmpty(checkFiles);

        // Every check in the list records the file it was written in, via [CallerFilePath].
        var sources = Checks.Select(c => Path.GetFullPath(c.Spec.SourceFile)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var source in sources) Assert.Contains(source, checkFiles.Select(Path.GetFullPath));
    }

    [Fact]
    public void TheEightSprayPaintPlusChecksAreAllPresent()
    {
        string[] expected =
        [
            "the first-use notice cap stops after three lines",
            "the join summary is one console line naming every blocked function",
            "the eyedropper explains a cross-family pick once per click",
            "the effective-settings line is one log line and never reaches the console",
            "the conflict banner is one boot line then six world lines",
            "the host own client half must not leak onto a joiner",
            "a non-owner reaches metallic while the owner is connected",
            "the entitlement outlives the owner",
        ];

        var names = Checks.Select(c => c.Spec.Name).ToList();
        foreach (var name in expected) Assert.Contains(name, names);

        // Exactly eight, so a check file that silently failed to register is caught here
        // rather than by a suite that quietly ran seven.
        var fromThisMod = Checks.Count(c => c.Spec.SourceFile.Contains(@"\SprayPaintPlus\playtests\", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(expected.Length, fromThisMod);
    }

    [Fact]
    public void NoCheckIsRegisteredTwice()
    {
        var names = Checks.Select(c => c.Spec.Name).ToList();
        Assert.Equal(names.Count, names.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void EveryCheckSaysWhatItIsFor()
    {
        foreach (var check in Checks)
        {
            Assert.False(string.IsNullOrWhiteSpace(check.Spec.Summary), $"'{check.Spec.Name}' has no summary");
        }
    }

    [Fact]
    public void EveryCheckLivesWhereAttestationCanDeriveItsModFromIt()
    {
        var files = new SystemFileSystem();
        foreach (var check in Checks)
        {
            var identity = ModIdentityResolver.Resolve(check.Spec.SourceFile, files);
            Assert.False(string.IsNullOrWhiteSpace(identity.Guid), $"'{check.Spec.Name}' resolved no guid");
            Assert.StartsWith("net.", identity.Guid, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void TheSprayPaintPlusChecksDeriveThatModAndItsRealGuid()
    {
        var files = new SystemFileSystem();
        foreach (var check in Checks.Where(c => c.Spec.SourceFile.Contains("SprayPaintPlus", StringComparison.OrdinalIgnoreCase)))
        {
            var identity = ModIdentityResolver.Resolve(check.Spec.SourceFile, files);
            Assert.Equal("SprayPaintPlus", identity.ModName);
            Assert.Equal("net.spraypaintplus", identity.Guid);
            // Local_ prefixed, because that is where the deploy puts it. This assertion
            // used to name the unprefixed path, which no correctly deployed instance has ever
            // had: every check reported binary-not-deployed on a correct rig and found a file
            // only when a stale seeded copy happened to sit there. Derived from Core's own
            // helper, so the deploy and the attestation cannot drift apart again.
            Assert.Equal(
                LaunchPadMods.DeployedRelativeDll("SprayPaintPlus"),
                identity.DeployedRelativePath);
            Assert.EndsWith(@"userdata\mods\Local_SprayPaintPlus\SprayPaintPlus.dll", identity.DeployedRelativePath, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void EveryInstanceEveryCheckNamesIsOneTheRigActuallyHas()
    {
        // The harness never creates an instance, so a check naming one that does not exist
        // costs a whole lock acquisition to find out.
        string[] known = ["hostie", "joiner"];
        foreach (var check in Checks)
        {
            foreach (var instance in check.Spec.Instances)
            {
                Assert.Contains(instance.Name, known);
            }
        }
    }

    [Fact]
    public void TheTwoDlcChecksDeclareTheJoinerFirstSoTeardownReachesItFirst()
    {
        // Teardown stops non-hosts before hosts in the order they were started, and both of
        // these end up with the SECOND instance holding the world.
        foreach (var name in new[] { "a non-owner reaches metallic while the owner is connected", "the entitlement outlives the owner" })
        {
            var check = Checks.Single(c => c.Spec.Name == name);
            Assert.Equal("joiner", check.Spec.Instances[0].Name);
            Assert.Empty(check.Spec.HostNames);
        }
    }

    [Fact]
    public void TheConflictBannerCheckDeclaresOneClientAndNoHost()
    {
        // Deliberate: it leaves bring-up at the menu instead of creating a world that the
        // restart would throw away, and the body drives the host endpoint itself.
        var check = Checks.Single(c => c.Spec.Name == "the conflict banner is one boot line then six world lines");
        Assert.Single(check.Spec.Instances);
        Assert.Equal(InstanceRole.Client, check.Spec.Instances[0].Role);
    }

    [Fact]
    public void TheMultiplayerChecksDeclareAHostWithAWorldAndAJoinerThatConnectsToIt()
    {
        foreach (var name in new[]
        {
            "the join summary is one console line naming every blocked function",
            "the eyedropper explains a cross-family pick once per click",
            "the effective-settings line is one log line and never reaches the console",
            "the host own client half must not leak onto a joiner",
        })
        {
            var check = Checks.Single(c => c.Spec.Name == name);
            Assert.Equal(2, check.Spec.Instances.Count);
            Assert.Equal(["hostie"], check.Spec.HostNames);
            Assert.Equal("Lunar", check.Spec.Instances[0].World);
            Assert.Equal("hostie", check.Spec.Instances[1].ConnectTo);
        }
    }

    [Fact]
    public void EveryCheckGetsTheLongerLockBecauseACheckOutlivesTenMinutes()
    {
        foreach (var check in Checks) Assert.True(check.Spec.TtlMinutes >= 20);
    }

    [Fact]
    public void TheBuildUnderTestPathFollowsTheRepositoriesOwnCsprojConvention()
    {
        var files = new SystemFileSystem();
        var check = Checks.First();
        var identity = ModIdentityResolver.Resolve(check.Spec.SourceFile, files);

        Assert.Contains(Path.Combine("bin", "Release"), identity.BuildDllPath, StringComparison.Ordinal);
        Assert.Equal(identity.ModName + ".dll", Path.GetFileName(identity.BuildDllPath));
    }
}
