using System.Text;
using TestRig.Core.Infrastructure;
using TestRig.Playtest.Evidence;
using TestRig.Tests.Infrastructure;
using Xunit;

namespace TestRig.Tests.Playtest;

/// <summary>
///     The developer's own save folder is LISTED, never read.
/// </summary>
/// <remarks>
///     Real files on a real volume, because the property that matters can only be satisfied by
///     an implementation that never opens a file: change the bytes, keep the length and the
///     write time, and the hash must not move. This is the strongest test in the suite and it
///     is worth nothing against a fake filesystem.
/// </remarks>
public sealed class SaveInventoryTests
{
    [Fact]
    public void TheListingIsPathLengthAndWriteTimeAndNothingElse()
    {
        using var temp = new TempDirectory("tier1-listing");
        var files = new SystemFileSystem();

        File.WriteAllText(Path.Combine(temp.Path, "world.save"), "hello");
        var inventory = SaveInventoryScanner.Capture(files, temp.Path);

        Assert.True(inventory.Exists);
        Assert.Equal(1, inventory.FileCount);
        Assert.Contains("world.save|5|", inventory.Lines[0], StringComparison.Ordinal);
    }

    [Fact]
    public void ChangingTheBYTESWithoutChangingLengthOrWriteTimeDoesNotMoveTheHash()
    {
        // The core property. A hash that moved here would mean something in the harness had
        // opened a file in the developer's save folder, which is off limits unconditionally.
        using var temp = new TempDirectory("tier1-bytes");
        var files = new SystemFileSystem();
        var path = Path.Combine(temp.Path, "world.save");

        File.WriteAllBytes(path, Encoding.UTF8.GetBytes("AAAAA"));
        var stamp = new DateTime(2026, 8, 14, 12, 0, 0, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(path, stamp);
        var before = SaveInventoryScanner.Capture(files, temp.Path);

        File.WriteAllBytes(path, Encoding.UTF8.GetBytes("BBBBB"));
        File.SetLastWriteTimeUtc(path, stamp);
        var after = SaveInventoryScanner.Capture(files, temp.Path);

        Assert.Equal(before.Sha256, after.Sha256);
        Assert.Equal(Tier1Verdict.Identical, SaveInventoryScanner.Compare(before, after).Verdict);
    }

    [Fact]
    public void AddingAFileChangesTheVerdictAndIsNamed()
    {
        using var temp = new TempDirectory("tier1-added");
        var files = new SystemFileSystem();

        File.WriteAllText(Path.Combine(temp.Path, "a.save"), "a");
        var before = SaveInventoryScanner.Capture(files, temp.Path);

        File.WriteAllText(Path.Combine(temp.Path, "b.save"), "b");
        var after = SaveInventoryScanner.Capture(files, temp.Path);

        var comparison = SaveInventoryScanner.Compare(before, after);
        Assert.Equal(Tier1Verdict.Changed, comparison.Verdict);
        Assert.False(comparison.Identical);
        Assert.Single(comparison.Added);
        Assert.Contains("b.save", comparison.Added[0], StringComparison.Ordinal);
        Assert.Empty(comparison.Removed);
    }

    [Fact]
    public void RemovingAFileIsNamedToo()
    {
        using var temp = new TempDirectory("tier1-removed");
        var files = new SystemFileSystem();

        File.WriteAllText(Path.Combine(temp.Path, "a.save"), "a");
        File.WriteAllText(Path.Combine(temp.Path, "b.save"), "b");
        var before = SaveInventoryScanner.Capture(files, temp.Path);

        File.Delete(Path.Combine(temp.Path, "b.save"));
        var after = SaveInventoryScanner.Capture(files, temp.Path);

        var comparison = SaveInventoryScanner.Compare(before, after);
        Assert.Equal(Tier1Verdict.Changed, comparison.Verdict);
        Assert.Single(comparison.Removed);
    }

    [Fact]
    public void TheListingIsRecursiveAndSorted()
    {
        using var temp = new TempDirectory("tier1-recursive");
        var files = new SystemFileSystem();

        Directory.CreateDirectory(Path.Combine(temp.Path, "world", "autosave"));
        File.WriteAllText(Path.Combine(temp.Path, "world", "autosave", "z.xml"), "z");
        File.WriteAllText(Path.Combine(temp.Path, "a.xml"), "a");

        var inventory = SaveInventoryScanner.Capture(files, temp.Path);
        Assert.Equal(2, inventory.FileCount);
        Assert.StartsWith("a.xml|", inventory.Lines[0], StringComparison.Ordinal);
    }

    [Fact]
    public void AMissingRootIsItsOwnState()
    {
        var files = new SystemFileSystem();
        var inventory = SaveInventoryScanner.Capture(files, @"C:\no\such\folder\anywhere");

        Assert.False(inventory.Exists);
        Assert.Equal(0, inventory.FileCount);
        Assert.Equal(SaveInventory.NoSuchRoot, inventory.Sha256);
    }

    [Fact]
    public void TwoMissingRootsDoNotCompareIdentical()
    {
        // Defect P-06, the highest-severity safety defect in the harness. Both missing roots
        // hashed to the same sentinel, so they compared equal, the verdict read IDENTICAL, and
        // the one check whose whole job is to notice the rig writing into the developer's
        // saves could never have failed. The root came from the composition root, which had no
        // tests at all.
        var files = new SystemFileSystem();
        var before = SaveInventoryScanner.Capture(files, @"C:\no\such\folder");
        var after = SaveInventoryScanner.Capture(files, @"C:\no\such\folder");

        var comparison = SaveInventoryScanner.Compare(before, after);
        Assert.Equal(Tier1Verdict.RootMissing, comparison.Verdict);
        Assert.False(comparison.Identical);
    }

    [Fact]
    public void AVanishedRootIsAChangeAndNotAMissingRoot()
    {
        using var temp = new TempDirectory("tier1-vanished");
        var files = new SystemFileSystem();
        File.WriteAllText(Path.Combine(temp.Path, "a.save"), "a");

        var before = SaveInventoryScanner.Capture(files, temp.Path);
        var after = SaveInventoryScanner.Capture(files, Path.Combine(temp.Path, "gone"));

        Assert.Equal(Tier1Verdict.Changed, SaveInventoryScanner.Compare(before, after).Verdict);
    }

    [Fact]
    public void TheVerdictFileShoutsAboutAMissingRootRatherThanReportingItClean()
    {
        var files = new SystemFileSystem();
        var missing = SaveInventoryScanner.Capture(files, @"C:\no\such\folder");
        var verdict = SaveInventoryScanner.RenderVerdict(SaveInventoryScanner.Compare(missing, missing));

        Assert.Contains("ROOT MISSING", verdict, StringComparison.Ordinal);
        Assert.Contains("NOTHING WAS WATCHED", verdict, StringComparison.Ordinal);
        Assert.Contains("tier-1 path is wrong", verdict, StringComparison.Ordinal);
    }

    [Fact]
    public void TheVerdictFileCarriesBothHashesAndWhatMoved()
    {
        using var temp = new TempDirectory("tier1-verdict");
        var files = new SystemFileSystem();
        File.WriteAllText(Path.Combine(temp.Path, "a.save"), "a");
        var before = SaveInventoryScanner.Capture(files, temp.Path);
        File.WriteAllText(Path.Combine(temp.Path, "b.save"), "b");
        var after = SaveInventoryScanner.Capture(files, temp.Path);

        var verdict = SaveInventoryScanner.RenderVerdict(SaveInventoryScanner.Compare(before, after));
        Assert.Contains("CHANGED", verdict, StringComparison.Ordinal);
        Assert.Contains(before.Sha256, verdict, StringComparison.Ordinal);
        Assert.Contains(after.Sha256, verdict, StringComparison.Ordinal);
        Assert.Contains("# added", verdict, StringComparison.Ordinal);
    }

    [Fact]
    public void TheInventoryFileCarriesAHeaderSayingWhatItIsAndIsNot()
    {
        using var temp = new TempDirectory("tier1-header");
        var files = new SystemFileSystem();
        File.WriteAllText(Path.Combine(temp.Path, "a.save"), "a");

        var rendered = SaveInventoryScanner.Render(SaveInventoryScanner.Capture(files, temp.Path), "2026-08-14T12:00:00Z");
        Assert.Contains("listed, never read", rendered, StringComparison.Ordinal);
        Assert.Contains("# files     : 1", rendered, StringComparison.Ordinal);
        Assert.Contains("# capturedAt: 2026-08-14T12:00:00Z", rendered, StringComparison.Ordinal);
    }

    [Fact]
    public void VerdictTextIsWhatAReportPrints()
    {
        Assert.Equal("IDENTICAL", SaveInventoryScanner.VerdictText(Tier1Verdict.Identical));
        Assert.Equal("CHANGED", SaveInventoryScanner.VerdictText(Tier1Verdict.Changed));
        Assert.Equal("ROOT MISSING", SaveInventoryScanner.VerdictText(Tier1Verdict.RootMissing));
    }

    [Fact]
    public void ScanningNeverWritesAnythingIntoTheFolderItIsWatching()
    {
        using var temp = new TempDirectory("tier1-readonly");
        var files = new SystemFileSystem();
        File.WriteAllText(Path.Combine(temp.Path, "a.save"), "a");

        var before = Directory.GetFileSystemEntries(temp.Path, "*", SearchOption.AllDirectories).OrderBy(p => p, StringComparer.Ordinal).ToArray();
        SaveInventoryScanner.Capture(files, temp.Path);
        var after = Directory.GetFileSystemEntries(temp.Path, "*", SearchOption.AllDirectories).OrderBy(p => p, StringComparer.Ordinal).ToArray();

        Assert.Equal(before, after);
    }
}
