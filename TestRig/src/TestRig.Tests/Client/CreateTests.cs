using TestRig.Core.Client;
using TestRig.Core.Rig;
using TestRig.Core.Session;
using TestRig.Tests.Session.Fakes;
using Xunit;

namespace TestRig.Tests.Client;

/// <summary>
/// Building an instance: identity allocation, the tree, the redirect, the mod seed and the
/// manifests.
/// </summary>
public sealed class CreateTests
{
    // ---- the gate and the shape of the command ----------------------------

    [Fact]
    public void CreateIsGatedOnTheSessionLock()
    {
        var fixture = new ClientFixture();
        var ex = Assert.Throws<RigRefusalException>(() => fixture.Create("client1", owner: null!));
        Assert.Equal(RigRefusalKind.NoLockHeld, ex.Kind);
        Assert.Empty(fixture.Registry.Read());
    }

    [Fact]
    public void ACommaSeparatedTargetIsRefusedBeforeAnythingHappens()
    {
        var fixture = new ClientFixture();
        var owner = fixture.Lease();

        var ex = Assert.Throws<RigRefusalException>(() => fixture.Create("a,b", owner));
        Assert.Contains("one instance at a time", ex.Message, StringComparison.Ordinal);
    }

    // ---- identity allocation (CLIENT-047, CLIENT-052 to CLIENT-059) --------

    [Fact]
    public void ThreeFlaglessCreatesProduceThreeNonCollidingInstances()
    {
        var fixture = new ClientFixture();
        var owner = fixture.Lease();

        var a = fixture.Create("a", owner);
        var b = fixture.Create("b", owner);
        var c = fixture.Create("c", owner);

        Assert.Equal([1, 2, 3], new[] { a.Index, b.Index, c.Index });
        Assert.Equal([27701, 27702, 27703], new[] { a.Port, b.Port, c.Port });
        Assert.Equal([27801, 27802, 27803], new[] { a.GamePortOr(0), b.GamePortOr(0), c.GamePortOr(0) });
        Assert.Equal(3, new[] { a.ClientIdOr(), b.ClientIdOr(), c.ClientIdOr() }.Distinct().Count());
    }

    [Fact]
    public void ARemovedIndexIsReusedByTheNextCreate()
    {
        var fixture = new ClientFixture();
        var owner = fixture.Lease();

        fixture.Create("a", owner);
        fixture.Create("b", owner);
        fixture.Remove("a", owner);

        var reused = fixture.Create("c", owner);
        Assert.Equal(1, reused.Index);
    }

    [Fact]
    public void RemoveSaysOutLoudThatTheFreedIndexRecyclesTheWholeIdentity()
    {
        // CLIENT-231: anything still referring to the old instance, a saved world or a joiner's
        // cached address, is inherited by whatever takes the index next.
        var fixture = new ClientFixture();
        var owner = fixture.Lease();
        fixture.Create("a", owner);

        fixture.Output.Clear();
        fixture.Remove("a", owner);

        Assert.True(fixture.Output.Said("Index 1 is free again"));
        Assert.True(fixture.Output.Said("27701"));
        Assert.True(fixture.Output.Said("900000000001"));
    }

    [Fact]
    public void ARebuildKeepsThePortTheClientIdAndTheUsernameAsWellAsTheRoleAndGamePort()
    {
        // CLIENT-052 to CLIENT-054 and CLIENT-306. The PowerShell kept only the role and the
        // game port, so create --force, and therefore update-game, silently reset the control
        // port, the ClientId and the username. The server keys a player's body on that id.
        var fixture = new ClientFixture();
        var owner = fixture.Lease();

        var original = fixture.CreateWith(new CreateOptions
        {
            Instance = "host1",
            CallerId = owner,
            Role = "host",
            Port = 31000,
            GamePort = 32000,
            ClientId = "123456789012",
            Username = "Ada",
        });

        var rebuilt = fixture.CreateWith(new CreateOptions
        {
            Instance = "host1",
            CallerId = owner,
            Force = true,
        });

        Assert.Equal(original.Port, rebuilt.Port);
        Assert.Equal(original.ClientIdOr(), rebuilt.ClientIdOr());
        Assert.Equal(original.UsernameOr("x"), rebuilt.UsernameOr("y"));
        Assert.Equal("host", rebuilt.RoleOr());
        Assert.Equal(32000, rebuilt.GamePortOr(0));
        Assert.Equal(original.Index, rebuilt.Index);
    }

    [Fact]
    public void ATypedValueStillWinsOverTheRecordedOneOnARebuild()
    {
        var fixture = new ClientFixture();
        var owner = fixture.Lease();

        fixture.CreateWith(new CreateOptions { Instance = "x", CallerId = owner, Role = "host" });

        var demoted = fixture.CreateWith(new CreateOptions
        {
            Instance = "x",
            CallerId = owner,
            Force = true,
            Role = "client",
        });

        Assert.Equal("client", demoted.RoleOr());
    }

    [Fact]
    public void ANonNumericClientIdAndZeroAreBothRefusedWithTheirOwnReasons()
    {
        var fixture = new ClientFixture();
        var owner = fixture.Lease();

        Assert.Contains("decimal ulong", Assert.Throws<RigRefusalException>(() =>
            fixture.CreateWith(new CreateOptions { Instance = "x", CallerId = owner, ClientId = "abc" })).Message, StringComparison.Ordinal);

        Assert.Contains("batch-mode sentinel", Assert.Throws<RigRefusalException>(() =>
            fixture.CreateWith(new CreateOptions { Instance = "x", CallerId = owner, ClientId = "0" })).Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ADuplicateClientIdIsRefusedAndTheMessageSaysWhyItMatters()
    {
        var fixture = new ClientFixture();
        var owner = fixture.Lease();
        fixture.CreateWith(new CreateOptions { Instance = "a", CallerId = owner, ClientId = "555" });

        var ex = Assert.Throws<RigRefusalException>(() =>
            fixture.CreateWith(new CreateOptions { Instance = "b", CallerId = owner, ClientId = "555" }));

        Assert.Contains("keys a player's body", ex.Message, StringComparison.Ordinal);
        Assert.Contains("'a'", ex.Message, StringComparison.Ordinal);
    }

    // ---- refusals before any side effect ----------------------------------

    [Fact]
    public void AnExistingTreeWithoutForceRefusesAndNamesBothWaysOut()
    {
        var fixture = new ClientFixture();
        var owner = fixture.Lease();
        fixture.Create("x", owner);

        var ex = Assert.Throws<RigRefusalException>(() => fixture.Create("x", owner));
        Assert.Contains("--force", ex.Message, StringComparison.Ordinal);
        Assert.Contains("testrig remove --target x", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ARunningInstanceIsRefusedEvenWithForce()
    {
        var fixture = new ClientFixture();
        var owner = fixture.Lease();
        fixture.Create("x", owner);

        var paths = fixture.Layout.PathsFor("x");
        fixture.Processes.Add(7777, "rocketstation", fixture.Clock.UtcNow);
        PidFiles.Write(fixture.Fs, paths.PidFile, 7777, fixture.Clock.UtcNow);

        var ex = Assert.Throws<RigRefusalException>(() =>
            fixture.CreateWith(new CreateOptions { Instance = "x", CallerId = owner, Force = true }));

        Assert.Contains("is running", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AVolumeMismatchIsRefusedWithBothRootsAndBothRemedies()
    {
        var fixture = new ClientFixture(typedInstancesRoot: @"Z:\elsewhere");
        var owner = fixture.Lease();

        var ex = Assert.Throws<RigRefusalException>(() => fixture.Create("x", owner));
        Assert.Contains("hard links cannot", ex.Message, StringComparison.Ordinal);
        Assert.Contains("STATIONEERS_CLIENTRIG_ROOT", ex.Message, StringComparison.Ordinal);
        Assert.Contains("--instances-root", ex.Message, StringComparison.Ordinal);
        Assert.Contains("DEV.md", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ThePortGuardsRunBeforeTheVolumeCheckSoOneThingIsFixedAtATime()
    {
        // CLIENT-062: a name clash is reported before a volume misconfiguration.
        var fixture = new ClientFixture(typedInstancesRoot: @"Z:\elsewhere");
        var owner = fixture.Lease();

        var ex = Assert.Throws<RigRefusalException>(() =>
            fixture.CreateWith(new CreateOptions { Instance = "x", CallerId = owner, ClientId = "0" }));

        Assert.Contains("batch-mode sentinel", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void NothingIsWrittenToTheRegistryWhenAGuardRefuses()
    {
        var fixture = new ClientFixture();
        var owner = fixture.Lease();

        Assert.Throws<RigRefusalException>(() =>
            fixture.CreateWith(new CreateOptions { Instance = "x", CallerId = owner, GamePort = 28016 }));

        Assert.Empty(fixture.Registry.Read());
    }

    // ---- the read-modify-write is serialised (CLIENT-045) -----------------

    [Fact]
    public void TheRegistryReadModifyWriteRunsInsideTheCriticalSection()
    {
        // The PowerShell's comment claimed the session lock covered this, but a lock assertion
        // is a point-in-time check: two concurrent creates from the SAME session both passed
        // it, both picked the same free index, and the second write won.
        var fixture = new ClientFixture();
        var owner = fixture.Lease();

        var before = fixture.RegistryMutex.Entered;
        fixture.Create("x", owner);

        Assert.True(fixture.RegistryMutex.Entered > before);
        Assert.Equal(1, fixture.RegistryMutex.MaxConcurrentHolders);
    }

    // ---- the tree ----------------------------------------------------------

    [Fact]
    public void TheGameDataIsLinkedAndBepInExIsARealCopy()
    {
        var fixture = new ClientFixture();
        var owner = fixture.Lease();
        fixture.Create("x", owner);

        var paths = fixture.Layout.PathsFor("x");
        Assert.True(fixture.Fs.FileExists(Path.Combine(paths.Tree, "rocketstation_Data", "Managed", "Assembly-CSharp.dll")));
        Assert.True(fixture.Fs.FileExists(Path.Combine(paths.Tree, "MonoBleedingEdge", "EmbedRuntime", "mono.dll")));
        Assert.True(fixture.Fs.FileExists(Path.Combine(paths.BepInEx, "core", "BepInEx.dll")));
        Assert.True(fixture.Fs.FileExists(Path.Combine(paths.BepInEx, "config", "stationeers.launchpad.cfg")));
    }

    [Fact]
    public void TheDevelopersLogsCacheAndInspectorFoldersAreNotCarriedIntoAFreshInstance()
    {
        var fixture = new ClientFixture();
        var owner = fixture.Lease();
        fixture.Create("x", owner);

        var paths = fixture.Layout.PathsFor("x");
        Assert.False(fixture.Fs.FileExists(Path.Combine(paths.BepInEx, "LogOutput.log")));
        Assert.False(fixture.Fs.FileExists(Path.Combine(paths.BepInEx, "cache", "stale.dat")));
        Assert.True(fixture.Fs.DirectoryExists(Path.Combine(paths.BepInEx, "cache")));
    }

    [Fact]
    public void TheTwoRootFilesTheGameWritesToAreRealCopiesAndTheSkipListIsNotCarried()
    {
        var fixture = new ClientFixture();
        var owner = fixture.Lease();
        fixture.Create("x", owner);

        var paths = fixture.Layout.PathsFor("x");
        Assert.True(fixture.Fs.FileExists(Path.Combine(paths.Tree, "doorstop_config.ini")));
        Assert.True(fixture.Fs.FileExists(Path.Combine(paths.Tree, "winhttp.dll")));
        Assert.False(fixture.Fs.FileExists(Path.Combine(paths.Tree, "imgui.ini")));
    }

    [Fact]
    public void ARebuildRemovesTheOldTreeButKeepsTheDataDirectory()
    {
        var fixture = new ClientFixture();
        var owner = fixture.Lease();
        fixture.Create("x", owner);

        var paths = fixture.Layout.PathsFor("x");
        fixture.Fs.AddFile(Path.Combine(paths.Tree, "stale-from-last-time.txt"), "x");
        fixture.Fs.AddFile(Path.Combine(paths.UserData, "saves", "Luna", "Luna.save"), "world");

        fixture.CreateWith(new CreateOptions { Instance = "x", CallerId = owner, Force = true });

        Assert.False(fixture.Fs.FileExists(Path.Combine(paths.Tree, "stale-from-last-time.txt")));
        // A staged save must not evaporate on a plugin rebuild.
        Assert.True(fixture.Fs.FileExists(Path.Combine(paths.UserData, "saves", "Luna", "Luna.save")));
    }

    // ---- the save-path redirect (CLIENT-075, CLIENT-076) ------------------

    [Fact]
    public void TheRedirectIsWrittenUnconditionallyAndPointsAtTheInstancesOwnSaveRoot()
    {
        var fixture = new ClientFixture();
        var owner = fixture.Lease();
        fixture.Create("x", owner, seedMods: false);

        var paths = fixture.Layout.PathsFor("x");
        Assert.Equal(paths.UserData, SavePathOverride.Read(fixture.Fs, paths.BepInEx));
    }

    [Fact]
    public void AHostWithNoLaunchPadConfigThrowsAndTheInstanceIsSTILLREGISTERED()
    {
        // CLIENT-076 fixed. The PowerShell threw AFTER the tree was built and BEFORE the entry
        // was written, so the tree existed with no registry entry and all three remedies its
        // own message named were unreachable.
        var fixture = new ClientFixture();
        var owner = fixture.Lease();
        fixture.Fs.DeleteFile(Path.Combine(RigFixture.SourceInstall, "BepInEx", "config", "stationeers.launchpad.cfg"));

        var ex = Assert.Throws<RigRefusalException>(() =>
            fixture.CreateWith(new CreateOptions { Instance = "host1", CallerId = owner, Role = "host" }));

        Assert.Contains("IS registered", ex.Message, StringComparison.Ordinal);
        Assert.Contains("testrig start --target host1", ex.Message, StringComparison.Ordinal);

        var entry = fixture.Registry.Find("host1");
        Assert.NotNull(entry);
        Assert.True(fixture.Fs.FileExists(fixture.Layout.PathsFor("host1").Exe));
    }

    [Fact]
    public void AClientWithNoLaunchPadConfigWarnsAndKeepsGoing()
    {
        var fixture = new ClientFixture();
        var owner = fixture.Lease();
        fixture.Fs.DeleteFile(Path.Combine(RigFixture.SourceInstall, "BepInEx", "config", "stationeers.launchpad.cfg"));

        fixture.Create("joiner", owner, role: "client");

        Assert.True(fixture.Output.Warned("NO separate save root"));
        Assert.NotNull(fixture.Registry.Find("joiner"));
    }

    // ---- the mod seed (CLIENT-088 to CLIENT-093) --------------------------

    [Fact]
    public void TheSeedRebasesLocalPathsOntoTheInstancesOwnCopyAndKeepsWorkshopEntries()
    {
        var fixture = new ClientFixture();
        var owner = fixture.Lease();
        fixture.Create("x", owner, seedMods: true);

        var paths = fixture.Layout.PathsFor("x");
        var entries = ModConfig.Read(fixture.Fs, paths.ModConfig);

        var local = Assert.Single(entries, e => e.Kind == "Local");
        Assert.StartsWith(paths.ModsDir, local.Path, StringComparison.OrdinalIgnoreCase);

        var workshop = Assert.Single(entries, e => e.Kind == "Workshop");
        Assert.Equal(@"C:\workshop\2345", workshop.Path);
        Assert.Equal("2345", workshop.WorkshopId);
    }

    [Fact]
    public void TheSeedCopiesTheTwoNonModFilesThatChangeWhatAClientLooksLike()
    {
        // Neither is mod configuration, and dropping them changes what a driven client looks
        // like to the server.
        var fixture = new ClientFixture();
        var owner = fixture.Lease();
        fixture.Create("x", owner, seedMods: true);

        var paths = fixture.Layout.PathsFor("x");
        Assert.True(fixture.Fs.FileExists(Path.Combine(paths.UserData, "modrepos.xml")));
    }

    [Fact]
    public void TheSeedWarnsThatItRemovedWhatTheRepositoryHadDeployed()
    {
        // CLIENT-090. The PowerShell's update-mods detected this; create --force and
        // update-game reached the same code with NO detection, so an instance under test
        // silently reverted to the developer's own mod set.
        var fixture = new ClientFixture();
        var owner = fixture.Lease();
        fixture.AddRepositoryMod("SprayPaintPlus");
        fixture.Create("x", owner, seedMods: true);

        var paths = fixture.Layout.PathsFor("x");
        fixture.Fs.AddFile(Path.Combine(paths.ModsDir, "Local_SprayPaintPlus", "SprayPaintPlus.dll"), "deployed");

        fixture.Output.Clear();
        fixture.CreateWith(new CreateOptions { Instance = "x", CallerId = owner, Force = true, SeedMods = true });

        Assert.True(fixture.Output.Warned("Local_SprayPaintPlus"));
        Assert.True(fixture.Output.Warned("Re-deploy them"));
    }

    [Fact]
    public void NoDeveloperModConfigWarnsAndSkipsTheSeedWithoutFailingTheCreate()
    {
        var fixture = new ClientFixture();
        var owner = fixture.Lease();
        fixture.Fs.DeleteFile(Path.Combine(RigFixture.UserData, "modconfig.xml"));

        fixture.Create("x", owner, seedMods: true);

        Assert.True(fixture.Output.Warned("Workshop mods only"));
        Assert.NotNull(fixture.Registry.Find("x"));
    }

    // ---- manifests and the stamp ------------------------------------------

    [Fact]
    public void EveryManifestCarriesTheWholeRigsPortList()
    {
        var fixture = new ClientFixture();
        var owner = fixture.Lease();
        fixture.Create("a", owner);
        fixture.Create("b", owner);

        foreach (var name in new[] { "a", "b" })
        {
            var manifest = System.Text.Json.JsonSerializer.Deserialize(
                fixture.Fs.ReadAllText(fixture.Layout.PathsFor(name).Manifest),
                ClientJsonContext.Default.InstanceManifest);

            Assert.NotNull(manifest);
            Assert.Equal([27701, 27702], manifest!.PeerPorts);
        }
    }

    [Fact]
    public void TheManifestCarriesTheWindowAndInputBlocksAndTheInstancesOwnSaveRoot()
    {
        var fixture = new ClientFixture();
        var owner = fixture.Lease();
        fixture.CreateWith(new CreateOptions
        {
            Instance = "x", CallerId = owner, Role = "host", Width = 1280, Height = 720,
        });

        var paths = fixture.Layout.PathsFor("x");
        var manifest = System.Text.Json.JsonSerializer.Deserialize(
            fixture.Fs.ReadAllText(paths.Manifest), ClientJsonContext.Default.InstanceManifest)!;

        Assert.Equal("host", manifest.Role);
        Assert.Equal(1280, manifest.Window.Width);
        Assert.Equal(720, manifest.Window.Height);
        Assert.True(manifest.Window.ForceWindowed);
        Assert.True(manifest.GameplayInput.Force);
        Assert.False(manifest.GameplayInput.Everywhere);
        Assert.Equal(paths.UserData, manifest.SavePath);
        Assert.Equal(RigConstants.DefaultDesktop, manifest.Desktop);
        // gamePort is load-bearing, because POST /host binds it.
        Assert.Equal(27801, manifest.GamePort);
    }

    [Fact]
    public void TheProvisionStampRecordsTheGameVersionAndTheMachineItWasBuiltOn()
    {
        var fixture = new ClientFixture();
        var owner = fixture.Lease();
        fixture.Create("x", owner);

        var stamp = System.Text.Json.JsonSerializer.Deserialize(
            fixture.Fs.ReadAllText(fixture.Layout.PathsFor("x").Stamp), ClientJsonContext.Default.ProvisionStamp)!;

        Assert.Equal("x", stamp.InstanceName);
        Assert.Equal("0.2.6428.27798", stamp.SourceVersion);
        Assert.Equal(RigFixture.SourceInstall, stamp.SourceInstall);
        // Read by nothing today, and the only field that would identify a tree built on a
        // different machine.
        Assert.Equal("RIGTEST", stamp.LauncherHostname);
    }

    // ---- the control plugin ------------------------------------------------

    [Fact]
    public void TheWholePluginOutputFolderIsDeployedAndNotJustTheOneFile()
    {
        // CLIENT-086. The PowerShell copied exactly one file, so the moment the plugin gained a
        // reference every instance would silently run without a control plane, and the warning
        // only fires when the DLL ITSELF is missing.
        var fixture = new ClientFixture();
        var owner = fixture.Lease();

        var buildDir = Path.GetDirectoryName(fixture.Layout.PluginDll)!;
        fixture.Fs.AddFile(fixture.Layout.PluginDll, "plugin");
        fixture.Fs.AddFile(Path.Combine(buildDir, "Newtonsoft.Json.dll"), "dependency");
        fixture.Fs.AddFile(Path.Combine(buildDir, "ClientDriver.pdb"), "symbols");

        fixture.Create("x", owner);

        var destination = Path.Combine(fixture.Layout.PathsFor("x").BepInEx, "plugins", "ClientDriver");
        Assert.True(fixture.Fs.FileExists(Path.Combine(destination, "ClientDriver.dll")));
        Assert.True(fixture.Fs.FileExists(Path.Combine(destination, "Newtonsoft.Json.dll")));
        Assert.True(fixture.Fs.FileExists(Path.Combine(destination, "ClientDriver.pdb")));
    }

    [Fact]
    public void AMissingPluginWarnsWithTheBuildCommandAndDoesNotStopTheCreate()
    {
        var fixture = new ClientFixture();
        var owner = fixture.Lease();
        fixture.Create("x", owner);

        Assert.True(fixture.Output.Warned("dotnet build"));
        Assert.True(fixture.Output.Warned("without a control plane"));
        Assert.NotNull(fixture.Registry.Find("x"));
    }

    // ---- the summary -------------------------------------------------------

    [Fact]
    public void TheSummaryReportsLinkedAndCopiedCountsAndTheDeployedPluginIsInTheCopiedTotal()
    {
        // CLIENT-072: the PowerShell measured the BepInEx copy BEFORE deploying into it, so
        // the plugin was missing from the total.
        var fixture = new ClientFixture();
        var owner = fixture.Lease();
        fixture.Fs.AddFile(fixture.Layout.PluginDll, "plugin");

        fixture.Create("x", owner);

        var linked = int.Parse(fixture.Output.ValueOf("linkedFiles")!, System.Globalization.CultureInfo.InvariantCulture);
        var withPlugin = int.Parse(fixture.Output.ValueOf("copiedFiles")!, System.Globalization.CultureInfo.InvariantCulture);

        Assert.True(linked > 0);
        Assert.True(fixture.Fs.FileExists(
            Path.Combine(fixture.Layout.PathsFor("x").BepInEx, "plugins", "ClientDriver", "ClientDriver.dll")));

        // The same rig with no plugin to deploy. The difference between the two totals is the
        // plugin itself, which is only true when the count is taken AFTER the deploy: the
        // PowerShell measured the BepInEx copy first, so the two would have been identical.
        var bare = new ClientFixture();
        var bareOwner = bare.Lease();
        bare.Create("x", bareOwner);
        var withoutPlugin = int.Parse(bare.Output.ValueOf("copiedFiles")!, System.Globalization.CultureInfo.InvariantCulture);

        Assert.Equal(withoutPlugin + 1, withPlugin);
    }

    [Fact]
    public void AHostGetsTheHostingSequenceAndAClientGetsTheStartCommand()
    {
        var fixture = new ClientFixture();
        var owner = fixture.Lease();

        fixture.CreateWith(new CreateOptions { Instance = "h", CallerId = owner, Role = "host" });
        Assert.True(fixture.Output.Said("must be in its world BEFORE any joiner connects"));
        Assert.True(fixture.Output.Said("127.0.0.1:27801"));

        fixture.Output.Clear();
        fixture.CreateWith(new CreateOptions { Instance = "c", CallerId = owner });
        Assert.True(fixture.Output.Said("testrig start --target c"));
        Assert.False(fixture.Output.Said("BEFORE any joiner"));
    }

    // ---- the instances root ------------------------------------------------

    [Fact]
    public void TheResolvedRootIsRecordedSoLaterCommandsNeedNoFlag()
    {
        var fixture = new ClientFixture();
        var owner = fixture.Lease();
        var entry = fixture.Create("x", owner);

        Assert.Equal(ClientFixture.InstancesRoot, entry.RecordedRoot);

        // A fresh layout with no typed flag still finds the tree.
        var reader = new ClientLayout(fixture.Fs, fixture.Env, fixture.Rig.Paths, fixture.Output, fixture.Registry);
        Assert.Equal(ClientFixture.InstancesRoot, reader.PathsFor("x").Root);
        Assert.Contains("recorded in the registry", reader.PathsFor("x").RootSource, StringComparison.Ordinal);
    }

    [Fact]
    public void AnEntryWithNoRecordedRootGetsOneNoticeAndOnlyOne()
    {
        // CLIENT-028: the PowerShell initialised the suppression map twice, so calling the
        // initialiser again in one process reset it and a four-instance command could print
        // eight notices.
        var fixture = new ClientFixture();
        fixture.Fs.AddFile(fixture.Rig.Paths.ClientRegistryFile, """[{"instanceName":"old","index":1,"port":27701}]""");

        var layout = new ClientLayout(fixture.Fs, fixture.Env, fixture.Rig.Paths, fixture.Output, fixture.Registry);
        for (var i = 0; i < 5; i++) layout.PathsFor("old");

        Assert.Single(fixture.Output.Lines, l => l.Text.Contains("provisioned before the instances root", StringComparison.Ordinal));
    }

    [Fact]
    public void MovingATreeWarnsThatTheOldOneIsNotDeleted()
    {
        var fixture = new ClientFixture();
        var owner = fixture.Lease();
        fixture.Create("x", owner);

        var moved = new ClientFixture();
        // Same in-memory rig, a different layout that has the flag typed.
        var layout = new ClientLayout(fixture.Fs, fixture.Env, fixture.Rig.Paths, fixture.Output, fixture.Registry,
            typedInstancesRoot: @"D:\other-root");
        Assert.True(layout.InstancesRootTyped);
        Assert.Equal(@"D:\other-root", layout.PathsFor("x").Root);
        Assert.NotNull(moved);
    }

    [Fact]
    public void EveryDistinctRecordedRootIsVisibleSoASplitRigCanBeScannedProperly()
    {
        // CLIENT-007: the shared session libraries take ONE root, so a rig split across two
        // roots had its orphan scan watching only the first.
        var fixture = new ClientFixture();
        fixture.Fs.AddFile(fixture.Rig.Paths.ClientRegistryFile,
            """
            [{"instanceName":"a","index":1,"port":27701,"instancesRoot":"D:\\one"},
             {"instanceName":"b","index":2,"port":27702,"instancesRoot":"E:\\two"},
             {"instanceName":"c","index":3,"port":27703,"instancesRoot":"D:\\one"}]
            """);

        Assert.Equal([@"D:\one", @"E:\two"], fixture.Layout.RecordedRoots());
        Assert.Equal(@"D:\one", fixture.Layout.LibraryInstanceRoot());
    }
}
