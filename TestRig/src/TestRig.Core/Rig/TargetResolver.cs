using TestRig.Core.Session;

namespace TestRig.Core.Rig;

/// <summary>What a <c>--target</c> turned into: which half, and which instances.</summary>
/// <param name="Names">
/// Always a collection, never a bare string. The PowerShell had to wrap this by hand or
/// downstream code enumerated a single name character by character (COMMON-113); a typed
/// collection makes that impossible.
/// </param>
/// <param name="Spec">The text the caller actually typed, for a message that echoes it.</param>
public sealed record ResolvedTarget(
    TargetKind Kind,
    bool Server,
    IReadOnlyList<string> Names,
    string Spec);

/// <summary>
/// The decision inputs a refusal needs from the command line.
/// </summary>
/// <remarks>
/// An explicit object rather than something read out of caller scope. The PowerShell read
/// these from <c>$PSBoundParameters</c>, which is PER SCOPE and therefore silently answers
/// false inside any function that asks (COMMON-115).
/// </remarks>
public sealed record VerbOptions
{
    /// <summary>The readiness stage asked for, when the verb is <c>wait</c>.</summary>
    public ReadinessStage? Stage { get; init; }

    /// <summary>The save name given, if any.</summary>
    public string? SaveName { get; init; }

    /// <summary>Whether a world argument was given to <c>start</c>.</summary>
    public bool HasWorld { get; init; }

    /// <summary>
    /// Instance-shape flags the caller ACTUALLY TYPED, bare (no leading dashes).
    /// </summary>
    /// <remarks>
    /// Typed, not merely present with a value. A default is not a flag the caller chose,
    /// and treating one as such fired the instance-shape refusal on every server command.
    /// </remarks>
    public IReadOnlyList<string> TypedInstanceFlags { get; init; } = [];
}

/// <summary>Turning <c>--target</c> into a decision, and refusing before any work happens.</summary>
public static class TargetResolver
{
    /// <summary>
    /// The eleven rig-wide verbs, which default to <c>all</c>.
    /// </summary>
    /// <remarks>
    /// This default is the fix for the failure that started the whole consolidation: an
    /// agent asked to "update the testrig" updated the clients and silently skipped the
    /// dedicated server, because refreshing mods was spelled one way on one half and
    /// another way on the other. Everything that acts on a specific running thing requires
    /// an explicit target instead, so a typo neither narrows nor widens the blast radius
    /// (COMMON-102, COMMON-103).
    /// </remarks>
    public static readonly IReadOnlyList<string> RigWideDefaultAll =
    [
        "lock", "unlock", "refresh-lock", "capture-baseline", "reset",
        "status", "list", "update-game", "update-mods", "deploy", "logs",
    ];

    /// <summary>The default target for a verb, or the empty string when it has none.</summary>
    public static string DefaultTarget(string verb) =>
        RigWideDefaultAll.Contains(verb, StringComparer.Ordinal) ? "all" : "";

    /// <summary>
    /// Resolves a target specification.
    /// </summary>
    /// <param name="allowUnknown">
    /// Skips the membership check, for <c>create</c>, which names an instance that does not
    /// exist yet (COMMON-112).
    /// </param>
    /// <exception cref="RigRefusalException">
    /// The verb has no target and no default, the specification names nothing, or a named
    /// instance is not provisioned. An unknown name is NEVER a silent empty set: an empty
    /// set makes a stop look successful and a start look done (COMMON-111).
    /// </exception>
    public static ResolvedTarget Resolve(
        string? target,
        string verb,
        IReadOnlyList<string>? knownInstances = null,
        bool allowUnknown = false)
    {
        var all = knownInstances ?? [];
        var spec = target;

        if (string.IsNullOrWhiteSpace(spec))
        {
            spec = DefaultTarget(verb);
            if (string.IsNullOrEmpty(spec))
            {
                throw new RigRefusalException(
                    RigRefusalKind.Refused,
                    $"'{verb}' needs an explicit --target: 'server', 'clients', or one or more instance "
                    + "names. It acts on a specific running thing, so it will not guess. See what exists "
                    + "with: testrig list");
            }
        }

        // The three keywords match case-insensitively (COMMON-108).
        switch (spec.ToLowerInvariant())
        {
            case "all":
                return new ResolvedTarget(TargetKind.All, true, all, spec);
            case "server":
                return new ResolvedTarget(TargetKind.Server, true, [], spec);
            case "clients":
                return new ResolvedTarget(TargetKind.Clients, false, all, spec);
        }

        var wanted = spec
            .Split(',')
            .Select(static s => s.Trim())
            .Where(static s => s.Length > 0)
            .ToList();

        if (wanted.Count == 0)
        {
            throw new RigRefusalException(
                RigRefusalKind.Refused,
                $"--target '{spec}' names nothing. Use 'all', 'server', 'clients', or one or more instance names.");
        }

        if (!allowUnknown)
        {
            foreach (var name in wanted)
            {
                if (all.Contains(name, StringComparer.Ordinal)) continue;

                var known = all.Count > 0 ? string.Join(", ", all) : "(none provisioned)";
                throw new RigRefusalException(
                    RigRefusalKind.Refused,
                    $"--target '{name}' is not a provisioned instance, and it is not 'all', 'server' or "
                    + $"'clients'. Provisioned: {known}. Create it with: testrig create --target {name} "
                    + "[--role host]");
            }
        }

        return new ResolvedTarget(TargetKind.Instance, false, wanted, spec);
    }

    /// <summary>
    /// Fires every refusal that applies to this verb and target.
    /// </summary>
    /// <remarks>
    /// Called BEFORE the lock is asserted and before any work (COMMON-114): a refusal
    /// corrects the caller's model of the rig, and is worth nothing once a side effect has
    /// already happened.
    /// </remarks>
    public static void AssertVerbApplies(string verb, ResolvedTarget resolved, VerbOptions? options = null)
    {
        options ??= new VerbOptions();
        var kind = resolved.Kind;
        var shown = string.IsNullOrEmpty(resolved.Spec) ? kind.ToString().ToLowerInvariant() : resolved.Spec;

        // The lock, the baseline and the reset are rig-wide by construction, and they
        // return immediately: nothing below applies to them (COMMON-117).
        if (RefusalMatrix.RigWideVerbs.Contains(verb, StringComparer.Ordinal))
        {
            if (kind != TargetKind.All) throw RefusalMatrix.Deny(verb, TargetKind.Narrow, target: shown);
            return;
        }

        switch (verb)
        {
            case "call":
                if (kind == TargetKind.Server) throw RefusalMatrix.Deny("call", TargetKind.Server, target: shown);
                if (kind == TargetKind.All) throw RefusalMatrix.Deny("call", TargetKind.All, target: shown);
                break;

            case "send":
                if (kind == TargetKind.Instance) throw RefusalMatrix.Deny("send", TargetKind.Instance, target: shown);
                if (kind == TargetKind.Clients) throw RefusalMatrix.Deny("send", TargetKind.Clients, target: shown);
                if (kind == TargetKind.All) throw RefusalMatrix.Deny("send", TargetKind.All, target: shown);
                break;

            case "create":
                if (kind == TargetKind.Server) throw RefusalMatrix.Deny("create", TargetKind.Server, target: shown);
                // 'all' and 'clients' share the one "name the instance" entry (COMMON-120).
                if (kind is TargetKind.All or TargetKind.Clients)
                {
                    throw RefusalMatrix.Deny("create", TargetKind.All, target: shown);
                }
                break;

            case "remove":
                if (kind == TargetKind.Server) throw RefusalMatrix.Deny("remove", TargetKind.Server, target: shown);
                if (kind == TargetKind.All) throw RefusalMatrix.Deny("remove", TargetKind.All, target: shown);
                if (kind == TargetKind.Clients) throw RefusalMatrix.Deny("remove", TargetKind.Clients, target: shown);
                break;

            case "snapshot":
                if (kind == TargetKind.Server) throw RefusalMatrix.Deny("snapshot", TargetKind.Server, target: shown);
                if (kind == TargetKind.All) throw RefusalMatrix.Deny("snapshot", TargetKind.All, target: shown);
                break;

            // The next three fire off Resolved.Server rather than an exact kind, so they
            // also fire under --target all. That is correct and is a different rule from
            // the five above; it is written down nowhere else (COMMON-123 to COMMON-125).
            case "wait":
                if (resolved.Server && options.Stage is { } stage && ReadinessStages.IsClientOnly(stage))
                {
                    throw RefusalMatrix.Deny("wait", TargetKind.Server, "client-stage", shown);
                }
                break;

            case "save":
                if (resolved.Server && string.IsNullOrEmpty(options.SaveName))
                {
                    throw RefusalMatrix.Deny("save", TargetKind.Server, "no-name", shown);
                }
                break;

            case "start":
                if (resolved.Server && !options.HasWorld)
                {
                    throw RefusalMatrix.Deny("start", TargetKind.Server, "no-world", shown);
                }
                break;
        }

        // Instance-shape flags against the one install that has no instances. Only on a
        // target of EXACTLY 'server': under --target all they legitimately describe the
        // client half (COMMON-126).
        if (kind == TargetKind.Server && options.TypedInstanceFlags.Count > 0)
        {
            throw RefusalMatrix.Deny(
                "*", TargetKind.Server, "instance-flags", shown,
                displayVerb: verb,
                substitutions: new Dictionary<string, string>
                {
                    ["flags"] = string.Join(", ", options.TypedInstanceFlags.Select(static f => "--" + f)),
                });
        }
    }
}
