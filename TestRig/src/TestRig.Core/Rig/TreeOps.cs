using TestRig.Core.Abstractions;

namespace TestRig.Core.Rig;

/// <summary>What a tree build cost: what was shared, and what is genuinely new disk.</summary>
public sealed record TreeStats
{
    public int LinkedFiles { get; set; }
    public long LinkedBytes { get; set; }
    public int CopiedFiles { get; set; }
    public long CopiedBytes { get; set; }

    public void AddLink(long bytes)
    {
        LinkedFiles++;
        LinkedBytes += bytes;
    }

    public void AddCopy(long bytes)
    {
        CopiedFiles++;
        CopiedBytes += bytes;
    }
}

/// <summary>
/// Whole-tree filesystem operations, composed from the narrow seam.
/// </summary>
/// <remarks>
/// <see cref="IFileSystem"/> deliberately has no recursive copy and no move: it is the
/// operations the rig performs, not a general facade, so a test double stays small enough
/// to be obviously correct. Everything here is built out of
/// <see cref="IFileSystem.EnumerateFiles"/>, <see cref="IFileSystem.EnumerateDirectories"/>,
/// <see cref="IFileSystem.CreateDirectory"/> and <see cref="IFileSystem.CopyFile"/>, so the
/// suite exercises the real seam rather than a shim around <c>Directory.Copy</c>.
/// </remarks>
public static class TreeOps
{
    /// <summary>Every directory under a root, deepest last, root excluded.</summary>
    /// <remarks>
    /// The interface only enumerates one level, so recursion is explicit. Empty directories
    /// matter: a game tree has several, and a link loop driven off file paths alone would
    /// silently drop them.
    /// </remarks>
    public static IReadOnlyList<string> AllDirectories(IFileSystem fs, string root)
    {
        var found = new List<string>();
        var pending = new Queue<string>();
        pending.Enqueue(root);

        while (pending.Count > 0)
        {
            var current = pending.Dequeue();
            foreach (var child in fs.EnumerateDirectories(current))
            {
                found.Add(child);
                pending.Enqueue(child);
            }
        }

        return found;
    }

    /// <summary>Recreates a source tree's directory structure under a destination.</summary>
    public static void MirrorDirectories(IFileSystem fs, string source, string destination)
    {
        fs.CreateDirectory(destination);
        var prefix = Path.TrimEndingDirectorySeparator(Path.GetFullPath(source));

        foreach (var dir in AllDirectories(fs, source))
        {
            var relative = Relative(prefix, dir);
            if (relative.Length == 0) continue;
            fs.CreateDirectory(Path.Combine(destination, relative));
        }
    }

    /// <summary>
    /// A real recursive copy: every file, every directory, nothing shared.
    /// </summary>
    /// <remarks>
    /// Used for the client half's <c>BepInEx</c> tree (CLIENT-070) and the server half's
    /// BepInEx mirror and mod sync (SERVER-015, SERVER-177). A real copy rather than links
    /// because config, plugins, cache, logs and the InspectorPlus folders must be
    /// per-instance, and a hard link shares the file DATA.
    /// </remarks>
    public static TreeStats CopyTree(IFileSystem fs, string source, string destination, bool overwrite = true)
    {
        var stats = new TreeStats();
        if (!fs.DirectoryExists(source)) return stats;

        MirrorDirectories(fs, source, destination);
        var prefix = Path.TrimEndingDirectorySeparator(Path.GetFullPath(source));

        foreach (var file in fs.EnumerateFiles(source, "*", recurse: true))
        {
            var relative = Relative(prefix, file);
            if (relative.Length == 0) continue;

            var target = Path.Combine(destination, relative);
            fs.CopyFile(file, target, overwrite);
            stats.AddCopy(SafeLength(fs, file));
        }

        return stats;
    }

    /// <summary>
    /// Builds a tree of hard links, real-copying only the files named.
    /// </summary>
    /// <param name="realCopyRelative">
    /// Paths, relative to <paramref name="source"/>, that must be COPIED and never linked,
    /// because a hard link shares the file data and a write would reach into the
    /// developer's read-only install (CLIENT-035).
    /// </param>
    /// <remarks>
    /// About 1,050 links build one instance, costing a few megabytes instead of the
    /// install's seven gigabytes.
    ///
    /// Hidden and system files are INCLUDED (CLIENT-037 fixed):
    /// <see cref="IFileSystem.EnumerateFiles"/> sets <c>AttributesToSkip</c> to None, while
    /// the PowerShell used <c>Get-ChildItem</c> without <c>-Force</c> and silently omitted
    /// them from every instance. A tree short of files is a confidently wrong test rather
    /// than a failed one.
    /// </remarks>
    public static TreeStats LinkTree(
        IFileSystem fs,
        string source,
        string destination,
        IReadOnlyCollection<string>? realCopyRelative = null,
        TreeStats? into = null)
    {
        var stats = into ?? new TreeStats();
        if (!fs.DirectoryExists(source)) return stats;

        MirrorDirectories(fs, source, destination);
        var prefix = Path.TrimEndingDirectorySeparator(Path.GetFullPath(source));

        var realCopy = new HashSet<string>(realCopyRelative ?? [], StringComparer.OrdinalIgnoreCase);

        foreach (var file in fs.EnumerateFiles(source, "*", recurse: true))
        {
            var relative = Relative(prefix, file);
            if (relative.Length == 0) continue;

            var target = Path.Combine(destination, relative);
            var length = SafeLength(fs, file);

            if (realCopy.Contains(relative))
            {
                fs.CopyFile(file, target, overwrite: true);
                stats.AddCopy(length);
            }
            else
            {
                fs.CreateHardLink(target, file);
                stats.AddLink(length);
            }
        }

        return stats;
    }

    /// <summary>
    /// Moves a file, composed from copy plus delete.
    /// </summary>
    /// <remarks>
    /// The seam has no move. Copy-then-delete is not atomic, which matters for exactly one
    /// caller: the server's LaunchPad zip download writes to a temp name and moves it into
    /// the cache (SERVER-022 fixed), and there the point is that a PARTIAL download never
    /// occupies the final path, which copy-then-delete still guarantees. Anything needing a
    /// genuinely atomic replace uses <see cref="IFileSystem.WriteAllTextDurable"/>.
    /// </remarks>
    public static void MoveFile(IFileSystem fs, string source, string destination)
    {
        fs.CopyFile(source, destination, overwrite: true);
        fs.DeleteFile(source);
    }

    /// <summary>Total bytes and file count under a tree, for a provision summary.</summary>
    public static (int Files, long Bytes) Measure(IFileSystem fs, string root)
    {
        if (!fs.DirectoryExists(root)) return (0, 0);

        var files = 0;
        long bytes = 0;
        foreach (var file in fs.EnumerateFiles(root, "*", recurse: true))
        {
            files++;
            bytes += SafeLength(fs, file);
        }
        return (files, bytes);
    }

    /// <summary>Top-level directory NAMES under a path, or empty when it does not exist.</summary>
    public static IReadOnlyList<string> ChildDirectoryNames(IFileSystem fs, string path)
    {
        if (!fs.DirectoryExists(path)) return [];

        try
        {
            return
            [
                .. fs.EnumerateDirectories(path)
                    .Select(static d => Path.GetFileName(d.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)))
                    .Where(static n => !string.IsNullOrEmpty(n)),
            ];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    private static string Relative(string sourcePrefix, string fullPath)
    {
        var full = Path.GetFullPath(fullPath);
        if (!full.StartsWith(sourcePrefix, StringComparison.OrdinalIgnoreCase)) return "";
        return full[sourcePrefix.Length..].TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }

    private static long SafeLength(IFileSystem fs, string path)
    {
        try
        {
            return fs.GetFileLength(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FileNotFoundException)
        {
            return 0;
        }
    }
}
