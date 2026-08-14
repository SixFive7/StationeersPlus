using TestRig.Core.Session;
using TestRig.Tests.Session.Fakes;
using Xunit;

namespace TestRig.Tests.Session;

/// <summary>
/// Path resolution. Ported from rig-lock.tests.ps1 section 1 and rig-reset.tests.ps1
/// section paths.
/// </summary>
public sealed class RigPathsTests
{
    [Fact]
    public void EveryStateFileHangsOffTheRigHome()
    {
        var paths = new RigPaths(@"C:\rig\TestRig");

        Assert.Equal(@"C:\rig\TestRig\session.lock", paths.LockFile);
        Assert.Equal(@"C:\rig\TestRig\session.dirty", paths.DirtyFile);
        Assert.Equal(@"C:\rig\TestRig\session.state.json", paths.SessionStateFile);
        Assert.Equal(@"C:\rig\TestRig\CLAUDE.md", paths.RulesPath);
        Assert.Equal(@"C:\rig\TestRig\baseline", paths.BaselineDir);
        Assert.Equal(@"C:\rig\TestRig\baseline\manifest.json", paths.BaselineManifest);
        Assert.Equal(@"C:\rig\TestRig\baseline\content", paths.BaselineStore);
    }

    [Fact]
    public void BothHalvesHangOffTheRigHomeToo()
    {
        var paths = new RigPaths(@"C:\rig\TestRig");

        Assert.Equal(@"C:\rig\TestRig\DedicatedServer\install", paths.DediInstall);
        Assert.Equal(@"C:\rig\TestRig\DedicatedServer\data", paths.DediData);
        Assert.Equal(@"C:\rig\TestRig\DedicatedServer\data\saves", paths.ServerSaveRoot);
        Assert.Equal(@"C:\rig\TestRig\DedicatedServer\data\server.pid", paths.ServerPidFile);
        Assert.Equal(@"C:\rig\TestRig\DedicatedServer\data\server.log", paths.ServerLog);
        Assert.Equal(@"C:\rig\TestRig\DedicatedServer\data\host.pid", paths.HostPidFile);
        Assert.Equal(@"C:\rig\TestRig\DedicatedServer\data\control.cmd", paths.ControlCmdFile);
        Assert.Equal(@"C:\rig\TestRig\DedicatedServer\data\setting.xml", paths.ServerSettingXml);
        Assert.Equal(@"C:\rig\TestRig\ClientRig\data", paths.ClientDataDir);
        Assert.Equal(@"C:\rig\TestRig\ClientRig\data\rig.json", paths.ClientRegistryFile);
    }

    [Fact]
    public void ThereIsExactlyOneServerSaveRoot()
    {
        // The reset planner's existence guard once read its own copy of this path while the
        // enumeration and the delete read the lock library's copy. They agreed only because
        // one library re-pointed the other; a caller that initialised them independently
        // would have checked one tree and deleted another.
        var paths = new RigPaths(@"C:\rig\TestRig");

        Assert.Equal(Path.Combine(paths.DediData, "saves"), paths.ServerSaveRoot);
    }

    [Fact]
    public void TheInstancesRootDefaultsUnderTheRigButIsNormallyElsewhere()
    {
        Assert.Equal(@"C:\rig\TestRig\ClientRig\instances", new RigPaths(@"C:\rig\TestRig").InstanceRoot);
        Assert.Equal(@"D:\elsewhere", new RigPaths(@"C:\rig\TestRig", @"D:\elsewhere").InstanceRoot);
    }

    [Fact]
    public void PerInstancePathsAreDerivedFromTheInstanceName()
    {
        var paths = new RigPaths(@"C:\rig\TestRig", @"D:\instances");

        Assert.Equal(@"C:\rig\TestRig\ClientRig\data\c1", paths.InstanceDataDir("c1"));
        Assert.Equal(@"C:\rig\TestRig\ClientRig\data\c1\userdata", paths.InstanceUserData("c1"));
        Assert.Equal(@"C:\rig\TestRig\ClientRig\data\c1\userdata\saves", paths.InstanceSaveRoot("c1"));
        Assert.Equal(@"C:\rig\TestRig\ClientRig\data\c1\game.pid", paths.InstancePidFile("c1"));
        Assert.Equal(@"C:\rig\TestRig\ClientRig\data\c1\instance.json", paths.InstanceManifest("c1"));
        Assert.Equal(@"C:\rig\TestRig\ClientRig\data\c1\logs", paths.InstanceLogDir("c1"));
        Assert.Equal(@"D:\instances\c1", paths.DefaultInstanceTree("c1"));
    }

    [Fact]
    public void TheProcessImagesAreConfigurableAndDefaultToTheRealOnes()
    {
        var defaults = new RigPaths(@"C:\rig\TestRig");
        Assert.Equal("rocketstation_DedicatedServer", defaults.ServerImage);
        Assert.Equal("rocketstation", defaults.ClientImage);
        Assert.Equal(["pwsh", "powershell"], defaults.HostWrapperImages);

        var custom = new RigPaths(@"C:\rig\TestRig", serverImage: "srv", clientImage: "cli",
            hostWrapperImages: ["wrapper"]);
        Assert.Equal("srv", custom.ServerImage);
        Assert.Equal("cli", custom.ClientImage);
        Assert.Equal(["wrapper"], custom.HostWrapperImages);
    }

    [Fact]
    public void AnEmptySourceInstallOrUserDataIsNullRatherThanEmpty()
    {
        var paths = new RigPaths(@"C:\rig\TestRig", sourceInstall: "   ", userDataDir: "");

        Assert.Null(paths.SourceInstall);
        Assert.Null(paths.UserDataDir);
    }

    [Fact]
    public void ARigHomeIsMandatory()
    {
        Assert.Throws<ArgumentException>(() => new RigPaths(""));
        Assert.Throws<ArgumentException>(() => new RigPaths("   "));
    }

    [Fact]
    public void TheFixtureNeverPointsAtTheRealRig()
    {
        // The PowerShell suites had to assert that their redirection took, because a mistake
        // would have driven the developer's actual rig. Nothing here can reach a disk at all,
        // so this is a statement of the arrangement rather than a guard.
        var rig = new RigFixture();

        Assert.StartsWith(@"C:\rigtest\", rig.Paths.RigHome, StringComparison.OrdinalIgnoreCase);
        Assert.IsType<FakeFileSystem>(rig.Fs);
        Assert.DoesNotContain("StationeersPlus", rig.Paths.RigHome, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TheSessionStateStoreCarriesTheResetStampAndItsValues()
    {
        var rig = new RigFixture();

        Assert.Null(rig.State.ReadLastResetUtc());
        Assert.Empty(rig.State.ReadValues());

        rig.State.Save("2026-08-14T12:00:00Z", new Dictionary<string, string> { ["cookieWorlds"] = "3" });

        Assert.Equal("2026-08-14T12:00:00Z", rig.State.ReadLastResetUtc());
        Assert.Equal("3", rig.State.ReadValues()["cookieWorlds"]);
    }

    [Fact]
    public void ABrokenSessionStateFileDegradesRatherThanThrowing()
    {
        var rig = new RigFixture();
        rig.Fs.AddFile(rig.Paths.SessionStateFile, "{ not json");

        Assert.Null(rig.State.ReadLastResetUtc());
        Assert.Empty(rig.State.ReadValues());
    }

    [Fact]
    public void SavingWithoutValuesPreservesTheOnesAlreadyOnDisk()
    {
        var rig = new RigFixture();
        rig.State.Save("2026-08-01T00:00:00Z", new Dictionary<string, string> { ["blueprints"] = "12" });

        rig.State.Save("2026-08-14T12:00:00Z");

        Assert.Equal("12", rig.State.ReadValues()["blueprints"]);
        Assert.Equal("2026-08-14T12:00:00Z", rig.State.ReadLastResetUtc());
    }
}
