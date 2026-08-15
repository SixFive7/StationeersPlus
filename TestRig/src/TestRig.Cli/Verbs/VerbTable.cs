using TestRig.Cli.Parsing;

namespace TestRig.Cli.Verbs;

/// <summary>A resolved target's shape.</summary>
public enum TargetKind
{
    /// <summary>Both halves.</summary>
    All,

    /// <summary>The dedicated server alone.</summary>
    Server,

    /// <summary>Every provisioned client instance.</summary>
    Clients,

    /// <summary>One or more named client instances.</summary>
    Instance,
}

/// <summary>What a verb does when <c>--target</c> is absent.</summary>
public enum TargetDefault
{
    /// <summary>Rig-wide. The whole point of the consolidation: one command reaches both halves.</summary>
    All,

    /// <summary>It acts on a specific running thing, so it will not guess.</summary>
    None,

    /// <summary>Never reaches the resolver: <c>help</c> and <c>host-mode</c>.</summary>
    NotApplicable,
}

/// <summary>Where a verb sits in the printed surface.</summary>
public enum VerbGroup
{
    Session,
    Observation,
    Provisioning,
    Lifecycle,
    Control,
    Internal,
}

/// <param name="Name">Exact, lower-case. There are no aliases.</param>
/// <param name="Default">What an absent <c>--target</c> resolves to.</param>
/// <param name="Accepts">Which resolved kinds reach the dispatcher. The refusal matrix explains the rest.</param>
/// <param name="NeedsLock">Whether the verb asserts the session lock before doing any work.</param>
/// <param name="ReadOnly">Whether the verb can change rig state. Read-only verbs are the ones automation polls.</param>
/// <param name="Summary">One line for the surface.</param>
/// <param name="Options">The options the verb consumes. Anything else typed is a usage error.</param>
public sealed record VerbSpec(
    string Name,
    VerbGroup Group,
    TargetDefault Default,
    IReadOnlyList<TargetKind> Accepts,
    bool NeedsLock,
    bool ReadOnly,
    string Summary,
    IReadOnlyList<string> Options);

/// <summary>
/// The twenty-two verbs, as data.
/// </summary>
/// <remarks>
/// <para>
/// Everything else reads this table: the parser's per-verb option check, target defaulting,
/// the refusal matrix's coverage test, the dispatcher's exhaustiveness and the printed
/// surface. The PowerShell launcher kept the verb list, the defaulting rule, the dispatch
/// switch and the eighty-seven line surface text in four places, and its suite checked the
/// correspondence by matching a switch arm's opening brace, so twenty of fifty-one dispatch
/// assertions would have passed on an empty arm.
/// </para>
/// <para>
/// <c>host-mode</c> is internal: the detached wrapper the server's <c>start</c> spawns. It
/// bypasses target resolution, the refusal matrix and the lock, because the <c>start</c>
/// that spawned it already holds one.
/// </para>
/// </remarks>
public static class VerbTable
{
    private static readonly TargetKind[] Everything =
        [TargetKind.All, TargetKind.Server, TargetKind.Clients, TargetKind.Instance];

    private static readonly TargetKind[] RigWideOnly = [TargetKind.All];

    private static readonly TargetKind[] ClientsOnly = [TargetKind.Clients, TargetKind.Instance];

    private static readonly TargetKind[] Nothing = [];

    public static readonly IReadOnlyList<VerbSpec> All =
    [
        new("help", VerbGroup.Session, TargetDefault.NotApplicable, Nothing, false, true,
            "Print this surface.", []),

        new("lock", VerbGroup.Session, TargetDefault.All, RigWideOnly, false, false,
            "Take the rig session lock. Prints TESTRIG-OWNER <id>.",
            [Options.Target, Options.Purpose, Options.As, Options.TtlMinutes, Options.IdleCeilingMinutes,
             Options.WaitSeconds, Options.BreakLock, Options.KeepState]),

        new("unlock", VerbGroup.Session, TargetDefault.All, RigWideOnly, false, false,
            "Release the lock, restoring the rig on the way out.",
            [Options.Target, Options.As, Options.BreakLock, Options.Force, Options.KeepState]),

        // Not marked as needing the lock even though it is meaningless without one: the verb
        // validates ownership itself, so a caller who forgot --as gets "'refresh-lock'
        // requires --as <id>" rather than the generic gate message.
        new("refresh-lock", VerbGroup.Session, TargetDefault.All, RigWideOnly, false, false,
            "Move both lock timers. Any mutating verb does this too.",
            [Options.Target, Options.As, Options.TtlMinutes, Options.IdleCeilingMinutes]),

        new("capture-baseline", VerbGroup.Session, TargetDefault.All, RigWideOnly, true, false,
            "Declare the rig as it stands to be the definition of clean.",
            [Options.Target, Options.As, Options.Force]),

        new("reset", VerbGroup.Session, TargetDefault.All, RigWideOnly, true, false,
            "Restore the rig without ending the session.",
            [Options.Target, Options.As, Options.DryRun, Options.KeepState, Options.AllowBulkWorldDelete]),

        new("status", VerbGroup.Observation, TargetDefault.All, Everything, false, true,
            "The lock, rig state, both halves' game versions and mod staleness.",
            [Options.Target, Options.As]),

        new("list", VerbGroup.Observation, TargetDefault.All, Everything, false, true,
            "What is provisioned and what is running.",
            [Options.Target]),

        new("logs", VerbGroup.Observation, TargetDefault.All, Everything, false, true,
            "Tail or grep each half's log.",
            [Options.Target, Options.Tail, Options.Grep, Options.Unity]),

        new("snapshot", VerbGroup.Observation, TargetDefault.None, ClientsOnly, false, true,
            "Fetch /status from each instance's control plane, as JSON.",
            [Options.Target, Options.OutFile]),

        new("update-game", VerbGroup.Provisioning, TargetDefault.All, Everything, true, false,
            "Bring each half's game binaries up to the source install.",
            [Options.Target, Options.As, Options.Desktop]),

        new("update-mods", VerbGroup.Provisioning, TargetDefault.All, Everything, true, false,
            "Re-seed each half's mods from the developer's set.",
            [Options.Target, Options.As, Options.FromModConfig]),

        new("deploy", VerbGroup.Provisioning, TargetDefault.All, Everything, true, false,
            "Copy built mods from this repository into each half.",
            [Options.Target, Options.Mod, Options.As, Options.Configuration]),

        new("create", VerbGroup.Provisioning, TargetDefault.None, [TargetKind.Instance], true, false,
            "Build ONE named client instance, hard-linked from the source install.",
            [Options.Target, Options.As, Options.Force, Options.Role, Options.Port, Options.GamePort,
             Options.ClientId, Options.Username, Options.Width, Options.Height, Options.ForceGameplayInput,
             Options.SeedMods, Options.UnderTest, Options.Desktop, Options.InstancesRoot]),

        new("remove", VerbGroup.Provisioning, TargetDefault.None, [TargetKind.Instance], true, false,
            "Delete ONE named instance and its save root.",
            [Options.Target, Options.As, Options.Force, Options.Desktop]),

        // No --width or --height. The window size is an instance's own, recorded when it was
        // provisioned, and a start that could override it would make the size depend on which
        // command last mentioned it. Typing either here is a usage error naming 'create',
        // which is where it belongs (CLIENT-121).
        new("start", VerbGroup.Lifecycle, TargetDefault.None, Everything, true, false,
            "Bring a half up. Clients boot to the menu; the server enters a world.",
            [Options.Target, Options.As, Options.Load, Options.Map, Options.New, Options.GamePort,
             Options.UpdatePort, Options.Desktop]),

        new("stop", VerbGroup.Lifecycle, TargetDefault.None, Everything, false, false,
            "Tear down, joiners first. Needs no lock, refuses under a live foreign one.",
            [Options.Target, Options.As, Options.SaveName, Options.TimeoutSeconds, Options.WaitSeconds,
             Options.Force, Options.Release, Options.BreakLock, Options.KeepState]),

        new("save", VerbGroup.Lifecycle, TargetDefault.None, Everything, true, false,
            "Write the world. Warns rather than claiming success on an unconfirmed save.",
            [Options.Target, Options.As, Options.SaveName, Options.WaitSeconds]),

        new("wait", VerbGroup.Lifecycle, TargetDefault.None, Everything, false, true,
            "Block until a readiness stage. Needs no lock but refreshes one you hold.",
            [Options.Target, Options.Stage, Options.WaitSeconds, Options.As]),

        // Everything, not ClientsOnly: one plugin loads into both halves, so the dedicated
        // server answers the same routes on its own port and 'call --target server' works.
        new("call", VerbGroup.Control, TargetDefault.None, Everything, true, false,
            "One HTTP request to each target's control plane, answer parsed. Works on both halves.",
            [Options.Target, Options.Path, Options.Body, Options.As, Options.CallTimeoutSeconds]),

        new("send", VerbGroup.Control, TargetDefault.None, [TargetKind.Server], true, false,
            "One line to the dedicated server's stdin. Fire and forget.",
            [Options.Target, Options.Command, Options.As]),

        // Not lock-gated, and that is not an oversight: the harness takes and releases the
        // lock ITSELF, once per check. That is what buys a state reset per check, since the
        // reset is between sessions by design and two checks under one lock would get none.
        new("playtest", VerbGroup.Control, TargetDefault.All, RigWideOnly, false, false,
            "Run a mod's in-game checks with nobody at the keyboard.",
            [
                Options.Target, Options.Only, Options.EvidenceRoot, Options.WaitSeconds,
                Options.SuiteName, Options.KeepState, Options.ListChecks, Options.ListFlakes,
            ]),

        new("host-mode", VerbGroup.Internal, TargetDefault.NotApplicable, Nothing, false, false,
            "Internal: the detached wrapper the server's 'start' spawns.",
            [Options.Load, Options.Map, Options.New, Options.GamePort, Options.UpdatePort]),
    ];

    private static readonly Dictionary<string, VerbSpec> ByName = BuildIndex();

    private static Dictionary<string, VerbSpec> BuildIndex()
    {
        var map = new Dictionary<string, VerbSpec>(StringComparer.Ordinal);
        foreach (var verb in All) map[verb.Name] = verb;
        return map;
    }

    /// <summary>Every verb name, including <c>help</c> and <c>host-mode</c>.</summary>
    public static IReadOnlyList<string> Names => [.. All.Select(static v => v.Name)];

    /// <summary>Everything a caller may type. <c>host-mode</c> is internal and excluded.</summary>
    public static IReadOnlyList<string> PublicNames =>
        [.. All.Where(static v => v.Group != VerbGroup.Internal).Select(static v => v.Name)];

    /// <summary>The verbs that resolve <c>--target</c> to <c>all</c>. Exactly eleven.</summary>
    public static IReadOnlyList<string> DefaultingToAll =>
        [.. All.Where(static v => v.Default == TargetDefault.All).Select(static v => v.Name)];

    /// <summary>The verbs that refuse to guess a target. Exactly nine.</summary>
    public static IReadOnlyList<string> RequiringTarget =>
        [.. All.Where(static v => v.Default == TargetDefault.None).Select(static v => v.Name)];

    public static bool TryGet(string name, out VerbSpec spec) => ByName.TryGetValue(name, out spec!);

    public static VerbSpec Get(string name) => ByName[name];

    public static bool Exists(string name) => ByName.ContainsKey(name);

    /// <summary>
    /// Known verbs sharing the first three characters of what was typed, case-insensitively.
    /// </summary>
    /// <remarks>
    /// A suggestion, never a match: there are no aliases and no abbreviation. <c>sta</c>
    /// offers <c>status</c> and <c>start</c> rather than picking one.
    /// </remarks>
    public static IReadOnlyList<string> Suggest(string typed)
    {
        if (string.IsNullOrEmpty(typed)) return [];
        var head = typed[..Math.Min(3, typed.Length)];
        var hits = new List<string>();
        foreach (var verb in All)
            if (verb.Name.StartsWith(head, StringComparison.OrdinalIgnoreCase))
                hits.Add(verb.Name);
        return hits;
    }
}
