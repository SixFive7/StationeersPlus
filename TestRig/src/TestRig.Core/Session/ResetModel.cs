namespace TestRig.Core.Session;

/// <summary>What a reset action does. Every kind is a delete site or a write site.</summary>
public enum ResetActionKind
{
    /// <summary>One file. Never recursive; the path is always plan-built.</summary>
    DeleteFile,

    /// <summary>Every top-level child of a directory, recursively. The directory itself survives.</summary>
    DeleteContents,

    /// <summary>Files matching a filter in one directory. Files only, so no directory can match.</summary>
    DeleteGlob,

    /// <summary>A directory, recreated empty afterwards because provisioning creates it too.</summary>
    DeleteDirectory,

    /// <summary>A world. The only irreversible action in the whole subsystem.</summary>
    DeleteTree,

    /// <summary>Copy the source install's config tree in, removing orphan .cfg files it lacks.</summary>
    CopyConfigTree,

    /// <summary>Put one captured config file back.</summary>
    RestoreBaselineFile,

    /// <summary>Blank one setting's value, leaving every comment and every other value intact.</summary>
    BlankSetting,

    /// <summary>Re-point an instance at its own save root. Always, and always after a config write.</summary>
    ReapplySavePathOverride,
}

/// <summary>Something the plan wants to say rather than do.</summary>
public enum ResetReportKind
{
    BaselineAbsent,
    BaselineStale,
    BaselineUsed,
    BaselineMissesInstance,
    ConfigCopySkipped,
    ConfigTouched,
    NoTree,
    PreservedLivePid,
    StaleMod,
    WorldsNotTracked,
    SavesRetained,
    ClientWorldsNotTracked,
    ClientSavesRetained,

    /// <summary>The plan wants to delete more worlds than the ceiling allows. See <see cref="ResetPlan.BulkDeleteCeiling"/>.</summary>
    BulkWorldDeleteRefused,
}

/// <summary>One thing the reset will do.</summary>
/// <param name="AfterCopy">
/// The action follows a config write that can wipe SavePathOverride, which makes a failed
/// re-apply this reset's fault and therefore fatal even on a client.
/// </param>
public sealed record ResetAction(
    string Half,
    string? Instance,
    ResetActionKind Kind,
    string Path,
    string Label,
    string Reason,
    string? Source = null,
    string? Filter = null,
    string? Setting = null,
    string? Target = null,
    string? Role = null,
    int Items = 0,
    bool AfterCopy = false)
{
    /// <summary>Grouping key for the printed summary: the instance when there is one, else the half.</summary>
    public string Group => string.IsNullOrEmpty(Instance) ? Half : Instance;

    /// <summary>Who the failure message names.</summary>
    public string Who => string.IsNullOrEmpty(Instance) ? $"the {Half} half" : $"instance '{Instance}'";
}

/// <summary>Something the plan reports without acting on it.</summary>
public sealed record ResetReport(string Half, string? Instance, ResetReportKind Kind, string Detail, bool Warn = false)
{
    public string Who => string.IsNullOrEmpty(Instance) ? Half : Instance;
}

/// <summary>Whether a baseline exists and whether it still describes this rig.</summary>
public sealed record BaselineStaleness(bool Present, bool Stale, IReadOnlyList<string> Reasons);

/// <summary>The whole plan. Pure data: producing it moves not one byte.</summary>
public sealed record ResetPlan(
    string GeneratedUtc,
    string RigHome,
    string? SourceInstall,
    IReadOnlyList<string> Instances,
    IReadOnlyList<ResetAction> Actions,
    IReadOnlyList<ResetReport> Reports,
    bool KeepState,
    string? LastResetUtc,
    BaselineStaleness Baseline,
    SessionWorldSnapshot ServerWorlds,
    SessionWorldSnapshot ClientWorlds,
    int WorldDeleteCount,
    bool BulkDeleteCeilingExceeded)
{
    /// <summary>
    /// How many <see cref="ResetActionKind.DeleteTree"/> actions a plan may carry before it
    /// is refused without an explicit override.
    /// </summary>
    /// <remarks>
    /// Belt and braces over the tri-state world scan, because the blast radius of getting
    /// this wrong is somebody's entire save tree. A session that legitimately creates more
    /// than a handful of worlds is vanishingly rare; a plan to delete twenty-five, which
    /// is what the empty-set defect produced on this machine, is almost certainly a bug.
    /// Five leaves generous room for a multi-world test and still catches the failure by a
    /// wide margin.
    /// </remarks>
    public const int BulkDeleteCeiling = 5;

    public IEnumerable<ResetAction> WorldDeletes =>
        Actions.Where(static a => a.Kind == ResetActionKind.DeleteTree);
}

/// <summary>What a reset run actually did.</summary>
public sealed record ResetRun(
    bool Refused,
    string RefusalReason,
    bool Skipped,
    bool WhatIf,
    IReadOnlyList<ResetAction> Performed,
    IReadOnlyList<string> Failures,
    ResetPlan Plan)
{
    /// <summary>
    /// On a dry run only: why the REAL reset would have been refused, or empty.
    /// </summary>
    /// <remarks>
    /// <c>Refused</c> is about the run that just happened, and a dry run is never refused
    /// because it never tries anything. Measured 2026-08-14: a dry run printed that the real
    /// reset would be refused by the bulk-delete ceiling with 25 worlds at risk, and still
    /// reported <c>refused: false</c> and exited 0, so a caller branching on either learned
    /// nothing. The finding is in the prose; it needs to be in the data as well.
    /// </remarks>
    public string WouldRefuseReason { get; init; } = string.Empty;

    /// <summary>True when a dry run found the real reset would be refused.</summary>
    public bool WouldRefuse => WouldRefuseReason.Length > 0;
}

/// <summary>Whether the rig will tolerate a state change right now.</summary>
public sealed record ResetGate(bool Allowed, string Reason, BusySignal Busy);
