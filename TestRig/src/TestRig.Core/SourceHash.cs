namespace TestRig.Core;

/// <summary>
/// Recomputes the digest of the rig's own source tree, so a committed binary can
/// tell that it no longer matches the tree it was built from.
/// </summary>
/// <remarks>
/// This is the runtime half of a pair. The build-time half is
/// <c>TestRig/src/build/compute-source-hash.ps1</c>, and the two must agree byte for
/// byte or the binary refuses on every run. <c>SourceHashParityTests</c> pins them
/// against each other by running the script and comparing.
///
/// Why this exists at all: a stale on-disk artifact has cost this project two whole
/// sessions (stale mods once, a stale game version once). A committed binary is a
/// third opportunity for the same mistake, so a mismatch is a refusal with a rebuild
/// command, never a warning that scrolls past.
/// </remarks>
public static class SourceHash
{
    /// <summary>
    /// File extensions that participate in the digest: everything that decides what
    /// the binary is.
    /// </summary>
    private static readonly string[] Extensions = [".cs", ".csproj", ".props", ".sln", ".slnx"];

    /// <summary>Result of a digest computation over a source tree.</summary>
    /// <param name="Hash">Lowercase hex SHA-256.</param>
    /// <param name="FileCount">How many files contributed.</param>
    public readonly record struct Result(string Hash, int FileCount);

    /// <summary>
    /// Computes the digest over <paramref name="srcRoot"/>.
    /// </summary>
    /// <remarks>
    /// Reproducibility rules, all of which exist because a clone may legitimately
    /// differ from this machine and must still produce the same digest:
    /// ordinal path sort (not culture-aware), CRLF normalised to LF, UTF-8 BOM
    /// stripped, relative path hashed with the content so a rename counts as a
    /// change, and bin/ and obj/ excluded as build output.
    /// </remarks>
    public static Result Compute(string srcRoot)
    {
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(srcRoot));

        var files = new List<(string Rel, string Full)>();
        foreach (var full in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            var ext = Path.GetExtension(full);
            if (Array.FindIndex(Extensions, e => string.Equals(e, ext, StringComparison.OrdinalIgnoreCase)) < 0)
                continue;

            var rel = full[(root.Length + 1)..].Replace('\\', '/');

            if (IsBuildOutput(rel)) continue;
            if (rel.EndsWith("SourceHash.g.cs", StringComparison.Ordinal)) continue;

            files.Add((rel, full));
        }

        files.Sort(static (a, b) => string.CompareOrdinal(a.Rel, b.Rel));

        using var sha = System.Security.Cryptography.IncrementalHash.CreateHash(
            System.Security.Cryptography.HashAlgorithmName.SHA256);

        foreach (var (rel, full) in files)
        {
            sha.AppendData(System.Text.Encoding.UTF8.GetBytes(rel + "\n"));
            sha.AppendData(Normalize(File.ReadAllBytes(full)));
            sha.AppendData("\n"u8);
        }

        return new Result(Convert.ToHexStringLower(sha.GetHashAndReset()), files.Count);
    }

    private static bool IsBuildOutput(string relForwardSlashed)
    {
        foreach (var segment in relForwardSlashed.Split('/'))
        {
            if (segment is "bin" or "obj") return true;
        }
        return false;
    }

    /// <summary>Strips a UTF-8 BOM and collapses CRLF to LF.</summary>
    private static byte[] Normalize(byte[] bytes)
    {
        var start = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF ? 3 : 0;

        var outBuf = new byte[bytes.Length - start];
        var n = 0;
        for (var i = start; i < bytes.Length; i++)
        {
            if (bytes[i] == 0x0D && i + 1 < bytes.Length && bytes[i + 1] == 0x0A) continue;
            outBuf[n++] = bytes[i];
        }

        return n == outBuf.Length ? outBuf : outBuf[..n];
    }
}
