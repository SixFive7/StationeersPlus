using System.Text.Json.Serialization;

namespace TestRig.Contracts;

// /saves, /save, /savepath, /load, /newworld, /nearby, /modal, /modal/click,
// /modsettings, /modsettings/list.

/// <summary><c>/saves</c>. No parameters.</summary>
public sealed record SavesRequest;

/// <summary>
///     The local save list. Each row is every public field and property of the game's own
///     save entry type, rendered as text, so the shape follows the game rather than this
///     assembly. On a throw this answers <c>ok:false</c> at HTTP <b>200</b>.
/// </summary>
public sealed record SavesResponse : IWireResult
{
    [JsonPropertyName("ok")]
    public bool Ok { get; init; }

    [JsonPropertyName("error")]
    public string? Error { get; init; }

    [JsonPropertyName("count")]
    public int Count { get; init; }

    /// <summary>Member name to rendered value. A null member renders as a JSON null.</summary>
    [JsonPropertyName("saves")]
    public Dictionary<string, string?>[]? Saves { get; init; }
}

/// <summary><c>/save</c>. Writes the world to disk and waits for the game's own confirmation.</summary>
public sealed record SaveRequest
{
    /// <summary>Defaults to the current station name. 400 on a quote or a control character.</summary>
    [JsonPropertyName("name")]
    public string? Name { get; init; }

    [JsonPropertyName("wait")]
    public bool? Wait { get; init; }

    [JsonPropertyName("timeoutMs")]
    public int? TimeoutMs { get; init; }
}

/// <summary>
///     Whether the save was confirmed, and by what evidence.
/// </summary>
/// <remarks>
///     <see cref="Ok"/> and <see cref="Confirmed"/> are separate on purpose: the request
///     always goes through, and 409 means it went out unconfirmed. The file write-stamp is
///     the primary signal only when the console patch is not applied, because the console
///     lines are the stronger evidence when they are available.
/// </remarks>
public sealed record SaveResponse : IWireResult
{
    [JsonPropertyName("ok")]
    public bool Ok { get; init; }

    /// <summary>Always true: the command was submitted.</summary>
    [JsonPropertyName("requested")]
    public bool Requested { get; init; }

    /// <summary>The one to assert on. False with <see cref="Ok"/> false is the 409 case.</summary>
    [JsonPropertyName("confirmed")]
    public bool Confirmed { get; init; }

    /// <summary><c>console</c>, <c>file</c>, <c>failed</c> or <c>timeout</c>.</summary>
    [JsonPropertyName("result")]
    public string? Result { get; init; }

    [JsonPropertyName("name")]
    public string? Name { get; init; }

    [JsonPropertyName("resolvedName")]
    public string? ResolvedName { get; init; }

    [JsonPropertyName("saveRoot")]
    public string? SaveRoot { get; init; }

    [JsonPropertyName("savePath")]
    public string? SavePath { get; init; }

    [JsonPropertyName("sizeBytes")]
    public long SizeBytes { get; init; }

    [JsonPropertyName("lastWriteUtc")]
    public string? LastWriteUtc { get; init; }

    /// <summary><c>console</c>, <c>file</c>, or null when nothing confirmed it.</summary>
    [JsonPropertyName("confirmedBy")]
    public string? ConfirmedBy { get; init; }

    /// <summary>The <c>Starting Save for </c> or <c>Starting NewSave for </c> line, when it was seen.</summary>
    [JsonPropertyName("startedLine")]
    public string? StartedLine { get; init; }

    /// <summary>The <c>Saved &lt;name&gt;</c> or <c>Created new save</c> line.</summary>
    [JsonPropertyName("confirmLine")]
    public string? ConfirmLine { get; init; }

    /// <summary>A <c>Save Failed</c>, <c>Failed to write save file</c> or <c>Cannot save game in GameState</c> line.</summary>
    [JsonPropertyName("errorLine")]
    public string? ErrorLine { get; init; }

    [JsonPropertyName("error")]
    public string? Error { get; init; }

    [JsonPropertyName("warning")]
    public string? Warning { get; init; }

    [JsonPropertyName("consoleTail")]
    public string[]? ConsoleTail { get; init; }
}

/// <summary>
///     <c>/savepath</c>. Reads the user-data root, or redirects it.
/// </summary>
/// <remarks>
///     Safety-critical. Omitting <see cref="Path"/> is a read. Sending one is a write that
///     is refused at 409 when the resolved path is inside the developer's real user-data
///     folder, which is tier 1. <see cref="Force"/> skips that gate entirely, and there is
///     no correct reason to pass it unless the user asked for exactly that. Send the path
///     as a query parameter: a JSON body decodes <c>\b</c> and <c>\f</c>, so
///     <c>"C:\builds"</c> and <c>"C:\files"</c> do not survive a body round trip and the
///     plugin answers 400 on the resulting control character.
/// </remarks>
public sealed record SavePathRequest
{
    /// <summary>Absent means read. Present means write.</summary>
    [JsonPropertyName("path")]
    public string? Path { get; init; }

    /// <summary>Skips the tier-1 gate. Never pass this unasked.</summary>
    [JsonPropertyName("force")]
    public bool? Force { get; init; }
}

/// <summary>
///     One record for both the read and the write shape. <see cref="Previous"/> and
///     <see cref="RequestedPath"/> appear only on a write;
///     <see cref="DefaultPathRedirected"/> and <see cref="InsideRealUserData"/> only on a
///     read.
/// </summary>
public sealed record SavePathResponse : IWireResult
{
    [JsonPropertyName("ok")]
    public bool Ok { get; init; }

    [JsonPropertyName("savePath")]
    public string? SavePath { get; init; }

    /// <summary>
    ///     Computed locally as MyDocuments/My Games/Stationeers, never read from the game.
    ///     The obvious source is Harmony-patched by StationeersLaunchPad to return the
    ///     instance's own override, and comparing against that inverted both answers.
    /// </summary>
    [JsonPropertyName("realUserDataPath")]
    public string? RealUserDataPath { get; init; }

    /// <summary>What the game reports as its default. Reporting only; never the comparand.</summary>
    [JsonPropertyName("reportedDefaultPath")]
    public string? ReportedDefaultPath { get; init; }

    [JsonPropertyName("defaultPathRedirected")]
    public bool? DefaultPathRedirected { get; init; }

    [JsonPropertyName("insideRealUserData")]
    public bool? InsideRealUserData { get; init; }

    [JsonPropertyName("previous")]
    public string? Previous { get; init; }

    [JsonPropertyName("requestedPath")]
    public string? RequestedPath { get; init; }

    [JsonPropertyName("note")]
    public string? Note { get; init; }
}

/// <summary><c>/load</c>. Submits <c>load "&lt;name&gt;"</c> to the game console.</summary>
public sealed record LoadRequest
{
    /// <summary>Required. 400 <c>missing 'save'</c> otherwise.</summary>
    [JsonPropertyName("save")]
    public string? Save { get; init; }

    [JsonPropertyName("wait")]
    public bool? Wait { get; init; }

    [JsonPropertyName("timeoutMs")]
    public int? TimeoutMs { get; init; }
}

/// <summary>200 when the world loaded, 409 on timeout.</summary>
public sealed record LoadResponse : IWireResult
{
    [JsonPropertyName("ok")]
    public bool Ok { get; init; }

    [JsonPropertyName("save")]
    public string? Save { get; init; }

    /// <summary><c>loaded</c> or <c>timeout</c>.</summary>
    [JsonPropertyName("result")]
    public string? Result { get; init; }
}

/// <summary><c>/newworld</c>. Submits <c>new &lt;world&gt; &lt;difficulty&gt; &lt;start&gt;</c>.</summary>
public sealed record NewWorldRequest
{
    /// <summary>Defaults to <c>Lunar</c>. Other ids: <c>Mars2</c>, <c>Europa3</c>, <c>MimasHerschel</c>, <c>Venus</c>, <c>Vulcan2</c>.</summary>
    [JsonPropertyName("world")]
    public string? World { get; init; }

    [JsonPropertyName("difficulty")]
    public string? Difficulty { get; init; }

    [JsonPropertyName("start")]
    public string? Start { get; init; }

    [JsonPropertyName("wait")]
    public bool? Wait { get; init; }

    [JsonPropertyName("timeoutMs")]
    public int? TimeoutMs { get; init; }
}

/// <summary>200 when the world loaded, 409 on timeout, where <see cref="Note"/> carries the world-id hint.</summary>
public sealed record NewWorldResponse : IWireResult
{
    [JsonPropertyName("ok")]
    public bool Ok { get; init; }

    [JsonPropertyName("world")]
    public string? World { get; init; }

    /// <summary><c>loaded</c> or <c>timeout</c>.</summary>
    [JsonPropertyName("result")]
    public string? Result { get; init; }

    [JsonPropertyName("note")]
    public string? Note { get; init; }
}

/// <summary><c>/nearby</c>. Scan Things around the local player.</summary>
public sealed record NearbyRequest
{
    /// <summary>Metres. Defaults to 10.</summary>
    [JsonPropertyName("radius")]
    public double? Radius { get; init; }

    /// <summary>Case-insensitive; matches the runtime type name OR the prefab name.</summary>
    [JsonPropertyName("filter")]
    public string? Filter { get; init; }

    /// <summary>Defaults to 100. Zero is unlimited.</summary>
    [JsonPropertyName("limit")]
    public int? Limit { get; init; }
}

/// <summary>
///     The scan result, or <c>{ok:false, error:"no local player"}</c> at the menu.
/// </summary>
/// <remarks>
///     Row fields are <see cref="NearbyThingRow"/>, whose colour field is
///     <c>customColorIndex</c>. The PowerShell fake called it <c>colorIndex</c>
///     (divergence D-46), which reads as an absent field rather than as a wrong value,
///     and that is the hardest kind of divergence to notice. The fake also omitted
///     <c>ok</c>, <c>epoch</c>, <c>scanned</c> and <c>count</c> (D-47).
/// </remarks>
public sealed record NearbyResponse : IWireResult
{
    [JsonPropertyName("ok")]
    public bool Ok { get; init; }

    /// <summary><c>no local player</c> at the menu.</summary>
    [JsonPropertyName("error")]
    public string? Error { get; init; }

    [JsonPropertyName("epoch")]
    public EpochBlock? Epoch { get; init; }

    /// <summary>How many Things were considered before the filter and the limit.</summary>
    [JsonPropertyName("scanned")]
    public int Scanned { get; init; }

    [JsonPropertyName("count")]
    public int Count { get; init; }

    [JsonPropertyName("scanError")]
    public string? ScanError { get; init; }

    [JsonPropertyName("things")]
    public NearbyThingRow[]? Things { get; init; }
}

/// <summary><c>/modal</c>. No parameters.</summary>
public sealed record ModalRequest;

/// <summary>
///     The confirmation dialog, if any. The block is <see cref="ModalBlock"/>, the same
///     type <c>/connect</c> and <c>/host</c> splice into a failure under <c>dialog</c>.
/// </summary>
public sealed record ModalResponse : IWireResult
{
    [JsonPropertyName("ok")]
    public bool Ok { get; init; }

    [JsonPropertyName("panelActive")]
    public bool PanelActive { get; init; }

    /// <summary><c>panelActive</c> AND a non-empty data stack. Read this, not <see cref="PanelActive"/>.</summary>
    [JsonPropertyName("visible")]
    public bool Visible { get; init; }

    [JsonPropertyName("title")]
    public string? Title { get; init; }

    [JsonPropertyName("message")]
    public string? Message { get; init; }

    [JsonPropertyName("button1")]
    public string? Button1 { get; init; }

    [JsonPropertyName("button2")]
    public string? Button2 { get; init; }

    [JsonPropertyName("button3")]
    public string? Button3 { get; init; }

    [JsonPropertyName("error")]
    public string? Error { get; init; }
}

/// <summary><c>/modal/click</c>. Press a dialog button.</summary>
public sealed record ModalClickRequest
{
    /// <summary>1, 2 or 3. Defaults to 1.</summary>
    [JsonPropertyName("button")]
    public int? Button { get; init; }
}

/// <summary>
///     Always answers HTTP 200; the payload carries the failure. This is one of the
///     <c>ok:false</c> at 200 endpoints, so a status-only caller reads a failed click as a
///     successful one.
/// </summary>
public sealed record ModalClickResponse : IWireResult
{
    [JsonPropertyName("ok")]
    public bool Ok { get; init; }

    [JsonPropertyName("button")]
    public int Button { get; init; }

    [JsonPropertyName("clickedLabel")]
    public string? ClickedLabel { get; init; }

    [JsonPropertyName("hadCallback")]
    public bool HadCallback { get; init; }

    [JsonPropertyName("error")]
    public string? Error { get; init; }
}

/// <summary><c>/modsettings/list</c>. No parameters.</summary>
public sealed record ModSettingsListRequest;

/// <summary>One mod as the settings panel knows it.</summary>
public sealed record ModSettingsModRow
{
    [JsonPropertyName("name")]
    public string? Name { get; init; }

    [JsonPropertyName("id")]
    public string? Id { get; init; }

    [JsonPropertyName("author")]
    public string? Author { get; init; }

    [JsonPropertyName("version")]
    public string? Version { get; init; }
}

/// <summary><c>ok:false</c> arrives at HTTP 200.</summary>
public sealed record ModSettingsListResponse : IWireResult
{
    [JsonPropertyName("ok")]
    public bool Ok { get; init; }

    [JsonPropertyName("error")]
    public string? Error { get; init; }

    [JsonPropertyName("count")]
    public int Count { get; init; }

    [JsonPropertyName("mods")]
    public ModSettingsModRow[]? Mods { get; init; }
}

/// <summary>
///     <c>/modsettings</c>. Shows or hides a mod's settings panel. Requires
///     <c>gameInitialized</c>: the overlay renderer skips the draw while a splash or
///     loading screen is up.
/// </summary>
public sealed record ModSettingsRequest
{
    /// <summary>A Name or an Id. Also matches a Name substring.</summary>
    [JsonPropertyName("mod")]
    public string? Mod { get; init; }

    [JsonPropertyName("show")]
    public bool? Show { get; init; }
}

/// <summary><c>ok:false</c> arrives at HTTP 200.</summary>
public sealed record ModSettingsResponse : IWireResult
{
    [JsonPropertyName("ok")]
    public bool Ok { get; init; }

    [JsonPropertyName("error")]
    public string? Error { get; init; }

    [JsonPropertyName("showing")]
    public bool Showing { get; init; }

    [JsonPropertyName("mod")]
    public string? Mod { get; init; }

    [JsonPropertyName("id")]
    public string? Id { get; init; }
}
