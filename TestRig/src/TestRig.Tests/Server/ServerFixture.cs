using TestRig.Core.Rig;
using TestRig.Core.Server;
using TestRig.Tests.Client;
using TestRig.Tests.Session.Fakes;

namespace TestRig.Tests.Server;

/// <summary>A dedicated-server game process a test drives directly.</summary>
public sealed class FakeServerProcess : IServerProcess
{
    public FakeServerProcess(int pid, DateTimeOffset started)
    {
        Pid = pid;
        StartTimeUtc = started;
    }

    public int Pid { get; }

    public bool HasExited { get; set; }

    public DateTimeOffset? StartTimeUtc { get; }

    /// <summary>Everything relayed into the game's stdin, in order.</summary>
    public List<string> StdIn { get; } = [];

    public bool InputClosed { get; private set; }

    public void WriteLine(string command) => StdIn.Add(command);

    public void CloseInput() => InputClosed = true;

    public void Dispose()
    {
    }
}

/// <summary>A launcher that records instead of starting anything.</summary>
public sealed class FakeServerProcessLauncher : IServerProcessLauncher
{
    public List<(string Exe, string[] Arguments, string WorkingDirectory)> Wrappers { get; } = [];

    public List<(string Exe, string[] Arguments, string WorkingDirectory)> Games { get; } = [];

    public FakeProcessTable? Processes { get; set; }

    public FakeClock? Clock { get; set; }

    public int NextWrapperPid { get; set; } = 8100;

    public int NextGamePid { get; set; } = 8200;

    /// <summary>The game process the next start hands back, so a test can drive its exit.</summary>
    public FakeServerProcess? LastGame { get; private set; }

    /// <summary>Set to have a wrapper launch NOT register the game, as a crash on boot would.</summary>
    public bool RegisterGamePid { get; set; } = true;

    /// <summary>
    /// Runs after a wrapper launch, so the fixture can model the wrapper actually starting
    /// the game and registering its pid, which is what the start barrier waits for.
    /// </summary>
    public Action<int>? AfterWrapperStarted { get; set; }

    public (int Pid, DateTimeOffset? StartedUtc) StartWrapper(string exePath, string commandLine, string workingDirectory)
    {
        Wrappers.Add((exePath, commandLine.Split('\0', StringSplitOptions.RemoveEmptyEntries), workingDirectory));

        var pid = NextWrapperPid++;
        var started = Clock?.UtcNow ?? DateTimeOffset.UtcNow;
        Processes?.Add(pid, "testrig", started);
        AfterWrapperStarted?.Invoke(pid);
        return (pid, started);
    }

    public IServerProcess StartGame(string exePath, string commandLine, string workingDirectory)
    {
        Games.Add((exePath, commandLine.Split('\0', StringSplitOptions.RemoveEmptyEntries), workingDirectory));

        var pid = NextGamePid++;
        var started = Clock?.UtcNow ?? DateTimeOffset.UtcNow;
        if (RegisterGamePid) Processes?.Add(pid, RigConstants.ServerImageName, started);

        LastGame = new FakeServerProcess(pid, started);
        return LastGame;
    }
}

/// <summary>SteamCMD, scripted.</summary>
public sealed class FakeSteamCmdRunner : ISteamCmdRunner
{
    public List<(string Path, string[] Arguments)> Runs { get; } = [];

    public int ExitCode { get; set; }

    /// <summary>Runs after the exit code is decided, so a test can plant the installed exe.</summary>
    public Action? OnRun { get; set; }

    public int Run(string steamCmdPath, IReadOnlyList<string> arguments)
    {
        Runs.Add((steamCmdPath, [.. arguments]));
        OnRun?.Invoke();
        return ExitCode;
    }
}

/// <summary>A downloader that writes whatever a test tells it to.</summary>
public sealed class FakeFileDownloader : IFileDownloader
{
    private readonly FakeFileSystem _fs;

    public FakeFileDownloader(FakeFileSystem fs) => _fs = fs;

    public List<(string Url, string Destination)> Downloads { get; } = [];

    /// <summary>What lands at the destination. Empty simulates a truncated download.</summary>
    public string Content { get; set; } = "PK zip bytes";

    public Exception? Throws { get; set; }

    public void Download(string url, string destinationPath)
    {
        Downloads.Add((url, destinationPath));
        if (Throws is not null) throw Throws;
        _fs.AddFile(destinationPath, Content);
    }
}

/// <summary>An extractor that unpacks a scripted file list.</summary>
public sealed class FakeArchiveExtractor : IArchiveExtractor
{
    private readonly FakeFileSystem _fs;

    public FakeArchiveExtractor(FakeFileSystem fs) => _fs = fs;

    public List<(string Archive, string Destination)> Extractions { get; } = [];

    /// <summary>Relative paths written under the destination on extract.</summary>
    public List<string> Contents { get; } = [@"StationeersLaunchPad\RG.ImGui.dll", @"StationeersLaunchPad\StationeersLaunchPad.dll"];

    public Exception? Throws { get; set; }

    public void Extract(string archivePath, string destinationDirectory)
    {
        Extractions.Add((archivePath, destinationDirectory));
        if (Throws is not null) throw Throws;
        foreach (var relative in Contents) _fs.AddFile(Path.Combine(destinationDirectory, relative), "from zip");
    }
}

/// <summary>
/// A whole dedicated-server half, wired from fakes, entirely in memory.
/// </summary>
public sealed class ServerFixture
{
    public ServerFixture()
    {
        Client = new ClientFixture();

        Launcher = new FakeServerProcessLauncher { Processes = Client.Processes, Clock = Client.Clock };
        SteamCmd = new FakeSteamCmdRunner();
        Downloader = new FakeFileDownloader(Client.Fs);
        Extractor = new FakeArchiveExtractor(Client.Fs);

        // The wrapper starts the game and registers its pid, which is what the start
        // barrier polls for. Clear it to model a wrapper that dies on boot.
        Launcher.AfterWrapperStarted = _ =>
        {
            Client.Processes.Add(ServerFixture.RegisteredServerPid, RigConstants.ServerImageName, Client.Clock.UtcNow);
            PidFiles.Write(Client.Fs, Client.Rig.Paths.ServerPidFile, ServerFixture.RegisteredServerPid, Client.Clock.UtcNow);
        };

        Half = new ServerHalf(
            Client.Fs, Client.Processes, Client.Clock, Client.Rig.Sleeper, Client.Output,
            Client.Rig.Paths, Client.Env, Client.Mods, Client.Rig.Lock,
            Launcher, SteamCmd, Downloader, Extractor,
            LauncherPath);

        Paths = Half.Paths;
    }

    /// <summary>The binary the host wrapper re-invokes in host mode.</summary>
    public const string LauncherPath = @"C:\rigtest\TestRig\testrig.exe";

    /// <summary>The pid the modelled wrapper registers for the game process.</summary>
    public const int RegisteredServerPid = 9101;

    public ClientFixture Client { get; }
    public FakeServerProcessLauncher Launcher { get; }
    public FakeSteamCmdRunner SteamCmd { get; }
    public FakeFileDownloader Downloader { get; }
    public FakeArchiveExtractor Extractor { get; }
    public ServerHalf Half { get; }
    public ServerPaths Paths { get; }

    public FakeFileSystem Fs => Client.Fs;
    public RecordingOutput Output => Client.Output;
    public FakeProcessTable Processes => Client.Processes;
    public FakeClock Clock => Client.Clock;

    public string Lease(string purpose = "server tests") => Client.Lease(purpose);

    public string? Owner => Client.Owner;

    /// <summary>Plants an installed dedicated server, with the BepInEx tree already mirrored.</summary>
    public ServerFixture Installed()
    {
        Fs.AddFile(Paths.Exe, "MZ");
        Fs.AddFile(Path.Combine(Paths.BepInEx, "core", "BepInEx.dll"), "bepinex");
        Fs.AddFile(Paths.LaunchPadDll, "launchpad");
        Fs.AddFile(Path.Combine(Path.GetDirectoryName(Paths.LaunchPadDll)!, "version.txt"), "2.4.1");
        Fs.AddDirectory(Paths.SaveRoot);
        Fs.AddDirectory(Paths.ModsDir);
        Fs.AddDirectory(Paths.PluginsDir);
        return this;
    }

    /// <summary>Plants a world that <c>--load</c> would accept.</summary>
    public ServerFixture World(string name)
    {
        Fs.AddFile(Path.Combine(Paths.World(name), name + ".save"), "world bytes");
        return this;
    }

    /// <summary>Makes the server and its wrapper live, with pid files that claim them.</summary>
    public ServerFixture Running(int serverPid = 9101, int wrapperPid = 9100)
    {
        Processes.Add(serverPid, RigConstants.ServerImageName, Clock.UtcNow);
        Processes.Add(wrapperPid, "testrig", Clock.UtcNow);
        PidFiles.Write(Fs, Paths.PidFile, serverPid, Clock.UtcNow);
        PidFiles.Write(Fs, Paths.HostPidFile, wrapperPid, Clock.UtcNow);
        return this;
    }

    /// <summary>Appends lines to the server log, as the running game would.</summary>
    public void Log(params string[] lines)
    {
        var existing = Fs.FileExists(Paths.LogFile) ? Fs.ReadAllText(Paths.LogFile) : "";
        Fs.AddFile(Paths.LogFile, existing + string.Join("\r\n", lines) + "\r\n");
    }

    // ---- synchronous wrappers ----------------------------------------------

    public void Start(ServerStartWorld world, string? owner = null, int gamePort = 0, int updatePort = 0) =>
        Half.StartAsync(world, owner, gamePort, updatePort).GetAwaiter().GetResult();

    public void Send(string command, string? owner = null) =>
        Half.SendAsync(command, owner).GetAwaiter().GetResult();

    public bool Save(string name, string? owner = null, int waitSeconds = 0) =>
        Half.SaveAsync(name, owner, waitSeconds).GetAwaiter().GetResult();

    public void Stop(string? owner = null, string? saveName = null, int teardownSeconds = 0, int waitSeconds = 0) =>
        Half.StopAsync(owner, saveName, teardownSeconds, waitSeconds).GetAwaiter().GetResult();

    public bool Wait(ReadinessStage stage = ReadinessStage.InWorld, string? owner = null, int waitSeconds = 0) =>
        Half.WaitAsync(stage, owner, waitSeconds).GetAwaiter().GetResult();

    public void HostMode(ServerStartWorld world, CancellationToken ct) =>
        Half.HostModeAsync(world, 0, 0, ct).GetAwaiter().GetResult();

    public void Teardown(int graceSeconds = 0) =>
        Half.TeardownAsync(graceSeconds).GetAwaiter().GetResult();
}
