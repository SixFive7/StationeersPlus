using System.Text.Json.Serialization;

namespace TestRig.Contracts;

// /player, /player/teleport, /player/look, /player/use, /player/swaphands, /cursor/force.
//
// Vector parameters accept either a JSON array [x,y,z] in the body or the string form
// "x,y,z" as a query parameter. A double[] serializes to the array form, which is the one
// to prefer; the string form exists so a vector survives a bare curl GET.

/// <summary><c>/player</c>. No parameters.</summary>
public sealed record PlayerRequest;

/// <summary>
///     The local character, nested under <c>player</c>.
/// </summary>
/// <remarks>
///     The nesting is load-bearing. The PowerShell reader documented itself as returning
///     "the player block only" and in fact returned the whole envelope, and the fake put a
///     bare <c>position</c> at the top level, so the documentation and the fake agreed with
///     each other and both disagreed with the endpoint (defect P-16, divergence D-51).
/// </remarks>
public sealed record PlayerResponse : IWireResult
{
    [JsonPropertyName("ok")]
    public bool Ok { get; init; }

    [JsonPropertyName("epoch")]
    public EpochBlock? Epoch { get; init; }

    /// <summary><c>{present:false}</c> and nothing else when there is no local Human.</summary>
    [JsonPropertyName("player")]
    public PlayerBlock? Player { get; init; }
}

/// <summary>
///     <c>/player/teleport</c>. Pass <see cref="Position"/> or any of the individual
///     components; anything omitted keeps its current value, and <see cref="Offset"/> is
///     applied afterwards.
/// </summary>
public sealed record PlayerTeleportRequest
{
    /// <summary>An array <c>[x,y,z]</c>, or the string form <c>"x,y,z"</c> as a query parameter.</summary>
    [JsonPropertyName("position")]
    public double[]? Position { get; init; }

    [JsonPropertyName("x")]
    public double? X { get; init; }

    [JsonPropertyName("y")]
    public double? Y { get; init; }

    [JsonPropertyName("z")]
    public double? Z { get; init; }

    [JsonPropertyName("offset")]
    public double[]? Offset { get; init; }
}

/// <summary>Where the character was and where it ended up.</summary>
public sealed record PlayerTeleportResponse : IWireResult
{
    [JsonPropertyName("ok")]
    public bool Ok { get; init; }

    [JsonPropertyName("from")]
    public double[]? From { get; init; }

    [JsonPropertyName("to")]
    public double[]? To { get; init; }

    /// <summary>Set on a remote client, where the move is local and the server may correct it.</summary>
    [JsonPropertyName("note")]
    public string? Note { get; init; }
}

/// <summary><c>/player/look</c>. <see cref="At"/> wins over <see cref="Yaw"/> and <see cref="Pitch"/>.</summary>
public sealed record PlayerLookRequest
{
    /// <summary>Aim at a world point. Takes precedence over the angles.</summary>
    [JsonPropertyName("at")]
    public double[]? At { get; init; }

    [JsonPropertyName("yaw")]
    public double? Yaw { get; init; }

    /// <summary>Clamped to plus or minus 89 degrees.</summary>
    [JsonPropertyName("pitch")]
    public double? Pitch { get; init; }
}

/// <summary>The resulting camera angles.</summary>
public sealed record PlayerLookResponse : IWireResult
{
    [JsonPropertyName("ok")]
    public bool Ok { get; init; }

    [JsonPropertyName("yaw")]
    public double Yaw { get; init; }

    [JsonPropertyName("pitch")]
    public double Pitch { get; init; }
}

/// <summary>
///     <c>/player/use</c>. Use the held item on a target. Prefer this over forcing the
///     cursor: <c>/cursor/force</c> on a collider-less Thing is what wedges
///     <c>GameManager.Update</c> permanently.
/// </summary>
public sealed record PlayerUseRequest
{
    /// <summary>The target's ReferenceId. Zero falls back to <see cref="Cursor"/>.</summary>
    [JsonPropertyName("targetId")]
    public long? TargetId { get; init; }

    /// <summary>Use whatever the cursor is on. Consulted only when <see cref="TargetId"/> is zero.</summary>
    [JsonPropertyName("cursor")]
    public bool? Cursor { get; init; }

    /// <summary>How far through a held interaction to report, 0 to 1. Defaults to 1.</summary>
    [JsonPropertyName("completedRatio")]
    public double? CompletedRatio { get; init; }

    [JsonPropertyName("destroy")]
    public bool? Destroy { get; init; }

    [JsonPropertyName("copy")]
    public bool? Copy { get; init; }

    /// <summary>The hit point. Defaults to the target's own position.</summary>
    [JsonPropertyName("point")]
    public double[]? Point { get; init; }
}

/// <summary>What was used on what.</summary>
public sealed record PlayerUseResponse : IWireResult
{
    [JsonPropertyName("ok")]
    public bool Ok { get; init; }

    [JsonPropertyName("instance")]
    public string? Instance { get; init; }

    [JsonPropertyName("targetId")]
    public long TargetId { get; init; }

    [JsonPropertyName("targetPrefab")]
    public string? TargetPrefab { get; init; }

    [JsonPropertyName("heldItem")]
    public string? HeldItem { get; init; }

    [JsonPropertyName("point")]
    public double[]? Point { get; init; }
}

/// <summary><c>/player/swaphands</c>. No parameters.</summary>
public sealed record PlayerSwapHandsRequest;

/// <summary>The player block after the swap.</summary>
public sealed record PlayerSwapHandsResponse : IWireResult
{
    [JsonPropertyName("ok")]
    public bool Ok { get; init; }

    [JsonPropertyName("player")]
    public PlayerBlock? Player { get; init; }
}

/// <summary>
///     <c>/cursor/force</c>. Pin the cursor onto a specific Thing, or clear the pin.
/// </summary>
/// <remarks>
///     A forced cursor on a Thing with no collider leaves <c>CursorTargetCollider</c> null,
///     and that state wedges <c>GameManager.Update</c> permanently. The plugin refuses at
///     409 rather than allowing it.
/// </remarks>
public sealed record CursorForceRequest
{
    [JsonPropertyName("clear")]
    public bool? Clear { get; init; }

    /// <summary>Required unless <see cref="Clear"/> is true. 400 otherwise.</summary>
    [JsonPropertyName("targetId")]
    public long? TargetId { get; init; }
}

/// <summary>
///     One shape covering both directions: the clear fields are set on a clear, the target
///     fields on a set.
/// </summary>
public sealed record CursorForceResponse : IWireResult
{
    [JsonPropertyName("ok")]
    public bool Ok { get; init; }

    [JsonPropertyName("cleared")]
    public bool? Cleared { get; init; }

    [JsonPropertyName("stateReset")]
    public bool? StateReset { get; init; }

    [JsonPropertyName("note")]
    public string? Note { get; init; }

    [JsonPropertyName("targetId")]
    public long? TargetId { get; init; }

    [JsonPropertyName("prefabName")]
    public string? PrefabName { get; init; }

    [JsonPropertyName("collider")]
    public string? Collider { get; init; }

    [JsonPropertyName("colliderType")]
    public string? ColliderType { get; init; }

    [JsonPropertyName("isSlotCollider")]
    public bool? IsSlotCollider { get; init; }
}
