using System.Globalization;
using System.Text;
using TestRig.Playtest.Flakes;
using TestRig.Playtest.Model;

namespace TestRig.Playtest;

/// <summary>Printing the two catalogues a caller asks about before running anything.</summary>
/// <remarks>
///     There is no registry any more, and the absence is the point. Checks used to announce
///     themselves from a <c>[ModuleInitializer]</c> into a process-global list, which meant
///     nothing statically referenced a check type and the ILC trimmer removed all eight from
///     the shipped binary. They are named one by one in <c>TestRig.Playtests.Playtests.All</c>
///     now, and a caller passes that list in. See that type for the whole story.
/// </remarks>
public static class PlaytestListing
{
    /// <summary>
    ///     Refuses an empty check set, whoever asked and whatever they asked for.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///     There is no such thing as a legitimately empty check set: the list is compiled in,
    ///     so an empty one is a build defect and never a state of the rig. The shipped binary
    ///     once carried zero checks, because the ILC trimmer removed every class a module
    ///     initializer registered, and <c>--list-checks</c> answered with a bare header and
    ///     exit 0. Nobody reads a clean exit and an empty list as a broken artifact.
    ///     </para>
    ///     <para>
    ///     The guard lives HERE rather than in the caller's ordering, so it cannot be defeated
    ///     by moving a branch: rendering the listing is the thing that must not succeed on an
    ///     empty set, and the running path checks the same rule.
    ///     </para>
    /// </remarks>
    /// <exception cref="InvalidOperationException">When nothing is compiled in.</exception>
    public static void AssertAnyCompiledIn(IReadOnlyList<IPlaytestCheck> checks)
    {
        ArgumentNullException.ThrowIfNull(checks);
        if (checks.Count > 0) return;

        throw new InvalidOperationException(
            "This binary has NO playtest checks compiled into it, which is a build defect and not an empty rig. "
            + "Checks are C# under Mods/<Mod>/playtests/, named one by one in TestRig.Playtests.Playtests.All; "
            + "that list is what roots them for the AOT trimmer, and a check reachable only from a "
            + "[ModuleInitializer] is removed from the published binary while still running fine under dotnet "
            + "test. Rebuild: dotnet publish TestRig/src/TestRig.Cli/TestRig.Cli.csproj -c Release -r win-x64");
    }

    /// <summary>The checks, with what each needs.</summary>
    public static string Checks(IReadOnlyList<IPlaytestCheck> checks, string only = "*")
    {
        AssertAnyCompiledIn(checks);

        var selected = Runner.SuiteRunner.Select(checks, only).Select(c => c.Spec.Name).ToHashSet(StringComparer.Ordinal);
        var builder = new StringBuilder("Registered checks:\n");

        foreach (var check in checks)
        {
            var mark = selected.Contains(check.Spec.Name) ? ' ' : '-';
            builder.Append(CultureInfo.InvariantCulture, $"  {mark} {check.Spec.Name,-40}  {check.Spec.Summary}\n");

            foreach (var instance in check.Spec.Instances)
            {
                var world = instance.World is not null ? $" world {instance.World}"
                    : instance.Save is not null ? $" save {instance.Save}"
                    : string.Empty;
                builder.Append(CultureInfo.InvariantCulture, $"      {instance.Name} ({instance.Role.ToString().ToLowerInvariant()}){world}\n");
            }
        }

        // The count, spelled out. An empty listing used to render as a bare header, which is
        // exactly what a binary with every check trimmed out printed while exiting 0.
        builder.Append(CultureInfo.InvariantCulture,
            $"\n{checks.Count} check(s) compiled in, {selected.Count} selected by --only '{only}'.\n");

        return builder.ToString();
    }

    /// <summary>The flake taxonomy, as the code has it.</summary>
    public static string Flakes(FlakeCatalogue catalogue)
    {
        ArgumentNullException.ThrowIfNull(catalogue);

        var builder = new StringBuilder("Flake taxonomy, in resolution order (first match wins):\n\n");
        foreach (var detector in catalogue.Detectors)
        {
            var remedy = detector.Remedy switch
            {
                FlakeRemedy.RestartInstance => "restart-instance",
                FlakeRemedy.Abort => "abort",
                _ => "retry",
            };

            builder.Append(CultureInfo.InvariantCulture,
                $"  {detector.Name,-24} {remedy} (max {detector.MaxAttempts} attempt(s), {detector.GapSeconds}s gap)\n");
            builder.Append(CultureInfo.InvariantCulture, $"    {detector.Summary}\n");
            builder.Append(CultureInfo.InvariantCulture, $"    see: {detector.Reference}\n");
        }

        builder.Append("\nEvery one of these ends a check as INCONCLUSIVE, never as a failure.\n");
        return builder.ToString();
    }
}
