using System.Text.Json;
using TestRig.Core.Abstractions;

namespace TestRig.Core.Session;

/// <summary>What kind of thing a surface record is.</summary>
public enum SurfaceClass
{
    /// <summary>A settings file. Captured by content, restored byte for byte.</summary>
    Config,

    /// <summary>A deployed plugin or seeded mod file. Hashed for staleness, never restored.</summary>
    Payload,

    /// <summary>A world directory. Recorded informationally; the session marker decides its fate.</summary>
    World,
}

/// <summary>One entry of the rig's mutable surface.</summary>
public sealed record SurfaceRecord(string Key, string Path, SurfaceClass Class, string Half, string? Instance);

/// <summary>Where an instance's hard-linked tree is, and where that answer came from.</summary>
/// <remarks>
/// The source travels with the path so a "no tree" report can say whether the reset
/// looked where the registry pointed or where the library defaults to, which is the
/// difference between a genuinely unprovisioned instance and one the reset simply could
/// not find. Trees normally live on the game install's volume rather than inside the rig.
/// </remarks>
public sealed record InstanceTree(string Path, string Source);

/// <summary>
/// The allow-list of everything the baseline has an opinion about.
/// </summary>
/// <remarks>
/// One definition, used by the capture, the staleness check and the restore, so the three
/// cannot disagree. Deliberately an allow-list: logs, caches, InspectorPlus drop files,
/// pid files, imgui.ini, setting.xml, the client save roots and the ~1,050 hard links per
/// instance are all excluded, so anything an agent deliberately places anywhere else in
/// an instance tree survives every restore untouched.
///
/// The world records come from the one world scanner rather than a second enumeration,
/// because the session marker records the same set with the same keys and a world is
/// deleted on the two agreeing. A second copy of the key shape would be the copy that
/// drifts, and what it would drift into is a delete decision about somebody's world.
/// </remarks>
public sealed class MutableSurface
{
    private readonly IFileSystem _fs;
    private readonly RigPaths _paths;
    private readonly WorldScanner _worlds;

    public MutableSurface(IFileSystem fs, RigPaths paths, WorldScanner worlds)
    {
        _fs = fs;
        _paths = paths;
        _worlds = worlds;
    }

    /// <summary>Provisioned instance names, sorted.</summary>
    public IReadOnlyList<string> InstanceNames()
    {
        if (!_fs.DirectoryExists(_paths.ClientDataDir)) return [];
        try
        {
            return
            [
                .. _fs.EnumerateDirectories(_paths.ClientDataDir)
                    .Select(static d => Path.GetFileName(d.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)))
                    .Where(static n => !string.IsNullOrEmpty(n))
                    .OrderBy(static n => n, StringComparer.OrdinalIgnoreCase)
            ];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    /// <summary>
    /// instanceName to the instances root recorded when it was provisioned.
    /// </summary>
    /// <remarks>
    /// A missing, unreadable or half-written registry yields an empty map and every
    /// instance falls back to the configured root, which is the behaviour before the field
    /// existed. Guessing the root instead is what once made the reset report "no instance
    /// tree" and silently skip the config re-copy and the SavePathOverride re-apply, which
    /// is half of what the reset is for.
    /// </remarks>
    public IReadOnlyDictionary<string, string> InstanceRootMap()
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (!_fs.FileExists(_paths.ClientRegistryFile)) return map;

        try
        {
            using var doc = JsonDocument.Parse(_fs.ReadAllText(_paths.ClientRegistryFile));
            if (doc.RootElement.ValueKind != JsonValueKind.Array) return map;

            foreach (var entry in doc.RootElement.EnumerateArray())
            {
                if (entry.ValueKind != JsonValueKind.Object) continue;
                if (!entry.TryGetProperty("instanceName", out var name) || name.ValueKind != JsonValueKind.String) continue;
                if (!entry.TryGetProperty("instancesRoot", out var root) || root.ValueKind != JsonValueKind.String) continue;

                var n = name.GetString();
                var r = root.GetString();
                if (string.IsNullOrEmpty(n) || string.IsNullOrEmpty(r)) continue;
                map[n] = r;
            }
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }

        return map;
    }

    public InstanceTree TreeFor(string instance, IReadOnlyDictionary<string, string> rootMap)
    {
        if (rootMap.TryGetValue(instance, out var root))
        {
            return new InstanceTree(Path.Combine(root, instance), "the instances root recorded in rig.json");
        }
        return new InstanceTree(
            _paths.DefaultInstanceTree(instance),
            "the configured instances root (this entry records none; a rebuild with testrig create --force records it)");
    }

    /// <summary>The instance's declared role, or 'unknown' when the manifest is missing or broken.</summary>
    public string RoleOf(string instance)
    {
        var manifest = _paths.InstanceManifest(instance);
        if (!_fs.FileExists(manifest)) return "unknown";
        try
        {
            using var doc = JsonDocument.Parse(_fs.ReadAllText(manifest));
            if (doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.TryGetProperty("role", out var role)
                && role.ValueKind == JsonValueKind.String)
            {
                var value = role.GetString();
                if (!string.IsNullOrEmpty(value)) return value;
            }
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return "unknown";
        }
        return "unknown";
    }

    /// <summary>Every record the baseline has an opinion about.</summary>
    public IReadOnlyList<SurfaceRecord> Enumerate()
    {
        var records = new List<SurfaceRecord>();
        var rootMap = InstanceRootMap();

        foreach (var name in InstanceNames())
        {
            var data = _paths.InstanceDataDir(name);
            var tree = TreeFor(name, rootMap).Path;
            var bep = Path.Combine(tree, "BepInEx");

            foreach (var cfg in Files(Path.Combine(bep, "config"), "*.cfg", recurse: false))
            {
                records.Add(new SurfaceRecord(
                    $"client/{name}/bepinex-config/{Path.GetFileName(cfg)}", cfg, SurfaceClass.Config, "client", name));
            }

            var modconfig = Path.Combine(data, "userdata", "modconfig.xml");
            if (_fs.FileExists(modconfig))
            {
                records.Add(new SurfaceRecord($"client/{name}/modconfig.xml", modconfig, SurfaceClass.Config, "client", name));
            }

            AddTree(records, Path.Combine(bep, "plugins"), $"client/{name}/plugins/", SurfaceClass.Payload, "client", name);
            AddTree(records, Path.Combine(data, "userdata", "mods"), $"client/{name}/mods/", SurfaceClass.Payload, "client", name);
        }

        var install = _paths.DediInstall;
        foreach (var cfg in Files(Path.Combine(install, "BepInEx", "config"), "*.cfg", recurse: false))
        {
            records.Add(new SurfaceRecord(
                $"server/bepinex-config/{Path.GetFileName(cfg)}", cfg, SurfaceClass.Config, "server", null));
        }

        var serverModconfig = Path.Combine(install, "modconfig.xml");
        if (_fs.FileExists(serverModconfig))
        {
            records.Add(new SurfaceRecord("server/modconfig.xml", serverModconfig, SurfaceClass.Config, "server", null));
        }

        AddTree(records, Path.Combine(install, "BepInEx", "plugins"), "server/plugins/", SurfaceClass.Payload, "server", null);

        var worlds = _worlds.ScanServer();
        foreach (var world in worlds.Worlds)
        {
            records.Add(new SurfaceRecord(world.Key, world.Path, SurfaceClass.World, "server", null));
        }

        return records;
    }

    private void AddTree(List<SurfaceRecord> into, string root, string keyPrefix, SurfaceClass cls, string half, string? instance)
    {
        if (!_fs.DirectoryExists(root)) return;
        var rootLen = root.Length;
        foreach (var file in Files(root, "*", recurse: true))
        {
            if (file.Length <= rootLen) continue;
            // Forward slashes, so a baseline keeps matching after the instances root moves.
            var rel = file[rootLen..].TrimStart('\\', '/').Replace('\\', '/');
            into.Add(new SurfaceRecord(keyPrefix + rel, file, cls, half, instance));
        }
    }

    private IReadOnlyList<string> Files(string dir, string pattern, bool recurse)
    {
        if (!_fs.DirectoryExists(dir)) return [];
        try
        {
            return [.. _fs.EnumerateFiles(dir, pattern, recurse).OrderBy(static f => f, StringComparer.OrdinalIgnoreCase)];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }
}
