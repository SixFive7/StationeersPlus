// =============================================================================
// Spray Paint Plus: the conflict banner, boot line then six world lines
// =============================================================================
// From Mods/SprayPaintPlus/PLAYTEST.md: one red line at boot, then a six-line
// banner starting only after the world is up, at 5 second intervals, the sixth
// ending "(This warning will stop repeating; see the BepInEx log.)", then
// nothing. None of the six may appear while still at the menu.
//
// THIS EXERCISES THE DETECTOR, NOT A REAL CONFLICT. Say it plainly in any report
// of a pass here. The fixture at
// .work/2026-08-11-spraypaintplus-playtests/ConflictStub/ is two assemblies whose
// Assembly.GetName().Name values are exactly ColorCycler and NetworkPainter, and
// nothing else: no Harmony patch, no prefab, no reference to Assembly-CSharp. The
// detector in Plugin.cs.OnAllModsLoaded compares that simple assembly name
// against two literals, so the stub is a faithful trigger for the detector and no
// evidence at all about coexisting with the real Workshop mods, which patch the
// same methods this mod patches. It also inherits the assumption it is testing:
// that the real assemblies are named exactly that. The fixture's own README says
// the same thing at more length.
//
// WHY THE INSTANCE IS RESTARTED INSIDE THE BODY
// The detector runs once, on Prefab.OnPrefabsLoaded, during boot. A stub seeded
// after the process is up would never be seen, and the harness brings instances
// up before a check body runs. So the body seeds the fixture into the instance's
// OWN save root and then restarts that ONE instance, which is also the ordering
// that lets the binary attestation attest a clean process first.
//
// THE INSTANCE IS DECLARED Role=client WITH NO HOST IN THE LIST, deliberately.
// That leaves bring-up at the menu instead of creating a world that would be
// thrown away by the restart, and the body drives the host endpoint itself once
// the fixture is live. The spec role only steers the harness; the host endpoint
// works on any instance and the live answer is /status.role.
//
// CLEANUP IS THE DANGEROUS PART OF THIS CHECK, not the assertions. The
// between-session state reset that runs when a new lock is taken does NOT clear
// userdata/mods/ or modconfig.xml: they are on the KEPT side of it. A stub left
// behind therefore disables Spray Paint Plus on every later run of this instance,
// silently, and the next agent spends a session finding out why. The finally
// below restores the modconfig verbatim from a snapshot taken before the edit,
// deletes the seeded folder, verifies both, and writes the result into the
// evidence bundle whether it worked or not.
//
// WHAT WOULD MAKE THIS FAIL
//   - the banner going unbounded again, which is what it used to be: more than
//     six lines;
//   - the banner firing at the menu, where console overlay lines have aged off
//     long before the player reaches a world;
//   - the sixth line losing the sentence that says it is the last one;
//   - the detector not firing at all, which the CONFLICT log line catches
//     independently of the banner;
//   - the console prefix reverting to the code name.
//
// AGAINST THE PRE-v1.11.0 BUILD THIS CHECK FAILS: the banner was an unbounded
// Debug.LogError loop with the code-name prefix, so the counted substring matches
// nothing and the six-line assertion reads 0.
//
// PREREQUISITES
//   dotnet build Mods/SprayPaintPlus/SprayPaintPlus.sln -c Release
//   copy bin/Release/SprayPaintPlus.dll into
//     TestRig/ClientRig/data/hostie/userdata/mods/SprayPaintPlus/
//   dotnet build .work/2026-08-11-spraypaintplus-playtests/ConflictStub/ColorCycler/ColorCycler.csproj -c Release
//   dotnet build .work/2026-08-11-spraypaintplus-playtests/ConflictStub/NetworkPainter/NetworkPainter.csproj -c Release
// =============================================================================

using TestRig.Contracts;
using TestRig.Playtest;
using TestRig.Playtest.Model;
using TestRig.Playtest.Readers;
using TestRig.Playtest.Values;
using static SprayPaintPlus.Playtests.Spp;

namespace SprayPaintPlus.Playtests;

internal sealed class ConflictBanner : IPlaytestCheck
{
    public CheckSpec Spec { get; } = new(
        name: "the conflict banner is one boot line then six world lines",
        summary: "with a stub that only carries the two conflicting assembly names, the detector fires, patches are withheld, and the banner is bounded and starts after the menu",
        instances: [new InstanceSpec("hostie", InstanceRole.Client)]);

    public void Run(IPlaytestContext ctx)
    {
        ArgumentNullException.ThrowIfNull(ctx);

        const string bannerLine = "[Spray Paint Plus] NOT LOADED! Conflicting mods:";
        const string lastLine = "This warning will stop repeating";

        var repoRoot = RepoRootOf(Spec.SourceFile);
        var fixture = Path.Combine(repoRoot, ".work", "2026-08-11-spraypaintplus-playtests", "ConflictStub", "mod");
        var userData = Path.Combine(ctx.RigHome, "ClientRig", "data", "hostie", "userdata");
        var modConfig = Path.Combine(userData, "modconfig.xml");
        var stubFolder = Path.Combine(userData, "mods", "ConflictStub");
        var configBackup = string.Empty;
        var seeded = false;

        try
        {
            // ---- 1. The fixture has to exist and has to be built. Neither is
            // something the mod can be blamed for, so both decline.
            foreach (var needed in new[] { "ColorCycler.dll", "NetworkPainter.dll" })
            {
                if (!ctx.Files.FileExists(Path.Combine(fixture, needed)))
                {
                    ctx.SetInconclusive(
                        $"the conflict stub is missing {needed} at {fixture}, so there is nothing for the detector to find and nothing was measured about the mod. Build it: dotnet build .work/2026-08-11-spraypaintplus-playtests/ConflictStub/ColorCycler/ColorCycler.csproj -c Release (and the NetworkPainter project beside it).",
                        "fixture-not-built");
                }
            }

            if (!ctx.Files.FileExists(modConfig))
            {
                ctx.SetInconclusive(
                    $"the instance has no modconfig.xml at {modConfig}, so a local mod cannot be registered with it. Re-provision: testrig create -Target hostie -Force -As <id>",
                    "instance-not-provisioned");
            }

            // ---- 2. Seed the stub into the instance's OWN save root. This tree
            // is tier 3 and free to edit; the developer's own mods folder and
            // modconfig are the read-only provisioning source and are never
            // touched.
            configBackup = ctx.Files.ReadAllText(modConfig);
            CopyTree(ctx, fixture, stubFolder);
            seeded = true;

            // StationeersLaunchPad prunes a <Local> entry whose folder is not
            // under the active save path, which is why the copy above has to live
            // inside this instance's own userdata rather than being referenced
            // where it was built.
            //
            // Inserted by index rather than by a regex replacement: the
            // replacement string carries a Windows path, and a regex replacement
            // treats $ specially, so a path with one in it would be silently
            // mangled.
            const string closing = "</ModConfig>";
            var at = configBackup.LastIndexOf(closing, StringComparison.Ordinal);
            if (at < 0)
            {
                ctx.SetInconclusive(
                    $"the instance's modconfig.xml has no {closing} element, so the stub cannot be registered with StationeersLaunchPad and the detector would never see it. Re-provision: testrig create -Target hostie -Force -As <id>",
                    "modconfig-unrecognised");
            }

            var entry = $"  <Local Enabled=\"true\">\r\n    <Path Value=\"{stubFolder}\" />\r\n  </Local>\r\n";
            ctx.Files.WriteAllText(modConfig, configBackup[..at] + entry + configBackup[at..]);

            ctx.WriteEvidence("conflict-stub-seeded.txt", string.Join('\n',
            [
                $"fixture   : {fixture}",
                $"stub      : {stubFolder}",
                $"modconfig : {modConfig}",
                $"seededAt  : {ctx.Stamp()}",
            ]));

            // ---- 3. Restart that ONE instance so the detector runs against it.
            ctx.RestartInstance("hostie", "seeding the conflict stub, which is only read at boot");
            ctx.WaitStage("hostie", Stage.Menu, 400);

            // ---- 4. The fixture is live, and the detector saw it. Every line in
            // this step is printed during BOOT, which is precisely what the
            // console tee cannot be asked for: it is a 2000-line ring per source
            // and StationeersLaunchPad's mod loading evicts thousands of lines
            // before a check can read anything. On 2026-08-11 this check declined
            // with console-tee-evicted for exactly that reason, which was the
            // honest answer to the wrong question.
            //
            // The bepinexlog reader reads BepInEx/LogOutput.log on disk instead.
            // It has no ring, so nothing ages off, and the between-session state
            // reset deletes it, so what it holds is this run and only this run.
            // Boot-time evidence belongs there; the tee is still the right reader
            // for the runtime half of this check below, where sequence numbers
            // are what separate "at the menu" from "in a world".
            var logExists = ctx.Read("hostie", Reader.BepInExLog, "exists");
            if (!ValueText.AsBoolean(logExists.Value))
            {
                ctx.SetInconclusive(
                    "the instance has no BepInEx/LogOutput.log to read, so a boot-time line cannot be looked for at all and nothing was measured about the mod. It is deleted by the state reset and written afresh on every launch, so an absent one means the instance tree is not where the registry says it is.",
                    "bepinex-log-missing");
            }

            ctx.AssertValue("hostie", Reader.BepInExLog, ValueMatcher.AtLeast(2),
                because: "both stub assemblies have to be loaded before anything is read into the banner or its absence; a fixture that did not load makes every assertion below meaningless",
                select: "count", readerArgs: new BepInExLogRequest("TEST FIXTURE ACTIVE"));

            foreach (var name in new[] { "CONFLICT: ColorCycler.dll is loaded", "CONFLICT: NetworkPainter.dll is loaded" })
            {
                ctx.AssertValue("hostie", Reader.BepInExLog, ValueMatcher.AtLeast(1),
                    because: "the deferred assembly scan on Prefab.OnPrefabsLoaded is what withholds PatchAll, and it names each conflict separately; a banner without this line would mean the banner fired for some other reason",
                    select: "count", readerArgs: new BepInExLogRequest(name));
            }

            ctx.AssertValue("hostie", Reader.BepInExLog, ValueMatcher.AtLeast(1),
                because: "the permanent record of a refused load is this line in the log, which is what a player is pointed at when the banner stops repeating",
                select: "count", readerArgs: new BepInExLogRequest("SprayPaintPlus NOT LOADED"));

            // ---- 5. Nothing may be announced while the player is at the menu.
            // The boot line above has already been printed by now, so counting
            // from here separates it from the six that must wait for a world.
            //
            // Is(0) over a Contains filter passes whether the literal is right or
            // wrong, so this counts for nothing on its own. What makes it evidence is
            // step 6: the same bannerLine, the same reader and the same window,
            // asserted PRESENT six times. A drifted literal fails there.
            var seqMenu = Seq(ctx.Read("hostie", Reader.Console, "nextSeq", readerArgs: new ConsoleLogRequest { Limit = 1 }));
            ctx.Wait(15);

            ctx.AssertValue("hostie", Reader.Console, ValueMatcher.Is(0),
                because: "the banner waits on PlayerMessage.WaitForWorld, and 15 seconds at the menu is three of its five-second intervals: a line here means the wait is not working and the whole banner would play to an empty room again",
                select: "count",
                readerArgs: new ConsoleLogRequest { Since = seqMenu, Source = "console", Contains = bannerLine, Limit = 200 });

            // ---- 6. Into a world, and then the six lines. The wait for a world
            // releases when GameManager.GameState leaves None, which is when
            // loading STARTS, so some of the six can land during the load. That
            // is what the code does and is what is asserted: none at the menu,
            // all six once it has left the menu.
            ctx.Act("hostie", Endpoints.Host, new HostRequest { World = "Lunar" }, blocking: true);
            ctx.WaitStage("hostie", Stage.InWorld, 600);

            ctx.AssertValue("hostie", Reader.Status, ValueMatcher.Is(true),
                because: "NetworkServer.Host() gives up quietly after three failed binds, so a world that came up without hosting would still run the banner and would still be the wrong arrangement to have measured",
                select: "hosting");

            // Six lines at five second intervals, plus slack for a busy load.
            ctx.Wait(45);

            ctx.AssertValue("hostie", Reader.Console, ValueMatcher.Is(6),
                because: "the banner is bounded at ConflictBannerRepeats and that bound is the point: the old form was an unbounded Debug.LogError every five seconds that the console re-printed in red with a stack trace and that no player could silence",
                select: "count",
                readerArgs: new ConsoleLogRequest { Since = seqMenu, Source = "console", Contains = bannerLine, Limit = 200 });

            ctx.AssertValue("hostie", Reader.Console, ValueMatcher.Is(1),
                because: "the last line has to say it is the last one and point at the log, because a banner that simply stops is indistinguishable from the mod having crashed",
                select: "count",
                readerArgs: new ConsoleLogRequest { Since = seqMenu, Source = "console", Contains = lastLine, Limit = 200 });
        }
        finally
        {
            // ---- Remove the fixture. This runs whatever happened above, and it
            // is the most important part of this file: the between-session state
            // reset keeps userdata/mods/ and modconfig.xml, so a stub left here
            // would disable Spray Paint Plus on every later run of this instance
            // with nothing to say why.
            var notes = new List<string>();
            if (configBackup.Length > 0)
            {
                try
                {
                    ctx.Files.WriteAllText(modConfig, configBackup);
                    notes.Add("modconfig.xml restored from the pre-seed snapshot");
                }
                catch (Exception ex)
                {
                    notes.Add($"modconfig.xml RESTORE FAILED: {ex.Message}");
                }
            }

            if (seeded)
            {
                try
                {
                    ctx.Files.DeleteDirectory(stubFolder, recursive: true);
                    notes.Add("stub folder deleted");
                }
                catch (Exception ex)
                {
                    notes.Add($"stub folder DELETE FAILED: {ex.Message}");
                }
            }

            // Verify the removal rather than trusting it, and say so out loud
            // either way. A silent cleanup is indistinguishable from none.
            var stubGone = !ctx.Files.DirectoryExists(stubFolder);
            bool configClean;
            try
            {
                configClean = !ctx.Files.ReadAllText(modConfig).Contains("ConflictStub", StringComparison.OrdinalIgnoreCase);
            }
            catch (Exception)
            {
                configClean = false;
            }

            notes.Add($"verify: stubFolderGone={stubGone} modConfigClean={configClean}");

            if (!stubGone || !configClean)
            {
                ctx.TeardownNotes.Add(
                    $"CONFLICT STUB NOT FULLY REMOVED from instance 'hostie'. It disables Spray Paint Plus on every later run of that instance and the state reset does NOT clear it. Delete {stubFolder} and the ConflictStub <Local> entry in {modConfig}, or re-provision: testrig create -Target hostie -Force -As <id>");
            }

            ctx.WriteEvidence("conflict-stub-cleanup.txt", string.Join('\n',
            [
                $"cleanedAt : {ctx.Stamp()}",
                $"stub      : {stubFolder}",
                $"modconfig : {modConfig}",
                string.Empty,
                string.Join('\n', notes),
            ]));
        }
    }

    /// <summary>The monorepo root, from the check's own location.</summary>
    private static string RepoRootOf(string sourceFile)
    {
        // <repo>\Mods\<Mod>\playtests\<file>.cs, so four steps up.
        var current = Path.GetDirectoryName(sourceFile);
        for (var i = 0; i < 3 && current is not null; i++) current = Path.GetDirectoryName(current);
        return current ?? string.Empty;
    }

    /// <summary>Copies a tree through the filesystem seam, so the seeding is testable.</summary>
    private static void CopyTree(IPlaytestContext ctx, string source, string destination)
    {
        ctx.Files.CreateDirectory(destination);
        foreach (var file in ctx.Files.EnumerateFiles(source, "*", recurse: true))
        {
            var relative = file[source.Length..].TrimStart('\\', '/');
            var target = Path.Combine(destination, relative);
            var folder = Path.GetDirectoryName(target);
            if (!string.IsNullOrEmpty(folder)) ctx.Files.CreateDirectory(folder);
            ctx.Files.CopyFile(file, target, overwrite: true);
        }
    }
}
