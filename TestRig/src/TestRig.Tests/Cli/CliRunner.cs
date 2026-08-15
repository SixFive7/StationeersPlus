using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using TestRig.Tests.Infrastructure;
using Xunit;

namespace TestRig.Tests.Cli;

/// <summary>What one invocation of the rig binary produced.</summary>
public sealed record CliResult(int ExitCode, string StdOut, string StdErr)
{
    public string All => StdOut + StdErr;

    /// <summary>Every non-empty stdout line, trimmed of the trailing newline only.</summary>
    public IReadOnlyList<string> OutLines =>
        StdOut.Replace("\r\n", "\n", StringComparison.Ordinal).TrimEnd('\n').Split('\n');

    /// <summary>The <c>--json</c> envelope. Fails the test if stdout is not one JSON document.</summary>
    public JsonDocument Json()
    {
        try
        {
            return JsonDocument.Parse(StdOut);
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException(
                $"stdout was not one JSON document ({ex.Message}). stdout was:\n{StdOut}\nstderr was:\n{StdErr}", ex);
        }
    }
}

/// <summary>
/// Runs the real binary and reads what came out.
/// </summary>
/// <remarks>
/// <para>
/// The whole CLI suite drives the shipped executable as a subprocess. That is deliberate and
/// it is the point: the PowerShell suite executed not one line of <c>testrig.ps1</c>. It
/// dot-sourced five libraries and read the launcher as text, so the parameter block,
/// positional binding, the entire dispatch switch, the refusal catch block and every exit
/// code were untested, and twenty of its fifty-one dispatch assertions matched a switch arm's
/// opening brace, which an empty arm would satisfy. Nothing here can pass against a no-op.
/// </para>
/// <para>
/// Every run is pointed at a throwaway rig home, a throwaway source install and a throwaway
/// user-data folder, so no test can reach the one real session lock, the real instance trees
/// or the developer's saves. No test starts the game: where a live process is needed it is a
/// copy of the shell wearing a game image name, and it is bounded in seconds.
/// </para>
/// </remarks>
public sealed class CliFixture : IDisposable
{
    private readonly TempDirectory _root = new("cli");
    private readonly Lock _gate = new();
    private JsonDocument? _surface;

    public CliFixture()
    {
        SourceRoot = FindSourceRoot();
        ExePath = ResolveBinary(SourceRoot);
    }

    /// <summary><c>TestRig/src/</c>.</summary>
    public string SourceRoot { get; }

    public string ExePath { get; }

    /// <summary>The machine-readable surface: verbs, options, refusals, exit codes.</summary>
    public JsonDocument Surface
    {
        get
        {
            lock (_gate)
            {
                if (_surface is not null) return _surface;
                var result = Run("--json");
                Assert.Equal(0, result.ExitCode);
                _surface = result.Json();
                return _surface;
            }
        }
    }

    /// <summary>A fresh, empty rig home with a throwaway source install beside it.</summary>
    /// <remarks>
    /// The fake install carries both markers the install resolver checks, so the halves get
    /// past "your machine is not set up" and reach their own logic. It carries nothing else:
    /// there is no <c>rocketstation_DedicatedServer.exe</c>, so every server verb refuses at
    /// its install check rather than starting a process, and no instance tree, so
    /// <c>start --target clients</c> refuses at its own.
    /// </remarks>
    public string NewHome(string label)
    {
        // Nested one level, so the rig home's PARENT is per-test too. The binary derives the
        // repository root from it, and a shared parent would make one test's Mods/ folder
        // visible to every other test's deploy.
        var home = Path.Combine(_root.Path, $"{label}-{Guid.NewGuid().ToString("N")[..6]}", "TestRig");
        Directory.CreateDirectory(home);

        var install = Path.Combine(home, "fake-install");
        Directory.CreateDirectory(Path.Combine(install, "rocketstation_Data", "Managed"));
        Directory.CreateDirectory(Path.Combine(install, "rocketstation_Data", "StreamingAssets"));
        Directory.CreateDirectory(Path.Combine(install, "BepInEx", "config"));
        Directory.CreateDirectory(Path.Combine(install, "BepInEx", "core"));
        File.WriteAllText(Path.Combine(install, "rocketstation.exe"), "MZ");
        File.WriteAllText(Path.Combine(install, "rocketstation_Data", "Managed", "Assembly-CSharp.dll"), "MZ");
        File.WriteAllText(
            Path.Combine(install, "rocketstation_Data", "StreamingAssets", "version.ini"),
            "UPDATEVERSION=Update 0.2.6428.27798\r\n");
        File.WriteAllText(Path.Combine(install, "BepInEx", "core", "BepInEx.dll"), "MZ");
        File.WriteAllText(
            Path.Combine(install, "BepInEx", "config", "stationeers.launchpad.cfg"),
            "[General]\r\nSavePathOverride = \r\n");

        return home;
    }

    /// <summary>
    /// Installs a fake dedicated-server executable, so a server verb gets past its install
    /// check.
    /// </summary>
    /// <remarks>
    /// Use only where the verb under test refuses BEFORE it would launch anything. In
    /// particular never with <c>start --new</c>: that path spawns the host wrapper, which
    /// spawns this binary again in host mode, and a test has no business starting either.
    /// </remarks>
    public static void InstallFakeServer(string home)
    {
        var install = Path.Combine(home, "DedicatedServer", "install");
        Directory.CreateDirectory(install);
        File.WriteAllText(Path.Combine(install, "rocketstation_DedicatedServer.exe"), "MZ");
    }

    /// <summary>
    /// Creates a repository mod folder that the rig can resolve but has no build for.
    /// </summary>
    /// <remarks>
    /// Enough for a deploy to get past "not found under Mods/" and reach the message that
    /// names the build it wanted, which is where the resolved configuration becomes visible.
    /// </remarks>
    public static void SeedRepositoryMod(string home, string name)
    {
        var repoRoot = Directory.GetParent(home)!.FullName;
        Directory.CreateDirectory(Path.Combine(repoRoot, "Mods", name, name));
    }

    /// <summary>
    /// Makes an instance look genuinely alive, with a real process behind its claim.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The only way to produce a running instance offline. The rig's liveness check is
    /// deliberately not a bare process lookup: it matches the process IMAGE too, because
    /// Windows recycles process ids and pid files outlive their processes. So the stand-in has
    /// to be named <c>rocketstation</c>, and a copy of the shell is the cheapest thing that
    /// can be.
    /// </para>
    /// <para>
    /// Nothing answers on the control plane, which is exactly the state a wedged or still
    /// booting instance is in. Whether that is refused or killed depends on the ROLE the
    /// instance was provisioned as, which is the classifier's own rule and not this helper's:
    /// a silent client cannot be holding a world, a silent host might be.
    /// </para>
    /// </remarks>
    public static StandInProcess ClaimInstanceWithALiveProcess(string home, string instance)
    {
        var stand = StartStandIn(home, "rocketstation");

        var dir = Path.Combine(home, "ClientRig", "data", instance);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "game.pid"), stand.ProcessId.ToString(CultureInfo.InvariantCulture));

        return stand;
    }

    /// <summary>
    /// A live process wearing a game image name, in a directory of the caller's choosing.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The rig identifies a game process by its image NAME and attributes it by its image
    /// PATH, so a copy of the shell in a chosen folder reproduces either half of that
    /// question exactly. Put it inside a rig home to make the rig's own liveness checks see
    /// it; put it anywhere else to stand for a game process belonging to somebody else.
    /// </para>
    /// <para>
    /// Thirty seconds, not three hundred. Every test it serves answers in well under one, and
    /// a process wearing a game image name is visible to every rig session on this machine:
    /// bounding the window bounds the blast radius if a test host dies before its finally
    /// runs.
    /// </para>
    /// </remarks>
    public static StandInProcess StartStandIn(string directory, string imageName)
    {
        Directory.CreateDirectory(directory);
        var stand = Path.Combine(directory, imageName + ".exe");
        File.Copy(Path.Combine(Environment.SystemDirectory, "cmd.exe"), stand, overwrite: true);

        var start = new ProcessStartInfo(stand)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardInput = true,
            WorkingDirectory = directory,
        };
        start.ArgumentList.Add("/c");
        start.ArgumentList.Add("ping -n 30 127.0.0.1");

        var process = Process.Start(start)
            ?? throw new InvalidOperationException($"could not start the stand-in process at {stand}");

        return new StandInProcess(process);
    }

    /// <summary>A stand-in process, killed when the test that started it finishes.</summary>
    public sealed class StandInProcess(Process process) : IDisposable
    {
        public int ProcessId { get; } = process.Id;

        public void Dispose()
        {
            try
            {
                if (!process.HasExited) process.Kill(entireProcessTree: true);
                process.WaitForExit(10_000);
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                // It exited on its own, which is the outcome this wanted anyway.
            }
            finally
            {
                process.Dispose();
            }
        }
    }

    /// <summary>
    /// Registers instances the way a real <c>create</c> leaves them: a registry row AND a
    /// data directory.
    /// </summary>
    /// <remarks>
    /// Both, because the two answer different questions and the rig reads both. The registry
    /// is what every client-half verb resolves a name through (it carries the port, the role
    /// and the recorded root); the data directory is what the reset planner enumerates. A
    /// fixture that wrote only the directory would let every verb resolve a target it could
    /// then do nothing with.
    ///
    /// No tree is built. These instances are provisioned as far as the launcher is concerned
    /// and unprovisioned as far as the filesystem is concerned, which is exactly the state a
    /// dispatch test wants: every verb reaches its half, and the half refuses for a reason
    /// that names the missing tree rather than starting a game.
    /// </remarks>
    public static void Provision(string home, params string[] instances) =>
        ProvisionRoles(home, [.. instances.Select(static i => (i, "client"))]);

    /// <summary>
    /// Registers instances with a role each.
    /// </summary>
    /// <remarks>
    /// The role is not decoration. A silent instance provisioned as a CLIENT is safe to kill,
    /// because nothing it holds can be a world; a silent instance provisioned as a HOST may
    /// be holding one and cannot be asked, so a teardown refuses rather than killing it. Any
    /// test about that refusal has to provision a host.
    /// </remarks>
    public static void ProvisionRoles(string home, params (string Name, string Role)[] instances)
    {
        var data = Path.Combine(home, "ClientRig", "data");
        Directory.CreateDirectory(data);

        var rows = new List<string>();
        for (var i = 0; i < instances.Length; i++)
        {
            var index = i + 1;
            Directory.CreateDirectory(Path.Combine(data, instances[i].Name));
            rows.Add(
                $$"""
                  {"instanceName":"{{instances[i].Name}}","index":{{index}},"role":"{{instances[i].Role}}","port":{{27700 + index}},
                   "gamePort":{{27800 + index}},"clientId":"{{900000000000L + index}}","username":"{{instances[i].Name}}",
                   "width":800,"height":600,"forceGameplayInput":true,
                   "instancesRoot":{{JsonSerializer.Serialize(Path.Combine(home, "ClientRig", "instances"))}},
                   "provisionedUtc":"2026-08-14T00:00:00Z"}
                  """);
        }

        File.WriteAllText(Path.Combine(data, "rig.json"), "[" + string.Join(",", rows) + "]");
    }

    /// <summary>
    /// A throwaway rig home with the session lock already taken, and the owner id.
    /// </summary>
    /// <remarks>
    /// <c>--keep-state</c> is passed so acquisition performs no state restore: this home has
    /// nothing to restore and a test has no business exercising the reset planner.
    /// </remarks>
    public (string Home, string Owner) LockedHome(string label, params string[] instances)
    {
        var home = NewHome(label);
        if (instances.Length > 0) Provision(home, instances);
        return (home, TakeLock(home, label));
    }

    /// <summary>Takes the throwaway lock on a home this caller provisioned itself.</summary>
    public string TakeLock(string home, string label)
    {
        var result = RunIn(home, "lock", "--purpose", $"CLI suite: {label}", "--keep-state", "--json");
        Assert.True(result.ExitCode == 0, $"could not take the throwaway lock:\n{result.All}");

        using var doc = result.Json();
        var owner = doc.RootElement.GetProperty("values").GetProperty("owner").GetString();
        Assert.False(string.IsNullOrWhiteSpace(owner), "lock did not record an owner id");
        return owner!;
    }

    public CliResult Run(params string[] args) => RunIn(NewHome("run"), args);

    public CliResult RunIn(string home, params string[] args)
    {
        var start = new ProcessStartInfo(ExePath)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = Path.GetDirectoryName(ExePath)!,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };

        foreach (var arg in args) start.ArgumentList.Add(arg);

        // Full isolation. TESTRIG_HOME is the one that matters: without it the binary would
        // resolve the real TestRig/ folder beside itself and contend for the real lock.
        //
        // STEAMCMD_PATH is cleared, not merely left alone. The child inherits this process's
        // environment, and the developer's machine has that variable set: without this line
        // 'update-game --target server' would get past its own guards and start a real
        // SteamCMD download into a temp folder.
        start.Environment["TESTRIG_HOME"] = home;
        start.Environment["TESTRIG_STATIONEERS_PATH"] = Path.Combine(home, "fake-install");
        start.Environment["TESTRIG_USERDATA"] = Path.Combine(home, "fake-userdata");
        start.Environment["STATIONEERS_CLIENTRIG_ROOT"] = string.Empty;
        start.Environment["STEAMCMD_PATH"] = string.Empty;

        // The shared per-user state is read at a session boundary and is the developer's own.
        // Both overrides point somewhere that does not exist, so the suite reads neither their
        // LocalLow folder nor their PlayerPrefs key. Reading is harmless; a suite that quietly
        // depends on a real machine's contents is not.
        start.Environment["TESTRIG_SHAREDDATA"] = Path.Combine(home, "fake-sharedstate");
        start.Environment["TESTRIG_PLAYERPREFSKEY"] = @"HKCU:\Software\StationeersPlus\TestRigSuiteNeverExists";

        using var process = Process.Start(start)
            ?? throw new InvalidOperationException($"could not start {ExePath}");

        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(120_000))
        {
            process.Kill(entireProcessTree: true);
            throw new TimeoutException($"testrig {string.Join(' ', args)} did not exit within two minutes.");
        }

        return new CliResult(process.ExitCode, stdout.Result, stderr.Result);
    }

    public void Dispose()
    {
        _surface?.Dispose();
        _root.Dispose();
    }

    private static string FindSourceRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "TestRig.slnx"))) return dir.FullName;
            dir = dir.Parent;
        }

        throw new InvalidOperationException(
            $"could not find TestRig.slnx above {AppContext.BaseDirectory}.");
    }

    /// <summary>
    /// The built binary, rebuilt when the tree beside it is newer.
    /// </summary>
    /// <remarks>
    /// A stale binary would make this whole suite describe code that is no longer there,
    /// which is the failure the shipped binary's own source-hash gate exists to prevent. The
    /// timestamp check is the cheap version of the same idea; a normal
    /// <c>dotnet build TestRig.slnx</c> before <c>dotnet test</c> makes it a no-op.
    /// </remarks>
    private static string ResolveBinary(string sourceRoot)
    {
        var project = Path.Combine(sourceRoot, "TestRig.Cli", "TestRig.Cli.csproj");
        var configuration = IsDebugBuild ? "Debug" : "Release";
        var exe = Path.Combine(sourceRoot, "TestRig.Cli", "bin", configuration, "net10.0", "testrig.exe");

        if (!NeedsBuild(exe, Path.Combine(sourceRoot, "TestRig.Cli"))) return exe;

        var built = Build(project, configuration);
        if (File.Exists(exe)) return exe;

        var alternative = Path.Combine(
            sourceRoot, "TestRig.Cli", "bin", IsDebugBuild ? "Release" : "Debug", "net10.0", "testrig.exe");
        if (File.Exists(alternative)) return alternative;

        throw new InvalidOperationException(
            $"testrig.exe is not at {exe} and building it did not produce one.\n{built}");
    }

    private static bool NeedsBuild(string exe, string projectDir)
    {
        if (!File.Exists(exe)) return true;
        var built = File.GetLastWriteTimeUtc(exe);
        foreach (var source in Directory.EnumerateFiles(projectDir, "*.cs", SearchOption.AllDirectories))
        {
            if (source.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)) continue;
            if (source.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal)) continue;
            if (File.GetLastWriteTimeUtc(source) > built) return true;
        }

        return false;
    }

    private static string Build(string project, string configuration)
    {
        var log = new StringBuilder();
        for (var attempt = 0; attempt < 3; attempt++)
        {
            var start = new ProcessStartInfo("dotnet")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            start.ArgumentList.Add("build");
            start.ArgumentList.Add(project);
            start.ArgumentList.Add("-c");
            start.ArgumentList.Add(configuration);

            using var process = Process.Start(start);
            if (process is null) continue;
            log.AppendLine(process.StandardOutput.ReadToEnd());
            log.AppendLine(process.StandardError.ReadToEnd());
            process.WaitForExit(300_000);
            if (process.ExitCode == 0) return log.ToString();
        }

        return log.ToString();
    }

    private static bool IsDebugBuild =>
#if DEBUG
        true;
#else
        false;
#endif
}

[CollectionDefinition("cli")]
public sealed class CliCollection : ICollectionFixture<CliFixture>;
