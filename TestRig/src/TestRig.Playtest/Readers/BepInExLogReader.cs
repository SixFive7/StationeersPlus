using System.Text.Json.Nodes;
using TestRig.Playtest.Seams;

namespace TestRig.Playtest.Readers;

/// <summary>One line of the BepInEx log file.</summary>
/// <param name="Src">
///     Always <c>bepinexfile</c>.
///     <para>
///     <b>Defect P-15.</b> The PowerShell reader named this field <c>source</c> while the
///     console endpoint names it <c>src</c>, and the two readers are documented as
///     interchangeable ("a check switches reader name and nothing else"). A check that read a
///     line's origin got a value from one and absent from the other. It is <c>src</c> here,
///     matching <c>TestRig.Contracts.ConsoleLine</c>.
///     </para>
/// </param>
/// <param name="Text">The line.</param>
public sealed record BepInExLogLine(string Src, string Text);

/// <summary>
///     The instance's BepInEx log file, shaped like the console endpoint's answer.
/// </summary>
/// <param name="Ok">False when the file is absent or unreadable.</param>
/// <param name="Instance">Whose log it is.</param>
/// <param name="Path">Where it was read from.</param>
/// <param name="Exists">
///     Reported separately so an absent file is a distinguishable fact rather than a count of
///     zero. A check that cannot find the log has learned nothing about the mod.
/// </param>
/// <param name="Bytes">File size.</param>
/// <param name="TotalLines">Every line in the file.</param>
/// <param name="Count">
///     The number of MATCHES, exactly as the console endpoint means it, and NOT clipped by
///     the limit: a check counting six banner lines with a limit of five must read 6 and fail,
///     not read 5 and pass.
/// </param>
/// <param name="Lines">The matches, clipped by the limit.</param>
public sealed record BepInExLogReading(
    bool Ok,
    string Instance,
    string Path,
    bool Exists,
    long Bytes,
    int TotalLines,
    int Count,
    IReadOnlyList<BepInExLogLine> Lines,
    string? Error = null)
{
    /// <summary>The node a select path runs over. Key order is fixed.</summary>
    public JsonNode ToNode()
    {
        var lines = new JsonArray();
        foreach (var line in Lines)
        {
            lines.Add((JsonNode)new JsonObject { ["src"] = line.Src, ["text"] = line.Text });
        }

        var obj = new JsonObject
        {
            ["ok"] = Ok,
            ["instance"] = Instance,
            ["path"] = Path,
            ["exists"] = Exists,
            ["bytes"] = Bytes,
            ["totalLines"] = TotalLines,
            ["count"] = Count,
            ["matched"] = Count,
            ["lines"] = lines,
        };

        if (Error is not null) obj["error"] = Error;
        return obj;
    }
}

/// <summary>
///     Reads the instance's BepInEx log file.
/// </summary>
/// <remarks>
///     The console tee is a bounded ring, 2000 lines per source, and StationeersLaunchPad's
///     mod loading evicts thousands of lines during boot, so a boot-time line is routinely
///     gone before any check can read it. The log file has no ring and the between-session
///     state reset deletes it, so what it holds is this run and only this run. That makes it
///     the right authority for anything printed during BOOT, and the tee still the right one
///     for the runtime half, where sequence numbers separate "at the menu" from "in a world".
/// </remarks>
public static class BepInExLogReader
{
    /// <summary>The source label every row carries.</summary>
    public const string SourceLabel = "bepinexfile";

    public static BepInExLogReading Read(ILogFiles files, string instance, string path, string? contains, int limit)
    {
        ArgumentNullException.ThrowIfNull(files);

        if (!files.Exists(path))
            return new BepInExLogReading(false, instance, path, false, 0, 0, 0, []);

        long bytes;
        try { bytes = files.Length(path); }
        catch (IOException) { bytes = 0; }

        IReadOnlyList<string> all;
        try
        {
            all = files.ReadAllLines(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new BepInExLogReading(false, instance, path, true, bytes, 0, 0, [], ex.Message);
        }

        // Defect P-14: this filter was case-SENSITIVE while the console endpoint's own
        // `contains` is case-INSENSITIVE (IndexOf with OrdinalIgnoreCase). The two readers
        // are documented as interchangeable, so they now filter the same way.
        var hits = new List<BepInExLogLine>();
        foreach (var line in all)
        {
            if (string.IsNullOrEmpty(line)) continue;
            if (!string.IsNullOrEmpty(contains) && line.IndexOf(contains, StringComparison.OrdinalIgnoreCase) < 0) continue;
            hits.Add(new BepInExLogLine(SourceLabel, line));
        }

        var matched = hits.Count;
        IReadOnlyList<BepInExLogLine> clipped = limit > 0 && matched > limit ? hits.GetRange(0, limit) : hits;

        return new BepInExLogReading(true, instance, path, true, bytes, all.Count, matched, clipped);
    }
}

/// <summary>The reader-args shape the log reader takes. Not a wire type; there is no endpoint.</summary>
/// <param name="Contains">Case-insensitive substring filter.</param>
/// <param name="Limit">Clips the returned lines. Never clips the count.</param>
public sealed record BepInExLogRequest(string? Contains = null, int Limit = 0);
