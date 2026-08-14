using System.Text;
using System.Text.Json;
using TestRig.Cli.Parsing;
using TestRig.Cli.Refusals;
using TestRig.Cli.Verbs;
using TestRig.Core.Abstractions;

namespace TestRig.Cli;

/// <summary>
/// What the rig prints when it is run with no verb.
/// </summary>
/// <remarks>
/// <para>
/// Generated from <see cref="VerbTable"/>, <see cref="Options"/> and
/// <see cref="RefusalMatrix"/> rather than hand-maintained, because it is the fastest
/// correct reference for a verb or a flag and <c>TestRig/CLAUDE.md</c> sends a reader
/// straight here. The PowerShell version was an eighty-seven line here-string kept in step
/// with the dispatch switch by hand, and nothing checked that it was.
/// </para>
/// <para>
/// <c>--json</c> emits the same surface as data: the verb table, the option catalogue, the
/// twenty-one refusals and the exit codes. That is what makes the suite able to resolve
/// every refusal's alternative against the real verb and endpoint tables, which is the test
/// the PowerShell suite never had.
/// </para>
/// </remarks>
public static class Surface
{
    public static void WriteHuman(IOutput output, string instancesRoot, string instancesRootSource)
    {
        ArgumentNullException.ThrowIfNull(output);
        foreach (var line in Compose(instancesRoot, instancesRootSource))
            output.Line(OutputLevel.Info, line);
    }

    private static IReadOnlyList<string> Compose(string instancesRoot, string instancesRootSource)
    {
        var lines = new List<string>
        {
            "testrig <verb> [--target all|server|clients|<instance>[,<instance>]] [options]",
            string.Empty,
            "One rig, one session lock, two halves: a headless dedicated server and N driven game",
            "clients. Where a verb cannot mean the same thing on both halves it refuses, says why,",
            "and names a command that works. Read the refusal; it is the documentation for that case.",
            string.Empty,
            "  Rules      TestRig/CLAUDE.md           auto-loads; read before any mutating command",
            "  Reference  TestRig/MANUAL.md           every verb per target, the working sequences",
            "  Internals  TestRig/RESEARCH.md         why the design is what it is",
            "  Playtests  TestRig/playtest/CLAUDE.md  running a mod's checks with nobody at the keyboard",
            string.Empty,
            "VERBS",
        };

        foreach (var group in new[]
                 {
                     (VerbGroup.Session, "the session"),
                     (VerbGroup.Observation, "observation (no lock needed)"),
                     (VerbGroup.Provisioning, "provisioning"),
                     (VerbGroup.Lifecycle, "lifecycle"),
                     (VerbGroup.Control, "driving"),
                 })
        {
            lines.Add($"  {group.Item2}");
            foreach (var verb in VerbTable.All)
            {
                if (verb.Group != group.Item1) continue;
                var def = verb.Default switch
                {
                    TargetDefault.All => "all",
                    TargetDefault.None => "(required)",
                    _ => string.Empty,
                };
                lines.Add($"    {verb.Name,-17}{def,-11}{verb.Summary}");
            }

            lines.Add(string.Empty);
        }

        lines.Add($"--target defaults to 'all' on: {string.Join(", ", VerbTable.DefaultingToAll)}.");
        lines.Add("These act on a specific running thing and will not guess, so name a target:");
        lines.Add($"  {string.Join(", ", VerbTable.RequiringTarget)}.");
        lines.Add("Instance names are matched case-insensitively and may be comma-separated.");
        lines.Add(string.Empty);

        lines.Add("THE SESSION LOCK  (rig-wide; the two halves share one game install and one Unity state)");
        lines.Add("  testrig lock --purpose \"<what you are testing>\"   prints TESTRIG-OWNER <id> as its last line");
        lines.Add("  testrig <verb> --as <id> ...                      every mutating verb");
        lines.Add("  testrig unlock --as <id>                          releases AND restores the rig");
        lines.Add(string.Empty);

        var gated = VerbTable.All.Where(static v => v.NeedsLock).Select(static v => v.Name);
        var free = VerbTable.All
            .Where(static v => v is { NeedsLock: false, ReadOnly: true, Group: not VerbGroup.Internal } && v.Name != "help")
            .Select(static v => v.Name);
        lines.Add($"  gated: {string.Join(", ", gated)}");
        lines.Add($"  free:  {string.Join(", ", free)}");
        lines.Add("  'wait' needs no lock but refreshes one you hold, because a barrier outlasts the TTL.");
        lines.Add("  'stop' needs no lock either, so an orphan or a dead session can always be cleaned");
        lines.Add("  up, but it refuses while another session's lock is live.");
        lines.Add("  --ttl-minutes (10) is a heartbeat; --idle-ceiling-minutes (60) is an absolute idle");
        lines.Add("  ceiling past which the lock is reclaimable even on a busy rig.");
        lines.Add("  --force overrides a refusal inside your own session. --break-lock takes a live lock");
        lines.Add("  off another session and is human-gated: only on the user's explicit say-so.");
        lines.Add(string.Empty);

        lines.Add("OPTIONS");
        foreach (var spec in Options.All)
        {
            var shape = spec.Kind switch
            {
                OptionKind.Flag when spec.DefaultsTrue => $"--{spec.Name} / --no-{spec.Name}",
                OptionKind.Flag => $"--{spec.Name}",
                OptionKind.Number => $"--{spec.Name} <n>",
                OptionKind.Choice => $"--{spec.Name} <{string.Join("|", spec.Choices!)}>",
                _ => $"--{spec.Name} <value>",
            };
            var def = spec.Default.Length > 0 ? $"[{spec.Default}] " : string.Empty;
            lines.Add($"  {shape,-46}{def}{spec.Help}");
        }

        lines.Add(string.Empty);
        lines.Add("Options are matched case-insensitively, dashes optional, and a unique prefix is");
        lines.Add("accepted: --targ, -Target and --target are one option. An option a verb does not");
        lines.Add("read is a usage error rather than a silent no-op.");
        lines.Add(string.Empty);

        lines.Add("EXIT CODES");
        lines.Add("  0  did what you asked                    5  no lock is held by you");
        lines.Add("  1  tried and failed                      6  the rig is busy");
        lines.Add("  2  the command itself was wrong          7  this binary is stale; rebuild it");
        lines.Add("  3  refused, with an alternative          8  a playtest was inconclusive");
        lines.Add("  4  the lock is held by another session");
        lines.Add(string.Empty);

        lines.Add($"instances root: {instancesRoot}");
        lines.Add($"                ({instancesRootSource})");
        lines.Add(string.Empty);
        lines.Add("Run 'testrig send --target clients' to see what a refusal looks like.");
        lines.Add("Add --json to any verb for the same answer as data.");

        return lines;
    }

    /// <summary>The whole surface as one JSON object. See the type remarks for why.</summary>
    public static string ToJson(string instancesRoot, string instancesRootSource)
    {
        var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = true }))
        {
            writer.WriteStartObject();
            writer.WriteString("usage", "testrig <verb> [--target all|server|clients|<instance>[,<instance>]] [options]");
            writer.WriteString("instancesRoot", instancesRoot);
            writer.WriteString("instancesRootSource", instancesRootSource);

            writer.WriteStartArray("verbs");
            foreach (var verb in VerbTable.All)
            {
                writer.WriteStartObject();
                writer.WriteString("name", verb.Name);
                writer.WriteString("group", verb.Group.ToString().ToLowerInvariant());
                writer.WriteString("defaultTarget", verb.Default switch
                {
                    TargetDefault.All => "all",
                    TargetDefault.None => "",
                    _ => "n/a",
                });
                writer.WriteStartArray("accepts");
                foreach (var kind in verb.Accepts) writer.WriteStringValue(RefusalMatrix.KindName(kind));
                writer.WriteEndArray();
                writer.WriteBoolean("needsLock", verb.NeedsLock);
                writer.WriteBoolean("readOnly", verb.ReadOnly);
                writer.WriteString("summary", verb.Summary);
                writer.WriteStartArray("options");
                foreach (var option in verb.Options) writer.WriteStringValue(option);
                writer.WriteEndArray();
                writer.WriteEndObject();
            }

            writer.WriteEndArray();

            writer.WriteStartArray("options");
            foreach (var spec in Options.All)
            {
                writer.WriteStartObject();
                writer.WriteString("name", spec.Name);
                writer.WriteString("kind", spec.Kind.ToString().ToLowerInvariant());
                writer.WriteString("default", spec.Default);
                writer.WriteBoolean("defaultsTrue", spec.DefaultsTrue);
                writer.WriteString("help", spec.Help);
                if (spec.Choices is not null)
                {
                    writer.WriteStartArray("choices");
                    foreach (var choice in spec.Choices) writer.WriteStringValue(choice);
                    writer.WriteEndArray();
                }

                writer.WriteEndObject();
            }

            writer.WriteEndArray();

            writer.WriteStartArray("refusals");
            foreach (var row in RefusalMatrix.All)
            {
                writer.WriteStartObject();
                writer.WriteString("verb", row.Verb);
                writer.WriteString("targetKind", row.TargetKind);
                writer.WriteString("condition", row.Condition);
                writer.WriteString("what", row.Refusal.What);
                writer.WriteString("why", row.Refusal.Why);
                writer.WriteString("insteadLabel", row.Refusal.InsteadLabel);
                writer.WriteString("instead", row.Refusal.Instead);
                writer.WriteString("reference", row.Refusal.Reference);
                writer.WriteEndObject();
            }

            writer.WriteEndArray();

            writer.WriteStartObject("exitCodes");
            writer.WriteNumber("ok", ExitCodes.Ok);
            writer.WriteNumber("failed", ExitCodes.Failed);
            writer.WriteNumber("usageError", ExitCodes.UsageError);
            writer.WriteNumber("refused", ExitCodes.Refused);
            writer.WriteNumber("lockHeldByOther", ExitCodes.LockHeldByOther);
            writer.WriteNumber("lockNotHeld", ExitCodes.LockNotHeld);
            writer.WriteNumber("rigBusy", ExitCodes.RigBusy);
            writer.WriteNumber("staleBinary", ExitCodes.StaleBinary);
            writer.WriteNumber("playtestInconclusive", ExitCodes.PlaytestInconclusive);
            writer.WriteEndObject();

            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.ToArray());
    }
}
