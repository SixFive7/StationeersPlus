using System.Text.Json.Serialization;

namespace TestRig.Contracts;

// /spawn/hand, /spawn/world, /spawn/structure, /prefabs.

/// <summary><c>/spawn/hand</c>. Create a Thing directly into the active hand slot.</summary>
public sealed record SpawnHandRequest
{
    /// <summary>Required. 400 otherwise.</summary>
    [JsonPropertyName("prefab")]
    public string? Prefab { get; init; }
}

/// <summary>
///     409 for no local player, no active hand slot, an unknown prefab, and for the lack
///     of server authority: spawning into a slot needs <c>GameManager.RunSimulation</c>.
/// </summary>
public sealed record SpawnHandResponse : IWireResult
{
    [JsonPropertyName("ok")]
    public bool Ok { get; init; }

    [JsonPropertyName("prefab")]
    public string? Prefab { get; init; }

    [JsonPropertyName("referenceId")]
    public long ReferenceId { get; init; }

    [JsonPropertyName("activeHand")]
    public SlotOccupant? ActiveHand { get; init; }
}

/// <summary><c>/spawn/world</c>. Create a Thing loose in the world.</summary>
public sealed record SpawnWorldRequest
{
    /// <summary>Required. 400 otherwise.</summary>
    [JsonPropertyName("prefab")]
    public string? Prefab { get; init; }

    /// <summary>
    ///     Defaults to the inverse of <c>GameManager.RunSimulation</c>: a client asks the
    ///     server, an authoritative process creates directly.
    /// </summary>
    [JsonPropertyName("viaServer")]
    public bool? ViaServer { get; init; }

    /// <summary>Defaults to the player position plus forward times <see cref="Distance"/>.</summary>
    [JsonPropertyName("position")]
    public double[]? Position { get; init; }

    [JsonPropertyName("distance")]
    public double? Distance { get; init; }

    [JsonPropertyName("offset")]
    public double[]? Offset { get; init; }
}

/// <summary>
///     Two shapes. The server route answers <c>{ok, prefab, route}</c> with no
///     <see cref="ReferenceId"/> or <see cref="Position"/>, because the create happens on
///     the server and this process never sees the result. The local route answers all
///     four.
/// </summary>
public sealed record SpawnWorldResponse : IWireResult
{
    [JsonPropertyName("ok")]
    public bool Ok { get; init; }

    [JsonPropertyName("prefab")]
    public string? Prefab { get; init; }

    /// <summary><c>SpawnDynamicThingMaxStack</c> (server) or <c>OnServer.Create</c> (local).</summary>
    [JsonPropertyName("route")]
    public string? Route { get; init; }

    /// <summary>Absent on the server route.</summary>
    [JsonPropertyName("referenceId")]
    public long? ReferenceId { get; init; }

    /// <summary>Absent on the server route.</summary>
    [JsonPropertyName("position")]
    public double[]? Position { get; init; }
}

/// <summary><c>/spawn/structure</c>. Place a built structure.</summary>
public sealed record SpawnStructureRequest
{
    /// <summary>Required. 400 otherwise.</summary>
    [JsonPropertyName("prefab")]
    public string? Prefab { get; init; }

    /// <summary>Defaults to the player position plus forward times <see cref="Distance"/>.</summary>
    [JsonPropertyName("position")]
    public double[]? Position { get; init; }

    [JsonPropertyName("offset")]
    public double[]? Offset { get; init; }

    /// <summary>Defaults to 3.</summary>
    [JsonPropertyName("distance")]
    public double? Distance { get; init; }

    [JsonPropertyName("yaw")]
    public double? Yaw { get; init; }

    /// <summary>A ColorSwatch index. Defaults to -1, meaning leave it alone.</summary>
    [JsonPropertyName("colorIndex")]
    public int? ColorIndex { get; init; }
}

/// <summary>
///     <see cref="ReferenceId"/> and <see cref="Position"/> are absent when the construct
///     call returned null, which on a client is <b>not</b> a failure: the server did the
///     placement and this process never got a handle. Callers guard on the id being
///     missing or zero, and <see cref="Note"/> explains which case it was.
/// </summary>
public sealed record SpawnStructureResponse : IWireResult
{
    [JsonPropertyName("ok")]
    public bool Ok { get; init; }

    [JsonPropertyName("prefab")]
    public string? Prefab { get; init; }

    [JsonPropertyName("requestedPosition")]
    public double[]? RequestedPosition { get; init; }

    [JsonPropertyName("yaw")]
    public double Yaw { get; init; }

    [JsonPropertyName("colorIndex")]
    public int ColorIndex { get; init; }

    [JsonPropertyName("referenceId")]
    public long? ReferenceId { get; init; }

    [JsonPropertyName("position")]
    public double[]? Position { get; init; }

    [JsonPropertyName("note")]
    public string? Note { get; init; }
}

/// <summary><c>/prefabs</c>. What can be spawned.</summary>
public sealed record PrefabsRequest
{
    /// <summary>Case-insensitive substring of the prefab name.</summary>
    [JsonPropertyName("contains")]
    public string? Contains { get; init; }

    /// <summary>Substring of the runtime type name.</summary>
    [JsonPropertyName("type")]
    public string? Type { get; init; }

    /// <summary>Defaults to 200.</summary>
    [JsonPropertyName("limit")]
    public int? Limit { get; init; }
}

/// <summary>Each entry is <c>"&lt;Name&gt; [&lt;Type&gt;]"</c>, sorted.</summary>
public sealed record PrefabsResponse : IWireResult
{
    [JsonPropertyName("ok")]
    public bool Ok { get; init; }

    [JsonPropertyName("scanned")]
    public int Scanned { get; init; }

    [JsonPropertyName("count")]
    public int Count { get; init; }

    [JsonPropertyName("prefabs")]
    public string[]? Prefabs { get; init; }
}
