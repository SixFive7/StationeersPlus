using TestRig.Core.Server;
using TestRig.Tests.Session.Fakes;
using Xunit;

namespace TestRig.Tests.Server;

/// <summary>
/// Watching the dedicated server's log without re-reading everything on every poll.
/// </summary>
public sealed class ServerLogWatcherTests
{
    private const string Log = @"C:\rig\data\server.log";

    private static FakeFileSystem WithLog(params string[] lines)
    {
        var fs = new FakeFileSystem();
        fs.AddFile(Log, lines.Length == 0 ? "" : string.Join("\r\n", lines) + "\r\n");
        return fs;
    }

    private static void Append(FakeFileSystem fs, params string[] lines) =>
        fs.AddFile(Log, fs.ReadAllText(Log) + string.Join("\r\n", lines) + "\r\n");

    [Fact]
    public void OnlyLinesAppendedAFTERTheWatcherWasBuiltAreReported()
    {
        var fs = WithLog("before one", "before two");
        var watcher = new ServerLogWatcher(fs, Log);

        Append(fs, "after one");
        Assert.Equal(["after one"], watcher.NewLines());
    }

    [Fact]
    public void EachLineIsSeenEXACTLYONCE()
    {
        // Spec D-12, SERVER-098: the PowerShell re-read and re-matched the entire appended
        // region every 500 ms, which grows quadratically over a 300 second budget on a server
        // writing steadily.
        var fs = WithLog("baseline");
        var watcher = new ServerLogWatcher(fs, Log);

        Append(fs, "one");
        Assert.Equal(["one"], watcher.NewLines());

        Append(fs, "two");
        Assert.Equal(["two"], watcher.NewLines());

        Append(fs, "three", "four");
        Assert.Equal(["three", "four"], watcher.NewLines());
    }

    [Fact]
    public void NothingChangedIsAnEmptyResultRatherThanTheWholeFile()
    {
        var fs = WithLog("baseline");
        var watcher = new ServerLogWatcher(fs, Log);

        Append(fs, "one");
        watcher.NewLines();

        Assert.Empty(watcher.NewLines());
        Assert.Empty(watcher.NewLines());
    }

    [Fact]
    public void ARotatedLogRestartsTheScanRatherThanSkippingEverythingAfterIt()
    {
        var fs = WithLog("old one", "old two", "old three", "old four");
        var watcher = new ServerLogWatcher(fs, Log);

        // The server rotated its log: the file shrank and everything the offsets described is
        // gone. Skipping to the old offset would hide the whole new file.
        fs.AddFile(Log, "fresh\r\n");
        Assert.Equal(["fresh"], watcher.NewLines());
    }

    [Fact]
    public void AMissingLogConfirmsNothingAndNeverThrows()
    {
        var fs = new FakeFileSystem();
        fs.AddDirectory(@"C:\rig\data");
        var watcher = new ServerLogWatcher(fs, Log);

        Assert.False(watcher.Exists);
        Assert.Empty(watcher.NewLines());

        // And a log that appears later is picked up from its beginning.
        fs.AddFile(Log, "first line\r\n");
        Assert.Equal(["first line"], watcher.NewLines());
    }

    [Fact]
    public void ALogThatCannotBeReadDegradesToNothingRatherThanFailingTheSave()
    {
        var fs = WithLog("baseline");
        var watcher = new ServerLogWatcher(fs, Log);

        Append(fs, "one");
        fs.ReadFailures[Path.GetFullPath(Log)] = "the file is locked";

        Assert.Empty(watcher.NewLines());
    }

    // ---- the timestamp stripping the anchoring depends on ------------------

    [Fact]
    public void ALeadingTimestampIsStrippedAndABracketedSOURCETAGIsNot()
    {
        Assert.Equal("Saved Luna", SaveConfirmation.StripTimestamp("12:04:55 Saved Luna"));
        Assert.Equal("Saved Luna", SaveConfirmation.StripTimestamp("12:04:55.123 Saved Luna"));
        Assert.Equal("Saved Luna", SaveConfirmation.StripTimestamp("[2026-08-14 12:04:55] Saved Luna"));

        // Stripping this is exactly what would let it match again.
        Assert.Equal("[Station Notepad] Saved file system", SaveConfirmation.StripTimestamp("[Station Notepad] Saved file system"));
    }
}
