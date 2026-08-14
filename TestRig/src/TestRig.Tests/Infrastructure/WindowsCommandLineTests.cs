using System.ComponentModel;
using System.Runtime.InteropServices;
using TestRig.Core.Infrastructure;
using Xunit;

namespace TestRig.Tests.Infrastructure;

/// <summary>
/// Command-line quoting, checked against the OS parser rather than against itself.
/// </summary>
/// <remarks>
/// The round-trip tests split the built line with CommandLineToArgvW, which is the same
/// parser a launched program's own startup code uses. That is the only way to make this
/// suite mean anything: a test that compared the output against a hand-written expected
/// string would agree with whatever the implementation happened to do, which is how the
/// trailing-backslash bug survived in the PowerShell with five assertions covering the
/// function.
///
/// Note argv[0] is parsed by different rules (no backslash escaping), so every round trip
/// prepends a program token and compares from index 1, exactly as a real launch does.
/// </remarks>
public sealed class WindowsCommandLineTests
{
    // ---- parity with the five assertions the PowerShell suite made -------

    [Theory]
    [InlineData("a b", "\"a b\"")]
    [InlineData("plain", "plain")]
    [InlineData(@"C:\rig\x.ps1", @"C:\rig\x.ps1")]
    [InlineData("say \"hi\"", "\"say \\\"hi\\\"\"")]
    [InlineData("", "\"\"")]
    public void QuoteArgument_MatchesTheBehaviourThePowerShellSuitePinned(string value, string expected)
    {
        Assert.Equal(expected, WindowsCommandLine.QuoteArgument(value));
    }

    [Fact]
    public void QuoteArgument_TreatsNullAsAnEmptyArgument()
    {
        Assert.Equal("\"\"", WindowsCommandLine.QuoteArgument(null));
    }

    // ---- the fix ---------------------------------------------------------

    [Fact]
    public void QuoteArgument_DoublesATrailingBackslashSoItDoesNotEscapeTheClosingQuote()
    {
        // The PowerShell produced "E:\Stationeers Rig\", whose final backslash escapes the
        // closing quote, so the receiving program reads one argument beginning
        // E:\Stationeers Rig" and then swallows every remaining token on the line.
        const string value = @"E:\Stationeers Rig\";

        var quoted = WindowsCommandLine.QuoteArgument(value);

        Assert.Equal(@"""E:\Stationeers Rig\\""", quoted);
        Assert.Equal([value], RoundTrip(value));
    }

    [Fact]
    public void QuoteArgument_TrailingBackslashesSurviveInEveryCount()
    {
        foreach (var count in new[] { 1, 2, 3, 4, 7 })
        {
            var value = @"E:\Rig Root" + new string('\\', count);
            Assert.Equal([value], RoundTrip(value));
        }
    }

    [Fact]
    public void QuoteArgument_DoublesBackslashesBeforeAnEmbeddedQuote()
    {
        // The other half of the same bug: a run before a quote also has to be doubled, or
        // the quote it was meant to escape is read as a delimiter.
        const string value = @"a\""b";

        Assert.Equal([value], RoundTrip(value));
    }

    [Fact]
    public void TheNaiveQuotingThePowerShellUsedDoesNotRoundTrip()
    {
        // Proves the tests above are not vacuous: this is exactly what
        // '"' + ($Value -replace '"', '\"') + '"' produced, and the OS parser does not
        // give the argument back.
        const string value = @"E:\Stationeers Rig\";
        var naive = "\"" + value.Replace("\"", "\\\"") + "\"";

        var parsed = SplitCommandLine("program.exe " + naive);

        Assert.NotEqual([value], parsed[1..]);
    }

    // ---- round trips over the shapes the rig actually passes -------------

    [Theory]
    [InlineData("plain")]
    [InlineData("a b")]
    [InlineData("the first-use notice cap")]
    [InlineData(@"C:\rig\testrig.ps1")]
    [InlineData(@"E:\StationeersRig\host1\rocketstation.exe")]
    [InlineData("My World")]
    [InlineData("say \"hi\"")]
    [InlineData("trailing space ")]
    [InlineData(" leading space")]
    [InlineData("tab\there")]
    [InlineData(@"{""hard"":false}")]
    [InlineData(@"C:\a b\c\")]
    [InlineData(@"\\server\share\file")]
    [InlineData(@"\\?\C:\long\path")]
    public void QuoteArgument_RoundTripsThroughTheOsParser(string value)
    {
        Assert.Equal([value], RoundTrip(value));
    }

    [Fact]
    public void Build_RoundTripsAWholeArgumentVector()
    {
        // The launch vector shape from the client half, plus the lock purpose string that
        // broke every playtest check when it was joined unquoted.
        string[] argv =
        [
            @"E:\StationeersRig\host1\rocketstation.exe",
            "-logFile",
            @"C:\rig\ClientRig\data\host 1\logs\unity-20260814-031500.log",
            "-settingspath",
            @"C:\rig\ClientRig\data\host 1\setting.xml",
            "-screen-width",
            "800",
            "-screen-fullscreen",
            "0",
            "-Purpose",
            "the first-use notice cap",
        ];

        var line = WindowsCommandLine.Build(argv);
        var parsed = SplitCommandLine(line);

        Assert.Equal(argv, parsed);
    }

    [Fact]
    public void Build_KeepsAnEmptyArgumentAsAPosition()
    {
        string?[] argv = ["program.exe", "", "after"];

        var parsed = SplitCommandLine(WindowsCommandLine.Build(argv));

        Assert.Equal(["program.exe", "", "after"], parsed);
    }

    [Fact]
    public void Build_JoinsWithSingleSpaces()
    {
        Assert.Equal("a \"b c\" d", WindowsCommandLine.Build("a", "b c", "d"));
        Assert.Equal(string.Empty, WindowsCommandLine.Build([]));
    }

    /// <summary>
    /// Quotes one argument, hands the line to the OS parser, and returns what came back.
    /// </summary>
    private static string[] RoundTrip(string value) =>
        SplitCommandLine("program.exe " + WindowsCommandLine.QuoteArgument(value))[1..];

    // ---- the oracle ------------------------------------------------------

    private static string[] SplitCommandLine(string commandLine)
    {
        var argv = CommandLineToArgvW(commandLine, out var count);
        if (argv == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());

        try
        {
            var result = new string[count];
            for (var i = 0; i < count; i++)
            {
                var element = Marshal.ReadIntPtr(argv, i * IntPtr.Size);
                result[i] = Marshal.PtrToStringUni(element) ?? string.Empty;
            }

            return result;
        }
        finally
        {
            LocalFree(argv);
        }
    }

    [DllImport("shell32.dll", EntryPoint = "CommandLineToArgvW", SetLastError = true,
        CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern IntPtr CommandLineToArgvW(string lpCmdLine, out int pNumArgs);

    [DllImport("kernel32.dll", ExactSpelling = true)]
    private static extern IntPtr LocalFree(IntPtr hMem);
}
