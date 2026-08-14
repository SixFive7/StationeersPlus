using System.ComponentModel;
using System.Diagnostics;
using TestRig.Core.Abstractions;

namespace TestRig.Core.Infrastructure;

/// <summary>
/// The real process table.
/// </summary>
/// <remarks>
/// Two rules govern everything in here, and both exist because the rig decides whether
/// to delete files on the strength of the answers.
///
/// The image name is checked, always. The dedicated server half used a bare
/// Get-Process -Id with no image check, so a recycled pid made start, deploy and
/// update-mods refuse and made status report a dead server as up, while the client half
/// and the shared library both checked and therefore disagreed with status about whether
/// the same server was alive.
///
/// The start time is carried, always. Checking pid plus image is still defeated by a pid
/// reused by the SAME image, which on this machine means a second game client: two
/// instances launched minutes apart, the first exits, Windows hands its pid to the
/// second, and the stale pid file now resolves to a live rocketstation that belongs to
/// somebody else. The caller records StartTimeUtc when it writes the pid file and
/// compares it on the way back, which is the only thing that closes that hole.
///
/// A ProcessInfo whose StartTimeUtc could not be read would defeat the point, so an
/// unreadable start time is reported as no match rather than as a record with a default
/// timestamp in it. In practice that only happens for protected system processes, which
/// are never the rig's own, and for a process that exits between two calls, which is
/// not a match either.
/// </remarks>
public sealed class SystemProcessTable : IProcessTable
{
    /// <summary>A shared instance. The type is stateless, so one is enough.</summary>
    public static readonly SystemProcessTable Instance = new();

    public ProcessInfo? TryGet(int pid)
    {
        // pid 0 is the System Idle Process and negative pids do not exist. Neither is
        // ever the rig's, and both are what a corrupt or empty pid file parses to.
        if (pid <= 0) return null;

        Process process;
        try
        {
            process = Process.GetProcessById(pid);
        }
        catch (ArgumentException)
        {
            return null;
        }
        catch (InvalidOperationException)
        {
            return null;
        }

        using (process)
        {
            return Describe(process);
        }
    }

    public ProcessInfo? TryGetMatching(int pid, string expectedImageName)
    {
        var info = TryGet(pid);
        if (info is null) return null;

        return string.Equals(info.Value.ImageName, NormalizeImageName(expectedImageName), StringComparison.OrdinalIgnoreCase)
            ? info
            : null;
    }

    public IReadOnlyList<ProcessInfo> FindByImage(string imageName)
    {
        var name = NormalizeImageName(imageName);

        Process[] processes;
        try
        {
            processes = Process.GetProcessesByName(name);
        }
        catch (InvalidOperationException)
        {
            return [];
        }

        var found = new List<ProcessInfo>(processes.Length);
        foreach (var process in processes)
        {
            using (process)
            {
                var info = Describe(process);
                if (info is not null) found.Add(info.Value);
            }
        }

        found.Sort(static (a, b) => a.Pid.CompareTo(b.Pid));
        return found;
    }

    /// <remarks>
    /// Terminate, then wait. There is no graceful step here on purpose: the polite ask
    /// is POST /quit over the control plane and it belongs to the caller, which knows
    /// whether the instance holds a world worth saving first. By the time anything
    /// reaches this method the decision to kill has been made.
    ///
    /// The process tree is deliberately not killed. Stop-Process -Force did not, and a
    /// game client's children are Steam's, not ours.
    /// </remarks>
    public async Task<bool> StopAsync(int pid, TimeSpan grace, CancellationToken ct = default)
    {
        if (pid <= 0) return true;

        Process process;
        try
        {
            process = Process.GetProcessById(pid);
        }
        catch (ArgumentException)
        {
            return true;
        }
        catch (InvalidOperationException)
        {
            return true;
        }

        using (process)
        {
            try
            {
                process.Kill(entireProcessTree: false);
            }
            catch (InvalidOperationException)
            {
                // Exited between the lookup and the kill.
                return true;
            }
            catch (Win32Exception)
            {
                // Access denied, or it is already terminating. Fall through to the wait
                // and let the answer be whether it actually went away.
            }

            if (grace <= TimeSpan.Zero) return process.HasExited;

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(grace);

            try
            {
                await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
                return true;
            }
            catch (OperationCanceledException)
            {
                // Caller cancellation is not the same answer as "it is still running",
                // so it propagates rather than being reported as a failed stop.
                ct.ThrowIfCancellationRequested();
                return false;
            }
        }
    }

    /// <summary>
    /// Reads the three fields, or reports no match if any of them cannot be read.
    /// </summary>
    private static ProcessInfo? Describe(Process process)
    {
        try
        {
            var name = process.ProcessName;
            var startedUtc = process.StartTime.ToUniversalTime();
            return new ProcessInfo(process.Id, name, new DateTimeOffset(startedUtc, TimeSpan.Zero));
        }
        catch (Win32Exception)
        {
            // A protected or elevated process we cannot open. Never one of ours, and a
            // record without a start time would be worse than no record.
            return null;
        }
        catch (InvalidOperationException)
        {
            // Exited while we were reading it.
            return null;
        }
        catch (NotSupportedException)
        {
            return null;
        }
    }

    /// <summary>
    /// Strips a trailing .exe.
    /// </summary>
    /// <remarks>
    /// Process.ProcessName and Process.GetProcessesByName both work in bare image names,
    /// so passing "rocketstation.exe" returns an empty set with no error: a confidently
    /// wrong "nothing is running" rather than a failure. The rig's own constants are
    /// bare, but a pid file, a manifest or a caller reading an exe path is one keystroke
    /// away from the other form.
    /// </remarks>
    private static string NormalizeImageName(string imageName)
    {
        var name = imageName?.Trim() ?? string.Empty;
        return name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? name[..^4] : name;
    }
}
