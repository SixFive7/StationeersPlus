using System.Text.Json.Serialization;

namespace TestRig.Contracts;

// The scenario endpoints: /scenarios, /scenario/run, /scenario/arm, /scenario/disarm.
//
// These four arrived with the merged TestRig plugin and are the half of it that used to be
// ScenarioRunner. Before the merge a scenario could only be armed by editing a config value
// and restarting the host, and its result could only be recovered by grepping a log file the
// caller had to name. That failed silently four separate ways: the rig's own state reset
// blanks config values at session boundaries, arming needed a restart which ends the session
// under test, three different causes of "emitted nothing" were indistinguishable, and the
// grep usually targeted data/server.log while the lines land in install/BepInEx/LogOutput.log.
//
// The response shapes below are what closes each of those: an armed set with its SOURCE, a
// per-scenario catalogue saying what is armed and what has been dispatched, and a run that
// returns the lines it produced rather than naming a file.
//
// They are answered by the dedicated server, which is the only host that ticks scenarios.

/// <summary><c>/scenarios</c>. No parameters.</summary>
public sealed record ScenariosRequest;

/// <summary>One row of the scenario catalogue.</summary>
/// <remarks>
///     <c>dispatched</c> means the id reached the switch, NOT that it emitted anything: a
///     one-shot whose fired-guard already tripped, a settle-gated probe short of its settle
///     tick, and a probe blocked on a missing assembly are all dispatched and all silent.
/// </remarks>
public sealed record ScenarioRow
{
    [JsonPropertyName("id")]
    public string? Id { get; init; }

    [JsonPropertyName("armed")]
    public bool Armed { get; init; }

    [JsonPropertyName("dispatched")]
    public bool Dispatched { get; init; }

    /// <summary>It must be running before or during a world load, so no HTTP call can time it.</summary>
    [JsonPropertyName("bootOrdered")]
    public bool BootOrdered { get; init; }

    /// <summary>A passive request-file poller rather than a one-shot probe. Armed, never run.</summary>
    [JsonPropertyName("poller")]
    public bool Poller { get; init; }

    [JsonPropertyName("continuous")]
    public bool Continuous { get; init; }

    [JsonPropertyName("suggestedTicks")]
    public int SuggestedTicks { get; init; }

    /// <summary>The mod assembly it reads. Absent when it needs none.</summary>
    [JsonPropertyName("requiresAssembly")]
    public string? RequiresAssembly { get; init; }

    /// <summary>Why it cannot fire, when it cannot. Absent when nothing blocks it.</summary>
    [JsonPropertyName("blocked")]
    public string? Blocked { get; init; }
}

/// <summary>The whole catalogue, plus where the armed set came from.</summary>
public sealed record ScenariosResponse : IWireResult
{
    [JsonPropertyName("ok")]
    public bool Ok { get; init; }

    /// <summary>Which process answered: the dedicated server or a game client.</summary>
    [JsonPropertyName("host")]
    public string? Host { get; init; }

    /// <summary>Whether <c>OnPrefabsLoaded</c> has fired. Nothing can run before it does.</summary>
    [JsonPropertyName("dispatcherArmed")]
    public bool DispatcherArmed { get; init; }

    [JsonPropertyName("armed")]
    public string? Armed { get; init; }

    /// <summary>
    ///     Which of the armed file and the config entry won.
    /// </summary>
    /// <remarks>
    ///     Deliberately not the BepInEx config entry by default: the rig's state reset blanks
    ///     that entry at session boundaries, which silently disarmed probes four times.
    /// </remarks>
    [JsonPropertyName("armedSource")]
    public string? ArmedSource { get; init; }

    /// <summary>Outside <c>BepInEx/config</c>, so the reset does not touch it.</summary>
    [JsonPropertyName("armedFile")]
    public string? ArmedFile { get; init; }

    [JsonPropertyName("configValue")]
    public string? ConfigValue { get; init; }

    [JsonPropertyName("fileValue")]
    public string? FileValue { get; init; }

    [JsonPropertyName("delayTicks")]
    public int DelayTicks { get; init; }

    /// <summary>Simulation ticks seen. Zero means nothing armed can ever have fired.</summary>
    [JsonPropertyName("ticksSeen")]
    public long TicksSeen { get; init; }

    [JsonPropertyName("count")]
    public int Count { get; init; }

    /// <summary>Armed ids that reached the switch unrecognised.</summary>
    [JsonPropertyName("unknownArmed")]
    public string[]? UnknownArmed { get; init; }

    [JsonPropertyName("scenarios")]
    public ScenarioRow[]? Scenarios { get; init; }

    /// <summary>The armed file and the config entry disagree. Reported, never silently resolved.</summary>
    [JsonPropertyName("conflict")]
    public string? Conflict { get; init; }

    [JsonPropertyName("fileError")]
    public string? FileError { get; init; }

    /// <summary>
    ///     Present when <see cref="TicksSeen"/> is zero on an armed dispatcher.
    /// </summary>
    /// <remarks>
    ///     Measured: a dedicated server started with <c>-new Lunar</c> and Force Unpause
    ///     Without Client off ran ZERO ticks across 287 seconds. Not "a few then a pause":
    ///     none. The control plane is unaffected, because the Unity main thread keeps running
    ///     at about 24 Hz throughout, which is why this endpoint answered at all.
    /// </remarks>
    [JsonPropertyName("warning")]
    public string? Warning { get; init; }

    [JsonPropertyName("note")]
    public string? Note { get; init; }
}

/// <summary><c>/scenario/run</c>: one scenario for N SIMULATION ticks.</summary>
/// <remarks>
///     Ticks, not frames. On the dedicated server the two clocks do not correspond, and the
///     dispatcher even dedupes by <c>Time.frameCount</c>, so waiting on frames would be
///     measuring a different clock entirely.
/// </remarks>
public sealed record ScenarioRunRequest
{
    [JsonPropertyName("id")]
    public string? Id { get; init; }

    /// <summary>Accepted alias for <see cref="Id"/>.</summary>
    [JsonPropertyName("scenario")]
    public string? Scenario { get; init; }

    /// <summary>Simulation ticks to run for. Unset takes the scenario's own suggestion.</summary>
    [JsonPropertyName("ticks")]
    public int? Ticks { get; init; }

    [JsonPropertyName("timeoutMs")]
    public int? TimeoutMs { get; init; }
}

/// <summary>One captured log line, with the console tee's own sequence number.</summary>
public sealed record ScenarioLine
{
    [JsonPropertyName("seq")]
    public long Seq { get; init; }

    [JsonPropertyName("text")]
    public string? Text { get; init; }
}

/// <summary>What one run produced, including every line it wrote.</summary>
/// <remarks>
///     The lines come back in the body precisely so the caller never picks a log file. The
///     grep that this replaces targeted <c>data/server.log</c>, which carries Unity output,
///     while <c>[ScenarioRunner]</c> lines land in <c>install/BepInEx/LogOutput.log</c>, and
///     the empty result was indistinguishable from a probe that never fired.
/// </remarks>
public sealed record ScenarioRunResponse : IWireResult
{
    [JsonPropertyName("ok")]
    public bool Ok { get; init; }

    [JsonPropertyName("id")]
    public string? Id { get; init; }

    [JsonPropertyName("host")]
    public string? Host { get; init; }

    [JsonPropertyName("ticksRequested")]
    public int TicksRequested { get; init; }

    [JsonPropertyName("ticksRun")]
    public long TicksRun { get; init; }

    /// <summary>How far the SIMULATION advanced during the call. Zero means it cannot have run.</summary>
    [JsonPropertyName("simTicksAdvanced")]
    public long SimTicksAdvanced { get; init; }

    [JsonPropertyName("ticksSeen")]
    public long TicksSeen { get; init; }

    [JsonPropertyName("completed")]
    public bool Completed { get; init; }

    [JsonPropertyName("bootOrdered")]
    public bool BootOrdered { get; init; }

    [JsonPropertyName("count")]
    public int Count { get; init; }

    [JsonPropertyName("requiresAssembly")]
    public string? RequiresAssembly { get; init; }

    [JsonPropertyName("lines")]
    public ScenarioLine[]? Lines { get; init; }

    /// <summary><c>pass</c>, <c>fail</c>, <c>inconclusive</c> or <c>none</c>.</summary>
    [JsonPropertyName("verdict")]
    public string? Verdict { get; init; }

    /// <summary>Set when the simulation did not tick at all, so the scenario cannot have run.</summary>
    [JsonPropertyName("error")]
    public string? Error { get; init; }

    /// <summary>Set when it ran and emitted nothing, naming the three causes of that.</summary>
    [JsonPropertyName("note")]
    public string? Note { get; init; }

    /// <summary>Set when a load-ordered scenario was run late, so the result is indicative only.</summary>
    [JsonPropertyName("warning")]
    public string? Warning { get; init; }
}

/// <summary><c>/scenario/arm</c>: one id, or several separated by commas.</summary>
public sealed record ScenarioArmRequest
{
    [JsonPropertyName("id")]
    public string? Id { get; init; }

    /// <summary>Accepted alias for <see cref="Id"/>, for a comma-separated set.</summary>
    [JsonPropertyName("ids")]
    public string? Ids { get; init; }

    /// <summary>Accepted alias for <see cref="Id"/>.</summary>
    [JsonPropertyName("scenario")]
    public string? Scenario { get; init; }

    /// <summary>
    ///     Write the armed set to the armed FILE as well, so it survives the next boot.
    ///     Defaults to true; the load-ordered probes cannot be served without it.
    /// </summary>
    [JsonPropertyName("persist")]
    public bool? Persist { get; init; }
}

/// <summary>What is armed now, and whether it will still be armed after a restart.</summary>
public sealed record ScenarioArmResponse : IWireResult
{
    [JsonPropertyName("ok")]
    public bool Ok { get; init; }

    [JsonPropertyName("armed")]
    public string? Armed { get; init; }

    [JsonPropertyName("armedSource")]
    public string? ArmedSource { get; init; }

    [JsonPropertyName("armedFile")]
    public string? ArmedFile { get; init; }

    [JsonPropertyName("persisted")]
    public bool Persisted { get; init; }

    /// <summary>The dispatcher re-reads the armed set every tick, so no restart is needed.</summary>
    [JsonPropertyName("liveFromNextTick")]
    public bool LiveFromNextTick { get; init; }

    [JsonPropertyName("note")]
    public string? Note { get; init; }
}

/// <summary><c>/scenario/disarm</c>: clears the armed set.</summary>
public sealed record ScenarioDisarmRequest
{
    [JsonPropertyName("persist")]
    public bool? Persist { get; init; }
}

/// <summary>
///     The armed set after clearing it.
/// </summary>
/// <remarks>
///     Disarming stops FUTURE ticks; it does not undo a probe. A scenario already dispatched
///     this session keeps whatever state it set.
/// </remarks>
public sealed record ScenarioDisarmResponse : IWireResult
{
    [JsonPropertyName("ok")]
    public bool Ok { get; init; }

    [JsonPropertyName("armed")]
    public string? Armed { get; init; }

    [JsonPropertyName("armedFile")]
    public string? ArmedFile { get; init; }

    [JsonPropertyName("persisted")]
    public bool Persisted { get; init; }

    [JsonPropertyName("note")]
    public string? Note { get; init; }
}
