using System.Text.Json.Serialization;

namespace TestRig.Contracts;

// /console/log, /console/clear, /console/buffer, /console/exec, /console/print,
// /console/commands.

/// <summary>
///     One tapped console or BepInEx line.
/// </summary>
/// <remarks>
///     <b>A row object, never a bare string.</b> The PowerShell fake emitted
///     <c>lines: ['[Example] a console line']</c> (divergence D-41), and the only reason
///     nothing broke is that the one consumer defensively handled both. A check wanting
///     <c>lines[0].level</c> could not be written, let alone tested. Note also that the
///     separate BepInEx log-file reader names this row's source field <c>source</c> while
///     the endpoint names it <c>src</c> (defect P-15); the two readers were documented as
///     interchangeable and are not.
/// </remarks>
public sealed record ConsoleLine
{
    [JsonPropertyName("seq")]
    public long Seq { get; init; }

    /// <summary>Seconds since startup, not a wall clock.</summary>
    [JsonPropertyName("t")]
    public double T { get; init; }

    /// <summary><c>console</c> or <c>bepinex</c>. The field is <c>src</c>, not <c>source</c>.</summary>
    [JsonPropertyName("src")]
    public string? Src { get; init; }

    [JsonPropertyName("level")]
    public string? Level { get; init; }

    [JsonPropertyName("text")]
    public string? Text { get; init; }

    /// <summary>Present and true only when the line was cut at the per-line character cap.</summary>
    [JsonPropertyName("truncated")]
    public bool? Truncated { get; init; }
}

/// <summary>
///     <c>/console/log</c>. All four filters are applied server-side.
/// </summary>
/// <remarks>
///     The fake ignored every one of them and always answered <c>count:1</c>
///     (divergence D-42), which left fifteen shipped assertions on <c>count</c>
///     unsimulatable. The whole console-counting discipline rests on these four fields
///     actually reaching the endpoint.
/// </remarks>
public sealed record ConsoleLogRequest
{
    /// <summary>Drop every line whose <c>seq</c> is below this. Zero means from the start of the ring.</summary>
    [JsonPropertyName("since")]
    public long? Since { get; init; }

    /// <summary>Keep the newest N. Zero means unlimited. Defaults to 200.</summary>
    [JsonPropertyName("limit")]
    public int? Limit { get; init; }

    /// <summary>
    ///     Case-INSENSITIVE substring. The separate BepInEx log-file reader filters
    ///     case-sensitively (defect P-14), so the same filter string can give different
    ///     answers across the two.
    /// </summary>
    [JsonPropertyName("contains")]
    public string? Contains { get; init; }

    /// <summary><c>console</c> or <c>bepinex</c>. Absent means both.</summary>
    [JsonPropertyName("source")]
    public string? Source { get; init; }
}

/// <summary>
///     The tee's output. Not sealed: <see cref="ConsoleExecResponse"/> is this exact
///     payload with <c>command</c> spliced in, and deriving from it is what stops the two
///     drifting apart.
/// </summary>
public record ConsoleLogResponse : IWireResult
{
    [JsonPropertyName("ok")]
    public bool Ok { get; init; }

    /// <summary>Pass this back as <c>since</c> on the next read to get only what is new.</summary>
    [JsonPropertyName("nextSeq")]
    public long NextSeq { get; init; }

    /// <summary>Lines evicted from the ring before anyone read them.</summary>
    [JsonPropertyName("dropped")]
    public long Dropped { get; init; }

    [JsonPropertyName("truncated")]
    public long Truncated { get; init; }

    [JsonPropertyName("bufferedLines")]
    public int BufferedLines { get; init; }

    [JsonPropertyName("bufferedChars")]
    public long BufferedChars { get; init; }

    [JsonPropertyName("count")]
    public int Count { get; init; }

    [JsonPropertyName("lines")]
    public ConsoleLine[]? Lines { get; init; }
}

/// <summary><c>/console/clear</c>. No parameters.</summary>
public sealed record ConsoleClearRequest;

/// <summary><c>{"ok":true}</c> and nothing else.</summary>
public sealed record ConsoleClearResponse : IWireResult
{
    [JsonPropertyName("ok")]
    public bool Ok { get; init; }
}

/// <summary><c>/console/buffer</c>. Reads the game's own 1024-entry ring, newest first.</summary>
public sealed record ConsoleBufferRequest
{
    [JsonPropertyName("limit")]
    public int? Limit { get; init; }

    [JsonPropertyName("contains")]
    public string? Contains { get; init; }
}

/// <summary>One row of the game's own console ring. A different shape from <see cref="ConsoleLine"/>.</summary>
public sealed record ConsoleBufferLine
{
    /// <summary>Position in the ring. Index 0 is the newest.</summary>
    [JsonPropertyName("i")]
    public int I { get; init; }

    /// <summary>The game's own formatted stamp, as text.</summary>
    [JsonPropertyName("time")]
    public string? Time { get; init; }

    [JsonPropertyName("color")]
    public int Color { get; init; }

    [JsonPropertyName("text")]
    public string? Text { get; init; }
}

/// <summary>
///     The game's own ring. Covers everything printed before this plugin loaded, and the
///     block and table printers that bypass <c>Print</c> and therefore never reach the tee.
/// </summary>
public sealed record ConsoleBufferResponse : IWireResult
{
    [JsonPropertyName("ok")]
    public bool Ok { get; init; }

    [JsonPropertyName("lines")]
    public ConsoleBufferLine[]? Lines { get; init; }

    [JsonPropertyName("count")]
    public int Count { get; init; }

    [JsonPropertyName("bufferSize")]
    public int BufferSize { get; init; }
}

/// <summary>
///     <c>/console/exec</c>. Runs a console command and returns every line it printed.
/// </summary>
/// <remarks>
///     This is the path. There is no <c>/console/run</c>; the PowerShell refusal matrix
///     named one and nothing caught it, because a path is only checked when a request
///     actually goes out against a locked rig.
/// </remarks>
public sealed record ConsoleExecRequest
{
    /// <summary>Required. 400 <c>missing 'command'</c> otherwise.</summary>
    [JsonPropertyName("command")]
    public string? Command { get; init; }

    /// <summary>Frames to wait for output after submitting. Defaults to 2.</summary>
    [JsonPropertyName("waitFrames")]
    public int? WaitFrames { get; init; }

    /// <summary>Extra wall-clock wait in milliseconds. Defaults to 0.</summary>
    [JsonPropertyName("waitMs")]
    public int? WaitMs { get; init; }
}

/// <summary>
///     A <see cref="ConsoleLogResponse"/> with the command echoed back. The plugin
///     literally splices <c>"command":...</c> into the console-log payload, so this
///     derives from it rather than restating the fields.
/// </summary>
public sealed record ConsoleExecResponse : ConsoleLogResponse
{
    [JsonPropertyName("command")]
    public string? Command { get; init; }
}

/// <summary><c>/console/print</c>. Writes a line into the game's console.</summary>
public sealed record ConsolePrintRequest
{
    [JsonPropertyName("text")]
    public string? Text { get; init; }

    /// <summary><c>error</c>, <c>info</c>, or anything else for the default action style.</summary>
    [JsonPropertyName("level")]
    public string? Level { get; init; }
}

/// <summary><c>{"ok":true}</c> and nothing else.</summary>
public sealed record ConsolePrintResponse : IWireResult
{
    [JsonPropertyName("ok")]
    public bool Ok { get; init; }
}

/// <summary><c>/console/commands</c>. Lists the registered console commands.</summary>
public sealed record ConsoleCommandsRequest
{
    [JsonPropertyName("contains")]
    public string? Contains { get; init; }
}

/// <summary>
///     Each entry is <c>"&lt;name&gt; (&lt;HandlerTypeName&gt;)"</c>. On a throw this
///     answers <c>ok:false</c> at HTTP <b>200</b>, not 409.
/// </summary>
public sealed record ConsoleCommandsResponse : IWireResult
{
    [JsonPropertyName("ok")]
    public bool Ok { get; init; }

    [JsonPropertyName("error")]
    public string? Error { get; init; }

    [JsonPropertyName("count")]
    public int Count { get; init; }

    [JsonPropertyName("commands")]
    public string[]? Commands { get; init; }
}
