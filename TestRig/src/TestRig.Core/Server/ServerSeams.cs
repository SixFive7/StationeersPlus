using System.Diagnostics;
using System.IO.Compression;
using TestRig.Core.Infrastructure;

namespace TestRig.Core.Server;

/// <summary>A running dedicated-server game process whose stdin the wrapper owns.</summary>
/// <remarks>
/// The console's answers go to the in-game console rather than the Unity log file, so this
/// channel is fire and forget by necessity. That is the whole reason <c>send</c> and
/// <c>call</c> are two verbs rather than one with two transports.
/// </remarks>
public interface IServerProcess : IDisposable
{
    int Pid { get; }

    bool HasExited { get; }

    DateTimeOffset? StartTimeUtc { get; }

    /// <summary>Writes one line to the game's stdin and flushes it.</summary>
    void WriteLine(string command);

    /// <summary>Closes stdin, which is what the wrapper does on the way out.</summary>
    void CloseInput();
}

/// <summary>Starting the two processes the server half owns.</summary>
/// <remarks>
/// An interface so the suite can assert on the exact command line, the working directory
/// and the stdin wiring without a 600 MB SteamCMD install and a headless Unity process.
/// </remarks>
public interface IServerProcessLauncher
{
    /// <summary>
    /// Starts the host wrapper: this launcher, re-invoked in host mode.
    /// </summary>
    /// <remarks>
    /// No console window is allocated at all. <c>Start-Process -WindowStyle Hidden</c>
    /// allocates a conhost that briefly steals focus on Windows 10 and 11 (SERVER-058), and
    /// the never-take-the-foreground constraint applies to this half exactly as it does to
    /// the client half.
    /// </remarks>
    (int Pid, DateTimeOffset? StartedUtc) StartWrapper(string exePath, string commandLine, string workingDirectory);

    /// <summary>Starts the game with stdin redirected, no window, and the install as its working directory.</summary>
    IServerProcess StartGame(string exePath, string commandLine, string workingDirectory);
}

/// <summary>Running SteamCMD.</summary>
public interface ISteamCmdRunner
{
    /// <summary>Runs SteamCMD to completion and returns its exit code.</summary>
    int Run(string steamCmdPath, IReadOnlyList<string> arguments);
}

/// <summary>Fetching the StationeersLaunchPad server zip.</summary>
public interface IFileDownloader
{
    /// <summary>Downloads to a path. Throws on any failure; the caller degrades.</summary>
    void Download(string url, string destinationPath);
}

/// <summary>Expanding the StationeersLaunchPad server zip.</summary>
public interface IArchiveExtractor
{
    void Extract(string archivePath, string destinationDirectory);
}

// =========================================================================
// the real implementations
// =========================================================================

/// <summary>The real process launcher for the server half.</summary>
public sealed class SystemServerProcessLauncher : IServerProcessLauncher
{
    public (int Pid, DateTimeOffset? StartedUtc) StartWrapper(
        string exePath, string commandLine, string workingDirectory)
    {
        var psi = new ProcessStartInfo
        {
            FileName = exePath,
            UseShellExecute = false,
            // Skips conhost allocation entirely. Hidden is not the same thing: a hidden
            // window is still created, and creating one flashes a focus claim.
            CreateNoWindow = true,
            WorkingDirectory = workingDirectory,
        };

        AppendArguments(psi, commandLine);

        var process = Process.Start(psi)
                      ?? throw new InvalidOperationException($"Could not start the host wrapper at {exePath}.");

        DateTimeOffset? started = null;
        try
        {
            started = new DateTimeOffset(process.StartTime.ToUniversalTime(), TimeSpan.Zero);
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            // A process that exited between Start and the read has no start time. The pid
            // file simply loses its exact reuse check and falls back to the margin.
        }

        return (process.Id, started);
    }

    public IServerProcess StartGame(string exePath, string commandLine, string workingDirectory)
    {
        var psi = new ProcessStartInfo
        {
            FileName = exePath,
            RedirectStandardInput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = workingDirectory,
        };

        AppendArguments(psi, commandLine);

        var process = Process.Start(psi)
                      ?? throw new InvalidOperationException($"Could not start the dedicated server at {exePath}.");

        return new SystemServerProcess(process);
    }

    /// <summary>
    /// Splits a built command line back into an argument list.
    /// </summary>
    /// <remarks>
    /// <see cref="ProcessStartInfo.ArgumentList"/> quotes each element itself, so passing an
    /// already-quoted string through <c>Arguments</c> would double-quote it. The rig builds
    /// its command lines through <see cref="WindowsCommandLine"/> for the client half, where
    /// <c>CreateProcessW</c> genuinely takes one string; here the list is the safer surface,
    /// so the caller hands over a list and this joins nothing.
    /// </remarks>
    private static void AppendArguments(ProcessStartInfo psi, string commandLine)
    {
        // The callers pass a NUL-delimited list rather than a quoted command line, precisely
        // so nothing has to be re-parsed. See ServerHalf's argument builders.
        foreach (var argument in commandLine.Split('\0', StringSplitOptions.RemoveEmptyEntries))
        {
            psi.ArgumentList.Add(argument);
        }
    }
}

/// <summary>The real dedicated-server process.</summary>
internal sealed class SystemServerProcess : IServerProcess
{
    private readonly Process _process;

    public SystemServerProcess(Process process) => _process = process;

    public int Pid => _process.Id;

    public bool HasExited => _process.HasExited;

    public DateTimeOffset? StartTimeUtc
    {
        get
        {
            try
            {
                return new DateTimeOffset(_process.StartTime.ToUniversalTime(), TimeSpan.Zero);
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                return null;
            }
        }
    }

    public void WriteLine(string command)
    {
        _process.StandardInput.WriteLine(command);
        _process.StandardInput.Flush();
    }

    public void CloseInput()
    {
        try
        {
            _process.StandardInput.Close();
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
        {
            // The process already exited and took the pipe with it.
        }
    }

    public void Dispose() => _process.Dispose();
}

/// <summary>The real SteamCMD runner.</summary>
public sealed class SystemSteamCmdRunner : ISteamCmdRunner
{
    public int Run(string steamCmdPath, IReadOnlyList<string> arguments)
    {
        var psi = new ProcessStartInfo
        {
            FileName = steamCmdPath,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var argument in arguments) psi.ArgumentList.Add(argument);

        using var process = Process.Start(psi)
                            ?? throw new InvalidOperationException($"Could not start SteamCMD at {steamCmdPath}.");
        process.WaitForExit();
        return process.ExitCode;
    }
}

/// <summary>The real downloader.</summary>
public sealed class SystemFileDownloader : IFileDownloader, IDisposable
{
    private readonly HttpClient _client = new() { Timeout = TimeSpan.FromMinutes(10) };

    public void Download(string url, string destinationPath)
    {
        using var response = _client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead)
            .GetAwaiter().GetResult();
        response.EnsureSuccessStatusCode();

        using var source = response.Content.ReadAsStream();
        using var destination = new FileStream(destinationPath, FileMode.Create, FileAccess.Write, FileShare.None);
        source.CopyTo(destination);
    }

    public void Dispose() => _client.Dispose();
}

/// <summary>The real archive extractor.</summary>
public sealed class SystemArchiveExtractor : IArchiveExtractor
{
    public void Extract(string archivePath, string destinationDirectory) =>
        ZipFile.ExtractToDirectory(archivePath, destinationDirectory, overwriteFiles: true);
}
