using TestRig.Core;

namespace TestRig.Cli;

/// <summary>
/// Entry point. Deliberately thin: the source-hash gate, then parse, then dispatch.
/// </summary>
internal static partial class Program
{
    private static int Main(string[] args)
    {
        // First, always. A binary that disagrees with the tree beside it must not act.
        var stale = BuildStamp.CheckSourceTree();
        if (stale is not null)
        {
            Console.Error.WriteLine(stale);
            return ExitCodes.StaleBinary;
        }

        return CliApp.Run(args);
    }
}

/// <summary>
/// Process exit codes, as this entry point names them.
/// </summary>
/// <remarks>
/// The values live in <see cref="TestRig.Core.Session.RigExitCodes"/>, because the playtest
/// engine records the code a lock attempt produced into its evidence bundle and two tables
/// that had to agree about what 4 means is the drift this port keeps removing. This class is
/// the local spelling and nothing else.
/// </remarks>
internal static class ExitCodes
{
    public const int Ok = TestRig.Core.Session.RigExitCodes.Ok;
    public const int Failed = TestRig.Core.Session.RigExitCodes.Failed;
    public const int UsageError = TestRig.Core.Session.RigExitCodes.UsageError;
    public const int Refused = TestRig.Core.Session.RigExitCodes.Refused;
    public const int LockHeldByOther = TestRig.Core.Session.RigExitCodes.LockHeldByOther;
    public const int LockNotHeld = TestRig.Core.Session.RigExitCodes.LockNotHeld;
    public const int RigBusy = TestRig.Core.Session.RigExitCodes.RigBusy;
    public const int StaleBinary = TestRig.Core.Session.RigExitCodes.StaleBinary;
    public const int PlaytestInconclusive = TestRig.Core.Session.RigExitCodes.PlaytestInconclusive;
}

internal static partial class BuildStamp
{
    /// <summary>
    /// Verifies this binary against the source tree beside it.
    /// </summary>
    /// <returns>A refusal message when the tree disagrees, otherwise null.</returns>
    /// <remarks>
    /// The binary lives at TestRig/testrig.exe and its sources at TestRig/src/. When
    /// that directory is absent the binary has been copied somewhere else entirely,
    /// which is not the staleness case this guard is for, so it passes.
    ///
    /// When the tree IS present and disagrees, this refuses outright rather than
    /// warning. A warning scrolls past; the two sessions this project has already
    /// lost to stale on-disk artifacts were both cases of something that could have
    /// warned and been missed.
    /// </remarks>
    public static string? CheckSourceTree()
    {
        string srcRoot;
        try
        {
            var exeDir = AppContext.BaseDirectory;
            srcRoot = Path.Combine(exeDir, "src");
            if (!Directory.Exists(srcRoot)) return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }

        Core.SourceHash.Result actual;
        try
        {
            actual = Core.SourceHash.Compute(srcRoot);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Cannot read our own sources. Do not block the rig on a permissions
            // quirk; the staleness this guards against is a git-state problem.
            return null;
        }

        if (string.Equals(actual.Hash, SourceHash, StringComparison.Ordinal)) return null;

        return $"""
            testrig.exe does not match the source tree it sits beside, so it will not run.

              built from : {SourceHash[..16]}  ({SourceFileCount} files)
              tree is now: {actual.Hash[..16]}  ({actual.FileCount} files)

            The committed binary is out of date with TestRig/src/. Rebuild it:

              dotnet publish TestRig/src/TestRig.Cli/TestRig.Cli.csproj -c Release -r win-x64

            then commit testrig.exe together with the source change that caused this.

            Why this is a refusal and not a warning: a stale on-disk artifact has cost
            this project two whole sessions, and in both cases the evidence was present
            and scrolled past. See TestRig/src/CLAUDE.md.
            """;
    }
}
