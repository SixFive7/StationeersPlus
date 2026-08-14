using System.Text;
using TestRig.Contracts;
using TestRig.Playtest.Model;
using TestRig.Playtest.Readers;
using TestRig.Playtest.Seams;
using TestRig.Playtest.Values;
using TestRig.Tests.Infrastructure;
using TestRig.Tests.Playtest.Fakes;
using Xunit;

namespace TestRig.Tests.Playtest;

/// <summary>
///     The BepInEx log reader, against real files.
/// </summary>
/// <remarks>
///     The sharing mode is the only interesting thing about this reader, and it only behaves
///     like itself on a real volume: the game holds the log open for append for as long as it
///     runs, so a plain read fails exactly when a check needs it most.
/// </remarks>
public sealed class BepInExLogReaderTests
{
    private static string WriteLog(TempDirectory temp, params string[] lines)
    {
        var path = Path.Combine(temp.Path, "LogOutput.log");
        File.WriteAllLines(path, lines);
        return path;
    }

    [Fact]
    public void ItCountsEveryMatchingLine()
    {
        using var temp = new TempDirectory("bepinexlog-count");
        var path = WriteLog(temp, "CONFLICT: ColorCycler.dll is loaded", "unrelated", "CONFLICT: NetworkPainter.dll is loaded");

        var reading = BepInExLogReader.Read(new SystemLogFiles(), "hostie", path, "CONFLICT", 0);
        Assert.True(reading.Ok);
        Assert.Equal(2, reading.Count);
        Assert.Equal(3, reading.TotalLines);
    }

    [Fact]
    public void ItReadsAFileTheGameStillHoldsOpenForAppend()
    {
        using var temp = new TempDirectory("bepinexlog-shared");
        var path = Path.Combine(temp.Path, "LogOutput.log");

        using var writer = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
        writer.Write(Encoding.UTF8.GetBytes("TEST FIXTURE ACTIVE: ColorCycler\nTEST FIXTURE ACTIVE: NetworkPainter\n"));
        writer.Flush();

        var reading = BepInExLogReader.Read(new SystemLogFiles(), "hostie", path, "TEST FIXTURE ACTIVE", 0);
        Assert.Equal(2, reading.Count);
    }

    [Fact]
    public void AnAbsentFileIsADistinguishableFactRatherThanACountOfZero()
    {
        using var temp = new TempDirectory("bepinexlog-absent");
        var reading = BepInExLogReader.Read(new SystemLogFiles(), "hostie", Path.Combine(temp.Path, "nothing.log"), null, 0);

        Assert.False(reading.Exists);
        Assert.False(reading.Ok);
        Assert.Equal(0, reading.Count);
    }

    [Fact]
    public void TheLimitClipsTheLinesAndNeverTheCount()
    {
        // A check counting six banner lines with a limit of five must read 6 and FAIL, not
        // read 5 and pass.
        using var temp = new TempDirectory("bepinexlog-limit");
        var path = WriteLog(temp, [.. Enumerable.Repeat("NOT LOADED! Conflicting mods:", 6)]);

        var reading = BepInExLogReader.Read(new SystemLogFiles(), "hostie", path, "NOT LOADED", 5);
        Assert.Equal(6, reading.Count);
        Assert.Equal(5, reading.Lines.Count);
    }

    [Fact]
    public void ALineFiveThousandRowsDeepIsStillFound()
    {
        using var temp = new TempDirectory("bepinexlog-deep");
        var lines = new List<string>(Enumerable.Repeat("noise", 5000)) { "SprayPaintPlus NOT LOADED" };
        var path = WriteLog(temp, [.. lines]);

        var reading = BepInExLogReader.Read(new SystemLogFiles(), "hostie", path, "SprayPaintPlus NOT LOADED", 0);
        Assert.Equal(1, reading.Count);
        Assert.Equal(5001, reading.TotalLines);
    }

    [Fact]
    public void EmptyLinesAreNotCounted()
    {
        using var temp = new TempDirectory("bepinexlog-blank");
        var path = WriteLog(temp, "one", string.Empty, "two", string.Empty);

        var reading = BepInExLogReader.Read(new SystemLogFiles(), "hostie", path, null, 0);
        Assert.Equal(2, reading.Count);
    }

    [Fact]
    public void TheFilterIsCaseInsensitiveJustLikeTheConsoleEndpointsOwn()
    {
        // Defect P-14. The reader filtered case-SENSITIVELY while the console endpoint's own
        // contains is case-INSENSITIVE, and the two readers are documented as interchangeable:
        // "a check switches reader name and nothing else".
        using var temp = new TempDirectory("bepinexlog-case");
        var path = WriteLog(temp, "SprayPaintPlus NOT LOADED");

        Assert.Equal(1, BepInExLogReader.Read(new SystemLogFiles(), "hostie", path, "spraypaintplus not loaded", 0).Count);
    }

    [Fact]
    public void ALineRowNamesItsSourceTheSameWayTheConsoleEndpointDoes()
    {
        // Defect P-15: the reader emitted rows as {source, text} while the console endpoint
        // emits {seq, t, src, level, text}. Same interchangeability claim, same breakage.
        using var temp = new TempDirectory("bepinexlog-rows");
        var path = WriteLog(temp, "a line");

        var node = BepInExLogReader.Read(new SystemLogFiles(), "hostie", path, null, 0).ToNode();
        Assert.Equal(BepInExLogReader.SourceLabel, ValueText.Render(SelectPath.Select(node, "lines[0].src")));
        Assert.Equal("a line", ValueText.Render(SelectPath.Select(node, "lines[0].text")));

        // The property name matches TestRig.Contracts.ConsoleLine, which is where the console
        // endpoint's own rows come from.
        Assert.Equal("src", typeof(ConsoleLine).GetProperty(nameof(ConsoleLine.Src))!
            .GetCustomAttributes(typeof(System.Text.Json.Serialization.JsonPropertyNameAttribute), false)
            .Cast<System.Text.Json.Serialization.JsonPropertyNameAttribute>().Single().Name);
    }

    [Fact]
    public void ExistsAndCountAreBothSelectable()
    {
        using var temp = new TempDirectory("bepinexlog-select");
        var path = WriteLog(temp, "a", "b");
        var node = BepInExLogReader.Read(new SystemLogFiles(), "hostie", path, null, 0).ToNode();

        Assert.Equal("True", ValueText.Render(SelectPath.Select(node, "exists")));
        Assert.Equal("2", ValueText.Render(SelectPath.Select(node, "count")));
    }

    [Fact]
    public void TheReaderGoesThroughTheNormalObservationPathAndLandsInTheBundle()
    {
        var fixture = new PlaytestFixture().WithInstance("hostie", 27701);
        fixture.LogFiles.Files[@"E:\rig\instances\hostie\BepInEx\LogOutput.log"] = ["CONFLICT: ColorCycler.dll is loaded"];

        var ctx = fixture.Context(new CheckSpec("a check", "s", [new InstanceSpec("hostie")]));
        var observation = ctx.Read("hostie", Reader.BepInExLog, "count", readerArgs: new BepInExLogRequest("CONFLICT"));

        Assert.Equal("1", observation.Text);
        Assert.StartsWith("FILE ", observation.Source, StringComparison.Ordinal);
        Assert.Contains(fixture.Files.AllFiles(), f => f.Contains("observations", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void TheLogPathComesFromTheInstancesRootAndNotFromTheRigHome()
    {
        // Two roots, and both are correct: the game TREE is under the instances root and the
        // instance DATA is under the rig home. This fallback used to be missing, so an entry
        // written before the root was recorded resolved to a path that never existed and the
        // read came back "absent" rather than wrong.
        var fixture = new PlaytestFixture().WithInstance("hostie", 27701);
        fixture.LogFiles.Files[@"E:\rig\instances\hostie\BepInEx\LogOutput.log"] = ["found via the instances root"];

        var ctx = fixture.Context(new CheckSpec("a check", "s", [new InstanceSpec("hostie")]));
        Assert.Equal("True", ctx.Read("hostie", Reader.BepInExLog, "exists").Text);
    }

    [Fact]
    public void TheReaderRefusesAReaderArgsShapeThatIsNotItsOwn()
    {
        var fixture = new PlaytestFixture().WithInstance("hostie", 27701);
        var ctx = fixture.Context(new CheckSpec("a check", "s", [new InstanceSpec("hostie")]));

        var thrown = Assert.Throws<PlaytestUsageException>(() =>
            ctx.Read("hostie", Reader.BepInExLog, "count", readerArgs: new ConsoleLogRequest { Contains = "x" }));

        Assert.Contains("reads a FILE", thrown.Message, StringComparison.Ordinal);
    }
}
