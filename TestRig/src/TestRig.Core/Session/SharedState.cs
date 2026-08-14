using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using TestRig.Core.Abstractions;

namespace TestRig.Core.Session;

/// <summary>
/// A reading of the per-user state nothing can isolate.
/// </summary>
/// <param name="CapturedUtc">When it was taken.</param>
/// <param name="SharedDataDir">The folder it read, carried so a drift report can name it.</param>
/// <param name="PlayerPrefsKey">The registry key it read, for the same reason.</param>
/// <param name="Values">Ordered, sorted, and comparable line by line against another snapshot.</param>
public sealed record SharedStateSnapshot(
    string CapturedUtc,
    string SharedDataDir,
    string PlayerPrefsKey,
    IReadOnlyDictionary<string, string> Values);

/// <summary>One difference between two snapshots.</summary>
public sealed record SharedStateDelta(string Key, string? Before, string? After)
{
    /// <summary>The three shapes RESET-145 specifies, and no fourth.</summary>
    public override string ToString() =>
        After is null ? $"{Key} : '{Before}' -> gone"
        : Before is null ? $"{Key} : new -> '{After}'"
        : $"{Key} : '{Before}' -> '{After}'";
}

/// <summary>
/// The shared per-user state: read, compared and REPORTED. Never restored.
/// </summary>
/// <remarks>
/// <para>
/// <c>PlayerCookie-v2.xml</c>, the game's PlayerPrefs registry key and <c>Blueprints\</c> are
/// per-Windows-user and shared with the developer's own client, because
/// <c>persistentDataPath</c> is fixed inside the serialized PlayerSettings in
/// <c>globalgamemanagers</c> and editing <c>app.info</c> was tested and does nothing. The rig
/// cannot separate its own use of them from the developer's.
/// </para>
/// <para>
/// So it names what moved. RESET-143 calls that the honest half of the guarantee: it converts
/// state that was invisible until a later test failed into a line at the session boundary.
/// There is deliberately NO function anywhere here that could put any of it back
/// (RESET-136, RESET-147), and <see cref="IRegistry"/> has no writer for the same reason.
/// </para>
/// </remarks>
public sealed partial class SharedStateReader
{
    /// <summary>The game's PlayerPrefs key (RESET-004).</summary>
    public const string DefaultPlayerPrefsKey = @"HKCU:\Software\Rocketwerkz\rocketstation";

    /// <summary>Above this many characters a value is stored as a hash instead (RESET-139).</summary>
    /// <remarks>
    /// The snapshot exists to spot a CHANGE, and a multi-kilobyte blob in a JSON file nobody
    /// reads is not worth the disk. A hash still changes when the value does.
    /// </remarks>
    public const int MaxValueLength = 200;

    private readonly IFileSystem _fs;
    private readonly IRegistry _registry;
    private readonly IClock _clock;
    private readonly string _sharedDataDir;
    private readonly string _playerPrefsKey;

    public SharedStateReader(
        IFileSystem fs,
        IRegistry registry,
        IClock clock,
        string? sharedDataDir,
        string? playerPrefsKey = null)
    {
        _fs = fs;
        _registry = registry;
        _clock = clock;
        _sharedDataDir = sharedDataDir ?? string.Empty;
        _playerPrefsKey = string.IsNullOrWhiteSpace(playerPrefsKey) ? DefaultPlayerPrefsKey : playerPrefsKey;
    }

    public string SharedDataDir => _sharedDataDir;

    public string PlayerPrefsKey => _playerPrefsKey;

    /// <summary>The developer's own player cookie. Read for its size only, never written.</summary>
    public string CookiePath => Path.Combine(_sharedDataDir, "PlayerCookie-v2.xml");

    /// <summary>The developer's own blueprint folder. Counted, never touched.</summary>
    public string BlueprintsPath => Path.Combine(_sharedDataDir, "Blueprints");

    /// <summary>Takes a reading. Read only, always (RESET-136).</summary>
    public SharedStateSnapshot Capture()
    {
        // Insertion-ordered, so the file reads the way the PowerShell's ordered hashtable did:
        // cookie, then prefs, then blueprints.
        var values = new Dictionary<string, string>(StringComparer.Ordinal);

        ReadCookie(values);
        ReadPlayerPrefs(values);
        ReadBlueprints(values);

        return new SharedStateSnapshot(RigTime.Stamp(_clock.UtcNow), _sharedDataDir, _playerPrefsKey, values);
    }

    /// <summary>RESET-137: the cookie's size and its world count, or why neither could be had.</summary>
    private void ReadCookie(Dictionary<string, string> values)
    {
        var cookie = CookiePath;
        if (_sharedDataDir.Length == 0 || !_fs.FileExists(cookie))
        {
            values["cookie.bytes"] = "absent";
            return;
        }

        try
        {
            values["cookie.bytes"] = _fs.GetFileLength(cookie).ToString(CultureInfo.InvariantCulture);
            values["cookie.worlds"] = WorldElement()
                .Matches(_fs.ReadAllText(cookie)).Count
                .ToString(CultureInfo.InvariantCulture);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Both keys collapse to one, so an unreadable cookie cannot look like a cookie of
            // zero worlds.
            values.Remove("cookie.worlds");
            values["cookie.bytes"] = "unreadable";
        }
    }

    /// <summary>RESET-138, RESET-139, RESET-140.</summary>
    private void ReadPlayerPrefs(Dictionary<string, string> values)
    {
        var read = _registry.TryReadValues(_playerPrefsKey);
        if (read is null)
        {
            values["prefs"] = "unreadable";
            return;
        }

        foreach (var (name, value) in read)
        {
            values["prefs." + name] = Shorten(value);
        }
    }

    /// <summary>RESET-141: a recursive file count, and never anything else.</summary>
    private void ReadBlueprints(Dictionary<string, string> values)
    {
        var count = 0;
        if (_sharedDataDir.Length > 0 && _fs.DirectoryExists(BlueprintsPath))
        {
            try
            {
                count = _fs.EnumerateFiles(BlueprintsPath, "*", recurse: true).Count;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                values["blueprints.files"] = "unreadable";
                return;
            }
        }

        values["blueprints.files"] = count.ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>RESET-139: a long value becomes <c>sha256:</c> plus its first eight bytes.</summary>
    public static string Shorten(string value)
    {
        if (value.Length <= MaxValueLength) return value;
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        return "sha256:" + Convert.ToHexString(hash.AsSpan(0, 8));
    }

    /// <summary>
    /// Every difference between two readings, sorted, gone before new (RESET-145).
    /// </summary>
    /// <remarks>
    /// Empty when nothing moved, which is the answer the report turns into its one-line
    /// "unchanged" sentence.
    /// </remarks>
    public static IReadOnlyList<SharedStateDelta> Compare(
        IReadOnlyDictionary<string, string> before,
        IReadOnlyDictionary<string, string> after)
    {
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(after);

        var deltas = new List<SharedStateDelta>();

        foreach (var key in before.Keys.OrderBy(static k => k, StringComparer.Ordinal))
        {
            if (!after.TryGetValue(key, out var now))
            {
                deltas.Add(new SharedStateDelta(key, before[key], null));
                continue;
            }
            if (!string.Equals(before[key], now, StringComparison.Ordinal))
            {
                deltas.Add(new SharedStateDelta(key, before[key], now));
            }
        }

        foreach (var key in after.Keys.OrderBy(static k => k, StringComparer.Ordinal))
        {
            if (!before.ContainsKey(key)) deltas.Add(new SharedStateDelta(key, null, after[key]));
        }

        return deltas;
    }

    [GeneratedRegex(@"<World[\s>]", RegexOptions.CultureInvariant)]
    private static partial Regex WorldElement();
}

/// <summary>
/// The <c>[State]</c> lines printed at the end of a session (RESET-150, 151, 152).
/// </summary>
/// <remarks>
/// This fixes nothing, and saying so is the point. It turns state that was invisible until a
/// later test failed into a line at the session boundary.
/// </remarks>
public static class SharedStateReport
{
    public const string NoBaseline = "[State] No shared-state baseline for this session, so no drift report.";

    public const string Unchanged =
        "[State] Shared per-user state is unchanged since the lock was taken (PlayerCookie-v2.xml, PlayerPrefs, "
        + "Blueprints).";

    public const string DriftHeader =
        "[State] Shared per-user state MOVED during this session. It cannot be isolated and is never restored, so "
        + "this is a report:";

    public const string DriftFooter =
        "[State] These are shared with the developer's own client. Nothing here is save data.";

    /// <summary>Renders the report for a baseline that may not exist.</summary>
    public static IReadOnlyList<string> Render(
        IReadOnlyDictionary<string, string>? baseline,
        IReadOnlyDictionary<string, string> now)
    {
        if (baseline is null) return [NoBaseline];

        var deltas = SharedStateReader.Compare(baseline, now);
        if (deltas.Count == 0) return [Unchanged];

        var lines = new List<string> { DriftHeader };
        foreach (var delta in deltas) lines.Add("[State]   " + delta);
        lines.Add(DriftFooter);
        return lines;
    }
}
