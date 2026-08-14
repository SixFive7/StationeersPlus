using TestRig.Core.Abstractions;

namespace TestRig.Playtest.Seams;

/// <summary>
///     The real log-file reader, over Core's filesystem seam.
/// </summary>
/// <remarks>
///     <para>
///     The sharing mode is why this interface exists: the game holds
///     <c>BepInEx/LogOutput.log</c> open for append for as long as it runs, and it may delete
///     it on the next launch, so the read has to permit both. An ordinary read fails exactly
///     when a check needs it most, while the instance under test is alive.
///     </para>
///     <para>
///     <b>Core already reads that way.</b> <see cref="IFileSystem.ReadLines"/> opens with
///     <c>FileShare.ReadWrite | FileShare.Delete</c>, for the same reason and in the same
///     words, so this type delegates rather than opening a second <c>FileStream</c> with its
///     own copy of the flags. Two implementations of one sharing rule is one that can drift,
///     and the failure it would drift into is a check that cannot read the log of the
///     instance it is testing.
///     </para>
///     <para>
///     What survives here is the two READS THAT MUST NOT THROW. A check asks for a log's
///     length before it exists and after it is rotated; the seam's own accessor throws on an
///     absent file, deliberately, because everything else that calls it is comparing
///     timestamps and a sentinel would read as infinitely stale.
///     </para>
/// </remarks>
public sealed class SystemLogFiles(IFileSystem files) : ILogFiles
{
    /// <summary>The real filesystem, which is the only thing this is ever pointed at outside a test.</summary>
    public SystemLogFiles() : this(Core.Infrastructure.SystemFileSystem.Instance)
    {
    }

    public bool Exists(string path) => files.FileExists(path);

    public long Length(string path)
    {
        try
        {
            return files.FileExists(path) ? files.GetFileLength(path) : 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FileNotFoundException)
        {
            return 0;
        }
    }

    public IReadOnlyList<string> ReadAllLines(string path) => files.ReadLines(path);
}
