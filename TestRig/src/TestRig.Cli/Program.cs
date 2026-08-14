using TestRig.Cli.Parsing;
using TestRig.Core;
using TestRig.Core.Rig;

namespace TestRig.Cli;

/// <summary>
/// Entry point. Deliberately thin: the source-hash gate, then parse, then dispatch.
/// </summary>
internal static partial class Program
{
    private static int Main(string[] args)
    {
        // First, always. A binary that disagrees with the tree beside it must not CHANGE the
        // rig. It must still be able to tear one down: refusing every verb was measured
        // pinning a live instance and an unreleasable lock behind the guard that exists to
        // protect the rig. See StaleBinaryPolicy for the whole argument.
        var drift = BuildStamp.CheckSourceTree();
        if (drift is not null)
        {
            var verb = VerbOf(args);
            if (!StaleBinaryPolicy.Tolerates(verb))
            {
                Console.Error.WriteLine(StaleBinaryPolicy.Refusal(drift.Value));
                return ExitCodes.StaleBinary;
            }

            Console.Error.WriteLine(StaleBinaryPolicy.Warning(drift.Value, verb));
        }

        return CliApp.Run(args);
    }

    /// <summary>
    /// The verb, or the empty string when the command line does not parse.
    /// </summary>
    /// <remarks>
    /// The real parser rather than a scan for the first bare token, because an option's VALUE
    /// is a bare token too and <c>testrig --target all stop</c> would otherwise resolve to
    /// <c>all</c>. A command line that does not parse resolves to nothing and is therefore not
    /// tolerated, which fails in the conservative direction.
    /// </remarks>
    private static string VerbOf(string[] args)
    {
        try
        {
            return CommandLine.Parse(args).Verb;
        }
        catch (CliUsageException)
        {
            return string.Empty;
        }
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
    /// <returns>The two digests when the tree disagrees, otherwise null.</returns>
    /// <remarks>
    /// The binary lives at TestRig/testrig.exe and its sources at TestRig/src/. When
    /// that directory is absent the binary has been copied somewhere else entirely,
    /// which is not the staleness case this guard is for, so it passes.
    ///
    /// This reports the disagreement and does not decide what to do about it.
    /// <see cref="StaleBinaryPolicy"/> owns that, because the answer differs by verb:
    /// a refusal for anything that changes the rig, a loud warning for teardown and
    /// observation.
    /// </remarks>
    public static SourceDrift? CheckSourceTree()
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

        return new SourceDrift(SourceHash, SourceFileCount, actual.Hash, actual.FileCount);
    }
}
