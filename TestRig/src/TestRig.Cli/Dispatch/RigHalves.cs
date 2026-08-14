using System.Globalization;
using TestRig.Core.Abstractions;
using TestRig.Core.Client;
using TestRig.Core.Rig;
using TestRig.Core.Server;

namespace TestRig.Cli.Dispatch;

/// <summary>How the CLI names a log request. Both halves take the same one.</summary>
/// <param name="Tail">How many lines to show.</param>
/// <param name="Grep">A regex filter, or empty.</param>
/// <param name="Unity">
/// Read the pre-BepInEx Unity log instead. Client instances only: every failure BEFORE
/// BepInEx loads lands in <c>data/&lt;instance&gt;/logs/unity-&lt;stamp&gt;.log</c>, which no
/// verb used to print. The dedicated server has one log and this flag means nothing there.
/// </param>
/// <remarks>
/// <b>Both apply.</b> In PowerShell <c>-Grep</c> silently overrode <c>-Tail</c>: when a
/// pattern was given the whole file was scanned and the tail count was ignored, so
/// <c>logs --tail 20 --grep Error</c> could return four thousand lines. The contract here is
/// filter first, then tail the matches, and a reader that ignores either half is wrong.
/// </remarks>
public sealed record LogQuery(int Tail, string Grep, bool Unity = false);

/// <summary>Everything <c>create</c> can set on a new instance.</summary>
/// <remarks>
/// <b>Every identity field is nullable, and null means "the caller did not type it."</b> That
/// distinction is load-bearing on a rebuild: <c>create --force</c> is the routine way to pick
/// up a new plugin build, and a value that was not typed is KEPT from the existing entry.
/// Handing Core a defaulted <c>--role client</c> would silently demote a host on every
/// rebuild, and a defaulted <c>--width 800</c> would overwrite a deliberate 1600.
/// </remarks>
public sealed record InstanceShape(
    string? Role,
    int? Port,
    int? GamePort,
    string? ClientId,
    string? Username,
    int? Width,
    int? Height,
    bool? ForceGameplayInput,
    bool SeedMods,
    string Desktop);

/// <summary>
/// The dedicated server half. Names match the PowerShell functions they replace.
/// </summary>
/// <remarks>
/// <b>Line prefixes are named after the verb.</b> Every line a half emits is tagged with a
/// bracketed source, and agents and humans grep those tags. In PowerShell two of them carried
/// over from the predecessor launchers and no longer matched anything a caller could type:
/// <c>update-game</c> printed <c>[Bootstrap]</c> and <c>create</c> printed <c>[Provision]</c>.
/// The tag for a verb is the verb, in the casing the surface uses:
/// <c>[UpdateGame]</c>, <c>[Create]</c>, <c>[Deploy]</c>, <c>[Start]</c>, <c>[Stop]</c>,
/// <c>[Save]</c>, <c>[Wait]</c>, <c>[Call]</c>, <c>[Send]</c>, <c>[Remove]</c>,
/// <c>[UpdateMods]</c>, <c>[Snapshot]</c>, <c>[Logs]</c>. Per-instance lines keep
/// <c>[&lt;instanceName&gt;]</c>.
/// </remarks>
public interface IServerHalf
{
    /// <summary>Was <c>Invoke-RigServerLogs</c>.</summary>
    void Logs(LogQuery query);

    /// <summary>Was <c>Invoke-RigServerUpdateGame</c>. SteamCMD app 600760, then the BepInEx mirror.</summary>
    void UpdateGame(string callerId);

    /// <summary>Was <c>Invoke-RigServerUpdateMods</c>.</summary>
    void UpdateMods(string callerId, string fromModConfig);

    /// <summary>Was <c>Invoke-RigServerDeploy</c>.</summary>
    void Deploy(string callerId, IReadOnlyList<string> mods, string configuration);

    /// <summary>Was <c>Invoke-RigServerStart</c>. Enters a world in the same call; there is no menu.</summary>
    void Start(string callerId, string load, string map, string newMap, int gamePort, int updatePort);

    /// <summary>Was <c>Invoke-RigServerStop</c>. Not lock-gated, so an orphan can always be cleaned up.</summary>
    void Stop(string callerId, string saveName, int timeoutSeconds, int waitSeconds);

    /// <summary>Was <c>Invoke-RigServerSave</c>. Returns false and warns rather than claiming success.</summary>
    bool Save(string callerId, string saveName, int waitSeconds);

    /// <summary>Was <c>Invoke-RigServerWait</c>. Only <c>inWorld</c> and <c>process</c> reach here.</summary>
    void Wait(string callerId, ReadinessStage stage, int waitSeconds);

    /// <summary>Was <c>Invoke-RigServerSend</c>. One line onto stdin through the host wrapper.</summary>
    void Send(string callerId, string command);

    /// <summary>Was <c>Invoke-RigServerHostMode</c>. The detached wrapper <c>start</c> spawns.</summary>
    void HostMode(string load, string map, string newMap, int gamePort, int updatePort);

    /// <summary>Was <c>Write-RigServerStatus</c> plus the version and mod-staleness readers.</summary>
    void WriteStatus();

    /// <summary>Was <c>Get-RigServerPaths</c> plus <c>Test-RigServerProcessAlive</c>, as one line.</summary>
    void WriteListRow();

    /// <summary>Tears the server and its wrapper down. The lock's reclaim callback, server side.</summary>
    void Teardown();
}

/// <summary>
/// The client half. Instances are named, never handed over as registry rows: resolving a
/// name to a tree, a port and a role is the half's own job, not the entry point's.
/// </summary>
public interface IClientHalf
{
    /// <summary>Was <c>Invoke-RigClientLogs</c>, once per instance.</summary>
    void Logs(IReadOnlyList<string> instances, LogQuery query);

    /// <summary>Was <c>Invoke-RigClientSnapshot</c>. Per-instance failures land in the JSON, never thrown.</summary>
    void Snapshot(IReadOnlyList<string> instances, string outFile);

    /// <summary>Was <c>Invoke-RigClientUpdateGame</c>. Re-links each tree from the source install.</summary>
    void UpdateGame(string callerId, IReadOnlyList<string> instances, string desktop);

    /// <summary>Was <c>Invoke-RigClientUpdateMods</c>.</summary>
    void UpdateMods(string callerId, IReadOnlyList<string> instances);

    /// <summary>Was <c>Invoke-RigClientDeploy</c>.</summary>
    void Deploy(string callerId, IReadOnlyList<string> instances, IReadOnlyList<string> mods, string configuration);

    /// <summary>Was <c>Invoke-RigClientCreate</c>. Exactly one instance, hard-linked from the source install.</summary>
    void Create(string callerId, string instance, bool force, InstanceShape shape);

    /// <summary>Was <c>Invoke-RigClientRemove</c>. Deletes the tree and the instance's own save root.</summary>
    void Remove(string callerId, string instance, bool force, string desktop);

    /// <summary>
    /// Was <c>Invoke-RigClientStart</c>. Boots to the menu, on the isolated desktop, never focused.
    /// </summary>
    /// <remarks>
    /// No window size: it comes from the instance's own registry entry, which is where a
    /// typed <c>--width</c> at provision time was recorded. A start that could override it
    /// would make the window size depend on which command last mentioned it (CLIENT-121).
    /// </remarks>
    void Start(string callerId, IReadOnlyList<string> instances, string desktop);

    /// <summary>Was <c>Invoke-RigClientStop</c>. Joiners first, then hosts.</summary>
    void Stop(string callerId, IReadOnlyList<string> instances, int timeoutSeconds, int waitSeconds, string saveName, bool force);

    /// <summary>Was <c>Invoke-RigClientSave</c>. Warns per instance rather than claiming success.</summary>
    void Save(string callerId, IReadOnlyList<string> instances, string saveName, int waitSeconds);

    /// <summary>Was <c>Invoke-RigClientWait</c>. Refreshes a lock the caller holds while it blocks.</summary>
    void Wait(string callerId, IReadOnlyList<string> instances, ReadinessStage stage, int waitSeconds);

    /// <summary>Was <c>Invoke-RigClientCall</c>. One HTTP request per instance, answer parsed.</summary>
    void Call(string callerId, IReadOnlyList<string> instances, string path, string body, int callTimeoutSeconds);

    /// <summary>Was <c>Write-RigClientStatus</c> plus the version and mod-staleness readers.</summary>
    void WriteStatus(IReadOnlyList<string> instances);

    /// <summary>Was <c>Get-RigClientListRows</c>.</summary>
    void WriteListRows(IReadOnlyList<string> instances);

    /// <summary>Tears down whatever a reclaimed session left running. The lock's reclaim callback.</summary>
    void StopOrphansByPid();
}

/// <summary>
/// The dedicated server half, joined to <see cref="ServerHalf"/>.
/// </summary>
/// <remarks>
/// <para>
/// Three shape differences are reconciled here and nowhere else. Core is async and this
/// dispatcher is synchronous, so every call blocks once, at the call site, which is what a
/// one-shot process wants. Core takes a <see cref="ServerStartWorld"/> rather than three
/// loose strings. Core's status readers RETURN rows rather than printing them, so the
/// rendering lives here, next to the client half's, where the two can be kept alike.
/// </para>
/// <para>
/// Nothing in this file decides anything. A rule that lives here rather than in Core is a
/// rule the suite cannot exercise without launching the binary.
/// </para>
/// </remarks>
public sealed class ServerHalfAdapter(ServerHalf half, IOutput output) : IServerHalf
{
    public void Logs(LogQuery query)
    {
        if (query.Unity)
        {
            output.Line(
                OutputLevel.Warning,
                "[Logs] --unity is a client-instance flag: an instance has a pre-BepInEx Unity log per run and "
                + "a BepInEx log, and this flag picks the first. The dedicated server writes one log and it is "
                + "already the Unity one, so the flag changes nothing here.");
        }

        half.Logs(query.Tail, NullIfEmpty(query.Grep));
    }

    public void UpdateGame(string callerId) => half.UpdateGame(NullIfEmpty(callerId));

    public void UpdateMods(string callerId, string fromModConfig) =>
        half.UpdateMods(NullIfEmpty(callerId), NullIfEmpty(fromModConfig));

    public void Deploy(string callerId, IReadOnlyList<string> mods, string configuration) =>
        half.Deploy(mods, NullIfEmpty(callerId), configuration);

    public void Start(string callerId, string load, string map, string newMap, int gamePort, int updatePort) =>
        half.StartAsync(World(load, map, newMap), NullIfEmpty(callerId), gamePort, updatePort)
            .GetAwaiter().GetResult();

    public void Stop(string callerId, string saveName, int timeoutSeconds, int waitSeconds) =>
        half.StopAsync(NullIfEmpty(callerId), NullIfEmpty(saveName), timeoutSeconds, waitSeconds)
            .GetAwaiter().GetResult();

    public bool Save(string callerId, string saveName, int waitSeconds) =>
        half.SaveAsync(saveName, NullIfEmpty(callerId), waitSeconds).GetAwaiter().GetResult();

    public void Wait(string callerId, ReadinessStage stage, int waitSeconds) =>
        half.WaitAsync(stage, NullIfEmpty(callerId), waitSeconds).GetAwaiter().GetResult();

    public void Send(string callerId, string command) =>
        half.SendAsync(command, NullIfEmpty(callerId)).GetAwaiter().GetResult();

    public void HostMode(string load, string map, string newMap, int gamePort, int updatePort) =>
        half.HostModeAsync(World(load, map, newMap), gamePort, updatePort).GetAwaiter().GetResult();

    public void WriteStatus()
    {
        half.Status();

        var version = half.VersionReport();
        output.Line(
            OutputLevel.Info,
            $"  game:         {version.Version} (source install {version.Source})"
            + (version.Stale ? "  STALE" : ""));
        if (version.Stale) output.Line(OutputLevel.Warning, $"  remedy:       {version.Remedy}");

        output.Value("serverGameVersion", version.Version);
        output.Value("serverGameStale", version.Stale);

        var stale = half.ModStaleness();
        output.Value("serverStaleMods", stale.Count);
        foreach (var row in stale)
        {
            output.Line(
                OutputLevel.Warning,
                $"  stale {row.Kind}: {row.Name} deployed {row.Deployed:u}, source {row.Source:u} "
                + $"[{row.LoadPath}]. {row.Remedy}");
        }
    }

    public void WriteListRow()
    {
        var installed = half.Paths;
        var version = half.VersionReport();

        output.Line(
            OutputLevel.Info,
            string.Format(
                CultureInfo.InvariantCulture,
                "{0,-14}{1,-12}{2,-12}{3,-10}{4}",
                "server",
                version.Present ? "installed" : "MISSING",
                half.ServerAlive ? $"pid {half.ServerPid}" : "stopped",
                half.WrapperAlive ? "wrapper" : "-",
                version.Version));

        output.Value("serverInstalled", version.Present);
        output.Value("serverRunning", half.ServerAlive);
        output.Value("serverPid", half.ServerPid);
        output.Value("serverInstall", installed.InstallDir);
    }

    public void Teardown() => half.TeardownAsync().GetAwaiter().GetResult();

    private static ServerStartWorld World(string load, string map, string newMap) =>
        new(NullIfEmpty(load), NullIfEmpty(map), NullIfEmpty(newMap));

    private static string? NullIfEmpty(string value) => string.IsNullOrEmpty(value) ? null : value;
}

/// <summary>
/// The client half, joined to <see cref="ClientHalf"/>.
/// </summary>
/// <remarks>
/// <para>
/// The one shape difference worth naming: Core takes <see cref="InstanceEntry"/> rows and the
/// CLI carries names, so the mapping happens ONCE, here, rather than at twelve call sites.
/// Resolution goes through <see cref="RigRegistry.Entries"/>, which refuses a name the
/// registry does not know and names the ones it does.
/// </para>
/// <para>
/// <c>SnapshotAsync</c> returns the JSON rather than writing it, so the out-file handling is
/// its caller's; it already resolves and writes when given one, and prints when not.
/// </para>
/// </remarks>
public sealed class ClientHalfAdapter(ClientHalf half, IOutput output) : IClientHalf
{
    public void Logs(IReadOnlyList<string> instances, LogQuery query)
    {
        foreach (var instance in instances) half.Logs(instance, query.Tail, NullIfEmpty(query.Grep), query.Unity);
    }

    public void Snapshot(IReadOnlyList<string> instances, string outFile) =>
        half.SnapshotAsync(Entries(instances), NullIfEmpty(outFile)).GetAwaiter().GetResult();

    public void UpdateGame(string callerId, IReadOnlyList<string> instances, string desktop) =>
        half.UpdateGameAsync(Entries(instances), NullIfEmpty(callerId), desktop).GetAwaiter().GetResult();

    public void UpdateMods(string callerId, IReadOnlyList<string> instances) =>
        half.UpdateMods(Entries(instances), NullIfEmpty(callerId));

    public void Deploy(string callerId, IReadOnlyList<string> instances, IReadOnlyList<string> mods, string configuration) =>
        half.Deploy(Entries(instances), mods, NullIfEmpty(callerId), configuration);

    public void Create(string callerId, string instance, bool force, InstanceShape shape) =>
        half.CreateAsync(new CreateOptions
        {
            Instance = instance,
            CallerId = NullIfEmpty(callerId),
            Force = force,
            Role = shape.Role,
            Port = shape.Port,
            GamePort = shape.GamePort,
            ClientId = shape.ClientId,
            Username = shape.Username,
            Width = shape.Width,
            Height = shape.Height,
            ForceGameplayInput = shape.ForceGameplayInput,
            SeedMods = shape.SeedMods,
            Desktop = shape.Desktop,
        }).GetAwaiter().GetResult();

    public void Remove(string callerId, string instance, bool force, string desktop) =>
        half.RemoveAsync(instance, NullIfEmpty(callerId), force, desktop).GetAwaiter().GetResult();

    public void Start(string callerId, IReadOnlyList<string> instances, string desktop) =>
        half.StartAsync(Entries(instances), NullIfEmpty(callerId), desktop).GetAwaiter().GetResult();

    public void Stop(string callerId, IReadOnlyList<string> instances, int timeoutSeconds, int waitSeconds, string saveName, bool force) =>
        half.StopAsync(Entries(instances), NullIfEmpty(callerId), timeoutSeconds, waitSeconds, NullIfEmpty(saveName), force)
            .GetAwaiter().GetResult();

    public void Save(string callerId, IReadOnlyList<string> instances, string saveName, int waitSeconds) =>
        half.SaveAsync(Entries(instances), NullIfEmpty(callerId), NullIfEmpty(saveName), waitSeconds)
            .GetAwaiter().GetResult();

    public void Wait(string callerId, IReadOnlyList<string> instances, ReadinessStage stage, int waitSeconds) =>
        half.WaitAsync(Entries(instances), NullIfEmpty(callerId), stage, waitSeconds).GetAwaiter().GetResult();

    public void Call(string callerId, IReadOnlyList<string> instances, string path, string body, int callTimeoutSeconds) =>
        half.CallAsync(Entries(instances), path, NullIfEmpty(body), NullIfEmpty(callerId), callTimeoutSeconds)
            .GetAwaiter().GetResult();

    public void WriteStatus(IReadOnlyList<string> instances)
    {
        var entries = Entries(instances);
        half.StatusAsync(entries).GetAwaiter().GetResult();

        var versions = half.VersionReport(entries);
        var staleVersions = 0;
        foreach (var row in versions)
        {
            if (!row.Stale) continue;
            staleVersions++;
            output.Line(
                OutputLevel.Warning,
                $"  {row.Name}: built from game {row.Version}, the install now carries {row.Source}. {row.Remedy}");
        }

        output.Value("clientGameStale", staleVersions);

        var stale = half.ModStaleness(entries);
        output.Value("clientStaleMods", stale.Count);
        foreach (var row in stale)
        {
            output.Line(
                OutputLevel.Warning,
                $"  {row.Instance}: stale {row.Kind} {row.Name}, deployed {row.Deployed:u}, source "
                + $"{row.Source:u}. {row.Remedy}");
        }
    }

    public void WriteListRows(IReadOnlyList<string> instances)
    {
        var rows = half.ListAsync(Entries(instances)).GetAwaiter().GetResult();
        if (rows.Count == 0)
        {
            output.Line(OutputLevel.Info, "clients: none provisioned.");
            return;
        }

        output.Line(
            OutputLevel.Info,
            string.Format(
                CultureInfo.InvariantCulture,
                "{0,-14}{1,-8}{2,-14}{3,-8}{4,-8}{5,-8}{6}",
                "instance", "role", "live", "hosting", "port", "game", "clientId"));

        foreach (var row in rows)
        {
            output.Line(
                OutputLevel.Info,
                string.Format(
                    CultureInfo.InvariantCulture,
                    "{0,-14}{1,-8}{2,-14}{3,-8}{4,-8}{5,-8}{6}",
                    row.InstanceName, row.Role, row.LiveRole, row.Hosting, row.Port, row.GamePort, row.ClientId));
        }

        output.Value("instanceCount", rows.Count);
    }

    public void StopOrphansByPid() => half.ReclaimAsync().GetAwaiter().GetResult();

    private IReadOnlyList<InstanceEntry> Entries(IReadOnlyList<string> names) => half.Registry.Entries(names);

    private static string? NullIfEmpty(string value) => string.IsNullOrEmpty(value) ? null : value;
}
