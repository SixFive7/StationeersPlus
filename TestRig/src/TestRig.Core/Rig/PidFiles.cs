using System.Globalization;
using TestRig.Core.Abstractions;

namespace TestRig.Core.Rig;

/// <summary>
/// Pid files, and the only correct way to ask whether one still names its process.
/// </summary>
/// <remarks>
/// <para>
/// Three layers, and each closes a hole the one above it leaves open.
/// </para>
/// <para>
/// 1. The number. The PowerShell server half used a bare process lookup, so a recycled
/// process id made start, deploy and update-mods refuse and made status report a dead
/// server as up.
/// </para>
/// <para>
/// 2. The IMAGE (COMMON-030, COMMON-031, COMMON-033). Windows recycles process ids and
/// these files outlive their processes on a force-kill, a crash or a reboot, so the image
/// has to match too. The wrapper check accepts any of several images because the wrapper
/// is whatever re-invoked the launcher.
/// </para>
/// <para>
/// 3. The START TIME. An image check does not close reuse by the SAME image, and two
/// instances of <c>rocketstation</c> is the normal case here, not an exotic one. The pid
/// file's writer records the process's own start instant in a SIDECAR file and the reader
/// compares it, which closes reuse exactly rather than heuristically.
/// </para>
/// <para>
/// The sidecar is a separate file and not a second line, deliberately: the session
/// subsystem's <c>BusyProbe.ReadPid</c> parses the WHOLE trimmed contents of
/// <c>game.pid</c> and <c>server.pid</c> as an integer, so a second line would make every
/// running instance invisible to the lock's busy signal, and an abandoned session would
/// look reclaimable mid-test. The file on disk stays exactly what it was.
/// </para>
/// <para>
/// A pid file with no sidecar falls back to the same last-write-time margin
/// <c>BusyProbe.IsPidClaimAlive</c> uses, so a file written by the old rig still reads
/// correctly.
/// </para>
/// </remarks>
public static class PidFiles
{
    /// <summary>The suffix of the sidecar carrying the recorded start instant.</summary>
    public const string StartedSuffix = ".started";

    /// <summary>
    /// How far a process's start may differ from the recorded instant and still be believed.
    /// </summary>
    /// <remarks>
    /// The stamp is written to whole seconds, so the round trip alone costs up to a second.
    /// A recycled pid is hours or days later, never two seconds.
    /// </remarks>
    public static readonly TimeSpan StartTimeTolerance = TimeSpan.FromSeconds(2);

    /// <summary>
    /// How much later than its pid file a process may have started when there is no sidecar.
    /// </summary>
    /// <remarks>
    /// Matches <c>BusyProbe.PidReuseMargin</c> exactly. Two different answers to "is this
    /// claim alive" is the drift this whole file exists to prevent.
    /// </remarks>
    public static readonly TimeSpan LegacyReuseMargin = TimeSpan.FromMinutes(5);

    /// <summary>Writes the claim: the bare number, plus the start instant beside it.</summary>
    public static void Write(IFileSystem fs, string pidFile, int pid, DateTimeOffset? startedUtc)
    {
        fs.WriteAllText(pidFile, pid.ToString(CultureInfo.InvariantCulture));

        if (startedUtc is null)
        {
            // No start time to record. Remove any stale sidecar rather than leaving one
            // that describes a previous process: a wrong sidecar is worse than none,
            // because the reader trusts it exactly.
            fs.DeleteFile(pidFile + StartedSuffix);
            return;
        }

        fs.WriteAllText(pidFile + StartedSuffix, Session.RigTime.Stamp(startedUtc.Value));
    }

    /// <summary>Removes the claim and its sidecar together.</summary>
    public static void Delete(IFileSystem fs, string pidFile)
    {
        fs.DeleteFile(pidFile);
        fs.DeleteFile(pidFile + StartedSuffix);
    }

    /// <summary>The pid a file claims, or null when it is missing, empty or not a number.</summary>
    /// <remarks>
    /// Never a cast. Both PowerShell launchers cast with <c>[int]</c>, which THROWS on a
    /// corrupt file, next to a library version using TryParse. The library version won.
    /// </remarks>
    public static int? Read(IFileSystem fs, string? pidFile)
    {
        if (string.IsNullOrEmpty(pidFile) || !fs.FileExists(pidFile)) return null;

        string text;
        try
        {
            text = fs.ReadAllText(pidFile);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }

        text = text.Trim();
        if (text.Length == 0) return null;
        return int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var pid) ? pid : null;
    }

    /// <summary>The start instant recorded beside a pid file, or null when there is none.</summary>
    public static DateTimeOffset? ReadStartedUtc(IFileSystem fs, string pidFile)
    {
        var sidecar = pidFile + StartedSuffix;
        if (!fs.FileExists(sidecar)) return null;

        try
        {
            return Session.RigTime.TryParse(fs.ReadAllText(sidecar).Trim());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// The live process a pid file claims, or null when the claim is stale.
    /// </summary>
    /// <param name="imageNames">
    /// Accepted images. The first match wins, which is how the wrapper check accepts
    /// several shells without three separate probes.
    /// </param>
    public static ProcessInfo? LiveProcess(
        IFileSystem fs,
        IProcessTable processes,
        string? pidFile,
        IReadOnlyList<string> imageNames)
    {
        if (string.IsNullOrEmpty(pidFile)) return null;

        var claimed = Read(fs, pidFile);
        if (claimed is null or 0) return null;

        ProcessInfo? match = null;
        foreach (var image in imageNames)
        {
            match = string.IsNullOrEmpty(image)
                ? processes.TryGet(claimed.Value)
                : processes.TryGetMatching(claimed.Value, image);
            if (match is not null) break;
        }
        if (match is null) return null;

        var recorded = ReadStartedUtc(fs, pidFile);
        if (recorded is not null)
        {
            // The exact check. A process whose start does not match the one the claim was
            // written for is a different process wearing a recycled number.
            var drift = match.Value.StartTimeUtc - recorded.Value;
            if (drift < TimeSpan.Zero) drift = -drift;
            return drift <= StartTimeTolerance ? match : null;
        }

        // No sidecar: a file from the PowerShell rig, or one whose start time could not be
        // read at launch. Fall back to the margin, whose failure direction is safe -
        // inside it the answer stays "alive", which keeps a live instance's claim rather
        // than deleting it.
        try
        {
            var written = fs.GetLastWriteTimeUtc(pidFile);
            if (match.Value.StartTimeUtc - written > LegacyReuseMargin) return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FileNotFoundException)
        {
            // No timestamp to compare against. The image check alone is what the
            // PowerShell rig did on every single call.
        }

        return match;
    }

    /// <summary>Whether the dedicated server's own claim is live (COMMON-030).</summary>
    public static bool ServerAlive(IFileSystem fs, IProcessTable processes, string? pidFile) =>
        LiveProcess(fs, processes, pidFile, [RigConstants.ServerImageName]) is not null;

    /// <summary>Whether a client instance's claim is live (COMMON-031).</summary>
    public static bool ClientAlive(IFileSystem fs, IProcessTable processes, string? pidFile) =>
        LiveProcess(fs, processes, pidFile, [RigConstants.ClientImageName]) is not null;

    /// <summary>
    /// Whether the dedicated server's host wrapper is live (COMMON-032, COMMON-033).
    /// </summary>
    /// <remarks>
    /// Returns false for a missing or zero pid without probing at all. Without an image
    /// name a recycled id would report the wrapper alive and stop would refuse to clean up.
    /// </remarks>
    public static bool WrapperAlive(IFileSystem fs, IProcessTable processes, string? pidFile) =>
        LiveProcess(fs, processes, pidFile, RigConstants.HostWrapperImageNames) is not null;

    /// <summary>Whether a bare pid, with no file behind it, is a live wrapper (COMMON-032).</summary>
    public static bool WrapperAlive(IProcessTable processes, int? pid)
    {
        if (pid is null or 0) return false;
        foreach (var image in RigConstants.HostWrapperImageNames)
        {
            if (processes.TryGetMatching(pid.Value, image) is not null) return true;
        }
        return false;
    }
}
