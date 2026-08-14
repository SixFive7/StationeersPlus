using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using TestRig.Core.Abstractions;
using TestRig.Playtest.Model;

namespace TestRig.Playtest.Evidence;

/// <summary>A listing of the developer's own save folder. Never its contents.</summary>
/// <param name="Root">The folder that was listed.</param>
/// <param name="Exists">Whether it was there at all.</param>
/// <param name="FileCount">How many files the listing covers.</param>
/// <param name="Lines">One line per file: relative path, length, last write time.</param>
/// <param name="Sha256">Uppercase hex of the listing, or <see cref="SaveInventory.NoSuchRoot"/>.</param>
public sealed record SaveInventory(string Root, bool Exists, int FileCount, IReadOnlyList<string> Lines, string Sha256)
{
    /// <summary>The hash sentinel for a root that was not there.</summary>
    public const string NoSuchRoot = "no-such-root";
}

/// <summary>What comparing two listings said.</summary>
public enum Tier1Verdict
{
    /// <summary>The listing is byte-for-byte the same on both sides of the run.</summary>
    Identical,

    /// <summary>Something in the developer's save folder moved. Nothing in the rig may write there.</summary>
    Changed,

    /// <summary>
    ///     The root did not exist at either end, so nothing was watched.
    /// </summary>
    /// <remarks>
    ///     <b>Defect P-06.</b> Two missing roots both hashed to the sentinel, so they compared
    ///     equal, the verdict read IDENTICAL and the run reported a clean tier-1 safety
    ///     result. The root is computed in the harness's composition root, which had no tests
    ///     at all, so a wrong tier-1 path yielded a permanently green safety verdict: the one
    ///     check whose whole job is to notice the rig touching the developer's saves could
    ///     never fail. This is now its own verdict and it is reported loudly.
    /// </remarks>
    RootMissing,
}

/// <summary>The result of comparing the before and after listings.</summary>
public sealed record SaveInventoryComparison(
    Tier1Verdict Verdict,
    SaveInventory Before,
    SaveInventory After,
    IReadOnlyList<string> Added,
    IReadOnlyList<string> Removed)
{
    /// <summary>True only when the folder was watched AND did not move.</summary>
    public bool Identical => Verdict == Tier1Verdict.Identical;
}

/// <summary>
///     The harness's only contact with the developer's own save folder.
/// </summary>
/// <remarks>
///     <b>No file is ever opened.</b> The listing is path, length and last write time, and the
///     hash is over that text. Tier 1 is off-limits unconditionally: not read, not copied, not
///     written. A hash that changed when the bytes changed but the metadata did not would mean
///     something here had read a save.
/// </remarks>
public static class SaveInventoryScanner
{
    /// <summary>Lists a folder and hashes the listing.</summary>
    public static SaveInventory Capture(IFileSystem files, string root)
    {
        ArgumentNullException.ThrowIfNull(files);

        if (string.IsNullOrWhiteSpace(root) || !files.DirectoryExists(root))
            return new SaveInventory(root ?? string.Empty, false, 0, [], SaveInventory.NoSuchRoot);

        List<string> paths;
        try
        {
            paths = [.. files.EnumerateFiles(root, "*", recurse: true)];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // An enumeration that failed is not an empty folder. Reporting it as one is how
            // the reset planner came to believe 25 real worlds were untracked.
            return new SaveInventory(root, true, 0, [], "enumeration-failed");
        }

        paths.Sort(StringComparer.OrdinalIgnoreCase);

        var lines = new List<string>(paths.Count);
        foreach (var path in paths)
        {
            var relative = path.StartsWith(root, StringComparison.OrdinalIgnoreCase)
                ? path[root.Length..].TrimStart('\\', '/')
                : path;

            lines.Add(string.Create(CultureInfo.InvariantCulture,
                $"{relative}|{files.GetFileLength(path)}|{Stamps.Format(files.GetLastWriteTimeUtc(path))}"));
        }

        return new SaveInventory(root, true, lines.Count, lines, Hash(lines));
    }

    /// <summary>Uppercase hex SHA-256 of the listing text, lines joined with a newline.</summary>
    public static string Hash(IReadOnlyList<string> lines)
    {
        ArgumentNullException.ThrowIfNull(lines);
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\n', lines)));
        return Convert.ToHexString(bytes);
    }

    /// <summary>Compares two listings, with a missing root as its own verdict.</summary>
    public static SaveInventoryComparison Compare(SaveInventory before, SaveInventory after)
    {
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(after);

        var beforeSet = new HashSet<string>(before.Lines, StringComparer.Ordinal);
        var afterSet = new HashSet<string>(after.Lines, StringComparer.Ordinal);

        var added = after.Lines.Where(line => !beforeSet.Contains(line)).ToList();
        var removed = before.Lines.Where(line => !afterSet.Contains(line)).ToList();

        var verdict = (before.Exists, after.Exists) switch
        {
            (false, false) => Tier1Verdict.RootMissing,
            (true, true) when string.Equals(before.Sha256, after.Sha256, StringComparison.Ordinal) => Tier1Verdict.Identical,
            _ => Tier1Verdict.Changed,
        };

        return new SaveInventoryComparison(verdict, before, after, added, removed);
    }

    /// <summary>The header-and-listing text written to save-inventory-&lt;when&gt;.txt.</summary>
    public static string Render(SaveInventory inventory, string capturedAt)
    {
        ArgumentNullException.ThrowIfNull(inventory);

        var builder = new StringBuilder();
        builder.Append("# The developer save folder (tier 1) is listed, never read. No file here is ever opened.\n");
        builder.Append(CultureInfo.InvariantCulture, $"# root      : {inventory.Root}\n");
        builder.Append(CultureInfo.InvariantCulture, $"# exists    : {inventory.Exists}\n");
        builder.Append(CultureInfo.InvariantCulture, $"# files     : {inventory.FileCount}\n");
        builder.Append(CultureInfo.InvariantCulture, $"# sha256    : {inventory.Sha256}\n");
        builder.Append(CultureInfo.InvariantCulture, $"# capturedAt: {capturedAt}\n");
        foreach (var line in inventory.Lines) builder.Append(line).Append('\n');
        return builder.ToString();
    }

    /// <summary>The verdict file, including what moved.</summary>
    public static string RenderVerdict(SaveInventoryComparison comparison)
    {
        ArgumentNullException.ThrowIfNull(comparison);

        var builder = new StringBuilder();
        builder.Append("# The developer save folder (tier 1) is off limits to the rig. This is a listing hash on either side of the run.\n");
        builder.Append(CultureInfo.InvariantCulture, $"root     : {comparison.Before.Root}\n");
        builder.Append(CultureInfo.InvariantCulture, $"before   : {comparison.Before.Sha256}\n");
        builder.Append(CultureInfo.InvariantCulture, $"after    : {comparison.After.Sha256}\n");
        builder.Append(CultureInfo.InvariantCulture, $"verdict  : {VerdictText(comparison.Verdict)}\n");

        if (comparison.Verdict == Tier1Verdict.RootMissing)
        {
            builder.Append(
                "\n# The root did not exist at either end of the run, so NOTHING WAS WATCHED. This is not a clean\n" +
                "# result: it means the tier-1 path is wrong, and the one check whose job is to notice the rig\n" +
                "# writing into the developer's saves could not have failed.\n");
        }

        builder.Append("\n# added\n");
        foreach (var line in comparison.Added) builder.Append(line).Append('\n');
        builder.Append("\n# removed\n");
        foreach (var line in comparison.Removed) builder.Append(line).Append('\n');
        return builder.ToString();
    }

    /// <summary>How a verdict prints.</summary>
    public static string VerdictText(Tier1Verdict verdict) => verdict switch
    {
        Tier1Verdict.Identical => "IDENTICAL",
        Tier1Verdict.Changed => "CHANGED",
        _ => "ROOT MISSING",
    };
}
