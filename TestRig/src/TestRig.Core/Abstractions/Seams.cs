namespace TestRig.Core.Abstractions;

/// <summary>
/// The clock. Every timer decision in the lock and the reset planner reads time
/// through this, so the xUnit suite can advance time rather than sleep.
/// </summary>
public interface IClock
{
    DateTimeOffset UtcNow { get; }
}

/// <summary>
/// Delays. Separate from <see cref="IClock"/> because a test wants time to move
/// without wall-clock cost, and the two are independently substitutable.
/// </summary>
public interface ISleeper
{
    Task DelayAsync(TimeSpan duration, CancellationToken ct = default);
}

/// <summary>
/// A live process, as the rig understands one.
/// </summary>
/// <param name="Pid">Process id.</param>
/// <param name="ImageName">Executable name without extension, e.g. "rocketstation".</param>
/// <param name="StartTimeUtc">
/// Process start time. This is what closes the pid-reuse hole: the PowerShell rig
/// matched on pid plus image name, which is defeated by a reused pid belonging to the
/// same image, and the reset planner deletes files it believes are stale on the
/// strength of that answer.
/// </param>
public readonly record struct ProcessInfo(int Pid, string ImageName, DateTimeOffset StartTimeUtc);

/// <summary>
/// Process table access: query, launch, terminate.
/// </summary>
/// <remarks>
/// Matching on pid alone is never correct here. The PowerShell implementation
/// checked the process image as well, deliberately, and that behaviour is
/// load-bearing across the lock's busy detection, the reset planner's stale-pid
/// decisions, and every teardown path.
/// </remarks>
public interface IProcessTable
{
    /// <summary>Returns the process if it is alive, otherwise null.</summary>
    ProcessInfo? TryGet(int pid);

    /// <summary>
    /// Returns the process only if it is alive AND its image matches, otherwise null.
    /// This is the query the rig actually wants nearly everywhere.
    /// </summary>
    ProcessInfo? TryGetMatching(int pid, string expectedImageName);

    /// <summary>All live processes with the given image name.</summary>
    IReadOnlyList<ProcessInfo> FindByImage(string imageName);

    /// <summary>Requests termination and waits up to <paramref name="grace"/>.</summary>
    Task<bool> StopAsync(int pid, TimeSpan grace, CancellationToken ct = default);
}

/// <summary>
/// Filesystem access. Deliberately narrow: the operations the rig actually performs,
/// not a general façade, so a test double is small enough to be obviously correct.
/// </summary>
public interface IFileSystem
{
    bool FileExists(string path);
    bool DirectoryExists(string path);

    string ReadAllText(string path);
    byte[] ReadAllBytes(string path);

    /// <summary>
    /// Every line, sharing the file for writing AND deletion.
    /// </summary>
    /// <remarks>
    /// The sharing mode is part of the contract, not an implementation detail. The rig reads
    /// pid files and BepInEx logs belonging to a game that is running and may rotate them,
    /// and the default <c>FileShare.Read</c> that <c>File.ReadAllLines</c> uses fails against
    /// every one of those, exactly when a caller needs them most.
    /// </remarks>
    IReadOnlyList<string> ReadLines(string path);

    /// <summary>Reads at most <paramref name="count"/> lines from the end of a file.</summary>
    IReadOnlyList<string> ReadTailLines(string path, int count);

    void WriteAllText(string path, string content);

    /// <summary>
    /// Appends text, creating the file when it is not there.
    /// </summary>
    /// <remarks>
    /// The one operation the evidence bundle needs that a write cannot express. Its files
    /// are written in two passes (a lock record before a check and again after it, a console
    /// tail on either side of the body), and a read-modify-write is not the same thing: it
    /// rewrites the whole file, so a failure part way through loses what was already there,
    /// which for an evidence bundle is exactly the run that most needed it.
    /// </remarks>
    void AppendAllText(string path, string content);

    /// <summary>
    /// Writes durably: temp file, flush to disk, atomic replace, with retries.
    /// The lock file and the dirty marker both depend on surviving a power cut
    /// mid-write, because the dirty marker is precisely what tells the next session
    /// that the previous one did not finish.
    /// </summary>
    void WriteAllTextDurable(string path, string content);

    void CreateDirectory(string path);
    void DeleteFile(string path);

    /// <summary>Deletes a directory tree. The one irreversible operation in the rig.</summary>
    void DeleteDirectory(string path, bool recursive);

    IReadOnlyList<string> EnumerateFiles(string path, string searchPattern, bool recurse);
    IReadOnlyList<string> EnumerateDirectories(string path);

    long GetFileLength(string path);
    DateTimeOffset GetLastWriteTimeUtc(string path);

    void CopyFile(string source, string destination, bool overwrite);

    /// <summary>
    /// Creates an NTFS hard link. There is no BCL API for this, so the real
    /// implementation is a P/Invoke to CreateHardLinkW. A client instance is built
    /// from roughly 1,050 of these against the developer's install, which is why it
    /// is a copy-on-demand tree rather than a real copy.
    /// </summary>
    void CreateHardLink(string linkPath, string existingFilePath);
}

/// <summary>
/// The machine's boot identity, used to tell "the previous session crashed" from
/// "the machine rebooted under it".
/// </summary>
public interface IBootIdentity
{
    string GetBootId();
}

/// <summary>Outcome of trying to enter the cross-process critical section.</summary>
public enum MutexAcquisition
{
    /// <summary>Entered cleanly.</summary>
    Acquired,

    /// <summary>
    /// Entered, but the previous holder died without releasing. The rig must treat
    /// whatever it is about to read as possibly half-written. PowerShell swallowed
    /// this distinction.
    /// </summary>
    AcquiredAbandoned,

    /// <summary>Timed out waiting.</summary>
    TimedOut,
}

/// <summary>
/// The cross-process critical section that serialises lock acquisition.
/// </summary>
/// <remarks>
/// The PowerShell implementation fell back from a Global\ to a Local\ mutex name per
/// process, so two processes could resolve differently and not be serialised at all,
/// with nothing logged. The measured cost without a working critical section was
/// four simultaneous winners per round across 20 rounds. This interface makes the
/// namespace an explicit property so a fallback is observable rather than silent.
/// </remarks>
public interface ICrossProcessLock
{
    /// <summary>The resolved mutex name, for diagnostics.</summary>
    string Name { get; }

    /// <summary>True when the implementation had to fall back off the global namespace.</summary>
    bool IsProcessLocal { get; }

    IDisposable? TryEnter(TimeSpan timeout, out MutexAcquisition outcome);
}
