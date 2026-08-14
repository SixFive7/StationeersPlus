using System.Text.Json.Serialization;

namespace TestRig.Contracts;

// The observation endpoints: /, /help, /ping, /instance, /identity, /status, /colors,
// /plugins.
//
// Every request record here is a body OR a query string; the router merges the two and
// the body wins on a collision, so nothing in this file records an HTTP method.

/// <summary><c>/</c> and <c>/help</c>. No parameters.</summary>
public sealed record HelpRequest;

/// <summary>The endpoint catalogue as the plugin itself publishes it.</summary>
public sealed record HelpResponse : IWireResult
{
    [JsonPropertyName("ok")]
    public bool Ok { get; init; }

    /// <summary>Name and version together, for example <c>ClientDriver 0.2.0</c>.</summary>
    [JsonPropertyName("plugin")]
    public string? Plugin { get; init; }

    [JsonPropertyName("instance")]
    public string? Instance { get; init; }

    [JsonPropertyName("note")]
    public string? Note { get; init; }

    [JsonPropertyName("inputContract")]
    public string? InputContract { get; init; }

    [JsonPropertyName("roleContract")]
    public string? RoleContract { get; init; }

    [JsonPropertyName("epochContract")]
    public string? EpochContract { get; init; }

    [JsonPropertyName("authorityContract")]
    public string? AuthorityContract { get; init; }

    [JsonPropertyName("epoch")]
    public EpochBlock? Epoch { get; init; }

    /// <summary>
    ///     The hand-maintained catalogue in <c>Routes/Help.cs</c>, with blank strings
    ///     acting as section separators. It is prose, not a machine list. Use
    ///     <see cref="Endpoints.All"/> for anything that has to be right.
    /// </summary>
    [JsonPropertyName("endpoints")]
    public string[]? Endpoints { get; init; }
}

/// <summary><c>/ping</c>. No parameters.</summary>
public sealed record PingRequest;

/// <summary>
///     Liveness. This route never touches the Unity main thread, so it answers while the
///     game is wedged, which is what makes it different from <c>/status</c>.
/// </summary>
public sealed record PingResponse : IWireResult
{
    [JsonPropertyName("ok")]
    public bool Ok { get; init; }

    [JsonPropertyName("plugin")]
    public string? Plugin { get; init; }

    [JsonPropertyName("version")]
    public string? Version { get; init; }

    [JsonPropertyName("instance")]
    public string? Instance { get; init; }

    [JsonPropertyName("port")]
    public int Port { get; init; }

    [JsonPropertyName("pumpFrames")]
    public long PumpFrames { get; init; }

    [JsonPropertyName("frame")]
    public int Frame { get; init; }

    /// <summary>False means the main-thread pump has stopped and every wrapped route will time out at 504.</summary>
    [JsonPropertyName("pumpAlive")]
    public bool PumpAlive { get; init; }

    [JsonPropertyName("epoch")]
    public EpochBlock? Epoch { get; init; }
}

/// <summary><c>/instance</c>.</summary>
public sealed record InstanceRequest
{
    /// <summary>
    ///     True runs the peer scan synchronously on the HTTP thread, 1500 ms per peer.
    ///     False (the default) uses the async scan behind a 15 second cache.
    /// </summary>
    [JsonPropertyName("rescan")]
    public bool? Rescan { get; init; }
}

/// <summary>Instance identity plus what the peer scan last saw.</summary>
public sealed record InstanceResponse : IWireResult
{
    [JsonPropertyName("ok")]
    public bool Ok { get; init; }

    [JsonPropertyName("instance")]
    public InstanceBlock? Instance { get; init; }

    [JsonPropertyName("peers")]
    public PeersBlock? Peers { get; init; }

    /// <summary>String, for the 2^53 reason. See <see cref="InstanceBlock.ClientId"/>.</summary>
    [JsonPropertyName("effectiveClientId")]
    public string? EffectiveClientId { get; init; }

    [JsonPropertyName("effectiveUsername")]
    public string? EffectiveUsername { get; init; }
}

/// <summary>
///     <c>/identity</c>. With neither field this is a read; with either, it rewrites the
///     live cookie and re-scans peers.
/// </summary>
public sealed record IdentityRequest
{
    /// <summary>
    ///     A decimal ulong, as text. 400 on anything else, and 400 on zero: zero is the
    ///     batch-mode sentinel.
    /// </summary>
    [JsonPropertyName("clientId")]
    public string? ClientId { get; init; }

    [JsonPropertyName("username")]
    public string? Username { get; init; }
}

/// <summary>The live identity cookie and whether this plugin's override took.</summary>
public sealed record IdentityResponse : IWireResult
{
    [JsonPropertyName("ok")]
    public bool Ok { get; init; }

    [JsonPropertyName("instanceName")]
    public string? InstanceName { get; init; }

    /// <summary>A <b>string</b> here, unlike the numeric <c>/status.localClientId</c>.</summary>
    [JsonPropertyName("localClientId")]
    public string? LocalClientId { get; init; }

    [JsonPropertyName("username")]
    public string? Username { get; init; }

    [JsonPropertyName("cookiePresent")]
    public bool CookiePresent { get; init; }

    /// <summary>A <b>string</b>.</summary>
    [JsonPropertyName("overrideClientId")]
    public string? OverrideClientId { get; init; }

    [JsonPropertyName("overrideUsername")]
    public string? OverrideUsername { get; init; }

    [JsonPropertyName("overrideApplied")]
    public bool OverrideApplied { get; init; }

    [JsonPropertyName("applyCount")]
    public long ApplyCount { get; init; }

    [JsonPropertyName("suppressedCookieSaves")]
    public long SuppressedCookieSaves { get; init; }

    /// <summary>
    ///     True when another instance on a peer port claims the same id. Both instances
    ///     would resolve onto one Brain on the server, and the second joiner would take
    ///     over the first joiner's character.
    /// </summary>
    [JsonPropertyName("duplicateIdentity")]
    public bool DuplicateIdentity { get; init; }

    [JsonPropertyName("duplicateIdentityDetail")]
    public string? DuplicateIdentityDetail { get; init; }

    [JsonPropertyName("lastError")]
    public string? LastError { get; init; }
}

/// <summary><c>/status</c>. No parameters.</summary>
public sealed record StatusRequest;

/// <summary>
///     Everything the plugin can say about the process in one read.
/// </summary>
/// <remarks>
///     <para>
///     <b>Assert on <see cref="Role"/>, never on <see cref="IsClient"/> or
///     <see cref="IsServer"/>.</b> A listen host is <c>NetworkRole.Server</c> and
///     therefore reports <c>isClient=false</c>. The role is computed in exactly one place:
///     server and batch mode is <c>dedicated</c>, server otherwise is <c>listenHost</c>,
///     client is <c>joinedClient</c>, and the rest is <c>singlePlayer</c> or <c>menu</c>
///     by GameState.
///     </para>
///     <para>
///     The PowerShell fake emitted nine of these roughly fifty fields (divergences D-07
///     through D-16) and omitted <c>isClient</c>/<c>isServer</c> entirely, so no test could
///     prove the harness avoided the trap the rules warn about.
///     </para>
/// </remarks>
public sealed record StatusResponse : IWireResult
{
    [JsonPropertyName("ok")]
    public bool Ok { get; init; }

    [JsonPropertyName("plugin")]
    public string? Plugin { get; init; }

    [JsonPropertyName("epoch")]
    public EpochBlock? Epoch { get; init; }

    [JsonPropertyName("instanceName")]
    public string? InstanceName { get; init; }

    [JsonPropertyName("port")]
    public int Port { get; init; }

    [JsonPropertyName("instance")]
    public InstanceBlock? Instance { get; init; }

    [JsonPropertyName("frame")]
    public int Frame { get; init; }

    [JsonPropertyName("realtime")]
    public double Realtime { get; init; }

    [JsonPropertyName("gameState")]
    public string? GameState { get; init; }

    [JsonPropertyName("phase")]
    public string? Phase { get; init; }

    [JsonPropertyName("gameInitialized")]
    public bool? GameInitialized { get; init; }

    /// <summary>Absent when reading it threw.</summary>
    [JsonPropertyName("batchMode")]
    public bool? BatchMode { get; init; }

    [JsonPropertyName("runSimulation")]
    public bool RunSimulation { get; init; }

    [JsonPropertyName("gameVersion")]
    public string? GameVersion { get; init; }

    /// <summary><c>menu</c>, <c>singlePlayer</c>, <c>joinedClient</c>, <c>listenHost</c>, <c>dedicated</c> or <c>unknown</c>.</summary>
    [JsonPropertyName("role")]
    public string? Role { get; init; }

    [JsonPropertyName("networkRole")]
    public string? NetworkRole { get; init; }

    [JsonPropertyName("networkState")]
    public string? NetworkState { get; init; }

    /// <summary>A listen host reports false here. Read <see cref="Role"/> instead.</summary>
    [JsonPropertyName("isClient")]
    public bool IsClient { get; init; }

    [JsonPropertyName("isServer")]
    public bool IsServer { get; init; }

    [JsonPropertyName("isActive")]
    public bool IsActive { get; init; }

    /// <summary>
    ///     Emitted as a JSON <b>number</b> here, and as a string on <c>/identity</c>,
    ///     <c>/instance</c> and every roster row. That asymmetry is real: this field is the
    ///     one that loses precision above 2^53, so prefer <c>/instance.clientId</c> when
    ///     the exact value matters.
    /// </summary>
    [JsonPropertyName("localClientId")]
    public long LocalClientId { get; init; }

    [JsonPropertyName("username")]
    public string? Username { get; init; }

    /// <summary><c>NetworkManager.TotalPlayersInGame</c>. A host counts itself.</summary>
    [JsonPropertyName("playersInGame")]
    public int PlayersInGame { get; init; }

    /// <summary>Present only when the network block threw.</summary>
    [JsonPropertyName("networkError")]
    public string? NetworkError { get; init; }

    [JsonPropertyName("hosting")]
    public bool Hosting { get; init; }

    /// <summary>0 when not hosting.</summary>
    [JsonPropertyName("hostPort")]
    public int HostPort { get; init; }

    /// <summary>Empty on anything that is not a server. The roster is the server's answer.</summary>
    [JsonPropertyName("connectedClients")]
    public ConnectedClient[]? ConnectedClients { get; init; }

    [JsonPropertyName("settingsPath")]
    public string? SettingsPath { get; init; }

    /// <summary>The live save root, after any <c>/savepath</c> redirect.</summary>
    [JsonPropertyName("savePathResolved")]
    public string? SavePathResolved { get; init; }

    /// <summary>
    ///     False means this instance would write a world inside the developer's real
    ///     user-data folder, which is tier 1 and off limits. <c>/host</c> refuses on it.
    /// </summary>
    [JsonPropertyName("saveRootIsolated")]
    public bool SaveRootIsolated { get; init; }

    /// <summary>
    ///     Read from <c>setting.xml</c> on disk by string scan, so it answers whether the
    ///     instance will host again on its next boot. Null when the file could not be read.
    /// </summary>
    [JsonPropertyName("startLocalHostPersisted")]
    public bool? StartLocalHostPersisted { get; init; }

    [JsonPropertyName("startLocalHostInMemory")]
    public bool StartLocalHostInMemory { get; init; }

    [JsonPropertyName("serverAddress")]
    public string? ServerAddress { get; init; }

    [JsonPropertyName("serverPort")]
    public string? ServerPort { get; init; }

    [JsonPropertyName("connectionMethod")]
    public string? ConnectionMethod { get; init; }

    [JsonPropertyName("worldName")]
    public string? WorldName { get; init; }

    [JsonPropertyName("worldId")]
    public string? WorldId { get; init; }

    [JsonPropertyName("worldPaused")]
    public bool WorldPaused { get; init; }

    [JsonPropertyName("worldInitialized")]
    public bool WorldInitialized { get; init; }

    /// <summary>Assembly-scan count. A value of 2 means StationeersLaunchPad failed to load anything.</summary>
    [JsonPropertyName("loadedPluginCount")]
    public int LoadedPluginCount { get; init; }

    [JsonPropertyName("consoleOpen")]
    public bool ConsoleOpen { get; init; }

    [JsonPropertyName("cursorVisible")]
    public bool CursorVisible { get; init; }

    [JsonPropertyName("foreground")]
    public ForegroundBlock? Foreground { get; init; }

    [JsonPropertyName("appFocused")]
    public bool? AppFocused { get; init; }

    /// <summary><c>Application.isFocused</c>. Documented unreliable.</summary>
    [JsonPropertyName("unityIsFocused")]
    public bool UnityIsFocused { get; init; }

    /// <summary>The gate that decides whether synthetic input does anything at all.</summary>
    [JsonPropertyName("gameplayInputGateOpen")]
    public bool GameplayInputGateOpen { get; init; }

    [JsonPropertyName("gameplayInputShutReason")]
    public string? GameplayInputShutReason { get; init; }

    [JsonPropertyName("player")]
    public PlayerBlock? Player { get; init; }

    [JsonPropertyName("driver")]
    public DriverBlock? Driver { get; init; }
}

/// <summary><c>/colors</c>. No parameters.</summary>
public sealed record ColorsRequest;

/// <summary>One vanilla or mod-registered ColorSwatch.</summary>
public sealed record ColorRow
{
    /// <summary>Position in <c>GameManager.Instance.CustomColors</c>.</summary>
    [JsonPropertyName("index")]
    public int Index { get; init; }

    /// <summary>The swatch's own <c>Index</c> field, which is what a paint comparison uses.</summary>
    [JsonPropertyName("swatchIndex")]
    public int SwatchIndex { get; init; }

    [JsonPropertyName("name")]
    public string? Name { get; init; }

    [JsonPropertyName("normalMaterial")]
    public string? NormalMaterial { get; init; }
}

/// <summary>The colour palette. <c>ok:false</c> arrives at HTTP 200 on a throw.</summary>
public sealed record ColorsResponse : IWireResult
{
    [JsonPropertyName("ok")]
    public bool Ok { get; init; }

    [JsonPropertyName("error")]
    public string? Error { get; init; }

    [JsonPropertyName("colors")]
    public ColorRow[]? Colors { get; init; }

    [JsonPropertyName("count")]
    public int Count { get; init; }
}

/// <summary><c>/plugins</c>. No parameters.</summary>
public sealed record PluginsRequest;

/// <summary>One loaded BepInEx plugin.</summary>
public sealed record PluginRow
{
    [JsonPropertyName("guid")]
    public string? Guid { get; init; }

    [JsonPropertyName("name")]
    public string? Name { get; init; }

    [JsonPropertyName("version")]
    public string? Version { get; init; }

    [JsonPropertyName("type")]
    public string? Type { get; init; }

    [JsonPropertyName("assembly")]
    public string? Assembly { get; init; }

    /// <summary>False means the type was found by assembly scan but BepInEx never chainloaded it.</summary>
    [JsonPropertyName("chainloaded")]
    public bool Chainloaded { get; init; }
}

/// <summary>What is actually loaded. <c>ok:false</c> arrives at HTTP 200 on a throw.</summary>
public sealed record PluginsResponse : IWireResult
{
    [JsonPropertyName("ok")]
    public bool Ok { get; init; }

    [JsonPropertyName("error")]
    public string? Error { get; init; }

    [JsonPropertyName("plugins")]
    public PluginRow[]? Plugins { get; init; }

    [JsonPropertyName("count")]
    public int Count { get; init; }

    [JsonPropertyName("chainloadedCount")]
    public int ChainloadedCount { get; init; }
}
