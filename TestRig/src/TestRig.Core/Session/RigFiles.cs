using TestRig.Core.Abstractions;

namespace TestRig.Core.Session;

/// <summary>
/// Reads and writes of the two session state files, with the one error-handling
/// decision that matters made explicitly.
/// </summary>
/// <remarks>
/// A read failure that reads as "the rig is free" is exactly the answer that gets a
/// live session stomped, so an unreadable lock file THROWS rather than reporting
/// absence. Only a genuinely missing file is absence.
///
/// The retry loops themselves live behind <see cref="IFileSystem"/>: the interface
/// documents that <see cref="IFileSystem.WriteAllTextDurable"/> stages, flushes and
/// atomically replaces with retries. This layer converts whatever gets past that into
/// the refusal an operator can act on.
/// </remarks>
internal static class RigFiles
{
    /// <summary>Reads a file's whole text, or null when it does not exist.</summary>
    /// <exception cref="RigRefusalException">The file exists but could not be read.</exception>
    public static string? ReadTextOrNull(IFileSystem fs, string path, string what)
    {
        if (string.IsNullOrEmpty(path)) return null;
        if (!fs.FileExists(path)) return null;

        try
        {
            return fs.ReadAllText(path);
        }
        catch (FileNotFoundException)
        {
            // Vanished between the existence test and the read. That is genuine absence.
            return null;
        }
        catch (DirectoryNotFoundException)
        {
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new RigRefusalException(
                RigRefusalKind.Broken,
                $"Could not read the {what} at {path}: {ex.Message}. Refusing to treat an unreadable "
                + "file as an absent one. Check for a process holding it open.");
        }
    }

    /// <summary>Writes atomically. Readers see the whole old file or the whole new one.</summary>
    public static void WriteAtomic(IFileSystem fs, string path, string text, string what)
    {
        try
        {
            var parent = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(parent)) fs.CreateDirectory(parent);
            fs.WriteAllText(path, text);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new RigRefusalException(
                RigRefusalKind.Broken,
                $"Could not replace the {what} at {path}: {ex.Message}. Something is holding it open. "
                + "It was not updated.");
        }
    }

    /// <summary>
    /// Writes durably: write-through, flush to disk, atomic replace.
    /// </summary>
    /// <remarks>
    /// Used for the dirty marker only. Its whole job is to survive a power cut in the
    /// next few seconds, because it is precisely what tells the next session that this
    /// one did not finish. A cached write that the filesystem has not committed is a
    /// marker that does not exist after the cut.
    /// </remarks>
    public static void WriteDurable(IFileSystem fs, string path, string text, string what)
    {
        try
        {
            var parent = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(parent)) fs.CreateDirectory(parent);
            fs.WriteAllTextDurable(path, text);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new RigRefusalException(
                RigRefusalKind.Broken,
                $"Could not replace the {what} at {path}: {ex.Message}. Something is holding it open.");
        }
    }

    /// <summary>Deletes a file that may already be gone.</summary>
    public static void Delete(IFileSystem fs, string path, string what)
    {
        try
        {
            if (!fs.FileExists(path)) return;
            fs.DeleteFile(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new RigRefusalException(
                RigRefusalKind.Broken,
                $"Could not delete the {what} at {path}: {ex.Message}. Something is holding it open.");
        }
    }

    /// <summary>Top-level entry count, files and directories, zero for a missing directory.</summary>
    public static int CountEntries(IFileSystem fs, string path, string? filePattern = null)
    {
        if (!fs.DirectoryExists(path)) return 0;
        var files = fs.EnumerateFiles(path, filePattern ?? "*", recurse: false).Count;
        if (filePattern is not null) return files;
        return files + fs.EnumerateDirectories(path).Count;
    }

    /// <summary>Recursive byte total, best effort. Metadata only: no file contents are opened.</summary>
    public static long DirectoryBytes(IFileSystem fs, string path)
    {
        if (!fs.DirectoryExists(path)) return 0;
        long total = 0;
        try
        {
            foreach (var file in fs.EnumerateFiles(path, "*", recurse: true))
            {
                try { total += fs.GetFileLength(file); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A size for a report. A failure here must never become a delete decision,
            // and it cannot: the delete predicate never reads this number.
        }
        return total;
    }
}
