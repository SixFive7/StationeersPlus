using System.Text.Json.Serialization;

namespace TestRig.Contracts;

// /diag/input, /diag/join, /screenshot.

/// <summary><c>/diag/input</c>. No parameters.</summary>
public sealed record DiagInputRequest;

/// <summary>
///     Why synthetic input did or did not land: which patches are installed, how often
///     each link in the call chain ran, whether the gameplay gate is open, and whether
///     this process is even on the input desktop.
/// </summary>
public sealed record DiagInputResponse : IWireResult
{
    [JsonPropertyName("ok")]
    public bool Ok { get; init; }

    [JsonPropertyName("epoch")]
    public EpochBlock? Epoch { get; init; }

    [JsonPropertyName("instance")]
    public string? Instance { get; init; }

    [JsonPropertyName("frame")]
    public int Frame { get; init; }

    [JsonPropertyName("patches")]
    public InputPatchesBlock? Patches { get; init; }

    [JsonPropertyName("chain")]
    public ChainBlock? Chain { get; init; }

    [JsonPropertyName("gate")]
    public InputGateBlock? Gate { get; init; }

    [JsonPropertyName("window")]
    public WindowBlock? Window { get; init; }

    [JsonPropertyName("foreground")]
    public ForegroundBlock? Foreground { get; init; }

    [JsonPropertyName("newScrollData")]
    public double NewScrollData { get; init; }

    [JsonPropertyName("keyOverrides")]
    public long KeyOverrides { get; init; }

    [JsonPropertyName("scrollOverrides")]
    public long ScrollOverrides { get; init; }

    /// <summary>A comma-joined string, not an array.</summary>
    [JsonPropertyName("heldKeys")]
    public string? HeldKeys { get; init; }
}

/// <summary><c>/diag/join</c>. No parameters.</summary>
public sealed record DiagJoinRequest;

/// <summary>
///     Why a join did or did not work: the RakNet peer's own state, the network settings
///     that were in force, and whatever the join recorder captured.
/// </summary>
public sealed record DiagJoinResponse : IWireResult
{
    [JsonPropertyName("ok")]
    public bool Ok { get; init; }

    [JsonPropertyName("epoch")]
    public EpochBlock? Epoch { get; init; }

    [JsonPropertyName("state")]
    public string? State { get; init; }

    [JsonPropertyName("peer")]
    public PeerProbeBlock? Peer { get; init; }

    [JsonPropertyName("useSteamP2P")]
    public bool UseSteamP2P { get; init; }

    [JsonPropertyName("localIpAddress")]
    public string? LocalIpAddress { get; init; }

    /// <summary>A <b>string</b> in the game's own settings, not a number.</summary>
    [JsonPropertyName("gamePort")]
    public string? GamePort { get; init; }

    [JsonPropertyName("isNewTutorial")]
    public bool IsNewTutorial { get; init; }

    [JsonPropertyName("serverAddress")]
    public string? ServerAddress { get; init; }

    /// <summary>A <b>string</b>.</summary>
    [JsonPropertyName("serverPort")]
    public string? ServerPort { get; init; }

    [JsonPropertyName("connectionMethod")]
    public string? ConnectionMethod { get; init; }

    [JsonPropertyName("joinTrace")]
    public JoinTraceBlock? JoinTrace { get; init; }

    [JsonPropertyName("note")]
    public string? Note { get; init; }
}

/// <summary>
///     <c>/screenshot</c>. The capture includes overlay UI, which means it needs a real
///     backbuffer: a headless or fully occluded process cannot answer.
/// </summary>
public sealed record ScreenshotRequest
{
    /// <summary>Unity supersampling factor. Minimum 1.</summary>
    [JsonPropertyName("supersize")]
    public int? Supersize { get; init; }

    /// <summary>Downscale ceiling. Defaults to 1920; zero disables the downscale.</summary>
    [JsonPropertyName("maxWidth")]
    public int? MaxWidth { get; init; }

    [JsonPropertyName("timeoutMs")]
    public int? TimeoutMs { get; init; }

    /// <summary>
    ///     Write the PNG here instead of returning it. Send this as a query parameter: a
    ///     JSON body decodes backslash escapes and a Windows path does not survive it.
    /// </summary>
    [JsonPropertyName("path")]
    public string? Path { get; init; }

    /// <summary>
    ///     Defaults to true when <see cref="Path"/> is empty. True returns raw
    ///     <c>image/png</c> bytes at 200, the only non-JSON success in the whole API, so a
    ///     caller must branch on the response content type before trying to parse
    ///     <see cref="ScreenshotResponse"/>.
    /// </summary>
    [JsonPropertyName("inline")]
    public bool? Inline { get; init; }
}

/// <summary>
///     The JSON shape, returned only when the image was written to disk rather than
///     inlined. An inline capture answers <c>image/png</c> and has no JSON body at all.
/// </summary>
public sealed record ScreenshotResponse : IWireResult
{
    [JsonPropertyName("ok")]
    public bool Ok { get; init; }

    [JsonPropertyName("instance")]
    public string? Instance { get; init; }

    [JsonPropertyName("path")]
    public string? Path { get; init; }

    [JsonPropertyName("bytes")]
    public int Bytes { get; init; }

    [JsonPropertyName("width")]
    public int Width { get; init; }

    [JsonPropertyName("height")]
    public int Height { get; init; }
}
