using System.Text.Json;
using Xunit;

namespace TestRig.Tests.Cli;

/// <summary>
/// Every verb reaches the right half and carries the right arguments.
/// </summary>
/// <remarks>
/// <para>
/// Asserted on what the halves DID, never on a routing table. Each case runs the real binary
/// against a throwaway rig and looks for a line only the intended half could have produced:
/// the dedicated server's own log path, an instance's own name, a refusal that names the
/// server install. An arm wired to the wrong half, or to nothing, produces none of them.
/// </para>
/// <para>
/// The rig these run against is provisioned but not built: registry rows exist, no instance
/// tree does, and there is no dedicated-server install. That is deliberate. Every verb gets
/// past target resolution, the refusal matrix and the lock gate, reaches its half, and is
/// then refused by the half's own pre-flight, so the routing is proved without a game process
/// ever starting.
/// </para>
/// </remarks>
[Collection("cli")]
public sealed class DispatchTests(CliFixture rig)
{
    private (string Home, string Owner) Locked(string label) => rig.LockedHome(label, "hostie", "joiner");

    /// <summary>Does the verb read <c>--as</c> at all? Handing it to one that does not is a usage error.</summary>
    private bool ReadsOwnerId(string verb) =>
        rig.Surface.RootElement.GetProperty("verbs")
            .EnumerateArray()
            .Single(v => v.GetProperty("name").GetString() == verb)
            .GetProperty("options")
            .EnumerateArray()
            .Any(o => o.GetString() == "as");

    /// <summary>Runs a verb against a freshly locked rig and returns everything it said.</summary>
    private (CliResult Result, string Text) Run(string label, params string[] args)
    {
        var (home, owner) = Locked(label);
        string[] full = ReadsOwnerId(args[0]) ? [.. args, "--as", owner] : args;
        var result = rig.RunIn(home, full);
        return (result, result.All);
    }

    [Theory]
    // The dedicated server half: every line names the server, its install or its log.
    [InlineData("No dedicated-server log at", "logs", "--target", "server")]
    [InlineData("[UpdateGame] Verifying environment", "update-game", "--target", "server")]
    [InlineData("dedicated server is not installed at", "update-mods", "--target", "server")]
    [InlineData("dedicated server is not installed at", "deploy", "--target", "server")]
    [InlineData("dedicated server is not installed at", "start", "--target", "server", "--new", "Mars")]
    [InlineData("[Stop] Dedicated server: nothing running.", "stop", "--target", "server")]
    [InlineData("Server is not running.", "save", "--target", "server", "--save-name", "World")]
    [InlineData("Server is not running.", "send", "--target", "server", "--command", "help")]
    [InlineData("dedicated server is not running, so there is no world to wait for",
        "wait", "--target", "server", "--stage", "inWorld", "--wait-seconds", "1")]
    [InlineData("server (dedicated):", "status", "--target", "server")]
    // The client half: every line names an instance.
    [InlineData("== hostie :", "logs", "--target", "clients")]
    [InlineData("\"instanceName\": \"hostie\"", "snapshot", "--target", "clients")]
    [InlineData("[UpdateGame] Re-linking 2 instance(s)", "update-game", "--target", "clients")]
    [InlineData("[UpdateMods] --- hostie", "update-mods", "--target", "clients")]
    [InlineData("No mods to deploy", "deploy", "--target", "clients")]
    [InlineData("[Provision] Instance 'brandnew' built.", "create", "--target", "brandnew")]
    [InlineData("[Remove] Instance 'hostie' deleted.", "remove", "--target", "hostie")]
    [InlineData("'hostie' is in the registry but has no tree at", "start", "--target", "clients")]
    [InlineData("[hostie] Not running.", "stop", "--target", "clients")]
    [InlineData("[hostie] Not running; there is nothing to save.", "save", "--target", "clients")]
    [InlineData("[Wait] Barrier: 2 instance(s) must reach stage 'menu'",
        "wait", "--target", "clients", "--wait-seconds", "1")]
    [InlineData("[Call] /status -> 2 instance(s)", "call", "--target", "clients", "--path", "/status")]
    [InlineData("clients (2):", "status", "--target", "clients")]
    public void AVerbReachesItsHalfAndTheHalfActs(string expected, params string[] args)
    {
        var (result, text) = Run("route", args);
        Assert.True(
            text.Contains(expected, StringComparison.Ordinal),
            $"testrig {string.Join(' ', args)} never produced '{expected}'\nexit {result.ExitCode}\n{text}");
    }

    [Fact]
    public void TheServerStartCarriesTheWorldItWasGiven()
    {
        // The world reaches Core, which is provable because the server half refuses by NAME:
        // a --load whose save folder is absent would otherwise start a brand-new empty world
        // under that name with nothing reported.
        var (home, owner) = Locked("startsrv");
        CliFixture.InstallFakeServer(home);

        var loaded = rig.RunIn(home, "start", "--target", "server", "--load", "Testbed", "--map", "Mars", "--as", owner);
        Assert.Equal(3, loaded.ExitCode);
        Assert.Contains("Save 'Testbed' not found at", loaded.All, StringComparison.Ordinal);
        Assert.Contains("Testbed", loaded.All, StringComparison.Ordinal);

        // And --map is read rather than ignored: --load without it is refused by Core.
        var noMap = rig.RunIn(home, "start", "--target", "server", "--load", "Testbed", "--as", owner);
        Assert.Equal(3, noMap.ExitCode);
        Assert.Contains("--load requires --map", noMap.All, StringComparison.Ordinal);
    }

    [Fact]
    public void BothServerPortsAreBoundToStartAndToHostModeAndNowhereElse()
    {
        // The ports themselves cannot be observed without launching the server, which no test
        // does. What IS observable is that both verbs read them and no other verb does, so a
        // flag that stopped being forwarded would surface as a usage error here.
        var (home, owner) = Locked("srvports");
        CliFixture.InstallFakeServer(home);

        var start = rig.RunIn(
            home, "start", "--target", "server", "--load", "Testbed", "--map", "Mars",
            "--game-port", "28116", "--update-port", "28115", "--as", owner);
        Assert.DoesNotContain("is not read by 'start'", start.All, StringComparison.Ordinal);
        Assert.Contains("Save 'Testbed' not found at", start.All, StringComparison.Ordinal);

        var elsewhere = rig.RunIn(home, "save", "--target", "server", "--save-name", "W", "--update-port", "1", "--as", owner);
        Assert.Equal(2, elsewhere.ExitCode);
        Assert.Contains("--update-port is not read by 'save'", elsewhere.StdErr, StringComparison.Ordinal);
    }

    [Fact]
    public void CreateForwardsOnlyTheFlagsThatWereTyped()
    {
        // The load-bearing case: create --force is the routine way to pick up a new plugin
        // build, and Core keeps an untyped value from the existing entry. Forwarding the
        // parser's defaults would demote a host to a client on every rebuild.
        var (home, owner) = Locked("shape");

        // A game port clear of the two instances the fixture provisioned: an instance that
        // took another's would be a test confidently wrong about which host a joiner reached,
        // which is exactly what the port guard refuses.
        var built = rig.RunIn(
            home, "create", "--target", "brandnew", "--role", "host", "--game-port", "27888",
            "--username", "Tester", "--no-seed-mods", "--as", owner, "--json");

        using (var doc = built.Json())
        {
            var values = doc.RootElement.GetProperty("values");
            Assert.Equal("brandnew", values.GetProperty("instanceName").GetString());
            Assert.Equal("host", values.GetProperty("role").GetString());
            Assert.Equal(27888, values.GetProperty("gamePort").GetInt32());
        }

        // Rebuilt naming nothing: every one of those survives, because none of them was typed
        // this time and Core is handed null rather than a default.
        var rebuilt = rig.RunIn(home, "create", "--target", "brandnew", "--force", "--as", owner, "--json");
        using (var doc = rebuilt.Json())
        {
            var values = doc.RootElement.GetProperty("values");
            Assert.Equal("host", values.GetProperty("role").GetString());
            Assert.Equal(27888, values.GetProperty("gamePort").GetInt32());
        }
    }

    [Fact]
    public void DeploySplitsTheModListOnCommasAndTrims()
    {
        var (home, owner) = Locked("modlist");
        var result = rig.RunIn(
            home, "deploy", "--target", "clients", "--mod", "SprayPaintPlus, InspectorPlus", "--as", owner);

        // Two names, each resolved separately: one list item would produce one message naming
        // the whole string.
        Assert.Contains("'SprayPaintPlus' not found", result.All, StringComparison.Ordinal);
        Assert.Contains("'InspectorPlus' not found", result.All, StringComparison.Ordinal);
    }

    [Fact]
    public void DeployWithNoModsFallsBackToEveryReleasedMod()
    {
        // An empty list means "everything under Mods/", and this rig has none, so the half
        // refuses rather than silently deploying nothing.
        var (home, owner) = Locked("nomods");
        var result = rig.RunIn(home, "deploy", "--target", "clients", "--as", owner);
        Assert.Equal(3, result.ExitCode);
        Assert.Contains("No mods to deploy", result.All, StringComparison.Ordinal);
    }

    [Fact]
    public void LogsAppliesBothTheTailAndTheFilter()
    {
        // In PowerShell --grep silently overrode --tail: with a pattern the whole file was
        // scanned and the tail count was ignored, so 'logs --tail 20 --grep Error' could
        // return four thousand lines.
        var (home, _) = Locked("logquery");
        var log = Path.Combine(home, "DedicatedServer", "data", "server.log");
        Directory.CreateDirectory(Path.GetDirectoryName(log)!);
        File.WriteAllLines(log, Enumerable.Range(1, 40).Select(i => $"line {i} Error"));

        var tailed = rig.RunIn(home, "logs", "--target", "server", "--tail", "5", "--grep", "Error");
        Assert.Contains("line 40 Error", tailed.StdOut, StringComparison.Ordinal);
        Assert.DoesNotContain("line 35 Error", tailed.StdOut, StringComparison.Ordinal);

        // And with no tail, the whole file is searched.
        var whole = rig.RunIn(home, "logs", "--target", "server", "--grep", "line 1 ");
        Assert.Contains("line 1 Error", whole.StdOut, StringComparison.Ordinal);
    }

    [Fact]
    public void TheUnityFlagPicksTheOtherLogAndIsClientOnly()
    {
        var (home, _) = Locked("unitylog");
        var result = rig.RunIn(home, "logs", "--target", "hostie", "--unity");

        // The instance has never started, so there is no per-run Unity log to read, and the
        // message says exactly that rather than reporting the BepInEx log instead.
        Assert.Contains("no Unity log under", result.StdOut, StringComparison.Ordinal);

        // The dedicated server has one log and it is already the Unity one.
        var server = rig.RunIn(home, "logs", "--target", "server", "--unity");
        Assert.Contains("--unity is a client-instance flag", server.All, StringComparison.Ordinal);
    }

    [Fact]
    public void TheClientHalfActsOnExactlyTheInstancesNamed()
    {
        var (home, owner) = Locked("instances");
        var result = rig.RunIn(home, "stop", "--target", "hostie", "--as", owner);

        Assert.Contains("[hostie] Not running.", result.All, StringComparison.Ordinal);
        Assert.DoesNotContain("[joiner]", result.All, StringComparison.Ordinal);
    }

    [Fact]
    public void StopTakesTheClientHalfDownBeforeTheServer()
    {
        // A joiner still attached when its server goes down leaves the host holding a peer
        // that never said goodbye, which is the state a world would be saved in.
        var (home, owner) = Locked("stoporder");
        var result = rig.RunIn(home, "stop", "--target", "all", "--as", owner);

        var client = result.All.IndexOf("[hostie] Not running.", StringComparison.Ordinal);
        var server = result.All.IndexOf("[Stop] Dedicated server: nothing running.", StringComparison.Ordinal);

        Assert.True(client >= 0 && server >= 0, $"both halves must report\n{result.All}");
        Assert.True(client < server, $"the client half must go first\n{result.All}");
    }

    [Fact]
    public void SaveWritesTheServerWorldBeforeTheClients()
    {
        // The opposite order to stop, and deliberately so: the world holder writes first. The
        // server is not running here, so it refuses and the sequence stops, which is itself
        // the proof that it ran before the clients did.
        var (home, owner) = Locked("saveorder");
        var result = rig.RunIn(home, "save", "--target", "all", "--save-name", "World", "--as", owner);

        Assert.Contains("Server is not running.", result.All, StringComparison.Ordinal);
        Assert.DoesNotContain("[hostie]", result.All, StringComparison.Ordinal);
    }

    [Fact]
    public void TheProcessStageMeansSomethingDifferentOnEachHalf()
    {
        var (home, _) = Locked("stages");

        // On the dedicated server 'process' is the process existing, and nothing else.
        var server = rig.RunIn(home, "wait", "--target", "server", "--stage", "process", "--wait-seconds", "1");
        Assert.Contains("process did not come up within 1s", server.All, StringComparison.Ordinal);

        // On a client instance it means the control plane answers, because a process that
        // exists and a process that responds are different questions there.
        var client = rig.RunIn(home, "wait", "--target", "clients", "--stage", "process", "--wait-seconds", "1");
        Assert.Contains("must reach stage 'ping'", client.All, StringComparison.Ordinal);
    }

    [Fact]
    public void HostModeBypassesTheTargetTheMatrixAndTheLock()
    {
        // Internal: the detached wrapper the server's start spawns, which holds no lock of its
        // own because the start that spawned it already has one. It refuses here because the
        // server is not installed, which is the wrapper reaching the server half with no lock
        // and no target resolution in the way.
        var result = rig.Run("host-mode", "--new", "Mars", "--json");
        using var doc = result.Json();
        var values = doc.RootElement.GetProperty("values");

        Assert.False(values.TryGetProperty("targetKind", out _), "host-mode must not resolve a target");
        Assert.Contains("not installed at", result.All, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("call", "--target", "clients")]
    [InlineData("send", "--target", "server")]
    public void AVerbThatNeedsOneMoreThingSaysWhichOne(params string[] args)
    {
        var (home, owner) = Locked("missing");
        var result = rig.RunIn(home, [.. args, "--as", owner]);
        Assert.Equal(2, result.ExitCode);
        Assert.Contains("requires --", result.StdErr, StringComparison.Ordinal);
    }

    [Fact]
    public void CallWithNoInstancesRefusesRatherThanSucceedingAtNothing()
    {
        var home = rig.NewHome("noinstances");
        var lockResult = rig.RunIn(home, "lock", "--purpose", "x", "--keep-state", "--json");
        using var lockDoc = lockResult.Json();
        var owner = lockDoc.RootElement.GetProperty("values").GetProperty("owner").GetString()!;

        var result = rig.RunIn(home, "call", "--target", "clients", "--path", "/status", "--as", owner);
        Assert.Equal(2, result.ExitCode);
        Assert.Contains("needs at least one instance", result.StdErr, StringComparison.Ordinal);
    }

    /// <summary>
    /// The playtest verb is wired to the checks compiled into this binary.
    /// </summary>
    /// <remarks>
    /// Driven with a filter that matches nothing, deliberately: the runner applies the filter
    /// before it takes a lock or starts anything, so this proves the verb reached the engine
    /// AND that the engine can see the check set, without a game process existing. A binary
    /// with no checks compiled in would say so instead, and an unwired verb would say nothing.
    /// </remarks>
    [Fact]
    public void PlaytestReachesTheEngineWithTheChecksCompiledIn()
    {
        var home = rig.NewHome("playtest");
        var result = rig.RunIn(home, "playtest", "--only", "no-such-check-anywhere");

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("No check matched 'no-such-check-anywhere'", result.All, StringComparison.Ordinal);

        // The message lists what IS registered, which is how a caller finds the right name.
        Assert.Contains("Registered:", result.All, StringComparison.Ordinal);
    }

    [Fact]
    public void EveryDispatchedVerbHasAnArmThatDoesSomething()
    {
        // Not a source-text check: each verb is invoked and has to produce something a caller
        // can see. A verb with an empty arm reports nothing at all. Each one gets its own
        // freshly locked rig, so no verb can be disarmed by what an earlier one did.
        var invocations = new Dictionary<string, string[]>(StringComparer.Ordinal)
        {
            ["lock"] = ["lock", "--purpose", "again", "--keep-state"],
            ["unlock"] = ["unlock", "--keep-state"],
            ["refresh-lock"] = ["refresh-lock"],
            ["capture-baseline"] = ["capture-baseline", "--force"],
            ["reset"] = ["reset", "--dry-run"],
            ["status"] = ["status"],
            ["list"] = ["list"],
            ["logs"] = ["logs", "--target", "server"],
            ["snapshot"] = ["snapshot", "--target", "clients"],
            ["update-game"] = ["update-game", "--target", "server"],
            ["update-mods"] = ["update-mods", "--target", "server"],
            ["deploy"] = ["deploy", "--target", "server"],
            ["create"] = ["create", "--target", "brandnew"],
            ["remove"] = ["remove", "--target", "hostie"],
            ["start"] = ["start", "--target", "clients"],
            ["stop"] = ["stop", "--target", "clients"],
            ["save"] = ["save", "--target", "clients"],
            ["wait"] = ["wait", "--target", "clients", "--wait-seconds", "1"],
            ["call"] = ["call", "--target", "clients", "--path", "/status"],
            ["send"] = ["send", "--target", "server", "--command", "help"],

            // A filter that matches nothing, so the runner answers before it takes a lock or
            // starts anything. No test in this suite may start a game.
            ["playtest"] = ["playtest", "--only", "no-such-check-anywhere"],
        };

        var dispatched = rig.Surface.RootElement.GetProperty("verbs")
            .EnumerateArray()
            .Select(v => v.GetProperty("name").GetString()!)
            .Where(n => n is not ("help" or "host-mode"))
            .ToArray();

        Assert.Equal(21, dispatched.Length);
        Assert.Equal(
            [.. dispatched.OrderBy(v => v, StringComparer.Ordinal)],
            [.. invocations.Keys.OrderBy(v => v, StringComparer.Ordinal)]);

        foreach (var (verb, args) in invocations)
        {
            var (home, owner) = Locked($"arm-{verb}");
            string[] full = ReadsOwnerId(verb) ? [.. args, "--as", owner, "--json"] : [.. args, "--json"];

            var result = rig.RunIn(home, full);
            using var doc = result.Json();
            var values = doc.RootElement.GetProperty("values");

            var recordedAValue = values.EnumerateObject().Any(p =>
                p.Name is not ("verb" or "target" or "targetKind" or "instances"));
            var saidSomething = doc.RootElement.GetProperty("lines").GetArrayLength() > 0;
            var explainedItself = doc.RootElement.GetProperty("error").ValueKind != JsonValueKind.Null;

            Assert.True(
                recordedAValue || saidSomething || explainedItself,
                $"'{verb}' dispatched to an arm that did nothing observable\n{result.All}");
        }
    }

    /// <summary>
    /// The rig follows the instances root RECORDED in the registry, not the launcher default.
    /// </summary>
    /// <remarks>
    /// A rig built under an explicit root, in a shell with no
    /// <c>STATIONEERS_CLIENTRIG_ROOT</c> set, would otherwise have every path resolution, the
    /// orphan scan and the state reset all watching <c>ClientRig/instances</c>, a folder that
    /// has never held anything, while the real trees sat on another volume. It reports a
    /// clean rig and finds no tree, and the two together read as "unprovisioned".
    /// </remarks>
    [Fact]
    public void TheRecordedInstancesRootWinsOverTheLauncherDefault()
    {
        var (home, _) = Locked("recordedroot");
        var elsewhere = Path.Combine(Directory.GetParent(home)!.FullName, "trees-over-here");
        Directory.CreateDirectory(Path.Combine(elsewhere, "hostie"));

        var registry = Path.Combine(home, "ClientRig", "data", "rig.json");
        File.WriteAllText(
            registry,
            File.ReadAllText(registry).Replace(
                JsonSerializer.Serialize(Path.Combine(home, "ClientRig", "instances")),
                JsonSerializer.Serialize(elsewhere),
                StringComparison.Ordinal));

        var result = rig.RunIn(home, "status", "--target", "hostie");

        Assert.Contains(Path.Combine(elsewhere, "hostie"), result.StdOut, StringComparison.Ordinal);
        Assert.Contains("recorded in the registry", result.StdOut, StringComparison.Ordinal);
    }

    [Fact]
    public void AReadOnlyVerbReportsBothHalvesInOneAnswer()
    {
        // status and list are what automation polls, and a rig-wide target has to answer for
        // the whole rig rather than whichever half happens to be up.
        var (home, _) = Locked("bothhalves");
        var result = rig.RunIn(home, "list", "--target", "all", "--json");
        using var doc = result.Json();

        Assert.Equal(0, doc.RootElement.GetProperty("exitCode").GetInt32());
        var text = string.Join("\n", doc.RootElement.GetProperty("lines")
            .EnumerateArray().Select(l => l.GetProperty("text").GetString()));

        Assert.Contains("server", text, StringComparison.Ordinal);
        Assert.Contains("hostie", text, StringComparison.Ordinal);
        Assert.Contains("joiner", text, StringComparison.Ordinal);
    }

    [Fact]
    public void StatusReportsTheLockAlongsideBothHalves()
    {
        // The lock state is the thing the playtest engine needs, and it is answered above the
        // halves so that a half with nothing to say cannot hide it.
        var (home, owner) = Locked("statuslock");
        var result = rig.RunIn(home, "status", "--as", owner, "--json");
        using var doc = result.Json();
        var values = doc.RootElement.GetProperty("values");

        Assert.Equal(0, doc.RootElement.GetProperty("exitCode").GetInt32());
        Assert.Equal("Mine", values.GetProperty("lockState").GetString());
        Assert.Equal(owner, values.GetProperty("owner").GetString());
    }
}
