using System.Text.Json.Serialization;

namespace TestRig.Contracts;

/// <summary>
///     The <c>instance</c> block from the manifest, carried by <c>/status</c> and
///     <c>/instance</c>.
/// </summary>
public sealed record InstanceBlock
{
    /// <summary>Manifest name, or the literal <c>(unnamed)</c>.</summary>
    [JsonPropertyName("name")]
    public string? Name { get; init; }

    [JsonPropertyName("port")]
    public int Port { get; init; }

    /// <summary>Advisory only: <c>client</c> or <c>host</c>. Nothing enforces it.</summary>
    [JsonPropertyName("role")]
    public string? Role { get; init; }

    [JsonPropertyName("gamePort")]
    public int GamePort { get; init; }

    /// <summary>
    ///     A <b>string</b>, never a number. A ClientId is above 2^53 and a JSON number
    ///     parsed through double loses precision there, so the plugin renders it as text.
    ///     Note that <c>/status.localClientId</c> is emitted numerically instead; the two
    ///     really do differ and this assembly reproduces both rather than harmonising them.
    /// </summary>
    [JsonPropertyName("clientId")]
    public string? ClientId { get; init; }

    [JsonPropertyName("username")]
    public string? Username { get; init; }

    [JsonPropertyName("manifestLoaded")]
    public bool ManifestLoaded { get; init; }

    [JsonPropertyName("manifestPath")]
    public string? ManifestPath { get; init; }

    [JsonPropertyName("manifestError")]
    public string? ManifestError { get; init; }

    /// <summary>The Win32 desktop the instance was launched onto.</summary>
    [JsonPropertyName("desktop")]
    public string? Desktop { get; init; }

    [JsonPropertyName("rigRoot")]
    public string? RigRoot { get; init; }

    /// <summary>Reported by the manifest, applied by nothing. Read <c>/status.savePathResolved</c> for the live value.</summary>
    [JsonPropertyName("savePath")]
    public string? SavePath { get; init; }

    /// <summary>
    ///     Key to <c>manifest</c>, <c>config</c> or <c>default</c>, for <c>port</c>,
    ///     <c>role</c>, <c>gamePort</c>, <c>window</c>, <c>gameplayInput</c> and
    ///     <c>identity</c>.
    /// </summary>
    [JsonPropertyName("valueSources")]
    public Dictionary<string, string>? ValueSources { get; init; }

    [JsonPropertyName("peerPorts")]
    public int[]? PeerPorts { get; init; }
}

/// <summary>
///     One peer instance seen by <c>PeerProbe</c>. A conflict here is the duplicate-identity
///     condition that <c>/connect</c> and <c>/host</c> refuse on.
/// </summary>
public sealed record PeerRow
{
    [JsonPropertyName("port")]
    public int Port { get; init; }

    [JsonPropertyName("reachable")]
    public bool Reachable { get; init; }

    [JsonPropertyName("name")]
    public string? Name { get; init; }

    /// <summary>String, for the same 2^53 reason as <see cref="InstanceBlock.ClientId"/>.</summary>
    [JsonPropertyName("clientId")]
    public string? ClientId { get; init; }

    [JsonPropertyName("conflicts")]
    public bool Conflicts { get; init; }

    [JsonPropertyName("error")]
    public string? Error { get; init; }
}

/// <summary>The <c>peers</c> block, and the payload of a duplicate-identity refusal.</summary>
public sealed record PeersBlock
{
    [JsonPropertyName("conflictDetected")]
    public bool ConflictDetected { get; init; }

    [JsonPropertyName("conflict")]
    public string? Conflict { get; init; }

    [JsonPropertyName("lastScanUtc")]
    public string? LastScanUtc { get; init; }

    [JsonPropertyName("peers")]
    public PeerRow[]? Peers { get; init; }

    [JsonPropertyName("peerCount")]
    public int PeerCount { get; init; }
}
