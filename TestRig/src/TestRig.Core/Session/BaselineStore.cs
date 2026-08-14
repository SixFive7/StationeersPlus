using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using TestRig.Core.Abstractions;

namespace TestRig.Core.Session;

/// <summary>One captured file or world.</summary>
public sealed record BaselineEntry(string Key, SurfaceClass Class, string Half, string Instance, long Bytes, string Sha256);

/// <summary>A captured definition of a clean rig.</summary>
public sealed record Baseline(
    string CapturedUtc,
    string CapturedBy,
    string GameVersion,
    string SourceInstall,
    string Host,
    IReadOnlyList<string> Instances,
    IReadOnlyDictionary<string, BaselineEntry> Files);

/// <summary>Result of a capture.</summary>
public sealed record BaselineCapture(bool WhatIf, int Entries, int Stored);

/// <summary>
/// The baseline: what the rig's config files should contain, declared by capturing them.
/// </summary>
/// <remarks>
/// A capture never protects a world and never did reliably. Staleness inspects the game
/// version, the instance-name set and files of class payload; class world is never
/// examined, so a world staged deliberately (copying a tier-2 source over tier 3, which
/// is exactly what the repository's save rules prescribe) left the baseline reading FRESH
/// while the staged world was absent from it, and the next session boundary deleted the
/// very thing the test was about. World fate belongs to the session marker alone; the
/// world entries here are informational and nothing reads them back.
/// </remarks>
public sealed class BaselineStore
{
    private readonly IFileSystem _fs;
    private readonly IClock _clock;
    private readonly RigPaths _paths;
    private readonly MutableSurface _surface;
    private readonly IOutput _output;
    private readonly LauncherIdentity _launcher;

    public BaselineStore(
        IFileSystem fs,
        IClock clock,
        RigPaths paths,
        MutableSurface surface,
        IOutput output,
        LauncherIdentity launcher)
    {
        _fs = fs;
        _clock = clock;
        _paths = paths;
        _surface = surface;
        _output = output;
        _launcher = launcher;
    }

    /// <summary>
    /// Where a captured config file's content lives.
    /// </summary>
    /// <remarks>
    /// Flat, not nested: a key contains '/' separators and a depth that do not survive
    /// being pasted into a directory tree. The hash is over the lowercased key, so the
    /// name is stable, and the leaf is appended so a human can see what a file is.
    /// </remarks>
    public string StoredPath(string key)
    {
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(key.ToLowerInvariant()));
        var hex = Convert.ToHexString(digest.AsSpan(0, 8));
        var leaf = key.Split('/', '\\')[^1];
        return Path.Combine(_paths.BaselineStore, $"{hex}-{leaf}");
    }

    /// <summary>The captured baseline, or null when there is none or it will not parse.</summary>
    public Baseline? Read()
    {
        if (!_fs.FileExists(_paths.BaselineManifest)) return null;

        try
        {
            using var doc = JsonDocument.Parse(_fs.ReadAllText(_paths.BaselineManifest));
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;

            var files = new Dictionary<string, BaselineEntry>(StringComparer.OrdinalIgnoreCase);
            if (root.TryGetProperty("files", out var fileArray) && fileArray.ValueKind == JsonValueKind.Array)
            {
                foreach (var element in fileArray.EnumerateArray())
                {
                    if (element.ValueKind != JsonValueKind.Object) continue;
                    if (!element.TryGetProperty("key", out var keyProp) || keyProp.ValueKind != JsonValueKind.String) continue;
                    var key = keyProp.GetString();
                    if (string.IsNullOrEmpty(key)) continue;

                    files[key] = new BaselineEntry(
                        key,
                        ParseClass(Str(element, "class")),
                        Str(element, "half"),
                        Str(element, "instance"),
                        element.TryGetProperty("bytes", out var b) && b.TryGetInt64(out var bytes) ? bytes : 0,
                        Str(element, "sha256"));
                }
            }

            var instances = new List<string>();
            if (root.TryGetProperty("instances", out var instArray) && instArray.ValueKind == JsonValueKind.Array)
            {
                foreach (var element in instArray.EnumerateArray())
                {
                    if (element.ValueKind == JsonValueKind.String)
                    {
                        var value = element.GetString();
                        if (!string.IsNullOrEmpty(value)) instances.Add(value);
                    }
                }
            }

            return new Baseline(
                Str(root, "capturedUtc"),
                Str(root, "capturedBy"),
                Str(root, "gameVersion"),
                Str(root, "sourceInstall"),
                Str(root, "host"),
                instances,
                files);
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static string Str(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    private static SurfaceClass ParseClass(string value) => value switch
    {
        "world" => SurfaceClass.World,
        "payload" => SurfaceClass.Payload,
        _ => SurfaceClass.Config,
    };

    private static string ClassName(SurfaceClass value) => value switch
    {
        SurfaceClass.World => "world",
        SurfaceClass.Payload => "payload",
        _ => "config",
    };

    /// <summary>
    /// The game version, the staleness anchor.
    /// </summary>
    /// <remarks>
    /// Reads <c>StreamingAssets/version.ini</c> under each candidate data directory, first
    /// hit wins. This used to read a <c>version.txt</c> at the install root; no such file
    /// has ever existed, so it returned 'unknown' on every real install, and staleness
    /// skips its comparison on 'unknown', which meant a game update could never mark a
    /// baseline stale, the one thing the anchor exists for. The test fixture wrote a
    /// matching bogus <c>version.txt</c>, so the assertions confirmed the broken reader.
    /// </remarks>
    public string GameVersion(string? sourceInstall = null)
    {
        var install = sourceInstall ?? _paths.SourceInstall;
        if (string.IsNullOrEmpty(install)) return "unknown";

        foreach (var dataDir in new[] { "rocketstation_Data", "rocketstation_DedicatedServer_Data" })
        {
            var file = Path.Combine(install, dataDir, "StreamingAssets", "version.ini");
            if (!_fs.FileExists(file)) continue;

            string first;
            try
            {
                var lines = _fs.ReadLines(file);
                if (lines.Count == 0) continue;
                first = lines[0];
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            var match = System.Text.RegularExpressions.Regex.Match(first, @"(\d+(?:\.\d+)+)");
            if (match.Success) return match.Value;

            var stripped = System.Text.RegularExpressions.Regex.Replace(first, @"^\s*UPDATEVERSION\s*=\s*", "").Trim();
            if (!string.IsNullOrEmpty(stripped)) return stripped;
        }

        return "unknown";
    }

    /// <summary>SHA-256 of a file's contents, or the empty string when it cannot be read.</summary>
    public string HashFile(string path)
    {
        try
        {
            return Convert.ToHexStringLower(SHA256.HashData(_fs.ReadAllBytes(path)));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return string.Empty;
        }
    }

    /// <summary>Whether the baseline still describes this rig.</summary>
    public BaselineStaleness CheckStale(Baseline? baseline, IReadOnlyList<SurfaceRecord>? surface = null)
    {
        if (baseline is null)
        {
            return new BaselineStaleness(false, true, ["no baseline has ever been captured"]);
        }

        var reasons = new List<string>();
        var live = surface ?? _surface.Enumerate();

        var current = GameVersion();
        if (!string.IsNullOrEmpty(baseline.GameVersion)
            && current != "unknown"
            && !string.Equals(baseline.GameVersion, current, StringComparison.Ordinal))
        {
            reasons.Add($"the game moved from {baseline.GameVersion} to {current} since the baseline was captured");
        }

        var liveInstances = _surface.InstanceNames();
        foreach (var name in liveInstances)
        {
            if (!baseline.Instances.Contains(name, StringComparer.OrdinalIgnoreCase))
            {
                reasons.Add($"instance '{name}' exists now and was not in the baseline");
            }
        }
        foreach (var name in baseline.Instances)
        {
            if (!liveInstances.Contains(name, StringComparer.OrdinalIgnoreCase))
            {
                reasons.Add($"instance '{name}' was in the baseline and is gone now");
            }
        }

        var drifted = 0;
        foreach (var record in live.Where(static r => r.Class == SurfaceClass.Payload))
        {
            if (!baseline.Files.TryGetValue(record.Key, out var entry)) { drifted++; continue; }
            if (!string.Equals(entry.Sha256, HashFile(record.Path), StringComparison.OrdinalIgnoreCase)) drifted++;
        }
        if (drifted > 0)
        {
            reasons.Add($"{drifted} deployed plugin or seeded mod file(s) differ from the baseline "
                        + "(a rebuild or a re-seed since it was captured)");
        }

        return new BaselineStaleness(true, reasons.Count > 0, reasons);
    }

    /// <summary>
    /// Declares the rig as it stands to be the definition of clean.
    /// </summary>
    /// <remarks>
    /// Capture is not purely additive. Once a baseline exists, the config branch of the
    /// plan flips from "report only" to "restore, and delete any .cfg not in the
    /// manifest". Capture on a rig whose configs are in the state you want, not
    /// mid-experiment.
    /// </remarks>
    public BaselineCapture Capture(ResetGate gate, string capturedBy, bool force = false, bool whatIf = false)
    {
        if (!gate.Allowed && !force)
        {
            throw new RigRefusalException(
                RigRefusalKind.RigBusy,
                $"Refusing to capture a baseline while the rig is in use ({gate.Reason}). A config file the game is "
                + "holding open, or a world mid-save, is not a definition of 'clean'. Stop what is running, then "
                + "capture.");
        }
        if (!gate.Allowed)
        {
            _output.Line(OutputLevel.Warning,
                $"[Baseline] --force: capturing while the rig is in use ({gate.Reason}). Whatever those processes "
                + "have half-written is about to become the definition of a clean rig.");
        }

        var surface = _surface.Enumerate();
        var entries = new List<BaselineEntry>(surface.Count);
        var stored = 0;

        foreach (var record in surface)
        {
            if (record.Class == SurfaceClass.World)
            {
                // Metadata only: no world is hashed, copied, moved, renamed or deleted by a
                // capture. FileInfo.Length walks; no file contents are opened.
                entries.Add(new BaselineEntry(record.Key, record.Class, record.Half, record.Instance ?? "",
                    RigFiles.DirectoryBytes(_fs, record.Path), string.Empty));
                continue;
            }

            long bytes = 0;
            try { bytes = _fs.GetFileLength(record.Path); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }

            var hash = HashFile(record.Path);
            entries.Add(new BaselineEntry(record.Key, record.Class, record.Half, record.Instance ?? "", bytes, hash));

            if (record.Class != SurfaceClass.Config) continue;

            stored++;
            if (whatIf) continue;

            _fs.CreateDirectory(_paths.BaselineStore);
            _fs.CopyFile(record.Path, StoredPath(record.Key), overwrite: true);
        }

        if (whatIf)
        {
            _output.Line(OutputLevel.Info,
                $"[Baseline] --what-if: nothing was written. A capture would record {entries.Count} entries "
                + $"({stored} config file(s) stored by content).");
            return new BaselineCapture(true, entries.Count, stored);
        }

        // The only delete a capture performs, and it cannot reach outside the store:
        // non-recursive, files only, confined to baseline/content/.
        var keep = new HashSet<string>(
            surface.Where(static r => r.Class == SurfaceClass.Config).Select(r => StoredPath(r.Key)),
            StringComparer.OrdinalIgnoreCase);

        if (_fs.DirectoryExists(_paths.BaselineStore))
        {
            foreach (var file in _fs.EnumerateFiles(_paths.BaselineStore, "*", recurse: false))
            {
                if (keep.Contains(file)) continue;
                try { _fs.DeleteFile(file); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            }
        }

        _fs.CreateDirectory(_paths.BaselineDir);
        _fs.WriteAllText(_paths.BaselineManifest, RenderManifest(capturedBy, entries));

        var instances = _surface.InstanceNames();
        _output.Line(OutputLevel.Info, $"[Baseline] file      : {_paths.BaselineManifest}");
        _output.Line(OutputLevel.Info, $"[Baseline] game      : {GameVersion()}");
        _output.Line(OutputLevel.Info, $"[Baseline] instances : {(instances.Count == 0 ? "(none)" : string.Join(", ", instances))}");
        _output.Line(OutputLevel.Info,
            $"[Baseline] recorded  : {entries.Count(static e => e.Class == SurfaceClass.Config)} config, "
            + $"{entries.Count(static e => e.Class == SurfaceClass.Payload)} payload, "
            + $"{entries.Count(static e => e.Class == SurfaceClass.World)} world");
        _output.Line(OutputLevel.Info, $"[Baseline] stored    : {stored} config file(s) copied by content");
        _output.Line(OutputLevel.Info, "[Baseline] plugins and seeded mods are recorded for staleness only; they are never restored.");
        _output.Line(OutputLevel.Info, "[Baseline] worlds are informational: a capture does not protect a world and never did reliably. session.dirty decides them.");
        _output.Line(OutputLevel.Info, "[Baseline] re-capture after a game update, a mod rebuild or a re-provision.");

        return new BaselineCapture(false, entries.Count, stored);
    }

    private string RenderManifest(string capturedBy, IReadOnlyList<BaselineEntry> entries)
    {
        var buffer = new System.IO.MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = true }))
        {
            writer.WriteStartObject();
            writer.WriteString("capturedUtc", RigTime.Stamp(_clock.UtcNow));
            writer.WriteString("capturedBy", capturedBy);
            writer.WriteString("gameVersion", GameVersion());
            writer.WriteString("sourceInstall", _paths.SourceInstall ?? "");
            writer.WriteString("host", _launcher.HostName);

            writer.WriteStartArray("instances");
            foreach (var name in _surface.InstanceNames()) writer.WriteStringValue(name);
            writer.WriteEndArray();

            writer.WriteStartArray("files");
            foreach (var entry in entries)
            {
                writer.WriteStartObject();
                writer.WriteString("key", entry.Key);
                writer.WriteString("class", ClassName(entry.Class));
                writer.WriteString("half", entry.Half);
                writer.WriteString("instance", entry.Instance);
                writer.WriteNumber("bytes", entry.Bytes);
                writer.WriteString("sha256", entry.Sha256);
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            writer.WriteEndObject();
        }
        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    /// <summary>
    /// Per-file restore and delete actions for one config scope.
    /// </summary>
    /// <remarks>
    /// The target path is derived from the KEY, not from the live file, so a config a
    /// session deleted is restored rather than staying missing. A baseline entry whose
    /// stored bytes are missing is skipped, because pretending otherwise would overwrite a
    /// real config with nothing. Only files that actually differ are planned, so a clean
    /// rig plans nothing.
    /// </remarks>
    public IReadOnlyList<ResetAction> ConfigActions(
        Baseline baseline,
        string prefix,
        string targetDir,
        string half,
        string? instance,
        IReadOnlyList<SurfaceRecord> liveRecords)
    {
        var actions = new List<ResetAction>();

        // StartsWith, not a wildcard match: PowerShell's -like treats '[' and ']' as
        // metacharacters, so an instance name containing brackets broke the prefix filter.
        var keys = baseline.Files.Values
            .Where(e => e.Class == SurfaceClass.Config && e.Key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            .OrderBy(static e => e.Key, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        // Filtered by class as well as by key. PowerShell filtered the modconfig scopes by
        // key alone, which was safe only because those keys happen to be unique to config
        // records.
        var live = liveRecords.Where(static r => r.Class == SurfaceClass.Config).ToArray();

        foreach (var entry in keys)
        {
            var storedPath = StoredPath(entry.Key);
            if (!_fs.FileExists(storedPath)) continue;

            var leaf = entry.Key.Split('/', '\\')[^1];
            var target = Path.Combine(targetDir, leaf);
            var liveRecord = live.FirstOrDefault(r => string.Equals(r.Key, entry.Key, StringComparison.OrdinalIgnoreCase));

            if (liveRecord is not null && string.Equals(HashFile(liveRecord.Path), entry.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            actions.Add(new ResetAction(
                half, instance, ResetActionKind.RestoreBaselineFile, target,
                Label: $"{leaf} restored from the baseline",
                Reason: liveRecord is not null
                    ? "its contents moved since the baseline was captured"
                    : "it was deleted since the baseline was captured",
                Source: storedPath));
        }

        foreach (var record in live)
        {
            if (baseline.Files.ContainsKey(record.Key)) continue;
            actions.Add(new ResetAction(
                half, instance, ResetActionKind.DeleteFile, record.Path,
                Label: $"{Path.GetFileName(record.Path)} removed",
                Reason: "a config file created after the baseline was captured is this session's garbage by the "
                        + "same argument as a value it flipped"));
        }

        return actions;
    }
}
