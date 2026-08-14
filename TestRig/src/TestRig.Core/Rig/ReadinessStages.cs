using TestRig.Contracts;

namespace TestRig.Core.Rig;

/// <summary>The readiness stages a barrier can wait for.</summary>
/// <remarks>
/// The three client stages are genuinely different and conflating them is a real trap: a
/// loaded plugin count alone is not "ready", because the splash screen is still drawing
/// and it suppresses the in-game windows.
/// </remarks>
public enum ReadinessStage
{
    /// <summary>The control plane answers at all. Never touches the Unity main thread.</summary>
    Ping,

    /// <summary>At least <see cref="RigConstants.StageMinPlugins"/> plugins loaded.</summary>
    ModsLoaded,

    /// <summary>Initialised AND sitting at the main menu.</summary>
    Menu,

    /// <summary>In a world.</summary>
    InWorld,

    /// <summary>
    /// The dedicated server's process is up. Explicitly NOT readiness (SERVER-128).
    /// </summary>
    Process,
}

/// <summary>Whether a status payload is at or past a stage. Pure, so a test can pin it.</summary>
public static class ReadinessStages
{
    /// <summary>The stage names a caller may type, and what each maps to.</summary>
    public static readonly IReadOnlyDictionary<string, ReadinessStage> ByName =
        new Dictionary<string, ReadinessStage>(StringComparer.OrdinalIgnoreCase)
        {
            ["ping"] = ReadinessStage.Ping,
            ["modsLoaded"] = ReadinessStage.ModsLoaded,
            ["menu"] = ReadinessStage.Menu,
            ["inWorld"] = ReadinessStage.InWorld,
            ["process"] = ReadinessStage.Process,
        };

    /// <summary>The canonical spelling, for a message that echoes what was asked for.</summary>
    public static string Name(ReadinessStage stage) => stage switch
    {
        ReadinessStage.Ping => "ping",
        ReadinessStage.ModsLoaded => "modsLoaded",
        ReadinessStage.Menu => "menu",
        ReadinessStage.InWorld => "inWorld",
        ReadinessStage.Process => "process",
        _ => "unknown",
    };

    /// <summary>The three stages a dedicated server can never reach (COMMON-123).</summary>
    public static bool IsClientOnly(ReadinessStage stage) =>
        stage is ReadinessStage.Ping or ReadinessStage.ModsLoaded or ReadinessStage.Menu;

    /// <summary>Whether a <c>/status</c> payload is at or past the named stage.</summary>
    /// <remarks>
    /// <para>
    /// A null status satisfies nothing except <see cref="ReadinessStage.Ping"/>, which is
    /// satisfied by any payload at all (COMMON-062, COMMON-063). An unrecognised stage is
    /// false, which is unreachable through the name table but is the safe fallthrough
    /// (COMMON-067).
    /// </para>
    /// <para>
    /// <b><c>modsLoaded</c> is <c>&gt;=</c> the minimum, not <c>&gt;</c>.</b> The PowerShell
    /// compared with <c>-gt</c> against a constant named for a minimum, so the effective
    /// threshold was one higher than the number every reader saw, and its own suite only ever
    /// exercised 22 and 2, which straddle the discrepancy without touching it.
    /// </para>
    /// </remarks>
    public static bool Reached(StatusResponse? status, ReadinessStage stage)
    {
        if (stage == ReadinessStage.Ping) return status is not null;
        if (status is null) return false;

        return stage switch
        {
            ReadinessStage.ModsLoaded => status.LoadedPluginCount >= RigConstants.StageMinPlugins,
            ReadinessStage.Menu => status.GameInitialized == true
                                   && string.Equals(status.Phase, "menu", StringComparison.Ordinal),
            ReadinessStage.InWorld => string.Equals(status.Phase, "inWorld", StringComparison.Ordinal),
            _ => false,
        };
    }
}
