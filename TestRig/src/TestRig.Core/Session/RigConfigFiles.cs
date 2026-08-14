namespace TestRig.Core.Session;

/// <summary>
/// The BepInEx config files the rig itself owns, by name.
/// </summary>
/// <remarks>
/// <para>
/// The rig's own control-plane plugins are the only ones whose config the reset has an
/// opinion about beyond restoring it, so they are named once here rather than spelled out
/// at each of the three places that ask.
/// </para>
/// <para>
/// Three names and not two, because the merge renamed the file. <c>ClientDriver</c> wrote
/// <c>net.clientdriver.cfg</c>, <c>ScenarioRunner</c> wrote <c>net.scenariorunner.cfg</c>,
/// and the merged plugin that replaces both writes <c>net.sixfive7.testrig.cfg</c>. All
/// three trees exist during the parity window and any of them may be the one deployed, so a
/// reset that knew only the old names would leave a probe armed on the new plugin and blank
/// nothing.
/// </para>
/// </remarks>
public static class RigConfigFiles
{
    /// <summary>The client half's control plane, before the merge.</summary>
    public const string ClientDriver = "net.clientdriver.cfg";

    /// <summary>The dedicated server's scenario probe host, before the merge.</summary>
    public const string ScenarioRunner = "net.scenariorunner.cfg";

    /// <summary>The merged plugin, which replaces both of the above on both halves.</summary>
    public const string TestRig = "net.sixfive7.testrig.cfg";

    /// <summary>Every config file the rig's own plugins write.</summary>
    public static readonly IReadOnlyList<string> All = [ClientDriver, ScenarioRunner, TestRig];

    /// <summary>
    /// The setting naming which probe fires on the next boot.
    /// </summary>
    /// <remarks>
    /// Blanked at every session boundary: a scenario left armed injects itself into an
    /// unrelated test's log, and the log line it produces is entirely plausible. The merged
    /// plugin moved the armed set OUT of the config file for exactly the opposite reason (so
    /// the reset could not silently disarm a session), and keeps the config entry as a
    /// fallback, which is why blanking it is still correct.
    /// </remarks>
    public const string ScenarioSetting = "Scenario";

    /// <summary>The config files that may carry <see cref="ScenarioSetting"/>.</summary>
    public static readonly IReadOnlyList<string> ScenarioCarrying = [ScenarioRunner, TestRig];

    /// <summary>Whether a path names one of the scenario-carrying config files.</summary>
    public static bool CarriesScenario(string? path)
    {
        if (string.IsNullOrEmpty(path)) return false;
        var leaf = Path.GetFileName(path);
        foreach (var name in ScenarioCarrying)
        {
            if (string.Equals(leaf, name, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    /// <summary>Whether a path names any config file the rig's own plugins write.</summary>
    public static bool IsRigOwned(string? path)
    {
        if (string.IsNullOrEmpty(path)) return false;
        var leaf = Path.GetFileName(path);
        foreach (var name in All)
        {
            if (string.Equals(leaf, name, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }
}
