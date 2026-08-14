using System.Text;
using TestRig.Core.Abstractions;

namespace TestRig.Tests.Session.Fakes;

/// <summary>
/// An in-memory filesystem that behaves like NTFS in the ways the rig depends on.
/// </summary>
/// <remarks>
/// This is a fake, not a shim. It enforces the things a real filesystem enforces, so code
/// that gets them wrong fails here instead of on the developer's machine:
/// <list type="bullet">
/// <item>paths are case-insensitive, so a case-only difference is the SAME file;</item>
/// <item>enumerating a directory that does not exist throws, rather than answering empty,
/// which is precisely the swallow that produced the 25-world delete plan;</item>
/// <item>writing into a directory that does not exist throws;</item>
/// <item>deleting a non-empty directory without recursion throws;</item>
/// <item>failures are injectable per path prefix, so "the enumeration threw" is a state a
/// test can actually produce.</item>
/// </list>
/// Nothing here touches a real disk. No test in this suite creates a file outside memory.
/// </remarks>
public sealed class FakeFileSystem : IFileSystem
{
    private sealed class Entry
    {
        public required string Path { get; init; }
        public byte[] Content { get; set; } = [];
        public DateTimeOffset LastWriteUtc { get; set; }
    }

    private readonly Dictionary<string, Entry> _files = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, DateTimeOffset> _dirs = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Path prefixes whose enumeration throws, and the exception message.</summary>
    public Dictionary<string, string> EnumerationFailures { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Exact paths whose read throws.</summary>
    public Dictionary<string, string> ReadFailures { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Exact paths whose write throws.</summary>
    public Dictionary<string, string> WriteFailures { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Exact paths whose delete throws.</summary>
    public Dictionary<string, string> DeleteFailures { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Paths written through the durable path, so a test can prove the marker used it.</summary>
    public List<string> DurableWrites { get; } = [];

    /// <summary>Every directory tree delete, so a test can prove what was destroyed.</summary>
    public List<string> DeletedTrees { get; } = [];

    /// <summary>
    /// Version resources, by path. A file with no entry here has none.
    /// </summary>
    /// <remarks>
    /// Separate from the file's content on purpose: on a real volume the version resource
    /// and the bytes are independent, and the port's earlier attempt to infer a version
    /// from a sidecar file is exactly the mistake that shape encourages.
    /// </remarks>
    public Dictionary<string, BinaryVersion> BinaryVersions { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Timestamp stamped onto anything written. Advance it to age a file.</summary>
    public DateTimeOffset Now { get; set; } = new(2026, 8, 14, 12, 0, 0, TimeSpan.Zero);

    private static string Key(string path) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));

    // ---- test-side helpers -------------------------------------------------

    public void AddFile(string path, string content) => AddFile(path, Encoding.UTF8.GetBytes(content));

    public void AddFile(string path, byte[] content)
    {
        var key = Key(path);
        var parent = Path.GetDirectoryName(key);
        if (!string.IsNullOrEmpty(parent)) AddDirectory(parent);
        _files[key] = new Entry { Path = key, Content = content, LastWriteUtc = Now };
    }

    public void AddDirectory(string path)
    {
        var key = Key(path);
        while (!string.IsNullOrEmpty(key))
        {
            if (!_dirs.ContainsKey(key)) _dirs[key] = Now;
            var parent = Path.GetDirectoryName(key);
            if (string.IsNullOrEmpty(parent) || string.Equals(parent, key, StringComparison.OrdinalIgnoreCase)) break;
            key = parent;
        }
    }

    public void SetLastWrite(string path, DateTimeOffset at)
    {
        var key = Key(path);
        if (_files.TryGetValue(key, out var entry)) entry.LastWriteUtc = at;
        else if (_dirs.ContainsKey(key)) _dirs[key] = at;
        else throw new FileNotFoundException(path);
    }

    /// <summary>Every file path currently present, for a whole-tree fingerprint.</summary>
    public IReadOnlyList<string> AllFiles() => [.. _files.Keys.OrderBy(static k => k, StringComparer.Ordinal)];

    /// <summary>A fingerprint of the whole tree, to prove a dry run moved nothing.</summary>
    public string Fingerprint()
    {
        var sb = new StringBuilder();
        foreach (var key in _dirs.Keys.OrderBy(static k => k, StringComparer.Ordinal)) sb.Append("D:").Append(key).Append('\n');
        foreach (var key in _files.Keys.OrderBy(static k => k, StringComparer.Ordinal))
        {
            sb.Append("F:").Append(key).Append(':').Append(Convert.ToHexString(_files[key].Content)).Append('\n');
        }
        return sb.ToString();
    }

    // ---- IFileSystem -------------------------------------------------------

    public bool FileExists(string path) => !string.IsNullOrEmpty(path) && _files.ContainsKey(Key(path));

    public bool DirectoryExists(string path) => !string.IsNullOrEmpty(path) && _dirs.ContainsKey(Key(path));

    public string ReadAllText(string path) => Encoding.UTF8.GetString(ReadAllBytes(path));

    public byte[] ReadAllBytes(string path)
    {
        var key = Key(path);
        if (ReadFailures.TryGetValue(key, out var message)) throw new IOException(message);
        if (!_files.TryGetValue(key, out var entry)) throw new FileNotFoundException($"No such file: {path}", path);
        return entry.Content;
    }

    public IReadOnlyList<string> ReadLines(string path) =>
        ReadAllText(path).Replace("\r\n", "\n").TrimEnd('\n').Split('\n');

    public IReadOnlyList<string> ReadTailLines(string path, int count)
    {
        var lines = ReadLines(path);
        return lines.Count <= count ? lines : [.. lines.Skip(lines.Count - count)];
    }

    public void WriteAllText(string path, string content)
    {
        var key = Key(path);
        if (WriteFailures.TryGetValue(key, out var message)) throw new IOException(message);

        var parent = Path.GetDirectoryName(key);
        if (!string.IsNullOrEmpty(parent) && !_dirs.ContainsKey(parent))
        {
            throw new DirectoryNotFoundException($"No such directory: {parent}");
        }

        _files[key] = new Entry { Path = key, Content = Encoding.UTF8.GetBytes(content), LastWriteUtc = Now };
    }

    /// <summary>
    /// Appends, with the same directory rule a write has.
    /// </summary>
    /// <remarks>
    /// A real append creates the file when it is absent and does not require it to exist,
    /// which is the whole point of it, so an absent file is not a failure here either.
    /// </remarks>
    public void AppendAllText(string path, string content)
    {
        var key = Key(path);
        if (WriteFailures.TryGetValue(key, out var message)) throw new IOException(message);

        var parent = Path.GetDirectoryName(key);
        if (!string.IsNullOrEmpty(parent) && !_dirs.ContainsKey(parent))
        {
            throw new DirectoryNotFoundException($"No such directory: {parent}");
        }

        var existing = _files.TryGetValue(key, out var entry) ? Encoding.UTF8.GetString(entry.Content) : "";
        _files[key] = new Entry { Path = key, Content = Encoding.UTF8.GetBytes(existing + content), LastWriteUtc = Now };
    }

    public void WriteAllTextDurable(string path, string content)
    {
        WriteAllText(path, content);
        DurableWrites.Add(Key(path));
    }

    public void CreateDirectory(string path) => AddDirectory(path);

    public void DeleteFile(string path)
    {
        var key = Key(path);
        if (DeleteFailures.TryGetValue(key, out var message)) throw new IOException(message);
        _files.Remove(key);
        BinaryVersions.Remove(key);
    }

    public void DeleteDirectory(string path, bool recursive)
    {
        var key = Key(path);
        if (DeleteFailures.TryGetValue(key, out var message)) throw new IOException(message);
        if (!_dirs.ContainsKey(key)) throw new DirectoryNotFoundException($"No such directory: {path}");

        var prefix = key + Path.DirectorySeparatorChar;
        var childFiles = _files.Keys.Where(k => k.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)).ToArray();
        var childDirs = _dirs.Keys.Where(k => k.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)).ToArray();

        if (!recursive && (childFiles.Length > 0 || childDirs.Length > 0))
        {
            throw new IOException($"The directory is not empty: {path}");
        }

        foreach (var file in childFiles) _files.Remove(file);
        foreach (var dir in childDirs) _dirs.Remove(dir);
        _dirs.Remove(key);
        DeletedTrees.Add(key);
    }

    public IReadOnlyList<string> EnumerateFiles(string path, string searchPattern, bool recurse)
    {
        var key = Key(path);
        ThrowIfEnumerationFails(key);
        if (!_dirs.ContainsKey(key)) throw new DirectoryNotFoundException($"No such directory: {path}");

        var prefix = key + Path.DirectorySeparatorChar;
        var result = new List<string>();
        foreach (var file in _files.Keys)
        {
            if (!file.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;
            var rel = file[prefix.Length..];
            if (!recurse && rel.Contains(Path.DirectorySeparatorChar)) continue;
            if (!Matches(Path.GetFileName(file), searchPattern)) continue;
            result.Add(file);
        }
        result.Sort(StringComparer.OrdinalIgnoreCase);
        return result;
    }

    public IReadOnlyList<string> EnumerateDirectories(string path)
    {
        var key = Key(path);
        ThrowIfEnumerationFails(key);
        if (!_dirs.ContainsKey(key)) throw new DirectoryNotFoundException($"No such directory: {path}");

        var prefix = key + Path.DirectorySeparatorChar;
        var result = new List<string>();
        foreach (var dir in _dirs.Keys)
        {
            if (!dir.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;
            if (dir[prefix.Length..].Contains(Path.DirectorySeparatorChar)) continue;
            result.Add(dir);
        }
        result.Sort(StringComparer.OrdinalIgnoreCase);
        return result;
    }

    public long GetFileLength(string path)
    {
        var key = Key(path);
        if (!_files.TryGetValue(key, out var entry)) throw new FileNotFoundException($"No such file: {path}", path);
        return entry.Content.Length;
    }

    public DateTimeOffset GetLastWriteTimeUtc(string path)
    {
        var key = Key(path);
        if (_files.TryGetValue(key, out var entry)) return entry.LastWriteUtc;
        if (_dirs.TryGetValue(key, out var at)) return at;
        throw new FileNotFoundException($"No such path: {path}", path);
    }

    public BinaryVersion? TryGetBinaryVersion(string path)
    {
        var key = Key(path);
        if (!_files.ContainsKey(key)) return null;
        return BinaryVersions.TryGetValue(key, out var version) ? version : new BinaryVersion("", "");
    }

    /// <summary>Stamps a version resource onto a file, creating it when absent.</summary>
    public void SetBinaryVersion(string path, string fileVersion, string productVersion)
    {
        if (!FileExists(path)) AddFile(path, "MZ");
        BinaryVersions[Key(path)] = new BinaryVersion(fileVersion, productVersion);
    }

    public void CopyFile(string source, string destination, bool overwrite)
    {
        var sourceKey = Key(source);
        if (!_files.TryGetValue(sourceKey, out var entry)) throw new FileNotFoundException($"No such file: {source}", source);

        var destKey = Key(destination);
        if (!overwrite && _files.ContainsKey(destKey)) throw new IOException($"Already exists: {destination}");

        var parent = Path.GetDirectoryName(destKey);
        if (!string.IsNullOrEmpty(parent) && !_dirs.ContainsKey(parent))
        {
            throw new DirectoryNotFoundException($"No such directory: {parent}");
        }

        _files[destKey] = new Entry { Path = destKey, Content = [.. entry.Content], LastWriteUtc = Now };

        // A real copy carries the version resource with the bytes, and the mirror in
        // update-game reads the version off the COPY. Without this the seam would answer
        // "no version" for every mirrored DLL, which is the shape of the bug it replaces.
        if (BinaryVersions.TryGetValue(sourceKey, out var version)) BinaryVersions[destKey] = version;
        else BinaryVersions.Remove(destKey);
    }

    public void CreateHardLink(string linkPath, string existingFilePath) => CopyFile(existingFilePath, linkPath, overwrite: false);

    private void ThrowIfEnumerationFails(string key)
    {
        foreach (var (prefix, message) in EnumerationFailures)
        {
            if (key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) throw new UnauthorizedAccessException(message);
        }
    }

    /// <summary>The subset of Win32 wildcard matching the rig actually uses: '*' only.</summary>
    private static bool Matches(string name, string pattern)
    {
        if (pattern == "*") return true;
        if (!pattern.Contains('*')) return string.Equals(name, pattern, StringComparison.OrdinalIgnoreCase);

        var parts = pattern.Split('*');
        var index = 0;
        for (var i = 0; i < parts.Length; i++)
        {
            var part = parts[i];
            if (part.Length == 0) continue;

            if (i == 0)
            {
                if (!name.StartsWith(part, StringComparison.OrdinalIgnoreCase)) return false;
                index = part.Length;
                continue;
            }

            if (i == parts.Length - 1 && !pattern.EndsWith('*'))
            {
                return name.Length >= index + part.Length
                       && name.EndsWith(part, StringComparison.OrdinalIgnoreCase);
            }

            var found = name.IndexOf(part, index, StringComparison.OrdinalIgnoreCase);
            if (found < 0) return false;
            index = found + part.Length;
        }
        return true;
    }
}
