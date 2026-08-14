using TestRig.Core.Abstractions;

namespace TestRig.Core.Session;

/// <summary>Which half a world belongs to.</summary>
public enum WorldScope
{
    /// <summary><c>DedicatedServer/data/saves/</c>.</summary>
    Server,

    /// <summary><c>ClientRig/data/&lt;instance&gt;/userdata/saves/</c>. A listen host writes real worlds here.</summary>
    Client,
}

/// <summary>How an enumeration of a world root turned out.</summary>
/// <remarks>
/// This tri-state is the fix for the highest-severity defect in the subsystem
/// (spec 03-reset H.4). In PowerShell, <c>Get-RigServerWorlds</c> enumerated with
/// <c>-ErrorAction SilentlyContinue</c>, which swallows a missing path, an access
/// denial, a transient sharing violation and a momentarily wrong path alike, and
/// returns an empty list. <c>Write-RigDirtyMarker</c> joined that to the empty string
/// and wrote <c>worlds=</c>. The snapshot reader tests the KEY's presence, not its
/// value, so it answered <c>Recorded=true, Degraded=false</c>, and the reset planner's
/// predicate was then true for EVERY world: 25 DeleteTree actions over 185 MB of real
/// worlds on this machine, irreversible, with no warning at all, because
/// <c>WorldsNotTracked</c> only fires when <c>Recorded</c> is false.
///
/// A failure must be representable and must never serialise as an empty set.
/// </remarks>
public enum WorldScanStatus
{
    /// <summary>
    /// The root was enumerated. The result may legitimately be empty, and an empty
    /// result IS a real answer meaning "the rig had no worlds"; that semantics is
    /// deliberate and tested, and stays.
    /// </summary>
    Enumerated,

    /// <summary>
    /// The enumeration failed, or produced a name that cannot be recorded without
    /// corrupting the marker. Never serialised as an empty set; the key is omitted so
    /// the reader lands in the degraded path that keeps every world.
    /// </summary>
    Failed,

    /// <summary>No enumeration was attempted: nothing named a root to look in.</summary>
    NotAttempted,
}

/// <summary>One world directory.</summary>
/// <param name="Name">The directory name, exactly as it is on disk. Never trimmed.</param>
/// <param name="Key">Rig-relative key, so a baseline or marker keeps matching if the rig moves.</param>
/// <param name="Path">Absolute path.</param>
/// <param name="Instance">The client instance that owns it, or null for a server world.</param>
public sealed record RigWorld(string Name, string Key, string Path, string? Instance);

/// <summary>The outcome of scanning one or more world roots.</summary>
public sealed record WorldScan(
    WorldScanStatus Status,
    IReadOnlyList<RigWorld> Worlds,
    string? FailureDetail)
{
    public bool IsUsable => Status == WorldScanStatus.Enumerated;

    public static WorldScan Ok(IReadOnlyList<RigWorld> worlds) =>
        new(WorldScanStatus.Enumerated, worlds, null);

    public static WorldScan Failed(string detail) =>
        new(WorldScanStatus.Failed, [], detail);

    public static WorldScan NotAttempted(string detail) =>
        new(WorldScanStatus.NotAttempted, [], detail);
}

/// <summary>Key shapes and the one representability rule the marker format imposes.</summary>
public static class WorldKey
{
    public static string ForServer(string name) => "server/saves/" + name;

    public static string ForClient(string instance, string name) => $"client/{instance}/saves/{name}";

    /// <summary>
    /// Whether a world name survives being written into the marker and read back.
    /// </summary>
    /// <remarks>
    /// The marker has no escaping. '|' is the list separator and is illegal in a Windows
    /// directory name anyway, so it can only arrive from a corrupted source. Leading and
    /// trailing whitespace is the real hazard: a directory named " Luna" is legal on
    /// NTFS (only trailing spaces and dots are normalised away by Win32), and the
    /// PowerShell reader trimmed it on the way back, so the key matched nothing and the
    /// world was deleted (spec 03-reset H.5 item 4).
    ///
    /// Rather than trim asymmetrically or bolt an escaping scheme onto a format that has
    /// none, an unrepresentable name makes the whole scan Failed, which omits the key
    /// and keeps every world. One exotic directory name costs a session its world
    /// scoping, loudly, instead of costing somebody a world, silently.
    /// </remarks>
    public static bool IsRoundTrippable(string name)
    {
        if (string.IsNullOrEmpty(name)) return false;
        if (!string.Equals(name, name.Trim(), StringComparison.Ordinal)) return false;
        return name.IndexOfAny(['|', '\r', '\n', '#']) < 0;
    }
}

/// <summary>Enumerates world roots for both halves.</summary>
public sealed class WorldScanner
{
    private readonly IFileSystem _fs;
    private readonly RigPaths _paths;

    public WorldScanner(IFileSystem fs, RigPaths paths)
    {
        _fs = fs;
        _paths = paths;
    }

    /// <summary>Every world directly under the dedicated server's save root.</summary>
    public WorldScan ScanServer()
    {
        var root = _paths.ServerSaveRoot;
        if (string.IsNullOrWhiteSpace(root))
        {
            return WorldScan.NotAttempted("no dedicated-server save root is configured");
        }

        return ScanRoot(root, name => new RigWorld(name, WorldKey.ForServer(name), Path.Combine(root, name), null));
    }

    /// <summary>
    /// Every world under every provisioned client instance's own save root.
    /// </summary>
    /// <remarks>
    /// A failure on ANY instance fails the whole client scan. Partial knowledge of which
    /// client worlds predate a session is worse than none: the missing instance's worlds
    /// would be absent from the recorded set and therefore deleted.
    /// </remarks>
    public WorldScan ScanClients()
    {
        var dataDir = _paths.ClientDataDir;
        if (string.IsNullOrWhiteSpace(dataDir)) return WorldScan.NotAttempted("no client data directory is configured");
        if (!_fs.DirectoryExists(dataDir)) return WorldScan.Ok([]);

        IReadOnlyList<string> instanceDirs;
        try
        {
            instanceDirs = _fs.EnumerateDirectories(dataDir);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return WorldScan.Failed($"the client instance list under {dataDir} could not be enumerated: {ex.Message}");
        }

        var all = new List<RigWorld>();
        foreach (var dir in instanceDirs.OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase))
        {
            var instance = Path.GetFileName(dir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            if (string.IsNullOrEmpty(instance)) continue;

            var saveRoot = _paths.InstanceSaveRoot(instance);
            var scan = ScanRoot(
                saveRoot,
                name => new RigWorld(name, WorldKey.ForClient(instance, name), Path.Combine(saveRoot, name), instance));

            if (scan.Status != WorldScanStatus.Enumerated) return scan;
            all.AddRange(scan.Worlds);
        }

        return WorldScan.Ok(all);
    }

    /// <summary>Worlds under one client instance's save root.</summary>
    public WorldScan ScanInstance(string instance)
    {
        var saveRoot = _paths.InstanceSaveRoot(instance);
        return ScanRoot(
            saveRoot,
            name => new RigWorld(name, WorldKey.ForClient(instance, name), Path.Combine(saveRoot, name), instance));
    }

    private WorldScan ScanRoot(string root, Func<string, RigWorld> make)
    {
        // A missing save root is a real, empty answer, not a failure: there is nothing to
        // delete, and any world that appears in it later was created after this moment.
        if (!_fs.DirectoryExists(root)) return WorldScan.Ok([]);

        IReadOnlyList<string> dirs;
        try
        {
            dirs = _fs.EnumerateDirectories(root);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return WorldScan.Failed($"the world root {root} exists but could not be enumerated: {ex.Message}");
        }

        var worlds = new List<RigWorld>(dirs.Count);
        foreach (var dir in dirs)
        {
            var name = Path.GetFileName(dir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            if (string.IsNullOrEmpty(name)) continue;

            if (!WorldKey.IsRoundTrippable(name))
            {
                return WorldScan.Failed(
                    $"the world directory name '{name}' under {root} cannot be recorded in the session marker "
                    + "without corrupting it, so which worlds predate this session cannot be established");
            }

            worlds.Add(make(name));
        }

        worlds.Sort(static (a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
        return WorldScan.Ok(worlds);
    }
}
