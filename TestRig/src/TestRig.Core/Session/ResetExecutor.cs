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
/// <para>
/// Execution continues past a failing action and only throws at the end, deliberately: a
/// plan whose fifth action fails still runs actions six through twenty, and the throw
/// names the half-reset instance while the rig stays marked dirty. Collect, then throw;
/// never fail fast.
/// </para>
/// <para>
/// An action that failed on an IO error is swept again before it counts as a failure. See
/// <see cref="TransientRetryDelaysMs"/> for the race that makes this necessary and for why
/// the budget is per RUN rather than per action.
/// </para>
/// </remarks>
public sealed class ResetExecutor : IRigRestore
{
    private readonly IFileSystem _fs;
    private readonly IClock _clock;
    private readonly ISleeper _sleeper;
    private readonly IOutput _output;
    private readonly ResetPlanner _planner;
    private readonly DirtyMarker _marker;
    private readonly SessionStateStore _state;

    public ResetExecutor(
        IFileSystem fs,
        IClock clock,
        ISleeper sleeper,
        IOutput output,
        ResetPlanner planner,
        DirtyMarker marker,
        SessionStateStore state)
    {
        _fs = fs;
        _clock = clock;
        _sleeper = sleeper;
        _output = output;
        _planner = planner;
        _marker = marker;
        _state = state;
    }

    /// <summary>
    /// How long the run waits between sweeps over the actions that hit an IO error.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Windows releases a process's file handles asynchronously after it exits.</b> "The
    /// process object is gone" does not mean "its files are closed", so the reset at the top of
    /// a session routinely runs while the instance the PREVIOUS check stopped is still letting
    /// go of its Unity log. Measured 2026-08-16: the reset failed on
    /// <c>&lt;instance&gt;\logs\unity-*.log</c> with a sharing violation, which ended that
    /// check and, through a lock leak that has since been fixed, the two behind it. A
    /// transient, self-healing condition was being treated as terminal.
    /// </para>
    /// <para>
    /// <b>The refusal it does not weaken.</b> Refusing to test on a half-reset rig is correct
    /// and is unchanged: an action that is still failing when the budget runs out is a failure,
    /// the run throws, the marker stays set, and nothing is silently skipped. A file the reset
    /// could not clear is exactly how a stale log poisons a later assertion, so the only thing
    /// bought here is time for a handle to close.
    /// </para>
    /// <para>
    /// <b>Per RUN, not per action, and that is the point of sweeping rather than looping in
    /// place.</b> Each pass retries every action still failing, so twenty held files cost the
    /// same wall clock as one: 7.75 s of added delay at worst, whatever the plan looks like.
    /// A per-action retry with this budget would be minutes on a plan that is genuinely stuck.
    /// The other actions running in between are free time for the handle as well.
    /// </para>
    /// <para>
    /// This sits on top of <c>SystemFileSystem.DeleteFile</c>'s own ten attempts, which is a
    /// 275 ms budget deliberately tuned for a virus scanner or the search indexer holding a
    /// file for a few milliseconds. Raising THAT to seconds would slow every failing delete in
    /// the rig and would still be per file. Two budgets, two causes, and the fast one stays
    /// fast.
    /// </para>
    /// </remarks>
    public static readonly IReadOnlyList<int> TransientRetryDelaysMs = [250, 500, 1000, 2000, 4000];

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

            // Whatever it prints here also comes back as data. A dry run whose only record of
            // "the real reset would refuse" was a console line left a caller branching on
            // refused (always false) and the exit code (always 0) with nothing to see.
            var wouldRefuse = string.Empty;

            if (!gate.Allowed)
            {
                wouldRefuse = gate.Reason;
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
                wouldRefuse = ResetPlanner.BulkDeleteDetail(plan.Actions);
                _output.Line(OutputLevel.Warning, "[Reset]   " + wouldRefuse);
            }

            WritePlanSummary(plan, "[Reset]  ", includeReports: true);
            return new ResetRun(false, string.Empty, false, true, [], [], plan)
            {
                WouldRefuseReason = wouldRefuse,
            };
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

        var (performed, failures) = PerformWithRetries(plan);

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

    /// <summary>
    /// Runs every action, sweeping the ones that hit an IO error again before giving up.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Only an IO-shaped failure is swept. A <see cref="RigRefusalException"/> or an
    /// <see cref="InvalidOperationException"/> is a decision the rig has already made (a
    /// setting that vanished between the plan and the execute, a redirect that cannot be
    /// re-applied after a config copy), and no amount of waiting changes it, so those fail on
    /// the first pass exactly as they always did.
    /// </para>
    /// <para>
    /// Every action here is idempotent, which is what makes a re-run safe: a delete checks for
    /// the file first, a contents-delete re-enumerates what is left, and a copy overwrites. An
    /// action that half-succeeded and then failed picks up from what is actually on disk.
    /// </para>
    /// </remarks>
    private (List<ResetAction> Performed, List<string> Failures) PerformWithRetries(ResetPlan plan)
    {
        var performed = new List<ResetAction>();
        var failures = new List<string>();
        var pending = new List<ResetAction>(plan.Actions);
        var sweep = 0;
        var recovered = 0;

        while (true)
        {
            var held = new List<(ResetAction Action, Exception Error)>();
            var failedBefore = failures.Count;

            foreach (var action in pending)
            {
                try
                {
                    // The action that goes into the summary is the one Perform hands back, not
                    // the one the plan carried (RESET-183). An action that warned and did
                    // nothing is relabelled, so the printed outcome cannot claim a write that
                    // did not happen.
                    performed.Add(Perform(action));
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    held.Add((action, ex));
                }
                catch (Exception ex) when (ex is RigRefusalException or InvalidOperationException)
                {
                    failures.Add(Describe(action, ex, sweep));
                }
            }

            if (sweep > 0) recovered += pending.Count - held.Count - (failures.Count - failedBefore);

            if (held.Count == 0)
            {
                if (recovered > 0)
                {
                    _output.Line(OutputLevel.Warning,
                        $"[Reset] {recovered} action(s) cleared on retry sweep {sweep} of "
                        + $"{TransientRetryDelaysMs.Count}: something was still holding those files open, and let go. "
                        + "The reset is complete and nothing was skipped, but a handle outliving the process that "
                        + "owned it is worth knowing about.");
                }
                break;
            }

            if (sweep >= TransientRetryDelaysMs.Count)
            {
                foreach (var (action, error) in held) failures.Add(Describe(action, error, sweep));
                break;
            }

            var delay = TransientRetryDelaysMs[sweep];
            _output.Line(OutputLevel.Info,
                $"[Reset] {held.Count} action(s) could not be performed because something still holds the file open; "
                + $"retrying in {delay} ms (sweep {sweep + 1} of {TransientRetryDelaysMs.Count}). Windows releases a "
                + "process's handles after it exits, so a just-stopped instance is the usual cause.");

            _sleeper.DelayAsync(TimeSpan.FromMilliseconds(delay)).GetAwaiter().GetResult();
            sweep++;
            pending = [.. held.Select(static h => h.Action)];
        }

        return (performed, failures);
    }

    /// <summary>One failure line, naming what was tried, how often, and what it looked like.</summary>
    /// <remarks>
    /// The sharing-violation hint is not decoration. This binary publishes with
    /// <c>UseSystemResourceKeys</c>, so the runtime's own message for a held file arrives as
    /// the bare resource key <c>IO_SharingViolation_File, &lt;path&gt;</c>, which teaches a
    /// reader nothing at all.
    /// </remarks>
    private static string Describe(ResetAction action, Exception error, int sweep)
    {
        var attempts = sweep == 0
            ? string.Empty
            : $" after {sweep + 1} attempts over {TransientRetryDelaysMs.Take(sweep).Sum()} ms";

        var hint = IsFileHeldOpen(error)
            ? " Another process still has the file open; it was not this reset's to close."
            : string.Empty;

        return $"{action.Who} : {action.Label} failed{attempts} ({action.Kind} {action.Path}): {error.Message}{hint}";
    }

    /// <summary>ERROR_SHARING_VIOLATION (32) or ERROR_LOCK_VIOLATION (33), as an HRESULT.</summary>
    /// <remarks>
    /// Read from <see cref="Exception.HResult"/> rather than from the message, because the
    /// message is a stripped resource key in the shipped binary and is localised in any build
    /// that keeps its resources. The text probe behind it is a fallback for a wrapped
    /// exception that lost the code, never the primary test.
    /// </remarks>
    public static bool IsFileHeldOpen(Exception? error)
    {
        if (error is null) return false;

        const int sharingViolation = unchecked((int)0x80070020);
        const int lockViolation = unchecked((int)0x80070021);

        if (error.HResult == sharingViolation || error.HResult == lockViolation) return true;

        return error.Message.Contains("IO_SharingViolation", StringComparison.OrdinalIgnoreCase)
               || error.Message.Contains("being used by another process", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Performs one action. Every path here is a delete site or a write site.</summary>
    /// <returns>
    /// The action AS PERFORMED. Identical to the input except where the action degraded to a
    /// warning, in which case its label says so (RESET-183): the outcome summary prints these
    /// labels, and one that still read "SavePathOverride re-applied" after the write failed
    /// would be the summary claiming the one thing standing between an instance and the
    /// developer's tier-1 save folder was in place when it was not.
    /// </returns>
    public ResetAction Perform(ResetAction action)
    {
        ArgumentNullException.ThrowIfNull(action);

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
                // RESET-184. The planner only plans this when GetSetting returned a non-empty
                // value, so reaching here with nothing to blank means the file changed between
                // the plan and the execute. Discarding the answer leaves a scenario armed with
                // nothing said, which is the one outcome this action exists to prevent.
                if (!ConfigFile.BlankSetting(_fs, action.Path, action.Setting!))
                {
                    throw new RigRefusalException(
                        RigRefusalKind.Broken,
                        $"setting '{action.Setting}' not found in {action.Path}. It was there when the plan was "
                        + "built, so the file changed underneath this reset and whatever that setting arms is "
                        + "still armed.");
                }
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

                    // RESET-182 and RESET-183: warn, relabel, and let the session start. Failing
                    // here would make the lock unobtainable, and rebuilding the instance needs
                    // the lock, so the rig would be unrepairable. The relabel is what keeps the
                    // summary honest about it.
                    if (!written) return action with { Label = FailedSavePathOverrideLabel };
                }
                break;

            default:
                throw new InvalidOperationException($"Unknown reset action kind: {action.Kind}");
        }

        return action;
    }

    /// <summary>What a re-apply that only warned is called in the outcome summary.</summary>
    public const string FailedSavePathOverrideLabel =
        "SavePathOverride NOT re-applied (see the warning above; this instance has no separate save root)";

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
