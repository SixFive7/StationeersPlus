using System.Text.Json.Serialization;

namespace TestRig.Contracts;

// /connect, /host, /disconnect, /quit, /waitfor.
//
// These are the long-running routes. None of them is wrapped on the Unity main thread, so
// none of them can answer 504; they answer 200 on success and 409 on a refusal or an
// unmet assertion, and the 409 body carries the forensics captured BEFORE cleanup wiped
// them.

/// <summary><c>/connect</c>. Join a server.</summary>
public sealed record ConnectRequest
{
    [JsonPropertyName("address")]
    public string? Address { get; init; }

    /// <summary>Defaults to 28016, the dedicated server's port. A listen host is 27800 plus the instance index.</summary>
    [JsonPropertyName("port")]
    public int? Port { get; init; }

    [JsonPropertyName("wait")]
    public bool? Wait { get; init; }

    [JsonPropertyName("timeoutMs")]
    public int? TimeoutMs { get; init; }

    /// <summary>Defaults to true, which calls <c>NetworkClient.StopConnectionTimer()</c>.</summary>
    [JsonPropertyName("suppressTimeout")]
    public bool? SuppressTimeout { get; init; }

    /// <summary>
    ///     Overrides the duplicate-identity refusal. Two instances sharing a ClientId
    ///     resolve onto one Brain on the server, and the second joiner takes over the
    ///     first joiner's character.
    /// </summary>
    [JsonPropertyName("allowDuplicateIdentity")]
    public bool? AllowDuplicateIdentity { get; init; }

    [JsonPropertyName("localIpAddress")]
    public string? LocalIpAddress { get; init; }
}

/// <summary>
///     One record covering all three shapes <c>/connect</c> emits: the <c>wait=false</c>
///     acknowledgement, the resolved outcome, and the duplicate-identity refusal.
/// </summary>
/// <remarks>
///     The fake produced only <c>{ok, result}</c> (divergences D-20 through D-23), so
///     neither <see cref="Target"/> nor the refusal shape nor the failure forensics were
///     ever exercised. A refusal misclassified as a first-attempt flake gets retried three
///     times and then reported under the wrong diagnosis.
/// </remarks>
public sealed record ConnectResponse : IWireResult
{
    [JsonPropertyName("ok")]
    public bool Ok { get; init; }

    /// <summary><c>"&lt;addr&gt;:&lt;port&gt;"</c>. Proof that the resolved address is the one that was dialled.</summary>
    [JsonPropertyName("target")]
    public string? Target { get; init; }

    /// <summary><c>connected</c>, <c>failed</c> or <c>timeout</c>. Absent on the <c>wait=false</c> shape.</summary>
    [JsonPropertyName("result")]
    public string? Result { get; init; }

    /// <summary>Present only on the <c>wait=false</c> shape.</summary>
    [JsonPropertyName("waiting")]
    public bool? Waiting { get; init; }

    /// <summary>Present only on the <c>wait=false</c> shape.</summary>
    [JsonPropertyName("timerSuppressed")]
    public bool? TimerSuppressed { get; init; }

    /// <summary>Set on the duplicate-identity refusal and on any other 409.</summary>
    [JsonPropertyName("error")]
    public string? Error { get; init; }

    /// <summary>Set on the duplicate-identity refusal.</summary>
    [JsonPropertyName("peers")]
    public PeersBlock? Peers { get; init; }

    /// <summary>The literal advice string on a duplicate-identity refusal.</summary>
    [JsonPropertyName("override")]
    public string? Override { get; init; }

    /// <summary>The modal that was up when the attempt failed, in <c>/modal</c>'s shape.</summary>
    [JsonPropertyName("dialog")]
    public ModalBlock? Dialog { get; init; }

    [JsonPropertyName("stateAtFailure")]
    public string? StateAtFailure { get; init; }

    [JsonPropertyName("peerAtFailure")]
    public PeerProbeBlock? PeerAtFailure { get; init; }

    [JsonPropertyName("statusAtFailure")]
    public StatusResponse? StatusAtFailure { get; init; }

    [JsonPropertyName("joinTrace")]
    public JoinTraceBlock? JoinTrace { get; init; }

    /// <summary>Carries the known "a first attempt after a server restart often fails" note.</summary>
    [JsonPropertyName("hint")]
    public string? Hint { get; init; }

    [JsonPropertyName("status")]
    public StatusResponse? Status { get; init; }
}

/// <summary>
///     <c>/host</c>. Turn this instance into a listen host: one process that runs the
///     simulation, accepts joiners over loopback, and plays a character.
/// </summary>
public sealed record HostRequest
{
    /// <summary>Load an existing save. Exactly one of this and <see cref="World"/>; 400 otherwise.</summary>
    [JsonPropertyName("save")]
    public string? Save { get; init; }

    /// <summary>
    ///     Create a new world. Ids: <c>Lunar</c>, <c>Mars2</c>, <c>Europa3</c>,
    ///     <c>MimasHerschel</c>, <c>Venus</c>, <c>Vulcan2</c>.
    /// </summary>
    [JsonPropertyName("world")]
    public string? World { get; init; }

    [JsonPropertyName("difficulty")]
    public string? Difficulty { get; init; }

    [JsonPropertyName("start")]
    public string? Start { get; init; }

    /// <summary>1 to 65535. 400 outside that.</summary>
    [JsonPropertyName("port")]
    public int? Port { get; init; }

    [JsonPropertyName("serverName")]
    public string? ServerName { get; init; }

    [JsonPropertyName("password")]
    public string? Password { get; init; }

    /// <summary>At least 1. 400 below that.</summary>
    [JsonPropertyName("maxPlayers")]
    public int? MaxPlayers { get; init; }

    [JsonPropertyName("localIpAddress")]
    public string? LocalIpAddress { get; init; }

    [JsonPropertyName("wait")]
    public bool? Wait { get; init; }

    [JsonPropertyName("timeoutMs")]
    public int? TimeoutMs { get; init; }

    [JsonPropertyName("allowDuplicateIdentity")]
    public bool? AllowDuplicateIdentity { get; init; }

    /// <summary>
    ///     Defaults to true and must stay that way. False writes a world into the
    ///     developer's own save tree, which is tier 1 and off limits to the rig. There is
    ///     no correct reason to pass false.
    /// </summary>
    [JsonPropertyName("requireIsolatedSavePath")]
    public bool? RequireIsolatedSavePath { get; init; }
}

/// <summary>
///     One record covering all four shapes <c>/host</c> emits: success, the
///     <c>wait=false</c> acknowledgement, the stage-1 failure (the world never ran) and
///     the stage-2 failure (the world is up but nothing is hosting).
/// </summary>
/// <remarks>
///     Hosting is asserted in two stages because they fail differently: stage 1 is the
///     world reaching GameState.Running, stage 2 is a 15 second poll for
///     <c>NetworkServer.IsHosting</c> and <c>role == "listenHost"</c>. The fake answered
///     <c>{ok:true, hostPort}</c> unconditionally (divergences D-17 through D-19), so
///     neither 409 shape has ever been exercised.
/// </remarks>
public sealed record HostResponse : IWireResult
{
    [JsonPropertyName("ok")]
    public bool Ok { get; init; }

    /// <summary><c>listenHost</c> on success. On a stage-2 failure this is whatever it actually is.</summary>
    [JsonPropertyName("role")]
    public string? Role { get; init; }

    [JsonPropertyName("hosting")]
    public bool? Hosting { get; init; }

    /// <summary>Present only on the <c>wait=false</c> shape.</summary>
    [JsonPropertyName("waiting")]
    public bool? Waiting { get; init; }

    [JsonPropertyName("hostPort")]
    public int? HostPort { get; init; }

    /// <summary>Present on a stage-2 failure: the port that was asked for, versus what is live.</summary>
    [JsonPropertyName("requestedPort")]
    public int? RequestedPort { get; init; }

    [JsonPropertyName("serverName")]
    public string? ServerName { get; init; }

    [JsonPropertyName("hasPassword")]
    public bool? HasPassword { get; init; }

    [JsonPropertyName("world")]
    public string? World { get; init; }

    [JsonPropertyName("save")]
    public string? Save { get; init; }

    [JsonPropertyName("savePath")]
    public string? SavePath { get; init; }

    /// <summary><c>instance</c> or <c>default</c>. Anything but <c>instance</c> is worth a second look.</summary>
    [JsonPropertyName("saveRoot")]
    public string? SaveRoot { get; init; }

    /// <summary>A <b>string</b>, for the 2^53 reason.</summary>
    [JsonPropertyName("localClientId")]
    public string? LocalClientId { get; init; }

    [JsonPropertyName("username")]
    public string? Username { get; init; }

    [JsonPropertyName("playersInGame")]
    public int? PlayersInGame { get; init; }

    [JsonPropertyName("connectedClients")]
    public ConnectedClient[]? ConnectedClients { get; init; }

    /// <summary>A ready-made <c>/connect</c> target for a joiner.</summary>
    [JsonPropertyName("joinWith")]
    public string? JoinWith { get; init; }

    /// <summary><c>failed</c>, <c>timeout</c> or <c>notHosting</c>. Present only on a failure.</summary>
    [JsonPropertyName("result")]
    public string? Result { get; init; }

    [JsonPropertyName("error")]
    public string? Error { get; init; }

    [JsonPropertyName("dialog")]
    public ModalBlock? Dialog { get; init; }

    [JsonPropertyName("hint")]
    public string? Hint { get; init; }

    /// <summary>Bare strings, not <see cref="ConsoleLine"/> rows. This one really is an array of text.</summary>
    [JsonPropertyName("consoleTail")]
    public string[]? ConsoleTail { get; init; }

    [JsonPropertyName("note")]
    public string? Note { get; init; }

    [JsonPropertyName("status")]
    public StatusResponse? Status { get; init; }
}

/// <summary><c>/disconnect</c>. Back to the main menu.</summary>
public sealed record DisconnectRequest
{
    [JsonPropertyName("wait")]
    public bool? Wait { get; init; }

    [JsonPropertyName("timeoutMs")]
    public int? TimeoutMs { get; init; }
}

/// <summary><c>{ok:(result=="menu"), result}</c>, at 200 or 409.</summary>
public sealed record DisconnectResponse : IWireResult
{
    [JsonPropertyName("ok")]
    public bool Ok { get; init; }

    /// <summary><c>menu</c> or <c>timeout</c>.</summary>
    [JsonPropertyName("result")]
    public string? Result { get; init; }
}

/// <summary><c>/quit</c>. Ends the process.</summary>
public sealed record QuitRequest
{
    /// <summary>False is <c>Application.Quit()</c>. True is <c>GameManager.QuitGame()</c>.</summary>
    [JsonPropertyName("hard")]
    public bool? Hard { get; init; }
}

/// <summary>Answers 200 <b>before</b> the process dies, because the quit is posted rather than run inline.</summary>
public sealed record QuitResponse : IWireResult
{
    [JsonPropertyName("ok")]
    public bool Ok { get; init; }

    [JsonPropertyName("hard")]
    public bool Hard { get; init; }
}

/// <summary><c>/waitfor</c>. Block until the game reaches a phase.</summary>
public sealed record WaitForRequest
{
    /// <summary>
    ///     A phase name (<c>menu</c>, <c>joining</c>, <c>loading</c>, <c>waiting</c>,
    ///     <c>paused</c>, <c>inWorld</c>) or a raw GameState name. Defaults to
    ///     <c>inWorld</c>.
    /// </summary>
    [JsonPropertyName("phase")]
    public string? Phase { get; init; }

    [JsonPropertyName("timeoutMs")]
    public int? TimeoutMs { get; init; }
}

/// <summary>200 when the phase arrived, 409 on timeout.</summary>
public sealed record WaitForResponse : IWireResult
{
    [JsonPropertyName("ok")]
    public bool Ok { get; init; }

    [JsonPropertyName("wanted")]
    public string? Wanted { get; init; }

    /// <summary>The phase actually reached.</summary>
    [JsonPropertyName("result")]
    public string? Result { get; init; }
}
