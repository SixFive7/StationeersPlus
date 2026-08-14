using System.Diagnostics;
using System.Text;
using TestRig.Core.Rig;
using TestRig.Tests.Infrastructure;
using Xunit;

namespace TestRig.Tests.Cli;

/// <summary>
/// A binary that disagrees with the tree beside it must still be able to free the rig.
/// </summary>
/// <remarks>
/// Measured 2026-08-14: the source-hash gate ran before everything and refused EVERY verb, so
/// with an instance running and the tree edited the agent could neither stop what it had
/// started nor release the lock. The guard that exists to protect the rig had pinned it.
///
/// These tests run the real binary out of a COPY of its output folder with a fake
/// <c>src/</c> beside it, which is the only way to make the shipped guard actually fire: the
/// binary in its own bin folder has no <c>src/</c> and therefore passes unconditionally.
/// </remarks>
[Collection("cli")]
public sealed class StaleBinaryTests : IDisposable
{
    private readonly CliFixture _rig;
    private readonly TempDirectory _temp = new("stale");
    private readonly string _staleExe;

    public StaleBinaryTests(CliFixture rig)
    {
        _rig = rig;
        _staleExe = PlantStaleCopy();
    }

    public void Dispose() => _temp.Dispose();

    [Fact]
    public void EveryVerbThatChangesTheRigIsStillRefused()
    {
        var refused = RunStale("lock", "--purpose", "should never be taken");

        Assert.Equal(7, refused.ExitCode);
        Assert.Contains("does not match the source tree", refused.StdErr, StringComparison.Ordinal);
        Assert.Contains(StaleBinaryPolicy.RebuildCommand, refused.StdErr, StringComparison.Ordinal);

        // Provisioning, deploying and resetting are all still refused, and the refusal names
        // the way out so an operator is not left guessing which verbs survive.
        Assert.Equal(7, RunStale("deploy", "--mod", "SprayPaintPlus").ExitCode);
        Assert.Equal(7, RunStale("reset", "--as", "nobody00").ExitCode);
        Assert.Contains("still run", refused.StdErr, StringComparison.Ordinal);
    }

    [Fact]
    public void TeardownAndObservationRunAnywayWithALoudWarning()
    {
        foreach (var verb in StaleBinaryPolicy.ToleratedVerbs)
        {
            var result = RunStale(verb, "--as", "nobody00");

            Assert.True(result.ExitCode != 7,
                $"'{verb}' must not be blocked by the staleness guard, but exited 7:\n{result.All}");

            Assert.Contains("does not match the source tree", result.StdErr, StringComparison.Ordinal);
            Assert.Contains(StaleBinaryPolicy.RebuildCommand, result.StdErr, StringComparison.Ordinal);

            // Both digests, so an operator can see which pair disagreed.
            Assert.Contains("built from :", result.StdErr, StringComparison.Ordinal);
            Assert.Contains("tree is now:", result.StdErr, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void TheExemptionIsThreeVerbsAndAMalformedCommandLineIsNotOneOfThem()
    {
        Assert.Equal(["status", "stop", "unlock"], StaleBinaryPolicy.ToleratedVerbs);

        Assert.True(StaleBinaryPolicy.Tolerates("stop"));
        Assert.True(StaleBinaryPolicy.Tolerates("STOP"));
        Assert.False(StaleBinaryPolicy.Tolerates("start"));
        Assert.False(StaleBinaryPolicy.Tolerates(""));
        Assert.False(StaleBinaryPolicy.Tolerates(null));

        // An option's VALUE is a bare token too, so the verb comes from the real parser
        // rather than a scan for the first one. A scan would exempt this; the parser sees
        // 'start'.
        var wrong = RunStale("--target", "stop", "start");
        Assert.Equal(7, wrong.ExitCode);
    }

    /// <summary>Runs the planted stale copy against a throwaway rig home.</summary>
    private CliResult RunStale(params string[] args)
    {
        var home = _rig.NewHome("stale");

        var start = new ProcessStartInfo(_staleExe)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = Path.GetDirectoryName(_staleExe)!,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };

        foreach (var arg in args) start.ArgumentList.Add(arg);

        start.Environment["TESTRIG_HOME"] = home;
        start.Environment["TESTRIG_STATIONEERS_PATH"] = Path.Combine(home, "fake-install");
        start.Environment["TESTRIG_USERDATA"] = Path.Combine(home, "fake-userdata");
        start.Environment["STATIONEERS_CLIENTRIG_ROOT"] = string.Empty;
        start.Environment["STEAMCMD_PATH"] = string.Empty;

        using var process = Process.Start(start)
            ?? throw new InvalidOperationException($"could not start {_staleExe}");

        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(120_000))
        {
            process.Kill(entireProcessTree: true);
            throw new TimeoutException($"testrig {string.Join(' ', args)} did not exit within two minutes.");
        }

        return new CliResult(process.ExitCode, stdout.Result, stderr.Result);
    }

    /// <summary>
    /// A copy of the built binary with a source tree beside it that it was not built from.
    /// </summary>
    /// <remarks>
    /// One <c>.cs</c> file is enough: the digest covers every source file's path and content,
    /// so a tree of one file can never match a tree of hundreds.
    /// </remarks>
    private string PlantStaleCopy()
    {
        var binDir = Path.GetDirectoryName(_rig.ExePath)!;
        var target = _temp.Dir("bin");

        foreach (var file in Directory.EnumerateFiles(binDir, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(binDir, file);
            var destination = Path.Combine(target, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(file, destination, overwrite: true);
        }

        var src = Path.Combine(target, "src");
        Directory.CreateDirectory(src);
        File.WriteAllText(Path.Combine(src, "NotTheRealTree.cs"), "// planted by the suite\n");

        return Path.Combine(target, Path.GetFileName(_rig.ExePath));
    }
}
