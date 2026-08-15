using System.Text;
using TestRig.Contracts;
using TestRig.Core.Abstractions;
using TestRig.Core.Session;

namespace TestRig.Core.Rig;

/// <summary>What a <c>--target</c> resolved to, as a kind.</summary>
public enum TargetKind
{
    /// <summary>Both halves.</summary>
    All,

    /// <summary>The dedicated server only.</summary>
    Server,

    /// <summary>Every client instance.</summary>
    Clients,

    /// <summary>One or more named instances.</summary>
    Instance,

    /// <summary>
    /// Anything other than <see cref="All"/>, for the five rig-wide verbs.
    /// </summary>
    /// <remarks>Not a resolution outcome: only a refusal-table key.</remarks>
    Narrow,

    /// <summary>
    /// Matches every kind.
    /// </summary>
    /// <remarks>
    /// No entry uses it today, and that is exactly why it is here: the PowerShell matcher
    /// supported it (COMMON-092) and a port that implemented only the exact-match path
    /// would pass every existing test while silently narrowing the matcher's contract.
    /// </remarks>
    Any,
}

/// <summary>One row of the refusal matrix, as data.</summary>
/// <param name="Verb">The verb, or <c>*</c> to match any verb (COMMON-091).</param>
/// <param name="Condition">Matched EXACTLY, including the empty-string case (COMMON-093).</param>
public sealed record RefusalEntry(
    string Verb,
    TargetKind TargetKind,
    string Condition,
    string What,
    string Instead,
    string InsteadLabel,
    string Reference);

/// <summary>
/// The complete refusal matrix.
/// </summary>
/// <remarks>
/// <para>
/// A REFUSAL IS A FEATURE, NOT AN ERROR PATH. A handful of things genuinely cannot mean
/// the same thing on both halves, and each one is a place where an agent's model of the
/// rig is about to be wrong. A refusal that only says no leaves that model wrong; one that
/// says what the verb needs, why this target cannot provide it, and the exact command that
/// would work, corrects it. That is cheaper than any document, because it arrives at the
/// moment of the mistake.
/// </para>
/// <para>
/// Every entry names an alternative, and the C# suite resolves every named endpoint
/// against <see cref="Endpoints"/>. The PowerShell suite only checked that an alternative
/// was PRESENT, so two entries pointed at <c>/console/run</c>, which has never existed
/// (COMMON-073, COMMON-074). Both now name <see cref="Endpoints.ConsoleExec"/> by
/// constant, so the same mistake cannot be made again.
/// </para>
/// </remarks>
public static class RefusalMatrix
{
    /// <summary>
    /// The prefix the CLI recognises so it prints a refusal plainly and exits 2, rather
    /// than dumping a stack trace over the top of it (COMMON-068, COMMON-069).
    /// </summary>
    public const string Sentinel = "[testrig refusal]";

    /// <summary>The column a refusal's explanation wraps at (COMMON-098).</summary>
    public const int WrapWidth = 74;

    /// <summary>Every rig-wide verb, which refuse any target other than <c>all</c> (COMMON-117).</summary>
    public static readonly IReadOnlyList<string> RigWideVerbs =
        ["lock", "unlock", "refresh-lock", "capture-baseline", "reset"];

    /// <summary>The matrix.</summary>
    public static readonly IReadOnlyList<RefusalEntry> Table =
    [
        new("start", TargetKind.Server, "no-world",
            "'start' on the dedicated server has to enter a world in the same call. The server takes "
            + "--load <save> <map> or --new <map> on its own command line and there is no way to bring it up "
            + "to a menu and decide later; a client instance is the opposite, and boots to the menu with no "
            + "world at all.",
            "testrig start --target server --load <SaveName> --map <Map>   (or --new <Map>)",
            "Name the world:",
            "TestRig/MANUAL.md, \"Verbs\""),

        new("send", TargetKind.Instance, "",
            "'send' writes one line to the dedicated server's stdin through its host wrapper. A client "
            + "instance has no stdin anybody can reach: it is launched with CreateProcessW on an isolated "
            + "desktop and driven entirely over its HTTP control plane, which returns a structured answer "
            + "instead of nothing.",
            "testrig call --target {target} --path " + Endpoints.ConsoleExec + " --body '{\"command\":\"<console text>\"}'",
            "Use the control plane:",
            "TestRig/MANUAL.md (the endpoint catalogue)"),

        new("send", TargetKind.Clients, "",
            "'send' is the dedicated server's stdin channel. There is nothing to fan it out over: a client "
            + "instance has no stdin anybody can reach.",
            "testrig call --target clients --path " + Endpoints.ConsoleExec + " --body '{\"command\":\"<console text>\"}'",
            "Use the control plane:",
            "TestRig/MANUAL.md (the endpoint catalogue)"),

        new("send", TargetKind.All, "",
            "'send' is the dedicated server's stdin channel and --target all includes client instances, "
            + "which have no stdin anybody can reach. The two control channels are not one channel with two "
            + "transports: stdin is fire and forget, the HTTP plane answers.",
            "testrig send --target server --command '<console text>'",
            "Name the server:",
            "TestRig/MANUAL.md, \"Verbs\""),

        new("create", TargetKind.Server, "",
            "'create' hard-links a fresh copy of the developer's game install into a new instance tree, one "
            + "of N. The dedicated server is not one of N: it is a single install downloaded from Steam app "
            + "600760 by SteamCMD, with its BepInEx loader mirrored out of the client install. Those are "
            + "different operations on different sources, so one verb cannot be a rename of the other.",
            "testrig update-game --target server",
            "Install or refresh the server:",
            "TestRig/MANUAL.md, \"Working sequences\""),

        new("create", TargetKind.All, "",
            "'create' builds ONE named client instance. It has no rig-wide meaning: the dedicated server is "
            + "not an instance, and the other instances already exist.",
            "testrig create --target <newInstanceName> [--role host]",
            "Name the instance:",
            "TestRig/MANUAL.md, \"The client half\""),

        new("remove", TargetKind.Server, "",
            "'remove' deletes an instance tree and its save root. The dedicated server has no equivalent and "
            + "the absence is deliberate: cleaning it is the developer's call, because its data/ tree holds "
            + "worlds that predate any session and nothing here is allowed to decide they are disposable.",
            "delete TestRig/DedicatedServer/install/ by hand, then: testrig update-game --target server",
            "To rebuild the binaries:",
            "TestRig/MANUAL.md, \"The dedicated server half\""),

        new("remove", TargetKind.All, "",
            "'remove' deletes one named instance and its world. It is never rig-wide: --target all would "
            + "delete every world on the client half in one command, which no test has ever wanted and no "
            + "undo exists for.",
            "testrig remove --target <instanceName>",
            "Name the instance:",
            "TestRig/CLAUDE.md"),

        new("remove", TargetKind.Clients, "",
            "'remove' deletes one named instance and its world. --target clients would delete every one of "
            + "them at once, which no test has ever wanted and no undo exists for.",
            "testrig remove --target <instanceName>",
            "Name the instance:",
            "TestRig/CLAUDE.md"),

        new("snapshot", TargetKind.Server, "",
            "'snapshot' writes an array of per-INSTANCE rows, each keyed by the name, port and role its "
            + "registry entry carries. The dedicated server does answer /status now, on 127.0.0.1:27750, but "
            + "it has no registry entry: it is one install rather than one of N, so there is no row shape to "
            + "put it in. Asking it directly gets the same payload without pretending it is an instance.",
            "testrig call --target server --path /status   (and: testrig status --target server)",
            "Ask the server directly:",
            "TestRig/MANUAL.md, \"Verbs\""),

        new("snapshot", TargetKind.All, "",
            "'snapshot' writes one row per client instance, keyed by the registry entry each one has. "
            + "--target all includes the dedicated server, which answers /status but has no registry entry, "
            + "so it has no row: a fan-out would silently cover one half and the file would not say so.",
            "testrig snapshot --target clients [--out-file before.json]",
            "Snapshot the clients:",
            "TestRig/MANUAL.md, \"The client half\""),

        new("wait", TargetKind.Server, "client-stage",
            "a dedicated server never has a menu. It takes -load or -new on its command line and enters that "
            + "world directly, so there is no state in which it sits waiting for somebody to choose one. "
            + "'ping' and 'modsLoaded' DO work here now: the merged plugin loads into this half too and "
            + "answers on 127.0.0.1:27750, which is also where 'inWorld' gets its evidence.",
            "testrig wait --target server --stage inWorld [--wait-seconds 600]",
            "Wait for the world:",
            "TestRig/MANUAL.md, \"Readiness\""),

        new("save", TargetKind.Server, "no-name",
            "the dedicated server's save is a console command that takes a name, and there is no 'save under "
            + "the current name' form of it: the console has no notion of the world's current name to fall "
            + "back on. A client instance does, which is why --save-name is optional there and required here.",
            "testrig save --target server --save-name <SaveName>",
            "Name the save:",
            "TestRig/MANUAL.md, \"Verbs\""),

        new("lock", TargetKind.Narrow, "",
            "the session lock is RIG-WIDE and cannot be taken over half of it. The two halves share the "
            + "developer's one game install and the per-Windows-user Unity state that nothing separates "
            + "(PlayerCookie-v2.xml, the HKCU PlayerPrefs key), which is why there is one lock rather than two.",
            "testrig lock --purpose \"<what you are testing>\"",
            "Take the whole rig:",
            "TestRig/CLAUDE.md, \"The session lock covers the whole rig\""),

        new("unlock", TargetKind.Narrow, "",
            "the session lock is RIG-WIDE and cannot be released for half of it.",
            "testrig unlock --as <id>",
            "Release the whole rig:",
            "TestRig/CLAUDE.md, \"The session lock covers the whole rig\""),

        new("refresh-lock", TargetKind.Narrow, "",
            "the session lock is RIG-WIDE and its timer is not per half.",
            "testrig refresh-lock --as <id>",
            "Refresh the whole rig:",
            "TestRig/CLAUDE.md, \"The session lock covers the whole rig\""),

        new("capture-baseline", TargetKind.Narrow, "",
            "the baseline is ONE definition of a clean rig covering both halves, exactly as one lock does. "
            + "Capturing half of it would leave the other half restored to whatever an older capture said.",
            "testrig capture-baseline --as <id> [--force]",
            "Capture the whole rig:",
            "TestRig/MANUAL.md, \"State hygiene\""),

        new("reset", TargetKind.Narrow, "",
            "the state reset is rig-wide by construction: it plans over both halves in one pass and clears "
            + "the session marker only when every action in that plan succeeded. A half reset would leave "
            + "the marker set and the next session would restore anyway.",
            "testrig reset --as <id> [--dry-run]",
            "Reset the whole rig:",
            "TestRig/MANUAL.md, \"State hygiene\""),

        new("*", TargetKind.Server, "instance-flags",
            "these flags describe one of N client instances ({flags}): an instance's identity, its ports, "
            + "its window and its role in a session. The dedicated server is a single install with a single "
            + "identity, so none of them has anything to bind to here.",
            "testrig create --target <instanceName> --role host --game-port <port>",
            "These belong to an instance:",
            "TestRig/CLAUDE.md, \"Two ways to host a world\""),
    ];

    /// <summary>
    /// The matching entry, or null.
    /// </summary>
    /// <remarks>
    /// A <c>*</c> verb matches any verb (COMMON-091), which is how the instance-flags entry
    /// covers the whole surface without being repeated. A
    /// <see cref="TargetKind.Any"/> entry matches any kind (COMMON-092). The condition is
    /// compared exactly, empty string included (COMMON-093).
    /// </remarks>
    public static RefusalEntry? Find(string verb, TargetKind targetKind, string condition = "")
    {
        foreach (var entry in Table)
        {
            if (!string.Equals(entry.Verb, verb, StringComparison.Ordinal)
                && !string.Equals(entry.Verb, "*", StringComparison.Ordinal))
            {
                continue;
            }
            if (entry.TargetKind != targetKind && entry.TargetKind != TargetKind.Any) continue;
            if (!string.Equals(entry.Condition, condition, StringComparison.Ordinal)) continue;
            return entry;
        }
        return null;
    }

    /// <summary>
    /// Renders one refusal. The shape is fixed and every part of it earns its place.
    /// </summary>
    /// <param name="displayVerb">
    /// The verb the CALLER typed. The <c>*</c> entries match any verb, so the echoed
    /// command has to be the real one rather than the wildcard (COMMON-128).
    /// </param>
    public static string Format(
        RefusalEntry entry,
        string displayVerb,
        string target = "",
        IReadOnlyDictionary<string, string>? substitutions = null)
    {
        var what = Substitute(entry.What, target, substitutions);
        var instead = Substitute(entry.Instead, target, substitutions);

        var lines = new List<string>
        {
            // The first line echoes the command as typed (COMMON-097).
            string.IsNullOrEmpty(target) ? $"testrig {displayVerb}" : $"testrig {displayVerb} --target {target}",
        };

        var first = true;
        foreach (var chunk in SplitText(what, WrapWidth))
        {
            lines.Add(first ? $"  x {chunk}" : $"    {chunk}");
            first = false;
        }

        var label = string.IsNullOrEmpty(entry.InsteadLabel) ? "Instead:" : entry.InsteadLabel;
        lines.Add($"    {label}  {instead}");

        if (!string.IsNullOrEmpty(entry.Reference)) lines.Add($"    Why: {entry.Reference}");

        return string.Join("\n", lines);
    }

    private static string Substitute(
        string text,
        string target,
        IReadOnlyDictionary<string, string>? substitutions)
    {
        if (substitutions is not null)
        {
            foreach (var (key, value) in substitutions)
            {
                text = text.Replace("{" + key + "}", value, StringComparison.Ordinal);
            }
        }
        if (!string.IsNullOrEmpty(target))
        {
            text = text.Replace("{target}", target, StringComparison.Ordinal);
        }
        return text;
    }

    /// <summary>
    /// Word wrap, so a refusal reads as prose in a terminal instead of one long line.
    /// </summary>
    /// <remarks>
    /// Splits on any whitespace RUN and drops empty tokens (COMMON-101), which is what lets
    /// an entry's explanation be authored across several source lines with indentation and
    /// still render as clean prose. Any refusal text carried across verbatim depends on it.
    /// </remarks>
    public static IReadOnlyList<string> SplitText(string text, int width = WrapWidth)
    {
        var output = new List<string>();
        var line = new StringBuilder();

        foreach (var word in text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
        {
            if (line.Length == 0)
            {
                line.Append(word);
                continue;
            }
            if (line.Length + 1 + word.Length <= width)
            {
                line.Append(' ').Append(word);
            }
            else
            {
                output.Add(line.ToString());
                line.Clear();
                line.Append(word);
            }
        }

        if (line.Length > 0) output.Add(line.ToString());
        return output;
    }

    /// <summary>
    /// Refuses, teaching.
    /// </summary>
    /// <exception cref="RigRefusalException">
    /// Always. It carries the STRUCTURED refusal as well as the rendered text, so
    /// <c>--json</c> renders it without re-parsing prose.
    /// </exception>
    public static RigRefusalException Deny(
        string verb,
        TargetKind targetKind,
        string condition = "",
        string target = "",
        string? displayVerb = null,
        IReadOnlyDictionary<string, string>? substitutions = null)
    {
        var entry = Find(verb, targetKind, condition);
        if (entry is null)
        {
            // Not "the command is wrong": the matrix has a hole (COMMON-127).
            return new RigRefusalException(
                RigRefusalKind.Refused,
                $"No refusal is defined for verb '{verb}' on target kind '{targetKind}' (condition "
                + $"'{condition}'). That is a bug in the refusal matrix in TestRig/src/TestRig.Core/Rig/"
                + "RefusalMatrix.cs, not a problem with the command.");
        }

        var shown = string.IsNullOrEmpty(displayVerb) ? verb : displayVerb;
        var text = Format(entry, shown, target, substitutions);

        var structured = new Refusal(
            string.IsNullOrEmpty(target) ? $"testrig {shown}" : $"testrig {shown} --target {target}",
            Substitute(entry.What, target, substitutions),
            entry.InsteadLabel,
            Substitute(entry.Instead, target, substitutions),
            entry.Reference);

        return new RigRefusalException(RigRefusalKind.Refused, Sentinel + "\n" + text, structured);
    }
}
