using TestRig.Core.Infrastructure;
using TestRig.Playtest.Attestation;
using TestRig.Playtest.Model;
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
[Collection(CheckRegistryCollection.Name)]
public sealed class ShippedChecksTests
{
    private static IReadOnlyList<IPlaytestCheck> Checks => TestRig.Playtests.Playtests.All;

    [Fact]
    public void EveryCheckFileRegistersItselfWithNoCentralListToForget()
    {
        // Each check carries its own module initializer, so adding a check is adding a file.
        Assert.NotEmpty(Checks);
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
            Assert.EndsWith(@"userdata\mods\SprayPaintPlus\SprayPaintPlus.dll", identity.DeployedRelativePath, StringComparison.Ordinal);
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
