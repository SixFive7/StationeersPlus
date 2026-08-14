using TestRig.Core.Abstractions;

namespace TestRig.Core.Session;

/// <summary>How a reset run was asked to behave.</summary>
public sealed record ResetOptions
{
    /// <summary>Compute and print the plan, change nothing.</summary>
    public bool WhatIf { get; init; }

    /// <summary>Release or acquire without restoring. The marker stays set, so the debt passes on.</summary>
    public bool KeepState { get; init; }

    /// <summary>
    /// Override the bulk world-delete ceiling.
    /// </summary>
    /// <remarks>
    /// The one flag that lets a plan delete more than <see cref="ResetPlan.BulkDeleteCeiling"/>
    /// worlds. Explicit by design: the failure it guards against produces a plan that looks
    /// entirely ordinary.
    /// </remarks>
    public bool AllowBulkWorldDelete { get; init; }

    public string Reason { get; init; } = "session start";
}

/// <summary>
/// Runs a restore plan.
/// </summary>
/// <remarks>
/// Execution continues past a failing action and only throws at the end, deliberately: a
/// plan whose fifth action fails still runs actions six through twenty, and the throw
/// names the half-reset instance while the rig stays marked dirty. Collect, then throw;
/// never fail fast.
/// </remarks>
public sealed class ResetExecutor : IRigRestore
{
    private readonly IFileSystem _fs;
    private readonly IClock _clock;
    private readonly IOutput _output;
    private readonly ResetPlanner _planner;
    private readonly DirtyMarker _marker;
    private readonly SessionStateStore _state;

    public ResetExecutor(
        IFileSystem fs,
        IClock clock,
        IOutput output,
        ResetPlanner planner,
        DirtyMarker marker,
        SessionStateStore state)
    {
        _fs = fs;
        _clock = clock;
        _output = output;
        _planner = planner;
        _marker = marker;
        _state = state;
    }

    /// <inheritdoc/>
    public ResetRun Restore(bool keepState, string reason) =>
        Run(null, new ResetOptions { KeepState = keepState, Reason = reason });

    public ResetRun Run(ResetPlan? plan, ResetOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var gate = _planner.CheckGate();
        plan ??= _planner.Build(options.KeepState);

        // Carried forward on every non-performing path. Overwriting it with nothing would
        // erase the only reference point the ConfigTouched report has.
        var previousReset = _state.ReadLastResetUtc();

        if (options.WhatIf)
        {
            // The PowerShell dry run returned BEFORE the busy gate and before the
            // keep-state branch, even though the gate had already been computed, so it
            // printed a full plan and never said the real reset would be refused (spec
            // 03-reset defect D-1). Here the dry run says what would actually happen.
            _output.Line(OutputLevel.Info, "[Reset] --what-if: nothing was changed. The reset would do:");

            if (!gate.Allowed)
            {
                _output.Line(OutputLevel.Warning,
                    $"[Reset]   nothing: the real reset would be REFUSED because the rig is in use ({gate.Reason}).");
            }
            else if (options.KeepState)
            {
                _output.Line(OutputLevel.Warning,
                    "[Reset]   nothing: --keep-state would SKIP the restore entirely. The plan below is what it "
                    + "would have done.");
            }
            else if (plan.BulkDeleteCeilingExceeded && !options.AllowBulkWorldDelete)
            {
                _output.Line(OutputLevel.Warning, "[Reset]   " + ResetPlanner.BulkDeleteDetail(plan.Actions));
            }

            WritePlanSummary(plan, "[Reset]  ", includeReports: true);
            return new ResetRun(false, string.Empty, false, true, [], [], plan);
        }

        if (!gate.Allowed)
        {
            _output.Line(OutputLevel.Warning,
                $"[Reset] State reset SKIPPED: the rig is in use ({gate.Reason}). Nothing was deleted. Stop what is "
                + "running (testrig stop --target all --as <id>, or kill an untracked pid), then release and re-take "
                + "the lock to get a clean rig. This session starts on whatever the previous one left behind.");

            // Still written, or this session's unlock would diff against a previous
            // session's snapshot and report that session's changes as its own.
            _state.Save(previousReset);
            return new ResetRun(true, gate.Reason, false, false, [], [], plan);
        }

        if (plan.BulkDeleteCeilingExceeded && !options.AllowBulkWorldDelete)
        {
            var detail = ResetPlanner.BulkDeleteDetail(plan.Actions);
            _output.Line(OutputLevel.Warning, "[Reset] " + detail);
            _state.Save(previousReset);
            return new ResetRun(true, detail, false, false, [], [], plan);
        }

        if (options.KeepState)
        {
            _output.Line(OutputLevel.Warning,
                "[Reset] --keep-state: the between-session state reset was SKIPPED on purpose. This session inherits "
                + "whatever the previous one left behind, dedicated-server worlds included, and the dirty marker "
                + "stays set so the next session cleans up.");
            WritePlanSummary(plan, "[Reset]   would have reset", includeReports: true);
            _state.Save(previousReset);
            return new ResetRun(false, string.Empty, true, false, [], [], plan);
        }

        var performed = new List<ResetAction>();
        var failures = new List<string>();

        foreach (var action in plan.Actions)
        {
            try
            {
                Perform(action);
                performed.Add(action);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or RigRefusalException or InvalidOperationException)
            {
                failures.Add($"{action.Who} : {action.Label} failed ({action.Kind} {action.Path}): {ex.Message}");
            }
        }

        WriteOutcome(plan, performed, options.Reason);

        // After the reset, so the session's shared-state comparison starts from the state
        // the session actually begins with.
        _state.Save(RigTime.Stamp(_clock.UtcNow));

        if (failures.Count == 0)
        {
            try
            {
                _marker.Clear();
            }
            catch (RigRefusalException ex)
            {
                // The rig IS clean at this point; the only cost is one redundant restore.
                _output.Line(OutputLevel.Warning,
                    $"[Reset] The restore completed but the dirty marker could not be cleared: {ex.Message}");
            }
        }
        else
        {
            foreach (var failure in failures) _output.Line(OutputLevel.Warning, "[Reset] " + failure);
            throw new RigRefusalException(
                RigRefusalKind.Broken,
                $"The rig state reset failed on {failures.Count} action(s), so at least one instance is HALF RESET "
                + $"and must not be trusted for a test: {string.Join("; ", failures)}");
        }

        return new ResetRun(false, string.Empty, false, false, performed, failures, plan);
    }

    /// <summary>Performs one action. Every path here is a delete site or a write site.</summary>
    public void Perform(ResetAction action)
    {
        switch (action.Kind)
        {
            case ResetActionKind.DeleteFile:
                if (_fs.FileExists(action.Path)) _fs.DeleteFile(action.Path);
                break;

            case ResetActionKind.DeleteContents:
                // Recursive per child, but the parent directory survives.
                if (_fs.DirectoryExists(action.Path))
                {
                    foreach (var file in _fs.EnumerateFiles(action.Path, "*", recurse: false)) _fs.DeleteFile(file);
                    foreach (var dir in _fs.EnumerateDirectories(action.Path)) _fs.DeleteDirectory(dir, recursive: true);
                }
                break;

            case ResetActionKind.DeleteGlob:
                // Files only, so no directory can ever match a filter.
                if (_fs.DirectoryExists(action.Path))
                {
                    foreach (var file in _fs.EnumerateFiles(action.Path, action.Filter ?? "*", recurse: false))
                    {
                        _fs.DeleteFile(file);
                    }
                }
                break;

            case ResetActionKind.DeleteDirectory:
                if (_fs.DirectoryExists(action.Path)) _fs.DeleteDirectory(action.Path, recursive: true);
                // Recreated empty because provisioning creates it too.
                _fs.CreateDirectory(action.Path);
                break;

            case ResetActionKind.DeleteTree:
                // The only irreversible action in the subsystem. Not recreated.
                if (_fs.DirectoryExists(action.Path)) _fs.DeleteDirectory(action.Path, recursive: true);
                break;

            case ResetActionKind.CopyConfigTree:
                PerformCopyConfigTree(action);
                break;

            case ResetActionKind.RestoreBaselineFile:
                {
                    var parent = Path.GetDirectoryName(action.Path);
                    if (!string.IsNullOrEmpty(parent)) _fs.CreateDirectory(parent);
                    _fs.CopyFile(action.Source!, action.Path, overwrite: true);
                }
                break;

            case ResetActionKind.BlankSetting:
                ConfigFile.BlankSetting(_fs, action.Path, action.Setting!);
                break;

            case ResetActionKind.ReapplySavePathOverride:
                {
                    var written = SavePathOverride.Write(
                        _fs, _output, action.Path, action.Target!, action.Role ?? "unknown",
                        action.Instance ?? "", context: "Reset");

                    // A failed write is fatal even on a client when it follows a config write,
                    // because then this reset is what wiped the redirect.
                    if (!written && action.AfterCopy)
                    {
                        throw new RigRefusalException(
                            RigRefusalKind.Refused,
                            $"SavePathOverride could not be re-applied for instance '{action.Instance}' after this "
                            + "reset re-copied its BepInEx config, which wipes the redirect. The instance would write "
                            + "into the developer's tier-1 save folder.");
                    }
                }
                break;

            default:
                throw new InvalidOperationException($"Unknown reset action kind: {action.Kind}");
        }
    }

    /// <summary>Copies the source config tree in and removes orphan .cfg files it lacks.</summary>
    /// <remarks>
    /// Nothing but <c>*.cfg</c> is touched. A config a previous test's plugin created is
    /// garbage by the same argument as a value it flipped, so it goes; anything else in the
    /// directory stays.
    /// </remarks>
    private void PerformCopyConfigTree(ResetAction action)
    {
        _fs.CreateDirectory(action.Path);

        var sourceNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (!string.IsNullOrEmpty(action.Source) && _fs.DirectoryExists(action.Source))
        {
            foreach (var file in _fs.EnumerateFiles(action.Source, "*", recurse: false))
            {
                var leaf = Path.GetFileName(file);
                sourceNames.Add(leaf);
                _fs.CopyFile(file, Path.Combine(action.Path, leaf), overwrite: true);
            }
        }

        foreach (var file in _fs.EnumerateFiles(action.Path, "*.cfg", recurse: false))
        {
            if (sourceNames.Contains(Path.GetFileName(file))) continue;
            _fs.DeleteFile(file);
        }
    }

    /// <summary>The grouped label lines a plan prints.</summary>
    public void WritePlanSummary(ResetPlan plan, string prefix, bool includeReports)
    {
        if (plan.Actions.Count == 0)
        {
            _output.Line(OutputLevel.Info, $"{prefix} nothing (the rig is already clean)");
        }
        else
        {
            foreach (var group in plan.Actions.GroupBy(static a => a.Group))
            {
                _output.Line(OutputLevel.Info, $"{prefix} {group.Key}: {string.Join(", ", group.Select(static a => a.Label))}");
            }
        }

        if (includeReports) WriteReports(plan);
    }

    public void WriteReports(ResetPlan plan)
    {
        foreach (var report in plan.Reports)
        {
            _output.Line(report.Warn ? OutputLevel.Warning : OutputLevel.Info,
                report.Warn ? $"[Reset] {report.Who} : {report.Detail}" : $"[Reset]   kept  {report.Who} : {report.Detail}");
        }
    }

    /// <summary>The per-session report, including the mandatory worlds line.</summary>
    public void WriteOutcome(ResetPlan plan, IReadOnlyList<ResetAction> performed, string reason)
    {
        var scope = plan.Instances.Count > 0
            ? $"{plan.Instances.Count} instance(s) and the server half"
            : "the server half";

        _output.Line(OutputLevel.Info, $"[Reset] State reset on {reason}, over {scope} ({performed.Count} action(s))");

        if (performed.Count == 0)
        {
            _output.Line(OutputLevel.Info, "[Reset]   nothing to clear");
        }
        else
        {
            foreach (var group in performed.GroupBy(static a => a.Group))
            {
                _output.Line(OutputLevel.Info, $"[Reset]   {group.Key}: {string.Join(", ", group.Select(static a => a.Label))}");
            }
        }

        _output.Line(OutputLevel.Info,
            "[Reset]   kept: worlds that predate this session, seeded mods, deployed plugins, and anything placed "
            + "outside the recorded surface.");

        var cleanState = !plan.Baseline.Present
            ? "no baseline (built-in delete list only)"
            : plan.Baseline.Stale
                ? "a STALE baseline (config still restored from it)"
                : "the captured baseline";
        _output.Line(OutputLevel.Info, $"[Reset]   clean state: {cleanState}");

        // Always printed, even when nothing happened, because this is the only irreversible
        // thing the reset does.
        _output.Line(OutputLevel.Info, $"[Reset]   worlds: {WorldsLine(plan)}");

        WriteReports(plan);

        _output.Line(OutputLevel.Info,
            "[Reset]   the rig resets BETWEEN sessions only, so two unrelated tests under one lock get no reset "
            + "between them. Release and re-take the lock when the subject changes.");
    }

    internal static string WorldsLine(ResetPlan plan)
    {
        var deleted = plan.WorldDeleteCount;
        if (deleted == 0)
        {
            return plan.ServerWorlds.Recorded
                ? $"none deleted ({plan.ServerWorlds.Count} recorded as predating this session)"
                : $"none deleted ({plan.ServerWorlds.Reason})";
        }
        return $"{deleted} deleted; every other world was on the rig before this session touched it";
    }
}
