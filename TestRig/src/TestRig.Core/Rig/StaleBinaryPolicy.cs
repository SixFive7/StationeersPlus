namespace TestRig.Core.Rig;

/// <summary>What the binary was built from, against what is on disk beside it now.</summary>
public readonly record struct SourceDrift(string BuiltHash, int BuiltFileCount, string TreeHash, int TreeFileCount);

/// <summary>
/// What a binary that disagrees with the source tree beside it is still allowed to do.
/// </summary>
/// <remarks>
/// <para>
/// The source-hash gate is a refusal rather than a warning because a stale on-disk artifact
/// has already cost this project two whole sessions and in both cases the evidence scrolled
/// past. That reasoning is sound for anything that changes the rig, and it was applied to
/// every verb, which was a mistake with a measured cost: with an instance running and the
/// tree edited, the binary refused <c>stop</c> and <c>unlock</c> as well, so the agent could
/// neither stop what it had started nor release the lock. The rig was pinned by the guard
/// that exists to protect it.
/// </para>
/// <para>
/// So the refusal stands for everything that provisions, starts, deploys, saves or resets,
/// and three verbs are exempted with a loud warning: the two that TEAR DOWN and the one that
/// OBSERVES. A stale binary that can still stop a process and release a lock is strictly
/// better than a locked rig nobody can free, and none of the three can make the disagreement
/// worse: <c>status</c> writes nothing, and <c>stop</c> and <c>unlock</c> only ever move the
/// rig towards the state a rebuild wants it in.
/// </para>
/// </remarks>
public static class StaleBinaryPolicy
{
    /// <summary>The one command that resolves the disagreement.</summary>
    public const string RebuildCommand =
        "dotnet publish TestRig/src/TestRig.Cli/TestRig.Cli.csproj -c Release -r win-x64";

    /// <summary>
    /// The verbs a stale binary still runs: teardown and observation, and nothing else.
    /// </summary>
    /// <remarks>
    /// Deliberately not "every read-only verb". <c>list</c> and <c>logs</c> are read-only too
    /// and are not here, because neither is needed to get out of the state this covers, and a
    /// list that quietly grows is how an exemption stops being an exemption.
    /// </remarks>
    public static readonly IReadOnlyList<string> ToleratedVerbs = ["status", "stop", "unlock"];

    /// <summary>True when this verb runs anyway, with a warning.</summary>
    public static bool Tolerates(string? verb)
    {
        if (string.IsNullOrEmpty(verb)) return false;
        foreach (var tolerated in ToleratedVerbs)
        {
            if (string.Equals(tolerated, verb, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    /// <summary>The refusal, for every verb that can change the rig.</summary>
    public static string Refusal(SourceDrift drift) =>
        $"""
         testrig.exe does not match the source tree it sits beside, so it will not run.

         {Digests(drift)}

         The committed binary is out of date with TestRig/src/. Rebuild it:

           {RebuildCommand}

         then commit testrig.exe together with the source change that caused this.

         Why this is a refusal and not a warning: a stale on-disk artifact has cost
         this project two whole sessions, and in both cases the evidence was present
         and scrolled past. See TestRig/src/CLAUDE.md.

         '{string.Join("', '", ToleratedVerbs)}' still run, so a rig left running by
         an earlier command can always be torn down and released.
         """;

    /// <summary>The warning a tolerated verb prints before doing its work anyway.</summary>
    public static string Warning(SourceDrift drift, string verb) =>
        $"""
         WARNING: testrig.exe does not match the source tree it sits beside. '{verb}' is
         running anyway because teardown and observation must never be blocked by this
         guard; every verb that changes the rig is still refused.

         {Digests(drift)}

           {RebuildCommand}

         What this means for the result below: it is produced by the OLD binary, so it
         describes the rig as that build understands it. Rebuild before trusting it for
         anything but getting the rig back to idle. See TestRig/src/CLAUDE.md.
         """;

    private static string Digests(SourceDrift drift) =>
        $"""
           built from : {Short(drift.BuiltHash)}  ({drift.BuiltFileCount} files)
           tree is now: {Short(drift.TreeHash)}  ({drift.TreeFileCount} files)
         """;

    private static string Short(string hash) => hash.Length <= 16 ? hash : hash[..16];
}
