using System.Diagnostics;
using System.Text;
using System.Text.Json;
using TestRig.Tests.Infrastructure;
using Xunit;

namespace TestRig.Tests.Playtest;

/// <summary>
///     What the SHIPPED binary answers, which is the only thing that settles this question.
/// </summary>
/// <remarks>
///     <para>
///     <b>The defect this exists for.</b> <c>TestRig/testrig.exe playtest --list-checks</c>
///     printed an empty list and exited 0, while <c>dotnet run</c> over the same sources listed
///     all eight and a scan of the 16.7 MB binary found none of the check names. ILC had
///     removed every check class, because a <c>[ModuleInitializer]</c> is not a trimmer root
///     under <c>PublishAot</c> with <c>TrimMode=full</c> and nothing else referenced them.
///     </para>
///     <para>
///     <b>Why the rest of the suite could not see it, and why this file is separate.</b> Every
///     other test here runs on CoreCLR, where module initializers DO run and no trimmer has
///     touched anything, so <c>ShippedChecksTests</c> asserted all eight were present and
///     stayed green against an artifact that had none. Trimming is a property of the published
///     artifact and can only be observed by running it. This test therefore executes
///     <c>TestRig/testrig.exe</c> as a subprocess and reads what it says.
///     </para>
///     <para>
///     <b>It runs a COPY, with no <c>src/</c> beside it.</b> The binary's own staleness guard
///     refuses every verb when the source tree next to it disagrees with the digest it was
///     built from, which is correct and is exactly the state an agent is in while editing. The
///     guard passes unconditionally when there is no <c>src/</c> at all, because that is a
///     binary copied somewhere else rather than a stale one. So the copy answers the question
///     this test asks (does the ARTIFACT carry the checks) without demanding a publish before
///     every <c>dotnet test</c>.
///     </para>
///     <para>
///     A missing <c>testrig.exe</c> FAILS rather than skipping. The binary is committed, so an
///     absent one is a broken working tree; skipping would turn the one test that watches the
///     artifact into a test that passes when there is no artifact.
///     </para>
/// </remarks>
public sealed class ShippedBinaryChecksTests : IDisposable
{
    /// <summary>What the repository ships today. A change here is a deliberate change.</summary>
    private static readonly string[] Expected =
    [
        "the first-use notice cap stops after three lines",
        "the join summary is one console line naming every blocked function",
        "the eyedropper explains a cross-family pick once per click",
        "the effective-settings line is one log line and never reaches the console",
        "the conflict banner is one boot line then six world lines",
        "the host own client half must not leak onto a joiner",
        "a non-owner reaches metallic while the owner is connected",
        "the entitlement outlives the owner",
    ];

    private readonly TempDirectory _temp = new("shipped");
    private readonly string _exe;

    public ShippedBinaryChecksTests() => _exe = PlantCopy();

    public void Dispose() => _temp.Dispose();

    [Fact]
    public void TheShippedBinaryListsEveryCheckThisRepositoryShips()
    {
        var result = Run("playtest", "--list-checks", "--json");

        Assert.Equal(0, result.Exit);

        using var doc = JsonDocument.Parse(result.StdOut);
        var values = doc.RootElement.GetProperty("values");

        var names = values.GetProperty("checks").EnumerateArray().Select(v => v.GetString()).ToList();

        Assert.Equal(Expected.Length, values.GetProperty("checkCount").GetInt32());
        foreach (var name in Expected) Assert.Contains(name, names);

        // Exactly the expected set, not merely a superset: a check silently dropped by the
        // trimmer is the failure this file exists for, and a check that appeared from nowhere
        // is worth knowing about too.
        Assert.Equal(Expected.Length, names.Count);
    }

    [Fact]
    public void TheShippedBinaryPrintsTheChecksAndNotJustAHeader()
    {
        // The human path, which is what an operator actually reads. The trimmed binary printed
        // "Registered checks:" and nothing else, and a bare header with a zero exit reads as a
        // clean answer rather than as a broken artifact.
        var result = Run("playtest", "--list-checks");

        Assert.Equal(0, result.Exit);
        Assert.Contains("Registered checks:", result.All, StringComparison.Ordinal);
        Assert.Contains($"{Expected.Length} check(s) compiled in", result.All, StringComparison.Ordinal);
        foreach (var name in Expected) Assert.Contains(name, result.All, StringComparison.Ordinal);
    }

    [Fact]
    public void TheShippedBinaryStillAnswersTheFlakeTaxonomyWithNoRigAtAll()
    {
        // The other listing, which must keep working whatever happens to the check set: it is
        // a fact about the code, and it answers with no rig, no lock and no game.
        var result = Run("playtest", "--list-flakes");

        Assert.Equal(0, result.Exit);
        Assert.Contains("Flake taxonomy, in resolution order", result.All, StringComparison.Ordinal);
    }

    /// <summary>The committed artifact, copied where its own staleness guard does not apply.</summary>
    private static string PlantCopy()
    {
        var shipped = Path.Combine(Directory.GetParent(RigSources.SrcRoot)!.FullName, "testrig.exe");

        Assert.True(File.Exists(shipped),
            $"The shipped binary is not at {shipped}. It is committed to git, so an absent one is a broken "
            + "working tree, not a reason to skip: this is the only test that can see what the AOT trimmer did. "
            + "Publish it: dotnet publish TestRig/src/TestRig.Cli/TestRig.Cli.csproj -c Release -r win-x64");

        var target = Path.Combine(Path.GetTempPath(), "testrig-shipped-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(target);

        var copy = Path.Combine(target, "testrig.exe");
        File.Copy(shipped, copy, overwrite: true);
        return copy;
    }

    private (int Exit, string StdOut, string All) Run(params string[] args)
    {
        var home = Path.Combine(_temp.Path, "home-" + Guid.NewGuid().ToString("N")[..6], "TestRig");
        Directory.CreateDirectory(home);

        var start = new ProcessStartInfo(_exe)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = Path.GetDirectoryName(_exe)!,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };

        foreach (var arg in args) start.ArgumentList.Add(arg);

        // The same isolation the CLI suite uses: nothing here may reach the one real session
        // lock, the real instance trees or the developer's saves. None of these verbs touches
        // the rig, and the isolation is what makes that a fact rather than an intention.
        start.Environment["TESTRIG_HOME"] = home;
        start.Environment["TESTRIG_STATIONEERS_PATH"] = Path.Combine(home, "fake-install");
        start.Environment["TESTRIG_USERDATA"] = Path.Combine(home, "fake-userdata");
        start.Environment["STATIONEERS_CLIENTRIG_ROOT"] = string.Empty;
        start.Environment["STEAMCMD_PATH"] = string.Empty;

        // The same set is seven, not five. Without these two the shipped binary reads the
        // developer's real LocalLow folder and their real PlayerPrefs key on its way through
        // a session boundary. Read-only, but "the same isolation the CLI suite uses" above is
        // only true if it is actually the same, and the point of the isolation is that no
        // result here depends on what happens to be on a particular machine.
        start.Environment["TESTRIG_SHAREDDATA"] = Path.Combine(home, "fake-sharedstate");
        start.Environment["TESTRIG_PLAYERPREFSKEY"] = @"HKCU:\Software\StationeersPlus\TestRigSuiteNeverExists";

        using var process = Process.Start(start)
            ?? throw new InvalidOperationException($"could not start {_exe}");

        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(120_000))
        {
            process.Kill(entireProcessTree: true);
            throw new TimeoutException($"testrig {string.Join(' ', args)} did not exit within two minutes.");
        }

        return (process.ExitCode, stdout.Result, stdout.Result + stderr.Result);
    }
}
