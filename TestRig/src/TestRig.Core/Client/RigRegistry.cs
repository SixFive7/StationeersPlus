using System.Text.Json;
using TestRig.Core.Abstractions;
using TestRig.Core.Session;

namespace TestRig.Core.Client;

/// <summary>
/// <c>ClientRig/data/rig.json</c>: one file listing every provisioned instance.
/// </summary>
/// <remarks>
/// It is what makes a rig-wide target mean anything, and it is where each instance's
/// manifest gets its <c>peerPorts</c> list, which is what lets an instance notice a sibling
/// claiming the same ClientId.
///
/// The read-modify-write of a create runs inside a named mutex (CLIENT-045 fixed). The
/// PowerShell's comment claimed the session lock covered it, but a lock assertion is a
/// point-in-time check: two concurrent creates from the SAME session both passed it, both
/// picked the same free index, and the second write won, producing exactly the duplicate
/// ClientId the comment said it prevented. The server keys a player's body on that id, so a
/// test that believes it has two players actually has one and the results look plausible.
///
/// A SEPARATE mutex from the session lock's, deliberately. Re-entering the session mutex
/// from inside a gated command would be a deadlock in the real implementation and an
/// exception in the fake, and neither is a useful way to discover the nesting.
/// </remarks>
public sealed class RigRegistry
{
    /// <summary>The mutex name serialising registry writes across processes.</summary>
    public const string MutexName = "StationeersPlus.TestRig.Registry";

    /// <summary>How long a registry write waits for the section before giving up.</summary>
    public static readonly TimeSpan MutexTimeout = TimeSpan.FromSeconds(15);

    private readonly IFileSystem _fs;
    private readonly IOutput _output;
    private readonly RigPaths _paths;
    private readonly ICrossProcessLock? _mutex;

    public RigRegistry(IFileSystem fs, IOutput output, RigPaths paths, ICrossProcessLock? mutex = null)
    {
        _fs = fs;
        _output = output;
        _paths = paths;
        _mutex = mutex;
    }

    /// <summary>The registry file.</summary>
    public string Path => _paths.ClientRegistryFile;

    /// <summary>
    /// Every entry, in registry order.
    /// </summary>
    /// <remarks>
    /// A missing file is an empty rig (CLIENT-014). A payload that parses to null is an
    /// empty rig (CLIENT-015). A corrupt file WARNS with the parse error and degrades to an
    /// empty rig rather than throwing (CLIENT-016): loud degradation, not a crash, because
    /// the alternative is a rig nothing can list or repair.
    /// </remarks>
    public IReadOnlyList<InstanceEntry> Read()
    {
        if (!_fs.FileExists(Path)) return [];

        try
        {
            var text = _fs.ReadAllText(Path);
            if (string.IsNullOrWhiteSpace(text)) return [];

            var parsed = JsonSerializer.Deserialize(text, ClientJsonContext.Default.InstanceEntryArray);
            return parsed is null ? [] : parsed;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            _output.Line(OutputLevel.Warning,
                $"rig.json could not be parsed ({ex.Message}); treating the rig as empty.");
            return [];
        }
    }

    /// <summary>
    /// How an instance name is compared, everywhere.
    /// </summary>
    /// <remarks>
    /// Case-insensitively, and not as a convenience. An instance name IS a directory name on
    /// an NTFS volume, so <c>hostie</c> and <c>HOSTIE</c> are the same tree and the same save
    /// root whatever this file thinks; PowerShell's <c>-eq</c> and <c>-contains</c> compared
    /// names this way and the target resolver still does. An ordinal comparison here made
    /// <c>--target HOSTIE</c> resolve at the launcher and then fail to resolve one layer
    /// down, and a create with different casing would have written a second registry entry
    /// pointing at the first one's tree.
    /// </remarks>
    public static readonly StringComparer NameComparer = StringComparer.OrdinalIgnoreCase;

    /// <summary>Whether two instance names refer to the same instance.</summary>
    public static bool SameInstance(string? a, string? b) =>
        string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    /// <summary>The first entry with this name, or null.</summary>
    public InstanceEntry? Find(string name) =>
        Read().FirstOrDefault(e => SameInstance(e.InstanceName, name));

    /// <summary>
    /// Entries for a set of names, IN THE ORDER THE CALLER ASKED.
    /// </summary>
    /// <remarks>
    /// Caller order, not registry order (CLIENT-023). <c>stop</c> re-sorts by class anyway,
    /// but <c>call</c> and <c>save</c> fan out in the order given, which is what lets a
    /// sequence be expressed as a target list.
    /// </remarks>
    /// <exception cref="RigRefusalException">A name matches nothing.</exception>
    public IReadOnlyList<InstanceEntry> Entries(IReadOnlyList<string> names)
    {
        var registry = Read();
        var hits = new List<InstanceEntry>(names.Count);

        foreach (var name in names)
        {
            var entry = registry.FirstOrDefault(e => SameInstance(e.InstanceName, name));
            if (entry is null)
            {
                var known = registry.Count > 0
                    ? string.Join(", ", registry.Select(static e => e.InstanceName))
                    : "(none)";
                throw new RigRefusalException(
                    RigRefusalKind.Refused,
                    $"Instance '{name}' is not provisioned. Known instances: {known}. Create it with: "
                    + $"testrig create --target {name} [--role host], or list them with: testrig list");
            }
            hits.Add(entry);
        }

        return hits;
    }

    /// <summary>Every instance name, in registry order.</summary>
    public IReadOnlyList<string> Names() => [.. Read().Select(static e => e.InstanceName)];

    /// <summary>
    /// Replaces the whole registry, atomically.
    /// </summary>
    /// <remarks>
    /// Durable write: temp file, flush to disk, atomic rename. Always a JSON ARRAY, even
    /// for one entry, so a reader never has to cope with two shapes. UTF-8 with no byte
    /// order mark, pinned rather than inherited from the shell (CLIENT-017 fixed).
    /// </remarks>
    public void Write(IEnumerable<InstanceEntry> entries)
    {
        _fs.CreateDirectory(_paths.ClientDataDir);
        var json = JsonSerializer.Serialize(entries.ToArray(), ClientJsonContext.Default.InstanceEntryArray);
        _fs.WriteAllTextDurable(Path, json);
    }

    /// <summary>
    /// Runs a read-modify-write of the registry inside the cross-process critical section.
    /// </summary>
    /// <param name="mutate">
    /// Given the current entries, returns the new set. It runs INSIDE the section, so
    /// nothing it reads can be changed by another process before the write lands.
    /// </param>
    /// <returns>Whatever <paramref name="mutate"/>'s out-parameter produced.</returns>
    public TResult Update<TResult>(Func<IReadOnlyList<InstanceEntry>, (IReadOnlyList<InstanceEntry> Entries, TResult Result)> mutate)
    {
        if (_mutex is null)
        {
            var (entries, result) = mutate(Read());
            Write(entries);
            return result;
        }

        using var holder = _mutex.TryEnter(MutexTimeout, out var outcome);
        if (holder is null)
        {
            throw new RigRefusalException(
                RigRefusalKind.Refused,
                $"Could not enter the registry critical section '{_mutex.Name}' within "
                + $"{MutexTimeout.TotalSeconds:F0}s ({outcome}). Another testrig process is part way through a "
                + "create or a remove. Retry; if it never clears, that process is wedged and should be killed.");
        }

        if (outcome == MutexAcquisition.AcquiredAbandoned)
        {
            // The previous holder died mid-write. rig.json is written durably, so what is on
            // disk is either the old file or the new one, never half of either, but the
            // caller deserves to know a create was interrupted.
            _output.Line(OutputLevel.Warning,
                "The registry critical section was abandoned by a process that died holding it. rig.json is "
                + "written atomically, so it is intact, but an interrupted create may have left a tree with "
                + "no entry: check with testrig list.");
        }

        var (next, value) = mutate(Read());
        Write(next);
        return value;
    }

    /// <summary>Removes one instance, atomically.</summary>
    public void Remove(string name) =>
        Update<bool>(current => ([.. current.Where(e => !SameInstance(e.InstanceName, name))], true));
}
