namespace TestRig.Core;

/// <summary>
/// Recomputes the digest of everything the rig binary is compiled from, so a committed
/// binary can tell that it no longer matches the tree it was built from.
/// </summary>
/// <remarks>
/// <para>
/// One implementation, called twice: <c>TestRig.BuildTool</c> calls it at build time to bake
/// the value in, and the binary calls it at startup to recompute it. An earlier design had a
/// PowerShell script compute the value and C# recompute it, which is two implementations that
/// had to agree byte for byte forever. One implementation cannot disagree with itself.
/// </para>
/// <para>
/// Why this exists at all: a stale on-disk artifact has cost this project two whole sessions
/// (stale mods once, a stale game version once). A committed binary is a third opportunity for
/// the same mistake, so a mismatch is a refusal with a rebuild command, never a warning that
/// scrolls past.
/// </para>
/// <para>
/// <b>It covers the checks as well as the rig.</b> The digest was scoped to
/// <c>TestRig/src/</c> alone while playtest checks live in <c>Mods/&lt;Mod&gt;/playtests/</c>
/// and are compiled into the binary, so editing, adding or deleting a check did not
/// invalidate the artifact at all: an agent could change what a check measures, forget the
/// rebuild, and the guard whose entire job is catching that would say nothing. See
/// <see cref="SourceRoots"/>.
/// </para>
/// </remarks>
public static class SourceHash
{
    /// <summary>
    /// File extensions that participate in the digest: everything that decides what
    /// the binary is.
    /// </summary>
    private static readonly string[] Extensions = [".cs", ".csproj", ".props", ".sln", ".slnx"];

    /// <summary>Result of a digest computation over the source trees.</summary>
    /// <param name="Hash">Lowercase hex SHA-256.</param>
    /// <param name="FileCount">How many files contributed.</param>
    public readonly record struct Result(string Hash, int FileCount);

    /// <summary>
    /// Computes the digest over <paramref name="srcRoot"/> and every check tree beside it.
    /// </summary>
    /// <remarks>
    /// Reproducibility rules, all of which exist because a clone may legitimately
    /// differ from this machine and must still produce the same digest:
    /// ordinal path sort (not culture-aware), CRLF normalised to LF, UTF-8 BOM
    /// stripped, the path hashed with the content so a rename counts as a change,
    /// and bin/ and obj/ excluded as build output.
    ///
    /// The hashed path is the root's LABEL plus the file's path within it, never an absolute
    /// path, so a repository cloned to a different folder produces the same digest.
    /// </remarks>
    public static Result Compute(string srcRoot) => Compute(SourceRoots.For(srcRoot));

    /// <summary>Computes the digest over an explicit set of labelled roots.</summary>
    public static Result Compute(IReadOnlyList<SourceRoot> roots)
    {
        ArgumentNullException.ThrowIfNull(roots);

        var files = new List<(string Key, string Full)>();

        foreach (var source in roots)
        {
            var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(source.Path));
            if (!Directory.Exists(root)) continue;

            foreach (var full in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
            {
                var ext = Path.GetExtension(full);
                if (Array.FindIndex(Extensions, e => string.Equals(e, ext, StringComparison.OrdinalIgnoreCase)) < 0)
                    continue;

                var rel = full[(root.Length + 1)..].Replace('\\', '/');

                if (IsBuildOutput(rel)) continue;
                if (rel.EndsWith("SourceHash.g.cs", StringComparison.Ordinal)) continue;

                files.Add((source.Label + "/" + rel, full));
            }
        }

        files.Sort(static (a, b) => string.CompareOrdinal(a.Key, b.Key));

        using var sha = System.Security.Cryptography.IncrementalHash.CreateHash(
            System.Security.Cryptography.HashAlgorithmName.SHA256);

        foreach (var (key, full) in files)
        {
            sha.AppendData(System.Text.Encoding.UTF8.GetBytes(key + "\n"));
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

/// <summary>One tree the digest covers, and the stable name it is hashed under.</summary>
/// <param name="Label">Repository-relative and clone-independent, e.g. <c>Mods/SprayPaintPlus/playtests</c>.</param>
/// <param name="Path">Where that tree is on this machine.</param>
public readonly record struct SourceRoot(string Label, string Path);

/// <summary>
/// Every tree the rig binary is compiled from.
/// </summary>
/// <remarks>
/// Derived from the one path a caller has, rather than passed in twice: the build tool and
/// the startup guard both know only <c>TestRig/src/</c>, and a second list they each had to
/// keep in step is the drift this whole file exists to remove.
/// </remarks>
public static class SourceRoots
{
    /// <summary>The folder inside a mod that holds its playtest checks.</summary>
    public const string PlaytestsFolder = "playtests";

    /// <summary>The two trees mods live in. Checks in either are compiled in.</summary>
    public static readonly IReadOnlyList<string> ModTrees = ["Mods", "Plans"];

    /// <summary>
    /// The rig source tree plus every check tree in the repository above it.
    /// </summary>
    /// <remarks>
    /// A missing repository root is not an error: the binary is also run from a copy with a
    /// planted <c>src/</c> beside it and nothing else, and from its own bin folder. Roots that
    /// do not exist simply contribute nothing.
    /// </remarks>
    public static IReadOnlyList<SourceRoot> For(string srcRoot)
    {
        var roots = new List<SourceRoot> { new("src", srcRoot) };

        // <repo>/TestRig/src -> <repo>. GetDirectoryName twice, not a string chop, so a
        // trailing separator cannot silently move the answer up a level.
        var rigHome = Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(Path.GetFullPath(srcRoot)));
        var repoRoot = rigHome is null ? null : Path.GetDirectoryName(rigHome);
        if (repoRoot is null) return roots;

        foreach (var tree in ModTrees)
        {
            var treeRoot = Path.Combine(repoRoot, tree);
            if (!Directory.Exists(treeRoot)) continue;

            foreach (var modDir in Directory.EnumerateDirectories(treeRoot))
            {
                var checks = Path.Combine(modDir, PlaytestsFolder);
                if (!Directory.Exists(checks)) continue;

                roots.Add(new SourceRoot($"{tree}/{Path.GetFileName(modDir)}/{PlaytestsFolder}", checks));
            }
        }

        // Ordinal by label, so the enumeration order of the filesystem cannot change a digest.
        roots.Sort(static (a, b) => string.CompareOrdinal(a.Label, b.Label));
        return roots;
    }
}
