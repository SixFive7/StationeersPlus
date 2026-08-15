using TestRig.Cli.Verbs;
using TestRig.Core.Abstractions;

namespace TestRig.Cli.Refusals;

/// <summary>
/// One row of the matrix: which verb, which resolved target kind, under which condition.
/// </summary>
/// <param name="Verb">A verb name, or <c>*</c> for a row that applies to every verb.</param>
/// <param name="TargetKind">
/// <c>all</c>, <c>server</c>, <c>clients</c>, <c>instance</c>, or <c>narrow</c> for the five
/// lock verbs, which mean the same thing on any target that is not the whole rig.
/// </param>
/// <param name="Condition">Empty when the kind alone decides it.</param>
public sealed record RefusalRow(string Verb, string TargetKind, string Condition, Refusal Refusal);

/// <summary>
/// The rig declining to do something, in the shape that teaches. Exit code 3.
/// </summary>
public sealed class RefusalException(RefusalRow row, string verb, string target, IReadOnlyDictionary<string, string>? substitutions = null)
    : Exception(row.Refusal.Why)
{
    public RefusalRow Row { get; } = row;

    /// <summary>The verb the caller typed. Differs from <see cref="RefusalRow.Verb"/> on the <c>*</c> row.</summary>
    public string Verb { get; } = verb;

    public string Target { get; } = target;

    public IReadOnlyDictionary<string, string> Substitutions { get; } =
        substitutions ?? new Dictionary<string, string>(StringComparer.Ordinal);

    /// <summary>The row's text with <c>{target}</c> and any other placeholder filled in.</summary>
    public Refusal Resolved => RefusalMatrix.Substitute(Row.Refusal, Target, Substitutions);
}

/// <summary>
/// Every case where a verb cannot mean the same thing on both halves.
/// </summary>
/// <remarks>
/// <para>
/// Twenty rows, each carrying what was attempted, why this target cannot do it, a command
/// that does work, and where the durable explanation lives. All five are mandatory because
/// <see cref="Refusal"/> makes them so: a refusal without a working alternative is the
/// failure the matrix exists to prevent.
/// </para>
/// <para>
/// The PowerShell suite asserted only that a refusal HAD an alternative, and pinned the row
/// count at "at least 18" against a real 21. Two rows pointed callers at
/// <c>/console/run</c>, an endpoint that has never existed; the real one is
/// <c>/console/exec</c>. Both are fixed here, the count is pinned exactly, and the suite
/// resolves every alternative against the verb table and the endpoint catalogue.
/// </para>
/// <para>
/// <b>A refusal whose reason has stopped being true is worse than no refusal.</b> Three rows
/// rested on "the dedicated server has no HTTP control plane", which was a fact about the
/// pre-merge world: one plugin now loads into BOTH halves and the server answers on
/// 127.0.0.1:27750. Both <c>call</c> rows are gone (the verb works there), the two
/// <c>snapshot</c> rows still refuse but for the reason that is actually true, and
/// <c>wait</c> no longer refuses 'ping' or 'modsLoaded' on the server. Every row here has to
/// be re-read whenever the shape of the rig changes, because the matrix teaches, and text
/// that teaches something false does more damage than silence.
/// </para>
/// </remarks>
public static class RefusalMatrix
{
    /// <summary>Word-wrap width for the explanation. The alternative is never wrapped.</summary>
    public const int WrapWidth = 74;

    public static readonly IReadOnlyList<RefusalRow> All =
    [
        // 1
        new("start", "server", "no-world", new Refusal(
            What: "'start' on the dedicated server without a world",
            Why: "'start' on the dedicated server has to enter a world in the same call. The server takes -load <save> <map> or -new <map> on its own command line and there is no way to bring it up to a menu and decide later; a client instance is the opposite, and boots to the menu with no world at all.",
            InsteadLabel: "Name the world:",
            Instead: "testrig start --target server --load <SaveName> --map <Map>   (or --new <Map>)",
            Reference: "TestRig/MANUAL.md, \"Verbs\"")),

        // 2
        new("send", "instance", "", new Refusal(
            What: "'send' against a named client instance",
            Why: "'send' writes one line to the dedicated server's stdin through its host wrapper. A client instance has no stdin anybody can reach: it is launched with CreateProcessW on an isolated desktop and driven entirely over its HTTP control plane, which returns a structured answer instead of nothing.",
            InsteadLabel: "Use the control plane:",
            Instead: "testrig call --target {target} --path /console/exec --body '{\"command\":\"<console text>\"}'",
            Reference: "TestRig/MANUAL.md (the endpoint catalogue)")),

        // 3
        new("send", "clients", "", new Refusal(
            What: "'send' fanned out over the client instances",
            Why: "'send' is the dedicated server's stdin channel. There is nothing to fan it out over: a client instance has no stdin anybody can reach.",
            InsteadLabel: "Use the control plane:",
            Instead: "testrig call --target clients --path /console/exec --body '{\"command\":\"<console text>\"}'",
            Reference: "TestRig/MANUAL.md (the endpoint catalogue)")),

        // 4
        new("send", "all", "", new Refusal(
            What: "'send' across both halves",
            Why: "'send' is the dedicated server's stdin channel and --target all includes client instances, which have no stdin anybody can reach. The two control channels are not one channel with two transports: stdin is fire and forget, the HTTP plane answers.",
            InsteadLabel: "Name the server:",
            Instead: "testrig send --target server --command '<console text>'",
            Reference: "TestRig/MANUAL.md, \"Verbs\"")),

        // 5
        new("create", "server", "", new Refusal(
            What: "'create' against the dedicated server",
            Why: "'create' hard-links a fresh copy of the developer's game install into a new instance tree, one of N. The dedicated server is not one of N: it is a single install downloaded from Steam app 600760 by SteamCMD, with its BepInEx loader mirrored out of the client install. Those are different operations on different sources, so one verb cannot be a rename of the other.",
            InsteadLabel: "Install or refresh the server:",
            Instead: "testrig update-game --target server",
            Reference: "TestRig/MANUAL.md, \"Working sequences\"")),

        // 6
        new("create", "all", "", new Refusal(
            What: "'create' with a rig-wide target",
            Why: "'create' builds ONE named client instance. It has no rig-wide meaning: the dedicated server is not an instance, and the other instances already exist.",
            InsteadLabel: "Name the instance:",
            Instead: "testrig create --target <newInstanceName> [--role host]",
            Reference: "TestRig/MANUAL.md, \"The client half\"")),

        // 7
        new("remove", "server", "", new Refusal(
            What: "'remove' against the dedicated server",
            Why: "'remove' deletes an instance tree and its save root. The dedicated server has no equivalent and the absence is deliberate: cleaning it is the developer's call, because its data/ tree holds worlds that predate any session and nothing here is allowed to decide they are disposable.",
            InsteadLabel: "To rebuild the binaries:",
            Instead: "delete TestRig/DedicatedServer/install/ by hand, then: testrig update-game --target server",
            Reference: "TestRig/MANUAL.md, \"The dedicated server half\"")),

        // 8
        new("remove", "all", "", new Refusal(
            What: "'remove' with a rig-wide target",
            Why: "'remove' deletes one named instance and its world. It is never rig-wide: --target all would delete every world on the client half in one command, which no test has ever wanted and no undo exists for.",
            InsteadLabel: "Name the instance:",
            Instead: "testrig remove --target <instanceName>",
            Reference: "TestRig/CLAUDE.md")),

        // 9
        new("remove", "clients", "", new Refusal(
            What: "'remove' fanned out over the client instances",
            Why: "'remove' deletes one named instance and its world. --target clients would delete every one of them at once, which no test has ever wanted and no undo exists for.",
            InsteadLabel: "Name the instance:",
            Instead: "testrig remove --target <instanceName>",
            Reference: "TestRig/CLAUDE.md")),

        // 10
        new("snapshot", "server", "", new Refusal(
            What: "'snapshot' against the dedicated server",
            Why: "'snapshot' writes an array of per-INSTANCE rows, each keyed by the name, port and role its registry entry carries. The dedicated server does answer /status now, on 127.0.0.1:27750, but it has no registry entry: it is one install rather than one of N, so there is no row shape to put it in. Asking it directly gets the same payload without pretending it is an instance.",
            InsteadLabel: "Ask the server directly:",
            Instead: "testrig call --target server --path /status   (and: testrig status --target server)",
            Reference: "TestRig/MANUAL.md, \"Verbs\"")),

        // 11
        new("snapshot", "all", "", new Refusal(
            What: "'snapshot' across both halves",
            Why: "'snapshot' writes one row per client instance, keyed by the registry entry each one has. --target all includes the dedicated server, which answers /status but has no registry entry, so it has no row: a fan-out would silently cover one half and the file would not say so.",
            InsteadLabel: "Snapshot the clients:",
            Instead: "testrig snapshot --target clients [--out-file before.json]",
            Reference: "TestRig/MANUAL.md, \"The client half\"")),

        // 12
        new("wait", "server", "client-stage", new Refusal(
            What: "'wait' for the menu stage on the dedicated server",
            Why: "a dedicated server never has a menu. It takes -load or -new on its command line and enters that world directly, so there is no state in which it sits waiting for somebody to choose one. 'ping' and 'modsLoaded' DO work here now: the merged plugin loads into this half too and answers on 127.0.0.1:27750, which is also where 'inWorld' gets its evidence.",
            InsteadLabel: "Wait for the world:",
            Instead: "testrig wait --target server --stage inWorld [--wait-seconds 600]",
            Reference: "TestRig/MANUAL.md, \"Readiness\"")),

        // 13
        new("save", "server", "no-name", new Refusal(
            What: "'save' on the dedicated server with no --save-name",
            Why: "the dedicated server's save is a console command that takes a name, and there is no 'save under the current name' form of it: the console has no notion of the world's current name to fall back on. A client instance does, which is why --save-name is optional there and required here.",
            InsteadLabel: "Name the save:",
            Instead: "testrig save --target server --save-name <SaveName>",
            Reference: "TestRig/MANUAL.md, \"Verbs\"")),

        // 14
        new("lock", "narrow", "", new Refusal(
            What: "'lock' over half the rig",
            Why: "the session lock is RIG-WIDE and cannot be taken over half of it. The two halves share the developer's one game install and the per-Windows-user Unity state that nothing separates (PlayerCookie-v2.xml, the HKCU PlayerPrefs key), which is why there is one lock rather than two.",
            InsteadLabel: "Take the whole rig:",
            Instead: "testrig lock --purpose \"<what you are testing>\"",
            Reference: "TestRig/CLAUDE.md, \"The session lock covers the whole rig\"")),

        // 15
        new("unlock", "narrow", "", new Refusal(
            What: "'unlock' over half the rig",
            Why: "the session lock is RIG-WIDE and cannot be released for half of it.",
            InsteadLabel: "Release the whole rig:",
            Instead: "testrig unlock --as <id>",
            Reference: "TestRig/CLAUDE.md, \"The session lock covers the whole rig\"")),

        // 16
        new("refresh-lock", "narrow", "", new Refusal(
            What: "'refresh-lock' over half the rig",
            Why: "the session lock is RIG-WIDE and its timer is not per half.",
            InsteadLabel: "Refresh the whole rig:",
            Instead: "testrig refresh-lock --as <id>",
            Reference: "TestRig/CLAUDE.md, \"The session lock covers the whole rig\"")),

        // 17
        new("capture-baseline", "narrow", "", new Refusal(
            What: "'capture-baseline' over half the rig",
            Why: "the baseline is ONE definition of a clean rig covering both halves, exactly as one lock does. Capturing half of it would leave the other half restored to whatever an older capture said.",
            InsteadLabel: "Capture the whole rig:",
            Instead: "testrig capture-baseline --as <id> [--force]",
            Reference: "TestRig/MANUAL.md, \"State hygiene\"")),

        // 18
        new("reset", "narrow", "", new Refusal(
            What: "'reset' over half the rig",
            Why: "the state reset is rig-wide by construction: it plans over both halves in one pass and clears the session marker only when every action in that plan succeeded. A half reset would leave the marker set and the next session would restore anyway.",
            InsteadLabel: "Reset the whole rig:",
            Instead: "testrig reset --as <id> [--dry-run]",
            Reference: "TestRig/MANUAL.md, \"State hygiene\"")),

        // 19
        new("playtest", "narrow", "", new Refusal(
            What: "'playtest' over half the rig",
            Why: "a playtest check declares the instances it needs and brings them up itself, host first and all the way into its world, then joiners. Naming half the rig cannot narrow that: the check would still start what it declared, and the target would only have changed which half the report claimed to be about. Selecting WHAT runs is --only, over check names.",
            InsteadLabel: "Select checks, not halves:",
            Instead: "testrig playtest --only \"<check name pattern>\"",
            Reference: "TestRig/playtest/CLAUDE.md")),

        // 20. Last, and the only '*' row, so a specific row always wins the scan.
        new("*", "server", "instance-flags", new Refusal(
            What: "instance-shape flags against the dedicated server",
            Why: "these flags describe one of N client instances ({flags}): an instance's identity, its ports, its window and its role in a session. The dedicated server is a single install with a single identity, so none of them has anything to bind to here.",
            InsteadLabel: "These belong to an instance:",
            Instead: "testrig create --target <instanceName> --role host --game-port <port>",
            Reference: "TestRig/CLAUDE.md, \"Two ways to host a world\"")),
    ];

    /// <summary>The five verbs whose refusal is the same on any target that is not the whole rig.</summary>
    public static readonly IReadOnlyList<string> LockFamily =
        ["lock", "unlock", "refresh-lock", "capture-baseline", "reset"];

    /// <summary>First match wins. A specific row always beats the <c>*</c> row, which is last.</summary>
    public static RefusalRow? Find(string verb, string targetKind, string condition = "")
    {
        foreach (var row in All)
        {
            if (!string.Equals(row.Verb, verb, StringComparison.Ordinal) && row.Verb != "*") continue;
            if (!string.Equals(row.TargetKind, targetKind, StringComparison.Ordinal) && row.TargetKind != "any") continue;
            if (!string.Equals(row.Condition, condition, StringComparison.Ordinal)) continue;
            return row;
        }

        return null;
    }

    /// <summary>Always throws. A missing row is a bug in this table, and says so.</summary>
    public static RefusalException Deny(
        string verb,
        string targetKind,
        string condition = "",
        string target = "",
        string? displayVerb = null,
        IReadOnlyDictionary<string, string>? substitutions = null)
    {
        var row = Find(verb, targetKind, condition)
            ?? throw new InvalidOperationException(
                $"No refusal is defined for verb '{verb}' on target kind '{targetKind}' (condition '{condition}'). "
                + "That is a bug in the refusal matrix in TestRig/src/TestRig.Cli/Refusals/RefusalMatrix.cs, not a "
                + "problem with the command.");

        return new RefusalException(row, displayVerb ?? verb, target, substitutions);
    }

    /// <summary>Fills <c>{target}</c> and any caller-supplied placeholder into a row's text.</summary>
    public static Refusal Substitute(Refusal refusal, string target, IReadOnlyDictionary<string, string> substitutions)
    {
        var why = refusal.Why;
        var instead = refusal.Instead;

        foreach (var (key, value) in substitutions)
        {
            var token = "{" + key + "}";
            why = why.Replace(token, value, StringComparison.Ordinal);
            instead = instead.Replace(token, value, StringComparison.Ordinal);
        }

        if (target.Length > 0)
        {
            why = why.Replace("{target}", target, StringComparison.Ordinal);
            instead = instead.Replace("{target}", target, StringComparison.Ordinal);
        }

        return refusal with { Why = why, Instead = instead };
    }

    /// <summary>
    /// The rendered form: the command echoed back, the explanation wrapped, the working
    /// alternative on one line, and where to read more.
    /// </summary>
    public static string Render(RefusalException refusal)
    {
        ArgumentNullException.ThrowIfNull(refusal);
        var text = refusal.Resolved;
        var lines = new List<string>
        {
            refusal.Target.Length > 0
                ? $"testrig {refusal.Verb} --target {refusal.Target}"
                : $"testrig {refusal.Verb}",
        };

        var wrapped = Wrap(text.Why, WrapWidth);
        for (var i = 0; i < wrapped.Count; i++)
            lines.Add((i == 0 ? "  x " : "    ") + wrapped[i]);

        var label = text.InsteadLabel.Length > 0 ? text.InsteadLabel : "Instead:";
        lines.Add($"    {label}  {text.Instead}");
        if (text.Reference.Length > 0) lines.Add($"    Why: {text.Reference}");

        return string.Join(Environment.NewLine, lines);
    }

    /// <summary>Greedy word wrap. A word longer than the width gets its own over-long line.</summary>
    public static IReadOnlyList<string> Wrap(string text, int width)
    {
        ArgumentNullException.ThrowIfNull(text);
        var words = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (words.Length == 0) return [];

        var lines = new List<string>();
        var line = words[0];
        for (var i = 1; i < words.Length; i++)
        {
            if (line.Length + 1 + words[i].Length > width)
            {
                lines.Add(line);
                line = words[i];
            }
            else
            {
                line = line + " " + words[i];
            }
        }

        lines.Add(line);
        return lines;
    }

    /// <summary>
    /// Fires every refusal that applies, before the lock is asserted and before any work.
    /// </summary>
    /// <remarks>
    /// Ordering is the behaviour: a refusal corrects the caller's model of the rig, and is
    /// worth nothing once a side effect has already happened.
    /// </remarks>
    public static void Assert(string verb, ResolvedTarget resolved, RefusalInputs inputs)
    {
        ArgumentNullException.ThrowIfNull(resolved);
        ArgumentNullException.ThrowIfNull(inputs);

        var kind = KindName(resolved.Kind);
        var shown = resolved.Spec.Length > 0 ? resolved.Spec : kind;

        // The lock family first, and it returns either way: these five verbs never reach the
        // instance-flag check, because "the lock is rig-wide" is the only thing worth saying.
        if (LockFamily.Contains(verb))
        {
            if (resolved.Kind != TargetKind.All) throw Deny(verb, "narrow", target: shown);
            return;
        }

        switch (verb)
        {
            case "start":
                if (resolved.Server && !inputs.HasWorld) throw Deny("start", "server", "no-world", shown);
                break;

            case "send":
                if (resolved.Kind == TargetKind.Instance) throw Deny("send", "instance", target: shown);
                if (resolved.Kind == TargetKind.Clients) throw Deny("send", "clients", target: shown);
                if (resolved.Kind == TargetKind.All) throw Deny("send", "all", target: shown);
                break;

            case "create":
                if (resolved.Kind == TargetKind.Server) throw Deny("create", "server", target: shown);
                // 'all' and 'clients' route to one row, as in PowerShell: there is no
                // create/clients row, unlike 'remove', which has one. The consequence is
                // cosmetic and preserved rather than fixed, so the count stays at 21: for
                // 'create --target clients' the echoed line reads "--target clients" while
                // the body says "It has no rig-wide meaning".
                if (resolved.Kind is TargetKind.All or TargetKind.Clients) throw Deny("create", "all", target: shown);
                break;

            case "remove":
                if (resolved.Kind == TargetKind.Server) throw Deny("remove", "server", target: shown);
                if (resolved.Kind == TargetKind.All) throw Deny("remove", "all", target: shown);
                if (resolved.Kind == TargetKind.Clients) throw Deny("remove", "clients", target: shown);
                break;

            case "snapshot":
                if (resolved.Kind == TargetKind.Server) throw Deny("snapshot", "server", target: shown);
                if (resolved.Kind == TargetKind.All) throw Deny("snapshot", "all", target: shown);
                break;

            case "wait":
                if (resolved.Server && IsClientStage(inputs.Stage))
                    throw Deny("wait", "server", "client-stage", shown);
                break;

            case "save":
                if (resolved.Server && string.IsNullOrEmpty(inputs.SaveName))
                    throw Deny("save", "server", "no-name", shown);
                break;

            // Rig-wide like the lock family, but for its own reason rather than theirs: a
            // check declares the instances it needs and brings them up itself, so a narrower
            // target could not change what runs, only what the report claims to cover.
            case "playtest":
                if (resolved.Kind != TargetKind.All) throw Deny("playtest", "narrow", target: shown);
                break;
        }

        // Only on a target of exactly 'server'. Under --target all those flags legitimately
        // describe the client half.
        if (resolved.Kind == TargetKind.Server && inputs.TypedInstanceFlags.Count > 0)
        {
            var flags = string.Join(", ", inputs.TypedInstanceFlags.Select(static f => "--" + f));
            throw Deny(
                "*", "server", "instance-flags", shown, verb,
                new Dictionary<string, string>(StringComparer.Ordinal) { ["flags"] = flags });
        }
    }

    /// <summary>
    /// The one readiness stage only a client instance can be in.
    /// </summary>
    /// <remarks>
    /// It was three ('ping', 'modsLoaded', 'menu') and two stopped being true when the two
    /// plugins merged: the dedicated server has a control plane to ping and a loaded-plugin
    /// count to reach 'modsLoaded' with. Core's <c>ReadinessStages.IsClientOnly</c> is the
    /// same rule over the enum; this is the string form the parser sees.
    /// </remarks>
    public static bool IsClientStage(string stage) => stage is "menu";

    public static string KindName(TargetKind kind) => kind switch
    {
        TargetKind.All => "all",
        TargetKind.Server => "server",
        TargetKind.Clients => "clients",
        _ => "instance",
    };
}

/// <summary>
/// The four things the matrix decides on, gathered by the caller.
/// </summary>
/// <remarks>
/// Passed in rather than read from ambient state, so the matrix stays pure and the suite can
/// drive every row without a rig.
/// </remarks>
/// <param name="Stage">The readiness stage <c>wait</c> was asked for.</param>
/// <param name="SaveName">The world name <c>save</c> was given, if any.</param>
/// <param name="HasWorld">Whether <c>start</c> was given a world: <c>--load</c> or <c>--new</c>.</param>
/// <param name="TypedInstanceFlags">
/// Which of the ten instance-shape flags were actually written on the command line. Not
/// which of them have non-default values: a typed <c>--width 800</c> is typed.
/// </param>
public sealed record RefusalInputs(
    string Stage,
    string SaveName,
    bool HasWorld,
    IReadOnlyList<string> TypedInstanceFlags)
{
    public static readonly RefusalInputs None = new(string.Empty, string.Empty, false, []);
}
