using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using TestRig.Core.Abstractions;

namespace TestRig.Core.Infrastructure;

/// <summary>
/// The real filesystem.
/// </summary>
/// <remarks>
/// Four things in here are load-bearing and none of them are what a general purpose
/// filesystem wrapper would do:
///
/// 1. Every write is UTF-8 with no byte order mark, stated explicitly. The PowerShell
///    rig leaned on implicit defaults, and those differ between pwsh 7 (utf8NoBOM) and
///    Windows PowerShell 5.1 (UTF-16 for redirection, ANSI for Set-Content). The state
///    reset stores several of these files byte for byte in the baseline, so an encoding
///    that changes with the shell makes a clean rig read as dirty.
/// 2. Every read shares the file for writing and deletion. The rig tails BepInEx logs
///    and reads pid files while the game that owns them is running, and the default
///    FileShare.Read would fail with "the process cannot access the file" against a
///    live instance.
/// 3. Enumeration includes hidden and system files, deliberately. Get-ChildItem without
///    -Force silently skipped them, in both hard-link loops, which produced an instance
///    tree that was quietly short of files with nothing logged.
/// 4. There is no BCL API for an NTFS hard link, so <see cref="CreateHardLink"/> is a
///    P/Invoke to CreateHardLinkW.
///
/// The import is DllImport rather than the source-generated LibraryImport because
/// LibraryImport emits unsafe code and therefore needs AllowUnsafeBlocks in the project
/// file. The signature is blittable apart from two UTF-16 strings, which NativeAOT
/// generates a static stub for either way, so nothing is lost. The type stays partial so
/// switching is a two-attribute change if AllowUnsafeBlocks is ever turned on.
/// </remarks>
public sealed partial class SystemFileSystem : IFileSystem
{
    /// <summary>A shared instance. The type is stateless, so one is enough.</summary>
    public static readonly SystemFileSystem Instance = new();

    /// <summary>
    /// UTF-8 with no byte order mark. The single encoding the rig writes text in.
    /// </summary>
    /// <remarks>
    /// throwOnInvalidBytes stays false so a log line carrying a stray byte decodes to
    /// U+FFFD instead of aborting a status read. A tail that throws is worse than a
    /// tail with one replacement character in it.
    /// </remarks>
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: false);

    /// <summary>
    /// How many times a durable write retries the atomic rename.
    /// </summary>
    /// <remarks>
    /// 10, matching Write-RigFileDurable in rig-lock.ps1. The retries exist because a
    /// virus scanner or the search indexer can hold the destination open for a few
    /// milliseconds after it appears, and the loser of that race is the lock file.
    /// </remarks>
    private const int DurableWriteAttempts = 10;

    // ---- existence -------------------------------------------------------

    public bool FileExists(string path) => File.Exists(path);

    public bool DirectoryExists(string path) => Directory.Exists(path);

    // ---- reads -----------------------------------------------------------

    public string ReadAllText(string path)
    {
        using var stream = OpenShared(path);
        // detectEncodingFromByteOrderMarks is on so a file some other tool wrote with a
        // BOM still reads clean. We never write one; we do have to read them.
        using var reader = new StreamReader(stream, Utf8NoBom, detectEncodingFromByteOrderMarks: true);
        return reader.ReadToEnd();
    }

    public byte[] ReadAllBytes(string path)
    {
        using var stream = OpenShared(path);
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return buffer.ToArray();
    }

    public IReadOnlyList<string> ReadLines(string path)
    {
        using var stream = OpenShared(path);
        using var reader = new StreamReader(stream, Utf8NoBom, detectEncodingFromByteOrderMarks: true);

        var lines = new List<string>();
        while (reader.ReadLine() is { } line) lines.Add(line);
        return lines;
    }

    /// <remarks>
    /// Streams the whole file through a ring buffer rather than seeking backwards from
    /// the end. A BepInEx LogOutput.log is tens of megabytes and the tail is typically
    /// 50 lines, so this costs one sequential read and holds only the lines asked for.
    /// Seeking backwards would be faster still and is not worth the off-by-one surface:
    /// this path runs once per `logs` invocation, never in a loop.
    /// </remarks>
    public IReadOnlyList<string> ReadTailLines(string path, int count)
    {
        if (count <= 0) return [];

        var ring = new string[count];
        var seen = 0;

        using (var stream = OpenShared(path))
        using (var reader = new StreamReader(stream, Utf8NoBom, detectEncodingFromByteOrderMarks: true))
        {
            while (reader.ReadLine() is { } line)
            {
                ring[seen % count] = line;
                seen++;
            }
        }

        if (seen == 0) return [];

        var take = Math.Min(seen, count);
        var first = seen - take;
        var result = new List<string>(take);
        for (var i = 0; i < take; i++) result.Add(ring[(first + i) % count]);
        return result;
    }

    // ---- writes ----------------------------------------------------------

    public void WriteAllText(string path, string content)
    {
        var full = Path.GetFullPath(path);
        EnsureParentDirectory(full);
        File.WriteAllText(full, content, Utf8NoBom);
    }

    /// <remarks>
    /// Same encoding rule as every other write here: UTF-8 with no byte order mark, stated
    /// rather than inherited. <c>File.AppendAllText</c> would emit one on a file it creates
    /// under some hosts, which would then sit in the middle of a bundle nobody re-reads until
    /// something has already gone wrong.
    /// </remarks>
    public void AppendAllText(string path, string content)
    {
        var full = Path.GetFullPath(path);
        EnsureParentDirectory(full);
        File.AppendAllText(full, content, Utf8NoBom);
    }

    /// <remarks>
    /// Temp file in the same directory, WriteThrough plus FlushFileBuffers, then an
    /// atomic rename onto the target. NTFS journals the rename, so a file that exists
    /// is a file that was complete.
    ///
    /// This is what makes the crash marker mean anything. session.dirty is written
    /// before a session's first mutating action and cleared only by a completed
    /// restore, so its presence is the one signal that survives a process kill, a
    /// bugcheck or a power cut. A marker half written is a marker that lies.
    ///
    /// File.Move(overwrite: true) rather than File.Replace: Replace requires the
    /// destination to already exist, and the first write of a marker or a lock file is
    /// exactly the case where it does not. Move maps to MoveFileEx with
    /// MOVEFILE_REPLACE_EXISTING, which covers both.
    ///
    /// The temp file is removed on every exit path. The PowerShell had this right and
    /// it is worth keeping: a leftover .tmp beside session.lock reads as debris from a
    /// crash that did not happen.
    /// </remarks>
    public void WriteAllTextDurable(string path, string content)
    {
        var full = Path.GetFullPath(path);
        EnsureParentDirectory(full);

        // Same directory as the target, because a rename is only atomic within one
        // volume and only cheap within one directory.
        var temp = $"{full}.{Environment.ProcessId}-{Guid.NewGuid().ToString("N")[..8]}.tmp";

        try
        {
            var bytes = Utf8NoBom.GetBytes(content);

            using (var stream = new FileStream(
                temp, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            {
                stream.Write(bytes, 0, bytes.Length);
                stream.Flush(flushToDisk: true);
            }

            Exception? last = null;
            for (var attempt = 1; attempt <= DurableWriteAttempts; attempt++)
            {
                try
                {
                    File.Move(temp, full, overwrite: true);
                    return;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    last = ex;
                    Thread.Sleep(5 * attempt);
                }
            }

            throw new IOException(
                $"Could not replace {full} after {DurableWriteAttempts} attempts. Something is holding it open.",
                last);
        }
        finally
        {
            // Best effort. A temp we cannot delete is untidy; failing the write over it
            // would turn a successful rename into a reported failure.
            try
            {
                if (File.Exists(temp)) File.Delete(temp);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }
    }

    // ---- directories -----------------------------------------------------

    public void CreateDirectory(string path) => Directory.CreateDirectory(path);

    /// <remarks>
    /// Clears the read-only attribute first. A hard link into the developer's Steam
    /// install inherits that install's attributes, and File.Delete refuses a read-only
    /// file, which would strand an instance tree that create was told to rebuild.
    /// A missing file, or a missing directory above it, is success: the caller asked
    /// for the file to be gone.
    /// </remarks>
    public void DeleteFile(string path)
    {
        try
        {
            if (!File.Exists(path)) return;
            ClearReadOnly(path);
            File.Delete(path);
        }
        catch (DirectoryNotFoundException)
        {
        }
    }

    /// <remarks>
    /// The one irreversible operation in the rig, so it is deliberate about two things
    /// the BCL is not.
    ///
    /// Read-only files: an instance tree is roughly 1,050 hard links into a Steam
    /// install, and Directory.Delete(recursive) stops dead on the first read-only one,
    /// leaving a half-deleted tree that then makes the next create refuse with "already
    /// exists". Attributes are cleared on the way down.
    ///
    /// Retries: a game process that has only just exited can still have its directory
    /// handles open for a few milliseconds, and so can the search indexer. Three
    /// attempts, short backoff. Past that the caller is told, because silently leaving
    /// a tree behind is how a rebuild ends up mixing two game versions.
    /// </remarks>
    public void DeleteDirectory(string path, bool recursive)
    {
        if (!Directory.Exists(path)) return;

        if (recursive) ClearReadOnlyTree(path);

        Exception? last = null;
        for (var attempt = 1; attempt <= 3; attempt++)
        {
            try
            {
                Directory.Delete(path, recursive);
                return;
            }
            catch (DirectoryNotFoundException)
            {
                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                last = ex;
                Thread.Sleep(50 * attempt);
            }
        }

        throw new IOException($"Could not delete the directory tree at {path} after 3 attempts.", last);
    }

    /// <remarks>
    /// AttributesToSkip is set to None on purpose. The default EnumerationOptions
    /// constructor skips Hidden and System, and PowerShell's Get-ChildItem without
    /// -Force does the same, which is how both hard-link loops came to silently omit
    /// files from an instance tree. A game install carries hidden files; an instance
    /// short of them is a confidently wrong test rather than a failed one.
    ///
    /// IgnoreInaccessible is false for the same reason: a permission failure reported
    /// is recoverable, a permission failure that shortens the list is not.
    ///
    /// Results are sorted ordinally so a link loop runs in the same order twice and a
    /// failure part way through names a reproducible position.
    /// </remarks>
    public IReadOnlyList<string> EnumerateFiles(string path, string searchPattern, bool recurse)
    {
        var options = new EnumerationOptions
        {
            AttributesToSkip = FileAttributes.None,
            IgnoreInaccessible = false,
            RecurseSubdirectories = recurse,
            MatchType = MatchType.Win32,
            ReturnSpecialDirectories = false,
        };

        var files = new List<string>(Directory.EnumerateFiles(path, searchPattern, options));
        files.Sort(StringComparer.Ordinal);
        return files;
    }

    /// <remarks>
    /// Top level only, and hidden and system directories included, for the reason in
    /// <see cref="EnumerateFiles"/>.
    /// </remarks>
    public IReadOnlyList<string> EnumerateDirectories(string path)
    {
        var options = new EnumerationOptions
        {
            AttributesToSkip = FileAttributes.None,
            IgnoreInaccessible = false,
            RecurseSubdirectories = false,
            MatchType = MatchType.Win32,
            ReturnSpecialDirectories = false,
        };

        var dirs = new List<string>(Directory.EnumerateDirectories(path, "*", options));
        dirs.Sort(StringComparer.Ordinal);
        return dirs;
    }

    // ---- metadata --------------------------------------------------------

    public long GetFileLength(string path) => new FileInfo(path).Length;

    /// <remarks>
    /// Throws when the file is absent rather than returning the BCL's 1601-01-01
    /// sentinel. Every caller of this is comparing timestamps to decide staleness, and
    /// a sentinel from 1601 reads as "infinitely stale", which is the answer that gets
    /// a mod redeployed or a tree rebuilt for the wrong reason. Callers that mean "if
    /// it is there" ask FileExists first.
    /// </remarks>
    public DateTimeOffset GetLastWriteTimeUtc(string path)
    {
        if (File.Exists(path)) return new DateTimeOffset(File.GetLastWriteTimeUtc(path), TimeSpan.Zero);
        if (Directory.Exists(path)) return new DateTimeOffset(Directory.GetLastWriteTimeUtc(path), TimeSpan.Zero);

        throw new FileNotFoundException(
            $"Cannot read a last-write time for {path}: it does not exist. Refusing to answer with the " +
            "1601 sentinel, which every staleness comparison reads as infinitely old.",
            path);
    }

    /// <remarks>
    /// Creates the destination's parent. Every call site in the rig was creating it
    /// first anyway, and the one that forgets gets a DirectoryNotFoundException part
    /// way through a tree copy with no indication of which of the two paths was wrong.
    /// </remarks>
    public void CopyFile(string source, string destination, bool overwrite)
    {
        var full = Path.GetFullPath(destination);
        EnsureParentDirectory(full);
        File.Copy(source, full, overwrite);
    }

    // ---- hard links ------------------------------------------------------

    /// <summary>
    /// CreateHardLinkW. Note the argument order relative to PowerShell.
    /// </summary>
    /// <remarks>
    /// New-Item -ItemType HardLink -Path &lt;new&gt; -Value &lt;existing&gt; maps to
    /// CreateHardLinkW(&lt;new&gt;, &lt;existing&gt;, IntPtr.Zero). The two arguments are
    /// in the opposite order to the cmdlet's parameter names, which is the single
    /// easiest thing to get backwards here: swapping them succeeds on the first file of
    /// a tree and then writes a link INTO the developer's install, which is strictly
    /// read-only.
    ///
    /// lpSecurityAttributes is reserved and must be IntPtr.Zero. There are no flags.
    /// No elevation is needed, unlike a symlink.
    /// </remarks>
    [DllImport("kernel32.dll", EntryPoint = "CreateHardLinkW", SetLastError = true,
        CharSet = CharSet.Unicode, ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateHardLinkW(string lpFileName, string lpExistingFileName, IntPtr lpSecurityAttributes);

    /// <remarks>
    /// A hard link shares the file DATA, so anything the game writes to must be a real
    /// copy and never a link. About 1,050 of these build one client instance from the
    /// developer's install, which is what makes an instance cost megabytes rather than
    /// the install's 7 GB.
    ///
    /// The failure message names the link path, the target path and the Win32 error,
    /// all three. The PowerShell named none of them: New-Item under
    /// $ErrorActionPreference = 'Stop' aborted a 1,050 file tree part way through with
    /// a raw error, no rollback, and a half-built tree that made the next create refuse
    /// with "already exists". The error codes worth recognising by name are called out
    /// below because each has a different fix and the raw number teaches nothing.
    /// </remarks>
    public void CreateHardLink(string linkPath, string existingFilePath)
    {
        // Extended-length prefixes: this is a raw Win32 call, so it does not get the
        // long-path handling System.IO does for us. An instances root is configurable
        // and the game's own paths are deep.
        var link = ExtendedPath(linkPath);
        var existing = ExtendedPath(existingFilePath);

        if (CreateHardLinkW(link, existing, IntPtr.Zero)) return;

        var error = Marshal.GetLastWin32Error();

        throw new Win32Exception(error,
            $"""
             CreateHardLinkW failed with {error} ({DescribeLinkError(error)}: {new Win32Exception(error).Message}).
                 link   : {linkPath}
                 target : {existingFilePath}
             """);
    }

    /// <summary>The CreateHardLinkW failures that have a specific cause worth naming.</summary>
    private static string DescribeLinkError(int error) => error switch
    {
        1 => "ERROR_INVALID_FUNCTION, which here means the volume is not NTFS",
        2 => "ERROR_FILE_NOT_FOUND, so the target does not exist",
        3 => "ERROR_PATH_NOT_FOUND, so the link's directory does not exist",
        5 => "ERROR_ACCESS_DENIED",
        17 => "ERROR_NOT_SAME_DEVICE, so the instances root is on a different volume from the game install",
        183 => "ERROR_ALREADY_EXISTS, so something is already at the link path",
        1142 => "ERROR_TOO_MANY_LINKS, the NTFS ceiling of 1,023 links per file",
        _ => "unrecognised",
    };

    // ---- helpers ---------------------------------------------------------

    /// <summary>
    /// Opens for reading while allowing the owner to keep writing and deleting.
    /// </summary>
    /// <remarks>
    /// The rig reads pid files, setting.xml and BepInEx logs belonging to a game that
    /// is running. FileShare.Read, which is what File.ReadAllText uses, fails against
    /// every one of those.
    /// </remarks>
    private static FileStream OpenShared(string path) =>
        new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);

    private static void EnsureParentDirectory(string fullPath)
    {
        var dir = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
    }

    private static void ClearReadOnly(string path)
    {
        var attributes = File.GetAttributes(path);
        if ((attributes & FileAttributes.ReadOnly) != 0)
        {
            File.SetAttributes(path, attributes & ~FileAttributes.ReadOnly);
        }
    }

    private static void ClearReadOnlyTree(string root)
    {
        var options = new EnumerationOptions
        {
            AttributesToSkip = FileAttributes.None,
            IgnoreInaccessible = true,
            RecurseSubdirectories = true,
            MatchType = MatchType.Win32,
        };

        foreach (var file in Directory.EnumerateFiles(root, "*", options))
        {
            // Best effort: this is a courtesy pass ahead of the delete, and the delete
            // itself reports anything that actually blocks it.
            try
            {
                ClearReadOnly(file);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }
    }

    /// <summary>
    /// Rewrites a path into the \\?\ form the raw Win32 entry points need for anything
    /// past MAX_PATH.
    /// </summary>
    private static string ExtendedPath(string path)
    {
        // GetFullPath is required, not cosmetic: \\?\ paths are passed to the object
        // manager verbatim, so a relative segment or a "." in one is not resolved.
        var full = Path.GetFullPath(path);

        if (full.StartsWith(@"\\?\", StringComparison.Ordinal)) return full;
        if (full.StartsWith(@"\\.\", StringComparison.Ordinal)) return full;
        if (full.StartsWith(@"\\", StringComparison.Ordinal)) return @"\\?\UNC\" + full[2..];

        return @"\\?\" + full;
    }
}
