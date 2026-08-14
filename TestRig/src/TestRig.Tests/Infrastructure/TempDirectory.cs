namespace TestRig.Tests.Infrastructure;

/// <summary>
/// A real directory on a real NTFS volume, removed when the test finishes.
/// </summary>
/// <remarks>
/// The infrastructure suite deliberately does not mock the filesystem. These classes
/// ARE the seam, so a double in front of them would test nothing: hard links, hidden
/// file attributes, sharing modes and atomic renames only behave like themselves on a
/// real volume. That is the whole finding of the fakery audit, applied here.
///
/// The temp root is used rather than the repository's .work/ folder because a suite
/// that leaves artifacts in the working tree shows up in git status and in the Stop
/// hooks, and because a hard link needs both paths on one volume, which a folder under
/// the temp root guarantees against itself.
/// </remarks>
internal sealed class TempDirectory : IDisposable
{
    public TempDirectory(string label)
    {
        Path = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            $"testrig-tests-{label}-{Guid.NewGuid().ToString("N")[..8]}");

        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    /// <summary>A path inside this directory. The file is not created.</summary>
    public string File(string relative) => System.IO.Path.Combine(Path, relative);

    /// <summary>A subdirectory inside this directory, created.</summary>
    public string Dir(string relative)
    {
        var full = System.IO.Path.Combine(Path, relative);
        Directory.CreateDirectory(full);
        return full;
    }

    public void Dispose()
    {
        try
        {
            if (!Directory.Exists(Path)) return;

            // Read-only attributes are cleared first for the same reason
            // SystemFileSystem.DeleteDirectory does it: a test that links into a
            // read-only file would otherwise leave its own tree behind.
            foreach (var file in Directory.EnumerateFiles(Path, "*", SearchOption.AllDirectories))
            {
                var attributes = System.IO.File.GetAttributes(file);
                if ((attributes & FileAttributes.ReadOnly) != 0)
                {
                    System.IO.File.SetAttributes(file, attributes & ~FileAttributes.ReadOnly);
                }
            }

            Directory.Delete(Path, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A leaked temp folder is not worth failing a green suite over.
        }
    }
}
