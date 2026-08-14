using TestRig.Cli.Parsing;

namespace TestRig.Cli.Verbs;

/// <param name="Kind">The shape the rest of the command reasons about.</param>
/// <param name="Server">Whether the dedicated server is in the target. True for <c>all</c> and <c>server</c>.</param>
/// <param name="Names">The instance names, always a list so a single name is not enumerated character by character.</param>
/// <param name="Spec">What the caller typed, casing preserved, echoed back in refusals.</param>
public sealed record ResolvedTarget(TargetKind Kind, bool Server, IReadOnlyList<string> Names, string Spec);

/// <summary>
/// Turns <c>--target</c> into a shape, or explains why it cannot.
/// </summary>
/// <remarks>
/// <para>
/// Pure: the known-instance list is fed in, so the whole resolver is exercisable with no rig
/// at all. That seam is the one piece of the PowerShell launcher its suite could test, and
/// it is kept.
/// </para>
/// <para>
/// <b>Matching is case-insensitive, deliberately.</b> PowerShell's <c>-contains</c> and
/// <c>-eq</c> are case-insensitive for strings, so <c>--target HOSTIE</c> resolves against a
/// registry entry named <c>hostie</c> and always has. An ordinal port would change that in
/// silence, and the first symptom would be a check that cannot find an instance it just
/// created.
/// </para>
/// </remarks>
public static class TargetResolver
{
    public static ResolvedTarget Resolve(
        string verb,
        string target,
        IReadOnlyList<string> knownInstances,
        bool allowUnknown = false)
    {
        ArgumentNullException.ThrowIfNull(knownInstances);

        var spec = target ?? string.Empty;
        if (spec.Length == 0)
        {
            spec = VerbTable.TryGet(verb, out var v) && v.Default == TargetDefault.All ? "all" : string.Empty;
            if (spec.Length == 0)
            {
                throw new CliUsageException(
                    $"'{verb}' needs an explicit --target: 'server', 'clients', or one or more instance names. "
                    + "It acts on a specific running thing, so it will not guess. See what exists with: testrig list");
            }
        }

        switch (spec.ToLowerInvariant())
        {
            case "all":
                return new ResolvedTarget(TargetKind.All, true, knownInstances, spec);
            case "server":
                return new ResolvedTarget(TargetKind.Server, true, [], spec);
            case "clients":
                return new ResolvedTarget(TargetKind.Clients, false, knownInstances, spec);
        }

        var wanted = new List<string>();
        foreach (var part in spec.Split(','))
        {
            var name = part.Trim();
            if (name.Length > 0) wanted.Add(name);
        }

        if (wanted.Count == 0)
        {
            throw new CliUsageException(
                $"--target '{spec}' names nothing. Use 'all', 'server', 'clients', or one or more instance names.");
        }

        if (!allowUnknown)
        {
            foreach (var name in wanted)
            {
                if (Contains(knownInstances, name)) continue;

                var known = knownInstances.Count > 0
                    ? string.Join(", ", knownInstances)
                    : "(none provisioned)";
                throw new CliUsageException(
                    $"--target '{name}' is not a provisioned instance, and it is not 'all', 'server' or 'clients'. "
                    + $"Provisioned: {known}. Create it with: testrig create --target {name} [--role host]");
            }
        }

        return new ResolvedTarget(TargetKind.Instance, false, wanted, spec);
    }

    private static bool Contains(IReadOnlyList<string> names, string candidate)
    {
        foreach (var name in names)
            if (string.Equals(name, candidate, StringComparison.OrdinalIgnoreCase))
                return true;
        return false;
    }
}
