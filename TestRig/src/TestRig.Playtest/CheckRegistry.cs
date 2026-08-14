using System.Globalization;
using System.Text;
using TestRig.Playtest.Flakes;
using TestRig.Playtest.Model;

namespace TestRig.Playtest;

/// <summary>
///     Where checks announce themselves.
/// </summary>
/// <remarks>
///     <para>
///     An AOT binary cannot load managed assemblies at run time, so checks are not plugins
///     discovered on disk: they are C# compiled in. Each check file carries its own
///     <c>[ModuleInitializer]</c> and registers itself, so adding a check is adding a file
///     and there is no central list to forget.
///     </para>
///     <para>
///     Registration is idempotent by check name and source file, because a module can be
///     initialized once per load context and a duplicate would run the same check twice
///     under two locks.
///     </para>
///     <para>
///     Nothing in the engine reads this registry: <see cref="Runner.SuiteRunner"/> takes its
///     checks as an argument, so a test supplies its own list and never touches process-global
///     state. The registry is a convenience for the composition root and nothing more.
///     </para>
/// </remarks>
public static class PlaytestCheckRegistry
{
    private static readonly List<IPlaytestCheck> Checks = [];
    private static readonly Lock Gate = new();

    /// <summary>Everything registered so far, in registration order.</summary>
    public static IReadOnlyList<IPlaytestCheck> Registered
    {
        get
        {
            lock (Gate) return [.. Checks];
        }
    }

    /// <summary>Registers a check. Ignores an exact duplicate.</summary>
    public static void Register(IPlaytestCheck check)
    {
        ArgumentNullException.ThrowIfNull(check);

        lock (Gate)
        {
            var already = Checks.Any(c =>
                string.Equals(c.Spec.Name, check.Spec.Name, StringComparison.Ordinal) &&
                string.Equals(c.Spec.SourceFile, check.Spec.SourceFile, StringComparison.OrdinalIgnoreCase));

            if (!already) Checks.Add(check);
        }
    }

    /// <summary>Empties the registry. For tests only.</summary>
    public static void Clear()
    {
        lock (Gate) Checks.Clear();
    }
}

/// <summary>Printing the two catalogues a caller asks about before running anything.</summary>
public static class PlaytestListing
{
    /// <summary>The checks, with what each needs.</summary>
    public static string Checks(IReadOnlyList<IPlaytestCheck> checks, string only = "*")
    {
        ArgumentNullException.ThrowIfNull(checks);

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
