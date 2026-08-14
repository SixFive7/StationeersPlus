using System.Text.RegularExpressions;
using TestRig.Core.Abstractions;

namespace TestRig.Core.Server;

/// <summary>Why a save wait ended.</summary>
public enum SaveVerdict
{
    /// <summary>The game said it landed, and the file is there.</summary>
    Confirmed,

    /// <summary>The game said it failed. The wait ended early rather than burning the budget.</summary>
    Failed,

    /// <summary>Nothing said either way inside the budget.</summary>
    Timeout,
}

/// <summary>The outcome of waiting for a save, and the evidence for it.</summary>
public sealed record SaveOutcome(SaveVerdict Verdict, string Evidence)
{
    public bool Confirmed => Verdict == SaveVerdict.Confirmed;
}

/// <summary>
/// Deciding whether a line in the server log confirms a save.
/// </summary>
/// <remarks>
/// <para>
/// Pure and separate from the polling, so every branch is reachable from a test with no
/// server running. Two real faults are fixed here.
/// </para>
/// <para>
/// <b>Spec D-06: the pattern was unanchored and case-insensitive.</b> The PowerShell matched
/// <c>Saved.*&lt;name&gt;</c> anywhere in a line, so <c>[Station Notepad] Saved file system
/// to ...</c> confirmed a save named <c>notepad</c>, and any line mentioning both words
/// confirmed names like <c>json</c> or <c>install</c>. Matching is anchored at the start of
/// the line, after an optional timestamp, so a bracketed source prefix can never match, and
/// it is ordinal and case-sensitive.
/// </para>
/// <para>
/// <b>Spec D-05: a first-time save cannot say the name.</b> A save into a folder that does
/// not exist yet goes down NewSaveTask and prints <c>Created new save</c>, which carries no
/// name at all, so the most common rig operation always reported a false warning. That line
/// is accepted, and disambiguated by whether the folder already existed when the save was
/// queued: a first-time save may take it, a re-save may not, because a re-save that printed
/// it would be saving something else.
/// </para>
/// </remarks>
public static class SaveConfirmation
{
    /// <summary>The game's own failure lines. Seeing one ends the wait immediately.</summary>
    public static readonly IReadOnlyList<string> FailureMarkers =
    [
        "Save Failed",
        "Failed to write save file",
        "Cannot save game in GameState",
    ];

    /// <summary>
    /// Strips a leading timestamp so the confirmation can be anchored at the real start.
    /// </summary>
    /// <remarks>
    /// Two forms appear in a Unity <c>-logFile</c>: a bare <c>HH:mm:ss</c> and a bracketed
    /// one. A bracketed SOURCE tag such as <c>[Station Notepad]</c> is deliberately NOT
    /// stripped, because stripping it is exactly what would let it match again.
    /// </remarks>
    public static string StripTimestamp(string line)
    {
        var match = Regex.Match(
            line,
            @"^\s*(?:\[(?<b>[0-9][0-9:.\- T]*)\]\s*|(?<t>[0-9]{2}:[0-9]{2}:[0-9]{2}(?:\.[0-9]+)?)\s+)?",
            RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));

        return match.Success ? line[match.Length..] : line;
    }

    /// <summary>Whether a line is the game reporting that the save with this name landed.</summary>
    /// <remarks>
    /// Ordinal and case-sensitive, anchored, and the remainder has to BE the name rather
    /// than merely contain it.
    /// </remarks>
    public static bool IsNamedConfirmation(string line, string saveName)
    {
        if (string.IsNullOrEmpty(saveName)) return false;

        var body = StripTimestamp(line);
        const string prefix = "Saved ";
        if (!body.StartsWith(prefix, StringComparison.Ordinal)) return false;

        var rest = body[prefix.Length..].TrimEnd();
        if (rest.EndsWith('.')) rest = rest[..^1].TrimEnd();

        return string.Equals(rest, saveName, StringComparison.Ordinal);
    }

    /// <summary>Whether a line is the first-time-save confirmation, which carries no name.</summary>
    public static bool IsNewSaveConfirmation(string line) =>
        StripTimestamp(line).StartsWith("Created new save", StringComparison.Ordinal);

    /// <summary>Whether a line is the game reporting that the save failed.</summary>
    public static bool IsFailure(string line)
    {
        var body = StripTimestamp(line);
        foreach (var marker in FailureMarkers)
        {
            if (body.Contains(marker, StringComparison.Ordinal)) return true;
        }
        return false;
    }

    /// <summary>
    /// Classifies one appended line.
    /// </summary>
    /// <param name="folderExistedBefore">
    /// Whether the save folder existed when the command was queued. A first-time save may
    /// take the nameless <c>Created new save</c> line; a re-save may not.
    /// </param>
    public static SaveOutcome? Classify(string line, string saveName, bool folderExistedBefore)
    {
        if (IsFailure(line)) return new SaveOutcome(SaveVerdict.Failed, line.Trim());
        if (IsNamedConfirmation(line, saveName)) return new SaveOutcome(SaveVerdict.Confirmed, line.Trim());
        if (!folderExistedBefore && IsNewSaveConfirmation(line))
        {
            return new SaveOutcome(SaveVerdict.Confirmed, line.Trim());
        }
        return null;
    }
}

/// <summary>
/// Watching the dedicated server's log for a line, without blocking its writes.
/// </summary>
/// <remarks>
/// <para>
/// <b>The baseline is captured BEFORE the command is queued (spec D-12 related, SERVER-096
/// fixed).</b> The PowerShell captured it after the rename, so a confirmation written in
/// between was already past the offset and could never match. The caller constructs the
/// watcher first and queues second.
/// </para>
/// <para>
/// <b>The offset ADVANCES (spec D-12, SERVER-098 fixed).</b> The PowerShell re-read and
/// re-matched the entire appended region every 500 ms, which grows quadratically over a
/// 300 s budget on a server writing steadily. Here each line is classified once. A log that
/// SHRANK has been rotated, so the scan restarts from the beginning rather than silently
/// skipping everything after the rotation.
/// </para>
/// <para>
/// The seam has no read-from-offset, so the file is still re-read each poll; what no longer
/// grows is the matching, which is where the cost actually was. Polls only happen when the
/// length changed.
/// </para>
/// <para>
/// The file is opened SHARING READ AND WRITE, which the seam's reader does unconditionally
/// (SERVER-097). Anything narrower blocks the running server's own writes, and a server that
/// cannot write its log is a server that stalls.
/// </para>
/// </remarks>
public sealed class ServerLogWatcher
{
    private readonly IFileSystem _fs;
    private readonly string _path;

    private long _lastLength;
    private int _consumedLines;

    /// <summary>Captures the current end of the log. Do this BEFORE queueing the command.</summary>
    public ServerLogWatcher(IFileSystem fs, string path)
    {
        _fs = fs;
        _path = path;
        Rebase();
    }

    /// <summary>Whether the log exists at all. A missing log confirms nothing, ever.</summary>
    public bool Exists => _fs.FileExists(_path);

    private void Rebase()
    {
        if (!_fs.FileExists(_path))
        {
            _lastLength = 0;
            _consumedLines = 0;
            return;
        }

        try
        {
            _lastLength = _fs.GetFileLength(_path);
            _consumedLines = _fs.ReadLines(_path).Count;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FileNotFoundException)
        {
            _lastLength = 0;
            _consumedLines = 0;
        }
    }

    /// <summary>
    /// Lines appended since the last call, or an empty list when nothing changed.
    /// </summary>
    public IReadOnlyList<string> NewLines()
    {
        if (!_fs.FileExists(_path)) return [];

        long length;
        try
        {
            length = _fs.GetFileLength(_path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FileNotFoundException)
        {
            return [];
        }

        if (length == _lastLength) return [];

        if (length < _lastLength)
        {
            // Rotated or truncated. Everything the old offsets described is gone, so the
            // scan restarts rather than skipping the whole new file.
            _lastLength = 0;
            _consumedLines = 0;
        }

        IReadOnlyList<string> lines;
        try
        {
            lines = _fs.ReadLines(_path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FileNotFoundException)
        {
            return [];
        }

        _lastLength = length;

        if (lines.Count <= _consumedLines)
        {
            _consumedLines = lines.Count;
            return [];
        }

        var fresh = lines.Skip(_consumedLines).ToArray();
        _consumedLines = lines.Count;
        return fresh;
    }
}
