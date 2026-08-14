using System.Text.Json.Serialization;

namespace TestRig.Contracts;

/// <summary>
///     The <c>epoch</c> block that rides most responses. A pure cache read, safe from any
///     thread.
/// </summary>
/// <remarks>
///     <para>
///     <see cref="Session"/> is the point of the block: it increments on any world or
///     network transition, so two readings that straddle a load are distinguishable. A
///     joiner arriving deliberately does not move it. Five things do: GameState,
///     NetworkRole, NetworkState, NetworkServer.IsHosting, WorldManager.CurrentWorldId.
///     </para>
///     <para>
///     The PowerShell fake emitted no epoch at all (divergence D-08), so nothing in 399
///     assertions could prove two readings came from the same world.
///     </para>
/// </remarks>
public sealed record EpochBlock
{
    [JsonPropertyName("instance")]
    public string? Instance { get; init; }

    [JsonPropertyName("port")]
    public int Port { get; init; }

    /// <summary>Monotonic, starts at 1, increments on any world or network transition.</summary>
    [JsonPropertyName("session")]
    public long Session { get; init; }

    /// <summary>
    ///     <c>menu</c>, <c>joining</c>, <c>loading</c>, <c>waiting</c>, <c>paused</c>,
    ///     <c>inWorld</c> or <c>unknown</c>. The fake only ever produced the first and
    ///     last (D-13), so no barrier that tolerates an intermediate phase was testable.
    /// </summary>
    [JsonPropertyName("phase")]
    public string? Phase { get; init; }

    [JsonPropertyName("gameState")]
    public string? GameState { get; init; }

    /// <summary>
    ///     <c>menu</c>, <c>singlePlayer</c>, <c>joinedClient</c>, <c>listenHost</c>,
    ///     <c>dedicated</c> or <c>unknown</c>. Assert on this, never on
    ///     <c>isClient</c>/<c>isServer</c>: a listen host is NetworkRole.Server and
    ///     reports <c>isClient=false</c>.
    /// </summary>
    [JsonPropertyName("role")]
    public string? Role { get; init; }

    [JsonPropertyName("networkRole")]
    public string? NetworkRole { get; init; }

    [JsonPropertyName("networkState")]
    public string? NetworkState { get; init; }

    [JsonPropertyName("hosting")]
    public bool Hosting { get; init; }

    /// <summary>0 when not hosting.</summary>
    [JsonPropertyName("hostPort")]
    public int HostPort { get; init; }

    /// <summary><c>GameManager.RunSimulation</c>.</summary>
    [JsonPropertyName("authoritative")]
    public bool Authoritative { get; init; }

    [JsonPropertyName("worldId")]
    public string? WorldId { get; init; }

    /// <summary><c>NetworkBase.Clients.Count</c>, 0 when not server.</summary>
    [JsonPropertyName("clients")]
    public int Clients { get; init; }

    [JsonPropertyName("frame")]
    public int Frame { get; init; }

    /// <summary>Wall clock, rounded to 2dp. Negative one when never sampled.</summary>
    [JsonPropertyName("sampledSecondsAgo")]
    public double SampledSecondsAgo { get; init; }

    /// <summary>True when never sampled, or sampled more than 5 seconds ago.</summary>
    [JsonPropertyName("stale")]
    public bool Stale { get; init; }

    [JsonPropertyName("sessionChangedAtFrame")]
    public int SessionChangedAtFrame { get; init; }

    [JsonPropertyName("sessionChangedSecondsAgo")]
    public double SessionChangedSecondsAgo { get; init; }

    /// <summary>Human text, for example <c>gameState None -&gt; Running; hosting false -&gt; true</c>.</summary>
    [JsonPropertyName("lastChange")]
    public string? LastChange { get; init; }

    /// <summary>Present only when never sampled, or stale beyond 5 seconds.</summary>
    [JsonPropertyName("warning")]
    public string? Warning { get; init; }
}
